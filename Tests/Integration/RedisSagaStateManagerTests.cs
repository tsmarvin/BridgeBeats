using BridgeBeats.Contracts.Constants;
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
/// Integration tests for <see cref="RedisSagaStateManager"/> against a real Redis instance (the shared
/// Testcontainers Redis). Verifies saga creation and idempotent get-or-create, per-provider state
/// updates, partial and final result URIs, completion semantics, origin-priority first-writer-wins,
/// the secondaries-queued guard, provider-state initialization that preserves existing state, deletion,
/// and deterministic saga-id generation. Requires Docker to be running on the host machine.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize] // Shares Redis saga:* keys with SagaPollingIntegrationTests
public class RedisSagaStateManagerTests {

    /// <summary>Concurrent first creation returns one authoritative token and core state.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ConcurrentFirstCreate_ReturnsOneAuthoritativeTokenAndCoreState( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( "isrc:CONCURRENT-FIRST-CREATE" );
        LookupSagaState[] reads = await Task.WhenAll( Enumerable.Range( 0, 16 ).Select( index =>
            _sagaManager.GetOrCreateAsync(
                sagaId,
                $"isrc:CONCURRENT-FIRST-CREATE-{index}",
                LookupRequestType.IsrcLookup,
                $"CONCURRENT-FIRST-CREATE-{index}",
                index % 2 == 0 ? QueuePriority.Interactive : QueuePriority.Background,
                TestContext.CancellationToken ) ) );

        string token = reads[0].InstanceToken!;
        Assert.IsFalse( string.IsNullOrWhiteSpace( token ) );
        Assert.IsTrue( reads.All( read => read.InstanceToken == token ) );
        Assert.IsTrue( reads.All( read => read.LookupKey == reads[0].LookupKey ) );
        Assert.IsTrue( reads.All( read => read.LookupValue == reads[0].LookupValue ) );
        LookupSagaState persisted = (await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken ))!;
        Assert.AreEqual( token, persisted.InstanceToken );
        Assert.AreEqual( reads[0].LookupKey, persisted.LookupKey );
        Assert.AreEqual( reads[0].LookupValue, persisted.LookupValue );
    }

    /// <summary>Verifies stale instance tokens cannot mutate a recreated saga.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task StaleInstanceToken_CannotClaimRecreatedSaga( ) {
        string sagaId = ISagaStateManager.GenerateSagaId("isrc:STALE-TOKEN");
        LookupSagaState first = await _sagaManager.GetOrCreateAsync(sagaId, "isrc:STALE-TOKEN", LookupRequestType.IsrcLookup, "STALE-TOKEN", cancellationToken: TestContext.CancellationToken);
        Assert.IsNotNull( first.InstanceToken );
        Assert.IsTrue( await _sagaManager.DeleteAsync( sagaId, TestContext.CancellationToken ) );
        LookupSagaState second = await _sagaManager.GetOrCreateAsync(sagaId, "isrc:STALE-TOKEN", LookupRequestType.IsrcLookup, "STALE-TOKEN", cancellationToken: TestContext.CancellationToken);
        Assert.AreNotEqual( first.InstanceToken, second.InstanceToken );
        Assert.IsFalse( await _sagaManager.TryClaimFinalizeAsync( sagaId, first.InstanceToken!, TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TryClaimFinalizeAsync( sagaId, second.InstanceToken!, TestContext.CancellationToken ) );
    }

    /// <summary>Initial-provider writes cannot recreate or mutate a saga after its instance is replaced.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task StaleInstanceToken_CannotSetInitialProviderOnRecreatedSaga( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( "isrc:STALE-INITIAL-PROVIDER" );
        LookupSagaState first = await _sagaManager.GetOrCreateAsync(
            sagaId, "isrc:STALE-INITIAL-PROVIDER", LookupRequestType.IsrcLookup,
            "STALE-INITIAL-PROVIDER", cancellationToken: TestContext.CancellationToken );
        Assert.IsNotNull( first.InstanceToken );
        Assert.IsTrue( await _sagaManager.DeleteAsync( sagaId, TestContext.CancellationToken ) );
        LookupSagaState second = await _sagaManager.GetOrCreateAsync(
            sagaId, "isrc:STALE-INITIAL-PROVIDER", LookupRequestType.IsrcLookup,
            "STALE-INITIAL-PROVIDER", cancellationToken: TestContext.CancellationToken );

        Assert.IsFalse( await _sagaManager.TrySetInitialProviderAsync(
            sagaId, SupportedProviders.Spotify, first.InstanceToken!, TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TrySetInitialProviderAsync(
            sagaId, SupportedProviders.AppleMusic, second.InstanceToken!, TestContext.CancellationToken ) );
        LookupSagaState current = (await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken ))!;
        Assert.AreEqual( SupportedProviders.AppleMusic, current.InitialProvider );
    }

    /// <summary>Stale provider snapshots are merged atomically instead of erasing siblings.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TrySetRateLimitInfoAsync_StaleSnapshots_PreserveBothProviders( ) {
        const string SagaId = "rate-limit-atomic-merge";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:RATELIMITMERGE1",
            LookupRequestType.IsrcLookup,
            "RATELIMITMERGE1",
            cancellationToken: TestContext.CancellationToken );
        ProviderRateLimitInfo spotify = new(
            SupportedProviders.Spotify,
            DateTimeOffset.UtcNow.AddMinutes( 1 ),
            "provider" );
        ProviderRateLimitInfo apple = new(
            SupportedProviders.AppleMusic,
            DateTimeOffset.UtcNow.AddMinutes( 2 ),
            "songs/:id" );

        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId, [spotify], saga.InstanceToken!, TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId, [apple], saga.InstanceToken!, TestContext.CancellationToken ) );

        LookupSagaState stored = (await _sagaManager.GetAsync(
            SagaId, TestContext.CancellationToken ))!;
        Assert.IsNotNull( stored.RateLimitInfo );
        Assert.HasCount( 2, stored.RateLimitInfo );
        SupportedProviders[] providers = [.. stored.RateLimitInfo.Select( info => info.Provider )];
        Assert.Contains( SupportedProviders.Spotify, providers );
        Assert.Contains( SupportedProviders.AppleMusic, providers );
    }

    /// <summary>An older same-provider cooldown cannot displace a newer instant.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TrySetRateLimitInfoAsync_SameProvider_PreservesLatestInstant( ) {
        const string SagaId = "rate-limit-same-provider-merge";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:RATELIMITMERGE2",
            LookupRequestType.IsrcLookup,
            "RATELIMITMERGE2",
            cancellationToken: TestContext.CancellationToken );
        DateTimeOffset later = DateTimeOffset.UtcNow.AddMinutes( 10 );
        DateTimeOffset earlierWithLargerClockText = later
            .AddMinutes( -1 )
            .ToOffset( TimeSpan.FromHours( 8 ) );

        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId,
            [new ProviderRateLimitInfo( SupportedProviders.Spotify, later, "tracks" )],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId,
            [new ProviderRateLimitInfo( SupportedProviders.Spotify, earlierWithLargerClockText, "albums" )],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );

        LookupSagaState stored = (await _sagaManager.GetAsync(
            SagaId, TestContext.CancellationToken ))!;
        Assert.IsNotNull( stored.RateLimitInfo );
        Assert.HasCount( 1, stored.RateLimitInfo );
        Assert.AreEqual( later.ToUnixTimeMilliseconds( ),
            stored.RateLimitInfo[0].RetryAfter.ToUnixTimeMilliseconds( ) );
        Assert.AreEqual( "tracks", stored.RateLimitInfo[0].Endpoint );
    }

    /// <summary>A newer same-provider cooldown replaces the previously stored instant.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TrySetRateLimitInfoAsync_SameProvider_NewerInstantReplacesOlder( ) {
        const string SagaId = "rate-limit-same-provider-replacement";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:RATELIMITMERGE4",
            LookupRequestType.IsrcLookup,
            "RATELIMITMERGE4",
            cancellationToken: TestContext.CancellationToken );
        DateTimeOffset earlier = DateTimeOffset.UtcNow.AddMinutes( 2 );
        DateTimeOffset later = earlier.AddMinutes( 4 );

        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId,
            [new ProviderRateLimitInfo( SupportedProviders.Spotify, earlier, "tracks" )],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId,
            [new ProviderRateLimitInfo( SupportedProviders.Spotify, later, "albums" )],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );

        LookupSagaState stored = (await _sagaManager.GetAsync(
            SagaId, TestContext.CancellationToken ))!;
        Assert.IsNotNull( stored.RateLimitInfo );
        Assert.HasCount( 1, stored.RateLimitInfo );
        Assert.AreEqual( later.ToUnixTimeMilliseconds( ),
            stored.RateLimitInfo[0].RetryAfter.ToUnixTimeMilliseconds( ) );
        Assert.AreEqual( "albums", stored.RateLimitInfo[0].Endpoint );
    }

    /// <summary>An empty atomic merge is rejected before it can persist a JSON object in place of a list.</summary>
    [TestMethod]
    public async Task TrySetRateLimitInfoAsync_EmptyList_ShouldReject( ) {
        const string SagaId = "rate-limit-empty-merge";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:RATELIMITMERGE3",
            LookupRequestType.IsrcLookup,
            "RATELIMITMERGE3",
            cancellationToken: TestContext.CancellationToken );

        _ = await Assert.ThrowsExactlyAsync<ArgumentException>( ( ) =>
            _sagaManager.TrySetRateLimitInfoAsync(
                SagaId, [], saga.InstanceToken!, TestContext.CancellationToken ) );
    }

    /// <summary>Malformed advisory rate-limit JSON does not make the rest of a saga unreadable.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetAsync_MalformedRateLimitInfo_IgnoresAuxiliaryField( ) {
        const string SagaId = "malformed-rate-limit-info";
        LookupSagaState created = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:MALFORMED-RATE-LIMIT",
            LookupRequestType.IsrcLookup,
            "MALFORMED-RATE-LIMIT",
            cancellationToken: TestContext.CancellationToken );
        _ = await s_redis!.GetDatabase( ).HashSetAsync(
            $"saga:{SagaId}",
            "rateLimitInfo",
            "{not-json" );

        LookupSagaState? loaded = await _sagaManager.GetAsync(
            SagaId, TestContext.CancellationToken );

        Assert.IsNotNull( loaded );
        Assert.AreEqual( created.InstanceToken, loaded.InstanceToken );
        Assert.IsNull( loaded.RateLimitInfo );
    }

    /// <summary>An explicitly stale finalize lease is replaced with a fresh instance and clean provider state.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExplicitlyStaleFinalizeClaim_IsReplacedWithoutProviderContamination( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( "isrc:STALE-FINALIZE" );
        IDatabase db = s_redis!.GetDatabase( );
        await db.HashSetAsync( $"saga:{sagaId}", [
            new HashEntry( "lookupKey", "isrc:STALE-FINALIZE" ),
            new HashEntry( "lookupType", "IsrcLookup" ),
            new HashEntry( "lookupValue", "STALE-FINALIZE" ),
            new HashEntry( "finalizeClaimed", "True" ),
            new HashEntry( "finalizeClaimedAt", DateTimeOffset.UtcNow.AddHours( -1 ).ToUnixTimeMilliseconds( ) ),
            new HashEntry( "instanceToken", "stale-instance" ) ] );
        await db.HashSetAsync( $"saga:{sagaId}:provider:Spotify", [
            new HashEntry( "isComplete", "True" ), new HashEntry( "isSuccess", "True" ),
            new HashEntry( "resultJson", "stale-result" ), new HashEntry( "completedAt", "" ),
            new HashEntry( "errorMessage", "" ) ] );

        LookupSagaState replacement = await _sagaManager.GetOrCreateAsync(
            sagaId, "isrc:STALE-FINALIZE", LookupRequestType.IsrcLookup, "STALE-FINALIZE",
            cancellationToken: TestContext.CancellationToken );

        Assert.AreNotEqual( "stale-instance", replacement.InstanceToken );
        Assert.IsEmpty( replacement.ProviderStates );
        Assert.IsNull( replacement.FinalResultUri );
    }

    /// <summary>A fresh finalize claim is not mistaken for a leftover during get-or-create.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task FreshFinalizeClaim_IsNotReplaced( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( "isrc:FRESH-FINALIZE" );
        IDatabase db = s_redis!.GetDatabase( );
        await db.HashSetAsync( $"saga:{sagaId}", [
            new HashEntry( "lookupKey", "isrc:FRESH-FINALIZE" ),
            new HashEntry( "lookupType", "IsrcLookup" ),
            new HashEntry( "lookupValue", "FRESH-FINALIZE" ),
            new HashEntry( "finalizeClaimed", "True" ),
            new HashEntry( "finalizeClaimedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds( ) ),
            new HashEntry( "instanceToken", "fresh-instance" ) ] );

        LookupSagaState current = await _sagaManager.GetOrCreateAsync(
            sagaId, "isrc:FRESH-FINALIZE", LookupRequestType.IsrcLookup, "FRESH-FINALIZE",
            cancellationToken: TestContext.CancellationToken );

        Assert.AreEqual( "fresh-instance", current.InstanceToken );
        Assert.AreEqual( "True", (await db.HashGetAsync( $"saga:{sagaId}", "finalizeClaimed" )).ToString( ) );
    }

    /// <summary>A malformed persisted timestamp follows the typed unreadable-saga path.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetAsync_MalformedCreatedAt_ThrowsUnreadableSagaState( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( "isrc:MALFORMED-TIMESTAMP" );
        await s_redis!.GetDatabase( ).HashSetAsync( $"saga:{sagaId}", [
            new HashEntry( "lookupKey", "isrc:MALFORMED-TIMESTAMP" ),
            new HashEntry( "lookupType", "IsrcLookup" ),
            new HashEntry( "lookupValue", "MALFORMED-TIMESTAMP" ),
            new HashEntry( "createdAt", "not-a-timestamp" ),
            new HashEntry( "instanceToken", "malformed-instance" ) ] );

        _ = await Assert.ThrowsAsync<BridgeBeats.Contracts.Exceptions.UnreadableSagaStateException>(
            ( ) => _sagaManager.GetAsync( sagaId, TestContext.CancellationToken ) );
    }

    /// <summary>A finalized saga's durable result URI is never replaced by a later get-or-create.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task FinalizedSaga_GetOrCreate_NeverReplacesFinalResultUri( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( "isrc:FINALIZED-URI" );
        IDatabase db = s_redis!.GetDatabase( );
        await db.HashSetAsync( $"saga:{sagaId}", [
            new HashEntry( "lookupKey", "isrc:FINALIZED-URI" ),
            new HashEntry( "lookupType", "IsrcLookup" ),
            new HashEntry( "lookupValue", "FINALIZED-URI" ),
            new HashEntry( "finalizeClaimed", "True" ),
            new HashEntry( "finalizeClaimedAt", DateTimeOffset.UtcNow.AddHours( -1 ).ToUnixTimeMilliseconds( ) ),
            new HashEntry( "finalResultUri", "at://durable/final" ),
            new HashEntry( "instanceToken", "final-instance" ) ] );

        LookupSagaState current = await _sagaManager.GetOrCreateAsync(
            sagaId, "isrc:FINALIZED-URI", LookupRequestType.IsrcLookup, "FINALIZED-URI",
            cancellationToken: TestContext.CancellationToken );

        Assert.AreEqual( "at://durable/final", current.FinalResultUri );
        Assert.AreEqual( "final-instance", current.InstanceToken );
    }

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
    /// Verifies get-or-create returns a new saga populated with the supplied key, type, and value, with
    /// empty provider states and no result URIs.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetOrCreateAsync_CreatesNewSaga( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        // Act
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.AreEqual( sagaId, saga.SagaId );
        Assert.AreEqual( lookupKey, saga.LookupKey );
        Assert.AreEqual( LookupRequestType.IsrcLookup, saga.LookupType );
        Assert.AreEqual( "USRC12345678", saga.LookupValue );
        Assert.IsEmpty( saga.ProviderStates );
        Assert.IsNull( saga.PartialResultUri );
        Assert.IsNull( saga.FinalResultUri );
        Assert.IsFalse( saga.IsComplete );
    }

    /// <summary>
    /// Verifies get-or-create for an existing saga id returns the original saga and ignores the
    /// differing key, type, and value passed on the second call.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetOrCreateAsync_ReturnsExistingSaga_WhenAlreadyExists( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        LookupSagaState original = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Act - try to create again with different values
        LookupSagaState second = await _sagaManager.GetOrCreateAsync(
            sagaId,
            "different-key",
            LookupRequestType.UpcLookup,
            "DIFFERENT",
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - should return original, not new values
        Assert.AreEqual( original.SagaId, second.SagaId );
        Assert.AreEqual( original.LookupKey, second.LookupKey );
        Assert.AreEqual( original.LookupType, second.LookupType );
    }

    /// <summary>
    /// Verifies fetching a saga that does not exist returns null.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetAsync_ReturnsNull_WhenSagaDoesNotExist( ) {
        // Act
        LookupSagaState? saga = await _sagaManager.GetAsync( "nonexistent-saga-id", TestContext.CancellationToken );

        // Assert
        Assert.IsNull( saga );
    }

    /// <summary>
    /// Verifies updating a provider's state persists it so a subsequent fetch returns the stored
    /// completion, success, and result JSON.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task UpdateProviderStateAsync_StoresProviderState( ) {
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

        ProviderLookupState providerState = new(
            Provider: SupportedProviders.Spotify,
            IsComplete: true,
            IsSuccess: true,
            ResultJson: """{"trackId": "abc123"}""",
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: null
        );

        // Act
        await _sagaManager.UpdateProviderStateAsync( sagaId, providerState, TestContext.CancellationToken );

        // Assert
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.HasCount( 1, saga.ProviderStates );
        Assert.IsTrue( saga.ProviderStates.ContainsKey( SupportedProviders.Spotify ) );

        ProviderLookupState retrieved = saga.ProviderStates[SupportedProviders.Spotify];
        Assert.IsTrue( retrieved.IsComplete );
        Assert.IsTrue( retrieved.IsSuccess );
        Assert.AreEqual( """{"trackId": "abc123"}""", retrieved.ResultJson );
    }

    /// <summary>
    /// Verifies updating two providers' states stores both, preserving their distinct success and error
    /// values.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task UpdateProviderStateAsync_UpdatesMultipleProviders( ) {
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

        ProviderLookupState spotifyState = new(
            Provider: SupportedProviders.Spotify,
            IsComplete: true,
            IsSuccess: true,
            ResultJson: """{"spotifyId": "123"}""",
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: null
        );

        ProviderLookupState appleState = new(
            Provider: SupportedProviders.AppleMusic,
            IsComplete: true,
            IsSuccess: false,
            ResultJson: null,
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: "Rate limited"
        );

        // Act
        await _sagaManager.UpdateProviderStateAsync( sagaId, spotifyState, TestContext.CancellationToken );
        await _sagaManager.UpdateProviderStateAsync( sagaId, appleState, TestContext.CancellationToken );

        // Assert
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.HasCount( 2, saga.ProviderStates );

        Assert.IsTrue( saga.ProviderStates[SupportedProviders.Spotify].IsSuccess );
        Assert.IsFalse( saga.ProviderStates[SupportedProviders.AppleMusic].IsSuccess );
        Assert.AreEqual( "Rate limited", saga.ProviderStates[SupportedProviders.AppleMusic].ErrorMessage );
    }

    /// <summary>
    /// Verifies setting the partial result URI stores it without setting the final result URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetPartialResultUriAsync_StoresUri( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );
        string partialUri = "at://did:plc:test/link.bridgebeats.lookup/track:USRC12345678";

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Act
        await _sagaManager.SetPartialResultUriAsync( sagaId, partialUri, TestContext.CancellationToken );

        // Assert
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.AreEqual( partialUri, saga.PartialResultUri );
        Assert.IsNull( saga.FinalResultUri );
    }

    /// <summary>
    /// Verifies setting the final result URI stores it on the saga.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetFinalResultUriAsync_StoresUri( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );
        string finalUri = "at://did:plc:test/link.bridgebeats.lookup/track:USRC12345678";

        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        // Act
        await _sagaManager.SetFinalResultUriAsync( sagaId, finalUri, TestContext.CancellationToken );

        // Assert
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.AreEqual( finalUri, saga.FinalResultUri );
    }

    /// <summary>The fenced final URI remains first-writer-wins and is idempotent for the same URI.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TrySetFinalResultUriAsync_PreservesFirstWriter( ) {
        string lookupKey = "isrc:FIRSTWRITER0001";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            sagaId, lookupKey, LookupRequestType.IsrcLookup, "FIRSTWRITER0001",
            cancellationToken: TestContext.CancellationToken );

        SagaFinalResultWriteOutcome first = await _sagaManager.TrySetFinalResultUriAsync(
            sagaId, "at://did:plc:test/link/first", saga.InstanceToken!, TestContext.CancellationToken );
        SagaFinalResultWriteOutcome idempotentReplay = await _sagaManager.TrySetFinalResultUriAsync(
            sagaId, "at://did:plc:test/link/first", saga.InstanceToken!, TestContext.CancellationToken );
        SagaFinalResultWriteOutcome conflictingWriter = await _sagaManager.TrySetFinalResultUriAsync(
            sagaId, "at://did:plc:test/link/second", saga.InstanceToken!, TestContext.CancellationToken );

        Assert.AreEqual( SagaFinalResultWriteOutcome.Stored, first );
        Assert.AreEqual( SagaFinalResultWriteOutcome.Idempotent, idempotentReplay );
        Assert.AreEqual( SagaFinalResultWriteOutcome.Conflict, conflictingWriter );
        Assert.AreEqual( "at://did:plc:test/link/first",
            (await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken ))!.FinalResultUri );
    }

    /// <summary>Partial-result mutations renew the core hash TTL just like other saga writes.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PartialResultMutations_RefreshSagaTtl( ) {
        const string SagaId = "partial-ttl-refresh";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId, "isrc:PARTIALTTL001", LookupRequestType.IsrcLookup, "PARTIALTTL001",
            cancellationToken: TestContext.CancellationToken );
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.KeyExpireAsync( $"saga:{SagaId}", TimeSpan.FromSeconds( 2 ) );

        Assert.IsTrue( await _sagaManager.TrySetPartialResultUriAsync(
            SagaId, "at://did:plc:test/link/partial", saga.InstanceToken!, TestContext.CancellationToken ) );
        TimeSpan? afterUri = await db.KeyTimeToLiveAsync( $"saga:{SagaId}" );
        Assert.IsNotNull( afterUri );
        Assert.IsGreaterThan( TimeSpan.FromMinutes( 50 ), afterUri.Value );

        _ = await db.KeyExpireAsync( $"saga:{SagaId}", TimeSpan.FromSeconds( 2 ) );
        Assert.IsTrue( await _sagaManager.TrySetIsPartialAsync(
            SagaId, true, saga.InstanceToken!, TestContext.CancellationToken ) );
        TimeSpan? afterFlag = await db.KeyTimeToLiveAsync( $"saga:{SagaId}" );
        Assert.IsNotNull( afterFlag );
        Assert.IsGreaterThan( TimeSpan.FromMinutes( 50 ), afterFlag.Value );
    }

    /// <summary>Marker, claim-release, and generation-reset writes all renew the core saga TTL.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GuardedControlMutations_RefreshSagaTtl( ) {
        const string SagaId = "guarded-control-ttl-refresh";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId, "isrc:CONTROLTTL001", LookupRequestType.IsrcLookup, "CONTROLTTL001",
            cancellationToken: TestContext.CancellationToken );
        string token = saga.InstanceToken!;
        IDatabase db = s_redis!.GetDatabase( );
        string key = $"saga:{SagaId}";

        _ = await db.KeyExpireAsync( key, TimeSpan.FromSeconds( 2 ) );
        Assert.IsTrue( await _sagaManager.TryMarkSecondariesQueuedAsync(
            SagaId, token, TestContext.CancellationToken ) );
        await AssertTtlRenewedAsync( db, key );

        Assert.IsTrue( await _sagaManager.TryClaimFinalizeAsync(
            SagaId, token, TestContext.CancellationToken ) );
        _ = await db.KeyExpireAsync( key, TimeSpan.FromSeconds( 2 ) );
        Assert.IsTrue( await _sagaManager.TryReleaseFinalizeClaimAsync(
            SagaId, token, TestContext.CancellationToken ) );
        await AssertTtlRenewedAsync( db, key );

        Assert.IsTrue( await _sagaManager.TryAdvanceWriteGenerationAsync(
            SagaId, 1, token, TestContext.CancellationToken ) );
        _ = await db.KeyExpireAsync( key, TimeSpan.FromSeconds( 2 ) );
        Assert.IsTrue( await _sagaManager.TryResetWriteGenerationAsync(
            SagaId, 1, 0, token, TestContext.CancellationToken ) );
        await AssertTtlRenewedAsync( db, key );
    }

    private static async Task AssertTtlRenewedAsync( IDatabase db, string key ) {
        TimeSpan? ttl = await db.KeyTimeToLiveAsync( key );
        Assert.IsNotNull( ttl );
        Assert.IsGreaterThan( TimeSpan.FromMinutes( 50 ), ttl.Value );
    }

    /// <summary>
    /// Verifies deleting a saga returns true and removes the saga and its provider states so a
    /// subsequent fetch returns null.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DeleteAsync_RemovesSagaAndProviderStates( ) {
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

        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null
        ), TestContext.CancellationToken );

        // Verify exists
        LookupSagaState? beforeDelete = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( beforeDelete );

        // Act
        bool deleted = await _sagaManager.DeleteAsync( sagaId, TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( deleted );

        LookupSagaState? afterDelete = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNull( afterDelete );
    }

    /// <summary>
    /// Verifies deleting a saga that does not exist returns false.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DeleteAsync_ReturnsFalse_WhenSagaDoesNotExist( ) {
        // Act
        bool deleted = await _sagaManager.DeleteAsync( "nonexistent-saga", TestContext.CancellationToken );

        // Assert
        Assert.IsFalse( deleted );
    }

    /// <summary>
    /// Verifies saga-id generation is deterministic and case-insensitive on the lookup key, differs for
    /// different keys, and produces a 32-character lowercase hex id.
    /// </summary>
    [TestMethod]
    public void GenerateSagaId_CreatesDeterministicId( ) {
        // Arrange
        string lookupKey1 = "isrc:USRC12345678";
        string lookupKey2 = "isrc:usrc12345678"; // Different case
        string lookupKey3 = "isrc:DIFFERENTISRC";

        // Act
        string id1a = ISagaStateManager.GenerateSagaId( lookupKey1 );
        string id1b = ISagaStateManager.GenerateSagaId( lookupKey1 );
        string id2 = ISagaStateManager.GenerateSagaId( lookupKey2 );
        string id3 = ISagaStateManager.GenerateSagaId( lookupKey3 );

        // Assert - same key produces same ID
        Assert.AreEqual( id1a, id1b );

        // Case insensitive normalization
        Assert.AreEqual( id1a, id2 );

        // Different key produces different ID
        Assert.AreNotEqual( id1a, id3 );

        // ID is 32 hex chars (16 bytes of SHA256)
        Assert.AreEqual( 32, id1a.Length );
        Assert.IsTrue( id1a.All( c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') ) );
    }

    /// <summary>
    /// Verifies a saga reports incomplete while any provider is incomplete and complete once all
    /// providers have completed.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Saga_IsComplete_WhenAllProvidersComplete( ) {
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

        // Add incomplete provider
        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            SupportedProviders.Spotify, false, false, null, null, null
        ), TestContext.CancellationToken );

        LookupSagaState? incomplete = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( incomplete );
        Assert.IsFalse( incomplete.IsComplete );

        // Act - mark provider as complete
        await _sagaManager.UpdateProviderStateAsync( sagaId, new ProviderLookupState(
            SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null
        ), TestContext.CancellationToken );

        // Assert
        LookupSagaState? complete = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( complete );
        Assert.IsTrue( complete.IsComplete );
    }

    /// <summary>
    /// Verifies the origin priority recorded by the first writer is persisted and not overwritten by a
    /// later resume with a different priority.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetOrCreateAsync_PersistsOriginPriority_FirstWriterWins( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        // Act - create with Bulk origin, then resume with Interactive
        LookupSagaState created = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            QueuePriority.Bulk,
            cancellationToken: TestContext.CancellationToken
        );

        LookupSagaState resumed = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            QueuePriority.Interactive,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - first recorded origin priority is kept
        Assert.AreEqual( QueuePriority.Bulk, created.OriginPriority );
        Assert.AreEqual( QueuePriority.Bulk, resumed.OriginPriority );

        LookupSagaState? fetched = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( fetched );
        Assert.AreEqual( QueuePriority.Bulk, fetched.OriginPriority );
    }

    /// <summary>
    /// Verifies a saga created without an explicit origin priority defaults to Background, and a later
    /// resume that supplies a priority records it.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetOrCreateAsync_WithoutOriginPriority_DefaultsToBackgroundUntilRecorded( ) {
        // Arrange
        string lookupKey = "isrc:USRC12345678";
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        // Act - create without an origin priority (orchestrator path)
        _ = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            cancellationToken: TestContext.CancellationToken
        );

        LookupSagaState? beforeRecorded = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );

        // Resume with the first request's priority (worker path)
        LookupSagaState resumed = await _sagaManager.GetOrCreateAsync(
            sagaId,
            lookupKey,
            LookupRequestType.IsrcLookup,
            "USRC12345678",
            QueuePriority.Interactive,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.IsNotNull( beforeRecorded );
        Assert.AreEqual( QueuePriority.Background, beforeRecorded.OriginPriority );
        Assert.AreEqual( QueuePriority.Interactive, resumed.OriginPriority );
    }

    /// <summary>
    /// Verifies the secondaries-queued guard succeeds for the first caller and fails for subsequent
    /// callers.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryMarkSecondariesQueuedAsync_OnlyFirstCallerWins( ) {
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

        // Act
        bool first = await _sagaManager.TryMarkSecondariesQueuedAsync( sagaId, TestContext.CancellationToken );
        bool second = await _sagaManager.TryMarkSecondariesQueuedAsync( sagaId, TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( first );
        Assert.IsFalse( second );
    }

    /// <summary>
    /// Verifies initializing provider states adds entries for any missing providers while preserving the
    /// already-completed state of existing ones.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryInitializeProviderStatesAsync_PreservesExistingProviderState( ) {
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

        ProviderLookupState completedState = new(
            Provider: SupportedProviders.Spotify,
            IsComplete: true,
            IsSuccess: true,
            ResultJson: """{"trackId": "abc123"}""",
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: null
        );

        await _sagaManager.UpdateProviderStateAsync( sagaId, completedState, TestContext.CancellationToken );

        // Act - re-initialize including the already-completed provider
        await _sagaManager.TryInitializeProviderStatesAsync(
            sagaId,
            [SupportedProviders.Spotify, SupportedProviders.AppleMusic],
            TestContext.CancellationToken
        );

        // Assert - completed provider is untouched, missing provider is created pending
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.HasCount( 2, saga.ProviderStates );

        ProviderLookupState spotify = saga.ProviderStates[SupportedProviders.Spotify];
        Assert.IsTrue( spotify.IsComplete );
        Assert.IsTrue( spotify.IsSuccess );
        Assert.AreEqual( """{"trackId": "abc123"}""", spotify.ResultJson );

        ProviderLookupState apple = saga.ProviderStates[SupportedProviders.AppleMusic];
        Assert.IsFalse( apple.IsComplete );
        Assert.IsFalse( apple.IsSuccess );
        Assert.IsNull( apple.ResultJson );
    }

    /// <summary>Provider initialization renews its existing leg without extending the core saga.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryInitializeProviderStatesAsync_ExistingProvider_RefreshesOnlyProviderTtl( ) {
        const string SagaId = "provider-init-does-not-refresh";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:TTLPROVIDER001",
            LookupRequestType.IsrcLookup,
            "TTLPROVIDER001",
            cancellationToken: TestContext.CancellationToken );
        Assert.IsTrue( await _sagaManager.TryInitializeProviderStatesAsync(
            SagaId,
            [SupportedProviders.Spotify],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );
        IDatabase db = s_redis!.GetDatabase( );
        string sagaKey = $"saga:{SagaId}";
        string providerKey = $"saga:{SagaId}:provider:{SupportedProviders.Spotify}";
        _ = await db.KeyExpireAsync( sagaKey, TimeSpan.FromSeconds( 20 ) );
        _ = await db.KeyExpireAsync( providerKey, TimeSpan.FromSeconds( 20 ) );

        Assert.IsTrue( await _sagaManager.TryInitializeProviderStatesAsync(
            SagaId,
            [SupportedProviders.Spotify],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );

        TimeSpan? sagaTtl = await db.KeyTimeToLiveAsync( sagaKey );
        TimeSpan? providerTtl = await db.KeyTimeToLiveAsync( providerKey );
        Assert.IsNotNull( sagaTtl );
        Assert.IsNotNull( providerTtl );
        Assert.IsLessThan( TimeSpan.FromMinutes( 1 ), sagaTtl.Value );
        Assert.IsGreaterThan( TimeSpan.FromMinutes( 1 ), providerTtl.Value );
    }

    /// <summary>Rate-limit deferral renews the core after initialization has renewed the provider leg.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RateLimitDeferral_RenewsCoreAndProviderLegTogether( ) {
        const string SagaId = "provider-deferral-refreshes-both";
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            SagaId,
            "isrc:TTLDEFERRAL001",
            LookupRequestType.IsrcLookup,
            "TTLDEFERRAL001",
            cancellationToken: TestContext.CancellationToken );
        Assert.IsTrue( await _sagaManager.TryInitializeProviderStatesAsync(
            SagaId,
            [SupportedProviders.Spotify],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );

        IDatabase db = s_redis!.GetDatabase( );
        string sagaKey = $"saga:{SagaId}";
        string providerKey = $"saga:{SagaId}:provider:{SupportedProviders.Spotify}";
        _ = await db.KeyExpireAsync( sagaKey, TimeSpan.FromSeconds( 20 ) );
        _ = await db.KeyExpireAsync( providerKey, TimeSpan.FromSeconds( 20 ) );

        Assert.IsTrue( await _sagaManager.TryInitializeProviderStatesAsync(
            SagaId,
            [SupportedProviders.Spotify],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TrySetIsPartialAsync(
            SagaId,
            true,
            saga.InstanceToken!,
            TestContext.CancellationToken ) );
        Assert.IsTrue( await _sagaManager.TrySetRateLimitInfoAsync(
            SagaId,
            [new ProviderRateLimitInfo(
                SupportedProviders.Spotify,
                DateTimeOffset.UtcNow.AddMinutes( 1 ),
                ProviderEndpointConstants.ProviderWide )],
            saga.InstanceToken!,
            TestContext.CancellationToken ) );

        TimeSpan? sagaTtl = await db.KeyTimeToLiveAsync( sagaKey );
        TimeSpan? providerTtl = await db.KeyTimeToLiveAsync( providerKey );
        Assert.IsNotNull( sagaTtl );
        Assert.IsNotNull( providerTtl );
        Assert.IsGreaterThan( TimeSpan.FromMinutes( 1 ), sagaTtl.Value );
        Assert.IsGreaterThan( TimeSpan.FromMinutes( 1 ), providerTtl.Value );
    }
}
