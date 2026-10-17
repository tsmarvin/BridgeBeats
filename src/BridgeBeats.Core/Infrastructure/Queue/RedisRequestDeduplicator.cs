using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Utilities;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Redis-backed single-flight deduplicator that collapses concurrent identical lookups and lets
/// callers wait for an in-flight lookup to complete. Implements <see cref="IRequestDeduplicator"/>
/// using SETNX for locking and Pub/Sub for completion notification.
/// </summary>
/// <param name="redis">Redis connection used for the in-flight lock and completion channel.</param>
/// <param name="logger">Logger for deduplication diagnostics.</param>
/// <remarks>
/// The first caller for a request key acquires an in-flight lock (<c>inflight:{requestKey}</c>,
/// set with NX and a timeout, holding a unique lease token); concurrent callers see the lock and
/// are told the request is already in flight. A caller-owned release compares that token before
/// deleting the lock and publishing the result URI on the <c>complete:{requestKey}</c>
/// Pub/Sub channel. Waiters subscribe to that channel, with a pre-subscription "already done?"
/// check that closes the lost-wakeup race. This is the mechanism that turns the asynchronous
/// saga pipeline into a synchronous response for interactive callers.
/// </remarks>
public sealed partial class RedisRequestDeduplicator(
    IConnectionMultiplexer redis,
    ILogger<RedisRequestDeduplicator> logger
) : IRequestDeduplicator {

    /// <summary>Redis connection used for lock and Pub/Sub operations.</summary>
    private readonly IConnectionMultiplexer _redis = redis
                                                   ?? throw new ArgumentNullException( nameof( redis ) );

    /// <summary>Logger for deduplication diagnostics.</summary>
    private readonly ILogger<RedisRequestDeduplicator> _logger = logger
                                                               ?? throw new ArgumentNullException( nameof( logger ) );

    private const string ReleaseOwnedScript = """
        if redis.call('get', KEYS[1]) ~= ARGV[1] then return 0 end
        redis.call('del', KEYS[1])
        redis.call('publish', ARGV[3], ARGV[2])
        return 1
        """;

    /// <summary>Key prefix for in-flight locks. Literal value: <c>"inflight:"</c>.</summary>
    private const string InFlightPrefix = "inflight:";

    /// <summary>
    /// Attempts to become the single in-flight processor for a request key.
    /// </summary>
    /// <param name="requestKey">The canonical request key to claim.</param>
    /// <param name="timeout">Lock lifetime; the lock self-expires after this window.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>
    /// A <see cref="DeduplicationResult"/> with <c>Acquired</c> true for the first caller, or
    /// <c>AlreadyInFlight</c> true for callers that found the lock already held (which also records
    /// a deduplication metric).
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="requestKey"/> is null or whitespace.</exception>
    public async Task<DeduplicationResult> TryAcquireAsync(
        string requestKey,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( requestKey );

        string lockKey = $"{InFlightPrefix}{requestKey}";
        IDatabase db = _redis.GetDatabase( );

        string leaseToken = Guid.NewGuid( ).ToString( "N" );
        // Try to acquire the lock using SETNX with a unique lease token and TTL.
        bool acquired = await db.StringSetAsync(
            lockKey,
            leaseToken,
            timeout,
            When.NotExists
        );

        if (acquired) {
            if (_logger.IsEnabled( LogLevel.Debug )) {
                string sanitizedRequestKey = requestKey.SanitizeForLogging( );
                LogLockAcquired( _logger, sanitizedRequestKey, leaseToken );
            }

            return new DeduplicationResult(
                Acquired: true,
                AlreadyInFlight: false,
                RequestKey: requestKey,
                LeaseToken: leaseToken
            );
        }

        // Lock is held by another instance - record deduplication metric
        QueueMetrics.RecordDeduplicated( );

        RedisValue holder = await db.StringGetAsync( lockKey );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string sanitizedRequestKey = requestKey.SanitizeForLogging( );
            string sanitizedHolder = holder.ToString( ).SanitizeForLogging( );
            LogAlreadyInFlight( _logger, sanitizedRequestKey, sanitizedHolder );
        }

        return new DeduplicationResult(
            Acquired: false,
            AlreadyInFlight: true,
            RequestKey: requestKey,
            LeaseToken: null
        );
    }

    /// <summary>
    /// Publishes completion for a request key without mutating its caller-owned lease.
    /// </summary>
    /// <param name="requestKey">The request key to release.</param>
    /// <param name="resultUri">The result URI to publish, or null to notify waiters that no result is available.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>A task that completes once completion is published.</returns>
    /// <remarks>
    /// Coordinators do not possess the acquisition token, so this publisher-only operation never
    /// reads or deletes the lease. The acquiring caller must later use <see cref="ReleaseOwnedAsync"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="requestKey"/> is null or whitespace.</exception>
    public Task ReleaseAsync(
        string requestKey,
        string? resultUri,
        CancellationToken cancellationToken = default
    ) => ReleaseCoreAsync(
        requestKey,
        resultUri ?? LookupConstants.NoResultSentinel,
        hasResult: resultUri is not null,
        cancellationToken );

    /// <inheritdoc/>
    public Task<bool> ReleaseOwnedAsync(
        string requestKey,
        string leaseToken,
        string? resultUri,
        CancellationToken cancellationToken = default
    ) => ReleaseOwnedCoreAsync(
        requestKey,
        leaseToken,
        resultUri ?? LookupConstants.NoResultSentinel,
        hasResult: resultUri is not null,
        cancellationToken );

    /// <inheritdoc/>
    public Task<bool> ReleaseOwnedForStateRecheckAsync(
        string requestKey,
        string leaseToken,
        CancellationToken cancellationToken = default
    ) => ReleaseOwnedCoreAsync(
        requestKey,
        leaseToken,
        LookupConstants.StateChangedSentinel,
        hasResult: false,
        cancellationToken );

    private async Task<bool> ReleaseOwnedCoreAsync(
        string requestKey,
        string leaseToken,
        string message,
        bool hasResult,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( requestKey );
        ArgumentException.ThrowIfNullOrWhiteSpace( leaseToken );

        string lockKey = $"{InFlightPrefix}{requestKey}";
        string channelKey = $"{LookupConstants.CompletionChannelPrefix}{requestKey}";
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            ReleaseOwnedScript,
            [lockKey],
            [leaseToken, message, channelKey] );
        if ((int)result != 1) {
            LogReleaseDenied(
                _logger,
                requestKey.SanitizeForLogging( ),
                "newer-or-foreign-lease",
                leaseToken.SanitizeForLogging( ) );
            return false;
        }

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string sanitizedRequestKey = requestKey.SanitizeForLogging( );
            LogLockReleased( _logger, sanitizedRequestKey, hasResult );
        }
        return true;
    }

    /// <inheritdoc/>
    public Task ReleaseResultNotPersistedAsync(
        string requestKey,
        CancellationToken cancellationToken = default
    ) => ReleaseCoreAsync(
        requestKey,
        LookupConstants.ResultNotPersistedSentinel,
        hasResult: false,
        cancellationToken );

    private async Task ReleaseCoreAsync(
        string requestKey,
        string message,
        bool hasResult,
        CancellationToken cancellationToken
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( requestKey );

        string channelKey = $"{LookupConstants.CompletionChannelPrefix}{requestKey}";

        ISubscriber subscriber = _redis.GetSubscriber( );

        // The coordinator owns saga completion but cannot prove ownership of the Web process's
        // acquisition lease. Publish without reading or mutating that lease; its owner performs the
        // token-checked release after observing this completion.
        _ = await subscriber.PublishAsync( RedisChannel.Literal( channelKey ), message );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string sanitizedRequestKey = requestKey.SanitizeForLogging( );
            LogLockReleased( _logger, sanitizedRequestKey, hasResult );
        }
    }

    /// <summary>
    /// Waits for an in-flight request to complete and returns its published result URI.
    /// </summary>
    /// <param name="requestKey">The request key to wait on.</param>
    /// <param name="timeout">Maximum time to wait before giving up.</param>
    /// <param name="cancellationToken">Token that can cancel the wait early.</param>
    /// <returns>The result URI if one was published within the timeout; otherwise null.</returns>
    /// <remarks>
    /// Subscribes to <c>complete:{requestKey}</c> first, then checks whether the in-flight lock
    /// still exists. If the lock is already gone the request completed before the subscription was
    /// active, so it returns null immediately (the caller should re-check via another path). Empty
    /// completion messages are ignored; the subscription is always unsubscribed on exit.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="requestKey"/> is null or whitespace.</exception>
    public async Task<string?> WaitForCompletionAsync(
        string requestKey,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( requestKey );

        string channelKey = $"{LookupConstants.CompletionChannelPrefix}{requestKey}";
        ISubscriber subscriber = _redis.GetSubscriber( );

        RedisChannel channel = RedisChannel.Literal( channelKey );

        // Subscribe to completion channel using ChannelMessageQueue for async iteration
        ChannelMessageQueue messageQueue = await subscriber.SubscribeAsync( channel );

        try {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
            cts.CancelAfter( timeout );

            // Check if already completed (race condition: completed between TryAcquire and WaitForCompletion)
            IDatabase db = _redis.GetDatabase( );
            string lockKey = $"{InFlightPrefix}{requestKey}";
            bool stillInFlight = await db.KeyExistsAsync( lockKey );

            if (!stillInFlight) {
                // Already completed, the result may have been published before we subscribed
                // Return null to indicate the caller should check cache
                if (_logger.IsEnabled( LogLevel.Debug )) {
                    string sanitizedRequestKey = requestKey.SanitizeForLogging( );
                    LogCompletedBeforeSubscription( _logger, sanitizedRequestKey );
                }
                return null;
            }

            // Wait for a non-empty message or timeout
            // Workers may publish empty strings before results are assembled;
            // skip those and keep waiting for a real result URI or sentinel.
            try {
                await foreach (ChannelMessage message in messageQueue.WithCancellation( cts.Token )) {
                    string result = message.Message.ToString( );
                    if (result == LookupConstants.StateChangedSentinel) {
                        return result;
                    }
                    if (IsTerminalWithoutResult( result )) {
                        return null;
                    }
                    if (!string.IsNullOrEmpty( result )) {
                        return result;
                    }
                }
                return null;
            } catch (OperationCanceledException) {
                if (_logger.IsEnabled( LogLevel.Debug )) {
                    string sanitizedRequestKey = requestKey.SanitizeForLogging( );
                    LogWaitTimeout( _logger, sanitizedRequestKey );
                }
                return null;
            }
        } finally {
            // Always unsubscribe
            messageQueue.Unsubscribe( );
        }
    }

    /// <summary>
    /// Waits for the final result of a request, using a caller-supplied check to catch a result
    /// that landed before the subscription was active.
    /// </summary>
    /// <param name="requestKey">The request key to wait on.</param>
    /// <param name="timeout">Maximum time to wait before giving up.</param>
    /// <param name="missedResultCheck">
    /// Optional callback invoked right after subscribing; if it returns a non-empty result the
    /// method returns that immediately, closing the lost-wakeup race for final results.
    /// </param>
    /// <param name="cancellationToken">Token that can cancel the wait early.</param>
    /// <returns>The final result URI if available within the timeout; otherwise null.</returns>
    /// <remarks>
    /// Differs from <see cref="WaitForCompletionAsync(string, TimeSpan, CancellationToken)"/> in
    /// that the pre-subscription check is delegated to <paramref name="missedResultCheck"/> rather
    /// than a lock-existence probe, which lets callers query the durable final result (for example
    /// from the PDS) instead of relying on the transient in-flight lock. Here the absence of the
    /// in-flight lock is not used as a final-completion signal; pending sagas retain it until
    /// terminal state or bounded lease expiry.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="requestKey"/> is null or whitespace.</exception>
    public async Task<string?> WaitForFinalCompletionAsync(
        string requestKey,
        TimeSpan timeout,
        Func<Task<string?>>? missedResultCheck = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( requestKey );

        string channelKey = $"{LookupConstants.CompletionChannelPrefix}{requestKey}";
        ISubscriber subscriber = _redis.GetSubscriber( );

        RedisChannel channel = RedisChannel.Literal( channelKey );

        // Subscribe to completion channel using ChannelMessageQueue for async iteration
        ChannelMessageQueue messageQueue = await subscriber.SubscribeAsync( channel );

        try {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
            cts.CancelAfter( timeout );

            // The result may have been published in the gap between the caller reading state
            // and this subscription becoming active - re-check durable state now that we
            // are guaranteed to observe any future publish.
            if (missedResultCheck is not null) {
                string? missedResult = await missedResultCheck( );
                if (!string.IsNullOrEmpty( missedResult )) {
                    if (_logger.IsEnabled( LogLevel.Debug )) {
                        string sanitizedRequestKey = requestKey.SanitizeForLogging( );
                        LogFinalCompletedBeforeSubscription( _logger, sanitizedRequestKey );
                    }
                    return missedResult;
                }
            }

            // Wait for a non-empty message or timeout. Unlike WaitForCompletionAsync, the
            // in-flight lock is not a final-completion signal here; durable saga state is.
            try {
                await foreach (ChannelMessage message in messageQueue.WithCancellation( cts.Token )) {
                    string result = message.Message.ToString( );
                    if (result == LookupConstants.StateChangedSentinel) {
                        if (missedResultCheck is not null) {
                            string? changedResult = await missedResultCheck( );
                            if (!string.IsNullOrEmpty( changedResult )) {
                                return changedResult;
                            }
                        }
                        return result;
                    }
                    if (IsTerminalWithoutResult( result )) {
                        return null;
                    }
                    if (!string.IsNullOrEmpty( result )) {
                        return result;
                    }
                }
                return null;
            } catch (OperationCanceledException) {
                if (_logger.IsEnabled( LogLevel.Debug )) {
                    string sanitizedRequestKey = requestKey.SanitizeForLogging( );
                    LogFinalWaitTimeout( _logger, sanitizedRequestKey );
                }
                return null;
            }
        } finally {
            // Always unsubscribe
            messageQueue.Unsubscribe( );
        }
    }

    private static bool IsTerminalWithoutResult( string value ) =>
        value == LookupConstants.NoResultSentinel
        || value == LookupConstants.ResultNotPersistedSentinel;

    /// <summary>
    /// Builds the canonical request key from a lookup type and value so that duplicate lookups
    /// collapse to the same key.
    /// </summary>
    /// <param name="lookupType">The lookup type; lower-cased in the key.</param>
    /// <param name="lookupValue">The lookup value; trimmed and upper-cased in the key.</param>
    /// <returns>The canonical key <c>{type-lower}:{value-trim-upper}</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when either argument is null or whitespace.</exception>
    public static string GenerateRequestKey( string lookupType, string lookupValue ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( lookupType );
        ArgumentException.ThrowIfNullOrWhiteSpace( lookupValue );

        return $"{lookupType.ToLowerInvariant( )}:{lookupValue.Trim( ).ToUpperInvariant( )}";
    }

    #region LoggerMessage Methods

    /// <summary>Logs that this instance acquired the in-flight lock for a request.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    /// <param name="instanceId">The acquiring instance id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorLockAcquired,
        Level = LogLevel.Debug,
        Message = "Acquired processing lock for {RequestKey} (instance: {InstanceId})" )]
    internal static partial void LogLockAcquired( ILogger logger, string requestKey, string instanceId );

    /// <summary>Logs that a request was already in flight, held by another instance.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    /// <param name="holder">The sanitized id of the instance holding the lock.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorAlreadyInFlight,
        Level = LogLevel.Debug,
        Message = "Request {RequestKey} already in-flight (held by: {Holder})" )]
    internal static partial void LogAlreadyInFlight( ILogger logger, string requestKey, string holder );

    /// <summary>Logs that a release was denied because another instance owns the lock.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    /// <param name="holder">The sanitized id of the owning instance.</param>
    /// <param name="instanceId">This instance's id, which did not match the holder.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorReleaseDenied,
        Level = LogLevel.Warning,
        Message = "Attempted to release lock for {RequestKey} but held by {Holder}, not {InstanceId}" )]
    internal static partial void LogReleaseDenied( ILogger logger, string requestKey, string holder, string instanceId );

    /// <summary>Logs that completion was published, with owned callers also releasing their lock.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    /// <param name="hasResult">Whether a non-empty result URI was published.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorLockReleased,
        Level = LogLevel.Debug,
        Message = "Published completion for {RequestKey} (owned lease released when applicable; hasResult: {HasResult})" )]
    internal static partial void LogLockReleased( ILogger logger, string requestKey, bool hasResult );

    /// <summary>Logs that a request completed before the waiter's subscription became active.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorCompletedBeforeSubscription,
        Level = LogLevel.Debug,
        Message = "Request {RequestKey} completed before subscription was active" )]
    internal static partial void LogCompletedBeforeSubscription( ILogger logger, string requestKey );

    /// <summary>Logs that waiting for completion timed out.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorWaitTimeout,
        Level = LogLevel.Debug,
        Message = "Timeout waiting for completion of {RequestKey}" )]
    internal static partial void LogWaitTimeout( ILogger logger, string requestKey );

    /// <summary>Logs that the final result was already available before the subscription became active.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorFinalCompletedBeforeSubscription,
        Level = LogLevel.Debug,
        Message = "Final result for {RequestKey} was already available before subscription was active" )]
    internal static partial void LogFinalCompletedBeforeSubscription( ILogger logger, string requestKey );

    /// <summary>Logs that waiting for the final completion timed out.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="requestKey">The sanitized request key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRequestDeduplicatorFinalWaitTimeout,
        Level = LogLevel.Debug,
        Message = "Timeout waiting for final completion of {RequestKey}" )]
    internal static partial void LogFinalWaitTimeout( ILogger logger, string requestKey );

    #endregion
}
