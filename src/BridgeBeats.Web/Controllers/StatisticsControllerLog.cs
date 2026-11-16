namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Source-generated <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> logging methods
/// for <see cref="StatisticsController"/>.
/// </summary>
public partial class StatisticsController {
    /// <summary>
    /// Logs that the statistics service is not configured.
    /// </summary>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.StatisticsControllerNotAvailable,
        Level = LogLevel.Warning,
        Message = "Statistics service not available" )]
    private partial void LogNotAvailable( );

    /// <summary>
    /// Logs an error while retrieving statistics.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.StatisticsControllerRetrieveError,
        Level = LogLevel.Error,
        Message = "Error retrieving statistics" )]
    private partial void LogRetrieveError( Exception ex );

    /// <summary>
    /// Logs an error while triggering a statistics refresh.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.StatisticsControllerRefreshError,
        Level = LogLevel.Error,
        Message = "Error refreshing statistics" )]
    private partial void LogRefreshError( Exception ex );

    /// <summary>
    /// Logs that a manual refresh request was suppressed by the per-admin throttle.
    /// </summary>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.StatisticsControllerRefreshThrottled,
        Level = LogLevel.Information,
        Message = "Statistics refresh request suppressed by per-admin throttle" )]
    private partial void LogRefreshThrottled( );
}
