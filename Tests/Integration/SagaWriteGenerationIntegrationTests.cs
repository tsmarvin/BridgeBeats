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
/// Integration tests for the write-generation compare-and-set (CAS) guard against a real Redis
/// instance. Verifies that concurrent handlers for the same generation level produce exactly one
/// PDS write, that sequential distinct generations produce one write each, that the CAS primitive
/// itself behaves correctly, and that a reset followed by re-advance works. Covers both the
/// terminal (finalize-claim) path and the non-terminal (partial-write) path. The shared Redis
/// Testcontainer is required; tests are tagged Integration and Docker so the CI filter handles
/// them consistently.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public class SagaWriteGenerationIntegrationTests {

    /// <summary>The shared Redis connection used by the tests.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>Queue settings supplied to the saga manager.</summary>
    private IOptions<QueueSettings> _settings = null!;

    /// <summary>The saga state manager backed by real Redis.</summary>
    private RedisSagaStateManager _sagaManager = null!;

    /// <summary>MSTest-injected context; its cancellation token bounds in-test delays.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Fixed saga id used across tests in this class.</summary>
    private const string TestSagaId = "saga-wgen-integ-0001";

    /// <summary>Fixed lookup key used in the saga fixture.</summary>
    private const string TestLookupKey = "isrc:USRC99999002";

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
    /// N=8 concurrent handlers all racing to finalize the same completion through the terminal path
    /// (<see cref="SagaCoordinatorBackgroundService.InvokeFinalizeForTestAsync"/>). The finalize-claim
    /// CAS ensures exactly one handler wins the claim and performs the PDS write; all others are
    /// rejected by the finalize-claim CAS before writing. This is the
    /// concurrent-terminal-trigger proof for the #315 claim-dedup guard.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ConcurrentTerminalTriggers_SameCompletion_ProducesExactlyOnePdsWrite( ) {
        // Arrange - seed one complete, finalizable saga
        LookupSagaState seededSaga = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999002",
            QueuePriority.Interactive, TestContext.CancellationToken
        );
        await _sagaManager.AddToPendingIndexAsync( TestSagaId, TestContext.CancellationToken );

        // Shared PDS write counter — the discriminator
        int pdsWriteCount = 0;
        Mock<IATProtoStorageService> storageDouble = new( );
        _ = storageDouble
            .Setup( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                _ = Interlocked.Increment( ref pdsWriteCount );
                string rkey = Guid.NewGuid( ).ToString( "N" );
                return $"at://did:plc:test/com.bridgebeats.medialink/{rkey}";
            } );

        string resultJson = """{"isrc":"USRC99999002","trackName":"Race Test","artistName":"Test","url":"https://spotify.com/t/1"}""";
        LookupSagaState completeSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC99999002",
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

        SagaCoordinatorBackgroundService BuildService( ) {
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
                .ReturnsAsync( false );

            return new SagaCoordinatorBackgroundService(
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
        }

        // N=8 concurrent handlers through a release gate
        const int Concurrency = 8;
        TaskCompletionSource gate = new( TaskCreationOptions.RunContinuationsAsynchronously );

        async Task RunHandler( int _ ) {
            await gate.Task;
            SagaCoordinatorBackgroundService svc = BuildService( );
            using CancellationTokenSource cts = new( TimeSpan.FromSeconds( 10 ) );
            await svc.InvokeFinalizeForTestAsync( completeSaga, cts.Token );
        }

        Task<Task>[] racers = [.. Enumerable.Range( 0, Concurrency )
            .Select( i => Task.Factory.StartNew(
                ( ) => RunHandler( i ),
                TestContext.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ) )];

        await Task.Delay( 50, TestContext.CancellationToken );
        gate.SetResult( );
        await Task.WhenAll( racers.Select( t => t.Unwrap( ) ) );

        // Assert - exactly one PDS write across all N concurrent handlers
        Assert.AreEqual( 1, pdsWriteCount,
            $"Expected exactly 1 PDS write under {Concurrency} concurrent handlers for the same generation, got {pdsWriteCount}" );
    }

    /// <summary>
    /// N=8 concurrent handlers all racing to write the same generation (k=1) through the
    /// non-terminal partial path (<see cref="SagaCoordinatorBackgroundService.InvokeWriteForTestAsync"/>
    /// with <c>terminal: false</c>). The write-generation CAS
    /// must ensure exactly one PDS write — the first racer to advance the generation wins; all
    /// others find the CAS already advanced and return without writing. This is the direct
    /// concurrent-partial-write dedup proof for the generation CAS.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ConcurrentPartialHandlers_SameGeneration_ProducesExactlyOnePdsWrite( ) {
        // Arrange - seed the saga
        LookupSagaState seededPartialSaga = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999002",
            QueuePriority.Interactive, TestContext.CancellationToken
        );

        // Shared PDS write counter — the discriminator
        int pdsWriteCount = 0;
        Mock<IATProtoStorageService> storageDouble = new( );
        _ = storageDouble
            .Setup( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                _ = Interlocked.Increment( ref pdsWriteCount );
                string rkey = Guid.NewGuid( ).ToString( "N" );
                return $"at://did:plc:test/com.bridgebeats.medialink/{rkey}";
            } );

        // One successful Spotify provider result — generation = Results.Count = 1
        string resultJson = """{"isrc":"USRC99999002","trackName":"Race Test","artistName":"Test","url":"https://spotify.com/t/1"}""";
        LookupSagaState partialSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC99999002",
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
            IsPartial = true,
            InstanceToken = seededPartialSaga.InstanceToken
        };

        SagaCoordinatorBackgroundService BuildPartialService( ) {
            Mock<IConnectionMultiplexer> redisMock = new( );
            Mock<ISubscriber> subscriberMock = new( );
            _ = redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) ).Returns( subscriberMock.Object );

            Mock<IMediaLinkCacheRepository> cacheMock = new( );
            _ = cacheMock
                .Setup( c => c.IndexResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
                .Returns( Task.CompletedTask );

            Mock<IRequestDeduplicator> deduplicatorMock = new( );
            _ = deduplicatorMock
                .Setup( d => d.ReleaseAsync( It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ) )
                .Returns( Task.CompletedTask );

            Mock<IProviderQueueResolver<QueuedLookupRequest>> queueResolverMock = new( );
            Mock<ILogger<SagaResultCombiner>> combinerLoggerMock = new( );
            SagaResultCombiner resultCombiner = new( combinerLoggerMock.Object );
            Mock<ILogger<SagaCoordinatorBackgroundService>> loggerMock = new( );
            HashSet<SupportedProviders> enabledProviders = [SupportedProviders.Spotify];

            Mock<ISagaStateManager> sagaMgrWrapper = new( );
            _ = sagaMgrWrapper
                .Setup( s => s.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
                .ReturnsAsync( partialSaga );
            // Only token-fenced generation operations hit real Redis;
            // all other saga manager calls are mocked to succeed without side effects.
            _ = sagaMgrWrapper
                .Setup( s => s.TryAdvanceWriteGenerationAsync( TestSagaId, It.IsAny<int>( ), seededPartialSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
                .Returns( ( string _, int gen, string _, CancellationToken ct ) => _sagaManager.TryAdvanceWriteGenerationAsync( TestSagaId, gen, seededPartialSaga.InstanceToken!, ct ) );
            _ = sagaMgrWrapper
                .Setup( s => s.TryResetWriteGenerationAsync( TestSagaId, It.IsAny<int>( ), It.IsAny<int>( ), seededPartialSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
                .ReturnsAsync( true );
            _ = sagaMgrWrapper
                .Setup( s => s.TrySetPartialResultUriAsync( It.IsAny<string>( ), It.IsAny<string>( ), seededPartialSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
                .ReturnsAsync( true );
            _ = sagaMgrWrapper
                .Setup( s => s.TryMarkSecondariesQueuedAsync( It.IsAny<string>( ), seededPartialSaga.InstanceToken!, It.IsAny<CancellationToken>( ) ) )
                .ReturnsAsync( false );

            return new SagaCoordinatorBackgroundService(
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
        }

        // N=8 concurrent handlers through a release gate
        const int Concurrency = 8;
        TaskCompletionSource gate = new( TaskCreationOptions.RunContinuationsAsynchronously );

        async Task RunHandler( int _ ) {
            await gate.Task;
            SagaCoordinatorBackgroundService svc = BuildPartialService( );
            using CancellationTokenSource cts = new( TimeSpan.FromSeconds( 10 ) );
            await svc.InvokeWriteForTestAsync( partialSaga, terminal: false, cts.Token );
        }

        Task<Task>[] racers = [.. Enumerable.Range( 0, Concurrency )
            .Select( i => Task.Factory.StartNew(
                ( ) => RunHandler( i ),
                TestContext.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            ) )];

        await Task.Delay( 50, TestContext.CancellationToken );
        gate.SetResult( );
        await Task.WhenAll( racers.Select( t => t.Unwrap( ) ) );

        // Assert - exactly one PDS write across all N concurrent handlers
        Assert.AreEqual( 1, pdsWriteCount,
            $"Expected exactly 1 PDS write under {Concurrency} concurrent partial handlers for the same generation, got {pdsWriteCount}" );
    }

    /// <summary>
    /// Verifies that sequential advances through distinct generations 0→1→2 are each accepted by
    /// the strict-less-than CAS. Each new generation is strictly greater than the stored value, so
    /// <c>TryAdvanceWriteGenerationAsync</c> returns <see langword="true"/> for both calls.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SequentialDistinctGenerations_AreEachAcceptedByTheCas( ) {
        // Arrange - seed the saga
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999002",
            QueuePriority.Interactive, TestContext.CancellationToken
        );

        // Generation 1: advance from 0 to 1 — must succeed
        bool advancedToGen1 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 1, TestContext.CancellationToken
        );
        Assert.IsTrue( advancedToGen1, "Advancing from 0 to generation 1 must succeed" );

        // Generation 2: advance from 1 to 2 — must succeed
        bool advancedToGen2 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 2, TestContext.CancellationToken
        );
        Assert.IsTrue( advancedToGen2, "Advancing from 1 to generation 2 must succeed" );
    }

    /// <summary>
    /// CAS primitive test: advance to 2, then attempt to advance to 1 (below stored) and 2
    /// (equal to stored) — both must be rejected. Then advance to 3 (above stored) — must
    /// succeed. Verifies the strict-less-than semantics of the Lua CAS.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CasPrimitive_StrictlyLessThanSemantics( ) {
        // Arrange - seed the saga
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999002",
            QueuePriority.Interactive, TestContext.CancellationToken
        );

        // Advance to generation 2 — must succeed (stored is 0)
        bool advancedTo2 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 2, TestContext.CancellationToken
        );
        Assert.IsTrue( advancedTo2, "Initial advance to generation 2 must succeed" );

        // Attempt to advance to 1 (below stored 2) — must fail
        bool advancedTo1 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 1, TestContext.CancellationToken
        );
        Assert.IsFalse( advancedTo1, "Advancing to generation 1 when stored is 2 must fail (below stored)" );

        // Attempt to advance to 2 again (equal to stored 2) — must fail
        bool advancedTo2Again = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 2, TestContext.CancellationToken
        );
        Assert.IsFalse( advancedTo2Again, "Advancing to generation 2 when stored is 2 must fail (equal to stored)" );

        // Advance to 3 (above stored 2) — must succeed
        bool advancedTo3 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 3, TestContext.CancellationToken
        );
        Assert.IsTrue( advancedTo3, "Advancing to generation 3 when stored is 2 must succeed" );
    }

    /// <summary>
    /// Verifies the reset-then-re-advance round trip: advance to 2, reset back to 1 (the
    /// prior generation, simulating a pre-durability write failure), then re-advance to 2
    /// succeeds. This ensures the failure path leaves the saga in a retryable state.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ResetThenReAdvance_AllowsRetry( ) {
        // Arrange - seed the saga
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999002",
            QueuePriority.Interactive, TestContext.CancellationToken
        );

        // Advance to generation 2 — must succeed
        bool firstAdvance = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 2, TestContext.CancellationToken
        );
        Assert.IsTrue( firstAdvance, "First advance to generation 2 must succeed" );

        // Simulate pre-durability failure: advancedTo=2, prior=1
        await _sagaManager.ResetWriteGenerationAsync( TestSagaId, 2, 1, TestContext.CancellationToken );

        // After the conditional reset, stored=1, so re-advancing to 2 must succeed
        bool reAdvance = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 2, TestContext.CancellationToken
        );
        Assert.IsTrue( reAdvance, "Re-advancing to generation 2 after reset to prior=1 must succeed" );
    }

    /// <summary>
    /// Verifies the conditional semantics of the fenced write-generation reset:
    /// a stale caller (handler A) that attempts to reset after a newer handler (handler B) has
    /// already advanced the generation further must NOT clobber the higher generation. The stored
    /// value must remain at the higher generation after the stale reset attempt.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ConditionalReset_StaleCallerDoesNotClobberHigherGeneration( ) {
        // Arrange - seed the saga
        _ = await _sagaManager.GetOrCreateAsync(
            TestSagaId, TestLookupKey, LookupRequestType.IsrcLookup, "USRC99999002",
            QueuePriority.Interactive, TestContext.CancellationToken
        );

        // Handler A advances to generation 2
        bool handlerAAdvance = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 2, TestContext.CancellationToken
        );
        Assert.IsTrue( handlerAAdvance, "Handler A must advance to generation 2" );

        // Handler B advances further to generation 3 (overtakes handler A)
        bool handlerBAdvance = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 3, TestContext.CancellationToken
        );
        Assert.IsTrue( handlerBAdvance, "Handler B must advance to generation 3" );

        // Handler A's PDS write failed; it attempts a conditional reset from 2 back to 1.
        // Because the stored generation is now 3 (not 2), the CAS condition fails and the
        // reset must be skipped — stored must remain at 3.
        await _sagaManager.ResetWriteGenerationAsync( TestSagaId, 2, 1, TestContext.CancellationToken );

        // Verify by trying to advance to 3 again — must fail (stored is still 3, not 1)
        bool reAdvanceTo3 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 3, TestContext.CancellationToken
        );
        Assert.IsFalse( reAdvanceTo3,
            "Stored generation must still be 3 after the stale handler's conditional reset was refused" );

        // Verify by advancing to 4 — must succeed (stored is 3)
        bool advanceTo4 = await _sagaManager.TryAdvanceWriteGenerationAsync(
            TestSagaId, 4, TestContext.CancellationToken
        );
        Assert.IsTrue( advanceTo4,
            "Advancing to 4 from stored=3 must succeed, confirming the stale reset did not corrupt the generation" );
    }
}
