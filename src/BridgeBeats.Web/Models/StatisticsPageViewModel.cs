namespace BridgeBeats.Web.Models;

/// <summary>
/// View model for the no-snapshot statistics page (the <c>Generating</c> view), carrying the
/// run-lifecycle state needed to select between the generating and error presentations and to
/// surface the next-run cadence. Populated by <c>StatisticsController.Index</c> from a single read
/// of the worker status document.
/// </summary>
public sealed class StatisticsPageViewModel {

    /// <summary>
    /// <see langword="true"/> when a statistics computation is genuinely in progress (the worker's
    /// real running flag). Drives the in-progress indicator; when <see langword="false"/> the page
    /// shows the honest "queued, not yet running" message without a spinner.
    /// </summary>
    public bool IsRefreshing { get; init; }

    /// <summary>
    /// The short, sanitized reason for the last failed run, or <see langword="null"/> when the last
    /// run was healthy. When set with no snapshot, the view renders the error presentation instead
    /// of the generating message.
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>
    /// When the last run failed, or <see langword="null"/> when no failure has been recorded.
    /// </summary>
    public DateTimeOffset? LastErrorTime { get; init; }

    /// <summary>
    /// When the next scheduled statistics run is due, or <see langword="null"/> when not scheduled.
    /// Surfaced as the fallback-cadence line on the generating view.
    /// </summary>
    public DateTimeOffset? NextScheduledRun { get; init; }
}
