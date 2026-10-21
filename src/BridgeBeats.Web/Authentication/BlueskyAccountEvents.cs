using System.Security.Claims;
using BridgeBeats.Core.Infrastructure.Identity;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    IHttpClientFactory httpClients,
    ILogger<BlueskyAccountEvents> logger
) : BlueskyAuthenticationEvents {
    internal const string UserIdProperty = "BridgeBeats.UserId";
    internal const string SecurityStampProperty = "BridgeBeats.SecurityStamp";
    private static readonly object s_verifiedUser = new( );

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
        // Each SDK identity is shared by DID. A new login must invalidate earlier reference tickets,
        // including copies retained across logout and re-login for the same local account.
        EnsureSucceeded( await users.UpdateSecurityStampAsync( user ) );
        context.Properties.SetString( UserIdProperty, user.Id );
        context.Properties.SetString( SecurityStampProperty, await users.GetSecurityStampAsync( user ) );
        context.Properties.IsPersistent = false;
        await context.HttpContext.SignOutAsync( IdentityConstants.ApplicationScheme );
        if (created) { LogAccountCreated( logger, user.Id ); }
        LogAccountSignedIn( logger, user.Id );
    }

    /// <inheritdoc/>
    public override async Task CheckSlidingExpiration( BlueskySlidingExpirationContext context ) {
        if (context.ShouldRenew) {
            ApplicationUser? user = await FindUserAsync( context.Principal, context.HttpContext );
            context.ShouldRenew = user is not null && await HasCurrentAccountBindingAsync( user, context.Properties );
        }
    }

    /// <inheritdoc/>
    public override async Task ValidatePrincipal( BlueskyValidatePrincipalContext context ) {
        ApplicationUser? user = await FindUserAsync( context.Principal, context.HttpContext );
        if (user is null) {
            context.RejectPrincipal( );
            await context.HttpContext.SignOutAsync( context.Scheme.Name );
            return;
        }
        if (!await HasCurrentAccountBindingAsync( user, context.Properties )) {
            context.RejectPrincipal( );
            // SDK sign-out revokes credentials by DID. An obsolete ticket must only clear its own cookie,
            // preserving any newer login for the same DID. CheckSlidingExpiration prevents its renewal.
            context.Options.CookieManager.DeleteCookie( context.HttpContext, context.Options.Cookie.Name!,
                context.Options.Cookie.Build( context.HttpContext ) );
            return;
        }
        if (users.SupportsUserLockout && await users.IsLockedOutAsync( user )) {
            context.RejectPrincipal( );
            await context.HttpContext.SignOutAsync( context.Scheme.Name );
            return;
        }
        ClaimsPrincipal localPrincipal = await principalFactory.CreateAsync( user );
        ClaimsIdentity identity = (ClaimsIdentity)localPrincipal.Identity!;
        // Preserve the SDK's credential claims (and their service issuers) for the injected BlueskyAgent.
        // NameIdentifier comes from Identity, keeping playlists, rate limits and API keys on the same user ID.
        identity.AddClaims( context.Principal!.Claims.Where( c => c.Type.StartsWith( "urn:atproto:", StringComparison.Ordinal ) ) );
        context.ReplacePrincipal( localPrincipal );
    }

    private Task<ApplicationUser?> FindUserAsync( ClaimsPrincipal? principal, HttpContext context ) {
        string? did = principal?.FindFirstValue( AtProtoClaims.Did );
        if (did is not null && context.Items.TryGetValue( s_verifiedUser, out object? cached ) &&
            cached is ApplicationUser verified && verified.AtProtoDid == did) {
            return Task.FromResult<ApplicationUser?>( verified );
        }
        return did is null ? Task.FromResult<ApplicationUser?>( null )
            : users.Users.SingleOrDefaultAsync( u => u.AtProtoDid == did, context.RequestAborted );
    }

    internal async Task<bool> HasCurrentAccountBindingAsync( AuthenticationTicket ticket, HttpContext context ) {
        ApplicationUser? user = await FindUserAsync( ticket.Principal, context );
        if (user is null || !await HasCurrentAccountBindingAsync( user, ticket.Properties )) {
            return false;
        }
        context.Items[s_verifiedUser] = user;
        return true;
    }

    private async Task<bool> HasCurrentAccountBindingAsync( ApplicationUser user, AuthenticationProperties properties ) =>
        properties.GetString( UserIdProperty ) == user.Id &&
        properties.GetString( SecurityStampProperty ) is { Length: > 0 } stamp &&
        stamp == await users.GetSecurityStampAsync( user );

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
