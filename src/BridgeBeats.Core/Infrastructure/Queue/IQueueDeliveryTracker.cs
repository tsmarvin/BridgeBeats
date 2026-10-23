namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Tracks deliveries currently executing inside one queue consumer so its own PEL recovery scan
/// cannot hand the same Redis entry to another concurrent processing task.
/// </summary>
public interface IQueueDeliveryTracker {
    /// <summary>Releases a failed delivery for a later recovery attempt without acknowledging it.</summary>
    void ReleaseDelivery( string messageId );
}
