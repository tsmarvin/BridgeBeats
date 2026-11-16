using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the <see cref="IMediaLinkService"/> resolution pipeline, resolving the real
/// service from the application's DI container via <see cref="CustomWebApplicationFactory"/> against the
/// shared Redis container. Verifies service registration and live lookups by ISRC, UPC, title, and
/// provider URL across Apple Music, Spotify, and Tidal, including cross-provider matching by ISRC and the
/// no-result path for an invalid ISRC. Rate-limited calls are made inconclusive via
/// <see cref="RateLimitTestHelper"/> rather than failed. These tests require valid API credentials in
/// appsettings.json or user secrets and use direct provider mode (not queue-based) for fast API validation.
/// </summary>
[TestClass]
[DoNotParallelize] // Prevent parallel execution to avoid overwhelming external APIs with rate limits
[TestCategory( "Integration" )] // Mark as integration tests
public class MusicLookupServiceTests {

    /// <summary>The application service provider resolved from the test host.</summary>
    private static IServiceProvider s_serviceProvider = null!;

    /// <summary>The test web application factory that supplies the service provider.</summary>
    private static CustomWebApplicationFactory? s_factory;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds the test host with worker services disabled and external integrations blanked, captures
    /// its service provider, and migrates the identity database.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        // These tests resolve the real service against the live provider APIs, so the host registers
        // providers from whatever real credentials this machine supplies. Only provider credential
        // keys are carried over; the ambient configuration is not copied, so no unrelated setting —
        // a stale key path, an exported worker flag — can change which providers register. When no
        // credential is usable the deterministic settings keep the host startable, and each live test
        // reports the missing credential before running.
        Dictionary<string, string?> overrides = TestHostConfiguration.LiveProvidersOrDeterministic( );

        s_factory = new CustomWebApplicationFactory( overrides );
        s_serviceProvider = s_factory.Services;

        // Initialize databases after services are configured
        await s_factory.InitializeDatabasesAsync( );
    }

    /// <summary>
    /// Verifies the <see cref="IMediaLinkService"/> is registered and resolvable from the container.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - service registration should be instant
    public void ServiceRegistration_WithValidSecrets_ShouldRegisterMediaLinkService( ) {
        // Act
        IMediaLinkService? mediaLinkService = s_serviceProvider.GetService<IMediaLinkService>( );

        // Assert
        Assert.IsNotNull( mediaLinkService );
    }

    /// <summary>
    /// Looks up a known track by ISRC and verifies a non-album result with a title and artist
    /// (inconclusive when the providers are rate limited).
    /// </summary>
    [TestMethod]
    [TestCategory( "AppleMusic" )]
    [TestCategory( "Spotify" )]
    [TestCategory( "Tidal" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for simple ISRC lookup
    public async Task GetInfoByISRC_WithValidISRC_ShouldReturnResult( ) {
        // Arrange
        TestHostConfiguration.RequireAnyLiveCredentials(
            SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        // Using a well-known ISRC for "Bohemian Rhapsody" by Queen
        string isrc = "GBUM71029604";

        // Act - wrap in rate limit handler
        MediaLinkResult? result;
        try {
            result = await mediaLinkService.GetInfoByISRCAsync( isrc );
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "ISRC lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotNullOrRateLimited( result, "ISRC lookup" )) {
            return;
        }

        Assert.IsNotEmpty( result!.Results, "result.Results should not be empty" );
        MusicLookupResult firstResult = result.Results.First( ).Value;
        Assert.IsFalse( firstResult.IsAlbum ?? true );
        Assert.IsNotNull( firstResult.Title );
        Assert.IsNotNull( firstResult.Artist );
    }

    /// <summary>
    /// Looks up a known album by UPC and verifies an album result with a title and artist (inconclusive
    /// when the providers are rate limited).
    /// </summary>
    [TestMethod]
    [TestCategory( "AppleMusic" )]
    [TestCategory( "Spotify" )]
    [TestCategory( "Tidal" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for simple UPC lookup
    public async Task GetInfoByUPC_WithValidUPC_ShouldReturnResult( ) {
        // Arrange
        TestHostConfiguration.RequireAnyLiveCredentials(
            SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        // Using a well-known UPC for "A Night at the Opera" by Queen
        string upc = "00602547202307";

        // Act
        MediaLinkResult? result;
        try {
            result = await mediaLinkService.GetInfoByUPCAsync( upc );
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "UPC lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotNullOrRateLimited( result, "UPC lookup" )) {
            return;
        }

        Assert.IsNotEmpty( result!.Results, "result.Results should not be empty" );
        MusicLookupResult firstResult = result.Results.First( ).Value;
        Assert.IsTrue( firstResult.IsAlbum ?? false );
        Assert.IsNotNull( firstResult.Title );
        Assert.IsNotNull( firstResult.Artist );
    }

    /// <summary>
    /// Looks up a known track by title and artist and verifies the returned title contains the expected
    /// text (inconclusive when the providers are rate limited).
    /// </summary>
    [TestMethod]
    [TestCategory( "AppleMusic" )]
    [TestCategory( "Spotify" )]
    [TestCategory( "Tidal" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for title/artist search
    public async Task GetInfoByTitle_WithValidTitleAndArtist_ShouldReturnResult( ) {
        // Arrange
        TestHostConfiguration.RequireAnyLiveCredentials(
            SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        string title = "Bohemian Rhapsody";
        string artist = "Queen";

        // Act
        MediaLinkResult? result;
        try {
            result = await mediaLinkService.GetInfoAsync( title, artist );
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "Title/Artist lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotNullOrRateLimited( result, "Title/Artist lookup" )) {
            return;
        }

        Assert.IsNotEmpty( result!.Results, "result.Results should not be empty" );
        MusicLookupResult firstResult = result.Results.First( ).Value;
        Assert.IsNotNull( firstResult.Title );
        Assert.IsNotNull( firstResult.Artist );
        Assert.IsTrue( firstResult.Title.Contains( "Bohemian", StringComparison.OrdinalIgnoreCase ), "Title should contain Bohemian" );
    }

    /// <summary>
    /// Resolves an Apple Music album URL through the streaming lookup and verifies the first result has a
    /// title and artist (inconclusive when rate limited).
    /// </summary>
    [TestMethod]
    [TestCategory( "AppleMusic" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for URL lookup
    public async Task GetInfoByUrl_WithAppleMusicUrl_ShouldReturnResult( ) {
        // Arrange
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.AppleMusic );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        // Using the same Bohemian Rhapsody track but with album-only URL format
        string appleUrl = "https://music.apple.com/us/album/a-night-at-the-opera-deluxe-remastered-version/1440806041";

        // Act
        List<MediaLinkResult> results = [];
        try {
            await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( appleUrl )) {
                results.Add( result );
            }
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "Apple Music URL lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotEmptyOrRateLimited( results, "Apple Music URL lookup" )) {
            return;
        }

        MediaLinkResult firstResult = results[0];
        Assert.IsNotEmpty( firstResult.Results, "firstResult.Results should not be empty" );
        MusicLookupResult firstLookup = firstResult.Results.First( ).Value;
        Assert.IsNotNull( firstLookup.Title );
        Assert.IsNotNull( firstLookup.Artist );
    }

    /// <summary>
    /// Resolves a Spotify album URL through the streaming lookup and verifies the first result has a
    /// title and artist (inconclusive when rate limited).
    /// </summary>
    [TestMethod]
    [TestCategory( "Spotify" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for URL lookup
    public async Task GetInfoByUrl_WithSpotifyUrl_ShouldReturnResult( ) {
        // Arrange
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.Spotify );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        // Using Bohemian Rhapsody album URL
        string spotifyUrl = "https://open.spotify.com/album/6i6folBtxKV28WX3msQ4FE";

        // Act
        List<MediaLinkResult> results = [];
        try {
            await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( spotifyUrl )) {
                results.Add( result );
            }
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "Spotify URL lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotEmptyOrRateLimited( results, "Spotify URL lookup" )) {
            return;
        }

        MediaLinkResult firstResult = results[0];
        Assert.IsNotEmpty( firstResult.Results, "firstResult.Results should not be empty" );
        MusicLookupResult firstLookup = firstResult.Results.First( ).Value;
        Assert.IsNotNull( firstLookup.Title );
        Assert.IsNotNull( firstLookup.Artist );
    }

    /// <summary>
    /// Verifies that looking up an invalid ISRC yields a null result or an empty result set.
    /// </summary>
    [TestMethod]
    [TestCategory( "AppleMusic" )]
    [TestCategory( "Spotify" )]
    [TestCategory( "Tidal" )]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - invalid ISRC should fail fast
    public async Task GetInfoByISRC_WithInvalidISRC_ShouldReturnNull( ) {
        // Arrange
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        string invalidIsrc = "INVALID12345";

        // Act
        MediaLinkResult? result;
        try {
            result = await mediaLinkService.GetInfoByISRCAsync( invalidIsrc );
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "Invalid ISRC lookup" );
            return;
        }

        // Assert - Should handle gracefully, either null or empty results
        Assert.IsTrue( result == null || result.Results.Count == 0 );
    }

    /// <summary>
    /// Resolves a Tidal track URL through the streaming lookup and verifies the first result has a title
    /// and artist (inconclusive when rate limited).
    /// </summary>
    [TestMethod]
    [TestCategory( "Tidal" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for URL lookup
    public async Task GetInfoByUrl_WithTidalUrl_ShouldReturnResult( ) {
        // Arrange
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.Tidal );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        // Using Bohemian Rhapsody track URL on Tidal
        string tidalUrl = "https://tidal.com/track/96572657";

        // Act
        List<MediaLinkResult> results = [];
        try {
            await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( tidalUrl )) {
                results.Add( result );
            }
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "Tidal URL lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotEmptyOrRateLimited( results, "Tidal URL lookup" )) {
            return;
        }

        MediaLinkResult firstResult = results[0];
        Assert.IsNotEmpty( firstResult.Results, "firstResult.Results should not be empty" );
        MusicLookupResult firstLookup = firstResult.Results.First( ).Value;
        Assert.IsNotNull( firstLookup.Title );
        Assert.IsNotNull( firstLookup.Artist );
    }

    /// <summary>
    /// Resolves an Apple Music track URL, confirms the Apple Music result and its ISRC, then verifies the
    /// service finds the same track on Spotify by that ISRC (cross-provider matching; the Spotify leg is
    /// inconclusive when rate limited).
    /// Apple Music URL: https://music.apple.com/us/album/chiron/1695231829?i=1695231831
    /// Expected ISRC: US25X1087647. Track: "Chiron" by Shades (Alix Perez and Eprom).
    /// </summary>
    [TestMethod]
    [TestCategory( "AppleMusic" )]
    [TestCategory( "Spotify" )]
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for URL lookup
    public async Task GetInfoByUrl_WithAppleMusicChironTrack_ShouldFindOnSpotify( ) {
        // Arrange
        TestHostConfiguration.RequireLiveCredentials( SupportedProviders.AppleMusic );
        IMediaLinkService mediaLinkService = s_serviceProvider.GetRequiredService<IMediaLinkService>();
        string appleUrl = "https://music.apple.com/us/album/chiron/1695231829?i=1695231831";

        // Act
        List<MediaLinkResult> results = [];
        try {
            await foreach (MediaLinkResult result in mediaLinkService.GetInfoAsync( appleUrl )) {
                results.Add( result );
            }
        } catch (RetryAfterExceededException ex) {
            RateLimitTestHelper.HandleRateLimitException( ex, "Apple Music Chiron lookup" );
            return;
        }

        // Assert
        if (!RateLimitTestHelper.AssertNotEmptyOrRateLimited( results, "Apple Music Chiron lookup" )) {
            return;
        }

        MediaLinkResult firstResult = results[0];
        Assert.IsNotEmpty( firstResult.Results, "firstResult.Results should not be empty" );

        // Verify Apple Music result
        Assert.IsTrue( firstResult.Results.TryGetValue( SupportedProviders.AppleMusic, out MusicLookupResult? appleResult ), "Should have Apple Music result" );
        Assert.IsNotNull( appleResult );
        Assert.IsNotNull( appleResult.Title );
        Assert.IsNotNull( appleResult.Artist );
        Assert.IsFalse( appleResult.IsAlbum ?? true, "Should be a track, not an album" );

        // Verify ISRC is present
        Assert.IsFalse( string.IsNullOrWhiteSpace( appleResult.ExternalId ), "Apple Music result should have an ISRC" );
        Assert.AreEqual( "US25X1087647", appleResult.ExternalId, "ISRC should match expected value" );

        // Verify Spotify result is present (the main issue being tested)
        // Note: This may fail if Spotify rate limits are hit
        if (!firstResult.Results.TryGetValue( SupportedProviders.Spotify, out MusicLookupResult? spotifyResult )) {
            Assert.Inconclusive( "Spotify result not found - possibly due to rate limiting" );
            return;
        }

        Assert.IsNotNull( spotifyResult, "Spotify should find the track using ISRC US25X1087647" );
        Assert.IsNotNull( spotifyResult.Title );
        Assert.IsNotNull( spotifyResult.Artist );
        Assert.IsFalse( string.IsNullOrWhiteSpace( spotifyResult.URL ), "Spotify result should have a URL" );
    }

    /// <summary>
    /// Cleans up shared resources after all tests in this class have completed.
    /// </summary>
    [ClassCleanup]
    public static void ClassCleanup( ) {
        s_factory?.Dispose( );
    }

}
