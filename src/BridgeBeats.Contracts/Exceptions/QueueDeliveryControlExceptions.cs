namespace BridgeBeats.Contracts.Exceptions;

/// <summary>Signals that durable provider state was committed but queue acknowledgement failed.</summary>
public sealed class PostCommitAcknowledgementException( string messageId, Exception innerException )
    : Exception( $"Acknowledgement failed after provider state commit for delivery {messageId}.", innerException );

/// <summary>Signals that recovery of an already-terminal delivery into the DLQ failed.</summary>
public sealed class TerminalDlqRecoveryException( string messageId, Exception innerException )
    : Exception( $"DLQ recovery failed for terminal delivery {messageId}.", innerException );

/// <summary>Signals that quarantining a delivery whose saga identity is invalid failed.</summary>
public sealed class QueueDeliveryIdentityQuarantineException(
    string messageId,
    string? sagaId,
    Exception innerException )
    : Exception(
        sagaId is null
            ? $"Identity quarantine failed for delivery {messageId}."
            : $"Saga identity quarantine failed for delivery {messageId} and saga {sagaId}.",
        innerException );
