using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;

namespace BridgeBeats.Core.Domain.Services.LinkResolver;

/// <summary>
/// Caching-path implementation of <see cref="ILookupProgressProbe"/>. Reads the saga state for
/// the given lookup key and returns <see langword="true"/> only when a saga is present, not yet
/// complete, and has no finalized result URI — the three conditions that together identify an
/// actively running lookup rather than a finalizing or finalized one.
/// </summary>
/// <remarks>
/// The predicate guards against two false-positive cases a naive null-check would miss:
/// <list type="bullet">
///   <item><description>
///     A saga that is complete (all providers have reported in) but whose PDS write is still
///     in flight. <see cref="LookupSagaState.IsComplete"/> is already true at that point, so
///     the predicate returns <see langword="false"/>.
///   </description></item>
///   <item><description>
///     A saga that has a finalized result URI set. The check on
///     <see cref="LookupSagaState.FinalResultUri"/> ensures lingering saga rows after cleanup
///     races do not trigger the indicator.
///   </description></item>
/// </list>
/// On a transient saga-store fault the probe returns <see langword="false"/> (cannot determine
/// → not in progress), matching the design's graceful-degradation intent. The fault is logged
/// at <see cref="LogLevel.Warning"/> so it surfaces in monitoring without disrupting the render.
/// Cancellation also returns <see langword="false"/>, but is not logged as a store fault.
/// </remarks>
/// <param name="sagaManager">Used to read saga state by id.</param>
/// <param name="logger">Used to log saga-store read faults at warning level.</param>
public sealed class LookupProgressProbe( ISagaStateManager sagaManager, ILogger<LookupProgressProbe> logger ) : ILookupProgressProbe {

    /// <summary>Saga state manager used to read per-lookup progress.</summary>
    private readonly ISagaStateManager _sagaManager = sagaManager
                                                    ?? throw new ArgumentNullException( nameof( sagaManager ) );

    /// <summary>Logger used to record transient saga-store read faults.</summary>
    private readonly ILogger<LookupProgressProbe> _logger = logger
                                                          ?? throw new ArgumentNullException( nameof( logger ) );

    /// <inheritdoc/>
    public async Task<bool> IsActiveAsync( string lookupKey, CancellationToken ct = default ) {
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        try {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            return saga is not null
                && !saga.IsComplete
                && string.IsNullOrEmpty( saga.FinalResultUri );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Request and host cancellation are expected lifecycle events. Stop the store read and
            // degrade quietly rather than converting shutdown into a render failure.
            return false;
        } catch (Exception ex) {
            string sanitizedSagaId = sagaId.SanitizeForLogging( );
            _logger.LogWarning( ex,
                "Saga store read failed for saga {SagaId}; reporting not in progress",
                sanitizedSagaId );
            return false;
        }
    }
}

/// <summary>
/// No-op implementation of <see cref="ILookupProgressProbe"/> for the non-caching path where
/// sagas do not exist. Always returns <see langword="false"/>.
/// </summary>
public sealed class NullLookupProgressProbe : ILookupProgressProbe {

    /// <inheritdoc/>
    public Task<bool> IsActiveAsync( string lookupKey, CancellationToken ct = default )
        => Task.FromResult( false );
}
