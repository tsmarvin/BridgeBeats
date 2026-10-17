using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Providers.Spotify.Models;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Providers.Spotify;

namespace BridgeBeats.Core.Domain.Providers.Spotify {

    /// <summary>
    /// Direct, in-worker Spotify lookup service that calls the real Spotify Web API and also serves
    /// batch lookups via <see cref="ISpotifyBulkLookupService"/>.
    /// </summary>
    /// <remarks>
    /// This is the direct variant (it extends <see cref="MusicLookupServiceBase"/>); the proxy variant
    /// that forwards to a worker is <see cref="SpotifyHttpLookupService"/>. Title/artist resolution
    /// searches the artist, enumerates their albums, and matches a sanitized album title, falling back
    /// to matching a sanitized song title across the artist's albums. Track-to-artist genre mappings are
    /// cached via <see cref="IGenreCacheService"/> as a best-effort, fire-and-forget side effect whose
    /// failures are logged and swallowed. Bulk requests surface a 429 as
    /// <see cref="Contracts.Exceptions.RetryAfterExceededException"/> rather than a partial result.
    /// </remarks>
    /// <param name="handler">Supplies the Spotify bearer token for authenticated API calls.</param>
    /// <param name="factory">Factory for the <c>spotify-api</c> HTTP client.</param>
    /// <param name="logger">Logger for lookup and parse diagnostics.</param>
    /// <param name="serializerOptions">JSON options for deserializing Spotify API responses.</param>
    /// <param name="genreCache">Genre cache used for best-effort track-to-artist mapping.</param>
    public sealed partial class SpotifyLookupService(
        SpotifyTokenHandler handler,
        IHttpClientFactory factory,
        ILogger<SpotifyLookupService> logger,
        JsonSerializerOptions serializerOptions,
        IGenreCacheService genreCache
    ) : MusicLookupServiceBase( logger, serializerOptions ), IMusicLookupService, ISpotifyBulkLookupService {

        /// <summary>Gets the provider this service resolves against (<see cref="SupportedProviders.Spotify"/>).</summary>
        public override SupportedProviders Provider => SupportedProviders.Spotify;

        /// <summary>Resolves a track by ISRC via the Spotify search endpoint.</summary>
        /// <param name="isrc">The International Standard Recording Code to resolve.</param>
        /// <returns>The first matching track, or <see langword="null"/> when none is found.</returns>
        public override async Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc )
            => ParseSpotifyResponse(
                await NewMusicApiRequest( SpotifyLinkParser.GetTracksIsrcURI( isrc ), LookupRequestType.IsrcLookup ),
                LookupRequestType.IsrcLookup,
                SpotifyEntity.Track,
                null
            );

        /// <summary>Resolves an album by UPC via the Spotify search endpoint.</summary>
        /// <param name="upc">The Universal Product Code to resolve.</param>
        /// <returns>The first matching album, or <see langword="null"/> when none is found.</returns>
        public override async Task<MusicLookupResult?> GetInfoByUPCAsync( string upc )
            => ParseSpotifyResponse(
                await NewMusicApiRequest( SpotifyLinkParser.GetAlbumUpcURI( upc ), LookupRequestType.UpcLookup ),
                LookupRequestType.UpcLookup,
                SpotifyEntity.Album,
                null
            );

        /// <summary>
        /// Resolves an entity by title and artist by searching the artist, then matching a sanitized
        /// album title, and finally falling back to matching a sanitized song title.
        /// </summary>
        /// <param name="title">The album or track title to match (compared after sanitization).</param>
        /// <param name="artist">The artist name to search for.</param>
        /// <returns>
        /// The matched album or track, or <see langword="null"/> when no artist, album, or track matches.
        /// </returns>
        public override async Task<MusicLookupResult?> GetInfoAsync( string title, string artist ) {
            List<(string id, string artistName)>? artistResults = await ParseSpotifyArtistList( artist );

            // Bail out early if we have no results
            if (artistResults == null || artistResults.Count == 0) { return null; }

            string sanitizedAlbumTitle = SanitizeAlbumTitle( title );
            foreach ((string id, string artistName) in artistResults) {
                List<string> artistAlbumIds = [];
                await foreach ((string albumId, MusicLookupResult album) in ParseArtistAlbumLists( id )) {
                    if (ValidateSanitizedAlbumTitle( album.Title, sanitizedAlbumTitle )) {
                        // The Artist Album endpoint doesn't return the albums with external Id's (UPC's) included.
                        // So we'll perform another direct lookup to get the UPC.
                        string? body = await NewMusicApiRequest(
                            SpotifyLinkParser.GetAlbumIdURI( albumId ),
                            LookupRequestType.AlbumIdLookup
                        );

                        // This really shouldnt happen, but if something goes wrong we can return what we've already matched.
                        if (string.IsNullOrWhiteSpace( body )) { return album; }

                        SpotifyAlbum? fullAlbum = JsonSerializer.Deserialize<SpotifyAlbum>( body, SerializerOptions );
                        if (fullAlbum == null) {
                            LogDeserializeAlbumFailed( Logger, LookupRequestType.AlbumIdLookup );
                        }
                        album.ExternalId = fullAlbum?.ExternalIds?.Upc ?? string.Empty;

                        return album;
                    } else {
                        artistAlbumIds.Add( albumId );
                    }
                }

                // If we couldnt match the artist + album combination, try searching for the
                // individual tracks on those albums instead.
                string sanitizedSongTitle = SanitizeSongTitle( title );
                foreach (string albumId in artistAlbumIds) {
                    MusicLookupResult? result = await ParseAlbumTrackListsAsync( albumId, sanitizedSongTitle );
                    if (result != null) { return result; }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a track or album from a Spotify URL by parsing its entity kind and id, then looking
        /// it up by id.
        /// </summary>
        /// <param name="uri">The Spotify URL to resolve (short links are resolved first).</param>
        /// <returns>
        /// The resolved album or track, or <see langword="null"/> when the URL cannot be parsed or its
        /// entity kind is neither album nor track.
        /// </returns>
        public override async Task<MusicLookupResult?> GetInfoAsync( string uri ) {
            (bool result, SpotifyEntity kind, string id) = await SpotifyLinkParser.TryParseUriAsync( uri );
            if (result) {
                if (kind == SpotifyEntity.Album) {
                    return await GetInfoByIDAsync( id, true );
                } else if (kind == SpotifyEntity.Track) {
                    return await GetInfoByIDAsync( id, false );
                }
            }
            return null;
        }

        /// <summary>Resolves an entity by its Spotify id by fetching it from the album or track endpoint.</summary>
        /// <param name="providerId">The Spotify entity id.</param>
        /// <param name="isAlbum"><see langword="true"/> to fetch an album, <see langword="false"/> for a track.</param>
        /// <returns>The resolved entity, or <see langword="null"/> when the API returns no body.</returns>
        public override async Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum ) {
            SpotifyEntity kind = isAlbum ? SpotifyEntity.Album : SpotifyEntity.Track;
            string requestUri = isAlbum
                ? SpotifyLinkParser.GetAlbumIdURI( providerId )
                : SpotifyLinkParser.GetTrackIdURI( providerId );

            LookupRequestType requestKey = isAlbum ? LookupRequestType.AlbumIdLookup : LookupRequestType.SongIdLookup;
            string? body = await NewMusicApiRequest( requestUri, requestKey );
            if (body == null) { return null; }

            using JsonDocument jsonDoc = JsonDocument.Parse(body);
            return ParseSpotifyResponse( jsonDoc.RootElement, requestKey, kind, true );
        }

        /// <summary>
        /// Performs a bulk lookup of tracks by their Spotify IDs, preserving result order against the
        /// requested ids.
        /// </summary>
        /// <param name="trackIds">The collection of Spotify track IDs to look up (max 50).</param>
        /// <returns>
        /// A dictionary mapping track ID to lookup result; a <see langword="null"/> value indicates the
        /// track was not found at that position. An empty dictionary is returned for an empty input or an
        /// empty/unparseable response body; an empty dictionary on a request-level failure (auth or
        /// network) signals the caller to requeue without writing saga state.
        /// </returns>
        /// <remarks>
        /// Uses the shared <c>/tracks</c> rate-limit key (<see cref="SpotifyConstants.TracksEndpoint"/>).
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when more than <see cref="SpotifyConstants.MaxTracksPerBatchLookup"/> track IDs are provided.
        /// </exception>
        /// <exception cref="Contracts.Exceptions.RetryAfterExceededException">Thrown when Spotify responds with HTTP 429.</exception>
        public async Task<Dictionary<string, MusicLookupResult?>> GetTracksByIdsAsync( IEnumerable<string> trackIds ) {
            List<string> idList = [.. trackIds];

            if (idList.Count == 0) {
                return [];
            }

            if (idList.Count > SpotifyConstants.MaxTracksPerBatchLookup) {
                throw new ArgumentException(
                    $"Cannot request more than {SpotifyConstants.MaxTracksPerBatchLookup} tracks at once. Received {idList.Count}.",
                    nameof( trackIds )
                );
            }

            string requestUri = SpotifyLinkParser.GetBulkTracksUri( idList );
            string? body = await NewBulkMusicApiRequest( requestUri, SpotifyConstants.TracksEndpoint );

            Dictionary<string, MusicLookupResult?> results = [];

            if (string.IsNullOrWhiteSpace( body )) {
                // Request failed - return empty dictionary (caller should requeue)
                return results;
            }

            try {
                SpotifyTracksResponse? response = JsonSerializer.Deserialize<SpotifyTracksResponse>( body, SerializerOptions );

                if (response?.Tracks == null) {
                    LogDeserializeBulkTracksFailed( Logger );
                    return results;
                }

                // Map results back to IDs - position in response matches position in request
                for (int i = 0; i < idList.Count && i < response.Tracks.Count; i++) {
                    SpotifyTrack? track = response.Tracks[i];
                    if (track == null) {
                        // Track not found - null indicates not found
                        results[idList[i]] = null;
                    } else {
                        results[idList[i]] = new MusicLookupResult {
                            Artist = track.Artists is { Count: > 0 } ? track.Artists[0].Name : string.Empty,
                            Title = track.Name,
                            ExternalId = track.ExternalIds?.Isrc ?? string.Empty,
                            URL = track.ExternalUrls?.Spotify ?? string.Empty,
                            ArtUrl = track.Album?.Images is { Count: > 0 } ? track.Album.Images[0].Url : string.Empty,
                            IsAlbum = false,
                            IsPrimary = true
                        };

                        // Cache track→artist mapping for genre resolution
                        CacheTrackArtistMapping( track.Id, track.Artists );
                    }
                }
            } catch (Exception ex) {
                LogParseBulkTracksError( Logger, ex );
                LogResponseBody( Logger, body );
            }

            return results;
        }

        /// <summary>
        /// Performs a bulk lookup of albums by their Spotify IDs, preserving result order against the
        /// requested ids.
        /// </summary>
        /// <param name="albumIds">The collection of Spotify album IDs to look up (max 20).</param>
        /// <returns>
        /// A dictionary mapping album ID to lookup result; a <see langword="null"/> value indicates the
        /// album was not found at that position. An empty dictionary is returned for an empty input or an
        /// empty/unparseable response body; an empty dictionary on a request-level failure signals the
        /// caller to requeue without writing saga state.
        /// </returns>
        /// <remarks>
        /// Uses the shared <c>/albums</c> rate-limit key (<see cref="SpotifyConstants.AlbumsEndpoint"/>).
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when more than <see cref="SpotifyConstants.MaxAlbumsPerBatchLookup"/> album IDs are provided.
        /// </exception>
        /// <exception cref="Contracts.Exceptions.RetryAfterExceededException">Thrown when Spotify responds with HTTP 429.</exception>
        public async Task<Dictionary<string, MusicLookupResult?>> GetAlbumsByIdsAsync( IEnumerable<string> albumIds ) {
            List<string> idList = [.. albumIds];

            if (idList.Count == 0) {
                return [];
            }

            if (idList.Count > SpotifyConstants.MaxAlbumsPerBatchLookup) {
                throw new ArgumentException(
                    $"Cannot request more than {SpotifyConstants.MaxAlbumsPerBatchLookup} albums at once. Received {idList.Count}.",
                    nameof( albumIds )
                );
            }

            string requestUri = SpotifyLinkParser.GetBulkAlbumsUri( idList );
            string? body = await NewBulkMusicApiRequest( requestUri, SpotifyConstants.AlbumsEndpoint );

            Dictionary<string, MusicLookupResult?> results = [];

            if (string.IsNullOrWhiteSpace( body )) {
                // Request failed - return empty dictionary (caller should requeue)
                return results;
            }

            try {
                SpotifyAlbumsResponse? response = JsonSerializer.Deserialize<SpotifyAlbumsResponse>( body, SerializerOptions );

                if (response?.Albums == null) {
                    LogDeserializeBulkAlbumsFailed( Logger );
                    return results;
                }

                // Map results back to IDs - position in response matches position in request
                for (int i = 0; i < idList.Count && i < response.Albums.Count; i++) {
                    SpotifyAlbum? album = response.Albums[i];
                    if (album == null) {
                        // Album not found - null indicates not found
                        results[idList[i]] = null;
                    } else {
                        results[idList[i]] = new MusicLookupResult {
                            Artist = album.Artists is { Count: > 0 } ? album.Artists[0].Name : string.Empty,
                            Title = album.Name,
                            ExternalId = album.ExternalIds?.Upc ?? string.Empty,
                            URL = album.ExternalUrls?.Spotify ?? string.Empty,
                            ArtUrl = album.Images is { Count: > 0 } ? album.Images[0].Url : string.Empty,
                            IsAlbum = true,
                            IsPrimary = true
                        };
                    }
                }
            } catch (Exception ex) {
                LogParseBulkAlbumsError( Logger, ex );
                LogResponseBody( Logger, body );
            }

            return results;
        }

        /// <summary>
        /// Performs a bulk lookup of artists by their Spotify IDs to retrieve genre information,
        /// preserving result order against the requested ids.
        /// </summary>
        /// <param name="artistIds">The collection of Spotify artist IDs to look up (max 50).</param>
        /// <returns>
        /// A dictionary mapping artist ID to their genres; a <see langword="null"/> value indicates the
        /// artist was not found at that position, and an empty list indicates the artist has no genres
        /// classified. An empty dictionary is returned for an empty input or an empty/unparseable response body.
        /// </returns>
        /// <remarks>
        /// Uses the shared <c>/artists</c> rate-limit key (<see cref="SpotifyConstants.ArtistsEndpoint"/>).
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when more than <see cref="SpotifyConstants.MaxArtistsPerBatchLookup"/> artist IDs are provided.
        /// </exception>
        /// <exception cref="Contracts.Exceptions.RetryAfterExceededException">Thrown when Spotify responds with HTTP 429.</exception>
        public async Task<Dictionary<string, List<string>?>> GetArtistsByIdsAsync( IEnumerable<string> artistIds ) {
            List<string> idList = [.. artistIds];

            if (idList.Count == 0) {
                return [];
            }

            if (idList.Count > SpotifyConstants.MaxArtistsPerBatchLookup) {
                throw new ArgumentException(
                    $"Cannot request more than {SpotifyConstants.MaxArtistsPerBatchLookup} artists at once. Received {idList.Count}.",
                    nameof( artistIds )
                );
            }

            string requestUri = SpotifyLinkParser.GetBulkArtistsUri( idList );
            string? body = await NewBulkMusicApiRequest( requestUri, SpotifyConstants.ArtistsEndpoint );

            Dictionary<string, List<string>?> results = [];

            if (string.IsNullOrWhiteSpace( body )) {
                // Request failed - return empty dictionary (caller should requeue)
                return results;
            }

            try {
                SpotifyArtistsResponse? response = JsonSerializer.Deserialize<SpotifyArtistsResponse>( body, SerializerOptions );

                if (response?.Artists == null) {
                    LogDeserializeBulkArtistsFailed( Logger );
                    return results;
                }

                // Map results back to IDs - position in response matches position in request
                for (int i = 0; i < idList.Count && i < response.Artists.Count; i++) {
                    SpotifyArtist? artist = response.Artists[i];
                    // Artist not found - null indicates not found
                    results[idList[i]] = artist?.Genres;
                }
            } catch (Exception ex) {
                LogParseBulkArtistsError( Logger, ex );
                LogResponseBody( Logger, body );
            }

            return results;
        }

        /// <summary>
        /// Performs an API request for bulk lookup operations, using a custom endpoint key for
        /// failure diagnostics.
        /// </summary>
        /// <param name="requestUri">The API endpoint URI.</param>
        /// <param name="endpointKey">The endpoint key for rate limit tracking.</param>
        /// <returns>The response body on success, or <see langword="null"/> on any non-2xx status other than 400 (which throws — see exceptions below); 401, 403, 404, and 5xx return <see langword="null"/>.</returns>
        /// <exception cref="Contracts.Exceptions.ProviderRateLimitException">
        /// Thrown by the provider HTTP pipeline when its Polly retry policy exhausts an HTTP 429.
        /// </exception>
        /// <exception cref="Contracts.Exceptions.SpotifyBulkRejectedException">
        /// Thrown when the response is HTTP 400 Bad Request. A 400 indicates a malformed or otherwise
        /// unacceptable id in the batch; re-enqueueing each item individually isolates the poison id.
        /// All other non-2xx responses (including 401, 403, 404, and 5xx) return <see langword="null"/>
        /// so the caller's batch back-off path handles them.
        /// </exception>
        private async Task<string?> NewBulkMusicApiRequest( string requestUri, string endpointKey ) {
            using HttpClient client = await CreateAuthenticatedClientAsync( );
            HttpResponseMessage response = await client.GetAsync( requestUri );

            if (response.IsSuccessStatusCode) {
                return await response.Content.ReadAsStringAsync( );
            }

            // Only a 400 Bad Request indicates a malformed id; other failures fall through to
            // the batch back-off path. 401/403 are request-wide auth failures that individual
            // retries cannot resolve; 404 and 5xx are similarly not id-specific. Returning null
            // routes all of those to the existing cooldown + rebatch path, unchanged.
            int statusCode = (int)response.StatusCode;
            if (statusCode == 400) {
                LogBulkRequestFailed( Logger, endpointKey, statusCode );
                throw new Contracts.Exceptions.SpotifyBulkRejectedException(
                    statusCode,
                    new Uri( client.BaseAddress!, requestUri ),
                    SupportedProviders.Spotify
                );
            }

            LogBulkRequestFailed( Logger, endpointKey, statusCode );
            return null;
        }

        /// <summary>
        /// Creates a <c>spotify-api</c> HTTP client with a current Spotify bearer token applied.
        /// </summary>
        /// <returns>An authenticated <see cref="HttpClient"/>; the caller owns and disposes it.</returns>
        private protected override async Task<HttpClient> CreateAuthenticatedClientAsync( ) {
            HttpClient client = factory.CreateClient("spotify-api");
            client.DefaultRequestHeaders.Authorization = await handler.NewBearerAuthenticationHeader( );
            return client;
        }

        /// <summary>
        /// Parses a raw Spotify response body, unwrapping a search result envelope if present, into a
        /// single result.
        /// </summary>
        /// <param name="body">The raw JSON response body; may be <see langword="null"/> or whitespace.</param>
        /// <param name="lookupKey">The lookup type, used for log correlation.</param>
        /// <param name="kind">The expected entity kind (album or track).</param>
        /// <param name="isPrimary">Whether to mark the result as the primary provider result.</param>
        /// <returns>The parsed result, or <see langword="null"/> when the body is empty, unparseable, or yields no element.</returns>
        private MusicLookupResult? ParseSpotifyResponse(
            string? body,
            LookupRequestType lookupKey,
            SpotifyEntity kind,
            bool? isPrimary
        ) {
            if (string.IsNullOrWhiteSpace( body )) { return null; }
            try {
                using JsonDocument jsonDoc = JsonDocument.Parse(body);
                if (CanParseJsonElement( jsonDoc.RootElement, out JsonElement element )) {
                    return ParseSpotifyResponse( element, lookupKey, kind, isPrimary );
                }
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogResponseBodySerialized( Logger, body, SerializerOptions );
            }
            return null;
        }

        /// <summary>
        /// Parses out the first track or album JsonElement from the Spotify API response, unwrapping a
        /// <c>tracks</c>/<c>albums</c> envelope, an <c>items</c> array, and taking the first element of an array.
        /// </summary>
        /// <param name="root">The root JSON element of the response.</param>
        /// <param name="output">When this returns, the element to deserialize into a single entity.</param>
        /// <returns>
        /// <see langword="true"/> when a single entity element is available; <see langword="false"/> when
        /// the resolved array is empty.
        /// </returns>
        private static bool CanParseJsonElement( JsonElement root, out JsonElement output ) {
            output = root;

            if (root.TryGetProperty( "tracks", out JsonElement trackProps )) {
                output = trackProps;
            } else if (root.TryGetProperty( "albums", out JsonElement albumProps )) {
                output = albumProps;
            }

            if (output.TryGetProperty( "items", out JsonElement items )) {
                output = items;
            }

            if (output.ValueKind == JsonValueKind.Array) {
                if (output.GetArrayLength( ) > 0) {
                    output = output.EnumerateArray( ).First( );
                } else {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Maps a single Spotify album or track JSON element into a <see cref="MusicLookupResult"/>.
        /// </summary>
        /// <param name="element">The JSON element representing the entity.</param>
        /// <param name="lookupKey">The lookup type, used for log correlation.</param>
        /// <param name="kind">The entity kind; determines album-versus-track mapping.</param>
        /// <param name="isPrimary">Whether to mark the result as the primary provider result.</param>
        /// <returns>
        /// The mapped result, or <see langword="null"/> when deserialization fails. As a side effect, a
        /// track's artist mapping is cached.
        /// </returns>
        private MusicLookupResult? ParseSpotifyResponse(
            JsonElement element,
            LookupRequestType lookupKey,
            SpotifyEntity kind,
            bool? isPrimary
        ) {
            bool isAlbum = kind == SpotifyEntity.Album;
            MusicLookupResult result = new() {
                IsAlbum = isAlbum,
                IsPrimary = isPrimary ?? false
            };

            try {
                switch (kind) {
                    case SpotifyEntity.Album:
                        SpotifyAlbum? albumData = element.Deserialize<SpotifyAlbum>( SerializerOptions );
                        if (albumData == null) {
                            LogDeserializeAlbumFailed( Logger, lookupKey );
                            return null;
                        }
                        result.Artist = albumData.Artists != null && albumData.Artists.Count > 0
                                        ? albumData.Artists[0].Name
                                        : string.Empty;
                        result.Title = albumData.Name;
                        result.ExternalId = albumData.ExternalIds?.Upc ?? string.Empty;
                        result.URL = albumData.ExternalUrls != null ? albumData.ExternalUrls.Spotify : string.Empty;
                        result.ArtUrl = albumData.Images.Count > 0
                                        ? albumData.Images[0].Url
                                        : string.Empty;
                        break;
                    case SpotifyEntity.Track:
                        SpotifyTrack? trackData = element.Deserialize<SpotifyTrack>( SerializerOptions );
                        if (trackData == null) {
                            LogDeserializeTrackFailed( Logger, lookupKey );
                            return null;
                        }
                        result.Artist = trackData.Artists != null && trackData.Artists.Count > 0
                                        ? trackData.Artists[0].Name
                                        : string.Empty;
                        result.Title = trackData.Name;
                        result.ExternalId = trackData.ExternalIds?.Isrc ?? string.Empty;
                        result.URL = trackData.ExternalUrls != null ? trackData.ExternalUrls.Spotify : string.Empty;
                        result.ArtUrl = trackData.Album?.Images != null && trackData.Album.Images.Count > 0
                                        ? trackData.Album.Images[0].Url
                                        : string.Empty;

                        // Cache track→artist mapping for genre resolution (fire and forget)
                        if (trackData.Artists != null) {
                            CacheTrackArtistMapping( trackData.Id, trackData.Artists );
                        }
                        break;
                }

                return result;
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogJsonElementSerialized( Logger, element, SerializerOptions );
                return null;
            }
        }

        /// <summary>
        /// Searches Spotify for an artist name and collects every matching artist's id and name across
        /// all result pages.
        /// </summary>
        /// <param name="artist">The artist name to search for.</param>
        /// <returns>
        /// The list of matching (id, name) pairs; an empty or partial list when pagination is interrupted;
        /// or <see langword="null"/> when the initial request fails or an error occurs.
        /// </returns>
        private async Task<List<(string id, string artistName)>?> ParseSpotifyArtistList( string artist ) {
            try {
                List<(string id, string artistName)> results = [];
                string? body = await NewMusicApiRequest(
                                        SpotifyLinkParser.GetArtistSearchUri( artist ),
                                        LookupRequestType.ArtistLookup
                                    );
                if (body == null) { return null; }

                SpotifySearchResponse? response = JsonSerializer.Deserialize<SpotifySearchResponse>( body );
                do {
                    if (null != response?.Artists) {
                        foreach (SpotifyArtist sArtist in response.Artists.Items) {
                            results.Add( (sArtist.Id, sArtist.Name) );
                        }

                        if (response.Artists.Next != null) {
                            body = await NewMusicApiRequest( response.Artists.Next, LookupRequestType.ArtistLookup );
                            if (body == null) { return results; }
                            response = JsonSerializer.Deserialize<SpotifySearchResponse>( body );
                        } else {
                            return results;
                        }
                    }
                } while (response?.Artists?.Next != null);
            } catch (JsonException ex) {
                LogParseArtistListError( Logger, ex );
            }

            return null;
        }

        /// <summary>
        /// Streams an artist's albums (id plus a lightweight result) across all paginated pages.
        /// </summary>
        /// <param name="id">The Spotify artist id whose albums to enumerate.</param>
        /// <returns>
        /// An async stream of (album id, partial result) tuples; the stream ends when a page request
        /// fails or no further pages remain. The yielded result has no external id populated.
        /// </returns>
        private async IAsyncEnumerable<(string id, MusicLookupResult album)> ParseArtistAlbumLists( string id ) {

            string? body = await NewMusicApiRequest( SpotifyLinkParser.GetArtistAlbumsURI( id ), LookupRequestType.ArtistAlbumLookup);
            if (body == null) { yield break; }

            do {
                SpotifyPaging<SpotifyAlbumSimplified>? paging = JsonSerializer.Deserialize<SpotifyPaging<SpotifyAlbumSimplified>>( body );
                if (paging == null || paging.Items.Count == 0) { yield break; }

                foreach (SpotifyAlbumSimplified album in paging.Items) {
                    yield return (album.Id, new MusicLookupResult {
                        Artist = album.Artists != null && album.Artists.Count > 0 ? album.Artists[0].Name : string.Empty,
                        Title = album.Name,
                        ExternalId = string.Empty, // Will be filled in later if matched
                        URL = album.ExternalUrls != null ? album.ExternalUrls.Spotify : string.Empty,
                        ArtUrl = album.Images != null && album.Images.Count > 0 ? album.Images[0].Url : string.Empty,
                        IsAlbum = true
                    });
                }

                body = string.IsNullOrWhiteSpace( paging.Next )
                    ? null
                    : await NewMusicApiRequest( paging.Next, LookupRequestType.ArtistAlbumLookup );
            } while (body != null);
        }

        /// <summary>
        /// Searches an album's tracks for one whose sanitized title matches, then fetches the full track
        /// for its ISRC and links.
        /// </summary>
        /// <param name="albumId">The Spotify album id whose tracks to scan.</param>
        /// <param name="sanitizedSongTitle">The target song title, already sanitized, to match against.</param>
        /// <returns>
        /// The matched track (fully populated when the per-track fetch succeeds, otherwise a lightweight
        /// result), or <see langword="null"/> when no track matches across all pages.
        /// </returns>
        private async Task<MusicLookupResult?> ParseAlbumTrackListsAsync(
            string albumId,
            string sanitizedSongTitle
        ) {
            string? body = await NewMusicApiRequest(SpotifyLinkParser.GetAlbumTracksURI(albumId), LookupRequestType.AlbumTrackLookup);
            if (body == null) { return null; }

            do {
                SpotifyPaging<SpotifyTrackSimplified>? paging = JsonSerializer.Deserialize<SpotifyPaging<SpotifyTrackSimplified>>( body );
                if (paging == null || paging.Items.Count == 0) { return null; }

                foreach (SpotifyTrackSimplified track in paging.Items) {
                    if (SanitizeSongTitle( track.Name ).Equals( sanitizedSongTitle, StringComparison.InvariantCultureIgnoreCase )) {

                        string? trackBody = await NewMusicApiRequest( SpotifyLinkParser.GetTrackIdURI( track.Id ), LookupRequestType.SongIdLookup );

                        // This shouldn't happen, but if we cant fetch full track details, return simplified track data
                        if (trackBody == null) {
                            return new MusicLookupResult {
                                Artist = track.Artists != null && track.Artists.Count > 0 ? track.Artists[0].Name : string.Empty,
                                Title = track.Name,
                                ExternalId = string.Empty,
                                URL = track.ExternalUrls != null ? track.ExternalUrls.Spotify : string.Empty,
                                IsAlbum = false
                            };
                        }

                        SpotifyTrack? fullTrack = JsonSerializer.Deserialize<SpotifyTrack>( trackBody );
                        if (fullTrack == null) {
                            LogDeserializeTrackFailed( Logger, LookupRequestType.SongIdLookup );
                            return null;
                        }
                        return new MusicLookupResult {
                            Artist = fullTrack.Artists != null && fullTrack.Artists.Count > 0 ? fullTrack.Artists[0].Name : string.Empty,
                            Title = fullTrack.Name,
                            ExternalId = fullTrack.ExternalIds?.Isrc ?? string.Empty,
                            URL = fullTrack.ExternalUrls != null ? fullTrack.ExternalUrls.Spotify : string.Empty,
                            IsAlbum = false
                        };
                    }
                }

                body = string.IsNullOrWhiteSpace( paging.Next )
                    ? null
                    : await NewMusicApiRequest( paging.Next, LookupRequestType.AlbumTrackLookup );
            } while (body != null);

            return null;
        }

        /// <summary>
        /// Caches the track→artist mapping for later genre resolution via batch worker. Fires and forgets
        /// to avoid blocking the main response.
        /// </summary>
        /// <param name="trackId">The Spotify track ID.</param>
        /// <param name="artists">The list of artists on the track; only those with a non-empty id are used.</param>
        /// <remarks>
        /// No-ops when the track id is blank or no usable artist ids are present. The cache work runs on
        /// a background task whose failures are logged and swallowed, so the caller is never blocked or
        /// made to fail by caching errors.
        /// </remarks>
        private void CacheTrackArtistMapping( string trackId, List<SpotifyArtistSimplified> artists ) {
            if (string.IsNullOrWhiteSpace( trackId ) || artists.Count == 0) {
                return;
            }

            List<string> artistIds = [.. artists
                .Where( a => !string.IsNullOrWhiteSpace( a.Id ) )
                .Select( a => a.Id )];

            if (artistIds.Count == 0) { return; }

            // Fire and forget - don't block the response
            _ = Task.Run( async ( ) => {
                try {
                    await genreCache.SetTrackArtistMappingAsync( SupportedProviders.Spotify, trackId, artistIds );
                    await genreCache.EnqueueArtistsForRefreshAsync( SupportedProviders.Spotify, artistIds );
                } catch (Exception ex) {
                    LogCacheTrackMappingFailed( Logger, ex, trackId );
                }
            } );
        }

        #region LoggerMessage Methods

        /// <summary>Logs that album deserialization failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="lookupKey">The lookup type being processed.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.DeserializeAlbumFailed,
            Level = LogLevel.Error,
            Message = "Failed to deserialize album data from Spotify response for {LookupKey}." )]
        internal static partial void LogDeserializeAlbumFailed( ILogger logger, LookupRequestType lookupKey );

        /// <summary>Logs that track deserialization failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="lookupKey">The lookup type being processed.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.DeserializeTrackFailed,
            Level = LogLevel.Error,
            Message = "Failed to deserialize track data from Spotify response for {LookupKey}." )]
        internal static partial void LogDeserializeTrackFailed( ILogger logger, LookupRequestType lookupKey );

        /// <summary>Logs that bulk tracks deserialization failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.DeserializeBulkTracksFailed,
            Level = LogLevel.Error,
            Message = "Failed to deserialize bulk tracks response from Spotify." )]
        internal static partial void LogDeserializeBulkTracksFailed( ILogger logger );

        /// <summary>Logs that bulk albums deserialization failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.DeserializeBulkAlbumsFailed,
            Level = LogLevel.Error,
            Message = "Failed to deserialize bulk albums response from Spotify." )]
        internal static partial void LogDeserializeBulkAlbumsFailed( ILogger logger );

        /// <summary>Logs that bulk artists deserialization failed.</summary>
        /// <param name="logger">The logger to write to.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.DeserializeBulkArtistsFailed,
            Level = LogLevel.Error,
            Message = "Failed to deserialize bulk artists response from Spotify." )]
        internal static partial void LogDeserializeBulkArtistsFailed( ILogger logger );

        /// <summary>Logs an error parsing bulk tracks response.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.ParseBulkTracksError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing bulk tracks response from Spotify." )]
        internal static partial void LogParseBulkTracksError( ILogger logger, Exception ex );

        /// <summary>Logs an error parsing bulk albums response.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.ParseBulkAlbumsError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing bulk albums response from Spotify." )]
        internal static partial void LogParseBulkAlbumsError( ILogger logger, Exception ex );

        /// <summary>Logs an error parsing bulk artists response.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.ParseBulkArtistsError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing bulk artists response from Spotify." )]
        internal static partial void LogParseBulkArtistsError( ILogger logger, Exception ex );

        /// <summary>Logs bulk API request failure.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="endpoint">The bulk endpoint label.</param>
        /// <param name="statusCode">The HTTP status code returned.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.BulkRequestFailed,
            Level = LogLevel.Warning,
            Message = "Bulk API request to {Endpoint} failed with status {StatusCode}" )]
        internal static partial void LogBulkRequestFailed( ILogger logger, string endpoint, int statusCode );

        /// <summary>Logs an error parsing the JSON response.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        /// <param name="lookupKey">The lookup type being processed.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.ParseResponseError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing the {LookupKey} json response from spotify." )]
        internal static partial void LogParseResponseError( ILogger logger, Exception ex, LookupRequestType lookupKey );

        /// <summary>Logs an error parsing the artist list response.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.ParseArtistListError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing the artist list json response from spotify." )]
        internal static partial void LogParseArtistListError( ILogger logger, Exception ex );

        /// <summary>Logs failure to cache track artist mapping.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        /// <param name="trackId">The track id whose mapping failed to cache.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.CacheTrackMappingFailed,
            Level = LogLevel.Warning,
            Message = "Failed to cache track→artist mapping for Spotify track {TrackId}" )]
        internal static partial void LogCacheTrackMappingFailed( ILogger logger, Exception ex, string trackId );

        /// <summary>Logs the response body for trace level debugging.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="responseBody">The response body to record.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Spotify.ResponseBodyTrace,
            Level = LogLevel.Trace,
            Message = "{ResponseBody}" )]
        internal static partial void LogResponseBody( ILogger logger, string? responseBody );

        /// <summary>
        /// Logs the serialized response body for trace level debugging, only when trace logging is enabled.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="body">The response body to serialize and record.</param>
        /// <param name="options">The serializer options used to format the body.</param>
        private static void LogResponseBodySerialized( ILogger logger, string? body, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedBody = JsonSerializer.Serialize( body, options );
                LogResponseBody( logger, serializedBody );
            }
        }

        /// <summary>
        /// Logs the serialized JSON element for trace level debugging, only when trace logging is enabled.
        /// </summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="element">The JSON element to serialize and record.</param>
        /// <param name="options">The serializer options used to format the element.</param>
        private static void LogJsonElementSerialized( ILogger logger, JsonElement element, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedElement = JsonSerializer.Serialize( element, options );
                LogResponseBody( logger, serializedElement );
            }
        }

        #endregion LoggerMessage Methods

    }
}
