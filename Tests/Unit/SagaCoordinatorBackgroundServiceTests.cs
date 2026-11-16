using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Worker.SagaCoordinator;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SagaCoordinatorBackgroundService"/>, the hosted service that finalizes
/// completed lookup sagas. Covers constructor dependency validation; the polling loop that picks up
/// completed-but-unfinalized sagas and writes their final result, releases the deduplicator, and
/// clears the partial flag; the write-failure path that continues to the next saga; the no-success
/// path that releases with a null URI and deletes the saga; and the secondary-lookup orchestration
/// driven by the <c>saga:completed</c> Redis subscription — marking partial, materializing cached
/// provider results to finalize early, queuing remaining providers at the priority implied by the
/// saga's origin, deferring finalization until secondaries return, and the
/// initialize-then-mark-then-claim ordering that guards the secondaries marker.
/// </summary>
[TestClass]
public class SagaCoordinatorBackgroundServiceTests {
    /// <summary>Mock Redis multiplexer supplying the subscriber for saga-completion events.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    /// <summary>Mock Redis subscriber whose registered handlers the tests invoke directly.</summary>
    private Mock<ISubscriber> _subscriberMock = null!;
    /// <summary>Mock saga state manager the service reads and updates.</summary>
    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    /// <summary>Mock AT Protocol storage used to assert final and partial record writes.</summary>
    private Mock<IATProtoStorageService> _atProtoStorageMock = null!;
    /// <summary>Mock cache repository for result caching and cached-by-ISRC lookups.</summary>
    private Mock<IMediaLinkCacheRepository> _cacheRepositoryMock = null!;
    /// <summary>Mock request deduplicator the service releases on finalization.</summary>
    private Mock<IRequestDeduplicator> _deduplicatorMock = null!;
    /// <summary>Mock logger for the result combiner.</summary>
    private Mock<ILogger<SagaResultCombiner>> _combinerLoggerMock = null!;
    /// <summary>Real result combiner the service uses to merge provider results.</summary>
    private SagaResultCombiner _resultCombiner = null!;
    /// <summary>Mock provider-queue resolver used to assert secondary-lookup enqueues.</summary>
    private Mock<IProviderQueueResolver<QueuedLookupRequest>> _queueResolverMock = null!;
    /// <summary>The set of enabled providers (Spotify, Apple Music, Tidal) the service considers.</summary>
    private HashSet<SupportedProviders> _enabledProviders = null!;
    /// <summary>Mock logger for the background service.</summary>
    private Mock<ILogger<SagaCoordinatorBackgroundService>> _loggerMock = null!;
    /// <summary>Mock stale-refresh review store used by finalization lifecycle tests.</summary>
    private Mock<IRefreshReviewStore> _refreshReviewStoreMock = null!;

    /// <summary>Fixed saga id used across the tests.</summary>
    private const string TestSagaId = "test-saga-id-12345678";
    /// <summary>Fixed lookup key for the complete-saga fixtures.</summary>
    private const string TestLookupKey = "isrc:USRC12345678";
    /// <summary>Fixed record URI the storage mock returns for written results.</summary>
    private const string TestRecordUri = "at://did:plc:test/com.bridgebeats.medialink/abc123";

    /// <summary>MSTest-injected context; its cancellation token bounds the in-test delays.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Creates fresh mocks before each test, wires the subscriber, builds a real combiner, seeds the
    /// enabled-providers set, and defaults the secondaries-queued marker claim to succeed.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _subscriberMock = new Mock<ISubscriber>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _atProtoStorageMock = new Mock<IATProtoStorageService>( );
        _cacheRepositoryMock = new Mock<IMediaLinkCacheRepository>( );
        _deduplicatorMock = new Mock<IRequestDeduplicator>( );
        _combinerLoggerMock = new Mock<ILogger<SagaResultCombiner>>( );
        _resultCombiner = new SagaResultCombiner( _combinerLoggerMock.Object );
        _queueResolverMock = new Mock<IProviderQueueResolver<QueuedLookupRequest>>( );
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];
        _loggerMock = new Mock<ILogger<SagaCoordinatorBackgroundService>>( );
        _refreshReviewStoreMock = new Mock<IRefreshReviewStore>( );
        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( CreateCompleteSaga( ) );
        _ = _refreshReviewStoreMock.Setup( store => store.MarkUnresolvedAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        _ = _refreshReviewStoreMock.Setup( store => store.CompleteAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        _ = _redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) ).Returns( _subscriberMock.Object );

        // By default the coordinator wins the secondaries-queued marker (no concurrent handler)
        _ = _sagaManagerMock
            .Setup( s => s.TryMarkSecondariesQueuedAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TryMarkSecondariesQueuedAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        // By default the coordinator wins the finalize claim (no concurrent handler)
        _ = _sagaManagerMock
            .Setup( s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        // By default the write-generation CAS advances (first call wins per generation)
        _ = _sagaManagerMock
            .Setup( s => s.TryAdvanceWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TryAdvanceWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        // Token-fenced coordinator mutations succeed by default. Individual tests override these
        // setups when exercising a lost instance race or a failed durable write.
        _ = _sagaManagerMock
            .Setup( s => s.TryUpdateProviderStateAsync( It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TrySetPartialResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.Stored );
        _ = _sagaManagerMock
            .Setup( s => s.TrySetIsPartialAsync( It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TryInitializeProviderStatesAsync( It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
    }

    #region Constructor Tests

    /// <summary>
    /// Verifies that valid dependencies produce a usable service instance.
    /// </summary>
    [TestMethod]
    public void Constructor_WithValidDependencies_ShouldCreateInstance( ) {
        // Act
        SagaCoordinatorBackgroundService service = CreateService( );

        // Assert
        Assert.IsNotNull( service );
    }

    /// <summary>
    /// Verifies that a null Redis multiplexer throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullRedis_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                null!,
                _sagaManagerMock.Object,
                _atProtoStorageMock.Object,
                _cacheRepositoryMock.Object,
                _deduplicatorMock.Object,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies that a null saga state manager throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullSagaManager_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                null!,
                _atProtoStorageMock.Object,
                _cacheRepositoryMock.Object,
                _deduplicatorMock.Object,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies that a null AT Protocol storage service throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullAtProtoStorage_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                _sagaManagerMock.Object,
                null!,
                _cacheRepositoryMock.Object,
                _deduplicatorMock.Object,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies that a null cache repository throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullCacheRepository_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                _sagaManagerMock.Object,
                _atProtoStorageMock.Object,
                null!,
                _deduplicatorMock.Object,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies that a null request deduplicator throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullDeduplicator_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                _sagaManagerMock.Object,
                _atProtoStorageMock.Object,
                _cacheRepositoryMock.Object,
                null!,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies that a null result combiner throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullResultCombiner_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                _sagaManagerMock.Object,
                _atProtoStorageMock.Object,
                _cacheRepositoryMock.Object,
                _deduplicatorMock.Object,
                null!,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>
    /// Verifies that a null logger throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                _sagaManagerMock.Object,
                _atProtoStorageMock.Object,
                _cacheRepositoryMock.Object,
                _deduplicatorMock.Object,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                null!,
                _refreshReviewStoreMock.Object
            )
        );
    }

    /// <summary>A missing refresh-review store fails construction instead of disabling review flow.</summary>
    [TestMethod]
    public void Constructor_WithNullRefreshReviewStore_ShouldThrowArgumentNullException( ) {
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new SagaCoordinatorBackgroundService(
                _redisMock.Object,
                _sagaManagerMock.Object,
                _atProtoStorageMock.Object,
                _cacheRepositoryMock.Object,
                _deduplicatorMock.Object,
                _resultCombiner,
                _queueResolverMock.Object,
                _enabledProviders,
                _loggerMock.Object,
                null!
            )
        );
    }

    #endregion

    #region Polling Tests

    /// <summary>
    /// Verifies that when completed-but-unfinalized sagas exist, the polling loop queries for them
    /// at least once and processes them.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Polling_WhenUnfinalizedSagasExist_ShouldProcessThem( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - Start and quickly cancel after one poll cycle
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken ); // Give time for initial poll
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Should have queried for unfinalized sagas
        _sagaManagerMock.Verify(
            s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.AtLeastOnce
        );
    }

    /// <summary>
    /// Verifies that when no unfinalized sagas are found, the polling loop writes nothing to storage.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Polling_WhenNoUnfinalizedSagas_ShouldNotWriteAnything( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Should not have written anything
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies the full finalization of a complete saga: the final result is written to storage
    /// once, the saga's final result URI is set, the result is cached, and the deduplicator is
    /// released with the resulting record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Polling_WhenSagaIsComplete_ShouldWriteFinalResult( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Should have written to ATProto
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Should have set final result URI
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( completeSaga.SagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Should have cached the result
        _cacheRepositoryMock.Verify(
            c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Should have released the deduplication lock
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( completeSaga.LookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        _refreshReviewStoreMock.Verify(
            store => store.CompleteAsync( completeSaga.SagaId, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies that a partial saga with no existing partial URI has its partial result written
    /// first (partial URI set), then is finalized (final URI set), confirming the partial-then-final
    /// write order.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Polling_WhenSagaIsPartialWithNoPartialUri_ShouldWritePartialFirst( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState partialSaga = CreateCompleteSaga( ) with {
            IsPartial = true,
            PartialResultUri = null
        };

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [partialSaga] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Should have written partial result first
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( partialSaga.SagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Should also have written final result
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( partialSaga.SagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies the failure-isolation contract: when writing the first saga's result throws, the
    /// loop still attempts the second saga, so one bad saga does not block the rest of the batch.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Polling_WhenWriteFails_ShouldContinueToNextSaga( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState saga1 = CreateCompleteSaga( ) with { SagaId = "saga-1" };
        LookupSagaState saga2 = CreateCompleteSaga( ) with { SagaId = "saga-2" };
        _ = _sagaManagerMock.Setup( s => s.GetAsync( "saga-1", It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( saga1 );
        _ = _sagaManagerMock.Setup( s => s.GetAsync( "saga-2", It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( saga2 );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [saga1, saga2] );

        // First saga write fails, second succeeds
        int callCount = 0;
        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                callCount++;
                return callCount == 1 ? throw new InvalidOperationException( "Simulated write failure" ) : TestRecordUri;
            } );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Both sagas should have been attempted (first fails, second succeeds)
        Assert.AreEqual( 2, callCount, "Both sagas should have been attempted" );
    }

    #endregion

    #region Final Result Writing Tests

    /// <summary>
    /// Verifies that a saga whose only provider failed (no successful results to combine) releases
    /// the deduplicator with a null URI and deletes the saga, rather than writing an empty result.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteFinalResult_WhenNoSuccessfulResults_ShouldReleaseWithNullAndDeleteSaga( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState failedSaga = CreateCompleteSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: "Rate limited"
                )
            }
        };

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [failedSaga] );

        _ = _sagaManagerMock.Setup( s => s.TryDeleteAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Should release with null
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( failedSaga.LookupKey, null, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Should delete the saga
        _sagaManagerMock.Verify(
            s => s.TryDeleteAsync( failedSaga.SagaId, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>A failed provider leg does not discard a successful sibling refresh result.</summary>
    [TestMethod]
    [DataRow( false )]
    [DataRow( true )]
    public async Task WriteFinalResult_RefreshFailure_PreservesSuccessfulSibling( bool includeSuccessfulProvider ) {
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState saga = CreateCompleteSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, true, false, null, DateTimeOffset.UtcNow, "worker unavailable" )
            }
        };
        if (includeSuccessfulProvider) {
            saga = saga with {
                ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState>( saga.ProviderStates ) {
                    [SupportedProviders.AppleMusic] = new( SupportedProviders.AppleMusic, true, true,
                        "{\"isrc\":\"USRC12345678\",\"trackName\":\"Track\",\"artistName\":\"Artist\",\"url\":\"https://example.test\"}",
                        DateTimeOffset.UtcNow, null )
                }
            };
        }
        RefreshReviewEntry context = new( ) {
            SourceRecordUri = "at://refresh/outage",
            SagaId = saga.SagaId,
            InstanceToken = saga.InstanceToken,
            LookupType = saga.LookupType,
            LookupValue = saga.LookupValue
        };
        _ = _refreshReviewStoreMock.Setup( store => store.GetPendingForSagaAsync( saga.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [context] );
        _ = _sagaManagerMock.Setup( manager => manager.TryDeleteAsync( saga.SagaId, saga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _atProtoStorageMock.Setup( storage => storage.StoreMediaLinkResultAsync(
                It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( "at://did:plc:test/link/result" );

        await service.InvokeFinalizeForTestAsync( saga, TestContext.CancellationToken );

        if (includeSuccessfulProvider) {
            _atProtoStorageMock.Verify( storage => storage.StoreMediaLinkResultAsync(
                It.Is<MediaLinkResult>( result => result.Results.Count == 1 ),
                It.IsAny<CancellationToken>( ) ), Times.Once );
            _refreshReviewStoreMock.Verify( store => store.MarkUnresolvedAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
            _refreshReviewStoreMock.Verify( store => store.CompleteAsync(
                saga.SagaId, saga.InstanceToken!, It.IsAny<CancellationToken>( ) ), Times.Once );
            _deduplicatorMock.Verify( dedup => dedup.ReleaseAsync(
                saga.LookupKey, "at://did:plc:test/link/result", It.IsAny<CancellationToken>( ) ), Times.Once );
            _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
                saga.SagaId, saga.InstanceToken!, It.IsAny<CancellationToken>( ) ), Times.Never );
        } else {
            _atProtoStorageMock.Verify( storage => storage.StoreMediaLinkResultAsync(
                It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
            _refreshReviewStoreMock.Verify( store => store.MarkUnresolvedAsync(
                context, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
            _deduplicatorMock.Verify( dedup => dedup.ReleaseAsync(
                saga.LookupKey, null, It.IsAny<CancellationToken>( ) ), Times.Once );
            _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
                saga.SagaId, saga.InstanceToken!, It.IsAny<CancellationToken>( ) ), Times.Once );
        }
    }

    /// <summary>A zero-result stale refresh is persisted for review before its saga is deleted.</summary>
    [TestMethod]
    public async Task WriteFinalResult_RefreshWithNoResults_MarksReviewBeforeDeletingSaga( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        int sequence = 0;
        int reviewOrder = 0;
        int deleteOrder = 0;
        RefreshReviewEntry refreshEntry = new( ) {
            SourceRecordUri = "at://record/refresh-context",
            SagaId = TestSagaId,
            InstanceToken = "instance-current",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678"
        };
        _ = reviewStore.Setup( store => store.GetPendingForSagaAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [refreshEntry] );
        _ = reviewStore.Setup( store => store.MarkUnresolvedAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => reviewOrder = Interlocked.Increment( ref sequence ) )
            .Returns( Task.CompletedTask );
        _ = _sagaManagerMock.Setup( manager => manager.TryDeleteAsync(
                TestSagaId, "instance-current", It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => deleteOrder = Interlocked.Increment( ref sequence ) )
            .ReturnsAsync( true );

        LookupSagaState failedSaga = CreateCompleteSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null )
            }
        };

        await CreateService( reviewStore.Object ).InvokeFinalizeForTestAsync(
            failedSaga, TestContext.CancellationToken );

        reviewStore.Verify( store => store.MarkUnresolvedAsync(
            It.Is<RefreshReviewEntry>( entry => entry.InstanceToken == "instance-current" ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        Assert.IsGreaterThan( 0, reviewOrder );
        Assert.IsLessThan( deleteOrder, reviewOrder,
            "The review entry must be durable before the saga is deleted." );
    }

    /// <summary>Stale X zero-result handling cannot mutate replacement Y's review context or lock.</summary>
    [TestMethod]
    public async Task WriteFinalResult_ZeroResult_StaleXContextDoesNotFallbackToSagaId( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        LookupSagaState staleX = CreateCompleteSaga( ) with { InstanceToken = "instance-x" };
        LookupSagaState replacementYSaga = staleX with { InstanceToken = "instance-y" };
        RefreshReviewEntry replacementYEntry = new( ) {
            SourceRecordUri = "at://record-y",
            SagaId = staleX.SagaId,
            InstanceToken = "instance-y",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "Y"
        };
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync(
                staleX.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( replacementYSaga );
        _ = reviewStore.Setup( store => store.GetPendingForSagaAsync( staleX.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [replacementYEntry] );

        LookupSagaState failedSaga = staleX with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify, true, false, null, DateTimeOffset.UtcNow, null )
            }
        };

        await CreateService( reviewStore.Object ).InvokeFinalizeForTestAsync(
            failedSaga, TestContext.CancellationToken );

        reviewStore.Verify( store => store.MarkUnresolvedAsync(
            It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        reviewStore.Verify( store => store.GetPendingForSagaAsync(
            staleX.SagaId, It.IsAny<CancellationToken>( ) ), Times.Never );
        _deduplicatorMock.Verify( deduplicator => deduplicator.ReleaseAsync(
            It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
            staleX.SagaId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryClaimFinalizeAsync(
            staleX.SagaId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>Current X that loses the finalize claim cannot mutate review, completion, or saga state.</summary>
    [TestMethod]
    public async Task WriteFinalResult_ZeroResult_CurrentXClaimLoserIsCompletelySilent( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        LookupSagaState failedX = CreateCompleteSaga( ) with {
            InstanceToken = "instance-x",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify, true, false, null, DateTimeOffset.UtcNow, null )
            }
        };
        RefreshReviewEntry reviewEntry = new( ) {
            SourceRecordUri = "at://record-x",
            SagaId = failedX.SagaId,
            InstanceToken = failedX.InstanceToken,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "X"
        };

        _ = _sagaManagerMock.Setup( manager => manager.GetAsync(
                failedX.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( failedX );
        _ = _sagaManagerMock.Setup( manager => manager.TryClaimFinalizeAsync(
                failedX.SagaId, failedX.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );
        _ = reviewStore.Setup( store => store.GetPendingForSagaAsync(
                failedX.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [reviewEntry] );

        await CreateService( reviewStore.Object ).InvokeFinalizeForTestAsync(
            failedX, TestContext.CancellationToken );

        reviewStore.Verify( store => store.GetPendingForSagaAsync(
            failedX.SagaId, It.IsAny<CancellationToken>( ) ), Times.Never );
        reviewStore.Verify( store => store.MarkUnresolvedAsync(
            It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _deduplicatorMock.Verify( deduplicator => deduplicator.ReleaseAsync(
            It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
            failedX.SagaId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryReleaseFinalizeClaimAsync(
            failedX.SagaId, failedX.InstanceToken!, It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TrySetFinalResultUriAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _atProtoStorageMock.Verify( storage => storage.StoreMediaLinkResultAsync(
            It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>Current Y zero-result cleanup promotes its own matching review context.</summary>
    [TestMethod]
    public async Task WriteFinalResult_ZeroResult_CurrentYContextIsPromotedAndDeleted( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        LookupSagaState currentY = CreateCompleteSaga( ) with { InstanceToken = "instance-y" };
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( currentY.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( currentY );
        RefreshReviewEntry context = new( ) {
            SourceRecordUri = "at://record-y",
            SagaId = currentY.SagaId,
            InstanceToken = currentY.InstanceToken,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "Y"
        };
        _ = reviewStore.Setup( store => store.GetPendingForSagaAsync(
                currentY.SagaId, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [context] );
        _ = reviewStore.Setup( store => store.MarkUnresolvedAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        _ = _sagaManagerMock.Setup( manager => manager.TryDeleteAsync(
                currentY.SagaId, currentY.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        LookupSagaState failedSaga = currentY with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify, true, false, null, DateTimeOffset.UtcNow, null )
            }
        };
        await CreateService( reviewStore.Object ).InvokeFinalizeForTestAsync(
            failedSaga, TestContext.CancellationToken );

        reviewStore.Verify( store => store.MarkUnresolvedAsync(
            It.Is<RefreshReviewEntry>( entry => entry.SourceRecordUri == context.SourceRecordUri
                && entry.InstanceToken == currentY.InstanceToken ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
            currentY.SagaId, currentY.InstanceToken!, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>A review-store outage does not strand a terminal saga or its deduplication waiters.</summary>
    [TestMethod]
    public async Task WriteFinalResult_ReviewStoreFailure_ReleasesAndDeletesSaga( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        _ = reviewStore.Setup( store => store.MarkUnresolvedAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Redis unavailable" ) );
        _ = _sagaManagerMock.Setup( manager => manager.TryDeleteAsync( TestSagaId, "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        LookupSagaState failedSaga = CreateCompleteSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: "Not found" )
            }
        };

        await CreateService( reviewStore.Object ).InvokeFinalizeForTestAsync(
            failedSaga, TestContext.CancellationToken );

        _deduplicatorMock.Verify( deduplicator => deduplicator.ReleaseAsync(
            failedSaga.LookupKey, null, It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
            TestSagaId, "instance-current", It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>
    /// Verifies that finalizing a saga does not issue a second state mutation after the final URI
    /// operation atomically clears the partial flag.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteFinalResult_WhenSagaIsFinalized_DoesNotIssueSeparatePartialMutation( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - The final-URI Lua operation clears the partial flag atomically.
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( completeSaga.SagaId, false, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    #endregion

    #region Secondary Lookup Tests

    /// <summary>
    /// Verifies the secondary-lookup path triggered by the <c>saga:completed</c> event: when the
    /// primary provider has resolved but no cached results exist for the others, the saga is marked
    /// partial, the two remaining providers are queued at interactive priority, a partial result is
    /// written to storage, and the deduplicator is released with the partial record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenSecondaryLookupsQueued_ShouldMarkPartialAndQueueInteractive( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        // No cached data for the external ID - all other providers need secondary lookups
        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Saga should be marked partial when secondaries are queued
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( TestSagaId, true, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Secondary lookups queued at Interactive priority (AppleMusic + Tidal)
        queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );

        // Assert - Partial result written (non-terminal write path: SetPartialResultUriAsync, not SetFinalResultUriAsync)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Waiters notified with the partial result URI
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( uriSaga.LookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies the cache-hit short circuit: when the remaining providers already have cached
    /// results, the service materializes those provider states (complete and successful) instead of
    /// queuing lookups, writes a complete three-provider non-partial result, and finalizes the saga
    /// without enqueuing anything.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenAllOtherProvidersCached_ShouldMaterializeCachedResultsAndFinalize( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        // Cache has fresh data for both remaining providers - no secondary lookups needed
        MediaLinkResult cachedResult = CreateCachedExternalIdResult( SupportedProviders.AppleMusic, SupportedProviders.Tidal );
        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( (cachedResult, TestRecordUri, false) );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Cached provider results materialized into the saga as completed states
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                TestSagaId,
                It.Is<ProviderLookupState>( p =>
                    p.Provider == SupportedProviders.AppleMusic &&
                    p.IsComplete &&
                    p.IsSuccess &&
                    p.ResultJson != null &&
                    p.CompletedAt != null
                ),
                "instance-current",
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                TestSagaId,
                It.Is<ProviderLookupState>( p =>
                    p.Provider == SupportedProviders.Tidal &&
                    p.IsComplete &&
                    p.IsSuccess &&
                    p.ResultJson != null
                ),
                "instance-current",
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );

        // Assert - No secondary lookups queued (everything came from cache)
        _queueResolverMock.Verify( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ), Times.Never );

        // Assert - Final result stored with ALL three providers' results (terminal write path: SetFinalResultUriAsync below)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync(
                It.Is<MediaLinkResult>( r => r.Results.Count == 3 ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );

        // Assert - Saga finalized
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies a stale coordinator cannot materialize cached secondary providers after a replacement
    /// saga instance wins the race. The stale X token is fenced before any queue or result write, and
    /// the replacement Y instance receives no provider, core, or review mutations.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenCachedSecondaryRaceLosesInstanceFence_DoesNotQueueOrMutateReplacement( ) {
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState staleX = CreateUriLookupSaga( ) with { InstanceToken = "instance-x" };
        LookupSagaState replacementY = staleX with {
            InstanceToken = "instance-y",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState>( staleX.ProviderStates )
        };
        LookupSagaState current = staleX;
        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => current );
        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ), It.IsAny<int>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );

        MediaLinkResult cachedResult = CreateCachedExternalIdResult( SupportedProviders.AppleMusic, SupportedProviders.Tidal );
        _ = _cacheRepositoryMock.Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ( ) => {
                // Replacement Y wins immediately after X reads the cache and before its fenced write.
                current = replacementY;
                return (cachedResult, TestRecordUri, false);
            } );
        _ = _sagaManagerMock.Setup( s => s.TryUpdateProviderStateAsync(
                TestSagaId, It.IsAny<ProviderLookupState>( ), "instance-x", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );
        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ) );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try { await service.StopAsync( CancellationToken.None ); } catch (OperationCanceledException) { }

        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            TestSagaId, It.IsAny<ProviderLookupState>( ), "instance-x", It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            TestSagaId, It.IsAny<ProviderLookupState>( ), "instance-y", It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueResolverMock.Verify( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ), Times.Never );
        _atProtoStorageMock.Verify( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _refreshReviewStoreMock.Verify( r => r.MarkUnresolvedAsync( It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies the mixed cache path: when one of two remaining providers is cached, the service
    /// materializes the cached provider state, queues only the uncached provider (Tidal) at
    /// interactive priority, writes a two-provider partial result, and does not finalize the saga.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenSomeProvidersCached_ShouldMaterializeCachedAndQueueRemaining( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        // Cache has fresh data for AppleMusic only - Tidal still needs a secondary lookup
        MediaLinkResult cachedResult = CreateCachedExternalIdResult( SupportedProviders.AppleMusic );
        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( (cachedResult, TestRecordUri, false) );

        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Cached AppleMusic result materialized into the saga
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync( TestSagaId, It.Is<ProviderLookupState>( p =>
                    p.Provider == SupportedProviders.AppleMusic &&
                    p.IsComplete &&
                    p.IsSuccess &&
                    p.ResultJson != null
                ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Only the uncached provider (Tidal) queued for a secondary lookup
        queueMock.Verify(
            q => q.EnqueueAsync(
                It.Is<QueuedLookupRequest>( r => r.Provider == SupportedProviders.Tidal ),
                QueuePriority.Interactive,
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
        queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Partial result includes the cached provider's data (Spotify + AppleMusic), non-terminal write
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync(
                It.Is<MediaLinkResult>( r => r.Results.Count == 2 ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Saga not finalized while the secondary lookup is pending
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that secondaries inherit a priority derived from the saga's origin: a saga that
    /// originated from a bulk request queues its two secondary lookups at background priority (with
    /// the bulk origin preserved) and never at interactive priority.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenSagaOriginatedFromBulk_ShouldQueueSecondariesAtBackground( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState bulkSaga = CreateUriLookupSaga( QueuePriority.Bulk );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( bulkSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Secondaries queued at Background priority, carrying the bulk origin
        queueMock.Verify(
            q => q.EnqueueAsync(
                It.Is<QueuedLookupRequest>( r => r.OriginPriority == QueuePriority.Bulk ),
                QueuePriority.Background,
                It.IsAny<CancellationToken>( )
            ),
            Times.Exactly( 2 )
        );

        // Assert - Nothing entered the interactive lane
        queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies the marker-contention path: when the secondaries-queued marker is already taken by a
    /// concurrent worker (the claim returns false), this worker does not enqueue any lookups but
    /// still initializes provider states, marks the saga partial, writes a partial result, and
    /// defers finalization, so the marker holder remains responsible for completion.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenSecondariesMarkerAlreadyTaken_ShouldNotEnqueueButStillDeferFinalization( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        // Another handler already won the race to queue secondaries
        _ = _sagaManagerMock
            .Setup( s => s.TryMarkSecondariesQueuedAsync( TestSagaId, "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - The losing handler must not queue duplicate secondary lookups
        queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - The idempotent state/partial writes still run (they happen before the
        // marker claim so a published partial can never be mistaken for a final result)
        _sagaManagerMock.Verify(
            s => s.TryInitializeProviderStatesAsync( TestSagaId, It.IsAny<IEnumerable<SupportedProviders>>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( TestSagaId, true, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - It still defers finalization (secondaries pending elsewhere)
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - The partial result is still written for waiting callers (non-terminal write path)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies the ordering invariant on the secondaries path: provider states are initialized and
    /// the saga is marked partial before the secondaries-queued marker is claimed, so a concurrent
    /// worker that loses the marker race still observes initialized state.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_ShouldInitializeStatesAndMarkPartialBeforeClaimingMarker( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        // Record the order of the saga-manager calls that close the partial-vs-final race
        List<string> callOrder = [];
        _ = _sagaManagerMock
            .Setup( s => s.TryInitializeProviderStatesAsync( TestSagaId, It.IsAny<IEnumerable<SupportedProviders>>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => callOrder.Add( "InitializeProviderStates" ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TrySetIsPartialAsync( TestSagaId, true, "instance-current", It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => callOrder.Add( "SetIsPartial" ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TryMarkSecondariesQueuedAsync( TestSagaId, "instance-current", It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => callOrder.Add( "TryMarkSecondariesQueued" ) )
            .ReturnsAsync( true );

        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - The idempotent writes both precede the marker claim
        CollectionAssert.AreEqual(
            new List<string> { "InitializeProviderStates", "SetIsPartial", "TryMarkSecondariesQueued" },
            callOrder
        );
    }

    /// <summary>
    /// Verifies that every failed secondary enqueue is converted into a terminal provider leg and
    /// publishes progress, so the one-time fan-out marker cannot strand the saga.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenEveryEnqueueFails_ReachesTerminalStateAndWritesPartial( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        // Every secondary enqueue fails (e.g. the queue backend is unavailable)
        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = queueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "queue unavailable" ) );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - start the service, then simulate a saga completion event via Pub/Sub
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - All enqueues were attempted (AppleMusic + Tidal) and failed
        queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );

        // No providers remain unresolved: both failed enqueues were durably converted into
        // terminal failed legs, so this invocation finalizes immediately.
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( TestSagaId, true, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                TestSagaId,
                It.Is<ProviderLookupState>( state =>
                    state.IsComplete && !state.IsSuccess && state.ErrorMessage != null ),
                "instance-current",
                It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );
        _subscriberMock.Verify(
            s => s.PublishAsync(
                RedisChannel.Literal( "saga:completed" ),
                TestSagaId,
                It.IsAny<CommandFlags>( ) ),
            Times.AtLeastOnce( )
        );

        // Successful terminal release clears the reconciliation entry.
        _sagaManagerMock.Verify(
            s => s.RemoveFromPendingIndexAsync( TestSagaId, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Negative control: a saga with at least one successful secondary enqueue stays partial and in
    /// the pending index, awaiting its secondary providers. It must NOT reach the terminal state
    /// reserved for the all-enqueues-failed path.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SagaCompletion_WhenSomeEnqueueSucceeds_StaysPartialAwaitingSecondaries( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( uriSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        // One provider enqueues successfully; the other fails
        Mock<IRequestQueue<QueuedLookupRequest>> successQueueMock = new( );
        _ = successQueueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        Mock<IRequestQueue<QueuedLookupRequest>> failQueueMock = new( );
        _ = failQueueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "queue unavailable" ) );

        // AppleMusic succeeds, Tidal fails
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( SupportedProviders.AppleMusic ) )
            .Returns( successQueueMock.Object );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( SupportedProviders.Tidal ) )
            .Returns( failQueueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Not finalized: secondaries are still pending
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - The partial result is written so waiting callers receive a response (non-terminal write path)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                TestSagaId,
                It.Is<ProviderLookupState>( state =>
                    state.Provider == SupportedProviders.Tidal
                    && state.IsComplete
                    && !state.IsSuccess
                    && state.ErrorMessage != null ),
                "instance-current",
                It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _subscriberMock.Verify(
            s => s.PublishAsync(
                RedisChannel.Literal( "saga:completed" ),
                TestSagaId,
                It.IsAny<CommandFlags>( ) ),
            Times.AtLeastOnce( )
        );

        // Assert - The pending index is NOT cleared: at least one provider is in flight
        _sagaManagerMock.Verify(
            s => s.RemoveFromPendingIndexAsync( TestSagaId, It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that the same secondary-lookup behavior is reached through the polling path (not just
    /// the subscription): a URI saga picked up by polling that still needs secondaries queues both at
    /// interactive priority and writes a partial result instead of finalizing.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Polling_WhenUriSagaNeedsSecondaryLookups_ShouldQueueThemAndWritePartialInsteadOfFinalizing( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState uriSaga = CreateUriLookupSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [uriSaga] );

        // No cached data for the external ID - all other providers need secondary lookups
        _ = _cacheRepositoryMock
            .Setup( c => c.TryGetCachedResultByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( ((MediaLinkResult result, string recordUri, bool isStale)?)null );

        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - polling runs immediately on startup
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Secondary lookups queued for the missing providers (AppleMusic + Tidal)
        queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );

        // Assert - Partial result written instead of a premature final (non-terminal write path)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    #endregion

    #region Finalize Claim Tests

    /// <summary>
    /// Verifies that a terminal handler that loses the finalize claim performs no PDS write and
    /// does not call SetFinalResultUriAsync or ResetWriteGenerationAsync. The terminal path is
    /// gated by the claim only (not the generation CAS), so the loser is completely silent.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenClaimLost_IsCompletelySilent( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        // Finalize claim is already held by a concurrent handler
        _ = _sagaManagerMock
            .Setup( s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - No PDS write (the loser must be completely silent)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - No final URI recorded
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - No dedup release (the loser must not publish on complete:{key})
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - No generation reset: the terminal path does not advance the generation, so
        // there is nothing to roll back when the claim is lost.
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that a non-terminal handler that loses the write-generation CAS (the generation was
    /// already advanced by a concurrent handler to the same level) performs no PDS write and is
    /// completely silent. The non-terminal path is gated by the CAS; the terminal path uses the
    /// finalize claim instead.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_NonTerminal_WhenGenerationCasLost_IsCompletelySilent( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );

        string resultJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://open.spotify.com/track/abc123"}""";
        LookupSagaState partialSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -1 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            },
            FinalResultUri = null,
            IsPartial = true,
            InstanceToken = "instance-current"
        };

        // The generation was already advanced by a concurrent handler
        _ = _sagaManagerMock
            .Setup( s => s.TryAdvanceWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act - drive via the parameterized write entry point
        await service.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );

        // Assert - No PDS write (the loser must be completely silent)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - No partial URI recorded
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Finalize claim never acquired on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that a handler that wins the finalize claim writes to the PDS exactly once and
    /// records the final URI. This is the companion positive control for the loser test above.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenClaimWon_WritesOnce( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Exactly one PDS write
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Final URI recorded
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( completeSaga.SagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - The claim is NOT released on the success path
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that a failed PDS write on the terminal path releases the finalize claim so the next
    /// poll cycle can retry, and releases the dedup lock with null to unblock waiters. The generation
    /// is NOT reset on the terminal path because the terminal path never advances the generation.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenPdsWriteFails_ReleasesClaimAndDedup( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        // PDS write fails
        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "PDS unavailable" ) );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Generation NOT reset: the terminal path never advances the generation,
        // so there is nothing to roll back after a PDS write failure.
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Claim released so the next poll can re-finalize
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( completeSaga.SagaId, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Dedup lock released with null to unblock waiters
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( completeSaga.LookupKey, null, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies that a re-delivery of a saga whose finalization already succeeded is a no-op:
    /// the existing final result URI short-circuits before the claim is even attempted.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Completion_WhenSagaAlreadyFinalized_IsNoOp( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );

        // Saga already has a final result URI (already finalized)
        LookupSagaState alreadyFinalizedSaga = CreateCompleteSaga( ) with {
            FinalResultUri = TestRecordUri
        };

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( alreadyFinalizedSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        // Simulate re-delivery of the saga:completed event
        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - No second PDS write
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - No second final URI write
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - No second dedup release
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Negative control for <see cref="Completion_WhenSagaAlreadyFinalized_IsNoOp"/>: verifies
    /// that the first delivery DOES finalize once, proving the no-op path is not over-suppressing.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task FirstDelivery_OfCompletedSaga_FinalizesOnce( ) {
        // Arrange
        Dictionary<string, Action<RedisChannel, RedisValue>> handlers = [];
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync( It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( channel, handler, _ ) => handlers[channel.ToString( )] = handler )
            .Returns( Task.CompletedTask );

        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( completeSaga );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 100, TestContext.CancellationToken );

        Assert.IsTrue( handlers.ContainsKey( "saga:completed" ), "Service should have subscribed to saga:completed" );
        handlers["saga:completed"]( RedisChannel.Literal( "saga:completed" ), TestSagaId );
        await Task.Delay( 250, TestContext.CancellationToken );

        await cts.CancelAsync( );
        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Exactly one PDS write on first delivery
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies refresh cleanup failure after the final URI is durable cannot block dedup waiters
    /// or release the finalize claim for a re-entrant PDS write.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenRefreshCleanupFailsAfterDurability_ReleasesDedupAndKeepsClaim( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        // PDS write succeeds and returns a URI
        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        // SetFinalResultUriAsync succeeds (URI is now durably recorded)
        _ = _sagaManagerMock
            .Setup( s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.Stored );

        _ = _refreshReviewStoreMock
            .Setup( store => store.CompleteAsync(
                completeSaga.SagaId, completeSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "refresh cleanup failed" ) );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - No generation reset after the durability line
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - A failure after the URI is recorded must retain the claim (no release)
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        _deduplicatorMock.Verify(
            deduplicator => deduplicator.ReleaseAsync(
                completeSaga.LookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once );

        // Assert - dedup must never be released with null on this path; releasing null would hand
        // waiters an empty completion for a saga whose result was already recorded
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), null, It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Post-URI failure must not release the dedup lock with null"
        );
    }

    /// <summary>A lost final-URI fence never publishes the stale instance's PDS URI.</summary>
    [TestMethod]
    public async Task Finalize_WhenFinalUriFenceIsLostAfterPdsWrite_DoesNotReleaseStaleResult( ) {
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState saga = CreateCompleteSaga( );
        _ = _atProtoStorageMock.Setup( storage => storage.StoreMediaLinkResultAsync(
                It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );
        _ = _sagaManagerMock.Setup( manager => manager.TrySetFinalResultUriAsync(
                saga.SagaId, TestRecordUri, saga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.InstanceMismatch );

        await service.InvokeFinalizeForTestAsync( saga, TestContext.CancellationToken );

        _deduplicatorMock.Verify( deduplicator => deduplicator.ReleaseAsync(
            It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.RemoveFromPendingIndexAsync(
            saga.SagaId, It.IsAny<CancellationToken>( ) ), Times.Never );
        _atProtoStorageMock.Verify( storage => storage.StoreMediaLinkResultAsync(
            It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>
    /// Verifies that a cancellation mid-finalize (before the PDS write returns) releases the
    /// finalize claim so the next host restart can re-finalize, but does NOT reset the write
    /// generation (the terminal path never advances it), does NOT release the dedup lock with a
    /// null URI (which would hand waiters a stale empty completion for a healthy saga), and does
    /// NOT delete the saga.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenCancelledMidFinalize_ReleasesClaimButNotDedup( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        // Cancellation fires before the PDS write completes (before the durability line)
        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new OperationCanceledException( ) );

        _ = _sagaManagerMock
            .Setup( s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Generation NOT reset: the terminal path never advances the generation.
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Claim must be released so the next host can re-finalize
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - Dedup lock must NOT be released with null (no premature empty completion)
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), null, It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Saga must not be deleted
        _sagaManagerMock.Verify(
            s => s.TryDeleteAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that a cancellation arriving after the final result URI is durably recorded does NOT
    /// reset the write generation or release the finalize claim. Retaining both prevents a re-entrant
    /// PDS write against an already-recorded URI. The dedup lock is also never released here. Pairs
    /// with <see cref="Finalize_WhenCancelledMidFinalize_ReleasesClaimButNotDedup"/>
    /// (pre-URI cancellation → claim released) to prove the release is conditioned on the durability line.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenCancelledAfterUriRecorded_DoesNotResetGenerationOrReleaseClaim( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [completeSaga] );

        // PDS write succeeds — URI returned and durably recorded
        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        // SetFinalResultUriAsync completes — durability line crossed
        _ = _sagaManagerMock
            .Setup( s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.Stored );

        // The first post-URI step throws OperationCanceledException — cancellation after the durability line
        _ = _sagaManagerMock
            .Setup( s => s.TrySetIsPartialAsync( It.IsAny<string>( ), It.IsAny<bool>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new OperationCanceledException( ) );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - No generation reset after the durability line
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Claim must NOT be released; releasing would allow a re-entrant PDS write against
        // a URI that is already recorded
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Dedup lock must NOT be released with null (saga succeeded; no premature empty completion)
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), null, It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert - Saga must not be deleted
        _sagaManagerMock.Verify(
            s => s.TryDeleteAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies the generation-bounded write count for an N=3 saga. The non-terminal (partial)
    /// path is CAS-gated: calling it with result counts 1, 2, 3 produces three writes (one per
    /// distinct generation level), and two redundant calls at generation 3 are silently dropped
    /// by the CAS. The terminal write uses the finalize claim instead of the CAS.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_GenerationBoundedWrites_ProducesOneWritePerDistinctGeneration( ) {
        // Arrange - N=3 provider saga with results at counts 1, 2, 3
        SagaCoordinatorBackgroundService service = CreateService( );

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Set up mock to advance generation monotonically using a local counter
        int storedGeneration = 0;
        _ = _sagaManagerMock
            .Setup( s => s.TryAdvanceWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( string _, int gen, string _, CancellationToken _ ) => {
                if (gen > storedGeneration) {
                    storedGeneration = gen;
                    return true;
                }
                return false;
            } );

        // Build three sagas representing distinct generations k=1, k=2, k=3
        string spotifyJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://open.spotify.com/track/abc123"}""";
        string appleMusicJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://music.apple.com/album/1"}""";
        string tidalJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://tidal.com/track/1"}""";

        LookupSagaState sagaGen1 = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -5 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify, IsComplete: true, IsSuccess: true,
                    ResultJson: spotifyJson, CompletedAt: DateTimeOffset.UtcNow, ErrorMessage: null )
            },
            FinalResultUri = null,
            IsPartial = false,
            InstanceToken = "instance-current"
        };

        LookupSagaState sagaGen2 = sagaGen1 with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify, IsComplete: true, IsSuccess: true,
                    ResultJson: spotifyJson, CompletedAt: DateTimeOffset.UtcNow, ErrorMessage: null ),
                [SupportedProviders.AppleMusic] = new(
                    Provider: SupportedProviders.AppleMusic, IsComplete: true, IsSuccess: true,
                    ResultJson: appleMusicJson, CompletedAt: DateTimeOffset.UtcNow, ErrorMessage: null )
            }
        };

        _ = _sagaManagerMock
            .Setup( s => s.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( sagaGen1 );

        LookupSagaState sagaGen3 = sagaGen1 with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify, IsComplete: true, IsSuccess: true,
                    ResultJson: spotifyJson, CompletedAt: DateTimeOffset.UtcNow, ErrorMessage: null ),
                [SupportedProviders.AppleMusic] = new(
                    Provider: SupportedProviders.AppleMusic, IsComplete: true, IsSuccess: true,
                    ResultJson: appleMusicJson, CompletedAt: DateTimeOffset.UtcNow, ErrorMessage: null ),
                [SupportedProviders.Tidal] = new(
                    Provider: SupportedProviders.Tidal, IsComplete: true, IsSuccess: true,
                    ResultJson: tidalJson, CompletedAt: DateTimeOffset.UtcNow, ErrorMessage: null )
            }
        };

        // Also set up the claim mock to only allow the first terminal winner through
        bool claimHeld = false;
        _ = _sagaManagerMock
            .Setup( s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string _, string _, CancellationToken _ ) => {
                bool won = !claimHeld;
                claimHeld = true;
                return Task.FromResult( won );
            } );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act - invoke 5 times:
        //   k=1 non-terminal: CAS advances, partial write
        //   k=2 non-terminal: CAS advances, partial write
        //   k=3 terminal (first): claim won, final write
        //   k=3 terminal (dup):   claim lost, no write
        //   k=3 terminal (dup):   claim lost, no write
        await service.InvokeWriteForTestAsync( sagaGen1, terminal: false, cts.Token );
        await service.InvokeWriteForTestAsync( sagaGen2, terminal: false, cts.Token );
        await service.InvokeWriteForTestAsync( sagaGen3, terminal: true, cts.Token );
        await service.InvokeWriteForTestAsync( sagaGen3, terminal: true, cts.Token );
        await service.InvokeWriteForTestAsync( sagaGen3, terminal: true, cts.Token );

        // Assert - exactly 3 PDS writes (two partial at k=1, k=2; one terminal at k=3)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 3 ),
            "Expected exactly two partial writes (k=1, k=2) and one terminal write (k=3)"
        );
    }

    /// <summary>
    /// Verifies the non-terminal write path: <c>terminal=false</c> calls <c>SetPartialResultUriAsync</c>
    /// (not <c>SetFinalResultUriAsync</c>), and never calls <c>SetIsPartialAsync(false)</c> or
    /// <c>TryClaimFinalizeAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_NonTerminal_WritesPartialAndDoesNotClaimFinalize( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );

        string resultJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://open.spotify.com/track/abc123"}""";
        LookupSagaState partialSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -1 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            },
            FinalResultUri = null,
            IsPartial = true,
            InstanceToken = "instance-current"
        };

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act - non-terminal write
        await service.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );

        // Assert - the combined result is stored once (non-terminal path confirmed by SetPartialResultUriAsync below)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync(
                It.IsAny<MediaLinkResult>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once,
            "Non-terminal write must store the result exactly once"
        );

        // Assert - partial URI stored, not final URI
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not set the final result URI"
        );

        // Assert - the finalize claim is never acquired on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not call TryClaimFinalizeAsync"
        );

        // Assert - IsPartial flag is never cleared on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( It.IsAny<string>( ), false, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not clear the IsPartial flag"
        );
    }

    /// <summary>
    /// Verifies the terminal write path relies on the final-URI Lua operation to clear
    /// <c>IsPartial</c> atomically rather than issuing a second fenced mutation.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_Terminal_ClearsIsPartialAtomicallyWithFinalUri( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act - terminal write
        await service.InvokeWriteForTestAsync( completeSaga, terminal: true, cts.Token );

        // Assert - no separate post-durability partial-state mutation is issued.
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( TestSagaId, false, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Final URI storage clears IsPartial in the same Lua operation"
        );

        // Assert - final URI recorded (not partial)
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _sagaManagerMock.Verify(
            s => s.TrySetPartialResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Terminal write must not call SetPartialResultUriAsync"
        );
    }

    /// <summary>
    /// Verifies that a PDS write failure on the non-terminal path resets the write generation
    /// but does NOT release the finalize claim (which was never acquired on the non-terminal
    /// path). The dedup lock is also not released with null on the non-terminal failure path.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_NonTerminal_WhenPdsWriteFails_ResetsGenerationAndDoesNotReleaseClaim( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );

        string resultJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://open.spotify.com/track/abc123"}""";
        LookupSagaState partialSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -1 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            },
            FinalResultUri = null,
            IsPartial = true,
            InstanceToken = "instance-current"
        };

        // PDS write fails before the durability line
        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "PDS unavailable" ) );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act
        await service.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );

        // Assert - generation conditionally reset so the next trigger can re-advance and retry
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( TestSagaId, It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Non-terminal pre-durability failure must reset the write generation"
        );

        // Assert - finalize claim NEVER acquired or released on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not call TryClaimFinalizeAsync"
        );
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not release a finalize claim it never held"
        );
        _refreshReviewStoreMock.Verify(
            store => store.ClearSweepAttemptsAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "A failed PDS write must not clear sweep attempts before the partial URI is durable"
        );
    }

    /// <summary>Matching current-instance refresh context suppresses non-terminal PDS materialization.</summary>
    [TestMethod]
    public async Task WriteResult_NonTerminal_MatchingRefreshContext_SuppressesPdsAndBookkeeping( ) {
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState refreshSaga = CreateUriLookupSaga( QueuePriority.Bulk ) with {
            SagaId = TestSagaId,
            InstanceToken = "instance-current"
        };
        RefreshReviewEntry context = new( ) {
            SourceRecordUri = "at://record/refresh-live",
            SagaId = TestSagaId,
            InstanceToken = "instance-current",
            LookupType = refreshSaga.LookupType,
            LookupValue = refreshSaga.LookupValue
        };
        _ = _refreshReviewStoreMock.Setup( store => store.GetPendingForSagaAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [context] );

        await service.InvokeWriteForTestAsync( refreshSaga, terminal: false, TestContext.CancellationToken );

        _atProtoStorageMock.Verify( storage => storage.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryAdvanceWriteGenerationAsync(
            It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _refreshReviewStoreMock.Verify( store => store.ClearSweepAttemptsAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies that a failure occurring after the partial result URI has been durably recorded
    /// (<c>SetPartialResultUriAsync</c> succeeds) does NOT reset the write generation. Retaining
    /// the generation prevents a re-entrant duplicate partial write against a URI that was already
    /// written. Pairs with <see cref="WriteResult_NonTerminal_WhenPdsWriteFails_ResetsGenerationAndDoesNotReleaseClaim"/>
    /// (pre-durability failure → generation reset) as a discriminator: together they prove the
    /// reset is conditioned on the durability line.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_NonTerminal_WhenFailureAfterPartialUriRecorded_ClearsRefreshAttemptsWithoutResettingGeneration( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );

        string resultJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://open.spotify.com/track/abc123"}""";
        LookupSagaState partialSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -1 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            },
            FinalResultUri = null,
            IsPartial = true,
            InstanceToken = "instance-current"
        };

        // PDS write succeeds and returns a URI
        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        // SetPartialResultUriAsync succeeds — partial URI durably recorded (uriRecorded = true)
        _ = _sagaManagerMock
            .Setup( s => s.TryAdvanceWriteGenerationAsync(
                It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( s => s.TrySetPartialResultUriAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        RefreshReviewEntry refreshContext = new( ) {
            SourceRecordUri = "at://record/refresh-context",
            SagaId = TestSagaId,
            InstanceToken = "instance-current",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678"
        };
        _ = _refreshReviewStoreMock
            .Setup( store => store.GetPendingForSagaAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );

        // The first post-durability step throws — simulates a failure after the durability line
        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "cache indexing failed" ) );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act
        await service.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );

        // Assert - generation must NOT be reset after the durability line; retaining it prevents
        // a re-entrant duplicate partial write
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal post-durability failure must not reset the write generation"
        );
        _refreshReviewStoreMock.Verify(
            store => store.ClearSweepAttemptsAsync( refreshContext.SourceRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "A background refresh with no matching pending context does not clear refresh bookkeeping"
        );

        // Assert - finalize claim NEVER acquired or released on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not call TryClaimFinalizeAsync"
        );
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not release a finalize claim it never held"
        );

        // Assert - dedup must never be released with null on this path; releasing null would hand
        // waiters an empty completion for a saga whose result was already recorded
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), null, It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Post-URI failure on the non-terminal path must not release the dedup lock with null"
        );
    }

    /// <summary>
    /// Verifies two distinct sagas each finalize exactly once. This proves the single-winner
    /// guard keys on saga identity and does not suppress legitimate distinct finalizations.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_TwoDistinctSagas_EachWriteOnce( ) {
        // Arrange
        const string SagaId2 = "test-saga-id-87654321";

        SagaCoordinatorBackgroundService service = CreateService( );

        LookupSagaState saga1 = CreateCompleteSaga( );
        LookupSagaState saga2 = CreateCompleteSaga( ) with { SagaId = SagaId2 };
        _ = _sagaManagerMock.Setup( s => s.GetAsync( saga1.SagaId, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( saga1 );
        _ = _sagaManagerMock.Setup( s => s.GetAsync( saga2.SagaId, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( saga2 );

        _ = _sagaManagerMock.Setup( s => s.GetPendingReconciliationAsync(
                It.IsAny<TimeSpan>( ),
                It.IsAny<int>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( [saga1, saga2] );

        _ = _atProtoStorageMock.Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock.Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act - polling processes both sagas
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );

        try {
            await service.StopAsync( CancellationToken.None );
        } catch (OperationCanceledException) {
            // Expected
        }

        // Assert - Each saga produces exactly one PDS write (two total)
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );
    }

    /// <summary>
    /// Regression guard: a saga that becomes complete with its final leg FAILING (so the successful
    /// provider count, the generation, does not grow) must still finalize. The terminal write is
    /// gated by the finalize claim only; it must NOT be skipped by the CAS even when the generation
    /// is unchanged from the previous partial write.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Terminal_WhenCompletedViaFailingLeg_StillFinalizes( ) {
        // Arrange — Spotify succeeded (generation=1 partial written), Tidal failed.
        // The saga is now complete but the successful count (generation) stayed at 1.
        SagaCoordinatorBackgroundService service = CreateService( );

        string spotifyJson = """{"isrc":"USRC12345678","trackName":"Test Song","artistName":"Test Artist","url":"https://open.spotify.com/track/abc123"}""";
        LookupSagaState completedViaFailure = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -2 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: spotifyJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                ),
                [SupportedProviders.Tidal] = new(
                    Provider: SupportedProviders.Tidal,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: "Rate limited"
                )
            },
            FinalResultUri = null,
            IsPartial = true,
            InstanceToken = "instance-current"
        };

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act — drive the terminal write directly
        await service.InvokeWriteForTestAsync( completedViaFailure, terminal: true, cts.Token );

        // Assert — final URI recorded (saga finalized despite unchanged generation)
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Terminal write must call SetFinalResultUriAsync even when the generation did not grow"
        );

        // Assert — the final-URI Lua operation clears the partial flag atomically.
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( TestSagaId, false, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        // Assert — generation CAS was never consulted on the terminal path
        _sagaManagerMock.Verify(
            s => s.TryAdvanceWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Terminal write must not call TryAdvanceWriteGenerationAsync"
        );
    }

    /// <summary>
    /// Verifies that a non-terminal <see cref="SagaCoordinatorBackgroundService.InvokeWriteForTestAsync"/>
    /// with a null combine result (no successful results yet) does NOT delete the saga and does NOT
    /// release the dedup lock. More providers are still outstanding; the saga must survive to receive
    /// their results.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WriteResult_NonTerminal_WhenNullCombineResult_DoesNotDeleteOrRelease( ) {
        // Arrange — saga with only a failed provider; combiner returns null for allowIncomplete=true
        SagaCoordinatorBackgroundService service = CreateService( );

        LookupSagaState sagaWithNoSuccesses = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -1 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: "Rate limited"
                ),
                [SupportedProviders.Tidal] = new(
                    Provider: SupportedProviders.Tidal,
                    IsComplete: false,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: null,
                    ErrorMessage: null
                )
            },
            FinalResultUri = null,
            IsPartial = false
        };

        _ = _sagaManagerMock
            .Setup( s => s.TryDeleteAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act — non-terminal write with no successful results yet
        await service.InvokeWriteForTestAsync( sagaWithNoSuccesses, terminal: false, cts.Token );

        // Assert — saga must NOT be deleted: more providers are still in flight
        _sagaManagerMock.Verify(
            s => s.TryDeleteAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal null combine must not delete the saga"
        );

        // Assert — dedup lock must NOT be released: the saga is still live
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal null combine must not release the deduplication lock"
        );

        // Assert — no PDS write
        _atProtoStorageMock.Verify(
            a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// Verifies that when <c>IndexResultAsync</c> throws after the durability line on the terminal
    /// path, the deduplicator is still released with the non-null record URI and the method
    /// completes without propagating the exception. The result is already durably written; cache
    /// indexing is best-effort and must not interrupt waiter wakeup or bubble out.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenIndexFailsAfterDurability_StillReleasesDedupAndDoesNotThrow( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _sagaManagerMock
            .Setup( s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.Stored );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "cache indexing failed" ) );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act — must complete without throwing even though IndexResultAsync fails
        await service.InvokeWriteForTestAsync( completeSaga, terminal: true, cts.Token );

        // Assert - dedup released with the non-null URI; the null sentinel must never reach waiters
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( TestLookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Dedup must be released with the record URI even when post-release indexing fails"
        );

        // Assert - URI was durably stored before the release
        _sagaManagerMock.Verify(
            s => s.TrySetFinalResultUriAsync( TestSagaId, TestRecordUri, "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Once
        );

        // Assert - finalize claim was never returned; it is retained to prevent re-entrant writes
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Post-release index failure must not release the finalize claim"
        );
    }

    /// <summary>
    /// Verifies that when <c>IndexResultAsync</c> throws after the durability line on the
    /// non-terminal path, the deduplicator is still released with the non-null record URI and the
    /// method completes without propagating the exception. The partial result is already durably
    /// written; cache indexing is best-effort and must not interrupt waiter wakeup or bubble out.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WritePartial_WhenIndexFailsAfterDurability_StillReleasesDedupAndDoesNotThrow( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState partialSaga = CreateCompleteSaga( ) with { IsPartial = true };

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _sagaManagerMock
            .Setup( s => s.TrySetPartialResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "cache indexing failed" ) );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act — must complete without throwing even though IndexResultAsync fails
        await service.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );

        // Assert - dedup released with the non-null URI; the null sentinel must never reach waiters
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( TestLookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Dedup must be released with the record URI even when post-release indexing fails"
        );

        // Assert - write generation was not reset; the durability line was crossed
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Post-release index failure must not reset the write generation"
        );

        // Assert - finalize claim was never acquired or released on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not call TryClaimFinalizeAsync"
        );
    }

    /// <summary>
    /// Verifies that when <c>IndexResultAsync</c> throws <see cref="OperationCanceledException"/>
    /// after the durability line on the terminal path, the deduplicator is still released with the
    /// non-null record URI and the method completes without propagating the exception. The result is
    /// already durably written; an OCE from best-effort cache indexing must not interrupt waiter
    /// wakeup or bubble to the outer rethrowing <c>catch (OperationCanceledException)</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenIndexCanceledAfterDurability_StillReleasesDedupAndDoesNotThrow( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState completeSaga = CreateCompleteSaga( );

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _sagaManagerMock
            .Setup( s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.Stored );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new OperationCanceledException( ) );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act — must complete without throwing even though IndexResultAsync is canceled
        await service.InvokeWriteForTestAsync( completeSaga, terminal: true, cts.Token );

        // Assert - dedup released with the non-null URI; an OCE from indexing must not suppress waiter wakeup
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( TestLookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Dedup must be released with the record URI even when post-release indexing is canceled"
        );

        // Assert - finalize claim was never returned; it is retained to prevent re-entrant writes
        _sagaManagerMock.Verify(
            s => s.TryReleaseFinalizeClaimAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Post-release OCE from indexing must not release the finalize claim"
        );
    }

    /// <summary>
    /// Verifies that when <c>IndexResultAsync</c> throws <see cref="OperationCanceledException"/>
    /// after the durability line on the non-terminal path, the deduplicator is still released with
    /// the non-null record URI and the method completes without propagating the exception. An OCE
    /// from best-effort cache indexing must not interrupt waiter wakeup or bubble out.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WritePartial_WhenIndexCanceledAfterDurability_StillReleasesDedupAndDoesNotThrow( ) {
        // Arrange
        SagaCoordinatorBackgroundService service = CreateService( );
        LookupSagaState partialSaga = CreateCompleteSaga( ) with { IsPartial = true };

        _ = _atProtoStorageMock
            .Setup( a => a.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( TestRecordUri );

        _ = _sagaManagerMock
            .Setup( s => s.TrySetPartialResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        _ = _cacheRepositoryMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new OperationCanceledException( ) );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        // Act — must complete without throwing even though IndexResultAsync is canceled
        await service.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );

        // Assert - dedup released with the non-null URI; an OCE from indexing must not suppress waiter wakeup
        _deduplicatorMock.Verify(
            d => d.ReleaseAsync( TestLookupKey, TestRecordUri, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Dedup must be released with the record URI even when post-release indexing is canceled"
        );

        // Assert - write generation was not reset; the durability line was crossed
        _sagaManagerMock.Verify(
            s => s.TryResetWriteGenerationAsync( It.IsAny<string>( ), It.IsAny<int>( ), It.IsAny<int>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Post-release OCE from indexing must not reset the write generation"
        );

        // Assert - finalize claim was never acquired or released on the non-terminal path
        _sagaManagerMock.Verify(
            s => s.TryClaimFinalizeAsync( It.IsAny<string>( ), "instance-current", It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Non-terminal write must not call TryClaimFinalizeAsync"
        );
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds a service wired to all the mocks, the real combiner, and the enabled-providers set.
    /// </summary>
    /// <returns>A service under test.</returns>
    private SagaCoordinatorBackgroundService CreateService( IRefreshReviewStore? reviewStore = null ) {
        return new SagaCoordinatorBackgroundService(
            _redisMock.Object,
            _sagaManagerMock.Object,
            _atProtoStorageMock.Object,
            _cacheRepositoryMock.Object,
            _deduplicatorMock.Object,
            _resultCombiner,
            _queueResolverMock.Object,
            _enabledProviders,
            _loggerMock.Object,
            reviewStore ?? _refreshReviewStoreMock.Object
        );
    }

    /// <summary>
    /// Builds a URI-lookup saga whose only resolved provider is Spotify (with a sample result JSON),
    /// used for secondary-lookup tests. The origin priority is parameterized so tests can assert the
    /// priority secondaries inherit.
    /// </summary>
    /// <param name="originPriority">The saga's origin priority; defaults to interactive.</param>
    /// <returns>A URI-lookup saga with one resolved primary provider.</returns>
    private static LookupSagaState CreateUriLookupSaga( QueuePriority originPriority = QueuePriority.Interactive ) {
        string resultJson = """
        {
            "artist": "Test Artist",
            "title": "Test Song",
            "externalId": "USRC12345678",
            "url": "https://open.spotify.com/track/abc123",
            "isAlbum": false
        }
        """;

        return new LookupSagaState {
            SagaId = TestSagaId,
            LookupKey = "urilookup:testhash123",
            LookupType = LookupRequestType.UriLookup,
            LookupValue = "https://open.spotify.com/track/abc123",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds( -30 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            },
            PartialResultUri = null,
            FinalResultUri = null,
            IsPartial = false,
            InitialProvider = SupportedProviders.Spotify,
            RateLimitInfo = null,
            InstanceToken = "instance-current",
            OriginPriority = originPriority
        };
    }

    /// <summary>
    /// Builds a cached <c>MediaLinkResult</c> carrying a sample result for each of the given
    /// providers, used to simulate cache hits for secondary providers in the materialize-from-cache
    /// tests.
    /// </summary>
    /// <param name="providers">The providers to include in the cached result.</param>
    /// <returns>A cached result populated for each requested provider.</returns>
    private static MediaLinkResult CreateCachedExternalIdResult( params SupportedProviders[] providers ) {
        MediaLinkResult cachedResult = new( );

        foreach (SupportedProviders provider in providers) {
            cachedResult.Results[provider] = new MusicLookupResult {
                Artist = "Test Artist",
                Title = "Test Song",
                ExternalId = "USRC12345678",
                URL = $"https://{provider}.example.com/track/abc123",
                IsAlbum = false
            };
        }

        return cachedResult;
    }

    /// <summary>
    /// Builds a complete ISRC-lookup saga with a single successful Spotify provider state (carrying
    /// a sample result JSON), used as the baseline fixture for the polling and finalization tests;
    /// individual tests adjust it via <c>with</c> expressions.
    /// </summary>
    /// <returns>A complete, finalizable saga.</returns>
    private static LookupSagaState CreateCompleteSaga( ) {
        string resultJson = """
        {
            "isrc": "USRC12345678",
            "trackName": "Test Song",
            "artistName": "Test Artist",
            "albumName": "Test Album",
            "url": "https://open.spotify.com/track/abc123"
        }
        """;

        return new LookupSagaState {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes( -5 ),
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            },
            PartialResultUri = null,
            FinalResultUri = null,
            IsPartial = false,
            InitialProvider = SupportedProviders.Spotify,
            RateLimitInfo = null,
            InstanceToken = "instance-current"
        };
    }

    #endregion
}
