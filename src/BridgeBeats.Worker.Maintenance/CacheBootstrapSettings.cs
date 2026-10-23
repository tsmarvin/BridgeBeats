namespace BridgeBeats.Worker.Maintenance;

/// <summary>
/// Tuning settings for <see cref="CacheBootstrapBackgroundService"/> and
/// <see cref="StaleCacheRefreshBackgroundService"/>: which ATProto PDS and user to read from,
/// how often to re-run the cache-rebuild pass, how many days to retain cached entries, how often
/// to run the stale-cache refresh sweep, how many stale records each sweep enqueues, and how long
/// to wait before retrying after a failed refresh pass.
/// </summary>
/// <param name="PdsUri">The base URI of the ATProto personal data server whose records are streamed.</param>
/// <param name="UserDid">The decentralized identifier of the user whose records are rebuilt into the cache.</param>
/// <param name="BootstrapInterval">The interval between bootstrap runs after the initial startup run.</param>
/// <param name="CacheDays">The number of days a cached result is considered fresh.</param>
/// <param name="RefreshInterval">The interval between stale-cache refresh sweeps.</param>
/// <param name="MaxRecordsPerRun">The maximum number of stale records enqueued per refresh sweep.</param>
/// <param name="RefreshRetryInterval">The interval to wait before retrying after a failed stale-cache refresh pass.</param>
public sealed record CacheBootstrapSettings(
    Uri PdsUri,
    string UserDid,
    TimeSpan BootstrapInterval,
    int CacheDays,
    TimeSpan RefreshInterval,
    int MaxRecordsPerRun,
    TimeSpan RefreshRetryInterval
);
