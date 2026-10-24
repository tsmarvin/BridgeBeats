namespace BridgeBeats.Contracts.Exceptions;

/// <summary>Thrown when an application-settings update contains invalid values.</summary>
public sealed class ApplicationSettingsValidationException : Exception {
    /// <summary>Initializes an exception with one or more non-sensitive validation messages.</summary>
    /// <param name="errors">The validation messages.</param>
    public ApplicationSettingsValidationException( IReadOnlyList<string> errors )
        : base( "Application settings validation failed." ) {
        Errors = errors;
    }

    /// <summary>Gets the non-sensitive validation messages.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Thrown when an application-settings update is based on a stale revision.</summary>
public sealed class ApplicationSettingsConcurrencyException : Exception {
    /// <summary>Initializes an application-settings concurrency exception.</summary>
    public ApplicationSettingsConcurrencyException( )
        : base( "Application settings changed after they were read. Reload them and retry the update." ) { }

    /// <summary>Initializes an application-settings concurrency exception with an inner failure.</summary>
    /// <param name="innerException">The persistence failure that detected the conflict.</param>
    public ApplicationSettingsConcurrencyException( Exception innerException )
        : base( "Application settings changed after they were read. Reload them and retry the update.", innerException ) { }
}

/// <summary>
/// Base exception thrown when stored application settings cannot be safely interpreted. Messages
/// never include stored payload data.
/// </summary>
public class ApplicationSettingsUnreadableException : InvalidOperationException {
    /// <summary>Initializes an unreadable-settings exception with a safe diagnostic.</summary>
    /// <param name="message">The non-sensitive operator diagnostic.</param>
    /// <param name="innerException">The underlying storage or protected-data failure.</param>
    protected ApplicationSettingsUnreadableException( string message, Exception innerException )
        : base( message, innerException ) { }
}

/// <summary>Thrown when the protected secret document cannot be decrypted or authenticated.</summary>
public sealed class ApplicationSettingsDecryptionException : ApplicationSettingsUnreadableException {
    /// <summary>Initializes a decryption exception with key-preservation guidance.</summary>
    /// <param name="innerException">The protected-data failure.</param>
    public ApplicationSettingsDecryptionException( Exception innerException )
        : base(
            "Stored application-setting secrets could not be decrypted. Do not overwrite the settings row. Verify that DataProtectionKeyPath points to the original persistent key ring; restore the original keys before considering a database restore.",
            innerException
        ) { }
}

/// <summary>Thrown when the stored settings envelope or JSON documents are malformed or unsupported.</summary>
public sealed class ApplicationSettingsDataException : ApplicationSettingsUnreadableException {
    /// <summary>Initializes a stored-data exception with compatible-backup guidance.</summary>
    /// <param name="innerException">The schema, protection-scheme, or deserialization failure.</param>
    public ApplicationSettingsDataException( Exception innerException )
        : base(
            "Stored application settings are malformed or use an unsupported schema. Do not overwrite the settings row; restore a compatible database backup or run the required application migration.",
            innerException
        ) { }
}

/// <summary>Thrown when new secret settings cannot be protected before persistence.</summary>
public sealed class ApplicationSettingsProtectionException : InvalidOperationException {
    /// <summary>Initializes a protection exception with a non-sensitive diagnostic.</summary>
    /// <param name="innerException">The protected-data failure.</param>
    public ApplicationSettingsProtectionException( Exception innerException )
        : base(
            "Application-setting secrets could not be encrypted. No settings were written. Verify the Data Protection key-ring configuration and permissions.",
            innerException
        ) { }
}
