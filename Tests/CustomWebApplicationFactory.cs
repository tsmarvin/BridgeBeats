using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using StackExchange.Redis;

namespace BridgeBeats.Tests;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> over the BridgeBeats web app's entry point that
/// hosts the real application in-memory for integration and end-to-end tests. It points the app at the
/// shared Redis container, replaces the identity store with a per-instance SQLite database, and applies
/// a fast retry/timeout resilience profile.
/// </summary>
/// <remarks>
/// <para>
/// The host registers its configuration-bound settings and its music providers during eager service
/// registration, so anything this factory needs the application to honor must reach the host before
/// that registration runs — not merely before the host is built. Host configuration is therefore the
/// seam: the test infrastructure replays host configuration entries as command-line arguments into the
/// entry point, and the application layers command-line arguments above its other sources. The entries
/// this factory supplies are consequently applied at the same point as any other command-line argument
/// and drive the app's normal registration path; no test-only provider mode or parallel registration
/// path exists.
/// </para>
/// <para>
/// Because those entries outrank the machine's environment, the defaults decide the enabled provider
/// set on their own: the credential keys are supplied empty and the worker enablement flags are
/// supplied <see langword="false"/>, so exactly the providers a test's configuration intends are
/// registered no matter how the machine running the tests is configured. A caller that wants a
/// provider — or the worker branch that derives the provider set from the per-provider worker flags —
/// supplies those keys itself and its values win.
/// </para>
/// <para>
/// Tests that deliberately exercise the real providers layer their credentials on top through
/// <see cref="TestConfiguration"/>; the rest pass only the values they depend on.
/// </para>
/// </remarks>
public class CustomWebApplicationFactory : WebApplicationFactory<Web.Program> {
    /// <summary>
    /// The configuration this instance supplies to the host, keyed by normal configuration key.
    /// </summary>
    private readonly Dictionary<string, string?> _configData;
    /// <summary>Filesystem path of this instance's isolated SQLite identity database.</summary>
    private readonly string _identityDbPath;

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

        // Default test configuration. The provider credential entries are deliberately empty: they
        // replace whatever the machine's environment or user secrets hold so that a developer's own
        // credentials can neither add providers the tests do not expect nor point a provider at a key
        // file that only exists on that developer's machine.
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
            // ATProto: a developer's service-account credentials would trip DID validation during
            // registration, and the signing-key path would enable Bluesky sign-in against a key file
            // this machine may not have.
            ["BridgeBeats:ATProtoIdentifier"] = "",
            ["BridgeBeats:ATProtoPassword"] = "",
            ["BridgeBeats:ATProtoUserDID"] = "",
            ["BridgeBeats:ATProtoOAuthSigningKeyPath"] = "",
            // Apple Music: a developer's AppleKeyPath points at a local .p8 file, and provider
            // registration throws FileNotFoundException when that file is absent from the machine.
            ["BridgeBeats:AppleTeamId"] = "",
            ["BridgeBeats:AppleKeyId"] = "",
            ["BridgeBeats:AppleKeyPath"] = "",
            // Tidal: absent credentials mean the provider is simply not enabled.
            ["BridgeBeats:TidalClientId"] = "",
            ["BridgeBeats:TidalClientSecret"] = "",
            // Worker wiring is pinned so the enabled provider set is a property of this factory
            // rather than of the machine. When worker services are enabled the enabled provider set
            // comes from the per-provider worker flags instead of from the credentials above, so an
            // ambient `true` here would silently register providers no test asked for. A caller that
            // wants the worker branch passes its own values and they win, as with every other key.
            ["BridgeBeats:Workers:UseWorkerServices"] = "false",
            ["BridgeBeats:Workers:SpotifyWorkerEnabled"] = "false",
            ["BridgeBeats:Workers:AppleMusicWorkerEnabled"] = "false",
            ["BridgeBeats:Workers:TidalWorkerEnabled"] = "false",
        };

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
    /// Contributes this instance's configuration as host configuration, which the test infrastructure
    /// replays into the entry point as command-line arguments. This is what makes the settings reach
    /// the host before its eager service registration; see the type remarks.
    /// </summary>
    /// <param name="builder">The host builder supplied by the test host.</param>
    /// <returns>The same <paramref name="builder"/> instance, to allow call chaining.</returns>
    protected override IHost CreateHost( IHostBuilder builder ) {
        _ = builder.ConfigureHostConfiguration( config => config.AddInMemoryCollection( _configData ) );
        return base.CreateHost( builder );
    }

    /// <summary>
    /// Configures the test host: switches to the <c>Testing</c> environment, repoints the Redis
    /// connection and identity <c>DbContext</c> at the test infrastructure, and registers a fast
    /// resilience profile.
    /// </summary>
    /// <param name="builder">The web host builder supplied by the test host.</param>
    protected override void ConfigureWebHost( IWebHostBuilder builder ) {
        _ = builder.UseEnvironment( "Testing" );

        // Override services registered by the application
        _ = builder.ConfigureServices( ( context, services ) => {
            // CRITICAL: Replace the Aspire Redis client registration with a direct connection.
            // The Aspire AddRedisClient registration does not resolve to the container's connection
            // string under an in-memory test host, so the multiplexer is created directly here.
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
    /// Disposes the test host and deletes this instance's SQLite identity database file.
    /// </summary>
    /// <param name="disposing"><c>true</c> when called from <c>Dispose</c> rather than a finalizer.</param>
    protected override void Dispose( bool disposing ) {
        base.Dispose( disposing );

        if (disposing) {
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
