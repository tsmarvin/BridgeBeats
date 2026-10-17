using System.Diagnostics;
using System.Text.Json.Serialization;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// Non-sensitive BridgeBeats application settings persisted in the application database.
/// Bootstrap settings needed to open the database and Data Protection key ring are intentionally
/// excluded.
/// </summary>
public sealed record ApplicationSettingsValues {
    /// <summary>Gets the Apple Developer team identifier.</summary>
    [JsonPropertyName( "appleTeamId" )]
    public string AppleTeamId { get; init; } = string.Empty;

    /// <summary>Gets the Apple Music key identifier.</summary>
    [JsonPropertyName( "appleKeyId" )]
    public string AppleKeyId { get; init; } = string.Empty;

    /// <summary>Gets the Spotify client identifier.</summary>
    [JsonPropertyName( "spotifyClientId" )]
    public string SpotifyClientId { get; init; } = string.Empty;

    /// <summary>Gets the Tidal client identifier.</summary>
    [JsonPropertyName( "tidalClientId" )]
    public string TidalClientId { get; init; } = string.Empty;

    /// <summary>Gets the ATProto service-account identifier.</summary>
    [JsonPropertyName( "atProtoIdentifier" )]
    public string ATProtoIdentifier { get; init; } = string.Empty;

    /// <summary>Gets the DID used for BridgeBeats ATProto records.</summary>
    [JsonPropertyName( "atProtoUserDid" )]
    public string ATProtoUserDid { get; init; } = string.Empty;

    /// <summary>Gets the ATProto personal data server URI.</summary>
    [JsonPropertyName( "atProtoPdsUri" )]
    public string ATProtoPdsUri { get; init; } = "https://pds.bridgebeats.link";

    /// <summary>Gets the public BridgeBeats domain or host name.</summary>
    [JsonPropertyName( "domain" )]
    public string Domain { get; init; } = string.Empty;

    /// <summary>Gets the per-user hourly lookup request limit.</summary>
    [JsonPropertyName( "rateLimitRequestsPerHour" )]
    public int RateLimitRequestsPerHour { get; init; } = 20;

    /// <summary>Gets the number of days a successful cache result remains fresh.</summary>
    [JsonPropertyName( "cacheDays" )]
    public int CacheDays { get; init; } = 7;

    /// <summary>Gets the number of days an ATProto service-account session is retained.</summary>
    [JsonPropertyName( "atProtoSessionTtlDays" )]
    public int ATProtoSessionTtlDays { get; init; } = 45;

    /// <summary>Gets the number of hours generated Open Graph cards remain cached.</summary>
    [JsonPropertyName( "cardCacheExpirationHours" )]
    public int CardCacheExpirationHours { get; init; } = 1;

    /// <summary>Gets the number of card writes between cache cleanup passes.</summary>
    [JsonPropertyName( "cardCacheCleanupInterval" )]
    public int CardCacheCleanupInterval { get; init; } = 500;

    /// <summary>Gets the maximum number of retained in-memory cards.</summary>
    [JsonPropertyName( "cardCacheMaxEntries" )]
    public int CardCacheMaxEntries { get; init; } = 25000;

    /// <summary>Gets provider-worker enablement settings.</summary>
    [JsonPropertyName( "workers" )]
    public ApplicationWorkerSettings Workers { get; init; } = new( );

    /// <summary>Gets outbound HTTP resilience settings.</summary>
    [JsonPropertyName( "resilience" )]
    public ApplicationResilienceSettings Resilience { get; init; } = new( );

    /// <summary>Gets queue and saga settings shared by the web and worker processes.</summary>
    [JsonPropertyName( "queue" )]
    public QueueSettings Queue { get; init; } = new( );

    /// <summary>Gets maintenance-worker scheduling settings.</summary>
    [JsonPropertyName( "maintenance" )]
    public ApplicationMaintenanceSettings Maintenance { get; init; } = new( );
}

/// <summary>Provider-worker enablement settings persisted with the application settings aggregate.</summary>
public sealed record ApplicationWorkerSettings {
    /// <summary>Gets a value indicating whether provider requests use worker HTTP services.</summary>
    [JsonPropertyName( "useWorkerServices" )]
    public bool UseWorkerServices { get; init; }

    /// <summary>Gets a value indicating whether the Spotify worker is enabled.</summary>
    [JsonPropertyName( "spotifyWorkerEnabled" )]
    public bool SpotifyWorkerEnabled { get; init; }

    /// <summary>Gets a value indicating whether the Apple Music worker is enabled.</summary>
    [JsonPropertyName( "appleMusicWorkerEnabled" )]
    public bool AppleMusicWorkerEnabled { get; init; }

    /// <summary>Gets a value indicating whether the Tidal worker is enabled.</summary>
    [JsonPropertyName( "tidalWorkerEnabled" )]
    public bool TidalWorkerEnabled { get; init; }
}

/// <summary>Outbound HTTP resilience settings persisted with the application settings aggregate.</summary>
public sealed record ApplicationResilienceSettings {
    /// <summary>Gets the largest server-supplied retry delay that will be honored.</summary>
    [JsonPropertyName( "maxRetryAfterSeconds" )]
    public int MaxRetryAfterSeconds { get; init; } = 120;

    /// <summary>Gets the maximum number of retry attempts.</summary>
    [JsonPropertyName( "maxRetryAttempts" )]
    public int MaxRetryAttempts { get; init; } = 5;

    /// <summary>Gets the total timeout across all attempts, in minutes.</summary>
    [JsonPropertyName( "totalTimeoutMinutes" )]
    public int TotalTimeoutMinutes { get; init; } = 10;

    /// <summary>Gets the timeout for each individual attempt, in seconds.</summary>
    [JsonPropertyName( "attemptTimeoutSeconds" )]
    public int AttemptTimeoutSeconds { get; init; } = 10;
}

/// <summary>Maintenance-worker scheduling settings persisted with the application settings aggregate.</summary>
public sealed record ApplicationMaintenanceSettings {
    /// <summary>Gets the interval between cache bootstrap passes, in hours.</summary>
    [JsonPropertyName( "bootstrapIntervalHours" )]
    public int BootstrapIntervalHours { get; init; } = 6;

    /// <summary>Gets the interval between stale-cache refresh passes, in hours.</summary>
    [JsonPropertyName( "refreshIntervalHours" )]
    public int RefreshIntervalHours { get; init; } = 6;

    /// <summary>Gets the maximum number of records handled by one refresh pass.</summary>
    [JsonPropertyName( "maxRecordsPerRun" )]
    public int MaxRecordsPerRun { get; init; } = 500;

    /// <summary>Gets the delay before retrying a failed refresh pass, in minutes.</summary>
    [JsonPropertyName( "refreshRetryMinutes" )]
    public int RefreshRetryMinutes { get; init; } = 5;
}

/// <summary>
/// A runtime secret value whose string and JSON representations never reveal its plaintext.
/// </summary>
[DebuggerDisplay( "{DebuggerDisplay,nq}" )]
public sealed class ApplicationSecretValue {
    private readonly string? _value;

    private ApplicationSecretValue( string? value ) {
        _value = string.IsNullOrWhiteSpace( value ) ? null : value;
    }

    /// <summary>Gets a value indicating whether the secret has a stored value.</summary>
    public bool IsConfigured => _value is not null;

    [JsonIgnore]
    private string DebuggerDisplay => IsConfigured ? "[CONFIGURED]" : "[NOT CONFIGURED]";

    /// <summary>Creates a redacting wrapper around a plaintext secret.</summary>
    /// <param name="value">The plaintext value, or <see langword="null"/> when unconfigured.</param>
    /// <returns>A wrapper that reveals only configuration status through ordinary display paths.</returns>
    public static ApplicationSecretValue FromPlaintext( string? value ) => new( value );

    /// <summary>
    /// Reveals the plaintext to an authorized runtime consumer. Callers must not log, serialize, or
    /// return the result through display models.
    /// </summary>
    /// <returns>The plaintext value, or <see langword="null"/> when unconfigured.</returns>
    public string? Reveal( ) => _value;

    /// <inheritdoc />
    public override string ToString( ) => DebuggerDisplay;
}

/// <summary>Runtime secret values loaded from encrypted database storage.</summary>
public sealed record ApplicationSettingsSecrets {
    /// <summary>Gets the Apple Music private signing key.</summary>
    public ApplicationSecretValue ApplePrivateKey { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the Spotify client secret.</summary>
    public ApplicationSecretValue SpotifyClientSecret { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the Tidal client secret.</summary>
    public ApplicationSecretValue TidalClientSecret { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the Discord bot token.</summary>
    public ApplicationSecretValue DiscordToken { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the ATProto service-account password.</summary>
    public ApplicationSecretValue ATProtoPassword { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the ATProto OAuth private signing JWK.</summary>
    public ApplicationSecretValue ATProtoOAuthSigningKey { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the salt used when hashing user API keys.</summary>
    public ApplicationSecretValue ApiKeySalt { get; init; } = ApplicationSecretValue.FromPlaintext( null );

    /// <summary>Gets the shared credential used for internal service authentication.</summary>
    public ApplicationSecretValue InternalServiceKey { get; init; } = ApplicationSecretValue.FromPlaintext( null );
}

/// <summary>Configured/not-configured status for every sensitive application setting.</summary>
public sealed record ApplicationSettingsSecretStatus {
    /// <summary>Gets a value indicating whether the Apple private key is configured.</summary>
    public bool ApplePrivateKeyConfigured { get; init; }

    /// <summary>Gets a value indicating whether the Spotify client secret is configured.</summary>
    public bool SpotifyClientSecretConfigured { get; init; }

    /// <summary>Gets a value indicating whether the Tidal client secret is configured.</summary>
    public bool TidalClientSecretConfigured { get; init; }

    /// <summary>Gets a value indicating whether the Discord token is configured.</summary>
    public bool DiscordTokenConfigured { get; init; }

    /// <summary>Gets a value indicating whether the ATProto password is configured.</summary>
    public bool ATProtoPasswordConfigured { get; init; }

    /// <summary>Gets a value indicating whether the ATProto OAuth signing key is configured.</summary>
    public bool ATProtoOAuthSigningKeyConfigured { get; init; }

    /// <summary>Gets a value indicating whether the API-key salt is configured.</summary>
    public bool ApiKeySaltConfigured { get; init; }

    /// <summary>Gets a value indicating whether the internal-service key is configured.</summary>
    public bool InternalServiceKeyConfigured { get; init; }
}

/// <summary>
/// A three-state secret update. Its string and JSON representations omit replacement plaintext.
/// </summary>
[DebuggerDisplay( "{Mode}" )]
public sealed class ApplicationSecretUpdate {
    private readonly string? _replacement;

    private ApplicationSecretUpdate( SecretUpdateMode mode, string? replacement = null ) {
        Mode = mode;
        _replacement = replacement;
    }

    /// <summary>Gets the requested update operation.</summary>
    public SecretUpdateMode Mode { get; }

    /// <summary>Creates an update that preserves the current secret.</summary>
    public static ApplicationSecretUpdate Keep( ) => new( SecretUpdateMode.Keep );

    /// <summary>Creates an update that deliberately removes the current secret.</summary>
    public static ApplicationSecretUpdate Remove( ) => new( SecretUpdateMode.Remove );

    /// <summary>Creates an update that replaces the current secret.</summary>
    /// <param name="replacement">The non-empty replacement plaintext.</param>
    /// <returns>A replacement update.</returns>
    /// <exception cref="ArgumentException">Thrown when the replacement is blank.</exception>
    public static ApplicationSecretUpdate Replace( string replacement ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( replacement );
        return new( SecretUpdateMode.Replace, replacement );
    }

    /// <summary>Returns replacement plaintext to the settings service, if this is a replacement.</summary>
    /// <returns>The replacement plaintext, or <see langword="null"/> for keep/remove operations.</returns>
    public string? RevealReplacement( ) => _replacement;

    /// <inheritdoc />
    public override string ToString( ) => Mode.ToString( );
}

/// <summary>Three-state updates for all sensitive application settings.</summary>
public sealed record ApplicationSettingsSecretUpdates {
    /// <summary>Gets the Apple private-key update.</summary>
    public ApplicationSecretUpdate ApplePrivateKey { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the Spotify client-secret update.</summary>
    public ApplicationSecretUpdate SpotifyClientSecret { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the Tidal client-secret update.</summary>
    public ApplicationSecretUpdate TidalClientSecret { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the Discord-token update.</summary>
    public ApplicationSecretUpdate DiscordToken { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the ATProto-password update.</summary>
    public ApplicationSecretUpdate ATProtoPassword { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the ATProto OAuth signing-key update.</summary>
    public ApplicationSecretUpdate ATProtoOAuthSigningKey { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the API-key salt update.</summary>
    public ApplicationSecretUpdate ApiKeySalt { get; init; } = ApplicationSecretUpdate.Keep( );

    /// <summary>Gets the internal-service key update.</summary>
    public ApplicationSecretUpdate InternalServiceKey { get; init; } = ApplicationSecretUpdate.Keep( );
}

/// <summary>A validated application-settings update request.</summary>
public sealed record ApplicationSettingsUpdate {
    /// <summary>Gets the complete replacement set of non-sensitive values.</summary>
    public required ApplicationSettingsValues Values { get; init; }

    /// <summary>Gets the requested changes to encrypted secret values.</summary>
    public ApplicationSettingsSecretUpdates Secrets { get; init; } = new( );

    /// <summary>
    /// Gets the revision returned by the last read. It must be <see langword="null"/> only when
    /// creating the first settings record.
    /// </summary>
    public string? ExpectedRevision { get; init; }
}

/// <summary>Application settings returned to an authorized runtime consumer.</summary>
/// <param name="Values">The non-sensitive settings.</param>
/// <param name="Secrets">Redacting wrappers around decrypted secret values.</param>
/// <param name="Revision">The optimistic-concurrency revision.</param>
/// <param name="UpdatedAtUtc">The time at which the aggregate was last updated.</param>
public sealed record ApplicationSettingsSnapshot(
    ApplicationSettingsValues Values,
    ApplicationSettingsSecrets Secrets,
    string Revision,
    DateTimeOffset UpdatedAtUtc
);

/// <summary>Application settings safe for ordinary display and serialization.</summary>
/// <param name="Values">The non-sensitive settings.</param>
/// <param name="Secrets">Configured/not-configured secret status.</param>
/// <param name="Revision">The optimistic-concurrency revision.</param>
/// <param name="UpdatedAtUtc">The time at which the aggregate was last updated.</param>
public sealed record ApplicationSettingsStatus(
    ApplicationSettingsValues Values,
    ApplicationSettingsSecretStatus Secrets,
    string Revision,
    DateTimeOffset UpdatedAtUtc
);
