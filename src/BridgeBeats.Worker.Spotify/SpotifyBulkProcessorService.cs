using System.Diagnostics;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Spotify;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Worker.Spotify.Logging;
using BridgeBeats.Worker.Spotify.Metrics;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Worker.Spotify;

/// <summary>
/// Background service that batches Spotify single-id track and album lookups and resolves them via
/// Spotify's multi-id bulk API.
/// </summary>
/// <remarks>
/// Spotify's API allows fetching many ids in one call, so single-id lookups are diverted onto
/// dedicated bulk streams (drained through <see cref="SpotifyBatchQueueHelper"/>) and processed here
/// in batches. The loop wakes on broker notifications and flushes a stream when its depth reaches
/// the per-batch maximum or when its oldest queued item exceeds the configured linger window
/// (<see cref="ShouldFlush"/>). On success it drives the same saga update and completion-publish path
/// as the per-message queue processor. Failure handling has four distinct shapes: a rate-limit
/// (<see cref="ProviderRateLimitException"/>, including its <see cref="RetryAfterExceededException"/> subtype)
/// marks affected sagas partial and requeues the whole
/// batch; a deterministic 4xx rejection (<see cref="SpotifyBulkRejectedException"/>) atomically
/// transfers each item to the single-item lane, preserving an interactive origin and otherwise
/// using background priority (isolating any poison id); an empty result dictionary is treated as a
/// transient request-level failure that arms an exponential per-stream cooldown and requeues the
/// batch; and an absent key within a non-empty result is a per-id partial that requeues only that
/// message.
/// </remarks>
public sealed partial class SpotifyBulkProcessorService : BackgroundService {

    /// <summary>Redis connection multiplexer used to publish completion and sentinel events.</summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>Helper that owns the bulk-stream consumer groups, dequeue, ack, and requeue.</summary>
    private readonly SpotifyBatchQueueHelper _batchHelper;

    /// <summary>Tracker used to read policy-derived shared rate-limit state.</summary>
    private readonly IRateLimitTracker _rateLimitTracker;

    /// <summary>Manager used to create, update, and inspect saga state.</summary>
    private readonly ISagaStateManager _sagaManager;

    /// <summary>Spotify bulk lookup service that performs the multi-id API calls.</summary>
    private readonly ISpotifyBulkLookupService _lookupService;

    /// <summary>
    /// The Spotify request queue used for generic dead-letter operations initiated by the bulk
    /// processor. Rejected-batch forwarding is owned by <see cref="SpotifyBatchQueueHelper"/> so
    /// replacement enqueue and source cleanup can be one atomic Redis operation.
    /// </summary>
    private readonly IRequestQueue<QueuedLookupRequest> _requestQueue;

    /// <summary>Logger for this service's structured log events.</summary>
    private readonly ILogger<SpotifyBulkProcessorService> _logger;

    /// <summary>JSON options used to serialize provider results into saga state (camelCase, compact).</summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>The batch linger window; a batch under the size threshold flushes once its oldest item reaches this age.</summary>
    private readonly TimeSpan _batchLinger;

    /// <summary>Base request-failure cooldown in seconds used as the exponent base for backoff.</summary>
    private readonly int _requestFailureCooldownSeconds;

    /// <summary>Queue timing settings shared with the single-item processor.</summary>
    private readonly QueueSettings _queueSettings;

    // Request-failure cooldown state — suppresses flush after empty-dict (network/auth error)
    // to avoid burning retry attempts faster than Spotify can recover.
    // Cooldown doubles per consecutive failure, capped at SpotifyBatchSettings.MaxRequestFailureCooldownSeconds.
    // Each stream maintains its own independent local cooldown: a tracks-operation failure does not
    // suppress album flushes (and vice versa). Shared Spotify HTTP rate limits are provider-wide.

    /// <summary>Time until which the track-id stream is in request-failure cooldown.</summary>
    private DateTimeOffset _trackIdCooldownUntil = DateTimeOffset.MinValue;

    /// <summary>Count of consecutive track-id request failures, driving exponential cooldown growth.</summary>
    private int _consecutiveTrackIdFailures;

    /// <summary>Time until which the album-id stream is in request-failure cooldown.</summary>
    private DateTimeOffset _albumIdCooldownUntil = DateTimeOffset.MinValue;

    /// <summary>Count of consecutive album-id request failures, driving exponential cooldown growth.</summary>
    private int _consecutiveAlbumIdFailures;

    /// <summary>Pub/Sub channel on which saga-level completion is published.</summary>
    private const string SagaCompletedChannel = "saga:completed";

    /// <summary>Scheduled recovery event after an unhandled loop error (5 seconds).</summary>
    private static readonly TimeSpan s_errorDelay = TimeSpan.FromSeconds( 5 );

    /// <summary>
    /// Initializes the service from its dependencies and binds the batch linger and failure-cooldown
    /// settings.
    /// </summary>
    /// <param name="redis">Redis connection multiplexer for publishing completion events.</param>
    /// <param name="batchHelper">Helper for bulk-stream dequeue, ack, and requeue.</param>
    /// <param name="rateLimitTracker">Tracker for policy-derived shared rate-limit state.</param>
    /// <param name="sagaManager">Manager for saga state.</param>
    /// <param name="lookupService">Spotify bulk lookup service.</param>
    /// <param name="requestQueue">
    /// The Spotify request queue used for generic dead-letter operations. Must be the queue
    /// registered for <see cref="SupportedProviders.Spotify"/> (the
    /// <c>SpotifyBulkQueueDecorator</c>-wrapped instance registered by <c>AddQueueProcessor</c>).
    /// </param>
    /// <param name="logger">Logger for the service.</param>
    /// <param name="batchSettings">Bound batch settings supplying the linger window and base cooldown.</param>
    /// <param name="queueSettings">Bound queue settings supplying the absolute job lifetime.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency or <paramref name="batchSettings"/> is <see langword="null"/>.</exception>
    public SpotifyBulkProcessorService(
        IConnectionMultiplexer redis,
        SpotifyBatchQueueHelper batchHelper,
        IRateLimitTracker rateLimitTracker,
        ISagaStateManager sagaManager,
        ISpotifyBulkLookupService lookupService,
        IRequestQueue<QueuedLookupRequest> requestQueue,
        ILogger<SpotifyBulkProcessorService> logger,
        IOptions<SpotifyBatchSettings> batchSettings,
        IOptions<QueueSettings> queueSettings
    ) {
        _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
        _batchHelper = batchHelper ?? throw new ArgumentNullException( nameof( batchHelper ) );
        _rateLimitTracker = rateLimitTracker ?? throw new ArgumentNullException( nameof( rateLimitTracker ) );
        _sagaManager = sagaManager ?? throw new ArgumentNullException( nameof( sagaManager ) );
        _lookupService = lookupService ?? throw new ArgumentNullException( nameof( lookupService ) );
        _requestQueue = requestQueue ?? throw new ArgumentNullException( nameof( requestQueue ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        ArgumentNullException.ThrowIfNull( batchSettings );
        ArgumentNullException.ThrowIfNull( queueSettings );
        int lingerMs = batchSettings.Value.LingerMs;
        _batchLinger = TimeSpan.FromMilliseconds( lingerMs );
        _requestFailureCooldownSeconds = batchSettings.Value.RequestFailureCooldownSeconds;
        _queueSettings = queueSettings.Value;
        if (_batchLinger >= TimeSpan.FromMinutes( _queueSettings.JobExpirationMinutes )) {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof( SpotifyBatchSettings ),
                ["BridgeBeats:Queue:JobExpirationMinutes must exceed BridgeBeats:Spotify:Batch:LingerMs."] );
        }

        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Ensures the bulk-stream consumer groups exist, then reacts to enqueue notifications and exact
    /// linger/rate-limit/cooldown expiry events until cancellation.
    /// </summary>
    /// <param name="stoppingToken">Token signaled when the host is shutting down.</param>
    /// <returns>A task that completes when the loop exits.</returns>
    /// <remarks>Unhandled errors are retried on a scheduled event rather than blocking a worker thread.</remarks>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        LogServiceStarting( _logger );

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await _batchHelper.InitializeWorkSignalAsync( stoppingToken );
                break;
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                LogProcessorLoopError( _logger, ex );
                long failedVersion = _batchHelper.CaptureWorkVersion( );
                await _batchHelper.WaitForWorkAsync(
                    failedVersion, DateTimeOffset.UtcNow.Add( s_errorDelay ), stoppingToken );
            }
        }

        // Ensure consumer groups exist for type-specific streams
        try {
            await _batchHelper.EnsureConsumerGroupsAsync( stoppingToken );
        } catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested) {
            LogProcessorLoopError( _logger, ex );
            long failedVersion = _batchHelper.CaptureWorkVersion( );
            await _batchHelper.WaitForWorkAsync(
                failedVersion, DateTimeOffset.UtcNow.Add( s_errorDelay ), stoppingToken );
        }

        while (!stoppingToken.IsCancellationRequested) {
            long observedWorkVersion = _batchHelper.CaptureWorkVersion( );
            try {
                // Check if we should process bulk track lookups
                if (await ShouldProcessBulkTracksAsync( stoppingToken )) {
                    await ProcessBulkTrackLookupsAsync( stoppingToken );
                }

                // Check if we should process bulk album lookups
                if (await ShouldProcessBulkAlbumsAsync( stoppingToken )) {
                    await ProcessBulkAlbumLookupsAsync( stoppingToken );
                }

                DateTimeOffset? nextWake = await GetNextBulkWakeAsync( stoppingToken );
                await _batchHelper.WaitForWorkAsync( observedWorkVersion, nextWake, stoppingToken );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (RedisServerException ex) when (ex.Message.Contains( "NOGROUP", StringComparison.OrdinalIgnoreCase )) {
                try {
                    await _batchHelper.EnsureConsumerGroupsAsync( stoppingToken );
                } catch (Exception repairEx) when (repairEx is not OperationCanceledException || !stoppingToken.IsCancellationRequested) {
                    LogProcessorLoopError( _logger, repairEx );
                    await _batchHelper.WaitForWorkAsync(
                        observedWorkVersion, DateTimeOffset.UtcNow.Add( s_errorDelay ), stoppingToken );
                }
            } catch (Exception ex) {
                LogProcessorLoopError( _logger, ex );
                await _batchHelper.WaitForWorkAsync(
                    observedWorkVersion, DateTimeOffset.UtcNow.Add( s_errorDelay ), stoppingToken );
            }
        }

        LogServiceStopping( _logger );
    }

    private async Task<DateTimeOffset?> GetNextBulkWakeAsync( CancellationToken ct ) {
        IdLookupDepth depth = await _batchHelper.GetIdLookupDepthAsync( ct );
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<DateTimeOffset> candidates = [];

        await AddCandidateAsync(
            depth.BulkTrackIdCount,
            SpotifyConstants.MaxTracksPerBatchLookup,
            SpotifyConstants.TracksEndpoint,
            isTracks: true,
            _trackIdCooldownUntil );
        await AddCandidateAsync(
            depth.BulkAlbumIdCount,
            SpotifyConstants.MaxAlbumsPerBatchLookup,
            SpotifyConstants.AlbumsEndpoint,
            isTracks: false,
            _albumIdCooldownUntil );

        return candidates.Count == 0 ? null : candidates.Min( );

        async Task AddCandidateAsync(
            int count,
            int threshold,
            string endpoint,
            bool isTracks,
            DateTimeOffset cooldownUntil
        ) {
            if (count == 0) return;

            DateTimeOffset candidate = now;
            RateLimitState rateLimit = await _rateLimitTracker.GetStateAsync(
                SupportedProviders.Spotify, endpoint, ct );
            if (rateLimit.IsRateLimited && rateLimit.RetryAfter is { } retryAfter && retryAfter > candidate) {
                candidate = retryAfter;
            }
            if (cooldownUntil > candidate) candidate = cooldownUntil;

            if (count < threshold) {
                DateTimeOffset? oldest = await _batchHelper.GetOldestEnqueuedAtAsync( isTracks, ct );
                if (oldest is null) {
                    // An unparseable timestamp must not strand a low-volume batch.
                    candidates.Add( candidate );
                    return;
                }
                DateTimeOffset lingerDue = oldest.Value.Add( _batchLinger );
                if (lingerDue > candidate) candidate = lingerDue;
            }

            candidates.Add( candidate );
        }
    }

    // Pure flush predicate — extracted for testability

    /// <summary>
    /// Decides whether a bulk batch should be flushed now, based on its size and the age of its
    /// oldest queued item (the batch linger window).
    /// </summary>
    /// <param name="count">The current number of queued items.</param>
    /// <param name="oldestAge">The age of the oldest queued item, or <see langword="null"/> when unknown.</param>
    /// <param name="threshold">The per-batch size at which a flush is forced.</param>
    /// <param name="linger">The maximum age the oldest item may reach before a flush is forced.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="count"/> reaches <paramref name="threshold"/>, or
    /// when there is at least one item whose age has reached <paramref name="linger"/>; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool ShouldFlush( int count, TimeSpan? oldestAge, int threshold, TimeSpan linger ) {
        if (count >= threshold) {
            return true;
        }
        return count > 0 && oldestAge.HasValue && oldestAge.Value >= linger;
    }

    // Flush-decision methods

    /// <summary>
    /// Determines whether the bulk track-id stream should be flushed this cycle.
    /// </summary>
    /// <param name="ct">Token used to cancel the checks.</param>
    /// <returns>
    /// <see langword="false"/> when the bulk-tracks endpoint is rate-limited, the stream is in
    /// request-failure cooldown, or the stream is empty; otherwise the result of
    /// <see cref="ShouldFlush"/> for the current depth and oldest-item age.
    /// </returns>
    private async Task<bool> ShouldProcessBulkTracksAsync( CancellationToken ct ) {
        // Rate-limit guard: don't call the bulk endpoint if it's currently limited
        RateLimitState rateLimitState = await _rateLimitTracker.GetStateAsync(
            SupportedProviders.Spotify,
            SpotifyConstants.TracksEndpoint,
            ct
        );

        if (rateLimitState.IsRateLimited) {
            if (_logger.IsEnabled( LogLevel.Warning )) {
                string retryAfterStr = rateLimitState.RetryAfter?.ToString( ) ?? "unknown";
                LogRateLimited( _logger, "Bulk tracks", retryAfterStr );
            }
            return false;
        }

        // Request-failure cooldown guard: suppress flush while in backoff after an
        // empty-dict failure so all attempts are not burned during a transient Spotify outage.
        if (DateTimeOffset.UtcNow < _trackIdCooldownUntil) {
            return false;
        }

        IdLookupDepth depth = await _batchHelper.GetIdLookupDepthAsync( ct );
        if (depth.BulkTrackIdCount == 0) {
            return false;
        }

        TimeSpan? oldestAge = null;
        if (depth.BulkTrackIdCount < SpotifyConstants.MaxTracksPerBatchLookup) {
            DateTimeOffset? oldest = await _batchHelper.GetOldestEnqueuedAtAsync( isTracks: true, ct );
            if (oldest.HasValue) {
                oldestAge = DateTimeOffset.UtcNow - oldest.Value;
            } else {
                oldestAge = _batchLinger;
            }
        }

        return ShouldFlush( depth.BulkTrackIdCount, oldestAge, SpotifyConstants.MaxTracksPerBatchLookup, _batchLinger );
    }

    /// <summary>
    /// Determines whether the bulk album-id stream should be flushed this cycle.
    /// </summary>
    /// <param name="ct">Token used to cancel the checks.</param>
    /// <returns>
    /// <see langword="false"/> when the bulk-albums endpoint is rate-limited, the stream is in
    /// request-failure cooldown, or the stream is empty; otherwise the result of
    /// <see cref="ShouldFlush"/> for the current depth and oldest-item age.
    /// </returns>
    private async Task<bool> ShouldProcessBulkAlbumsAsync( CancellationToken ct ) {
        // Rate-limit guard: don't call the bulk endpoint if it's currently limited
        RateLimitState rateLimitState = await _rateLimitTracker.GetStateAsync(
            SupportedProviders.Spotify,
            SpotifyConstants.AlbumsEndpoint,
            ct
        );

        if (rateLimitState.IsRateLimited) {
            if (_logger.IsEnabled( LogLevel.Warning )) {
                string retryAfterStr = rateLimitState.RetryAfter?.ToString( ) ?? "unknown";
                LogRateLimited( _logger, "Bulk albums", retryAfterStr );
            }
            return false;
        }

        // Request-failure cooldown guard
        if (DateTimeOffset.UtcNow < _albumIdCooldownUntil) {
            return false;
        }

        IdLookupDepth depth = await _batchHelper.GetIdLookupDepthAsync( ct );
        if (depth.BulkAlbumIdCount == 0) {
            return false;
        }

        TimeSpan? oldestAge = null;
        if (depth.BulkAlbumIdCount < SpotifyConstants.MaxAlbumsPerBatchLookup) {
            DateTimeOffset? oldest = await _batchHelper.GetOldestEnqueuedAtAsync( isTracks: false, ct );
            if (oldest.HasValue) {
                oldestAge = DateTimeOffset.UtcNow - oldest.Value;
            } else {
                oldestAge = _batchLinger;
            }
        }

        return ShouldFlush( depth.BulkAlbumIdCount, oldestAge, SpotifyConstants.MaxAlbumsPerBatchLookup, _batchLinger );
    }

    // Batch-processing methods

    /// <summary>
    /// Dequeues and resolves a batch of track-id lookups via the bulk Spotify API, then applies each
    /// result to its saga.
    /// </summary>
    /// <param name="ct">Token used to stop processing early.</param>
    /// <returns>A task that completes once the batch has been resolved, applied, or requeued.</returns>
    /// <remarks>
    /// Messages are grouped by track id so duplicates share one API result. An empty result
    /// dictionary is treated as a transient request-level failure: it arms the exponential track-id
    /// cooldown and requeues the whole batch. A present-but-<see langword="null"/> result records a
    /// not-found provider state; an absent key requeues only that message. A
    /// <see cref="ProviderRateLimitException"/> is routed to <see cref="HandleBulkRateLimitAsync"/>.
    /// A <see cref="SpotifyBulkRejectedException"/> (deterministic 4xx) atomically transfers each
    /// item to its origin-aware single-item lane while removing the original bulk delivery so the
    /// per-message processor handles it individually. Any other exception requeues the whole batch.
    /// </remarks>
    internal async Task ProcessBulkTrackLookupsAsync( CancellationToken ct ) {
        LogProcessingBulkTracks( _logger );

        // Dequeue directly from the type-specific stream only.
        // Top-up from generic priority streams is intentionally omitted (would race the generic worker's unacked deliveries).
        DateTimeOffset dequeueWallStart = DateTimeOffset.UtcNow;
        Stopwatch dequeueWallTimer = Stopwatch.StartNew( );
        List<QueuedMessage<QueuedLookupRequest>> messages =
            [.. await _batchHelper.DequeueTrackIdBatchAsync( SpotifyConstants.MaxTracksPerBatchLookup, ct )];

        if (messages.Count == 0) {
            LogNoTrackLookups( _logger );
            return;
        }

        try {
            await RejectIneligibleMessagesAsync(
                messages,
                SpotifyConstants.TracksEndpoint,
                ct,
                dequeueWallTimer,
                dequeueWallStart );
            if (messages.Count == 0) {
                return;
            }

            LogProcessingTrackCount( _logger, messages.Count );
            SpotifyBatchMetrics.RecordBatchSize( "tracks", messages.Count );

            // Build a track-ID → messages map so each bulk API result maps back to its request(s)
            Dictionary<string, List<QueuedMessage<QueuedLookupRequest>>> idToMessages = [];
            foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
                string trackId = message.Payload.LookupValue;
                if (!idToMessages.TryGetValue( trackId, out List<QueuedMessage<QueuedLookupRequest>>? value )) {
                    value = [];
                    idToMessages[trackId] = value;
                }
                value.Add( message );
            }

            // Call the bulk lookup API (GET /tracks?ids=...)
            Stopwatch httpTimer = Stopwatch.StartNew( );
            using Activity? httpActivity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.provider_http" );
            _ = (httpActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
            _ = (httpActivity?.SetTag( QueueMetricTags.Priority, "bulk" ));
            Dictionary<string, MusicLookupResult?> results;
            try { results = await _lookupService.GetTracksByIdsAsync( idToMessages.Keys ); } finally { httpTimer.Stop( ); httpActivity?.Stop( ); QueueMetrics.ProviderHttpDuration.Record( httpTimer.Elapsed.TotalSeconds, new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ), new KeyValuePair<string, object?>( QueueMetricTags.Priority, "bulk" ) ); }

            // Empty dictionary = request failure (auth error, network error).
            // Requeue ALL messages without writing any saga state. Arm the request-failure
            // cooldown so the linger-trigger does not immediately re-flush.
            if (results.Count == 0) {
                LogBulkDispatchRequeuingAll( _logger, messages.Count, "empty-dict (request failure)" );
                ArmRequestFailureCooldown( ref _trackIdCooldownUntil, ref _consecutiveTrackIdFailures );
                await RequeueAllAsync( messages, ct, dequeueWallTimer, dequeueWallStart );
                return;
            }

            // Success: reset the consecutive-failure counter for this stream.
            _consecutiveTrackIdFailures = 0;
            _trackIdCooldownUntil = DateTimeOffset.MinValue;

            // Dispatch each result to its queued messages
            foreach (KeyValuePair<string, List<QueuedMessage<QueuedLookupRequest>>> kvp in idToMessages) {
                string trackId = kvp.Key;
                bool keyPresent = results.TryGetValue( trackId, out MusicLookupResult? result );

                foreach (QueuedMessage<QueuedLookupRequest> message in kvp.Value) {
                    if (!keyPresent) {
                        // Key absent from non-empty dict = partial parse failure.
                        // Requeue this individual message, no saga write.
                        LogBulkDispatchRequeuingOne( _logger, trackId, "absent key (partial parse)" );
                        await RequeueSingleAsync( message, ct, dequeueWallTimer, dequeueWallStart );
                    } else {
                        // key present, result may be null (genuine not-found) — correct path
                        await ProcessBulkResultAsync( message, result, ct, dequeueWallTimer, dequeueWallStart );
                    }
                }
            }

            LogTrackLookupsSuccess( _logger, messages.Count );
        } catch (ProviderRateLimitException ex) {
            await HandleBulkRateLimitAsync( messages, SpotifyConstants.TracksEndpoint, ex, ct, dequeueWallTimer, dequeueWallStart );
        } catch (SpotifyBulkRejectedException ex) {
            await HandleBulkRejectionAsync( messages, ex, ct, dequeueWallTimer, dequeueWallStart );
        } catch (PostCommitAcknowledgementException) {
            throw;
        } catch (TerminalDlqRecoveryException) {
            throw;
        } catch (QueueDeliveryIdentityQuarantineException) {
            throw;
        } catch (Exception ex) {
            LogTrackLookupsError( _logger, ex );
            ArmRequestFailureCooldown( ref _trackIdCooldownUntil, ref _consecutiveTrackIdFailures );
            await RequeueAllAsync( messages, ct, dequeueWallTimer, dequeueWallStart );
        }
    }

    /// <summary>
    /// Dequeues and resolves a batch of album-id lookups via the bulk Spotify API, then applies each
    /// result to its saga.
    /// </summary>
    /// <param name="ct">Token used to stop processing early.</param>
    /// <returns>A task that completes once the batch has been resolved, applied, or requeued.</returns>
    /// <remarks>
    /// Mirrors <see cref="ProcessBulkTrackLookupsAsync"/> for albums: messages are grouped by album
    /// id, an empty result dictionary arms the album-id cooldown and requeues the batch, an absent
    /// key requeues only that message, a rate limit routes to <see cref="HandleBulkRateLimitAsync"/>,
    /// a deterministic 4xx routes to <see cref="HandleBulkRejectionAsync"/>, and any other exception
    /// requeues the whole batch.
    /// </remarks>
    internal async Task ProcessBulkAlbumLookupsAsync( CancellationToken ct ) {
        LogProcessingBulkAlbums( _logger );

        // Type-specific stream only; no priority-stream top-up.
        DateTimeOffset dequeueWallStart = DateTimeOffset.UtcNow;
        Stopwatch dequeueWallTimer = Stopwatch.StartNew( );
        List<QueuedMessage<QueuedLookupRequest>> messages =
            [.. await _batchHelper.DequeueAlbumIdBatchAsync( SpotifyConstants.MaxAlbumsPerBatchLookup, ct )];

        if (messages.Count == 0) {
            LogNoAlbumLookups( _logger );
            return;
        }

        try {
            await RejectIneligibleMessagesAsync(
                messages,
                SpotifyConstants.AlbumsEndpoint,
                ct,
                dequeueWallTimer,
                dequeueWallStart );
            if (messages.Count == 0) {
                return;
            }

            LogProcessingAlbumCount( _logger, messages.Count );
            SpotifyBatchMetrics.RecordBatchSize( "albums", messages.Count );

            // Build an album-ID → messages map
            Dictionary<string, List<QueuedMessage<QueuedLookupRequest>>> idToMessages = [];
            foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
                string albumId = message.Payload.LookupValue;
                if (!idToMessages.TryGetValue( albumId, out List<QueuedMessage<QueuedLookupRequest>>? value )) {
                    value = [];
                    idToMessages[albumId] = value;
                }
                value.Add( message );
            }

            // Call the bulk lookup API (GET /albums?ids=...)
            Stopwatch httpTimer = Stopwatch.StartNew( );
            using Activity? httpActivity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.provider_http" );
            _ = (httpActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
            _ = (httpActivity?.SetTag( QueueMetricTags.Priority, "bulk" ));
            Dictionary<string, MusicLookupResult?> results;
            try { results = await _lookupService.GetAlbumsByIdsAsync( idToMessages.Keys ); } finally { httpTimer.Stop( ); httpActivity?.Stop( ); QueueMetrics.ProviderHttpDuration.Record( httpTimer.Elapsed.TotalSeconds, new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ), new KeyValuePair<string, object?>( QueueMetricTags.Priority, "bulk" ) ); }

            // Empty dictionary = request failure → requeue ALL. Arm cooldown.
            if (results.Count == 0) {
                LogBulkDispatchRequeuingAll( _logger, messages.Count, "empty-dict (request failure)" );
                ArmRequestFailureCooldown( ref _albumIdCooldownUntil, ref _consecutiveAlbumIdFailures );
                await RequeueAllAsync( messages, ct, dequeueWallTimer, dequeueWallStart );
                return;
            }

            // Success: reset cooldown counter.
            _consecutiveAlbumIdFailures = 0;
            _albumIdCooldownUntil = DateTimeOffset.MinValue;

            // Dispatch each result to its queued messages
            foreach (KeyValuePair<string, List<QueuedMessage<QueuedLookupRequest>>> kvp in idToMessages) {
                string albumId = kvp.Key;
                bool keyPresent = results.TryGetValue( albumId, out MusicLookupResult? result );

                foreach (QueuedMessage<QueuedLookupRequest> message in kvp.Value) {
                    if (!keyPresent) {
                        // Absent key in non-empty dict = partial parse failure → requeue one
                        LogBulkDispatchRequeuingOne( _logger, albumId, "absent key (partial parse)" );
                        await RequeueSingleAsync( message, ct, dequeueWallTimer, dequeueWallStart );
                    } else {
                        await ProcessBulkResultAsync( message, result, ct, dequeueWallTimer, dequeueWallStart );
                    }
                }
            }

            LogAlbumLookupsSuccess( _logger, messages.Count );
        } catch (ProviderRateLimitException ex) {
            await HandleBulkRateLimitAsync( messages, SpotifyConstants.AlbumsEndpoint, ex, ct, dequeueWallTimer, dequeueWallStart );
        } catch (SpotifyBulkRejectedException ex) {
            await HandleBulkRejectionAsync( messages, ex, ct, dequeueWallTimer, dequeueWallStart );
        } catch (PostCommitAcknowledgementException) {
            throw;
        } catch (TerminalDlqRecoveryException) {
            throw;
        } catch (QueueDeliveryIdentityQuarantineException) {
            throw;
        } catch (Exception ex) {
            LogAlbumLookupsError( _logger, ex );
            ArmRequestFailureCooldown( ref _albumIdCooldownUntil, ref _consecutiveAlbumIdFailures );
            await RequeueAllAsync( messages, ct, dequeueWallTimer, dequeueWallStart );
        }
    }

    /// <summary>
    /// Applies a single resolved bulk result to its saga, then publishes completion and acknowledges
    /// the message.
    /// </summary>
    /// <param name="message">The queued message whose saga is being updated.</param>
    /// <param name="result">The resolved result, or <see langword="null"/> when the id was not found.</param>
    /// <param name="ct">Token used to cancel the saga and publish operations.</param>
    /// <param name="dequeueWallTimer">Timestamp started before the batch dequeue.</param>
    /// <param name="dequeueWallStart">UTC timestamp captured immediately before the batch dequeue.</param>
    /// <returns>A task that completes once the saga is updated and the message acknowledged.</returns>
    /// <remarks>
    /// Ensures the saga exists, initializes the Spotify provider state, writes the provider result
    /// (success when <paramref name="result"/> is non-null, otherwise an unsuccessful leg with no
    /// provider error message), then
    /// publishes saga-level and per-lookup completion before acknowledging. On error the message is
    /// requeued.
    /// </remarks>
    private async Task ProcessBulkResultAsync(
        QueuedMessage<QueuedLookupRequest> message,
        MusicLookupResult? result,
        CancellationToken ct,
        Stopwatch dequeueWallTimer,
        DateTimeOffset dequeueWallStart
    ) {
        QueuedLookupRequest request = message.Payload;
        using Activity? wallActivity = StartHistoricalActivity( "queue.spotify.message.wall", dequeueWallStart );
        _ = (wallActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
        _ = (wallActivity?.SetTag( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ));
        bool legUpdated = false;

        try {
            // Admit only the saga instance that owns this queued lookup. Bulk messages can outlive
            // a saga replacement; creating or mutating a mismatched hash would cross-contaminate
            // records. Producers create and fence the saga before publishing the delivery.
            string lookupKey = LookupKeyBuilder.TypedKey( request.LookupType, SupportedProviders.Spotify, request.LookupValue );
            LookupSagaState? saga = await _sagaManager.GetAsync( request.SagaId, ct );
            if (saga is null) {
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }

            if (string.IsNullOrWhiteSpace( request.SagaInstanceToken )
                || !string.Equals( request.SagaInstanceToken, saga.InstanceToken, StringComparison.Ordinal )) {
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }

            bool rootIdentityMatches = string.Equals( saga.LookupKey, lookupKey, StringComparison.Ordinal )
                && saga.LookupType == request.LookupType
                && string.Equals( saga.LookupValue, request.LookupValue, StringComparison.Ordinal );
            bool initializedSpotifyLeg = saga.ProviderStates.TryGetValue( SupportedProviders.Spotify, out ProviderLookupState? spotifyState )
                && !spotifyState.IsComplete;
            if (saga.ProviderStates.TryGetValue( SupportedProviders.Spotify, out ProviderLookupState? completedSpotifyState )
                && completedSpotifyState.IsComplete) {
                if (completedSpotifyState.ErrorMessage is not null) {
                    try {
                        await _requestQueue.MoveToDlqAsync(
                            message.MessageId,
                            completedSpotifyState.ErrorMessage,
                            ct );
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        throw;
                    } catch (Exception ex) {
                        throw new TerminalDlqRecoveryException( message.MessageId, ex );
                    }
                    QueueMetrics.RecordTerminalOutcome( SupportedProviders.Spotify, message.Priority, "dlq_recovery" );
                    return;
                }
                // A lost ACK can redeliver a child after its active Spotify leg was committed.
                // Clear only this stale delivery; never re-query or mutate the completed leg.
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }
            if (!rootIdentityMatches && !initializedSpotifyLeg) {
                await QuarantineBulkMessageAsync( message, "Saga identity does not match queued request.", ct );
                return;
            }

            string instanceToken = request.SagaInstanceToken!;

            if (result is null
                && request.FallbackLookupType is not null
                && !string.IsNullOrWhiteSpace( request.FallbackLookupValue )) {
                result = request.FallbackLookupType.Value switch {
                    LookupRequestType.IsrcLookup =>
                        await GetFallbackResultAsync( request, LookupRequestType.IsrcLookup, message.Priority, ct ),
                    LookupRequestType.UpcLookup =>
                        await GetFallbackResultAsync( request, LookupRequestType.UpcLookup, message.Priority, ct ),
                    _ => throw new InvalidOperationException(
                        $"Unsupported Spotify bulk fallback type {request.FallbackLookupType.Value}." )
                };
            }

            // Allocate the Spotify provider slot in the saga (idempotent)
            if (!await _sagaManager.TryInitializeProviderStatesAsync(
                request.SagaId, [SupportedProviders.Spotify], instanceToken, ct )) {
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }

            // Write the lookup result (found or not-found)
            Stopwatch sagaTimer = Stopwatch.StartNew( );
            using Activity? sagaActivity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.saga.update" );
            _ = (sagaActivity?.SetTag( QueueMetricTags.Provider, "spotify" ));
            _ = (sagaActivity?.SetTag( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ));
            try {
                if (!await _sagaManager.TryUpdateProviderStateAsync(
                    request.SagaId,
                    new ProviderLookupState(
                        Provider: SupportedProviders.Spotify,
                        IsComplete: true,
                        IsSuccess: result is not null,
                        ResultJson: result is not null ? JsonSerializer.Serialize( result, _jsonOptions ) : null,
                        CompletedAt: DateTimeOffset.UtcNow,
                        ErrorMessage: null
                    ),
                    instanceToken,
                    ct )) {
                    await AcknowledgeStaleDeliveryAsync( message, ct );
                    return;
                }
                legUpdated = true;
            } finally {
                sagaTimer.Stop( );
                sagaActivity?.Stop( );
                QueueMetrics.SagaUpdateDuration.Record( sagaTimer.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ) );
            }

            // The coordinator performs the authoritative completeness check. Publishing the saga id
            // is a durable-progress hint and needs no fallible post-commit saga reread.
            try {
                await PublishSagaProgressAsync( request.SagaId );
            } catch (Exception ex) {
                // Provider state is already committed. Do not route this delivery through the
                // ordinary lookup retry path, which could overwrite the successful leg at cap.
                // Leave the delivery pending; reconciliation can publish completion and a
                // redelivery can safely acknowledge the already-complete leg.
                throw new PostCommitAcknowledgementException( message.MessageId, ex );
            }

            // Acknowledge only after the provider state and completion publishes are committed.
            // If XACK fails, leave the original PEL delivery pending for worker recovery; do not
            // send the already-committed result through the ordinary retry/cap mutation path.
            try {
                await _batchHelper.AcknowledgeAsync( message.MessageId );
                QueueMetrics.RecordTerminalOutcome( SupportedProviders.Spotify, message.Priority, "success" );
            } catch (Exception ex) {
                throw new PostCommitAcknowledgementException( message.MessageId, ex );
            }

            LogBulkResultProcessed( _logger, request.SagaId, result is not null ? "found" : "not found" );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (UnreadableSagaStateException ex) {
            await QuarantineBulkMessageAsync( message, ex.Message, ct );
        } catch (PostCommitAcknowledgementException) {
            throw;
        } catch (TerminalDlqRecoveryException) {
            throw;
        } catch (QueueDeliveryIdentityQuarantineException) {
            throw;
        } catch (Exception ex) {
            LogBulkResultError( _logger, ex, request.SagaId );
            await RequeueSingleAsync( message, ct, dequeueWallTimer, dequeueWallStart, recordWall: false );
        } finally {
            wallActivity?.Stop( );
            QueueMetrics.MessageWallDuration.Record( dequeueWallTimer.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
                new KeyValuePair<string, object?>( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ) );
            if (legUpdated) {
                QueueMetrics.RecordSagaLegCompleted( SupportedProviders.Spotify, message.Priority, "committed" );
            }
        }
    }

    // Rate-limit and requeue helpers

    private async Task QuarantineBulkMessageAsync(
        QueuedMessage<QueuedLookupRequest> message,
        string reason,
        CancellationToken ct
    ) {
        try {
            await _requestQueue.MoveToDlqAsync( message.MessageId, reason, ct );
            QueueMetrics.RecordTerminalOutcome( SupportedProviders.Spotify, message.Priority, "identity_quarantine" );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogBulkResultError( _logger, ex, message.Payload.SagaId );
            throw new QueueDeliveryIdentityQuarantineException( message.MessageId, message.Payload.SagaId, ex );
        }
    }

    /// <summary>
    /// Handles a bulk-endpoint rate limit by marking each affected saga partial and
    /// requeuing the whole batch.
    /// </summary>
    /// <param name="messages">The messages whose batch hit the rate limit.</param>
    /// <param name="endpoint">The bulk endpoint that was rate-limited.</param>
    /// <param name="ex">The exception carrying the retry-after value.</param>
    /// <param name="ct">Token used to cancel the work.</param>
    /// <param name="dequeueWallTimer">Timestamp started before the batch dequeue.</param>
    /// <param name="dequeueWallStart">UTC timestamp captured immediately before the batch dequeue.</param>
    /// <returns>A task that completes once all sagas are marked and the batch requeued.</returns>
    /// <remarks>
    /// The outbound handler owns the shared endpoint cooldown. This method marks each saga partial,
    /// merges a <see cref="ProviderRateLimitInfo"/> for Spotify into the saga, and publishes a
    /// rate-limit sentinel so synchronous waiters receive a signal. The entire batch is requeued at
    /// the end. Per-saga errors are logged and do not stop the loop.
    /// </remarks>
    internal async Task HandleBulkRateLimitAsync(
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages,
        string endpoint,
        ProviderRateLimitException ex,
        CancellationToken ct,
        Stopwatch? dequeueWallTimer = null,
        DateTimeOffset? dequeueWallStart = null
    ) {
        dequeueWallTimer ??= Stopwatch.StartNew( );
        dequeueWallStart ??= DateTimeOffset.UtcNow;
        string effectiveEndpoint = ProviderEndpointConstants.ResolveEffective( ex.Endpoint, endpoint );
        LogRateLimitEncountered( _logger, effectiveEndpoint, ex.RetryAfterValue.ToString( ) );

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset retryAfter = _queueSettings.GetUpperBoundedRateLimitRetryAfter(
            now,
            ex.RetryAfterValue );
        // Keep the in-process bulk-operation lanes independent. The handler-derived
        // effective endpoint is used for diagnostics and shared policy, where Spotify
        // data requests intentionally collapse to a provider-wide window.
        ArmRateLimitCooldown( endpoint, retryAfter );
        List<QueuedMessage<QueuedLookupRequest>> admittedMessages = [];
        HashSet<string> wallRecordedMessageIds = [];

        try {
            // SetIsPartialAsync + merge SetRateLimitInfoAsync + publish rate-limited sentinel so
            // interactive callers whose request lands in a bulk batch do not hang until timeout on a 429.
            foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
                if (ct.IsCancellationRequested) { break; }
                QueuedLookupRequest request = message.Payload;
                try {
                    if (_queueSettings.IsPastAbsoluteDeadline( request.CreatedAt, DateTimeOffset.UtcNow )) {
                        await TerminalizeExpiredMessageAsync( message, ct );
                        _ = wallRecordedMessageIds.Add( message.MessageId );
                        RecordMessageWall( message, dequeueWallTimer, dequeueWallStart.Value );
                        continue;
                    }
                    string lookupKey = LookupKeyBuilder.TypedKey( request.LookupType, SupportedProviders.Spotify, request.LookupValue );
                    LookupSagaState? saga = await _sagaManager.GetAsync( request.SagaId, ct );
                    if (saga is null) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace( saga.InstanceToken )) {
                        await QuarantineBulkMessageAsync( message, "Saga instance is missing or invalid.", ct );
                        continue;
                    }
                    string instanceToken = saga.InstanceToken;
                    if (string.IsNullOrWhiteSpace( request.SagaInstanceToken )
                        || !string.Equals( request.SagaInstanceToken, instanceToken, StringComparison.Ordinal )) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        continue;
                    }
                    bool rootIdentityMatches = string.Equals( saga.LookupKey, lookupKey, StringComparison.Ordinal )
                        && saga.LookupType == request.LookupType
                        && string.Equals( saga.LookupValue, request.LookupValue, StringComparison.Ordinal );
                    bool initializedSpotifyLeg = saga.ProviderStates.TryGetValue( SupportedProviders.Spotify, out ProviderLookupState? spotifyState )
                        && !spotifyState.IsComplete;
                    if (!rootIdentityMatches && !initializedSpotifyLeg) {
                        await QuarantineBulkMessageAsync( message, "Saga identity does not match queued request.", ct );
                        continue;
                    }

                    if (!await _sagaManager.TrySetIsPartialAsync( request.SagaId, true, instanceToken, ct )) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        continue;
                    }

                    List<ProviderRateLimitInfo> mergedRateLimitInfo = saga?.RateLimitInfo is not null
                        ? [.. saga.RateLimitInfo.Where( r => r.Provider != SupportedProviders.Spotify )]
                        : [];
                    mergedRateLimitInfo.Add( new ProviderRateLimitInfo( SupportedProviders.Spotify, retryAfter, effectiveEndpoint ) );
                    if (!await _sagaManager.TrySetRateLimitInfoAsync( request.SagaId, mergedRateLimitInfo, instanceToken, ct )) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        continue;
                    }

                    await PublishRateLimitSentinelAsync( request.SagaId, instanceToken, ct );
                    admittedMessages.Add( message );

                    LogBulkSagaMarkedPartial( _logger, request.SagaId, effectiveEndpoint, retryAfter );
                } catch (PostCommitAcknowledgementException) {
                    throw;
                } catch (TerminalDlqRecoveryException) {
                    throw;
                } catch (QueueDeliveryIdentityQuarantineException) {
                    throw;
                } catch (Exception sagaEx) {
                    LogBulkResultError( _logger, sagaEx, request.SagaId );
                }
            }

            // A rate limit is a deferral, not a failed lookup attempt. Requeue every admitted
            // message with its current AttemptCount and the exact endpoint key intact.
            await RequeueAllAsync(
                admittedMessages,
                ct,
                dequeueWallTimer,
                dequeueWallStart.Value,
                wallRecordedMessageIds,
                preserveAttemptCount: true,
                rateLimitedEndpoint: effectiveEndpoint,
                notBefore: retryAfter );
        } finally {
            // Quarantined, stale, failed, and cancellation-skipped messages never reach
            // RequeueSingleAsync. Close the telemetry obligation for every dequeued message here,
            // while the set prevents a duplicate sample for admitted messages already requeued.
            foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
                if (wallRecordedMessageIds.Add( message.MessageId )) {
                    RecordMessageWall( message, dequeueWallTimer, dequeueWallStart.Value );
                }
            }
        }
    }

    private async Task RejectIneligibleMessagesAsync(
        List<QueuedMessage<QueuedLookupRequest>> messages,
        string operationEndpoint,
        CancellationToken ct,
        Stopwatch dequeueWallTimer,
        DateTimeOffset dequeueWallStart
    ) {
        foreach (QueuedMessage<QueuedLookupRequest> message in messages.ToArray( )) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_queueSettings.IsPastAbsoluteDeadline( message.Payload.CreatedAt, now )) {
                await TerminalizeExpiredMessageAsync( message, ct );
                RecordMessageWall( message, dequeueWallTimer, dequeueWallStart );
                // Mutate the caller-owned working set as each terminalization commits. If a later
                // item fails, the outer fallback cannot requeue deliveries already made terminal.
                _ = messages.Remove( message );
                continue;
            }

            if (message.Payload.NotBefore is { } notBefore && notBefore > now) {
                // NotBefore is the durable source of truth for a rate-limit deferral. The shared
                // tracker and in-process cooldown avoid most early dequeues, but this guard remains
                // authoritative after tracker write failures or process restarts.
                DateTimeOffset boundedNotBefore = _queueSettings.BoundExistingRateLimitNotBefore(
                    now,
                    notBefore,
                    message.Payload.CreatedAt );
                ArmRateLimitCooldown(
                    message.Payload.RateLimitedEndpoint ?? operationEndpoint,
                    boundedNotBefore );
                await RequeueSingleAsync(
                    message,
                    ct,
                    dequeueWallTimer,
                    dequeueWallStart,
                    preserveAttemptCount: true,
                    rateLimitedEndpoint: message.Payload.RateLimitedEndpoint,
                    notBefore: boundedNotBefore );
                _ = messages.Remove( message );
            }
        }
    }

    private async Task TerminalizeExpiredMessageAsync(
        QueuedMessage<QueuedLookupRequest> message,
        CancellationToken ct
    ) {
        const string ExpirationReason = "Lookup job expired before provider completion.";
        QueuedLookupRequest request = message.Payload;
        string lookupKey = LookupKeyBuilder.TypedKey(
            request.LookupType, SupportedProviders.Spotify, request.LookupValue );
        LookupSagaState? saga = await _sagaManager.GetAsync( request.SagaId, ct );
        if (saga is null) {
            await AcknowledgeStaleDeliveryAsync( message, ct );
            return;
        }
        if (string.IsNullOrWhiteSpace( saga.InstanceToken )) {
            await QuarantineBulkMessageAsync( message, "Saga instance is missing or invalid.", ct );
            return;
        }
        if (string.IsNullOrWhiteSpace( request.SagaInstanceToken )
            || !string.Equals( request.SagaInstanceToken, saga.InstanceToken, StringComparison.Ordinal )) {
            await AcknowledgeStaleDeliveryAsync( message, ct );
            return;
        }

        bool rootIdentityMatches = string.Equals( saga.LookupKey, lookupKey, StringComparison.Ordinal )
            && saga.LookupType == request.LookupType
            && string.Equals( saga.LookupValue, request.LookupValue, StringComparison.Ordinal );
        bool initializedSpotifyLeg = saga.ProviderStates.TryGetValue(
            SupportedProviders.Spotify, out ProviderLookupState? spotifyState )
            && !spotifyState.IsComplete;
        if (!rootIdentityMatches && !initializedSpotifyLeg) {
            await QuarantineBulkMessageAsync( message, "Saga identity does not match queued request.", ct );
            return;
        }

        string instanceToken = saga.InstanceToken;
        if (!await _sagaManager.TryUpdateProviderStateAsync(
            request.SagaId,
            new ProviderLookupState(
                SupportedProviders.Spotify,
                IsComplete: true,
                IsSuccess: false,
                ResultJson: null,
                CompletedAt: DateTimeOffset.UtcNow,
                ErrorMessage: ExpirationReason ),
            instanceToken,
            ct )) {
            await AcknowledgeStaleDeliveryAsync( message, ct );
            return;
        }

        try {
            await PublishSagaProgressAsync( request.SagaId );
        } catch (Exception ex) {
            throw new PostCommitAcknowledgementException( message.MessageId, ex );
        }
        try {
            await _requestQueue.MoveToDlqAsync( message.MessageId, ExpirationReason, ct );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            throw new TerminalDlqRecoveryException( message.MessageId, ex );
        }
        QueueMetrics.RecordSagaLegCompleted(
            SupportedProviders.Spotify, message.Priority, "expired" );
    }

    private void ArmRateLimitCooldown( string operationEndpoint, DateTimeOffset retryAfter ) {
        string normalizedEndpoint = ProviderEndpointConstants.Normalize( operationEndpoint );
        if (normalizedEndpoint == SpotifyConstants.TracksEndpoint
            && retryAfter > _trackIdCooldownUntil) {
            _trackIdCooldownUntil = retryAfter;
        } else if (normalizedEndpoint == SpotifyConstants.AlbumsEndpoint
            && retryAfter > _albumIdCooldownUntil) {
            _albumIdCooldownUntil = retryAfter;
        }
    }

    /// <summary>
    /// Handles a deterministic 4xx bulk rejection by atomically transferring each message to the
    /// origin-aware single-item stream and removing the original bulk-stream delivery.
    /// </summary>
    /// <param name="messages">The messages whose bulk batch was rejected by the API.</param>
    /// <param name="ex">The exception carrying the HTTP status code of the rejection.</param>
    /// <param name="ct">Token used to stop early.</param>
    /// <param name="dequeueWallTimer">Timestamp started before the batch dequeue.</param>
    /// <param name="dequeueWallStart">UTC timestamp captured immediately before the batch dequeue.</param>
    /// <returns>A task that completes once all item transfers have been attempted.</returns>
    /// <remarks>
    /// A 4xx rejection is data-dependent: the batch contains at least one id the API cannot
    /// accept. Re-enqueueing individually lets the per-message processor resolve each id with a
    /// single-id call so the poison id is isolated without penalizing the remaining ids. Manual
    /// interactive work remains interactive; maintenance work remains background. AttemptCount is
    /// carried unchanged — the 4xx is a route change, not
    /// a failed lookup attempt. No cooldown is armed and no saga state is written; the
    /// per-message processor owns those on the next attempt. ArmRequestFailureCooldown is
    /// intentionally NOT called on this path.
    /// </remarks>
    internal async Task HandleBulkRejectionAsync(
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages,
        SpotifyBulkRejectedException ex,
        CancellationToken ct,
        Stopwatch? dequeueWallTimer = null,
        DateTimeOffset? dequeueWallStart = null
    ) {
        dequeueWallTimer ??= Stopwatch.StartNew( );
        dequeueWallStart ??= DateTimeOffset.UtcNow;
        LogBulkBatchRejected( _logger, ex.HttpStatusCode, messages.Count );

        foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
            if (ct.IsCancellationRequested) { break; }
            try {
                // The helper performs XADD + XACK + XDEL atomically and checks source existence,
                // so an ambiguous response or redelivery cannot create duplicate replacements.
                _ = await _batchHelper.ForwardToSingleItemAsync( message, ct );
            } catch (Exception itemEx) {
                LogRequeueError( _logger, itemEx, message.MessageId );
                // Keep forward failures bounded. The atomic bulk requeue increments the attempt
                // count; a persistent destination failure therefore reaches the normal cap rather
                // than leaving one PEL entry to fail every reclaim window forever.
                await RequeueSingleAsync(
                    message,
                    ct,
                    dequeueWallTimer,
                    dequeueWallStart.Value,
                    recordWall: false );
            } finally {
                RecordMessageWall( message, dequeueWallTimer, dequeueWallStart.Value );
            }
        }
    }

    /// <summary>
    /// Requeues every message in a batch, continuing past per-message errors.
    /// </summary>
    /// <param name="messages">The messages to requeue.</param>
    /// <param name="ct">Token used to stop requeuing early.</param>
    /// <param name="dequeueWallTimer">Timestamp started before the batch dequeue.</param>
    /// <param name="dequeueWallStart">UTC timestamp captured immediately before the batch dequeue.</param>
    /// <param name="wallRecordedMessageIds">Optional set used to prevent duplicate wall samples.</param>
    /// <param name="preserveAttemptCount">Whether requeue is a deferral that must not consume attempts.</param>
    /// <param name="rateLimitedEndpoint">Endpoint to persist on a rate-limit deferral.</param>
    /// <param name="notBefore">Earliest eligibility instant to persist on the replacement.</param>
    /// <returns>A task that completes once all messages have been attempted.</returns>
    private async Task RequeueAllAsync(
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages,
        CancellationToken ct,
        Stopwatch dequeueWallTimer,
        DateTimeOffset dequeueWallStart,
        HashSet<string>? wallRecordedMessageIds = null,
        bool preserveAttemptCount = false,
        string? rateLimitedEndpoint = null,
        DateTimeOffset? notBefore = null
    ) {
        foreach (QueuedMessage<QueuedLookupRequest> message in messages) {
            if (ct.IsCancellationRequested) { break; }
            try {
                DateTimeOffset? effectiveNotBefore = preserveAttemptCount && notBefore is not null
                    ? _queueSettings.BoundExistingRateLimitNotBefore(
                        DateTimeOffset.UtcNow,
                        notBefore.Value,
                        message.Payload.CreatedAt )
                    : notBefore;
                await RequeueSingleAsync( message, ct, dequeueWallTimer, dequeueWallStart,
                    wallRecordedMessageIds: wallRecordedMessageIds,
                    preserveAttemptCount: preserveAttemptCount,
                    rateLimitedEndpoint: rateLimitedEndpoint,
                    notBefore: effectiveNotBefore );
            } catch (PostCommitAcknowledgementException) {
                throw;
            } catch (QueueDeliveryIdentityQuarantineException) {
                throw;
            } catch (Exception ex) {
                LogRequeueError( _logger, ex, message.MessageId );
            }
        }
    }

    /// <summary>
    /// Requeues a single message and, when its retry cap is reached, finalizes the saga as a failure.
    /// </summary>
    /// <param name="message">The message to requeue.</param>
    /// <param name="ct">Token used to cancel the work.</param>
    /// <param name="dequeueWallTimer">Timestamp started before the batch dequeue.</param>
    /// <param name="dequeueWallStart">UTC timestamp captured immediately before the batch dequeue.</param>
    /// <param name="recordWall">Whether this helper owns the terminal wall sample.</param>
    /// <param name="wallRecordedMessageIds">Optional set used to prevent duplicate wall samples.</param>
    /// <param name="preserveAttemptCount">Whether requeue is a deferral that must not consume attempts.</param>
    /// <param name="rateLimitedEndpoint">Endpoint to persist on a rate-limit deferral.</param>
    /// <param name="notBefore">Earliest eligibility instant to persist on the replacement.</param>
    /// <returns>A task that completes once the message is requeued or its saga is finalized.</returns>
    /// <remarks>
    /// When <see cref="SpotifyBatchQueueHelper.RequeueAsync"/> returns
    /// <see cref="RequeueOutcome.CapReached"/>, the Spotify provider state is written as a failure and
    /// saga-level and per-lookup completion are published so the lookup does not hang. Other outcomes
    /// require no further action here.
    /// </remarks>
    private async Task RequeueSingleAsync(
        QueuedMessage<QueuedLookupRequest> message,
        CancellationToken ct,
        Stopwatch dequeueWallTimer,
        DateTimeOffset dequeueWallStart,
        bool recordWall = true,
        HashSet<string>? wallRecordedMessageIds = null,
        bool preserveAttemptCount = false,
        string? rateLimitedEndpoint = null,
        DateTimeOffset? notBefore = null
    ) {
        QueuedLookupRequest request = message.Payload;
        try {
            RequeueOutcome outcome = await _batchHelper.RequeueAsync(
                message.MessageId,
                request.SagaId,
                preserveAttemptCount,
                rateLimitedEndpoint,
                notBefore,
                ct );

            if (outcome == RequeueOutcome.CapReached) {
                // Cap hit or unrecoverable payload — write the failed provider state and
                // publish completion so the saga coordinator and interactive waiters unblock.
                try {
                    string lookupKey = LookupKeyBuilder.TypedKey( request.LookupType, SupportedProviders.Spotify, request.LookupValue );
                    LookupSagaState? saga = await _sagaManager.GetAsync( request.SagaId, ct );
                    if (saga is null) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        return;
                    }
                    if (string.IsNullOrWhiteSpace( saga.InstanceToken )) {
                        await QuarantineBulkMessageAsync( message, "Saga instance is missing or invalid.", ct );
                        return;
                    }
                    string instanceToken = saga.InstanceToken;
                    if (string.IsNullOrWhiteSpace( request.SagaInstanceToken )
                        || !string.Equals( request.SagaInstanceToken, instanceToken, StringComparison.Ordinal )) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        return;
                    }
                    bool rootIdentityMatches = string.Equals( saga.LookupKey, lookupKey, StringComparison.Ordinal )
                        && saga.LookupType == request.LookupType
                        && string.Equals( saga.LookupValue, request.LookupValue, StringComparison.Ordinal );
                    bool initializedSpotifyLeg = saga.ProviderStates.TryGetValue( SupportedProviders.Spotify, out ProviderLookupState? spotifyState )
                        && !spotifyState.IsComplete;
                    if (!rootIdentityMatches && !initializedSpotifyLeg) {
                        await QuarantineBulkMessageAsync( message, "Saga identity does not match queued request.", ct );
                        return;
                    }

                    if (!await _sagaManager.TryUpdateProviderStateAsync(
                        request.SagaId,
                        new ProviderLookupState(
                            Provider: SupportedProviders.Spotify,
                            IsComplete: true,
                            IsSuccess: false,
                            ResultJson: null,
                            CompletedAt: DateTimeOffset.UtcNow,
                            ErrorMessage: $"Bulk lookup failed after {LookupConstants.MaxQueueRetryAttempts} attempts"
                        ),
                        instanceToken,
                        ct )) {
                        await AcknowledgeStaleDeliveryAsync( message, ct );
                        return;
                    }
                    try {
                        await PublishSagaProgressAsync( request.SagaId );
                    } catch (Exception ex) {
                        throw new PostCommitAcknowledgementException( message.MessageId, ex );
                    }
                    try {
                        await _requestQueue.MoveToDlqAsync(
                            message.MessageId,
                            $"Bulk lookup failed after {LookupConstants.MaxQueueRetryAttempts} attempts",
                            ct );
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        throw;
                    } catch (Exception ex) {
                        throw new TerminalDlqRecoveryException( message.MessageId, ex );
                    }
                    QueueMetrics.RecordSagaLegCompleted(
                        SupportedProviders.Spotify,
                        message.Priority,
                        "committed_failure" );
                } catch (PostCommitAcknowledgementException) {
                    throw;
                } catch (TerminalDlqRecoveryException) {
                    throw;
                } catch (QueueDeliveryIdentityQuarantineException) {
                    throw;
                } catch (Exception ex) {
                    LogBulkResultError( _logger, ex, request.SagaId );
                }
            }
            // RequeueOutcome.NotFound: entry already XDELed — another consumer processed it.
            // Leave the saga untouched; logging was done inside RequeueAsync.
            // RequeueOutcome.Requeued: nothing to do here.
        } finally {
            if (recordWall) {
                RecordMessageWall( message, dequeueWallTimer, dequeueWallStart );
                _ = (wallRecordedMessageIds?.Add( message.MessageId ));
            }
        }
    }

    private async Task<MusicLookupResult?> GetFallbackResultAsync(
        QueuedLookupRequest request,
        LookupRequestType fallbackType,
        QueuePriority priority,
        CancellationToken ct
    ) {
        Stopwatch timer = Stopwatch.StartNew( );
        ct.ThrowIfCancellationRequested( );
        Activity? activity = QueueMetrics.ActivitySource.StartActivity( "queue.spotify.provider_http" );
        _ = (activity?.SetTag( QueueMetricTags.Provider, "spotify" ));
        _ = (activity?.SetTag( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ));
        try {
            return fallbackType switch {
                LookupRequestType.IsrcLookup => await _lookupService.GetInfoByISRCAsync( request.FallbackLookupValue! ),
                LookupRequestType.UpcLookup => await _lookupService.GetInfoByUPCAsync( request.FallbackLookupValue! ),
                _ => throw new InvalidOperationException( $"Unsupported Spotify bulk fallback type {fallbackType}." )
            };
        } finally {
            timer.Stop( );
            activity?.Stop( );
            QueueMetrics.ProviderHttpDuration.Record( timer.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
                new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ) );
        }
    }

    private static void RecordMessageWall(
        QueuedMessage<QueuedLookupRequest> message,
        Stopwatch dequeueWallTimer,
        DateTimeOffset dequeueWallStart
    ) {
        Activity? activity = StartHistoricalActivity( "queue.spotify.message.wall", dequeueWallStart );
        _ = (activity?.SetTag( QueueMetricTags.Provider, "spotify" ));
        _ = (activity?.SetTag( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ));
        activity?.Stop( );
        QueueMetrics.MessageWallDuration.Record( dequeueWallTimer.Elapsed.TotalSeconds,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ) );
    }

    private static Activity? StartHistoricalActivity( string name, DateTimeOffset startTime ) {
        Activity? activity = QueueMetrics.ActivitySource.CreateActivity( name, ActivityKind.Internal );
        if (activity is null) return null;
        _ = activity.SetStartTime( startTime.UtcDateTime );
        return activity.Start( );
    }

    /// <summary>
    /// Increments a stream's consecutive-failure count and sets its cooldown deadline using
    /// exponential backoff.
    /// </summary>
    /// <param name="cooldownUntil">The stream's cooldown deadline, updated to now plus the computed cooldown.</param>
    /// <param name="consecutiveFailures">The stream's consecutive-failure counter, incremented in place.</param>
    /// <remarks>The cooldown grows with <see cref="ComputeCooldownSeconds"/> and is capped at <see cref="SpotifyBatchSettings.MaxRequestFailureCooldownSeconds"/>.</remarks>
    private void ArmRequestFailureCooldown( ref DateTimeOffset cooldownUntil, ref int consecutiveFailures ) {
        consecutiveFailures++;
        int cooldownSeconds = ComputeCooldownSeconds(
            consecutiveFailures,
            _requestFailureCooldownSeconds,
            SpotifyBatchSettings.MaxRequestFailureCooldownSeconds
        );
        cooldownUntil = DateTimeOffset.UtcNow.AddSeconds( cooldownSeconds );
    }

    /// <summary>
    /// Computes the request-failure cooldown in seconds using exponential backoff with a cap.
    /// The exponent is clamped to 5 before the left-shift to prevent integer overflow when
    /// <paramref name="consecutiveFailures"/> is large.
    /// </summary>
    /// <param name="consecutiveFailures">The number of consecutive failures (one-based; must be ≥ 1).</param>
    /// <param name="baseSeconds">The base cooldown applied to the first failure.</param>
    /// <param name="maxSeconds">The maximum cooldown the result is clamped to.</param>
    /// <returns>
    /// <paramref name="baseSeconds"/> doubled once per prior failure (the exponent is capped at five),
    /// clamped to <paramref name="maxSeconds"/>.
    /// </returns>
    internal static int ComputeCooldownSeconds( int consecutiveFailures, int baseSeconds, int maxSeconds ) {
        int exp = Math.Min( consecutiveFailures - 1, 5 );
        return Math.Min( baseSeconds * (1 << exp), maxSeconds );
    }

    // Saga completion and pub/sub helpers

    private async Task PublishSagaProgressAsync( string sagaId ) {
        ISubscriber subscriber = _redis.GetSubscriber( );
        _ = await subscriber.PublishAsync( RedisChannel.Literal( SagaCompletedChannel ), sagaId );
    }

    /// <summary>
    /// Publishes a saga-level completion event when the saga is complete and not yet finalized.
    /// </summary>
    /// <param name="sagaId">The saga to check and publish for.</param>
    /// <param name="expectedInstanceToken">The saga instance fence.</param>
    /// <param name="ct">Token used to cancel the saga read.</param>
    /// <returns>A task that completes once the check (and any publish) finishes.</returns>
    /// <remarks>
    /// No event is published when the saga is missing, not yet complete, or already has a final result
    /// uri. Errors are logged and swallowed so a publish failure does not abort batch processing.
    /// </remarks>
    private async Task<LookupSagaState?> CheckAndPublishSagaCompletionAsync( string sagaId, string expectedInstanceToken, CancellationToken ct ) {
        try {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null || saga.InstanceToken != expectedInstanceToken) {
                return null;
            }
            if (!saga.IsComplete || !string.IsNullOrEmpty( saga.FinalResultUri )) {
                return saga;
            }

            LogSagaComplete( _logger, sagaId );

            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( SagaCompletedChannel ), sagaId );
            return saga;
        } catch (Exception ex) {
            LogSagaCompletionCheckError( _logger, ex, sagaId );
            return null;
        }
    }

    /// <summary>
    /// Publishes a per-lookup completion event so synchronous waiters on the lookup are woken.
    /// </summary>
    /// <param name="sagaId">The saga whose lookup completion is published.</param>
    /// <param name="expectedInstanceToken">The saga instance fence.</param>
    /// <param name="ct">Token used to cancel the saga read.</param>
    /// <returns>A task that completes once the event is published.</returns>
    /// <remarks>
    /// Publishes the saga's partial or final result uri (or an empty string when neither is set) on
    /// the <c>complete:{lookupKey}</c> channel. Nothing is published when the saga is missing; errors
    /// are logged and swallowed.
    /// </remarks>
    private async Task PublishLookupCompletionAsync( string sagaId, string expectedInstanceToken, CancellationToken ct ) {
        try {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null || saga.InstanceToken != expectedInstanceToken) {
                return;
            }

            string resultUri = saga.PartialResultUri ?? saga.FinalResultUri ?? string.Empty;
            string channel = $"{LookupConstants.CompletionChannelPrefix}{saga.LookupKey}";
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( channel ), resultUri );
        } catch (Exception ex) {
            LogPublishCompletionError( _logger, ex, sagaId );
        }
    }

    /// <summary>
    /// Publishes a rate-limit sentinel on the saga's per-lookup channel so waiters learn the lookup
    /// was deferred rather than completed.
    /// </summary>
    /// <param name="sagaId">The saga whose lookup is rate-limited.</param>
    /// <param name="expectedInstanceToken">The saga instance fence.</param>
    /// <param name="ct">Token used to cancel the saga read.</param>
    /// <returns>A task that completes once the sentinel is published.</returns>
    /// <remarks>Nothing is published when the saga is missing; errors are logged and swallowed.</remarks>
    private async Task PublishRateLimitSentinelAsync( string sagaId, string expectedInstanceToken, CancellationToken ct ) {
        try {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );
            if (saga is null || saga.InstanceToken != expectedInstanceToken) { return; }

            string channel = $"{LookupConstants.CompletionChannelPrefix}{saga.LookupKey}";
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( channel ), LookupConstants.RateLimitedSentinel );
        } catch (Exception ex) {
            LogBulkPublishRateLimitSentinelError( _logger, ex, sagaId );
        }
    }

    private async Task AcknowledgeStaleDeliveryAsync( QueuedMessage<QueuedLookupRequest> message, CancellationToken ct ) {
        try {
            await _batchHelper.AcknowledgeAsync( message.MessageId );
            QueueMetrics.RecordTerminalOutcome( SupportedProviders.Spotify, message.Priority, "stale_delivery" );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            throw new PostCommitAcknowledgementException( message.MessageId, ex );
        }
    }

    #region LoggerMessage Methods

    /// <summary>Logs that the bulk processor service has started.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkProcessorStarting,
        Level = LogLevel.Information,
        Message = "Spotify bulk processor service starting" )]
    private static partial void LogServiceStarting( ILogger logger );

    /// <summary>Logs an unhandled error in the bulk processor loop.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkProcessorLoopError,
        Level = LogLevel.Error,
        Message = "Error in bulk processor loop" )]
    private static partial void LogProcessorLoopError( ILogger logger, Exception ex );

    /// <summary>Logs that the bulk processor service is stopping.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkProcessorStopping,
        Level = LogLevel.Information,
        Message = "Spotify bulk processor service stopping" )]
    private static partial void LogServiceStopping( ILogger logger );

    /// <summary>Logs that a bulk endpoint is rate-limited and its batch was deferred.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="endpoint">The rate-limited endpoint.</param>
    /// <param name="retryAfter">The time until which the endpoint is rate-limited.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkRateLimited,
        Level = LogLevel.Debug,
        Message = "{Endpoint} endpoint is rate-limited until {RetryAfter}" )]
    private static partial void LogRateLimited( ILogger logger, string endpoint, string retryAfter );

    /// <summary>Logs that a bulk track-id flush is starting.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.ProcessingBulkTracks,
        Level = LogLevel.Information,
        Message = "Processing bulk track lookups" )]
    private static partial void LogProcessingBulkTracks( ILogger logger );

    /// <summary>Logs that no track-id lookups were available to process.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoTrackLookups,
        Level = LogLevel.Debug,
        Message = "No track ID lookups to process" )]
    private static partial void LogNoTrackLookups( ILogger logger );

    /// <summary>Logs the number of track-id lookups in the current batch.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of track-id lookups being processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.ProcessingTrackCount,
        Level = LogLevel.Information,
        Message = "Processing {Count} track ID lookups in bulk" )]
    private static partial void LogProcessingTrackCount( ILogger logger, int count );

    /// <summary>Logs that a batch of track-id lookups was processed successfully.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of track-id lookups processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.TrackLookupsSuccess,
        Level = LogLevel.Information,
        Message = "Successfully processed {Count} track ID lookups" )]
    private static partial void LogTrackLookupsSuccess( ILogger logger, int count );

    /// <summary>Logs an error while processing bulk track-id lookups.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.TrackLookupsError,
        Level = LogLevel.Error,
        Message = "Error processing bulk track lookups" )]
    private static partial void LogTrackLookupsError( ILogger logger, Exception ex );

    /// <summary>Logs that a bulk album-id flush is starting.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.ProcessingBulkAlbums,
        Level = LogLevel.Information,
        Message = "Processing bulk album lookups" )]
    private static partial void LogProcessingBulkAlbums( ILogger logger );

    /// <summary>Logs that no album-id lookups were available to process.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoAlbumLookups,
        Level = LogLevel.Debug,
        Message = "No album ID lookups to process" )]
    private static partial void LogNoAlbumLookups( ILogger logger );

    /// <summary>Logs the number of album-id lookups in the current batch.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of album-id lookups being processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.ProcessingAlbumCount,
        Level = LogLevel.Information,
        Message = "Processing {Count} album ID lookups in bulk" )]
    private static partial void LogProcessingAlbumCount( ILogger logger, int count );

    /// <summary>Logs that a batch of album-id lookups was processed successfully.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of album-id lookups processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.AlbumLookupsSuccess,
        Level = LogLevel.Information,
        Message = "Successfully processed {Count} album ID lookups" )]
    private static partial void LogAlbumLookupsSuccess( ILogger logger, int count );

    /// <summary>Logs an error while processing bulk album-id lookups.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.AlbumLookupsError,
        Level = LogLevel.Error,
        Message = "Error processing bulk album lookups" )]
    private static partial void LogAlbumLookupsError( ILogger logger, Exception ex );

    /// <summary>Logs that a single bulk result was applied to its saga.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga the result was applied to.</param>
    /// <param name="result">A short outcome label (for example <c>found</c> or <c>not found</c>).</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkResultProcessed,
        Level = LogLevel.Debug,
        Message = "Processed bulk result for saga {SagaId}: {Result}" )]
    private static partial void LogBulkResultProcessed( ILogger logger, string sagaId, string result );

    /// <summary>Logs an error while applying a single bulk result to its saga.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="sagaId">The saga being updated when the error occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkResultError,
        Level = LogLevel.Error,
        Message = "Error processing bulk result for saga {SagaId}" )]
    private static partial void LogBulkResultError( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that a rate limit was encountered on a bulk endpoint.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="endpoint">The bulk endpoint that was rate-limited.</param>
    /// <param name="retryAfter">The retry-after value reported by the provider.</param>
    [LoggerMessage(
        EventId = LogEventIds.RateLimitEncountered,
        Level = LogLevel.Warning,
        Message = "Rate limit encountered on bulk endpoint {Endpoint}, retry after {RetryAfter}" )]
    private static partial void LogRateLimitEncountered( ILogger logger, string endpoint, string retryAfter );

    /// <summary>Logs that a saga was marked partial because a bulk endpoint was rate-limited.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga marked partial.</param>
    /// <param name="endpoint">The rate-limited endpoint.</param>
    /// <param name="retryAfter">The time until which the endpoint is rate-limited.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkSagaMarkedPartial,
        Level = LogLevel.Information,
        Message = "Bulk saga {SagaId} marked partial due to rate limit on {Endpoint} (retry after {RetryAfter})" )]
    private static partial void LogBulkSagaMarkedPartial( ILogger logger, string sagaId, string endpoint, DateTimeOffset retryAfter );

    /// <summary>Logs an error while requeuing a single message.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="messageId">The composite id of the message that failed to requeue.</param>
    [LoggerMessage(
        EventId = LogEventIds.RequeueError,
        Level = LogLevel.Error,
        Message = "Error requeuing message {MessageId}" )]
    private static partial void LogRequeueError( ILogger logger, Exception ex, string messageId );

    /// <summary>Logs that the whole current batch is being requeued.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of messages being requeued.</param>
    /// <param name="reason">The reason the batch is being requeued.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkDispatchRequeuingAll,
        Level = LogLevel.Warning,
        Message = "Bulk dispatch: requeuing all {Count} messages, reason: {Reason}" )]
    private static partial void LogBulkDispatchRequeuingAll( ILogger logger, int count, string reason );

    /// <summary>Logs that a single message is being requeued.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="id">The lookup id whose message is being requeued.</param>
    /// <param name="reason">The reason the message is being requeued.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkDispatchRequeuingOne,
        Level = LogLevel.Warning,
        Message = "Bulk dispatch: requeuing message for id={Id}, reason: {Reason}" )]
    private static partial void LogBulkDispatchRequeuingOne( ILogger logger, string id, string reason );

    /// <summary>Logs that a saga became complete and a completion event is being published.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The completed saga.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaComplete,
        Level = LogLevel.Information,
        Message = "Saga {SagaId} is complete, publishing completion event" )]
    private static partial void LogSagaComplete( ILogger logger, string sagaId );

    /// <summary>Logs a failure to check or publish saga completion.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="sagaId">The saga whose completion check failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaCompletionCheckError,
        Level = LogLevel.Error,
        Message = "Failed to check/publish saga completion for {SagaId}" )]
    private static partial void LogSagaCompletionCheckError( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs a failure to publish a per-lookup completion event.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="sagaId">The saga whose lookup completion failed to publish.</param>
    [LoggerMessage(
        EventId = LogEventIds.PublishCompletionError,
        Level = LogLevel.Error,
        Message = "Failed to publish lookup completion for saga {SagaId}" )]
    private static partial void LogPublishCompletionError( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs a failure to publish the rate-limit sentinel for a saga.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="sagaId">The saga whose sentinel failed to publish.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkPublishRateLimitSentinelError,
        Level = LogLevel.Error,
        Message = "Failed to publish rate-limit sentinel for saga {SagaId}" )]
    private static partial void LogBulkPublishRateLimitSentinelError( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that the bulk endpoint returned a deterministic 4xx and each item is being re-enqueued individually.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="statusCode">The HTTP status code returned by the bulk endpoint.</param>
    /// <param name="count">The number of items being re-enqueued.</param>
    [LoggerMessage(
        EventId = LogEventIds.BulkBatchRejected,
        Level = LogLevel.Warning,
        Message = "Bulk batch rejected with HTTP {StatusCode}; re-enqueueing {Count} items as individual lookups" )]
    private static partial void LogBulkBatchRejected( ILogger logger, int statusCode, int count );

    #endregion
}
