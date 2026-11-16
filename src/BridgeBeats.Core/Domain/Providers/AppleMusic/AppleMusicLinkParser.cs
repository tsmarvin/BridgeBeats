using System.Text.RegularExpressions;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Utilities;

namespace BridgeBeats.Providers.AppleMusic {

    /// <summary>
    /// Parses Apple Music links and builds Apple Music API request URIs. Handles <c>music.apple.com</c>
    /// album and song URLs, extracting the storefront, entity identifier, and entity type.
    /// </summary>
    /// <remarks>
    /// The parser recognizes <c>music.apple.com</c> album and song URLs, extracts the storefront and entity
    /// identifier, and produces the relative API paths used by
    /// <see cref="BridgeBeats.Core.Domain.Providers.AppleMusic.AppleMusicLookupService"/>. It also
    /// exposes builders for the search, ISRC, UPC, and artist-catalog endpoints. An album URL carrying an
    /// <c>?i=</c> selector resolves to the individual song within the album. Matching is performed with
    /// compiled, source-generated regular expressions.
    /// </remarks>
    public static partial class AppleMusicLinkParser {

        /// <summary>
        /// Attempts to parse an Apple Music link into the API request URI for the referenced entity. Handles
        /// both album and song URLs, including album URLs with track-specific query parameters.
        /// </summary>
        /// <param name="link">The candidate Apple Music URL.</param>
        /// <param name="requestUri">
        /// When this method returns <see langword="true"/>, the relative Apple Music API path for the album
        /// or song; otherwise an empty string.
        /// </param>
        /// <param name="storefront">
        /// When this method returns <see langword="true"/>, the storefront (country) segment taken from the
        /// link; otherwise an empty string. This is an ISO 3166-1 alpha-2 country code used for
        /// market-specific catalog queries.
        /// </param>
        /// <param name="isAlbum">
        /// When this method returns <see langword="true"/>, indicates whether the link points to an album
        /// (<see langword="true"/>) or a song (<see langword="false"/>). A link carrying an <c>?i=</c>
        /// selector resolves to the individual song within the album.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the link is a recognized Apple Music album or song URL; otherwise
        /// <see langword="false"/>.
        /// </returns>
        public static bool TryParseUri(
            string link,
            out string requestUri,
            out string storefront,
            out bool isAlbum
        ) {
            requestUri = string.Empty;
            storefront = string.Empty;
            isAlbum = false;
            if (s_appleLink.IsMatch( link )) {
                string? uri = s_appleLink.GetGroupValues(link, "URI").FirstOrDefault();

                if (uri != null && (s_validAlbum.IsMatch( uri ) || s_validSong.IsMatch( uri ))) {
                    string candidateStorefront = GetUriStoreFront( uri );
                    if (!s_validStorefront.IsMatch( candidateStorefront )) {
                        return false;
                    }

                    string id = GetUriId(uri);
                    string albumSongId = GetSongId(uri);

                    if (string.IsNullOrWhiteSpace( albumSongId )) {
                        if (!s_validCatalogId.IsMatch( id )) {
                            return false;
                        }
                        storefront = candidateStorefront;
                        requestUri = GetAlbumsURI( storefront, id );
                        isAlbum = true;
                    } else {
                        if (!s_validCatalogId.IsMatch( albumSongId )) {
                            return false;
                        }
                        storefront = candidateStorefront;
                        requestUri = GetSongsURI( storefront, albumSongId );
                    }
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Extracts the Apple Music entity identifier from a URL, delegating to the shared
        /// provider URL parser.
        /// </summary>
        /// <param name="url">The Apple Music URL to inspect.</param>
        /// <returns>The extracted identifier, or <see langword="null"/> if none is found.</returns>
        public static string? ExtractId( string url )
            => ProviderUrlParser.ExtractAppleMusicId( url );

        /// <summary>Builds the Apple Music API path that fetches a song by its catalog identifier.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="songId">The Apple Music song identifier.</param>
        /// <returns>The relative API path for the song.</returns>
        public static string GetSongIdUri( string storefront, string songId )
            => GetSongsURI( storefront, songId );

        /// <summary>Builds the Apple Music API path that fetches an album by its catalog identifier.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="albumId">The Apple Music album identifier.</param>
        /// <returns>The relative API path for the album.</returns>
        public static string GetAlbumIdUri( string storefront, string albumId )
            => GetAlbumsURI( storefront, albumId );

        /// <summary>Builds the Apple Music search API path that finds artists by name.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="artist">The artist name; URL-escaped into the query.</param>
        /// <returns>The relative search API path filtered to artist results.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code.
        /// </exception>
        public static string GetArtistSearchUri( string storefront, string artist ) {
            ValidateStorefront( storefront );
            return ArtistsSearchURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{artist}", Uri.EscapeDataString( artist ) );
        }

        /// <summary>Builds the Apple Music API path that finds songs by ISRC.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="isrc">The International Standard Recording Code to filter on.</param>
        /// <returns>The relative API path filtered by ISRC.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code, or when
        /// <paramref name="isrc"/> contains characters outside the expected alphanumeric set.
        /// </exception>
        public static string GetSongsIsrcURI( string storefront, string isrc ) {
            ValidateStorefront( storefront );
            ValidateIsrc( isrc );
            return SongsIsrcURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{isrc}", Uri.EscapeDataString( isrc ) );
        }

        /// <summary>Builds the Apple Music API path that finds albums by UPC.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="upc">The Universal Product Code to filter on.</param>
        /// <returns>The relative API path filtered by UPC.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code, or when
        /// <paramref name="upc"/> contains characters outside the expected numeric set.
        /// </exception>
        public static string GetAlbumUpcURI( string storefront, string upc ) {
            ValidateStorefront( storefront );
            ValidateUpc( upc );
            return AlbumsUpcURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{upc}", Uri.EscapeDataString( upc ) );
        }

        /// <summary>Builds the Apple Music API path that lists an artist's albums.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="artistId">The Apple Music artist identifier.</param>
        /// <returns>The relative API path for the artist's albums.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code, or when
        /// <paramref name="artistId"/> is not a numeric catalog identifier.
        /// </exception>
        public static string GetArtistAlbumsURI( string storefront, string artistId ) {
            ValidateStorefront( storefront );
            ValidateCatalogId( artistId );
            return ArtistAlbumsURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{artist}", Uri.EscapeDataString( artistId ) );
        }

        /// <summary>Builds the Apple Music API path that lists an artist's songs.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="artistId">The Apple Music artist identifier.</param>
        /// <returns>The relative API path for the artist's songs.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code, or when
        /// <paramref name="artistId"/> is not a numeric catalog identifier.
        /// </exception>
        public static string GetArtistSongsURI( string storefront, string artistId ) {
            ValidateStorefront( storefront );
            ValidateCatalogId( artistId );
            return ArtistSongsURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{artist}", Uri.EscapeDataString( artistId ) );
        }

        /// <summary>Template for the catalog album-by-id endpoint path.</summary>
        private const string AlbumsURI = "{storefront}/albums/{id}";

        /// <summary>Template for the catalog song-by-id endpoint path.</summary>
        private const string SongsURI = "{storefront}/songs/{id}";

        /// <summary>Template for the song search-by-ISRC endpoint path.</summary>
        private const string SongsIsrcURI = "{storefront}/songs?filter[isrc]={isrc}";

        /// <summary>Template for the album search-by-UPC endpoint path.</summary>
        private const string AlbumsUpcURI = "{storefront}/albums?filter[upc]={upc}";

        /// <summary>Template for the artist's-albums endpoint path.</summary>
        private const string ArtistAlbumsURI = "{storefront}/artists/{artist}/albums";

        /// <summary>Template for the artist's-songs endpoint path.</summary>
        private const string ArtistSongsURI = "{storefront}/artists/{artist}/songs";

        /// <summary>Template for the artist search endpoint path.</summary>
        private const string ArtistsSearchURI = "{storefront}/search?types=artists&term={artist}";

        /// <summary>Returns the storefront segment (the first path segment) of a parsed Apple Music URI.</summary>
        /// <param name="uri">The parsed Apple Music URI fragment.</param>
        /// <returns>The storefront (country) segment.</returns>
        private static string GetUriStoreFront( string uri )
            => uri.Split( '/' )[0];

        /// <summary>Returns the trailing identifier segment of a parsed Apple Music URI, dropping any query string.</summary>
        /// <param name="uri">The parsed Apple Music URI fragment.</param>
        /// <returns>The entity identifier.</returns>
        private static string GetUriId( string uri )
            => uri.Split( '/' ).Last( ).Split( '?' )[0];

        /// <summary>Fills the album-by-id template with the given storefront and album identifier.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="id">The album identifier.</param>
        /// <returns>The relative API path for the album.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code, or when
        /// <paramref name="id"/> is not a numeric catalog identifier.
        /// </exception>
        private static string GetAlbumsURI( string storefront, string id ) {
            ValidateStorefront( storefront );
            ValidateCatalogId( id );
            return AlbumsURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{id}", Uri.EscapeDataString( id ) );
        }

        /// <summary>
        /// Extracts the song identifier from a parsed URI, preferring the <c>?i=</c> in-album selector and
        /// falling back to the trailing identifier of a song URL.
        /// </summary>
        /// <param name="uri">The parsed Apple Music URI fragment.</param>
        /// <returns>The song identifier, or an empty string if none is present.</returns>
        private static string GetSongId( string uri )
            => s_albumSongIdRegex.IsMatch( uri )
                ? s_albumSongIdRegex.Match( uri ).Groups["songId"].Value
                : s_validSong.IsMatch( uri )
                    ? s_validSong.Match( uri ).Groups["Identifier"].Value
                    : string.Empty;

        /// <summary>Fills the song-by-id template with the given storefront and song identifier.</summary>
        /// <param name="storefront">The storefront (country) segment.</param>
        /// <param name="id">The song identifier.</param>
        /// <returns>The relative API path for the song.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="storefront"/> is not a 2–3-letter alphabetic code, or when
        /// <paramref name="id"/> is not a numeric catalog identifier.
        /// </exception>
        private static string GetSongsURI( string storefront, string id ) {
            ValidateStorefront( storefront );
            ValidateCatalogId( id );
            return SongsURI
                .Replace( "{storefront}", Uri.EscapeDataString( storefront ) )
                .Replace( "{id}", Uri.EscapeDataString( id ) );
        }

        #region Validation

        /// <summary>
        /// Validates that <paramref name="storefront"/> is a 2–3-letter alphabetic Apple Music storefront code
        /// (ISO 3166-1 alpha-2, or the special-purpose "all" value).
        /// </summary>
        /// <param name="storefront">The storefront segment to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the value does not match the expected pattern.</exception>
        private static void ValidateStorefront( string storefront ) {
            if (!s_validStorefront.IsMatch( storefront )) {
                throw new ArgumentException(
                    "Storefront must be 2–3 alphabetic characters.",
                    nameof( storefront )
                );
            }
        }

        /// <summary>
        /// Validates that <paramref name="id"/> is a purely numeric Apple Music catalog identifier.
        /// </summary>
        /// <param name="id">The catalog identifier to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the value contains non-numeric characters.</exception>
        private static void ValidateCatalogId( string id ) {
            if (!s_validCatalogId.IsMatch( id )) {
                throw new ArgumentException(
                    "Catalog id must contain only numeric digits.",
                    nameof( id )
                );
            }
        }

        /// <summary>
        /// Validates that <paramref name="isrc"/> conforms to a conservative alphanumeric ISRC pattern.
        /// Apple Music strips ISRC hyphens, so the expected form is 4–18 uppercase letters and digits.
        /// The canonical 12-character ISRC (hyphens stripped) fits well within this range; the upper
        /// bound is intentionally permissive to avoid rejecting non-standard registrant codes.
        /// </summary>
        /// <param name="isrc">The ISRC value to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the value does not match the expected pattern.</exception>
        private static void ValidateIsrc( string isrc ) {
            if (!s_validIsrc.IsMatch( isrc )) {
                throw new ArgumentException(
                    "ISRC must be 4–18 alphanumeric characters.",
                    nameof( isrc )
                );
            }
        }

        /// <summary>
        /// Validates that <paramref name="upc"/> is a numeric barcode in the range of standard UPC/EAN lengths
        /// (8–14 digits).
        /// </summary>
        /// <param name="upc">The UPC value to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the value does not match the expected pattern.</exception>
        private static void ValidateUpc( string upc ) {
            if (!s_validUpc.IsMatch( upc )) {
                throw new ArgumentException(
                    "UPC must be 8–14 numeric digits.",
                    nameof( upc )
                );
            }
        }

        #endregion Validation

        #region Regex

        /// <summary>Compiled regex matching a <c>music.apple.com</c> URL and capturing its path in the <c>URI</c> group.</summary>
        private static readonly Regex s_appleLink = AppleMusicLink();

        /// <summary>Source-generated factory for <see cref="s_appleLink"/>.</summary>
        /// <returns>The compiled Apple Music link regex.</returns>
        [GeneratedRegex( @"[Mm][Uu][Ss][Ii][Cc]\.[Aa][Pp][Pp][Ll][Ee]\.[Cc][Oo][Mm]/(?<URI>[_\w\d\/\=\?\.\:\-%&]*)", RegexOptions.Compiled )]
        private static partial Regex AppleMusicLink( );

        /// <summary>Compiled regex capturing the in-album song selector from a <c>?i=</c> query parameter.</summary>
        private static readonly Regex s_albumSongIdRegex = AlbumSongId();

        /// <summary>Source-generated factory for <see cref="s_albumSongIdRegex"/>.</summary>
        /// <returns>The compiled in-album song-id regex.</returns>
        [GeneratedRegex( @"\?i\=(?<songId>[^&#]*)", RegexOptions.Compiled )]
        private static partial Regex AlbumSongId( );

        /// <summary>Compiled regex validating an album URL and capturing its storefront and identifier.</summary>
        private static readonly Regex s_validAlbum = ValidAlbumURI();

        /// <summary>Source-generated factory for <see cref="s_validAlbum"/>.</summary>
        /// <returns>The compiled album-URL regex.</returns>
        [GeneratedRegex( @"(?<StoreFront>\w+)/[Aa][Ll][Bb][Uu][Mm]/(?<Identifier>[^?#]*)", RegexOptions.Compiled )]
        private static partial Regex ValidAlbumURI( );

        /// <summary>Compiled regex validating a song URL and capturing its storefront and identifier.</summary>
        private static readonly Regex s_validSong = ValidSongURI();

        /// <summary>Source-generated factory for <see cref="s_validSong"/>.</summary>
        /// <returns>The compiled song-URL regex.</returns>
        [GeneratedRegex( @"(?<StoreFront>\w+)/[Ss][Oo][Nn][Gg](?:/.*)?/(?<Identifier>[^?#]*)", RegexOptions.Compiled )]
        private static partial Regex ValidSongURI( );

        /// <summary>
        /// Compiled regex that accepts a valid Apple Music storefront: 2–3 alphabetic characters
        /// (ISO 3166-1 alpha-2 country codes, or the special "all" value). Full-string match enforced
        /// by anchors.
        /// </summary>
        private static readonly Regex s_validStorefront = ValidStorefront();

        /// <summary>Source-generated factory for <see cref="s_validStorefront"/>.</summary>
        /// <returns>The compiled storefront validation regex.</returns>
        [GeneratedRegex( @"\A[a-zA-Z]{2,3}\z", RegexOptions.Compiled )]
        private static partial Regex ValidStorefront( );

        /// <summary>
        /// Compiled regex that accepts a valid Apple Music numeric catalog identifier (one or more
        /// decimal digits). Full-string match enforced by anchors.
        /// </summary>
        private static readonly Regex s_validCatalogId = ValidCatalogId();

        /// <summary>Source-generated factory for <see cref="s_validCatalogId"/>.</summary>
        /// <returns>The compiled catalog-id validation regex.</returns>
        [GeneratedRegex( @"\A[0-9]+\z", RegexOptions.Compiled )]
        private static partial Regex ValidCatalogId( );

        /// <summary>
        /// Compiled regex that accepts a conservative ISRC value: 4–18 uppercase or lowercase letters
        /// and digits, with no hyphens (Apple Music strips ISRC hyphens). The canonical 12-character
        /// ISRC fits within this range; the upper bound is intentionally permissive to avoid rejecting
        /// non-standard registrant codes.
        /// </summary>
        private static readonly Regex s_validIsrc = ValidIsrc();

        /// <summary>Source-generated factory for <see cref="s_validIsrc"/>.</summary>
        /// <returns>The compiled ISRC validation regex.</returns>
        [GeneratedRegex( @"\A[A-Za-z0-9]{4,18}\z", RegexOptions.Compiled )]
        private static partial Regex ValidIsrc( );

        /// <summary>
        /// Compiled regex that accepts a numeric barcode in the range of standard UPC/EAN lengths
        /// (EAN-8 through EAN-14, inclusive). Full-string match enforced by anchors.
        /// </summary>
        private static readonly Regex s_validUpc = ValidUpc();

        /// <summary>Source-generated factory for <see cref="s_validUpc"/>.</summary>
        /// <returns>The compiled UPC validation regex.</returns>
        [GeneratedRegex( @"\A[0-9]{8,14}\z", RegexOptions.Compiled )]
        private static partial Regex ValidUpc( );

        #endregion Regex

    }

}
