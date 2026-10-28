using System.Security.Claims;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Web.Authentication;

/// <summary>Ends one browser session, or every session for account deletion and lockout.</summary>
public sealed partial class BlueskySessionService(
    IOptionsMonitor<BlueskyAuthenticationOptions> authenticationOptions,
    IOptionsMonitor<BlueskyAgentOptions> agentOptions,
    IHttpClientFactory httpClients,
    ILogger<BlueskySessionService> logger
) {
    internal static readonly object SessionEndedKey = new( );

    /// <summary>Ends this browser when <paramref name="did"/> is null; an explicit DID ends all its sessions.</summary>
    public async Task EndSessionAsync( HttpContext? context, string? did ) {
        BlueskyAuthenticationOptions options = authenticationOptions.Get( BlueskyAuthenticationDefaults.AuthenticationScheme );
        try {
            if (!string.IsNullOrWhiteSpace( did )) {
                await EndAllSessionsAsync( options, new Did( did ) );
                return;
            }
            if (context is null) { return; }
            AuthenticationTicket? ticket = BlueskyAccountEvents.GetVerifiedTicket( context );
            if (ticket is null && context.Request.Cookies.ContainsKey( options.Cookie.Name! )) {
                AuthenticateResult result = await context.AuthenticateAsync( BlueskyAuthenticationDefaults.AuthenticationScheme );
                ticket = result.Succeeded ? BlueskyAccountEvents.GetVerifiedTicket( context ) : null;
            }
            if (ticket is not null) { await EndBrowserSessionAsync( options, ticket ); }
        } finally {
            if (context is not null) { ClearBrowserCookie( options, context ); }
        }
    }

    /// <summary>
    /// Clears the Bluesky reference cookie for this response without touching stored rows. Account
    /// deletion uses this because it removes the rows itself, in the same transaction as the user row.
    /// </summary>
    internal void ClearBrowserCookie( HttpContext context ) => ClearBrowserCookie(
        authenticationOptions.Get( BlueskyAuthenticationDefaults.AuthenticationScheme ), context );

    private static void ClearBrowserCookie( BlueskyAuthenticationOptions options, HttpContext context ) {
        // The SDK may have queued a sliding renewal during authentication. Its response
        // hook must not write the old reference cookie after this deletion.
        context.Items[SessionEndedKey] = true;
        options.CookieManager.DeleteCookie( context, options.Cookie.Name!, options.Cookie.Build( context ) );
    }

    /// <summary>
    /// Revokes a credential row returned by <see cref="BridgeBeatsIdentityStore.RemoveAccountAsync"/> after
    /// its transaction committed, so a failed account deletion never revokes credentials that are still in
    /// use. Account deletion calls this only when the OAuth service is registered, because revoking a grant
    /// requires it.
    /// </summary>
    internal async Task RevokeRemovedAccountAsync( byte[]? removed, string? did ) {
        if (removed is null || string.IsNullOrWhiteSpace( did )) { return; }
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)authenticationOptions
            .Get( BlueskyAuthenticationDefaults.AuthenticationScheme ).IdentityStore!;
        ClaimsIdentity? identity = await store.ReadCredentialAsync( removed, did );
        if (identity is not null) { await RevokeAsync( identity, did ); }
    }

    internal Task EndReplacedSessionAsync( AuthenticationTicket ticket ) =>
        EndBrowserSessionAsync( authenticationOptions.Get( BlueskyAuthenticationDefaults.AuthenticationScheme ), ticket );

    internal Task RevokeCredentialsAsync( ClaimsIdentity identity, string did ) => RevokeAsync( identity, did );

    private async Task EndBrowserSessionAsync( BlueskyAuthenticationOptions options, AuthenticationTicket ticket ) {
        string? did = ticket.Principal.FindFirst( AtProtoClaims.Did )?.Value;
        string? sessionId = ticket.Properties.GetString( BlueskyAccountEvents.BrowserSessionProperty );
        if (did is null || sessionId is null) { return; }
        Did identifier = new( did );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)options.IdentityStore!;
        byte[]? removed = await store.RemoveBrowserSessionAsync( identifier, sessionId );
        await RevokeRemovedAsync( store, removed, did );
    }

    private async Task EndAllSessionsAsync( BlueskyAuthenticationOptions options, Did did ) {
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)options.IdentityStore!;
        byte[]? removed = await store.RemoveCurrentAsync( did );
        await RevokeRemovedAsync( store, removed, did.ToString( ) );
    }

    /// <summary>
    /// Revokes the credential row a removal returned. The row came out of the store in the same write
    /// that deleted it, so this cannot revoke a grant that a concurrent sign-in stored afterwards.
    /// </summary>
    private async Task RevokeRemovedAsync( BridgeBeatsIdentityStore store, byte[]? removed, string did ) {
        ClaimsIdentity? identity = await store.ReadCredentialAsync( removed, did );
        if (identity is not null) { await RevokeAsync( identity, did ); }
    }

    private async Task RevokeAsync( ClaimsIdentity identity, string did ) {
        try {
            using BlueskyAgent agent = new( identity: identity, httpClientFactory: httpClients, options: agentOptions.CurrentValue );
            await agent.Logout( );
        } catch (Exception ex) {
            LogRevocationFailed( logger, ex, did );
        }
    }

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyRevocationFailed, Level = LogLevel.Warning,
        Message = "Bluesky credentials for {Did} could not be revoked; removing the local session." )]
    private static partial void LogRevocationFailed( ILogger logger, Exception exception, string did );
}
