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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using System.Threading.RateLimiting;

namespace BridgeBeats.Web.Configuration;

/// <summary>Configures SDK-managed OAuth with persistent stores and the application's local identity mapping.</summary>
internal static class BlueskyAuthenticationConfiguration {
    internal const string CookieName = "__Secure-BridgeBeats.Bluesky";
    internal const string ClientMetadataPath = "/oauth-client-metadata.json";
    internal const string LegacyIdentityCookieName = ".AspNetCore.Identity.Application";
    internal const string UpgradedIdentityCookieName = "__Secure-BridgeBeats.Identity.v2";
    internal const string LoginStartRateLimitPolicy = "bluesky-login-start";
    internal const int LoginStartTimeoutSeconds = 30;
    private const string ValidatedPasswordCookie = "BridgeBeats.ValidatedPasswordCookie";
    internal static readonly object PasswordSignInKey = new( );

    internal static IServiceCollection AddBridgeBeatsBluesky(
        this IServiceCollection services, AppSettings settings, string clientId, string domain
    ) {
        if (string.IsNullOrWhiteSpace( settings.ATProtoOAuthSigningKeyPath )) {
            throw new InvalidOperationException( "BridgeBeats:ATProtoOAuthSigningKeyPath is required when Bluesky sign-in is enabled." );
        }
        OAuthClientSigningKey signingKey = LoadSigningKey( settings.ATProtoOAuthSigningKeyPath );
        OAuthClientSigningKey[] previousKeys = [.. settings.ATProtoOAuthAdditionalSigningKeyPaths.Select( LoadSigningKey )];
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
        _ = services.AddHttpContextAccessor( );
        _ = services.AddMemoryCache( );
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
        // OAuth discovery follows untrusted handles. Bound each outbound request and exclude
        // the app-wide retry policy, which otherwise honors multi-minute Retry-After delays.
        // This .NET extension is currently marked experimental, but is the supported way
        // to remove the inherited standard pipeline from a single named client.
#pragma warning disable EXTEXP0001
        _ = services.AddHttpClient( "idunno.atproto" )
            .RemoveAllResilienceHandlers( );
#pragma warning restore EXTEXP0001
        // The SDK also configures this named client and sets a five-minute timeout.
        // PostConfigure runs after its registration, so this bound wins.
        _ = services.PostConfigure<HttpClientFactoryOptions>( "idunno.atproto", options =>
            options.HttpClientActions.Add( client => client.Timeout = TimeSpan.FromSeconds( 30 ) ) );
        _ = services.AddSingleton( new OAuthLoginClientAddressResolver( settings.OAuthTrustedProxyHost ) );
        _ = services.AddHostedService( provider => provider.GetRequiredService<OAuthLoginClientAddressResolver>( ) );
        _ = services.AddRateLimiter( options => {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy( LoginStartRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter( context.RequestServices.GetRequiredService<OAuthLoginClientAddressResolver>( ).GetPartitionKey( context ),
                    _ => new FixedWindowRateLimiterOptions {
                        PermitLimit = 60,
                        Window = TimeSpan.FromMinutes( 1 ),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    } ) );
        } );
        _ = services.AddOptions<BlueskyAuthenticationOptions>( BlueskyAuthenticationDefaults.AuthenticationScheme )
            .Configure<ILoggerFactory, IHttpContextAccessor>( ( options, loggerFactory, httpContextAccessor ) => {
                options.IdentityStore = new BridgeBeatsIdentityStore( settings.IdentityConnectionString, httpContextAccessor, loggerFactory );
                options.CorrelationStateCache = new SqliteCorrelationStateCache( settings.IdentityConnectionString, loggerFactory: loggerFactory );
            } );
        // SDK v8 registers its concrete handler through AddBluesky. Our handler must run before
        // SDK cookie reads so a stale reference cannot revoke a newer DID session.
        _ = services.PostConfigure<AuthenticationOptions>( options =>
            options.SchemeMap[BlueskyAuthenticationDefaults.AuthenticationScheme].HandlerType = typeof( BridgeBeatsBlueskyAuthenticationHandler ) );
        _ = services.PostConfigure<BlueskyAuthenticationOptions>( BlueskyAuthenticationDefaults.AuthenticationScheme, options =>
            options.CookieManager = new BridgeBeatsBlueskyAuthenticationHandler.GuardedCookieManager( options.CookieManager ) );
        _ = services.PostConfigure<CookieAuthenticationOptions>( IdentityConstants.ApplicationScheme, options => {
            Func<CookieValidatePrincipalContext, Task> validate = options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal = async context => {
                await validate( context );
                if (context.Principal is null) { return; }
                if (context.Properties.GetString( ValidatedPasswordCookie ) == "1") { return; }
                UserManager<ApplicationUser> users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>( );
                ApplicationUser? user = await users.GetUserAsync( context.Principal );
                // Old OAuth-only Identity cookies carry no SDK session. Require one new OAuth login after upgrade.
                if (user is null or { AtProtoDid: not null, PasswordHash: null }) {
                    context.RejectPrincipal( );
                    await context.HttpContext.SignOutAsync( IdentityConstants.ApplicationScheme );
                } else {
                    // Only legacy/unmarked tickets need this lookup. Renew once with a protected marker;
                    // built-in security-stamp validation continues on its normal schedule.
                    context.Properties.SetString( ValidatedPasswordCookie, "1" );
                    context.ShouldRenew = true;
                }
            };
            Func<CookieSigningInContext, Task> signIn = options.Events.OnSigningIn;
            options.Events.OnSigningIn = async context => {
                DeleteLegacyHostOnlyIdentityCookie( context.HttpContext, options );
                if (context.HttpContext.Items.ContainsKey( PasswordSignInKey )) {
                    context.Properties.SetString( ValidatedPasswordCookie, "1" );
                }
                await signIn( context );
            };
            Func<CookieSigningOutContext, Task> signOut = options.Events.OnSigningOut;
            options.Events.OnSigningOut = async context => {
                DeleteLegacyHostOnlyIdentityCookie( context.HttpContext, options );
                await signOut( context );
            };
        } );
        _ = services.AddOpenTelemetry( ).WithMetrics( metrics => metrics.AddBlueskyAuthenticationMetrics( ) );
        _ = services.AddBlueskyAgentFactory( );
        _ = services.AddBlueskyOAuthClientMetadata( options => options.LogoUri = new Uri( $"{domain}/public/Icons/BridgeBeats_FullColor.svg" ) );
        return services;
    }

    private static void DeleteLegacyHostOnlyIdentityCookie( HttpContext context, CookieAuthenticationOptions options ) {
        if (options.Cookie.Domain is null) { return; }
        // The previous release used a host-only cookie. A domain-scoped deletion cannot remove it.
        CookieOptions hostOnly = options.Cookie.Build( context );
        hostOnly.Domain = null;
        string name = options.Cookie.Name == UpgradedIdentityCookieName
            ? LegacyIdentityCookieName : options.Cookie.Name!;
        context.Response.Cookies.Delete( name, hostOnly );
        foreach (string chunk in context.Request.Cookies.Keys.Where( key =>
                     key.StartsWith( name + "C", StringComparison.Ordinal ) &&
                     int.TryParse( key.AsSpan( name.Length + 1 ), out _ ) )) {
            context.Response.Cookies.Delete( chunk, hostOnly );
        }
    }

    // Keep existing installer-generated JWK secrets and their key IDs valid; PEM is also accepted.
    internal static OAuthClientSigningKey LoadSigningKey( string path ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( path );
        string contents = File.ReadAllText( path );
        if (string.IsNullOrWhiteSpace( contents ) || contents.Trim( ) == "{}") {
            throw new InvalidDataException( $"ATProto OAuth signing key file is empty: {path}" );
        }
        if (contents.TrimStart( ).StartsWith( '{' )) {
            using ATProtoSigningKeyProvider key = new( contents );
            return OAuthClientSigningKey.FromPem( key.SigningKey.ExportPkcs8PrivateKeyPem( ), key.KeyId );
        }
        return OAuthClientSigningKey.FromPem( contents );
    }
}
