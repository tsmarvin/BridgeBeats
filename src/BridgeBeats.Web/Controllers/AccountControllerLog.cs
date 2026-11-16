namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Source-generated <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> logging methods
/// for <see cref="AccountController"/>.
/// </summary>
public partial class AccountController {
    /// <summary>
    /// Logs that a user registered successfully.
    /// </summary>
    /// <param name="userId">The id of the newly registered user.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerRegistered,
        Level = LogLevel.Information,
        Message = "User registered successfully with ID: {UserId}" )]
    private partial void LogUserRegistered( string userId );

    /// <summary>
    /// Logs that a user signed in successfully.
    /// </summary>
    /// <param name="userId">The id of the user who signed in.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerLoggedIn,
        Level = LogLevel.Information,
        Message = "User logged in successfully with ID: {UserId}" )]
    private partial void LogUserLoggedIn( string userId );

    /// <summary>
    /// Logs that a user regenerated their API key.
    /// </summary>
    /// <param name="userId">The id of the user whose API key was regenerated.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerApiKeyRegenerated,
        Level = LogLevel.Information,
        Message = "User regenerated API key with ID: {UserId}" )]
    private partial void LogApiKeyRegenerated( string userId );

    /// <summary>
    /// Logs that a user exported their personal data.
    /// </summary>
    /// <param name="userId">The id of the user who downloaded their data.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerDataDownloaded,
        Level = LogLevel.Information,
        Message = "User downloaded personal data with ID: {UserId}" )]
    private partial void LogDataDownloaded( string userId );

    /// <summary>
    /// Logs that an account deletion attempt failed.
    /// </summary>
    /// <param name="userId">The id of the user whose deletion failed.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerDeleteFailed,
        Level = LogLevel.Error,
        Message = "Failed to delete account for user ID: {UserId}" )]
    private partial void LogDeleteFailed( string userId );

    /// <summary>
    /// Logs that a user account was deleted.
    /// </summary>
    /// <param name="userId">The id of the deleted user.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerDeleted,
        Level = LogLevel.Information,
        Message = "User account deleted with ID: {UserId}" )]
    private partial void LogAccountDeleted( string userId );

    /// <summary>
    /// Logs that starting ATProto OAuth authorization failed for a handle.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    /// <param name="handle">The Bluesky handle that failed to start authorization.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerAtProtoOAuthStartFailed,
        Level = LogLevel.Error,
        Message = "Failed to start ATProto OAuth for handle {Handle}" )]
    private partial void LogAtProtoOAuthStartFailed( Exception ex, string handle );

    /// <summary>
    /// Logs that completing the ATProto OAuth callback failed.
    /// </summary>
    /// <param name="ex">The exception that caused the failure.</param>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerAtProtoCallbackFailed,
        Level = LogLevel.Error,
        Message = "Failed to complete ATProto OAuth callback" )]
    private partial void LogAtProtoCallbackFailed( Exception ex );

    /// <summary>Logs an SDK callback failure without recording OAuth secrets.</summary>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerAtProtoCallbackRejected,
        Level = LogLevel.Warning,
        Message = "ATProto OAuth callback rejected: {Reason}" )]
    private partial void LogAtProtoCallbackRejected( string reason );

    /// <summary>Logs the OAuth error code returned by the authorization server.</summary>
    [LoggerMessage(
        EventId = Logging.LogEventIds.Controllers.AccountControllerAtProtoOAuthServerError,
        Level = LogLevel.Warning,
        Message = "ATProto OAuth server returned error {ErrorCode}" )]
    private partial void LogAtProtoOAuthServerError( string errorCode );
}
