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
    /// <summary>Revokes the session named by the browser's Bluesky cookie and clears that cookie.</summary>
    public Task SignOutAsync( HttpContext context ) =>
        context.SignOutAsync( BlueskyAuthenticationDefaults.AuthenticationScheme );

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
