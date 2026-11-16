using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using QueueFieldNames = BridgeBeats.Core.Infrastructure.Queue.QueueStreamFieldNames;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Decorates the Spotify <see cref="IRequestQueue{T}"/> to route typed ID lookups
/// directly into the Spotify type-specific bulk streams, bypassing the generic
/// priority queue.
/// </summary>
/// <remarks>
/// Applied only to the Spotify <see cref="QueuedLookupRequest"/> queue. Spotify's API supports
/// multi-id batch fetches, so non-interactive <c>SongIdLookup</c> and <c>AlbumIdLookup</c>
/// enqueues are re-routed to <see cref="BridgeBeats.Contracts.Constants.SpotifyConstants.BulkTrackIdStream"/>
/// (<c>queue:spotify:bulk:track-id</c>) or
/// <see cref="BridgeBeats.Contracts.Constants.SpotifyConstants.BulkAlbumIdStream"/>
/// (<c>queue:spotify:bulk:album-id</c>), where a separate bulk processor drains them in batches.
/// Interactive-priority requests and every other lookup type pass straight through to the inner
/// queue, and all queue operations other than enqueue delegate to the inner queue unchanged.
/// </remarks>
public sealed partial class SpotifyBulkQueueDecorator : IRequestQueue<QueuedLookupRequest>, IConsumerGroupAssurance, IQueueDeliveryTracker, IQueueWorkSignal {

    private const string EnqueueBulkDeliveryScript = """
        local messageId = redis.call(
            'XADD', KEYS[1], '*', ARGV[1], ARGV[2], ARGV[3], ARGV[4])
        redis.call('PUBLISH', ARGV[5], messageId)
        return messageId
        """;

    /// <summary>The wrapped queue that handles normal (non-bulk-routed) operations.</summary>
    private readonly IRequestQueue<QueuedLookupRequest> _inner;

    /// <summary>Redis connection used to write directly to the bulk streams.</summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>Logger for bulk-routing diagnostics.</summary>
    private readonly ILogger<SpotifyBulkQueueDecorator> _logger;
    private readonly string _bulkTrackStream;
    private readonly string _bulkAlbumStream;
    private readonly string _bulkWorkSignalChannel;

    /// <summary>Camel-case, non-indented options used to serialize the request payload.</summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpotifyBulkQueueDecorator"/> class around an
    /// inner queue.
    /// </summary>
    /// <param name="inner">The underlying Spotify request queue, which handles non-bulk operations.</param>
    /// <param name="redis">The Redis connection multiplexer, used to write bulk-stream entries.</param>
    /// <param name="logger">Logger for bulk-routing diagnostics.</param>
    /// <param name="keyPrefix">Optional isolated queue-key prefix shared with the bulk consumer.</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public SpotifyBulkQueueDecorator(
        IRequestQueue<QueuedLookupRequest> inner,
        IConnectionMultiplexer redis,
        ILogger<SpotifyBulkQueueDecorator> logger,
        string? keyPrefix = null
    ) {
        _inner = inner ?? throw new ArgumentNullException( nameof( inner ) );
        _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _bulkTrackStream = QueueStreamKeys.SpotifyBulkFor( isTrack: true, keyPrefix );
        _bulkAlbumStream = QueueStreamKeys.SpotifyBulkFor( isTrack: false, keyPrefix );
        _bulkWorkSignalChannel = QueueStreamKeys.SpotifyBulkWorkSignal( keyPrefix );

        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Enqueues a request, routing non-interactive single-id Spotify track/album lookups onto a
    /// dedicated bulk stream and delegating everything else to the inner queue.
    /// </summary>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="priority">The requested priority. Interactive requests are never bulk-routed.</param>
    /// <param name="cancellationToken">Token used when delegating to the inner queue.</param>
    /// <returns>A task that completes once the request is written.</returns>
    /// <remarks>
    /// When the lookup type is <see cref="LookupRequestType.SongIdLookup"/> or
    /// <see cref="LookupRequestType.AlbumIdLookup"/> and the priority is not
    /// <see cref="QueuePriority.Interactive"/>, the request is serialized and added directly to the
    /// matching bulk stream with <c>payload</c> and <c>enqueuedAt</c> fields, and an enqueue metric
    /// is recorded at bulk priority. When <paramref name="priority"/> is
    /// <see cref="QueuePriority.Interactive"/>, typed ID lookups pass through to the inner queue so
    /// they are served on the interactive stream with a single-item <c>GetInfoByIDAsync</c> call,
    /// preserving the interactive latency budget. All other lookup types are forwarded to the
    /// underlying queue unchanged regardless of priority.
    /// </remarks>
    public async Task EnqueueAsync(
        QueuedLookupRequest request,
        QueuePriority priority,
        CancellationToken cancellationToken = default
    ) {
        if (request.LookupType is LookupRequestType.SongIdLookup or LookupRequestType.AlbumIdLookup
            && priority != QueuePriority.Interactive
            && !request.BypassBulkRouting) {

            string stream = request.LookupType == LookupRequestType.SongIdLookup
                ? _bulkTrackStream
                : _bulkAlbumStream;

            string payload = JsonSerializer.Serialize( request, _jsonOptions );

            IDatabase db = _redis.GetDatabase( );
            NameValueEntry[] fields = [
                new NameValueEntry( QueueFieldNames.Payload, payload ),
                new NameValueEntry( QueueFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
            ];

            RedisResult enqueueResult = await db.ScriptEvaluateAsync(
                EnqueueBulkDeliveryScript,
                [stream],
                [
                    QueueFieldNames.Payload,
                    fields[0].Value,
                    QueueFieldNames.EnqueuedAt,
                    fields[1].Value,
                    _bulkWorkSignalChannel
                ] );
            RedisValue messageId = (RedisValue)enqueueResult;
            if (!messageId.HasValue) {
                throw new InvalidOperationException( $"Redis did not return a message id for enqueue to '{stream}'." );
            }

            // Record enqueue metric with QueuePriority.Bulk so dashboards can distinguish
            // bulk-stream enqueues from generic interactive/background ones.
            QueueMetrics.RecordEnqueue( SupportedProviders.Spotify, QueuePriority.Bulk, request.EnqueueOrigin );

            LogRoutedToBulkStream( _logger, request.LookupType, request.SagaId, stream );

            return;
        }

        // Interactive SongIdLookup/AlbumIdLookup, and all other lookup types, pass through
        // to the inner queue so they are dispatched on the caller's intended priority lane.
        await _inner.EnqueueAsync( request, priority, cancellationToken );
    }

    /// <summary>Delegates to the inner queue. See <see cref="IRequestQueue{T}.DequeueAsync(CancellationToken)"/>.</summary>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>The next message, or null if none is available.</returns>
    public Task<QueuedMessage<QueuedLookupRequest>?> DequeueAsync( CancellationToken cancellationToken = default )
        => _inner.DequeueAsync( cancellationToken );

    /// <summary>Delegates to the inner queue's rate-limit-aware dequeue.</summary>
    /// <param name="rateLimitTracker">Tracker that identifies currently rate-limited endpoints.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>The next eligible message, or null if none is available.</returns>
    public Task<QueuedMessage<QueuedLookupRequest>?> DequeueAsync( IRateLimitTracker rateLimitTracker, CancellationToken cancellationToken = default )
        => _inner.DequeueAsync( rateLimitTracker, cancellationToken );

    /// <summary>Delegates to the inner queue to acknowledge (XACK + XDEL) a message.</summary>
    /// <param name="messageId">Composite message id to acknowledge.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>A task that completes when the message is acknowledged.</returns>
    public Task AcknowledgeAsync( string messageId, CancellationToken cancellationToken = default )
        => _inner.AcknowledgeAsync( messageId, cancellationToken );

    /// <summary>Delegates to the inner queue to requeue a message.</summary>
    /// <param name="messageId">Composite message id to requeue.</param>
    /// <param name="delay">Requested delay; honored only to the extent the inner queue honors it.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>A task that completes when the message is requeued.</returns>
    public Task RequeueAsync( string messageId, TimeSpan? delay = null, CancellationToken cancellationToken = default )
        => _inner.RequeueAsync( messageId, delay, cancellationToken );

    /// <summary>Delegates to the inner queue to read current per-lane depths.</summary>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>The current queue depth across the priority lanes.</returns>
    public Task<QueueDepth> GetDepthAsync( CancellationToken cancellationToken = default )
        => _inner.GetDepthAsync( cancellationToken );

    /// <summary>Delegates to the inner queue to read dead-letter messages.</summary>
    /// <param name="limit">Maximum number of dead-letter messages to return.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>The dead-letter messages, up to <paramref name="limit"/>.</returns>
    public Task<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>> GetDlqMessagesAsync( int limit, CancellationToken cancellationToken = default )
        => _inner.GetDlqMessagesAsync( limit, cancellationToken );

    /// <summary>Delegates to the inner queue to move a message out of the dead-letter queue.</summary>
    /// <param name="messageId">Composite dead-letter message id.</param>
    /// <param name="priority">Priority lane to requeue the message onto.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>A task that completes when the message is requeued.</returns>
    public Task RequeueFromDlqAsync( string messageId, QueuePriority priority, CancellationToken cancellationToken = default )
        => _inner.RequeueFromDlqAsync( messageId, priority, cancellationToken );

    /// <summary>Delegates to the inner queue to move a message into the dead-letter queue.</summary>
    /// <param name="messageId">Composite message id to move.</param>
    /// <param name="reason">Reason recorded with the dead-letter entry.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>A task that completes when the message is moved.</returns>
    public Task MoveToDlqAsync( string messageId, string reason, CancellationToken cancellationToken = default )
        => _inner.MoveToDlqAsync( messageId, reason, cancellationToken );

    /// <summary>Delegates to the inner queue to delete a dead-letter message.</summary>
    /// <param name="messageId">Composite dead-letter message id to delete.</param>
    /// <param name="cancellationToken">Token forwarded to the inner queue.</param>
    /// <returns>True if a message was deleted; otherwise false.</returns>
    public Task<bool> DeleteFromDlqAsync( string messageId, CancellationToken cancellationToken = default )
        => _inner.DeleteFromDlqAsync( messageId, cancellationToken );

    /// <inheritdoc/>
    public void ReleaseDelivery( string messageId ) {
        if (_inner is IQueueDeliveryTracker tracker) tracker.ReleaseDelivery( messageId );
    }

    /// <inheritdoc/>
    public Task InitializeWorkSignalAsync( CancellationToken cancellationToken = default ) =>
        _inner is IQueueWorkSignal signal ? signal.InitializeWorkSignalAsync( cancellationToken ) : Task.CompletedTask;

    /// <inheritdoc/>
    public long CaptureWorkVersion( ) =>
        _inner is IQueueWorkSignal signal ? signal.CaptureWorkVersion( ) : 0;

    /// <inheritdoc/>
    public Task WaitForWorkAsync(
        long observedVersion,
        DateTimeOffset? scheduledWake,
        CancellationToken cancellationToken = default
    ) => _inner is IQueueWorkSignal signal
        ? signal.WaitForWorkAsync( observedVersion, scheduledWake, cancellationToken )
        : Task.CompletedTask;

    /// <summary>Repairs the generic queue groups and both bulk-stream groups.</summary>
    public async Task EnsureConsumerGroupsAsync( CancellationToken cancellationToken = default ) {
        if (_inner is IConsumerGroupAssurance innerAssurance) {
            await innerAssurance.EnsureConsumerGroupsAsync( cancellationToken );
        }

        IDatabase db = _redis.GetDatabase( );
        foreach (string stream in new[] { _bulkTrackStream, _bulkAlbumStream }) {
            cancellationToken.ThrowIfCancellationRequested( );
            try {
                _ = await db.StreamCreateConsumerGroupAsync(
                    stream, SpotifyConstants.ConsumerGroup, StreamPosition.Beginning, createStream: true );
            } catch (RedisServerException ex) when (ex.Message.Contains( "BUSYGROUP", StringComparison.OrdinalIgnoreCase )) {
                // Idempotent repair: the required group already exists.
            }
        }
    }

    #region LoggerMessage Methods

    /// <summary>
    /// Logs (at debug level) that a typed ID lookup was routed to a Spotify type-specific bulk stream.
    /// </summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="lookupType">The lookup type that was routed.</param>
    /// <param name="sagaId">The saga id of the routed request.</param>
    /// <param name="stream">The bulk stream the request was written to.</param>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "SpotifyBulkQueueDecorator routed {LookupType} (SagaId={SagaId}) to {Stream}" )]
    private static partial void LogRoutedToBulkStream(
        ILogger logger,
        LookupRequestType lookupType,
        string sagaId,
        string stream );

    #endregion
}
