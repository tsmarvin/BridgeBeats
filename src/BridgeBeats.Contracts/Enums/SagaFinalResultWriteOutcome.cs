namespace BridgeBeats.Contracts.Enums;

/// <summary>Outcome of the instance-fenced, first-writer-wins final-result URI operation.</summary>
public enum SagaFinalResultWriteOutcome {
    /// <summary>The expected saga generation no longer owns the hash.</summary>
    InstanceMismatch = 0,

    /// <summary>The URI was stored for the first time.</summary>
    Stored = 1,

    /// <summary>The same URI was already stored by an idempotent replay.</summary>
    Idempotent = 2,

    /// <summary>A different URI was already stored for the same saga generation.</summary>
    Conflict = -1
}
