using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Authentication;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using idunno.Bluesky.AspNet.Authentication.SQLite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;

namespace BridgeBeats.Web.Configuration;

/// <summary>Configures SDK-managed OAuth with persistent stores and the application's local identity mapping.</summary>
internal static class BlueskyAuthenticationConfiguration {
    internal const string CookieName = "__Secure-BridgeBeats.Bluesky";
    internal const string ClientMetadataPath = "/oauth-client-metadata.json";

    internal static IServiceCollection AddBridgeBeatsBluesky(
        this IServiceCollection services, AppSettings settings, string clientId, string domain
    ) {
        OAuthClientSigningKey? signingKey = LoadSigningKey( settings.ATProtoOAuthSigningKeyPath, optional: true );
        OAuthClientSigningKey[] previousKeys = [.. settings.ATProtoOAuthAdditionalSigningKeyPaths.Select( path => LoadSigningKey( path, optional: false )! )];
        _ = services.Configure<BlueskyAgentOptions>( options => {
            options.EnableBackgroundTokenRefresh = false;
            options.OAuthOptions = new OAuthOptions(
                clientId, new Uri( $"{domain}/account/atproto-callback" ),
                ["atproto", "repo:link.bridgebeats.playlist"] ) {
                ClientName = "BridgeBeats",
                ClientUri = new Uri( domain ),
                TosUri = new Uri( $"{domain}/Home/Tos" ),
                PolicyUri = new Uri( $"{domain}/Home/Privacy" ),
                ClientSigningKey = signingKey
            };
            foreach (OAuthClientSigningKey key in previousKeys) {
                options.OAuthOptions.AdditionalClientSigningKeys.Add( key );
            }
        } );
        _ = services.AddScoped<BlueskyAccountEvents>( );
        _ = services.AddTransient<BridgeBeatsBlueskyAuthenticationHandler>( );
        _ = services.AddScoped<BlueskySessionService>( );
        services.TryAddSingleton<IDidHandleResolver>( _ => new DidHandleCache( ) );
        _ = services.AddAuthentication( ).AddBluesky( options => {
            options.Cookie.Name = CookieName;
            options.Cookie.Domain = AuthenticationOrigin.Parse( domain )?.CookieDomain;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/account/login";
            options.ExpireTimeSpan = TimeSpan.FromDays( 7 );
            options.SlidingExpiration = true;
            options.EventsType = typeof( BlueskyAccountEvents );
        } );
        _ = services.AddOptions<BlueskyAuthenticationOptions>( BlueskyAuthenticationDefaults.AuthenticationScheme )
            .Configure<ILoggerFactory>( ( options, loggerFactory ) => {
                options.IdentityStore = new SqliteIdentityStore( settings.IdentityConnectionString, loggerFactory: loggerFactory );
                options.CorrelationStateCache = new SqliteCorrelationStateCache( settings.IdentityConnectionString, loggerFactory: loggerFactory );
            } );
        _ = services.PostConfigure<AuthenticationOptions>( options =>
            options.SchemeMap[BlueskyAuthenticationDefaults.AuthenticationScheme].HandlerType = typeof( BridgeBeatsBlueskyAuthenticationHandler ) );
        _ = services.PostConfigure<BlueskyAuthenticationOptions>( BlueskyAuthenticationDefaults.AuthenticationScheme, options =>
            options.CookieManager = new BridgeBeatsBlueskyAuthenticationHandler.GuardedCookieManager( options.CookieManager ) );
        _ = services.PostConfigure<CookieAuthenticationOptions>( IdentityConstants.ApplicationScheme, options => {
            Func<CookieValidatePrincipalContext, Task> validate = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = async context => {
                await validate( context );
                if (context.Principal is null) { return; }
                UserManager<ApplicationUser> users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>( );
                ApplicationUser? user = await users.GetUserAsync( context.Principal );
                // Old OAuth-only Identity cookies carry no SDK session. Require one new OAuth login after upgrade.
                if (user is { AtProtoDid: not null, PasswordHash: null }) {
                    context.RejectPrincipal( );
                    await context.HttpContext.SignOutAsync( IdentityConstants.ApplicationScheme );
                }
            };
        } );
        _ = services.AddOpenTelemetry( ).WithMetrics( metrics => metrics.AddBlueskyAuthenticationMetrics( ) );
        _ = services.AddBlueskyAgentFactory( );
        _ = services.AddBlueskyOAuthClientMetadata( options => options.LogoUri = new Uri( $"{domain}/public/Icons/BridgeBeats_FullColor.svg" ) );
        return services;
    }

    // Keep existing installer-generated JWK secrets and their key IDs valid; PEM is also accepted.
    internal static OAuthClientSigningKey? LoadSigningKey( string path, bool optional ) {
        if (optional && (string.IsNullOrWhiteSpace( path ) || !File.Exists( path ))) {
            return null;
        }
        string contents = File.ReadAllText( path );
        if (optional && (string.IsNullOrWhiteSpace( contents ) || contents.Trim( ) == "{}")) {
            return null;
        }
        if (contents.TrimStart( ).StartsWith( '{' )) {
            using ATProtoSigningKeyProvider key = new( contents );
            return OAuthClientSigningKey.FromPem( key.SigningKey.ExportPkcs8PrivateKeyPem( ), key.KeyId );
        }
        return OAuthClientSigningKey.FromPem( contents );
    }
}
