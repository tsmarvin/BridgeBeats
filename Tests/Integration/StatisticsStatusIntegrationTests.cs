using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Web.Services;
using BridgeBeats.Worker.Maintenance;
using BridgeBeats.Worker.Maintenance.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the statistics status document written by
/// <see cref="CacheBootstrapBackgroundService"/> to a real Redis instance (Testcontainers).
/// <list type="bullet">
///   <item>I-1: Single-enumeration dedup — <see cref="IATProtoStorageService.ListAllRecordsAsync"/>
///   is called exactly once per bootstrap pass (bootstrap + statistics fold share one
///   enumeration).</item>
///   <item>I-2: No-expiry — the statistics status key has no TTL after a bootstrap run, while the
///   bootstrap status key carries a TTL.</item>
///   <item>I-3: Cross-process round-trip — publish to <c>stats:refresh-requested</c> causes the
///   subscriber to trigger a bootstrap pass, after which <see cref="RedisStatisticsReader"/>
///   reflects the updated snapshot. Also covers the startup-race variant (publish before
///   subscribe).</item>
///   <item>I-4: Page recovers — writing failure and success <see cref="StatisticsStatus"/>
///   documents to Redis and verifying <see cref="RedisStatisticsReader"/> reflects each after
///   the memo window expires.</item>
/// </list>
/// Requires Docker to be running on the host machine.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
public class StatisticsStatusIntegrationTests {

    private static IConnectionMultiplexer? s_redis;

    /// <summary>MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    private static readonly Uri s_pdsUri = new( "https://pds.test.example" );
    private const string TestUserDid = "did:plc:statsintegration";

    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        BridgeBeats.Tests.SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync(
            BridgeBeats.Tests.SharedTestInfrastructure.RedisConnectionString );
    }

    [ClassCleanup]
    public static async Task ClassCleanup( ) {
        if (s_redis is not null) {
            await s_redis.CloseAsync( );
            s_redis.Dispose( );
        }
    }

    [TestInitialize]
    public async Task TestInitialize( ) {
        // Remove any leftover keys from prior test runs.
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.KeyDeleteAsync( CacheBootstrapStatus.RedisKey );
        _ = await db.KeyDeleteAsync( StatisticsStatus.RedisKey );
    }

    #region I-1 — Dedup: ListAllRecordsAsync called exactly once per bootstrap pass

    /// <summary>
    /// I-1: One bootstrap pass calls <see cref="IATProtoStorageService.ListAllRecordsAsync"/> exactly
    /// once. Both the bootstrap loop and the statistics fold consume records from the same enumeration;
    /// they do NOT trigger a second download.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnePass_CallsListAllRecordsExactlyOnce( ) {
        // Arrange: mock storage returning one record.
        Mock<IATProtoStorageService> storageMock = new( );
        Mock<IMediaLinkCacheRepository> cacheMock = new( );
        Mock<IStatisticsRefreshTrigger> triggerMock = new( );
        Mock<ILogger<CacheBootstrapBackgroundService>> loggerMock = new( );

        _ = triggerMock.Setup( t => t.TryAcquire( ) ).Returns( true );
        _ = cacheMock
            .Setup( c => c.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ) )
            .Returns( Task.CompletedTask );

        (string, MediaLinkResult)[] records = [BuildRecord( "at://did/post/aaaaaaa" )];
        _ = storageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( records.ToAsyncEnumerable( ) );

        CacheBootstrapSettings settings = BuildSettings( );
        CacheBootstrapBackgroundService service = new(
            storageMock.Object,
            cacheMock.Object,
            s_redis!,
            settings,
            triggerMock.Object,
            loggerMock.Object,
            statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 )
        );

        // Act: start and wait until the statistics status doc is written with IsRunning=false.
        TaskCompletionSource statsDoneTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        using PollingRedisTcs statsDonePoller = new(
            s_redis!.GetDatabase( ),
            StatisticsStatus.RedisKey,
            doc => doc?.GetProperty( "IsRunning" ).GetBoolean( ) == false,
            statsDoneTcs );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await statsDoneTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert: exactly one enumeration.
        storageMock.Verify(
            s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.Once( ) );

        // Cache was written once for the one record.
        cacheMock.Verify(
            c => c.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ),
            Times.Once( ) );
    }

    #endregion

    #region I-2 — No-expiry: statistics key has no TTL; bootstrap key has a TTL

    /// <summary>
    /// I-2: After a bootstrap pass, the statistics status key (<see cref="StatisticsStatus.RedisKey"/>)
    /// must have no TTL (i.e., <c>db.KeyTimeToLive</c> returns <see langword="null"/>). The bootstrap
    /// status key (<see cref="CacheBootstrapStatus.RedisKey"/>) must have a positive TTL.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_StatisticsStatusKey_HasNoTtl_BootstrapStatusKey_HasTtl( ) {
        // Arrange.
        Mock<IATProtoStorageService> storageMock = new( );
        Mock<IMediaLinkCacheRepository> cacheMock = new( );
        Mock<IStatisticsRefreshTrigger> triggerMock = new( );
        Mock<ILogger<CacheBootstrapBackgroundService>> loggerMock = new( );

        _ = triggerMock.Setup( t => t.TryAcquire( ) ).Returns( true );
        _ = cacheMock
            .Setup( c => c.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ) )
            .Returns( Task.CompletedTask );
        _ = storageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );

        CacheBootstrapSettings settings = BuildSettings( );
        CacheBootstrapBackgroundService service = new(
            storageMock.Object,
            cacheMock.Object,
            s_redis!,
            settings,
            triggerMock.Object,
            loggerMock.Object,
            statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 )
        );

        // Act: wait until statistics doc is committed.
        TaskCompletionSource statsDoneTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        using PollingRedisTcs statsDonePoller = new(
            s_redis!.GetDatabase( ),
            StatisticsStatus.RedisKey,
            doc => doc?.GetProperty( "IsRunning" ).GetBoolean( ) == false,
            statsDoneTcs );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await statsDoneTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert: statistics key has no TTL.
        IDatabase db = s_redis.GetDatabase( );
        TimeSpan? statsTtl = await db.KeyTimeToLiveAsync( StatisticsStatus.RedisKey );
        Assert.IsNull( statsTtl, $"Statistics key must have no TTL; actual TTL: {statsTtl}" );

        // Assert: bootstrap key has a positive TTL.
        TimeSpan? bootstrapTtl = await db.KeyTimeToLiveAsync( CacheBootstrapStatus.RedisKey );
        Assert.IsNotNull( bootstrapTtl, "Bootstrap key must have a TTL" );
        Assert.IsTrue( bootstrapTtl > TimeSpan.Zero, $"Bootstrap key TTL must be positive; actual: {bootstrapTtl}" );
    }

    #endregion

    #region I-3 — Cross-process round-trip: publish → subscriber → status:statistics updated → reader reflects

    /// <summary>
    /// I-3: Publishing to <see cref="RedisChannels.StatisticsRefreshRequested"/> causes the
    /// <see cref="StatisticsRefreshSubscriberService"/> to fire, which triggers a bootstrap pass,
    /// which writes a new <c>status:statistics</c> document; <see cref="RedisStatisticsReader"/>
    /// then reflects the updated snapshot.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PublishRefreshRequested_SubscriberReceives_StatusStatisticsUpdated_ReaderReflectsIt( ) {
        // Arrange: mock storage returning one record, cache mock that accepts adds.
        Mock<IATProtoStorageService> storageMock = new( );
        Mock<IMediaLinkCacheRepository> cacheMock = new( );
        Mock<IStatisticsRefreshTrigger> triggerMock = new( );
        Mock<ILogger<CacheBootstrapBackgroundService>> bootstrapLogMock = new( );
        Mock<ILogger<StatisticsRefreshSubscriberService>> subscriberLogMock = new( );

        _ = triggerMock.Setup( t => t.TryAcquire( ) ).Returns( true );
        _ = cacheMock
            .Setup( c => c.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ) )
            .Returns( Task.CompletedTask );

        (string, MediaLinkResult)[] records = [BuildRecord( "at://did/post/i3test" )];
        _ = storageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( records.ToAsyncEnumerable( ) );

        CacheBootstrapSettings settings = BuildSettings( );
        CacheBootstrapBackgroundService bootstrapService = new(
            storageMock.Object,
            cacheMock.Object,
            s_redis!,
            settings,
            triggerMock.Object,
            bootstrapLogMock.Object,
            statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 )
        );
        StatisticsRefreshSubscriberService subscriberService = new(
            s_redis!,
            bootstrapService,
            subscriberLogMock.Object
        );

        // Wait for status:statistics to be populated (IsRunning=false = pass complete).
        TaskCompletionSource statsDoneTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        using PollingRedisTcs statsDonePoller = new(
            s_redis!.GetDatabase( ),
            StatisticsStatus.RedisKey,
            doc => doc?.GetProperty( "IsRunning" ).GetBoolean( ) == false,
            statsDoneTcs );

        using CancellationTokenSource cts = new( );

        // Start bootstrap + subscriber services.
        Task bootstrapTask = bootstrapService.StartAsync( cts.Token );
        Task subscriberTask = subscriberService.StartAsync( cts.Token );

        // Publish to the refresh channel (simulates Web publishing RequestRefresh).
        ISubscriber pub = s_redis!.GetSubscriber( );
        _ = await pub.PublishAsync(
            RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ),
            "" );

        // Wait for the status document to be updated.
        await statsDoneTcs.Task.WaitAsync( TestContext.CancellationToken );

        await cts.CancelAsync( );
        await bootstrapTask;
        await subscriberTask;

        // Assert: RedisStatisticsReader can read the written snapshot.
        FakeTimeProvider fakeTime = new( );
        RedisStatisticsReader reader = new(
            s_redis!,
            fakeTime,
            Mock.Of<ILogger<RedisStatisticsReader>>( )
        );

        LookupStatistics? snapshot = reader.GetCachedStatistics( );
        Assert.IsNotNull( snapshot, "RedisStatisticsReader must serve the snapshot written by the bootstrap pass" );
        Assert.AreEqual( 1, snapshot.TotalRecords );
        Assert.IsFalse( reader.IsRefreshing, "IsRefreshing must be false after a completed pass" );
    }

    /// <summary>
    /// I-3 startup-race: publishing before the subscriber is wired produces no error and the
    /// message is best-effort (no subscribers receive it). This verifies the publish call itself
    /// is safe even with no listeners.
    /// </summary>
    [TestMethod]
    public async Task PublishBeforeSubscribe_BestEffort_NoError( ) {
        // Arrange: publish BEFORE starting any subscriber.
        ISubscriber pub = s_redis!.GetSubscriber( );

        // Act: publish without a subscriber attached; expect no exception.
        Exception? caught = null;
        try {
            _ = await pub.PublishAsync(
                RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ),
                "" );
        } catch (Exception ex) {
            caught = ex;
        }

        Assert.IsNull( caught, "Publish to an unsubscribed channel must not throw" );
    }

    #endregion

    #region I-4 — Page recovers: reader reflects failure and success StatisticsStatus documents

    /// <summary>
    /// I-4: Writing a failure <see cref="StatisticsStatus"/> (IsRunning=false, LastError set) to
    /// Redis and then reading via <see cref="RedisStatisticsReader"/> shows <c>IsRefreshing==false</c>
    /// and a null snapshot (if no Snapshot in the document). Overwriting with a success document
    /// (IsRunning=false, Snapshot populated) and advancing past the 5-second memo window shows the
    /// new snapshot.
    /// </summary>
    [TestMethod]
    public async Task Reader_ReflectsFailureDocument_ThenRecoversWith_SuccessDocument( ) {
        IDatabase db = s_redis!.GetDatabase( );

        // Phase 1: write a failure status (no snapshot, IsRunning=false, error message set).
        StatisticsStatus failureStatus = new( ) {
            IsRunning = false,
            LastError = "Simulated failure for I-4",
            Snapshot = null
        };
        string failureJson = System.Text.Json.JsonSerializer.Serialize( failureStatus );
        _ = await db.StringSetAsync( StatisticsStatus.RedisKey, failureJson );

        FakeTimeProvider fakeTime = new( );
        RedisStatisticsReader reader = new(
            s_redis!,
            fakeTime,
            Mock.Of<ILogger<RedisStatisticsReader>>( )
        );

        Assert.IsFalse( reader.IsRefreshing, "IsRefreshing must be false during a failure state" );
        Assert.IsNull( reader.GetCachedStatistics( ), "GetCachedStatistics must return null when Snapshot is null" );

        // Phase 2: overwrite with a success document.
        LookupStatistics snapshot = new( ) {
            TotalRecords = 42,
            GeneratedAt = DateTimeOffset.UtcNow
        };
        StatisticsStatus successStatus = new( ) {
            IsRunning = false,
            Snapshot = snapshot
        };
        string successJson = System.Text.Json.JsonSerializer.Serialize( successStatus );
        _ = await db.StringSetAsync( StatisticsStatus.RedisKey, successJson );

        // Advance past the 5-second memo window so the next read goes to Redis.
        fakeTime.Advance( TimeSpan.FromSeconds( 6 ) );

        LookupStatistics? recovered = reader.GetCachedStatistics( );
        Assert.IsNotNull( recovered, "GetCachedStatistics must serve the updated snapshot after memo expiry" );
        Assert.AreEqual( 42, recovered.TotalRecords );
    }

    #endregion

    #region Helper types and methods

    private static CacheBootstrapSettings BuildSettings( ) =>
        new( s_pdsUri, TestUserDid, TimeSpan.FromHours( 6 ),
            CacheDays: 30, RefreshInterval: TimeSpan.FromHours( 24 ), MaxRecordsPerRun: 100,
            RefreshRetryInterval: TimeSpan.FromMinutes( 5 ) );

    private static (string AtUri, MediaLinkResult Result) BuildRecord( string atUri ) {
        MediaLinkResult result = new( ) { LookedUpAt = DateTime.UtcNow };
        result.Results.Add( BridgeBeats.Contracts.Enums.SupportedProviders.Spotify, new MusicLookupResult {
            Artist = "Test",
            Title = "Track",
            IsAlbum = false,
            ExternalId = "ISRC001",
            URL = "https://open.spotify.com/track/001"
        } );
        return (atUri, result);
    }

    /// <summary>
    /// Polls a Redis key every 100 ms until the stored JSON document satisfies a predicate, then
    /// signals a <see cref="TaskCompletionSource"/>. Implements <see cref="IDisposable"/> to cancel
    /// the polling loop.
    /// </summary>
    private sealed class PollingRedisTcs : IDisposable {
        private readonly CancellationTokenSource _cts = new( );

        public PollingRedisTcs(
            IDatabase db,
            string key,
            Func<JsonElement?, bool> predicate,
            TaskCompletionSource tcs ) {
            _ = Task.Run( async ( ) => {
                while (!_cts.Token.IsCancellationRequested) {
                    try {
                        RedisValue raw = await db.StringGetAsync( key );
                        if (raw.HasValue) {
                            JsonDocument doc = JsonDocument.Parse( raw.ToString( ) );
                            if (predicate( doc.RootElement )) {
                                _ = tcs.TrySetResult( );
                                return;
                            }
                        }
                    } catch { }
                    await Task.Delay( 100, _cts.Token );
                }
            }, _cts.Token );
        }

        public void Dispose( ) => _cts.Cancel( );
    }

    #endregion
}
