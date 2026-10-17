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
        // The SDK stores the full identity server-side and writes only its DID to the cookie.
        // Local account claims are rebuilt on each request rather than persisted with rotating credentials.
        context.Properties.IsPersistent = false;
        await context.HttpContext.SignOutAsync( IdentityConstants.ApplicationScheme );
    }

    /// <inheritdoc/>
    public override async Task ValidatePrincipal( BlueskyValidatePrincipalContext context ) {
        string? did = context.Principal?.FindFirstValue( AtProtoClaims.Did );
        ApplicationUser? user = did is null ? null : await users.Users.SingleOrDefaultAsync(
            u => u.AtProtoDid == did, context.HttpContext.RequestAborted );
        if (user is null || users.SupportsUserLockout && await users.IsLockedOutAsync( user )) {
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

    private static void EnsureSucceeded( IdentityResult result ) {
        if (!result.Succeeded) {
            throw new InvalidOperationException( "Unable to save the linked BridgeBeats account." );
        }
    }
}
