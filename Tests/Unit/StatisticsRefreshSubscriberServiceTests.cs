using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Worker.Maintenance;
using BridgeBeats.Worker.Maintenance.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

#pragma warning disable CS1591
#pragma warning disable CA1873 // Moq Verify lambdas that call ILogger.Log trigger this; the lambdas are never actually executed as logging calls

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="StatisticsRefreshSubscriberService"/>: the hosted service that
/// subscribes to the <c>stats:refresh-requested</c> Redis Pub/Sub channel and delegates to
/// <see cref="CacheBootstrapBackgroundService.TriggerStatisticsRefreshAsync"/>.
/// </summary>
/// <remarks>
/// T12 covers:
/// <list type="bullet">
///   <item>StartAsync subscribes to the correct Redis channel.</item>
///   <item>A Pub/Sub message routes through to the bootstrap service trigger.</item>
///   <item>An exception in the handler is swallowed (fire-and-forget).</item>
///   <item>StopAsync unsubscribes from the channel.</item>
/// </list>
/// </remarks>
[TestClass]
public class StatisticsRefreshSubscriberServiceTests {

    private Mock<IConnectionMultiplexer> _redisMock = null!;
    private Mock<ISubscriber> _subscriberMock = null!;
    private Mock<IATProtoStorageService> _atProtoStorageMock = null!;
    private Mock<IMediaLinkCacheRepository> _cacheRepositoryMock = null!;
    private Mock<IDatabase> _redisDatabaseMock = null!;
    private Mock<IStatisticsRefreshTrigger> _refreshTriggerMock = null!;
    private Mock<ILogger<StatisticsRefreshSubscriberService>> _subscriberLoggerMock = null!;
    private Mock<ILogger<CacheBootstrapBackgroundService>> _bootstrapLoggerMock = null!;
    private CacheBootstrapSettings _settings = null!;

    /// <summary>MSTest-injected context for cancellation token propagation.</summary>
    public TestContext TestContext { get; set; } = null!;

    private static readonly Uri s_pdsUri = new( "https://pds.test.example" );
    private const string TestUserDid = "did:plc:subscribertest";

    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _subscriberMock = new Mock<ISubscriber>( );
        _atProtoStorageMock = new Mock<IATProtoStorageService>( );
        _cacheRepositoryMock = new Mock<IMediaLinkCacheRepository>( );
        _redisDatabaseMock = new Mock<IDatabase>( );
        _refreshTriggerMock = new Mock<IStatisticsRefreshTrigger>( );
        _subscriberLoggerMock = new Mock<ILogger<StatisticsRefreshSubscriberService>>( );
        _bootstrapLoggerMock = new Mock<ILogger<CacheBootstrapBackgroundService>>( );

        _ = _refreshTriggerMock.Setup( t => t.TryAcquire( ) ).Returns( true );

        _ = _redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) )
            .Returns( _subscriberMock.Object );
        _ = _redisMock.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( _redisDatabaseMock.Object );
        _ = _redisMock.Setup( r => r.GetEndPoints( It.IsAny<bool>( ) ) ).Returns( [] );

        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( RedisValue.Null );
        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( true );

        _ = _atProtoStorageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );

        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync(
                It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Returns( Task.CompletedTask );
        _ = _subscriberMock
            .Setup( s => s.UnsubscribeAsync(
                It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Returns( Task.CompletedTask );

        _settings = new CacheBootstrapSettings(
            s_pdsUri,
            TestUserDid,
            TimeSpan.FromHours( 6 ),
            CacheDays: 30,
            RefreshInterval: TimeSpan.FromHours( 24 ),
            MaxRecordsPerRun: 100,
            RefreshRetryInterval: TimeSpan.FromMinutes( 5 )
        );
    }

    #region T12 — Subscriber lifecycle

    /// <summary>
    /// T12a: StartAsync subscribes to <see cref="RedisChannels.StatisticsRefreshRequested"/> using
    /// <c>RedisChannel.Literal</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )]
    public async Task StartAsync_SubscribesToStatisticsRefreshRequestedChannel( ) {
        // Signal deterministically when SubscribeAsync has been reached, instead of a fixed delay
        // that can race the cancellation below and skip the subscribe call entirely.
        TaskCompletionSource subscribedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync(
                It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( _, _, _ ) => subscribedTcs.TrySetResult( ) )
            .Returns( Task.CompletedTask );

        using CancellationTokenSource cts = new( );

        StatisticsRefreshSubscriberService service = CreateSubscriberService( );
        _ = service.StartAsync( cts.Token );

        await subscribedTcs.Task.WaitAsync( TestContext.CancellationToken );
        await service.StopAsync( CancellationToken.None );

        _subscriberMock.Verify(
            s => s.SubscribeAsync(
                It.Is<RedisChannel>( c => c == RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ) ),
                It.IsAny<Action<RedisChannel, RedisValue>>( ),
                It.IsAny<CommandFlags>( ) ),
            Times.Once( ) );
    }

    /// <summary>
    /// T12b: A Pub/Sub message on the channel routes through to
    /// <see cref="CacheBootstrapBackgroundService.TriggerStatisticsRefreshAsync"/>.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )]
    public async Task OnMessage_RoutesToBootstrapServiceTrigger( ) {
        // Capture the handler registered during SubscribeAsync, signalling deterministically once
        // registration completes instead of racing a fixed delay.
        Action<RedisChannel, RedisValue>? capturedHandler = null;
        TaskCompletionSource subscribedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync(
                It.IsAny<RedisChannel>( ),
                It.IsAny<Action<RedisChannel, RedisValue>>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( _, handler, _ ) => {
                    capturedHandler = handler;
                    _ = subscribedTcs.TrySetResult( );
                } )
            .Returns( Task.CompletedTask );

        // Signal when ListAllRecordsAsync is called (= TriggerStatisticsRefreshAsync ran).
        TaskCompletionSource triggerFiredTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Callback<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => triggerFiredTcs.TrySetResult( ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );

        using CancellationTokenSource cts = new( );
        StatisticsRefreshSubscriberService service = CreateSubscriberService( );
        _ = service.StartAsync( cts.Token );

        // Wait for the subscription to be registered.
        await subscribedTcs.Task.WaitAsync( TestContext.CancellationToken );
        Assert.IsNotNull( capturedHandler, "Handler must have been registered" );

        // Fire a message on the channel.
        capturedHandler!( RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ), "trigger" );

        // The handler is fire-and-forget; wait for the trigger to propagate.
        await triggerFiredTcs.Task.WaitAsync( TestContext.CancellationToken );

        await service.StopAsync( CancellationToken.None );
    }

    /// <summary>
    /// T12c: An exception thrown by the handler (inside the fire-and-forget <c>Task.Run</c>) is
    /// swallowed and does not propagate to the subscriber's execute task. After the exception, the
    /// service still responds to a second trigger, proving it is still running.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )]
    public async Task OnMessage_HandlerException_IsSwallowedAndServiceContinues( ) {
        // Capture the handler, signalling deterministically once registration completes instead of
        // racing a fixed delay.
        Action<RedisChannel, RedisValue>? capturedHandler = null;
        TaskCompletionSource subscribedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync(
                It.IsAny<RedisChannel>( ),
                It.IsAny<Action<RedisChannel, RedisValue>>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( _, handler, _ ) => {
                    capturedHandler = handler;
                    _ = subscribedTcs.TrySetResult( );
                } )
            .Returns( Task.CompletedTask );

        // Make TriggerStatisticsRefreshAsync (via ListAllRecordsAsync) throw.
        int listAllCallCount = 0;
        TaskCompletionSource firstExceptionTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource secondCallTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        _ = _atProtoStorageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                int call = Interlocked.Increment( ref listAllCallCount );
                if (call == 1) {
                    _ = firstExceptionTcs.TrySetResult( );
                    throw new InvalidOperationException( "Simulated handler failure" );
                }
                _ = secondCallTcs.TrySetResult( );
                return AsyncEnumerable.Empty<(string, MediaLinkResult)>( );
            } );

        using CancellationTokenSource cts = new( );
        StatisticsRefreshSubscriberService service = CreateSubscriberService( );
        _ = service.StartAsync( cts.Token );

        await subscribedTcs.Task.WaitAsync( TestContext.CancellationToken );
        Assert.IsNotNull( capturedHandler );

        // Fire first message — handler will throw inside Task.Run.
        capturedHandler!( RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ), "trigger1" );
        await firstExceptionTcs.Task.WaitAsync( TestContext.CancellationToken );

        // Fire a second message — service must still be running and responsive. No fixed delay is
        // needed here: TryAcquire is stubbed to always return true, so the second trigger does not
        // depend on the first trigger's fire-and-forget Task.Run having completed.
        capturedHandler!( RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ), "trigger2" );
        await secondCallTcs.Task.WaitAsync( TestContext.CancellationToken );

        // Service handled a second trigger successfully — it didn't crash after the first exception.
        Assert.AreEqual( 2, listAllCallCount, "Service must remain responsive after handler exception" );

        await service.StopAsync( CancellationToken.None );
    }

    /// <summary>
    /// T12d: StopAsync (via cancellation) unsubscribes from the channel.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundService.StartAsync</c> returns <c>Task.CompletedTask</c> unconditionally and
    /// immediately — it schedules <c>ExecuteAsync</c> via <c>Task.Run</c> without waiting for it to
    /// reach a first await, so awaiting the captured start task is a no-op and does not synchronize
    /// with the post-cancellation <c>UnsubscribeAsync</c> call. The test instead awaits
    /// <c>StopAsync</c>, which signals cancellation on the linked stopping token internally and
    /// genuinely awaits the underlying execute task before returning, so the Verify below only runs
    /// once <c>ExecuteAsync</c> has unwound past its cancellation-triggered unsubscribe.
    /// </remarks>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )]
    public async Task StopAsync_UnsubscribesFromStatisticsRefreshChannel( ) {
        // Signal deterministically when SubscribeAsync has been reached, instead of a fixed delay.
        TaskCompletionSource subscribedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _subscriberMock
            .Setup( s => s.SubscribeAsync(
                It.IsAny<RedisChannel>( ), It.IsAny<Action<RedisChannel, RedisValue>>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisChannel, Action<RedisChannel, RedisValue>, CommandFlags>(
                ( _, _, _ ) => subscribedTcs.TrySetResult( ) )
            .Returns( Task.CompletedTask );

        using CancellationTokenSource cts = new( );
        StatisticsRefreshSubscriberService service = CreateSubscriberService( );
        _ = service.StartAsync( cts.Token );

        await subscribedTcs.Task.WaitAsync( TestContext.CancellationToken );

        // StopAsync cancels the service's internal (linked) stopping token and genuinely awaits
        // ExecuteAsync's completion, including the post-cancellation UnsubscribeAsync call below.
        await service.StopAsync( CancellationToken.None );

        _subscriberMock.Verify(
            s => s.UnsubscribeAsync(
                It.Is<RedisChannel>( c => c == RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ) ),
                It.IsAny<Action<RedisChannel, RedisValue>?>( ),
                It.IsAny<CommandFlags>( ) ),
            Times.Once( ) );
    }

    #endregion

    #region Helper methods

    private StatisticsRefreshSubscriberService CreateSubscriberService( ) {
        CacheBootstrapBackgroundService bootstrapService = new(
            _atProtoStorageMock.Object,
            _cacheRepositoryMock.Object,
            _redisMock.Object,
            _settings,
            _refreshTriggerMock.Object,
            _bootstrapLoggerMock.Object
        );
        return new StatisticsRefreshSubscriberService(
            _redisMock.Object,
            bootstrapService,
            _subscriberLoggerMock.Object
        );
    }

    #endregion
}
