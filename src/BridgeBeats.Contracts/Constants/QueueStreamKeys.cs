using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Constants;

/// <summary>Canonical derivation for Redis queue stream and work-signal keys.</summary>
public static class QueueStreamKeys {
    /// <summary>Returns the provider stream for a priority lane and optional isolated key prefix.</summary>
    public static string For(
        SupportedProviders provider,
        QueuePriority priority,
        string? keyPrefix = null
    ) => $"{ProviderPrefix( provider, keyPrefix )}:{priority.ToString( ).ToLowerInvariant( )}";

    /// <summary>Returns the provider dead-letter stream.</summary>
    public static string DlqFor( SupportedProviders provider, string? keyPrefix = null ) =>
        $"{ProviderPrefix( provider, keyPrefix )}:dlq";

    /// <summary>Returns the provider's generic work-signal channel.</summary>
    public static string WorkSignalFor( SupportedProviders provider, string? keyPrefix = null ) =>
        $"{ProviderPrefix( provider, keyPrefix )}:work";

    /// <summary>Returns a Spotify type-specific bulk stream.</summary>
    public static string SpotifyBulkFor( bool isTrack, string? keyPrefix = null ) =>
        $"{ProviderPrefix( SupportedProviders.Spotify, keyPrefix )}:bulk:{(isTrack ? "track-id" : "album-id")}";

    /// <summary>Returns the Spotify bulk processor's work-signal channel.</summary>
    public static string SpotifyBulkWorkSignal( string? keyPrefix = null ) =>
        $"{ProviderPrefix( SupportedProviders.Spotify, keyPrefix )}:bulk:work";

    private static string ProviderPrefix( SupportedProviders provider, string? keyPrefix ) {
        string providerName = provider.ToString( ).ToLowerInvariant( );
        return string.IsNullOrWhiteSpace( keyPrefix )
            ? $"queue:{providerName}"
            : $"queue:{keyPrefix}:{providerName}";
    }
}
