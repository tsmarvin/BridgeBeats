using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the Aspire dashboard authorization endpoint (<c>/api/dashboard/authorize</c>),
/// running the real web app in-memory via a custom factory that installs a stub authentication scheme.
/// Verifies the endpoint returns 401 for unauthenticated callers, 403 for authenticated users lacking
/// the dashboard-access role, and 200 for users holding it, and that database initialization seeds the
/// role.
/// </summary>
[TestClass]
[DoNotParallelize] // Uses the process-wide Redis statistics keys also exercised by StatisticsStatusIntegrationTests.
public class DashboardAuthorizationTests : IDisposable {
    /// <summary>The test web application factory with the stub authentication scheme installed.</summary>
    private DashboardTestWebApplicationFactory? _factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private HttpClient? _client;
    /// <summary>The email of the test user created for each test.</summary>
    private const string TestUserEmail = "dashboardtest@example.com";
    /// <summary>The password used when creating the test user.</summary>
    private const string TestUserPassword = "TestPassword123!";

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds the test host with worker services disabled and external integrations blanked, creates a
    /// client, and migrates the identity database before each test.
    /// </summary>
    [TestInitialize]
    public async Task Setup( ) {
        // Load configuration from appsettings.json and user secrets
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile( Path.Combine( "src", "BridgeBeats.Web", "appsettings.json" ), optional: true )
            .AddUserSecrets<Web.Program>( optional: true )
            .AddEnvironmentVariables()
            .Build();

        // Build config overrides from loaded configuration
        Dictionary<string, string?> configData = configuration
            .AsEnumerable()
            .Where( kv => kv.Value is not null )
            .Where( kv => !kv.Key.EndsWith( "ConnectionString", StringComparison.OrdinalIgnoreCase ) )
            .ToDictionary( );

        // Force Discord token to null to prevent Discord service registration
        configData["BridgeBeats:DiscordToken"] = string.Empty;
        // Disable worker services mode - use direct provider implementations
        configData["BridgeBeats:Workers:UseWorkerServices"] = "false";
        // Force ATProto credentials to empty to disable caching service
        configData["BridgeBeats:ATProtoIdentifier"] = string.Empty;
        configData["BridgeBeats:ATProtoPassword"] = string.Empty;
        configData["BridgeBeats:ATProtoUserDID"] = string.Empty;

        _factory = new DashboardTestWebApplicationFactory( configData );
        _client = _factory.CreateClient( );

        // Initialize databases after services are configured
        await _factory.InitializeDatabasesAsync( );
    }

    /// <summary>
    /// Tears down the test host after each test by delegating to <see cref="Dispose()"/>.
    /// </summary>
    [TestCleanup]
    public void Cleanup( ) {
        Dispose( );
    }

    /// <summary>
    /// Disposes the client and factory, deleting the identity database created for the test.
    /// </summary>
    public void Dispose( ) {
        _client?.Dispose( );
        _client = null;

        // Clean up the in-memory database
        // Note: We need to clean up before disposing the factory
        if (_factory != null) {
            try {
                using IServiceScope scope = _factory.Services.CreateScope( );
                ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
                _ = dbContext.Database.EnsureDeleted( );
            } catch (ObjectDisposedException) {
                // Factory was already disposed, nothing to clean up
            }
            _factory.Dispose( );
            _factory = null;
        }

        GC.SuppressFinalize( this );
    }

    /// <summary>
    /// Verifies an unauthenticated request to the authorize endpoint returns 401 Unauthorized.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DashboardAuthorize_UnauthenticatedUser_Returns401( ) {
        // Arrange
        using HttpClient client = _factory!.CreateClient( );

        // Act
        HttpResponseMessage response = await client.GetAsync( "/api/dashboard/authorize", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.Unauthorized, response.StatusCode );
    }

    /// <summary>
    /// Verifies an authenticated user lacking the dashboard-access role receives 403 Forbidden.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DashboardAuthorize_UserWithoutRole_Returns403( ) {
        // Arrange
        await CreateTestUserAsync( hasRole: false );
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "TestScheme" );

        // Act
        HttpResponseMessage response = await _client.GetAsync( "/api/dashboard/authorize", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.Forbidden, response.StatusCode );
    }

    /// <summary>
    /// Verifies an authenticated user holding the dashboard-access role receives 200 OK.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DashboardAuthorize_UserWithRole_Returns200( ) {
        // Arrange
        await CreateTestUserAsync( hasRole: true );
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "TestScheme" );

        // Act
        HttpResponseMessage response = await _client.GetAsync( "/api/dashboard/authorize", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
    }

    /// <summary>Low-role statistics responses omit the bootstrap panel and sentinel.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Statistics_LowRole_OmitsBootstrapPanelAndSentinel( ) {
        await SeedStatisticsAsync( );
        await CreateTestUserAsync( hasRole: false );
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "TestScheme" );
        HttpResponseMessage response = await _client.GetAsync( "/Statistics", TestContext.CancellationToken );
        string body = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.IsFalse( body.Contains( "Cache Bootstrap Status", StringComparison.Ordinal ) );
        Assert.IsFalse( body.Contains( "987,654,321", StringComparison.Ordinal ) );
    }

    /// <summary>Dashboard-role statistics responses render the bootstrap panel and sentinel.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Statistics_Admin_RendersBootstrapPanelAndSentinel( ) {
        await SeedStatisticsAsync( );
        await CreateTestUserAsync( hasRole: true );
        _client!.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue( "TestScheme" );
        HttpResponseMessage response = await _client.GetAsync( "/Statistics", TestContext.CancellationToken );
        string body = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        StringAssert.Contains( body, "Cache Bootstrap Status" );
        StringAssert.Contains( body, "987,654,321" );
    }

    /// <summary>Anonymous statistics requests are challenged without rendering page content.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Statistics_Anonymous_IsChallengedWithoutStatsBody( ) {
        await SeedStatisticsAsync( );
        using HttpClient anonymous = _factory!.CreateClient( new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions {
            AllowAutoRedirect = false
        } );
        HttpResponseMessage response = await anonymous.GetAsync( "/Statistics", TestContext.CancellationToken );
        string body = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Unauthorized, response.StatusCode );
        Assert.IsFalse( body.Contains( "Total Records", StringComparison.Ordinal ) );
    }

    private async Task SeedStatisticsAsync( ) {
        using IServiceScope scope = _factory!.Services.CreateScope( );
        IConnectionMultiplexer redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>( );
        IDatabase db = redis.GetDatabase( );
        _ = await db.StringSetAsync( StatisticsStatus.RedisKey, JsonSerializer.Serialize( new StatisticsStatus {
            Snapshot = new LookupStatistics { TotalRecords = 1, GeneratedAt = DateTimeOffset.UtcNow }
        } ) );
        _ = await db.StringSetAsync( CacheBootstrapStatus.RedisKey, JsonSerializer.Serialize( new CacheBootstrapStatus {
            LastSuccessCount = 987654321
        } ) );
    }

    /// <summary>
    /// Verifies that database initialization seeds the Aspire dashboard-access role.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DatabaseInitialization_CreatesAspireDashboardAccessRole( ) {
        // Arrange
        using IServiceScope scope = _factory!.Services.CreateScope( );
        RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>( );

        // Act
        bool roleExists = await roleManager.RoleExistsAsync( Roles.AspireDashboardAccess );

        // Assert
        Assert.IsTrue( roleExists );
    }

    /// <summary>
    /// Creates the test user (seeding the dashboard-access role first), optionally assigns the role, and
    /// registers the user's identity with the stub authentication handler.
    /// </summary>
    /// <param name="hasRole">Whether to grant the user the dashboard-access role.</param>
    private async Task CreateTestUserAsync( bool hasRole ) {
        using IServiceScope scope = _factory!.Services.CreateScope( );
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
        RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>( );

        // Create role if it doesn't exist
        if (!await roleManager.RoleExistsAsync( Roles.AspireDashboardAccess )) {
            _ = await roleManager.CreateAsync( new IdentityRole( Roles.AspireDashboardAccess ) );
        }

        // Create test user
        ApplicationUser user = new( ) {
            UserName = TestUserEmail,
            Email = TestUserEmail,
            EmailConfirmed = true
        };

        IdentityResult result = await userManager.CreateAsync( user, TestUserPassword );
        if (!result.Succeeded) {
            throw new InvalidOperationException( "Failed to create test user: " + string.Join( ", ", result.Errors.Select( e => e.Description ) ) );
        }

        // Assign role if requested
        if (hasRole) {
            _ = await userManager.AddToRoleAsync( user, Roles.AspireDashboardAccess );
        }

        // Store user info in the factory for the test auth handler
        _factory.SetTestUser( user.Id, TestUserEmail, hasRole );
    }

    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> variant that installs a stub <c>TestScheme</c>
    /// authentication handler so tests can present a configured identity (with or without the
    /// dashboard-access role) without going through a real login flow.
    /// </summary>
    /// <param name="configOverrides">Configuration overrides forwarded to the base factory.</param>
    private sealed class DashboardTestWebApplicationFactory(
        Dictionary<string, string?>? configOverrides
    ) : CustomWebApplicationFactory( configOverrides ) {
        /// <summary>The identity id presented by the stub authentication handler.</summary>
        private string? _testUserId;
        /// <summary>The email presented by the stub authentication handler.</summary>
        private string? _testUserEmail;
        /// <summary>Whether the stub identity carries the dashboard-access role.</summary>
        private bool _testUserHasRole;

        /// <summary>
        /// Configures the identity the stub authentication handler will present on the next request.
        /// </summary>
        /// <param name="userId">The user id to put on the principal.</param>
        /// <param name="email">The email to put on the principal.</param>
        /// <param name="hasRole">Whether to add the dashboard-access role claim.</param>
        public void SetTestUser( string userId, string email, bool hasRole ) {
            _testUserId = userId;
            _testUserEmail = email;
            _testUserHasRole = hasRole;
        }

        /// <summary>
        /// Extends the base host configuration by registering the <c>TestScheme</c> authentication
        /// handler and exposing this factory to it for identity lookup.
        /// </summary>
        /// <param name="builder">The web host builder supplied by the test host.</param>
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );

            _ = builder.ConfigureServices( services => {
                // Add test authentication scheme
                _ = services.AddAuthentication( options => {
                    options.DefaultAuthenticateScheme = "TestScheme";
                    options.DefaultChallengeScheme = "TestScheme";
                } )
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>( "TestScheme", _ => { } );

                // Register this factory as a singleton so the handler can access it
                _ = services.AddSingleton( this );
                _ = services.AddSingleton<IStatisticsService, BridgeBeats.Web.Services.RedisStatisticsReader>( );
            } );
        }

        /// <summary>
        /// Stub authentication handler for the <c>TestScheme</c>. Returns no result when no
        /// Authorization header is present, fails when no test user is configured, and otherwise
        /// authenticates a principal built from the factory's configured identity and role.
        /// </summary>
        /// <param name="options">The authentication scheme options monitor.</param>
        /// <param name="logger">The logger factory.</param>
        /// <param name="encoder">The URL encoder.</param>
        /// <param name="factory">The owning factory holding the configured test identity.</param>
        private sealed class TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            DashboardTestWebApplicationFactory factory
        ) : AuthenticationHandler<AuthenticationSchemeOptions>( options, logger, encoder ) {
            /// <summary>
            /// Builds and returns the authentication result for the current request based on the
            /// factory's configured test identity.
            /// </summary>
            protected override Task<AuthenticateResult> HandleAuthenticateAsync( ) {
                // Only authenticate if Authorization header is present
                if (!Request.Headers.ContainsKey( "Authorization" )) {
                    return Task.FromResult( AuthenticateResult.NoResult( ) );
                }

                // Check if test user has been set
                if (string.IsNullOrEmpty( factory._testUserId ) || string.IsNullOrEmpty( factory._testUserEmail )) {
                    return Task.FromResult( AuthenticateResult.Fail( "No test user configured" ) );
                }

                List<Claim> claims = [
                    new Claim( ClaimTypes.NameIdentifier, factory._testUserId ),
                    new Claim( ClaimTypes.Name, factory._testUserEmail ),
                    new Claim( ClaimTypes.Email, factory._testUserEmail )
                ];

                // Add role claim if the test user has the role
                if (factory._testUserHasRole) {
                    claims.Add( new Claim( ClaimTypes.Role, Roles.AspireDashboardAccess ) );
                }

                ClaimsIdentity identity = new( claims, "TestScheme" );
                ClaimsPrincipal principal = new( identity );
                AuthenticationTicket ticket = new( principal, "TestScheme" );

                return Task.FromResult( AuthenticateResult.Success( ticket ) );
            }
        }
    }
}
