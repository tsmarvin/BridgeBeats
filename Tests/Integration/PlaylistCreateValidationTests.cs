using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the <c>POST playlist/create</c> endpoint's model-state guard, running
/// the real web app in-memory via <see cref="PlaylistCreateTestWebApplicationFactory"/>.
/// Verifies that the <c>[StringLength]</c> annotation on
/// <see cref="PlaylistController.CreatePlaylistRequest.Title"/> is enforced at the controller
/// layer (not only via <c>TryValidateObject</c>), returning HTTP 400 and never reaching the
/// playlist-service persistence layer.
/// </summary>
/// <remarks>
/// Failure-first evidence context: before the <c>ModelState.IsValid</c> guard was added to
/// <c>CreatePlaylist</c>, an over-length title bypassed the annotation and reached the DB layer,
/// where <c>HasMaxLength(200)</c> caused a database exception and a HTTP 500. After the guard is
/// added, <c>ModelState.IsValid</c> is false and the action returns 400 before the playlist
/// service is called.
/// </remarks>
[TestClass]
[TestCategory( "Integration" )]
public class PlaylistCreateValidationTests : IDisposable {

    /// <summary>The test web application factory hosting the app for this test class.</summary>
    private PlaylistCreateTestWebApplicationFactory? _factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private HttpClient? _client;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds the test host with stub authentication installed, creates a client, and migrates
    /// the identity database before each test.
    /// </summary>
    [TestInitialize]
    public async Task Setup( ) {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile( Path.Combine( "src", "BridgeBeats.Web", "appsettings.json" ), optional: true )
            .AddUserSecrets<Web.Program>( optional: true )
            .AddEnvironmentVariables()
            .Build();

        Dictionary<string, string?> configData = configuration
            .AsEnumerable()
            .Where( kv => kv.Value is not null )
            .Where( kv => !kv.Key.EndsWith( "ConnectionString", StringComparison.OrdinalIgnoreCase ) )
            .ToDictionary();

        configData["BridgeBeats:DiscordToken"] = string.Empty;
        configData["BridgeBeats:Workers:UseWorkerServices"] = "false";
        configData["BridgeBeats:ATProtoIdentifier"] = string.Empty;
        configData["BridgeBeats:ATProtoPassword"] = string.Empty;
        configData["BridgeBeats:ATProtoUserDID"] = string.Empty;
        configData["BridgeBeats:Domain"] = "bridgebeats.link";

        _factory = new PlaylistCreateTestWebApplicationFactory( configData );
        _factory.SetTestUser( "test-user-id", "playlistTest@example.com" );
        _client = _factory.CreateClient( );
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue( "TestScheme" );

        await _factory.InitializeDatabasesAsync( );
    }

    /// <summary>Tears down the test host after each test.</summary>
    [TestCleanup]
    public void Cleanup( ) {
        Dispose( );
    }

    /// <inheritdoc/>
    public void Dispose( ) {
        _client?.Dispose( );
        _client = null;

        if (_factory != null) {
            try {
                using IServiceScope scope = _factory.Services.CreateScope();
                ApplicationDbContext dbContext =
                    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                _ = dbContext.Database.EnsureDeleted( );
            } catch (ObjectDisposedException) {
                // Factory already disposed.
            }
            _factory.Dispose( );
            _factory = null;
        }

        GC.SuppressFinalize( this );
    }

    /// <summary>
    /// Verifies that an authenticated POST to <c>playlist/create</c> with a 201-character
    /// <c>Title</c> returns HTTP 400, not 500, confirming the <c>ModelState.IsValid</c> guard
    /// fires before the playlist service is invoked, and that no playlist is persisted.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before the <c>ModelState.IsValid</c> guard was added to
    /// <c>CreatePlaylist</c>, this test returned HTTP 500 (the over-length title bypassed
    /// annotation validation and reached the EF <c>HasMaxLength(200)</c> constraint, throwing a
    /// database exception). After the guard is added, <c>ModelState.IsValid</c> is false and
    /// the action returns HTTP 400 before any persistence occurs.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CreatePlaylist_OverLengthTitle_Returns400AndPlaylistIsNotPersisted( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        var request = new {
            title = new string( 'a', 201 ),
            description = (string?)null,
            cardIds = new[] { "card1" },
            cardRkeys = new[] { "track:isrc1" }
        };

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "playlist/create",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert — ModelState guard returns 400 before reaching the service or the DB
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode,
            "A 201-character title must trigger ModelState validation and return 400, not 500." );

        // Confirm no playlist was persisted: the Playlists table must be empty because the
        // model-state rejection short-circuits before CreatePlaylistAsync is ever called.
        using IServiceScope scope = _factory!.Services.CreateScope();
        ApplicationDbContext db =
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        int playlistCount = db.Playlists.Count();
        Assert.AreEqual( 0, playlistCount,
            "No playlist must be stored when model validation rejects the request." );
    }

    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> variant that installs a stub <c>TestScheme</c>
    /// authentication handler so these tests can present an authenticated principal without a real
    /// login flow.
    /// </summary>
    /// <param name="configOverrides">Configuration overrides forwarded to the base factory.</param>
    private sealed class PlaylistCreateTestWebApplicationFactory(
        Dictionary<string, string?>? configOverrides
    ) : CustomWebApplicationFactory( configOverrides ) {

        /// <summary>The identity id presented by the stub authentication handler.</summary>
        private string? _testUserId;
        /// <summary>The email presented by the stub authentication handler.</summary>
        private string? _testUserEmail;

        /// <summary>
        /// Configures the stub authentication principal the handler will present on each request.
        /// </summary>
        /// <param name="userId">The user id to place on the principal's NameIdentifier claim.</param>
        /// <param name="email">The email to place on the principal's Name and Email claims.</param>
        public void SetTestUser( string userId, string email ) {
            _testUserId = userId;
            _testUserEmail = email;
        }

        /// <summary>
        /// Extends the base host configuration by registering the <c>TestScheme</c> authentication
        /// handler and exposing this factory to it for principal construction.
        /// </summary>
        /// <param name="builder">The web host builder supplied by the test host.</param>
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );

            _ = builder.ConfigureServices( services => {
                _ = services.AddAuthentication( options => {
                    options.DefaultAuthenticateScheme = "TestScheme";
                    options.DefaultChallengeScheme = "TestScheme";
                } )
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>( "TestScheme", _ => { } );

                _ = services.AddSingleton( this );
            } );
        }

        /// <summary>
        /// Stub authentication handler for the <c>TestScheme</c>. Returns an authenticated
        /// principal when an Authorization header is present; otherwise returns no result.
        /// </summary>
        private sealed class TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            PlaylistCreateTestWebApplicationFactory factory
        ) : AuthenticationHandler<AuthenticationSchemeOptions>( options, logger, encoder ) {

            /// <summary>
            /// Returns an authenticated principal when the Authorization header is present.
            /// </summary>
            protected override Task<AuthenticateResult> HandleAuthenticateAsync( ) {
                if (!Request.Headers.ContainsKey( "Authorization" )) {
                    return Task.FromResult( AuthenticateResult.NoResult( ) );
                }

                if (string.IsNullOrEmpty( factory._testUserId ) || string.IsNullOrEmpty( factory._testUserEmail )) {
                    return Task.FromResult( AuthenticateResult.Fail( "No test user configured" ) );
                }

                List<Claim> claims = [
                    new Claim( ClaimTypes.NameIdentifier, factory._testUserId ),
                    new Claim( ClaimTypes.Name, factory._testUserEmail ),
                    new Claim( ClaimTypes.Email, factory._testUserEmail ),
                ];

                ClaimsIdentity identity = new( claims, "TestScheme" );
                ClaimsPrincipal principal = new( identity );
                AuthenticationTicket ticket = new( principal, "TestScheme" );

                return Task.FromResult( AuthenticateResult.Success( ticket ) );
            }
        }
    }
}
