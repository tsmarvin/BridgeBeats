namespace BridgeBeats.Contracts.Enums;

/// <summary>Origin of an accepted queue enqueue.</summary>
public enum QueueEnqueueOrigin {
    /// <summary>New request.</summary>
    New,
    /// <summary>Retry or requeue.</summary>
    Requeue,
    /// <summary>Maintenance refresh sweep.</summary>
    RefreshSweep
}
