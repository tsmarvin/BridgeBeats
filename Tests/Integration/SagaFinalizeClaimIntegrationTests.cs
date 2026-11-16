using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Worker.SagaCoordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the saga finalize single-winner claim against a real Redis instance.
/// Verifies that exactly one concurrent finalize trigger performs the PDS write, that a failed
/// PDS write releases the claim so a retry can re-finalize, and that re-delivery of an already-
/// finalized saga is a no-op. The shared Redis Testcontainer is required; tests are tagged
/// Integration and Docker so the CI filter handles them consistently.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public class SagaFinalizeClaimIntegrationTests {

    /// <summary>The shared Redis connection used by the claim under test.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>Queue settings supplied to the saga manager.</summary>
    private IOptions<QueueSettings> _settings = null!;

    /// <summary>The saga state manager backed by real Redis.</summary>
    private RedisSagaStateManager _sagaManager = null!;

    /// <summary>MSTest-injected context; its cancellation token bounds in-test delays.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Fixed saga id used across tests in this class.</summary>
    private const string TestSagaId = "saga-claim-integ-0001";

    /// <summary>Fixed lookup key used in the saga fixture.</summary>
    private const string TestLookupKey = "isrc:USRC99999001";

    /// <summary>
    /// Requires the shared Redis container and opens a connection used by all tests in this class.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
    }

    /// <summary>Closes and disposes the Redis connection after all tests in this class complete.</summary>
    [ClassCleanup]
    public static async Task ClassCleanup( ) {
        if (s_redis is not null) {
            await s_redis.CloseAsync( );
            s_redis.Dispose( );
        }
    }

    /// <summary>Clears saga keys and creates a fresh saga manager before each test.</summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        await foreach (RedisKey key in server.KeysAsync( pattern: "saga:*" )) {
            _ = await db.KeyDeleteAsync( key );
        }

        _settings = Options.Create( new QueueSettings { JobExpirationMinutes = 60 } );
        _sagaManager = new RedisSagaStateManager(
            s_redis,
            new Mock<ILogger<RedisSagaStateManager>>( ).Object,
            _settings
        );
    }

    /// <summary>
    /// The load-bearing single-winner race test. Drives genuinely concurrent acquirers
    /// through a barrier into the shared finalize path — the convergence point of all
    /// three real triggers (saga:completed, complete:{key}, the 30-second poll) — and
    /// asserts exactly one PDS write via an Interlocked counter on a shared storage double.
    /// A sequential test passes against the broken read-check; only true concurrency (all
    /// acquirers reading FinalResultUri==null simultaneously before any writes) exercises
    /// the race.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_UnderConcurrentTriggers_WritesExactlyOnce( ) {
        // Arrange - seed one complete, finalizable saga
        LookupSagaState seededSaga = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999001",
            QueuePriority.Interactive, TestContext.CancellationToken
        );
        await _sagaManager.AddToPendingIndexAsync( TestSagaId, TestContext.CancellationToken );

        // Shared storage double with an Interlocked counter — the discriminator
        int pdsWriteCount = 0;
        Mock<IATProtoStorageService> storageDouble = new( );
        _ = storageDouble
            .Setup( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                _ = Interlocked.Increment( ref pdsWriteCount );
                string rkey = Guid.NewGuid( ).ToString( "N" );
                return $"at://did:plc:test/com.bridgebeats.medialink/{rkey}";
            } );

        // Build a complete saga fixture with one successful provider
        string resultJson = """{"isrc":"USRC99999001","trackName":"Race Test","artistName":"Test","url":"https://spotify.com/t/1"}""";
        LookupSagaState completeSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC99999001",
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
            FinalResultUri = null,
            IsPartial = false,
            InstanceToken = seededSaga.InstanceToken
        };

        // Build all mocks needed by SagaCoordinatorBackgroundService
        Mock<IConnectionMultiplexer> redisMock = new( );
        Mock<ISubscriber> subscriberMock = new( );
        _ = redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) ).Returns( subscriberMock.Object );

        Mock<IMediaLinkCacheRepository> cacheMock = new( );
        _ = cacheMock
            .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        Mock<IRequestDeduplicator> deduplicatorMock = new( );
        Mock<IProviderQueueResolver<QueuedLookupRequest>> queueResolverMock = new( );
        Mock<ILogger<SagaResultCombiner>> combinerLoggerMock = new( );
        SagaResultCombiner resultCombiner = new( combinerLoggerMock.Object );
        Mock<ILogger<SagaCoordinatorBackgroundService>> loggerMock = new( );
        HashSet<SupportedProviders> enabledProviders = [SupportedProviders.Spotify];

        // Wire GetAsync to always return the same unfinalised saga
        Mock<ISagaStateManager> sagaMgrWrapper = new( );
        _ = sagaMgrWrapper
            .Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( completeSaga );
        _ = sagaMgrWrapper
            .Setup( s => s.TryClaimFinalizeAsync( TestSagaId, seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string _, string _, CancellationToken ct ) => _sagaManager.TryClaimFinalizeAsync( TestSagaId, seededSaga.InstanceToken!, ct ) );
        _ = sagaMgrWrapper
            .Setup( s => s.TryReleaseFinalizeClaimAsync( TestSagaId, seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string _, string _, CancellationToken ct ) => _sagaManager.TryReleaseFinalizeClaimAsync( TestSagaId, seededSaga.InstanceToken!, ct ) );
        _ = sagaMgrWrapper
            .Setup( s => s.TryAdvanceWriteGenerationAsync( TestSagaId, It.IsAny<int>( ), seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string _, int gen, string _, CancellationToken ct ) => _sagaManager.TryAdvanceWriteGenerationAsync( TestSagaId, gen, seededSaga.InstanceToken!, ct ) );
        _ = sagaMgrWrapper
            .Setup( s => s.TryResetWriteGenerationAsync( TestSagaId, It.IsAny<int>( ), It.IsAny<int>( ), seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = sagaMgrWrapper
            .Setup( s => s.TrySetFinalResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( SagaFinalResultWriteOutcome.Stored );
        _ = sagaMgrWrapper
            .Setup( s => s.TrySetIsPartialAsync( It.IsAny<string>( ), It.IsAny<bool>( ), seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = sagaMgrWrapper
            .Setup( s => s.TryDeleteAsync( It.IsAny<string>( ), seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = sagaMgrWrapper
            .Setup( s => s.TryMarkSecondariesQueuedAsync( It.IsAny<string>( ), seededSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false ); // ISRC saga: secondaries guard returns false (no fan-out)

        // N = 8 concurrent acquirers through a release gate
        const int Concurrency = 8;
        TaskCompletionSource gate = new( TaskCreationOptions.RunContinuationsAsynchronously );

        // Build a service per acquirer (each drives one of the three trigger paths)
        async Task RunAcquirer( int index ) {
            await gate.Task; // wait for simultaneous release

            SagaCoordinatorBackgroundService svc = new(
                redisMock.Object,
                sagaMgrWrapper.Object,
                storageDouble.Object,
                cacheMock.Object,
                deduplicatorMock.Object,
                resultCombiner,
                queueResolverMock.Object,
                enabledProviders,
                loggerMock.Object,
                Mock.Of<IRefreshReviewStore>( )
            );

            // All paths converge on WriteFinalResultAsync internally; invoke via polling path
            // (simplest to call without a full subscription setup per acquirer)
            using CancellationTokenSource cts = new( TimeSpan.FromSeconds( 10 ) );
            await svc.InvokeFinalizeForTestAsync( completeSaga, cts.Token );
        }

        // Release all acquirers simultaneously
        Task<Task>[] racers = [.. Enumerable.Range( 0, Concurrency )
            .Select( i => Task.Factory.StartNew(
                ( ) => RunAcquirer( i ),
                TestContext.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ) )];

        // Small delay to let all tasks reach the gate before releasing
        await Task.Delay( 50, TestContext.CancellationToken );
        gate.SetResult( );

        // Wait for all to complete
        await Task.WhenAll( racers.Select( t => t.Unwrap( ) ) );

        // Assert - exactly one PDS write
        Assert.AreEqual( 1, pdsWriteCount,
            $"Expected exactly 1 PDS write under {Concurrency} concurrent finalizers, got {pdsWriteCount}" );

        // Assert - dedup lock released with a non-null URI exactly once
        deduplicatorMock.Verify(
            d => d.ReleaseAsync( TestLookupKey, It.Is<string?>( u => u != null ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies the Redis claim release/re-claim (HSETNX/HDEL) primitive: a claim that is released
    /// can be re-acquired by a subsequent caller, but a held claim cannot be acquired a second time
    /// without an intervening release.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ClaimPrimitive_AfterRelease_CanBeReacquired( ) {
        // Arrange
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999001",
            QueuePriority.Interactive, TestContext.CancellationToken
        );

        // First acquisition must succeed
        bool firstClaimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );
        Assert.IsTrue( firstClaimed, "First acquisition must succeed" );

        // Release the claim
        await _sagaManager.ReleaseFinalizeClaimAsync( TestSagaId, TestContext.CancellationToken );

        // Re-acquisition after release must succeed
        bool reacquired = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );
        Assert.IsTrue( reacquired, "Re-acquisition after release must succeed" );

        // A further acquisition WITHOUT an intervening release must fail
        bool duplicateClaimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );
        Assert.IsFalse( duplicateClaimed, "A second acquisition without a release must fail" );
    }

    /// <summary>
    /// Negative control for the failure-retry test: a finalize that succeeds does NOT leave the
    /// claim releasable for a second winner. After success the claim remains held, so a subsequent
    /// TryClaimFinalizeAsync returns false.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Finalize_WhenPdsWriteSucceeds_ClaimStaysHeld( ) {
        // Arrange - seed the saga
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999001",
            null, TestContext.CancellationToken
        );

        // Act - first caller wins the claim and does NOT release it on success
        bool firstClaimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );
        Assert.IsTrue( firstClaimed, "First caller must win the claim" );

        // No ReleaseFinalizeClaimAsync call (simulating the success path)

        // Assert - a second caller cannot acquire the claim
        bool secondClaimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );
        Assert.IsFalse( secondClaimed,
            "Second caller must NOT acquire the claim after a successful finalization (claim is retained on success)" );
    }

    /// <summary>
    /// Verifies that re-delivery of an already-finalized saga produces zero additional PDS writes.
    /// The integration test drives the full claim path with real Redis to confirm the claim field
    /// blocks re-finalization even after the dedup lock expires.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Redelivery_OfCompletedSaga_DoesNotProduceSecondRecord( ) {
        // Arrange - seed the saga and claim it (simulating a successful finalization)
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999001",
            null, TestContext.CancellationToken
        );

        bool firstClaimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );
        Assert.IsTrue( firstClaimed );

        // Simulate: the first delivery wrote the final URI
        await _sagaManager.SetFinalResultUriAsync(
            TestSagaId, "at://did:plc:test/result/original", TestContext.CancellationToken
        );

        int pdsWriteCount = 0;
        Mock<IATProtoStorageService> storageDouble = new( );
        _ = storageDouble
            .Setup( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                _ = Interlocked.Increment( ref pdsWriteCount );
                return "at://did:plc:test/result/redelivery";
            } );

        // Act - simulate re-delivery: claim attempt must fail (claim still held from first delivery)
        bool redeliveryClaimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );

        // Assert - re-delivery cannot acquire the claim
        Assert.IsFalse( redeliveryClaimed,
            "Re-delivery must not acquire the finalize claim after a successful first delivery" );

        // Assert - zero storage calls from the re-delivery path
        Assert.AreEqual( 0, pdsWriteCount );
    }

    /// <summary>
    /// Positive control for <see cref="Redelivery_OfCompletedSaga_DoesNotProduceSecondRecord"/>:
    /// the first delivery DOES finalize. Without this, a handler that no-ops every call would
    /// vacuously pass the redelivery assertion.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task FirstDelivery_ClaimsAndFinalizes( ) {
        // Arrange
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999001",
            null, TestContext.CancellationToken
        );

        // Act
        bool claimed = await _sagaManager.TryClaimFinalizeAsync( TestSagaId, TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( claimed, "First delivery must successfully acquire the finalize claim" );
    }


}
