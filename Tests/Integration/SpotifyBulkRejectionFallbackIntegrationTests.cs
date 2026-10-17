using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Spotify;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Worker.Spotify;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration test for the 4xx bulk-rejection fallback: seeds the bulk track stream, mocks the
/// lookup service to throw <see cref="SpotifyBulkRejectedException"/>, runs one cycle, and verifies
/// that the bulk stream drains to 0 and each item is atomically forwarded to the interactive
/// stream rather than being re-added to the bulk stream.
/// </summary>
/// <remarks>
/// Guards against a regression where the rejection path silently discards items (the stream drains
/// but items are not forwarded) or re-adds them to the bulk stream (creating an infinite loop).
/// Requires a live Redis container via Testcontainers; marked Inconclusive when Docker is unavailable.
/// </remarks>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public class SpotifyBulkRejectionFallbackIntegrationTests {

    /// <summary>Shared Redis connection for the test class.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>JSON options used to serialize request payloads (camelCase, compact).</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Opens the shared Redis connection after verifying Docker is available.
    /// </summary>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
    }

    /// <summary>Closes and disposes the shared Redis connection.</summary>
    [ClassCleanup]
    public static async Task ClassCleanup( ) {
        if (s_redis is not null) {
            await s_redis.CloseAsync( );
            s_redis.Dispose( );
        }
    }

    /// <summary>
    /// Clears the bulk track stream before each test so prior test state does not leak.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.KeyDeleteAsync( SpotifyConstants.BulkTrackIdStream );
        _ = await db.KeyDeleteAsync( SpotifyConstants.BackgroundStream );
    }

    /// <summary>
    /// Seeds the bulk track stream with two items, mocks the lookup service to throw
    /// <see cref="SpotifyBulkRejectedException"/>, runs one cycle, and asserts:
    /// (a) the bulk stream drains to 0 — original items are acknowledged and removed, and
    /// (b) each item is forwarded at Interactive priority to the inner queue, not re-added to the
    /// bulk stream.
    /// </summary>
    [TestMethod]
    public async Task BulkTrackRejection_WhenLookupThrows4xx_DrainsBulkStreamAndForwardsToInteractiveQueue( ) {
        SharedTestInfrastructure.RequireRedis( );

        IDatabase db = s_redis!.GetDatabase( );
        const string TrackStream = SpotifyConstants.BulkTrackIdStream;
        const string TrackId1 = "spotifyRejectionTestTrack001";
        const string TrackId2 = "spotifyRejectionTestTrack002";
        const string TestSagaId = "ffffffffffffffffffffffffffffffff";

        // Arrange — seed two entries in the bulk track stream
        QueuedLookupRequest request1 = new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = TrackId1,
            SagaId = TestSagaId,
            IsAlbum = false,
            OriginPriority = QueuePriority.Bulk,
            AttemptCount = 0
        };
        QueuedLookupRequest request2 = request1 with {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            LookupValue = TrackId2
        };

        NameValueEntry[] fields1 = [
            new NameValueEntry( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request1, s_jsonOptions ) ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ];
        NameValueEntry[] fields2 = [
            new NameValueEntry( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request2, s_jsonOptions ) ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ];
        _ = await db.StreamAddAsync( TrackStream, fields1 );
        _ = await db.StreamAddAsync( TrackStream, fields2 );

        // Verify seed — stream has 2 entries before the cycle
        long depthBefore = await db.StreamLengthAsync( TrackStream );
        Assert.AreEqual( 2L, depthBefore, "Stream must have 2 entries before the cycle" );

        // Mock lookup service to throw the 4xx rejection on the bulk call
        Mock<ISpotifyBulkLookupService> lookupServiceMock = new( );
        _ = lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new SpotifyBulkRejectedException( 400, null, SupportedProviders.Spotify ) );

        // The handoff now writes atomically through Redis; the generic queue remains required by
        // the service for unrelated DLQ paths but is not involved in rejection forwarding.
        Mock<IRequestQueue<QueuedLookupRequest>> requestQueueMock = new( );

        // Wire up remaining mocks
        Mock<IRateLimitTracker> rateLimitTrackerMock = new( );
        _ = rateLimitTrackerMock.Setup( t => t.GetStateAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new RateLimitState( false, null, null ) );

        Mock<ISagaStateManager> sagaManagerMock = new( );

        SpotifyBatchQueueHelper helper = new(
            s_redis,
            new Mock<ILogger<SpotifyBatchQueueHelper>>( ).Object );
        await helper.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        IOptions<SpotifyBatchSettings> options = Options.Create(
            new SpotifyBatchSettings { LingerMs = 500 } );

        // Use a real Redis connection for the service so AcknowledgeAsync hits the real stream
        Mock<IConnectionMultiplexer> redisMock = new( );
        Mock<ISubscriber> subscriberMock = new( );
        _ = redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) )
            .Returns( subscriberMock.Object );
        _ = subscriberMock.Setup( s => s.PublishAsync(
                It.IsAny<RedisChannel>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        // The service uses the real Redis (s_redis) through the helper for stream operations;
        // we pass s_redis directly so AcknowledgeAsync operates on the real stream.
        SpotifyBulkProcessorService service = new(
            s_redis,
            helper,
            rateLimitTrackerMock.Object,
            sagaManagerMock.Object,
            lookupServiceMock.Object,
            requestQueueMock.Object,
            new Mock<ILogger<SpotifyBulkProcessorService>>( ).Object,
            options,
            Options.Create( new QueueSettings( ) ) );

        // Act — calling ProcessBulkTrackLookupsAsync directly bypasses the linger/threshold gate in
        // ShouldProcessBulkTracksAsync, so both seeded entries are processed unconditionally.
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert (a): bulk stream drains to 0 — original entries were acknowledged and deleted
        long depthAfter = await db.StreamLengthAsync( TrackStream );
        Assert.AreEqual( 0L, depthAfter,
            "Bulk track stream must be empty after the 4xx rejection cycle — original entries must be acknowledged" );

        // Assert (b): exactly two items were atomically forwarded to the single-item background stream.
        StreamEntry[] background = await db.StreamRangeAsync( SpotifyConstants.BackgroundStream );
        Assert.HasCount( 2, background,
            "Both items must be forwarded as individual background lookups" );
        List<QueuedLookupRequest?> forwarded = [.. background
            .Select( entry => JsonSerializer.Deserialize<QueuedLookupRequest>(
                entry[QueueStreamFieldNames.Payload].ToString( ), s_jsonOptions ) )];
        Assert.IsTrue(
            forwarded.Any( request => request?.LookupValue == TrackId1 ) &&
            forwarded.Any( request => request?.LookupValue == TrackId2 ),
            "Both track IDs must appear in the re-enqueued items" );
        Assert.IsTrue( forwarded.All( request => request?.EnqueueOrigin == QueueEnqueueOrigin.Requeue ) );
        Assert.IsTrue( forwarded.All( request => request?.BypassBulkRouting == true ) );
        requestQueueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );
    }

    /// <summary>
    /// A delivery left pending in this consumer's PEL is returned on the next dequeue without
    /// waiting for the aged XAUTOCLAIM threshold; this is the recovery path after a lost XACK.
    /// </summary>
    [TestMethod]
    public async Task BulkTrackOwnPelEntry_IsRecoveredBeforeAutoClaimIdleThreshold( ) {
        IDatabase db = s_redis!.GetDatabase( );
        const string TrackId = "spotifyOwnPelRecoveryTrack";
        QueuedLookupRequest request = new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = TrackId,
            SagaId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            OriginPriority = QueuePriority.Bulk,
            AttemptCount = 0
        };
        _ = await db.StreamAddAsync( SpotifyConstants.BulkTrackIdStream, [
            new NameValueEntry( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request, s_jsonOptions ) ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ] );

        SpotifyBatchQueueHelper helper = new(
            s_redis,
            new Mock<ILogger<SpotifyBatchQueueHelper>>( ).Object );
        await helper.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> first =
            await helper.DequeueTrackIdBatchAsync( 1, TestContext.CancellationToken );
        Assert.HasCount( 1, first );

        // Do not ACK: this is the lost-ACK state. The entry remains owned by this helper's consumer.
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> recovered =
            await helper.DequeueTrackIdBatchAsync( 1, TestContext.CancellationToken );

        Assert.HasCount( 1, recovered );
        Assert.AreEqual( first[0].MessageId, recovered[0].MessageId );
    }

    /// <summary>A repeated rejection handoff cannot create a second interactive replacement.</summary>
    [TestMethod]
    public async Task ForwardToBackground_RepeatedForSameSource_IsIdempotent( ) {
        IDatabase db = s_redis!.GetDatabase( );
        QueuedLookupRequest request = new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = "spotifyAtomicHandoffTrack",
            SagaId = "dddddddddddddddddddddddddddddddd",
            OriginPriority = QueuePriority.Bulk
        };
        _ = await db.StreamAddAsync( SpotifyConstants.BulkTrackIdStream, [
            new NameValueEntry( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request, s_jsonOptions ) ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ] );
        SpotifyBatchQueueHelper helper = new(
            s_redis!, new Mock<ILogger<SpotifyBatchQueueHelper>>( ).Object );
        await helper.EnsureConsumerGroupsAsync( TestContext.CancellationToken );
        QueuedMessage<QueuedLookupRequest> message = (await helper.DequeueTrackIdBatchAsync(
            1, TestContext.CancellationToken )).Single( );

        bool first = await helper.ForwardToSingleItemAsync( message, TestContext.CancellationToken );
        bool repeated = await helper.ForwardToSingleItemAsync( message, TestContext.CancellationToken );

        Assert.IsTrue( first );
        Assert.IsFalse( repeated );
        Assert.AreEqual( 0L, await db.StreamLengthAsync( SpotifyConstants.BulkTrackIdStream ) );
        Assert.AreEqual( 1L, await db.StreamLengthAsync( SpotifyConstants.BackgroundStream ) );
    }
}
