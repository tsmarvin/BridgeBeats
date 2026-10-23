using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.LinkResolver;
using Microsoft.Extensions.Logging;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="CachingMediaLinkService"/>, which fronts <see cref="ILookupOrchestrator"/> and
/// translates each orchestrator <see cref="LookupResult"/> into a <see cref="MediaLinkResult"/>,
/// attaching user-facing messages when a result is partial.
/// </summary>
/// <remarks>
/// The tests verify constructor argument validation, that each lookup entry point (metadata, ISRC,
/// UPC, provider id, and the streaming content lookup) delegates to the matching orchestrator method
/// and surfaces null when the orchestrator has no result, and the partial-result messaging contract:
/// rate-limited providers each produce a "temporarily unavailable" message, a partial result with no
/// rate-limited providers produces a "still being fetched" message, a non-partial result adds no
/// messages, and the returned DTO is flagged partial for downstream consumers.
/// </remarks>
[TestClass]
public class CachingMediaLinkServiceTests {
    /// <summary>Mock orchestrator the service delegates every lookup to.</summary>
    private Mock<ILookupOrchestrator> _orchestratorMock = null!;

    /// <summary>Mock logger injected into the service.</summary>
    private Mock<ILogger<CachingMediaLinkService>> _loggerMock = null!;

    /// <summary>The service under test, rebuilt before each test.</summary>
    private CachingMediaLinkService _service = null!;

    /// <summary>Sample ISRC used in ISRC-lookup tests.</summary>
    private const string TestIsrc = "USRC17607839";

    /// <summary>Sample UPC used in UPC-lookup tests.</summary>
    private const string TestUpc = "012345678901";

    /// <summary>Sample track title used in metadata tests.</summary>
    private const string TestTitle = "Test Song";

    /// <summary>Sample artist name used in metadata tests.</summary>
    private const string TestArtist = "Test Artist";

    /// <summary>Sample provider-native id used in provider-id tests.</summary>
    private const string TestProviderId = "spotify-123";

    /// <summary>Sample free-text content (a Spotify URL) used in content-lookup tests.</summary>
    private const string TestContent = "https://open.spotify.com/track/abc123";

    /// <summary>Constructs fresh mocks and a new service instance before each test.</summary>
    [TestInitialize]
    public void Initialize( ) {
        _orchestratorMock = new Mock<ILookupOrchestrator>( );
        _loggerMock = new Mock<ILogger<CachingMediaLinkService>>( );
        _service = new CachingMediaLinkService( _orchestratorMock.Object, _loggerMock.Object );
    }

    #region Constructor Tests

    /// <summary>Verifies that the service constructs successfully with valid dependencies.</summary>
    [TestMethod]
    public void Constructor_WithValidDependencies_ShouldCreateInstance( ) {
        // Act
        CachingMediaLinkService service = new( _orchestratorMock.Object, _loggerMock.Object );

        // Assert
        Assert.IsNotNull( service );
    }

    /// <summary>Verifies that a null orchestrator argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullOrchestrator_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new CachingMediaLinkService( null!, _loggerMock.Object )
        );
    }

    /// <summary>Verifies that a null logger argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new CachingMediaLinkService( _orchestratorMock.Object, null! )
        );
    }

    #endregion

    #region GetInfoAsync (Metadata) Tests

    /// <summary>
    /// Verifies that the metadata lookup delegates to
    /// <see cref="ILookupOrchestrator.LookupByMetadataAsync"/> with the title and artist and returns the
    /// translated result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoAsync_WithMetadata_ShouldDelegateToOrchestrator( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _orchestratorMock
            .Setup( o => o.LookupByMetadataAsync( TestTitle, TestArtist ) )
            .ReturnsAsync( new LookupResult { Result = expectedResult } );

        // Act
        MediaLinkResult? result = await _service.GetInfoAsync( TestTitle, TestArtist );

        // Assert
        Assert.IsNotNull( result );
        _orchestratorMock.Verify( o => o.LookupByMetadataAsync( TestTitle, TestArtist ), Times.Once );
    }

    /// <summary>
    /// Verifies that the metadata lookup returns null when the orchestrator's
    /// <see cref="LookupResult"/> carries no result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoAsync_WhenOrchestratorReturnsNull_ShouldReturnNull( ) {
        // Arrange
        _ = _orchestratorMock
            .Setup( o => o.LookupByMetadataAsync( It.IsAny<string>( ), It.IsAny<string>( ) ) )
            .ReturnsAsync( new LookupResult { Result = null } );

        // Act
        MediaLinkResult? result = await _service.GetInfoAsync( TestTitle, TestArtist );

        // Assert
        Assert.IsNull( result );
    }

    #endregion

    #region GetInfoByISRCAsync Tests

    /// <summary>
    /// Verifies that the ISRC lookup delegates to
    /// <see cref="ILookupOrchestrator.LookupByIsrcAsync"/> and returns the translated result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_ShouldDelegateToOrchestrator( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( TestIsrc ) )
            .ReturnsAsync( new LookupResult { Result = expectedResult } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result );
        _orchestratorMock.Verify( o => o.LookupByIsrcAsync( TestIsrc ), Times.Once );
    }

    /// <summary>
    /// Verifies that the ISRC lookup returns null when the orchestrator's result is empty.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenOrchestratorReturnsNull_ShouldReturnNull( ) {
        // Arrange
        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( new LookupResult { Result = null } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        // Assert
        Assert.IsNull( result );
    }

    #endregion

    #region GetInfoByUPCAsync Tests

    /// <summary>
    /// Verifies that the UPC lookup delegates to
    /// <see cref="ILookupOrchestrator.LookupByUpcAsync"/> and returns the translated result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByUPCAsync_ShouldDelegateToOrchestrator( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _orchestratorMock
            .Setup( o => o.LookupByUpcAsync( TestUpc ) )
            .ReturnsAsync( new LookupResult { Result = expectedResult } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByUPCAsync( TestUpc );

        // Assert
        Assert.IsNotNull( result );
        _orchestratorMock.Verify( o => o.LookupByUpcAsync( TestUpc ), Times.Once );
    }

    /// <summary>
    /// Verifies that the UPC lookup returns null when the orchestrator's result is empty.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByUPCAsync_WhenOrchestratorReturnsNull_ShouldReturnNull( ) {
        // Arrange
        _ = _orchestratorMock
            .Setup( o => o.LookupByUpcAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( new LookupResult { Result = null } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByUPCAsync( TestUpc );

        // Assert
        Assert.IsNull( result );
    }

    #endregion

    #region GetInfoByProviderIdAsync Tests

    /// <summary>
    /// Verifies that a track provider-id lookup delegates to
    /// <see cref="ILookupOrchestrator.LookupByProviderIdAsync"/> with <c>isAlbum = false</c> and returns
    /// the translated result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByProviderIdAsync_ForTrack_ShouldDelegateToOrchestrator( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _orchestratorMock
            .Setup( o => o.LookupByProviderIdAsync( TestProviderId, SupportedProviders.Spotify, false ) )
            .ReturnsAsync( new LookupResult { Result = expectedResult } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByProviderIdAsync( TestProviderId, SupportedProviders.Spotify, false );

        // Assert
        Assert.IsNotNull( result );
        _orchestratorMock.Verify(
            o => o.LookupByProviderIdAsync( TestProviderId, SupportedProviders.Spotify, false ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies that an album provider-id lookup delegates to
    /// <see cref="ILookupOrchestrator.LookupByProviderIdAsync"/> with <c>isAlbum = true</c> and returns
    /// the translated result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByProviderIdAsync_ForAlbum_ShouldDelegateToOrchestrator( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _orchestratorMock
            .Setup( o => o.LookupByProviderIdAsync( TestProviderId, SupportedProviders.AppleMusic, true ) )
            .ReturnsAsync( new LookupResult { Result = expectedResult } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByProviderIdAsync( TestProviderId, SupportedProviders.AppleMusic, true );

        // Assert
        Assert.IsNotNull( result );
        _orchestratorMock.Verify(
            o => o.LookupByProviderIdAsync( TestProviderId, SupportedProviders.AppleMusic, true ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies that a provider-id lookup returns null when the orchestrator's result is empty.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByProviderIdAsync_WhenOrchestratorReturnsNull_ShouldReturnNull( ) {
        // Arrange
        _ = _orchestratorMock
            .Setup( o => o.LookupByProviderIdAsync( It.IsAny<string>( ), It.IsAny<SupportedProviders>( ), It.IsAny<bool>( ) ) )
            .ReturnsAsync( new LookupResult { Result = null } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByProviderIdAsync( TestProviderId, SupportedProviders.Spotify, false );

        // Assert
        Assert.IsNull( result );
    }

    #endregion

    #region GetInfoAsync (Content) Tests

    /// <summary>
    /// Verifies that the streaming content lookup delegates to
    /// <see cref="ILookupOrchestrator.LookupByContentAsync"/> and yields the translated result for each
    /// orchestrator result.
    /// </summary>
    [TestMethod]
    public async Task GetInfoAsync_WithContent_ShouldDelegateToOrchestrator( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _orchestratorMock
            .Setup( o => o.LookupByContentAsync( TestContent ) )
            .Returns( CreateAsyncEnumerable( new LookupResult { Result = expectedResult } ) );

        // Act
        List<MediaLinkResult> results = [];
        await foreach (MediaLinkResult result in _service.GetInfoAsync( TestContent )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 1, results );
        Assert.IsNotNull( results[0] );
        _orchestratorMock.Verify( o => o.LookupByContentAsync( TestContent ), Times.Once );
    }

    /// <summary>
    /// Verifies that the streaming content lookup skips orchestrator results that carry no value,
    /// yielding only the non-null translated results.
    /// </summary>
    [TestMethod]
    public async Task GetInfoAsync_WithContentReturningNull_ShouldSkipNullResults( ) {
        // Arrange
        _ = _orchestratorMock
            .Setup( o => o.LookupByContentAsync( TestContent ) )
            .Returns( CreateAsyncEnumerable(
                new LookupResult { Result = null },
                new LookupResult { Result = CreateMediaLinkResult( ) }
            ) );

        // Act
        List<MediaLinkResult> results = [];
        await foreach (MediaLinkResult result in _service.GetInfoAsync( TestContent )) {
            results.Add( result );
        }

        // Assert - should only have the non-null result
        Assert.HasCount( 1, results );
    }

    /// <summary>
    /// Verifies that the streaming content lookup yields nothing when the orchestrator produces no
    /// results.
    /// </summary>
    [TestMethod]
    public async Task GetInfoAsync_WithNoResults_ShouldReturnEmpty( ) {
        // Arrange
        _ = _orchestratorMock
            .Setup( o => o.LookupByContentAsync( It.IsAny<string>( ) ) )
            .Returns( CreateAsyncEnumerable<LookupResult>( ) );

        // Act
        List<MediaLinkResult> results = [];
        await foreach (MediaLinkResult result in _service.GetInfoAsync( TestContent )) {
            results.Add( result );
        }

        // Assert
        Assert.IsEmpty( results );
    }

    #endregion

    #region Partial Result Message Tests

    /// <summary>
    /// Verifies that a rate-limited saga with no provider payload still returns an API DTO marked
    /// partial, rather than forcing clients to infer its state from the message text.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenRateLimitedWithoutPayload_ShouldReturnPartialPlaceholder( ) {
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );
        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( TestIsrc ) )
            .ReturnsAsync( new LookupResult {
                Result = null,
                SagaId = "test-saga",
                RateLimitedProviders = [
                    new ProviderRateLimitInfo( SupportedProviders.AppleMusic, retryAfter, "/v1/catalog" )
                ]
            } );

        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        Assert.IsNotNull( result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        Assert.Contains( SupportedProviders.AppleMusic, result.RateLimitedProviders );
    }

    /// <summary>
    /// Verifies that a partial result with one rate-limited provider produces a single message naming
    /// that provider and describing it as temporarily unavailable.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenPartial_ShouldAddRateLimitMessage( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );

        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( TestIsrc ) )
            .ReturnsAsync( new LookupResult {
                Result = expectedResult,
                SagaId = "test-saga",
                RateLimitedProviders = [
                    new ProviderRateLimitInfo( SupportedProviders.AppleMusic, retryAfter, "/v1/catalog" )
                ]
            } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result );
        Assert.IsTrue( result.IsPartial, "A saga-backed partial result must expose isPartial on the API DTO." );
        Assert.IsNotNull( result.Messages );
        Assert.HasCount( 1, result.Messages );
        Assert.Contains( "AppleMusic", result.Messages[0] );
        Assert.Contains( "temporarily unavailable", result.Messages[0] );
    }

    /// <summary>
    /// Verifies that a partial result with multiple rate-limited providers produces one message per
    /// provider, in order.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenPartialWithMultipleRateLimited_ShouldAddAllMessages( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );

        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( TestIsrc ) )
            .ReturnsAsync( new LookupResult {
                Result = expectedResult,
                SagaId = "test-saga",
                RateLimitedProviders = [
                    new ProviderRateLimitInfo( SupportedProviders.AppleMusic, retryAfter, "/v1/catalog" ),
                    new ProviderRateLimitInfo( SupportedProviders.Tidal, retryAfter.AddMinutes( 2 ), "/v1/tracks" )
                ]
            } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result );
        Assert.IsTrue( result.IsPartial, "A saga-backed partial result must expose isPartial on the API DTO." );
        Assert.IsNotNull( result.Messages );
        Assert.HasCount( 2, result.Messages );
        Assert.Contains( "AppleMusic", result.Messages[0] );
        Assert.Contains( "Tidal", result.Messages[1] );
    }

    /// <summary>
    /// Verifies that a non-partial result carries no messages, so a complete lookup is presented without
    /// status text.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenNotPartial_ShouldNotAddMessages( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );

        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( TestIsrc ) )
            .ReturnsAsync( new LookupResult {
                Result = expectedResult
            } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result );
        Assert.IsFalse( result.IsPartial, "A finalized result must expose isPartial=false on the API DTO." );
        Assert.IsNull( result.Messages );
    }

    /// <summary>
    /// Verifies that a partial result with no rate-limited providers is flagged partial and carries a
    /// single "still being fetched" message, covering the in-progress (not rate-limited) case.
    /// </summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenPartialButNoRateLimitedProviders_ShouldAddPendingMessage( ) {
        // Arrange
        MediaLinkResult expectedResult = CreateMediaLinkResult( );

        _ = _orchestratorMock
            .Setup( o => o.LookupByIsrcAsync( TestIsrc ) )
            .ReturnsAsync( new LookupResult {
                Result = expectedResult,
                SagaId = "test-saga",
                RateLimitedProviders = []
            } );

        // Act
        MediaLinkResult? result = await _service.GetInfoByISRCAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result );
        Assert.IsTrue( result.IsPartial, "Pending secondary lookups must be machine-readable without parsing Messages." );
        Assert.IsNotNull( result.Messages );
        Assert.HasCount( 1, result.Messages );
        Assert.Contains( "still being fetched", result.Messages[0] );
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds a representative <see cref="MediaLinkResult"/> with a single Spotify provider result for
    /// use as the orchestrator's return value.
    /// </summary>
    /// <returns>A populated media-link result.</returns>
    private static MediaLinkResult CreateMediaLinkResult( ) {
        return new MediaLinkResult {
            Results = new Dictionary<SupportedProviders, MusicLookupResult> {
                [SupportedProviders.Spotify] = new MusicLookupResult {
                    Artist = TestArtist,
                    Title = TestTitle,
                    ExternalId = TestIsrc,
                    URL = "https://open.spotify.com/track/123",
                    ArtUrl = "https://example.com/art.jpg",
                    IsAlbum = false
                }
            }
        };
    }

    /// <summary>
    /// Wraps the supplied items as an async sequence (yielding after a <see cref="Task.Yield"/>) so the
    /// orchestrator mock can return a realistic streaming result.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="items">The items to yield in order.</param>
    /// <returns>An async sequence over <paramref name="items"/>.</returns>
    private static async IAsyncEnumerable<T> CreateAsyncEnumerable<T>( params T[] items ) {
        foreach (T item in items) {
            await Task.Yield( );
            yield return item;
        }
    }

    #endregion
}
