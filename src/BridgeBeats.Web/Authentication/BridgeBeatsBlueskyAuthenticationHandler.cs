using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Web.Authentication;

/// <summary>Checks protected account references before the SDK can read or revoke credentials by DID.</summary>
internal sealed class BridgeBeatsBlueskyAuthenticationHandler(
    IOptionsMonitor<BlueskyAuthenticationOptions> options,
    IOptionsMonitor<BlueskyAgentOptions> agentOptions,
    ILoggerFactory logger,
    IMeterFactory meterFactory,
    UrlEncoder encoder,
    IHttpClientFactory httpClientFactory,
    BlueskyAccountEvents accountEvents,
    BlueskySessionService sessions
) : BlueskyAuthenticationHandler( options, agentOptions, logger, meterFactory, encoder, httpClientFactory ) {
    private static readonly object s_rejectedCookie = new( );

    protected override async Task InitializeHandlerAsync( ) {
        await base.InitializeHandlerAsync( );
        string? cookie = Options.CookieManager.GetRequestCookie( Context, Options.Cookie.Name! );
        if (string.IsNullOrEmpty( cookie )) { return; }

        byte[]? binding = Context.Features.Get<ITlsTokenBindingFeature>( )?.GetProvidedTokenBindingId( );
        AuthenticationTicket? ticket = Options.TicketDataFormat.Unprotect( cookie,
            binding is null ? null : Convert.ToBase64String( binding ) );
        DateTimeOffset now = (Options.TimeProvider ?? TimeProvider.System).GetUtcNow( );
        if (ticket is not null && ticket.Properties.ExpiresUtc is { } expires && expires > now &&
            await accountEvents.HasCurrentAccountBindingAsync( ticket, Context )) {
            return;
        }

        // SDK v8 reads cookies from authentication, sign-in and sign-out, and can revoke before events run.
        // Hide a rejected reference from all three paths without changing shared options or the request headers.
        Context.Items[s_rejectedCookie] = Options.Cookie.Name;
        Options.CookieManager.DeleteCookie( Context, Options.Cookie.Name!, Options.Cookie.Build( Context ) );
    }

    protected override Task HandleSignInAsync( ClaimsPrincipal user, AuthenticationProperties? properties ) {
        // SDK v8 revokes the current DID before SigningIn runs. Preserve it until the new
        // account has passed our authority and lockout checks and its browser row is stored.
        Context.Items[s_rejectedCookie] = Options.Cookie.Name;
        CurrentUserDid = null;
        return base.HandleSignInAsync( user, properties );
    }

    protected override Task HandleSignOutAsync( AuthenticationProperties? properties ) =>
        sessions.EndSessionAsync( Context, null );

    internal sealed class GuardedCookieManager( ICookieManager inner ) : ICookieManager {
        public string? GetRequestCookie( HttpContext context, string key ) =>
            context.Items.TryGetValue( s_rejectedCookie, out object? rejected ) && key == rejected as string
                ? null : inner.GetRequestCookie( context, key );

        public void AppendResponseCookie( HttpContext context, string key, string? value, CookieOptions options ) =>
            inner.AppendResponseCookie( context, key, value, options );

        public void DeleteCookie( HttpContext context, string key, CookieOptions options ) =>
            inner.DeleteCookie( context, key, options );
    }
}
