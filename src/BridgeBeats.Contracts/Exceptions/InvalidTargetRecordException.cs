namespace BridgeBeats.Contracts.Exceptions;

/// <summary>
/// Identifies a refresh target that can never be updated by the authenticated BridgeBeats
/// repository because its AT-URI is malformed, belongs to another collection, or names another
/// repository.
/// </summary>
public sealed class InvalidTargetRecordException : ArgumentException {
    /// <summary>Creates a deterministic refresh-target validation failure.</summary>
    public InvalidTargetRecordException( string message, string? paramName = null )
        : base( message, paramName ) { }

    /// <summary>Creates a deterministic refresh-target validation failure with its parse cause.</summary>
    public InvalidTargetRecordException( string message, string? paramName, Exception innerException )
        : base( message, paramName, innerException ) { }
}
