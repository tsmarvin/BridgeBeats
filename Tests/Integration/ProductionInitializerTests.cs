using BridgeBeats.Contracts.Constants;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Exercises the actual production initializer <see cref="DatabaseExtensions.InitializeDatabaseAsync"/>
/// against a real <see cref="WebApplication"/> host built through the lower-level production service
/// registration (<see cref="StartupExtensions.ConfigureBridgeBeatsServices"/>), with a real SQLite
/// identity database and Identity services.
/// </summary>
/// <remarks>
/// Configuration isolation: the host's default configuration sources (appsettings.json, user
/// secrets, environment variables) are removed and replaced by one explicit in-memory source
/// composed here, so owned disposable values are authoritative before any production registration
/// reads them. Ownership of the SQLite data source and the fixture Redis endpoint is verified from
/// the built service provider before the first database operation.
/// </remarks>
[TestClass]
[DoNotParallelize] // Uses the shared disposable Redis fixture and mutates process environment in the isolation regression.
public class ProductionInitializerTests {
    /// <summary>Filesystem path of the current instance's owned SQLite identity database.</summary>
    private string _identityDbPath = string.Empty;
    /// <summary>Filesystem path of the decoy database used by the isolation regression.</summary>
    private string _decoyDbPath = string.Empty;
    /// <summary>The built web application for the current test.</summary>
    private WebApplication? _app;
    /// <summary>Environment variables overridden by the isolation regression, with prior values for restore.</summary>
    private List<(string Name, string? PriorValue)>? _envRestore;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Creates unique owned and decoy SQLite database paths for the current test.
    /// </summary>
    [TestInitialize]
    public void Setup( ) {
        _identityDbPath = Path.Combine(
            Path.GetTempPath( ),
            $"BridgeBeats_Initializer_{Guid.NewGuid( ).ToString( "N" )[..8]}.db" );
        _decoyDbPath = Path.Combine(
            Path.GetTempPath( ),
            $"BridgeBeats_Initializer_DECOY_{Guid.NewGuid( ).ToString( "N" )[..8]}.db" );
    }

    /// <summary>
    /// Disposes the host (never started: only built) and deletes owned database files.
    /// </summary>
    [TestCleanup]
    public async Task Cleanup( ) {
        if (_app is not null) {
            await _app.DisposeAsync( );
            _app = null;
        }

        if (_envRestore is not null) {
            foreach ((string name, string? priorValue) in _envRestore) {
                Environment.SetEnvironmentVariable( name, priorValue );
            }
            _envRestore = null;
        }

        foreach (string path in new[] { _identityDbPath, _decoyDbPath }) {
            try {
                if (File.Exists( path )) {
                    File.Delete( path );
                }
            } catch (IOException) {
                // Best-effort cleanup.
            }
        }
    }

    /// <summary>
    /// Runs the actual initializer on a fresh empty database and asserts migrations are applied and
    /// both required roles exist.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task InitializeDatabaseAsync_FreshDatabase_AppliesMigrationsAndSeedsRoles( ) {
        // Arrange
        WebApplication app = BuildApp( );
        _app = app;
        VerifyOwnedDataSource( app );

        // Act — the actual production method.
        _ = await app.InitializeDatabaseAsync( );

        // Assert — read back through a fresh scope.
        using IServiceScope scope = app.Services.CreateScope( );
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole> roleManager =
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>( );

        List<string> applied = [.. await context.Database.GetAppliedMigrationsAsync( TestContext.CancellationToken )];
        Assert.IsNotEmpty( applied, "Migrations must be applied by the initializer on a fresh database." );

        Assert.IsTrue( await roleManager.RoleExistsAsync( Roles.AspireDashboardAccess ),
            "AspireDashboardAccess role must be seeded." );
        Assert.IsTrue( await roleManager.RoleExistsAsync( Roles.PdsRecordAdministrator ),
            "PdsRecordAdministrator role must be seeded." );
    }

    /// <summary>
    /// Seeds expired, fresh, and null rate-limit windows, runs the actual initializer, reloads from a
    /// fresh context, and asserts expired rows are reset while fresh/null rows survive unchanged.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task InitializeDatabaseAsync_ExpiredWindows_ResetAndFreshWindowsUnchanged( ) {
        // Arrange
        WebApplication app = BuildApp( );
        _app = app;
        VerifyOwnedDataSource( app );
        _ = await app.InitializeDatabaseAsync( ); // migrate + seed first

        DateTime expiredWindow = DateTime.UtcNow.AddHours( -2 ); // comfortably past the 1h floor
        DateTime freshWindow = DateTime.UtcNow.AddMinutes( -5 );  // comfortably inside the 1h floor

        using (IServiceScope scope = app.Services.CreateScope( )) {
            ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );

            ApplicationUser expired = new( ) {
                Id = Guid.NewGuid( ).ToString( ),
                UserName = "expired-user@example.com",
                NormalizedUserName = "EXPIRED-USER@EXAMPLE.COM",
                Email = "expired-user@example.com",
                NormalizedEmail = "EXPIRED-USER@EXAMPLE.COM",
                SecurityStamp = Guid.NewGuid( ).ToString( ),
                RequestCount = 7,
                RateLimitWindowStart = expiredWindow,
            };
            ApplicationUser fresh = new( ) {
                Id = Guid.NewGuid( ).ToString( ),
                UserName = "fresh-user@example.com",
                NormalizedUserName = "FRESH-USER@EXAMPLE.COM",
                Email = "fresh-user@example.com",
                NormalizedEmail = "FRESH-USER@EXAMPLE.COM",
                SecurityStamp = Guid.NewGuid( ).ToString( ),
                RequestCount = 3,
                RateLimitWindowStart = freshWindow,
            };
            ApplicationUser nullWindow = new( ) {
                Id = Guid.NewGuid( ).ToString( ),
                UserName = "null-window-user@example.com",
                NormalizedUserName = "NULL-WINDOW-USER@EXAMPLE.COM",
                Email = "null-window-user@example.com",
                NormalizedEmail = "NULL-WINDOW-USER@EXAMPLE.COM",
                SecurityStamp = Guid.NewGuid( ).ToString( ),
                RequestCount = 4,
                RateLimitWindowStart = null,
            };
            context.Users.AddRange( expired, fresh, nullWindow );
            _ = await context.SaveChangesAsync( TestContext.CancellationToken );
        }

        // Act — run the actual production initializer again with pre-existing rows.
        _ = await app.InitializeDatabaseAsync( );

        // Assert — reload from a fresh context so no tracked state masks the persisted rows.
        using (IServiceScope scope = app.Services.CreateScope( )) {
            ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );

            ApplicationUser expiredAfter = await context.Users.AsNoTracking( )
                .FirstAsync( u => u.UserName == "expired-user@example.com", TestContext.CancellationToken );
            Assert.AreEqual( 0, expiredAfter.RequestCount,
                "Expired-window user must have RequestCount reset to 0 by the recovery pass." );
            Assert.IsNull( expiredAfter.RateLimitWindowStart,
                "Expired-window user must have RateLimitWindowStart cleared to NULL." );

            ApplicationUser freshAfter = await context.Users.AsNoTracking( )
                .FirstAsync( u => u.UserName == "fresh-user@example.com", TestContext.CancellationToken );
            Assert.AreEqual( 3, freshAfter.RequestCount,
                "Fresh-window user must keep its request count." );
            Assert.IsNotNull( freshAfter.RateLimitWindowStart,
                "Fresh-window user must keep its window start." );

            ApplicationUser nullAfter = await context.Users.AsNoTracking( )
                .FirstAsync( u => u.UserName == "null-window-user@example.com", TestContext.CancellationToken );
            Assert.AreEqual( 4, nullAfter.RequestCount,
                "Null-window user must keep its request count." );
            Assert.IsNull( nullAfter.RateLimitWindowStart );
        }
    }

    /// <summary>
    /// Runs the initializer twice on the same database and asserts exactly one of each required role
    /// exists — no duplicates.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task InitializeDatabaseAsync_Repeated_IsIdempotentWithoutDuplicateRoles( ) {
        // Arrange
        WebApplication app = BuildApp( );
        _app = app;
        VerifyOwnedDataSource( app );
        _ = await app.InitializeDatabaseAsync( );

        // Act — second invocation on the same database.
        _ = await app.InitializeDatabaseAsync( );

        // Assert
        using IServiceScope scope = app.Services.CreateScope( );
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );

        int dashboardRoles = await context.Roles
            .CountAsync( r => r.Name == Roles.AspireDashboardAccess, TestContext.CancellationToken );
        int pdsRoles = await context.Roles
            .CountAsync( r => r.Name == Roles.PdsRecordAdministrator, TestContext.CancellationToken );

        Assert.AreEqual( 1, dashboardRoles, "AspireDashboardAccess must exist exactly once after repeated initialization." );
        Assert.AreEqual( 1, pdsRoles, "PdsRecordAdministrator must exist exactly once after repeated initialization." );
    }

    /// <summary>
    /// Isolation regression: with ambient-like competing values present in the process environment
    /// (harmless synthetic decoy paths and a decoy Redis endpoint), the built host must still use
    /// only the owned temporary database and the fixture Redis. The decoy database file must never
    /// be created by the initializer.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task InitializeDatabaseAsync_AmbientLikeCompetingSettings_CannotWinOwnership( ) {
        // Arrange - install synthetic competing "ambient" values, as a developer environment might carry.
        _envRestore = [];
        string[] envKeys = [
            "BridgeBeats__IdentityConnectionString",
            "ConnectionStrings__redis",
            "BridgeBeats__AppleTeamId",
            "BridgeBeats__AppleKeyId",
            "BridgeBeats__AppleKeyPath",
        ];
        foreach (string key in envKeys) {
            _envRestore.Add( (key, Environment.GetEnvironmentVariable( key )) );
        }
        Environment.SetEnvironmentVariable( "BridgeBeats__IdentityConnectionString", $"Data Source={_decoyDbPath}" );
        Environment.SetEnvironmentVariable( "ConnectionStrings__redis", "localhost:1" );
        Environment.SetEnvironmentVariable( "BridgeBeats__AppleTeamId", "DECOYTEAM" );
        Environment.SetEnvironmentVariable( "BridgeBeats__AppleKeyId", "DECOYKEY" );
        Environment.SetEnvironmentVariable( "BridgeBeats__AppleKeyPath", "/nonexistent/decoy.p8" );

        WebApplication app = BuildApp( );
        _app = app;

        // Act — ownership verification BEFORE the first database operation: the decoy values must
        // not have won. This assert is the regression: the host's configuration sources were
        // replaced, so environment values cannot override the composed safe configuration.
        VerifyOwnedDataSource( app );

        _ = await app.InitializeDatabaseAsync( );

        // Assert — only the owned database changed; the decoy file was never created.
        Assert.IsTrue( File.Exists( _identityDbPath ),
            "The owned temporary database must exist after initialization." );
        Assert.IsFalse( File.Exists( _decoyDbPath ),
            "The decoy database named by ambient-like settings must never be created." );

        using IServiceScope scope = app.Services.CreateScope( );
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole> roleManager =
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>( );
        Assert.IsTrue( await roleManager.RoleExistsAsync( Roles.AspireDashboardAccess ),
            "Roles must be seeded in the owned database, not a decoy." );
    }

    /// <summary>
    /// Builds a real <see cref="WebApplication"/> through the production service registration
    /// (<see cref="StartupExtensions.ConfigureBridgeBeatsServices"/>) pointed at the owned SQLite
    /// database and the shared disposable Redis fixture. The Testing environment is established at
    /// builder construction; every default configuration source (appsettings.json, user secrets,
    /// environment variables) is removed and replaced by one explicit in-memory source so no ambient
    /// value can win. The host is built but never started: the test invokes the initializer
    /// directly, mirroring the non-Testing startup branch without binding a port.
    /// </summary>
    private WebApplication BuildApp( ) {
        // Testing environment established at construction, before any configuration source is read.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" } );

        // Remove ALL default sources (appsettings.json, appsettings.Testing.json, user secrets,
        // environment variables, command line) and supply exactly one explicit safe source.
        // ComposeBridgeBeatsServices's ConfigureAppSettings is therefore not used here at all:
        // the lower-level overload binds AppSettings from the configuration passed below.
        builder.Configuration.Sources.Clear( );
        _ = builder.Configuration.AddInMemoryCollection( BuildSafeConfiguration( ) );

        // Facade-equivalent registrations with the safe configuration, per the approved
        // lower-level entry point: genre cache + Redis client + the internal service-graph overload.
        _ = builder.Services.AddGenreCache( );
        builder.AddRedisClient( "redis" );
        _ = builder.WebHost.ConfigureBridgeBeatsServices(
            builder.Services,
            builder.Configuration,
            // Testing environment uses relaxed authentication configuration, mirroring
            // StartupExtensions.RequiresStrictAuthConfiguration.
            failFastOnInvalidAuthConfiguration: false );

        return builder.Build( );
    }

    /// <summary>
    /// Composes the explicit safe configuration: owned temporary SQLite path, the shared disposable
    /// Redis fixture, harmless synthetic provider credentials, and every external integration
    /// disabled. No developer secret and no ambient endpoint is consulted.
    /// </summary>
    private Dictionary<string, string?> BuildSafeConfiguration( ) {
        return new Dictionary<string, string?> {
            ["BridgeBeats:IdentityConnectionString"] = $"Data Source={_identityDbPath}",
            ["ConnectionStrings:redis"] = SharedTestInfrastructure.RedisConnectionString,
            ["Aspire:StackExchange:Redis:ConnectionString"] = SharedTestInfrastructure.RedisConnectionString,
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:Domain"] = "localhost",
            ["BridgeBeats:DiscordToken"] = "",
            ["BridgeBeats:SpotifyClientId"] = "test",
            ["BridgeBeats:SpotifyClientSecret"] = "test",
            ["BridgeBeats:AppleTeamId"] = "",
            ["BridgeBeats:AppleKeyId"] = "",
            ["BridgeBeats:AppleKeyPath"] = "",
            ["BridgeBeats:TidalClientId"] = "",
            ["BridgeBeats:TidalClientSecret"] = "",
            ["BridgeBeats:ATProtoIdentifier"] = "",
            ["BridgeBeats:ATProtoPassword"] = "",
            ["BridgeBeats:ATProtoUserDID"] = "",
            ["BridgeBeats:Workers:UseWorkerServices"] = "false",
        };
    }

    /// <summary>
    /// Verifies, before any database operation, that the effective <see cref="ApplicationDbContext"/>
    /// data source is exactly the owned temporary file and that the effective Redis configuration
    /// points at the shared disposable fixture (which must actually be available). Fails the test
    /// (before any write) on any mismatch.
    /// </summary>
    private void VerifyOwnedDataSource( WebApplication app ) {
        // The Redis fixture must be live: RequireRedis starts/returns the shared Testcontainers
        // instance and throws (failing the test) when Docker is unavailable, so a nonempty
        // connection string alone is never treated as proof of a running fixture.
        SharedTestInfrastructure.RequireRedis( );

        using IServiceScope scope = app.Services.CreateScope( );
        IDbContextFactory<ApplicationDbContext> factory =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( );
        using ApplicationDbContext context = factory.CreateDbContext( );
        string effectiveDataSource = context.Database.GetConnectionString( ) ?? string.Empty;

        Assert.AreEqual( $"Data Source={_identityDbPath}", effectiveDataSource,
            "Initializer tests must operate on the owned temporary SQLite database; " +
            $"effective data source was '{effectiveDataSource}'." );

        string effectiveRedis = app.Configuration["ConnectionStrings:redis"] ?? string.Empty;
        Assert.AreEqual( SharedTestInfrastructure.RedisConnectionString, effectiveRedis,
            "Initializer tests must be configured to use the shared disposable Redis fixture; " +
            $"effective Redis configuration was '{effectiveRedis}'." );
    }
}
