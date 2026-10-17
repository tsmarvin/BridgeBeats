namespace BridgeBeats.Contracts.Enums;

/// <summary>Outcome of atomically initializing a provider leg and staging its dispatch outbox item.</summary>
public enum ProviderDispatchStageOutcome {
    /// <summary>The saga instance no longer owns the requested provider leg.</summary>
    SagaInstanceMismatch = 0,
    /// <summary>A durable outbox item is pending delivery.</summary>
    Staged = 1,
    /// <summary>
    /// The provider leg is complete or has a fresh published delivery. Incomplete published legs
    /// become eligible for a guarded re-drive after the outbox visibility window.
    /// </summary>
    AlreadyDispatched = 2
}
