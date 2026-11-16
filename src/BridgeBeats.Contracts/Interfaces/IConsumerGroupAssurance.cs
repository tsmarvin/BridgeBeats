namespace BridgeBeats.Contracts.Interfaces;

/// <summary>Provides an idempotent repair operation for a queue's Redis consumer groups.</summary>
public interface IConsumerGroupAssurance {
    /// <summary>Ensures all consumer groups required by the queue exist.</summary>
    Task EnsureConsumerGroupsAsync( CancellationToken cancellationToken = default );
}
