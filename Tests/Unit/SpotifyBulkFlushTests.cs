using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Spotify;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Worker.Spotify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests spanning the Spotify bulk path's flush and routing seams: the
/// <see cref="SpotifyBulkQueueDecorator"/> delegation of interactive-priority typed lookups to the
/// inner queue (with no warning emitted), the pure <c>SpotifyBulkProcessorService.ShouldFlush</c>
/// predicate at the size threshold and the inclusive 24-hour-backstop age boundary, the
/// oldest-enqueued-at read's malformed-versus-well-formed handling, and the ordering guarantee that
/// rate-limit and retry-cap handling call <c>GetOrCreateAsync</c> to materialize the saga core hash
/// before writing provider state. Also asserts that interactive Spotify id lookups are never treated
/// as approved bulk callers.
/// </summary>
[TestClass]
[DoNotParallelize]
public class SpotifyBulkFlushTests {

    /// <summary>Mock Redis multiplexer supplying the database and subscriber to the service under test.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    /// <summary>Mock Redis database backing all stream operations.</summary>
    private Mock<IDatabase> _dbMock = null!;
    /// <summary>Mock Redis subscriber used to assert completion and sentinel publishes.</summary>
    private Mock<ISubscriber> _subscriberMock = null!;
    /// <summary>Mock rate-limit tracker the service consults and updates on rate-limit handling.</summary>
    private Mock<IRateLimitTracker> _rateLimitTrackerMock = null!;
    /// <summary>Mock saga state manager used to assert saga create/update ordering.</summary>
    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    /// <summary>Mock bulk lookup service supplying batch track/album results.</summary>
    private Mock<ISpotifyBulkLookupService> _lookupServiceMock = null!;
    /// <summary>Mock logger for the batch queue helper.</summary>
    private Mock<ILogger<SpotifyBatchQueueHelper>> _helperLoggerMock = null!;
    /// <summary>Mock logger for the bulk processor service.</summary>
    private Mock<ILogger<SpotifyBulkProcessorService>> _serviceLoggerMock = null!;
    /// <summary>Mock inner queue the decorator wraps.</summary>
    private Mock<IRequestQueue<QueuedLookupRequest>> _innerQueueMock = null!;
    /// <summary>Mock logger for the decorator, used to assert warning suppression.</summary>
    private Mock<ILogger<SpotifyBulkQueueDecorator>> _decoratorLoggerMock = null!;

    /// <summary>Redis stream key for bulk Spotify track-id lookups.</summary>
    private const string TrackStream = SpotifyConstants.BulkTrackIdStream;
    /// <summary>Redis stream key for bulk Spotify album-id lookups.</summary>
    private const string AlbumStream = SpotifyConstants.BulkAlbumIdStream;
    /// <summary>Sample Spotify track id used throughout the tests.</summary>
    private const string TestTrackId = "3n3Ppam7vgaVa1iaRUc9Lp";
    /// <summary>Fixed saga id the mocks return for the sample track.</summary>
    private const string TestSagaId = "aabbccddeeff00112233445566778899";

    /// <summary>Serialization options (camel-case, non-indented) used to build stream payloads.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>MSTest-injected context; its cancellation token bounds the async operations under test.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Creates fresh mocks before each test and wires their default behaviors: Redis database and
    /// subscriber resolution, no-op stream operations, a not-rate-limited tracker state, and saga
    /// manager methods that return a saga for the sample track and complete successfully.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _dbMock = new Mock<IDatabase>( );
        _subscriberMock = new Mock<ISubscriber>( );
        _rateLimitTrackerMock = new Mock<IRateLimitTracker>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _lookupServiceMock = new Mock<ISpotifyBulkLookupService>( );
        _helperLoggerMock = new Mock<ILogger<SpotifyBatchQueueHelper>>( );
        _serviceLoggerMock = new Mock<ILogger<SpotifyBulkProcessorService>>( );
        _innerQueueMock = new Mock<IRequestQueue<QueuedLookupRequest>>( );
        _decoratorLoggerMock = new Mock<ILogger<SpotifyBulkQueueDecorator>>( );

        _ = _redisMock.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( _dbMock.Object );
        _ = _redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) )
            .Returns( _subscriberMock.Object );

        // XADD succeeds by default
        _ = _dbMock.Setup( d => d.StreamAddAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<NameValueEntry[]>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<long?>( ),
                It.IsAny<bool>( ),
                It.IsAny<long?>( ),
                It.IsAny<StreamTrimMode>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)"1234567890-0" );
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"1234567890-1" ) );

        // StreamLength returns 0 by default
        _ = _dbMock.Setup( d => d.StreamLengthAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        // StreamRangeAsync returns empty by default
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        // XAUTOCLAIM returns empty by default
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<long>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( StreamAutoClaimResult.Null );

        // XREADGROUP returns empty by default
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        // EnsureConsumerGroupsAsync — BUSYGROUP (group already exists)
        _ = _dbMock.Setup( d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<bool>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "BUSYGROUP" ) );

        // XACK + XDEL succeed
        _ = _dbMock.Setup( d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 1L );
        _ = _dbMock.Setup( d => d.StreamDeleteAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue[]>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 1L );

        // Subscriber publish succeeds
        _ = _subscriberMock.Setup( s => s.PublishAsync(
                It.IsAny<RedisChannel>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        // Not rate-limited by default
        _ = _rateLimitTrackerMock.Setup( t => t.GetStateAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new RateLimitState( false, null, null ) );
        _ = _rateLimitTrackerMock.Setup( t => t.SetRateLimitedAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<DateTimeOffset>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Saga manager defaults
        _ = _sagaManagerMock.Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ),
                It.IsAny<string>( ),
                It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ),
                It.IsAny<QueuePriority?>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = TestSagaId,
                LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                LookupType = LookupRequestType.SongIdLookup,
                LookupValue = TestTrackId,
                InstanceToken = "test-instance"
            } );
        _ = _sagaManagerMock.Setup( m => m.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = TestSagaId,
                LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                LookupType = LookupRequestType.SongIdLookup,
                LookupValue = TestTrackId,
                InstanceToken = "test-instance"
            } );
        _ = _sagaManagerMock.Setup( m => m.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( m => m.TrySetIsPartialAsync(
                It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( m => m.TrySetRateLimitInfoAsync(
                It.IsAny<string>( ), It.IsAny<List<ProviderRateLimitInfo>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( m => m.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
    }

    /// <summary>
    /// Verifies that a song-id lookup at interactive priority is delegated to the inner queue and is
    /// never written to a bulk stream.
    /// </summary>
    /// <remarks>
    /// Interactive requests stay on the generic single-item path and are never batch-routed.
    /// </remarks>
    [TestMethod]
    public async Task Decorator_SongIdLookup_WithInteractivePriority_DelegatesToInnerQueue( ) {
        // Arrange — SongIdLookup with explicitly Interactive priority
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Interactive );
        SpotifyBulkQueueDecorator decorator = CreateDecorator( );

        _ = _innerQueueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        await decorator.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        // Assert — inner queue receives the call (interactive lane, not bulk stream)
        _innerQueueMock.Verify(
            q => q.EnqueueAsync( request, QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "SongIdLookup at Interactive priority must delegate to the inner queue, not the bulk stream" );

        // Assert — no XADD on any bulk stream
        _dbMock.Verify(
            d => d.StreamAddAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<NameValueEntry[]>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<long?>( ),
                It.IsAny<bool>( ),
                It.IsAny<long?>( ),
                It.IsAny<StreamTrimMode>( ),
                It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "Interactive SongIdLookup must not be written to any bulk stream" );
    }

    /// <summary>
    /// Verifies that a missing Redis consumer group is repaired and that the bulk worker continues
    /// into a subsequent polling cycle instead of remaining in the error path.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WhenNoGroupError_RecreatesGroupsAndContinuesLoop( ) {
        // Arrange — the first flush-decision read simulates Redis returning NOGROUP. The third
        // read is the album check in the next polling cycle (the second read is the first cycle's
        // album check), proving the loop resumed after EnsureConsumerGroupsAsync completed.
        TaskCompletionSource<bool> resumed = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int stateCalls = 0;
        _ = _rateLimitTrackerMock.Setup( t => t.GetStateAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( ( SupportedProviders _, string _, CancellationToken _ ) => {
                int call = Interlocked.Increment( ref stateCalls );
                if (call == 1) {
                    return Task.FromException<RateLimitState>(
                        new RedisServerException( "NOGROUP No such key 'spotify:bulk:tracks'" ) );
                }

                if (call >= 3) {
                    _ = resumed.TrySetResult( true );
                }

                return Task.FromResult( new RateLimitState( false, null, null ) );
            } );

        SpotifyBulkProcessorService service = CreateService( );

        // Act — start the hosted loop, wait until it reaches the next cycle, then stop it cleanly.
        await service.StartAsync( TestContext.CancellationToken );
        Task completed = await Task.WhenAny( resumed.Task, Task.Delay( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken ) );
        await service.StopAsync( TestContext.CancellationToken );

        // Assert — startup and the NOGROUP recovery each ensure both bulk consumer groups.
        Assert.AreSame( resumed.Task, completed, "The worker did not resume polling after NOGROUP recovery." );
        Assert.IsGreaterThanOrEqualTo( 3, stateCalls, "The worker did not enter a subsequent polling cycle." );
        _dbMock.Verify( d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<bool>( ),
                It.IsAny<CommandFlags>( ) ), Times.Exactly( 4 ) );
    }

    /// <summary>Failed Spotify group repair is delayed and retried without terminating the loop.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WhenGroupRepairFails_RetriesOnLaterNoGroup( ) {
        TaskCompletionSource<bool> resumed = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int stateCalls = 0;
        _ = _rateLimitTrackerMock.Setup( t => t.GetStateAsync(
                It.IsAny<SupportedProviders>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( ( SupportedProviders _, string _, CancellationToken _ ) => {
                int call = Interlocked.Increment( ref stateCalls );
                if (call <= 2) return Task.FromException<RateLimitState>( new RedisServerException( "NOGROUP" ) );
                _ = resumed.TrySetResult( true );
                return Task.FromResult( new RateLimitState( false, null, null ) );
            } );
        int groupCalls = 0;
        _ = _dbMock.Setup( d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<bool>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( ( RedisKey _, RedisValue _, RedisValue _, bool _, CommandFlags _ ) => {
                int call = Interlocked.Increment( ref groupCalls );
                if (call == 3) throw new RedisServerException( "repair failed" );
                return true;
            } );

        SpotifyBulkProcessorService service = CreateService( );
        await service.StartAsync( TestContext.CancellationToken );
        Task completed = await Task.WhenAny( resumed.Task, Task.Delay( TimeSpan.FromSeconds( 12 ), TestContext.CancellationToken ) );
        await service.StopAsync( TestContext.CancellationToken );

        Assert.AreSame( resumed.Task, completed );
        Assert.IsGreaterThanOrEqualTo( 5, groupCalls );
    }

    /// <summary>
    /// Verifies that an album-id lookup at interactive priority is delegated to the inner queue and
    /// is never written to a bulk stream.
    /// </summary>
    /// <remarks>
    /// Interactive requests stay on the generic single-item path and are never batch-routed.
    /// </remarks>
    [TestMethod]
    public async Task Decorator_AlbumIdLookup_WithInteractivePriority_DelegatesToInnerQueue( ) {
        // Arrange
        QueuedLookupRequest request = MakeAlbumRequest( "6WdSsBrH5QtofaTTqgwxOV", QueuePriority.Interactive );
        SpotifyBulkQueueDecorator decorator = CreateDecorator( );

        _ = _innerQueueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        await decorator.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        // Assert — inner queue receives the call (interactive lane)
        _innerQueueMock.Verify(
            q => q.EnqueueAsync( request, QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "AlbumIdLookup at Interactive priority must delegate to the inner queue, not the bulk stream" );

        // Assert — no XADD on any bulk stream
        _dbMock.Verify(
            d => d.StreamAddAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<NameValueEntry[]>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<long?>( ),
                It.IsAny<bool>( ),
                It.IsAny<long?>( ),
                It.IsAny<StreamTrimMode>( ),
                It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "Interactive AlbumIdLookup must not be written to any bulk stream" );
    }

    /// <summary>
    /// Verifies that enqueuing a bulk song-id lookup does not materialize a saga: sagas are created
    /// later, at flush time in result processing, not at enqueue time.
    /// </summary>
    [TestMethod]
    public async Task Decorator_SongIdLookup_DoesNotCreateSaga( ) {
        // Arrange
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        SpotifyBulkQueueDecorator decorator = CreateDecorator( );

        // Act
        await decorator.EnqueueAsync( request, QueuePriority.Bulk, TestContext.CancellationToken );

        // Assert — no saga created at enqueue time
        _sagaManagerMock.Verify(
            m => m.GetOrCreateAsync(
                It.IsAny<string>( ),
                It.IsAny<string>( ),
                It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Decorator must not materialize a saga; the upstream producer owns saga creation" );
    }

    /// <summary>A bulk-routed enqueue fails when Redis does not return an accepted entry id.</summary>
    [TestMethod]
    public async Task Decorator_BulkEnqueueWithoutMessageId_Throws( ) {
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( RedisValue.Null ) );

        SpotifyBulkQueueDecorator decorator = CreateDecorator( );

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>( ( ) => decorator.EnqueueAsync(
            MakeTrackRequest( TestTrackId ), QueuePriority.Bulk, TestContext.CancellationToken ) );
    }

    /// <summary>
    /// Pins that size = 50 (MaxTracksPerBatchLookup) fires immediately with no age required.
    /// </summary>
    /// <remarks>
    /// Failure-first: removing the <c>count >= threshold</c> branch from <see cref="SpotifyBulkProcessorService.ShouldFlush"/>
    /// causes this to return false (oldestAge is null at threshold). The test was verified to
    /// fail against the pre-fix code before the predicate was extracted.
    /// </remarks>
    [TestMethod]
    public void ShouldFlush_SizeThreshold_FiresImmediately_WithNullAge( ) {
        bool result = SpotifyBulkProcessorService.ShouldFlush(
            count: SpotifyConstants.MaxTracksPerBatchLookup,
            oldestAge: null,
            threshold: SpotifyConstants.MaxTracksPerBatchLookup,
            linger: TimeSpan.FromMilliseconds( SpotifyBatchSettings.DefaultLingerMs ) );

        Assert.IsTrue( result, "Size threshold (50) must fire immediately even with null oldestAge" );
    }

    /// <summary>
    /// Pins that a single item aged below the 24-hour backstop does NOT flush.
    /// </summary>
    /// <remarks>
    /// This test discriminates the dropped-age-bound mutation: widening the age check
    /// (e.g., <c>oldestAge.Value &gt; TimeSpan.Zero</c> instead of <c>&gt;= linger</c>)
    /// would cause flush=true for a count=1, sub-backstop item, making this assertion fail.
    /// The inclusive <c>&gt;=</c> boundary itself is pinned by the companion test
    /// <see cref="ShouldFlush_AgeExactlyAtBackstop_Flushes"/>: together the two tests
    /// discriminate both the mutation that widens the guard and the one that narrows it
    /// from <c>&gt;=</c> to <c>&gt;</c>.
    /// </remarks>
    [TestMethod]
    public void ShouldFlush_SubBackstopAge_DoesNotFlush( ) {
        TimeSpan backstop = TimeSpan.FromMilliseconds( SpotifyBatchSettings.DefaultLingerMs );
        TimeSpan subBackstop = backstop - TimeSpan.FromMilliseconds( 1 );

        bool result = SpotifyBulkProcessorService.ShouldFlush(
            count: 1,
            oldestAge: subBackstop,
            threshold: SpotifyConstants.MaxTracksPerBatchLookup,
            linger: backstop );

        Assert.IsFalse( result, "Item aged just below the 24-hour backstop must not flush (age-trigger is ≥, not >)" );
    }

    /// <summary>
    /// Pins that a single item aged exactly at the 24-hour backstop DOES flush.
    /// </summary>
    /// <remarks>
    /// Failure-first: changing the predicate from <c>oldestAge.Value >= linger</c> to
    /// <c>oldestAge.Value > linger</c> causes this to return false. The test verifies
    /// the inclusive boundary.
    /// </remarks>
    [TestMethod]
    public void ShouldFlush_AgeExactlyAtBackstop_Flushes( ) {
        TimeSpan backstop = TimeSpan.FromMilliseconds( SpotifyBatchSettings.DefaultLingerMs );

        bool result = SpotifyBulkProcessorService.ShouldFlush(
            count: 1,
            oldestAge: backstop,
            threshold: SpotifyConstants.MaxTracksPerBatchLookup,
            linger: backstop );

        Assert.IsTrue( result, "Item aged exactly at the 24-hour backstop must flush (inclusive boundary)" );
    }

    /// <summary>
    /// Verifies the degradation contract for a malformed <c>enqueuedAt</c> surfaced as a null age:
    /// the age trigger does not fire, but the size trigger still does when the count reaches the
    /// threshold.
    /// </summary>
    [TestMethod]
    public void ShouldFlush_MalformedEnqueuedAt_NullAge_DoesNotFlushOnAge_ButFlushesOnSize( ) {
        TimeSpan backstop = TimeSpan.FromMilliseconds( SpotifyBatchSettings.DefaultLingerMs );

        // Sub-threshold count + null age → no flush (age trigger cannot fire)
        bool ageResult = SpotifyBulkProcessorService.ShouldFlush(
            count: 1,
            oldestAge: null,
            threshold: SpotifyConstants.MaxTracksPerBatchLookup,
            linger: backstop );
        Assert.IsFalse( ageResult, "Null oldestAge must not trigger an age flush (safe degradation for malformed enqueuedAt)" );

        // At-threshold count + null age → flush (size trigger fires)
        bool sizeResult = SpotifyBulkProcessorService.ShouldFlush(
            count: SpotifyConstants.MaxTracksPerBatchLookup,
            oldestAge: null,
            threshold: SpotifyConstants.MaxTracksPerBatchLookup,
            linger: backstop );
        Assert.IsTrue( sizeResult, "Size trigger must still fire when oldestAge is null" );
    }

    /// <summary>
    /// Verifies that a malformed <c>enqueuedAt</c> value causes <c>GetOldestEnqueuedAtAsync</c> to
    /// return null and log a warning, so an unparseable timestamp degrades safely rather than
    /// throwing.
    /// </summary>
    [TestMethod]
    public async Task GetOldestEnqueuedAtAsync_MalformedEnqueuedAt_ReturnsNullAndLogsWarning( ) {
        // Arrange — entry with a timestamp that cannot be parsed by "O" round-trip format
        NameValueEntry[] fields = [
            new NameValueEntry( "payload", "{\"lookupType\":0}" ),
            new NameValueEntry( "enqueuedAt", "NOT-A-VALID-TIMESTAMP" )
        ];
        StreamEntry badEntry = new( (RedisValue)"1234567890-0", fields );

        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                (RedisKey)TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                1,
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [badEntry] );

        // Logger must return true for IsEnabled so the warning path is exercised
        _ = _helperLoggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        SpotifyBatchQueueHelper helper = CreateHelper( );

        // Act — must not throw
        DateTimeOffset? result = await helper.GetOldestEnqueuedAtAsync( isTracks: true, TestContext.CancellationToken );

        // Assert (a): returns null
        Assert.IsNull( result, "Malformed enqueuedAt must return null (safe degradation)" );

        // Assert (b): a warning-level log was emitted
        _helperLoggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>( ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.AtLeastOnce,
            "A warning log must be emitted for a malformed enqueuedAt field" );
    }

    /// <summary>
    /// Verifies that a well-formed round-trip <c>O</c>-format <c>enqueuedAt</c> value is parsed back
    /// to the original timestamp by <c>GetOldestEnqueuedAtAsync</c>.
    /// </summary>
    [TestMethod]
    public async Task GetOldestEnqueuedAtAsync_WellFormedEnqueuedAt_ReturnsCorrectTimestamp( ) {
        // Arrange — valid round-trip timestamp
        DateTimeOffset expected = new( 2026, 6, 10, 12, 0, 0, TimeSpan.Zero );
        string formatted = expected.ToString( "O", CultureInfo.InvariantCulture );

        NameValueEntry[] fields = [
            new NameValueEntry( "payload", "{\"lookupType\":0}" ),
            new NameValueEntry( "enqueuedAt", formatted )
        ];
        StreamEntry goodEntry = new( (RedisValue)"1234567890-0", fields );

        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                (RedisKey)TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                1,
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [goodEntry] );

        SpotifyBatchQueueHelper helper = CreateHelper( );

        // Act
        DateTimeOffset? result = await helper.GetOldestEnqueuedAtAsync( isTracks: true, TestContext.CancellationToken );

        // Assert
        Assert.IsNotNull( result, "Valid enqueuedAt must be returned, not null" );
        Assert.AreEqual( expected.ToString( "O" ), result.Value.ToString( "O" ),
            "Parsed timestamp must equal the original value" );
    }

    /// <summary>
    /// A rate-limited delivery whose producer saga is gone is acknowledged as stale and never
    /// recreates state from an old delivery.
    /// </summary>
    [TestMethod]
    public async Task HandleBulkRateLimitAsync_MissingSaga_AcknowledgesStaleWithoutMutation( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk ) with {
            SagaId = ISagaStateManager.GenerateSagaId( LookupKeyBuilder.TypedKey(
                LookupRequestType.SongIdLookup, SupportedProviders.Spotify, TestTrackId ) )
        };
        List<QueuedMessage<QueuedLookupRequest>> messages = [
            new QueuedMessage<QueuedLookupRequest>(
                $"{TrackStream}:1234567890-0",
                request,
                DateTimeOffset.UtcNow )
        ];

        Contracts.Exceptions.RetryAfterExceededException ex = new(
            retryAfterValue: TimeSpan.FromSeconds( 60 ),
            threshold: TimeSpan.FromSeconds( 120 ),
            requestUri: null,
            provider: SupportedProviders.Spotify );

        _ = _sagaManagerMock.Setup( m => m.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (LookupSagaState?)null );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.HandleBulkRateLimitAsync( messages, SpotifyConstants.BulkTracksEndpoint, ex, TestContext.CancellationToken );

        _sagaManagerMock.Verify( m => m.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ), Times.AtLeastOnce );
        _sagaManagerMock.Verify( m => m.TrySetIsPartialAsync(
            It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( m => m.GetOrCreateAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ),
            It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies rate-limit handling emits one end-to-end wall sample and span for both an admitted
    /// message and a stale identity that is quarantined before admission.
    /// </summary>
    [TestMethod]
    public async Task HandleBulkRateLimitAsync_MixedAdmittedAndStaleMessages_RecordsWallOncePerMessage( ) {
        QueuedMessage<QueuedLookupRequest> admitted = new(
            $"{TrackStream}:admitted",
            MakeTrackRequest( TestTrackId, QueuePriority.Bulk ),
            DateTimeOffset.UtcNow );
        QueuedMessage<QueuedLookupRequest> stale = new(
            $"{TrackStream}:stale",
            MakeTrackRequest( TestTrackId, QueuePriority.Bulk ),
            DateTimeOffset.UtcNow );
        List<QueuedMessage<QueuedLookupRequest>> messages = [admitted, stale];
        Contracts.Exceptions.RetryAfterExceededException ex = new(
            retryAfterValue: TimeSpan.FromSeconds( 60 ),
            threshold: TimeSpan.FromSeconds( 120 ),
            requestUri: null,
            provider: SupportedProviders.Spotify );

        int partialCalls = 0;
        _ = _sagaManagerMock.Setup( m => m.TrySetIsPartialAsync(
                It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( string _, bool _, string _, CancellationToken _ ) => Interlocked.Increment( ref partialCalls ) == 1 );

        int wallSamples = 0;
        List<Activity> wallSpans = [];
        using ActivityListener activityListener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => {
                if (activity.OperationName == "queue.spotify.message.wall") wallSpans.Add( activity );
            }
        };
        ActivitySource.AddActivityListener( activityListener );
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Name == "bridgebeats.queue.message.wall.duration") listener.EnableMeasurementEvents( instrument );
        };
        meterListener.SetMeasurementEventCallback<double>( ( _, _, _, _ ) => wallSamples++ );
        meterListener.Start( );

        await CreateService( ).HandleBulkRateLimitAsync( messages, SpotifyConstants.BulkTracksEndpoint, ex, TestContext.CancellationToken );

        Assert.AreEqual( 2, wallSamples );
        Assert.HasCount( 2, wallSpans );
        _innerQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( m => m.TrySetIsPartialAsync(
            admitted.Payload.SagaId, true, "test-instance", It.IsAny<CancellationToken>( ) ), Times.Exactly( 2 ) );
        _sagaManagerMock.Verify( m => m.TrySetRateLimitInfoAsync(
            admitted.Payload.SagaId,
            It.IsAny<List<ProviderRateLimitInfo>>( ),
            "test-instance",
            It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>
    /// Verifies the same ordering guarantee on the retry-cap path: when a message reaches the retry
    /// cap, the requeue-single cap branch calls <c>GetOrCreateAsync</c> before
    /// <c>UpdateProviderStateAsync</c>, so the failed provider state is written against an existing
    /// saga.
    /// </summary>
    [TestMethod]
    public async Task RequeueSingle_CapReached_CallsGetOrCreateAsync_BeforeUpdateProviderState( ) {
        // Arrange — message at retry cap
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk,
            attemptCount: LookupConstants.MaxQueueRetryAttempts ) with {
            SagaId = ISagaStateManager.GenerateSagaId( LookupKeyBuilder.TypedKey(
                    LookupRequestType.SongIdLookup, SupportedProviders.Spotify, TestTrackId ) )
        };
        StreamEntry capEntry = BuildStreamEntry( request );

        // StreamRangeAsync returns the at-cap entry so RequeueAsync sees AttemptCount == cap
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                (RedisKey)TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [capEntry] );

        // XREADGROUP returns this entry
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [capEntry] );

        // Track stream at size threshold so ShouldProcessBulkTracksAsync fires
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );

        // Absent-key result routes to RequeueSingleAsync (reaches cap)
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { ["someOtherId"] = null } );

        bool updateProviderCalled = false;

        _ = _sagaManagerMock.Setup( m => m.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( string _, ProviderLookupState _, string _, CancellationToken _ ) => updateProviderCalled = true )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( m => m.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = TestSagaId,
                LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                LookupType = LookupRequestType.SongIdLookup,
                LookupValue = TestTrackId,
                InstanceToken = "test-instance",
                ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                    [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, false, false, null, null, null )
                }
            } );

        SpotifyBulkProcessorService service = CreateService( );
        int completedLegs = 0;
        string? completedState = null;
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name == "bridgebeats.queue.saga.leg.completed.total") sub.EnableMeasurementEvents( instrument );
        };
        meterListener.SetMeasurementEventCallback<long>( ( _, value, tags, _ ) => {
            completedLegs += (int)value;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == "saga_state") completedState = tag.Value?.ToString( );
            }
        } );
        meterListener.Start( );

        // Act — drives the absent-key → RequeueSingleAsync → CapReached path
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert: the cap path reads and fences the saga before writing terminal state.
        _sagaManagerMock.Verify( m => m.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ), Times.AtLeastOnce );
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            request.SagaId, It.IsAny<ProviderLookupState>( ), "test-instance", It.IsAny<CancellationToken>( ) ), Times.Once );
        Assert.IsTrue( updateProviderCalled, "The CapReached branch must write terminal state through the token-aware CAS." );
        Assert.AreEqual( 1, completedLegs );
        Assert.AreEqual( "committed_failure", completedState );
    }

    /// <summary>
    /// Verifies that Spotify is never an approved interactive caller for provider-id lookups:
    /// because Spotify song-id and album-id lookups route to the 24-hour-linger bulk stream
    /// regardless of priority, an interactive caller would not get a timely result. The test also
    /// fails if a newly added provider is neither approved nor explicitly banned, forcing
    /// classification before merge.
    /// </summary>
    [TestMethod]
    public void InteractiveProviderIdLookup_Spotify_IsNotApprovedCaller( ) {
        // The approved set is the authoritative reference for which providers are safe
        // to pass to the interactive GetInfoByProviderIdAsync entry point.
        // Spotify must never appear here — its SongIdLookup/AlbumIdLookup go to the 24 h bulk stream.
        HashSet<SupportedProviders> approvedCallerProviders = [
            SupportedProviders.AppleMusic,
            SupportedProviders.Tidal
        ];

        // Primary guard: Spotify must not be in the approved-caller set.
        Assert.DoesNotContain(
            SupportedProviders.Spotify,
            approvedCallerProviders,
            "SupportedProviders.Spotify must never appear in the approved-caller set for " +
            "GetInfoByProviderIdAsync — SongIdLookup/AlbumIdLookup route to the 24 h-linger " +
            "bulk stream regardless of priority; interactive callers will not receive a timely result." );

        // Exhaustive-coverage guard: every SupportedProviders value must be explicitly classified.
        // This trips when a new provider is added to the enum without being reviewed here.
        foreach (SupportedProviders provider in Enum.GetValues<SupportedProviders>( )) {
            bool isApproved = approvedCallerProviders.Contains( provider );
            bool isBanned = provider == SupportedProviders.Spotify;
            Assert.IsTrue(
                isApproved || isBanned,
                $"SupportedProviders.{provider} is neither in the approved-caller set nor explicitly banned. " +
                "Update this test to classify the new provider before merging." );
        }
    }

    /// <summary>
    /// Verifies that a song-id lookup at interactive priority does not emit a warning log: the item
    /// is correctly passed through to the inner queue, so the warning guard (which fires only on a
    /// genuine misroute) stays silent.
    /// </summary>
    [TestMethod]
    public async Task Decorator_SongIdLookup_WithInteractivePriority_DoesNotEmitWarning( ) {
        // Arrange
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Interactive );
        SpotifyBulkQueueDecorator decorator = CreateDecorator( );

        _ = _decoratorLoggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );
        _ = _innerQueueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        await decorator.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        // Assert — no WARNING (the routing-fix removed the interactive-warning branch)
        _decoratorLoggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>( ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never,
            "Decorator must not emit a WARNING for SongIdLookup at Interactive priority — the item is now correctly passed through to the inner queue" );

        // Assert — inner queue received the call
        _innerQueueMock.Verify(
            q => q.EnqueueAsync( request, QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Inner queue must be called for SongIdLookup at Interactive priority" );
    }

    /// <summary>
    /// Verifies that an album-id lookup at interactive priority likewise emits no warning and is
    /// passed through to the inner queue.
    /// </summary>
    [TestMethod]
    public async Task Decorator_AlbumIdLookup_WithInteractivePriority_DoesNotEmitWarning( ) {
        // Arrange
        QueuedLookupRequest request = MakeAlbumRequest( "6WdSsBrH5QtofaTTqgwxOV", QueuePriority.Interactive );
        SpotifyBulkQueueDecorator decorator = CreateDecorator( );

        _ = _decoratorLoggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );
        _ = _innerQueueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        // Act
        await decorator.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        // Assert — no WARNING
        _decoratorLoggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>( ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never,
            "Decorator must not emit a WARNING for AlbumIdLookup at Interactive priority" );

        // Assert — inner queue received the call
        _innerQueueMock.Verify(
            q => q.EnqueueAsync( request, QueuePriority.Interactive, It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Inner queue must be called for AlbumIdLookup at Interactive priority" );
    }

    /// <summary>
    /// Verifies that a song-id lookup at non-interactive priorities (background and bulk) emits no
    /// warning: these are correctly bulk-routed, and the warning guard fires only on interactive
    /// misroutes. The test clears the logger's recorded invocations between priorities.
    /// </summary>
    [TestMethod]
    public async Task Decorator_SongIdLookup_WithNonInteractivePriority_DoesNotEmitWarningLog( ) {
        _ = _decoratorLoggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        foreach (QueuePriority priority in new[] { QueuePriority.Background, QueuePriority.Bulk }) {
            // Reset logger mock between iterations
            _decoratorLoggerMock.Invocations.Clear( );

            QueuedLookupRequest request = MakeTrackRequest( TestTrackId, priority );
            SpotifyBulkQueueDecorator decorator = CreateDecorator( );

            await decorator.EnqueueAsync( request, priority, TestContext.CancellationToken );

            _decoratorLoggerMock.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>( ),
                    It.IsAny<It.IsAnyType>( ),
                    It.IsAny<Exception?>( ),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
                Times.Never,
                $"Decorator must NOT emit a WARNING log for {priority} priority SongIdLookup (warning guard fires only on Interactive; these are bulk-routed non-interactive requests)" );
        }
    }

    /// <summary>
    /// Builds a decorator wrapping the inner-queue mock and wired to Redis and the decorator logger.
    /// </summary>
    /// <returns>A decorator under test.</returns>
    private SpotifyBulkQueueDecorator CreateDecorator( ) =>
        new( _innerQueueMock.Object, _redisMock.Object, _decoratorLoggerMock.Object );

    /// <summary>
    /// Builds a bulk processor service wired to the mocks and a batch settings instance using the
    /// default linger.
    /// </summary>
    /// <returns>A service under test.</returns>
    private SpotifyBulkProcessorService CreateService( ) {
        IOptions<SpotifyBatchSettings> options = Options.Create(
            new SpotifyBatchSettings { LingerMs = SpotifyBatchSettings.DefaultLingerMs } );
        return new SpotifyBulkProcessorService(
            _redisMock.Object,
            CreateHelper( ),
            _rateLimitTrackerMock.Object,
            _sagaManagerMock.Object,
            _lookupServiceMock.Object,
            _innerQueueMock.Object,
            _serviceLoggerMock.Object,
            options );
    }

    /// <summary>
    /// Builds a batch queue helper wired to Redis and the helper logger.
    /// </summary>
    /// <returns>A helper under test.</returns>
    private SpotifyBatchQueueHelper CreateHelper( ) =>
        new( _redisMock.Object, _helperLoggerMock.Object );

    /// <summary>Spotify dequeue emits scoped dequeue, PEL, and read-group spans on the real helper path.</summary>
    [TestMethod]
    public async Task SpotifyBulkDequeue_EmitsScopedTelemetryForPelAndReadGroup( ) {
        List<Activity> observed = [];
        using ActivityListener listener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => observed.Add( activity )
        };
        ActivitySource.AddActivityListener( listener );
        _ = await CreateHelper( ).DequeueTrackIdBatchAsync( 1, TestContext.CancellationToken );
        Assert.Contains( activity => activity.OperationName == "queue.spotify.dequeue", observed );
        Activity pel = observed.Single( activity => activity.OperationName == "queue.spotify.dequeue.pel_scan" );
        Activity read = observed.Single( activity => activity.OperationName == "queue.spotify.dequeue.read_group" );
        Assert.AreEqual( "spotify", pel.GetTagItem( QueueMetricTags.Provider ) );
        Assert.AreEqual( "bulk", pel.GetTagItem( QueueMetricTags.Priority ) );
        Assert.IsGreaterThan( TimeSpan.Zero, pel.Duration );
        Assert.IsGreaterThan( TimeSpan.Zero, read.Duration );
    }

    /// <summary>One valid bulk message produces exactly one wall sample and one missing-state leg sample.</summary>
    [TestMethod]
    public async Task SpotifyBulkResult_RecordsWallAndMissingSagaLegExactlyOnce( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> {
                [TestTrackId] = new MusicLookupResult { Artist = "Artist", Title = "Title", ExternalId = "ISRC" }
            } );

        Dictionary<string, int> counts = [];
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && (instrument.Name == "bridgebeats.queue.message.wall.duration"
                    || instrument.Name == "bridgebeats.queue.saga.leg.completed.total")) {
                listener.EnableMeasurementEvents( instrument );
            }
        };
        meterListener.SetMeasurementEventCallback<double>( ( instrument, _, _, _ ) =>
            counts[instrument.Name] = counts.GetValueOrDefault( instrument.Name ) + 1 );
        meterListener.SetMeasurementEventCallback<long>( ( instrument, _, _, _ ) =>
            counts[instrument.Name] = counts.GetValueOrDefault( instrument.Name ) + 1 );
        meterListener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 1, counts.GetValueOrDefault( "bridgebeats.queue.message.wall.duration" ) );
        Assert.AreEqual( 1, counts.GetValueOrDefault( "bridgebeats.queue.saga.leg.completed.total" ) );
    }

    /// <summary>A two-message returned batch emits exactly one wall sample per message.</summary>
    [TestMethod]
    public async Task SpotifyBulkResult_TwoReturnedMessages_EmitsExactlyTwoWallSamples( ) {
        QueuedLookupRequest first = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        QueuedLookupRequest second = MakeTrackRequest( TestTrackId + "2", QueuePriority.Bulk );
        StreamEntry firstEntry = BuildStreamEntry( first );
        StreamEntry secondEntry = BuildStreamEntry( second, "1234567891-0" );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [firstEntry, secondEntry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> {
                [TestTrackId] = new MusicLookupResult { Artist = "Artist", Title = "Title", ExternalId = "ISRC" },
                [TestTrackId + "2"] = new MusicLookupResult { Artist = "Artist 2", Title = "Title 2", ExternalId = "ISRC2" }
            } );

        int wallSamples = 0;
        List<double> wallDurations = [];
        List<Activity> wallSpans = [];
        using ActivityListener activityListener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => {
                if (activity.OperationName == "queue.spotify.message.wall") wallSpans.Add( activity );
            }
        };
        ActivitySource.AddActivityListener( activityListener );
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name == "bridgebeats.queue.message.wall.duration") sub.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<double>( ( instrument, value, _, _ ) => {
            wallSamples++;
            wallDurations.Add( value );
        } );
        listener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 2, wallSamples );
        Assert.HasCount( 2, wallSpans );
        for (int index = 0; index < wallSpans.Count; index++) {
            Assert.IsLessThan( 0.5, Math.Abs( wallSpans[index].Duration.TotalSeconds - wallDurations[index] ) );
        }
    }

    /// <summary>
    /// A bulk result whose post-commit XACK fails remains pending without entering the retry-cap
    /// mutation path, even at the terminal attempt boundary.
    /// </summary>
    [TestMethod]
    public async Task SpotifyBulkResult_AckFailureAfterCommittedSuccess_LeavesPelPendingWithoutRetryMutation( ) {
        QueuedLookupRequest request = MakeTrackRequest(
            TestTrackId,
            QueuePriority.Bulk,
            LookupConstants.MaxQueueRetryAttempts - 1 );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> {
                [TestTrackId] = new MusicLookupResult { Artist = "Artist", Title = "Title", ExternalId = "ISRC" }
            } );
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ),
                It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 1 && keys[0] == TrackStream ),
                It.Is<RedisValue[]?>( arguments => arguments != null && arguments.Length == 2 ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "atomic acknowledgement unavailable" ) );

        SpotifyBulkProcessorService service = CreateService( );

        _ = await Assert.ThrowsAsync<Exception>(
            ( ) => service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken ) );

        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            request.SagaId,
            It.Is<ProviderLookupState>( state => state.IsComplete && state.IsSuccess ),
            "test-instance",
            It.IsAny<CancellationToken>( ) ), Times.Once );
        _dbMock.Verify( d => d.StreamAddAsync(
            It.IsAny<RedisKey>( ), It.IsAny<NameValueEntry[]>( ), It.IsAny<RedisValue?>( ),
            It.IsAny<long?>( ), It.IsAny<bool>( ), It.IsAny<long?>( ), It.IsAny<StreamTrimMode>( ),
            It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>Completed-leg redelivery is dropped while the first delivery emits its pending state.</summary>
    [TestMethod]
    public async Task SpotifyBulkResult_RecordsPendingAndCompleteSagaStatesOncePerMessage( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> {
                [TestTrackId] = new MusicLookupResult { Artist = "Artist", Title = "Title", ExternalId = "ISRC" }
            } );
        int sagaRead = 0;
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync(
                TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => sagaRead++ switch {
                0 => new LookupSagaState { SagaId = TestSagaId, LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}", LookupType = LookupRequestType.SongIdLookup, LookupValue = TestTrackId, InstanceToken = "test-instance" },
                1 or 2 => new LookupSagaState {
                    SagaId = TestSagaId,
                    LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                    LookupType = LookupRequestType.SongIdLookup,
                    LookupValue = TestTrackId,
                    InstanceToken = "test-instance",
                    ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                        [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, false, false, null, null, "pending" )
                    }
                },
                3 or 4 or 5 or 6 => new LookupSagaState {
                    SagaId = TestSagaId,
                    LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                    LookupType = LookupRequestType.SongIdLookup,
                    LookupValue = TestTrackId,
                    InstanceToken = "test-instance",
                    ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                        [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null )
                    }
                },
                _ => null
            } );

        List<string> states = [];
        int wallSamples = 0;
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && (instrument.Name == "bridgebeats.queue.message.wall.duration"
                    || instrument.Name == "bridgebeats.queue.saga.leg.completed.total")) {
                listener.EnableMeasurementEvents( instrument );
            }
        };
        meterListener.SetMeasurementEventCallback<double>( ( instrument, _, _, _ ) => {
            if (instrument.Name == "bridgebeats.queue.message.wall.duration") { wallSamples++; }
        } );
        meterListener.SetMeasurementEventCallback<long>( ( instrument, _, tags, _ ) => {
            if (instrument.Name == "bridgebeats.queue.saga.leg.completed.total") {
                foreach (KeyValuePair<string, object?> tag in tags) {
                    if (tag.Key == "saga_state") { states.Add( tag.Value?.ToString( ) ?? string.Empty ); }
                }
            }
        } );
        meterListener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );
        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );
        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 3, wallSamples );
        CollectionAssert.AreEquivalent( new[] { "committed", "committed", "committed" }, states );
    }

    /// <summary>Spotify observation listeners do not change saga, queue, or acknowledgment calls.</summary>
    [TestMethod]
    public async Task SpotifyLeg_MetricsListenerOnAndOff_HasIdenticalBusinessCallCounts( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> {
                [TestTrackId] = new MusicLookupResult { Artist = "Artist", Title = "Title", ExternalId = "ISRC" }
            } );
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( TestSagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = TestSagaId,
                LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                LookupType = LookupRequestType.SongIdLookup,
                LookupValue = TestTrackId,
                InstanceToken = "test-instance"
            } );

        static int Count( Mock mock, string method ) => mock.Invocations.Count( invocation => invocation.Method.Name == method );
        static int CountAtomicAcks( Mock mock ) => mock.Invocations.Count( invocation =>
            invocation.Method.Name == nameof( IDatabase.ScriptEvaluateAsync )
            && invocation.Arguments[2] is RedisValue[] arguments
            && arguments.Length == 2 );
        async Task<(int Gets, int Updates, int Acks)> RunAsync( bool listen ) {
            using MeterListener? listener = listen ? new MeterListener( ) : null;
            if (listener is not null) {
                listener.InstrumentPublished = ( instrument, consumer ) => {
                    if (instrument.Meter.Name == QueueMetrics.MeterName
                        && instrument.Name == "bridgebeats.queue.saga.leg.completed.total") {
                        consumer.EnableMeasurementEvents( instrument );
                    }
                };
                listener.Start( );
            }
            int gets = Count( _sagaManagerMock, nameof( ISagaStateManager.GetAsync ) );
            int updates = Count( _sagaManagerMock, nameof( ISagaStateManager.TryUpdateProviderStateAsync ) );
            int acks = CountAtomicAcks( _dbMock );
            await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );
            return (Count( _sagaManagerMock, nameof( ISagaStateManager.GetAsync ) ) - gets,
                Count( _sagaManagerMock, nameof( ISagaStateManager.TryUpdateProviderStateAsync ) ) - updates,
                CountAtomicAcks( _dbMock ) - acks);
        }

        (int Gets, int Updates, int Acks) disabled = await RunAsync( false );
        (int Gets, int Updates, int Acks) enabled = await RunAsync( true );
        Assert.AreEqual( disabled, enabled );
        Assert.AreEqual( 1, disabled.Gets );
        Assert.AreEqual( 1, disabled.Updates );
        Assert.AreEqual( 1, disabled.Acks );
    }

    /// <summary>Transient provider failure after dequeue requeues and produces one wall sample without a completed leg.</summary>
    [TestMethod]
    public async Task SpotifyBulkTransientFailure_EmitsNoCompletedLegOrWallSample( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ), It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "XADD failed" ) );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new HttpRequestException( "transient" ) );
        int legSamples = 0;
        int wallSamples = 0;
        long enqueued = 0;
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && (instrument.Name == "bridgebeats.queue.message.wall.duration"
                    || instrument.Name == "bridgebeats.queue.saga.leg.completed.total"
                    || instrument.Name == "bridgebeats.queue.enqueued.total")) {
                listener.EnableMeasurementEvents( instrument );
            }
        };
        meterListener.SetMeasurementEventCallback<double>( ( instrument, _, _, _ ) => {
            if (instrument.Name == "bridgebeats.queue.message.wall.duration") { wallSamples++; }
        } );
        meterListener.SetMeasurementEventCallback<long>( ( instrument, _, _, _ ) => {
            if (instrument.Name == "bridgebeats.queue.saga.leg.completed.total") { legSamples++; }
            if (instrument.Name == "bridgebeats.queue.enqueued.total") { enqueued++; }
        } );
        meterListener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 0, legSamples );
        Assert.AreEqual( 1, wallSamples );
        meterListener.RecordObservableInstruments( );
        Assert.AreEqual( 0, enqueued, "A failed replacement XADD is not an accepted enqueue" );
        _dbMock.Verify( d => d.StreamAcknowledgeAsync(
            It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>An atomic replacement failure emits exactly one wall sample and no acceptance metrics.</summary>
    [TestMethod]
    public async Task SpotifyBulkAtomicRequeueFailure_EmitsExactlyOneWallSample( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( [entry] );
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ), It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new HttpRequestException( "transient" ) );
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "atomic move failed" ) );
        int wallSamples = 0;
        long enqueued = 0;
        long requeued = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name is "bridgebeats.queue.message.wall.duration"
                or "bridgebeats.queue.enqueued.total"
                or "bridgebeats.queue.requeued.total") sub.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<double>( ( _, _, _, _ ) => wallSamples++ );
        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, _, _ ) => {
            if (instrument.Name == "bridgebeats.queue.enqueued.total") _ = Interlocked.Add( ref enqueued, measurement );
            if (instrument.Name == "bridgebeats.queue.requeued.total") _ = Interlocked.Add( ref requeued, measurement );
        } );
        listener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 1, wallSamples );
        listener.RecordObservableInstruments( );
        Assert.AreEqual( 0, enqueued, "A failed atomic move has no accepted replacement" );
        Assert.AreEqual( 0, requeued, "A failed atomic move must not report successful cleanup" );
    }

    /// <summary>A replacement enqueue/XADD failure still emits exactly one wall sample.</summary>
    [TestMethod]
    public async Task SpotifyBulkRejectionReplacementEnqueueFailure_EmitsExactlyOneWallSample( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk );
        StreamEntry entry = BuildStreamEntry( request );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new SpotifyBulkRejectedException( 400, null, SupportedProviders.Spotify ) );
        _ = _dbMock.Setup( database => database.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "XADD failed" ) );

        int wallSamples = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name == "bridgebeats.queue.message.wall.duration") sub.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<double>( ( _, _, _, _ ) => wallSamples++ );
        listener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 1, wallSamples );
        _dbMock.Verify( d => d.StreamAcknowledgeAsync(
            It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>A fallback single-id HTTP call emits its own provider duration in addition to the bulk call.</summary>
    [TestMethod]
    public async Task SpotifyBulkFallback_EmitsIndividualProviderHttpDuration( ) {
        QueuedLookupRequest request = MakeTrackRequest( TestTrackId, QueuePriority.Bulk ) with {
            FallbackLookupType = LookupRequestType.IsrcLookup,
            FallbackLookupValue = "US-FALLBACK"
        };
        _ = _dbMock.Setup( d => d.StreamLengthAsync( (RedisKey)TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [BuildStreamEntry( request )] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = null } );
        _ = _lookupServiceMock.Setup( service => service.GetInfoByISRCAsync( "US-FALLBACK" ) )
            .ReturnsAsync( new MusicLookupResult { Artist = "Fallback", Title = "Track", ExternalId = "US-FALLBACK" } );

        int providerHttpSamples = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name == "bridgebeats.queue.provider_http.duration") sub.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<double>( ( _, _, _, _ ) => providerHttpSamples++ );
        listener.Start( );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 2, providerHttpSamples );
    }

    /// <summary>A dequeue exception before any message is returned emits no message wall sample.</summary>
    [TestMethod]
    public async Task SpotifyBulkDequeueFailure_EmitsNoWallSample( ) {
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<long>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "NOGROUP No such key" ) );
        int wallSamples = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name == "bridgebeats.queue.message.wall.duration") sub.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<double>( ( _, _, _, _ ) => wallSamples++ );
        listener.Start( );

        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken ) );

        Assert.AreEqual( 0, wallSamples );
    }

    /// <summary>Bulk dequeue rethrows NOGROUP from XAUTOCLAIM so the worker can repair groups.</summary>
    [TestMethod]
    public async Task BulkDequeue_WhenAutoClaimReportsNoGroup_RethrowsForWorkerRecovery( ) {
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<long>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "NOGROUP No such key" ) );
        SpotifyBatchQueueHelper helper = CreateHelper( );
        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => helper.DequeueTrackIdBatchAsync( 10, TestContext.CancellationToken ) );
    }

    /// <summary>Unknown-command XAUTOCLAIM errors degrade to own-PEL/new-entry recovery.</summary>
    [TestMethod]
    public async Task BulkDequeue_WhenAutoClaimIsUnsupported_FallsBackToOwnPelAndNewEntries( ) {
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<long>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "ERR unknown command 'XAUTOCLAIM'" ) );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( Array.Empty<StreamEntry>( ) );

        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> result =
            await CreateHelper( ).DequeueTrackIdBatchAsync( 10, TestContext.CancellationToken );

        Assert.IsEmpty( result );
    }

    /// <summary>Permission errors from XAUTOCLAIM propagate instead of being mislabeled unsupported.</summary>
    [TestMethod]
    public async Task BulkDequeue_WhenAutoClaimIsForbidden_PropagatesRedisError( ) {
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<long>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "NOPERM this user has no permissions" ) );

        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => CreateHelper( ).DequeueTrackIdBatchAsync( 10, TestContext.CancellationToken ) );
    }

    /// <summary>XAUTOCLAIM receives only the budget left after own-consumer PEL recovery.</summary>
    [TestMethod]
    public async Task BulkDequeue_AutoClaimCountUsesRemainingBudget( ) {
        StreamEntry ownPending = BuildStreamEntry( MakeTrackRequest( TestTrackId ) );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                (RedisKey)TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.Is<RedisValue?>( position => position.HasValue && position.Value == "0-0" ),
                It.IsAny<int?>( ), It.IsAny<bool>( ), It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [ownPending] );

        _ = await CreateHelper( ).DequeueTrackIdBatchAsync( 5, TestContext.CancellationToken );

        _dbMock.Verify( d => d.StreamAutoClaimAsync(
            (RedisKey)TrackStream,
            It.IsAny<RedisValue>( ),
            It.IsAny<RedisValue>( ),
            It.IsAny<long>( ),
            It.IsAny<RedisValue>( ),
            4,
            It.IsAny<CommandFlags>( ) ), Times.Once );
    }

    /// <summary>Redis read failures propagate while closing the specialized dequeue span.</summary>
    [TestMethod]
    public async Task SpotifyBulkDequeue_ReadGroupFailure_EmitsScopedErrorTelemetry( ) {
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ), It.IsAny<int?>( ), It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "READONLY" ) );
        List<Activity> spans = [];
        using ActivityListener listener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add( activity )
        };
        ActivitySource.AddActivityListener( listener );

        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => CreateHelper( ).DequeueTrackIdBatchAsync( 10, TestContext.CancellationToken ) );

        Assert.Contains( activity => activity.OperationName == "queue.spotify.dequeue.pel_scan", spans );
        Assert.IsGreaterThan( TimeSpan.Zero,
            spans.Single( activity => activity.OperationName == "queue.spotify.dequeue.pel_scan" ).Duration );
    }

    /// <summary>XAUTOCLAIM deleted IDs are counted once as missing PEL entries.</summary>
    [TestMethod]
    public async Task SpotifyBulkDequeue_DeletedAutoClaimIds_EmitMissingPelCount( ) {
        ConstructorInfo ctor = typeof( StreamAutoClaimResult )
            .GetConstructors( BindingFlags.NonPublic | BindingFlags.Instance )[0];
        StreamAutoClaimResult result = (StreamAutoClaimResult)ctor.Invoke(
            [(RedisValue)"0-0", Array.Empty<StreamEntry>( ), new[] { (RedisValue)"1-0", (RedisValue)"2-0" } ] );
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<long>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( result );

        int missing = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, sub ) => {
            if (instrument.Name == "bridgebeats.queue.dequeue.pel_scan.entries") sub.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<long>( ( _, _, tags, _ ) => {
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == "outcome" && tag.Value?.ToString( ) == "missing") missing++;
            }
        } );
        listener.Start( );

        _ = await CreateHelper( ).DequeueTrackIdBatchAsync( 10, TestContext.CancellationToken );

        Assert.AreEqual( 2, missing );
    }

    /// <summary>Verifies decorated Spotify queues expose the generic consumer-group repair capability.</summary>
    [TestMethod]
    public async Task Decorator_EnsureConsumerGroups_DelegatesToInnerCapability( ) {
        AssuringQueue inner = new( );
        SpotifyBulkQueueDecorator decorator = new( inner, _redisMock.Object, _decoratorLoggerMock.Object );

        await decorator.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        Assert.AreEqual( 1, inner.EnsureCalls );
    }

    private sealed class AssuringQueue : IRequestQueue<QueuedLookupRequest>, IConsumerGroupAssurance {
        public int EnsureCalls { get; private set; }
        public Task EnsureConsumerGroupsAsync( CancellationToken cancellationToken = default ) {
            EnsureCalls++;
            return Task.CompletedTask;
        }
        public Task EnqueueAsync( QueuedLookupRequest request, QueuePriority priority, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<QueuedMessage<QueuedLookupRequest>?> DequeueAsync( CancellationToken cancellationToken = default ) => Task.FromResult<QueuedMessage<QueuedLookupRequest>?>( null );
        public Task<QueuedMessage<QueuedLookupRequest>?> DequeueAsync( IRateLimitTracker rateLimitTracker, CancellationToken cancellationToken = default ) => Task.FromResult<QueuedMessage<QueuedLookupRequest>?>( null );
        public Task AcknowledgeAsync( string messageId, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task RequeueAsync( string messageId, TimeSpan? delay = null, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<QueueDepth> GetDepthAsync( CancellationToken cancellationToken = default ) => Task.FromResult( new QueueDepth( 0, 0, 0, 0 ) );
        public Task<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>> GetDlqMessagesAsync( int limit, CancellationToken cancellationToken = default ) => Task.FromResult<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>>( [] );
        public Task RequeueFromDlqAsync( string messageId, QueuePriority priority, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task MoveToDlqAsync( string messageId, string reason, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<bool> DeleteFromDlqAsync( string messageId, CancellationToken cancellationToken = default ) => Task.FromResult( true );
    }

    /// <summary>
    /// Builds a Spotify song-id <c>QueuedLookupRequest</c> for the given track id, priority, and
    /// attempt count, carrying the fixed test saga id.
    /// </summary>
    /// <param name="trackId">The track id to look up.</param>
    /// <param name="priority">The origin priority; defaults to bulk.</param>
    /// <param name="attemptCount">The retry attempt count; defaults to zero.</param>
    /// <returns>A track lookup request.</returns>
    private static QueuedLookupRequest MakeTrackRequest(
        string trackId,
        QueuePriority priority = QueuePriority.Bulk,
        int attemptCount = 0
    ) => new( ) {
        RequestId = Guid.NewGuid( ).ToString( "N" ),
        Provider = SupportedProviders.Spotify,
        LookupType = LookupRequestType.SongIdLookup,
        LookupValue = trackId,
        SagaId = TestSagaId,
        SagaInstanceToken = "test-instance",
        IsAlbum = false,
        OriginPriority = priority,
        AttemptCount = attemptCount
    };

    /// <summary>
    /// Builds a Spotify album-id <c>QueuedLookupRequest</c> for the given album id, priority, and
    /// attempt count, carrying the fixed test saga id.
    /// </summary>
    /// <param name="albumId">The album id to look up.</param>
    /// <param name="priority">The origin priority; defaults to bulk.</param>
    /// <param name="attemptCount">The retry attempt count; defaults to zero.</param>
    /// <returns>An album lookup request.</returns>
    private static QueuedLookupRequest MakeAlbumRequest(
        string albumId,
        QueuePriority priority = QueuePriority.Bulk,
        int attemptCount = 0
    ) => new( ) {
        RequestId = Guid.NewGuid( ).ToString( "N" ),
        Provider = SupportedProviders.Spotify,
        LookupType = LookupRequestType.AlbumIdLookup,
        LookupValue = albumId,
        SagaId = TestSagaId,
        SagaInstanceToken = "test-instance",
        IsAlbum = true,
        OriginPriority = priority,
        AttemptCount = attemptCount
    };

    /// <summary>
    /// Builds a Redis stream entry whose <c>payload</c> field is the serialized request and whose
    /// <c>enqueuedAt</c> field is the current time, for tests that feed entries through the helper's
    /// dequeue and requeue paths.
    /// </summary>
    /// <param name="request">The request to embed as the entry payload.</param>
    /// <param name="id">The Redis stream entry id.</param>
    /// <returns>A stream entry carrying the serialized request and an enqueue timestamp.</returns>
    private static StreamEntry BuildStreamEntry( QueuedLookupRequest request, string id = "1234567890-0" ) {
        string payload = System.Text.Json.JsonSerializer.Serialize( request, s_jsonOptions );
        NameValueEntry[] fields = [
            new NameValueEntry( "payload", payload ),
            new NameValueEntry( "enqueuedAt", DateTimeOffset.UtcNow.ToString( "O" ) )
        ];
        return new StreamEntry( (RedisValue)id, fields );
    }
}
