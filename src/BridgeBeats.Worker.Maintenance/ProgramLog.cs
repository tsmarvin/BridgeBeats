using BridgeBeats.Worker.Maintenance.Logging;

namespace BridgeBeats.Worker.Maintenance;

/// <summary>
/// Source-generated structured-logging helpers for the Maintenance startup path. These wrap the
/// Redis-connection verification messages emitted from <see cref="Program"/> before the worker runs.
/// </summary>
internal static partial class ProgramLog {
    /// <summary>Logs the Redis connection endpoints and state at startup.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="endpoints">
    /// The Redis connection string with the password already masked (must be pre-sanitized by the
    /// caller using <c>SanitizeRedisConfiguration</c> before passing here).
    /// </param>
    /// <param name="isConnected">Whether the multiplexer reports a live connection.</param>
    /// <param name="database">The Redis database number in use.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisConnectionInfo,
        Level = LogLevel.Information,
        Message = "Redis connection: {Endpoints}, IsConnected: {IsConnected}, Database: {Database}" )]
    internal static partial void LogRedisConnectionInfo(
        ILogger logger,
        string? endpoints,
        bool isConnected,
        int database );

    /// <summary>Logs the result of the startup Redis write/read-back smoke test.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="setResult">Whether the test key was written successfully.</param>
    /// <param name="readBack">The value read back from the test key.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisWriteTest,
        Level = LogLevel.Information,
        Message = "Redis write test - SetResult: {SetResult}, ReadBack: {ReadBack}" )]
    internal static partial void LogRedisWriteTest(
        ILogger logger,
        bool setResult,
        string? readBack );

    /// <summary>Logs that the startup Redis write/read-back smoke test failed to verify.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="setResult">Whether the test key was reported as written.</param>
    /// <param name="readBack">The value read back from the test key, if any.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisWriteVerificationFailed,
        Level = LogLevel.Error,
        Message = "Redis write verification failed! SetResult: {SetResult}, ReadBack: {ReadBack}" )]
    internal static partial void LogRedisWriteVerificationFailed(
        ILogger logger,
        bool setResult,
        string? readBack );

    /// <summary>Logs the Redis key count for an endpoint at startup.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="endpoint">The Redis endpoint that was measured.</param>
    /// <param name="keyCount">The number of keys present on that endpoint.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisKeyCount,
        Level = LogLevel.Information,
        Message = "Redis server {Endpoint} has {KeyCount} keys" )]
    internal static partial void LogRedisKeyCount(
        ILogger logger,
        string endpoint,
        long keyCount );
}
