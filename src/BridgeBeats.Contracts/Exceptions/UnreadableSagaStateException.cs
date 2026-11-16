namespace BridgeBeats.Contracts.Exceptions;

/// <summary>
/// Indicates that a Redis saga hash exists but cannot be reconstructed into a valid saga state.
/// Queued deliveries referencing this state must be quarantined rather than retried forever.
/// </summary>
public sealed class UnreadableSagaStateException : InvalidOperationException {
    /// <summary>The deterministic identifier of the unreadable saga.</summary>
    public string SagaId { get; }

    /// <summary>Creates an exception for an unreadable persisted saga.</summary>
    public UnreadableSagaStateException( string sagaId )
        : base( $"Saga '{sagaId}' exists but its persisted state is unreadable." ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        SagaId = sagaId;
    }
}
