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
            if (context is not null) {
                options.CookieManager.DeleteCookie( context, options.Cookie.Name!, options.Cookie.Build( context ) );
            }
        }
    }

    internal Task EndReplacedSessionAsync( AuthenticationTicket ticket ) =>
        EndBrowserSessionAsync( authenticationOptions.Get( BlueskyAuthenticationDefaults.AuthenticationScheme ), ticket );

    private async Task EndBrowserSessionAsync( BlueskyAuthenticationOptions options, AuthenticationTicket ticket ) {
        string? did = ticket.Principal.FindFirst( AtProtoClaims.Did )?.Value;
        string? sessionId = ticket.Properties.GetString( BlueskyAccountEvents.BrowserSessionProperty );
        if (did is null || sessionId is null) { return; }
        Did identifier = new( did );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)options.IdentityStore!;
        ClaimsIdentity? identity = await ReadIdentityAsync( store, identifier, did );
        if (await store.RemoveBrowserSessionAsync( identifier, sessionId ) && identity is not null) {
            await RevokeAsync( identity, did );
        }
    }

    private async Task EndAllSessionsAsync( BlueskyAuthenticationOptions options, Did did ) {
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)options.IdentityStore!;
        ClaimsIdentity? identity = await ReadIdentityAsync( store, did, did.ToString( ) );
        await store.RemoveCurrentAsync( did );
        if (identity is not null) { await RevokeAsync( identity, did.ToString( ) ); }
    }

    private async Task<ClaimsIdentity?> ReadIdentityAsync( BridgeBeatsIdentityStore store, Did did, string didValue ) {
        try {
            return await store.GetIdentity( did );
        } catch (Exception ex) {
            LogIdentityReadFailed( logger, ex, didValue );
            return null;
        }
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

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyIdentityReadFailed, Level = LogLevel.Warning,
        Message = "Bluesky credentials for {Did} could not be read before local session removal." )]
    private static partial void LogIdentityReadFailed( ILogger logger, Exception exception, string did );
}
