using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Serves aggregate lookup statistics by reading the worker-published <c>status:statistics</c>
/// Redis document, plus the live cache-bootstrap worker status that can be merged into the stats.
/// Refresh requests are dispatched to the Maintenance worker over Redis Pub/Sub.
/// </summary>
public interface IStatisticsService {

    /// <summary>
    /// Returns the full worker status document, including the snapshot, run-lifecycle flags, and
    /// the last-failure fields, served from the same in-process memo as
    /// <see cref="GetCachedStatistics"/>. Lets a caller select the page state (data, generating, or
    /// error) and surface <see cref="StatisticsStatus.LastError"/> /
    /// <see cref="StatisticsStatus.NextScheduledRun"/> without a second Redis round-trip.
    /// </summary>
    /// <returns>
    /// The current <see cref="StatisticsStatus"/>, or <see langword="null"/> when the worker has
    /// not yet published a status document (before the first run).
    /// </returns>
    StatisticsStatus? GetStatus( );

    /// <summary>
    /// Returns the most recently cached statistics snapshot without computing or waiting. The
    /// value reflects the last snapshot written by the Maintenance worker. Lets a caller
    /// distinguish "no data yet" from "data available". A projection over
    /// <see cref="GetStatus"/> (the snapshot field of the same status document).
    /// </summary>
    /// <returns>
    /// The cached <see cref="LookupStatistics"/>, or <see langword="null"/> when the worker has
    /// not yet completed a run.
    /// </returns>
    LookupStatistics? GetCachedStatistics( );

    /// <summary>
    /// Whether the Maintenance worker is currently running a statistics computation, as
    /// reported by the <c>IsRunning</c> field of the <c>status:statistics</c> Redis document. A
    /// projection over <see cref="GetStatus"/> (the running flag of the same status document).
    /// </summary>
    bool IsRefreshing { get; }

    /// <summary>
    /// Publishes a manual-refresh request to the Maintenance worker over Redis Pub/Sub.
    /// Returns immediately (fire-and-forget). Admin-gated and rate-limited by the caller.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the publish succeeded; <see langword="false"/> when the
    /// publish could not be delivered (for example, a Redis failure).
    /// </returns>
    bool RequestRefresh( );

    /// <summary>
    /// Reads the current cache-bootstrap worker status directly from Redis, bypassing the
    /// statistics cache. The status can be merged into the statistics projection.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the live <see cref="CacheBootstrapStatus"/>, or <see langword="null"/>
    /// when the status is absent or cannot be read.
    /// </returns>
    Task<CacheBootstrapStatus?> GetLiveBootstrapStatusAsync( CancellationToken cancellationToken = default );
}
