using System.Diagnostics;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Core.Infrastructure.Utilities;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace BridgeBeats.Core.Domain.Services.Queue;

/// <summary>
/// Worker-side queue consumer: one instance per provider drains that provider's request queue,
/// performs the actual provider lookup, records the outcome in the shared saga state, and publishes
/// completion so the orchestrator's waiters can wake. The saga state is the durable contract shared
/// with the lookup orchestrator on the web side.
/// </summary>
/// <remarks>
/// Per message the loop: resolves or creates the saga and initializes this provider's state;
/// pre-checks the endpoint rate limit and requeues if limited; performs the lookup; on success
/// records the serialized result, checks/publishes saga completion, publishes the per-lookup
/// completion, and acknowledges. A <see cref="ProviderRateLimitException"/> (including its
/// <see cref="RetryAfterExceededException"/> subtype) marks the saga partial,
/// merges this provider's rate-limit window, publishes the rate-limited sentinel, and re-enqueues on
/// the origin lane (interactive requests remain interactive). Other exceptions retry up to
/// <see cref="LookupConstants.MaxQueueRetryAttempts"/>, then move to the DLQ.
/// </remarks>
public sealed partial class QueueProcessorBackgroundService : BackgroundService {
    private sealed class PermanentRequestException( string message ) : Exception( message );

    /// <summary>The Redis connection used to publish saga and lookup completion events.</summary>
    private readonly IConnectionMultiplexer _redis;
    /// <summary>This provider's request queue, drained for work.</summary>
    private readonly IRequestQueue<QueuedLookupRequest> _queue;
    /// <summary>Tracks per-provider, per-endpoint rate-limit windows.</summary>
    private readonly IRateLimitTracker _rateLimitTracker;
    /// <summary>Reads and updates the durable saga state shared with the orchestrator.</summary>
    private readonly ISagaStateManager _sagaManager;
    /// <summary>The provider lookup service that performs the actual API call for this provider.</summary>
    private readonly IMusicLookupService _lookupService;
    /// <summary>The provider this processor instance serves.</summary>
    private readonly SupportedProviders _provider;
    /// <summary>The logger for processing lifecycle, rate-limit, and failure diagnostics.</summary>
    private readonly ILogger<QueueProcessorBackgroundService> _logger;
    /// <summary>Bound on simultaneous provider calls made by this worker.</summary>
    private readonly int _maxConcurrency;
    /// <summary>camelCase JSON options used to serialize provider results into saga state.</summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Redis pub/sub channel announcing that a whole saga has completed.</summary>
    private const string SagaCompletedChannel = "saga:completed";
    /// <summary>Prefix for the per-lookup Redis channel (<c>complete:{lookupKey}</c>) that wakes orchestrator waiters.</summary>
    private const string LookupCompleteChannelPrefix = "complete:";
    /// <summary>Scheduled recovery event after an unexpected loop error (1 second).</summary>
    private static readonly TimeSpan s_errorDelay = TimeSpan.FromSeconds( 1 );
    private const int MaxAcknowledgementRecoveryAttempts = 4;

    /// <summary>
    /// Initializes a processor for a single provider with its queue, rate-limit tracker, saga manager,
    /// lookup service, Redis connection, and logger.
    /// </summary>
    /// <param name="redis">The Redis connection used to publish completion events.</param>
    /// <param name="queue">This provider's request queue.</param>
    /// <param name="rateLimitTracker">Tracker for per-provider, per-endpoint rate-limit windows.</param>
    /// <param name="sagaManager">Manager for the shared, durable saga state.</param>
    /// <param name="lookupService">The provider lookup service that performs the actual API call.</param>
    /// <param name="provider">The provider this processor serves.</param>
    /// <param name="logger">The logger for processing diagnostics.</param>
    /// <param name="settings">Queue settings that bound per-provider concurrency.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null.</exception>
    public QueueProcessorBackgroundService(
        IConnectionMultiplexer redis,
        IRequestQueue<QueuedLookupRequest> queue,
        IRateLimitTracker rateLimitTracker,
        ISagaStateManager sagaManager,
        IMusicLookupService lookupService,
        SupportedProviders provider,
        ILogger<QueueProcessorBackgroundService> logger,
        QueueSettings? settings = null
    ) {
        _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
        _queue = queue ?? throw new ArgumentNullException( nameof( queue ) );
        _rateLimitTracker = rateLimitTracker ?? throw new ArgumentNullException( nameof( rateLimitTracker ) );
        _sagaManager = sagaManager ?? throw new ArgumentNullException( nameof( sagaManager ) );
        _lookupService = lookupService ?? throw new ArgumentNullException( nameof( lookupService ) );
        _provider = provider;
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _maxConcurrency = (settings ?? new QueueSettings( )).GetProviderConcurrency( provider );

        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Runs the consume loop until the host stops. Ensures Redis Streams consumer groups exist, then
    /// repeatedly dequeues (rate-limit aware) up to the configured concurrency. When no delivery is
    /// eligible it waits for a Redis work notification, an in-flight completion, or the exact expiry
    /// of a provider rate-limit window. Cancellation ends the loop; control-plane failures use a
    /// scheduled recovery event rather than sleeping the consumer.
    /// </summary>
    /// <param name="stoppingToken">Signals that the host is shutting down.</param>
    /// <returns>A task that completes when the processor stops.</returns>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        LogQueueProcessorStarting( _logger, _provider );

        if (_queue is IQueueWorkSignal workSignal) {
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await workSignal.InitializeWorkSignalAsync( stoppingToken );
                    break;
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    LogQueueProcessorLoopError( _logger, ex, _provider );
                    await WaitForTimerEventAsync( s_errorDelay, stoppingToken );
                }
            }
        }

        // Ensure consumer groups exist before starting to consume. Decorated queues expose the
        // same capability so the processor does not depend on a concrete queue implementation.
        if (_queue is IConsumerGroupAssurance startupQueue) {
            try {
                await startupQueue.EnsureConsumerGroupsAsync( stoppingToken );
            } catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested) {
                LogQueueProcessorLoopError( _logger, ex, _provider );
                await WaitForTimerEventAsync( s_errorDelay, stoppingToken );
            }
        }

        Dictionary<Task, string> inFlight = [];
        while (!stoppingToken.IsCancellationRequested) {
            try {
                if (inFlight.Count >= _maxConcurrency) {
                    Task completed = await Task.WhenAny( inFlight.Keys );
                    _ = inFlight.Remove( completed );
                    await completed;
                    continue;
                }

                long observedWorkVersion = _queue is IQueueWorkSignal versionedSignal
                    ? versionedSignal.CaptureWorkVersion( )
                    : 0;
                QueuedMessage<QueuedLookupRequest>? message;
                // Use rate-limit-aware dequeue to skip messages for blocked endpoints
                message = await _queue.DequeueAsync( _rateLimitTracker, stoppingToken );

                if (message is null) {
                    foreach (Task completed in inFlight.Keys.Where( task => task.IsCompleted ).ToArray( )) {
                        _ = inFlight.Remove( completed );
                        await completed;
                    }
                    await WaitForQueueEventAsync( observedWorkVersion, inFlight, stoppingToken );
                    continue;
                }

                if (inFlight.ContainsValue( message.MessageId )) {
                    // Defensive second fence: a queue implementation must not hand out an active
                    // PEL entry, but the processor still refuses concurrent work for one delivery.
                    Task completed = await Task.WhenAny( inFlight.Keys );
                    _ = inFlight.Remove( completed );
                    await completed;
                    continue;
                }

                Task processing = ProcessDequeuedMessageAsync( message, stoppingToken );
                inFlight.Add( processing, message.MessageId );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // Normal shutdown
                break;
            } catch (RedisServerException ex) when (ex.Message.Contains( "NOGROUP", StringComparison.OrdinalIgnoreCase )
                                                      && _queue is IConsumerGroupAssurance recoveryQueue) {
                try {
                    await recoveryQueue.EnsureConsumerGroupsAsync( stoppingToken );
                } catch (Exception repairEx) when (repairEx is not OperationCanceledException || !stoppingToken.IsCancellationRequested) {
                    LogQueueProcessorLoopError( _logger, repairEx, _provider );
                    await WaitForTimerEventAsync( s_errorDelay, stoppingToken );
                }
            } catch (Exception ex) {
                LogQueueProcessorLoopError( _logger, ex, _provider );
                await WaitForTimerEventAsync( s_errorDelay, stoppingToken );
            }
        }

        try {
            await Task.WhenAll( inFlight.Keys );
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal shutdown of in-flight provider calls.
        }

        LogQueueProcessorStopping( _logger, _provider );
    }

    private async Task WaitForQueueEventAsync(
        long observedWorkVersion,
        IReadOnlyDictionary<Task, string> inFlight,
        CancellationToken cancellationToken
    ) {
        if (_queue is IQueueWorkSignal workSignal) {
            IReadOnlyList<RateLimitedEndpoint> limits =
                await _rateLimitTracker.GetAllRateLimitedAsync( _provider, cancellationToken );
            DateTimeOffset? nextEligibility = limits.Count == 0
                ? null
                : limits.Min( limit => limit.RetryAfter );
            Task wake = workSignal.WaitForWorkAsync( observedWorkVersion, nextEligibility, cancellationToken );
            if (inFlight.Count == 0) {
                await wake;
            } else {
                _ = await Task.WhenAny( inFlight.Keys.Append( wake ) );
            }
            return;
        }

        if (inFlight.Count > 0) {
            _ = await Task.WhenAny( inFlight.Keys );
            return;
        }

        // Alternate queue implementations without broker notifications receive a scheduled event;
        // the production Redis queue never enters this fallback.
        await WaitForTimerEventAsync( TimeSpan.FromMilliseconds( 100 ), cancellationToken );
    }

    private static async Task WaitForTimerEventAsync( TimeSpan dueTime, CancellationToken cancellationToken ) {
        TaskCompletionSource elapsed = new( TaskCreationOptions.RunContinuationsAsynchronously );
        using Timer timer = new(
            static state => ((TaskCompletionSource)state!).TrySetResult( ),
            elapsed,
            dueTime,
            Timeout.InfiniteTimeSpan );
        await elapsed.Task.WaitAsync( cancellationToken );
    }

    private async Task ProcessDequeuedMessageAsync(
        QueuedMessage<QueuedLookupRequest> message,
        CancellationToken stoppingToken
    ) {
        Stopwatch wallStopwatch = Stopwatch.StartNew( );
        using Activity? wallActivity = QueueMetrics.ActivitySource.StartActivity( "queue.message.wall" );
        _ = (wallActivity?.SetTag( QueueMetricTags.Provider, _provider.ToString( ).ToLowerInvariant( ) ));
        _ = (wallActivity?.SetTag( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ));
        try {
            await ProcessMessageAsync( message, stoppingToken );
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // The unacknowledged delivery remains in the PEL for recovery after restart.
        } catch (PostCommitAcknowledgementException ex) {
            LogQueueProcessorLoopError( _logger, ex, _provider );
            // The provider result is committed. Broker recovery must not consume one of the
            // bounded provider-work slots during a Redis outage.
            Task recovery = RecoverCommittedAcknowledgementAsync( message, stoppingToken );
            if (_queue is IQueueDeliveryTracker) {
                _ = recovery;
            } else {
                // Alternate/test queues have no active-delivery fence, so they must keep this
                // delivery attached to the loop until recovery finishes or shutdown cancels it.
                await recovery;
            }
        } catch (Exception ex) {
            LogQueueProcessorLoopError( _logger, ex, _provider );
            // Control-plane recovery is scheduled independently of provider retries. Keeping the
            // delivery active until the event fires prevents an immediate own-PEL hot loop.
            try {
                await WaitForTimerEventAsync( s_errorDelay, stoppingToken );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            if (_queue is IQueueDeliveryTracker tracker) tracker.ReleaseDelivery( message.MessageId );
        } finally {
            wallStopwatch.Stop( );
            QueueMetrics.MessageWallDuration.Record( wallStopwatch.Elapsed.TotalSeconds,
                new KeyValuePair<string, object?>( QueueMetricTags.Provider, _provider.ToString( ).ToLowerInvariant( ) ),
                new KeyValuePair<string, object?>( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ) );
        }
    }

    /// <summary>Retries a post-commit acknowledgement with bounded exponential backoff.</summary>
    internal async Task RecoverCommittedAcknowledgementAsync(
        QueuedMessage<QueuedLookupRequest> message,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? waitAsync = null
    ) {
        waitAsync ??= WaitForTimerEventAsync;
        for (int attempt = 1; attempt <= MaxAcknowledgementRecoveryAttempts; attempt++) {
            try {
                await waitAsync(
                    TimeSpan.FromSeconds( 1 << (attempt - 1) ), cancellationToken );
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            }
            try {
                await _queue.AcknowledgeAsync( message.MessageId, cancellationToken );
                QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "ack_recovered" );
                return;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                return;
            } catch (Exception ackEx) {
                LogQueueProcessorLoopError( _logger, ackEx, _provider );
                QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "ack_recovery_failed" );
            }
        }

        // The entry remains in Redis's PEL. Release only the local delivery fence so normal
        // idempotent processing can recover it on a later work signal or reclaim wake.
        if (_queue is IQueueDeliveryTracker tracker) tracker.ReleaseDelivery( message.MessageId );
    }

    /// <summary>
    /// Processes one dequeued message end to end: rebuilds the lookup key, ensures the saga and this
    /// provider's state, pre-checks the endpoint rate limit (requeuing if limited), performs the
    /// lookup, records the result in saga state, publishes saga and lookup completion, and
    /// acknowledges. Rate-limit and other failures are routed to their handlers. The processing
    /// duration recorded here is deliberately post-dequeue only (it excludes <c>DequeueAsync</c>);
    /// the end-to-end dequeue-to-completion metric is <c>queue.message.wall.duration</c>.
    /// </summary>
    /// <param name="message">The dequeued message carrying the lookup request.</param>
    /// <param name="ct">Cancels processing.</param>
    /// <returns>A task that completes when the message has been processed.</returns>
    private async Task ProcessMessageAsync( QueuedMessage<QueuedLookupRequest> message, CancellationToken ct ) {
        QueuedLookupRequest request = message.Payload;
        Stopwatch stopwatch = Stopwatch.StartNew( );
        Stopwatch? httpTimer = null;
        string status = "success";
        string? instanceToken = null;

        LogProcessingMessage( _logger, message.MessageId, request.SagaId, request.LookupType );

        try {

            // Every producer creates and fences the saga before publishing its delivery. A missing
            // saga therefore identifies an expired or stale delivery and must never be recreated
            // here, because doing so would attach old work to a new generation.
            // The lookupKey MUST use the same canonical format as LookupOrchestrator so that
            // orchestrator-produced and worker-produced saga IDs agree for the same entity.
            // Use LookupKeyBuilder to derive the key from the request's own fields.
            string lookupKey = request.LookupType is LookupRequestType.SongIdLookup or LookupRequestType.AlbumIdLookup
            ? LookupKeyBuilder.TypedKey( request.LookupType, _provider, request.LookupValue )
            : request.LookupType == LookupRequestType.UriLookup
                ? LookupKeyBuilder.UrlKey( request.LookupValue )
                : $"{request.LookupType}:{request.LookupValue}";
            LookupSagaState? authoritativeSaga = await _sagaManager.GetAsync( request.SagaId, ct );
            if (authoritativeSaga is null) {
                // Producers create and fence the saga before publishing a delivery. Recreating it
                // here would let a delivery from an expired generation attach to a new instance.
                status = "stale";
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }

            bool rootIdentityMatches = string.Equals( authoritativeSaga.LookupKey, lookupKey, StringComparison.Ordinal )
                && authoritativeSaga.LookupType == request.LookupType
                && string.Equals( authoritativeSaga.LookupValue, request.LookupValue, StringComparison.Ordinal );
            instanceToken = authoritativeSaga.InstanceToken;
            if (string.IsNullOrWhiteSpace( instanceToken )) {
                status = "stale";
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }
            if (string.IsNullOrWhiteSpace( request.SagaInstanceToken )
                || !string.Equals( request.SagaInstanceToken, instanceToken, StringComparison.Ordinal )) {
                status = "stale";
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }
            bool legitimateChild = authoritativeSaga.ProviderStates.TryGetValue( _provider, out ProviderLookupState? existingProviderState )
                && !existingProviderState.IsComplete;
            if (authoritativeSaga.ProviderStates.TryGetValue( _provider, out ProviderLookupState? completedProviderState )
                && completedProviderState.IsComplete) {
                if (completedProviderState.ErrorMessage is not null) {
                    try {
                        await _queue.MoveToDlqAsync( message.MessageId, completedProviderState.ErrorMessage, ct );
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        throw;
                    } catch (Exception ex) {
                        throw new TerminalDlqRecoveryException( message.MessageId, ex );
                    }
                    QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "dlq_recovery" );
                    status = "failure";
                    return;
                }
                // A redelivered delivery can follow a successful provider commit whose ACK was
                // lost. It is already represented by the active saga leg, so clear only this stale
                // delivery and never invoke the provider, mutate state, or quarantine it.
                status = "stale";
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }
            if (!rootIdentityMatches
                && !legitimateChild) {
                status = "failure";
                await QuarantineIdentityMismatchAsync( message, request, lookupKey, authoritativeSaga, ct );
                return;
            }
            if (!await _sagaManager.TryInitializeProviderStatesAsync( request.SagaId, [_provider], instanceToken, ct )) {
                status = "stale";
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }

            // Check if the endpoint for this lookup type is rate-limited
            string endpoint = request.LookupType.ToString( );
            RateLimitState rateLimitState = await _rateLimitTracker.GetStateAsync( _provider, endpoint, ct );

            if (rateLimitState.IsRateLimited) {
                status = "rate_limited";
                if (_logger.IsEnabled( LogLevel.Debug )) {
                    DateTimeOffset retryAfter = rateLimitState.RetryAfter.GetValueOrDefault( );
                    LogEndpointRateLimited( _logger, endpoint, retryAfter, message.MessageId );
                }

                // Requeue with delay until rate limit expires
                await _queue.RequeueAsync( message.MessageId, rateLimitState.TimeRemaining, ct );

                return;
            }

            // Perform the lookup
            using Activity? httpActivity = QueueMetrics.ActivitySource.StartActivity( "queue.provider_http" );
            _ = (httpActivity?.SetTag( QueueMetricTags.Provider, _provider.ToString( ).ToLowerInvariant( ) ));
            _ = (httpActivity?.SetTag( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ));
            httpTimer = Stopwatch.StartNew( );
            MusicLookupResult? result;
            try {
                result = await PerformLookupAsync( request, ct );
            } finally {
                httpTimer.Stop( );
                httpActivity?.Stop( );
            }

            Stopwatch sagaTimer = Stopwatch.StartNew( );
            using Activity? sagaActivity = QueueMetrics.ActivitySource.StartActivity( "queue.saga.update" );
            _ = (sagaActivity?.SetTag( QueueMetricTags.Provider, _provider.ToString( ).ToLowerInvariant( ) ));
            _ = (sagaActivity?.SetTag( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ));
            try {
                if (!await _sagaManager.TryUpdateProviderStateAsync( request.SagaId,
                        new ProviderLookupState( _provider, true, result is not null,
                            result is not null ? JsonSerializer.Serialize( result, _jsonOptions ) : null,
                            DateTimeOffset.UtcNow, null ), instanceToken, ct )) {
                    status = "stale";
                    await AcknowledgeStaleDeliveryAsync( message, ct );
                    return;
                }
            } finally {
                sagaTimer.Stop( );
                sagaActivity?.Stop( );
                QueueMetrics.SagaUpdateDuration.Record( sagaTimer.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>( QueueMetricTags.Provider, _provider.ToString( ).ToLowerInvariant( ) ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ) );
            }
            // Acknowledge the message before publishing completion events so that a crash
            // between the two does not leave the message in the PEL and re-publish both
            // completion channels on redelivery. If the publish calls are lost, the 30-second
            // polling sweep re-finalizes within one cycle.
            try {
                await _queue.AcknowledgeAsync( message.MessageId, ct );
                QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "success" );
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                // Provider state is already committed. Keep the PEL delivery pending for the
                // worker loop's recovery/backoff path; do not feed an ACK failure into the
                // ordinary provider-failure handler, which could overwrite or DLQ the result.
                throw new PostCommitAcknowledgementException( message.MessageId, ex );
            }

            // Provider state is already durable. Publish an idempotent progress event without a
            // second saga read; a transient read failure here must not suppress finalization after
            // the delivery has been acknowledged.
            await PublishSagaProgressAsync( request.SagaId );
            QueueMetrics.RecordSagaLegCompleted( _provider, message.Priority, "committed" );

            LogMessageProcessed( _logger, message.MessageId, request.SagaId );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Host shutdown is not a retryable provider failure; leave the message pending for
            // redelivery after restart.
            throw;
        } catch (UnreadableSagaStateException ex) {
            status = "failure";
            await QuarantineUnreadableSagaAsync( message, request, ex, ct );
        } catch (PostCommitAcknowledgementException) {
            status = "failure";
            throw;
        } catch (TerminalDlqRecoveryException) {
            status = "failure";
            throw;
        } catch (QueueDeliveryIdentityQuarantineException) {
            status = "failure";
            throw;
        } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
            status = "failure";
            await HandlePermanentRequestExceptionAsync(
                message,
                request,
                new PermanentRequestException( "Provider returned HTTP 404 (not found)." ),
                instanceToken,
                ct );
        } catch (PermanentRequestException ex) {
            status = "failure";
            await HandlePermanentRequestExceptionAsync( message, request, ex, instanceToken, ct );
        } catch (ProviderRateLimitException ex) {
            status = "rate_limited";
            if (string.IsNullOrWhiteSpace( instanceToken )) throw;
            await HandleRateLimitExceptionAsync( message, request, ex, instanceToken, ct );
        } catch (Exception ex) {
            status = "failure";
            if (string.IsNullOrWhiteSpace( instanceToken )) throw;
            await HandleProcessingExceptionAsync( message, request, ex, instanceToken, ct );
        } finally {
            stopwatch.Stop( );
            if (httpTimer is not null) {
                QueueMetrics.ProviderHttpDuration.Record( httpTimer.Elapsed.TotalSeconds,
                    new KeyValuePair<string, object?>( QueueMetricTags.Provider, _provider.ToString( ).ToLowerInvariant( ) ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Priority, message.Priority.ToString( ).ToLowerInvariant( ) ) );
            }
            QueueMetrics.RecordProcessingDuration( _provider, request.LookupType, status, stopwatch.Elapsed.TotalSeconds, message.Priority );
        }
    }

    /// <summary>
    /// Dispatches a queued request to the matching method on the provider lookup service based on its
    /// <see cref="LookupRequestType"/> (URL, ISRC, UPC, song/album id, or title+artist search).
    /// </summary>
    /// <param name="request">The lookup request to perform.</param>
    /// <param name="ct">Cancels the lookup; checked before dispatch.</param>
    /// <returns>The provider result, or <see langword="null"/> when the provider found no match.</returns>
    /// <remarks>
    /// Title+artist lookup types (<see cref="LookupRequestType.ArtistLookup"/>,
    /// <see cref="LookupRequestType.SongLookup"/>, <see cref="LookupRequestType.AlbumLookup"/>,
    /// <see cref="LookupRequestType.ArtistAlbumLookup"/>, and <see cref="LookupRequestType.AlbumTrackLookup"/>)
    /// all resolve through <see cref="IMusicLookupService.GetInfoAsync(string, string)"/>, which performs
    /// the artist-to-album and album-to-track expansion internally. The request must carry non-null
    /// <see cref="QueuedLookupRequest.Title"/> and <see cref="QueuedLookupRequest.Artist"/> values; a
    /// request missing either falls through to the <see cref="InvalidOperationException"/> arm. Any other
    /// unrecognized type, or a search type missing its title/artist, throws
    /// <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown for an unsupported lookup type or missing required parameters.</exception>
    private async Task<MusicLookupResult?> PerformLookupAsync( QueuedLookupRequest request, CancellationToken ct ) {
        // Check for cancellation before performing the lookup
        ct.ThrowIfCancellationRequested( );

        MusicLookupResult? result = await PerformSingleLookupAsync(
            request.LookupType,
            request.LookupValue,
            request.Title,
            request.Artist,
            request.Storefront
        );

        if (result is not null
            || request.FallbackLookupType is null
            || string.IsNullOrWhiteSpace( request.FallbackLookupValue )) {
            return result;
        }

        return await PerformSingleLookupAsync(
            request.FallbackLookupType.Value,
            request.FallbackLookupValue,
            request.Title,
            request.Artist,
            request.Storefront
        );
    }

    private async Task<MusicLookupResult?> PerformSingleLookupAsync(
        LookupRequestType lookupType,
        string lookupValue,
        string? title,
        string? artist,
        string? storefront
    ) {
        IStorefrontMusicLookupService? storefrontService = !string.IsNullOrWhiteSpace( storefront )
            ? _lookupService as IStorefrontMusicLookupService
            : null;

        // Map LookupRequestType to the appropriate lookup method
        return lookupType switch {
            LookupRequestType.UriLookup => await _lookupService.GetInfoAsync( lookupValue ),
            LookupRequestType.IsrcLookup when storefrontService is not null =>
                await storefrontService.GetInfoByISRCAsync( lookupValue, storefront! ),
            LookupRequestType.IsrcLookup => await _lookupService.GetInfoByISRCAsync( lookupValue ),
            LookupRequestType.UpcLookup when storefrontService is not null =>
                await storefrontService.GetInfoByUPCAsync( lookupValue, storefront! ),
            LookupRequestType.UpcLookup => await _lookupService.GetInfoByUPCAsync( lookupValue ),
            LookupRequestType.SongIdLookup when storefrontService is not null =>
                await storefrontService.GetInfoByIDAsync( lookupValue, false, storefront! ),
            LookupRequestType.SongIdLookup => await _lookupService.GetInfoByIDAsync( lookupValue, false ),
            LookupRequestType.AlbumIdLookup when storefrontService is not null =>
                await storefrontService.GetInfoByIDAsync( lookupValue, true, storefront! ),
            LookupRequestType.AlbumIdLookup => await _lookupService.GetInfoByIDAsync( lookupValue, true ),
            LookupRequestType.ArtistLookup when title is not null && artist is not null =>
                await _lookupService.GetInfoAsync( title, artist ),
            LookupRequestType.SongLookup when title is not null && artist is not null =>
                await _lookupService.GetInfoAsync( title, artist ),
            LookupRequestType.AlbumLookup when title is not null && artist is not null =>
                await _lookupService.GetInfoAsync( title, artist ),
            LookupRequestType.ArtistAlbumLookup when title is not null && artist is not null =>
                await _lookupService.GetInfoAsync( title, artist ),
            LookupRequestType.AlbumTrackLookup when title is not null && artist is not null =>
                await _lookupService.GetInfoAsync( title, artist ),
            _ => throw new PermanentRequestException(
                            $"Unsupported lookup type {lookupType} or missing required parameters"
                        )
        };
    }

    private async Task HandlePermanentRequestExceptionAsync(
        QueuedMessage<QueuedLookupRequest> message,
        QueuedLookupRequest request,
        PermanentRequestException ex,
        string? instanceToken,
        CancellationToken ct
    ) {
        LogProcessingFailed( _logger, ex, request.RequestId, request.SagaId );
        if (string.IsNullOrWhiteSpace( instanceToken ) || !await _sagaManager.TryUpdateProviderStateAsync(
            request.SagaId,
            new ProviderLookupState(
                _provider,
                IsComplete: true,
                IsSuccess: false,
                ResultJson: null,
                CompletedAt: DateTimeOffset.UtcNow,
                ErrorMessage: ex.Message ),
            instanceToken, ct )) {
            await AcknowledgeStaleDeliveryAsync( message, ct );
            return;
        }
        await _queue.MoveToDlqAsync( message.MessageId, ex.Message, ct );
        QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "permanent_failure" );
        await PublishSagaProgressAsync( request.SagaId );
        QueueMetrics.RecordSagaLegCompleted( _provider, message.Priority, "committed_failure" );
    }

    /// <summary>
    /// Handles a provider rate-limit hit: records the endpoint's retry window, marks the saga partial,
    /// merges this provider's rate-limit info into the saga (replacing any prior entry for it),
    /// publishes the rate-limited sentinel to wake waiters with a partial, then re-enqueues the
    /// request before acknowledging the current message. Interactive-origin work remains on the
    /// interactive single-item lane across the rate-limit window.
    /// </summary>
    /// <param name="message">The message being processed when the limit was hit.</param>
    /// <param name="request">The lookup request that hit the rate limit.</param>
    /// <param name="ex">The rate-limit exception carrying the retry-after window.</param>
    /// <param name="instanceToken">The saga instance fence captured before provider work.</param>
    /// <param name="ct">Cancels the saga and queue operations.</param>
    /// <returns>A task that completes once the saga is updated and the request re-enqueued.</returns>
    private async Task HandleRateLimitExceptionAsync(
        QueuedMessage<QueuedLookupRequest> message,
        QueuedLookupRequest request,
        ProviderRateLimitException ex,
        string instanceToken,
        CancellationToken ct
    ) {
        // Use the LookupRequestType as the endpoint identifier
        string endpoint = request.LookupType.ToString( );

        LogRateLimitEncountered( _logger, _provider, endpoint, ex.RetryAfterValue );

        // Record the rate limit state
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan boundedRetry = TimeSpan.FromTicks( Math.Min( ex.RetryAfterValue.Ticks, (DateTimeOffset.MaxValue - now).Ticks ) );
        DateTimeOffset retryAfter = now.Add( boundedRetry );
        await _rateLimitTracker.SetRateLimitedAsync( _provider, endpoint, retryAfter, ct );

        // Mark saga as partial and record rate limit info for user notification
        if (!await _sagaManager.TrySetIsPartialAsync( request.SagaId, true, instanceToken, ct )) {
            await AcknowledgeStaleDeliveryAsync( message, ct );
            return;
        }

        // Merge with rate limit info recorded by other providers so no entry is lost -
        // the orchestrator's all-pending-providers-rate-limited escape hatch depends on
        // every rate-limited provider being present. This is a read-modify-write (the
        // saga manager has no atomic merge); a lost update under concurrent writes only
        // degrades to the previous overwrite behavior.
        LookupSagaState? sagaForRateLimit = await _sagaManager.GetAsync( request.SagaId, ct );
        List<ProviderRateLimitInfo> mergedRateLimitInfo = sagaForRateLimit?.RateLimitInfo is not null
            ? [.. sagaForRateLimit.RateLimitInfo.Where( r => r.Provider != _provider )]
            : [];
        mergedRateLimitInfo.Add( new ProviderRateLimitInfo( _provider, retryAfter, endpoint ) );

        if (!await _sagaManager.TrySetRateLimitInfoAsync( request.SagaId, mergedRateLimitInfo, instanceToken, ct )) {
            await AcknowledgeStaleDeliveryAsync( message, ct );
            return;
        }

        // Publish rate-limited sentinel so waiting clients know this is a partial result
        await PublishRateLimitSentinelAsync( request.SagaId, instanceToken, ct );

        // Requeue request - it will be skipped by rate-limit-aware dequeue until rate limit expires
        // The RateLimitedEndpoint field helps track which endpoint triggered the limit
        QueuedLookupRequest requeuedRequest = request with {
            AttemptCount = request.AttemptCount + 1,
            RateLimitedEndpoint = endpoint,
            EnqueueOrigin = QueueEnqueueOrigin.Requeue,
            // Interactive typed-id work must remain on the single-item lane rather than being
            // intercepted by Spotify's bulk decorator after the rate-limit window.
            BypassBulkRouting = request.BypassBulkRouting
                || request.OriginPriority == QueuePriority.Interactive
        };

        QueuePriority requeuePriority = request.OriginPriority;
        await _queue.EnqueueAsync( requeuedRequest, requeuePriority, ct );
        if (requeuePriority == QueuePriority.Interactive) {
            QueueMetrics.RecordInteractiveRetry( _provider, endpoint );
        }
        await _queue.AcknowledgeAsync( message.MessageId, ct );

        LogSagaMarkedPartial( _logger, request.SagaId, endpoint, _provider, retryAfter );
    }

    /// <summary>
    /// Handles a non-rate-limit processing failure. When the request has reached
    /// <see cref="LookupConstants.MaxQueueRetryAttempts"/>, marks this provider's saga state failed,
    /// moves the message to the dead-letter queue, and checks/publishes saga completion. Otherwise
    /// records the provider failure as incomplete and atomically replaces the delivery for another
    /// event-driven attempt.
    /// </summary>
    /// <param name="message">The message being processed when the failure occurred.</param>
    /// <param name="request">The lookup request that failed.</param>
    /// <param name="ex">The exception that caused the failure.</param>
    /// <param name="instanceToken">The saga instance fence captured before provider work.</param>
    /// <param name="ct">Cancels the saga and queue operations.</param>
    /// <returns>A task that completes once the failure has been recorded and routed.</returns>
    private async Task HandleProcessingExceptionAsync(
        QueuedMessage<QueuedLookupRequest> message,
        QueuedLookupRequest request,
        Exception ex,
        string instanceToken,
        CancellationToken ct
    ) {
        LogProcessingFailed( _logger, ex, request.RequestId, request.SagaId );

        if (request.AttemptCount >= LookupConstants.MaxQueueRetryAttempts - 1) {
            LogMaxRetriesExceeded( _logger, message.MessageId, LookupConstants.MaxQueueRetryAttempts );

            // Update saga with error state
            if (!await _sagaManager.TryUpdateProviderStateAsync(
                request.SagaId,
                new ProviderLookupState(
                    Provider: _provider,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: $"Failed after {LookupConstants.MaxQueueRetryAttempts} attempts: {ex.Message}"
                ),
                instanceToken, ct )) {
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }

            // Move to Dead Letter Queue
            await _queue.MoveToDlqAsync( message.MessageId, ex.Message, ct );
            QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "retry_exhausted" );
            await PublishSagaProgressAsync( request.SagaId );
            QueueMetrics.RecordSagaLegCompleted( _provider, message.Priority, "committed_failure" );
        } else {
            if (!await _sagaManager.TryUpdateProviderStateAsync(
                request.SagaId,
                new ProviderLookupState( _provider, false, false, null, null, ex.Message ), instanceToken, ct )) {
                await AcknowledgeStaleDeliveryAsync( message, ct );
                return;
            }
            await _queue.RequeueAsync( message.MessageId, cancellationToken: ct );
        }
    }

    private async Task QuarantineUnreadableSagaAsync(
        QueuedMessage<QueuedLookupRequest> message,
        QueuedLookupRequest request,
        UnreadableSagaStateException exception,
        CancellationToken ct
    ) {
        try {
            await _queue.MoveToDlqAsync( message.MessageId, exception.Message, ct );
            QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "corrupt_saga_quarantine" );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogIdentityQuarantineFailed( _logger, ex, message.MessageId, request.SagaId );
            throw new QueueDeliveryIdentityQuarantineException( message.MessageId, request.SagaId, ex );
        }
    }

    private async Task QuarantineIdentityMismatchAsync(
        QueuedMessage<QueuedLookupRequest> message,
        QueuedLookupRequest request,
        string requestLookupKey,
        LookupSagaState? authoritativeSaga,
        CancellationToken ct
    ) {
        LogSagaIdentityMismatch( _logger, request.SagaId, message.MessageId, requestLookupKey,
            authoritativeSaga?.LookupKey ?? "(missing)", request.LookupType,
            authoritativeSaga?.LookupType ?? request.LookupType, request.LookupValue,
            authoritativeSaga?.LookupValue ?? "(missing)" );
        try {
            await _queue.MoveToDlqAsync( message.MessageId, "Saga identity does not match queued request.", ct );
            QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "identity_quarantine" );
        } catch (Exception ex) {
            LogIdentityQuarantineFailed( _logger, ex, message.MessageId, request.SagaId );
            throw new QueueDeliveryIdentityQuarantineException( message.MessageId, request.SagaId, ex );
        }
    }

    private async Task AcknowledgeStaleDeliveryAsync( QueuedMessage<QueuedLookupRequest> message, CancellationToken ct ) {
        try {
            await _queue.AcknowledgeAsync( message.MessageId, ct );
            QueueMetrics.RecordTerminalOutcome( _provider, message.Priority, "stale_delivery" );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            throw new PostCommitAcknowledgementException( message.MessageId, ex );
        }
    }

    /// <summary>
    /// Publishes the saga-completed event on the <c>saga:completed</c> channel when the saga exists, is
    /// complete, and has not already been finalized. No-ops (with a diagnostic log) when the saga is
    /// missing, incomplete, or already has a final result. Failures are logged and swallowed.
    /// </summary>
    /// <param name="sagaId">The saga to check and possibly announce.</param>
    /// <param name="expectedInstanceToken">The saga instance fence.</param>
    /// <param name="ct">Cancels the saga read.</param>
    /// <returns>A task that completes once the check (and any publish) finishes.</returns>
    private async Task<LookupSagaState?> CheckAndPublishSagaCompletionAsync( string sagaId, string expectedInstanceToken, CancellationToken ct ) {
        try {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null || saga.InstanceToken != expectedInstanceToken) {
                LogSagaNotFoundForCompletion( _logger, sagaId );
                return null;
            }

            if (!saga.IsComplete) {
                LogSagaNotYetComplete( _logger, sagaId );
                return saga;
            }

            if (!string.IsNullOrEmpty( saga.FinalResultUri )) {
                LogSagaAlreadyHasFinalResult( _logger, sagaId );
                return saga;
            }

            // Saga is complete but doesn't have a final result - publish completion event
            LogSagaCompletePublishing( _logger, sagaId );
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( SagaCompletedChannel ), sagaId );
            return saga;
        } catch (Exception ex) {
            LogSagaCompletionCheckFailed( _logger, ex, sagaId );
            return null;
        }
    }

    /// <summary>
    /// Publishes an idempotent provider-leg progress hint. The coordinator owns the authoritative
    /// completeness and instance-token checks; publishing does not depend on a post-commit read.
    /// </summary>
    private async Task PublishSagaProgressAsync( string sagaId ) {
        try {
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( SagaCompletedChannel ), sagaId );
        } catch (Exception ex) {
            LogSagaCompletionCheckFailed( _logger, ex, sagaId );
        }
    }

    /// <summary>
    /// Publishes the per-lookup completion on the <c>complete:{lookupKey}</c> channel, carrying the
    /// saga's partial-or-final result URI (or an empty string), to wake orchestrator waiters. No-ops
    /// (with a diagnostic log) when the saga is missing. Failures are logged and swallowed.
    /// </summary>
    /// <param name="sagaId">The saga whose lookup completion is announced.</param>
    /// <param name="expectedInstanceToken">The saga instance fence.</param>
    /// <param name="ct">Cancels the saga read.</param>
    /// <returns>A task that completes once the publish (or no-op) finishes.</returns>
    private async Task PublishLookupCompletionAsync( string sagaId, string expectedInstanceToken, CancellationToken ct ) {
        try {
            // Get the saga to retrieve the lookup key for the completion channel
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null || saga.InstanceToken != expectedInstanceToken) {
                LogSagaNotFoundForLookupCompletion( _logger, sagaId );
                return;
            }

            // Publish current saga result URI (may be empty for in-progress lookups)
            // Empty publishes trigger the SagaCoordinator's pattern subscription
            // while WaitForCompletionAsync skips them and waits for a real result
            string resultUri = saga.PartialResultUri ?? saga.FinalResultUri ?? string.Empty;
            string channel = $"{LookupCompleteChannelPrefix}{saga.LookupKey}";
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( channel ), resultUri );

            LogLookupCompletionPublished( _logger, sagaId, channel );
        } catch (Exception ex) {
            LogLookupCompletionPublishFailed( _logger, ex, sagaId );
        }
    }

    /// <summary>
    /// Publishes the rate-limited sentinel on the <c>complete:{lookupKey}</c> channel, signaling
    /// waiters that pending providers are rate-limited and a partial result should be returned now.
    /// No-ops (with a diagnostic log) when the saga is missing. Failures are logged and swallowed.
    /// </summary>
    /// <param name="sagaId">The saga whose pending providers are rate-limited.</param>
    /// <param name="expectedInstanceToken">The saga instance fence.</param>
    /// <param name="ct">Cancels the saga read.</param>
    /// <returns>A task that completes once the publish (or no-op) finishes.</returns>
    private async Task PublishRateLimitSentinelAsync( string sagaId, string expectedInstanceToken, CancellationToken ct ) {
        try {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null || saga.InstanceToken != expectedInstanceToken) {
                LogSagaNotFoundForLookupCompletion( _logger, sagaId );
                return;
            }

            // Publish rate-limited sentinel so waiting clients know this is a partial result
            string channel = $"{LookupCompleteChannelPrefix}{saga.LookupKey}";
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync( RedisChannel.Literal( channel ), LookupConstants.RateLimitedSentinel );

            LogLookupCompletionPublished( _logger, sagaId, channel );
        } catch (Exception ex) {
            LogLookupCompletionPublishFailed( _logger, ex, sagaId );
        }
    }

    #region LoggerMessage Definitions

    /// <summary>Logs (Information) that the queue processor is starting for a provider.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider this processor serves.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.QueueProcessorStarting,
        Level = LogLevel.Information,
        Message = "Queue processor starting for provider {Provider}" )]
    private static partial void LogQueueProcessorStarting( ILogger logger, SupportedProviders provider );

    /// <summary>Logs (Information) that the queue processor is stopping for a provider.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider this processor serves.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.QueueProcessorStopping,
        Level = LogLevel.Information,
        Message = "Queue processor stopping for provider {Provider}" )]
    private static partial void LogQueueProcessorStopping( ILogger logger, SupportedProviders provider );

    /// <summary>Logs (Error) that the consume loop threw; the loop backs off and continues.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception raised in the loop.</param>
    /// <param name="provider">The provider this processor serves.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.QueueProcessorLoopError,
        Level = LogLevel.Error,
        Message = "Error in queue processing loop for {Provider}" )]
    private static partial void LogQueueProcessorLoopError( ILogger logger, Exception ex, SupportedProviders provider );

    /// <summary>Logs (Debug) that a message is being processed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The queue message id.</param>
    /// <param name="sagaId">The saga the message belongs to.</param>
    /// <param name="lookupType">The lookup type being processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.ProcessingMessage,
        Level = LogLevel.Debug,
        Message = "Processing message {MessageId} for saga {SagaId}, lookup type {LookupType}" )]
    private static partial void LogProcessingMessage( ILogger logger, string messageId, string sagaId, LookupRequestType lookupType );

    /// <summary>Logs an error when the authoritative saga identity differs from the queued request.</summary>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaIdentityMismatch,
        Level = LogLevel.Error,
        Message = "Saga identity mismatch for message {MessageId}, saga {SagaId}: request {RequestLookupType}/{RequestLookupValue}/{RequestLookupKey}; authoritative {AuthoritativeLookupType}/{AuthoritativeLookupValue}/{AuthoritativeLookupKey}" )]
    private static partial void LogSagaIdentityMismatch(
        ILogger logger,
        string sagaId,
        string messageId,
        string requestLookupKey,
        string authoritativeLookupKey,
        LookupRequestType requestLookupType,
        LookupRequestType authoritativeLookupType,
        string requestLookupValue,
        string authoritativeLookupValue );

    [LoggerMessage( EventId = LogEventIds.Services.Queue.SagaIdentityQuarantineFailed, Level = LogLevel.Error,
        Message = "Failed to quarantine identity-mismatched message {MessageId} for saga {SagaId}" )]
    private static partial void LogIdentityQuarantineFailed( ILogger logger, Exception ex, string messageId, string sagaId );

    /// <summary>Logs (Debug) that an endpoint is rate-limited and the message is being requeued.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="endpoint">The rate-limited endpoint.</param>
    /// <param name="retryAfter">The instant after which the endpoint may be retried.</param>
    /// <param name="messageId">The queue message id being requeued.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.EndpointRateLimited,
        Level = LogLevel.Debug,
        Message = "Endpoint {Endpoint} is rate-limited until {RetryAfter}, requeuing message {MessageId}" )]
    private static partial void LogEndpointRateLimited( ILogger logger, string endpoint, DateTimeOffset retryAfter, string messageId );

    /// <summary>Logs (Debug) that a message was processed successfully.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The queue message id.</param>
    /// <param name="sagaId">The saga the message belonged to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.MessageProcessed,
        Level = LogLevel.Debug,
        Message = "Successfully processed message {MessageId} for saga {SagaId}" )]
    private static partial void LogMessageProcessed( ILogger logger, string messageId, string sagaId );

    /// <summary>Logs (Warning) that the provider returned a rate-limit response for an endpoint.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The rate-limited provider.</param>
    /// <param name="endpoint">The rate-limited endpoint.</param>
    /// <param name="retryAfter">The retry-after window reported by the provider.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.RateLimitEncountered,
        Level = LogLevel.Warning,
        Message = "Rate limit encountered for {Provider} endpoint {Endpoint}, retry after {RetryAfter}" )]
    private static partial void LogRateLimitEncountered( ILogger logger, SupportedProviders provider, string endpoint, TimeSpan retryAfter );

    /// <summary>Logs (Information) that a saga was marked partial and the request requeued after a rate limit.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga marked partial.</param>
    /// <param name="endpoint">The rate-limited endpoint.</param>
    /// <param name="provider">The provider the request was requeued for.</param>
    /// <param name="retryAfter">The instant until which the request is deferred.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaMarkedPartial,
        Level = LogLevel.Information,
        Message = "Saga {SagaId} marked as partial due to rate limit on {Endpoint}, requeued for {Provider} (will be deferred until {RetryAfter})" )]
    private static partial void LogSagaMarkedPartial( ILogger logger, string sagaId, string endpoint, SupportedProviders provider, DateTimeOffset retryAfter );

    /// <summary>Logs (Warning) that an interactive lookup was deferred to the background lane after a rate limit.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose interactive request was deferred.</param>
    /// <param name="lookupType">The lookup type that was deferred.</param>
    /// <param name="endpoint">The rate-limited endpoint.</param>
    /// <param name="retryAfter">The instant after which the request may be retried.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.InteractiveDeferredToBackground,
        Level = LogLevel.Warning,
        Message = "Interactive lookup {SagaId}/{LookupType} deferred to the background lane due to a rate limit on {Endpoint}, retry after {RetryAfter}" )]
    private static partial void LogInteractiveDeferredToBackground(
        ILogger logger,
        string sagaId,
        LookupRequestType lookupType,
        string endpoint,
        DateTimeOffset retryAfter );

    /// <summary>Logs (Error) that a lookup request failed to process.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that caused the failure.</param>
    /// <param name="requestId">The failing request id.</param>
    /// <param name="sagaId">The saga the request belonged to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.ProcessingFailed,
        Level = LogLevel.Error,
        Message = "Failed to process lookup request {RequestId} for saga {SagaId}" )]
    private static partial void LogProcessingFailed( ILogger logger, Exception ex, string requestId, string sagaId );

    /// <summary>Logs (Warning) that a message exceeded its retry budget and is being dead-lettered.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="messageId">The queue message id moving to the DLQ.</param>
    /// <param name="maxRetries">The maximum retry attempts that were exceeded.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.MaxRetriesExceeded,
        Level = LogLevel.Warning,
        Message = "Message {MessageId} exceeded max retry attempts ({MaxRetries}), moving to DLQ" )]
    private static partial void LogMaxRetriesExceeded( ILogger logger, string messageId, int maxRetries );

    /// <summary>Logs (Debug) that a saga could not be found when checking completion.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The missing saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaNotFoundForCompletion,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} not found, cannot check completion" )]
    private static partial void LogSagaNotFoundForCompletion( ILogger logger, string sagaId );

    /// <summary>Logs (Debug) that a saga is not yet complete, so no completion event is published.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaNotYetComplete,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} is not yet complete" )]
    private static partial void LogSagaNotYetComplete( ILogger logger, string sagaId );

    /// <summary>Logs (Debug) that a saga already has a final result, so completion is not re-published.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaAlreadyHasFinalResult,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} already has final result" )]
    private static partial void LogSagaAlreadyHasFinalResult( ILogger logger, string sagaId );

    /// <summary>Logs (Information) that a saga is complete and its completion event is being published.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The completed saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaCompletePublishing,
        Level = LogLevel.Information,
        Message = "Saga {SagaId} is complete, publishing completion event" )]
    private static partial void LogSagaCompletePublishing( ILogger logger, string sagaId );

    /// <summary>Logs (Error) that checking or publishing saga completion failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception raised.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaCompletionCheckFailed,
        Level = LogLevel.Error,
        Message = "Failed to check/publish saga completion for {SagaId}" )]
    private static partial void LogSagaCompletionCheckFailed( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs (Warning) that a saga could not be found when publishing lookup completion.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The missing saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.SagaNotFoundForLookupCompletion,
        Level = LogLevel.Warning,
        Message = "Saga {SagaId} not found, cannot publish lookup completion" )]
    private static partial void LogSagaNotFoundForLookupCompletion( ILogger logger, string sagaId );

    /// <summary>Logs (Debug) that a lookup completion was published on a channel.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose completion was published.</param>
    /// <param name="channel">The channel the completion was published on.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.LookupCompletionPublished,
        Level = LogLevel.Debug,
        Message = "Published lookup completion for saga {SagaId} on channel {Channel}" )]
    private static partial void LogLookupCompletionPublished( ILogger logger, string sagaId, string channel );

    /// <summary>Logs (Error) that publishing a lookup completion failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception raised.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.Queue.LookupCompletionPublishFailed,
        Level = LogLevel.Error,
        Message = "Failed to publish lookup completion for {SagaId}" )]
    private static partial void LogLookupCompletionPublishFailed( ILogger logger, Exception ex, string sagaId );

    #endregion LoggerMessage Definitions
}
