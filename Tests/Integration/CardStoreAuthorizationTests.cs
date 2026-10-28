using System.Net;
using System.Net.Http.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Web.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the <c>POST /api/card/store</c> endpoint's authorization behavior, running
/// the real web app in-memory via <see cref="CardStoreTestWebApplicationFactory"/>. Verifies that
/// anonymous callers receive 401, callers presenting a valid internal service key receive 200 with a
/// non-null card URL, and callers presenting an invalid key (wrong value or wrong length) receive 401.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
public class CardStoreAuthorizationTests : IDisposable {
    /// <summary>The test web application factory hosting the app for this test class.</summary>
    private CardStoreTestWebApplicationFactory? _factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private HttpClient? _client;
    /// <summary>The internal service key used by tests that send a valid key.</summary>
    private const string ValidKey = "test-internal-key";

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds the test host with a known internal service key and a non-empty domain so the card
    /// service is enabled.
    /// </summary>
    [TestInitialize]
    public async Task Setup( ) {
        Dictionary<string, string?> configData = TestHostConfiguration.Isolated( );
        configData["BridgeBeats:Domain"] = "bridgebeats.link";

        _factory = new CardStoreTestWebApplicationFactory( configData, ValidKey );
        _client = _factory.CreateClient( );

        await _factory.InitializeDatabasesAsync( );
    }

    /// <summary>
    /// Tears down the test host after each test by delegating to <see cref="Dispose()"/>.
    /// </summary>
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
                Core.Infrastructure.Identity.ApplicationDbContext dbContext =
                    scope.ServiceProvider.GetRequiredService<Core.Infrastructure.Identity.ApplicationDbContext>();
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
    /// Verifies that a request with no service-key header returns 401 Unauthorized.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Anonymous_NoServiceKey_Returns401( ) {
        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync(
            "/api/card/store",
            MakePayload(),
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.Unauthorized, response.StatusCode );
    }

    /// <summary>
    /// Verifies that a request with a valid service key returns 200 OK with a non-null card URL.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ValidServiceKey_WithResult_Returns200( ) {
        // Arrange
        _client!.DefaultRequestHeaders.Add( InternalServiceDefaults.HeaderName, ValidKey );

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/card/store",
            MakePayload(),
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Web.Controllers.CardApiController.StoreCardResponse? body =
            await response.Content.ReadFromJsonAsync<Web.Controllers.CardApiController.StoreCardResponse>(
                TestContext.CancellationToken
            );
        Assert.IsNotNull( body );
        Assert.IsNotNull( body.CardUrl, "CardUrl should be non-null for a valid request." );
    }

    /// <summary>
    /// Verifies that a request presenting an invalid key whose length differs from the valid key
    /// returns 401 Unauthorized.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task InvalidServiceKey_Returns401( ) {
        // Arrange — use a key shorter than ValidKey to exercise the unequal-length path
        _client!.DefaultRequestHeaders.Add( InternalServiceDefaults.HeaderName, "wrong" );

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/card/store",
            MakePayload(),
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.Unauthorized, response.StatusCode );
    }

    /// <summary>
    /// Verifies that a request presenting an invalid key of the same byte-length as the valid key
    /// returns 401 Unauthorized (exercises the fixed-time comparison path where lengths match but
    /// values differ).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WrongKeyEqualLength_Returns401( ) {
        // Arrange — same length as ValidKey ("test-internal-key" = 17 chars), different value
        const string WrongEqualLengthKey = "test-internal-XXX";
        Assert.AreEqual( ValidKey.Length, WrongEqualLengthKey.Length );
        _client!.DefaultRequestHeaders.Add( InternalServiceDefaults.HeaderName, WrongEqualLengthKey );

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/card/store",
            MakePayload(),
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.Unauthorized, response.StatusCode );
    }

    /// <summary>
    /// Verifies that a request with a valid service key but an empty result body returns 400 Bad
    /// Request.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ValidServiceKey_EmptyResult_Returns400( ) {
        // Arrange
        _client!.DefaultRequestHeaders.Add( InternalServiceDefaults.HeaderName, ValidKey );
        MediaLinkResult emptyResult = new() { Results = [] };

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/card/store",
            emptyResult,
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
    }

    /// <summary>
    /// Builds a minimal <see cref="MediaLinkResult"/> suitable for posting to the store endpoint.
    /// </summary>
    private static MediaLinkResult MakePayload( ) {
        return new MediaLinkResult {
            Results = new Dictionary<SupportedProviders, MusicLookupResult> {
                {
                    SupportedProviders.Spotify, new MusicLookupResult {
                        ExternalId = "USRC12345678",
                        IsAlbum = false,
                        Title = "Test Track",
                        Artist = "Test Artist",
                        URL = "https://open.spotify.com/track/test"
                    }
                }
            }
        };
    }

    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> variant that overrides the
    /// <see cref="InternalServiceAuthOptions.ServiceKey"/> via a post-configure service-layer override
    /// so the test key is active regardless of what was bound from configuration at startup.
    /// </summary>
    /// <param name="configOverrides">Configuration overrides forwarded to the base factory.</param>
    /// <param name="serviceKey">The internal service key the handler should accept.</param>
    private sealed class CardStoreTestWebApplicationFactory(
        Dictionary<string, string?>? configOverrides,
        string serviceKey
    ) : CustomWebApplicationFactory( configOverrides ) {
        /// <summary>
        /// Extends the base host configuration by injecting a post-configure that writes the test
        /// service key into <see cref="InternalServiceAuthOptions"/> after the app has registered
        /// its own service configuration.
        /// </summary>
        /// <param name="builder">The web host builder supplied by the test host.</param>
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );

            _ = builder.ConfigureServices( services => {
                _ = services.PostConfigure<InternalServiceAuthOptions>(
                    InternalServiceDefaults.AuthenticationScheme,
                    options => { options.ServiceKey = serviceKey; }
                );
            } );
        }
    }
}
