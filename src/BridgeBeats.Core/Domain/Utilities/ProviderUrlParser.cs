using System.Text.RegularExpressions;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Core.Domain.Utilities;

/// <summary>
/// Provider-agnostic helper that pulls the entity id out of an Apple Music, Spotify, or Tidal link.
/// Used by both the Infrastructure layer (cache) and the per-provider link parsers under
/// <c>Providers/</c>, which delegate their own <c>ExtractId</c> calls here. The regular expressions
/// here duplicate the URL patterns those per-provider parsers carry; keep the patterns aligned when a
/// provider changes its URL shape.
/// </summary>
public static partial class ProviderUrlParser {

    // Regex patterns for URL ID extraction
    /// <summary>Compiled instance of <see cref="AppleMusicSongIdRegex"/> matching the Apple Music <c>?i=</c> track selector.</summary>
    private static readonly Regex s_appleMusicSongIdRegex = AppleMusicSongIdRegex( );

    /// <summary>Compiled instance of <see cref="AppleMusicLinkRegex"/> matching a <c>music.apple.com</c> path.</summary>
    private static readonly Regex s_appleMusicLinkRegex = AppleMusicLinkRegex( );

    /// <summary>Compiled instance of <see cref="SpotifyLinkRegex"/> matching an <c>open.spotify.com</c> link.</summary>
    private static readonly Regex s_spotifyLinkRegex = SpotifyLinkRegex( );

    /// <summary>Compiled instance of <see cref="TidalLinkRegex"/> matching a <c>tidal.com</c> link.</summary>
    private static readonly Regex s_tidalLinkRegex = TidalLinkRegex( );

    /// <summary>
    /// Source-generated regex matching the Apple Music <c>?i=&lt;songId&gt;</c> query selector, which
    /// names the individual track within an album link. Captures the selector value as group
    /// <c>songId</c>.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex( @"\?i\=(?<songId>[^&#]*)" )]
    private static partial Regex AppleMusicSongIdRegex( );

    /// <summary>
    /// Source-generated regex matching a <c>music.apple.com</c> link (case-insensitive host) and
    /// capturing the path-and-query that follows the host as group <c>URI</c>.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex( @"[Mm][Uu][Ss][Ii][Cc]\.[Aa][Pp][Pp][Ll][Ee]\.[Cc][Oo][Mm]/(?<URI>[_\w\d\/\=\?\.\:\-%&]*)" )]
    private static partial Regex AppleMusicLinkRegex( );

    /// <summary>
    /// Source-generated regex matching an <c>open.spotify.com</c> link, capturing the entity kind
    /// (<c>track</c>, <c>album</c>, or <c>prerelease</c>) as group <c>type</c> and the base-62 id as
    /// group <c>id</c>.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex( @"(?:open\.spotify\.com/)(?<type>track|album|prerelease)/(?<id>[A-Za-z0-9]+)", RegexOptions.IgnoreCase )]
    private static partial Regex SpotifyLinkRegex( );

    /// <summary>
    /// Source-generated regex matching a <c>tidal.com</c> (or <c>listen.tidal.com</c>, with an
    /// optional <c>browse/</c> segment) link, capturing the entity kind (<c>track</c> or
    /// <c>album</c>) as group <c>type</c> and the numeric id as group <c>id</c>.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex( @"(?:(?:listen\.)?tidal\.com/)(?:browse/)?(?<type>track|album)/(?<id>\d+)", RegexOptions.IgnoreCase )]
    private static partial Regex TidalLinkRegex( );

    /// <summary>
    /// Extracts the provider entity id from a link, dispatching to the parser for the requested
    /// <paramref name="provider"/>.
    /// </summary>
    /// <param name="provider">The provider whose link format <paramref name="url"/> is expected to use.</param>
    /// <param name="url">The link to parse.</param>
    /// <returns>
    /// The extracted id, or <see langword="null"/> when <paramref name="url"/> is blank, the
    /// <paramref name="provider"/> is unsupported, or no id could be matched.
    /// </returns>
    public static string? ExtractId( SupportedProviders provider, string url ) {
        return string.IsNullOrWhiteSpace( url )
            ? null
            : provider switch {
                SupportedProviders.AppleMusic => ExtractAppleMusicId( url ),
                SupportedProviders.Spotify => ExtractSpotifyId( url ),
                SupportedProviders.Tidal => ExtractTidalId( url ),
                _ => null
            };
    }

    /// <summary>
    /// Extracts a catalog storefront from provider URLs that encode one. Apple Music stores the
    /// storefront as the first path segment; Spotify and Tidal URLs do not carry equivalent data.
    /// </summary>
    /// <param name="provider">The provider whose URL is being inspected.</param>
    /// <param name="url">The provider URL.</param>
    /// <returns>The normalized lowercase storefront, or <see langword="null"/> when absent.</returns>
    public static string? ExtractStorefront( SupportedProviders provider, string url ) {
        if (provider != SupportedProviders.AppleMusic
            || !Uri.TryCreate( url, UriKind.Absolute, out Uri? uri )
            || !uri.Host.Equals( "music.apple.com", StringComparison.OrdinalIgnoreCase )) {
            return null;
        }

        string? storefront = uri.Segments
            .Select( segment => segment.Trim( '/' ) )
            .FirstOrDefault( segment => !string.IsNullOrWhiteSpace( segment ) );

        return storefront is { Length: >= 2 and <= 3 }
            && storefront.All( char.IsAsciiLetter )
                ? storefront.ToLowerInvariant( )
                : null;
    }

    /// <summary>
    /// Extracts the entity id from an Apple Music link. A <c>?i=</c> track selector takes precedence
    /// (it identifies the specific track inside an album link); otherwise the last path segment of
    /// the <c>music.apple.com</c> URL is used, stripped of any trailing query string.
    /// </summary>
    /// <param name="url">The Apple Music link to parse.</param>
    /// <returns>
    /// The track or album id, or <see langword="null"/> when <paramref name="url"/> is blank or no id
    /// could be matched. Any parsing exception is swallowed and yields <see langword="null"/>.
    /// </returns>
    public static string? ExtractAppleMusicId( string url ) {
        if (string.IsNullOrWhiteSpace( url )) {
            return null;
        }

        try {
            // Check for ?i= query parameter (song ID in album URL)
            Match songIdMatch = s_appleMusicSongIdRegex.Match( url );
            if (songIdMatch.Success) {
                string songId = songIdMatch.Groups["songId"].Value;
                if (!string.IsNullOrWhiteSpace( songId )) {
                    return songId;
                }
            }

            // Try to parse as regular Apple Music URL
            Match match = s_appleMusicLinkRegex.Match( url );
            if (match.Success) {
                string? uri = match.Groups["URI"].Value;
                if (!string.IsNullOrWhiteSpace( uri )) {
                    // Extract ID from URI (last segment before query params)
                    string id = uri.Split( '/' ).Last( ).Split( '?' )[0];
                    if (!string.IsNullOrWhiteSpace( id )) {
                        return id;
                    }
                }
            }
        } catch {
            // Return null on any parsing error
        }

        return null;
    }

    /// <summary>
    /// Extracts the entity id from an <c>open.spotify.com</c> track, album, or prerelease link.
    /// </summary>
    /// <param name="url">The Spotify link to parse.</param>
    /// <returns>
    /// The captured base-62 id, or <see langword="null"/> when <paramref name="url"/> is blank or no
    /// id could be matched. Any parsing exception is swallowed and yields <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Synchronous and regex-only: it handles direct <c>open.spotify.com</c> links and does not
    /// resolve <c>spotify.link</c> short URLs.
    /// </remarks>
    public static string? ExtractSpotifyId( string url ) {
        if (string.IsNullOrWhiteSpace( url )) {
            return null;
        }

        try {
            Match match = s_spotifyLinkRegex.Match( url );
            if (match.Success) {
                string id = match.Groups["id"].Value;
                if (!string.IsNullOrWhiteSpace( id )) {
                    return id;
                }
            }
        } catch {
            // Return null on any parsing error
        }

        return null;
    }

    /// <summary>
    /// Extracts the numeric entity id from a <c>tidal.com</c> track or album link. The id is returned
    /// only when the captured entity kind is <c>track</c> or <c>album</c>.
    /// </summary>
    /// <param name="url">The Tidal link to parse.</param>
    /// <returns>
    /// The numeric id, or <see langword="null"/> when <paramref name="url"/> is blank, the entity kind
    /// is not a track or album, or no id could be matched. Any parsing exception is swallowed and
    /// yields <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// Handles both <c>tidal.com</c> and <c>listen.tidal.com</c> links. Artist ids are not returned.
    /// </remarks>
    public static string? ExtractTidalId( string url ) {
        if (string.IsNullOrWhiteSpace( url )) {
            return null;
        }

        try {
            Match match = s_tidalLinkRegex.Match( url );
            if (match.Success) {
                string type = match.Groups["type"].Value;

                // Only extract IDs for tracks and albums
                if (type.Equals( "track", StringComparison.OrdinalIgnoreCase ) ||
                    type.Equals( "album", StringComparison.OrdinalIgnoreCase )) {
                    string id = match.Groups["id"].Value;
                    if (!string.IsNullOrWhiteSpace( id )) {
                        return id;
                    }
                }
            }
        } catch {
            // Return null on any parsing error
        }

        return null;
    }
}
