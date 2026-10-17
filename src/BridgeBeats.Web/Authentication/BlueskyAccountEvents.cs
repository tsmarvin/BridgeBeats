using System.Security.Claims;
using BridgeBeats.Core.Infrastructure.Identity;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky.AspNet.Authentication.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BridgeBeats.Web.Authentication;

/// <summary>Maps verified Bluesky DIDs to local Identity accounts without copying OAuth credentials into user rows.</summary>
public sealed class BlueskyAccountEvents(
    UserManager<ApplicationUser> users,
    IUserClaimsPrincipalFactory<ApplicationUser> principalFactory,
    IDidHandleResolver handleResolver
) : BlueskyAuthenticationEvents {
    internal const string UserIdProperty = "BridgeBeats.UserId";
    internal const string SecurityStampProperty = "BridgeBeats.SecurityStamp";

    /// <inheritdoc/>
    public override async Task SigningIn( BlueskySigningInContext context ) {
        string did = context.Principal?.FindFirstValue( AtProtoClaims.Did )
            ?? throw new InvalidOperationException( "The verified Bluesky identity has no DID." );
        ApplicationUser? user = await users.Users.SingleOrDefaultAsync(
            u => u.AtProtoDid == did, context.HttpContext.RequestAborted );
        Handle handle = await handleResolver.ResolveHandleAsync( new Did( did ), context.HttpContext.RequestAborted );
        string? verifiedHandle = handle == Handle.Invalid ? null : handle.ToString( );
        if (user is null) {
            user = new ApplicationUser {
                UserName = $"bluesky_{Guid.NewGuid( ):N}",
                AtProtoDid = did,
                AtProtoHandle = verifiedHandle,
                CreatedAt = DateTime.UtcNow
            };
            EnsureSucceeded( await users.CreateAsync( user ) );
        } else if (verifiedHandle is not null && user.AtProtoHandle != verifiedHandle) {
            user.AtProtoHandle = verifiedHandle;
            EnsureSucceeded( await users.UpdateAsync( user ) );
        }
        if (users.SupportsUserLockout && await users.IsLockedOutAsync( user )) {
            throw new InvalidOperationException( "The BridgeBeats account is locked." );
        }
        // Bind the protected reference ticket to this local account and its security stamp.
        // Reusing a DID after account deletion must not make a previous account's cookie valid again.
        context.Properties.SetString( UserIdProperty, user.Id );
        context.Properties.SetString( SecurityStampProperty, await users.GetSecurityStampAsync( user ) );
        context.Properties.IsPersistent = false;
        await context.HttpContext.SignOutAsync( IdentityConstants.ApplicationScheme );
    }

    /// <inheritdoc/>
    public override async Task CheckSlidingExpiration( BlueskySlidingExpirationContext context ) {
        if (context.ShouldRenew) {
            ApplicationUser? user = await FindUserAsync( context.Principal, context.HttpContext.RequestAborted );
            context.ShouldRenew = user is not null && await HasCurrentAccountBindingAsync( user, context.Properties );
        }
    }

    /// <inheritdoc/>
    public override async Task ValidatePrincipal( BlueskyValidatePrincipalContext context ) {
        ApplicationUser? user = await FindUserAsync( context.Principal, context.HttpContext.RequestAborted );
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

    private Task<ApplicationUser?> FindUserAsync( ClaimsPrincipal? principal, CancellationToken cancellationToken ) {
        string? did = principal?.FindFirstValue( AtProtoClaims.Did );
        return did is null ? Task.FromResult<ApplicationUser?>( null )
            : users.Users.SingleOrDefaultAsync( u => u.AtProtoDid == did, cancellationToken );
    }

    internal async Task<bool> HasCurrentAccountBindingAsync( AuthenticationTicket ticket, CancellationToken cancellationToken ) {
        ApplicationUser? user = await FindUserAsync( ticket.Principal, cancellationToken );
        return user is not null && await HasCurrentAccountBindingAsync( user, ticket.Properties );
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
}
