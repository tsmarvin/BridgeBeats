using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
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
/// Integration tests for <see cref="RedisRequestQueue{T}"/> against a real Redis instance (the shared
/// Testcontainers Redis), using Redis streams and consumer groups. Verifies enqueue/dequeue across the
/// interactive, background, and bulk priority streams; acknowledgement; the dead-letter queue (move,
/// requeue, delete); per-provider isolation; consumer-group fan-out across worker instances; retry
/// requeue; consumer-id formatting; and the weighted scheduling policy (interactive preemption, bounded
/// background starvation via aging, two-phase bulk gating, and rate-limit-aware selection). Requires
/// Docker to be running on the host machine.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public partial class RedisRequestQueueTests {

    /// <summary>Generic dequeue emits one scoped span and measurement for each Redis boundary.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_TelemetryHasExactOuterReadDepthAndPelBoundaries( ) {
        List<string> spans = [];
        Dictionary<string, int> measurements = [];
        using ActivityListener activityListener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add( activity.OperationName )
        };
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && (instrument.Name.Contains( "dequeue", StringComparison.Ordinal ))) {
                listener.EnableMeasurementEvents( instrument );
            }
        };
        meterListener.SetMeasurementEventCallback<double>( ( instrument, _, _, _ ) =>
            measurements[instrument.Name] = measurements.GetValueOrDefault( instrument.Name ) + 1 );
        ActivitySource.AddActivityListener( activityListener );
        meterListener.Start( );

        await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        _ = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );

        _ = Assert.ContainsSingle( spans.Where( name => name == "queue.dequeue" ) );
        Assert.Contains( "queue.dequeue.depth_probe", spans );
        Assert.Contains( "queue.dequeue.pel_scan", spans );
        Assert.Contains( "queue.dequeue.read_group", spans );
        Assert.AreEqual( 1, measurements.GetValueOrDefault( "bridgebeats.queue.dequeue.depth_probe.duration" ) );
        Assert.IsGreaterThan( 0, measurements.GetValueOrDefault( "bridgebeats.queue.dequeue.pel_scan.duration" ) );
        Assert.IsGreaterThan( 0, measurements.GetValueOrDefault( "bridgebeats.queue.dequeue.read_group.duration" ) );
    }

    /// <summary>Both public dequeue overloads close their Redis-boundary telemetry on NOGROUP errors.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_NogroupException_ClosesPlainAndRateAwareTelemetry( ) {
        List<string> spans = [];
        using ActivityListener activityListener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add( activity.OperationName )
        };
        ActivitySource.AddActivityListener( activityListener );

        IDatabase db = s_redis!.GetDatabase( );
        string stream = $"queue:{s_runToken}:spotify:interactive";
        _ = await db.ExecuteAsync( "XGROUP", "DESTROY", stream, "spotify-workers" );
        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => _queue.DequeueAsync( TestContext.CancellationToken ) );
        await _queue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );
        _ = await db.ExecuteAsync( "XGROUP", "DESTROY", stream, "spotify-workers" );
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );
        await _queue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        Assert.IsGreaterThanOrEqualTo( 2, spans.Count( name => name == "queue.dequeue" ) );
        Assert.Contains( "queue.dequeue.depth_probe", spans );
        Assert.Contains( "queue.dequeue.read_group", spans );
        Assert.Contains( "queue.dequeue.pel_scan", spans );
    }

    /// <summary>Deletes a claimed stream body and verifies the dequeue scan removes its ghost PEL entry.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_GhostPelEntry_IsRemovedOnScan( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        IDatabase db = s_redis!.GetDatabase();
        List<string> ghostIds = [];
        for (int i = 0; i < 50; i++) {
            await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
            QueuedMessage<QueuedLookupRequest>? claimed = await _queue.DequeueAsync(TestContext.CancellationToken);
            Assert.IsNotNull( claimed );
            ghostIds.Add( claimed.MessageId[(claimed.MessageId.LastIndexOf( ':' ) + 1)..] );
        }
        _ = await db.StreamDeleteAsync( stream, [.. ghostIds.Select( id => (RedisValue)id )] );
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new();
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [] );
        _ = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        RedisResult pendingSummary = await db.ExecuteAsync("XPENDING", stream, "spotify-workers");
        RedisResult[] pendingParts = (RedisResult[])pendingSummary!;
        Assert.AreEqual( 0, (long)pendingParts[0] );
        Assert.IsEmpty( await db.StreamRangeAsync( stream, ghostIds.First( ), ghostIds.Last( ) ) );
        Assert.IsNull( await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );
    }

    /// <summary>Own-PEL pagination eventually reaches entries beyond the per-call scan cap.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_OwnPelPagination_ReachesEntriesBeyondFifty( ) {
        const int Count = 505;
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint( "IsrcLookup", DateTimeOffset.UtcNow.AddMinutes( 5 ) )] );
        for (int i = 0; i < Count; i++) {
            await _queue.EnqueueAsync( CreateTestRequest( ) with { RateLimitedEndpoint = "IsrcLookup" }, QueuePriority.Interactive, TestContext.CancellationToken );
        }
        QueuedLookupRequest tailRequest = CreateTestRequest( lookupType: LookupRequestType.SongIdLookup ) with {
            RateLimitedEndpoint = ProviderEndpointConstants.AuthToken
        };
        await _queue.EnqueueAsync( tailRequest, QueuePriority.Interactive, TestContext.CancellationToken );

        // Seed the current consumer's PEL without using the rate-aware path: the first 505 entries
        // are deliberately blocked, so the rate-aware scan must advance past them to the tail.
        for (int i = 0; i < Count; i++) {
            QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
            Assert.IsNotNull( message );
            _queue.ReleaseDelivery( message.MessageId );
        }
        QueuedMessage<QueuedLookupRequest>? seededTail = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( seededTail );
        Assert.AreEqual( tailRequest.RequestId, seededTail.Payload.RequestId );
        _queue.ReleaseDelivery( seededTail.MessageId );

        QueuedMessage<QueuedLookupRequest>? tail = null;
        for (int i = 0; i < 20 && tail is null; i++) {
            tail = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        }
        Assert.IsNotNull( tail );
        Assert.AreEqual( tailRequest.RequestId, tail.Payload.RequestId );
    }

    /// <summary>XAUTOCLAIM cursor reaches an aged eligible tail beyond a young dead-consumer prefix.</summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task DequeueAsync_DeadConsumerPelPagination_ReachesAgedTailBeyondFiveHundred( ) {
        const int Count = 505;
        string stream = $"queue:{s_runToken}:spotify:interactive";
        IDatabase db = s_redis!.GetDatabase( );
        for (int i = 0; i < Count; i++) {
            await _queue.EnqueueAsync( CreateTestRequest( ) with { RateLimitedEndpoint = "IsrcLookup" }, QueuePriority.Interactive, TestContext.CancellationToken );
        }
        QueuedLookupRequest tailRequest = CreateTestRequest( lookupType: LookupRequestType.SongIdLookup ) with {
            RateLimitedEndpoint = ProviderEndpointConstants.AuthToken
        };
        await _queue.EnqueueAsync( tailRequest, QueuePriority.Interactive, TestContext.CancellationToken );
        StreamEntry[] claimed = await db.StreamReadGroupAsync( stream, "spotify-workers", "dead-consumer", StreamPosition.NewMessages, Count + 1, noAck: false );
        Assert.HasCount( Count + 1, claimed );
        RedisValue[] ids = [.. claimed.Select( entry => entry.Id )];
        _ = await db.ExecuteAsync( "XCLAIM", stream, "spotify-workers", "dead-consumer", "0", ids[^1], "IDLE", "1800000", "JUSTID" );

        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint( "IsrcLookup", DateTimeOffset.UtcNow.AddMinutes( 5 ) )] );
        QueuedMessage<QueuedLookupRequest>? found = null;
        for (int i = 0; i < 20 && found is null; i++) {
            found = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        }
        Assert.IsNotNull( found );
        Assert.AreEqual( tailRequest.RequestId, found.Payload.RequestId );
    }

    /// <summary>Fresh malformed, literal-null, and missing payload entries are ACKed and deleted.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_FreshPoisonEntries_AreRemovedFromPelAndStream( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.StreamAddAsync( stream, [new NameValueEntry( QueueStreamFieldNames.Payload, "{not-json" )] );
        _ = await db.StreamAddAsync( stream, [new NameValueEntry( QueueStreamFieldNames.Payload, "null" )] );
        _ = await db.StreamAddAsync( stream, [new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )] );

        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [] );
        Assert.IsNull( await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );

        RedisResult pendingSummary = await db.ExecuteAsync( "XPENDING", stream, "spotify-workers" );
        RedisResult[] pendingParts = (RedisResult[])pendingSummary!;
        Assert.AreEqual( 0, (long)pendingParts[0] );
        Assert.IsEmpty( await db.StreamRangeAsync( stream, "-", "+" ) );
    }

    /// <summary>An already-owned malformed payload is removed from the current consumer PEL.</summary>
    [TestMethod]
    public async Task DequeueAsync_OwnPoisonEntry_IsRemovedFromPelAndStream( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue id = await db.StreamAddAsync( stream, [new NameValueEntry( QueueStreamFieldNames.Payload, "null" )] );
        string consumer = (string)typeof( RedisRequestQueue<QueuedLookupRequest> )
            .GetField( "_consumerId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic )!
            .GetValue( _queue )!;
        _ = await db.StreamReadGroupAsync( stream, "spotify-workers", consumer, StreamPosition.NewMessages, 1, noAck: false );

        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [] );
        Assert.IsNull( await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );
        Assert.IsEmpty( await db.StreamRangeAsync( stream, id, id ) );
    }

    /// <summary>A malformed payload abandoned by another consumer is reclaimed and removed.</summary>
    [TestMethod]
    public async Task DequeueAsync_ReclaimedPoisonEntry_IsRemovedFromPelAndStream( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue id = await db.StreamAddAsync( stream, [new NameValueEntry( QueueStreamFieldNames.Payload, "{broken" )] );
        _ = await db.StreamReadGroupAsync( stream, "spotify-workers", "dead-consumer", StreamPosition.NewMessages, 1, noAck: false );
        _ = await db.ExecuteAsync( "XCLAIM", stream, "spotify-workers", "dead-consumer", 0, id, "IDLE", 1_800_000 );

        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [] );
        Assert.IsNull( await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );
        Assert.IsEmpty( await db.StreamRangeAsync( stream, id, id ) );
    }

    /// <summary>Redis does not reclaim a live entry while it is below the generic worker idle threshold.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task AutoClaim_BelowIdleThreshold_DoesNotReclaim( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? claimed = await _queue.DequeueAsync(TestContext.CancellationToken);
        Assert.IsNotNull( claimed );
        string id = claimed.MessageId[(claimed.MessageId.LastIndexOf(':') + 1)..];
        _ = await s_redis!.GetDatabase( ).ExecuteAsync( "XCLAIM", stream, "spotify-workers", "probe-consumer", 1, id, "IDLE", 1 );
        RedisRequestQueue<QueuedLookupRequest> second = NewQueue("probe-consumer");
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [] );
        Assert.IsNull( await second.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );
        RedisResult pending = await s_redis!.GetDatabase( ).ExecuteAsync( "XPENDING", stream, "spotify-workers" );
        Assert.IsGreaterThan( 0, (long)((RedisResult[])pending!)[0] );
    }

    /// <summary>Redis XAUTOCLAIM reassigns a pending entry once the idle threshold is met.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task AutoClaim_EligibleIdleEntry_IsReclaimed( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? claimed = await _queue.DequeueAsync(TestContext.CancellationToken);
        Assert.IsNotNull( claimed );
        string id = claimed.MessageId[(claimed.MessageId.LastIndexOf(':') + 1)..];
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.ExecuteAsync( "XCLAIM", stream, "spotify-workers", "dead-consumer", 0, id, "TIME", 1 );
        StreamAutoClaimResult aged = await db.StreamAutoClaimAsync( stream, "spotify-workers", "probe", 15 * 60 * 1000, "0-0", 10 );
        Assert.IsNotNull( aged.ClaimedEntries.FirstOrDefault( entry => entry.Id == id ) );
        // Reassign the aged entry to the dead consumer without waiting so the production dequeue
        // surface must reclaim it.
        _ = await db.ExecuteAsync( "XCLAIM", stream, "spotify-workers", "dead-consumer", 0, id, "TIME", 1 );
        RedisRequestQueue<QueuedLookupRequest> second = NewQueue("reclaimer");
        await second.EnsureConsumerGroupsAsync( TestContext.CancellationToken );
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( [] );
        QueuedMessage<QueuedLookupRequest>? reclaimed = await second.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        Assert.IsNotNull( reclaimed );
        Assert.AreEqual( id, reclaimed.MessageId[(reclaimed.MessageId.LastIndexOf( ':' ) + 1)..] );
    }

    /// <summary>XAUTOCLAIM leaves an aged message pending when its endpoint is rate-limited.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task AutoClaim_AgedBlockedEntry_RemainsPending( ) {
        string stream = $"queue:{s_runToken}:spotify:interactive";
        QueuedLookupRequest blockedRequest = CreateTestRequest(
            lookupType: LookupRequestType.IsrcLookup ) with { RateLimitedEndpoint = "tracks" };
        await _queue.EnqueueAsync( blockedRequest, QueuePriority.Interactive, TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? claimed = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( claimed );
        string id = claimed.MessageId[(claimed.MessageId.LastIndexOf( ':' ) + 1)..];
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.ExecuteAsync( "XCLAIM", stream, "spotify-workers", "dead-consumer", 0, id, "TIME", 1 );
        RedisRequestQueue<QueuedLookupRequest> second = NewQueue( "blocked-reclaimer" );
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint( "tracks", DateTimeOffset.UtcNow.AddMinutes( 5 ) )] );
        Assert.IsNull( await second.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );
        RedisResult pending = await db.ExecuteAsync( "XPENDING", stream, "spotify-workers" );
        Assert.IsGreaterThan( 0, (long)((RedisResult[])pending!)[0] );
    }

    /// <summary>Consumer-group recreation is idempotent after an operational group deletion.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnsureConsumerGroups_RecreatesDeletedGroup( ) {
        IDatabase db = s_redis!.GetDatabase();
        string stream = $"queue:{s_runToken}:spotify:interactive";
        _ = await db.ExecuteAsync( "XGROUP", "DESTROY", stream, "spotify-workers" );
        await _queue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );
        RedisResult groups = await db.ExecuteAsync("XINFO", "GROUPS", stream);
        RedisResult[] groupArray = (RedisResult[])groups!;
        Assert.IsGreaterThan( 0, groupArray.Length );
    }

    /// <summary>Recreated groups start at the stream beginning and can drain pre-existing entries.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnsureConsumerGroups_RecreatesDeletedGroup_DequeuesPreExistingEntry( ) {
        IDatabase db = s_redis!.GetDatabase( );
        string stream = $"queue:{s_runToken}:spotify:interactive";
        await _queue.EnqueueAsync( CreateTestRequest( lookupType: LookupRequestType.IsrcLookup ), QueuePriority.Interactive, TestContext.CancellationToken );
        _ = await db.ExecuteAsync( "XGROUP", "DESTROY", stream, "spotify-workers" );

        await _queue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( message );
    }

    /// <summary>The shared Redis connection used by the queue under test.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>
    /// Per-run token used to namespace all stream keys so concurrent test classes cannot delete
    /// each other's in-flight data. Generated once in <see cref="ClassInitialize"/> and stable
    /// for the entire class run.
    /// </summary>
    private static string s_runToken = string.Empty;

    /// <summary>Mock logger captured for the queue under test.</summary>
    private Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> _mockLogger = null!;
    /// <summary>Queue settings supplied to the queue under test.</summary>
    private IOptions<QueueSettings> _settings = null!;
    /// <summary>The Spotify-provider queue under test, recreated for each test.</summary>
    private RedisRequestQueue<QueuedLookupRequest> _queue = null!;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Requires the shared Redis container, opens a connection, and generates a stable per-run key
    /// prefix token so every stream key created by this class is isolated from other concurrent classes.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
        s_runToken = Guid.NewGuid( ).ToString( "N" )[..8];
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
    /// Clears only this run's prefixed <c>queue:{runToken}:*</c> keys, constructs a fresh Spotify
    /// queue using the same prefix, and ensures its consumer groups exist before each test.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        await CleanupOwnKeysAsync( );

        _mockLogger = new Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( );
        _settings = Options.Create( new QueueSettings( ) );

        _queue = new RedisRequestQueue<QueuedLookupRequest>(
            s_redis!,
            _mockLogger.Object,
            _settings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );

        // Ensure consumer groups exist
        await _queue.EnsureConsumerGroupsAsync( CancellationToken.None );
    }

    /// <summary>
    /// Deletes this run's own prefixed <c>queue:{runToken}:*</c> keys. Uses the run-token-namespaced
    /// pattern only — no bare wildcard — so keys owned by other concurrently executing test classes
    /// are never touched.
    /// </summary>
    private async Task CleanupOwnKeysAsync( ) {
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        string ownPattern = $"queue:{s_runToken}:*";
        await foreach (RedisKey key in server.KeysAsync( pattern: ownPattern )) {
            _ = await db.KeyDeleteAsync( key );
        }
    }

    private RedisRequestQueue<QueuedLookupRequest> NewQueue( string _ ) => new(
        s_redis!,
        new Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ).Object,
        _settings,
        SupportedProviders.Spotify,
        keyPrefix: s_runToken );

    /// <summary>
    /// Verifies enqueuing at interactive priority increases only the interactive depth.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueAsync_AddsMessageToInteractiveStream( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );

        // Act
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        // Assert
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 1, depth.Interactive );
        Assert.AreEqual( 0, depth.Background );
        Assert.AreEqual( 0, depth.Bulk );
        Assert.AreEqual( 1, depth.Total );
    }

    /// <summary>
    /// Verifies enqueuing at background priority increases only the background depth.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueAsync_AddsMessageToBackgroundStream( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );

        // Act
        await _queue.EnqueueAsync( request, QueuePriority.Background, TestContext.CancellationToken );

        // Assert
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 0, depth.Interactive );
        Assert.AreEqual( 1, depth.Background );
        Assert.AreEqual( 0, depth.Bulk );
    }

    /// <summary>
    /// Verifies enqueuing at bulk priority increases only the bulk depth.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EnqueueAsync_AddsMessageToBulkStream( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );

        // Act
        await _queue.EnqueueAsync( request, QueuePriority.Bulk, TestContext.CancellationToken );

        // Assert
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 0, depth.Interactive );
        Assert.AreEqual( 0, depth.Background );
        Assert.AreEqual( 1, depth.Bulk );
    }

    /// <summary>
    /// Verifies dequeuing returns the enqueued message with its request, saga, and lookup values intact.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_ReturnsMessage_WhenAvailable( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        // Act
        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );

        // Assert
        Assert.IsNotNull( message );
        Assert.AreEqual( request.RequestId, message.Payload.RequestId );
        Assert.AreEqual( request.SagaId, message.Payload.SagaId );
        Assert.AreEqual( request.LookupValue, message.Payload.LookupValue );
    }

    /// <summary>
    /// Verifies dequeuing from an empty queue returns null.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_ReturnsNull_WhenQueueEmpty( ) {
        // Act
        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );

        // Assert
        Assert.IsNull( message );
    }

    /// <summary>
    /// Verifies acknowledging a dequeued message removes it so the queue depth returns to zero.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task AcknowledgeAsync_RemovesMessageFromStream( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( message );

        // Act
        await _queue.AcknowledgeAsync( message.MessageId, TestContext.CancellationToken );

        // Assert - queue should be empty after ack
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 0, depth.Total );
    }

    /// <summary>Unreadable deliveries are preserved in the DLQ instead of being silently destroyed.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_UnreadablePayload_MovesRawEntryToDlq( ) {
        IDatabase db = s_redis!.GetDatabase( );
        string interactive = $"queue:{s_runToken}:spotify:interactive";
        string dlq = $"queue:{s_runToken}:spotify:dlq";
        _ = await db.StreamAddAsync( interactive, [
            new NameValueEntry( QueueStreamFieldNames.Payload, "{not-json" ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ] );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );

        Assert.IsNull( message );
        Assert.AreEqual( 0L, await db.StreamLengthAsync( interactive ) );
        Assert.AreEqual( 1L, await db.StreamLengthAsync( dlq ) );
        StreamEntry[] entries = await db.StreamRangeAsync( dlq );
        Assert.AreEqual( "{not-json", entries[0][QueueStreamFieldNames.Payload].ToString( ) );
        Assert.AreEqual( "Unreadable queue payload.", entries[0]["dlqReason"].ToString( ) );
    }

    /// <summary>
    /// Verifies moving a dequeued message to the dead-letter queue removes it from the main queue and
    /// makes it retrievable from the DLQ.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task MoveToDlqAsync_MovesMessageToDlq( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( message );

        // Act
        await _queue.MoveToDlqAsync( message.MessageId, "Test failure reason", TestContext.CancellationToken );
        // Retrying after an ambiguous client-side outcome must not create a second DLQ entry.
        await _queue.MoveToDlqAsync( message.MessageId, "Test failure reason", TestContext.CancellationToken );

        // Assert - main queue empty, DLQ has message
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 0, depth.Total );

        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> dlqMessages = await _queue.GetDlqMessagesAsync( 10, TestContext.CancellationToken );
        Assert.HasCount( 1, dlqMessages );
        Assert.AreEqual( request.RequestId, dlqMessages[0].Payload.RequestId );
    }

    /// <summary>
    /// Verifies requeuing a message from the dead-letter queue removes it from the DLQ and places it
    /// back on the requested priority stream.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RequeueFromDlqAsync_MovesMessageBackToQueue( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( message );

        await _queue.MoveToDlqAsync( message.MessageId, "Test failure", TestContext.CancellationToken );

        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> dlqMessages = await _queue.GetDlqMessagesAsync( 10, TestContext.CancellationToken );
        Assert.HasCount( 1, dlqMessages );

        // Act
        await _queue.RequeueFromDlqAsync( dlqMessages[0].MessageId, QueuePriority.Background, TestContext.CancellationToken );

        // Assert - DLQ empty, background queue has message
        dlqMessages = await _queue.GetDlqMessagesAsync( 10, TestContext.CancellationToken );
        Assert.IsEmpty( dlqMessages );

        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 1, depth.Background );
    }

    /// <summary>
    /// Verifies deleting a message from the dead-letter queue returns true and removes it.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DeleteFromDlqAsync_RemovesMessageFromDlq( ) {
        // Arrange - use Interactive priority to avoid bulk gating
        QueuedLookupRequest request = CreateTestRequest( );
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( message );

        await _queue.MoveToDlqAsync( message.MessageId, "Test failure", TestContext.CancellationToken );

        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> dlqMessages = await _queue.GetDlqMessagesAsync( 10, TestContext.CancellationToken );
        Assert.HasCount( 1, dlqMessages );

        // Act
        bool deleted = await _queue.DeleteFromDlqAsync( dlqMessages[0].MessageId, TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( deleted );

        dlqMessages = await _queue.GetDlqMessagesAsync( 10, TestContext.CancellationToken );
        Assert.IsEmpty( dlqMessages );
    }

    /// <summary>
    /// Verifies queues for different providers are isolated: each provider's depth and dequeued messages
    /// reflect only that provider's traffic.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task MultipleProviderQueues_AreIsolated( ) {
        // Arrange
        RedisRequestQueue<QueuedLookupRequest> spotifyQueue = _queue;

        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> appleLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> appleQueue = new(
            s_redis!,
            appleLogger.Object,
            _settings,
            SupportedProviders.AppleMusic,
            keyPrefix: s_runToken
        );
        await appleQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        QueuedLookupRequest spotifyRequest = CreateTestRequest( provider: SupportedProviders.Spotify );
        QueuedLookupRequest appleRequest = CreateTestRequest( provider: SupportedProviders.AppleMusic );

        // Act
        await spotifyQueue.EnqueueAsync( spotifyRequest, QueuePriority.Interactive, TestContext.CancellationToken );
        await appleQueue.EnqueueAsync( appleRequest, QueuePriority.Interactive, TestContext.CancellationToken );

        // Assert - each queue has its own message
        QueueDepth spotifyDepth = await spotifyQueue.GetDepthAsync( TestContext.CancellationToken );
        QueueDepth appleDepth = await appleQueue.GetDepthAsync( TestContext.CancellationToken );

        Assert.AreEqual( 1, spotifyDepth.Interactive );
        Assert.AreEqual( 1, appleDepth.Interactive );

        // Dequeue from Spotify should get Spotify request
        QueuedMessage<QueuedLookupRequest>? spotifyMessage = await spotifyQueue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( spotifyMessage );
        Assert.AreEqual( SupportedProviders.Spotify, spotifyMessage.Payload.Provider );

        // Dequeue from Apple should get Apple request
        QueuedMessage<QueuedLookupRequest>? appleMessage = await appleQueue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( appleMessage );
        Assert.AreEqual( SupportedProviders.AppleMusic, appleMessage.Payload.Provider );
    }

    /// <summary>
    /// Verifies that, with bulk gating disabled, repeated dequeues drain every message across all three
    /// priorities and leave the queue empty.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WeightedPrioritySelection_ProcessesAllMessages( ) {
        // Arrange - add multiple messages to each priority
        const int MessagesPerPriority = 10;

        // Create a queue with bulk gating disabled for this test; use the run-token prefix so
        // this instance shares the namespaced stream with _queue and TestInitialize cleanup.
        IOptions<QueueSettings> testSettings = Options.Create( new QueueSettings {
            DefaultMinBulkQueueThreshold = 0 // Disable bulk gating
        } );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> testLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> testQueue = new(
            s_redis!,
            testLogger.Object,
            testSettings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await testQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        for (int i = 0; i < MessagesPerPriority; i++) {
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Background, TestContext.CancellationToken );
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Bulk, TestContext.CancellationToken );
        }

        // Act - dequeue all and track which priorities are drained first
        // With weighted selection, all messages should eventually be processed
        int totalDequeued = 0;

        QueuedMessage<QueuedLookupRequest>? message;
        while ((message = await testQueue.DequeueAsync( TestContext.CancellationToken )) is not null) {
            await testQueue.AcknowledgeAsync( message.MessageId, TestContext.CancellationToken );
            totalDequeued++;
        }

        // Assert - all messages should be processed
        Assert.AreEqual( MessagesPerPriority * 3, totalDequeued, "All messages should be dequeued" );

        QueueDepth depth = await testQueue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 0, depth.Total, "Queue should be empty after processing all messages" );
    }

    /// <summary>
    /// Verifies two queue instances sharing a consumer group split the messages between them (each
    /// processing at least one) and together drain the queue.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ConsumerGroups_AllowMultipleWorkerInstances( ) {
        // Arrange - create two queue instances (simulating two workers); both must share
        // the same run-token prefix so they operate on the same namespaced stream.
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> logger2 = new( );
        RedisRequestQueue<QueuedLookupRequest> worker2Queue = new(
            s_redis!,
            logger2.Object,
            _settings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await worker2Queue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        // Enqueue multiple messages
        const int MessageCount = 10;
        for (int i = 0; i < MessageCount; i++) {
            await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        }

        // Act - both workers dequeue concurrently
        int worker1Count = 0;
        int worker2Count = 0;

        for (int i = 0; i < MessageCount; i++) {
            // Alternate between workers
            if (i % 2 == 0) {
                QueuedMessage<QueuedLookupRequest>? msg = await _queue.DequeueAsync( TestContext.CancellationToken );
                if (msg is not null) {
                    worker1Count++;
                    await _queue.AcknowledgeAsync( msg.MessageId, TestContext.CancellationToken );
                }
            } else {
                QueuedMessage<QueuedLookupRequest>? msg = await worker2Queue.DequeueAsync( TestContext.CancellationToken );
                if (msg is not null) {
                    worker2Count++;
                    await worker2Queue.AcknowledgeAsync( msg.MessageId, TestContext.CancellationToken );
                }
            }
        }

        // Assert - both workers processed messages, no duplicates
        Assert.AreEqual( MessageCount, worker1Count + worker2Count );
        Assert.IsGreaterThan( 0, worker1Count, "Worker 1 should have processed at least one message" );
        Assert.IsGreaterThan( 0, worker2Count, "Worker 2 should have processed at least one message" );

        // Queue should be empty
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 0, depth.Total );
    }

    /// <summary>
    /// Verifies requeuing a dequeued message for retry returns it to the queue and makes it dequeuable
    /// again.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RequeueAsync_PutsMessageBackForRetry( ) {
        // Arrange
        QueuedLookupRequest request = CreateTestRequest( );
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( message );

        // Simulate temporary failure - requeue with delay
        await _queue.RequeueAsync( message.MessageId, TimeSpan.FromMilliseconds( 100 ), TestContext.CancellationToken );

        // Assert - message should be back in queue
        QueueDepth depth = await _queue.GetDepthAsync( TestContext.CancellationToken );
        Assert.AreEqual( 1, depth.Total );

        // Should be able to dequeue again
        QueuedMessage<QueuedLookupRequest>? requeued = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( requeued );
        Assert.AreEqual( request.RequestId, requeued.Payload.RequestId );
    }

    /// <summary>An own-PEL scan cannot return a delivery that a local task is still processing.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DequeueAsync_ActiveOwnPelDelivery_IsNotReturnedConcurrently( ) {
        await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );

        QueuedMessage<QueuedLookupRequest>? first = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        Assert.IsNotNull( first );

        QueuedMessage<QueuedLookupRequest>? duplicate = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        Assert.IsNull( duplicate );

        ((IQueueDeliveryTracker)_queue).ReleaseDelivery( first.MessageId );
        QueuedMessage<QueuedLookupRequest>? recovered = await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken );
        Assert.IsNotNull( recovered );
        Assert.AreEqual( first.MessageId, recovered.MessageId );
        await _queue.AcknowledgeAsync( recovered.MessageId, TestContext.CancellationToken );
    }

    /// <summary>A transient requeue atomically replaces the delivery and advances its attempt.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RequeueAsync_TransientRetry_IncrementsAttemptExactlyOnce( ) {
        QueuedLookupRequest request = CreateTestRequest( ) with { AttemptCount = 2 };
        await _queue.EnqueueAsync( request, QueuePriority.Interactive, TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? original = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( original );

        await _queue.RequeueAsync( original.MessageId, cancellationToken: TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? retry = await _queue.DequeueAsync( TestContext.CancellationToken );

        Assert.IsNotNull( retry );
        Assert.AreEqual( 3, retry.Payload.AttemptCount );
        await _queue.AcknowledgeAsync( retry.MessageId, TestContext.CancellationToken );
    }

    /// <summary>Rate-aware workers defer an ordinary transient retry until its backoff expires.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RequeueAsync_TransientRetry_HonorsNotBefore( ) {
        await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? original = await _queue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( original );
        await _queue.RequeueAsync( original.MessageId, cancellationToken: TestContext.CancellationToken );

        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                It.IsAny<SupportedProviders>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        Assert.IsNull( await _queue.DequeueAsync( tracker.Object, TestContext.CancellationToken ) );

        await Task.Delay( TimeSpan.FromMilliseconds( 1100 ), TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest>? retry = await _queue.DequeueAsync(
            tracker.Object, TestContext.CancellationToken );
        Assert.IsNotNull( retry );
        Assert.AreEqual( 1, retry.Payload.AttemptCount );
        Assert.IsNotNull( retry.Payload.NotBefore );
        await _queue.AcknowledgeAsync( retry.MessageId, TestContext.CancellationToken );
    }

    /// <summary>
    /// Verifies each queue instance gets a unique consumer id prefixed by its provider, ending in a
    /// 32-character hex GUID, and kept within a reasonable length.
    /// </summary>
    [TestMethod]
    public void ConsumerIds_AreUniqueAndWellFormatted( ) {
        // Arrange - create multiple queue instances for the same provider
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> logger1 = new( );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> logger2 = new( );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> logger3 = new( );

        RedisRequestQueue<QueuedLookupRequest> queue1 = new(
            s_redis!,
            logger1.Object,
            _settings,
            SupportedProviders.Spotify
        );

        RedisRequestQueue<QueuedLookupRequest> queue2 = new(
            s_redis!,
            logger2.Object,
            _settings,
            SupportedProviders.Spotify
        );

        RedisRequestQueue<QueuedLookupRequest> queue3 = new(
            s_redis!,
            logger3.Object,
            _settings,
            SupportedProviders.AppleMusic
        );

        // Act - get consumer IDs using reflection
        System.Reflection.FieldInfo? consumerIdField = typeof( RedisRequestQueue<QueuedLookupRequest> )
            .GetField( "_consumerId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance );
        Assert.IsNotNull( consumerIdField, "Could not find _consumerId field" );

        string? consumerId1 = consumerIdField.GetValue( queue1 ) as string;
        string? consumerId2 = consumerIdField.GetValue( queue2 ) as string;
        string? consumerId3 = consumerIdField.GetValue( queue3 ) as string;

        // Assert - consumer IDs should be unique
        Assert.IsNotNull( consumerId1 );
        Assert.IsNotNull( consumerId2 );
        Assert.IsNotNull( consumerId3 );

        Assert.AreNotEqual( consumerId1, consumerId2, "Consumer IDs for same provider should be unique" );
        Assert.AreNotEqual( consumerId1, consumerId3, "Consumer IDs for different providers should be unique" );
        Assert.AreNotEqual( consumerId2, consumerId3, "Consumer IDs for different providers should be unique" );

        // Assert - format should be "{provider}-worker-{guid}"
        // Spotify consumer IDs should start with "spotify-worker-"
        Assert.IsTrue( consumerId1.StartsWith( "spotify-worker-", StringComparison.OrdinalIgnoreCase ),
            $"Consumer ID '{consumerId1}' should start with 'spotify-worker-'" );
        Assert.IsTrue( consumerId2.StartsWith( "spotify-worker-", StringComparison.OrdinalIgnoreCase ),
            $"Consumer ID '{consumerId2}' should start with 'spotify-worker-'" );

        // Apple Music consumer IDs should start with "applemusic-worker-"
        Assert.IsTrue( consumerId3.StartsWith( "applemusic-worker-", StringComparison.OrdinalIgnoreCase ),
            $"Consumer ID '{consumerId3}' should start with 'applemusic-worker-'" );

        // Assert - GUID portion should be 32 characters (format N) and be valid hex strings
        const string SpotifyPrefix = "spotify-worker-";
        const string AppleMusicPrefix = "applemusic-worker-";

        string guidPart1 = consumerId1[ SpotifyPrefix.Length..];
        string guidPart2 = consumerId2[ SpotifyPrefix.Length..];
        string guidPart3 = consumerId3[ AppleMusicPrefix.Length..];

        Assert.AreEqual( 32, guidPart1.Length, "GUID portion should be 32 characters" );
        Assert.AreEqual( 32, guidPart2.Length, "GUID portion should be 32 characters" );
        Assert.AreEqual( 32, guidPart3.Length, "GUID portion should be 32 characters" );

        Assert.IsTrue( GuidHexRegex( ).IsMatch( guidPart1 ),
            $"GUID portion '{guidPart1}' should be a valid hex string" );
        Assert.IsTrue( GuidHexRegex( ).IsMatch( guidPart2 ),
            $"GUID portion '{guidPart2}' should be a valid hex string" );
        Assert.IsTrue( GuidHexRegex( ).IsMatch( guidPart3 ),
            $"GUID portion '{guidPart3}' should be a valid hex string" );

        // Assert - total length should be reasonable (provider name + "-worker-" + 32 char GUID)
        // Longest provider name is "applemusic" (10) + "-worker-" (8) + GUID (32) = 50 chars max
        Assert.IsLessThanOrEqualTo( 60, consumerId1.Length, $"Consumer ID '{consumerId1}' should be reasonably short (length: {consumerId1.Length})" );
        Assert.IsLessThanOrEqualTo( 60, consumerId2.Length, $"Consumer ID '{consumerId2}' should be reasonably short (length: {consumerId2.Length})" );
        Assert.IsLessThanOrEqualTo( 60, consumerId3.Length, $"Consumer ID '{consumerId3}' should be reasonably short (length: {consumerId3.Length})" );
    }

    // -------------------------------------------------------------------------
    // B7–B11: Interactive-first ordering with aging (new deterministic algorithm)
    // -------------------------------------------------------------------------

    /// <summary>
    /// B7 — Interactive preemption end-to-end.
    /// Background is enqueued first; then an interactive message arrives.
    /// On the very next non-aging dequeue the interactive message must be served first.
    /// Failure-first: before the deterministic algorithm, weighted-random might deliver background first.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task B7_InteractivePreemption_DequeuesInteractiveBeforeBackground( ) {
        // Arrange — background arrives first, interactive arrives second
        IOptions<QueueSettings> testSettings = Options.Create( new QueueSettings {
            DefaultMinBulkQueueThreshold = 0,
            InteractiveAgingInterval = 8 // default; first aging slot at counter=8
        } );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> testLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> testQueue = new(
            s_redis!,
            testLogger.Object,
            testSettings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await testQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        QueuedLookupRequest bgRequest = CreateTestRequest( );
        QueuedLookupRequest interactiveRequest = CreateTestRequest( );

        await testQueue.EnqueueAsync( bgRequest, QueuePriority.Background, TestContext.CancellationToken );
        await testQueue.EnqueueAsync( interactiveRequest, QueuePriority.Interactive, TestContext.CancellationToken );

        // Act — counter=1 (not an aging slot); interactive must lead
        QueuedMessage<QueuedLookupRequest>? first = await testQueue.DequeueAsync( TestContext.CancellationToken );

        // Assert
        Assert.IsNotNull( first );
        Assert.AreEqual( interactiveRequest.RequestId, first.Payload.RequestId,
            "Interactive message must be dequeued before background on a normal (non-aging) slot" );
    }

    /// <summary>
    /// B8 — Bounded starvation over a >N window.
    /// Enqueues only background messages and drives N dequeues.
    /// The Nth dequeue is the first aging slot (backgroundLeads=true), so background must be served.
    /// This is the highest-value guard: verifies starvation is bounded within N calls.
    /// Failure-first: without the aging algorithm, background would never be served
    /// (interactive stream checked first every time, returning nothing, but background also checked).
    /// Actually without aging, background IS served when interactive is empty — so the real guard is
    /// that interactive does NOT block background when interactive is empty, AND that with only
    /// background+interactive mixed, aging ensures background gets a turn within N.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task B8_BoundedStarvation_BackgroundServedWithinAgingWindow( ) {
        // Arrange — use N=4 so the test stays fast; first aging slot at counter=4
        const int AgingInterval = 4;
        IOptions<QueueSettings> testSettings = Options.Create( new QueueSettings {
            DefaultMinBulkQueueThreshold = 0,
            InteractiveAgingInterval = AgingInterval
        } );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> testLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> testQueue = new(
            s_redis!,
            testLogger.Object,
            testSettings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await testQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        // Enqueue AgingInterval interactive messages AND AgingInterval background messages
        // so interactive stream is not empty on the first (N-1) dequeues.
        const int MsgCount = AgingInterval;
        for (int i = 0; i < MsgCount; i++) {
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Background, TestContext.CancellationToken );
        }

        // Act — dequeue exactly N times (counters 1..N), tracking priority of each result
        List<QueuePriority> servedPriorities = [];
        for (int call = 1; call <= AgingInterval; call++) {
            QueuedMessage<QueuedLookupRequest>? msg = await testQueue.DequeueAsync( TestContext.CancellationToken );
            Assert.IsNotNull( msg, $"Expected a message on dequeue call {call}" );

            // Determine which stream the message came from (messageId starts with stream key)
            QueuePriority priority = msg.MessageId.Contains( ":background:" ) || msg.MessageId.Contains( "background:" )
                ? QueuePriority.Background
                : QueuePriority.Interactive;
            servedPriorities.Add( priority );

            await testQueue.AcknowledgeAsync( msg.MessageId, TestContext.CancellationToken );
        }

        // Assert — at least one background message must have been served (the aging slot at call N)
        bool backgroundServed = servedPriorities.Contains( QueuePriority.Background );
        Assert.IsTrue( backgroundServed,
            $"Background must be served within {AgingInterval} dequeues (aging slot at call {AgingInterval}). " +
            $"Served: [{string.Join( ", ", servedPriorities )}]" );

        // Assert — calls 1..(N-1) must all be interactive (aging slot only at call N)
        for (int i = 0; i < AgingInterval - 1; i++) {
            Assert.AreEqual( QueuePriority.Interactive, servedPriorities[i],
                $"Call {i + 1} (non-aging slot) must serve interactive; served {servedPriorities[i]}" );
        }

        // Assert — call N must be background (first aging slot, backgroundLeads=true for agingSlotIndex=1)
        Assert.AreEqual( QueuePriority.Background, servedPriorities[AgingInterval - 1],
            $"Call {AgingInterval} (aging slot) must serve background; served {servedPriorities[AgingInterval - 1]}" );
    }

    /// <summary>
    /// B9 — Bulk gating two-phase.
    /// Phase 1: bulk depth below threshold → bulk messages not served.
    /// Phase 2: bulk depth reaches threshold → bulk messages eligible.
    /// Failure-first: without bulk gating, bulk would be served in phase 1.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task B9_BulkGating_TwoPhase_BulkServedOnlyAboveThreshold( ) {
        const int BulkThreshold = 3;
        IOptions<QueueSettings> testSettings = Options.Create( new QueueSettings {
            DefaultMinBulkQueueThreshold = BulkThreshold,
            InteractiveAgingInterval = 100 // large N so aging slot doesn't interfere
        } );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> testLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> testQueue = new(
            s_redis!,
            testLogger.Object,
            testSettings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await testQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        // Phase 1: enqueue fewer bulk messages than threshold (BulkThreshold - 1)
        QueuedLookupRequest bgRequest = CreateTestRequest( );
        await testQueue.EnqueueAsync( bgRequest, QueuePriority.Background, TestContext.CancellationToken );
        for (int i = 0; i < BulkThreshold - 1; i++) {
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Bulk, TestContext.CancellationToken );
        }

        // Dequeue — must get background (bulk gated out)
        QueuedMessage<QueuedLookupRequest>? phase1Msg = await testQueue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( phase1Msg );
        Assert.AreEqual( bgRequest.RequestId, phase1Msg.Payload.RequestId,
            "Phase 1: background must be served when bulk depth is below threshold" );
        await testQueue.AcknowledgeAsync( phase1Msg.MessageId, TestContext.CancellationToken );

        // Phase 2: add more bulk to reach threshold
        for (int i = 0; i < BulkThreshold - (BulkThreshold - 1); i++) {
            await testQueue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Bulk, TestContext.CancellationToken );
        }
        // Now bulk depth = BulkThreshold; add interactive so interactive is still first
        QueuedLookupRequest interactiveRequest = CreateTestRequest( );
        await testQueue.EnqueueAsync( interactiveRequest, QueuePriority.Interactive, TestContext.CancellationToken );

        // Normal dequeue: interactive first (non-aging slot)
        QueuedMessage<QueuedLookupRequest>? phase2First = await testQueue.DequeueAsync( TestContext.CancellationToken );
        Assert.IsNotNull( phase2First );
        await testQueue.AcknowledgeAsync( phase2First.MessageId, TestContext.CancellationToken );

        // Drain remaining: with no interactive left and bulk eligible, bulk must eventually appear
        bool bulkServed = false;
        QueuedMessage<QueuedLookupRequest>? next;
        while ((next = await testQueue.DequeueAsync( TestContext.CancellationToken )) is not null) {
            if (next.MessageId.Contains( "bulk:" )) {
                bulkServed = true;
            }
            await testQueue.AcknowledgeAsync( next.MessageId, TestContext.CancellationToken );
        }
        Assert.IsTrue( bulkServed,
            "Phase 2: bulk messages must be served once depth reaches threshold" );
    }

    /// <summary>
    /// B10 — Rate-limited interactive stream must not block eligible background.
    /// Enqueues an interactive message whose lookup type is "blocked" and a background message
    /// whose lookup type is not blocked. DequeueAsync(rateLimitTracker) must skip the interactive
    /// message and return the background message.
    /// Failure-first: before the rate-limit-aware dequeue, interactive would be returned regardless.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task B10_RateLimitedInteractive_DoesNotBlockEligibleBackground( ) {
        // Arrange
        IOptions<QueueSettings> testSettings = Options.Create( new QueueSettings {
            DefaultMinBulkQueueThreshold = 0,
            InteractiveAgingInterval = 100
        } );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> testLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> testQueue = new(
            s_redis!,
            testLogger.Object,
            testSettings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await testQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        // Interactive request uses IsrcLookup (will be blocked)
        const string BlockedEndpoint = "tracks";
        QueuedLookupRequest interactiveRequest = CreateTestRequest(
            lookupType: LookupRequestType.IsrcLookup ) with { RateLimitedEndpoint = BlockedEndpoint };
        // Authentication has an independent cooldown from Spotify's provider-wide data API.
        QueuedLookupRequest bgRequest = CreateTestRequest( lookupType: LookupRequestType.SongIdLookup ) with {
            RateLimitedEndpoint = ProviderEndpointConstants.AuthToken
        };

        await testQueue.EnqueueAsync( interactiveRequest, QueuePriority.Interactive, TestContext.CancellationToken );
        await testQueue.EnqueueAsync( bgRequest, QueuePriority.Background, TestContext.CancellationToken );

        // Mock rate limit tracker: IsrcLookup is blocked for Spotify
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> trackerMock = new( );
        _ = trackerMock
            .Setup( t => t.GetAllRateLimitedAsync( SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (IReadOnlyList<RateLimitedEndpoint>)[
                new RateLimitedEndpoint( BlockedEndpoint, DateTimeOffset.UtcNow.AddMinutes( 1 ) )
            ] );

        // Act — rate-limit-aware dequeue must skip interactive and serve background
        QueuedMessage<QueuedLookupRequest>? message = await testQueue.DequeueAsync( trackerMock.Object, TestContext.CancellationToken );

        // Assert
        Assert.IsNotNull( message );
        Assert.AreEqual( bgRequest.RequestId, message.Payload.RequestId,
            "Background message must be served when the interactive message's endpoint is rate-limited" );
    }

    /// <summary>A provider-wide cooldown blocks fresh work that has no carried endpoint yet.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProviderWideCooldown_BlocksNewWorkWithoutRateLimitedEndpoint( ) {
        await _queue.EnqueueAsync( CreateTestRequest( ), QueuePriority.Interactive, TestContext.CancellationToken );
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint(
                ProviderEndpointConstants.ProviderWide,
                DateTimeOffset.UtcNow.AddMinutes( 1 ) )] );

        QueuedMessage<QueuedLookupRequest>? message = await _queue.DequeueAsync(
            tracker.Object, TestContext.CancellationToken );

        Assert.IsNull( message );
    }

    /// <summary>
    /// B11 — Control: interactive served first when not blocked.
    /// With both interactive and background messages present and no rate limiting,
    /// the first dequeue must return the interactive message.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task B11_Control_InteractiveServedFirstWhenNotBlocked( ) {
        // Arrange
        IOptions<QueueSettings> testSettings = Options.Create( new QueueSettings {
            DefaultMinBulkQueueThreshold = 0,
            InteractiveAgingInterval = 100
        } );
        Mock<ILogger<RedisRequestQueue<QueuedLookupRequest>>> testLogger = new( );
        RedisRequestQueue<QueuedLookupRequest> testQueue = new(
            s_redis!,
            testLogger.Object,
            testSettings,
            SupportedProviders.Spotify,
            keyPrefix: s_runToken
        );
        await testQueue.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        QueuedLookupRequest bgRequest = CreateTestRequest( );
        QueuedLookupRequest interactiveRequest = CreateTestRequest( );
        await testQueue.EnqueueAsync( bgRequest, QueuePriority.Background, TestContext.CancellationToken );
        await testQueue.EnqueueAsync( interactiveRequest, QueuePriority.Interactive, TestContext.CancellationToken );

        // Mock tracker: nothing rate-limited
        Mock<BridgeBeats.Contracts.Interfaces.IRateLimitTracker> trackerMock = new( );
        _ = trackerMock
            .Setup( t => t.GetAllRateLimitedAsync( SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (IReadOnlyList<RateLimitedEndpoint>)[] );

        // Act
        QueuedMessage<QueuedLookupRequest>? message = await testQueue.DequeueAsync( trackerMock.Object, TestContext.CancellationToken );

        // Assert
        Assert.IsNotNull( message );
        Assert.AreEqual( interactiveRequest.RequestId, message.Payload.RequestId,
            "Interactive must be served first when no rate limiting is active" );
    }

    /// <summary>
    /// Builds a <see cref="QueuedLookupRequest"/> with unique identifiers for the given provider and
    /// lookup type.
    /// </summary>
    /// <param name="provider">The music provider for the request. Defaults to Spotify.</param>
    /// <param name="lookupType">The lookup type for the request. Defaults to IsrcLookup.</param>
    /// <returns>A new <see cref="QueuedLookupRequest"/> instance.</returns>
    private static QueuedLookupRequest CreateTestRequest(
        SupportedProviders provider = SupportedProviders.Spotify,
        LookupRequestType lookupType = LookupRequestType.IsrcLookup
    ) {
        string id = Guid.NewGuid( ).ToString( "N" )[..8];
        return new QueuedLookupRequest {
            RequestId = $"req-{id}",
            Provider = provider,
            LookupType = lookupType,
            LookupValue = $"USRC{id}",
            SagaId = $"saga-{id}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Regression guard: verifies that <see cref="CleanupOwnKeysAsync"/> never touches keys outside
    /// this run's own prefix. A bare <c>queue:*</c> wipe would delete the sentinel and fail this
    /// test, proving the cleanup is scoped to this class's namespaced keyspace.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TestIsolation_ForeignPrefixKeys_AreNeverTouched( ) {
        IDatabase db = s_redis!.GetDatabase( );

        // Seed a sentinel under a foreign prefix before the cleanup runs
        string foreignKey = "queue:__sentinel__:guard";
        _ = await db.StringSetAsync( foreignKey, "exists", TimeSpan.FromMinutes( 5 ) );

        // Exercise the real cleanup method (same code path as TestInitialize)
        await CleanupOwnKeysAsync( );

        // The sentinel must survive: cleanup must be scoped to this run's prefix only
        bool sentinelStillExists = await db.KeyExistsAsync( foreignKey );
        Assert.IsTrue( sentinelStillExists,
            "CleanupOwnKeysAsync touched a key outside this class's prefix; isolation is broken." );

        // Cleanup: remove the sentinel we placed
        _ = await db.KeyDeleteAsync( foreignKey );
    }

    [GeneratedRegex( "^[0-9a-f]{32}$" )]
    private static partial Regex GuidHexRegex( );
}
