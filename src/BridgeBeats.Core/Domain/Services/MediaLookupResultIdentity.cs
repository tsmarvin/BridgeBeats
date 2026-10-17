using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Core.Domain.Services;

/// <summary>Defines the provider-result identity required for deterministic durable storage.</summary>
internal static class MediaLookupResultIdentity {
    internal static bool IsPersistable( MusicLookupResult result ) =>
        HasUsableExternalId( result )
        || !string.IsNullOrWhiteSpace( result.Title )
        || !string.IsNullOrWhiteSpace( result.Artist );

    internal static bool HasUsableExternalId( MusicLookupResult result ) =>
        result.ExternalId?.Any( char.IsLetterOrDigit ) == true;

    /// <summary>Normalizes an external identifier to the durable record-key character set.</summary>
    internal static string NormalizeExternalId( string? externalId ) =>
        string.IsNullOrWhiteSpace( externalId )
            ? string.Empty
            : new string( [.. externalId.Where(
                character => char.IsLetterOrDigit( character ) || character == '-' )] );

    internal static string? GetExternalKey( MusicLookupResult result ) {
        ArgumentNullException.ThrowIfNull( result );

        string normalizedExternalId = NormalizeExternalId( result.ExternalId );
        if (!normalizedExternalId.Any( char.IsLetterOrDigit )) {
            return null;
        }

        return $"{(result.IsAlbum == true ? "album" : "track")}:{normalizedExternalId}";
    }
}
