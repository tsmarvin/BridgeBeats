using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using idunno.AtProto;
using idunno.AtProto.Authentication;
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

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync( ) {
        AuthenticateResult result = await base.HandleAuthenticateAsync( );
        // Credential cleanup runs while the SDK hydrates the stored identity (GetIdentity), before the
        // authentication result is known. When that cleanup deleted this browser's rows, the cookie which
        // referred to them must not outlive the request or be renewed by sliding expiration. The outcome is
        // bound to the verified ticket's DID and browser session, so a stale or replaced account is not
        // invalidated on the strength of the DID alone.
        if (!result.Succeeded &&
            Context.Items.TryGetValue( BridgeBeatsIdentityStore.CleanupOutcomeKey, out object? outcome ) &&
            outcome is BridgeBeatsIdentityStore.CleanupOutcome cleanup &&
            BlueskyAccountEvents.GetVerifiedTicket( Context ) is { } verified &&
            verified.Principal.FindFirstValue( AtProtoClaims.Did ) == cleanup.Did &&
            verified.Properties.GetString( BlueskyAccountEvents.BrowserSessionProperty ) == cleanup.BrowserSessionId) {
            Context.Items[s_rejectedCookie] = Options.Cookie.Name;
            CurrentUserDid = null;
            Context.Items[BlueskySessionService.SessionEndedKey] = true;
            _ = accountEvents.ClearVerifiedSignInAsync( Context );
            Options.CookieManager.DeleteCookie( Context, Options.Cookie.Name!, Options.Cookie.Build( Context ) );
        }
        return result;
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

    protected override Task FinishResponseAsync( ) =>
        Context.Items.ContainsKey( BlueskySessionService.SessionEndedKey )
            ? Task.CompletedTask : base.FinishResponseAsync( );

    internal sealed class GuardedCookieManager( ICookieManager inner ) : ICookieManager {
        private static readonly object s_deletedCookieHeaders = new( );

        public string? GetRequestCookie( HttpContext context, string key ) =>
            context.Items.TryGetValue( s_rejectedCookie, out object? rejected ) && key == rejected as string
                ? null : inner.GetRequestCookie( context, key );

        public void AppendResponseCookie( HttpContext context, string key, string? value, CookieOptions options ) {
            if (!string.IsNullOrEmpty( value ) &&
                context.Items.TryGetValue( s_deletedCookieHeaders, out object? deletedObj ) &&
                deletedObj is Dictionary<string, List<string>> deleted &&
                deleted.TryGetValue( key, out List<string>? ourDeletes )) {
                // A cleanup ran earlier in this response and asked to delete the cookie this append replaces.
                // Remove exactly the Set-Cookie headers that deletion produced, so the fresh cookie wins and an
                // earlier cleanup deletion cannot override it. The removable list is guarded by the same value
                // this append writes, so a different value still deletes.
                IHeaderDictionary responseHeaders = context.Response.Headers;
                string[] existing = [.. responseHeaders.SetCookie.Where( value => value is not null )!];
                if (existing.Where( ourDeletes.Contains ).Any( )) {
                    responseHeaders.SetCookie = new Microsoft.Extensions.Primitives.StringValues(
                        [.. existing.Where( header => !ourDeletes.Contains( header ) )] );
                    _ = context.Items.Remove( s_deletedCookieHeaders );
                }
            }
            inner.AppendResponseCookie( context, key, value, options );
        }

        public void DeleteCookie( HttpContext context, string key, CookieOptions options ) {
            // Record the exact Set-Cookie header values this deletion appends so a later fresh cookie for the
            // same name can strip them. ChunkingCookieManager can write multiple chunk headers per deletion.
            IHeaderDictionary responseHeaders = context.Response.Headers;
            string[] before = [.. responseHeaders.SetCookie.Where( value => value is not null )!];
            inner.DeleteCookie( context, key, options );
            List<string> added = [.. responseHeaders.SetCookie
                .Where( value => value is not null )
                .Where( value => !before.Contains( value ) )
                .Select( value => value! )
                .Where( value => value.StartsWith( key + "=", StringComparison.OrdinalIgnoreCase ) || value.StartsWith( key + "C", StringComparison.OrdinalIgnoreCase ) )];
            if (added.Count == 0) { return; }
            if (context.Items[s_deletedCookieHeaders] is not Dictionary<string, List<string>> deleted) {
                deleted = new Dictionary<string, List<string>>( StringComparer.Ordinal );
                context.Items[s_deletedCookieHeaders] = deleted;
            }
            if (!deleted.TryGetValue( key, out List<string>? entries )) {
                entries = new List<string>( );
                deleted[key] = entries;
            }
            entries.AddRange( added );
        }
    }
}
