using BridgeBeats.Worker.SagaCoordinator.Logging;

namespace BridgeBeats.Worker.SagaCoordinator;

/// <summary>
/// Source-generated structured-logging helpers for the SagaCoordinator startup path. These wrap the
/// diagnostic messages emitted from <see cref="Program"/> before the coordinator's hosted services run.
/// </summary>
internal static partial class ProgramLog {
    /// <summary>Logs the set of providers enabled for secondary lookups at startup.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="enabledProviders">
    /// A comma-joined list of the enabled providers, or <c>"(none)"</c> when no secondary
    /// providers are enabled.
    /// </param>
    [LoggerMessage(
        EventId = LogEventIds.EnabledProviders,
        Level = LogLevel.Information,
        Message = "Enabled providers for secondary lookups: {EnabledProviders}" )]
    internal static partial void LogEnabledProviders( ILogger logger, string enabledProviders );
}
