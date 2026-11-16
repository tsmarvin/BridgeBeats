using System.Diagnostics;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BridgeBeats.Web.Controllers {
    /// <summary>
    /// Serves the public site pages and browser-facing music-lookup flows: the home page, privacy and terms
    /// pages, a health check, and lookup endpoints that resolve media links by URL, ISRC, UPC, or
    /// title/artist and return rendered partials, streamed result cards, or JSON. Most services are optional
    /// and the controller degrades gracefully when they are not configured.
    /// </summary>
    /// <param name="logger">Logger for lookup-stream and cache events.</param>
    /// <param name="viewEngine">Composite view engine used to render result-card partials to HTML strings for streaming.</param>
    /// <param name="mediaLinkService">Optional media-link service used to resolve lookups; when null, lookup endpoints report that the service is unavailable.</param>
    /// <param name="cardService">Optional Open Graph card service used to store results and produce shareable card URLs.</param>
    /// <param name="cacheRepository">Optional cache repository used to resolve the ATProto URI for a result.</param>
    /// <param name="probe">Optional saga-progress probe used to set the "lookup in progress" indicator on result cards; when null, the indicator is never shown.</param>
    public partial class HomeController(
        ILogger<HomeController> logger,
        ICompositeViewEngine viewEngine,
        IMediaLinkService? mediaLinkService = null,
        IOpenGraphCardService? cardService = null,
        IMediaLinkCacheRepository? cacheRepository = null,
        ILookupProgressProbe? probe = null
    ) : Controller {

        /// <summary>
        /// Renders the home page.
        /// </summary>
        /// <returns>The default home view.</returns>
        public IActionResult Index( ) => View( );

        /// <summary>
        /// Resolves a media URL and renders the matching result cards as a partial view. Each result's primary
        /// provider is selected, an Open Graph card is stored when the card service is enabled, and the ATProto
        /// URI is resolved when available.
        /// </summary>
        /// <param name="uri">The media URL to resolve, bound from the form post.</param>
        /// <returns>
        /// HTTP POST. The <c>_LookupResults</c> partial view populated with results, or carrying a message when
        /// the service is unavailable, the URI is missing, or no results are found. Requires a valid anti-forgery token.
        /// </returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LookupResults( string uri ) {
            if (mediaLinkService == null) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "Music lookup service not available"
                } );
            }

            if (string.IsNullOrWhiteSpace( uri )) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "URI is required"
                } );
            }

            // Perform lookup server-side and collect all results
            MusicLookupViewModel viewModel = new( );
            List<string> placeholderMessages = [];
            await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( uri )) {
                if (result.Results.Count == 0) {
                    if (result.Messages is { Count: > 0 }) {
                        placeholderMessages.AddRange(
                            result.Messages.Where( message => !string.IsNullOrWhiteSpace( message ) ) );
                    }
                    continue;
                }

                // Find primary result
                MusicLookupResult? primaryResult = null;
                SupportedProviders primaryProvider = default;
                foreach ((SupportedProviders provider, MusicLookupResult dto) in result.Results) {
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
                    continue;
                }

                // Store result and get card URL (server-side only)
                string? cardUrl = null;
                if (cardService?.IsEnabled == true) {
                    cardUrl = cardService.StoreResult( result );
                }

                // Get ATProto URI from cache if available
                string? atProtoUri = await GetATProtoUriFromCache( result );

                string? urlProbeKey = result.InputLinks.Count > 0
                    ? LookupKeyBuilder.UrlKey( result.InputLinks[0] )
                    : null;
                bool isLookupInProgress = urlProbeKey is not null && probe is not null
                    && await probe.IsActiveAsync( urlProbeKey, HttpContext.RequestAborted );

                viewModel.Items.Add( new MusicLookupViewModel.MusicLookupResultItem {
                    CardUrl = cardUrl,
                    ATProtoUri = atProtoUri,
                    Result = result,
                    PrimaryProvider = primaryProvider,
                    PrimaryResult = primaryResult,
                    IsLookupInProgress = isLookupInProgress
                } );
            }

            if (viewModel.Items.Count == 0) {
                viewModel.Message = placeholderMessages.Count > 0
                    ? string.Join( " ", placeholderMessages )
                    : "No results found";
            }

            return PartialView( "_LookupResults", viewModel );
        }

        /// <summary>
        /// Resolves a recording by ISRC and renders the result as a partial view.
        /// </summary>
        /// <param name="isrc">The ISRC to resolve, bound from the form post.</param>
        /// <returns>
        /// HTTP POST. The <c>_LookupResults</c> partial view with the result, or carrying a message when the
        /// service is unavailable, the ISRC is missing, or no result is found. Requires a valid anti-forgery token.
        /// </returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LookupResultsByIsrc( string isrc ) {
            if (mediaLinkService == null) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "Music lookup service not available"
                } );
            }

            if (string.IsNullOrWhiteSpace( isrc )) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "ISRC is required"
                } );
            }

            MediaLinkResult? result = await mediaLinkService.GetInfoByISRCAsync( isrc );
            return await CreateViewModelFromResult( result, "No results found for ISRC", LookupKeyBuilder.IsrcKey( isrc ) );
        }

        /// <summary>
        /// Resolves a release by UPC and renders the result as a partial view.
        /// </summary>
        /// <param name="upc">The UPC to resolve, bound from the form post.</param>
        /// <returns>
        /// HTTP POST. The <c>_LookupResults</c> partial view with the result, or carrying a message when the
        /// service is unavailable, the UPC is missing, or no result is found. Requires a valid anti-forgery token.
        /// </returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LookupResultsByUpc( string upc ) {
            if (mediaLinkService == null) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "Music lookup service not available"
                } );
            }

            if (string.IsNullOrWhiteSpace( upc )) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "UPC is required"
                } );
            }

            MediaLinkResult? result = await mediaLinkService.GetInfoByUPCAsync( upc );
            return await CreateViewModelFromResult( result, "No results found for UPC", LookupKeyBuilder.UpcKey( upc ) );
        }

        /// <summary>
        /// Resolves a track or album by title and artist and renders the result as a partial view.
        /// </summary>
        /// <param name="title">The title to resolve, bound from the form post.</param>
        /// <param name="artist">The artist to resolve, bound from the form post.</param>
        /// <returns>
        /// HTTP POST. The <c>_LookupResults</c> partial view with the result, or carrying a message when the
        /// service is unavailable, either field is missing, or no result is found. Requires a valid anti-forgery token.
        /// </returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LookupResultsByTitle( string title, string artist ) {
            if (mediaLinkService == null) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "Music lookup service not available"
                } );
            }

            if (string.IsNullOrWhiteSpace( title ) || string.IsNullOrWhiteSpace( artist )) {
                return PartialView( "_LookupResults", new MusicLookupViewModel {
                    Message = "Title and artist are required"
                } );
            }

            MediaLinkResult? result = await mediaLinkService.GetInfoAsync( title, artist );
            return await CreateViewModelFromResult( result, "No results found for title/artist", LookupKeyBuilder.MetadataKey( title, artist ) );
        }

        /// <summary>
        /// Builds a single-item lookup view model from a resolved result, selecting the primary provider,
        /// storing an Open Graph card when enabled, resolving the ATProto URI, and probing the saga to
        /// set the "lookup in progress" indicator. Preserves provider guidance from result-less placeholders,
        /// falling back to the supplied no-results message only when no guidance is available.
        /// </summary>
        /// <param name="result">The resolved media-link result, or null when nothing matched.</param>
        /// <param name="noResultsMessage">The message to display when no usable result is present.</param>
        /// <param name="lookupKey">The normalized lookup key used to probe saga progress.</param>
        /// <returns>The <c>_LookupResults</c> partial view populated with the item or the no-results message.</returns>
        private async Task<IActionResult> CreateViewModelFromResult(
            MediaLinkResult? result,
            string noResultsMessage,
            string lookupKey
        ) {
            MusicLookupViewModel viewModel = new( );

            if (result == null || result.Results.Count == 0) {
                viewModel.Message = GetResultMessage( result, noResultsMessage );
                return PartialView( "_LookupResults", viewModel );
            }

            // Find primary result
            MusicLookupResult? primaryResult = null;
            SupportedProviders primaryProvider = default;
            foreach ((SupportedProviders provider, MusicLookupResult dto) in result.Results) {
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
                viewModel.Message = GetResultMessage( result, noResultsMessage );
                return PartialView( "_LookupResults", viewModel );
            }

            // Store result and get card URL
            string? cardUrl = null;
            if (cardService?.IsEnabled == true) {
                cardUrl = cardService.StoreResult( result );
            }

            // Get ATProto URI from cache if available
            string? atProtoUri = await GetATProtoUriFromCache( result );

            bool isLookupInProgress = probe is not null
                && await probe.IsActiveAsync( lookupKey, HttpContext.RequestAborted );

            viewModel.Items.Add( new MusicLookupViewModel.MusicLookupResultItem {
                CardUrl = cardUrl,
                ATProtoUri = atProtoUri,
                Result = result,
                PrimaryProvider = primaryProvider,
                PrimaryResult = primaryResult,
                IsLookupInProgress = isLookupInProgress
            } );

            return PartialView( "_LookupResults", viewModel );
        }

        private static string GetResultMessage( MediaLinkResult? result, string fallback ) {
            if (result?.Messages is not { Count: > 0 }) {
                return fallback;
            }

            string message = string.Join(
                " ",
                result.Messages.Where( value => !string.IsNullOrWhiteSpace( value ) )
            );
            return string.IsNullOrWhiteSpace( message ) ? fallback : message;
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
        /// Renders the privacy policy page.
        /// </summary>
        /// <returns>The privacy view.</returns>
        public IActionResult Privacy( ) => View( );

        /// <summary>
        /// Renders the terms-of-service page.
        /// </summary>
        /// <returns>The terms-of-service view.</returns>
        public IActionResult Tos( ) => View( );

        /// <summary>
        /// Health-check endpoint reporting that the application is responsive.
        /// </summary>
        /// <returns>
        /// HTTP GET at the configured health path. <c>200 OK</c> with a status and timestamp. Responses are not cached.
        /// </returns>
        [HttpGet( EndpointPaths.Health )]
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Health( ) {
            return Ok( new { status = "healthy", timestamp = DateTime.UtcNow } );
        }

        /// <summary>
        /// Resolves a media URL for non-browser web clients, returning a JSON payload of result items (each with
        /// an optional card URL and the underlying result) rather than rendered HTML. The endpoint accepts JSON
        /// via <c>[FromBody]</c>, so CSRF protection is not applicable.
        /// </summary>
        /// <param name="req">The request containing the media URL, bound from the JSON request body.</param>
        /// <returns>
        /// HTTP POST <c>/lookup/web</c>. <c>200 OK</c> with <c>hasResults</c> and the items, or a no-results
        /// payload; <c>400 Bad Request</c> when the service is unavailable or the URI is missing. Anti-forgery
        /// validation is ignored for this endpoint.
        /// </returns>
        [HttpPost( "/lookup/web" )]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> WebLookup( [FromBody] WebLookupRequest req ) {
            if (mediaLinkService == null) {
                return BadRequest( new { error = "Music lookup service not available" } );
            }

            if (string.IsNullOrWhiteSpace( req.Uri )) {
                return BadRequest( new { error = "URI is required" } );
            }

            // Perform lookup server-side and collect all results
            List<WebLookupResultItem> items = [];
            await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( req.Uri )) {
                if (result.Results.Count == 0) {
                    continue; // Skip empty results
                }

                // Store result and get card URL (server-side only)
                string? cardUrl = null;
                if (cardService?.IsEnabled == true) {
                    cardUrl = cardService.StoreResult( result );
                }

                items.Add( new WebLookupResultItem( cardUrl, result ) );
            }

            if (items.Count == 0) {
                return Ok( new { hasResults = false, message = "No results found" } );
            }

            return Ok( new { hasResults = true, items } );
        }

        /// <summary>
        /// Resolves a media URL and streams rendered result-card HTML to the response as each result is
        /// produced, flushing incrementally. Rate-limit messages are surfaced inline, and a trailing marker
        /// element reports processed, error, and rate-limited counts.
        /// </summary>
        /// <param name="uri">The media URL to resolve, bound from the form post.</param>
        /// <returns>
        /// HTTP POST <c>/Home/LookupResultsStream</c>. Writes a chunked <c>text/html</c> stream directly to the
        /// response; sets status <c>503</c> when the service is unavailable or <c>400</c> when the URI is missing
        /// before writing an error fragment. Requires a valid anti-forgery token.
        /// </returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route( "/Home/LookupResultsStream" )]
        public async Task LookupResultsStream( string uri ) {
            if (mediaLinkService == null) {
                Response.StatusCode = 503;
                await Response.WriteAsync( "<div class=\"alert alert-danger\">Music lookup service not available</div>" );
                return;
            }

            if (string.IsNullOrWhiteSpace( uri )) {
                Response.StatusCode = 400;
                await Response.WriteAsync( "<div class=\"alert alert-danger\">URI is required</div>" );
                return;
            }

            Response.ContentType = "text/html; charset=utf-8";
            Response.Headers.Append( "Cache-Control", "no-cache" );
            Response.Headers.Append( "X-Accel-Buffering", "no" ); // Disable nginx buffering

            int processedCount = 0;
            int errorCount = 0;
            int rateLimitCount = 0;

            try {
                await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( uri )) {
                    try {
                        if (result.Results.Count == 0) {
                            // Check if this is a rate-limited result with messages
                            if (result.Messages is { Count: > 0 }) {
                                rateLimitCount++;
                                string warningHtml = "<div class=\"alert alert-warning\"><strong>Rate Limited</strong><br/>"
                                    + string.Join( "<br/>", result.Messages )
                                    + "</div>";
                                await Response.WriteAsync( warningHtml );
                                await Response.Body.FlushAsync( );
                            } else {
                                errorCount++;
                            }
                            continue;
                        }

                        // Find primary result
                        MusicLookupResult? primaryResult = null;
                        SupportedProviders primaryProvider = default;
                        foreach ((SupportedProviders provider, MusicLookupResult dto) in result.Results) {
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

                        // Store result and get card URL
                        string? cardUrl = null;
                        if (cardService?.IsEnabled == true) {
                            cardUrl = cardService.StoreResult( result );
                        }

                        // Get ATProto URI from cache if available
                        string? atProtoUri = await GetATProtoUriFromCache( result );

                        string? streamProbeKey = result.InputLinks.Count > 0
                            ? LookupKeyBuilder.UrlKey( result.InputLinks[0] )
                            : null;
                        bool streamIsInProgress = streamProbeKey is not null && probe is not null
                            && await probe.IsActiveAsync( streamProbeKey, HttpContext.RequestAborted );

                        // Create single-item model
                        MusicLookupViewModel.MusicLookupResultItem item = new( ) {
                            CardUrl = cardUrl,
                            ATProtoUri = atProtoUri,
                            Result = result,
                            PrimaryProvider = primaryProvider,
                            PrimaryResult = primaryResult,
                            IsLookupInProgress = streamIsInProgress
                        };

                        // Render partial view to string and stream it
                        string html = await RenderViewToStringAsync( "_LookupResultCard", item );
                        await Response.WriteAsync( html );
                        await Response.Body.FlushAsync( );

                        processedCount++;

                    } catch (Exception ex) {
                        LogStreamResultError( ex, uri.SanitizeForLogging( ) );
                        errorCount++;
                    }
                }

                // Send completion status as a hidden data element
                if (processedCount == 0 && errorCount > 0) {
                    await Response.WriteAsync( $"<div class=\"alert alert-warning\" data-stream-complete=\"true\" data-processed=\"0\" data-errors=\"{errorCount}\" data-rate-limited=\"{rateLimitCount}\">No results found</div>" );
                } else if (processedCount == 0 && rateLimitCount > 0) {
                    await Response.WriteAsync( $"<div class=\"d-none\" data-stream-complete=\"true\" data-processed=\"0\" data-errors=\"{errorCount}\" data-rate-limited=\"{rateLimitCount}\"></div>" );
                } else if (processedCount == 0) {
                    await Response.WriteAsync( "<div class=\"alert alert-info\" data-stream-complete=\"true\" data-processed=\"0\" data-errors=\"0\" data-rate-limited=\"0\">No results found</div>" );
                } else if (errorCount > 0) {
                    await Response.WriteAsync( $"<div class=\"d-none\" data-stream-complete=\"true\" data-processed=\"{processedCount}\" data-errors=\"{errorCount}\" data-rate-limited=\"{rateLimitCount}\"></div>" );
                } else {
                    await Response.WriteAsync( $"<div class=\"d-none\" data-stream-complete=\"true\" data-processed=\"{processedCount}\" data-errors=\"0\" data-rate-limited=\"{rateLimitCount}\"></div>" );
                }

            } catch (Exception ex) {
                LogStreamError( ex, uri.SanitizeForLogging( ) );
                await Response.WriteAsync( $"<div class=\"alert alert-danger\" data-stream-complete=\"true\" data-processed=\"{processedCount}\" data-errors=\"{errorCount + 1}\">An error occurred during lookup: {ex.Message}</div>" );
            }
        }

        /// <summary>
        /// Renders a view to an HTML string using the supplied model, for streaming partial results to the client.
        /// </summary>
        /// <param name="viewName">The name of the view to render.</param>
        /// <param name="model">The model to bind to the view.</param>
        /// <returns>The rendered view as an HTML string.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the named view cannot be found.</exception>
        private async Task<string> RenderViewToStringAsync( string viewName, object model ) {
            ViewData.Model = model;
            using StringWriter sw = new( );
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
                new HtmlHelperOptions( )
            );

            await viewResult.View.RenderAsync( viewContext );
            return sw.ToString( );
        }

        /// <summary>
        /// Renders the error page with the current request id for correlation.
        /// </summary>
        /// <returns>The error view populated with an <c>ErrorViewModel</c>. Responses are not cached.</returns>
        [ResponseCache( Duration = 0, Location = ResponseCacheLocation.None, NoStore = true )]
        public IActionResult Error( ) {
            return View( new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier } );
        }

        /// <summary>
        /// Resolves a recording by ISRC for authenticated browser clients and returns the result as JSON. Uses
        /// cookie authentication and requires the antiforgery token in the <c>X-XSRF-TOKEN</c> header.
        /// </summary>
        /// <param name="req">The request containing the ISRC, bound from the JSON request body.</param>
        /// <returns>
        /// HTTP POST <c>/lookup/browser/isrc</c>. <c>200 OK</c> with the result or a result carrying a
        /// no-results message; <c>400 Bad Request</c> when the service is unavailable or the ISRC is missing.
        /// Requires <c>[Authorize]</c> and a valid anti-forgery token.
        /// </returns>
        [Authorize]
        [HttpPost( "/lookup/browser/isrc" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BrowserLookupByIsrc( [FromBody] BrowserIsrcRequest req ) {
            if (mediaLinkService == null) {
                return BadRequest( new { error = "Music lookup service not available" } );
            }

            if (string.IsNullOrWhiteSpace( req.Isrc )) {
                return BadRequest( new { error = "ISRC is required" } );
            }

            MediaLinkResult? result = await mediaLinkService.GetInfoByISRCAsync( req.Isrc );
            return Ok( result ?? new MediaLinkResult { Messages = ["No results found for ISRC."] } );
        }

        /// <summary>
        /// Resolves a release by UPC for authenticated browser clients and returns the result as JSON. Uses
        /// cookie authentication and requires the antiforgery token in the <c>X-XSRF-TOKEN</c> header.
        /// </summary>
        /// <param name="req">The request containing the UPC, bound from the JSON request body.</param>
        /// <returns>
        /// HTTP POST <c>/lookup/browser/upc</c>. <c>200 OK</c> with the result or a result carrying a
        /// no-results message; <c>400 Bad Request</c> when the service is unavailable or the UPC is missing.
        /// Requires <c>[Authorize]</c> and a valid anti-forgery token.
        /// </returns>
        [Authorize]
        [HttpPost( "/lookup/browser/upc" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BrowserLookupByUpc( [FromBody] BrowserUpcRequest req ) {
            if (mediaLinkService == null) {
                return BadRequest( new { error = "Music lookup service not available" } );
            }

            if (string.IsNullOrWhiteSpace( req.Upc )) {
                return BadRequest( new { error = "UPC is required" } );
            }

            MediaLinkResult? result = await mediaLinkService.GetInfoByUPCAsync( req.Upc );
            return Ok( result ?? new MediaLinkResult { Messages = ["No results found for UPC."] } );
        }

        /// <summary>
        /// Resolves a track or album by title and artist for authenticated browser clients and returns the
        /// result as JSON. Uses cookie authentication and requires the antiforgery token in the
        /// <c>X-XSRF-TOKEN</c> header.
        /// </summary>
        /// <param name="req">The request containing the title and artist, bound from the JSON request body.</param>
        /// <returns>
        /// HTTP POST <c>/lookup/browser/title</c>. <c>200 OK</c> with the result or a result carrying a
        /// no-results message; <c>400 Bad Request</c> when the service is unavailable or either field is missing.
        /// Requires <c>[Authorize]</c> and a valid anti-forgery token.
        /// </returns>
        [Authorize]
        [HttpPost( "/lookup/browser/title" )]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BrowserLookupByTitle( [FromBody] BrowserTitleRequest req ) {
            if (mediaLinkService == null) {
                return BadRequest( new { error = "Music lookup service not available" } );
            }

            if (string.IsNullOrWhiteSpace( req.Title ) || string.IsNullOrWhiteSpace( req.Artist )) {
                return BadRequest( new { error = "Title and artist are required" } );
            }

            MediaLinkResult? result = await mediaLinkService.GetInfoAsync( req.Title, req.Artist );
            return Ok( result ?? new MediaLinkResult { Messages = ["No results found."] } );
        }
    }

    /// <summary>Request payload for a browser ISRC lookup.</summary>
    /// <param name="Isrc">The ISRC to resolve.</param>
    public record BrowserIsrcRequest( string Isrc );

    /// <summary>Request payload for a browser UPC lookup.</summary>
    /// <param name="Upc">The UPC to resolve.</param>
    public record BrowserUpcRequest( string Upc );

    /// <summary>Request payload for a browser title/artist lookup.</summary>
    /// <param name="Title">The title to resolve.</param>
    /// <param name="Artist">The artist to resolve.</param>
    public record BrowserTitleRequest( string Title, string Artist );
}
