namespace BridgeBeats.Contracts.DTOs;

/// <summary>
/// Point-in-time status of the statistics computation, published to Redis under
/// <see cref="RedisKey"/> and read by Web to surface the statistics page state.
/// Composes <see cref="DTOs.LookupStatistics"/> for the snapshot projection and adds run-lifecycle
/// fields mirroring <see cref="CacheBootstrapStatus"/>.
/// </summary>
public sealed class StatisticsStatus {

    /// <summary>
    /// The well-known Redis key under which the current status is published. Consumers read this
    /// exact key to retrieve the latest <see cref="StatisticsStatus"/>.
    /// </summary>
    public const string RedisKey = "status:statistics";

    /// <summary>
    /// The most recently successfully computed statistics snapshot, or <see langword="null"/>
    /// before the first completed run.
    /// </summary>
    public LookupStatistics? Snapshot { get; init; }

    /// <summary>
    /// <see langword="true"/> while a statistics computation is currently in progress.
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>
    /// When the last successful computation completed, or <see langword="null"/> if no run has
    /// completed successfully yet.
    /// </summary>
    public DateTimeOffset? LastRunTime { get; init; }

    /// <summary>
    /// When the next scheduled statistics run is due, or <see langword="null"/> if not scheduled.
    /// </summary>
    public DateTimeOffset? NextScheduledRun { get; init; }

    /// <summary>
    /// Short, sanitized reason for the last failure, for operator display. <see langword="null"/>
    /// when the last run was healthy or no run has been attempted.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// When the last run failed, or <see langword="null"/> when no failure has been recorded.
    /// </summary>
    public DateTimeOffset? LastErrorTime { get; init; }
}
