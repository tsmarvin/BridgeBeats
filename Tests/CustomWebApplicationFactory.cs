using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Providers.AppleMusic;
using BridgeBeats.Core.Domain.Providers.Spotify;
using BridgeBeats.Core.Domain.Providers.Tidal;
using BridgeBeats.Core.Domain.Services.LinkResolver;
using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Tests;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> over the BridgeBeats web app's entry point that
/// hosts the real application in-memory for integration and end-to-end tests. It points the app at the
/// shared Redis container, replaces the identity store with a per-instance SQLite database, applies a
/// fast retry/timeout resilience profile, and can swap the media-link service into a direct
/// (non-worker) mode for tests that bypass the distributed queue.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Web.Program> {
    /// <summary>The in-memory configuration overlaid on the host's normal configuration sources.</summary>
    private readonly Dictionary<string, string?> _configData;
    /// <summary>Filesystem path of this instance's isolated SQLite identity database.</summary>
    private readonly string _identityDbPath;
    /// <summary>Environment variables overridden for this factory, with their prior process values, restored on dispose to prevent cross-test contamination.</summary>
    private readonly List<(string Name, string? PriorValue)> _envRestore;

    /// <summary>
    /// Environment variables forced empty for the lifetime of every factory instance.
    /// </summary>
    /// <remarks>
    /// The host binds <c>BridgeBeats</c> settings and registers the music providers during eager
    /// service registration, which happens before this factory's in-memory configuration is applied.
    /// Any credential a developer keeps in their local user secrets therefore reaches provider
    /// startup here, so these must be neutralized at the environment level to keep the suite
    /// independent of the machine it runs on. Each entry is restored in <see cref="Dispose(bool)"/>.
    /// </remarks>
    private static readonly string[] s_hostBootCredentialKeys = [
        // ATProto: a developer's credentials would trip DID validation during registration.
        "BridgeBeats__ATProtoIdentifier",
        "BridgeBeats__ATProtoPassword",
        "BridgeBeats__ATProtoUserDID",
        "BridgeBeats__ATProtoOAuthSigningKeyPath",
        // Apple Music: a developer's AppleKeyPath points at a local .p8 file, and provider
        // registration throws FileNotFoundException when that file is absent from the machine.
        "BridgeBeats__AppleTeamId",
        "BridgeBeats__AppleKeyId",
        "BridgeBeats__AppleKeyPath",
    ];

    /// <summary>
    /// Creates a factory with the default test configuration and no overrides.
    /// </summary>
    public CustomWebApplicationFactory( ) : this( null ) { }

    /// <summary>
    /// Creates a factory, optionally overlaying caller-supplied configuration on top of the test
    /// defaults. Requires the shared Redis container and provisions a unique SQLite identity database;
    /// the Redis connection keys are protected and cannot be overridden.
    /// </summary>
    /// <param name="configOverrides">
    /// Optional configuration key/value overrides; protected Redis keys are ignored.
    /// </param>
    public CustomWebApplicationFactory( Dictionary<string, string?>? configOverrides ) {
        // IMPORTANT: Require Redis FIRST, before accessing RedisConnectionString.
        // This ensures the container is started and the connection string is populated.
        SharedTestInfrastructure.RequireRedis( );

        // Generate unique database file paths for this test instance
        string uniqueId = Guid.NewGuid().ToString( "N" )[..8];
        _identityDbPath = Path.Combine( Path.GetTempPath( ), $"BridgeBeats_Identity_{uniqueId}.db" );

        // Get the Redis connection string AFTER RequireRedis() has initialized the container
        string redisConnectionString = SharedTestInfrastructure.RedisConnectionString;

        // Default test configuration (Spotify only)
        Dictionary<string, string?> defaults = new( ) {
            ["BridgeBeats:SpotifyClientId"] = "test",
            ["BridgeBeats:SpotifyClientSecret"] = "test",
            ["BridgeBeats:DiscordToken"] = "", // Empty string to prevent Discord service registration
            ["BridgeBeats:IdentityConnectionString"] = $"Data Source={_identityDbPath}",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            // Redis connection from shared test infrastructure (captured AFTER initialization)
            // Set both keys to ensure Aspire can find the connection string
            ["ConnectionStrings:redis"] = redisConnectionString,
            ["Aspire:StackExchange:Redis:ConnectionString"] = redisConnectionString,
        };

        // Force the host-boot credentials empty so the host does not pick up a developer's
        // local user-secret credentials. See s_hostBootCredentialKeys for why environment
        // variables rather than the in-memory overlay are required.
        // Prior values are captured and restored in Dispose to keep the override scoped to
        // this factory's lifetime.
        _envRestore = [];
        foreach (string key in s_hostBootCredentialKeys) {
            _envRestore.Add( (key, Environment.GetEnvironmentVariable( key )) );
            Environment.SetEnvironmentVariable( key, "" );
        }

        // Merge overrides onto defaults (overrides win), EXCEPT for critical test infrastructure keys
        // that must always use the test container connection
        HashSet<string> protectedKeys = [
            "ConnectionStrings:redis",
            "Aspire:StackExchange:Redis:ConnectionString",
        ];

        _configData = defaults;
        if (configOverrides is not null) {
            foreach (KeyValuePair<string, string?> kvp in configOverrides) {
                // Skip protected keys - test infrastructure values must not be overridden
                if (protectedKeys.Contains( kvp.Key )) {
                    continue;
                }
                _configData[kvp.Key] = kvp.Value;
            }

            if (configOverrides.TryGetValue( "BridgeBeats:IdentityConnectionString", out string? identityCs ) && identityCs != null) {
                _identityDbPath = ExtractDataSource( identityCs );
            }
        }
    }

    /// <summary>
    /// Extracts the <c>Data Source=</c> file path from a SQLite connection string, returning the whole
    /// string when no such segment is found.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string to parse.</param>
    /// <returns>The data-source path, or the original string if no data source is present.</returns>
    private static string ExtractDataSource( string connectionString ) {
        // Extract Data Source path from connection string
        string[] parts = connectionString.Split( ';' );
        foreach (string part in parts) {
            if (part.Trim( ).StartsWith( "Data Source=", StringComparison.OrdinalIgnoreCase )) {
                return part.Trim( )["Data Source=".Length..];
            }
        }
        return connectionString; // Return original if parsing fails
    }

    /// <summary>
    /// Configures the test host: switches to the <c>Testing</c> environment, replaces any in-memory
    /// configuration with the test configuration, repoints the Redis connection and identity
    /// <c>DbContext</c> at the test infrastructure, registers a fast resilience profile, and (when
    /// <c>BridgeBeats:Workers:UseWorkerServices</c> is <c>false</c>) wires a direct, in-process
    /// media-link service over the enabled providers.
    /// </summary>
    /// <param name="builder">The web host builder supplied by the test host.</param>
    protected override void ConfigureWebHost( IWebHostBuilder builder ) {
        _ = builder.UseEnvironment( "Testing" );

        _ = builder.ConfigureAppConfiguration( ( context, config ) => {
            // Remove any existing in-memory collections to avoid conflicts
            IConfigurationSource[] existingSources = [.. config.Sources];
            config.Sources.Clear( );

            // Add back non-memory sources (like environment variables, command line, etc.)
            foreach (IConfigurationSource source in existingSources.Where( s => s is not Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource )) {
                config.Sources.Add( source );
            }

            // Add test configuration as the highest priority source (last wins)
            _ = config.AddInMemoryCollection( _configData );
        } );

        // Override services registered by the application
        _ = builder.ConfigureServices( ( context, services ) => {
            // CRITICAL: Replace the Aspire Redis client registration with a direct connection
            // The Aspire AddRedisClient doesn't work properly with WebApplicationFactory because
            // it captures configuration before ConfigureAppConfiguration runs.
            _ = services.RemoveAll<IConnectionMultiplexer>( );
            _ = services.AddSingleton<IConnectionMultiplexer>( _ => {
                string connStr = _configData.TryGetValue( "ConnectionStrings:redis", out string? cs ) && !string.IsNullOrEmpty( cs )
                    ? cs
                    : SharedTestInfrastructure.RedisConnectionString;
                return ConnectionMultiplexer.Connect( connStr );
            } );

            // Get the connection strings from our config (they may have been overridden)
            string identityConnStr = _configData.TryGetValue( "BridgeBeats:IdentityConnectionString", out string? idCs ) && idCs != null
                ? idCs
                : $"Data Source={_identityDbPath}";

            // Remove existing DbContext factory registrations
            _ = services.RemoveAll<IDbContextFactory<ApplicationDbContext>>( );
            _ = services.RemoveAll<ApplicationDbContext>( );

            // Re-register DbContext factories with test connection strings
            _ = services.AddDbContextFactory<ApplicationDbContext>( options =>
                options.UseSqlite( identityConnStr )
            );

            // Re-register scoped ApplicationDbContext for Identity
            _ = services.AddScoped( sp => {
                IDbContextFactory<ApplicationDbContext> factory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( );
                return factory.CreateDbContext( );
            } );

            // Add configuration for test-specific resilience
            _ = services.Configure<HttpStandardResilienceOptions>( "test-resilience", options => {
                options.Retry.MaxRetryAttempts = 2; // Reduce retries in tests
                options.Retry.Delay = TimeSpan.FromMilliseconds( 500 );
                options.Retry.MaxDelay = TimeSpan.FromSeconds( 5 ); // Much shorter max delay
                options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
                options.Retry.UseJitter = false; // Disable jitter for predictable test timing

                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds( 20 ); // Shorter total timeout
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds( 5 ); // Shorter per-attempt timeout
            } );

            // CRITICAL: Check if we're in direct provider mode (bypassing queue/caching)
            // This fixes the timing issue where the application's service registration happens before
            // our configuration overrides are applied. The application may have registered
            // CachingMediaLinkService (which uses LookupOrchestrator and requires workers) based on
            // the original configuration. We need to explicitly replace it with DefaultMediaLinkService
            // for tests that want direct provider access.
            bool useDirectMode = _configData.TryGetValue( "BridgeBeats:Workers:UseWorkerServices", out string? workerMode )
                && workerMode?.Equals( "false", StringComparison.OrdinalIgnoreCase ) == true;

            if (useDirectMode) {
                // Remove the potentially-registered caching service and orchestrator
                _ = services.RemoveAll<IMediaLinkService>( );
                _ = services.RemoveAll<ILookupOrchestrator>( );

                // Re-register DefaultMediaLinkService for direct provider access
                _ = services.AddTransient<IMediaLinkService>( sp => {
                    // Get enabled providers from the service provider (registered by the application)
                    HashSet<SupportedProviders> enabledProviders = sp.GetRequiredService<HashSet<SupportedProviders>>( );

                    // Build provider dictionary from registered lookup services
                    Dictionary<SupportedProviders, IMusicLookupService> providerServices = [];
                    foreach (SupportedProviders provider in enabledProviders) {
                        IMusicLookupService? lookupService = provider switch {
                            SupportedProviders.Spotify => sp.GetService<SpotifyLookupService>( ),
                            SupportedProviders.AppleMusic => sp.GetService<AppleMusicLookupService>( ),
                            SupportedProviders.Tidal => sp.GetService<TidalLookupService>( ),
                            _ => null
                        };
                        if (lookupService is not null) {
                            providerServices.Add( provider, lookupService );
                        }
                    }

                    return new DefaultMediaLinkService(
                        providerServices,
                        sp.GetRequiredService<ILogger<DefaultMediaLinkService>>( ),
                        sp.GetRequiredService<JsonSerializerOptions>( )
                    );
                } );
            }
        } );
    }

    /// <summary>
    /// Applies pending EF Core migrations to the isolated identity database and seeds the Aspire
    /// dashboard access role. Call once after the host is built and before exercising endpoints that
    /// touch identity.
    /// </summary>
    public async Task InitializeDatabasesAsync( ) {
        // Get service provider from the server
        IServiceProvider services = Services;

        // Initialize identity database and seed roles
        using (IServiceScope scope = services.CreateScope( )) {
            IDbContextFactory<ApplicationDbContext> identityFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( );
            using ApplicationDbContext identityContext = identityFactory.CreateDbContext( );
            await identityContext.Database.MigrateAsync( );
        }

        // Seed roles using the same scope approach as the production code
        using (IServiceScope scope = services.CreateScope( )) {
            Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole> roleManager =
                scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>( );

            const string AspireDashboardRole = "AspireDashboardAccess";
            if (!await roleManager.RoleExistsAsync( AspireDashboardRole )) {
                _ = await roleManager.CreateAsync( new Microsoft.AspNetCore.Identity.IdentityRole( AspireDashboardRole ) );
            }
        }
    }

    /// <summary>
    /// Disposes the test host, restores the environment variables this factory forced empty to their
    /// pre-construction values, and deletes this instance's SQLite identity database file.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <c>Dispose</c> rather than a finalizer.</param>
    protected override void Dispose( bool disposing ) {
        base.Dispose( disposing );

        if (disposing) {
            // Restore the env vars captured in the constructor so this factory does
            // not leak empty credentials settings into other tests in the same process.
            foreach ((string name, string? priorValue) in _envRestore) {
                Environment.SetEnvironmentVariable( name, priorValue );
            }

            // Clean up temp database files
            TryDeleteFile( _identityDbPath );
        }
    }

    /// <summary>
    /// Deletes the file at the given path if it exists, swallowing any I/O errors during cleanup.
    /// </summary>
    /// <param name="path">The file path to delete.</param>
    private static void TryDeleteFile( string path ) {
        try {
            if (File.Exists( path )) {
                File.Delete( path );
            }
        } catch {
            // Ignore cleanup failures - temp files will be cleaned up eventually
        }
    }
}
