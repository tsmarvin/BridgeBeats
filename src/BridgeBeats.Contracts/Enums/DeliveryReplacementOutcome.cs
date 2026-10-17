namespace BridgeBeats.Contracts.Enums;

/// <summary>Outcome of atomically replacing one Redis queue delivery with another.</summary>
public enum DeliveryReplacementOutcome {
    /// <summary>The source delivery no longer exists.</summary>
    SourceMissing = 0,
    /// <summary>The replacement was published and the source delivery was retired.</summary>
    Replaced = 1,
    /// <summary>The recovery record already points at a newer delivery, so the stale source was retired.</summary>
    StaleDeliveryRetired = 2,
    /// <summary>The source belongs to an older saga generation and was retired.</summary>
    GenerationChanged = 3,
    /// <summary>
    /// The source identifies a recovery record that no longer exists. The source is preserved so
    /// the caller can investigate or retry without losing the request.
    /// </summary>
    RecoveryRecordMissing = 4
}
