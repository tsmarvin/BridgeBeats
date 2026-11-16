using System.Text;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Providers.Tidal.Models;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Providers.Tidal;

namespace BridgeBeats.Core.Domain.Providers.Tidal {

    /// <summary>
    /// Direct Tidal API lookup service: resolves tracks and albums by calling the real
    /// Tidal API and mapping its JSON:API-style responses to
    /// <see cref="MusicLookupResult"/>.
    /// </summary>
    /// <remarks>
    /// This is the in-worker, real-API half of the provider abstraction (the
    /// worker-delegating proxy is <see cref="TidalHttpLookupService"/>). It extends
    /// <see cref="MusicLookupServiceBase"/> and authenticates each request with a
    /// bearer token from <see cref="TidalTokenHandler"/>.
    /// <para>
    /// Tidal returns a primary <c>data</c> resource plus a flat <c>included</c> array
    /// of side-loaded resources. Mapping therefore stitches relationships against
    /// included resources: artist names are resolved by replacing <c>|artistId|</c>
    /// placeholders with the matching included artist's name, and album art for a track
    /// requires a secondary album lookup. Genre names found in a response are written
    /// to the <see cref="IGenreCacheService"/> as a best-effort
    /// side effect.
    /// </para>
    /// </remarks>
    /// <param name="handler">Supplies bearer authorization headers for Tidal API calls.</param>
    /// <param name="factory">Factory used to create the named <c>tidal-api</c> HTTP client.</param>
    /// <param name="logger">Logger for lookup and parse diagnostics.</param>
    /// <param name="serializerOptions">JSON options used to deserialize Tidal responses.</param>
    /// <param name="genreCache">
    /// Genre cache used to store provider-native track genres.
    /// </param>
    public sealed partial class TidalLookupService(
        TidalTokenHandler handler,
        IHttpClientFactory factory,
        ILogger<TidalLookupService> logger,
        JsonSerializerOptions serializerOptions,
        IGenreCacheService genreCache
    ) : MusicLookupServiceBase( logger, serializerOptions ), IStorefrontMusicLookupService {

        /// <summary>
        /// The default Tidal storefront (country code) used to scope API requests.
        /// </summary>
        public const string DefaultStorefront = "US";

        /// <summary>Gets the provider this service resolves for: <see cref="SupportedProviders.Tidal"/>.</summary>
        public override SupportedProviders Provider => SupportedProviders.Tidal;

        /// <summary>
        /// Resolves a track by its ISRC via the Tidal ISRC-filtered tracks endpoint.
        /// </summary>
        /// <param name="isrc">The ISRC to look up.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/>, or <see langword="null"/> when
        /// no matching track is found or the response cannot be parsed.
        /// </returns>
        public override Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc )
            => GetInfoByISRCAsync( isrc, DefaultStorefront );

        /// <inheritdoc/>
        public async Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc, string storefront ) {
            storefront = NormalizeStorefront( storefront );
            return await ParseTidalResponse(
                await NewMusicApiRequest( TidalLinkParser.GetTracksIsrcURI( storefront, isrc ), LookupRequestType.IsrcLookup ),
                LookupRequestType.IsrcLookup,
                TidalEntity.Track,
                null,
                storefront
            );
        }

        /// <summary>
        /// Resolves an album by its UPC via the Tidal UPC-filtered albums endpoint.
        /// </summary>
        /// <param name="upc">The UPC (barcode id) to look up.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/>, or <see langword="null"/> when
        /// no matching album is found or the response cannot be parsed.
        /// </returns>
        public override Task<MusicLookupResult?> GetInfoByUPCAsync( string upc )
            => GetInfoByUPCAsync( upc, DefaultStorefront );

        /// <inheritdoc/>
        public async Task<MusicLookupResult?> GetInfoByUPCAsync( string upc, string storefront ) {
            storefront = NormalizeStorefront( storefront );
            return await ParseTidalResponse(
                await NewMusicApiRequest( TidalLinkParser.GetAlbumUpcURI( storefront, upc ), LookupRequestType.UpcLookup ),
                LookupRequestType.UpcLookup,
                TidalEntity.Album,
                null,
                storefront
            );
        }

        /// <summary>
        /// Resolves a track or album by title and artist.
        /// </summary>
        /// <remarks>
        /// Searches Tidal for the artist, then for each matching artist enumerates that
        /// artist's albums and tries to match a sanitized album title; if no album
        /// matches, it falls back to enumerating that artist's tracks and matching a
        /// sanitized song title. The first match wins.
        /// </remarks>
        /// <param name="title">The track or album title to match.</param>
        /// <param name="artist">The artist name to search for.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/> for the first matching album or
        /// track, or <see langword="null"/> when no artist or title match is found.
        /// </returns>
        public override async Task<MusicLookupResult?> GetInfoAsync( string title, string artist ) {
            List<(string id, string artistName)>? artistResults = ParseTidalArtistList(
                await NewMusicApiRequest(
                    TidalLinkParser.GetArtistSearchUri(DefaultStorefront, artist),
                    LookupRequestType.ArtistLookup
                )
            );
            MusicLookupResult? result = null;

            // Bail out early if we have no results
            if (artistResults == null || artistResults.Count == 0) { return result; }

            string sanitizedAlbumTitle = SanitizeAlbumTitle( title );
            string sanitizedTrackTitle = SanitizeSongTitle( title );
            foreach ((string id, string artistName) in artistResults) {
                result = await ParseArtistAlbums( id, sanitizedAlbumTitle );
                if (result != null) { return result; }

                result = await ParseArtistTracks( id, sanitizedTrackTitle );
                if (result != null) { return result; }
            }

            return null;
        }

        /// <summary>
        /// Resolves a track or album from a Tidal URL.
        /// </summary>
        /// <param name="uri">The Tidal track or album URL.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/> marked as the primary result, or
        /// <see langword="null"/> when the URL does not parse to a track or album or
        /// the response cannot be parsed.
        /// </returns>
        public override async Task<MusicLookupResult?> GetInfoAsync( string uri ) {
            if (TidalLinkParser.TryParseUri( uri, out TidalEntity kind, out string id )) {
                if (kind == TidalEntity.Album) {
                    return await NewAlbumIdLookup( id, true );
                } else if (kind == TidalEntity.Track) {
                    return await NewTrackIdLookup( id, true );
                }
            }
            return null;
        }

        /// <summary>
        /// Resolves a track or album by its Tidal id.
        /// </summary>
        /// <param name="providerId">The Tidal track or album id.</param>
        /// <param name="isAlbum">
        /// <see langword="true"/> to resolve the id as an album; <see langword="false"/>
        /// to resolve it as a track.
        /// </param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/> marked as the primary result, or
        /// <see langword="null"/> when the entity is not found or cannot be parsed.
        /// </returns>
        public override Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum )
            => GetInfoByIDAsync( providerId, isAlbum, DefaultStorefront );

        /// <inheritdoc/>
        public async Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum, string storefront ) {
            storefront = NormalizeStorefront( storefront );
            return isAlbum
                ? await NewAlbumIdLookup( providerId, true, storefront )
                : await NewTrackIdLookup( providerId, true, storefront );
        }

        private static string NormalizeStorefront( string storefront ) {
            string normalized = string.IsNullOrWhiteSpace( storefront )
                ? DefaultStorefront
                : storefront.Trim( ).ToUpperInvariant( );
            return normalized.Length == 2 && normalized.All( char.IsAsciiLetter )
                ? normalized
                : throw new ArgumentException(
                    "Tidal storefront must be a two-letter ASCII country code.",
                    nameof( storefront ) );
        }

        /// <summary>
        /// Fetches an artist's albums and returns the first whose sanitized title
        /// matches the requested album title.
        /// </summary>
        /// <param name="artistId">The artist id whose albums are searched.</param>
        /// <param name="title">The already-sanitized album title to match.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/> for the matching album, or
        /// <see langword="null"/> when no album matches.
        /// </returns>
        private async Task<MusicLookupResult?> ParseArtistAlbums( string artistId, string title ) {
            string? body = await NewMusicApiRequest( TidalLinkParser.GetArtistAlbumsUri( DefaultStorefront, artistId ), LookupRequestType.ArtistAlbumLookup );
            if (body == null) { return null; }

            TidalResponse<TidalResource>? response = JsonSerializer.Deserialize<TidalResponse<TidalResource>>( body, SerializerOptions );
            if (response?.Included != null) {
                MusicLookupResult? albumResult = await ParseIncludedElementList( response.Included, title, true );
                if (albumResult != null) { return albumResult; }
            }
            return null;
        }

        /// <summary>
        /// Fetches an artist's tracks and returns the first whose sanitized title
        /// matches the requested song title.
        /// </summary>
        /// <param name="artistId">The artist id whose tracks are searched.</param>
        /// <param name="title">The already-sanitized song title to match.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/> for the matching track, or
        /// <see langword="null"/> when no track matches.
        /// </returns>
        private async Task<MusicLookupResult?> ParseArtistTracks( string artistId, string title ) {
            string? body = await NewMusicApiRequest( TidalLinkParser.GetArtistTracksUri( DefaultStorefront, artistId ), LookupRequestType.AlbumTrackLookup );
            if (body == null) { return null; }

            TidalResponse<TidalResource>? response = JsonSerializer.Deserialize<TidalResponse<TidalResource>>( body, SerializerOptions );
            if (response?.Included != null) {
                MusicLookupResult? trackResult = await ParseIncludedElementList( response.Included, title, false );
                if (trackResult != null) { return trackResult; }
            }
            return null;
        }

        /// <summary>
        /// Scans a side-loaded <c>included</c> resource list for the first album or
        /// track whose sanitized title matches, then performs a by-id lookup for it.
        /// </summary>
        /// <param name="included">The side-loaded resources to scan.</param>
        /// <param name="title">The already-sanitized title to match.</param>
        /// <param name="isAlbum">
        /// <see langword="true"/> to match album resources; <see langword="false"/> to
        /// match track resources.
        /// </param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/> for the first matching resource
        /// (fetched fresh by id, not marked primary), or <see langword="null"/> when
        /// nothing matches.
        /// </returns>
        private async Task<MusicLookupResult?> ParseIncludedElementList( List<TidalResource> included, string title, bool isAlbum ) {
            foreach (TidalResource item in included) {
                if (!string.IsNullOrWhiteSpace( item.Id )
                    && ((isAlbum && item.Type == "albums") || (!isAlbum && item.Type == "tracks"))
                    && item.Attributes != null
                    && !string.IsNullOrWhiteSpace( item.Attributes.Title )
                    && (isAlbum
                        ? SanitizeAlbumTitle( item.Attributes.Title )
                        : SanitizeSongTitle( item.Attributes.Title )
                       ).Equals( title, StringComparison.InvariantCultureIgnoreCase )
                ) {
                    return isAlbum
                            ? await NewAlbumIdLookup( item.Id, false )
                            : await NewTrackIdLookup( item.Id, false );
                }
            }
            return null;
        }

        /// <summary>
        /// Fetches an album by id from the Tidal API and maps it to a result.
        /// </summary>
        /// <param name="albumId">The Tidal album id to fetch.</param>
        /// <param name="isPrimary">
        /// Whether the mapped result should be marked as the primary lookup result.
        /// </param>
        /// <param name="storefront">The country code used to scope the catalog request.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/>, or <see langword="null"/> when
        /// the album is not found or cannot be parsed.
        /// </returns>
        private async Task<MusicLookupResult?> NewAlbumIdLookup(
            string albumId,
            bool isPrimary,
            string storefront = DefaultStorefront
        ) {
            string? body = await NewMusicApiRequest( TidalLinkParser.GetAlbumIdURI( storefront, albumId ), LookupRequestType.AlbumLookup );
            if (body != null) {
                (TidalResource? data, List<TidalResource>? included) = ExtractSingleResource( body );
                if (data != null && included != null) {
                    return await ParseTidalResponse( data, included, LookupRequestType.AlbumLookup, TidalEntity.Album, isPrimary, storefront );
                }
            }
            return null;
        }

        /// <summary>
        /// Fetches a track by id from the Tidal API and maps it to a result.
        /// </summary>
        /// <param name="trackId">The Tidal track id to fetch.</param>
        /// <param name="isPrimary">
        /// Whether the mapped result should be marked as the primary lookup result.
        /// </param>
        /// <param name="storefront">The country code used to scope the catalog request.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/>, or <see langword="null"/> when
        /// the track is not found or cannot be parsed.
        /// </returns>
        private async Task<MusicLookupResult?> NewTrackIdLookup(
            string trackId,
            bool isPrimary,
            string storefront = DefaultStorefront
        ) {
            string? body = await NewMusicApiRequest( TidalLinkParser.GetTrackIdURI( storefront, trackId ), LookupRequestType.SongLookup );
            if (body != null) {
                (TidalResource? data, List<TidalResource>? included) = ExtractSingleResource( body );
                if (data != null && included != null) {
                    return await ParseTidalResponse( data, included, LookupRequestType.SongLookup, TidalEntity.Track, isPrimary, storefront );
                }
            }
            return null;
        }

        /// <summary>
        /// Creates the Tidal API HTTP client with a bearer authorization header
        /// attached.
        /// </summary>
        /// <returns>
        /// An <see cref="HttpClient"/> for the named <c>tidal-api</c> client,
        /// authenticated with a current Tidal access token.
        /// </returns>
        private protected override async Task<HttpClient> CreateAuthenticatedClientAsync( ) {
            HttpClient client = factory.CreateClient("tidal-api");
            client.DefaultRequestHeaders.Authorization = await handler.NewBearerAuthenticationHeader( );
            return client;
        }

        /// <summary>
        /// Extracts a single primary resource and the side-loaded included resources
        /// from a raw Tidal response body.
        /// </summary>
        /// <remarks>
        /// Handles both response shapes: when <c>data</c> is an array (search/filter
        /// results) the first element is taken as the primary resource; when <c>data</c>
        /// is a single object it is taken directly. Parse failures are logged and
        /// produce an empty result rather than throwing.
        /// </remarks>
        /// <param name="body">The raw JSON response body.</param>
        /// <returns>
        /// A tuple of the primary resource and the included resource list. Either or
        /// both members are <see langword="null"/> when absent or on parse failure.
        /// </returns>
        private (TidalResource? data, List<TidalResource>? included) ExtractSingleResource( string body ) {
            try {
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;

                // Try to get included first
                List<TidalResource>? included = null;
                if (root.TryGetProperty( "included", out JsonElement includedElement )) {
                    included = JsonSerializer.Deserialize<List<TidalResource>>( includedElement.GetRawText( ), SerializerOptions );
                }

                // Try to get data
                if (root.TryGetProperty( "data", out JsonElement dataElement )) {
                    if (dataElement.ValueKind == JsonValueKind.Array) {
                        // Data is an array, take the first element
                        List<TidalResource>? dataArray = JsonSerializer.Deserialize<List<TidalResource>>( dataElement.GetRawText( ), SerializerOptions );
                        if (dataArray != null && dataArray.Count > 0) {
                            return (dataArray[0], included);
                        }
                    } else if (dataElement.ValueKind == JsonValueKind.Object) {
                        // Data is a single object
                        TidalResource? singleResource = JsonSerializer.Deserialize<TidalResource>( dataElement.GetRawText( ), SerializerOptions );
                        return (singleResource, included);
                    }
                }
            } catch (Exception ex) {
                LogExtractSingleResourceError( Logger, ex );
            }

            return (null, null);
        }

        /// <summary>
        /// Parses a raw Tidal response body into a result by first extracting the
        /// primary and included resources, then mapping them.
        /// </summary>
        /// <param name="body">The raw JSON response body; may be <see langword="null"/> or empty.</param>
        /// <param name="lookupKey">The lookup type, used for diagnostic logging.</param>
        /// <param name="kind">The expected entity kind (track or album).</param>
        /// <param name="isPrimary">
        /// Whether the mapped result should be marked primary; <see langword="null"/>
        /// is treated as not primary.
        /// </param>
        /// <param name="storefront">The country code used for the request and stored on the result.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/>, or <see langword="null"/> when
        /// the body is empty, lacks usable data, or fails to parse.
        /// </returns>
        private async Task<MusicLookupResult?> ParseTidalResponse(
            string? body,
            LookupRequestType lookupKey,
            TidalEntity kind,
            bool? isPrimary,
            string storefront = DefaultStorefront
        ) {
            if (string.IsNullOrWhiteSpace( body )) { return null; }
            try {
                (TidalResource? data, List<TidalResource>? included) = ExtractSingleResource( body );
                if (data != null && included != null) {
                    return await ParseTidalResponse( data, included, lookupKey, kind, isPrimary, storefront );
                }
            } catch (ProviderRateLimitException) {
                throw;
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogResponseBodySerialized( Logger, body, SerializerOptions );
            }
            return null;
        }

        /// <summary>
        /// Maps an already-extracted primary resource and its included resources into a
        /// <see cref="MusicLookupResult"/>.
        /// </summary>
        /// <remarks>
        /// Resolves the artist name by stitching relationship references against the
        /// included artist resources, sets the title, external id (UPC for albums, ISRC
        /// for tracks) and public URL from the resource attributes, resolves album art
        /// (for tracks this triggers a secondary album lookup), and caches any genres
        /// best-effort. Mapping failures are logged and yield <see langword="null"/>.
        /// </remarks>
        /// <param name="data">The primary track or album resource.</param>
        /// <param name="included">The side-loaded related resources.</param>
        /// <param name="lookupKey">The lookup type, used for diagnostic logging.</param>
        /// <param name="kind">The entity kind (track or album) the resource represents.</param>
        /// <param name="isPrimary">
        /// Whether the result should be marked primary; <see langword="null"/> is
        /// treated as not primary.
        /// </param>
        /// <param name="storefront">The country code stored on the mapped result.</param>
        /// <returns>
        /// The mapped <see cref="MusicLookupResult"/>, or <see langword="null"/> when
        /// mapping fails.
        /// </returns>
        private async Task<MusicLookupResult?> ParseTidalResponse(
            TidalResource data,
            List<TidalResource> included,
            LookupRequestType lookupKey,
            TidalEntity kind,
            bool? isPrimary,
            string storefront = DefaultStorefront
        ) {
            bool isAlbum = kind == TidalEntity.Album;
            MusicLookupResult result = new() {
                IsAlbum = isAlbum,
                IsPrimary = isPrimary ?? false,
                MarketRegion = storefront
            };

            try {
                if (data.Relationships != null) {
                    result.Artist = GetArtistName( data.Relationships, included );
                }

                if (data.Attributes != null) {
                    result.Title = data.Attributes.Title ?? data.Attributes.Name ?? string.Empty;

                    result.ExternalId = GetExternalIdFromAttributes( data.Attributes, isAlbum );

                    if (data.Attributes.ExternalLinks != null
                        && data.Attributes.ExternalLinks.Count > 0
                    ) {
                        result.URL = data.Attributes.ExternalLinks[0]?.Href ?? string.Empty;
                    }
                }

                result.ArtUrl = await GetAlbumArtUrl( included, isAlbum, storefront );

                if (!isAlbum) {
                    await CacheGenresFromIncludedAsync( data, included );
                }

                return result;
            } catch (ProviderRateLimitException) {
                throw;
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogDataSerialized( Logger, data, SerializerOptions );
                LogIncludedSerialized( Logger, included, SerializerOptions );
                return null;
            }
        }

        /// <summary>
        /// Selects the external identifier for a resource: the album barcode (UPC) for
        /// albums, or the track ISRC for tracks.
        /// </summary>
        /// <param name="attributes">The resource attributes to read from.</param>
        /// <param name="isAlbum">
        /// <see langword="true"/> to return the barcode id; <see langword="false"/> to
        /// return the ISRC.
        /// </param>
        /// <returns>The selected identifier, or an empty string when not present.</returns>
        private static string GetExternalIdFromAttributes( TidalAttributes attributes, bool isAlbum ) {
            return isAlbum
                ? (attributes.BarcodeId ?? string.Empty)
                : (attributes.Isrc ?? string.Empty);
        }

        /// <summary>
        /// Resolves a display artist name by stitching the resource's artist
        /// relationship references against the side-loaded artist resources.
        /// </summary>
        /// <remarks>
        /// Builds a placeholder string of the form <c>|artistId| &amp; |artistId|</c>
        /// from the relationship's artist identifiers, then replaces each
        /// <c>|artistId|</c> placeholder with the matching included artist's name. Any
        /// placeholder whose artist is absent from the included list is left unresolved.
        /// </remarks>
        /// <param name="relationships">The resource's relationships, including its artists.</param>
        /// <param name="included">The side-loaded resources containing artist details.</param>
        /// <returns>
        /// The combined artist name, or an empty string when no artist relationship is
        /// present.
        /// </returns>
        private static string GetArtistName(
            TidalRelationships relationships,
            List<TidalResource> included
        ) {
            if (relationships.Artists?.Data == null) {
                return string.Empty;
            }

            List<string> artistIds = [];
            StringBuilder resultBuilder = new();

            foreach (TidalResourceIdentifier artistInfo in relationships.Artists.Data) {
                if (artistInfo.Type == "artists" && !string.IsNullOrWhiteSpace( artistInfo.Id )) {
                    artistIds.Add( artistInfo.Id );
                    _ = resultBuilder.Append( $"|{artistInfo.Id}| & " );
                }
            }

            string result = resultBuilder.ToString( ).TrimEnd( ' ', '&' );

            // Find the matching artist in the included output
            foreach (TidalResource includedItem in included) {
                if (includedItem.Type == "artists"
                    && includedItem.Attributes != null
                    && !string.IsNullOrWhiteSpace( includedItem.Id )
                    && !string.IsNullOrWhiteSpace( includedItem.Attributes.Name )
                ) {
                    result = result.Replace( $"|{includedItem.Id}|", includedItem.Attributes.Name );
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves the album art URL for a track or album from the side-loaded
        /// resources.
        /// </summary>
        /// <remarks>
        /// For an album, returns the first <c>IMAGE</c> artwork file's href found in
        /// the included resources. For a track, the artwork is not side-loaded with the
        /// track, so this performs a secondary album lookup for each related album and
        /// returns the first non-empty art URL it finds.
        /// </remarks>
        /// <param name="included">The side-loaded related resources.</param>
        /// <param name="isAlbum">
        /// <see langword="true"/> when resolving art for an album; <see langword="false"/>
        /// when resolving art for a track.
        /// </param>
        /// <param name="storefront">The country code used for a track's secondary album lookup.</param>
        /// <returns>The album art URL, or an empty string when none is found.</returns>
        private async Task<string> GetAlbumArtUrl(
            List<TidalResource> included,
            bool isAlbum,
            string storefront = DefaultStorefront
        ) {
            if (!isAlbum) {
                // Lookup album from album details and then return album art
                foreach (TidalResource item in included) {
                    if (item.Type == "albums" && !string.IsNullOrWhiteSpace( item.Id )) {
                        MusicLookupResult? album = await NewAlbumIdLookup( item.Id, false, storefront );
                        if (!string.IsNullOrWhiteSpace( album?.ArtUrl )) {
                            return album.ArtUrl;
                        }
                    }
                }
            } else {
                // Parse Album art directly from included output
                foreach (TidalResource item in included) {
                    if (item.Type == "artworks"
                        && item.Attributes != null
                        && item.Attributes.MediaType == "IMAGE"
                        && item.Attributes.Files != null
                        && item.Attributes.Files.Count > 0
                    ) {
                        return item.Attributes.Files[0]?.Href ?? string.Empty;
                    }
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Parses an artist-search response into the list of matching artist
        /// (id, name) pairs found in its included resources.
        /// </summary>
        /// <param name="body">The raw artist-search JSON response body.</param>
        /// <returns>
        /// The list of matching artists, or <see langword="null"/> when the body is
        /// <see langword="null"/>, contains no artist resources, or fails to parse.
        /// </returns>
        private List<(string id, string artistName)>? ParseTidalArtistList( string? body ) {
            if (body == null) { return null; }

            List<(string id, string artistName)> results = [];
            try {
                TidalResponse<TidalResource>? response = JsonSerializer.Deserialize<TidalResponse<TidalResource>>( body, SerializerOptions );
                if (response?.Included != null && response.Included.Count > 0) {
                    foreach (TidalResource item in response.Included) {
                        if (item.Type == "artists"
                            && item.Attributes != null
                            && !string.IsNullOrWhiteSpace( item.Attributes.Name )
                            && !string.IsNullOrWhiteSpace( item.Id )
                        ) {
                            results.Add( (item.Id, item.Attributes.Name) );
                        }
                    }
                    return results;
                }
                return null;
            } catch (Exception ex) {
                LogParseArtistListError( Logger, ex );
                LogResponseBodySerialized( Logger, body, SerializerOptions );
                return null;
            }
        }

        /// <summary>
        /// Resolves the genre names for a resource and writes them to the genre cache
        /// as an awaited, best-effort side effect.
        /// </summary>
        /// <remarks>
        /// Collects the genre ids from the resource's genres relationship, resolves
        /// their names from the side-loaded genre resources, and stores them via the
        /// <see cref="IGenreCacheService"/>. Cache failures are logged without failing
        /// the provider lookup. No-ops when the resource has no id or its genre
        /// relationship was not returned by Tidal.
        /// </remarks>
        /// <param name="data">The primary resource whose genres are cached.</param>
        /// <param name="included">The side-loaded resources containing genre details.</param>
        private async Task CacheGenresFromIncludedAsync( TidalResource data, List<TidalResource> included ) {
            List<TidalResourceIdentifier>? genreReferences = data.Relationships?.Genres?.Data;
            if (string.IsNullOrWhiteSpace( data.Id ) || genreReferences == null) {
                return;
            }

            // Get genre IDs from relationships
            HashSet<string> genreIds = [];
            foreach (TidalResourceIdentifier genreRef in genreReferences) {
                if (genreRef.Type == "genres" && !string.IsNullOrWhiteSpace( genreRef.Id )) {
                    _ = genreIds.Add( genreRef.Id );
                }
            }

            // Find matching genre names in included array
            List<string> genreNames = [];
            foreach (TidalResource item in included) {
                if (item.Type == "genres"
                    && !string.IsNullOrWhiteSpace( item.Id )
                    && genreIds.Contains( item.Id )
                    && item.Attributes != null
                    && !string.IsNullOrWhiteSpace( item.Attributes.GenreName )
                ) {
                    genreNames.Add( item.Attributes.GenreName );
                }
            }

            try {
                await genreCache.SetGenresAsync( SupportedProviders.Tidal, data.Id, genreNames );
            } catch (Exception ex) {
                LogCacheGenresFailed( Logger, ex, data.Id );
            }
        }

        #region LoggerMessage Methods

        /// <summary>
        /// Logs (at Error level) a failure while extracting a single resource from a
        /// Tidal response.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.ExtractSingleResourceError,
            Level = LogLevel.Error,
            Message = "An error occurred while extracting single resource from Tidal response." )]
        internal static partial void LogExtractSingleResourceError( ILogger logger, Exception ex );

        /// <summary>
        /// Logs (at Error level) a failure while parsing a Tidal JSON response for a
        /// given lookup type.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        /// <param name="lookupKey">The lookup type whose response failed to parse.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.ParseResponseError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing the {LookupKey} json response from tidal." )]
        internal static partial void LogParseResponseError( ILogger logger, Exception ex, LookupRequestType lookupKey );

        /// <summary>
        /// Logs (at Error level) a failure while parsing a Tidal artist-list response.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.ParseArtistListError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing the artist list json response from tidal." )]
        internal static partial void LogParseArtistListError( ILogger logger, Exception ex );

        /// <summary>
        /// Logs (at Warning level) a failure to cache genres for a Tidal resource.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        /// <param name="resourceId">The id of the resource whose genres failed to cache.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.CacheGenresFailed,
            Level = LogLevel.Warning,
            Message = "Failed to cache genres for Tidal resource {ResourceId}" )]
        internal static partial void LogCacheGenresFailed( ILogger logger, Exception ex, string resourceId );

        /// <summary>
        /// Logs (at Trace level) a raw Tidal response body.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="responseBody">The response body to trace.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.ResponseBodyTrace,
            Level = LogLevel.Trace,
            Message = "{ResponseBody}" )]
        internal static partial void LogResponseBody( ILogger logger, string? responseBody );

        /// <summary>
        /// Logs (at Trace level) the serialized primary <c>data</c> resource of a Tidal
        /// response.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="data">The serialized data resource to trace.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.DataTrace,
            Level = LogLevel.Trace,
            Message = "Data: {Data}" )]
        internal static partial void LogDataTrace( ILogger logger, string? data );

        /// <summary>
        /// Logs (at Trace level) the serialized <c>included</c> resource list of a Tidal
        /// response.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="included">The serialized included resources to trace.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Tidal.IncludedTrace,
            Level = LogLevel.Trace,
            Message = "Included: {Included}" )]
        internal static partial void LogIncludedTrace( ILogger logger, string? included );

        /// <summary>
        /// Serializes and traces a raw response body, but only when Trace logging is
        /// enabled.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="body">The response body to serialize and trace.</param>
        /// <param name="options">The JSON options used for serialization.</param>
        private static void LogResponseBodySerialized( ILogger logger, string? body, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedBody = JsonSerializer.Serialize( body, options );
                LogResponseBody( logger, serializedBody );
            }
        }

        /// <summary>
        /// Serializes and traces a primary <c>data</c> resource, but only when Trace
        /// logging is enabled.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="data">The data resource to serialize and trace.</param>
        /// <param name="options">The JSON options used for serialization.</param>
        private static void LogDataSerialized( ILogger logger, TidalResource data, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedData = JsonSerializer.Serialize( data, options );
                LogDataTrace( logger, serializedData );
            }
        }

        /// <summary>
        /// Serializes and traces an <c>included</c> resource list, but only when Trace
        /// logging is enabled.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="included">The included resources to serialize and trace.</param>
        /// <param name="options">The JSON options used for serialization.</param>
        private static void LogIncludedSerialized( ILogger logger, List<TidalResource> included, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedIncluded = JsonSerializer.Serialize( included, options );
                LogIncludedTrace( logger, serializedIncluded );
            }
        }

        #endregion LoggerMessage Methods

    }
}
