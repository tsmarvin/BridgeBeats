using System.Security.Claims;
using idunno.AtProto;
using idunno.AtProto.Authentication;
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
    /// <summary>One path for logout, sign-in-method switching and account deletion.</summary>
    public async Task EndSessionAsync( HttpContext? context, string? did ) {
        BlueskyAuthenticationOptions options = authenticationOptions.Get( BlueskyAuthenticationDefaults.AuthenticationScheme );
        try {
            if (string.IsNullOrWhiteSpace( did ) && context is not null) {
                did = context.User.FindFirst( AtProtoClaims.Did )?.Value
                    ?? BlueskyAccountEvents.GetVerifiedDid( context );
                if (did is null && context.Request.Cookies.ContainsKey( options.Cookie.Name! )) {
                    AuthenticateResult result = await context.AuthenticateAsync( BlueskyAuthenticationDefaults.AuthenticationScheme );
                    did = result.Principal?.FindFirst( AtProtoClaims.Did )?.Value;
                }
            }
            if (string.IsNullOrWhiteSpace( did )) { return; }

            Did identifier = new( did );
            BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)options.IdentityStore!;
            ClaimsIdentity? identity = null;
            try {
                identity = await store.GetIdentity( identifier );
            } catch (Exception ex) {
                LogIdentityReadFailed( logger, ex, did );
            }
            await store.RemoveCurrentAsync( identifier );
            if (identity is not null) {
                try {
                    using BlueskyAgent agent = new( identity: identity, httpClientFactory: httpClients, options: agentOptions.CurrentValue );
                    await agent.Logout( );
                } catch (Exception ex) {
                    LogRevocationFailed( logger, ex, did );
                }
            }
        } finally {
            if (context is not null) {
                options.CookieManager.DeleteCookie( context, options.Cookie.Name!, options.Cookie.Build( context ) );
            }
        }
    }

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyRevocationFailed, Level = LogLevel.Warning,
        Message = "Bluesky credentials for {Did} could not be revoked; removing the local session." )]
    private static partial void LogRevocationFailed( ILogger logger, Exception exception, string did );

    [LoggerMessage( EventId = Logging.LogEventIds.Authentication.BlueskyIdentityReadFailed, Level = LogLevel.Warning,
        Message = "Bluesky credentials for {Did} could not be read before local session removal." )]
    private static partial void LogIdentityReadFailed( ILogger logger, Exception exception, string did );
}
