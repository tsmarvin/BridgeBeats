using System.Diagnostics;
using System.Text.Json.Serialization;

namespace BridgeBeats.Core.Infrastructure.Settings;

/// <summary>
/// Singleton persistence record for the versioned application-settings aggregate. The settings
/// service protects the secret document before assigning <see cref="ProtectedSecrets"/>.
/// </summary>
internal sealed class ApplicationSettingsRecord {
    internal const int SingletonId = 1;
    internal const int CurrentSchemaVersion = 1;
    internal const string CurrentSecretsProtectionScheme = "aspnet-data-protection:v1";

    public int Id { get; set; } = SingletonId;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string ValuesJson { get; set; } = string.Empty;

    public string SecretsProtectionScheme { get; set; } = CurrentSecretsProtectionScheme;

    public string ProtectedSecrets { get; set; } = string.Empty;

    public string Revision { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Plaintext secret document used only between Data Protection and typed runtime wrappers.</summary>
[DebuggerDisplay( "[REDACTED APPLICATION SETTINGS SECRETS]" )]
internal sealed record StoredApplicationSettingsSecrets {
    [JsonPropertyName( "applePrivateKey" )]
    public string? ApplePrivateKey { get; init; }

    [JsonPropertyName( "spotifyClientSecret" )]
    public string? SpotifyClientSecret { get; init; }

    [JsonPropertyName( "tidalClientSecret" )]
    public string? TidalClientSecret { get; init; }

    [JsonPropertyName( "discordToken" )]
    public string? DiscordToken { get; init; }

    [JsonPropertyName( "atProtoPassword" )]
    public string? ATProtoPassword { get; init; }

    [JsonPropertyName( "atProtoOAuthSigningKey" )]
    public string? ATProtoOAuthSigningKey { get; init; }

    [JsonPropertyName( "apiKeySalt" )]
    public string? ApiKeySalt { get; init; }

    [JsonPropertyName( "internalServiceKey" )]
    public string? InternalServiceKey { get; init; }

    public override string ToString( ) => "[REDACTED APPLICATION SETTINGS SECRETS]";
}
