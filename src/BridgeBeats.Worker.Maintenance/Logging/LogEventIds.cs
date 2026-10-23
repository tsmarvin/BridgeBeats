namespace BridgeBeats.Worker.Maintenance.Logging;

/// <summary>
/// Stable numeric event identifiers for the Maintenance worker's structured log messages (5500-5749).
/// Extends <see cref="Core.Infrastructure.Logging.LogEventIds"/> with Maintenance worker-specific EventIds.
/// Each constant is the <c>EventId</c> assigned to a corresponding
/// <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> method. The
/// <see cref="CacheBootstrapBackgroundService"/> messages occupy 5500–5519, the
/// <see cref="StaleCacheRefreshBackgroundService"/> messages occupy 5520–5549, and the startup
/// messages occupy 5550–5574; values are part of the logging contract and must not be changed once shipped.
/// </summary>
public static class LogEventIds {
    #region CacheBootstrapBackgroundService (5500-5519)

    /// <summary>A periodic (non-startup) bootstrap run failed and will retry at the next interval.</summary>
    public const int BootstrapPeriodicError = 5500;

    /// <summary>The bootstrap service is shutting down.</summary>
    public const int BootstrapShuttingDown = 5501;

    /// <summary>The Redis key count measured before a bootstrap run began.</summary>
    public const int RedisKeyCountBefore = 5502;

    /// <summary>Measuring the Redis key count before a run failed.</summary>
    public const int RedisKeyCountBeforeError = 5503;

    /// <summary>A bootstrap run is starting against the configured PDS and DID.</summary>
    public const int BootstrapStarting = 5504;

    /// <summary>Periodic progress update reporting how many records have been processed.</summary>
    public const int BootstrapProgress = 5505;

    /// <summary>Caching a single PDS record failed; the run continues with the next record.</summary>
    public const int CacheRecordError = 5506;

    /// <summary>The bootstrap run was cancelled by host shutdown.</summary>
    public const int BootstrapCancelled = 5507;

    /// <summary>A fatal error aborted the bootstrap run.</summary>
    public const int BootstrapFatalError = 5508;

    /// <summary>The Redis key count measured after a bootstrap run completed.</summary>
    public const int RedisKeyCountAfter = 5509;

    /// <summary>Measuring the Redis key count after a run failed.</summary>
    public const int RedisKeyCountAfterError = 5510;

    /// <summary>A bootstrap run completed, with timing, success/error counts, and key-count delta.</summary>
    public const int BootstrapCompleted = 5511;

    /// <summary>Writing the bootstrap status document to Redis failed.</summary>
    public const int StatusUpdateError = 5512;

    /// <summary>Reading the bootstrap status document from Redis failed.</summary>
    public const int StatusReadError = 5513;

    /// <summary>The startup (first, immediate) bootstrap run failed; the periodic loop still starts.</summary>
    public const int BootstrapStartupError = 5514;

    #endregion

    #region StaleCacheRefreshBackgroundService (5520-5549)

    /// <summary>The stale-cache refresh service is shutting down.</summary>
    public const int RefreshShuttingDown = 5520;

    // 5521 retired (§8.1) — do not reuse
    // 5522 retired (§8.1) — do not reuse

    /// <summary>A refresh pass is starting; reports the number of stale records selected.</summary>
    public const int RefreshStarting = 5523;

    /// <summary>Enqueuing one stale record for re-lookup failed; the sweep continues with remaining records.</summary>
    public const int RefreshEnqueueError = 5524;

    /// <summary>A refresh pass completed, with counts of enqueued, skipped, and errored records.</summary>
    public const int RefreshCompleted = 5525;

    /// <summary>A record was skipped because it carries no usable lookup identifier.</summary>
    public const int RefreshRecordSkipped = 5526;

    /// <summary>The refresh pass was cancelled by host shutdown.</summary>
    public const int RefreshCancelled = 5527;

    // 5528 retired (§8.1) — do not reuse

    /// <summary>The refresh sweep has no enabled providers to enqueue to; the pass is skipped.</summary>
    public const int RefreshNoEnabledProviders = 5529;

    // 5530 retired (§8.1) — do not reuse

    /// <summary>Writing the durable last-run marker to Redis failed; the pass proceeds.</summary>
    public const int RefreshMarkerWriteError = 5531;

    /// <summary>Reading the durable last-run marker from Redis failed; treating as due-now and running.</summary>
    public const int RefreshMarkerReadError = 5532;

    /// <summary>Enqueuing one provider leg of a refresh record failed; the leg is marked complete-as-failed in the saga.</summary>
    public const int RefreshLegEnqueueFailed = 5533;

    /// <summary>A stale-cache refresh pass failed; the service will retry after the configured retry interval.</summary>
    public const int RefreshPassFailedRetrying = 5534;

    #endregion

    #region Statistics fold (5575-5599)

    /// <summary>A statistics computation pass is starting.</summary>
    public const int PassStarting = 5575;

    /// <summary>A statistics computation pass completed successfully.</summary>
    public const int PassCompleted = 5576;

    /// <summary>Writing the statistics status document to Redis failed.</summary>
    public const int StatisticsStatusUpdateError = 5577;

    /// <summary>A statistics computation pass failed; will retry after the configured interval.</summary>
    public const int PassError = 5578;

    /// <summary>Both the initial statistics pass and the retry failed; falling back to the periodic window.</summary>
    public const int RetryExhausted = 5579;

    /// <summary>A manual statistics refresh was requested via Pub/Sub.</summary>
    public const int RefreshRequested = 5580;

    /// <summary>A manual statistics refresh trigger was coalesced because a pass is already running.</summary>
    public const int RefreshCoalesced = 5581;

    /// <summary>An unhandled exception was thrown in the statistics refresh Pub/Sub handler.</summary>
    public const int RefreshHandlerError = 5582;

    /// <summary>Reading the statistics status document from Redis failed.</summary>
    public const int StatisticsStatusReadError = 5583;

    #endregion

    #region Program Startup (5550-5574)

    /// <summary>Startup diagnostic describing the Redis connection configuration and state.</summary>
    public const int RedisConnectionInfo = 5550;

    /// <summary>Result of the startup Redis write/read-back smoke test.</summary>
    public const int RedisWriteTest = 5551;

    /// <summary>The startup Redis write/read-back smoke test failed to verify.</summary>
    public const int RedisWriteVerificationFailed = 5552;

    /// <summary>Startup diagnostic reporting the Redis key count for an endpoint.</summary>
    public const int RedisKeyCount = 5553;

    #endregion
}
