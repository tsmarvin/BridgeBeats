using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;

namespace BridgeBeats.Tests.Infrastructure;

/// <summary>Test conveniences that obtain the current token before exercising fenced Redis operations.</summary>
internal static class RedisSagaStateManagerTestExtensions {
    internal static async Task UpdateProviderStateAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        ProviderLookupState state,
        CancellationToken cancellationToken = default
    ) {
        string token = await GetTokenAsync( manager, sagaId, cancellationToken );
        if (!await manager.TryUpdateProviderStateAsync( sagaId, state, token, cancellationToken )) {
            throw new InvalidOperationException( $"Lost saga fence for '{sagaId}'." );
        }
    }

    internal static async Task SetPartialResultUriAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        string uri,
        CancellationToken cancellationToken = default
    ) {
        string token = await GetTokenAsync( manager, sagaId, cancellationToken );
        if (!await manager.TrySetPartialResultUriAsync( sagaId, uri, token, cancellationToken )) {
            throw new InvalidOperationException( $"Lost saga fence for '{sagaId}'." );
        }
    }

    internal static async Task SetFinalResultUriAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        string uri,
        CancellationToken cancellationToken = default
    ) {
        string token = await GetTokenAsync( manager, sagaId, cancellationToken );
        SagaFinalResultWriteOutcome outcome = await manager.TrySetFinalResultUriAsync( sagaId, uri, token, cancellationToken );
        if (outcome is not SagaFinalResultWriteOutcome.Stored and not SagaFinalResultWriteOutcome.Idempotent) {
            throw new InvalidOperationException( $"Lost saga fence for '{sagaId}'." );
        }
    }

    internal static async Task<bool> DeleteAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        CancellationToken cancellationToken = default
    ) {
        LookupSagaState? saga = await manager.GetAsync( sagaId, cancellationToken );
        return saga?.InstanceToken is { Length: > 0 } token
            && await manager.TryDeleteAsync( sagaId, token, cancellationToken );
    }

    internal static async Task<bool> TryClaimFinalizeAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        CancellationToken cancellationToken = default
    ) {
        LookupSagaState? saga = await manager.GetAsync( sagaId, cancellationToken );
        return saga?.InstanceToken is { Length: > 0 } token
            && await manager.TryClaimFinalizeAsync( sagaId, token, cancellationToken );
    }

    internal static async Task<bool> TryMarkSecondariesQueuedAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        CancellationToken cancellationToken = default
    ) {
        LookupSagaState? saga = await manager.GetAsync( sagaId, cancellationToken );
        return saga?.InstanceToken is { Length: > 0 } token
            && await manager.TryMarkSecondariesQueuedAsync( sagaId, token, cancellationToken );
    }

    internal static async Task ReleaseFinalizeClaimAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        CancellationToken cancellationToken = default
    ) {
        string token = await GetTokenAsync( manager, sagaId, cancellationToken );
        _ = await manager.TryReleaseFinalizeClaimAsync( sagaId, token, cancellationToken );
    }

    internal static async Task<bool> TryAdvanceWriteGenerationAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        int generation,
        CancellationToken cancellationToken = default
    ) {
        LookupSagaState? saga = await manager.GetAsync( sagaId, cancellationToken );
        return saga?.InstanceToken is { Length: > 0 } token
            && await manager.TryAdvanceWriteGenerationAsync( sagaId, generation, token, cancellationToken );
    }

    internal static async Task ResetWriteGenerationAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        int advancedTo,
        int priorGeneration,
        CancellationToken cancellationToken = default
    ) {
        string token = await GetTokenAsync( manager, sagaId, cancellationToken );
        _ = await manager.TryResetWriteGenerationAsync(
            sagaId, advancedTo, priorGeneration, token, cancellationToken );
    }

    internal static async Task TryInitializeProviderStatesAsync(
        this RedisSagaStateManager manager,
        string sagaId,
        IEnumerable<SupportedProviders> providers,
        CancellationToken cancellationToken = default
    ) {
        string token = await GetTokenAsync( manager, sagaId, cancellationToken );
        if (!await manager.TryInitializeProviderStatesAsync( sagaId, providers, token, cancellationToken )) {
            throw new InvalidOperationException( $"Lost saga fence for '{sagaId}'." );
        }
    }

    private static async Task<string> GetTokenAsync(
        RedisSagaStateManager manager,
        string sagaId,
        CancellationToken cancellationToken
    ) {
        LookupSagaState? saga = await manager.GetAsync( sagaId, cancellationToken );
        return saga?.InstanceToken
            ?? throw new InvalidOperationException( $"Saga '{sagaId}' has no readable instance token." );
    }
}
