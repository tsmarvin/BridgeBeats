using System.Net;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Infrastructure.Storage;

/// <summary>
/// Source-generated logging methods for <see cref="RedisATProtoSessionManager"/>, declared as a
/// partial-class continuation. Each method maps to a single structured log message and carries no logic.
/// </summary>
public sealed partial class RedisATProtoSessionManager {

    /// <summary>Logs that a forced re-authentication was requested for the account.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerForceReauthRequested,
        Level = LogLevel.Warning,
        Message = "Force re-authentication requested for {Identifier}" )]
    private partial void LogForceReauthRequested( string identifier );

    /// <summary>Logs that no persisted session was found in Redis, so a fresh login is needed.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerNoStoredCredentials,
        Level = LogLevel.Debug,
        Message = "No stored credentials found in Redis for {Identifier}" )]
    private partial void LogNoStoredCredentials( string identifier );

    /// <summary>Logs that the persisted session could not be deserialized.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerDeserializeFailed,
        Level = LogLevel.Warning,
        Message = "Failed to deserialize stored credentials for {Identifier}" )]
    private partial void LogDeserializeFailed( string identifier );

    /// <summary>Logs the start of a session-restore attempt, noting when the session was persisted.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    /// <param name="persistedAt">The time the restored session was persisted.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerRestoringSession,
        Level = LogLevel.Information,
        Message = "Attempting to restore session for {Identifier} (persisted at {PersistedAt})" )]
    private partial void LogRestoringSession( string identifier, DateTimeOffset persistedAt );

    /// <summary>Logs that the persisted session was restored and refreshed successfully.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerSessionRestored,
        Level = LogLevel.Information,
        Message = "Successfully restored session for {Identifier}" )]
    private partial void LogSessionRestored( string identifier );

    /// <summary>Logs that session restore failed because the credential refresh returned false.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerRestoreFailed,
        Level = LogLevel.Warning,
        Message = "Failed to restore session for {Identifier} - refresh returned false" )]
    private partial void LogRestoreFailed( string identifier );

    /// <summary>Logs an exception thrown while restoring the session.</summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerRestoreException,
        Level = LogLevel.Warning,
        Message = "Exception while restoring session for {Identifier}" )]
    private partial void LogRestoreException( Exception ex, string identifier );

    /// <summary>
    /// Logs a cryptographic failure while decrypting the stored session, caused by a key-ring mismatch
    /// or a legacy plaintext value that has not yet been rotated to ciphertext. The manager degrades
    /// to a fresh login — not a generic restore failure — so this event is tracked distinctly.
    /// </summary>
    /// <param name="ex">The <see cref="System.Security.Cryptography.CryptographicException"/> that occurred.</param>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerRestoreCryptoMismatch,
        Level = LogLevel.Warning,
        Message = "Cryptographic mismatch decrypting stored session for {Identifier} — key-ring may have rotated or value is pre-encryption plaintext; falling back to fresh login" )]
    private partial void LogRestoreCryptoMismatch( Exception ex, string identifier );

    /// <summary>Logs that another instance holds the distributed lock and this instance is polling for it.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerWaitingForLock,
        Level = LogLevel.Debug,
        Message = "Another instance holds the credential-mutation lock for {Identifier}, waiting..." )]
    private partial void LogWaitingForLock( string identifier );

    /// <summary>Logs that the wait for the distributed lock timed out, so the operation fails closed to protect the shared refresh token.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerLockWaitTimeout,
        Level = LogLevel.Error,
        Message = "Distributed lock wait timed out for {Identifier} — failing closed to protect shared refresh token" )]
    private partial void LogLockWaitTimeout( string identifier );

    /// <summary>Logs the start of a fresh login (when no session could be restored).</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerFreshLogin,
        Level = LogLevel.Information,
        Message = "Performing fresh login for {Identifier}" )]
    private partial void LogFreshLogin( string identifier );

    /// <summary>Logs that a fresh login to the PDS succeeded.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerAuthenticated,
        Level = LogLevel.Information,
        Message = "Successfully authenticated to PDS for {Identifier}" )]
    private partial void LogAuthenticated( string identifier );

    /// <summary>Logs a failure to persist credentials following the credentials-updated event.</summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerPersistUpdateFailed,
        Level = LogLevel.Error,
        Message = "Failed to persist updated credentials for {Identifier}" )]
    private partial void LogPersistUpdateFailed( Exception ex, string identifier );

    /// <summary>
    /// Logs receipt of the agent's token-refresh-failed event, with the HTTP status code if any.
    /// Demoted to Debug because with background refresh disabled this event fires only on explicit
    /// refresh calls; the relevant diagnostic is in the exception-path logs, not here. The status
    /// code is included to aid triage of unrecoverable versus transport failures.
    /// </summary>
    /// <param name="identifier">The service-account identifier.</param>
    /// <param name="statusCode">The HTTP status code associated with the failure, if available.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerTokenRefreshFailed,
        Level = LogLevel.Debug,
        Message = "Token refresh failed for {Identifier} (StatusCode: {StatusCode})" )]
    private partial void LogTokenRefreshFailed( string identifier, HttpStatusCode? statusCode );

    /// <summary>Logs that a credential clear was suppressed because it fell within the cooldown window.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerClearSuppressedByCooldown,
        Level = LogLevel.Debug,
        Message = "Credential clear suppressed by cooldown for {Identifier}" )]
    private partial void LogClearSuppressedByCooldown( string identifier );

    /// <summary>Logs a failure to clear credentials after handling a token-refresh failure.</summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerClearAfterRefreshFailed,
        Level = LogLevel.Error,
        Message = "Failed to clear credentials after token refresh failure for {Identifier}" )]
    private partial void LogClearAfterRefreshFailed( Exception ex, string identifier );

    /// <summary>Logs receipt of the agent's unauthenticated event.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerUnauthenticated,
        Level = LogLevel.Information,
        Message = "Agent unauthenticated event for {Identifier}" )]
    private partial void LogUnauthenticated( string identifier );

    /// <summary>Logs a failure to clear credentials after handling an unauthenticated event.</summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerClearAfterUnauthFailed,
        Level = LogLevel.Error,
        Message = "Failed to clear credentials after unauthenticated event for {Identifier}" )]
    private partial void LogClearAfterUnauthFailed( Exception ex, string identifier );

    /// <summary>Logs that credentials could not be persisted because the agent is not authenticated.</summary>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerCannotPersist,
        Level = LogLevel.Debug,
        Message = "Cannot persist credentials - agent not authenticated" )]
    private partial void LogCannotPersist( );

    /// <summary>Logs that credentials were persisted to Redis.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerPersisted,
        Level = LogLevel.Debug,
        Message = "Persisted credentials to Redis for {Identifier}" )]
    private partial void LogPersisted( string identifier );

    /// <summary>Logs that the persisted session was cleared from Redis.</summary>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerCleared,
        Level = LogLevel.Debug,
        Message = "Cleared stored credentials from Redis for {Identifier}" )]
    private partial void LogCleared( string identifier );

    /// <summary>Logs a failure to release the distributed credential-mutation lock.</summary>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="identifier">The service-account identifier.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Storage.RedisATProtoSessionManagerLockReleaseFailed,
        Level = LogLevel.Error,
        Message = "Failed to release distributed credential-mutation lock for {Identifier}" )]
    private partial void LogLockReleaseFailed( Exception ex, string identifier );
}
