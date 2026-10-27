using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Authentication;
using BridgeBeats.Web.Configuration;
using idunno.AtProto;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Handles account flows for the web application: registration, login, logout, API-key issuance and
/// regeneration, authentication status, personal-data export, account deletion, and ATProto (Bluesky)
/// OAuth sign-in. Action routes are rooted under the <c>account/</c> prefix.
/// </summary>
/// <param name="userManager">ASP.NET Core Identity user manager used to find, create, update, and delete users.</param>
/// <param name="signInManager">ASP.NET Core Identity sign-in manager used to establish and clear authentication sessions.</param>
/// <param name="logger">Logger for account lifecycle and OAuth events.</param>
/// <param name="hasher">Generates API keys and produces the stored hash so the raw key is never persisted.</param>
public partial class AccountController(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ILogger<AccountController> logger,
    ApiKeyHasher hasher
) : Controller {

    /// <summary>Shared JSON serializer options that emit indented output, used for the personal-data export file.</summary>
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new( ) { WriteIndented = true };

    /// <summary>
    /// Renders the registration page.
    /// </summary>
    /// <returns>The <c>Register</c> view (HTTP GET <c>account/register</c>).</returns>
    [HttpGet]
    [Route( "account/register" )]
    public IActionResult RegisterPage( ) {
        return View( "Register" );
    }

    /// <summary>
    /// Renders the login page.
    /// </summary>
    /// <returns>The <c>Login</c> view (HTTP GET <c>account/login</c>).</returns>
    [HttpGet]
    [Route( "account/login" )]
    public IActionResult LoginPage( ) {
        return View( "Login" );
    }

    /// <summary>
    /// Renders the authenticated user's account page.
    /// </summary>
    /// <returns>The <c>User</c> view (HTTP GET <c>account/user</c>); requires an authenticated session via <c>[Authorize]</c>.</returns>
    [Authorize]
    [HttpGet]
    [Route( "account/user" )]
    public IActionResult UserPage( ) {
        return View( "User" );
    }

    /// <summary>
    /// Registers a new account from the supplied credentials, signs the user in, and returns a freshly
    /// issued API key. Fails if the email is already registered.
    /// </summary>
    /// <param name="request">The registration payload (email and password) bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>account/register</c>. <c>200 OK</c> with the new user id, the one-time API key, and a
    /// message on success; <c>400 Bad Request</c> with model-state errors when validation fails or the email
    /// is already in use. Requires a valid anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "account/register" )]
    public async Task<IActionResult> Register( [FromBody] RegisterRequest request ) {
        if (!ModelState.IsValid) {
            return BadRequest( ModelState );
        }

        // Explicit duplicate-email check: RequireUniqueEmail is disabled at the Identity level
        // (ATProto users have null email), so enforce uniqueness for password accounts here.
        ApplicationUser? existingUser = await userManager.FindByEmailAsync( request.Email );
        if (existingUser is not null) {
            ModelState.AddModelError( string.Empty, userManager.ErrorDescriber.DuplicateEmail( request.Email ).Description );
            return BadRequest( ModelState );
        }

        // Generate API key
        (string apiKey, string hashedApiKey) = IssueApiKey( );

        ApplicationUser user = new() {
            UserName = request.Email,
            Email = request.Email,
            ApiKeyHash = hashedApiKey,
            CreatedAt = DateTime.UtcNow
        };

        // Validate before ending a live session. CreateAsync validates again after cleanup.
        bool validationSucceeded = true;
        foreach (IPasswordValidator<ApplicationUser> validator in userManager.PasswordValidators) {
            IdentityResult validation = await validator.ValidateAsync( userManager, user, request.Password );
            validationSucceeded &= validation.Succeeded;
            foreach (IdentityError error in validation.Errors) {
                ModelState.AddModelError( string.Empty, error.Description );
            }
        }
        foreach (IUserValidator<ApplicationUser> validator in userManager.UserValidators) {
            IdentityResult validation = await validator.ValidateAsync( userManager, user );
            validationSucceeded &= validation.Succeeded;
            foreach (IdentityError error in validation.Errors) {
                ModelState.AddModelError( string.Empty, error.Description );
            }
        }
        if (!validationSucceeded) {
            return BadRequest( ModelState );
        }

        await SignOutBlueskyAsync( );

        IdentityResult result;
        try {
            result = await userManager.CreateAsync( user, request.Password );
        } catch (DbUpdateException dbEx) when (dbEx.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 19 && sqliteEx.Message.Contains( "AspNetUsers.NormalizedEmail", StringComparison.OrdinalIgnoreCase )) {
            // A concurrent registration slipped past the FindByEmailAsync pre-check and hit the
            // DB-level unique index on NormalizedEmail. Surface as the same friendly 400 the
            // pre-check produces rather than letting an unhandled 500 reach the client.
            ModelState.AddModelError( string.Empty, userManager.ErrorDescriber.DuplicateEmail( request.Email ).Description );
            return BadRequest( ModelState );
        }

        if (!result.Succeeded) {
            foreach (IdentityError error in result.Errors) {
                ModelState.AddModelError( string.Empty, error.Description );
            }
            return BadRequest( ModelState );
        }

        // Sign in the user automatically with a cookie for web UI
        HttpContext.Items[BlueskyAuthenticationConfiguration.PasswordSignInKey] = true;
        await signInManager.SignInAsync( user, isPersistent: false );

        LogUserRegistered( user.Id );

        return Ok( new {
            userId = user.Id,
            apiKey,
            message = "Registration successful. Save your API key - it will not be shown again."
        } );
    }

    /// <summary>
    /// Authenticates a user with email and password. On success the user is signed in; if the account
    /// has no stored API-key hash yet, a new API key is generated and persisted, otherwise the response
    /// indicates an existing key is in use without exposing it.
    /// </summary>
    /// <param name="request">The login payload (email and password) bound from the JSON request body.</param>
    /// <returns>
    /// HTTP POST <c>account/login</c>. <c>200 OK</c> with the user id and either a newly generated API key
    /// or a placeholder indicating an existing key; <c>401 Unauthorized</c> for an unknown email or wrong
    /// password; <c>400 Bad Request</c> when the model is invalid. Requires a valid anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "account/login" )]
    public async Task<IActionResult> Login( [FromBody] LoginRequest request ) {
        if (!ModelState.IsValid) {
            return BadRequest( ModelState );
        }

        ApplicationUser? user = await userManager.FindByEmailAsync( request.Email );
        if (user == null) {
            return Unauthorized( new { message = "Invalid email or password" } );
        }

        Microsoft.AspNetCore.Identity.SignInResult result = await signInManager.CheckPasswordSignInAsync(
            user,
            request.Password,
            lockoutOnFailure: false
        );

        if (!result.Succeeded) {
            return Unauthorized( new { message = "Invalid email or password" } );
        }

        // Sign in the user with a cookie for web UI
        await SignOutBlueskyAsync( );
        await ReloadLinkedAccountAsync( user );
        HttpContext.Items[BlueskyAuthenticationConfiguration.PasswordSignInKey] = true;
        await signInManager.SignInAsync( user, isPersistent: false );

        // Generate new API key only if user doesn't have one
        string apiKey;
        if (string.IsNullOrEmpty( user.ApiKeyHash )) {
            (apiKey, user.ApiKeyHash) = IssueApiKey( );
            _ = await userManager.UpdateAsync( user );
        } else {
            // Return a message that the API key is already set
            // User should use regenerate-api-key endpoint if they need a new one
            apiKey = "***EXISTING_KEY***";
        }

        LogUserLoggedIn( user.Id );

        return Ok( new {
            userId = user.Id,
            apiKey,
            message = apiKey == "***EXISTING_KEY***"
                ? "Login successful. Use /account/regenerate-api-key to get a new API key if needed."
                : "Login successful. New API key generated."
        } );
    }

    /// <summary>
    /// Signs the current user out, clearing the authentication session.
    /// </summary>
    /// <returns>HTTP POST <c>account/logout</c>. <c>200 OK</c> with a confirmation message. Requires a valid anti-forgery token.</returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "account/logout" )]
    public async Task<IActionResult> Logout( ) {
        await SignOutBlueskyAsync( );
        await signInManager.SignOutAsync( );
        return Ok( new { message = "Logged out successfully" } );
    }

    /// <summary>
    /// Issues an anti-forgery request token and stores its companion cookie, allowing browser clients to
    /// supply the token on subsequent state-changing requests in the <c>X-XSRF-TOKEN</c> header.
    /// </summary>
    /// <param name="antiforgery">The anti-forgery service, resolved from the request services, used to generate and store the token pair.</param>
    /// <returns>HTTP GET <c>account/antiforgery-token</c>. <c>200 OK</c> with the request token in JSON.</returns>
    [HttpGet]
    [Route( "account/antiforgery-token" )]
    public IActionResult GetAntiforgeryToken( [FromServices] Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery ) {
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens( HttpContext );
        return Ok( new { token = tokens.RequestToken } );
    }

    /// <summary>
    /// Reports whether the caller is currently authenticated and, if so, the associated user id and email.
    /// </summary>
    /// <returns>
    /// HTTP GET <c>account/status</c>. <c>200 OK</c> with <c>isAuthenticated = true</c> plus the user id and
    /// email when signed in, or <c>isAuthenticated = false</c> otherwise.
    /// </returns>
    [HttpGet]
    [Route( "account/status" )]
    public async Task<IActionResult> GetAuthStatus( ) {
        if (User.Identity?.IsAuthenticated == true) {
            ApplicationUser? user = await userManager.GetUserAsync( User );
            return Ok( new {
                isAuthenticated = true,
                userId = user?.Id,
                email = user?.Email
            } );
        }
        return Ok( new { isAuthenticated = false } );
    }

    /// <summary>
    /// Generates a new API key for the authenticated user, replacing the stored hash. The previous key
    /// stops working once the update succeeds.
    /// </summary>
    /// <returns>
    /// HTTP POST <c>account/regenerate-api-key</c>. <c>200 OK</c> with the new one-time API key on success;
    /// <c>400 Bad Request</c> if the update fails; <c>401 Unauthorized</c> if no user is resolved. Requires
    /// <c>[Authorize]</c> and a valid anti-forgery token.
    /// </returns>
    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "account/regenerate-api-key" )]
    public async Task<IActionResult> RegenerateApiKey( ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( );
        }

        (string apiKey, user.ApiKeyHash) = IssueApiKey( );
        IdentityResult result = await userManager.UpdateAsync( user );

        if (!result.Succeeded) {
            return BadRequest( new { message = "Failed to regenerate API key" } );
        }

        LogApiKeyRegenerated( user.Id );

        return Ok( new {
            apiKey,
            message = "API key regenerated successfully. Update your applications with the new key."
        } );
    }

    /// <summary>
    /// Exports the authenticated user's personal data as a downloadable JSON file, including profile,
    /// rate-limiting counters, third-party token expiry, and the user's playlists (when the playlist
    /// service is available).
    /// </summary>
    /// <param name="playlistService">Optional playlist service, resolved from request services, used to include the user's playlists in the export; when null, no playlists are included.</param>
    /// <returns>
    /// HTTP POST <c>account/download-data</c>. A JSON file attachment named
    /// <c>bridgebeats-personal-data-{date}.json</c> on success; <c>401 Unauthorized</c> if no user is
    /// resolved. Requires <c>[Authorize]</c> and a valid anti-forgery token.
    /// </returns>
    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "account/download-data" )]
    public async Task<IActionResult> DownloadPersonalData( [FromServices] IPlaylistService? playlistService ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( );
        }

        // Get user's playlists
        List<PlaylistEntryDto> playlists = playlistService != null
            ? await playlistService.ExportUserDataAsync( user.Id )
            : [];

        var personalData = new {
            exportDate = DateTime.UtcNow,
            user = new {
                userId = user.Id,
                email = user.Email,
                createdAt = user.CreatedAt,
                emailConfirmed = user.EmailConfirmed
            },
            rateLimitingData = new {
                requestCount = user.RequestCount,
                rateLimitWindowStart = user.RateLimitWindowStart
            },
            thirdParty = new {
                appleMusicTokenExpiration = user.AppleMusicTokenExpiration
            },
            playlists = playlists.Select( p => new {
                playlistId = p.PlaylistId,
                title = p.Title,
                description = p.Description,
                cardIds = p.CardIds.Split( ',', StringSplitOptions.RemoveEmptyEntries ).ToList( ),
                createdAt = p.CreatedAt,
                url = $"https://{Request.Host}/playlist/{p.PlaylistId}"
            } )
        };

        LogDataDownloaded( user.Id );

        string json = JsonSerializer.Serialize(
            personalData,
            s_indentedJsonOptions
        );

        return File(
            System.Text.Encoding.UTF8.GetBytes( json ),
            "application/json",
            $"bridgebeats-personal-data-{DateTime.UtcNow:yyyy-MM-dd}.json"
        );
    }

    /// <summary>
    /// Permanently deletes the authenticated user's account. The user is signed out first, then the
    /// account and its associated identity data are removed.
    /// </summary>
    /// <returns>
    /// HTTP POST <c>account/delete</c>. <c>200 OK</c> with a confirmation message on success;
    /// <c>400 Bad Request</c> if deletion fails; <c>401 Unauthorized</c> if no user is resolved. Requires
    /// <c>[Authorize]</c> and a valid anti-forgery token.
    /// </returns>
    [Authorize]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route( "account/delete" )]
    public async Task<IActionResult> DeleteAccount( ) {
        ApplicationUser? user = await userManager.GetUserAsync( User );
        if (user == null) {
            return Unauthorized( );
        }

        // Revoke the linked OAuth session even when deletion is authenticated with an API key.
        BlueskySessionService? sessions = GetBlueskySessions( );
        if (sessions is not null) {
            await sessions.EndSessionAsync( HttpContext, user.AtProtoDid );
            await ReloadLinkedAccountAsync( user );
        }
        await signInManager.SignOutAsync( );

        // Delete the user
        IdentityResult result = await userManager.DeleteAsync( user );

        if (!result.Succeeded) {
            LogDeleteFailed( user.Id );
            return BadRequest( new { message = "Failed to delete account. Please try again." } );
        }

        LogAccountDeleted( user.Id );

        return Ok( new {
            message = "Account deleted successfully. All your personal data has been removed."
        } );
    }

    /// <summary>Starts SDK-managed OAuth and sets its browser correlation cookie.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting( BlueskyAuthenticationConfiguration.LoginStartRateLimitPolicy )]
    [Route( "account/login-atproto" )]
    public async Task<IActionResult> LoginWithAtProto( [FromBody] AtProtoLoginRequest request ) {
        BlueskySignInManager? blueskySignInManager = HttpContext.RequestServices.GetService<BlueskySignInManager>( );
        if (blueskySignInManager is null) {
            return BadRequest( new { message = "ATProto OAuth is not configured on this server." } );
        }
        if (string.IsNullOrWhiteSpace( request.Handle )) {
            return BadRequest( new { message = "Please enter your Bluesky handle." } );
        }
        try {
            Handle handle = new( request.Handle.Trim( ).TrimStart( '@' ) );
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource( HttpContext.RequestAborted );
            deadline.CancelAfter( TimeSpan.FromSeconds( BlueskyAuthenticationConfiguration.LoginStartTimeoutSeconds ) );
            Uri authorizationUrl = await blueskySignInManager.CreateRedirectUri(
                handle, cancellationToken: deadline.Token );
            return Ok( new { authorizationUrl = authorizationUrl.ToString( ) } );
        } catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested) {
            LogAtProtoOAuthStartFailed( ex, request.Handle );
            return BadRequest( new { message = "Failed to start login. Please check your handle and try again." } );
        }
    }

    /// <summary>Completes SDK-managed OAuth, including correlation, token validation and local account mapping.</summary>
    [HttpGet]
    [Route( "account/atproto-callback" )]
    public async Task<IActionResult> AtProtoCallback( [FromServices] BlueskySignInManager? blueskySignInManager ) {
        if (Request.Query.TryGetValue( "error", out var oauthError )) {
            string code = oauthError.ToString( );
            LogAtProtoOAuthServerError( code.Length is > 0 and <= 64 &&
                code.All( character => char.IsAsciiLetterOrDigit( character ) || character == '_' ) ? code : "invalid_error_code" );
        }
        if (blueskySignInManager is not null) {
            try {
                idunno.Bluesky.AspNet.Authentication.SignInResult result = await blueskySignInManager.SignIn( );
                if (result.Succeeded) {
                    return Redirect( "/" );
                }
                LogAtProtoCallbackRejected( result.MissingQueryString ? "missing_query" :
                    result.MissingCorrelationState ? "missing_correlation" :
                    result.ErrorProcessingOAuth2Response ? "invalid_oauth_response" : "unknown" );
            } catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested) {
                LogAtProtoCallbackFailed( ex );
            }
        }
        return RedirectToAction( nameof( LoginPage ), new { error = "Bluesky login failed. Please try again." } );
    }

    private async Task SignOutBlueskyAsync( ) {
        BlueskySessionService? sessions = GetBlueskySessions( );
        if (sessions is not null) {
            await sessions.EndSessionAsync( HttpContext, null );
        }
    }

    private BlueskySessionService? GetBlueskySessions( ) =>
        HttpContext?.RequestServices?.GetService<BlueskySessionService>( );

    /// <summary>Refreshes tracked account stamps after session removal updates them directly in SQLite.</summary>
    private async Task ReloadLinkedAccountAsync( ApplicationUser user ) {
        if (user.AtProtoDid is not null) {
            ApplicationDbContext db = HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>( );
            await db.Entry( user ).ReloadAsync( HttpContext.RequestAborted );
        }
    }

    /// <summary>
    /// Generates a new API key and its stored hash. All three generation sites (Register, Login,
    /// RegenerateApiKey) use this helper so that any future change to key length or encoding is made in
    /// exactly one place.
    /// </summary>
    /// <returns>A tuple of the raw API key (shown once to the caller) and the hash to persist.</returns>
    private (string ApiKey, string ApiKeyHash) IssueApiKey( ) {
        string apiKey = ApiKeyHasher.GenerateApiKey( );
        return (apiKey, hasher.HashApiKey( apiKey ));
    }
}
