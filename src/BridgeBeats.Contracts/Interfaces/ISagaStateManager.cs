using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Durable state store for multi-provider lookup sagas: create-or-get, read, per-provider and
/// per-field updates, the pending-saga index, reconciliation queries, a once-only secondary
/// fan-out guard, and deterministic saga-id derivation.
/// </summary>
/// <remarks>
/// Implemented in <c>BridgeBeats.Core</c> by <c>RedisSagaStateManager</c>
/// (<c>Infrastructure/Queue/RedisSagaStateManager.cs</c>). Tracks lookup progress across
/// providers; per-provider states are stored separately for efficient partial updates. The saga
/// id is deterministically derived from the lookup key (see <see cref="GenerateSagaId"/>), so
/// identical lookups always map to the same saga. Saga state is the <see cref="LookupSagaState"/>
/// record; result pointers are AT-URIs (<c>at://…</c>).
/// Instance fencing intentionally remains an explicit string parameter in this pre-v0.1 contract:
/// the token is a serialized wire/storage value shared by Redis hashes and queued request payloads,
/// while validation belongs at every public mutation boundary. A wrapper would not remove those
/// validation or serialization obligations and would substantially widen this interface.
/// </remarks>
public interface ISagaStateManager {
    /// <summary>
    /// Returns the existing saga for the id, or creates one with the supplied seed values if it
    /// does not yet exist. Idempotent: repeated calls with the same id return the same saga.
    /// </summary>
    /// <param name="sagaId">The saga id, typically produced by <see cref="GenerateSagaId"/> from the lookup key.</param>
    /// <param name="lookupKey">The normalized deduplication key this saga resolves (for example <c>isrc:USRC12345678</c>).</param>
    /// <param name="lookupType">The lookup strategy this saga uses.</param>
    /// <param name="lookupValue">The raw lookup value being resolved.</param>
    /// <param name="originPriority">
    /// The priority the originating request arrived on. When provided, it is recorded as the
    /// saga's origin priority only if one has not been recorded yet (first writer wins). When
    /// <see langword="null"/>, no origin priority is written and the saga defaults to
    /// <see cref="QueuePriority.Background"/> until a worker records one.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the existing or newly created <see cref="LookupSagaState"/>.</returns>
    Task<LookupSagaState> GetOrCreateAsync(
        string sagaId,
        string lookupKey,
        LookupRequestType lookupType,
        string lookupValue,
        QueuePriority? originPriority = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Reads a saga's current state by id. Used by workers processing a queued request that
    /// references a saga id.
    /// </summary>
    /// <param name="sagaId">The saga id to read.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the <see cref="LookupSagaState"/>, or <see langword="null"/> when no
    /// saga exists with the given id or it has expired.
    /// </returns>
    /// <exception cref="UnreadableSagaStateException">
    /// Thrown when the saga hash exists but cannot be reconstructed safely, including a missing or
    /// invalid instance token.
    /// </exception>
    Task<LookupSagaState?> GetAsync( string sagaId, CancellationToken cancellationToken = default );

    /// <summary>Updates a provider leg only when the saga instance token still matches.</summary>
    Task<bool> TryUpdateProviderStateAsync( string sagaId, ProviderLookupState state, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>
    /// Promotes a background/bulk saga to interactive urgency once, fenced to its current instance.
    /// </summary>
    /// <returns><see langword="true"/> only for the call that performed the promotion.</returns>
    Task<bool> TryPromoteToInteractiveAsync(
        string sagaId,
        string expectedInstanceToken,
        CancellationToken cancellationToken = default );

    /// <summary>Stores the partial-result AT-URI when the saga instance token still matches.</summary>
    /// <param name="sagaId">The saga id to update.</param>
    /// <param name="uri">The AT-URI (<c>at://…</c>) of the partial result record.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns><see langword="true"/> when the URI was stored; otherwise <see langword="false"/>.</returns>
    Task<bool> TrySetPartialResultUriAsync( string sagaId, string uri, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Stores the final-result AT-URI with token fencing and first-writer-wins semantics.</summary>
    /// <param name="sagaId">The saga id to update.</param>
    /// <param name="uri">The AT-URI (<c>at://…</c>) of the final result record.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns>The applied, idempotent, conflicting, or instance-mismatch outcome.</returns>
    Task<SagaFinalResultWriteOutcome> TrySetFinalResultUriAsync(
        string sagaId,
        string uri,
        string expectedInstanceToken,
        CancellationToken cancellationToken = default );

    /// <summary>Deletes a saga and its provider states only when its instance token matches.</summary>
    /// <param name="sagaId">The saga id to delete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> when a saga was deleted, or
    /// <see langword="false"/> when no saga existed with the given id.
    /// </returns>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    Task<bool> TryDeleteAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>
    /// Finds completed sagas that still require reconciliation. This includes sagas without a
    /// final URI and finalized sagas deliberately retained in the pending index until their
    /// deduplication waiters have been notified.
    /// </summary>
    /// <param name="minimumAge">Only sagas at least this old are returned, to avoid racing in-flight Pub/Sub messages.</param>
    /// <param name="limit">The maximum number of sagas to return per call; defaults to 100.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the completed sagas still requiring reconciliation; an empty list when none qualify.
    /// </returns>
    Task<IReadOnlyList<LookupSagaState>> GetPendingReconciliationAsync(
        TimeSpan minimumAge,
        int limit = 100,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Adds a saga to the pending-saga index when it is created, so reconciliation sweeps can find
    /// it without scanning the keyspace.
    /// </summary>
    /// <param name="sagaId">The saga id to add to the pending index.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the saga has been added to the index.</returns>
    Task AddToPendingIndexAsync( string sagaId, CancellationToken cancellationToken = default );

    /// <summary>
    /// Removes a saga from the pending-saga index, typically once it has been finalized or
    /// deleted.
    /// </summary>
    /// <param name="sagaId">The saga id to remove from the pending index.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the saga has been removed from the index.</returns>
    Task RemoveFromPendingIndexAsync( string sagaId, CancellationToken cancellationToken = default );

    /// <summary>Sets whether a token-matched saga is partial.</summary>
    /// <param name="sagaId">The saga id to update.</param>
    /// <param name="isPartial"><see langword="true"/> when the result is still partial; <see langword="false"/> when complete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns><see langword="true"/> when the flag was stored; otherwise <see langword="false"/>.</returns>
    Task<bool> TrySetIsPartialAsync( string sagaId, bool isPartial, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Initializes provider legs only when the saga instance token still matches.</summary>
    Task<bool> TryInitializeProviderStatesAsync( string sagaId, IEnumerable<SupportedProviders> providers, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Records the provider that seeded a token-matched saga.</summary>
    /// <param name="sagaId">The saga id to update.</param>
    /// <param name="provider">The initial provider for the saga.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns><see langword="true"/> when the provider was stored; otherwise <see langword="false"/>.</returns>
    Task<bool> TrySetInitialProviderAsync( string sagaId, SupportedProviders provider, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Stores per-provider rate-limit information for a token-matched saga.</summary>
    /// <param name="sagaId">The saga id to update.</param>
    /// <param name="rateLimitInfo">The per-provider <see cref="ProviderRateLimitInfo"/> entries to store.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns><see langword="true"/> when the information was stored; otherwise <see langword="false"/>.</returns>
    Task<bool> TrySetRateLimitInfoAsync( string sagaId, List<ProviderRateLimitInfo> rateLimitInfo, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Claims token-matched secondary fan-out once using a set-if-absent marker.</summary>
    /// <param name="sagaId">The saga id to mark.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> when this caller set the marker (and should
    /// queue secondaries), or <see langword="false"/> when it was already set by an earlier caller.
    /// </returns>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    Task<bool> TryMarkSecondariesQueuedAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Claims token-matched finalization once using a set-if-absent marker.</summary>
    /// <param name="sagaId">The saga id to claim finalization for.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> when this caller acquired the claim (and must
    /// finalize), or <see langword="false"/> when another caller already holds it.
    /// </returns>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    Task<bool> TryClaimFinalizeAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Releases a token-matched finalize claim after a pre-durability failure.</summary>
    /// <param name="sagaId">The saga id whose finalize claim should be released.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns><see langword="true"/> when the claim was released; otherwise <see langword="false"/>.</returns>
    Task<bool> TryReleaseFinalizeClaimAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Advances a token-matched saga's write generation using compare-and-set semantics.</summary>
    /// <param name="sagaId">The saga id to advance.</param>
    /// <param name="generation">The generation to advance to; typically the number of provider results in the combined result.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> when this caller advanced the generation (and
    /// must perform the PDS write), or <see langword="false"/> when the stored generation was
    /// already at or above <paramref name="generation"/> and the write should be skipped.
    /// </returns>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    Task<bool> TryAdvanceWriteGenerationAsync( string sagaId, int generation, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>
    /// Resets a token-matched saga's write generation after a pre-durability failure, but only when
    /// the stored value still equals <paramref name="advancedTo"/>.
    /// </summary>
    /// <param name="sagaId">The saga id whose write generation should be reset.</param>
    /// <param name="advancedTo">
    /// The generation this caller previously advanced to; the stored value must equal this for the
    /// reset to take effect.
    /// </param>
    /// <param name="priorGeneration">The generation to restore; typically <c>advancedTo - 1</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <param name="expectedInstanceToken">The required saga instance token.</param>
    /// <returns><see langword="true"/> when reset; otherwise <see langword="false"/>.</returns>
    Task<bool> TryResetWriteGenerationAsync( string sagaId, int advancedTo, int priorGeneration, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>
    /// Derives a deterministic saga id from a lookup key. The key is upper-cased, hashed with
    /// SHA-256, and the first 16 bytes of the hash are returned as lowercase hexadecimal.
    /// </summary>
    /// <param name="lookupKey">The lookup key to derive an id from (for example <c>isrc:USRC12345678</c>); must not be null or whitespace.</param>
    /// <returns>A 32-character lowercase hex saga id.</returns>
    /// <remarks>
    /// Because the key is upper-cased before hashing, the saga id is case-insensitive in the
    /// lookup key: two inputs differing only by case collapse to the same saga id. The id is also
    /// stable across calls and processes for the same key.
    /// </remarks>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="lookupKey"/> is null, empty, or whitespace.</exception>
    static string GenerateSagaId( string lookupKey ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( lookupKey );
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes( lookupKey.ToUpperInvariant( ) )
        );
        // Use first 16 bytes for a shorter but still unique ID
        return Convert.ToHexString( hash.AsSpan( 0, 16 ) ).ToLowerInvariant( );
    }
}
