using System.Security.Claims;
using BridgeBeats.Core.Infrastructure.Identity;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using idunno.Bluesky.AspNet.Authentication.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace BridgeBeats.Web.Authentication;

/// <summary>Maps verified Bluesky DIDs to local Identity accounts without copying OAuth credentials into user rows.</summary>
public sealed partial class BlueskyAccountEvents(
    UserManager<ApplicationUser> users,
    ApplicationDbContext db,
    IUserClaimsPrincipalFactory<ApplicationUser> principalFactory,
    IDidHandleResolver handleResolver,
    IOptionsMonitor<BlueskyAgentOptions> agentOptions,
    IOptionsMonitor<BlueskyAuthenticationOptions> authenticationOptions,
    IHttpClientFactory httpClients,
    IMemoryCache principalCache,
    BlueskySessionService sessions,
    ILogger<BlueskyAccountEvents> logger
) : BlueskyAuthenticationEvents {
    internal const string UserIdProperty = "BridgeBeats.UserId";
    internal const string SecurityStampProperty = "BridgeBeats.SecurityStamp";
    internal const string BrowserSessionProperty = "BridgeBeats.BrowserSession";
    private static readonly object s_verifiedUser = new( );
    private static readonly object s_verifiedTicket = new( );

    /// <inheritdoc/>
    public override async Task SigningIn( BlueskySigningInContext context ) {
        ClaimsPrincipal principal = context.Principal
            ?? throw new InvalidOperationException( "The Bluesky identity has no principal." );
        if (!AtProtoCredential.TryCreate( principal, out DPoPAccessCredentials? credentials ) || credentials is null) {
            throw new InvalidOperationException( "The Bluesky identity has no OAuth credentials." );
        }
        // The SDK validates the token issuer and PDS audience, but its login flow does not bind
        // the returned subject to the DID document that authorizes the PDS. Check that binding
        // before a token-controlled DID can select or create a local account.
        using (BlueskyAgent agent = new( httpClientFactory: httpClients, options: agentOptions.CurrentValue )) {
            Uri? authoritativePds = await agent.ResolvePds( credentials.Did, context.HttpContext.RequestAborted );
            if (authoritativePds is null || !authoritativePds.Equals( credentials.Service )) {
                throw new InvalidOperationException( "The Bluesky DID is not authoritative for the OAuth service." );
            }
            Uri? authoritativeIssuer = await agent.ResolveAuthorizationServer( authoritativePds, context.HttpContext.RequestAborted );
            if (authoritativeIssuer is null ||
                !Uri.TryCreate( new JsonWebToken( credentials.AccessJwt ).Issuer, UriKind.Absolute, out Uri? tokenIssuer ) ||
                !authoritativeIssuer.Equals( tokenIssuer )) {
                throw new InvalidOperationException( "The Bluesky DID's PDS does not authorize the OAuth issuer." );
            }
        }
        string did = credentials.Did.ToString( );
        ApplicationUser? user = await users.Users.SingleOrDefaultAsync(
            u => u.AtProtoDid == did, context.HttpContext.RequestAborted );
        Handle handle = await handleResolver.ResolveHandleAsync( new Did( did ), context.HttpContext.RequestAborted );
        string? verifiedHandle = handle == Handle.Invalid ? null : handle.ToString( );
        bool created = false;
        if (user is null) {
            user = new ApplicationUser {
                UserName = $"bluesky_{Guid.NewGuid( ):N}",
                AtProtoDid = did,
                AtProtoHandle = verifiedHandle,
                CreatedAt = DateTime.UtcNow
            };
            try {
                EnsureSucceeded( await users.CreateAsync( user ) );
                created = true;
            } catch (DbUpdateException ex) when (ex.InnerException is SqliteException {
                SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067
            } sqlite && sqlite.Message.Contains( "AspNetUsers.AtProtoDid", StringComparison.Ordinal )) {
                // Another callback created the account for this DID after our lookup.
                db.Entry( user ).State = EntityState.Detached;
                user = await users.Users.SingleAsync( u => u.AtProtoDid == did, context.HttpContext.RequestAborted );
            }
        }
        if (users.SupportsUserLockout && await users.IsLockedOutAsync( user )) {
            throw new InvalidOperationException( "The BridgeBeats account is locked." );
        }
        if (verifiedHandle is not null && user.AtProtoHandle != verifiedHandle) {
            user.AtProtoHandle = verifiedHandle;
            EnsureSucceeded( await users.UpdateAsync( user ) );
        }
        // The DID identity is shared, while this protected reference identifies one browser.
        context.Properties.SetString( UserIdProperty, user.Id );
        string stamp = await users.GetSecurityStampAsync( user );
        context.Properties.SetString( SecurityStampProperty, stamp );
        context.Properties.SetString( BrowserSessionProperty, Guid.NewGuid( ).ToString( "N" ) );
        context.HttpContext.Items[BridgeBeatsIdentityStore.ExpectedStampKey] = stamp;
        if (GetVerifiedTicket( context.HttpContext ) is { } previous &&
            previous.Principal.FindFirstValue( AtProtoClaims.Did ) is { } previousDid &&
            previous.Properties.GetString( BrowserSessionProperty ) is { } previousSessionId) {
            context.HttpContext.Items[BridgeBeatsIdentityStore.ExpectedBrowserSessionKey] =
                new BridgeBeatsIdentityStore.BrowserSessionExpectation( previousDid, previousSessionId );
        }
        context.Properties.IsPersistent = false;
        await context.HttpContext.SignOutAsync( IdentityConstants.ApplicationScheme );
        if (created) { LogAccountCreated( logger, user.Id ); }
        LogAccountSignedIn( logger, user.Id );
    }

    /// <inheritdoc/>
    public override async Task SignedIn( BlueskySignedInContext context ) {
        string did = context.Principal?.FindFirstValue( AtProtoClaims.Did )
            ?? throw new InvalidOperationException( "The Bluesky identity has no DID." );
        string sessionId = context.Properties.GetString( BrowserSessionProperty )
            ?? throw new InvalidOperationException( "The Bluesky browser session is missing." );
        DateTimeOffset expires = context.Properties.ExpiresUtc
            ?? throw new InvalidOperationException( "The Bluesky browser session has no expiry." );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)authenticationOptions
            .Get( BlueskyAuthenticationDefaults.AuthenticationScheme ).IdentityStore!;
        await store.RegisterBrowserSessionAsync( new Did( did ), sessionId, expires, context.HttpContext.RequestAborted );
        if (GetVerifiedTicket( context.HttpContext ) is { } replaced) {
            await sessions.EndReplacedSessionAsync( replaced );
        }
    }

    /// <inheritdoc/>
    public override async Task CheckSlidingExpiration( BlueskySlidingExpirationContext context ) {
        if (context.ShouldRenew) {
            ApplicationUser? user = await FindUserAsync( context.Principal, context.HttpContext );
            context.ShouldRenew = user is not null && await HasCurrentAccountBindingAsync( user, context.Properties );
            if (context.ShouldRenew && user?.AtProtoDid is { } did &&
                context.Properties.GetString( BrowserSessionProperty ) is { } sessionId) {
                BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)authenticationOptions
                    .Get( BlueskyAuthenticationDefaults.AuthenticationScheme ).IdentityStore!;
                context.ShouldRenew = await store.ExtendBrowserSessionAsync( new Did( did ), sessionId,
                    DateTimeOffset.UtcNow.Add( context.Options.ExpireTimeSpan ), context.HttpContext.RequestAborted );
            }
        }
    }

    /// <inheritdoc/>
    public override async Task ValidatePrincipal( BlueskyValidatePrincipalContext context ) {
        ApplicationUser? user = await FindUserAsync( context.Principal, context.HttpContext );
        if (user is null) {
            RejectAndClearCookie( context );
            return;
        }
        if (!await HasCurrentAccountBindingAsync( user, context.Properties )) {
            RejectAndClearCookie( context );
            return;
        }
        if (users.SupportsUserLockout && await users.IsLockedOutAsync( user )) {
            context.RejectPrincipal( );
            await sessions.EndSessionAsync( context.HttpContext, user.AtProtoDid );
            return;
        }
        string cacheKey = $"BlueskyPrincipal:{user.Id}:{user.SecurityStamp}:{user.ConcurrencyStamp}";
        if (!principalCache.TryGetValue( cacheKey, out ClaimsPrincipal? basePrincipal ) || basePrincipal is null) {
            basePrincipal = await principalFactory.CreateAsync( user );
            _ = principalCache.Set( cacheKey, basePrincipal, TimeSpan.FromMinutes( 1 ) );
        }
        ClaimsPrincipal localPrincipal = new( basePrincipal.Identities.Select( identity => new ClaimsIdentity( identity ) ) );
        ClaimsIdentity identity = (ClaimsIdentity)localPrincipal.Identity!;
        // Preserve the SDK's credential claims (and their service issuers) for the injected BlueskyAgent.
        // NameIdentifier comes from Identity, keeping playlists, rate limits and API keys on the same user ID.
        identity.AddClaims( context.Principal!.Claims.Where( c => c.Type.StartsWith( "urn:atproto:", StringComparison.Ordinal ) ) );
        context.ReplacePrincipal( localPrincipal );
    }

    private static void RejectAndClearCookie( BlueskyValidatePrincipalContext context ) {
        context.RejectPrincipal( );
        context.Options.CookieManager.DeleteCookie( context.HttpContext, context.Options.Cookie.Name!,
            context.Options.Cookie.Build( context.HttpContext ) );
    }

    private Task<ApplicationUser?> FindUserAsync( ClaimsPrincipal? principal, HttpContext context ) {
        string? did = principal?.FindFirstValue( AtProtoClaims.Did );
        if (did is not null && context.Items.TryGetValue( s_verifiedUser, out object? cached ) &&
            cached is ApplicationUser verified && verified.AtProtoDid == did) {
            return Task.FromResult<ApplicationUser?>( verified );
        }
        return did is null ? Task.FromResult<ApplicationUser?>( null )
            : users.Users.AsNoTracking( ).SingleOrDefaultAsync( u => u.AtProtoDid == did, context.RequestAborted );
    }

    internal async Task<bool> HasCurrentAccountBindingAsync( AuthenticationTicket ticket, HttpContext context ) {
        ApplicationUser? user = await FindUserAsync( ticket.Principal, context );
        if (user is null || !await HasCurrentAccountBindingAsync( user, ticket.Properties )) {
            return false;
        }
        context.Items[s_verifiedUser] = user;
        context.Items[s_verifiedTicket] = ticket;
        return true;
    }

    internal static AuthenticationTicket? GetVerifiedTicket( HttpContext context ) =>
        context.Items.TryGetValue( s_verifiedTicket, out object? ticket ) ? ticket as AuthenticationTicket : null;

    private async Task<bool> HasCurrentAccountBindingAsync( ApplicationUser user, AuthenticationProperties properties ) {
        if (properties.GetString( UserIdProperty ) != user.Id ||
            properties.GetString( SecurityStampProperty ) is not { Length: > 0 } stamp ||
            stamp != await users.GetSecurityStampAsync( user ) ||
            properties.GetString( BrowserSessionProperty ) is not { Length: > 0 } sessionId ||
            user.AtProtoDid is null) { return false; }
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)authenticationOptions
            .Get( BlueskyAuthenticationDefaults.AuthenticationScheme ).IdentityStore!;
        return await store.IsBrowserSessionCurrentAsync( new Did( user.AtProtoDid ), sessionId );
    }

    private static void EnsureSucceeded( IdentityResult result ) {
        if (!result.Succeeded) {
            throw new InvalidOperationException( "Unable to save the linked BridgeBeats account." );
        }
    }

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyAccountCreated, Level = LogLevel.Information,
        Message = "Bluesky account created for local user {UserId}" )]
    private static partial void LogAccountCreated( ILogger logger, string userId );

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyAccountSignedIn, Level = LogLevel.Information,
        Message = "Bluesky sign-in completed for local user {UserId}" )]
    private static partial void LogAccountSignedIn( ILogger logger, string userId );
}
