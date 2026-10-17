namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Source-generated <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> logging methods
/// for <see cref="HomeController"/>.
/// </summary>
public partial class HomeController {
    /// <summary>
    /// Logs that resolving the ATProto URI from the cache failed with an invalid-operation error and is being skipped.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.HomeControllerCacheInvalidOp,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve ATProto URI from cache due to invalid operation, continuing without it" )]
    private partial void LogCacheInvalidOp( Exception ex );

    /// <summary>
    /// Logs that resolving the ATProto URI from the cache failed with an argument error and is being skipped.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.HomeControllerCacheArgError,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve ATProto URI from cache due to argument error, continuing without it" )]
    private partial void LogCacheArgError( Exception ex );

    /// <summary>
    /// Logs that resolving the ATProto URI from the cache failed and is being skipped.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.HomeControllerCacheError,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve ATProto URI from cache, continuing without it" )]
    private partial void LogCacheError( Exception ex );

    /// <summary>
    /// Logs an error processing an individual result during a lookup stream.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="uri">The (sanitized) URI being processed.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.HomeControllerStreamResultError,
        Level = LogLevel.Error,
        Message = "Error processing individual result for URI: {Uri}" )]
    private partial void LogStreamResultError( Exception ex, string uri );

    /// <summary>
    /// Logs an error that aborted a lookup stream.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="uri">The (sanitized) URI being processed.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.HomeControllerStreamError,
        Level = LogLevel.Error,
        Message = "Error during lookup stream for URI: {Uri}" )]
    private partial void LogStreamError( Exception ex, string uri );

    /// <summary>Logs an expected client disconnect while streaming lookup results.</summary>
    /// <param name="uri">The sanitized URI being processed.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.HomeControllerStreamClientDisconnected,
        Level = LogLevel.Debug,
        Message = "Client disconnected during lookup stream for URI: {Uri}" )]
    private partial void LogStreamClientDisconnected( string uri );
}
