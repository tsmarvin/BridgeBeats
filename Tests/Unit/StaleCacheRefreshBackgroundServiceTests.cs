using System.Runtime.CompilerServices;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Worker.Maintenance;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="StaleCacheRefreshBackgroundService"/>: cadence (startup grace, durable
/// schedule), the bootstrap-running skip guard, bounded age-only selection, leg decomposition,
/// multi-leg seeding, per-queue routing, the mode-B registration-set pin, saga keying, bulk-priority
/// enforcement, fire-and-forget behavior, per-record resilience, cancellation, and config binding.
/// </summary>
[TestClass]
public class StaleCacheRefreshBackgroundServiceTests {

    private Mock<IATProtoStorageService> _atProtoStorageMock = null!;
    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    private Mock<IProviderQueueResolver<QueuedLookupRequest>> _queueResolverMock = null!;
    private Mock<IRequestQueue<QueuedLookupRequest>> _queueMock = null!;
    private Mock<ILookupDispatchOutbox> _dispatchOutboxMock = null!;
    private System.Collections.Concurrent.ConcurrentDictionary<SupportedProviders, QueuedLookupRequest>
        _stagedRequests = null!;
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    private Mock<IDatabase> _redisDatabaseMock = null!;
    private Mock<ILogger<StaleCacheRefreshBackgroundService>> _loggerMock = null!;
    private Mock<IRefreshReviewStore> _refreshReviewStoreMock = null!;
    private HashSet<SupportedProviders> _enabledProviders = null!;
    private CacheBootstrapSettings _settings = null!;

    /// <summary>MSTest-injected context, used to flow the test's cancellation token into awaits.</summary>
    public TestContext TestContext { get; set; } = null!;

    private static readonly Uri s_testPdsUri = new( "https://pds.test.example" );
    private const string TestUserDid = "did:plc:testuser123";

    /// <summary>Creates fresh mocks and default settings before each test.</summary>
    [TestInitialize]
    public void Initialize( ) {
        _atProtoStorageMock = new Mock<IATProtoStorageService>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _queueResolverMock = new Mock<IProviderQueueResolver<QueuedLookupRequest>>( );
        _queueMock = new Mock<IRequestQueue<QueuedLookupRequest>>( );
        _dispatchOutboxMock = new Mock<ILookupDispatchOutbox>( );
        _stagedRequests = new( );
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _redisDatabaseMock = new Mock<IDatabase>( );
        _loggerMock = new Mock<ILogger<StaleCacheRefreshBackgroundService>>( );
        _refreshReviewStoreMock = new Mock<IRefreshReviewStore>( );

        _ = _refreshReviewStoreMock.Setup( store => store.GetUnresolvedAsync(
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        _ = _refreshReviewStoreMock.Setup( store => store.RegisterPendingAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        _ = _atProtoStorageMock.Setup( storage => storage.GetMediaLinkRecordCidAsync(
                It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( "bafyreitestcid" );

        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        _ = _redisMock
            .Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( _redisDatabaseMock.Object );
        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( RedisValue.Null );
        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<ValueCondition>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( true );

        _ = _queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( _queueMock.Object );
        _ = _queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        _ = _dispatchOutboxMock
            .Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (
                IReadOnlyList<QueuedLookupRequest> requests,
                QueuePriority _,
                RefreshReviewEntry _,
                CancellationToken _ ) => {
                    foreach (QueuedLookupRequest request in requests) {
                        _stagedRequests[request.Provider] = request;
                    }

                    return (IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>)
                        requests.ToDictionary(
                            request => request.Provider,
                            _ => ProviderDispatchStageOutcome.Staged );
                } );
        _ = _dispatchOutboxMock
            .Setup( outbox => outbox.DispatchAsync(
                It.IsAny<string>( ),
                It.IsAny<SupportedProviders>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( async (
                string _,
                SupportedProviders provider,
                CancellationToken cancellationToken ) => {
                    if (!_stagedRequests.TryGetValue( provider, out QueuedLookupRequest? request )) {
                        return false;
                    }

                    await _queueResolverMock.Object.GetQueue( provider ).EnqueueAsync(
                        request, QueuePriority.Bulk, cancellationToken );
                    return true;
                } );

        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ),
                It.IsAny<string>( ),
                It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ),
                It.IsAny<QueuePriority?>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( MakeMinimalSaga( ) );
        _ = _sagaManagerMock
            .Setup( m => m.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ),
                It.IsAny<IEnumerable<SupportedProviders>>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock
            .Setup( m => m.TryUpdateProviderStateAsync(
                It.IsAny<string>( ),
                It.IsAny<ProviderLookupState>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        _enabledProviders = [SupportedProviders.Spotify];

        _settings = MakeSettings( );
    }

    #region Cadence / Schedule Tests

    /// <summary>
    /// At service startup <c>ListAllRecordsAsync</c> must NOT be called before the 120-second startup
    /// grace period elapses. A 200 ms observation window is used (far shorter than the 120 s grace),
    /// so the pass cannot fire.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_AtStartup_DoesNotCallListAllRecordsBeforeGrace( ) {
        // The interval is very short so the schedule would fire immediately after the grace,
        // but we cancel well before the 120 s grace ends.
        _settings = MakeSettings( refreshInterval: TimeSpan.FromMilliseconds( 1 ) );
        StaleCacheRefreshBackgroundService service = CreateService( );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        Task executeTask = service.StartAsync( cts.Token );

        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        _atProtoStorageMock.Verify(
            s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// The durable marker (<c>cache:refresh:last-run</c>) is written via <c>StringSetAsync</c>
    /// AFTER <c>EnqueueAsync</c> completes, not before. A crash mid-pass therefore leaves the marker
    /// unwritten so the next start treats itself as due-now rather than skipping the interval.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_WritesMarkerAfterSuccessfulPass( ) {
        int markerWriteOrder = -1;
        int enqueueOrder = -1;
        int callCounter = 0;

        // Signals when EnqueueAsync has been called so we know both events have fired.
        TaskCompletionSource enqueueCalled = new( TaskCreationOptions.RunContinuationsAsynchronously );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                "cache:refresh:last-run", It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<ValueCondition>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>( ( _, _, _, _, _ ) => {
                markerWriteOrder = System.Threading.Interlocked.Increment( ref callCounter );
            } )
            .ReturnsAsync( true );

        SetupRecordList( [MakeStaleRecord( "at://test/1", DateTime.UtcNow.AddDays( -60 ) )] );

        _ = _queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( _, _, _ ) => {
                enqueueOrder = System.Threading.Interlocked.Increment( ref callCounter );
                _ = enqueueCalled.TrySetResult( );
            } )
            .Returns( Task.CompletedTask );

        _settings = MakeSettings( refreshRetryInterval: TimeSpan.FromMilliseconds( 100 ) );
        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        _ = service.StartAsync( cts.Token );

        // Wait until the enqueue phase fires, then allow the marker write to complete.
        await enqueueCalled.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        Assert.AreNotEqual( -1, enqueueOrder, "EnqueueAsync must be called" );
        Assert.AreNotEqual( -1, markerWriteOrder, "Marker write must occur after successful pass" );
        Assert.IsLessThan( markerWriteOrder, enqueueOrder,
            "Marker must be written AFTER enqueue completes (pass success), not before" );
    }

    /// <summary>
    /// When the marker is absent, the service treats elapsed time as infinite (due-now): driving
    /// <c>ExecuteAsync</c> via <c>StartAsync</c> with zero grace and jitter and a 6-hour interval,
    /// <c>ListAllRecordsAsync</c> fires within 5 seconds because no schedule wait is imposed.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Schedule_NoMarker_TreatsAsDueNow( ) {
        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( "cache:refresh:last-run", It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( RedisValue.Null );

        TaskCompletionSource listCalled = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                _ = listCalled.TrySetResult( );
                return AsyncEnumerable.Empty<(string, MediaLinkResult)>( );
            } );

        _settings = MakeSettings( refreshInterval: TimeSpan.FromHours( 6 ) );
        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );
        _ = service.StartAsync( cts.Token );

        await listCalled.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _atProtoStorageMock.Verify(
            s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.AtLeastOnce
        );
    }

    /// <summary>
    /// When the marker read throws, the service treats it as due-now (fail-toward-running) and
    /// proceeds: driving <c>ExecuteAsync</c> via <c>StartAsync</c> with zero grace and jitter and
    /// a 6-hour interval, <c>ListAllRecordsAsync</c> fires within 5 seconds despite the exception.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Schedule_MarkerReadThrows_FailsTowardRunning( ) {
        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( "cache:refresh:last-run", It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisConnectionException( ConnectionFailureType.UnableToConnect, "test" ) );

        TaskCompletionSource listCalled = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                _ = listCalled.TrySetResult( );
                return AsyncEnumerable.Empty<(string, MediaLinkResult)>( );
            } );

        _settings = MakeSettings( refreshInterval: TimeSpan.FromHours( 6 ) );
        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );
        _ = service.StartAsync( cts.Token );

        await listCalled.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _atProtoStorageMock.Verify(
            s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.AtLeastOnce
        );
    }

    /// <summary>
    /// A future-dated marker (e.g. clock skew) must be treated as due-now, not cause an
    /// over-long wait. Driving <c>ExecuteAsync</c> with zero grace and jitter and a 6-hour
    /// interval, a marker set one hour in the future fires the pass promptly rather than stalling
    /// for approximately 7 hours.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Schedule_FutureDatedMarker_TreatsAsDueNow( ) {
        string futureMarker = DateTimeOffset.UtcNow.AddHours( 1 ).ToString( "O" );
        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( "cache:refresh:last-run", It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)futureMarker );

        TaskCompletionSource listCalled = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                _ = listCalled.TrySetResult( );
                return AsyncEnumerable.Empty<(string, MediaLinkResult)>( );
            } );

        _settings = MakeSettings( refreshInterval: TimeSpan.FromHours( 6 ) );
        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );
        _ = service.StartAsync( cts.Token );

        await listCalled.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _atProtoStorageMock.Verify(
            s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.AtLeastOnce
        );
    }

    #endregion

    #region Failure Backoff and Gate-Removed Tests

    /// <summary>
    /// When <c>ListAllRecordsAsync</c> throws a non-OCE exception, the loop logs 5534 exactly once,
    /// never writes the marker for the failing pass, and retries after the configured
    /// <c>RefreshRetryInterval</c> (not the 6-hour refresh interval). The retry is confirmed by the
    /// second <c>ListAllRecordsAsync</c> arriving within a 5-second timeout; cancellation fires
    /// inside the second call so it cannot write a marker, confirming the marker-never assertion
    /// applies to the failure path only.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_PassFails_LogsRetryEvent_NoMarker_RetriesShortly( ) {
        int listCallCount = 0;
        TaskCompletionSource secondCallTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                int count = System.Threading.Interlocked.Increment( ref listCallCount );
                if (count == 1) {
                    throw new InvalidOperationException( "Simulated fatal enumeration failure" );
                }
                _ = secondCallTcs.TrySetResult( );
                cts.Cancel( );
                throw new OperationCanceledException( cts.Token );
            } );

        _settings = MakeSettings(
            refreshInterval: TimeSpan.FromHours( 6 ),
            refreshRetryInterval: TimeSpan.FromMilliseconds( 500 )
        );
        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        _ = service.StartAsync( cts.Token );

        await secondCallTcs.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await service.StopAsync( CancellationToken.None );

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>( e => e.Id == BridgeBeats.Worker.Maintenance.Logging.LogEventIds.RefreshPassFailedRetrying ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once,
            "5534 must be logged exactly once for the failing pass" );

        _redisDatabaseMock.Verify(
            d => d.StringSetAsync(
                "cache:refresh:last-run", It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<ValueCondition>( ), It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "The marker must NOT be written when a pass fails" );
    }

    /// <summary>
    /// When a pass throws and the backoff <c>Task.Delay</c> is cancelled during shutdown,
    /// <c>ExecuteAsync</c> must reach a clean <c>RanToCompletion</c> terminal state AND
    /// <c>StopAsync</c> must return within a short wall-clock bound.
    /// <para>
    /// The <c>RanToCompletion</c> assertion discriminates the precise G3 inner-try structure: with
    /// the inner <c>try/catch</c> present the OCE from the backoff delay is caught and the loop
    /// exits via <c>break</c>, leaving <c>ExecuteAsync</c> at <c>RanToCompletion</c>. Without it
    /// the OCE escapes the outer catch, faulting or cancelling <c>ExecuteAsync</c> — a structural
    /// defect the wall-clock assertion alone cannot detect because on net10.0
    /// <c>BackgroundService.StopAsync</c> awaits via
    /// <c>WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c> and does not
    /// re-throw.
    /// </para>
    /// <para>
    /// The wall-clock assertion independently guards against a backoff <c>Task.Delay</c> that drops
    /// the cancellation token entirely, which would cause <c>StopAsync</c> to hang for the full
    /// retry interval (~30 s).
    /// </para>
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_BackoffObservesToken_StopsPromptlyOnCancellation( ) {
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) =>
                throw new InvalidOperationException( "Force backoff" ) );

        _settings = MakeSettings(
            refreshRetryInterval: TimeSpan.FromSeconds( 30 )
        );
        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );
        _ = service.StartAsync( cts.Token );

        await Task.Delay( 300, TestContext.CancellationToken );

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew( );
        cts.CancelAfter( 200 );
        await service.StopAsync( CancellationToken.None );
        sw.Stop( );

        Assert.AreEqual( TaskStatus.RanToCompletion, service.ExecuteTask!.Status,
            "ExecuteAsync must reach RanToCompletion; Faulted/Canceled means the inner-try around the backoff delay is missing and the OCE escaped" );

        Assert.IsLessThan( TimeSpan.FromSeconds( 3 ), sw.Elapsed,
            "StopAsync must return in under 3 seconds; a backoff that ignores the token would hang ~30s" );

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.Is<EventId>( e => e.Id == BridgeBeats.Worker.Maintenance.Logging.LogEventIds.RefreshPassFailedRetrying ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once,
            "5534 must be logged once for the pass failure; the cancellation must not be double-counted as a failure" );
    }

    /// <summary>
    /// Pin: a bootstrap status document with <c>IsRunning=true</c> no longer gates the pass.
    /// <c>ListAllRecordsAsync</c> must still be called and records enqueued even when the status
    /// shows a bootstrap is in progress.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_BootstrapRunning_NoLongerGatesThePass( ) {
        string runningJson = System.Text.Json.JsonSerializer.Serialize(
            new BridgeBeats.Contracts.DTOs.CacheBootstrapStatus { IsRunning = true } );
        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( BridgeBeats.Contracts.DTOs.CacheBootstrapStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)runningJson );

        SetupRecordList( [MakeStaleRecord( "at://test/1", DateTime.UtcNow.AddDays( -60 ) )] );
        StaleCacheRefreshBackgroundService service = CreateService( );

        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _atProtoStorageMock.Verify(
            s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.Once,
            "Gate removed: ListAllRecordsAsync must be called regardless of bootstrap status" );
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Gate removed: records must still be enqueued when bootstrap is running" );
    }

    #endregion

    #region Selection Tests (Age-Only)

    /// <summary>
    /// When more than N stale records exist, exactly N are enqueued AND they are the N oldest by
    /// <c>LookedUpAt</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_MoreThanN_EnqueuesExactlyNOldest( ) {
        _settings = MakeSettings( maxRecordsPerRun: 2 );

        DateTime oldest = DateTime.UtcNow.AddDays( -90 );
        DateTime middle = DateTime.UtcNow.AddDays( -60 );
        DateTime newest = DateTime.UtcNow.AddDays( -31 );

        (string, MediaLinkResult) rec1 = MakeStaleRecord( "at://oldest", oldest, isrc: "ISRC_OLDEST" );
        (string, MediaLinkResult) rec2 = MakeStaleRecord( "at://middle", middle, isrc: "ISRC_MIDDLE" );
        (string, MediaLinkResult) rec3 = MakeStaleRecord( "at://newest", newest, isrc: "ISRC_NEWEST" );

        SetupRecordList( [rec2, rec3, rec1] );

        // Capture the saga lookup key from each GetOrCreateAsync call to determine which records
        // were selected. All three records share the same Spotify URL (same native leg LookupValue),
        // so the saga lookup key — derived from the ISRC — is the only distinguishing signal.
        List<string> sagaLookupKeys = [];
        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ),
                It.IsAny<string>( ),
                It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ),
                It.IsAny<QueuePriority?>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<string, string, LookupRequestType, string, QueuePriority?, CancellationToken>(
                ( _, lookupKey, _, _, _, _ ) => sagaLookupKeys.Add( lookupKey ) )
            .ReturnsAsync( MakeMinimalSaga( ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 2, sagaLookupKeys, "Expected exactly N=2 records selected" );
        Assert.Contains( k => k.Contains( "ISRC_OLDEST" ), sagaLookupKeys,
            "The oldest record must be among the selected." );
        Assert.Contains( k => k.Contains( "ISRC_MIDDLE" ), sagaLookupKeys,
            "The middle record must be among the selected." );
        Assert.DoesNotContain( k => k.Contains( "ISRC_NEWEST" ), sagaLookupKeys,
            "The newest record must NOT be among the selected." );
    }

    /// <summary>
    /// When fewer than N stale records exist, all stale records are enqueued.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_FewerThanN_EnqueuesAllStale( ) {
        _settings = MakeSettings( maxRecordsPerRun: 10 );

        SetupRecordList( [
            MakeStaleRecord( "at://test/1", DateTime.UtcNow.AddDays( -60 ) ),
            MakeStaleRecord( "at://test/2", DateTime.UtcNow.AddDays( -50 ) )
        ] );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );
    }

    /// <summary>
    /// A record whose <c>LookedUpAt</c> is one second older than the cache window IS selected as
    /// stale; a record one day inside the window is NOT selected.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_BoundaryTimestamps_SelectsOlderNotNewer( ) {
        int cacheDays = 30;
        _settings = MakeSettings( cacheDays: cacheDays );

        DateTime justStale = DateTime.UtcNow.AddDays( -cacheDays ).AddSeconds( -1 );
        DateTime justFresh = DateTime.UtcNow.AddDays( -cacheDays ).AddDays( 1 );

        (string, MediaLinkResult) staleRec = MakeStaleRecord( "at://stale", justStale, isrc: "STALE_ISRC" );
        (string, MediaLinkResult) freshRec = MakeFreshRecord( "at://fresh", justFresh, isrc: "FRESH_ISRC" );

        SetupRecordList( [staleRec, freshRec] );

        List<QueuedLookupRequest> enqueuedRequests = [];
        _ = _queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>(
                ( req, _, _ ) => enqueuedRequests.Add( req ) )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 1, enqueuedRequests );
        // The stale record's Spotify URL parses to native track ID "3SPOTID12345"; the fresh record
        // is not selected, so a count of 1 with this native LookupValue confirms the correct record.
        Assert.AreEqual( LookupRequestType.SongIdLookup, enqueuedRequests[0].LookupType,
            "Stale record with parseable URL yields a native SongIdLookup leg." );
        Assert.AreEqual( "3SPOTID12345", enqueuedRequests[0].LookupValue,
            "Native leg LookupValue is the extracted track ID, not the ISRC." );
    }

    /// <summary>
    /// A record with a FRESH timestamp is NOT selected regardless of its provider coverage.
    /// Freshness is age-only: the only criterion is whether the record has exceeded the cache
    /// window; provider coverage has no bearing on selection.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_FreshRecord_IsNotSelected( ) {
        DateTime fresh = DateTime.UtcNow.AddDays( -1 ); // well inside cache window

        // Fresh timestamp — under age-only rule this must NOT be selected.
        (string, MediaLinkResult) ageFreshRec = MakeStaleRecord( "at://age-fresh", fresh, isrc: "AGE_FRESH_ISRC" );
        // Also fresh timestamp — also NOT selected.
        (string, MediaLinkResult) freshRec = MakeFreshRecord( "at://fresh", fresh, isrc: "FRESH_ISRC" );

        SetupRecordList( [ageFreshRec, freshRec] );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        // Neither record is stale under age-only freshness: zero enqueues expected.
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// A record whose timestamp is outside the freshness window is selected based on age alone.
    /// Provider coverage is not part of the selection predicate.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_ExpiredRecord_IsSelected( ) {
        DateTime expired = DateTime.UtcNow.AddDays( -31 ); // outside 30-day window

        (string, MediaLinkResult) expiredRec = MakeStaleRecord(
            "at://expired", expired, isrc: "EXPIRED_ISRC" );

        SetupRecordList( [expiredRec] );

        List<QueuedLookupRequest> enqueuedRequests = [];
        _ = _queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>(
                ( req, _, _ ) => enqueuedRequests.Add( req ) )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 1, enqueuedRequests );
        Assert.AreEqual( LookupRequestType.SongIdLookup, enqueuedRequests[0].LookupType,
            "Expired record with a parseable URL yields a native SongIdLookup leg." );
        Assert.AreEqual( "3SPOTID12345", enqueuedRequests[0].LookupValue,
            "Native leg LookupValue is the extracted track ID, not the ISRC." );
    }

    /// <summary>
    /// Equivalence pin: the age-only selection produces the same result set as the inline age check
    /// without the old <c>IsPartial</c> arm. A corpus with fresh records of differing provider
    /// coverage confirms both are excluded.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_SelectionEquivalentToAgeOnlyCheck_ExcludesFreshRecords( ) {
        int cacheDays = 30;
        _settings = MakeSettings( cacheDays: cacheDays );
        DateTime utcNow = DateTime.UtcNow;

        // Records: one expired, one fresh, one fresh with limited provider coverage.
        (string, MediaLinkResult) expiredRec = MakeStaleRecord(
            "at://expired", utcNow.AddDays( -60 ), isrc: "EXPIRED_ISRC" );
        (string, MediaLinkResult) freshRec = MakeFreshRecord(
            "at://fresh", utcNow.AddDays( -1 ), isrc: "FRESH_ISRC" );
        (string, MediaLinkResult) ageFreshRec = MakeStaleRecord(
            "at://age-fresh", utcNow.AddDays( -1 ), isrc: "AGE_FRESH_ISRC" );

        SetupRecordList( [expiredRec, freshRec, ageFreshRec] );

        List<QueuedLookupRequest> enqueuedRequests = [];
        _ = _queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>(
                ( req, _, _ ) => enqueuedRequests.Add( req ) )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        // Only the age-expired record should be selected.
        Assert.HasCount( 1, enqueuedRequests, "Only the expired record should be enqueued" );
        Assert.AreEqual( LookupRequestType.SongIdLookup, enqueuedRequests[0].LookupType,
            "Expired record with parseable URL yields a native SongIdLookup leg." );
        Assert.AreEqual( "3SPOTID12345", enqueuedRequests[0].LookupValue,
            "Native leg LookupValue is the extracted track ID, not the ISRC." );
    }

    #endregion

    #region Leg Decomposition Tests (DeriveRefreshLegs)

    /// <summary>
    /// All three providers present with parseable track URLs yields three native-id SongIdLookup
    /// legs and zero fallback legs.
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_AllProvidersPresentParseable_ThreeNativeLegsZeroFallback( ) {
        MediaLinkResult record = MakeThreeProviderRecord( isrc: "TESTISRC" );
        HashSet<SupportedProviders> providers = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, providers );

        Assert.HasCount( 3, legs, "Expected 3 native-id legs" );
        Assert.IsTrue( legs.All( l => l.LookupType is LookupRequestType.SongIdLookup ),
            "All legs should be SongIdLookup" );
        Assert.DoesNotContain( l => l.LookupType is LookupRequestType.IsrcLookup, legs,
            "No fallback legs expected" );
        // Each provider covered exactly once.
        CollectionAssert.AreEquivalent(
            providers.ToList( ),
            legs.Select( l => l.Provider ).ToList( )
        );
    }

    /// <summary>
    /// Album entries (IsAlbum=true) yield <c>AlbumIdLookup</c> legs; the per-entry IsAlbum is used.
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_AlbumEntries_YieldAlbumIdLookup( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/album/3TESTID111",
            IsAlbum = true,
            ExternalId = "UPCTESTVAL"
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, [SupportedProviders.Spotify] );

        Assert.HasCount( 1, legs );
        Assert.AreEqual( LookupRequestType.AlbumIdLookup, legs[0].LookupType );
        Assert.IsTrue( legs[0].IsAlbum );
    }

    /// <summary>A native Apple leg retains its stored storefront and carries the ISRC fallback.</summary>
    [TestMethod]
    public void DeriveRefreshLegs_NativeLeg_CarriesStorefrontAndExternalFallback( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/jp/album/example/1234567890?i=9876543210",
            ExternalId = "jp-test-isrc",
            MarketRegion = "jp",
            IsAlbum = false
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, [SupportedProviders.AppleMusic] );

        Assert.HasCount( 1, legs );
        Assert.AreEqual( LookupRequestType.SongIdLookup, legs[0].LookupType );
        Assert.AreEqual( "jp", legs[0].Storefront );
        Assert.AreEqual( LookupRequestType.IsrcLookup, legs[0].FallbackLookupType );
        Assert.AreEqual( "JP-TEST-ISRC", legs[0].FallbackLookupValue );
    }

    /// <summary>An Apple URL supplies the storefront only when the stored market is blank.</summary>
    [TestMethod]
    public void DeriveRefreshLegs_BlankMarket_UsesAppleUrlStorefrontFallback( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/br/album/example/1234567890?i=9876543210",
            ExternalId = "BRABC1234567",
            MarketRegion = string.Empty,
            IsAlbum = false
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, [SupportedProviders.AppleMusic] );

        Assert.HasCount( 1, legs );
        Assert.AreEqual( "br", legs[0].Storefront );
    }

    /// <summary>A missing provider uses a region from a later result, independently of the id anchor.</summary>
    [TestMethod]
    public void DeriveRefreshLegs_MissingTidal_UsesLaterAppleStorefront( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/spotify-id",
            ExternalId = "JPABC1234567",
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/jp/album/example/1234567890?i=9876543210",
            ExternalId = "JPABC1234567",
            MarketRegion = "jp",
            IsAlbum = false
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs(
                record,
                [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal] );

        StaleCacheRefreshBackgroundService.RefreshLeg tidal = legs.Single(
            leg => leg.Provider == SupportedProviders.Tidal );
        Assert.AreEqual( "JP", tidal.Storefront );
    }

    /// <summary>A storefront unsupported by the target provider is discarded before queueing.</summary>
    [TestMethod]
    public void DeriveRefreshLegs_AppleAllStorefront_DoesNotReachTidalWorker( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/all/album/example/1234567890?i=9876543210",
            ExternalId = "USRC12345678",
            MarketRegion = "all",
            IsAlbum = false
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs(
                record,
                [SupportedProviders.AppleMusic, SupportedProviders.Tidal] );

        StaleCacheRefreshBackgroundService.RefreshLeg tidal = legs.Single(
            leg => leg.Provider == SupportedProviders.Tidal );
        Assert.IsNull( tidal.Storefront );
    }

    /// <summary>
    /// Spotify+Apple present and parseable, Tidal absent, ISRC present → 2 native legs + 1 Tidal
    /// IsrcLookup fallback.
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_MissingProviderWithIsrc_EmitsExternalIdFallbackLeg( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = "testisrc",
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/something/1234567890?i=9876543210",
            ExternalId = "testisrc",
            IsAlbum = false
        };
        // Tidal absent from Results.

        HashSet<SupportedProviders> providers = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];
        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, providers );

        Assert.HasCount( 3, legs );

        StaleCacheRefreshBackgroundService.RefreshLeg tidalFallback =
            legs.Single( l => l.Provider == SupportedProviders.Tidal );
        Assert.AreEqual( LookupRequestType.IsrcLookup, tidalFallback.LookupType );
        Assert.AreEqual( "TESTISRC", tidalFallback.LookupValue,
            "ISRC fallback value should be uppercased" );
    }

    /// <summary>
    /// One provider absent, IsAlbum=true, UPC present → UpcLookup fallback leg; UPC is trim-only
    /// (not uppercased).
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_MissingProviderAlbumWithUpc_EmitsUpcFallbackLeg( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/album/3TESTID111",
            ExternalId = "  upc12345  ",
            IsAlbum = true
        };
        // Tidal absent.

        HashSet<SupportedProviders> providers = [SupportedProviders.Spotify, SupportedProviders.Tidal];
        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, providers );

        StaleCacheRefreshBackgroundService.RefreshLeg tidalFallback =
            legs.Single( l => l.Provider == SupportedProviders.Tidal );
        Assert.AreEqual( LookupRequestType.UpcLookup, tidalFallback.LookupType );
        Assert.AreEqual( "upc12345", tidalFallback.LookupValue,
            "UPC fallback value should be trimmed but NOT uppercased" );
    }

    /// <summary>
    /// Spotify present but URL is garbage (ExtractId returns null): treated as missing, gets an
    /// IsrcLookup fallback leg.
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_PresentButUnparseableUrl_TreatedAsMissing_GetsFallback( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://not-a-real-spotify-url.example/garbage",
            ExternalId = "GOODISRC",
            IsAlbum = false
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, [SupportedProviders.Spotify] );

        // No native leg (unparseable URL), one fallback.
        Assert.HasCount( 1, legs );
        Assert.AreEqual( LookupRequestType.IsrcLookup, legs[0].LookupType );
        Assert.AreEqual( SupportedProviders.Spotify, legs[0].Provider );
    }

    /// <summary>
    /// Tidal absent with no external id anywhere: no fallback leg emitted (nothing to search by).
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_MissingProviderNoExternalId_EmitsNoFallbackForIt( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = string.Empty,
            IsAlbum = false
        };
        // Tidal absent, no external id anywhere.

        HashSet<SupportedProviders> providers = [SupportedProviders.Spotify, SupportedProviders.Tidal];
        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, providers );

        // Only the Spotify native leg; no Tidal leg.
        Assert.HasCount( 1, legs );
        Assert.AreEqual( SupportedProviders.Spotify, legs[0].Provider );
        Assert.DoesNotContain( l => l.Provider == SupportedProviders.Tidal, legs );
    }

    /// <summary>
    /// All present providers unparseable and no external id → empty list (the skip case).
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_NoParseableUrlAndNoExternalId_ReturnsEmpty( ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://garbage.example/noid",
            ExternalId = string.Empty
        };

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, [SupportedProviders.Spotify] );

        Assert.HasCount( 0, legs );
    }

    /// <summary>
    /// A provider present in record.Results that is NOT in the enabled set produces no leg — neither
    /// a native-id leg nor a fallback leg. The provider drops off the record on the next refresh write.
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_DisabledProviderInResults_ProducesNoLeg( ) {
        // Record has both Spotify and Tidal with parseable URLs and an ISRC.
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = "TESTISRC",
            IsAlbum = false
        };
        record.Results[SupportedProviders.Tidal] = new MusicLookupResult {
            URL = "https://tidal.com/browse/track/12345678",
            ExternalId = "TESTISRC",
            IsAlbum = false
        };

        // Only Spotify is enabled; Tidal has been switched off.
        HashSet<SupportedProviders> onlySpotify = [SupportedProviders.Spotify];

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, onlySpotify );

        // Only one leg (Spotify native); Tidal must not appear at all.
        Assert.HasCount( 1, legs );
        Assert.AreEqual( SupportedProviders.Spotify, legs[0].Provider );
        Assert.DoesNotContain( l => l.Provider == SupportedProviders.Tidal, legs,
            "A disabled provider must produce no leg, even when present in record.Results." );
    }

    /// <summary>
    /// At most one leg per provider: a provider gets a native leg XOR a fallback leg, never both.
    /// </summary>
    [TestMethod]
    public void DeriveRefreshLegs_AtMostOneLegPerProvider( ) {
        MediaLinkResult record = MakeThreeProviderRecord( isrc: "TESTISRC" );
        HashSet<SupportedProviders> providers = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        IReadOnlyList<StaleCacheRefreshBackgroundService.RefreshLeg> legs =
            StaleCacheRefreshBackgroundService.DeriveRefreshLegs( record, providers );

        IEnumerable<SupportedProviders> providerList = legs.Select( l => l.Provider );
        CollectionAssert.AllItemsAreUnique( providerList.ToList( ),
            "Each provider must appear at most once in the leg list" );
    }

    /// <summary>
    /// A record with no identifier (no parseable URL and no external id) is skipped (zero legs)
    /// and does not cause an error.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_RecordWithNoIdentifier_IsSkippedAndNotAnError( ) {
        MediaLinkResult result = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        result.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = string.Empty,
            ExternalId = string.Empty,
            Title = string.Empty,
            Artist = string.Empty
        };

        SetupRecordList( [("at://noid", result)] );
        StaleCacheRefreshBackgroundService service = CreateService( );

        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never
        );

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>( ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never
        );
    }

    #endregion

    #region Multi-Leg Seeding Tests (EnqueueRecordAsync / RunRefreshPassAsync)

    /// <summary>
    /// All legs for a single record share the same saga id; GetOrCreateAsync is called exactly once
    /// per record.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_MultiProviderRecord_AllLegsShareOneSagaId( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        SetupRecordList( [("at://three-provider", MakeThreeProviderRecord( isrc: "SHARED_ISRC" ))] );

        List<QueuedLookupRequest> captured = [];
        _ = _queueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => captured.Add( r ) )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 3, captured, "Expected 3 legs for a 3-provider record" );
        string firstSagaId = captured[0].SagaId;
        Assert.IsTrue( captured.All( r => r.SagaId == firstSagaId ),
            "All legs must share the same SagaId" );

        _sagaManagerMock.Verify(
            m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "GetOrCreateAsync must be called exactly once per record" );
    }

    /// <summary>
    /// Each leg is enqueued to its own provider queue: GetQueue is called with the leg's provider
    /// and the request's Provider matches.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_EachLegEnqueuedToItsOwnProviderQueue( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        SetupRecordList( [("at://three-provider", MakeThreeProviderRecord( isrc: "TESTISRC" ))] );

        List<(SupportedProviders QueueProvider, SupportedProviders RequestProvider)> routingCapture = [];

        Mock<IRequestQueue<QueuedLookupRequest>> spotifyQ = new( );
        Mock<IRequestQueue<QueuedLookupRequest>> appleQ = new( );
        Mock<IRequestQueue<QueuedLookupRequest>> tidalQ = new( );

        foreach (Mock<IRequestQueue<QueuedLookupRequest>> q in new[] { spotifyQ, appleQ, tidalQ }) {
            _ = q.Setup( x => x.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
                .Returns( Task.CompletedTask );
        }

        _ = _queueResolverMock
            .Setup( r => r.GetQueue( SupportedProviders.Spotify ) ).Returns( spotifyQ.Object );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( SupportedProviders.AppleMusic ) ).Returns( appleQ.Object );
        _ = _queueResolverMock
            .Setup( r => r.GetQueue( SupportedProviders.Tidal ) ).Returns( tidalQ.Object );

        List<QueuedLookupRequest> spotifyReqs = [];
        List<QueuedLookupRequest> appleReqs = [];
        List<QueuedLookupRequest> tidalReqs = [];

        _ = spotifyQ.Setup( x => x.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => spotifyReqs.Add( r ) )
            .Returns( Task.CompletedTask );
        _ = appleQ.Setup( x => x.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => appleReqs.Add( r ) )
            .Returns( Task.CompletedTask );
        _ = tidalQ.Setup( x => x.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => tidalReqs.Add( r ) )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 1, spotifyReqs );
        Assert.AreEqual( SupportedProviders.Spotify, spotifyReqs[0].Provider );

        Assert.HasCount( 1, appleReqs );
        Assert.AreEqual( SupportedProviders.AppleMusic, appleReqs[0].Provider );

        Assert.HasCount( 1, tidalReqs );
        Assert.AreEqual( SupportedProviders.Tidal, tidalReqs[0].Provider );
    }

    /// <summary>
    /// Every enqueued request carries <c>OriginPriority = QueuePriority.Bulk</c> AND the
    /// <c>EnqueueAsync</c> priority argument is also <c>QueuePriority.Bulk</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_AllLegs_CarryBulkOriginAndEnqueuePriority( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];
        SetupRecordList( [("at://three", MakeThreeProviderRecord( isrc: "TESTISRC" ))] );

        List<(QueuedLookupRequest Req, QueuePriority Priority)> captured = [];
        _ = _queueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, p, _ ) => captured.Add( (r, p) ) )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsNotEmpty( captured, "At least one leg should be enqueued" );
        Assert.IsTrue( captured.All( c => c.Req.OriginPriority == QueuePriority.Bulk ),
            "All requests must have Bulk OriginPriority" );
        Assert.IsTrue( captured.All( c => c.Priority == QueuePriority.Bulk ),
            "All EnqueueAsync calls must use Bulk priority" );
    }

    /// <summary>
    /// <c>EnqueueAsync</c> must never be called with <c>QueuePriority.Interactive</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_NeverEnqueuesWithInteractivePriority( ) {
        SetupRecordList( [MakeStaleRecord( "at://test/1", DateTime.UtcNow.AddDays( -60 ) )] );
        StaleCacheRefreshBackgroundService service = CreateService( );

        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                QueuePriority.Interactive,
                It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    /// <summary>Records already in the unresolved review list are not selected again.</summary>
    [TestMethod]
    public async Task RunRefreshPass_UnresolvedRecord_IsSuppressed( ) {
        const string AtUri = "at://did:plc:test/link.bridgebeats.lookup/track:unresolved";
        SetupRecordList( [MakeStaleRecord( AtUri, DateTime.UtcNow.AddDays( -60 ) )] );

        Mock<IRefreshReviewStore> reviewStore = new( );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RefreshReviewEntry {
                SourceRecordUri = AtUri,
                SagaId = "old-saga",
                LookupType = LookupRequestType.IsrcLookup,
                LookupValue = "ISRC_DEFAULT",
                FailedAt = DateTimeOffset.UtcNow
            }] );

        await CreateService( reviewStore.Object ).RunRefreshPassAsync( TestContext.CancellationToken );

        _queueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ),
            It.IsAny<QueuePriority>( ),
            It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>The refresh source URI is staged atomically with provider work.</summary>
    [TestMethod]
    public async Task RunRefreshPass_RegistersPendingReviewContext( ) {
        const string AtUri = "at://did:plc:test/link.bridgebeats.lookup/track:pending";
        SetupRecordList( [MakeStaleRecord( AtUri, DateTime.UtcNow.AddDays( -60 ), "PENDING_ISRC" )] );

        Mock<IRefreshReviewStore> reviewStore = new( );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        RefreshReviewEntry? capturedReviewEntry = null;
        _ = _dispatchOutboxMock.Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                QueuePriority.Bulk,
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<IReadOnlyList<QueuedLookupRequest>, QueuePriority, RefreshReviewEntry, CancellationToken>(
                ( _, _, entry, _ ) => capturedReviewEntry = entry )
            .ReturnsAsync( ( IReadOnlyList<QueuedLookupRequest> requests, QueuePriority _,
                RefreshReviewEntry _, CancellationToken _ ) => requests.ToDictionary(
                    request => request.Provider,
                    _ => ProviderDispatchStageOutcome.Staged ) );

        await CreateService( reviewStore.Object ).RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsNotNull( capturedReviewEntry );
        Assert.AreEqual( AtUri, capturedReviewEntry.SourceRecordUri );
        Assert.AreEqual( "bafyreitestcid", capturedReviewEntry.SourceRecordCid );
        Assert.AreEqual( LookupRequestType.IsrcLookup, capturedReviewEntry.LookupType );
        Assert.AreEqual( "PENDING_ISRC", capturedReviewEntry.LookupValue );
        Assert.IsFalse( string.IsNullOrWhiteSpace( capturedReviewEntry.InstanceToken ) );
    }

    #endregion

    #region Mode-B Pin — Registration Set

    /// <summary>
    /// THE MODE-B PIN: TryInitializeProviderStatesAsync receives the exact enqueued-leg provider set,
    /// NOT all enabledProviders. For a record where only Spotify+Apple are present/parseable and there
    /// is no external id, Tidal must NOT appear in the registered set.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_InitializesProviderStatesForEnqueuedLegsOnly_NotAllEnabledProviders( ) {
        // Record: only Spotify+Apple parseable, no external id — Tidal gets no leg.
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = string.Empty,
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/x/1234567890?i=9876543210",
            ExternalId = string.Empty,
            IsAlbum = false
        };

        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        SetupRecordList( [("at://two-provider-no-external-id", record)] );

        IEnumerable<SupportedProviders>? capturedProviders = null;
        _ = _dispatchOutboxMock
            .Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                QueuePriority.Bulk,
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<IReadOnlyList<QueuedLookupRequest>, QueuePriority, RefreshReviewEntry, CancellationToken>(
                ( requests, _, _, _ ) => capturedProviders = [.. requests.Select( request => request.Provider )] )
            .ReturnsAsync( ( IReadOnlyList<QueuedLookupRequest> requests, QueuePriority _, RefreshReviewEntry _, CancellationToken _ ) =>
                requests.ToDictionary(
                    request => request.Provider,
                    _ => ProviderDispatchStageOutcome.Staged ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsNotNull( capturedProviders, "The atomic outbox batch must contain the provider legs" );
        HashSet<SupportedProviders> registered = [.. capturedProviders];

        // Positive: exactly {Spotify, Apple} registered.
        Assert.Contains( SupportedProviders.Spotify, registered );
        Assert.Contains( SupportedProviders.AppleMusic, registered );

        // Negative control: Tidal must NOT be registered (it has no leg).
        Assert.DoesNotContain( SupportedProviders.Tidal, registered,
            "Today's bug: registering all enabledProviders (including Tidal with no leg) keeps the saga incomplete forever. This assertion is the mode-B pin." );
    }

    /// <summary>
    /// For a 2-native + 1-fallback record, the registered set equals all three providers.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_RegisteredSet_EqualsDistinctLegProviders( ) {
        // Spotify+Apple parseable, Tidal absent but ISRC present → Tidal gets a fallback leg.
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = "TESTISRC",
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/x/1234567890?i=9876543210",
            ExternalId = "TESTISRC",
            IsAlbum = false
        };

        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];
        SetupRecordList( [("at://two-native-one-fallback", record)] );

        IEnumerable<SupportedProviders>? capturedProviders = null;
        _ = _dispatchOutboxMock
            .Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                QueuePriority.Bulk,
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<IReadOnlyList<QueuedLookupRequest>, QueuePriority, RefreshReviewEntry, CancellationToken>(
                ( requests, _, _, _ ) => capturedProviders = [.. requests.Select( request => request.Provider )] )
            .ReturnsAsync( ( IReadOnlyList<QueuedLookupRequest> requests, QueuePriority _, RefreshReviewEntry _, CancellationToken _ ) =>
                requests.ToDictionary(
                    request => request.Provider,
                    _ => ProviderDispatchStageOutcome.Staged ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsNotNull( capturedProviders );
        HashSet<SupportedProviders> registered = [.. capturedProviders];
        CollectionAssert.AreEquivalent(
            _enabledProviders.ToList( ),
            registered.ToList( ),
            "All three providers should be registered when each has a leg" );
    }

    /// <summary>
    /// A missing provider with no external id produces no leg and therefore is absent from the
    /// registered set, so it cannot block IsComplete.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_ProviderWithNoLeg_IsNotRegistered( ) {
        // Only Spotify present + parseable; no external id; Tidal enabled but no leg.
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = string.Empty,
            IsAlbum = false
        };

        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.Tidal];
        SetupRecordList( [("at://spotify-only", record)] );

        IEnumerable<SupportedProviders>? capturedProviders = null;
        _ = _dispatchOutboxMock
            .Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                QueuePriority.Bulk,
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<IReadOnlyList<QueuedLookupRequest>, QueuePriority, RefreshReviewEntry, CancellationToken>(
                ( requests, _, _, _ ) => capturedProviders = [.. requests.Select( request => request.Provider )] )
            .ReturnsAsync( ( IReadOnlyList<QueuedLookupRequest> requests, QueuePriority _, RefreshReviewEntry _, CancellationToken _ ) =>
                requests.ToDictionary(
                    request => request.Provider,
                    _ => ProviderDispatchStageOutcome.Staged ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsNotNull( capturedProviders );
        HashSet<SupportedProviders> registered = [.. capturedProviders];
        Assert.DoesNotContain( SupportedProviders.Tidal, registered,
            "Tidal has no leg and must not be registered" );
        Assert.Contains( SupportedProviders.Spotify, registered );
    }

    /// <summary>
    /// A stale record with no derivable refresh legs still consumes sweep attempts and is promoted
    /// to review on the third selection, after which the unresolved index suppresses selection.
    /// </summary>
    [TestMethod]
    public async Task RunRefreshPass_NoLegRecord_QuarantinesAfterThreeSweepsAndSuppressesLaterSelection( ) {
        const string RecordUri = "at://no-leg-quarantine";
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = string.Empty,
            ExternalId = string.Empty,
            IsAlbum = false
        };
        _enabledProviders = [SupportedProviders.Spotify];
        SetupRecordList( [(RecordUri, record)] );

        int attempts = 0;
        bool promoted = false;
        _ = _refreshReviewStoreMock.Setup( store => store.IncrementSweepAttemptAsync(
                RecordUri, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => ++attempts );
        _ = _refreshReviewStoreMock.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => promoted
                ? [new RefreshReviewEntry {
                    SourceRecordUri = RecordUri,
                    SagaId = "no-leg-saga",
                    LookupType = LookupRequestType.UriLookup,
                    LookupValue = RecordUri
                }]
                : [] );
        _ = _refreshReviewStoreMock.Setup( store => store.PromoteDirectAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => promoted = true )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.AreEqual( StaleCacheRefreshBackgroundService.MaxRefreshSweepAttemptsBeforeReview, attempts );
        _refreshReviewStoreMock.Verify( store => store.PromoteDirectAsync(
            It.Is<RefreshReviewEntry>( entry => entry.SourceRecordUri == RecordUri
                && entry.LookupType == LookupRequestType.UriLookup
                && entry.LookupValue == RecordUri
                && entry.SourceRecordCid == "bafyreitestcid" ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.GetOrCreateAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ),
            It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    #endregion

    #region Saga Keying Tests

    /// <summary>
    /// A track with ISRC seeds the saga with IsrcLookup key and type (fan-out suppressed).
    /// The saga id matches what an interactive ISRC lookup for the same entity produces (dedup parity).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueRecord_TrackWithIsrc_SeedsSagaWithIsrcKeyAndType( ) {
        const string Isrc = "testisrc123";
        SetupRecordList( [MakeStaleRecord( "at://track", DateTime.UtcNow.AddDays( -60 ), isrc: Isrc )] );

        string? capturedLookupKey = null;
        LookupRequestType capturedLookupType = default;
        string? capturedSagaId = null;

        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<string, string, LookupRequestType, string, QueuePriority?, CancellationToken>(
                ( sagaId, lookupKey, lookupType, _, _, _ ) => {
                    capturedSagaId = sagaId;
                    capturedLookupKey = lookupKey;
                    capturedLookupType = lookupType;
                } )
            .ReturnsAsync( MakeMinimalSaga( ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsNotNull( capturedLookupKey );
        Assert.AreEqual( LookupRequestType.IsrcLookup, capturedLookupType );
        Assert.IsTrue( capturedLookupKey.StartsWith( "IsrcLookup:", StringComparison.Ordinal ) );

        // Dedup parity: the saga id must equal the one an interactive ISRC lookup would produce.
        string expectedKey = $"IsrcLookup:{Isrc.ToUpperInvariant( )}";
        string expectedSagaId = ISagaStateManager.GenerateSagaId( expectedKey );
        Assert.AreEqual( expectedSagaId, capturedSagaId );
    }

    /// <summary>
    /// An album with UPC seeds the saga with UpcLookup key and type.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueRecord_AlbumWithUpc_SeedsSagaWithUpcKeyAndType( ) {
        MediaLinkResult albumRecord = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        albumRecord.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/album/3TESTID111",
            ExternalId = "UPC98765",
            IsAlbum = true
        };

        SetupRecordList( [("at://album", albumRecord)] );

        LookupRequestType capturedLookupType = default;
        string? capturedLookupKey = null;

        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<string, string, LookupRequestType, string, QueuePriority?, CancellationToken>(
                ( _, key, type, _, _, _ ) => { capturedLookupKey = key; capturedLookupType = type; } )
            .ReturnsAsync( MakeMinimalSaga( ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.AreEqual( LookupRequestType.UpcLookup, capturedLookupType );
        Assert.IsTrue( capturedLookupKey!.StartsWith( "UpcLookup:", StringComparison.Ordinal ) );
    }

    /// <summary>
    /// A record with parseable URLs but no external id: saga key derived from the first native leg
    /// in provider-enum order; stable across calls.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueRecord_NoExternalId_SeedsSagaFromFirstNativeLegDeterministically( ) {
        // Two records with identical URLs but no ISRC — saga ids must match across both.
        MediaLinkResult record1 = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record1.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID99999",
            ExternalId = string.Empty,
            IsAlbum = false
        };
        MediaLinkResult record2 = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -55 ) };
        record2.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID99999",
            ExternalId = string.Empty,
            IsAlbum = false
        };

        SetupRecordList( [("at://r1", record1), ("at://r2", record2)] );

        List<string> capturedSagaIds = [];
        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<string, string, LookupRequestType, string, QueuePriority?, CancellationToken>(
                ( sagaId, _, _, _, _, _ ) => capturedSagaIds.Add( sagaId ) )
            .ReturnsAsync( MakeMinimalSaga( ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 2, capturedSagaIds );
        Assert.AreEqual( capturedSagaIds[0], capturedSagaIds[1],
            "Same native key should produce the same saga id across passes" );
    }

    /// <summary>
    /// The seeded LookupType is always external-id-typed (IsrcLookup or UpcLookup) when an external
    /// id exists — the precondition that makes the coordinator's secondary fan-out suppress.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueRecord_SagaLookupType_IsExternalIdTyped_ToSuppressFanout( ) {
        SetupRecordList( [MakeStaleRecord( "at://track", DateTime.UtcNow.AddDays( -60 ), isrc: "FANOUTSUPPRESSTEST" )] );

        LookupRequestType capturedLookupType = default;
        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<string, string, LookupRequestType, string, QueuePriority?, CancellationToken>(
                ( _, _, type, _, _, _ ) => capturedLookupType = type )
            .ReturnsAsync( MakeMinimalSaga( ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsTrue(
            capturedLookupType is LookupRequestType.IsrcLookup or LookupRequestType.UpcLookup,
            $"Saga LookupType must be external-id-typed to suppress coordinator fan-out; got {capturedLookupType}" );
    }

    /// <summary>
    /// Two records that resolve to the same lookup key produce the same saga id.
    /// GetOrCreateAsync is called with Bulk origin priority.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_TwoRecordsSameLookupKey_ProduceSameSagaId( ) {
        const string SharedIsrc = "SHARED_ISRC_123";

        (string, MediaLinkResult) rec1 = MakeStaleRecord( "at://r1", DateTime.UtcNow.AddDays( -60 ), isrc: SharedIsrc );
        (string, MediaLinkResult) rec2 = MakeStaleRecord( "at://r2", DateTime.UtcNow.AddDays( -50 ), isrc: SharedIsrc );
        SetupRecordList( [rec1, rec2] );

        List<string> capturedSagaIds = [];
        _ = _sagaManagerMock
            .Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<string, string, LookupRequestType, string, QueuePriority?, CancellationToken>(
                ( sagaId, _, _, _, _, _ ) => capturedSagaIds.Add( sagaId ) )
            .ReturnsAsync( MakeMinimalSaga( ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 2, capturedSagaIds );
        Assert.AreEqual( capturedSagaIds[0], capturedSagaIds[1],
            "Both records must produce the same deterministic sagaId" );

        _sagaManagerMock.Verify(
            m => m.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), QueuePriority.Bulk, It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );
    }

    /// <summary>
    /// A shared deterministic saga is owned by record A while record B is observed across three
    /// sweeps. B is promoted directly from its concrete context without overwriting A's pending
    /// slot or deleting the shared active saga.
    /// </summary>
    [TestMethod]
    public async Task RunRefreshPass_SharedSaga_ThreeSweepsPromotesBWithoutDeletingAOwner( ) {
        const string RecordA = "at://shared/a";
        const string RecordB = "at://shared/b";
        const string SharedIsrc = "SHARED_THREE_SWEEP";
        SetupRecordList( [
            MakeStaleRecord( RecordA, DateTime.UtcNow.AddDays( -60 ), isrc: SharedIsrc ),
            MakeStaleRecord( RecordB, DateTime.UtcNow.AddDays( -60 ), isrc: SharedIsrc )
        ] );

        string sharedSagaId = ISagaStateManager.GenerateSagaId( $"{LookupRequestType.IsrcLookup}:{SharedIsrc}" );
        LookupSagaState activeSaga = MakeMinimalSaga( ) with { SagaId = sharedSagaId, InstanceToken = "owner-a" };
        int getCount = 0;
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync(
                It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => Interlocked.Increment( ref getCount ) == 1 ? null : activeSaga );
        _ = _sagaManagerMock.Setup( manager => manager.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( activeSaga );
        Dictionary<string, int> attempts = new( StringComparer.Ordinal );
        _ = _refreshReviewStoreMock.Setup( store => store.IncrementSweepAttemptAsync(
                It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( string uri, CancellationToken _ ) => {
                if (uri == RecordA) { return 1; }
                attempts[uri] = attempts.GetValueOrDefault( uri ) + 1;
                return attempts[uri];
            } );
        RefreshReviewEntry ownerContext = new( ) {
            SourceRecordUri = RecordA,
            SagaId = activeSaga.SagaId,
            InstanceToken = activeSaga.InstanceToken,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = SharedIsrc
        };
        _ = _refreshReviewStoreMock.Setup( store => store.GetPendingAsync(
                RecordA, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( ownerContext );
        _ = _refreshReviewStoreMock.Setup( store => store.GetPendingAsync(
                RecordB, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( (RefreshReviewEntry?)null );
        RefreshReviewEntry? promoted = null;
        _ = _refreshReviewStoreMock.Setup( store => store.PromoteDirectAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<RefreshReviewEntry, string, CancellationToken>( ( entry, _, _ ) => promoted = entry )
            .Returns( Task.CompletedTask );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _refreshReviewStoreMock.Verify( store => store.PromoteDirectAsync(
            It.Is<RefreshReviewEntry>( entry => entry.SourceRecordUri == RecordB ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        Assert.IsNotNull( promoted );
        Assert.AreEqual( activeSaga.SagaId, promoted!.SagaId );
        Assert.AreEqual( activeSaga.InstanceToken, promoted.InstanceToken );
        _refreshReviewStoreMock.Verify( store => store.MarkUnresolvedAsync(
            It.Is<RefreshReviewEntry>( entry => entry.SourceRecordUri == RecordA ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryDeleteAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>An active owner is quarantined after three sweeps without deleting its saga.</summary>
    [TestMethod]
    public async Task RunRefreshPass_ActiveOwner_ReachesThreeSweepsWithoutDeletingSaga( ) {
        const string RecordUri = "at://active-owner";
        const string Isrc = "ACTIVE_OWNER_THREE_SWEEPS";
        SetupRecordList( [MakeStaleRecord( RecordUri, DateTime.UtcNow.AddDays( -60 ), Isrc )] );
        string sagaId = ISagaStateManager.GenerateSagaId( $"{LookupRequestType.IsrcLookup}:{Isrc}" );
        LookupSagaState activeSaga = MakeMinimalSaga( ) with { SagaId = sagaId, InstanceToken = "active-owner" };
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( sagaId, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( activeSaga );
        int attempts = 0;
        _ = _refreshReviewStoreMock.Setup( store => store.IncrementSweepAttemptAsync( RecordUri, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => ++attempts );
        RefreshReviewEntry pending = new( ) {
            SourceRecordUri = RecordUri,
            SagaId = sagaId,
            InstanceToken = activeSaga.InstanceToken,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = Isrc
        };
        _ = _refreshReviewStoreMock.Setup( store => store.GetPendingAsync( RecordUri, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( pending );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _refreshReviewStoreMock.Verify( store => store.PromoteDirectAsync(
            It.Is<RefreshReviewEntry>( entry => entry.SourceRecordUri == RecordUri && entry.InstanceToken == activeSaga.InstanceToken ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _refreshReviewStoreMock.Verify( store => store.MarkUnresolvedAsync(
            It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryDeleteAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _refreshReviewStoreMock.Verify( store => store.RegisterPendingAsync( It.IsAny<RefreshReviewEntry>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    #endregion

    #region Fire-and-Forget Tests

    /// <summary>
    /// The refresh pass enqueues and never polls saga completion — <c>GetAsync</c> is never called.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_DoesNotWaitForSagaCompletion( ) {
        SetupRecordList( [
            MakeStaleRecord( "at://test/1", DateTime.UtcNow.AddDays( -60 ), isrc: "ISRC_ONE" ),
            MakeStaleRecord( "at://test/2", DateTime.UtcNow.AddDays( -50 ), isrc: "ISRC_TWO" )
        ] );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );

        _sagaManagerMock.Verify(
            m => m.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 2 )
        );
    }

    #endregion

    #region Reliability Tests

    /// <summary>A replaced saga rejected by atomic staging leaves no pending context or queued legs.</summary>
    [TestMethod]
    public async Task EnqueueRecordAsync_WhenInitializationFenceIsLost_DoesNotRegisterOrEnqueue( ) {
        _enabledProviders = [SupportedProviders.Spotify];
        SetupRecordList( [MakeStaleRecord( "at://init-replaced", DateTime.UtcNow.AddDays( -60 ), isrc: "INIT_REPLACED" )] );
        _ = _dispatchOutboxMock.Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                QueuePriority.Bulk,
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new Dictionary<SupportedProviders, ProviderDispatchStageOutcome> {
                [SupportedProviders.Spotify] = ProviderDispatchStageOutcome.SagaInstanceMismatch
            } );

        await CreateService( ).RunRefreshPassAsync( TestContext.CancellationToken );

        _refreshReviewStoreMock.Verify( store => store.RegisterPendingAsync(
            It.IsAny<RefreshReviewEntry>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>An immediate relay failure leaves every staged leg durable for the owner relay.</summary>
    [TestMethod]
    public async Task EnqueueRecordAsync_WhenEnqueueFailureCasIsLost_DoesNotEnqueueRemainingLegs( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic];
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = "CAS_LOST",
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/x/1234567890?i=9876543210",
            ExternalId = "CAS_LOST",
            IsAlbum = false
        };
        SetupRecordList( [("at://enqueue-cas-lost", record)] );

        Mock<IRequestQueue<QueuedLookupRequest>> spotifyQueue = new( );
        Mock<IRequestQueue<QueuedLookupRequest>> appleQueue = new( );
        _ = spotifyQueue.Setup( queue => queue.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "enqueue failed" ) );
        _ = appleQueue.Setup( queue => queue.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        _ = _queueResolverMock.Setup( resolver => resolver.GetQueue( SupportedProviders.Spotify ) ).Returns( spotifyQueue.Object );
        _ = _queueResolverMock.Setup( resolver => resolver.GetQueue( SupportedProviders.AppleMusic ) ).Returns( appleQueue.Object );
        await CreateService( ).RunRefreshPassAsync( TestContext.CancellationToken );

        _dispatchOutboxMock.Verify( outbox => outbox.StageRefreshBatchAsync(
            It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
            QueuePriority.Bulk,
            It.IsAny<RefreshReviewEntry>( ),
            It.IsAny<CancellationToken>( ) ), Times.Once );
        appleQueue.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// When enqueuing one record throws, it is counted as an error and skipped; the remaining
    /// records are still enqueued.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_PerRecordException_SkipsAndContinues( ) {
        (string, MediaLinkResult) rec1 = MakeStaleRecord( "at://test/1", DateTime.UtcNow.AddDays( -60 ), isrc: "ISRC_ONE" );
        (string, MediaLinkResult) rec2 = MakeStaleRecord( "at://test/2", DateTime.UtcNow.AddDays( -50 ), isrc: "ISRC_TWO" );
        (string, MediaLinkResult) rec3 = MakeStaleRecord( "at://test/3", DateTime.UtcNow.AddDays( -40 ), isrc: "ISRC_THREE" );
        SetupRecordList( [rec1, rec2, rec3] );

        int callCount = 0;
        _ = _queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns<QueuedLookupRequest, QueuePriority, CancellationToken>( ( _, _, _ ) => {
                int count = System.Threading.Interlocked.Increment( ref callCount );
                if (count == 2) {
                    throw new InvalidOperationException( "Simulated enqueue failure" );
                }
                return Task.CompletedTask;
            } );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.AreEqual( 3, callCount );
    }

    /// <summary>
    /// When <c>ListAllRecordsAsync</c> throws a non-OCE exception, <c>RunRefreshPassAsync</c> now
    /// bubbles the exception rather than swallowing it. The swallow was removed so the outer loop
    /// can apply the failure backoff.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPassAsync_FatalEnumerationError_Bubbles( ) {
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) =>
                throw new InvalidOperationException( "Fatal enumeration error" ) );

        StaleCacheRefreshBackgroundService service = CreateService( );

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async ( ) => await service.RunRefreshPassAsync( TestContext.CancellationToken ),
            "A fatal enumeration error must bubble out of RunRefreshPassAsync, not be swallowed" );
    }

    /// <summary>
    /// When <c>ListAllRecordsAsync</c> returns an empty enumerable, the pass completes without error
    /// and <c>EnqueueAsync</c> is never called.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPassAsync_EmptyEnumerable_CompletesWithoutEnqueue( ) {
        SetupRecordList( [] );
        StaleCacheRefreshBackgroundService service = CreateService( );

        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );
    }

    /// <summary>
    /// When the cancellation token is cancelled during enumeration, <c>RunRefreshPassAsync</c>
    /// throws <see cref="OperationCanceledException"/> (the kept OCE guard logs 5527 and rethrows).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPassAsync_TokenCancelled_ThrowsOce( ) {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );

        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, ct, _ ) => {
                ct.ThrowIfCancellationRequested( );
                return AsyncEnumerable.Empty<(string, MediaLinkResult)>( );
            } );

        await cts.CancelAsync( );
        StaleCacheRefreshBackgroundService service = CreateService( );

        _ = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async ( ) => await service.RunRefreshPassAsync( cts.Token ),
            "An OCE from a cancelled token must propagate out of RunRefreshPassAsync" );

        int refreshCancelledId = BridgeBeats.Worker.Maintenance.Logging.LogEventIds.RefreshCancelled;
#pragma warning disable CA1873
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.Is<EventId>( e => e.Id == refreshCancelledId ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once,
            "5527 (RefreshCancelled) must be logged when the OCE guard fires" );
#pragma warning restore CA1873
    }

    /// <summary>
    /// When the host cancels the service, the loop exits cleanly with no exception.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundService.StartAsync</c> returns <c>Task.CompletedTask</c> unconditionally and
    /// immediately — it schedules <c>ExecuteAsync</c> via <c>Task.Run</c> without waiting for it to
    /// reach a first await — so awaiting that captured task is a no-op and would make this test pass
    /// vacuously even if <c>StopAsync</c> itself threw. <c>StopAsync</c> genuinely awaits the
    /// underlying execute task before returning, but on net10.0 it does so via
    /// <c>WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c> and does not
    /// re-throw a faulted or cancelled execute task — so "<c>StopAsync</c> completes without
    /// throwing" alone cannot distinguish a graceful stop from an
    /// <c>OperationCanceledException</c> escaping one of the loop's guarded await points and
    /// faulting/cancelling <c>ExecuteAsync</c>. <c>ExecuteTask.Status</c> is the assertable surface
    /// for that distinction. The <c>startedTcs</c> start-gate additionally proves the pass actually
    /// reached <c>ListAllRecordsAsync</c> before cancellation fires — without it, a fast-enough
    /// cancel could skip dispatch entirely before <c>ExecuteAsync</c> ever installs its own
    /// try/catch, leaving <c>Task.Run</c>'s own token-cancellation to end <c>ExecuteTask</c> as
    /// <c>Canceled</c> rather than <c>RanToCompletion</c> — failing this assertion for a reason
    /// unrelated to the guard under test, not passing it vacuously.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WhenCancelled_StopsGracefully( ) {
        // Arrange: zero startup grace/jitter, and a PDS stream that blocks on a cancellable infinite
        // wait once reached, so the first pass is genuinely in flight (not already completed) when
        // cancellation fires below — a pass that had already finished and moved on to the next loop
        // wait would exit gracefully via that wait's own catch regardless of whether this pass's own
        // guard exists, masking a missing guard. startedTcs signals once the stream is reached,
        // proving ExecuteAsync's first pass actually began before cancellation.
        TaskCompletionSource startedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, ct, _ ) => BlockUntilCancelledAsync( startedTcs, ct ) );

        StaleCacheRefreshBackgroundService service = CreateService( );
        service._startupGrace = TimeSpan.Zero;
        service._maxJitter = TimeSpan.Zero;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );
        _ = service.StartAsync( cts.Token );
        await startedTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );

        await service.StopAsync( CancellationToken.None ); // must not throw
        Assert.AreEqual( TaskStatus.RanToCompletion, service.ExecuteTask!.Status,
            "ExecuteAsync must reach RanToCompletion; Canceled/Faulted means an OperationCanceledException "
            + "escaped one of the loop's guarded await points instead of being caught and exiting cleanly." );
    }

    /// <summary>
    /// Async-iterator stream used by <see cref="ExecuteAsync_WhenCancelled_StopsGracefully"/>:
    /// signals <paramref name="startedTcs"/> once enumeration begins, then blocks on a cancellable
    /// infinite delay so the caller's refresh pass is genuinely mid-flight when the token is
    /// cancelled, rather than already completed.
    /// </summary>
    private static async IAsyncEnumerable<(string AtUri, MediaLinkResult Result)> BlockUntilCancelledAsync(
        TaskCompletionSource startedTcs,
        [EnumeratorCancellation] CancellationToken cancellationToken ) {
        _ = startedTcs.TrySetResult( );
        await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
        yield break;
    }

    #endregion

    #region Leg Enqueue Failure Tests

    /// <summary>
    /// When one provider's queue throws on EnqueueAsync, that provider's state is recorded as
    /// complete-and-failed in the saga; the other legs are still enqueued; and SetIsPartialAsync
    /// is never called.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueRecordAsync_OneImmediateRelayThrows_LeavesLegPendingAndRelaysOthers( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic];

        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = "ISRC_TWOLEG",
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/x/1234567890?i=9876543210",
            ExternalId = "ISRC_TWOLEG",
            IsAlbum = false
        };

        SetupRecordList( [("at://two-leg", record)] );

        Mock<IRequestQueue<QueuedLookupRequest>> spotifyQueue = new( );
        Mock<IRequestQueue<QueuedLookupRequest>> appleQueue = new( );

        _ = spotifyQueue
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Simulated Spotify queue failure" ) );
        _ = appleQueue
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        _ = _queueResolverMock.Setup( r => r.GetQueue( SupportedProviders.Spotify ) ).Returns( spotifyQueue.Object );
        _ = _queueResolverMock.Setup( r => r.GetQueue( SupportedProviders.AppleMusic ) ).Returns( appleQueue.Object );

        List<ProviderLookupState> recordedStates = [];
        _ = _sagaManagerMock
            .Setup( m => m.TryUpdateProviderStateAsync(
                It.IsAny<string>( ),
                It.IsAny<ProviderLookupState>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<string, ProviderLookupState, string, CancellationToken>( ( _, state, _, _ ) => recordedStates.Add( state ) )
            .ReturnsAsync( true );

        StaleCacheRefreshBackgroundService service = CreateService( );
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        // Immediate relay failure is non-terminal: the durable outbox owner will retry Spotify.
        Assert.IsEmpty( recordedStates );

        // Apple leg still receives its immediate relay attempt.
        appleQueue.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "The non-failing leg must still be enqueued" );
        Assert.DoesNotContain( s => s.Provider == SupportedProviders.AppleMusic, recordedStates,
            "A successful leg must not be recorded as failed" );

    }

    /// <summary>
    /// When all provider queues throw on EnqueueAsync, all legs are marked complete-as-failed in
    /// the saga; the method returns true (the record was dispatched — the saga will be finalized
    /// via the coordinator poll backstop); and SetIsPartialAsync is never called.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueRecordAsync_AllImmediateRelaysThrow_LeavesAllLegsPending( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic];

        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = "ISRC_ALLTHROW",
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/x/1234567890?i=9876543210",
            ExternalId = "ISRC_ALLTHROW",
            IsAlbum = false
        };

        SetupRecordList( [("at://all-throw", record)] );

        _ = _queueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "All queues down" ) );

        List<ProviderLookupState> recordedStates = [];
        _ = _sagaManagerMock
            .Setup( m => m.TryUpdateProviderStateAsync(
                It.IsAny<string>( ),
                It.IsAny<ProviderLookupState>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<string, ProviderLookupState, string, CancellationToken>( ( _, state, _, _ ) => recordedStates.Add( state ) )
            .ReturnsAsync( true );

        StaleCacheRefreshBackgroundService service = CreateService( );

        // RunRefreshPassAsync must complete without throwing and count the record as enqueued.
        await service.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.IsEmpty( recordedStates,
            "An immediate relay outage must not terminally fail durable provider legs" );

    }

    #endregion

    #region AppHost Branches Tests

    /// <summary>
    /// Both the production (AddProductionExecutable) and development (AddProject) branches of the
    /// AppHost cache-bootstrap wiring inject the new refresh tunables and the retuned defaults.
    /// Each tunable is asserted to appear in EACH branch sub-string individually, so a tunable
    /// injected twice in the prod branch but zero times in the dev branch is caught — the silent-revert
    /// trap where the whole-file count of 2 would previously pass.
    /// </summary>
    [TestMethod]
    public void AppHost_BothCacheBootstrapBranches_InjectNewTunables( ) {
        // Locate the AppHost Program.cs relative to the test assembly.
        // Assembly → Tests/bin/Debug/net10.0 → walk up to repo root → src/BridgeBeats.AppHost/Program.cs
        string assemblyDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly( ).Location )!;

        string? repoRoot = assemblyDir;
        while (repoRoot is not null && !System.IO.File.Exists( System.IO.Path.Combine( repoRoot, "BridgeBeats.sln" ) )) {
            repoRoot = System.IO.Path.GetDirectoryName( repoRoot );
        }

        if (repoRoot is null) {
            Assert.Inconclusive( "Could not locate BridgeBeats.sln from the test assembly path; skipping AppHost source assertion." );
            return;
        }

        string appHostPath = System.IO.Path.Combine( repoRoot, "src", "BridgeBeats.AppHost", "Program.cs" );
        Assert.IsTrue( System.IO.File.Exists( appHostPath ),
            $"AppHost Program.cs not found at expected path: {appHostPath}" );

        string source = System.IO.File.ReadAllText( appHostPath );

        // Locate the cache-bootstrap production and development branch sub-strings by their unique
        // markers. The production branch is identified by its AddProductionExecutable call for
        // "cache-bootstrap"; the development branch is identified by the AddProject call for the
        // same worker. Splitting at these two markers yields two non-overlapping sub-strings, one
        // per branch. Asserting each tunable appears in each sub-string catches the silent-revert
        // trap: a tunable removed from one branch but still present in the other still has a
        // whole-file count >= 1, but fails the per-branch assertion.
        const string ProdBranchMarker = "AddProductionExecutable( \"maintenance\"";
        const string DevBranchMarker  = "AddProject<Projects.BridgeBeats_Worker_Maintenance>( \"maintenance\" )";

        int prodIdx = source.IndexOf( ProdBranchMarker, StringComparison.Ordinal );
        int devIdx  = source.IndexOf( DevBranchMarker,  StringComparison.Ordinal );

        if (prodIdx < 0 || devIdx < 0 || prodIdx >= devIdx) {
            Assert.Inconclusive(
                "Could not locate the distinct cache-bootstrap prod/dev branch markers in AppHost Program.cs. " +
                "The source structure may have changed; update the marker constants in this test." );
            return;
        }

        // prod sub-string: from the prod marker to the start of the dev marker.
        string prodBranch = source.Substring( prodIdx, devIdx - prodIdx );
        // dev sub-string: from the dev marker to end-of-file (nothing after dev branch for cache-bootstrap).
        string devBranch  = source.Substring( devIdx );

        string[] requiredEnvVars = [
            "BridgeBeats__RefreshIntervalHours",
            "BridgeBeats__MaxRecordsPerRun",
            "BridgeBeats__RefreshRetryMinutes",
        ];

        foreach (string envVar in requiredEnvVars) {
            Assert.IsTrue( prodBranch.Contains( envVar, StringComparison.Ordinal ),
                $"'{envVar}' must appear in the production cache-bootstrap branch (AddProductionExecutable). " +
                $"A missing occurrence means the prod branch silently omits the tunable." );

            Assert.IsTrue( devBranch.Contains( envVar, StringComparison.Ordinal ),
                $"'{envVar}' must appear in the development cache-bootstrap branch (AddProject). " +
                $"A missing occurrence means the dev branch silently omits the tunable." );
        }
    }

    /// <summary>
    /// When <c>BridgeBeats:RefreshRetryMinutes</c> is absent from configuration,
    /// <c>ValidateConfiguration</c> defaults to 5 minutes.
    /// </summary>
    [TestMethod]
    public void ValidateConfiguration_RefreshRetryMinutes_DefaultsToFiveMinutes( ) {
        Microsoft.Extensions.Hosting.HostApplicationBuilder builder =
            Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder( );

        builder.Configuration["BridgeBeats:ATProtoIdentifier"] = "test@example.com";
        builder.Configuration["BridgeBeats:ATProtoPassword"] = "password";
        builder.Configuration["BridgeBeats:ATProtoUserDID"] = "did:plc:testuser123";

        (_, _, _, _, _, _, _, _, int refreshRetryMinutes) =
            BridgeBeats.Worker.Maintenance.Program.ValidateConfiguration( builder );

        Assert.AreEqual( 5, refreshRetryMinutes,
            "RefreshRetryMinutes must default to 5 when unset in configuration" );
    }

    #endregion

    #region Settings Binding Tests

    /// <summary>
    /// Settings with unset env falls back to 6h interval, 500 max records, and 5-minute retry
    /// (the defaults verified via the settings record constructor).
    /// </summary>
    [TestMethod]
    public void Settings_DefaultValues_MatchSpecifiedDefaults( ) {
        CacheBootstrapSettings defaults = MakeSettings( );

        Assert.AreEqual( TimeSpan.FromHours( 6 ), defaults.RefreshInterval );
        Assert.AreEqual( 500, defaults.MaxRecordsPerRun );
        Assert.AreEqual( TimeSpan.FromMinutes( 5 ), defaults.RefreshRetryInterval );
    }

    /// <summary>
    /// The settings record stores exactly the values it is constructed with.
    /// </summary>
    [TestMethod]
    public void Settings_StoresProvidedValues( ) {
        CacheBootstrapSettings settings = new(
            s_testPdsUri,
            TestUserDid,
            BootstrapInterval: TimeSpan.FromHours( 6 ),
            CacheDays: 30,
            RefreshInterval: TimeSpan.FromHours( 12 ),
            MaxRecordsPerRun: 250,
            RefreshRetryInterval: TimeSpan.FromMinutes( 3 )
        );

        Assert.AreEqual( TimeSpan.FromHours( 12 ), settings.RefreshInterval );
        Assert.AreEqual( 250, settings.MaxRecordsPerRun );
        Assert.AreEqual( TimeSpan.FromMinutes( 3 ), settings.RefreshRetryInterval );
    }

    #endregion

    #region No-Drip Regression Guard

    /// <summary>
    /// A multi-record pass enqueues every leg without any artificial inter-leg delay: all 6 legs
    /// (2 records × 3 providers) are enqueued, and the entire pass completes within 5 seconds.
    /// The wall-clock bound is a re-introduction guard, not a performance benchmark; it would be
    /// exceeded if any per-leg delay were restored.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunRefreshPass_MultiRecord_EnqueuesAllLegsWithoutDelay( ) {
        _enabledProviders = [SupportedProviders.Spotify, SupportedProviders.AppleMusic, SupportedProviders.Tidal];

        SetupRecordList( [
            ("at://rec1", MakeThreeProviderRecord( isrc: "ISRC_ONE" )),
            ("at://rec2", MakeThreeProviderRecord( isrc: "ISRC_TWO" ))
        ] );

        StaleCacheRefreshBackgroundService service = CreateService( );

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource( TestContext.CancellationToken );
        cts.CancelAfter( TimeSpan.FromSeconds( 5 ) );

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew( );
        await service.RunRefreshPassAsync( cts.Token );
        sw.Stop( );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Exactly( 6 ),
            "2 records × 3 providers = 6 legs must all be enqueued" );

        Assert.IsLessThan( TimeSpan.FromSeconds( 5 ), sw.Elapsed,
            "The pass must complete in under 5 seconds; any per-leg delay would exceed this bound." );
    }

    #endregion

    #region Helper Methods

    private StaleCacheRefreshBackgroundService CreateService( IRefreshReviewStore? reviewStore = null ) =>
        new(
            _atProtoStorageMock.Object,
            _sagaManagerMock.Object,
            _dispatchOutboxMock.Object,
            _redisMock.Object,
            _enabledProviders,
            _settings,
            _loggerMock.Object,
            reviewStore ?? _refreshReviewStoreMock.Object
        );

    private CacheBootstrapSettings MakeSettings(
        TimeSpan? refreshInterval = null,
        int maxRecordsPerRun = 500,
        int cacheDays = 30,
        TimeSpan? refreshRetryInterval = null
    ) =>
        new(
            s_testPdsUri,
            TestUserDid,
            BootstrapInterval: TimeSpan.FromHours( 6 ),
            CacheDays: cacheDays,
            RefreshInterval: refreshInterval ?? TimeSpan.FromHours( 6 ),
            MaxRecordsPerRun: maxRecordsPerRun,
            RefreshRetryInterval: refreshRetryInterval ?? TimeSpan.FromMinutes( 5 )
        );

    private void SetupRecordList( List<(string AtUri, MediaLinkResult Result)> records ) {
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( records.ToAsyncEnumerable( ) );
    }

    private static (string AtUri, MediaLinkResult Result) MakeStaleRecord(
        string atUri,
        DateTime lookedUpAt,
        string isrc = "ISRC_DEFAULT"
    ) {
        MediaLinkResult result = new( ) { LookedUpAt = lookedUpAt };
        result.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = isrc,
            Title = "Stale Song",
            Artist = "Stale Artist",
            IsAlbum = false
        };
        return (atUri, result);
    }

    private static (string AtUri, MediaLinkResult Result) MakeFreshRecord(
        string atUri,
        DateTime lookedUpAt,
        string isrc = "FRESH_ISRC_DEFAULT"
    ) {
        MediaLinkResult result = new( ) { LookedUpAt = lookedUpAt };
        result.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTFRESH1",
            ExternalId = isrc,
            Title = "Fresh Song",
            Artist = "Fresh Artist",
            IsAlbum = false
        };
        return (atUri, result);
    }

    /// <summary>
    /// Creates a record with parseable URLs for all three providers and the given ISRC.
    /// </summary>
    private static MediaLinkResult MakeThreeProviderRecord( string isrc ) {
        MediaLinkResult result = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        result.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = "https://open.spotify.com/track/3SPOTID12345",
            ExternalId = isrc,
            IsAlbum = false,
            Title = "Test",
            Artist = "Artist"
        };
        result.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = "https://music.apple.com/us/album/something/1234567890?i=9876543210",
            ExternalId = isrc,
            IsAlbum = false,
            Title = "Test",
            Artist = "Artist"
        };
        result.Results[SupportedProviders.Tidal] = new MusicLookupResult {
            URL = "https://tidal.com/browse/track/12345678",
            ExternalId = isrc,
            IsAlbum = false,
            Title = "Test",
            Artist = "Artist"
        };
        return result;
    }

    private static LookupSagaState MakeMinimalSaga( ) =>
        new( ) {
            SagaId = "minisaga",
            LookupKey = "test:key",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "TESTISRC",
            OriginPriority = QueuePriority.Bulk,
            InstanceToken = "test-instance"
        };

    #endregion
}
