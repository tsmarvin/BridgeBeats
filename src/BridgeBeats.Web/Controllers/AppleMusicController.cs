using System.Text.Encodings.Web;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.AppleMusic;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Web.Models;
using BridgeBeats.Web.Streaming;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Drives Apple Music integration for authenticated users: serving the Apple Music page, issuing the
/// MusicKit developer token, storing the per-user Music-User-Token, listing and processing the user's
/// Apple Music library playlists, looking up media links by Apple Music id, and streaming rendered result
/// cards. Actions are rooted under the <c>applemusic/</c> route prefix and require an authenticated session.
/// </summary>
/// <param name="userManager">Identity user manager used to resolve the current user and persist Apple Music token state.</param>
/// <param name="httpClientFactory">Factory for the named <c>musickit-api</c> HTTP client used to call the Apple Music API.</param>
/// <param name="logger">Logger for Apple Music token and playlist-processing events.</param>
/// <param name="mediaLinkService">Service that resolves media links across providers from an Apple Music id.</param>
/// <param name="viewEngine">Composite view engine used to render result-card partials to HTML strings for streaming.</param>
/// <param name="jwtHandler">Optional Apple Music JWT handler that produces the developer token; when null, Apple Music is not configured.</param>
/// <param name="cardService">Optional Open Graph card service used to store lookup results and produce shareable card URLs.</param>
/// <param name="cacheRepository">Optional cache repository used to resolve the ATProto URI for a result.</param>
/// <param name="probe">Optional saga-progress probe used to set the "lookup in progress" indicator on result cards; when null, the indicator is never shown.</param>
[Authorize]
public partial class AppleMusicController(
    UserManager<ApplicationUser> userManager,
    IHttpClientFactory httpClientFactory,
    ILogger<AppleMusicController> logger,
    IMediaLinkService mediaLinkService,
    ICompositeViewEngine viewEngine,
    AppleJwtHandler? jwtHandler = null,
    IOpenGraphCardService? cardService = null,
    IMediaLinkCacheRepository? cacheRepository = null,
    ILookupProgressProbe? probe = null
) : Controller {

    /// <summary>
    /// Renders the Apple Music page.
    /// </summary>
    /// <returns>The default Apple Music view (HTTP GET <c>applemusic</c>).</returns>
    [HttpGet]
    [Route( "applemusic" )]
    public IActionResult Index( ) => View( );

    /// <summary>
    /// Returns the Apple Music MusicKit developer token that browser clients use to initialize MusicKit.
    /// </summary>
    /// <returns>
    /// HTTP GET <c>applemusic/developer-token</c>. <c>200 OK</c> with the token in JSON when Apple Music is
    /// configured; <c>503 Service Unavailable</c> when the JWT handler is not configured.
    /// </returns>
    [HttpGet]
    [Route( "applemusic/developer-token" )]
    public IActionResult GetDeveloperToken( ) {
        if (jwtHandler == null) {
            return StatusCode( 503, new { message = "Apple Music service is not configured" } );
        }

        string token = jwtHandler.NewAuthenticationHeader( ).Parameter ?? string.Empty;
        return Ok( new { token } );
    }

    /// <summary>
    /// Stores the authenticated user's Apple Music Music-User-Token and its expiration so later requests can
    /// act on the user's library.
    /// </summary>
    /// <param name="request">The token payload (the Music-User-Token and its lifetime in milliseconds) bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>applemusic/store-token</c>. <c>200 OK</c> on success; <c>400 Bad Request</c> when the
    /// model is invalid or the update fails; <c>401 Unauthorized</c> if no user is resolved. Requires a valid
    /// anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "applemusic/store-token" )]
    public async Task<IActionResult> StoreToken( [FromBody] StoreTokenRequest request ) {
        if (!ModelState.IsValid) {
            return BadRequest( ModelState );
        }

        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( );
        }

        user.AppleMusicUserToken = request.UserToken;
        user.AppleMusicTokenExpiration = DateTime.UtcNow.AddMilliseconds( request.ExpiresInMs );

        Microsoft.AspNetCore.Identity.IdentityResult result = await userManager.UpdateAsync( user );

        if (!result.Succeeded) {
            LogStoreTokenFailed( user.Id );
            return BadRequest( new { message = "Failed to store token" } );
        }

        LogTokenStored( user.Id );

        return Ok( new { message = "Token stored successfully" } );
    }

    /// <summary>
    /// Retrieves the authenticated user's Apple Music library playlists, returning each playlist's id and
    /// name. Requires a stored, unexpired Music-User-Token.
    /// </summary>
    /// <returns>
    /// HTTP GET <c>applemusic/playlists</c>. <c>200 OK</c> with the list of playlists on success;
    /// <c>401 Unauthorized</c> when the user is not authenticated or the token is missing or expired; the
    /// upstream status code when the Apple Music API call fails; <c>500</c> on an unexpected error.
    /// </returns>
    [HttpGet]
    [Route( "applemusic/playlists" )]
    public async Task<IActionResult> GetPlaylists( ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( new { message = "User not authenticated" } );
        }

        if (string.IsNullOrEmpty( user.AppleMusicUserToken )) {
            return Unauthorized( new { message = "Apple Music token not found. Please authenticate first." } );
        }

        if (user.AppleMusicTokenExpiration.HasValue && user.AppleMusicTokenExpiration.Value < DateTime.UtcNow) {
            return Unauthorized( new { message = "Apple Music token expired. Please authenticate again." } );
        }

        try {
            HttpClient client = httpClientFactory.CreateClient( "musickit-api" );

            // Add developer token (JWT) for API authentication
            if (jwtHandler != null) {
                client.DefaultRequestHeaders.Authorization = jwtHandler.NewAuthenticationHeader( );
            }

            // Add user token for accessing user's library
            client.DefaultRequestHeaders.Add( "Music-User-Token", user.AppleMusicUserToken );

            HttpResponseMessage response = await client.GetAsync( "https://api.music.apple.com/v1/me/library/playlists" );

            if (!response.IsSuccessStatusCode) {
                LogGetPlaylistsFailed( user.Id, (int)response.StatusCode );
                return StatusCode( (int)response.StatusCode, new { message = "Failed to retrieve playlists from Apple Music" } );
            }

            string content = await response.Content.ReadAsStringAsync( );
            using JsonDocument doc = JsonDocument.Parse( content );

            List<object> playlists = [];
            if (doc.RootElement.TryGetProperty( "data", out JsonElement dataElement )) {
                playlists.AddRange(
                    dataElement.EnumerateArray( )
                        .Where( playlist =>
                            playlist.TryGetProperty( "id", out _ ) &&
                            playlist.TryGetProperty( "attributes", out JsonElement attributesElement ) &&
                            attributesElement.TryGetProperty( "name", out _ )
                        )
                        .Select( playlist => new {
                            id = playlist.GetProperty( "id" ).GetString( ),
                            name = playlist.GetProperty( "attributes" ).GetProperty( "name" ).GetString( )
                        } )
                );
            }

            return Ok( new { playlists } );

        } catch (Exception ex) {
            LogGetPlaylistsError( ex, user.Id );
            return StatusCode( 500, new { message = "An error occurred while retrieving playlists" } );
        }
    }

    /// <summary>
    /// Resolves media-link results for a batch of Apple Music song ids, returning the results that were found.
    /// </summary>
    /// <param name="songIds">The Apple Music catalog song ids to look up, bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>applemusic/getsongsbyid</c>. <c>200 OK</c> with the resolved results; <c>401 Unauthorized</c>
    /// if no user is resolved; <c>503 Service Unavailable</c> when Apple Music is not configured. Requires a
    /// valid anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "applemusic/getsongsbyid" )]
    public async Task<IActionResult> GetSongsByID( [FromBody] string[] songIds ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( );
        }
        if (jwtHandler == null) {
            return StatusCode( 503, new { message = "Apple Music service is not configured" } );
        }
        List<MediaLinkResult> results = [];

        foreach (string songId in songIds) {
            MediaLinkResult? result = await mediaLinkService.GetInfoByProviderIdAsync( songId, SupportedProviders.AppleMusic, false );
            if (result != null) {
                results.Add( result );
            }
        }

        return Ok( new { results } );
    }

    /// <summary>
    /// Attempts to resolve the ATProto record URI for a result from the cache, swallowing cache errors so a
    /// missing URI does not break the response.
    /// </summary>
    /// <param name="result">The media-link result whose ATProto URI is being resolved.</param>
    /// <returns>The ATProto URI when available; otherwise <see langword="null"/>.</returns>
    private async Task<string?> GetATProtoUriFromCache( MediaLinkResult result ) {
        try {
            return await ATProtoUriHelper.GetATProtoUriFromCacheAsync( result, cacheRepository );
        } catch (InvalidOperationException ex) {
            LogCacheInvalidOp( ex );
        } catch (ArgumentException ex) {
            LogCacheArgError( ex );
        } catch (Exception ex) {
            LogCacheError( ex );
        }

        return null;
    }

    /// <summary>
    /// Reports the authenticated user's Apple Music token status: whether a token is stored, whether it is
    /// expired, and its expiration time.
    /// </summary>
    /// <returns>
    /// HTTP GET <c>applemusic/status</c>. <c>200 OK</c> with the token status; <c>401 Unauthorized</c> if no
    /// user is resolved.
    /// </returns>
    [HttpGet]
    [Route( "applemusic/status" )]
    public async Task<IActionResult> GetStatus( ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( );
        }

        bool hasToken = !string.IsNullOrEmpty( user.AppleMusicUserToken );
        bool isExpired = user.AppleMusicTokenExpiration.HasValue && user.AppleMusicTokenExpiration.Value < DateTime.UtcNow;

        return Ok( new {
            hasToken,
            isExpired,
            expiresAt = user.AppleMusicTokenExpiration
        } );
    }

    /// <summary>
    /// Reads an Apple Music library playlist's tracks, following pagination and collecting catalog track ids
    /// up to a cap of 1000 tracks. Returns the collected ids and flags whether the playlist was truncated or
    /// is large enough to warrant client-side handling.
    /// </summary>
    /// <param name="request">The request containing the Apple Music playlist id, bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>applemusic/process-playlist</c>. <c>200 OK</c> with the track count, track ids, and
    /// truncation flags; <c>401 Unauthorized</c> when the user is not authenticated or the token is missing
    /// or expired; the upstream status code when an Apple Music call fails; <c>400 Bad Request</c> on invalid
    /// model state; <c>500</c> on an unexpected error. Requires a valid anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "applemusic/process-playlist" )]
    public async Task<IActionResult> ProcessPlaylist( [FromBody] ProcessPlaylistRequest request ) {
        if (!ModelState.IsValid) {
            return BadRequest( ModelState );
        }

        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( new { message = "User not authenticated" } );
        }

        if (string.IsNullOrEmpty( user.AppleMusicUserToken )) {
            return Unauthorized( new { message = "Apple Music token not found. Please authenticate first." } );
        }

        if (user.AppleMusicTokenExpiration.HasValue && user.AppleMusicTokenExpiration.Value < DateTime.UtcNow) {
            return Unauthorized( new { message = "Apple Music token expired. Please authenticate again." } );
        }

        try {
            HttpClient client = httpClientFactory.CreateClient( "musickit-api" );

            // Add developer token (JWT) for API authentication
            if (jwtHandler != null) {
                client.DefaultRequestHeaders.Authorization = jwtHandler.NewAuthenticationHeader( );
            }

            // Add user token for accessing user's library
            client.DefaultRequestHeaders.Add( "Music-User-Token", user.AppleMusicUserToken );

            // Fetch all playlist tracks with pagination.
            // Uri.EscapeDataString is defense-in-depth; the [RegularExpression] annotation on
            // ProcessPlaylistRequest.PlaylistId has already enforced the safe character set via
            // ModelState validation above.
            string playlistId = Uri.EscapeDataString( request.PlaylistId );

            List<string> trackIds = [];
            string? nextUrl = $"https://api.music.apple.com/v1/me/library/playlists/{playlistId}/tracks";
            int totalTracks = 0;
            bool overonekay = false;
            while (!string.IsNullOrEmpty( nextUrl )) {
                HttpResponseMessage response = await client.GetAsync( nextUrl );

                if (!response.IsSuccessStatusCode) {
                    LogGetTracksFailed( user.Id, (int)response.StatusCode );
                    return StatusCode( (int)response.StatusCode, new { message = "Failed to retrieve playlist tracks from Apple Music" } );
                }

                string content = await response.Content.ReadAsStringAsync( );
                using JsonDocument doc = JsonDocument.Parse( content );

                // Extract track IDs
                if (doc.RootElement.TryGetProperty( "data", out JsonElement dataElement )) {
                    trackIds.AddRange(
                        dataElement.EnumerateArray( )
                            .Select( trackElement => {
                                return (
                                    trackElement.TryGetProperty( "attributes", out JsonElement attributesElement ) &&
                                    attributesElement.TryGetProperty( "playParams", out JsonElement playParamsElement ) &&
                                    playParamsElement.TryGetProperty( "catalogId", out JsonElement idElement )
                                )
                                ? idElement.GetString( )
                                : null;
                            } )
                            .Where( id => !string.IsNullOrEmpty( id ) )
                            .Cast<string>( )
                    );
                }

                // Check for pagination: look for a "next" link in the response
                if (doc.RootElement.TryGetProperty( "next", out JsonElement nextElement )) {
                    string? nextValue = nextElement.GetString( );
                    nextUrl = (!string.IsNullOrEmpty( nextValue ) && !nextValue.StartsWith( "http", StringComparison.OrdinalIgnoreCase ))
                        ? "https://api.music.apple.com" + nextValue
                        : nextValue;
                } else {
                    nextUrl = null; // No more pages
                }

                // Apple Music API limit: 100 tracks per request, cap processing at 1000 tracks
                if (doc.RootElement.TryGetProperty( "data", out JsonElement trackDataElement )) {
                    totalTracks += trackDataElement.GetArrayLength( );
                }
                if (totalTracks >= 1000) {
                    overonekay = true;
                    LogPlaylistTooLarge( request.PlaylistId.SanitizeForLogging( ) );
                    break;
                }
            }

            return Ok( new {
                success = true,
                message = overonekay ? $"First {trackIds.Count} items processed successfully" : "Playlist processed successfully",
                trackCount = trackIds.Count,
                trackIds,
                tooLarge = trackIds.Count > 100
            } );

        } catch (Exception ex) {
            LogProcessPlaylistError( ex, user.Id );
            return StatusCode( 500, new { message = "An error occurred while processing the playlist" } );
        }
    }

    /// <summary>
    /// Streams rendered result-card HTML for a batch of Apple Music song ids, writing each card to the
    /// response as it is resolved and flushing incrementally. Rate-limit messages are surfaced inline, and a
    /// trailing marker element reports processed, error, and rate-limited counts.
    /// </summary>
    /// <param name="songIds">The Apple Music catalog song ids to look up and render, bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>applemusic/playlist-results-stream</c>. Writes a chunked <c>text/html</c> stream directly
    /// to the response; sets status <c>401</c> when unauthenticated or <c>503</c> when Apple Music is not
    /// configured before writing a JSON error response. Requires a valid anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "applemusic/playlist-results-stream" )]
    public async Task PlaylistResultsStream( [FromBody] string[] songIds ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            Response.StatusCode = 401;
            await Response.WriteAsJsonAsync( new {
                error = "Authentication required",
                message = "Sign in before looking up Apple Music playlist tracks."
            } );
            return;
        }
        if (jwtHandler == null) {
            Response.StatusCode = 503;
            await Response.WriteAsJsonAsync( new {
                error = "Service unavailable",
                message = "Apple Music service is not configured."
            } );
            return;
        }
        Response.ContentType = "text/html; charset=utf-8";
        Response.Headers.Append( "Cache-Control", "no-cache" );
        Response.Headers.Append( "X-Accel-Buffering", "no" );
        int processedCount = 0;
        int errorCount = 0;
        int rateLimitCount = 0;
        try {
            foreach (string songId in songIds) {
                try {
                    MediaLinkResult? lookupResult = await mediaLinkService.GetInfoByProviderIdAsync( songId, SupportedProviders.AppleMusic, false );
                    if (lookupResult == null || lookupResult.Results.Count == 0) {
                        if (lookupResult?.Messages is { Count: > 0 }) {
                            rateLimitCount++;
                            string warningHtml = "<div class=\"alert alert-warning\"><strong>Rate Limited</strong><br/>"
                            + string.Join( "<br/>", lookupResult.Messages.Select( HtmlEncoder.Default.Encode ) )
                            + "</div>";
                            await Response.WriteAsync( warningHtml + StreamFraming.ItemDelimiter );
                            await Response.Body.FlushAsync( );
                        } else {
                            errorCount++;
                        }
                        continue;
                    }

                    MusicLookupResult? primaryResult = null;
                    SupportedProviders primaryProvider = SupportedProviders.AppleMusic;
                    foreach ((SupportedProviders provider, MusicLookupResult dto) in lookupResult.Results) {
                        if (dto.IsPrimary) {
                            primaryResult = dto;
                            primaryProvider = provider;
                            break;
                        }
                        if (primaryResult == null) {
                            primaryResult = dto;
                            primaryProvider = provider;
                        }
                    }

                    if (primaryResult == null) {
                        errorCount++;
                        continue;
                    }

                    string? cardUrl = null;
                    if (cardService?.IsEnabled == true) {
                        cardUrl = cardService.StoreResult( lookupResult );
                    }

                    string? atProtoUri = await GetATProtoUriFromCache( lookupResult );

                    string appleProbeKey = LookupKeyBuilder.TypedKey(
                    LookupRequestType.SongIdLookup, SupportedProviders.AppleMusic, songId.Trim( ) );
                    bool appleIsInProgress = probe is not null
                    && await probe.IsActiveAsync( appleProbeKey, HttpContext.RequestAborted );

                    MusicLookupViewModel.MusicLookupResultItem item = new( )
                {
                        CardUrl = cardUrl,
                        ATProtoUri = atProtoUri,
                        Result = lookupResult,
                        PrimaryProvider = primaryProvider,
                        PrimaryResult = primaryResult,
                        IsLookupInProgress = appleIsInProgress
                    };

                    string html = await RenderViewToStringAsync( "_LookupResultCard", item );
                    await Response.WriteAsync( html + StreamFraming.ItemDelimiter );
                    await Response.Body.FlushAsync( );
                    processedCount++;
                } catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    LogProcessSongError( ex, songId.SanitizeForLogging( ) );
                    errorCount++;
                }
            }
            if (processedCount == 0 && errorCount > 0) {
                await Response.WriteAsync( $"<div class=\"alert alert-warning\" data-stream-complete=\"true\" data-processed=\"0\" data-errors=\"{errorCount}\" data-rate-limited=\"{rateLimitCount}\">No results found for the tracks in this playlist</div>{StreamFraming.ItemDelimiter}" );
            } else if (processedCount == 0 && rateLimitCount > 0) {
                await Response.WriteAsync( $"<div class=\"d-none\" data-stream-complete=\"true\" data-processed=\"0\" data-errors=\"{errorCount}\" data-rate-limited=\"{rateLimitCount}\"></div>{StreamFraming.ItemDelimiter}" );
            } else if (errorCount > 0) {
                await Response.WriteAsync( $"<div class=\"d-none\" data-stream-complete=\"true\" data-processed=\"{processedCount}\" data-errors=\"{errorCount}\" data-rate-limited=\"{rateLimitCount}\"></div>{StreamFraming.ItemDelimiter}" );
            } else {
                await Response.WriteAsync( $"<div class=\"d-none\" data-stream-complete=\"true\" data-processed=\"{processedCount}\" data-errors=\"0\" data-rate-limited=\"{rateLimitCount}\"></div>{StreamFraming.ItemDelimiter}" );
            }
        } catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) {
            // The client disconnected; there is no response stream left to complete.
        } catch (Exception ex) {
            LogPlaylistResultsStreamError( ex );
            if (!HttpContext.RequestAborted.IsCancellationRequested) {
                await Response.WriteAsync( $"<div class=\"alert alert-danger\" data-stream-complete=\"true\" data-processed=\"{processedCount}\" data-errors=\"{errorCount + 1}\" data-rate-limited=\"{rateLimitCount}\">An error occurred while processing the playlist. Please try again.</div>{StreamFraming.ItemDelimiter}" );
            }
        }
    }

    /// <summary>
    /// Returns the Apple Music content partial for in-page rendering.
    /// </summary>
    /// <returns>The <c>_AppleMusicContent</c> partial view (HTTP GET <c>applemusic/content</c>).</returns>
    [HttpGet]
    [Route( "applemusic/content" )]
    public IActionResult ContentPartial( ) => PartialView( "_AppleMusicContent" );

    /// <summary>
    /// Renders a view to an HTML string using the supplied model, for streaming partial results to the client.
    /// </summary>
    /// <param name="viewName">The name of the view to render.</param>
    /// <param name="model">The model to bind to the view.</param>
    /// <returns>The rendered view as an HTML string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the named view cannot be found.</exception>
    private async Task<string> RenderViewToStringAsync( string viewName, object model ) {
        ViewData.Model = model;
        using StringWriter sw = new();
        ViewEngineResult viewResult = viewEngine.FindView( ControllerContext, viewName, false );

        if (!viewResult.Success) {
            throw new InvalidOperationException( $"View '{viewName}' not found" );
        }

        Microsoft.AspNetCore.Mvc.Rendering.ViewContext viewContext = new(
            ControllerContext,
            viewResult.View,
            ViewData,
            TempData,
            sw,
            new HtmlHelperOptions()
        );

        await viewResult.View.RenderAsync( viewContext );
        return sw.ToString( );
    }
}
