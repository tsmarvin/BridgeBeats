using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Worker.Spotify.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Worker.Spotify;

/// <summary>
/// Redis Streams helper for the Spotify bulk-id lookup path: the dedicated track-id and album-id
/// bulk streams that <see cref="SpotifyBulkProcessorService"/> drains in batches.
/// </summary>
/// <remarks>
/// Single-id Spotify lookups are diverted onto dedicated bulk streams so they can be fetched in
/// multi-id batches. This helper owns the consumer-group lifecycle for those streams, depth and
/// age inspection (to drive flush decisions), batch dequeue (including reclaiming stalled pending
/// entries via XAUTOCLAIM), acknowledgement, and bounded requeue. Poison entries (missing or
/// unparseable payloads) are removed atomically rather than retried.
/// </remarks>
public sealed partial class SpotifyBatchQueueHelper : IQueueWorkSignal {

    /// <summary>Redis connection multiplexer used for all stream operations.</summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>Logger for this helper's structured log events.</summary>
    private readonly ILogger<SpotifyBatchQueueHelper> _logger;

    /// <summary>JSON options used to serialize and deserialize queued request payloads (camelCase, compact).</summary>
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _bulkTrackStream;
    private readonly string _bulkAlbumStream;
    private readonly string _interactiveStream;
    private readonly string _backgroundStream;
    private readonly string _workSignalChannel;
    private readonly string _bulkWorkSignalChannel;

    /// <summary>Name of the Redis consumer group shared by the Spotify bulk streams.</summary>
    private const string ConsumerGroup = SpotifyConstants.ConsumerGroup;

    /// <summary>
    /// This process instance's consumer id within <see cref="ConsumerGroup"/>, derived from
    /// <see cref="Environment.MachineName"/> and a full GUID. The GUID is retained even when
    /// multiple replicas run on the same host.
    /// </summary>
    private readonly string _consumerId;
    private readonly Dictionary<string, RedisValue> _autoClaimCursors = [];
    private readonly SemaphoreSlim _workSignalInitialization = new( 1, 1 );
    private TaskCompletionSource _workSignal = CreateWorkSignal( );
    private long _workVersion;
    private bool _workSignalInitialized;
    private DateTimeOffset? _nextReclaimCheck;

    /// <summary>Internal identity seam used to verify replica uniqueness without touching Redis.</summary>
    internal string ConsumerId => _consumerId;

    /// <summary>
    /// Minimum idle time (ms) before XAUTOCLAIM reclaims a pending entry.
    /// 15 minutes is long enough that a slow-but-alive processing cycle is
    /// never inadvertently reclaimed, while short enough that a crashed consumer
    /// does not block the stream for more than 15 minutes.
    /// </summary>
    private const int AutoClaimMinIdleMs = 15 * 60 * 1000;

    /// <summary>
    /// Atomically forwards one live bulk delivery to the appropriate generic single-item stream,
    /// wakes its consumer, and removes the source. Source existence makes redelivery idempotent.
    /// </summary>
    private const string ForwardToSingleItemScript = """
        local source = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #source == 0 then return false end
        local groupExists = false
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        for _, group in ipairs(groups) do
            for i = 1, #group, 2 do
                if group[i] == 'name' and group[i + 1] == ARGV[6] then
                    groupExists = true
                    break
                end
            end
            if groupExists then break end
        end
        if not groupExists then return redis.error_reply('NOGROUP source consumer group is missing') end
        local replacement = redis.call('XADD', KEYS[2], '*', ARGV[2], ARGV[3], ARGV[4], ARGV[5])
        redis.call('PUBLISH', ARGV[7], replacement)
        redis.call('XACK', KEYS[1], ARGV[6], ARGV[1])
        redis.call('XDEL', KEYS[1], ARGV[1])
        return replacement
        """;

    private const string RequeueDeliveryScript = """
        local source = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #source == 0 then return false end
        local replacement = redis.call(
            'XADD', KEYS[1], '*', ARGV[3], ARGV[4], ARGV[5], ARGV[6])
        redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
        redis.call('XDEL', KEYS[1], ARGV[1])
        redis.call('PUBLISH', ARGV[7], replacement)
        return replacement
        """;

    private const string RemoveDeliveryScript = """
        redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
        return redis.call('XDEL', KEYS[1], ARGV[1])
        """;

    /// <summary>
    /// Initializes the helper, deriving this process instance's consumer id.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="logger">The logger for this helper.</param>
    /// <param name="keyPrefix">Optional isolated queue-key prefix shared with the bulk producer.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="redis"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public SpotifyBatchQueueHelper(
        IConnectionMultiplexer redis,
        ILogger<SpotifyBatchQueueHelper> logger,
        string? keyPrefix = null
    ) {
        _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _bulkTrackStream = QueueStreamKeys.SpotifyBulkFor( isTrack: true, keyPrefix );
        _bulkAlbumStream = QueueStreamKeys.SpotifyBulkFor( isTrack: false, keyPrefix );
        _interactiveStream = QueueStreamKeys.For( SupportedProviders.Spotify, QueuePriority.Interactive, keyPrefix );
        _backgroundStream = QueueStreamKeys.For( SupportedProviders.Spotify, QueuePriority.Background, keyPrefix );
        _workSignalChannel = QueueStreamKeys.WorkSignalFor( SupportedProviders.Spotify, keyPrefix );
        _bulkWorkSignalChannel = QueueStreamKeys.SpotifyBulkWorkSignal( keyPrefix );

        string uniqueId = Guid.NewGuid( ).ToString( "N" );
        // Redis stream consumer names are not bounded to 32 characters. Keep the full random
        // suffix so long machine names cannot truncate it away and cause a restart collision.
        _consumerId = $"spotify-batch-{Environment.MachineName}-{uniqueId}";

        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    private static TaskCompletionSource CreateWorkSignal( ) =>
        new( TaskCreationOptions.RunContinuationsAsynchronously );

    /// <inheritdoc/>
    public async Task InitializeWorkSignalAsync( CancellationToken cancellationToken = default ) {
        if (_workSignalInitialized) return;
        await _workSignalInitialization.WaitAsync( cancellationToken );
        try {
            if (_workSignalInitialized) return;
            await _redis.GetSubscriber( ).SubscribeAsync(
                RedisChannel.Literal( _bulkWorkSignalChannel ),
                ( _, _ ) => NotifyWorkAvailable( ) );
            _workSignalInitialized = true;
        } finally {
            _ = _workSignalInitialization.Release( );
        }
    }

    /// <inheritdoc/>
    public long CaptureWorkVersion( ) => Interlocked.Read( ref _workVersion );

    /// <inheritdoc/>
    public async Task WaitForWorkAsync(
        long observedVersion,
        DateTimeOffset? scheduledWake,
        CancellationToken cancellationToken = default
    ) {
        if (_nextReclaimCheck is { } reclaimCheck
            && (scheduledWake is null || scheduledWake <= DateTimeOffset.UtcNow)) {
            scheduledWake = reclaimCheck;
        }
        while (Interlocked.Read( ref _workVersion ) == observedVersion) {
            Task signal = Volatile.Read( ref _workSignal ).Task;
            if (Interlocked.Read( ref _workVersion ) != observedVersion) return;
            if (scheduledWake is null) {
                await signal.WaitAsync( cancellationToken );
                return;
            }

            TimeSpan remaining = scheduledWake.Value - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            TaskCompletionSource elapsed = CreateWorkSignal( );
            using Timer timer = new(
                static state => ((TaskCompletionSource)state!).TrySetResult( ),
                elapsed,
                remaining,
                Timeout.InfiniteTimeSpan );
            Task completed = await Task.WhenAny( signal, elapsed.Task ).WaitAsync( cancellationToken );
            await completed;
            return;
        }
    }

    private void NotifyWorkAvailable( ) {
        _ = Interlocked.Increment( ref _workVersion );
        TaskCompletionSource previous = Interlocked.Exchange( ref _workSignal, CreateWorkSignal( ) );
        _ = previous.TrySetResult( );
    }

    /// <summary>
    /// Ensures the consumer group exists on both the bulk track-id and bulk album-id streams,
    /// creating each stream if absent.
    /// </summary>
    /// <param name="cancellationToken">Token used to stop between streams.</param>
    /// <returns>A task that completes once both groups are ensured.</returns>
    /// <remarks>
    /// A <c>BUSYGROUP</c> server error (the group already exists) is treated as success and logged
    /// at debug level.
    /// </remarks>
    public async Task EnsureConsumerGroupsAsync( CancellationToken cancellationToken = default ) {
        IDatabase db = _redis.GetDatabase( );

        foreach (string stream in new[] { _bulkTrackStream, _bulkAlbumStream }) {
            if (cancellationToken.IsCancellationRequested) { break; }
            try {
                _ = await db.StreamCreateConsumerGroupAsync(
                    stream,
                    ConsumerGroup,
                    StreamPosition.Beginning,
                    createStream: true
                );
                LogConsumerGroupCreated( _logger, ConsumerGroup, stream );
            } catch (RedisServerException ex) when (ex.Message.Contains( "BUSYGROUP" )) {
                LogConsumerGroupExists( _logger, ConsumerGroup, stream );
            }
        }
    }

    /// <summary>
    /// Reads the current depth of the bulk track-id and album-id streams.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>
    /// An <see cref="IdLookupDepth"/> reporting the bulk track and album stream lengths. The
    /// non-bulk count fields mirror the bulk counts for this helper.
    /// </returns>
    public async Task<IdLookupDepth> GetIdLookupDepthAsync( CancellationToken cancellationToken = default ) {
        cancellationToken.ThrowIfCancellationRequested( );
        IDatabase db = _redis.GetDatabase( );

        long bulkTrackCount = await db.StreamLengthAsync( _bulkTrackStream );
        long bulkAlbumCount = await db.StreamLengthAsync( _bulkAlbumStream );

        return new IdLookupDepth(
            TrackIdCount: (int)bulkTrackCount,
            AlbumIdCount: (int)bulkAlbumCount,
            BulkTrackIdCount: (int)bulkTrackCount,
            BulkAlbumIdCount: (int)bulkAlbumCount
        );
    }

    /// <summary>
    /// Returns the enqueue timestamp of the oldest entry in a bulk stream, used to decide whether
    /// the batch linger window has elapsed.
    /// </summary>
    /// <param name="isTracks"><see langword="true"/> to inspect the bulk track-id stream; <see langword="false"/> for the album-id stream.</param>
    /// <param name="cancellationToken">Token used to skip the read.</param>
    /// <returns>
    /// The oldest entry's enqueue time, or <see langword="null"/> when the stream is empty, the
    /// <c>enqueuedAt</c> field is absent or malformed, or a Redis read error occurs.
    /// </returns>
    /// <remarks>
    /// A malformed <c>enqueuedAt</c> value is logged and treated as absent (returns
    /// <see langword="null"/>) so a bad timestamp cannot wedge the flush decision.
    /// </remarks>
    public async Task<DateTimeOffset?> GetOldestEnqueuedAtAsync( bool isTracks, CancellationToken cancellationToken = default ) {
        if (cancellationToken.IsCancellationRequested) {
            return null;
        }

        string stream = isTracks ? _bulkTrackStream : _bulkAlbumStream;
        IDatabase db = _redis.GetDatabase( );

        try {
            // Redis streams are time-ordered: the first entry is always the oldest.
            StreamEntry[] entries = await db.StreamRangeAsync( stream, "-", "+", count: 1 );
            if (entries.Length == 0) {
                return null;
            }

            string? enqueuedAtStr = entries[0][QueueStreamFieldNames.EnqueuedAt];
            if (string.IsNullOrEmpty( enqueuedAtStr )) {
                return null;
            }

            if (!DateTimeOffset.TryParseExact( enqueuedAtStr, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed )) {
                // Malformed enqueuedAt — treat as absent so the age trigger does not strand a low-volume stream.
                LogMalformedEnqueuedAt( _logger, enqueuedAtStr, stream );
                return null;
            }

            return parsed;
        } catch (RedisServerException) {
            return null;
        }
    }

    /// <summary>
    /// Dequeues a batch of track-id lookup messages from the bulk track-id stream.
    /// </summary>
    /// <param name="maxCount">Maximum number of messages to claim; defaults to the configured per-batch track maximum.</param>
    /// <param name="cancellationToken">Token used to stop dequeuing early.</param>
    /// <returns>The claimed messages, which may be fewer than requested (including empty).</returns>
    public async Task<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>> DequeueTrackIdBatchAsync(
        int? maxCount = null,
        CancellationToken cancellationToken = default
    ) {
        int count = maxCount ?? SpotifyConstants.MaxTracksPerBatchLookup;
        return await DequeueBatchFromStreamAsync( _bulkTrackStream, count, cancellationToken );
    }

    /// <summary>
    /// Dequeues a batch of album-id lookup messages from the bulk album-id stream.
    /// </summary>
    /// <param name="maxCount">Maximum number of messages to claim; defaults to the configured per-batch album maximum.</param>
    /// <param name="cancellationToken">Token used to stop dequeuing early.</param>
    /// <returns>The claimed messages, which may be fewer than requested (including empty).</returns>
    public async Task<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>> DequeueAlbumIdBatchAsync(
        int? maxCount = null,
        CancellationToken cancellationToken = default
    ) {
        int count = maxCount ?? SpotifyConstants.MaxAlbumsPerBatchLookup;
        return await DequeueBatchFromStreamAsync( _bulkAlbumStream, count, cancellationToken );
    }

    /// <summary>
    /// Dequeues up to <paramref name="count"/> messages in three bounded stages: this consumer's
    /// pending entries, stalled entries reclaimed via XAUTOCLAIM, then new entries.
    /// </summary>
    /// <param name="stream">The bulk stream key to read from.</param>
    /// <param name="count">Maximum number of messages to return.</param>
    /// <param name="cancellationToken">Token used to stop reading early.</param>
    /// <returns>The decoded messages in claim order; poison entries are dropped, not returned.</returns>
    /// <remarks>
    /// Recovered and reclaimed entries are counted against <paramref name="count"/> so a single
    /// call never exceeds the requested batch size. Entries with missing or unparseable payloads
    /// are acknowledged and deleted as poison. If XAUTOCLAIM is unsupported by the server, only
    /// dead-consumer reclaim is skipped; this consumer's pending entries and new entries remain
    /// readable.
    /// </remarks>
    private async Task<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>> DequeueBatchFromStreamAsync(
        string stream,
        int count,
        CancellationToken cancellationToken
    ) {
        IDatabase db = _redis.GetDatabase( );
        List<QueuedMessage<QueuedLookupRequest>> messages = [];
        HashSet<string> scannedEntryIds = [];
        Stopwatch dequeueTimer = Stopwatch.StartNew( );
        Activity? dequeueActivity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.dequeue" );
        _ = (dequeueActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
        _ = (dequeueActivity?.SetTag( QueueMetricTags.Priority, "bulk" ));
        try {

            // XAUTOCLAIM sweep: reclaim any PEL entries idle longer than AutoClaimMinIdleMs,
            // then immediately deserialize and append them to the result — claimed entries land
            // in THIS consumer's PEL and XREADGROUP with ">" can never see them again.
            // Safe here because there is exactly one consumer service (SpotifyBulkProcessorService)
            // reading these streams — no other consumer group member can be legitimately processing
            // the entry after 15 minutes of idle time.
            int remainingCount = count;
            Stopwatch pelTimer = Stopwatch.StartNew( );
            using Activity? pelActivity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.dequeue.pel_scan" );
            _ = (pelActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
            _ = (pelActivity?.SetTag( QueueMetricTags.Priority, "bulk" ));
            _ = (pelActivity?.SetTag( QueueMetricTags.Stream, stream ));
            try {
                // Recover entries still owned by this consumer before waiting for the idle
                // threshold. XREADGROUP with an explicit 0-0 position reads this consumer's PEL
                // without delivering new entries or resetting idle clocks on entries beyond the
                // bounded batch budget.
                StreamEntry[] ownPending = await db.StreamReadGroupAsync(
                    stream,
                    ConsumerGroup,
                    _consumerId,
                    position: "0-0",
                    count: remainingCount,
                    noAck: false );
                remainingCount = await AppendDecodedEntriesAsync(
                    db, stream, ownPending, messages, remainingCount, scannedEntryIds,
                    recordPelOutcome: true, cancellationToken: cancellationToken );

                if (remainingCount <= 0) {
                    RecordBulkDequeues( messages );
                    return messages;
                }

                RedisValue autoClaimStart = _autoClaimCursors.TryGetValue( stream, out RedisValue cursor ) ? cursor : "0-0";
                StreamAutoClaimResult claimed = await db.StreamAutoClaimAsync(
                stream,
                ConsumerGroup,
                _consumerId,
                AutoClaimMinIdleMs,
                autoClaimStart,
                remainingCount
            );
                _autoClaimCursors[stream] = claimed.NextStartId.HasValue && claimed.NextStartId != "0-0"
                    ? claimed.NextStartId
                    : "0-0";

                foreach (RedisValue deletedId in claimed.DeletedIds) {
                    QueueMetrics.PelScanEntries.Add( 1,
                        new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ),
                        new KeyValuePair<string, object?>( QueueMetricTags.Outcome, "missing" ) );
                }

                if (claimed.ClaimedEntries.Length > 0) {
                    LogAutoClaimRecovered( _logger, claimed.ClaimedEntries.Length, stream );
                    remainingCount = await AppendDecodedEntriesAsync(
                        db, stream, claimed.ClaimedEntries, messages, remainingCount,
                        scannedEntryIds, recordPelOutcome: true, cancellationToken: cancellationToken );
                }
            } catch (RedisServerException ex) when (ex.Message.Contains( "NOGROUP", StringComparison.OrdinalIgnoreCase )) {
                throw;
            } catch (RedisServerException ex) when (IsAutoClaimUnsupported( ex )) {
                // XAUTOCLAIM was added in Redis 6.2; if the server is older, log and continue
                LogAutoClaimNotSupported( _logger, ex, stream );
            } finally {
                pelTimer.Stop( );
                pelActivity?.Stop( );
                QueueMetrics.PelScanDuration.Record( pelTimer.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Priority, "bulk" ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ) );
            }

            // Skip XREADGROUP if the claimed entries already filled the budget.
            if (remainingCount <= 0) {
                RecordBulkDequeues( messages );
                return messages;
            }

            Stopwatch readTimer = Stopwatch.StartNew( );
            Activity? readActivity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.dequeue.read_group" );
            _ = (readActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
            _ = (readActivity?.SetTag( QueueMetricTags.Priority, "bulk" ));
            _ = (readActivity?.SetTag( QueueMetricTags.Stream, stream ));
            StreamEntry[] entries;
            try {
                entries = await db.StreamReadGroupAsync(
                    stream,
                    ConsumerGroup,
                    _consumerId,
                    count: remainingCount,
                    noAck: false
                );

            } catch (RedisServerException ex) when (ex.Message.Contains( "NOGROUP", StringComparison.OrdinalIgnoreCase )) {
                throw;
            } catch (RedisServerException) {
                throw;
            } finally {
                readTimer.Stop( );
                readActivity?.Stop( );
                QueueMetrics.ReadGroupDuration.Record( readTimer.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Priority, "bulk" ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ) );
            }

            _ = await AppendDecodedEntriesAsync(
                db, stream, entries, messages, remainingCount, scannedEntryIds,
                recordPelOutcome: false, cancellationToken: cancellationToken );

            RecordBulkDequeues( messages );
            return messages;
        } finally {
            _nextReclaimCheck = messages.Count == 0
                ? DateTimeOffset.UtcNow.AddMilliseconds( AutoClaimMinIdleMs )
                : null;
            dequeueTimer.Stop( );
            dequeueActivity?.Stop( );
            QueueMetrics.DequeueDuration.Record( dequeueTimer.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
                new KeyValuePair<string, object?>( QueueMetricTags.Priority, "bulk" ) );
        }
    }

    private async Task<int> AppendDecodedEntriesAsync(
        IDatabase db,
        string stream,
        IReadOnlyList<StreamEntry> entries,
        List<QueuedMessage<QueuedLookupRequest>> messages,
        int remainingCount,
        HashSet<string> scannedEntryIds,
        bool recordPelOutcome,
        CancellationToken cancellationToken
    ) {
        foreach (StreamEntry entry in entries) {
            if (cancellationToken.IsCancellationRequested || remainingCount <= 0) break;
            string compositeId = $"{stream}:{entry.Id}";
            if (!scannedEntryIds.Add( compositeId )) continue;
            string? payload = entry[QueueStreamFieldNames.Payload];
            string? enqueuedAtStr = entry[QueueStreamFieldNames.EnqueuedAt];
            if (string.IsNullOrEmpty( payload )) {
                await AckAndDeletePoisonEntryAsync( db, stream, entry.Id.ToString( ) );
                if (recordPelOutcome) {
                    QueueMetrics.PelScanEntries.Add( 1,
                        new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ),
                        new KeyValuePair<string, object?>( QueueMetricTags.Outcome, "poison" ) );
                }
                continue;
            }

            try {
                QueuedLookupRequest? request = JsonSerializer.Deserialize<QueuedLookupRequest>( payload, _jsonOptions );
                if (request is null) {
                    await AckAndDeletePoisonEntryAsync( db, stream, entry.Id.ToString( ) );
                    if (recordPelOutcome) {
                        QueueMetrics.PelScanEntries.Add( 1,
                            new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ),
                            new KeyValuePair<string, object?>( QueueMetricTags.Outcome, "poison" ) );
                    }
                    continue;
                }

                DateTimeOffset enqueuedAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrEmpty( enqueuedAtStr )
                    && !DateTimeOffset.TryParseExact( enqueuedAtStr, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out enqueuedAt )) {
                    LogMalformedEnqueuedAt( _logger, enqueuedAtStr, stream );
                    enqueuedAt = DateTimeOffset.UtcNow;
                }

                messages.Add( new QueuedMessage<QueuedLookupRequest>( compositeId, request, enqueuedAt ) {
                    Priority = QueuePriority.Bulk
                } );
                if (recordPelOutcome) {
                    QueueMetrics.PelScanEntries.Add( 1,
                        new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ),
                        new KeyValuePair<string, object?>( QueueMetricTags.Outcome, "eligible" ) );
                }
                remainingCount--;
            } catch (JsonException ex) {
                LogDeserializationError( _logger, ex, entry.Id.ToString( ), stream );
                await AckAndDeletePoisonEntryAsync( db, stream, entry.Id.ToString( ) );
                if (recordPelOutcome) {
                    QueueMetrics.PelScanEntries.Add( 1,
                        new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ),
                        new KeyValuePair<string, object?>( QueueMetricTags.Outcome, "poison" ) );
                }
            }
        }

        return remainingCount;
    }

    private static void RecordBulkDequeues( IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages ) {
        foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
            QueueMetrics.RecordDequeue( SupportedProviders.Spotify, QueuePriority.Bulk );
            QueueMetrics.RecordQueueSojourn( SupportedProviders.Spotify, QueuePriority.Bulk, message.EnqueuedAt );
        }
    }

    /// <summary>
    /// Acknowledges and deletes a single poison entry (missing or unparseable payload) so it is
    /// never re-read.
    /// </summary>
    /// <param name="db">The Redis database to operate on.</param>
    /// <param name="stream">The stream the entry belongs to.</param>
    /// <param name="id">The Redis entry id to acknowledge and delete.</param>
    /// <returns>A task that completes once the entry is removed (Redis errors are logged and swallowed).</returns>
    private async Task AckAndDeletePoisonEntryAsync( IDatabase db, string stream, string id ) {
        try {
            _ = await db.ScriptEvaluateAsync(
                RemoveDeliveryScript,
                [stream],
                [id, ConsumerGroup] );
            QueueMetrics.RecordDeliveryRemoved( SupportedProviders.Spotify, QueuePriority.Bulk );
            QueueMetrics.RecordTerminalOutcome( SupportedProviders.Spotify, QueuePriority.Bulk, "poison_deleted" );
        } catch (RedisServerException ex) {
            LogStreamReadWarning( _logger, ex, stream );
        }
    }

    private static bool IsAutoClaimUnsupported( RedisServerException exception ) {
        string message = exception.Message;
        return message.Contains( "XAUTOCLAIM", StringComparison.OrdinalIgnoreCase )
            && (message.Contains( "unknown command", StringComparison.OrdinalIgnoreCase )
                || message.Contains( "not supported", StringComparison.OrdinalIgnoreCase ));
    }

    /// <summary>
    /// Acknowledges and deletes a processed message from its bulk stream.
    /// </summary>
    /// <param name="messageId">The composite message id (<c>"{stream}:{redisId}"</c>) to acknowledge.</param>
    /// <returns>A task that completes once the message is acknowledged and deleted.</returns>
    /// <remarks>Acknowledgement deletes the entry (XACK + XDEL); processed messages are not retained in the stream.</remarks>
    public async Task AcknowledgeAsync( string messageId ) {
        (string stream, string id) = ParseMessageId( messageId );

        IDatabase db = _redis.GetDatabase( );
        _ = await db.ScriptEvaluateAsync(
            RemoveDeliveryScript,
            [stream],
            [id, ConsumerGroup] );
        QueueMetrics.RecordDeliveryRemoved( SupportedProviders.Spotify, QueuePriority.Bulk );

        LogMessageAcknowledged( _logger, id, stream );
    }

    /// <summary>
    /// Atomically transfers a rejected bulk delivery to the generic Spotify background stream.
    /// </summary>
    /// <param name="message">The claimed bulk delivery to transfer.</param>
    /// <param name="cancellationToken">Token checked before the atomic Redis operation.</param>
    /// <returns>
    /// <see langword="true"/> when Redis created the background replacement and removed the bulk
    /// source; <see langword="false"/> when the source had already been removed by another attempt.
    /// </returns>
    /// <remarks>
    /// XADD, XACK, and XDEL run in one Lua script. A redelivery after an ambiguous client-side
    /// response finds no source entry and therefore cannot create a second background copy.
    /// </remarks>
    public async Task<bool> ForwardToSingleItemAsync(
        QueuedMessage<QueuedLookupRequest> message,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull( message );
        cancellationToken.ThrowIfCancellationRequested( );
        (string stream, string id) = ParseMessageId( message.MessageId );
        QueuedLookupRequest requeued = message.Payload with {
            EnqueueOrigin = QueueEnqueueOrigin.Requeue,
            BypassBulkRouting = true
        };
        string payload = JsonSerializer.Serialize( requeued, _jsonOptions );
        string enqueuedAt = DateTimeOffset.UtcNow.ToString( "O" );

        QueuePriority destinationPriority = message.Payload.OriginPriority == QueuePriority.Interactive
            ? QueuePriority.Interactive
            : QueuePriority.Background;
        string destinationStream = destinationPriority == QueuePriority.Interactive
            ? _interactiveStream
            : _backgroundStream;

        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            ForwardToSingleItemScript,
            keys: [stream, destinationStream],
            values: [
                id,
                QueueStreamFieldNames.Payload,
                payload,
                QueueStreamFieldNames.EnqueuedAt,
                enqueuedAt,
                ConsumerGroup,
                _workSignalChannel
            ] );
        RedisValue replacementId = (RedisValue)result;
        if (!replacementId.HasValue) {
            return false;
        }

        QueueMetrics.RecordDeliveryRemoved( SupportedProviders.Spotify, QueuePriority.Bulk );
        QueueMetrics.RecordRequeue( SupportedProviders.Spotify );
        QueueMetrics.RecordEnqueue(
            SupportedProviders.Spotify,
            destinationPriority,
            QueueEnqueueOrigin.Requeue );
        return true;
    }

    /// <summary>
    /// Requeues a bulk-stream message with an incremented attempt count, enforcing the retry cap.
    /// </summary>
    /// <param name="messageId">The composite message id (<c>"{stream}:{redisId}"</c>) to requeue.</param>
    /// <param name="sagaId">The saga id associated with the message, used for diagnostic logging.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// <see cref="RequeueOutcome.Requeued"/> when the message was re-added with an incremented
    /// attempt; <see cref="RequeueOutcome.CapReached"/> when the retry cap was reached (the
    /// original delivery remains pending until the caller records terminal saga state); or
    /// <see cref="RequeueOutcome.NotFound"/> when the original entry no longer exists.
    /// </returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled before work begins.</exception>
    /// <remarks>
    /// A fresh replacement is added before the original entry is acknowledged and deleted, with a
    /// new <c>enqueuedAt</c> timestamp and <c>AttemptCount + 1</c>. The caller
    /// is responsible for finalizing the saga when the cap is reached.
    /// </remarks>
    public async Task<RequeueOutcome> RequeueAsync(
        string messageId,
        string sagaId,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested( );
        (string stream, string id) = ParseMessageId( messageId );

        IDatabase db = _redis.GetDatabase( );

        // Read the original message
        StreamEntry[] entries = await db.StreamRangeAsync( stream, id, id, count: 1 );

        if (entries.Length == 0) {
            // Entry already XDELed — duplicate in-flight after XAUTOCLAIM re-claim.
            // Log and signal caller to leave saga untouched (another consumer processed it).
            LogMessageNotFound( _logger, id, stream );
            return RequeueOutcome.NotFound;
        }

        StreamEntry original = entries[0];
        string? rawPayload = original[QueueStreamFieldNames.Payload];

        // Deserialize to get current AttemptCount
        QueuedLookupRequest? request = null;
        if (!string.IsNullOrEmpty( rawPayload )) {
            try {
                request = JsonSerializer.Deserialize<QueuedLookupRequest>( rawPayload, _jsonOptions );
            } catch (JsonException) {
                // Fall through to terminal poison handling if we cannot parse.
            }
        }

        int currentAttempt = request?.AttemptCount ?? 0;

        if (currentAttempt >= LookupConstants.MaxQueueRetryAttempts - 1) {
            // The retry budget counts total executions: attempts 0..Max-2 may be replaced;
            // the final execution at Max-1 is terminal and must not create a sixth delivery.
            // The caller must durably record terminal saga state before removing this final
            // recovery delivery. Leaving it in the PEL makes a Redis failure during that write
            // recoverable through normal redelivery.
            LogMaxRetriesExceeded( _logger, id, LookupConstants.MaxQueueRetryAttempts, sagaId );
            return RequeueOutcome.CapReached;
        }

        if (request is null) {
            // Payload could not be deserialized — cannot safely increment AttemptCount.
            // Drop the message and signal CapReached so the caller writes failed saga state.
            LogPoisonPayloadDiscarded( _logger, id, sagaId );
            _ = await db.ScriptEvaluateAsync(
                RemoveDeliveryScript,
                [stream],
                [id, ConsumerGroup] );
            QueueMetrics.RecordDeliveryRemoved( SupportedProviders.Spotify, QueuePriority.Bulk );
            QueueMetrics.RecordTerminalOutcome( SupportedProviders.Spotify, QueuePriority.Bulk, "poison_deleted" );
            return RequeueOutcome.CapReached;
        }

        // Build re-serialized payload with AttemptCount + 1
        QueuedLookupRequest requeuedRequest = request with {
            AttemptCount = currentAttempt + 1,
            EnqueueOrigin = QueueEnqueueOrigin.Requeue
        };

        string newPayload = JsonSerializer.Serialize( requeuedRequest, _jsonOptions );

        NameValueEntry[] fields = [
            new NameValueEntry( QueueStreamFieldNames.Payload, newPayload ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ];

        RedisResult moveResult = await db.ScriptEvaluateAsync(
            RequeueDeliveryScript,
            [stream],
            [
                id,
                ConsumerGroup,
                QueueStreamFieldNames.Payload,
                fields[0].Value,
                QueueStreamFieldNames.EnqueuedAt,
                fields[1].Value,
                _bulkWorkSignalChannel
            ] );
        RedisValue replacementId = (RedisValue)moveResult;
        if (!replacementId.HasValue) {
            LogMessageNotFound( _logger, id, stream );
            return RequeueOutcome.NotFound;
        }
        NotifyWorkAvailable( );

        // Count the replacement as accepted immediately after XADD; cleanup failures must not
        // erase the enqueue acceptance from telemetry.
        QueueMetrics.RecordEnqueue( SupportedProviders.Spotify, QueuePriority.Bulk, QueueEnqueueOrigin.Requeue );

        QueueMetrics.RecordDeliveryRemoved( SupportedProviders.Spotify, QueuePriority.Bulk );
        QueueMetrics.RecordRequeue( SupportedProviders.Spotify );

        LogMessageRequeued( _logger, stream, currentAttempt + 1 );
        return RequeueOutcome.Requeued;
    }

    /// <summary>
    /// Splits a composite message id into its stream key and Redis entry id.
    /// </summary>
    /// <param name="compositeId">The composite id, normally <c>"{stream}:{redisId}"</c>.</param>
    /// <returns>A tuple of the stream key and the Redis entry id.</returns>
    /// <exception cref="ArgumentException">Thrown when the id does not match a known bulk stream prefix and has no parseable colon separator.</exception>
    /// <remarks>
    /// Known bulk stream prefixes are matched first (their keys themselves contain colons); the
    /// last-colon fallback handles any other well-formed composite id.
    /// </remarks>
    private (string stream, string id) ParseMessageId( string compositeId ) {
        // Format: stream:id where id may contain colons (Redis stream IDs are timestamp-sequence)
        foreach (string streamPrefix in new[] { _bulkTrackStream, _bulkAlbumStream }) {
            if (compositeId.StartsWith( streamPrefix + ":", StringComparison.OrdinalIgnoreCase )) {
                string id = compositeId[(streamPrefix.Length + 1)..];
                return (streamPrefix, id);
            }
        }

        // Fallback: assume last segment after colon is the ID
        int lastColon = compositeId.LastIndexOf( ':' );
        return lastColon <= 0
            ? throw new ArgumentException( $"Invalid composite message ID: {compositeId}", nameof( compositeId ) )
            : ((string stream, string id))(compositeId[..lastColon], compositeId[(lastColon + 1)..]);
    }

    #region LoggerMessage Methods

    /// <summary>Logs creation of a consumer group on a bulk stream.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="group">The consumer group name.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.ConsumerGroupCreated,
        Level = LogLevel.Information,
        Message = "Created consumer group {Group} for stream {Stream}" )]
    private static partial void LogConsumerGroupCreated( ILogger logger, string group, string stream );

    /// <summary>Logs that a consumer group already existed on a bulk stream (benign).</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="group">The consumer group name.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.ConsumerGroupExists,
        Level = LogLevel.Debug,
        Message = "Consumer group {Group} already exists for stream {Stream}" )]
    private static partial void LogConsumerGroupExists( ILogger logger, string group, string stream );

    /// <summary>Logs that a request was routed onto a type-specific bulk stream.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="lookupType">The lookup type of the routed request.</param>
    /// <param name="stream">The destination bulk stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.EnqueuedToBulkStream,
        Level = LogLevel.Debug,
        Message = "Enqueued {LookupType} request to type-specific bulk stream {Stream}" )]
    private static partial void LogEnqueuedToBulkStream( ILogger logger, string lookupType, string stream );

    /// <summary>Logs that pending entries were reclaimed from a stream via XAUTOCLAIM.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of entries reclaimed.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.AutoClaimRecovered,
        Level = LogLevel.Information,
        Message = "XAUTOCLAIM recovered {Count} pending entries from {Stream}" )]
    private static partial void LogAutoClaimRecovered( ILogger logger, int count, string stream );

    /// <summary>Logs that XAUTOCLAIM is unsupported; only dead-consumer recovery is disabled.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The Redis error returned by the server.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.AutoClaimNotSupported,
        Level = LogLevel.Warning,
        Message = "XAUTOCLAIM not supported on {Stream} — dead-consumer recovery disabled (XAUTOCLAIM requires Redis ≥ 6.2); current-consumer PEL recovery remains available" )]
    private static partial void LogAutoClaimNotSupported( ILogger logger, Exception ex, string stream );

    /// <summary>Logs that a stream entry payload failed to deserialize and was discarded.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The deserialization exception.</param>
    /// <param name="id">The Redis entry id.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.DeserializationError,
        Level = LogLevel.Error,
        Message = "Failed to deserialize message {Id} from {Stream}" )]
    private static partial void LogDeserializationError( ILogger logger, Exception ex, string id, string stream );

    /// <summary>Logs a non-fatal error while reading from a bulk stream.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.StreamReadWarning,
        Level = LogLevel.Warning,
        Message = "Error reading from stream {Stream}" )]
    private static partial void LogStreamReadWarning( ILogger logger, Exception ex, string stream );

    /// <summary>Logs that a message was acknowledged and deleted from its stream.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The Redis entry id acknowledged.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.MessageAcknowledged,
        Level = LogLevel.Debug,
        Message = "Acknowledged and deleted message {MessageId} from {Stream}" )]
    private static partial void LogMessageAcknowledged( ILogger logger, string messageId, string stream );

    /// <summary>Logs that a message targeted for requeue was not found.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The Redis entry id that was not found.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.MessageNotFound,
        Level = LogLevel.Warning,
        Message = "Message {MessageId} not found in {Stream} for requeue" )]
    private static partial void LogMessageNotFound( ILogger logger, string messageId, string stream );

    /// <summary>Logs that a message was requeued with an incremented attempt count.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="stream">The stream key.</param>
    /// <param name="attempt">The new attempt count after requeue.</param>
    [LoggerMessage(
        EventId = LogEventIds.MessageRequeued,
        Level = LogLevel.Debug,
        Message = "Requeued message from {Stream} (attempt {Attempt})" )]
    private static partial void LogMessageRequeued( ILogger logger, string stream, int attempt );

    /// <summary>Logs that a message exceeded the retry cap and was discarded.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The Redis entry id discarded.</param>
    /// <param name="maxRetries">The retry-attempt cap that was reached.</param>
    /// <param name="sagaId">The saga id associated with the message.</param>
    [LoggerMessage(
        EventId = LogEventIds.MaxRetriesExceeded,
        Level = LogLevel.Warning,
        Message = "Message {MessageId} in bulk stream exceeded {MaxRetries} retry attempts; discarding (saga={SagaId})" )]
    private static partial void LogMaxRetriesExceeded( ILogger logger, string messageId, int maxRetries, string sagaId );

    /// <summary>Logs that a message with an unserializable payload was discarded as poison.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The Redis entry id discarded.</param>
    /// <param name="sagaId">The saga id associated with the message.</param>
    [LoggerMessage(
        EventId = LogEventIds.PoisonPayloadDiscarded,
        Level = LogLevel.Warning,
        Message = "Message {MessageId} in bulk stream has an unserializable payload; discarding (saga={SagaId})" )]
    private static partial void LogPoisonPayloadDiscarded( ILogger logger, string messageId, string sagaId );

    /// <summary>Logs that a malformed <c>enqueuedAt</c> field was encountered and treated as absent.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="enqueuedAtStr">The raw, unparseable timestamp value.</param>
    /// <param name="stream">The stream key.</param>
    [LoggerMessage(
        EventId = LogEventIds.MalformedEnqueuedAt,
        Level = LogLevel.Warning,
        Message = "Malformed enqueuedAt field '{EnqueuedAtStr}' in stream {Stream}; treating as absent" )]
    private static partial void LogMalformedEnqueuedAt( ILogger logger, string enqueuedAtStr, string stream );

    #endregion
}

/// <summary>
/// Snapshot of the Spotify bulk-id stream depths used to drive batch flush decisions.
/// </summary>
/// <param name="TrackIdCount">Number of pending track-id entries (mirrors <paramref name="BulkTrackIdCount"/>).</param>
/// <param name="AlbumIdCount">Number of pending album-id entries (mirrors <paramref name="BulkAlbumIdCount"/>).</param>
/// <param name="BulkTrackIdCount">Length of the bulk track-id stream.</param>
/// <param name="BulkAlbumIdCount">Length of the bulk album-id stream.</param>
public sealed record IdLookupDepth(
    int TrackIdCount,
    int AlbumIdCount,
    int BulkTrackIdCount,
    int BulkAlbumIdCount
);

/// <summary>
/// Result of a bulk-stream requeue attempt.
/// </summary>
public enum RequeueOutcome {
    /// <summary>The message was re-added to its stream with an incremented attempt count.</summary>
    Requeued,
    /// <summary>The retry cap was reached, or the payload was unserializable; the message was discarded.</summary>
    CapReached,
    /// <summary>The original entry no longer existed in the stream, so nothing was requeued.</summary>
    NotFound
}
