using System.Text.RegularExpressions;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Domain.Utilities;

namespace BridgeBeats.Providers.Tidal {

    /// <summary>
    /// Parses Tidal share/listen URLs and builds Tidal API request URIs from templates.
    /// </summary>
    /// <remarks>
    /// Provides the URL-to-entity parsing used when a lookup starts from a Tidal link,
    /// plus the set of relative request-URI builders the Tidal API client uses for
    /// search, ISRC/UPC filtering, relationship traversal, and by-id fetches. The URL
    /// match pattern is compiled from a source-generated regular expression. A
    /// provider-agnostic id extractor also exists in
    /// <see cref="BridgeBeats.Core.Domain.Utilities.ProviderUrlParser"/>; the two carry
    /// the same Tidal track/album pattern.
    /// </remarks>
    public static partial class TidalLinkParser {

        /// <summary>
        /// Attempts to parse a Tidal track or album URL into its entity kind and id.
        /// </summary>
        /// <param name="link">The candidate Tidal URL.</param>
        /// <param name="kind">
        /// When this method returns, the parsed entity kind, or
        /// <see cref="TidalEntity.Unknown"/> when the URL does not match.
        /// </param>
        /// <param name="id">
        /// When this method returns, the parsed numeric entity id, or an empty string
        /// when the URL does not match.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the URL parses to a track or album with a
        /// non-empty id; otherwise <see langword="false"/>. The match pattern accepts
        /// track and album links only, so artist links yield <see langword="false"/>.
        /// </returns>
        public static bool TryParseUri(
            string link,
            out TidalEntity kind,
            out string id
        ) {
            kind = TidalEntity.Unknown;
            id = string.Empty;

            if (s_tidalLink.IsMatch( link )) {
                Match tidalMatch = s_tidalLink.Match(link);

                if (tidalMatch.Groups.ContainsKey( "type" )) {
                    kind = tidalMatch.Groups["type"].Value switch {
                        "track" => TidalEntity.Track,
                        "album" => TidalEntity.Album,
                        "artist" => TidalEntity.Artist,
                        _ => TidalEntity.Unknown,
                    };
                }

                if (tidalMatch.Groups.ContainsKey( "id" )) {
                    id = tidalMatch.Groups["id"].Value;
                }
            }

            return (kind == TidalEntity.Track || kind == TidalEntity.Album) && !string.IsNullOrEmpty( id );
        }

        /// <summary>
        /// Compiled regular expression matching Tidal track and album URLs, capturing
        /// the entity type and numeric id.
        /// </summary>
        private static readonly Regex s_tidalLink = TidalMusicLink();

        /// <summary>
        /// Source-generated factory for the Tidal track/album URL regular expression.
        /// </summary>
        /// <returns>
        /// A compiled, case-insensitive regex with named <c>type</c> and <c>id</c>
        /// capture groups.
        /// </returns>
        [GeneratedRegex( @"(?:(?:listen\.)?tidal\.com/)(?:browse/)?(?<type>track|album)/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled )]
        private static partial Regex TidalMusicLink( );

        /// <summary>
        /// Extracts the Tidal entity id from a URL using the shared provider-agnostic
        /// URL parser.
        /// </summary>
        /// <param name="url">The Tidal URL to extract an id from.</param>
        /// <returns>
        /// The extracted id, or <see langword="null"/> when none is found. Delegates to
        /// <see cref="BridgeBeats.Core.Domain.Utilities.ProviderUrlParser.ExtractTidalId(string)"/>.
        /// </returns>
        public static string? ExtractId( string url )
            => ProviderUrlParser.ExtractTidalId( url );

        /// <summary>
        /// Builds the relative Tidal API URI that searches for an artist by name.
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the search to.</param>
        /// <param name="artist">The artist name to search for; URL-encoded into the path.</param>
        /// <returns>A relative request URI for the artist search endpoint.</returns>
        public static string GetArtistSearchUri( string storefront, string artist )
            => ArtistSearchURI
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{artist}", EscapeRequired( artist, nameof( artist ) ) );

        /// <summary>
        /// Builds the relative Tidal API URI for an artist's tracks relationship.
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the request to.</param>
        /// <param name="artistId">The artist id whose tracks are requested.</param>
        /// <returns>A relative request URI for the artist-tracks relationship endpoint.</returns>
        public static string GetArtistTracksUri( string storefront, string artistId )
            => ArtistTrackRelationshipsUri
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{artistId}", ValidateNumericId( artistId, nameof( artistId ) ) );

        /// <summary>
        /// Builds the relative Tidal API URI for an artist's albums relationship.
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the request to.</param>
        /// <param name="artistId">The artist id whose albums are requested.</param>
        /// <returns>A relative request URI for the artist-albums relationship endpoint.</returns>
        public static string GetArtistAlbumsUri( string storefront, string artistId )
            => ArtistAlbumRelationshipsUri
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{artistId}", ValidateNumericId( artistId, nameof( artistId ) ) );

        /// <summary>
        /// Builds the relative Tidal API URI that filters tracks by ISRC.
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the request to.</param>
        /// <param name="isrc">The ISRC to filter tracks by.</param>
        /// <returns>A relative request URI for the ISRC-filtered tracks endpoint.</returns>
        public static string GetTracksIsrcURI( string storefront, string isrc )
            => TracksIsrcURI
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{isrc}", EscapeRequired( isrc, nameof( isrc ) ) );

        /// <summary>
        /// Builds the relative Tidal API URI that filters albums by UPC (barcode id).
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the request to.</param>
        /// <param name="upc">The UPC/barcode id to filter albums by.</param>
        /// <returns>A relative request URI for the UPC-filtered albums endpoint.</returns>
        public static string GetAlbumUpcURI( string storefront, string upc )
            => AlbumsUpcURI
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{upc}", EscapeRequired( upc, nameof( upc ) ) );

        /// <summary>
        /// Builds the relative Tidal API URI that fetches an album by id.
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the request to.</param>
        /// <param name="albumId">The album id to fetch.</param>
        /// <returns>A relative request URI for the album-by-id endpoint.</returns>
        public static string GetAlbumIdURI( string storefront, string albumId )
            => AlbumIdUri
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{albumId}", ValidateNumericId( albumId, nameof( albumId ) ) );

        /// <summary>
        /// Builds the relative Tidal API URI that fetches a track by id.
        /// </summary>
        /// <param name="storefront">The Tidal country code (storefront) to scope the request to.</param>
        /// <param name="trackId">The track id to fetch.</param>
        /// <returns>A relative request URI for the track-by-id endpoint.</returns>
        public static string GetTrackIdURI( string storefront, string trackId )
            => TrackIdUri
                .Replace( "{storefront}", ValidateStorefront( storefront ) )
                .Replace( "{trackId}", ValidateNumericId( trackId, nameof( trackId ) ) );

        private static string ValidateStorefront( string storefront ) {
            ArgumentException.ThrowIfNullOrWhiteSpace( storefront );
            string normalized = storefront.Trim( ).ToUpperInvariant( );
            return normalized.Length == 2 && normalized.All( char.IsAsciiLetter )
                ? normalized
                : throw new ArgumentException(
                    "Tidal storefront must be a two-letter ASCII country code.",
                    nameof( storefront ) );
        }

        private static string ValidateNumericId( string value, string parameterName ) {
            ArgumentException.ThrowIfNullOrWhiteSpace( value, parameterName );
            string normalized = value.Trim( );
            return normalized.All( char.IsAsciiDigit )
                ? normalized
                : throw new ArgumentException( "Tidal entity ids must contain only digits.", parameterName );
        }

        private static string EscapeRequired( string value, string parameterName ) {
            ArgumentException.ThrowIfNullOrWhiteSpace( value, parameterName );
            return Uri.EscapeDataString( value.Trim( ) );
        }

        /// <summary>Template for the ISRC-filtered tracks request, side-loading albums, artists, and genres.</summary>
        private const string TracksIsrcURI = "tracks?filter%5Bisrc%5D={isrc}&countryCode={storefront}&include=albums,artists,genres";

        /// <summary>Template for the UPC-filtered albums request, side-loading artists and cover art.</summary>
        private const string AlbumsUpcURI = "albums?filter%5BbarcodeId%5D={upc}&countryCode={storefront}&include=artists,coverArt";

        /// <summary>Template for the artist search request, side-loading matching artist resources.</summary>
        private const string ArtistSearchURI = "searchResults/{artist}?countryCode={storefront}&explicitFilter=include&include=artists";

        /// <summary>Template for the artist-albums relationship request, side-loading album resources.</summary>
        private const string ArtistAlbumRelationshipsUri = "artists/{artistId}/relationships/albums?countryCode={storefront}&include=albums";

        /// <summary>Template for the artist-tracks relationship request, fingerprint-collapsed and side-loading track resources.</summary>
        private const string ArtistTrackRelationshipsUri = "artists/{artistId}/relationships/tracks?countryCode={storefront}&collapseBy=FINGERPRINT&include=tracks";

        /// <summary>Template for the track-by-id request, side-loading albums, artists, and genres.</summary>
        private const string TrackIdUri = "tracks/{trackId}?countryCode={storefront}&include=albums,artists,genres";

        /// <summary>Template for the album-by-id request, side-loading artists and cover art.</summary>
        private const string AlbumIdUri = "albums/{albumId}?countryCode={storefront}&include=artists,coverArt";

    }
}
