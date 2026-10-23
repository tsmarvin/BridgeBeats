using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Services.LinkResolver;
using Microsoft.Extensions.Logging;
using Moq;

#pragma warning disable MSTEST0049 // ILookupOrchestrator methods do not have CancellationToken overloads

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="LookupOrchestrator"/>, the saga/dedup/queue orchestrator for the distributed lookup path.
/// Covers constructor argument guards; the ISRC, UPC, metadata, provider-id, and free-text content entry points;
/// cache-hit short-circuits and cache-miss enqueue behavior; the per-link interactive wait budget (90s total, a
/// 5s floor); and the saga partial-versus-final resolution including rate-limit sentinels, in-flight dedup waits,
/// resumed sagas, and lock release on error. Collaborators are mocked.
/// </summary>
[TestClass]
public class LookupOrchestratorTests {
    /// <summary>Mock cache repository used to drive cache-hit and cache-miss paths.</summary>
    private Mock<IMediaLinkCacheRepository> _cacheMock = null!;
    /// <summary>Mock request deduplicator used to drive acquire/in-flight and completion-wait behavior.</summary>
    private Mock<IRequestDeduplicator> _deduplicatorMock = null!;
    /// <summary>Mock saga state manager used to drive saga creation and partial/final state reads.</summary>
    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    /// <summary>Mock provider-queue resolver returning <see cref="_queueMock"/> for any provider.</summary>
    private Mock<IProviderQueueResolver<QueuedLookupRequest>> _queueResolverMock = null!;
    /// <summary>Mock ATProto storage used to return the stored <see cref="MediaLinkResult"/> for a result URI.</summary>
    private Mock<IATProtoStorageService> _atProtoStorageMock = null!;
    /// <summary>Mock request queue used to assert enqueue calls and their payloads/priorities.</summary>
    private Mock<IRequestQueue<QueuedLookupRequest>> _queueMock = null!;
    /// <summary>Mock logger for the orchestrator.</summary>
    private Mock<ILogger<LookupOrchestrator>> _loggerMock = null!;
    /// <summary>The set of enabled providers (Spotify, Apple Music, Tidal) the orchestrator fans out across.</summary>
    private HashSet<SupportedProviders> _enabledProviders = null!;
    /// <summary>The orchestrator under test, rebuilt before each test.</summary>
    private LookupOrchestrator _orchestrator = null!;

    /// <summary>Sample ISRC used as a track lookup value.</summary>
    private const string TestIsrc = "USRC17607839";
    /// <summary>Sample UPC used as an album lookup value.</summary>
    private const string TestUpc = "012345678901";
    /// <summary>Sample track title used in metadata lookups.</summary>
    private const string TestTitle = "Test Song";
    /// <summary>Sample artist used in metadata lookups.</summary>
    private const string TestArtist = "Test Artist";
    /// <summary>Sample AT-URI returned as a stored result pointer.</summary>
    private const string TestRecordUri = "at://did:plc:test/com.bridgebeats.media.link/123abc";

    /// <summary>
    /// MSTest-injected test context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Creates fresh mocks, sets the enabled-provider set, wires the queue resolver to the queue mock, and builds the
    /// orchestrator before each test.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _cacheMock = new Mock<IMediaLinkCacheRepository>( );
        _deduplicatorMock = new Mock<IRequestDeduplicator>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _queueResolverMock = new Mock<IProviderQueueResolver<QueuedLookupRequest>>( );
        _atProtoStorageMock = new Mock<IATProtoStorageService>( );
        _queueMock = new Mock<IRequestQueue<QueuedLookupRequest>>( );
        _loggerMock = new Mock<ILogger<LookupOrchestrator>>( );

        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        // Default queue resolver setup
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( _queueMock.Object );

        _orchestrator = CreateOrchestrator( );
    }

    #region Constructor Tests

    /// <summary>
    /// Verifies the constructor succeeds when all dependencies are provided.
    /// </summary>
    [TestMethod]
    public void Constructor_WithValidDependencies_ShouldCreateInstance( ) {
        // Act
        LookupOrchestrator orchestrator = CreateOrchestrator( );

        // Assert
        Assert.IsNotNull( orchestrator );
    }

    /// <summary>
    /// Verifies a null cache repository is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullCache_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                null!,
                _deduplicatorMock.Object,
                _sagaManagerMock.Object,
                _queueResolverMock.Object,
                _atProtoStorageMock.Object,
                _enabledProviders,
                _loggerMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies a null deduplicator is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullDeduplicator_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                _cacheMock.Object,
                null!,
                _sagaManagerMock.Object,
                _queueResolverMock.Object,
                _atProtoStorageMock.Object,
                _enabledProviders,
                _loggerMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies a null saga state manager is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullSagaManager_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                _cacheMock.Object,
                _deduplicatorMock.Object,
                null!,
                _queueResolverMock.Object,
                _atProtoStorageMock.Object,
                _enabledProviders,
                _loggerMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies a null queue resolver is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullQueueResolver_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                _cacheMock.Object,
                _deduplicatorMock.Object,
                _sagaManagerMock.Object,
                null!,
                _atProtoStorageMock.Object,
                _enabledProviders,
                _loggerMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies a null ATProto storage service is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullAtProtoStorage_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                _cacheMock.Object,
                _deduplicatorMock.Object,
                _sagaManagerMock.Object,
                _queueResolverMock.Object,
                null!,
                _enabledProviders,
                _loggerMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies a null enabled-providers set is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullEnabledProviders_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                _cacheMock.Object,
                _deduplicatorMock.Object,
                _sagaManagerMock.Object,
                _queueResolverMock.Object,
                _atProtoStorageMock.Object,
                null!,
                _loggerMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies a null logger is rejected with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new LookupOrchestrator(
                _cacheMock.Object,
                _deduplicatorMock.Object,
                _sagaManagerMock.Object,
                _queueResolverMock.Object,
                _atProtoStorageMock.Object,
                _enabledProviders,
                null!
            )
        );
    }

    #endregion

    #region LookupByIsrcAsync Tests

    /// <summary>
    /// Verifies a null ISRC returns an empty, non-partial result without touching the deduplicator.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithNullIsrc_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( null! );

        // Assert
        Assert.IsNull( result.Result );
        Assert.IsFalse( result.IsPartial );
    }

    /// <summary>
    /// Verifies an empty ISRC returns an empty, non-partial result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithEmptyIsrc_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( string.Empty );

        // Assert
        Assert.IsNull( result.Result );
        Assert.IsFalse( result.IsPartial );
    }

    /// <summary>
    /// Verifies a whitespace-only ISRC returns an empty, non-partial result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithWhitespaceIsrc_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( "   " );

        // Assert
        Assert.IsNull( result.Result );
        Assert.IsFalse( result.IsPartial );
    }

    /// <summary>
    /// Verifies a fresh (non-stale) ISRC cache hit returns immediately without acquiring the deduplication lock.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithCacheHit_ShouldReturnCachedResult( ) {
        // Arrange
        MediaLinkResult cachedResult = CreateMediaLinkResult( );
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( (cachedResult, TestRecordUri, false) );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
        _deduplicatorMock.Verify(
            d => d.TryAcquireAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that a fresh cache hit (isStale = false) on a subset record — one where only some
    /// providers contributed results — yields a non-partial envelope with no saga ID. Under the
    /// computed-property model, <c>LookupResult.IsPartial</c> derives from <c>!string.IsNullOrEmpty(SagaId)</c>.
    /// The cache-hit path sets no <c>SagaId</c>, so the envelope is always non-partial for a
    /// fresh cache hit regardless of how many providers contributed to the stored result.
    /// A regression that incorrectly sets <c>SagaId</c> on a cache-hit result would fail
    /// the <c>Assert.IsNull(result.SagaId)</c> assertion below.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithFreshCacheHitOnSubsetRecord_ShouldReturnNonPartialWithNoSagaId( ) {
        // Arrange — a subset result (only Spotify responded); the cache-hit path must not
        // propagate any partial state to the envelope via SagaId.
        MediaLinkResult subsetResult = new( ) {
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

        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( (subsetResult, TestRecordUri, false) ); // isStale = false → served as fresh

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert — cold-served subset record is non-partial with no saga
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial, "Cold-served subset record must not be partial in the envelope." );
        Assert.IsNull( result.SagaId, "Cold-served subset record must carry no saga ID." );

        // Deduplicator must not be touched — cache hit short-circuits before dedup
        _deduplicatorMock.Verify(
            d => d.TryAcquireAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies a cache miss with the dedup lock acquired creates an ISRC saga and enqueues the first provider request
    /// at <see cref="QueuePriority.Interactive"/>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithCacheMissAndDeduplicationAcquired_ShouldCreateSagaAndEnqueue( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult result1 = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( result1 );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        _sagaManagerMock.Verify( s => s.GetOrCreateAsync(
            It.IsAny<string>( ),
            It.Is<string>( k => k.Contains( "IsrcLookup" ) ),
            LookupRequestType.IsrcLookup,
            It.Is<string>( v => v.Equals( TestIsrc, StringComparison.InvariantCultureIgnoreCase ) ),
            QueuePriority.Interactive
        ), Times.Once );
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), QueuePriority.Interactive ),
            Times.Exactly( _enabledProviders.Count )
        );
    }

    /// <summary>A fenced provider-state initialization loss aborts before enqueue or completion publication.</summary>
    [TestMethod]
    public async Task LookupByIsrcAsync_WhenProviderInitializationFenceIsLost_ShouldAbortBeforeEnqueue( ) {
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        _ = _sagaManagerMock
            .Setup( s => s.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            ( ) => _orchestrator.LookupByIsrcAsync( TestIsrc ) );

        _queueMock.Verify( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ) ), Times.Never );
        _deduplicatorMock.Verify( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ), Times.Never );
        _deduplicatorMock.Verify( d => d.ReleaseAsync( It.IsAny<string>( ), null ), Times.Once );
    }

    /// <summary>
    /// Verifies the enqueued request carries <see cref="QueuePriority.Interactive"/> as its origin priority, so a
    /// rate-limit deferral can later distinguish an interactive-origin request.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithCacheMissAndDeduplicationAcquired_ShouldEnqueueWithInteractiveOriginPriority( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult result1 = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( result1 );

        // Act
        _ = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        _queueMock.Verify(
            q => q.EnqueueAsync(
                It.Is<QueuedLookupRequest>( r => r.OriginPriority == QueuePriority.Interactive ),
                QueuePriority.Interactive
            ),
            Times.Exactly( _enabledProviders.Count )
        );
    }

    /// <summary>
    /// A manual lookup promotes an existing maintenance saga and enqueues interactive copies only for its unfinished
    /// direct provider legs. The generation token lets those copies race old bulk deliveries safely.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenMaintenanceSagaExists_ShouldPromotePendingLegsToInteractive( ) {
        SetupCacheMiss( );
        SetupDeduplicationInFlight( );
        SetupDeduplicationWaitWithResult( );

        LookupSagaState maintenanceSaga = CreateSagaState(
            providers: [
                (SupportedProviders.Spotify, true),
                (SupportedProviders.AppleMusic, false),
                (SupportedProviders.Tidal, false)
        ] );
        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( maintenanceSaga );
        _ = _sagaManagerMock
            .Setup( s => s.TryPromoteToInteractiveAsync(
                It.IsAny<string>( ), maintenanceSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( CreateMediaLinkResult( ) );

        _ = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        _queueMock.Verify( q => q.EnqueueAsync(
            It.Is<QueuedLookupRequest>( r =>
                r.Provider != SupportedProviders.Spotify
                && r.OriginPriority == QueuePriority.Interactive
                && r.SagaInstanceToken == maintenanceSaga.InstanceToken ),
            QueuePriority.Interactive ), Times.Exactly( 2 ) );
        _queueMock.Verify( q => q.EnqueueAsync(
            It.Is<QueuedLookupRequest>( r => r.Provider == SupportedProviders.Spotify ),
            It.IsAny<QueuePriority>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies that when another caller is already in flight, this caller waits for completion and reads the result
    /// rather than creating a new saga.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithInFlightRequest_ShouldWaitForCompletion( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationInFlight( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( TestRecordUri );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( TestRecordUri ) )
            .ReturnsAsync( expectedResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
        _sagaManagerMock.Verify(
            s => s.GetOrCreateAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ), It.IsAny<QueuePriority>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that when an in-flight wait times out (returns no URI), the orchestrator re-checks the cache (which
    /// now hits), reading the cache twice in total.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithInFlightRequestAndTimeout_ShouldRetryCache( ) {
        // Arrange
        MediaLinkResult cachedResult = CreateMediaLinkResult( );

        _ = _cacheMock
            .SetupSequence( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null )
            .ReturnsAsync( (cachedResult, TestRecordUri, false) );

        SetupDeduplicationInFlight( );
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( (string?)null ); // Timeout

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        _cacheMock.Verify( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ), Times.Exactly( 2 ) );
    }

    #endregion

    #region LookupByUpcAsync Tests

    /// <summary>
    /// Verifies a null UPC returns an empty, non-partial result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByUpcAsync_WithNullUpc_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByUpcAsync( null! );

        // Assert
        Assert.IsNull( result.Result );
        Assert.IsFalse( result.IsPartial );
    }

    /// <summary>
    /// Verifies a fresh UPC cache hit returns immediately.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByUpcAsync_WithCacheHit_ShouldReturnCachedResult( ) {
        // Arrange
        MediaLinkResult cachedResult = CreateMediaLinkResult( );
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByUPCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( (cachedResult, TestRecordUri, false) );

        // Act
        LookupResult result = await _orchestrator.LookupByUpcAsync( TestUpc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
    }

    /// <summary>
    /// Verifies a UPC cache miss enqueues a request marked <c>IsAlbum = true</c> with
    /// <see cref="LookupRequestType.UpcLookup"/> (UPC resolves an album/release).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByUpcAsync_WithCacheMiss_ShouldQueueWithIsAlbumTrue( ) {
        // Arrange
        SetupCacheMissForUpc( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        _ = await _orchestrator.LookupByUpcAsync( TestUpc );

        // Assert
        _queueMock.Verify( q => q.EnqueueAsync(
            It.Is<QueuedLookupRequest>( r => r.IsAlbum == true && r.LookupType == LookupRequestType.UpcLookup ),
            QueuePriority.Interactive
        ), Times.Exactly( _enabledProviders.Count ) );
    }

    #endregion

    #region LookupByMetadataAsync Tests

    /// <summary>
    /// Verifies a null title returns an empty result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByMetadataAsync_WithNullTitle_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByMetadataAsync( null!, TestArtist );

        // Assert
        Assert.IsNull( result.Result );
    }

    /// <summary>
    /// Verifies a null artist returns an empty result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByMetadataAsync_WithNullArtist_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByMetadataAsync( TestTitle, null! );

        // Assert
        Assert.IsNull( result.Result );
    }

    /// <summary>
    /// Verifies a metadata (title + artist) cache hit returns immediately.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByMetadataAsync_WithCacheHit_ShouldReturnCachedResult( ) {
        // Arrange
        MediaLinkResult cachedResult = CreateMediaLinkResult( );
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByMetadataAsync( TestTitle, TestArtist ) )
            .ReturnsAsync( (cachedResult, TestRecordUri, false) );

        // Act
        LookupResult result = await _orchestrator.LookupByMetadataAsync( TestTitle, TestArtist );

        // Assert
        Assert.IsNotNull( result.Result );
    }

    /// <summary>
    /// Verifies a metadata cache miss enqueues a <see cref="LookupRequestType.SongLookup"/> request carrying the title
    /// and artist.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByMetadataAsync_WithCacheMiss_ShouldQueueWithTitleAndArtist( ) {
        // Arrange
        SetupCacheMissForMetadata( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        _ = await _orchestrator.LookupByMetadataAsync( TestTitle, TestArtist );

        // Assert
        _queueMock.Verify( q => q.EnqueueAsync(
            It.Is<QueuedLookupRequest>( r =>
                r.Title == TestTitle &&
                r.Artist == TestArtist &&
                r.LookupType == LookupRequestType.SongLookup
            ),
            QueuePriority.Interactive
        ), Times.Exactly( _enabledProviders.Count ) );
    }

    #endregion

    #region LookupByProviderIdAsync Tests

    /// <summary>
    /// Verifies a null provider id returns an empty result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByProviderIdAsync_WithNullProviderId_ShouldReturnEmptyResult( ) {
        // Act
        LookupResult result = await _orchestrator.LookupByProviderIdAsync( null!, SupportedProviders.Spotify, false );

        // Assert
        Assert.IsNull( result.Result );
    }

    /// <summary>
    /// Verifies a provider-id lookup for a track (<c>isAlbum: false</c>) enqueues a
    /// <see cref="LookupRequestType.SongIdLookup"/> request.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByProviderIdAsync_ForTrack_ShouldUseSongIdLookupType( ) {
        // Arrange
        SetupCacheMissForProviderId( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        _ = await _orchestrator.LookupByProviderIdAsync( "spotify-id", SupportedProviders.Spotify, false );

        // Assert
        _queueMock.Verify( q => q.EnqueueAsync(
            It.Is<QueuedLookupRequest>( r => r.LookupType == LookupRequestType.SongIdLookup && r.IsAlbum == false ),
            QueuePriority.Interactive
        ), Times.Once );
    }

    /// <summary>An initial-provider fence loss aborts before enqueue or completion publication.</summary>
    [TestMethod]
    public async Task LookupByProviderIdAsync_WhenInitialProviderFenceIsLost_ShouldAbortBeforeEnqueue( ) {
        SetupCacheMissForProviderId( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        _ = _sagaManagerMock
            .Setup( s => s.TrySetInitialProviderAsync(
                It.IsAny<string>( ), It.IsAny<SupportedProviders>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            ( ) => _orchestrator.LookupByProviderIdAsync( "spotify-id", SupportedProviders.Spotify, false ) );

        _queueMock.Verify( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ) ), Times.Never );
        _deduplicatorMock.Verify( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ), Times.Never );
        _deduplicatorMock.Verify( d => d.ReleaseAsync( It.IsAny<string>( ), null ), Times.Once );
        _sagaManagerMock.Verify( s => s.TryInitializeProviderStatesAsync(
            It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>
    /// Verifies a provider-id lookup for an album (<c>isAlbum: true</c>) enqueues a
    /// <see cref="LookupRequestType.AlbumIdLookup"/> request.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByProviderIdAsync_ForAlbum_ShouldUseAlbumIdLookupType( ) {
        // Arrange
        SetupCacheMissForProviderId( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        _ = await _orchestrator.LookupByProviderIdAsync( "spotify-id", SupportedProviders.Spotify, true );

        // Assert
        _queueMock.Verify( q => q.EnqueueAsync(
            It.Is<QueuedLookupRequest>( r => r.LookupType == LookupRequestType.AlbumIdLookup && r.IsAlbum == true ),
            QueuePriority.Interactive
        ), Times.Once );
    }

    #endregion

    #region LookupByContentAsync Tests

    /// <summary>
    /// Verifies null free-text content yields no streamed results.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithNullContent_ShouldReturnEmpty( ) {
        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( null! )) {
            results.Add( result );
        }

        // Assert
        Assert.IsEmpty( results );
    }

    /// <summary>
    /// Verifies empty free-text content yields no streamed results.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithEmptyContent_ShouldReturnEmpty( ) {
        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( string.Empty )) {
            results.Add( result );
        }

        // Assert
        Assert.IsEmpty( results );
    }

    /// <summary>
    /// Verifies a Spotify URL embedded in free text is extracted and resolved to a single streamed result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithSpotifyUrl_ShouldReturnResult( ) {
        // Arrange
        string content = "Check out this song: https://open.spotify.com/track/abc123";
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 1, results );
        Assert.IsNotNull( results[0].Result );
        MediaLinkResult actualResult = results[0].Result!;
        Assert.HasCount( 1, actualResult.InputLinks );
        Assert.AreEqual(
            "https://open.spotify.com/track/abc123",
            actualResult.InputLinks[0],
            "The caching orchestrator must restore the submitted URL because PDS records do not persist InputLinks."
        );
    }

    /// <summary>
    /// Verifies an Apple Music URL in free text is extracted and resolved to a single streamed result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithAppleMusicUrl_ShouldReturnResult( ) {
        // Arrange
        string content = "Check out: https://music.apple.com/us/album/test/12345";
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 1, results );
    }

    /// <summary>
    /// Verifies a Tidal URL in free text is extracted and resolved to a single streamed result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithTidalUrl_ShouldReturnResult( ) {
        // Arrange
        string content = "Listen here: https://tidal.com/browse/track/12345";
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 1, results );
    }

    /// <summary>
    /// Verifies the same URL appearing twice in free text is deduplicated to a single streamed result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithMultipleUrls_ShouldDeduplicateSameLinks( ) {
        // Arrange
        string url = "https://open.spotify.com/track/abc123";
        string content = $"Check these: {url} and again {url}";
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );
        SetupDeduplicationWaitWithResult( );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert - should only process the unique URL once
        Assert.HasCount( 1, results );
    }

    /// <summary>
    /// Verifies a single link gets the full interactive wait budget (30 seconds), the configured interactive wait.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithSingleLink_ShouldUseFullInteractiveBudget( ) {
        // Arrange
        string content = "Check out https://open.spotify.com/track/abc123";
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        List<TimeSpan> capturedTimeouts = [];
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .Callback<string, TimeSpan, CancellationToken>( ( _, timeout, _ ) => capturedTimeouts.Add( timeout ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 1, results );
        Assert.HasCount( 1, capturedTimeouts );
        Assert.AreEqual( TimeSpan.FromSeconds( 30 ), capturedTimeouts[0] );
    }

    /// <summary>
    /// Verifies that with four links the 90-second total budget is divided per link, giving each a 22.5-second wait.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithMultipleLinks_ShouldScalePerLinkWaitBudget( ) {
        // Arrange - 4 links: per-link budget = min(30s, 90s / 4) = 22.5s
        string content = string.Join(
            " ",
            Enumerable.Range( 1, 4 ).Select( i => $"https://open.spotify.com/track/track{i}" )
        );
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        List<TimeSpan> capturedTimeouts = [];
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .Callback<string, TimeSpan, CancellationToken>( ( _, timeout, _ ) => capturedTimeouts.Add( timeout ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 4, results );
        Assert.HasCount( 4, capturedTimeouts );
        foreach (TimeSpan timeout in capturedTimeouts) {
            Assert.AreEqual( TimeSpan.FromSeconds( 22.5 ), timeout );
        }
    }

    /// <summary>
    /// Verifies that with many links (20) the per-link wait is clamped at the 5-second floor rather than shrinking
    /// proportionally.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithManyLinks_ShouldApplyPerLinkBudgetFloor( ) {
        // Arrange - 20 links: 90s / 20 = 4.5s, clamped up to the 5s floor
        string content = string.Join(
            " ",
            Enumerable.Range( 1, 20 ).Select( i => $"https://open.spotify.com/track/track{i}" )
        );
        SetupCacheMissForUrl( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        List<TimeSpan> capturedTimeouts = [];
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .Callback<string, TimeSpan, CancellationToken>( ( _, timeout, _ ) => capturedTimeouts.Add( timeout ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.HasCount( 20, results );
        Assert.HasCount( 20, capturedTimeouts );
        foreach (TimeSpan timeout in capturedTimeouts) {
            Assert.AreEqual( TimeSpan.FromSeconds( 5 ), timeout );
        }
    }

    /// <summary>
    /// Verifies free text containing only non-music URLs yields no results (the link regex matches only the supported
    /// provider hosts).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByContentAsync_WithNoMusicUrls_ShouldReturnEmpty( ) {
        // Arrange
        string content = "Check out https://example.com and https://github.com";

        // Act
        List<LookupResult> results = [];
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            results.Add( result );
        }

        // Assert
        Assert.IsEmpty( results );
    }

    #endregion

    #region Saga and Partial Result Tests

    /// <summary>
    /// Verifies that when the saga is partial with rate-limit info, the result is flagged partial and carries the
    /// rate-limited provider list.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenSagaReturnsPartialResult_ShouldReturnPartialWithInfo( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = "test-saga",
                LookupKey = "test-key",
                LookupType = LookupRequestType.IsrcLookup,
                LookupValue = TestIsrc,
                CreatedAt = DateTimeOffset.UtcNow,
                IsPartial = true,
                PartialResultUri = TestRecordUri,
                ProviderStates = [],
                RateLimitInfo = rateLimitInfo
            } );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        Assert.HasCount( 1, result.RateLimitedProviders );
        Assert.AreEqual( SupportedProviders.AppleMusic, result.RateLimitedProviders[0].Provider );
    }

    /// <summary>
    /// Verifies that when a partial saga becomes final within the wait budget, the orchestrator waits for final
    /// completion and returns the final (non-partial) result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenPartialThenFinalWithinBudget_ShouldReturnFinal( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        // First read: saga is partial with a pending provider; second read: saga finalized
        LookupSagaState partialSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );
        LookupSagaState finalSaga = CreateSagaState(
            isPartial: false,
            partialResultUri: PartialUri,
            finalResultUri: TestRecordUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, true)]
        );

        _ = _sagaManagerMock
            .SetupSequence( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialSaga )
            .ReturnsAsync( finalSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
        _deduplicatorMock.Verify(
            d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies that when the final result never arrives within budget, the orchestrator returns an honest partial
    /// result carrying the saga id.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenFinalNeverArrives_ShouldReturnHonestPartial( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        LookupSagaState partialSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialSaga );

        // Final never published - wait times out
        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( (string?)null );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.SagaId );
    }

    /// <summary>
    /// Verifies that when the rate-limit sentinel is published during the final wait, the orchestrator returns a
    /// partial result with the rate-limited provider info.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenRateLimitSentinelDuringFinalWait_ShouldReturnPartialWithInfo( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        // First read: two providers pending, none rate-limited yet (so the wait proceeds);
        // second read (after sentinel): rate limit info recorded
        LookupSagaState pendingSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false), (SupportedProviders.Tidal, false)]
        );
        LookupSagaState rateLimitedSaga = pendingSaga with { RateLimitInfo = rateLimitInfo };

        _ = _sagaManagerMock
            .SetupSequence( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( pendingSaga )
            .ReturnsAsync( rateLimitedSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( LookupConstants.RateLimitedSentinel );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        Assert.AreEqual( SupportedProviders.AppleMusic, result.RateLimitedProviders[0].Provider );
    }

    /// <summary>
    /// Verifies that when every pending provider is already rate-limited, the orchestrator returns a partial result
    /// immediately without entering the final wait.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenAllPendingProvidersRateLimited_ShouldReturnPartialImmediately( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        LookupSagaState rateLimitedSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            rateLimitInfo: rateLimitInfo,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( rateLimitedSaga );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        _deduplicatorMock.Verify(
            d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that an in-flight request whose saga is currently partial keeps waiting for the final result and
    /// returns it once available, without creating a new saga.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithInFlightPartialResult_ShouldKeepWaitingForFinal( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationInFlight( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        LookupSagaState partialSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );
        LookupSagaState finalSaga = CreateSagaState(
            isPartial: false,
            partialResultUri: PartialUri,
            finalResultUri: TestRecordUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, true)]
        );

        _ = _sagaManagerMock
            .SetupSequence( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialSaga )
            .ReturnsAsync( finalSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
        _sagaManagerMock.Verify(
            s => s.GetOrCreateAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ), It.IsAny<QueuePriority>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that when the acquired saga is resumed and already holds a partial result, the orchestrator does not
    /// re-enqueue and does not blind-wait, returning the existing partial.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenResumedSagaHasPartialResult_ShouldNotReenqueue( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        LookupSagaState resumedSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetOrCreateAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ), It.IsAny<QueuePriority>( ) ) )
            .ReturnsAsync( resumedSaga );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( resumedSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( (string?)null );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ) ),
            Times.Never
        );
        _deduplicatorMock.Verify(
            d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that when a resumed saga has a stored partial result URI, the dedup lock is released with that known
    /// URI (so waiters wake with the result) rather than with null.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenResumedSagaHasStoredResult_ShouldReleaseLockWithKnownUri( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        LookupSagaState resumedSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetOrCreateAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ), It.IsAny<QueuePriority>( ) ) )
            .ReturnsAsync( resumedSaga );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( resumedSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( (string?)null );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        _ = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - released with the known URI (not null: an empty publish would leave waiters stuck)
        _deduplicatorMock.Verify( d => d.ReleaseAsync( It.IsAny<string>( ), PartialUri ), Times.Once );
        _deduplicatorMock.Verify( d => d.ReleaseAsync( It.IsAny<string>( ), null ), Times.Never );
    }

    /// <summary>
    /// Verifies that an in-flight request whose saga already has a stored rate-limited result skips the blind
    /// completion wait and returns the stored partial directly.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithInFlightSagaHavingStoredResult_ShouldSkipBlindWaitAndHonorEscapeHatch( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationInFlight( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        LookupSagaState rateLimitedSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            rateLimitInfo: rateLimitInfo,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( rateLimitedSaga );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - escape hatch returned the partial without ever blind-waiting
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        _deduplicatorMock.Verify(
            d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that an in-flight wait resolving to the rate-limit sentinel returns the stored partial data (read from
    /// the saga that appears on the second read) with rate-limited provider info.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WithInFlightRateLimitSentinel_ShouldReturnStoredPartialData( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationInFlight( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        LookupSagaState rateLimitedSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            rateLimitInfo: rateLimitInfo,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        // First read (pre-wait check): no saga yet; second read (after sentinel): partial stored
        _ = _sagaManagerMock
            .SetupSequence( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( (LookupSagaState?)null )
            .ReturnsAsync( rateLimitedSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( LookupConstants.RateLimitedSentinel );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( PartialUri ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - available partial links are returned alongside the rate-limit info
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        Assert.AreEqual( SupportedProviders.AppleMusic, result.RateLimitedProviders[0].Provider );
    }

    /// <summary>
    /// Verifies that when the saga's recorded rate limits have already expired, the orchestrator keeps waiting for the
    /// final result rather than returning a stale rate-limited partial, and returns the final once it arrives.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenAllPendingRateLimitsExpired_ShouldKeepWaitingForFinal( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        // Rate limit lapsed five minutes ago - waiting CAN help, escape hatch must not fire
        List<ProviderRateLimitInfo> expiredRateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( -5 ), "/v1/catalog" )
        ];

        LookupSagaState partialSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            rateLimitInfo: expiredRateLimitInfo,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );
        LookupSagaState finalSaga = CreateSagaState(
            isPartial: false,
            partialResultUri: PartialUri,
            finalResultUri: TestRecordUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, true)]
        );

        _ = _sagaManagerMock
            .SetupSequence( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialSaga )
            .ReturnsAsync( finalSaga );

        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( TestRecordUri );

        MediaLinkResult expectedResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( expectedResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - the final result was waited for instead of returning a stale partial
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
        _deduplicatorMock.Verify(
            d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies that when rate limits have expired and the budget is exhausted, the returned partial does not report
    /// the expired rate limits (the rate-limited provider list is null).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenRateLimitExpiredAndBudgetExhausted_ShouldNotReportExpiredRateLimits( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        List<ProviderRateLimitInfo> expiredRateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( -5 ), "/v1/catalog" )
        ];

        LookupSagaState partialSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            rateLimitInfo: expiredRateLimitInfo,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialSaga );

        // Final never published - wait times out
        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .ReturnsAsync( (string?)null );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNull( result.RateLimitedProviders );
    }

    /// <summary>
    /// Verifies that when the create-path completion wait times out but the saga has since completed with a final
    /// result, the orchestrator re-reads the saga and returns the final (non-partial) result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenCreateWaitTimesOutWithFinalResult_ShouldReturnFinal( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        // No completion notification arrives within the budget
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( (string?)null );

        LookupSagaState finalSaga = CreateSagaState(
            isPartial: false,
            finalResultUri: TestRecordUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, true), (SupportedProviders.Tidal, true)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( finalSaga );

        MediaLinkResult finalResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( TestRecordUri ) )
            .ReturnsAsync( finalResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - the cached final result is returned, honestly flagged as complete
        Assert.IsNotNull( result.Result );
        Assert.IsFalse( result.IsPartial );
        Assert.IsNull( result.SagaId );
    }

    /// <summary>
    /// Verifies that when the create-path wait resolves to the rate-limit sentinel, the orchestrator returns the
    /// stored partial result with rate-limited provider info.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenRateLimitSentinelOnCreatePath_ShouldReturnStoredPartialData( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( LookupConstants.RateLimitedSentinel );

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        LookupSagaState rateLimitedSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            rateLimitInfo: rateLimitInfo,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( rateLimitedSaga );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( PartialUri ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - available partial links are returned alongside the rate-limit info
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        Assert.AreEqual( SupportedProviders.AppleMusic, result.RateLimitedProviders[0].Provider );
    }

    /// <summary>
    /// Verifies that a rate limit appearing in the gap between enqueue and subscription is caught by the final-wait
    /// missed-result check, returning a partial with rate-limited provider info.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_WhenRateLimitArisesInSubscribeGap_ShouldReturnPartialViaSentinel( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );
        SetupSagaCreation( );

        const string PartialUri = "at://did:plc:test/com.bridgebeats.media.link/partial";

        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( PartialUri );

        List<ProviderRateLimitInfo> rateLimitInfo = [
            new ProviderRateLimitInfo( SupportedProviders.AppleMusic, DateTimeOffset.UtcNow.AddMinutes( 5 ), "/v1/catalog" )
        ];

        LookupSagaState pendingSaga = CreateSagaState(
            isPartial: true,
            partialResultUri: PartialUri,
            providers: [(SupportedProviders.Spotify, true), (SupportedProviders.AppleMusic, false)]
        );
        LookupSagaState rateLimitedSaga = pendingSaga with { RateLimitInfo = rateLimitInfo };

        // First read (wait loop): no rate limits yet; second read (gap check inside the
        // deduplicator): rate limit recorded; third read (sentinel handling): unchanged
        _ = _sagaManagerMock
            .SetupSequence( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( pendingSaga )
            .ReturnsAsync( rateLimitedSaga )
            .ReturnsAsync( rateLimitedSaga );

        // Simulate the deduplicator running the missed-result check after subscribing
        _ = _deduplicatorMock
            .Setup( d => d.WaitForFinalCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ), It.IsAny<Func<Task<string?>>?>( ) ) )
            .Returns( ( string _, TimeSpan _, Func<Task<string?>>? missedResultCheck, CancellationToken _ ) => missedResultCheck!( ) );

        MediaLinkResult partialResult = CreateMediaLinkResult( );
        _ = _atProtoStorageMock
            .Setup( a => a.GetMediaLinkResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( partialResult );

        // Act
        LookupResult result = await _orchestrator.LookupByIsrcAsync( TestIsrc );

        // Assert - the gap check surfaced the sentinel and the partial was returned
        Assert.IsNotNull( result.Result );
        Assert.IsTrue( result.IsPartial );
        Assert.IsNotNull( result.RateLimitedProviders );
        Assert.AreEqual( SupportedProviders.AppleMusic, result.RateLimitedProviders[0].Provider );
    }

    /// <summary>
    /// Verifies that when saga creation throws, the exception propagates and the dedup lock is released (with a null
    /// result URI), preventing a stuck in-flight lock.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task LookupByIsrcAsync_OnError_ShouldReleaseDeduplicationLock( ) {
        // Arrange
        SetupCacheMiss( );
        SetupDeduplicationAcquired( );

        _ = _sagaManagerMock
            .Setup( s => s.GetOrCreateAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ), It.IsAny<QueuePriority>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Test error" ) );

        // Act & Assert
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>( ( ) =>
            _orchestrator.LookupByIsrcAsync( TestIsrc )
        );

        _deduplicatorMock.Verify( d => d.ReleaseAsync( It.IsAny<string>( ), null ), Times.Once );
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds a <see cref="LookupOrchestrator"/> wired to the current mock objects and enabled-provider set.
    /// </summary>
    /// <returns>A new orchestrator instance.</returns>
    private LookupOrchestrator CreateOrchestrator( ) {
        return new LookupOrchestrator(
            _cacheMock.Object,
            _deduplicatorMock.Object,
            _sagaManagerMock.Object,
            _queueResolverMock.Object,
            _atProtoStorageMock.Object,
            _enabledProviders,
            _loggerMock.Object
        );
    }

    /// <summary>
    /// Builds a sample <see cref="MediaLinkResult"/> with one Spotify provider result, used as the stored result the
    /// ATProto storage mock returns.
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
    /// Builds a <see cref="LookupSagaState"/> with the given partial/final URIs, rate-limit info, and per-provider
    /// completion states (each provider's <c>IsSuccess</c> mirrors its <c>IsComplete</c>).
    /// </summary>
    /// <param name="isPartial">Whether the saga is marked partial.</param>
    /// <param name="partialResultUri">The partial result AT-URI, if any.</param>
    /// <param name="finalResultUri">The final result AT-URI, if any.</param>
    /// <param name="rateLimitInfo">The recorded provider rate-limit info, if any.</param>
    /// <param name="providers">The per-provider (provider, isComplete) pairs to seed provider states.</param>
    /// <returns>A populated saga state.</returns>
    private static LookupSagaState CreateSagaState(
        bool isPartial = false,
        string? partialResultUri = null,
        string? finalResultUri = null,
        List<ProviderRateLimitInfo>? rateLimitInfo = null,
        (SupportedProviders provider, bool isComplete)[]? providers = null
    ) {
        Dictionary<SupportedProviders, ProviderLookupState> states = [];
        foreach ((SupportedProviders provider, bool isComplete) in providers ?? []) {
            states[provider] = new ProviderLookupState(
                Provider: provider,
                IsComplete: isComplete,
                IsSuccess: isComplete,
                ResultJson: null,
                CompletedAt: isComplete ? DateTimeOffset.UtcNow : null,
                ErrorMessage: null
            );
        }

        return new LookupSagaState {
            SagaId = "test-saga",
            LookupKey = "test-key",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = TestIsrc,
            CreatedAt = DateTimeOffset.UtcNow,
            InstanceToken = "test-instance",
            IsPartial = isPartial,
            PartialResultUri = partialResultUri,
            FinalResultUri = finalResultUri,
            ProviderStates = states,
            RateLimitInfo = rateLimitInfo
        };
    }

    /// <summary>
    /// Configures the cache mock to return a miss for ISRC lookups.
    /// </summary>
    private void SetupCacheMiss( ) {
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );
    }

    /// <summary>
    /// Configures the cache mock to return a miss for UPC lookups.
    /// </summary>
    private void SetupCacheMissForUpc( ) {
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByUPCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );
    }

    /// <summary>
    /// Configures the cache mock to return a miss for metadata (title + artist) lookups.
    /// </summary>
    private void SetupCacheMissForMetadata( ) {
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByMetadataAsync( It.IsAny<string>( ), It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );
    }

    /// <summary>
    /// Configures the cache mock to return a miss for provider-id lookups.
    /// </summary>
    private void SetupCacheMissForProviderId( ) {
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultByProviderIdAsync( It.IsAny<string>( ), It.IsAny<SupportedProviders>( ), It.IsAny<bool>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );
    }

    /// <summary>
    /// Configures the cache mock to return a miss for URL (free-text link) lookups.
    /// </summary>
    private void SetupCacheMissForUrl( ) {
        _ = _cacheMock
            .Setup( c => c.TryGetCachedResultAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );
    }

    /// <summary>
    /// Configures the deduplicator mock so this caller acquires the in-flight lock.
    /// </summary>
    private void SetupDeduplicationAcquired( ) {
        _ = _deduplicatorMock
            .Setup( d => d.TryAcquireAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( new DeduplicationResult( Acquired: true, AlreadyInFlight: false, RequestKey: "test-key" ) );
    }

    /// <summary>
    /// Configures the deduplicator mock so another caller already holds the in-flight lock.
    /// </summary>
    private void SetupDeduplicationInFlight( ) {
        _ = _deduplicatorMock
            .Setup( d => d.TryAcquireAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( new DeduplicationResult( Acquired: false, AlreadyInFlight: true, RequestKey: "test-key" ) );
    }

    /// <summary>
    /// Configures the saga manager mock to create a saga, accept provider-state initialization and initial-provider
    /// calls, and return a non-partial saga on read.
    /// </summary>
    private void SetupSagaCreation( ) {
        _ = _sagaManagerMock
            .Setup( s => s.GetOrCreateAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ), It.IsAny<QueuePriority>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = "test-saga",
                LookupKey = "test-key",
                LookupType = LookupRequestType.IsrcLookup,
                LookupValue = TestIsrc,
                CreatedAt = DateTimeOffset.UtcNow,
                InstanceToken = "test-instance",
                ProviderStates = []
            } );

        _ = _sagaManagerMock
            .Setup( s => s.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        _ = _sagaManagerMock
            .Setup( s => s.TrySetInitialProviderAsync(
                It.IsAny<string>( ), It.IsAny<SupportedProviders>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = "test-saga",
                LookupKey = "test-key",
                LookupType = LookupRequestType.IsrcLookup,
                LookupValue = TestIsrc,
                CreatedAt = DateTimeOffset.UtcNow,
                InstanceToken = "test-instance",
                IsPartial = false,
                ProviderStates = []
            } );
    }

    /// <summary>
    /// Configures the deduplicator mock so the completion wait resolves to the sample result URI.
    /// </summary>
    private void SetupDeduplicationWaitWithResult( ) {
        _ = _deduplicatorMock
            .Setup( d => d.WaitForCompletionAsync( It.IsAny<string>( ), It.IsAny<TimeSpan>( ) ) )
            .ReturnsAsync( TestRecordUri );
    }

    #endregion
}
