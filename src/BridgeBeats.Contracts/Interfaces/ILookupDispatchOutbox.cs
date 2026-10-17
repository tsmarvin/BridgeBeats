using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Transactional outbox for initial provider-leg dispatch. Staging atomically initializes saga
/// provider state and records the queue payload; dispatch atomically transfers that payload to the
/// provider stream. Published envelopes retain the current delivery id, payload, and eligibility
/// time until completion so missing deliveries can be recovered without duplicating live or
/// deliberately deferred work.
/// </summary>
public interface ILookupDispatchOutbox {
    /// <summary>
    /// Atomically stages every initial provider dispatch for one saga generation. Either the whole
    /// expected provider set is initialized and staged, or none of it is changed.
    /// </summary>
    Task<IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>> StageBatchAsync(
        IReadOnlyList<QueuedLookupRequest> requests,
        QueuePriority priority,
        CancellationToken cancellationToken = default );

    /// <summary>
    /// Atomically registers stale-record review context and stages every initial provider
    /// dispatch for that refresh. No staged leg becomes relay-visible unless its review context
    /// is committed in the same Redis transaction.
    /// </summary>
    Task<IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>> StageRefreshBatchAsync(
        IReadOnlyList<QueuedLookupRequest> requests,
        QueuePriority priority,
        RefreshReviewEntry reviewEntry,
        CancellationToken cancellationToken = default );

    /// <summary>Stages one provider dispatch while the supplied saga instance token still owns it.</summary>
    Task<ProviderDispatchStageOutcome> StageAsync(
        QueuedLookupRequest request,
        QueuePriority priority,
        CancellationToken cancellationToken = default );

    /// <summary>Attempts to atomically publish one staged provider dispatch.</summary>
    Task<bool> DispatchAsync(
        string sagaId,
        SupportedProviders provider,
        CancellationToken cancellationToken = default );

    /// <summary>
    /// Promotes a saga and its unfinished, unclaimed provider deliveries to the interactive lane.
    /// Already-claimed deliveries remain with their active workers so promotion cannot duplicate
    /// provider calls. Any recorded eligibility delay is preserved.
    /// </summary>
    /// <returns><see langword="true"/> when the supplied saga generation still owns the promotion.</returns>
    Task<bool> PromoteToInteractiveAsync(
        string sagaId,
        string instanceToken,
        IReadOnlyCollection<SupportedProviders> pendingProviders,
        CancellationToken cancellationToken = default );

    /// <summary>Fairly relays a bounded batch of due pending items and published-delivery recovery checks.</summary>
    Task<int> DispatchPendingAsync( int limit, CancellationToken cancellationToken = default );
}
