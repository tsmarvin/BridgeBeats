using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Providers.AppleMusic.Models;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Providers.AppleMusic;

namespace BridgeBeats.Core.Domain.Providers.AppleMusic {

    /// <summary>
    /// An <see cref="IMusicLookupService"/> implementation for <see cref="SupportedProviders.AppleMusic"/>
    /// that resolves tracks and albums against the Apple Music API.
    /// </summary>
    /// <remarks>
    /// This is the direct, in-worker provider client: it calls the real Apple Music API over HTTP using a
    /// developer JWT minted by <see cref="AppleJwtHandler"/>. The distributed-path proxy that forwards to a
    /// remote worker is <see cref="AppleMusicHttpLookupService"/>. Each lookup parses the Apple Music JSON
    /// response into a <see cref="BridgeBeats.Contracts.DTOs.MusicLookupResult"/>.
    /// </remarks>
    /// <param name="jwtHandler">The <see cref="AppleJwtHandler"/> used to authenticate the API calls performed by the service.</param>
    /// <param name="factory">The pre-configured HttpClientFactory used to perform the API calls for the service.</param>
    /// <param name="logger">The logger used to record errors.</param>
    /// <param name="serializerOptions">The JSON serializer options used to deserialize Apple Music responses and to serialize the body of the API results on error when using trace logging.</param>
    /// <param name="genreCache">The cache used to store provider-native track genres.</param>
    public partial class AppleMusicLookupService(
        AppleJwtHandler jwtHandler,
        IHttpClientFactory factory,
        ILogger<AppleMusicLookupService> logger,
        JsonSerializerOptions serializerOptions,
        IGenreCacheService genreCache
    ) : MusicLookupServiceBase( logger, serializerOptions ), IStorefrontMusicLookupService {

        /// <summary>
        /// The default market region/storefront (country) used for Apple Music API requests when a lookup
        /// does not carry one.
        /// </summary>
        public const string DefaultStorefront = "us";

        /// <inheritdoc/>
        public override SupportedProviders Provider => SupportedProviders.AppleMusic;

        #region IMusicLookupService Public

        /// <inheritdoc/>
        public override Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc )
            => GetInfoByISRCAsync( isrc, DefaultStorefront );

        /// <inheritdoc/>
        public async Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc, string storefront ) {
            storefront = NormalizeStorefront( storefront );
            return await ParseAppleMusicResponse(
                await NewMusicApiRequest( AppleMusicLinkParser.GetSongsIsrcURI( storefront, isrc ), LookupRequestType.IsrcLookup ),
                LookupRequestType.IsrcLookup,
                storefront,
                false
            );
        }

        /// <inheritdoc/>
        public override Task<MusicLookupResult?> GetInfoByUPCAsync( string upc )
            => GetInfoByUPCAsync( upc, DefaultStorefront );

        /// <inheritdoc/>
        public async Task<MusicLookupResult?> GetInfoByUPCAsync( string upc, string storefront ) {
            storefront = NormalizeStorefront( storefront );
            return await ParseAppleMusicResponse(
                await NewMusicApiRequest( AppleMusicLinkParser.GetAlbumUpcURI( storefront, upc ), LookupRequestType.UpcLookup ),
                LookupRequestType.UpcLookup,
                storefront,
                true
            );
        }

        /// <inheritdoc/>
        public override async Task<MusicLookupResult?> GetInfoAsync( string title, string artist ) {
            List<(string id, string artistName)>? artistResults = ParseAppleMusicArtistList(
                await NewMusicApiRequest(
                    AppleMusicLinkParser.GetArtistSearchUri(DefaultStorefront, artist),
                    LookupRequestType.ArtistLookup
                )
            );

            // Bail out early if we have no results
            if (artistResults == null || artistResults.Count == 0) { return null; }

            string sanitizedAlbumTitle = SanitizeAlbumTitle(title);
            string sanitizedSongTitle = SanitizeSongTitle(title);
            foreach ((string id, _) in artistResults) {
                MusicLookupResult? result = await ParseArtistElementLists(
                    LookupRequestType.ArtistAlbumLookup,
                    await NewMusicApiRequest(AppleMusicLinkParser.GetArtistAlbumsURI(DefaultStorefront, id), LookupRequestType.ArtistAlbumLookup),
                    sanitizedAlbumTitle,
                    DefaultStorefront,
                    true
                );

                if (result != null) { return result; }

                // if no album match, try songs
                result = await ParseArtistElementLists(
                    LookupRequestType.AlbumTrackLookup,
                    await NewMusicApiRequest( AppleMusicLinkParser.GetArtistSongsURI( DefaultStorefront, id ), LookupRequestType.AlbumTrackLookup ),
                    sanitizedSongTitle,
                    DefaultStorefront,
                    false
                );

                if (result != null) { return result; }
            }

            return null;
        }

        /// <inheritdoc/>
        public override async Task<MusicLookupResult?> GetInfoAsync( string uri )
            => AppleMusicLinkParser.TryParseUri( uri, out string requestUri, out string storefront, out bool isAlbum )
                ? await ParseAppleMusicResponse(
                    await NewMusicApiRequest( requestUri, LookupRequestType.UriLookup ),
                    LookupRequestType.UriLookup,
                    storefront,
                    isAlbum
                )
                : null;

        /// <inheritdoc/>
        public override Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum )
            => GetInfoByIDAsync( providerId, isAlbum, DefaultStorefront );

        /// <inheritdoc/>
        public async Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum, string storefront ) {
            storefront = NormalizeStorefront( storefront );
            string requestUri = isAlbum
                ? AppleMusicLinkParser.GetAlbumIdUri( storefront, providerId )
                : AppleMusicLinkParser.GetSongIdUri( storefront, providerId );

            LookupRequestType requestKey = isAlbum ? LookupRequestType.AlbumIdLookup : LookupRequestType.SongIdLookup;
            return await ParseAppleMusicResponse(
                await NewMusicApiRequest( requestUri, requestKey ),
                requestKey,
                storefront,
                isAlbum
            );
        }

        private static string NormalizeStorefront( string storefront )
            => string.IsNullOrWhiteSpace( storefront )
                ? DefaultStorefront
                : storefront.Trim( ).ToLowerInvariant( );

        #endregion IMusicLookupService Public

        #region IMusicLookupService Private Methods

        /// <summary>
        /// Creates an HTTP client for the Apple Music API with the signed developer JWT applied as the
        /// authorization header.
        /// </summary>
        /// <returns>An authenticated <see cref="HttpClient"/> for Apple Music API requests.</returns>
        private protected override Task<HttpClient> CreateAuthenticatedClientAsync( ) {
            HttpClient client = factory.CreateClient("musickit-api");
            client.DefaultRequestHeaders.Authorization = jwtHandler.NewAuthenticationHeader( );
            return Task.FromResult( client );
        }

        /// <summary>
        /// Deserializes an Apple Music artist-search response into a list of artist identifiers and names.
        /// </summary>
        /// <param name="body">The raw JSON response body, or <see langword="null"/>.</param>
        /// <returns>
        /// The matched artists as <c>(id, artistName)</c> tuples, or <see langword="null"/> if the body is
        /// <see langword="null"/>, empty, or cannot be parsed.
        /// </returns>
        private List<(string id, string artistName)>? ParseAppleMusicArtistList( string? body ) {
            if (body == null) { return null; }

            List<(string id, string artistName)> results = [];
            try {
                AppleMusicSearchResponse? response = JsonSerializer.Deserialize<AppleMusicSearchResponse>( body, SerializerOptions );
                if (response?.Results?.Artists?.Data != null && response.Results.Artists.Data.Count > 0) {
                    foreach (AppleMusicArtist artist in response.Results.Artists.Data) {
                        if (artist.Attributes != null) {
                            results.Add( (artist.Id, artist.Attributes.Name) );
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
        /// Scans a paged list of artist albums or songs and returns the first entry whose sanitized title
        /// matches the requested title.
        /// </summary>
        /// <param name="lookupKey">The lookup type, used for diagnostic logging.</param>
        /// <param name="body">The raw JSON response body, or <see langword="null"/>.</param>
        /// <param name="title">The sanitized title to match against.</param>
        /// <param name="storefront">The storefront (country) to record on the result.</param>
        /// <param name="isAlbum"><see langword="true"/> to match albums; <see langword="false"/> to match songs.</param>
        /// <returns>The matching result, or <see langword="null"/> if no entry matches or the body cannot be parsed.</returns>
        private async Task<MusicLookupResult?> ParseArtistElementLists(
            LookupRequestType lookupKey,
            string? body,
            string title,
            string storefront,
            bool isAlbum
        ) {
            if (body == null) { return null; }

            try {
                AppleMusicDataResponse<JsonElement>? response = JsonSerializer.Deserialize<AppleMusicDataResponse<JsonElement>>( body, SerializerOptions );
                if (response?.Data != null && response.Data.Count > 0) {
                    foreach (JsonElement item in response.Data) {
                        if (isAlbum) {
                            AppleMusicAlbum? album = item.Deserialize<AppleMusicAlbum>( SerializerOptions );
                            if (album?.Attributes != null) {
                                string name = album.Attributes.Name.Trim();
                                if (SanitizeAlbumTitle( name ).Equals( title, StringComparison.InvariantCultureIgnoreCase )) {
                                    return ParseAppleMusicAlbumResponse( album, lookupKey, storefront );
                                }
                            }
                        } else {
                            AppleMusicSong? song = item.Deserialize<AppleMusicSong>( SerializerOptions );
                            if (song?.Attributes != null) {
                                string name = song.Attributes.Name.Trim();
                                if (SanitizeSongTitle( name ).Equals( title, StringComparison.InvariantCultureIgnoreCase )) {
                                    return await ParseAppleMusicSongResponse( song, lookupKey, storefront );
                                }
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogResponseBodySerialized( Logger, body, SerializerOptions );
            }
            return null;
        }

        /// <summary>
        /// Parses a single-entity Apple Music response, taking the first data element as the album or song.
        /// </summary>
        /// <param name="body">The raw JSON response body, or <see langword="null"/>.</param>
        /// <param name="lookupKey">The lookup type, used for diagnostic logging.</param>
        /// <param name="storeFront">The storefront (country) to record on the result.</param>
        /// <param name="isAlbum">
        /// <see langword="true"/> to parse the entity as an album; otherwise it is parsed as a song.
        /// </param>
        /// <returns>The parsed result, or <see langword="null"/> if the response is empty or cannot be parsed.</returns>
        private async Task<MusicLookupResult?> ParseAppleMusicResponse(
            string? body,
            LookupRequestType lookupKey,
            string storeFront,
            bool? isAlbum
        ) {
            if (body == null) { return null; }
            try {
                AppleMusicDataResponse<JsonElement>? response = JsonSerializer.Deserialize<AppleMusicDataResponse<JsonElement>>( body, SerializerOptions );
                if (response?.Data == null || response.Data.Count == 0) {
                    return null;
                }

                JsonElement firstItem = response.Data[0];
                if (isAlbum == true) {
                    AppleMusicAlbum? album = firstItem.Deserialize<AppleMusicAlbum>( SerializerOptions );
                    return album != null ? ParseAppleMusicAlbumResponse( album, lookupKey, storeFront ) : null;
                } else {
                    AppleMusicSong? song = firstItem.Deserialize<AppleMusicSong>( SerializerOptions );
                    return song != null ? await ParseAppleMusicSongResponse( song, lookupKey, storeFront ) : null;
                }
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogResponseBodySerialized( Logger, body, SerializerOptions );
            }
            return null;
        }

        /// <summary>
        /// Maps an Apple Music song into a <see cref="BridgeBeats.Contracts.DTOs.MusicLookupResult"/>,
        /// expanding the artwork URL template with concrete width and height.
        /// </summary>
        /// <param name="song">The Apple Music song to map.</param>
        /// <param name="lookupKey">The lookup type, used for diagnostic logging.</param>
        /// <param name="storeFront">The storefront (country) to record on the result.</param>
        /// <returns>The mapped result, or <see langword="null"/> if the song has no attributes or mapping fails.</returns>
        private async Task<MusicLookupResult?> ParseAppleMusicSongResponse(
            AppleMusicSong song,
            LookupRequestType lookupKey,
            string storeFront
        ) {
            try {
                if (song.Attributes == null) { return null; }

                MusicLookupResult result = new() {
                    MarketRegion = storeFront,
                    IsAlbum = false,
                    Artist = song.Attributes.ArtistName,
                    Title = song.Attributes.Name,
                    ExternalId = song.Attributes.Isrc ?? string.Empty,
                    URL = song.Attributes.Url
                };

                if (song.Attributes.Artwork != null && !string.IsNullOrWhiteSpace( song.Attributes.Artwork.Url )) {
                    result.ArtUrl = song.Attributes.Artwork.Url
                        .Replace( "{w}", song.Attributes.Artwork.Width.ToString( ) )
                        .Replace( "{h}", song.Attributes.Artwork.Height.ToString( ) );
                }

                if (!string.IsNullOrWhiteSpace( song.Id )) {
                    try {
                        await genreCache.SetGenresAsync(
                            SupportedProviders.AppleMusic,
                            song.Id,
                            song.Attributes.GenreNames );
                    } catch (Exception ex) {
                        LogCacheGenresFailed( Logger, ex, song.Id );
                    }
                }

                return result;
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogSongSerialized( Logger, song, SerializerOptions );
                return null;
            }
        }

        /// <summary>
        /// Maps an Apple Music album into a <see cref="BridgeBeats.Contracts.DTOs.MusicLookupResult"/>,
        /// expanding the artwork URL template with concrete width and height.
        /// </summary>
        /// <param name="album">The Apple Music album to map.</param>
        /// <param name="lookupKey">The lookup type, used for diagnostic logging.</param>
        /// <param name="storeFront">The storefront (country) to record on the result.</param>
        /// <returns>The mapped result, or <see langword="null"/> if the album has no attributes or mapping fails.</returns>
        private MusicLookupResult? ParseAppleMusicAlbumResponse(
            AppleMusicAlbum album,
            LookupRequestType lookupKey,
            string storeFront
        ) {
            try {
                if (album.Attributes == null) { return null; }

                MusicLookupResult result = new() {
                    MarketRegion = storeFront,
                    IsAlbum = true,
                    Artist = album.Attributes.ArtistName,
                    Title = album.Attributes.Name,
                    ExternalId = album.Attributes.Upc ?? string.Empty,
                    URL = album.Attributes.Url
                };

                if (album.Attributes.Artwork != null && !string.IsNullOrWhiteSpace( album.Attributes.Artwork.Url )) {
                    result.ArtUrl = album.Attributes.Artwork.Url
                        .Replace( "{w}", album.Attributes.Artwork.Width.ToString( ) )
                        .Replace( "{h}", album.Attributes.Artwork.Height.ToString( ) );
                }

                return result;
            } catch (Exception ex) {
                LogParseResponseError( Logger, ex, lookupKey );
                LogAlbumSerialized( Logger, album, SerializerOptions );
                return null;
            }
        }

        #endregion IMusicLookupService Private Methods

        #region LoggerMessage Methods

        /// <summary>Logs an error raised while parsing the Apple Music artist-list response.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.AppleMusic.ParseArtistListError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing the artist list json response from apple." )]
        internal static partial void LogParseArtistListError( ILogger logger, Exception ex );

        /// <summary>Logs an error raised while parsing an Apple Music response of the given lookup type.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception that occurred.</param>
        /// <param name="lookupKey">The lookup type whose response failed to parse.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.AppleMusic.ParseResponseError,
            Level = LogLevel.Error,
            Message = "An error occurred while parsing the {LookupKey} json response from apple." )]
        internal static partial void LogParseResponseError( ILogger logger, Exception ex, LookupRequestType lookupKey );

        /// <summary>Logs a best-effort Apple Music track genre cache failure.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="ex">The exception raised by the cache.</param>
        /// <param name="trackId">The Apple Music track id whose genres could not be stored.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.AppleMusic.CacheGenresFailed,
            Level = LogLevel.Warning,
            Message = "Failed to cache genres for Apple Music track {TrackId}" )]
        internal static partial void LogCacheGenresFailed( ILogger logger, Exception ex, string trackId );

        /// <summary>Writes a raw Apple Music response body to the trace log.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="responseBody">The response body to log.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.AppleMusic.ResponseBodyTrace,
            Level = LogLevel.Trace,
            Message = "{ResponseBody}" )]
        internal static partial void LogResponseBody( ILogger logger, string? responseBody );

        /// <summary>Serializes and trace-logs a response body when trace logging is enabled.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="body">The response body to serialize and log.</param>
        /// <param name="options">JSON options used for serialization.</param>
        private static void LogResponseBodySerialized( ILogger logger, string? body, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedBody = JsonSerializer.Serialize( body, options );
                LogResponseBody( logger, serializedBody );
            }
        }

        /// <summary>Serializes and trace-logs an Apple Music song when trace logging is enabled.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="song">The song to serialize and log.</param>
        /// <param name="options">JSON options used for serialization.</param>
        private static void LogSongSerialized( ILogger logger, AppleMusicSong song, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedSong = JsonSerializer.Serialize( song, options );
                LogResponseBody( logger, serializedSong );
            }
        }

        /// <summary>Serializes and trace-logs an Apple Music album when trace logging is enabled.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="album">The album to serialize and log.</param>
        /// <param name="options">JSON options used for serialization.</param>
        private static void LogAlbumSerialized( ILogger logger, AppleMusicAlbum album, JsonSerializerOptions options ) {
            if (logger.IsEnabled( LogLevel.Trace )) {
                string serializedAlbum = JsonSerializer.Serialize( album, options );
                LogResponseBody( logger, serializedAlbum );
            }
        }

        #endregion LoggerMessage Methods

    }
}
