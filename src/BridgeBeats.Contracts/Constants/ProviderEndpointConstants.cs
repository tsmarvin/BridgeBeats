namespace BridgeBeats.Contracts.Constants;

/// <summary>
/// Reserved endpoint keys and resource-family names shared by provider workers, queue processors,
/// and Redis state. Provider-specific derivation may append bounded path-shape components.
/// </summary>
public static class ProviderEndpointConstants {
    /// <summary>Spotify OAuth authority host.</summary>
    public const string SpotifyAuthHost = "accounts.spotify.com";
    /// <summary>Tidal OAuth authority host.</summary>
    public const string TidalAuthHost = "auth.tidal.com";
    /// <summary>Fallback used when an endpoint cannot be derived from a provider request.</summary>
    public const string Unknown = "unknown";
    /// <summary>Provider token-acquisition endpoint.</summary>
    public const string AuthToken = "auth/token";
    /// <summary>Provider-wide data API cooldown.</summary>
    public const string ProviderWide = "provider";
    /// <summary>Track lookup endpoint family.</summary>
    public const string Tracks = "tracks";
    /// <summary>Album lookup endpoint family.</summary>
    public const string Albums = "albums";
    /// <summary>Artist lookup endpoint family.</summary>
    public const string Artists = "artists";

    /// <summary>
    /// Normalizes the bounded endpoint vocabulary used by bulk and single-item provider paths.
    /// Provider-specific granularity is applied by the Core policy.
    /// </summary>
    public static string Normalize( string endpoint ) {
        string normalized = endpoint.Trim( );
        if (normalized.Equals( "BulkTracks", StringComparison.OrdinalIgnoreCase )) {
            return Tracks;
        }
        if (normalized.Equals( "BulkAlbums", StringComparison.OrdinalIgnoreCase )) {
            return Albums;
        }
        if (normalized.Equals( "BulkArtists", StringComparison.OrdinalIgnoreCase )) {
            return Artists;
        }
        return normalized.ToLowerInvariant( );
    }

    /// <summary>
    /// Chooses a reported endpoint when it is concrete; otherwise normalizes the supplied fallback.
    /// </summary>
    public static string ResolveEffective( string? reportedEndpoint, string? fallbackEndpoint ) =>
        Normalize( !string.IsNullOrWhiteSpace( reportedEndpoint )
            && !reportedEndpoint.Equals( Unknown, StringComparison.OrdinalIgnoreCase )
                ? reportedEndpoint
                : fallbackEndpoint ?? Unknown );
}
