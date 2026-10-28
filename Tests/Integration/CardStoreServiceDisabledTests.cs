using System.Net;
using System.Net.Http.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Services.Cards;
using BridgeBeats.Web.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the <c>POST /api/card/store</c> endpoint when the card service is disabled
/// (domain is empty). Running the real web app in-memory via a custom factory that replaces the card
/// service with a disabled instance, verifies that an authenticated request returns 503 Service
/// Unavailable.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
public class CardStoreServiceDisabledTests : IDisposable {
    /// <summary>The test web application factory hosting the app for this test class.</summary>
    private CardStoreDisabledTestWebApplicationFactory? _factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private HttpClient? _client;
    /// <summary>The internal service key used for authenticated requests.</summary>
    private const string ValidKey = "test-internal-key";

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds the test host with a known internal service key and a replaced card service whose
    /// domain is empty so the service is disabled.
    /// </summary>
    [TestInitialize]
    public async Task Setup( ) {
        // The card service is replaced with a disabled one below, so the host needs no domain and
        // no provider credentials; only the settings that keep it deterministic are supplied.
        Dictionary<string, string?> configData = TestHostConfiguration.Isolated( );

        _factory = new CardStoreDisabledTestWebApplicationFactory( configData, ValidKey );
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
    /// Verifies that an authenticated request returns 503 Service Unavailable when the card service
    /// is disabled (domain is empty).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ValidServiceKey_ServiceDisabled_Returns503( ) {
        // Arrange
        _client!.DefaultRequestHeaders.Add( InternalServiceDefaults.HeaderName, ValidKey );
        MediaLinkResult payload = new() {
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

        // Act
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/card/store",
            payload,
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.ServiceUnavailable, response.StatusCode );
    }

    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> variant that replaces the registered
    /// <see cref="IOpenGraphCardService"/> with a disabled instance (empty domain) and overrides the
    /// <see cref="InternalServiceAuthOptions.ServiceKey"/> via post-configure so that the test key is
    /// accepted by the authentication handler.
    /// </summary>
    /// <param name="configOverrides">Configuration overrides forwarded to the base factory.</param>
    /// <param name="serviceKey">The internal service key the handler should accept.</param>
    private sealed class CardStoreDisabledTestWebApplicationFactory(
        Dictionary<string, string?>? configOverrides,
        string serviceKey
    ) : CustomWebApplicationFactory( configOverrides ) {
        /// <summary>
        /// Extends the base host configuration by replacing the card service singleton with a
        /// disabled instance and injecting the test service key into the auth options.
        /// </summary>
        /// <param name="builder">The web host builder supplied by the test host.</param>
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );

            _ = builder.ConfigureServices( services => {
                // Replace the card service with a disabled instance (empty domain means IsEnabled = false).
                _ = services.RemoveAll<IOpenGraphCardService>( );
                _ = services.AddSingleton<IOpenGraphCardService>(
                    _ => new OpenGraphCardService( string.Empty, 1, 500, 10000 )
                );

                // Inject the test service key so the auth handler accepts requests.
                _ = services.PostConfigure<InternalServiceAuthOptions>(
                    InternalServiceDefaults.AuthenticationScheme,
                    options => { options.ServiceKey = serviceKey; }
                );
            } );
        }
    }
}
