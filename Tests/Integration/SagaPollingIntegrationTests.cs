using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the saga-polling path of <see cref="RedisSagaStateManager"/> against a real
/// Redis instance (the shared Testcontainers Redis). Verifies that the polling fallback mechanism
/// correctly discovers and processes sagas that may have been missed by Pub/Sub: querying sagas that are
/// complete but not yet finalized (honoring minimum-age and limit, excluding finalized and incomplete
/// sagas, and cleaning expired entries from the pending index) and the pending-index lifecycle as sagas
/// are created, finalized, and deleted. Requires Docker to be running on the host machine.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize] // Shares Redis saga:* keys with RedisSagaStateManagerTests
public class SagaPollingIntegrationTests {

    /// <summary>The shared Redis connection used by the saga manager under test.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>Mock logger captured for the saga manager under test.</summary>
    private Mock<ILogger<RedisSagaStateManager>> _mockLogger = null!;
    /// <summary>Queue settings (including job expiration) supplied to the saga manager.</summary>
    private IOptions<QueueSettings> _settings = null!;
    /// <summary>The saga state manager under test, recreated for each test.</summary>
    private RedisSagaStateManager _sagaManager = null!;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Requires the shared Redis container and opens a connection to it for the test class.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
    }

    /// <summary>
    /// Closes and disposes the Redis connection after the class completes.
    /// </summary>
    [ClassCleanup]
    public static async Task ClassCleanup( ) {
        if (s_redis is not null) {
            await s_redis.CloseAsync( );
            s_redis.Dispose( );
        }
    }

    /// <summary>
    /// Clears leftover <c>saga:*</c> keys and constructs a fresh saga manager before each test.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        // Clear only saga-related keys before each test
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        await foreach (RedisKey key in server.KeysAsync( pattern: "saga:*" )) {
            _ = await db.KeyDeleteAsync( key );
        }

        _mockLogger = new Mock<ILogger<RedisSagaStateManager>>( );
        _settings = Options.Create( new QueueSettings { JobExpirationMinutes = 60 } );

        _sagaManager = new RedisSagaStateManager(
            s_redis,
            _mockLogger.Object,
            _settings
        );
    }

    /// <summary>
    /// Verifies that a saga whose provider state is complete but which has no final result URI is
    /// returned by the completed-but-unfinalized query.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_ReturnsCompleteSagasWithNoFinalUri( ) {
        // Arrange - Create a saga that is complete but not finalized
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Mark provider as complete
        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            Provider: SupportedProviders.Spotify,
            IsComplete: true,
            IsSuccess: true,
            ResultJson: """{"trackId": "abc123"}""",
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: null
        ), TestContext.CancellationToken );

        // Backdate the saga by modifying the createdAt directly in Redis
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.HashSetAsync(
            $"saga:{sagaId}",
            "createdAt",
            DateTimeOffset.UtcNow.AddSeconds( -30 ).ToString( "O" )
        );

        // Act - Query for unfinalized sagas with minimum age of 0 seconds
        IReadOnlyList<LookupSagaState> results = await _sagaManager.GetPendingReconciliationAsync(
            minimumAge: TimeSpan.Zero,
            limit: 100,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.HasCount( 1, results );
        Assert.AreEqual( sagaId, results[0].SagaId );
        Assert.IsTrue( results[0].IsComplete );
        Assert.IsNull( results[0].FinalResultUri );
    }

    /// <summary>
    /// Verifies that a saga which already has a final result URI is excluded from the
    /// completed-but-unfinalized query.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_IncludesFinalizedSagaUntilWaitersAreReleased( ) {
        // Arrange - Create a finalized saga
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null
        ), TestContext.CancellationToken );

        // Final URI persistence deliberately keeps the saga indexed until waiter release succeeds.
        await _sagaManager.SetFinalResultUriAsync( sagaId, "at://did:plc:test/link/abc", TestContext.CancellationToken );

        // Act
        IReadOnlyList<LookupSagaState> results = await _sagaManager.GetPendingReconciliationAsync(
            minimumAge: TimeSpan.Zero,
            limit: 100,
            cancellationToken: TestContext.CancellationToken
        );

        Assert.HasCount( 1, results );
        LookupSagaState result = results[0];
        Assert.AreEqual( sagaId, result.SagaId );
        Assert.AreEqual( "at://did:plc:test/link/abc", result.FinalResultUri );
    }

    /// <summary>
    /// Verifies that a saga with an incomplete provider state is excluded from the
    /// completed-but-unfinalized query.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_ExcludesIncompleteSagas( ) {
        // Arrange - Create an incomplete saga
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Add provider but don't complete it
        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            SupportedProviders.Spotify, false, false, null, null, null
        ), TestContext.CancellationToken );

        // Act
        IReadOnlyList<LookupSagaState> results = await _sagaManager.GetPendingReconciliationAsync(
            minimumAge: TimeSpan.Zero,
            limit: 100,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Should be empty since saga is incomplete
        Assert.IsEmpty( results );
    }

    /// <summary>
    /// Verifies a complete saga younger than the requested minimum age is excluded from the
    /// completed-but-unfinalized query.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_RespectsMinimumAge( ) {
        // Arrange - Create a saga that was just created
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null
        ), TestContext.CancellationToken );

        // Act - Query with 15-second minimum age (saga is too young)
        IReadOnlyList<LookupSagaState> results = await _sagaManager.GetPendingReconciliationAsync(
            minimumAge: TimeSpan.FromSeconds( 15 ),
            limit: 100,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Should be empty since saga is too young
        Assert.IsEmpty( results );
    }

    /// <summary>
    /// Verifies the completed-but-unfinalized query returns no more than the requested limit when more
    /// eligible sagas exist.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_RespectsLimit( ) {
        // Arrange - Create multiple sagas
        for (int i = 0; i < 5; i++) {
            string lookupKey = $"isrc:USRC{i:D10}";
            string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

            _ = await _sagaManager.GetOrCreateAsync(
                sagaId,
                lookupKey,
                LookupRequestType.IsrcLookup,
                $"USRC{i:D10}",
                cancellationToken: TestContext.CancellationToken
            );

            await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
                SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null
            ), TestContext.CancellationToken );

            // Backdate the saga
            IDatabase db = s_redis!.GetDatabase( );
            _ = await db.HashSetAsync(
                $"saga:{sagaId}",
                "createdAt",
                DateTimeOffset.UtcNow.AddSeconds( -30 ).ToString( "O" )
            );
        }

        // Act - Query with limit of 3
        IReadOnlyList<LookupSagaState> results = await _sagaManager.GetPendingReconciliationAsync(
            minimumAge: TimeSpan.Zero,
            limit: 3,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Should only return 3 despite 5 being available
        Assert.HasCount( 3, results );
    }

    /// <summary>
    /// Verifies the completed-but-unfinalized query removes pending-index entries that point at expired
    /// (missing) saga records.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_CleansUpExpiredSagasFromIndex( ) {
        // Arrange - Add a saga ID to the pending index but don't create the saga itself
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.SetAddAsync( "saga:pending", "expired-saga-id" );

        // Verify it's in the index
        RedisValue[] pendingBefore = await db.SetMembersAsync( "saga:pending" );
        Assert.Contains<RedisValue>( v => v.ToString( ) == "expired-saga-id", pendingBefore );

        // Act - Query should clean up the orphaned index entry
        _ = await _sagaManager.GetPendingReconciliationAsync(
            minimumAge: TimeSpan.Zero,
            limit: 100,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Orphaned entry should be removed from index
        RedisValue[] pendingAfter = await db.SetMembersAsync( "saga:pending" );
        Assert.DoesNotContain<RedisValue>( v => v.ToString( ) == "expired-saga-id", pendingAfter );
    }

    /// <summary>A malformed indexed saga is pruned without preventing healthy sagas from reconciling.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetPendingReconciliationAsync_UnreadableSaga_DoesNotAbortSweep( ) {
        const string HealthySagaId = "healthy-reconciliation-saga";
        LookupSagaState healthy = await _sagaManager.GetOrCreateAsync(
            HealthySagaId, "isrc:HEALTHY000001", LookupRequestType.IsrcLookup, "HEALTHY000001",
            cancellationToken: TestContext.CancellationToken );
        _ = await _sagaManager.TryInitializeProviderStatesAsync(
            HealthySagaId, [SupportedProviders.Spotify], healthy.InstanceToken!, TestContext.CancellationToken );
        _ = await _sagaManager.TryUpdateProviderStateAsync(
            HealthySagaId,
            new ProviderLookupState( SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null ),
            healthy.InstanceToken!, TestContext.CancellationToken );

        IDatabase db = s_redis!.GetDatabase( );
        const string PoisonSagaId = "poison-reconciliation-saga";
        await db.HashSetAsync( $"saga:{PoisonSagaId}", [
            new HashEntry( "lookupKey", "isrc:POISON000001" ),
            new HashEntry( "instanceToken", "poison-token" )
        ] );
        _ = await db.SetAddAsync( "saga:pending", PoisonSagaId );

        IReadOnlyList<LookupSagaState> results = await _sagaManager.GetPendingReconciliationAsync(
            TimeSpan.Zero, cancellationToken: TestContext.CancellationToken );

        Assert.Contains( HealthySagaId, results.Select( saga => saga.SagaId ) );
        Assert.IsFalse( await db.SetContainsAsync( "saga:pending", PoisonSagaId ) );
    }

    /// <summary>
    /// Verifies adding a saga to the pending index inserts it into the Redis pending set.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task AddToPendingIndexAsync_AddsSagaToSet( ) {
        // Arrange
        string sagaId = "test-saga-id";

        // Act
        await _sagaManager.AddToPendingIndexAsync( sagaId, TestContext.CancellationToken );

        // Assert
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue[] members = await db.SetMembersAsync( "saga:pending" );
        Assert.Contains<RedisValue>( m => m.ToString( ) == sagaId, members );
    }

    /// <summary>
    /// Verifies removing a saga from the pending index deletes it from the Redis pending set.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RemoveFromPendingIndexAsync_RemovesSagaFromSet( ) {
        // Arrange
        string sagaId = "test-saga-id";
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.SetAddAsync( "saga:pending", sagaId );

        // Act
        await _sagaManager.RemoveFromPendingIndexAsync( sagaId, TestContext.CancellationToken );

        // Assert
        RedisValue[] members = await db.SetMembersAsync( "saga:pending" );
        Assert.DoesNotContain<RedisValue>( m => m.ToString( ) == sagaId, members );
    }

    /// <summary>
    /// Verifies creating a saga via get-or-create automatically registers it in the pending index.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetOrCreateAsync_AutomaticallyAddsToPendingIndex( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        // Act
        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Should be in pending index
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue[] members = await db.SetMembersAsync( "saga:pending" );
        Assert.Contains<RedisValue>( m => m.ToString( ) == sagaId, members );
    }

    /// <summary>
    /// Verifies final URI persistence retains the reconciliation entry until waiter release removes it.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetFinalResultUriAsync_RetainsPendingIndexUntilExplicitRemoval( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Verify it's in the index
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue[] membersBefore = await db.SetMembersAsync( "saga:pending" );
        Assert.Contains<RedisValue>( m => m.ToString( ) == sagaId, membersBefore );

        // Act
        await _sagaManager.SetFinalResultUriAsync( sagaId, "at://did:plc:test/link/abc", TestContext.CancellationToken );

        // Finalized-but-unnotified sagas remain discoverable by the polling backstop.
        RedisValue[] membersAfter = await db.SetMembersAsync( "saga:pending" );
        Assert.Contains<RedisValue>( m => m.ToString( ) == sagaId, membersAfter );

        await _sagaManager.RemoveFromPendingIndexAsync( sagaId, TestContext.CancellationToken );
        RedisValue[] membersAfterRelease = await db.SetMembersAsync( "saga:pending" );
        Assert.DoesNotContain<RedisValue>( m => m.ToString( ) == sagaId, membersAfterRelease );
    }

    /// <summary>
    /// Verifies deleting a saga removes it from the pending index along with the saga state.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DeleteAsync_RemovesFromPendingIndex( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Verify it's in the index
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue[] membersBefore = await db.SetMembersAsync( "saga:pending" );
        Assert.Contains<RedisValue>( m => m.ToString( ) == sagaId, membersBefore );

        // Act
        _ = await _sagaManager.DeleteAsync( sagaId, TestContext.CancellationToken );

        // Assert - Should be removed from pending index
        RedisValue[] membersAfter = await db.SetMembersAsync( "saga:pending" );
        Assert.DoesNotContain<RedisValue>( m => m.ToString( ) == sagaId, membersAfter );
    }
}
