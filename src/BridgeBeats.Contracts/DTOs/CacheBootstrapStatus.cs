namespace BridgeBeats.Contracts.DTOs;

/// <summary>
/// Point-in-time status of the Maintenance worker, which warms the cache on a schedule.
/// Published to Redis under <see cref="RedisKey"/> and read back to surface bootstrap state
/// alongside lookup statistics.
/// </summary>
public sealed class CacheBootstrapStatus {

    /// <summary>
    /// The well-known Redis key under which the current status is published. Consumers read this
    /// exact key to retrieve the latest <see cref="CacheBootstrapStatus"/>.
    /// </summary>
    public const string RedisKey = "status:cache-bootstrap";

    /// <summary>
    /// When the worker last ran, or <see langword="null"/> if it has not run yet.
    /// </summary>
    public DateTimeOffset? LastRunTime { get; init; }

    /// <summary>
    /// When the worker is next scheduled to run, or <see langword="null"/> if no run is scheduled.
    /// </summary>
    public DateTimeOffset? NextScheduledRun { get; init; }

    /// <summary>
    /// <see langword="true"/> while a bootstrap run is currently in progress.
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// Number of items processed successfully in the last run, or <see langword="null"/> if the
    /// worker has not completed a run.
    /// </summary>
    public int? LastSuccessCount { get; init; }

    /// <summary>
    /// Number of items that errored in the last run, or <see langword="null"/> if the worker has
    /// not completed a run.
    /// </summary>
    public int? LastErrorCount { get; init; }

    /// <summary>
    /// Duration of the last run in seconds, or <see langword="null"/> if the worker has not
    /// completed a run.
    /// </summary>
    public double? LastDurationSeconds { get; init; }

    /// <summary>
    /// Number of Redis keys present after the last run, or <see langword="null"/> if not recorded.
    /// </summary>
    public long? RedisKeyCount { get; init; }
}
