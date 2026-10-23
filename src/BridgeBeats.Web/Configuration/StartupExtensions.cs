using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AspNetCore.Authentication.ApiKey;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Infrastructure.Cache;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Web.Authentication;
using BridgeBeats.Web.Middleware;
using BridgeBeats.Web.Services;
using idunno.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using StackExchange.Redis;

namespace BridgeBeats.Web.Configuration {

    /// <summary>
    /// Extension methods that compose the BridgeBeats web application: registering configuration,
    /// services, authentication, databases, and the HTTP request pipeline.
    /// </summary>
    internal static class StartupExtensions {

        /// <summary>
        /// Configures the host-level services: application settings, file logging (outside the Testing
        /// environment), Aspire service defaults, the Redis client, and the BridgeBeats service graph.
        /// </summary>
        /// <param name="builder">The web application builder being configured.</param>
        /// <param name="args">The command-line arguments used when building configuration.</param>
        /// <returns>The same <paramref name="builder"/> instance, to allow call chaining.</returns>
        public static WebApplicationBuilder ConfigureBridgeBeatsServices(
            this WebApplicationBuilder builder,
            string[] args
        ) {
            // Always load configuration (appsettings.json, user secrets, env vars)
            // Tests can override via environment variables or WebApplicationFactory hooks
            _ = builder.Configuration.ConfigureAppSettings( args );

            // Configure logging only in non-Testing environments
            if (builder.Environment.EnvironmentName != "Testing") {
                _ = builder.ConfigureFileLogging( "Web" );
            }

            _ = builder.AddServiceDefaults( );

            // Register Redis client via Aspire (provides IConnectionMultiplexer)
            // In tests, CustomWebApplicationFactory provides the connection string via Testcontainers
            builder.AddRedisClient( "redis" );

            // The direct-mode Apple Music, Tidal, and Spotify lookup services require the track genre
            // cache. It is registered unconditionally (not only in direct-provider mode) so that
            // the ASP.NET Core Development-environment default for ValidateOnBuild can resolve those
            // services' dependencies regardless of which provider mode is configured.
            _ = builder.Services.AddGenreCache( );

            _ = builder.WebHost.ConfigureBridgeBeatsServices(
                builder.Services,
                builder.Configuration
            );

            return builder;
        }

        /// <summary>
        /// Registers the BridgeBeats service graph against the supplied service collection from within a
        /// web host builder, returning the builder for chaining.
        /// </summary>
        /// <typeparam name="TBuilder">The web host builder type.</typeparam>
        /// <param name="builder">The web host builder being configured.</param>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="config">The configuration used to bind settings.</param>
        /// <returns>The same <paramref name="builder"/> instance, to allow call chaining.</returns>
        internal static TBuilder ConfigureBridgeBeatsServices<TBuilder>(
            this TBuilder builder,
            IServiceCollection services,
            IConfiguration config
        ) where TBuilder : IWebHostBuilder {
            _ = AddBridgeBeatsServices( services, config );
            return builder;
        }

        /// <summary>
        /// Registers the full BridgeBeats service graph: MVC with views, configuration-bound settings,
        /// memory cache, antiforgery, databases, identity, API-key authentication, Swagger, ATProto
        /// integration, and the enabled music providers.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="config">The configuration used to bind the <c>BridgeBeats</c> settings section.</param>
        /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no music providers can be enabled from the configured settings.</exception>
        internal static IServiceCollection AddBridgeBeatsServices(
            this IServiceCollection services,
            IConfiguration config
        ) {
            // Add services to the container.
            _ = services
                .AddControllersWithViews( )
                .AddRazorOptions( o => {
                    o.ViewLocationFormats.Clear( );
                    o.ViewLocationFormats.Add( "/Views/{1}/{0}.cshtml" );
                    o.ViewLocationFormats.Add( "/Views/Shared/{0}.cshtml" );
                } );

            AppSettings settings = new( );
            config.GetRequiredSection( "BridgeBeats" ).Bind( settings );

            // Common singletons and caching
            _ = services.AddSingleton( new JsonSerializerOptions { WriteIndented = true } );
            _ = services.AddSingleton( TimeProvider.System );
            _ = services.AddMemoryCache( );

            // Configure QueueSettings from configuration (with defaults)
            _ = services.Configure<QueueSettings>(
                config.GetSection( "BridgeBeats:Queue" )
            );

            // Configure antiforgery to accept tokens in headers for JSON requests
            _ = services.AddAntiforgery( options => {
                options.HeaderName = "X-XSRF-TOKEN";
            } );

            ConfigureDatabases( services, settings );
            ConfigureIdentity( services, settings );
            ConfigureApiKeyAuth( services, settings );
            ConfigureSwagger( services );

            // Optional ATProto storage and cache services
            ConfigureATProtoIfEnabled( services, settings );

            // Provider registrations (Apple/Spotify/Tidal)
            HashSet<SupportedProviders> enabledProviders = RegisterMusicProviders( services, settings );

            // Validate at least one provider
            if (enabledProviders.Count == 0) {
                throw new InvalidOperationException( "Required settings are missing. Cannot add BridgeBeats services if no IMusicLookupService(s) are available." );
            }

            // Register all BridgeBeats services (media link resolver, card services, playlist cleanup)
            bool useCaching = !string.IsNullOrWhiteSpace( settings.ATProtoIdentifier ) &&
                              !string.IsNullOrWhiteSpace( settings.ATProtoPassword );
            _ = services.AddBridgeBeatsServices(
                enabledProviders,
                useCaching,
                settings.Domain,
                settings.CardCacheExpirationHours,
                settings.CardCacheCleanupInterval,
                settings.CardCacheMaxEntries
            );
            _ = services.AddSingleton( enabledProviders );

            return services;
        }

        /// <summary>
        /// Builds the application and configures the HTTP request pipeline: database initialization
        /// (outside Testing), exception handling and HSTS in non-development environments, HTTPS
        /// redirection, static file serving with a Content Security Policy, routing, the BridgeBeats
        /// middleware (health, authentication, authorization, Swagger gating, rate limiting), Swagger,
        /// and controller routing.
        /// </summary>
        /// <param name="builder">The configured web application builder to build and wire.</param>
        /// <returns>The built and configured <see cref="WebApplication"/>.</returns>
        public static async Task<WebApplication> ConfigureBridgeBeatsAsync(
            this WebApplicationBuilder builder
        ) {
            WebApplication app = builder.Build();

            IConfiguration config = app.Configuration;

            AppSettings settings = new( );
            config.GetRequiredSection( "BridgeBeats" ).Bind( settings );

            // Skip database initialization in Testing environment - tests will configure their own databases
            // via WebApplicationFactory.ConfigureServices which replaces the DbContext factories
            if (app.Environment.EnvironmentName != "Testing") {
                // Initialize cache database if configured
                InitializeCacheDatabase( app.Services );

                // Initialize database and seed roles
                _ = await app.InitializeDatabaseAsync( );
            }

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment( )) {
                _ = app.UseExceptionHandler( "/Home/Error" );
                _ = app.UseHsts( );
            }

            _ = app.UseHttpsRedirection( );

            // Configure static file provider with proper MIME types for AT Protocol lexicon files
            FileExtensionContentTypeProvider provider = new() { Mappings = { [".json"] = "application/json" } };

            // Serve .well-known directory with proper content types for AT Protocol lexicons
            _ = app.UseStaticFiles( new StaticFileOptions {
                FileProvider = new PhysicalFileProvider( Path.Combine( builder.Environment.WebRootPath, ".well-known" ) ),
                RequestPath = "/.well-known",
                ContentTypeProvider = provider,
                ServeUnknownFileTypes = true, // Allow serving files without extensions (required for ATProto lexicons)
                DefaultContentType = "application/octet-stream", // Default for unknown types
                OnPrepareResponse = ctx => {
                    // Serve lexicon files in atproto-lexicon directory as application/json
                    if (ctx.File.PhysicalPath != null && ctx.File.PhysicalPath.Contains( "atproto-lexicon", StringComparison.OrdinalIgnoreCase )) {
                        ctx.Context.Response.ContentType = "application/json; charset=utf-8";
                    }

                    // Add CORS headers to allow ATProto PDS to fetch lexicon files
                    ctx.Context.Response.Headers.Append( "Access-Control-Allow-Origin", "*" );
                    ctx.Context.Response.Headers.Append( "Access-Control-Allow-Methods", "GET, HEAD, OPTIONS" );
                    ctx.Context.Response.Headers.Append( "Access-Control-Allow-Headers", "Content-Type" );

                    // Cache lexicon files for 1 day
                    ctx.Context.Response.Headers.Append( "Cache-Control", "public, max-age=86400" );
                }
            } );

            // CSP middleware: adds Content-Security-Policy header. Relax frame-ancestors for embed endpoints.
            // Must be placed before the main UseStaticFiles() call to ensure CSP headers are applied to those responses.
            _ = app.Use( async ( ctx, next ) => {
                string path = ctx.Request.Path.Value ?? string.Empty;
                bool isEmbed =
                    path.EndsWith( "/embed", StringComparison.OrdinalIgnoreCase ) ||
                    path.EndsWith( "/embed/qr", StringComparison.OrdinalIgnoreCase )
                ;
                bool isAppleMusic = path.StartsWith( "/applemusic", StringComparison.OrdinalIgnoreCase );

                // Generate a unique nonce for this request to allow inline scripts and styles
                string nonce = Convert.ToBase64String( RandomNumberGenerator.GetBytes( 16 ) );
                ctx.Items["CSPNonce"] = nonce;

                // Build CSP policy
                // Allow MusicKit JS CDN, Cloudflare analytics, nonce-based inline styles, and API calls to music providers
                // Embed endpoints omit frame-ancestors entirely so they can be framed from any origin including
                // non-network schemes (file://, blob:, etc.) that the '*' wildcard does not cover per the CSP spec.
                // Apple Music integration pages allow HTTPS framing; all other pages restrict to same-origin.
                List<string> directives = [
                    "default-src 'self'",
                    $"script-src 'self' 'nonce-{nonce}' https://js-cdn.music.apple.com https://static.cloudflareinsights.com",
                    $"style-src 'self' 'nonce-{nonce}'",
                    "img-src 'self' data: https:",
                    "font-src 'self' data:",
                    "connect-src 'self' https://api.music.apple.com https://accounts.spotify.com https://api.spotify.com https://openapi.tidal.com https://cloudflareinsights.com",
                    "media-src 'self' https:",
                    "object-src 'none'",
                    "base-uri 'self'",
                    "form-action 'self'"
                ];

                if (!isEmbed) {
                    string frameAncestors = isAppleMusic ? "https: 'self'" : "'self'";
                    directives.Add( "frame-ancestors " + frameAncestors );
                }

                string csp = string.Join( "; ", directives );

                _ = ctx.Response.Headers.Remove( "Content-Security-Policy" );
                ctx.Response.Headers.Append( "Content-Security-Policy", csp );

                await next( );
            } );

            _ = app.UseStaticFiles( ); // Serve static files from wwwroot
            _ = app.UseRouting( );

            // Restrict liveness endpoint access to internal requests only
            // NOTE: This middleware is intentionally placed before authentication because it uses IP-based authorization
            // and does not require authenticated user context. If future changes require authentication, adjust the order accordingly.
            _ = app.UseMiddleware<HealthEndpointAuthorizationMiddleware>( );

            _ = app.UseAuthentication( );
            _ = app.UseAuthorization( );

            // Restrict Swagger UI access to authenticated users
            _ = app.UseMiddleware<SwaggerAuthorizationMiddleware>( );

            // Add rate limiting middleware with configured rate limit
            _ = app.UseMiddleware<RateLimitingMiddleware>( settings.RateLimitRequestsPerHour );

            // Enable Swagger middleware
            _ = app.UseSwagger( );
            _ = app.UseSwaggerUI( options => {
                options.SwaggerEndpoint( "/swagger/v1/swagger.json", "BridgeBeats API v1" );
                options.RoutePrefix = "swagger";
                options.DocumentTitle = "BridgeBeats API Documentation";
            } );

            _ = app.MapDefaultEndpoints( );
            _ = app.MapStaticAssets( );
            _ = app.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}" )
                .WithStaticAssets( );

            return app;
        }

        /// <summary>
        /// Layers the configuration sources in precedence order: the required <c>appsettings.json</c> file,
        /// optional user secrets, command-line arguments, and environment variables.
        /// </summary>
        /// <param name="config">The configuration builder to populate.</param>
        /// <param name="args">The command-line arguments to add as a configuration source.</param>
        /// <returns>The same <paramref name="config"/> instance, to allow call chaining.</returns>
        private static IConfigurationBuilder ConfigureAppSettings(
            this IConfigurationBuilder config,
            string[] args
        ) {
            _ = config.AddJsonFile(
                path: "appsettings.json",
                optional: false,
                reloadOnChange: false
            );

            // Always load user secrets in non-production environments
            // Tests use WebApplicationFactory.ConfigureAppConfiguration to inject test config
            // which overrides user secrets values for test-specific settings
            _ = config.AddUserSecrets<Program>( optional: true );

            return config
                .AddCommandLine( args )
                .AddEnvironmentVariables( );
        }

        /// <summary>
        /// Verifies the Redis cache connection at startup, logging when Redis is absent and throwing when a
        /// configured Redis instance is not reachable.
        /// </summary>
        /// <param name="serviceProvider">The service provider used to resolve the Redis connection and logger.</param>
        /// <exception cref="InvalidOperationException">Thrown when Redis is configured but not connected.</exception>
        private static void InitializeCacheDatabase( IServiceProvider serviceProvider ) {
            ILogger logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BridgeBeats.Web.Configuration.StartupExtensions");
            try {
                // Validate Redis connection (fail-fast if unavailable)
                IConnectionMultiplexer? redis = serviceProvider.GetService<IConnectionMultiplexer>( );
                if (redis is null) {
                    StartupExtensionsLog.LogRedisNotConfigured( logger );
                    return;
                }

                if (!redis.IsConnected) {
                    throw new InvalidOperationException( "Redis connection is not established. Check Redis configuration and connectivity." );
                }

                StartupExtensionsLog.LogRedisConnected( logger );
            } catch (Exception ex) {
                StartupExtensionsLog.LogRedisFailed( logger, ex );
                throw; // Fail-fast on Redis unavailability
            }
        }

        /// <summary>
        /// Registers the Identity database context factory backed by SQLite, with migrations sourced from
        /// the <c>BridgeBeats.Core</c> assembly.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="settings">The settings supplying the Identity connection string.</param>
        internal static void ConfigureDatabases( IServiceCollection services, AppSettings settings ) {
            // Register DbContext factory for Identity only (SQLite).
            // Lane B: the sp-overload of AddDbContextFactory receives IServiceProvider so we can resolve
            // the DP provider and pass the token protector directly to ApplicationDbContext.
            // ConfigureDatabases runs before ConfigureIdentity (see AddBridgeBeatsServices call order),
            // so IDataProtectionProvider is not yet registered when this line executes — but the
            // singleton factory lambda below captures the service provider and resolves the protector
            // lazily at first use, after all registrations are complete. Call order no longer affects
            // which descriptor survives — both null-protector descriptors are removed immediately after
            // AddDbContextFactory (see below); a pathological reverse order would fail-fast at first
            // resolve (the factory lambda throws if IDataProtectionProvider is unregistered), never
            // silently fall back to plaintext.
            _ = services.AddDbContextFactory<ApplicationDbContext>( ( sp, options ) => {
                _ = options.UseSqlite(
                    settings.IdentityConnectionString,
                    b => b.MigrationsAssembly( "BridgeBeats.Core" )
                );
            } );

            // AddDbContextFactory<T> auto-registers TWO descriptors: an IDbContextFactory<T> and a
            // scoped ApplicationDbContext — both wired to the options-only constructor (null protector).
            // Remove BOTH before registering the singleton factory below so that no null-protector
            // ApplicationDbContext descriptor survives regardless of the order ConfigureDatabases and
            // ConfigureIdentity are called. The only remaining scoped ApplicationDbContext descriptor
            // will be the one AddBridgeBeatsIdentity registers via the factory (with the protector).
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor? existingFactory =
                services.FirstOrDefault( d =>
                    d.ServiceType == typeof( Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> ) );
            if (existingFactory is not null) {
                _ = services.Remove( existingFactory );
            }

            Microsoft.Extensions.DependencyInjection.ServiceDescriptor? nullScopedContext =
                services.FirstOrDefault( d =>
                    d.ServiceType == typeof( ApplicationDbContext ) );
            if (nullScopedContext is not null) {
                _ = services.Remove( nullScopedContext );
            }

            _ = services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>( sp => {
                // Lane B: create the token protector once at factory-build time. The protector is
                // singleton-safe: IDataProtector created from a singleton IDataProtectionProvider is
                // thread-safe and stateless.
                Microsoft.AspNetCore.DataProtection.IDataProtector tokenProtector =
                    sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>( )
                      .CreateProtector( ApplicationDbContext.TokenProtectorPurpose );

                DbContextOptions<ApplicationDbContext> contextOptions =
                    new DbContextOptionsBuilder<ApplicationDbContext>( )
                        .UseSqlite(
                            settings.IdentityConnectionString,
                            b => b.MigrationsAssembly( "BridgeBeats.Core" )
                        )
                        .Options;

                return new TokenEncryptingDbContextFactory( contextOptions, tokenProtector );
            } );
        }

        /// <summary>
        /// Singleton factory that injects the token protector into every <see cref="ApplicationDbContext"/>
        /// it creates, enabling the EF value converter for the four credential columns at rest.
        /// Thread-safe: <c>CreateDbContext</c> allocates a new context per call; the shared options and
        /// protector are immutable.
        /// </summary>
        private sealed class TokenEncryptingDbContextFactory
            : Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext> {
            private readonly DbContextOptions<ApplicationDbContext> _options;
            private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _protector;

            internal TokenEncryptingDbContextFactory(
                DbContextOptions<ApplicationDbContext> options,
                Microsoft.AspNetCore.DataProtection.IDataProtector protector
            ) {
                _options = options;
                _protector = protector;
            }

            public ApplicationDbContext CreateDbContext( ) => new( _options, _protector );
        }

        /// <summary>
        /// Registers BridgeBeats Identity with Data Protection and configures the application cookie as
        /// secure-always, HTTP-only, and same-site lax, scoping the cookie domain when the configured
        /// domain is a registrable host.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="settings">The settings supplying the Data Protection key path and domain.</param>
        private static void ConfigureIdentity( IServiceCollection services, AppSettings settings ) {
            // Use the centralized Identity configuration from Infrastructure project.
            // This registers Data Protection (shared with workers), Identity Core, and
            // AddPersonalDataProtection — which makes IPersonalDataProtector available for the ATProto
            // OAuth service's OAuth-state encrypt/decrypt path. The four credential columns
            // (AppleMusicUserToken, AtProtoAccessToken, AtProtoRefreshToken, AtProtoDPoPKey) are
            // encrypted separately via the EF value converter in ApplicationDbContext.OnModelCreating
            // using a dedicated IDataProtector with purpose BridgeBeats.ApplicationUser.Tokens.v1 —
            // not via [ProtectedPersonalData], which has been removed from those fields.
            _ = services.AddBridgeBeatsIdentity( settings.DataProtectionKeyPath );

            _ = services.ConfigureApplicationCookie( options => {
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.HttpOnly = true;

                string domain = settings.Domain.Trim().TrimStart('.').TrimEnd('.');
                if (ShouldSetCookieDomain( domain )) {
                    options.Cookie.Domain = $".{domain}";
                }
            } );
        }

        /// <summary>
        /// Determines whether the application cookie should be scoped to a parent domain. Returns
        /// <c>false</c> for empty values, values containing scheme, path, or port characters, <c>localhost</c>,
        /// and bare IP addresses.
        /// </summary>
        /// <param name="domain">The candidate cookie domain.</param>
        /// <returns><c>true</c> when the domain is a registrable host suitable for cookie scoping; otherwise <c>false</c>.</returns>
        private static bool ShouldSetCookieDomain( string domain ) {
            if (string.IsNullOrWhiteSpace( domain )) {
                return false;
            }

            // Cookie domains must be host-only (no scheme, path, or port)
            if (domain.Contains( "://", StringComparison.OrdinalIgnoreCase ) ||
                domain.Contains( '/', StringComparison.Ordinal ) ||
                domain.Contains( ':', StringComparison.Ordinal )) {
                return false;
            }

            // Avoid forcing Domain on localhost/IPs; let the browser use host-only cookies
            return !string.Equals( domain, "localhost", StringComparison.OrdinalIgnoreCase ) &&
!IPAddress.TryParse( domain, out _ );
        }

        /// <summary>
        /// Registers the API-key hasher and the multi-scheme authentication policy. The policy forwards to
        /// internal-service-key authentication when the service-key header is present, to API-key
        /// authentication when the <c>X-API-Key</c> header is present, and otherwise to the Identity cookie.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="settings">The settings supplying the API-key salt and internal-service key.</param>
        /// <exception cref="InvalidOperationException">Thrown when the API-key salt is missing.</exception>
        private static void ConfigureApiKeyAuth( IServiceCollection services, AppSettings settings ) {
            // Configure API Key hashing
            if (string.IsNullOrWhiteSpace( settings.ApiKeySalt )) {
                throw new InvalidOperationException( "ApiKeySalt is required in configuration for secure API key storage." );
            }
            _ = services.AddSingleton( new ApiKeyHasher( settings.ApiKeySalt ) );

            // Configure API Key authentication for API endpoints
            _ = services.AddTransient<IApiKeyProvider, ApiKeyProvider>( );
            _ = services.AddAuthentication( options => {
                options.DefaultScheme = "MultiScheme";
                options.DefaultChallengeScheme = "MultiScheme";
            } )
            .AddPolicyScheme( "MultiScheme", "API Key, Service Key, or Cookie", options => {
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey( InternalServiceDefaults.HeaderName )
                        ? InternalServiceDefaults.AuthenticationScheme
                        : context.Request.Headers.ContainsKey( "X-API-Key" )
                            ? ApiKeyDefaults.AuthenticationScheme
                            : IdentityConstants.ApplicationScheme;
            } )
            .AddApiKeyInHeader<ApiKeyProvider>( options => {
                options.Realm = "BridgeBeats API";
                options.KeyName = "X-API-Key";
            } )
            .AddScheme<InternalServiceAuthOptions, InternalServiceAuthHandler>(
                InternalServiceDefaults.AuthenticationScheme,
                options => { options.ServiceKey = settings.InternalServiceKey; }
            )
            .AddIdentityCookies( ); // Add cookie authentication for web UI

            _ = services.AddAuthorization( );
        }

        /// <summary>
        /// Registers Swagger generation with the API metadata, an API-key security scheme, and inclusion of
        /// the generated XML documentation files found alongside the application.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        private static void ConfigureSwagger( IServiceCollection services ) {
            _ = services.AddEndpointsApiExplorer( );
            _ = services.AddSwaggerGen( options => {
                options.SwaggerDoc( "v1", new OpenApiInfo {
                    Title = "BridgeBeats API",
                    Version = "v1",
                    Description = "Cross-platform music link converter and lookup service for Apple Music, Spotify, and Tidal. Convert music links between platforms, search by URL, ISRC, UPC, or title/artist.",
                    Contact = new OpenApiContact {
                        Name = "BridgeBeats",
                        Url = new Uri( "https://github.com/tsmarvin/BridgeBeats" )
                    },
                    License = new OpenApiLicense {
                        Name = "MIT License",
                        Url = new Uri( "https://github.com/tsmarvin/BridgeBeats/blob/main/LICENSE" )
                    }
                } );
                options.AddSecurityDefinition( "ApiKey", new OpenApiSecurityScheme {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-API-Key",
                    Description = "API Key authentication. Get your API key by registering at /account/register"
                } );
                options.AddSecurityRequirement( _ => new OpenApiSecurityRequirement { [new( "X-API-Key" )] = [] } );

                // Include XML comments from all assemblies if available
                foreach (string xmlPath in Directory.GetFiles( AppContext.BaseDirectory, "*.xml" )) {
                    options.IncludeXmlComments( xmlPath );
                }
            } );
        }

        /// <summary>
        /// Wires the ATProto integration when configured. When a domain is set, registers the OAuth signing
        /// key (if its JWK file exists and is non-empty), the SSRF-guarded OAuth HTTP client, the OAuth
        /// service, and the OAuth state cleanup hosted service. When service-account credentials are also
        /// present, validates the user DID and registers the session manager, storage, media-link cache,
        /// statistics services, and queue infrastructure.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="settings">The settings supplying the domain, signing key path, and ATProto credentials.</param>
        private static void ConfigureATProtoIfEnabled( IServiceCollection services, AppSettings settings ) {
            // Register ATProto OAuth service (available even without server ATProto credentials)
            // This allows users to log in with Bluesky for playlist management
            string? domainWithScheme = AppSettings.NormalizeDomain( settings.Domain );
            if (!string.IsNullOrWhiteSpace( domainWithScheme )) {
                string clientId = $"{domainWithScheme}/.well-known/client-metadata.json";

                // Load the OAuth signing key for confidential client authentication if configured
                ATProtoSigningKeyProvider? signingKeyProvider = null;
                if (!string.IsNullOrWhiteSpace( settings.ATProtoOAuthSigningKeyPath ) &&
                    File.Exists( settings.ATProtoOAuthSigningKeyPath )) {
                    string jwkJson = File.ReadAllText( settings.ATProtoOAuthSigningKeyPath );
                    if (!string.IsNullOrWhiteSpace( jwkJson ) && jwkJson.Trim( ) != "{}") {
                        signingKeyProvider = new ATProtoSigningKeyProvider( jwkJson );
                    }
                }

                // Register the signing key provider as a singleton (used by WellKnownController for JWKS endpoint)
                if (signingKeyProvider is not null) {
                    _ = services.AddSingleton( signingKeyProvider );
                }

                // Register HTTP client for OAuth token endpoint with standard resilience
                _ = services.AddATProtoOAuthHttpClient( );

                _ = services.AddScoped<IATProtoOAuthService>( sp =>
                    new ATProtoOAuthService(
                        sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( ),
                        sp.GetRequiredService<ILogger<ATProtoOAuthService>>( ),
                        clientId,
                        sp.GetRequiredService<IHttpClientFactory>( ),
                        sp.GetRequiredService<IPersonalDataProtector>( ),
                        signingKeyProvider
                    )
                );

                // Register background service for cleaning up expired OAuth states
                _ = services.AddHostedService<OAuthStateCleanupService>( );
            }

            if (string.IsNullOrWhiteSpace( settings.ATProtoIdentifier ) ||
                string.IsNullOrWhiteSpace( settings.ATProtoPassword )) {
                return;
            }

            // Validate DID format if provided (must start with did:plc: or did:web:)
            ATProtoUriHelper.ValidateDid( settings.ATProtoUserDID, "ATProtoUserDID" );

            // Register ATProto session manager and storage service (centralized authentication)
            _ = services.AddATProtoSessionManager( settings.ATProtoIdentifier, settings.ATProtoPassword, settings.ATProtoSessionTtlDays );
            _ = services.AddATProtoStorage( );

            // Register Redis-based cache service (requires IConnectionMultiplexer from Aspire)
            _ = services.AddSingleton<IMediaLinkCacheRepository>( s => new RedisMediaLinkCache(
                s.GetRequiredService<IConnectionMultiplexer>( ),
                s.GetRequiredService<IATProtoStorageService>( ),
                s.GetRequiredService<ILogger<RedisMediaLinkCache>>( ),
                settings.CacheDays,
                settings.ATProtoUserDID
            ) );

            // Register statistics reader (reads status:statistics from Redis; worker computes)
            _ = services.AddSingleton<IStatisticsService, RedisStatisticsReader>( );

            // Register queue infrastructure (deduplicator, rate limit tracker, saga manager)
            // and provider-specific queues for the LookupOrchestrator.
            // AddAllProviderQueues automatically applies SpotifyBulkQueueDecorator for QueuedLookupRequest,
            // routing Spotify SongIdLookup/AlbumIdLookup to the type-specific bulk streams.
            _ = services.AddQueueInfrastructure( );
            _ = services.AddAllProviderQueues<QueuedLookupRequest>( );
            _ = services.AddScoped<IRefreshReviewDispositionService, RefreshReviewDispositionService>( );

        }

        // SSRF guard (default block-list): auth-server metadata + token-endpoint hosts are
        // derived from an attacker-supplied handle on the anonymous POST account/login-atproto
        // path, so gate outbound connects at the socket layer.
        /// <summary>
        /// Registers the named <c>ATProtoOAuth</c> HTTP client with an SSRF-guarded primary handler (10-second
        /// connect timeout) and the standard resilience pipeline.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <returns>The HTTP client builder for the registered client.</returns>
        internal static IHttpClientBuilder AddATProtoOAuthHttpClient( this IServiceCollection services ) {
            IHttpClientBuilder builder = services.AddHttpClient( "ATProtoOAuth" )
                .ConfigurePrimaryHttpMessageHandler( ( ) =>
                    SsrfSocketsHttpHandlerFactory.Create( connectTimeout: TimeSpan.FromSeconds( 10 ) ) );
            _ = builder.AddStandardResilienceHandler( );
            return builder;
        }

        /// <summary>
        /// Registers the music providers. When worker services are enabled, registers the Apple Music JWT
        /// handler and the per-provider HTTP clients for the enabled workers; otherwise registers the
        /// in-process provider services from the configured credentials.
        /// </summary>
        /// <param name="services">The service collection to populate.</param>
        /// <param name="settings">The settings supplying provider credentials and worker enablement flags.</param>
        /// <returns>The set of providers that were enabled.</returns>
        private static HashSet<SupportedProviders> RegisterMusicProviders( IServiceCollection services, AppSettings settings ) {
            // When UseWorkerServices is true, register HTTP clients that call worker services
            // instead of direct provider implementations
            if (settings.Workers.UseWorkerServices) {
                // Still need AppleJwtHandler for MusicKit JS (AppleMusicController generates developer tokens)
                // and musickit-api HTTP client for user library access, even when using workers for lookups
                _ = services.AddAppleMusicJwtHandler(
                    settings.AppleTeamId,
                    settings.AppleKeyId,
                    settings.AppleKeyPath,
                    settings.Resilience.MaxRetryAfterSeconds
                );

                return services.AddMusicProviderHttpClients(
                    settings.Workers.SpotifyWorkerEnabled,
                    settings.Workers.AppleMusicWorkerEnabled,
                    settings.Workers.TidalWorkerEnabled
                );
            }

            // Otherwise, register direct provider implementations (legacy mode for testing/standalone)
            return services.AddMusicProviders(
                settings.AppleTeamId,
                settings.AppleKeyId,
                settings.AppleKeyPath,
                settings.SpotifyClientId,
                settings.SpotifyClientSecret,
                settings.TidalClientId,
                settings.TidalClientSecret,
                settings.Resilience.MaxRetryAfterSeconds
            );
        }

    }
}
