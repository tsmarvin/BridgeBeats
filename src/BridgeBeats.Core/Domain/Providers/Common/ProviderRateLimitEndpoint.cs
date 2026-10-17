using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Core.Domain.Providers.Common;

/// <summary>
/// Produces stable, low-cardinality keys for provider HTTP endpoints. These keys are shared by
/// the HTTP resilience pipeline, Redis rate-limit tracking, and durable queue deferrals.
/// </summary>
internal static class ProviderRateLimitEndpoint {
    internal const string Unknown = ProviderEndpointConstants.Unknown;

    internal static string FromRequest( SupportedProviders? provider, Uri? requestUri ) {
        if (provider is null || requestUri is null) {
            return Unknown;
        }

        if (requestUri.Host.Equals( ProviderEndpointConstants.SpotifyAuthHost, StringComparison.OrdinalIgnoreCase )
            || requestUri.Host.Equals( ProviderEndpointConstants.TidalAuthHost, StringComparison.OrdinalIgnoreCase )) {
            return ProviderEndpointConstants.AuthToken;
        }

        string[] segments = [.. requestUri.AbsolutePath
            .Split( '/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .Select( segment => Uri.UnescapeDataString( segment ).ToLowerInvariant( ) )];

        return provider.Value switch {
            SupportedProviders.Spotify => SpotifyEndpoint( segments, requestUri.Query ),
            SupportedProviders.AppleMusic => AppleMusicEndpoint( segments ),
            SupportedProviders.Tidal => TidalEndpoint( segments ),
            _ => Unknown
        };
    }

    private static string SpotifyEndpoint( string[] segments, string query ) {
        int searchIndex = Array.IndexOf( segments, "search" );
        if (searchIndex >= 0) {
            string? type = GetQueryValue( query, "type" )?.Trim( ).ToLowerInvariant( );
            return type is "track" or "album" or "artist"
                ? $"search:{type}"
                : "search";
        }

        return NormalizeResourcePath( segments, "tracks", "albums", "artists" );
    }

    private static string AppleMusicEndpoint( string[] segments ) {
        int catalogIndex = Array.IndexOf( segments, "catalog" );
        int resourceIndex = catalogIndex >= 0 ? catalogIndex + 2 : 0;
        if (resourceIndex >= segments.Length) {
            return Unknown;
        }

        return NormalizeResourcePath( segments[resourceIndex..], "songs", "albums", "artists", "search" );
    }

    private static string TidalEndpoint( string[] segments ) {
        int searchIndex = Array.IndexOf( segments, "searchresults" );
        if (searchIndex >= 0) {
            return "search-results";
        }

        return NormalizeResourcePath( segments, "tracks", "albums", "artists" );
    }

    private static string NormalizeResourcePath( string[] segments, params string[] resources ) {
        int resourceIndex = Array.FindIndex(
            segments,
            segment => resources.Contains( segment, StringComparer.OrdinalIgnoreCase ) );
        if (resourceIndex < 0) {
            return Unknown;
        }

        string resource = segments[resourceIndex];
        if (resourceIndex + 1 >= segments.Length) {
            return resource;
        }
        if (resourceIndex + 2 >= segments.Length) {
            return $"{resource}/:id";
        }

        string child = segments[resourceIndex + 2];
        return child is "albums" or "tracks" or "songs" or "artists"
            or "top-tracks" or "related-artists" or "relationships"
                ? $"{resource}/:id/{child}" + NormalizeRelationshipSuffix( segments, resourceIndex + 3 )
                : $"{resource}/:id";
    }

    private static string NormalizeRelationshipSuffix( string[] segments, int nextIndex ) {
        if (nextIndex >= segments.Length) {
            return string.Empty;
        }

        string suffix = segments[nextIndex];
        return suffix is "albums" or "tracks" or "songs" ? $"/{suffix}" : string.Empty;
    }

    private static string? GetQueryValue( string query, string name ) {
        foreach (string part in query.TrimStart( '?' ).Split( '&', StringSplitOptions.RemoveEmptyEntries )) {
            int separator = part.IndexOf( '=' );
            string key = separator >= 0 ? part[..separator] : part;
            if (key.Equals( name, StringComparison.OrdinalIgnoreCase )) {
                return separator >= 0 ? Uri.UnescapeDataString( part[(separator + 1)..] ) : string.Empty;
            }
        }

        return null;
    }
}
