using System.Text.Json;
using System.Text.RegularExpressions;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Domain.Providers.Common {
    /// <summary>
    /// Abstract base for direct, in-worker provider services that call the real Spotify, Apple Music,
    /// or Tidal HTTP APIs.
    /// </summary>
    /// <remarks>
    /// This is the direct half of the two <see cref="IMusicLookupService"/> implementation families;
    /// the proxy half is <see cref="HttpMusicLookupService"/>. The base owns title-sanitization helpers
    /// (so cross-provider titles compare equal), the <see cref="NewMusicApiRequest"/> request/log helper,
    /// and an abstract authenticated-client factory each provider supplies. The lookup methods that vary
    /// by provider API are abstract; <see cref="GetInfoAsync(MusicLookupResult)"/> is implemented here as
    /// a dispatch over the more specific overloads.
    /// </remarks>
    /// <param name="logger">Logger shared with derived services through <see cref="Logger"/>.</param>
    /// <param name="serializerOptions">JSON options shared with derived services through <see cref="SerializerOptions"/>.</param>
    public abstract partial class MusicLookupServiceBase(
        ILogger<MusicLookupServiceBase> logger,
        JsonSerializerOptions serializerOptions
    ) : IMusicLookupService {

        #region IMusicLookupService Implementation

        /// <summary>Gets the provider this service resolves against.</summary>
        public abstract SupportedProviders Provider { get; }

        /// <summary>Resolves a track by ISRC against the provider API.</summary>
        /// <param name="isrc">The International Standard Recording Code to resolve.</param>
        /// <returns>The resolved track, or <see langword="null"/> when no match is found.</returns>
        public abstract Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc );

        /// <summary>Resolves an album by UPC against the provider API.</summary>
        /// <param name="upc">The Universal Product Code to resolve.</param>
        /// <returns>The resolved album, or <see langword="null"/> when no match is found.</returns>
        public abstract Task<MusicLookupResult?> GetInfoByUPCAsync( string upc );

        /// <summary>Resolves an entity by title and artist against the provider API.</summary>
        /// <param name="title">The track or album title to match (compared after sanitization).</param>
        /// <param name="artist">The artist name to search for.</param>
        /// <returns>The resolved entity, or <see langword="null"/> when no match is found.</returns>
        public abstract Task<MusicLookupResult?> GetInfoAsync( string title, string artist );

        /// <summary>Resolves an entity from its provider URL.</summary>
        /// <param name="uri">The provider URL to parse and resolve.</param>
        /// <returns>The resolved entity, or <see langword="null"/> when the URL cannot be resolved.</returns>
        public abstract Task<MusicLookupResult?> GetInfoAsync( string uri );

        /// <summary>
        /// Re-resolves an entity on this provider from an already-resolved result, used to back-fill a
        /// provider missing from a combined result.
        /// </summary>
        /// <param name="lookup">The source result whose external id, album flag, title, and artist drive the lookup.</param>
        /// <returns>
        /// The resolved entity. Dispatch prefers UPC when the source is an album and ISRC when it is a
        /// track; it falls back to a title/artist search when no external id is present or the id lookup
        /// returns nothing.
        /// </returns>
        public async Task<MusicLookupResult?> GetInfoAsync( MusicLookupResult lookup )
            => string.IsNullOrWhiteSpace( lookup.ExternalId ) || lookup.IsAlbum == null
                        ? await GetInfoAsync( lookup.Title, lookup.Artist )
                        : ((bool)lookup.IsAlbum
                            ? await GetInfoByUPCAsync( lookup.ExternalId )
                            : await GetInfoByISRCAsync( lookup.ExternalId ))
                        ?? await GetInfoAsync( lookup.Title, lookup.Artist ); // Fallback to title/artist search if lookup by id fails

        /// <summary>Resolves an entity by its provider id against the provider API.</summary>
        /// <param name="providerId">The provider-native entity id.</param>
        /// <param name="isAlbum"><see langword="true"/> to resolve an album, <see langword="false"/> for a track.</param>
        /// <returns>The resolved entity, or <see langword="null"/> when no match is found.</returns>
        public abstract Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum );

        #endregion IMusicLookupService Implementation

        /// <summary>
        /// Creates an HTTP client pre-configured with this provider's authentication for a single request.
        /// </summary>
        /// <returns>An authenticated <see cref="HttpClient"/>; the caller owns and disposes it.</returns>
        private protected abstract Task<HttpClient> CreateAuthenticatedClientAsync( );

        #region Base Class Defaults

        /// <summary>Logger available to derived provider services.</summary>
        protected readonly ILogger<MusicLookupServiceBase> Logger = logger;

        /// <summary>JSON serializer options available to derived provider services.</summary>
        protected readonly JsonSerializerOptions SerializerOptions = serializerOptions;

        /// <summary>
        /// Issues an authenticated GET against the provider API and returns the response body, logging
        /// non-success responses.
        /// </summary>
        /// <param name="requestUri">The request URI, relative to the authenticated client's base address.</param>
        /// <param name="lookupKey">The lookup type, used for log correlation.</param>
        /// <returns>The response body as a string, or <see langword="null"/> on a non-success status code.</returns>
        private protected async Task<string?> NewMusicApiRequest(
            string requestUri,
            LookupRequestType lookupKey
        ) {
            using HttpClient http = await CreateAuthenticatedClientAsync();

            HttpResponseMessage resp = await http.GetAsync( requestUri );
            if (!resp.IsSuccessStatusCode) {
                LogApiRequestError( Logger, lookupKey, Provider.ToString( ), (int)resp.StatusCode, resp.ReasonPhrase );
                if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.RequestTimeout
                    || (int)resp.StatusCode >= 500) {
                    throw new HttpRequestException( $"{Provider} returned {(int)resp.StatusCode}.", null, resp.StatusCode );
                }
                return null;
            }
            return await resp.Content.ReadAsStringAsync( );
        }

        /// <summary>
        /// Tests whether a candidate album title matches an already-sanitized target title.
        /// </summary>
        /// <param name="albumTitle">The raw candidate album title to sanitize and compare.</param>
        /// <param name="sanitizedTitle">The target title, already sanitized by <see cref="SanitizeAlbumTitle"/>.</param>
        /// <returns><see langword="true"/> when the sanitized forms are equal, ignoring case; otherwise <see langword="false"/>.</returns>
        protected static bool ValidateSanitizedAlbumTitle( string albumTitle, string sanitizedTitle )
            => sanitizedTitle
                .Equals( SanitizeAlbumTitle( albumTitle ), StringComparison.InvariantCultureIgnoreCase );

        /// <summary>
        /// Removes quote characters that interfere with cross-provider title comparison.
        /// </summary>
        /// <param name="input">The title string to clean.</param>
        /// <returns>The input with quote characters stripped.</returns>
        protected static string SanitizeTitleString( string input ) =>
            s_ignoredChars.Replace( input, string.Empty );

        /// <summary>
        /// Normalizes a song title so a trailing "Radio Edit" addendum compares equal across providers.
        /// </summary>
        /// <param name="title">The raw song title.</param>
        /// <returns>The sanitized title with the edit-type addendum normalized and quote characters stripped.</returns>
        protected static string SanitizeSongTitle( string title )
            => SanitizeTitleString(
                s_songTitleAddendum.IsMatch( title ) ?
                    title.Replace( s_songTitleAddendum.GetGroupValues( title, "Addendum" ).First( ), string.Empty ).Trim( ) + " "
                    + s_songTitleAddendum.GetGroupValues( title, "EditType" ).First( ).Trim( )
                : title
            );

        /// <summary>
        /// Normalizes an album title so a trailing "(Single)" or "(EP)" addendum compares equal across providers.
        /// </summary>
        /// <param name="title">The raw album title.</param>
        /// <returns>The sanitized title with the addendum stripped and quote characters removed.</returns>
        protected static string SanitizeAlbumTitle( string title )
            => SanitizeTitleString(
                s_albumTitleAddendum.IsMatch( title )
                    ? title.Replace( s_albumTitleAddendum.GetGroupValues( title, "Addendum" ).First( ), string.Empty ).Trim( )
                    : title
            );

        /// <summary>Compiled regex matching quote characters stripped during title sanitization.</summary>
        private static readonly Regex s_ignoredChars = InvalidSearchCharsRegex();

        /// <summary>Builds the compiled <see cref="s_ignoredChars"/> regex.</summary>
        /// <returns>The compiled regex matching quote characters.</returns>
        [GeneratedRegex( @"[''""'\""]", RegexOptions.Compiled )]
        private static partial Regex InvalidSearchCharsRegex( );

        /// <summary>Compiled regex matching a trailing "(Single)"/"(EP)" album addendum.</summary>
        private static readonly Regex s_albumTitleAddendum = AlbumAddendumRegex();

        /// <summary>Builds the compiled <see cref="s_albumTitleAddendum"/> regex.</summary>
        /// <returns>The compiled regex matching the album addendum.</returns>
        [GeneratedRegex( @"(?<Addendum> (?:- )?\(?(?:Single|EP)\)?)$", RegexOptions.Compiled )]
        private static partial Regex AlbumAddendumRegex( );

        /// <summary>Compiled regex matching a trailing "Radio Edit" song addendum.</summary>
        private static readonly Regex s_songTitleAddendum = SongAddendumRegex();

        /// <summary>Builds the compiled <see cref="s_songTitleAddendum"/> regex.</summary>
        /// <returns>The compiled regex matching the song addendum.</returns>
        [GeneratedRegex( @"(?<Addendum> (?:- )?\(?(?<EditType>Radio Edit)\)?)$", RegexOptions.Compiled )]
        private static partial Regex SongAddendumRegex( );

        #endregion Base Class Defaults

        #region LoggerMessage Methods

        /// <summary>Logs a failed provider API request, including the lookup type and HTTP status.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="lookupKey">The lookup type that was being performed.</param>
        /// <param name="provider">The provider name.</param>
        /// <param name="statusCode">The HTTP status code returned.</param>
        /// <param name="reasonPhrase">The HTTP reason phrase, if any.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Common.ApiRequestError,
            Level = LogLevel.Error,
            Message = "An error occurred while fetching {LookupKey} data from {Provider}: HTTP {StatusCode} {ReasonPhrase}" )]
        internal static partial void LogApiRequestError(
            ILogger logger,
            LookupRequestType lookupKey,
            string provider,
            int statusCode,
            string? reasonPhrase
        );

        #endregion LoggerMessage Methods

    }

}
