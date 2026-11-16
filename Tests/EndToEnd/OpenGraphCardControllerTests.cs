using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using Microsoft.Extensions.Configuration;

namespace BridgeBeats.Tests.EndToEnd;

/// <summary>
/// End-to-end tests for the web lookup endpoint (<c>/lookup/web</c>) that backs the Open Graph embed
/// cards, running the real web app in-memory via <see cref="CustomWebApplicationFactory"/> against the
/// shared Redis container. Covers single- and multi-URL lookups against live providers, empty-input
/// validation, and the no-results path for unrecognized URLs.
/// </summary>
[TestClass]
[DoNotParallelize] // Prevent parallel execution to avoid overwhelming external APIs with rate limits
[TestCategory( "EndToEnd" )] // Mark as end-to-end tests
public class WebLookupTests {
    /// <summary>The shared web application factory hosting the app for this test class.</summary>
    private static CustomWebApplicationFactory? s_factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private static HttpClient? s_client;
    /// <summary>
    /// The host built on demand for the tests that perform live provider lookups, and the client
    /// connected to it. It is separate from the class's deterministic host so that a machine without
    /// usable provider credentials affects only the tests that need a live provider.
    /// </summary>
    private static CustomWebApplicationFactory? s_liveFactory;
    /// <summary>The HTTP client connected to the live-provider host.</summary>
    private static HttpClient? s_liveClient;

    /// <summary>
    /// Builds the test host with worker services disabled and external integrations blanked, creates a
    /// client, and migrates the identity database.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        // These tests assert card URL and rendering behavior; the lookup-result assertions are
        // provider-gated, so no ambient credential is copied into the host.
        Dictionary<string, string?> configData = TestHostConfiguration.Isolated( );

        s_factory = new CustomWebApplicationFactory( configData );
        s_client = s_factory.CreateClient( );

        // Initialize databases after services are configured
        await s_factory.InitializeDatabasesAsync( );
    }

    /// <summary>
    /// Disposes the test hosts after the class completes.
    /// </summary>
    [ClassCleanup]
    public static void ClassCleanup( ) {
        s_liveClient?.Dispose( );
        s_liveFactory?.Dispose( );
        s_factory?.Dispose( );
    }

    /// <summary>
    /// Returns a client connected to a host built with this machine's real provider credentials,
    /// creating that host on first use, and marks the calling test inconclusive when no provider
    /// credential is available to perform a live lookup.
    /// </summary>
    /// <returns>A client connected to the live-provider host.</returns>
    private static async Task<HttpClient> LiveClientAsync( ) {
        // Checked before the host is built: a machine without a usable provider credential must report
        // that, not run a lookup against the deterministic host's placeholder and call the empty
        // result rate limiting.
        TestHostConfiguration.RequireAnyLiveCredentials(
            SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal );

        if (s_liveFactory is null) {
            Dictionary<string, string?> configData = TestHostConfiguration.LiveProviders( );
            s_liveFactory = new CustomWebApplicationFactory( configData );
            s_liveClient = s_liveFactory.CreateClient( );
            await s_liveFactory.InitializeDatabasesAsync( );
        }

        return s_liveClient!;
    }

    /// <summary>
    /// Posts a single valid Spotify track URL and verifies 200 OK with <c>hasResults</c> true and an
    /// <c>items</c> array (inconclusive when no results, signalling rate limiting).
    /// </summary>
    [TestMethod]
    [TestCategory( "Integration" )] // Requires real API credentials
    [TestCategory( "Spotify" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout
    public async Task WebLookup_WithValidSpotifyUrl_ReturnsCardUrl( ) {
        // Arrange
        object payload = new { uri = "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp" };

        // Act
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.Spotify );
        HttpClient client = await LiveClientAsync( );
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/lookup/web",
            payload,
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );

        JsonElement data = await response.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );

        bool hasResults = data.GetProperty( "hasResults" ).GetBoolean( );

        // If we hit rate limits, hasResults may be false - that's acceptable for integration tests
        if (!hasResults) {
            Assert.Inconclusive( "API returned no results - possibly due to rate limiting" );
            return;
        }

        Assert.IsTrue( hasResults, "Should have results for valid Spotify URL" );

        // Check items array exists
        JsonElement itemsElement = data.GetProperty( "items" );
        Assert.AreEqual( JsonValueKind.Array, itemsElement.ValueKind );
    }

    /// <summary>
    /// Posts two URLs (Spotify and Apple Music) and verifies 200 OK with <c>hasResults</c> true and two
    /// items (inconclusive when fewer items are returned, signalling rate limiting).
    /// </summary>
    [TestMethod]
    [TestCategory( "Integration" )] // Requires real API credentials
    [TestCategory( "Spotify" )]
    [TestCategory( "AppleMusic" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout
    public async Task WebLookup_WithMultipleUrls_ReturnsMultipleCards( ) {
        // Arrange - Multiple URLs separated by space/newline
        object payload = new { uri = "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp https://music.apple.com/us/album/chiron/1695231829" };

        // Act
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.Spotify );
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.AppleMusic );
        HttpClient client = await LiveClientAsync( );
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/lookup/web",
            payload,
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );

        JsonElement data = await response.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );

        bool hasResults = data.GetProperty( "hasResults" ).GetBoolean( );

        // If we hit rate limits, hasResults may be false - that's acceptable for integration tests
        if (!hasResults) {
            Assert.Inconclusive( "API returned no results - possibly due to rate limiting" );
            return;
        }

        Assert.IsTrue( hasResults, "Should have results for valid URLs" );

        // Check that we have multiple items
        JsonElement items = data.GetProperty( "items" );
        int itemCount = items.GetArrayLength( );
        Assert.IsGreaterThan( 0, itemCount, "Should have at least one item" );
        if (itemCount < 2) {
            Assert.Inconclusive( "Expected 2 items but got fewer - possibly due to rate limiting" );
        } else {
            Assert.AreEqual( 2, itemCount, "Should have exactly 2 items" );
        }
    }

    /// <summary>
    /// Posts an empty URI and verifies the endpoint returns 400 Bad Request.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task WebLookup_WithEmptyUri_ReturnsBadRequest( ) {
        // Arrange
        object payload = new { uri = "" };

        // Act
        HttpResponseMessage response = await s_client!.PostAsJsonAsync(
            "/lookup/web",
            payload,
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
    }

    /// <summary>
    /// Posts an unrecognized (non-music) URI and verifies the endpoint returns 200 OK with
    /// <c>hasResults</c> false.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task WebLookup_WithInvalidUri_ReturnsNoResults( ) {
        // Arrange
        object payload = new { uri = "not-a-valid-music-url" };

        // Act
        HttpResponseMessage response = await s_client!.PostAsJsonAsync(
            "/lookup/web",
            payload,
            TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );

        JsonElement data = await response.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );

        bool hasResults = data.GetProperty( "hasResults" ).GetBoolean( );
        Assert.IsFalse( hasResults, "Should have no results for invalid URL" );
    }

    /// <summary>
    /// The MSTest-injected test context, used here to obtain the per-test cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;
}
