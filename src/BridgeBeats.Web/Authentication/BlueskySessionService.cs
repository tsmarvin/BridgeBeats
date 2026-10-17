using System.Security.Claims;
using idunno.AtProto;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Web.Authentication;

/// <summary>Ends SDK OAuth sessions when users log out, switch sign-in methods or delete their account.</summary>
public sealed partial class BlueskySessionService(
    IOptionsMonitor<BlueskyAuthenticationOptions> authenticationOptions,
    IOptionsMonitor<BlueskyAgentOptions> agentOptions,
    IHttpClientFactory httpClients,
    ILogger<BlueskySessionService> logger
) {
    /// <summary>Revokes an authenticated Bluesky session, or only clears an invalid browser cookie.</summary>
    public async Task SignOutAsync( HttpContext context ) {
        string scheme = BlueskyAuthenticationDefaults.AuthenticationScheme;
        AuthenticateResult result = await context.AuthenticateAsync( scheme );
        if (result.Succeeded) {
            await context.SignOutAsync( scheme );
        } else {
            // The SDK falls back to the original request cookie's DID even after authentication rejects it.
            BlueskyAuthenticationOptions options = authenticationOptions.Get( scheme );
            options.CookieManager.DeleteCookie( context, options.Cookie.Name!, options.Cookie.Build( context ) );
        }
    }

    /// <summary>Removes a linked account's stored credentials, including when deletion uses an API key.</summary>
    public async Task RemoveAsync( string? did ) {
        if (string.IsNullOrWhiteSpace( did )) {
            return;
        }
        IIdentityStore store = authenticationOptions.Get( BlueskyAuthenticationDefaults.AuthenticationScheme ).IdentityStore!;
        Did identifier = new( did );
        ClaimsIdentity? identity = await store.GetIdentity( identifier );
        try {
            if (identity is not null) {
                using BlueskyAgent agent = new( identity: identity, httpClientFactory: httpClients, options: agentOptions.CurrentValue );
                await agent.Logout( );
            }
        } catch (Exception ex) {
            LogRevocationFailed( logger, ex, did );
        } finally {
            await store.Remove( identifier );
        }
    }

    [LoggerMessage( Level = LogLevel.Warning,
        Message = "Bluesky credentials for {Did} could not be revoked; removing the local session." )]
    private static partial void LogRevocationFailed( ILogger logger, Exception exception, string did );
}
