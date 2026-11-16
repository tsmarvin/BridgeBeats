using System.Reflection;
using System.Text.Json;
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
/// Unit tests pinning the dispatch contract of <see cref="SpotifyBulkProcessorService"/> and its
/// <see cref="SpotifyBatchQueueHelper"/>. Covers the saga-write discipline (a request-level failure,
/// represented by an empty result dictionary or an absent key, requeues without writing saga state,
/// whereas a genuine not-found — key present with a null value — writes an <c>IsSuccess=false</c>
/// state and acknowledges without requeue), the retry-cap semantics of <c>RequeueAsync</c> and the
/// complete-failed completion publishes at the cap, poison-entry handling (empty or null-JSON
/// payloads are acknowledged and deleted), the <c>XAUTOCLAIM</c> reclaim path, rate-limit handling
/// (mark partial, merge rate-limit info, publish the sentinel), the canonical <c>LookupKeyBuilder</c>
/// key formats and cross-producer saga-id alignment, the exponential cooldown computation, and the
/// album path's parity with the track path.
/// </summary>
[TestClass]
public class SpotifyBulkDispatchContractTests {

    /// <summary>Mock Redis multiplexer supplying the database and subscriber.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    /// <summary>Mock Redis database backing all stream operations.</summary>
    private Mock<IDatabase> _dbMock = null!;
    /// <summary>Mock Redis subscriber used to assert completion and sentinel publishes.</summary>
    private Mock<ISubscriber> _subscriberMock = null!;
    /// <summary>Mock rate-limit tracker the service consults and updates.</summary>
    private Mock<IRateLimitTracker> _rateLimitTrackerMock = null!;
    /// <summary>Mock saga state manager used to assert saga create/update behavior.</summary>
    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    /// <summary>Mock bulk lookup service supplying batch track/album results.</summary>
    private Mock<ISpotifyBulkLookupService> _lookupServiceMock = null!;
    /// <summary>Mock request queue used by unrelated generic queue paths.</summary>
    private Mock<IRequestQueue<QueuedLookupRequest>> _requestQueueMock = null!;
    /// <summary>Mock logger for the batch queue helper.</summary>
    private Mock<ILogger<SpotifyBatchQueueHelper>> _helperLoggerMock = null!;
    /// <summary>Mock logger for the bulk processor service.</summary>
    private Mock<ILogger<SpotifyBulkProcessorService>> _serviceLoggerMock = null!;

    /// <summary>Serialization options (camel-case, non-indented) used to build and read stream payloads.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Redis stream key for bulk Spotify track-id lookups.</summary>
    private const string TrackStream = SpotifyConstants.BulkTrackIdStream;
    /// <summary>Redis stream key for bulk Spotify album-id lookups.</summary>
    private const string AlbumStream = SpotifyConstants.BulkAlbumIdStream;
    /// <summary>Sample Spotify track id used throughout the tests.</summary>
    private const string TestTrackId = "3n3Ppam7vgaVa1iaRUc9Lp";
    /// <summary>Fixed saga id the mocks return for the sample track.</summary>
    private const string TestSagaId = "aabbccddeeff00112233445566778899";

    /// <summary>MSTest-injected context; its cancellation token bounds the async operations under test.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Two helper instances retain independent full GUID suffixes, even on a long host name.</summary>
    [TestMethod]
    public void Helpers_UseDistinctUntruncatedConsumerIds( ) {
        SpotifyBatchQueueHelper first = new( _redisMock.Object, _helperLoggerMock.Object );
        SpotifyBatchQueueHelper second = new( _redisMock.Object, _helperLoggerMock.Object );

        Assert.AreNotEqual( first.ConsumerId, second.ConsumerId );
        Assert.IsGreaterThan( 32, first.ConsumerId.Length );
        Assert.IsGreaterThan( 32, second.ConsumerId.Length );
    }

    /// <summary>The bulk consumer honors the same isolated key prefix as its producer.</summary>
    [TestMethod]
    public async Task Helper_WithKeyPrefix_RepairsOnlyPrefixedBulkStreams( ) {
        SpotifyBatchQueueHelper helper = new(
            _redisMock.Object,
            _helperLoggerMock.Object,
            keyPrefix: "isolated" );

        await helper.EnsureConsumerGroupsAsync( TestContext.CancellationToken );

        _dbMock.Verify( database => database.StreamCreateConsumerGroupAsync(
            It.Is<RedisKey>( key => key == "queue:isolated:spotify:bulk:track-id"
                || key == "queue:isolated:spotify:bulk:album-id" ),
            It.IsAny<RedisValue>( ),
            It.IsAny<RedisValue>( ),
            true,
            It.IsAny<CommandFlags>( ) ), Times.Exactly( 2 ) );
        _dbMock.Verify( database => database.StreamCreateConsumerGroupAsync(
            It.Is<RedisKey>( key => key == TrackStream || key == AlbumStream ),
            It.IsAny<RedisValue>( ),
            It.IsAny<RedisValue>( ),
            It.IsAny<bool>( ),
            It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>
    /// Creates fresh mocks before each test and wires their defaults: Redis database and subscriber
    /// resolution, a not-rate-limited tracker, a track stream sized at the batch maximum with a
    /// single sample entry, stream group/ack/range/add operations, and saga manager methods that
    /// return a saga for the sample track and complete successfully.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _dbMock = new Mock<IDatabase>( );
        _subscriberMock = new Mock<ISubscriber>( );
        _rateLimitTrackerMock = new Mock<IRateLimitTracker>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _lookupServiceMock = new Mock<ISpotifyBulkLookupService>( );
        _requestQueueMock = new Mock<IRequestQueue<QueuedLookupRequest>>( );
        _helperLoggerMock = new Mock<ILogger<SpotifyBatchQueueHelper>>( );
        _serviceLoggerMock = new Mock<ILogger<SpotifyBulkProcessorService>>( );

        // EnqueueAsync succeeds by default (no-op for the fallback path mock)
        _ = _requestQueueMock.Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        _ = _redisMock.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( _dbMock.Object );
        _ = _redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) )
            .Returns( _subscriberMock.Object );

        // Not rate-limited by default
        _ = _rateLimitTrackerMock.Setup( t => t.GetStateAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new RateLimitState( false, null, null ) );

        // Track stream: 50 messages (size threshold met — no linger check needed)
        _ = _dbMock.Setup( d => d.StreamLengthAsync( TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxTracksPerBatchLookup );
        // Album stream: 0 (won't trigger album processing)
        _ = _dbMock.Setup( d => d.StreamLengthAsync( AlbumStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        // XAUTOCLAIM returns no claimed entries
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<long>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( StreamAutoClaimResult.Null );

        // XREADGROUP returns one track message by default (8-param overload: includes TimeSpan? for idle time)
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [BuildStreamEntry( TestTrackId )] );

        // XREADGROUP for album stream: empty (8-param overload)
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                AlbumStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        // EnsureConsumerGroupsAsync — BUSYGROUP exception (group exists)
        _ = _dbMock.Setup( d => d.StreamCreateConsumerGroupAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<bool>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "BUSYGROUP" ) );

        // RequeueAsync reads the original message by StreamRangeAsync
        // Return the same entry so RequeueAsync can read AttemptCount
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [BuildStreamEntry( TestTrackId )] );

        // XACK — single-ID overload (used by both AcknowledgeAsync and RequeueAsync)
        _ = _dbMock.Setup( d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 1L );
        // XACK — batch-ID overload (defensive stub)
        _ = _dbMock.Setup( d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue[]>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 1L );
        _ = _dbMock.Setup( d => d.StreamDeleteAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue[]>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 1L );

        // XADD (for requeue) returns a new entry ID
        _ = _dbMock.Setup( d => d.StreamAddAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<NameValueEntry[]>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<long?>( ),
                It.IsAny<bool>( ),
                It.IsAny<long?>( ),
                It.IsAny<StreamTrimMode>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)"9999999999-0" );

        // Atomic bulk-rejection transfer returns the interactive replacement id.
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"9999999999-1" ) );

        // Subscriber PublishAsync succeeds
        _ = _subscriberMock.Setup( s => s.PublishAsync(
                It.IsAny<RedisChannel>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        // Saga manager defaults
        _ = _sagaManagerMock.Setup( m => m.GetOrCreateAsync(
                It.IsAny<string>( ),
                It.IsAny<string>( ),
                It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ),
                It.IsAny<QueuePriority>( ),
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
        _ = _sagaManagerMock.Setup( m => m.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( m => m.TrySetIsPartialAsync(
                It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( m => m.TrySetRateLimitInfoAsync(
                It.IsAny<string>( ), It.IsAny<List<ProviderRateLimitInfo>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
    }

    /// <summary>
    /// Verifies that when the lookup service returns an empty dictionary (a whole-request failure),
    /// every message is acknowledged and requeued but no saga state is written: a request failure
    /// must not be recorded as a per-track outcome.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenLookupServiceReturnsEmptyDict_ShouldRequeueAllWithoutSagaWrite( ) {
        // Arrange — lookup service returns empty dict (request failure)
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBulkProcessorService service = CreateService( );

        // Act — call the internal method directly (InternalsVisibleTo in Worker.Spotify.csproj)
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRequeue( TrackStream, Times.AtLeastOnce( ) );

        // Assert — no saga state was written (no UpdateProviderStateAsync calls)
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            It.IsAny<string>( ),
            It.IsAny<ProviderLookupState>( ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Empty-dict (request failure) must not write saga state for any message" );

        // Negative control: the 5xx/empty-dict path must NOT re-enqueue via the Interactive queue
        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ),
            QueuePriority.Interactive,
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Empty-dict (transient request failure) must not trigger Interactive re-enqueue; only 4xx does" );
    }

    /// <summary>
    /// Verifies that when the result dictionary is present but the requested track's key is absent
    /// (an inconclusive result), the message is acknowledged and requeued without writing saga
    /// state: only a key-present null value counts as a genuine not-found.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenLookupServiceReturnsAbsentKey_ShouldRequeueOneWithoutSagaWrite( ) {
        // Arrange — non-empty dict but our TestTrackId is absent (partial API parse)
        Dictionary<string, MusicLookupResult?> results = new( ) {
            ["someOtherTrackId"] = null
        };
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( results );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRequeue( TrackStream, Times.AtLeastOnce( ) );

        // Assert — no saga update for the absent-key message
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            It.IsAny<string>( ),
            It.IsAny<ProviderLookupState>( ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Absent-key message must not write saga state" );
    }

    /// <summary>
    /// Verifies the genuine not-found contract: when the requested track's key is present with a
    /// null value, the service writes a complete <c>IsSuccess=false</c> provider state, acknowledges
    /// the message once, and does not requeue it.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenKeyPresentWithNullValue_ShouldWriteNotFoundSagaState( ) {
        // Arrange — key present, null value = genuine not-found
        Dictionary<string, MusicLookupResult?> results = new( ) {
            [TestTrackId] = null
        };
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( results );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert — saga was written with IsSuccess=false (not-found)
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            TestSagaId,
            It.Is<ProviderLookupState>( s =>
                s.Provider == SupportedProviders.Spotify &&
                s.IsComplete &&
                !s.IsSuccess ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Genuine not-found (key present, null value) must write IsSuccess=false saga state" );

        VerifyAtomicRemoval( TrackStream, Times.Once( ) );

        // Assert — no XADD (not requeued)
        _dbMock.Verify( d => d.StreamAddAsync(
            (RedisKey)TrackStream,
            It.IsAny<NameValueEntry[]>( ),
            It.IsAny<RedisValue?>( ),
            It.IsAny<long?>( ),
            It.IsAny<bool>( ),
            It.IsAny<long?>( ),
            It.IsAny<StreamTrimMode>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "Genuine not-found must not be requeued" );
    }

    /// <summary>Bulk admission drops a delivery fenced to a replaced saga instance.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_ReplacedSaga_AcknowledgesStaleDeliveryWithoutMutation( ) {
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = null } );
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = TestSagaId,
                LookupKey = "other-root",
                LookupType = LookupRequestType.UpcLookup,
                LookupValue = "OTHER",
                InstanceToken = "replacement-instance",
                ProviderStates = []
            } );
        _ = _requestQueueMock.Setup( queue => queue.MoveToDlqAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRemoval( TrackStream, Times.Once( ) );
        _requestQueueMock.Verify( queue => queue.MoveToDlqAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A lost saga-instance CAS acknowledges and drops the bulk message without retry or publication.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenProviderStateCasIsLost_AcknowledgesAndDrops( ) {
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = null } );
        _ = _sagaManagerMock.Setup( manager => manager.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRemoval( TrackStream, Times.Once( ) );
        _requestQueueMock.Verify( queue => queue.MoveToDlqAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _requestQueueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _subscriberMock.Verify( subscriber => subscriber.PublishAsync(
            It.IsAny<RedisChannel>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>A lost saga-instance CAS whose stale-delivery ACK also fails escapes retry mutation.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenProviderStateCasIsLostAndAckFails_DoesNotRequeue( ) {
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = null } );
        _ = _sagaManagerMock.Setup( manager => manager.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );
        _ = _dbMock.Setup( database => database.ScriptEvaluateAsync(
                It.IsAny<string>( ),
                It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 1 && keys[0] == TrackStream ),
                It.Is<RedisValue[]?>( arguments => arguments != null && arguments.Length == 2 ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "atomic acknowledgement unavailable" ) );

        Exception failure = await Assert.ThrowsAsync<Exception>(
            ( ) => CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken ) );
        StringAssert.Contains( failure.Message, "Acknowledgement failed after provider state commit" );

        _requestQueueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _requestQueueMock.Verify( queue => queue.MoveToDlqAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>Initial and completed-leg recovery ACK failures propagate without cap mutation.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenInitialAndRecoveryAckFail_DoesNotRequeueCompletedLeg( ) {
        bool committed = false;
        LookupSagaState activeSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = TestTrackId,
            InstanceToken = "test-instance"
        };
        LookupSagaState completedSaga = activeSaga with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null )
            }
        };
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => committed ? completedSaga : activeSaga );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = null } );
        _ = _sagaManagerMock.Setup( manager => manager.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => committed = true )
            .ReturnsAsync( true );
        _ = _dbMock.Setup( database => database.ScriptEvaluateAsync(
                It.IsAny<string>( ),
                It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 1 && keys[0] == TrackStream ),
                It.Is<RedisValue[]?>( arguments => arguments != null && arguments.Length == 2 ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "atomic acknowledgement unavailable" ) );

        SpotifyBulkProcessorService service = CreateService( );
        Exception firstFailure = await Assert.ThrowsAsync<Exception>(
            ( ) => service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken ) );
        StringAssert.Contains( firstFailure.Message, "Acknowledgement failed after provider state commit" );
        Exception recoveryFailure = await Assert.ThrowsAsync<Exception>(
            ( ) => service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken ) );
        StringAssert.Contains( recoveryFailure.Message, "Acknowledgement failed after provider state commit" );

        // The batch API is requested again on redelivery; the completed-leg guard must prevent
        // that result from mutating saga state or entering the retry path.
        _lookupServiceMock.Verify( lookup => lookup.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ), Times.Exactly( 2 ) );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _requestQueueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _requestQueueMock.Verify( queue => queue.MoveToDlqAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A progress-publish failure after a successful saga write never enters retry/cap mutation.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenProgressPublishFails_DoesNotOverwriteCommittedSuccess( ) {
        MusicLookupResult result = new( ) { ExternalId = TestTrackId, IsAlbum = false };
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = result } );
        _ = _subscriberMock.Setup( subscriber => subscriber.PublishAsync(
                RedisChannel.Literal( "saga:completed" ),
                TestSagaId,
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisConnectionException( ConnectionFailureType.UnableToResolvePhysicalConnection, "publish unavailable" ) );

        _ = await Assert.ThrowsExactlyAsync<PostCommitAcknowledgementException>(
            ( ) => CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken ) );

        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            TestSagaId,
            It.Is<ProviderLookupState>( state => state.IsComplete && state.IsSuccess ),
            "test-instance",
            It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            It.IsAny<string>( ),
            It.Is<ProviderLookupState>( state => state.IsComplete && !state.IsSuccess && state.ErrorMessage != null ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ), Times.Never );
        VerifyAtomicRequeue( TrackStream, Times.Never( ) );
        VerifyAtomicRemoval( TrackStream, Times.Never( ) );
    }

    /// <summary>A refresh-native miss resolves its ISRC fallback inside the original work item.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_NativeNotFoundWithFallback_ResolvesInline( ) {
        QueuedLookupRequest request = CreateRequest( TestTrackId ) with {
            FallbackLookupType = LookupRequestType.IsrcLookup,
            FallbackLookupValue = "USRC12345678"
        };
        StreamEntry entry = BuildStreamEntryFromRequest( request );
        _ = _dbMock.Setup( database => database.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { [TestTrackId] = null } );
        _ = _lookupServiceMock.Setup( service => service.GetInfoByISRCAsync( "USRC12345678" ) )
            .ReturnsAsync( new MusicLookupResult { ExternalId = "USRC12345678", IsAlbum = false } );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        _lookupServiceMock.Verify( service => service.GetInfoByISRCAsync( "USRC12345678" ), Times.Once );
        _requestQueueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            TestSagaId,
            It.Is<ProviderLookupState>( state => state.IsComplete && state.IsSuccess ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ), Times.Once );
        VerifyAtomicRemoval( TrackStream, Times.Once( ) );
    }

    /// <summary>
    /// Verifies that <c>ShouldFlush</c> returns true when the count exceeds the size threshold.
    /// </summary>
    [TestMethod]
    public void ShouldFlush_WhenCountExceedsThreshold_ShouldReturnTrue( ) {
        bool result = SpotifyBulkProcessorService.ShouldFlush(
            count: 51,
            oldestAge: null,
            threshold: 50,
            linger: TimeSpan.FromMilliseconds( 500 )
        );
        Assert.IsTrue( result, "count > threshold must return true" );
    }

    /// <summary>
    /// Verifies that <c>ShouldFlush</c> returns true when the oldest entry's age equals the linger,
    /// confirming the inclusive (<c>&gt;=</c>) age boundary.
    /// </summary>
    [TestMethod]
    public void ShouldFlush_WhenAgeEqualsLinger_ShouldReturnTrue( ) {
        bool result = SpotifyBulkProcessorService.ShouldFlush(
            count: 1,
            oldestAge: TimeSpan.FromMilliseconds( 500 ),
            threshold: 50,
            linger: TimeSpan.FromMilliseconds( 500 )
        );
        Assert.IsTrue( result, "age == linger must trigger flush (>= boundary)" );
    }

    /// <summary>
    /// Verifies that requeuing a message with attempt count zero re-adds it with the attempt count
    /// incremented to one: the outcome is <c>Requeued</c>, the stream is added to once, and the
    /// re-serialized payload carries <c>AttemptCount = 1</c>.
    /// </summary>
    [TestMethod]
    public async Task RequeueAsync_WhenAttemptCountIsZero_ShouldRequeueWithAttemptCountOne( ) {
        // Arrange — stream has a message with AttemptCount=0 (fresh message)
        QueuedLookupRequest request = CreateRequest( TestTrackId, attemptCount: 0 );
        StreamEntry entry = BuildStreamEntryFromRequest( request );

        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );

        RedisValue[]? capturedArguments = null;
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback( ( string _, RedisKey[]? _, RedisValue[]? arguments, CommandFlags _ ) => capturedArguments = arguments )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"9999-0" ) );

        SpotifyBatchQueueHelper helper = CreateHelper( );
        string compositeId = $"{TrackStream}:1234567890-0";

        // Act — returns Requeued (not capped, not not-found)
        RequeueOutcome outcome = await helper.RequeueAsync( compositeId, TestSagaId, TestContext.CancellationToken );

        // Assert — method signals "requeued"
        Assert.AreEqual( RequeueOutcome.Requeued, outcome, "Return value must be Requeued (message re-added, not capped)" );

        VerifyAtomicRequeue( TrackStream, Times.Once( ) );

        // Assert — re-serialized payload has AttemptCount=1
        Assert.IsNotNull( capturedArguments, "The atomic requeue script must receive the replacement payload" );
        string? payloadJson = capturedArguments[3];
        Assert.IsNotNull( payloadJson );
        QueuedLookupRequest? requeuedPayload = JsonSerializer.Deserialize<QueuedLookupRequest>( payloadJson, s_jsonOptions );
        Assert.IsNotNull( requeuedPayload );
        Assert.AreEqual( 1, requeuedPayload.AttemptCount, "AttemptCount must be incremented to 1 on first requeue" );
    }

    /// <summary>A failed replacement XADD leaves the original Spotify delivery pending.</summary>
    [TestMethod]
    public async Task RequeueAsync_WhenReplacementAddFails_DoesNotAcknowledgeOriginal( ) {
        QueuedLookupRequest request = CreateRequest( TestTrackId, attemptCount: 0 );
        StreamEntry entry = BuildStreamEntryFromRequest( request );
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ),
                It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( [entry] );
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) ).ThrowsAsync( new RedisServerException( "atomic move failed" ) );

        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => CreateHelper( ).RequeueAsync( $"{TrackStream}:1234567890-0", TestSagaId, TestContext.CancellationToken ) );

    }

    /// <summary>
    /// Verifies that requeuing a message already at the retry cap returns <c>CapReached</c> without
    /// removing its last recovery delivery; the caller must first record terminal saga state.
    /// </summary>
    [TestMethod]
    public async Task RequeueAsync_WhenAttemptCountAtCap_ShouldLeaveDeliveryPending( ) {
        // Arrange — the fifth and final execution carries AttemptCount=4 (attempts 0..4).
        QueuedLookupRequest request = CreateRequest( TestTrackId, attemptCount: LookupConstants.MaxQueueRetryAttempts - 1 );
        StreamEntry entry = BuildStreamEntryFromRequest( request );

        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );

        SpotifyBatchQueueHelper helper = CreateHelper( );
        string compositeId = $"{TrackStream}:1234567890-0";

        // Act — returns CapReached
        RequeueOutcome outcome = await helper.RequeueAsync( compositeId, TestSagaId, TestContext.CancellationToken );

        // Assert — method signals "gave up"
        Assert.AreEqual( RequeueOutcome.CapReached, outcome, "Return value must be CapReached when the retry cap is reached" );

        _dbMock.Verify( d => d.ScriptEvaluateAsync(
            It.IsAny<string>( ),
            It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 1 && keys[0] == TrackStream ),
            It.Is<RedisValue[]?>( args => args != null && args.Length == 2 ),
            It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>The fourth execution (AttemptCount=3) is still replaceable and produces AttemptCount=4.</summary>
    [TestMethod]
    public async Task RequeueAsync_WhenAttemptCountIsThree_ShouldRequeueWithAttemptCountFour( ) {
        QueuedLookupRequest request = CreateRequest( TestTrackId, attemptCount: LookupConstants.MaxQueueRetryAttempts - 2 );
        StreamEntry entry = BuildStreamEntryFromRequest( request );
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<int?>( ),
                It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( [entry] );
        RedisValue[]? capturedArguments = null;
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback( ( string _, RedisKey[]? _, RedisValue[]? arguments, CommandFlags _ ) => capturedArguments = arguments )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"9999-0" ) );

        RequeueOutcome outcome = await CreateHelper( ).RequeueAsync(
            $"{TrackStream}:1234567890-0", TestSagaId, TestContext.CancellationToken );

        Assert.AreEqual( RequeueOutcome.Requeued, outcome );
        VerifyAtomicRequeue( TrackStream, Times.Once( ) );
        string payload = (string)capturedArguments![3]!;
        QueuedLookupRequest requeued = JsonSerializer.Deserialize<QueuedLookupRequest>( payload, s_jsonOptions )!;
        Assert.AreEqual( LookupConstants.MaxQueueRetryAttempts - 1, requeued.AttemptCount );
    }

    /// <summary>A failed terminal saga write leaves the retry-cap delivery available for recovery.</summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenTerminalStateWriteFails_ShouldLeaveCapDeliveryPending( ) {
        QueuedLookupRequest request = CreateRequest(
            TestTrackId,
            attemptCount: LookupConstants.MaxQueueRetryAttempts - 1 );
        StreamEntry entry = BuildStreamEntryFromRequest( request );
        _ = _dbMock.Setup( database => database.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _dbMock.Setup( database => database.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );
        _ = _lookupServiceMock.Setup( service => service.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( [] );
        _ = _sagaManagerMock.Setup( manager => manager.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new RedisConnectionException( ConnectionFailureType.UnableToResolvePhysicalConnection, "state unavailable" ) );

        await CreateService( ).ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRemoval( TrackStream, Times.Never( ) );
        _requestQueueMock.Verify( queue => queue.MoveToDlqAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies the end-to-end cap path: when a dequeued message is already at the max retry count
    /// and the lookup remains inconclusive, the service writes an <c>IsSuccess=false</c> provider
    /// state with a "Bulk lookup failed after" message and publishes both the <c>saga:completed</c>
    /// and the per-lookup-key <c>complete:</c> notifications.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenAttemptCountAtCap_ShouldWriteCompleteFailedStateAndPublishCompletion( ) {
        // Arrange — message at retry cap (AttemptCount = MaxQueueRetryAttempts)
        QueuedLookupRequest request = CreateRequest( TestTrackId, attemptCount: LookupConstants.MaxQueueRetryAttempts );
        StreamEntry capEntry = BuildStreamEntryFromRequest( request );

        string expectedLookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}";

        // XREADGROUP returns the at-cap message
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [capEntry] );

        // RequeueAsync reads the original entry via StreamRangeAsync — returns the cap entry
        // so the helper reads AttemptCount = MaxQueueRetryAttempts and returns false.
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [capEntry] );

        // Absent-key: non-empty dict that does NOT contain TestTrackId.
        // This routes the message through the absent-key branch → RequeueSingleAsync,
        // which hits the cap and executes the failed-state + publish path.
        // An empty dict would take the "request failure / requeue ALL" branch instead,
        // also reaching RequeueSingleAsync but via a different route — absent-key is
        // used here because it isolates the single-message cap path unambiguously.
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { ["someOtherTrackId"] = null } );

        // Saga returned by GetAsync: IsComplete=true (all providers done, no FinalResultUri)
        // so CheckAndPublishSagaCompletionAsync proceeds to publish saga:completed.
        LookupSagaState completedSaga = new( ) {
            SagaId = TestSagaId,
            LookupKey = expectedLookupKey,
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = TestTrackId,
            InstanceToken = "test-instance",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new ProviderLookupState(
                    Provider: SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: $"Bulk lookup failed after {LookupConstants.MaxQueueRetryAttempts} attempts" )
            },
            FinalResultUri = null
        };
        _ = _sagaManagerMock.Setup( m => m.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( completedSaga );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert (a): UpdateProviderStateAsync called with IsSuccess=false and the cap error message.
        // This is the service-level write that marks the saga provider as permanently failed.
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            TestSagaId,
            It.Is<ProviderLookupState>( s =>
                !s.IsSuccess &&
                s.ErrorMessage != null &&
                s.ErrorMessage.Contains( "Bulk lookup failed after" ) ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "RequeueSingleAsync cap path must write IsSuccess=false with 'Bulk lookup failed after' error" );

        // Assert (b1): saga-completed channel publish fires (CheckAndPublishSagaCompletionAsync).
        // This unblocks saga coordinators waiting on the saga:completed channel.
        _subscriberMock.Verify( s => s.PublishAsync(
            It.Is<RedisChannel>( ch => ch == RedisChannel.Literal( "saga:completed" ) ),
            It.IsAny<RedisValue>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Once,
            "saga:completed must be published when the retry cap is reached (F2)" );

        // The coordinator consumes the saga progress event and owns waiter publication after
        // authoritative finalization; the provider worker does not publish a speculative URI.
        _subscriberMock.Verify( s => s.PublishAsync(
            It.Is<RedisChannel>( ch => ch == RedisChannel.Literal( $"complete:{expectedLookupKey}" ) ),
            It.IsAny<RedisValue>( ),
            It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies the rate-limit handling contract: each affected saga is marked partial, the
    /// rate-limit info is merged into the saga with a Spotify provider entry, and a rate-limited
    /// sentinel is published so interactive callers are not left hanging.
    /// </summary>
    [TestMethod]
    public async Task HandleBulkRateLimit_WhenCalled_ShouldMarkSagasPartialAndPublishSentinel( ) {
        // Arrange — one queued message with known SagaId
        QueuedLookupRequest request = CreateRequest( TestTrackId );
        List<QueuedMessage<QueuedLookupRequest>> messages = [
            new QueuedMessage<QueuedLookupRequest>(
                $"{TrackStream}:1234567890-0",
                request,
                DateTimeOffset.UtcNow )
        ];

        RetryAfterExceededException ex = new(
            retryAfterValue: TimeSpan.FromSeconds( 30 ),
            threshold: TimeSpan.FromSeconds( 120 ),
            requestUri: null,
            provider: SupportedProviders.Spotify );

        _ = _rateLimitTrackerMock.Setup( t => t.SetRateLimitedAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<DateTimeOffset>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        SpotifyBulkProcessorService service = CreateService( );

        // Act — call internal method directly
        await service.HandleBulkRateLimitAsync( messages, SpotifyConstants.BulkTracksEndpoint, ex, TestContext.CancellationToken );

        // Assert — SetIsPartialAsync called for the saga
        _sagaManagerMock.Verify( m => m.TrySetIsPartialAsync(
            TestSagaId,
            true,
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Each rate-limited saga must be marked partial (parity with QueueProcessorBackgroundService rate-limit handling)" );

        // Assert — SetRateLimitInfoAsync called to merge rate-limit info into saga
        _sagaManagerMock.Verify( m => m.TrySetRateLimitInfoAsync(
            TestSagaId,
            It.Is<List<ProviderRateLimitInfo>>( list =>
                list.Any( r => r.Provider == SupportedProviders.Spotify ) ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ),
            Times.Once,
            "Rate-limit info must be merged into the saga with Spotify provider entry" );

        // Assert — sentinel published on the saga's completion channel
        _subscriberMock.Verify( s => s.PublishAsync(
            It.Is<RedisChannel>( c => c.ToString( ).Contains( TestTrackId ) ),
            LookupConstants.RateLimitedSentinel,
            It.IsAny<CommandFlags>( ) ),
            Times.Once,
            "Rate-limited sentinel must be published so interactive callers are not left hanging" );
    }

    /// <summary>
    /// Verifies that <c>TypedKey</c> produces the canonical <c>{LookupType}:{Provider}:{id}</c>
    /// format.
    /// </summary>
    [TestMethod]
    public void LookupKeyBuilder_TypedKey_ShouldMatchCanonicalFormat( ) {
        string key = LookupKeyBuilder.TypedKey(
            LookupRequestType.SongIdLookup,
            SupportedProviders.Spotify,
            TestTrackId
        );

        Assert.AreEqual(
            $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
            key,
            "TypedKey must produce '{LookupType}:{Provider}:{id}'" );
    }

    /// <summary>
    /// Verifies that <c>UrlKey</c> produces a key prefixed with <c>UriLookup:</c> and is
    /// deterministic for the same URL.
    /// </summary>
    [TestMethod]
    public void LookupKeyBuilder_UrlKey_ShouldMatchCanonicalFormat( ) {
        string url = "https://open.spotify.com/artist/6qqNVTkY8uBg9cP3Jd7DAH";
        string key = LookupKeyBuilder.UrlKey( url );

        // Key must start with UriLookup: prefix
        Assert.IsTrue(
            key.StartsWith( "UriLookup:", StringComparison.Ordinal ),
            "UrlKey must start with 'UriLookup:'" );

        // Key must be deterministic (same input → same output)
        string key2 = LookupKeyBuilder.UrlKey( url );
        Assert.AreEqual( key, key2, "UrlKey must be deterministic for the same URL" );
    }

    /// <summary>
    /// Verifies cross-producer saga-id alignment: the JetStream watcher, bulk processor, and queue
    /// processor each build the same typed key for the same track, so <c>GenerateSagaId</c> yields
    /// one identical saga id across all three.
    /// </summary>
    [TestMethod]
    public void LookupKeyBuilder_JetStreamAndBulkProcessor_ShouldProduceSameSagaIdForSameTrackId( ) {
        // JetStreamWatcher path: uses LookupKeyBuilder.TypedKey
        string jetStreamKey = LookupKeyBuilder.TypedKey(
            LookupRequestType.SongIdLookup,
            SupportedProviders.Spotify,
            TestTrackId );

        // SpotifyBulkProcessorService path: uses LookupKeyBuilder.TypedKey (ProcessBulkResultAsync)
        string bulkProcessorKey = LookupKeyBuilder.TypedKey(
            LookupRequestType.SongIdLookup,
            SupportedProviders.Spotify,
            TestTrackId );

        // QueueProcessorBackgroundService path: also uses LookupKeyBuilder.TypedKey
        string queueProcessorKey = LookupKeyBuilder.TypedKey(
            LookupRequestType.SongIdLookup,
            SupportedProviders.Spotify,
            TestTrackId );

        string sagaIdFromJetStream = ISagaStateManager.GenerateSagaId( jetStreamKey );
        string sagaIdFromBulkProcessor = ISagaStateManager.GenerateSagaId( bulkProcessorKey );
        string sagaIdFromQueueProcessor = ISagaStateManager.GenerateSagaId( queueProcessorKey );

        Assert.AreEqual( sagaIdFromJetStream, sagaIdFromBulkProcessor,
            "JetStreamWatcher and SpotifyBulkProcessorService must produce the same saga ID for the same track" );
        Assert.AreEqual( sagaIdFromBulkProcessor, sagaIdFromQueueProcessor,
            "SpotifyBulkProcessorService and QueueProcessorBackgroundService must produce the same saga ID for the same track" );
    }

    /// <summary>
    /// Verifies that a stream entry with no payload field is treated as poison: it is discarded (not
    /// returned), acknowledged to remove it from the pending list, and deleted so it cannot
    /// re-appear via <c>XAUTOCLAIM</c>.
    /// </summary>
    [TestMethod]
    public async Task DequeueBatch_WhenXReadGroupEntryHasEmptyPayload_ShouldAckAndDeletePoisonEntry( ) {
        // Arrange — XAUTOCLAIM returns empty; XREADGROUP returns one entry with no payload
        NameValueEntry[] emptyPayloadFields = [
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
            // Note: no "payload" field — simulates a corrupt / empty entry
        ];
        StreamEntry poisonEntry = new( (RedisValue)"1234567890-0", emptyPayloadFields );

        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [poisonEntry] );

        SpotifyBatchQueueHelper helper = CreateHelper( );

        // Act
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages =
            await helper.DequeueTrackIdBatchAsync( cancellationToken: TestContext.CancellationToken );

        // Assert — no messages returned (poison was discarded)
        Assert.IsEmpty( messages, "Poison entry must be discarded, not returned" );

        VerifyAtomicRemoval( TrackStream, Times.Once( ) );
    }

    /// <summary>
    /// Verifies that a stream entry whose payload field is the literal JSON <c>null</c> is likewise
    /// treated as poison: discarded, acknowledged, and deleted so it cannot re-appear via
    /// <c>XAUTOCLAIM</c>.
    /// </summary>
    [TestMethod]
    public async Task DequeueBatch_WhenXReadGroupEntryHasNullJsonPayload_ShouldAckAndDeletePoisonEntry( ) {
        // Arrange — XAUTOCLAIM returns empty (default); XREADGROUP returns one entry whose
        // payload field is the literal JSON string "null" (valid JSON, but deserializes to null).
        NameValueEntry[] nullPayloadFields = [
            new NameValueEntry( QueueStreamFieldNames.Payload, "null" ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ];
        StreamEntry poisonEntry = new( (RedisValue)"1234567890-1", nullPayloadFields );

        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [poisonEntry] );

        SpotifyBatchQueueHelper helper = CreateHelper( );

        // Act
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages =
            await helper.DequeueTrackIdBatchAsync( cancellationToken: TestContext.CancellationToken );

        // Assert — no messages returned (poison was discarded)
        Assert.IsEmpty( messages, "Null-JSON-payload poison entry must be discarded, not returned" );

        VerifyAtomicRemoval( TrackStream, Times.Once( ) );
    }

    /// <summary>
    /// Verifies that requeuing a composite id whose entry no longer exists returns <c>NotFound</c>
    /// and issues neither an acknowledge nor a re-add, leaving the saga untouched.
    /// </summary>
    [TestMethod]
    public async Task RequeueAsync_WhenEntryNotFound_ShouldReturnNotFoundWithoutSagaWrite( ) {
        // Arrange — StreamRangeAsync returns empty (entry already XDELed)
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBatchQueueHelper helper = CreateHelper( );
        string compositeId = $"{TrackStream}:1234567890-0";

        // Act
        RequeueOutcome outcome = await helper.RequeueAsync( compositeId, TestSagaId, TestContext.CancellationToken );

        // Assert — returns NotFound, not CapReached
        Assert.AreEqual( RequeueOutcome.NotFound, outcome,
            "Missing entry must return NotFound so the caller leaves the saga untouched" );

        // Assert — no XACK (nothing to ACK — entry is already gone)
        _dbMock.Verify( d => d.StreamAcknowledgeAsync(
            TrackStream,
            It.IsAny<RedisValue>( ),
            It.IsAny<RedisValue>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "No ACK should be issued when the entry is not found" );

        // Assert — no XADD (not re-queued)
        _dbMock.Verify( d => d.StreamAddAsync(
            (RedisKey)TrackStream,
            It.IsAny<NameValueEntry[]>( ),
            It.IsAny<RedisValue?>( ),
            It.IsAny<long?>( ),
            It.IsAny<bool>( ),
            It.IsAny<long?>( ),
            It.IsAny<StreamTrimMode>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "Entry not found must not be re-added" );
    }

    /// <summary>
    /// Verifies that when a requeue resolves to <c>NotFound</c> during track processing, the service
    /// writes no saga state: a vanished entry must not be recorded as an outcome.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenRequeueReturnsNotFound_ShouldNotWriteSagaState( ) {
        // Arrange — lookup service returns a non-empty dict that doesn't contain TestTrackId
        // so the absent-key branch calls RequeueSingleAsync. The stream entry is not found
        // so RequeueAsync returns NotFound.
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( new Dictionary<string, MusicLookupResult?> { ["otherTrackId"] = null } );

        // StreamRangeAsync returns empty — simulates entry already XDELed by another consumer.
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert — saga state must NOT be written when entry is not found
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies that <c>ComputeCooldownSeconds</c> returns a positive value for all tested
    /// consecutive-failure counts and clamps at <c>maxSeconds</c>.
    /// </summary>
    /// <remarks>
    /// Failure-first discipline: the pure function is tested directly.
    /// Before the overflow-protection fix, <c>1 &lt;&lt; (consecutiveFailures - 1)</c> would
    /// overflow int at n≈32, producing a negative product; the clamped formula prevents this.
    /// A test against the pre-fix formula would fail at n=32 or n=40 because the result
    /// would be negative, violating the "stays positive" assertion.
    /// </remarks>
    [TestMethod]
    public void ComputeCooldownSeconds_ForRepresentativeFailureCounts_ShouldStayPositiveAndClampAtMax( ) {
        const int BaseSeconds = 5;
        const int MaxSeconds = SpotifyBatchSettings.MaxRequestFailureCooldownSeconds;

        int[] testCases = [1, 2, 6, 32, 40];
        foreach (int n in testCases) {
            int result = SpotifyBulkProcessorService.ComputeCooldownSeconds( n, BaseSeconds, MaxSeconds );

            Assert.IsGreaterThan( 0, result,
                $"ComputeCooldownSeconds(n={n}) must be positive; got {result}" );
            Assert.IsLessThanOrEqualTo( MaxSeconds, result,
                $"ComputeCooldownSeconds(n={n}) must not exceed MaxSeconds ({MaxSeconds}); got {result}" );
        }

        // Boundary: n=6 is the first iteration where the exponent clamp at 5 takes effect.
        // base * 2^5 = 5 * 32 = 160 > MaxSeconds(60) → result must equal MaxSeconds.
        int atClamp = SpotifyBulkProcessorService.ComputeCooldownSeconds( 6, BaseSeconds, MaxSeconds );
        Assert.AreEqual( MaxSeconds, atClamp,
            "n=6 (exponent=5, first clamped value) must return exactly MaxSeconds" );
    }

    /// <summary>
    /// Builds a bulk processor service wired to the mocks and a batch settings instance with a
    /// 500&#160;ms linger.
    /// </summary>
    /// <returns>A service under test.</returns>
    private SpotifyBulkProcessorService CreateService( ) {
        SpotifyBatchQueueHelper helper = CreateHelper( );
        IOptions<SpotifyBatchSettings> options = Microsoft.Extensions.Options.Options.Create(
            new SpotifyBatchSettings { LingerMs = 500 } );

        return new SpotifyBulkProcessorService(
            _redisMock.Object,
            helper,
            _rateLimitTrackerMock.Object,
            _sagaManagerMock.Object,
            _lookupServiceMock.Object,
            _requestQueueMock.Object,
            _serviceLoggerMock.Object,
            options
        );
    }

    /// <summary>
    /// Builds a batch queue helper wired to Redis and the helper logger.
    /// </summary>
    /// <returns>A helper under test.</returns>
    private SpotifyBatchQueueHelper CreateHelper( ) =>
        new( _redisMock.Object, _helperLoggerMock.Object );

    /// <summary>
    /// Builds a Spotify song-id <c>QueuedLookupRequest</c> for the given track id and attempt count,
    /// carrying the fixed test saga id and bulk origin priority.
    /// </summary>
    /// <param name="trackId">The track id to look up.</param>
    /// <param name="attemptCount">The retry attempt count; defaults to zero.</param>
    /// <returns>A track lookup request.</returns>
    private static QueuedLookupRequest CreateRequest( string trackId, int attemptCount = 0 ) =>
        new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = trackId,
            SagaId = TestSagaId,
            SagaInstanceToken = "test-instance",
            IsAlbum = false,
            OriginPriority = QueuePriority.Bulk,
            AttemptCount = attemptCount
        };

    /// <summary>
    /// Builds a stream entry for the given track id by wrapping a freshly created request.
    /// </summary>
    /// <param name="trackId">The track id to embed.</param>
    /// <returns>A stream entry carrying the track request payload.</returns>
    private StreamEntry BuildStreamEntry( string trackId ) {
        QueuedLookupRequest request = CreateRequest( trackId );
        return BuildStreamEntryFromRequest( request );
    }

    /// <summary>
    /// Builds a stream entry whose <c>payload</c> field is the serialized request and whose
    /// <c>enqueuedAt</c> field is the current time.
    /// </summary>
    /// <param name="request">The request to embed as the entry payload.</param>
    /// <returns>A stream entry carrying the serialized request and an enqueue timestamp.</returns>
    private StreamEntry BuildStreamEntryFromRequest( QueuedLookupRequest request ) {
        string payload = JsonSerializer.Serialize( request, s_jsonOptions );
        NameValueEntry[] values = [
            new NameValueEntry( QueueStreamFieldNames.Payload, payload ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ];
        return new StreamEntry( (RedisValue)"1234567890-0", values );
    }

    /// <summary>
    /// Verifies that an entry reclaimed via <c>XAUTOCLAIM</c> (a stranded message from a dead
    /// consumer) is included in the dequeued batch, with its payload and preserved attempt count
    /// surfaced to the caller.
    /// </summary>
    [TestMethod]
    public async Task DequeueTrackIdBatch_WhenAutoClaimReturnsEntry_ShouldIncludeClaimedEntryInBatch( ) {
        // Arrange — XAUTOCLAIM returns one stranded entry; XREADGROUP returns empty
        QueuedLookupRequest strandedRequest = CreateRequest( TestTrackId, attemptCount: 1 );
        StreamEntry strandedEntry = BuildStreamEntryFromRequest( strandedRequest );

        // StreamAutoClaimResult has an internal constructor; invoke it via reflection.
        ConstructorInfo autoClaimCtor = typeof( StreamAutoClaimResult )
            .GetConstructors( BindingFlags.NonPublic | BindingFlags.Instance )[0];
        StreamAutoClaimResult autoClaimResult = (StreamAutoClaimResult)autoClaimCtor.Invoke(
            [(RedisValue)"0-0", new StreamEntry[] { strandedEntry }, Array.Empty<RedisValue>( )] );

        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<long>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( autoClaimResult );

        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBatchQueueHelper helper = CreateHelper( );

        // Act
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages =
            await helper.DequeueTrackIdBatchAsync( cancellationToken: TestContext.CancellationToken );

        // Assert — the claimed entry must appear in the returned batch
        Assert.HasCount( 1, messages,
            "Claimed entry from XAUTOCLAIM must be included in the returned batch" );
        Assert.AreEqual( TestTrackId, messages[0].Payload.LookupValue,
            "Returned message must match the claimed entry's payload" );
        Assert.AreEqual( 1, messages[0].Payload.AttemptCount,
            "AttemptCount from the claimed entry must be preserved" );
    }

    /// <summary>
    /// Verifies that when reclaimed entries already fill the requested count budget,
    /// the fresh <c>XREADGROUP &gt;</c> read is skipped; the own-consumer PEL probe remains bounded
    /// and is allowed before the aged reclaim pass.
    /// </summary>
    [TestMethod]
    public async Task DequeueTrackIdBatch_WhenAutoClaimFillsBudget_ShouldSkipXReadGroup( ) {
        // Arrange — request count=1; XAUTOCLAIM already fills the budget
        QueuedLookupRequest strandedRequest = CreateRequest( TestTrackId, attemptCount: 2 );
        StreamEntry strandedEntry = BuildStreamEntryFromRequest( strandedRequest );

        // StreamAutoClaimResult has an internal constructor; invoke it via reflection.
        ConstructorInfo autoClaimCtor2 = typeof( StreamAutoClaimResult )
            .GetConstructors( BindingFlags.NonPublic | BindingFlags.Instance )[0];
        StreamAutoClaimResult autoClaimResult2 = (StreamAutoClaimResult)autoClaimCtor2.Invoke(
            [(RedisValue)"0-0", new StreamEntry[] { strandedEntry }, Array.Empty<RedisValue>( )] );

        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<long>( ),
                It.IsAny<RedisValue>( ),
                1,        // count=1 passed to XAUTOCLAIM
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( autoClaimResult2 );

        SpotifyBatchQueueHelper helper = CreateHelper( );

        // Act — request exactly 1 message
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> messages =
            await helper.DequeueTrackIdBatchAsync( maxCount: 1, TestContext.CancellationToken );

        // Assert — exactly one message from the claimed entry
        Assert.HasCount( 1, messages, "Budget of 1 must be filled by the claimed entry alone" );

        // Assert — the fresh XREADGROUP > read was NOT called (budget exhausted by claimed entries)
        _dbMock.Verify( d => d.StreamReadGroupAsync(
            TrackStream,
            It.IsAny<RedisValue>( ),
            It.IsAny<RedisValue>( ),
            It.Is<RedisValue?>( position => position == null ),
            It.IsAny<int?>( ),
            It.IsAny<bool>( ),
            It.IsAny<TimeSpan?>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "XREADGROUP must not be called when the count budget is already filled by claimed entries" );
    }

    /// <summary>Spotify bulk XAUTOCLAIM advances its cursor so a later batch reaches the tail.</summary>
    [TestMethod]
    public async Task DequeueTrackIdBatch_WhenAutoClaimHasTail_AdvancesCursorAcrossCalls( ) {
        StreamEntry first = BuildStreamEntryFromRequest( CreateRequest( "spotify-young-prefix", attemptCount: 1 ) );
        StreamEntry tail = BuildStreamEntryFromRequest( CreateRequest( "spotify-aged-tail", attemptCount: 2 ) );
        ConstructorInfo ctor = typeof( StreamAutoClaimResult ).GetConstructors( BindingFlags.NonPublic | BindingFlags.Instance )[0];
        StreamAutoClaimResult firstPage = (StreamAutoClaimResult)ctor.Invoke( [(RedisValue)"100-0", new[] { first }, Array.Empty<RedisValue>( )] );
        StreamAutoClaimResult tailPage = (StreamAutoClaimResult)ctor.Invoke( [(RedisValue)"0-0", new[] { tail }, Array.Empty<RedisValue>( )] );
        List<RedisValue> starts = [];
        int calls = 0;
        _ = _dbMock.Setup( d => d.StreamAutoClaimAsync(
                TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<long>( ),
                It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, RedisValue, long, RedisValue, int?, CommandFlags>(
                ( _, _, _, _, start, _, _ ) => starts.Add( start ) )
            .ReturnsAsync( ( ) => Interlocked.Increment( ref calls ) == 1 ? firstPage : tailPage );
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream, It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ), It.IsAny<bool>( ), It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBatchQueueHelper helper = CreateHelper( );
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> firstBatch = await helper.DequeueTrackIdBatchAsync( 1, TestContext.CancellationToken );
        IReadOnlyList<QueuedMessage<QueuedLookupRequest>> tailBatch = await helper.DequeueTrackIdBatchAsync( 1, TestContext.CancellationToken );

        Assert.AreEqual( "spotify-young-prefix", firstBatch[0].Payload.LookupValue );
        Assert.AreEqual( "spotify-aged-tail", tailBatch[0].Payload.LookupValue );
        Assert.HasCount( 2, starts );
        Assert.AreEqual( (RedisValue)"0-0", starts[0] );
        Assert.AreEqual( (RedisValue)"100-0", starts[1] );
    }

    /// <summary>
    /// Verifies idempotent completion: when the saga already carries a final result URI, the service
    /// does not re-publish <c>saga:completed</c>, avoiding duplicate completion notifications for an
    /// already-finalized saga.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenSagaAlreadyHasFinalResultUri_ShouldSkipCompletionPublish( ) {
        // Arrange — lookup service returns a valid result for the track
        Dictionary<string, MusicLookupResult?> results = new( ) { [TestTrackId] = null };
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( results );

        // Saga already has a FinalResultUri — the completion has been published before.
        // IsComplete is computed from ProviderStates, so populate a completed Spotify entry.
        _ = _sagaManagerMock.Setup( m => m.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = TestSagaId,
                LookupKey = $"{LookupRequestType.SongIdLookup}:{SupportedProviders.Spotify}:{TestTrackId}",
                LookupType = LookupRequestType.SongIdLookup,
                LookupValue = TestTrackId,
                ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                    [SupportedProviders.Spotify] = new ProviderLookupState(
                        Provider: SupportedProviders.Spotify,
                        IsComplete: true,
                        IsSuccess: true,
                        ResultJson: null,
                        CompletedAt: DateTimeOffset.UtcNow,
                        ErrorMessage: null )
                },
                FinalResultUri = "https://bridgebeats.example/songs/abc"  // already finalized
            } );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert — saga:completed must NOT be published (FinalResultUri guard fires)
        _subscriberMock.Verify( s => s.PublishAsync(
            It.Is<RedisChannel>( c => c.ToString( ) == "saga:completed" ),
            It.IsAny<RedisValue>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "saga:completed must not be published when the saga already has a FinalResultUri" );
    }

    /// <summary>
    /// Verifies album-path parity with the track path: when the album lookup returns an empty
    /// dictionary (a request failure), album messages are acknowledged and requeued to the album
    /// stream without writing saga state.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkAlbums_WhenLookupServiceReturnsEmptyDict_ShouldRequeueAllWithoutSagaWrite( ) {
        const string TestAlbumId = "6WdSsBrH5QtofaTTqgwxOV";

        // Arrange — lookup service returns empty dict (request failure)
        _ = _lookupServiceMock.Setup( s => s.GetAlbumsByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( [] );

        // Album stream at threshold so ShouldProcessBulkAlbumsAsync fires
        _ = _dbMock.Setup( d => d.StreamLengthAsync( AlbumStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxAlbumsPerBatchLookup );
        // Track stream back to 0 so only the album path runs
        _ = _dbMock.Setup( d => d.StreamLengthAsync( TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        // Build an album-typed request
        QueuedLookupRequest albumRequest = new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.AlbumIdLookup,
            LookupValue = TestAlbumId,
            SagaId = TestSagaId,
            IsAlbum = true,
            OriginPriority = QueuePriority.Bulk,
            AttemptCount = 0
        };
        StreamEntry albumEntry = BuildStreamEntryFromRequest( albumRequest );

        // XREADGROUP for album stream returns one entry
        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                AlbumStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [albumEntry] );

        // RequeueAsync reads the original message by StreamRangeAsync for the album stream
        _ = _dbMock.Setup( d => d.StreamRangeAsync(
                AlbumStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ),
                It.IsAny<Order>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [albumEntry] );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkAlbumLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRequeue( AlbumStream, Times.AtLeastOnce( ) );

        // Assert — no saga state written (empty-dict = request failure, not not-found)
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );

        // Negative control: the empty-dict path must NOT re-enqueue via the Interactive queue
        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ),
            QueuePriority.Interactive,
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Empty-dict on the album path must not trigger Interactive re-enqueue" );
    }

    // ──── 4xx bulk-rejection tests ────────────────────────────────────────────

    /// <summary>
    /// Verifies the processor-tier contract for an auth failure represented as an empty-dict result:
    /// when <see cref="ISpotifyBulkLookupService.GetTracksByIdsAsync"/> returns an empty dictionary
    /// (the shape a 401 produces after the lookup service translates it), the bulk processor takes
    /// the cooldown-and-rebatch path (XADD) and does NOT re-enqueue at Interactive priority. A 401
    /// is a request-wide credential failure; re-routing individual items cannot resolve it.
    /// </summary>
    /// <remarks>
    /// This test pins the PROCESSOR-tier contract: that an empty-dict result routes to
    /// cooldown+rebatch, not Interactive re-enqueue. It stubs
    /// <see cref="ISpotifyBulkLookupService"/> at the interface boundary and does not drive
    /// the real HTTP status mapping inside <c>NewBulkMusicApiRequest</c>. The HTTP-level
    /// assertion that a 401 produces an empty dict (not a throw) is covered by
    /// <see cref="SpotifyBulkStatusMappingTests"/>.
    /// </remarks>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenLookupReturnsEmptyDict_BulkAuth401_ShouldTakeCooldownRebatchPathNotInteractive( ) {
        // Arrange — lookup service returns empty dict (simulates a 401 auth failure: NewBulkMusicApiRequest
        // returns null → GetTracksByIdsAsync returns empty dict, not SpotifyBulkRejectedException)
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRequeue( TrackStream, Times.AtLeastOnce( ) );

        // Negative control: Interactive re-enqueue must NOT fire for an auth failure
        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ),
            QueuePriority.Interactive,
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "A 401 auth failure (empty dict) must not trigger Interactive re-enqueue; only HTTP 400 does" );
    }

    /// <summary>
    /// Verifies the processor-tier contract for an authorization failure represented as an
    /// empty-dict result: when <see cref="ISpotifyBulkLookupService.GetTracksByIdsAsync"/> returns
    /// an empty dictionary (the shape a 403 produces after the lookup service translates it), the
    /// bulk processor takes the cooldown-and-rebatch path (XADD) and does NOT re-enqueue at
    /// Interactive priority.
    /// </summary>
    /// <remarks>
    /// This test pins the PROCESSOR-tier contract: that an empty-dict result routes to
    /// cooldown+rebatch, not Interactive re-enqueue. It stubs
    /// <see cref="ISpotifyBulkLookupService"/> at the interface boundary and does not drive
    /// the real HTTP status mapping inside <c>NewBulkMusicApiRequest</c>. The HTTP-level
    /// assertion that a 403 produces an empty dict (not a throw) is covered by
    /// <see cref="SpotifyBulkStatusMappingTests"/>.
    /// </remarks>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenLookupReturnsEmptyDict_BulkAuth403_ShouldTakeCooldownRebatchPathNotInteractive( ) {
        // Arrange — lookup service returns empty dict (simulates a 403 authorization failure)
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRequeue( TrackStream, Times.AtLeastOnce( ) );

        // Negative control: Interactive re-enqueue must NOT fire for an authorization failure
        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ),
            QueuePriority.Interactive,
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "A 403 authorization failure (empty dict) must not trigger Interactive re-enqueue; only HTTP 400 does" );
    }

    /// <summary>
    /// Verifies the 4xx track batch rejection contract: when the lookup service throws
    /// <see cref="SpotifyBulkRejectedException"/> (deterministic 4xx), every message is
    /// transferred individually to the Interactive stream in one Redis script. No saga state is
    /// written and no cooldown is armed.
    /// </summary>
    /// <remarks>Test was red before HandleBulkRejectionAsync existed; green after.</remarks>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenBulkRejected4xx_ShouldAtomicallyTransferAllToInteractive( ) {
        const int ExpectedCount = 1; // one message from default XREADGROUP setup

        // Arrange — lookup service throws the 4xx rejection exception
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new SpotifyBulkRejectedException( 400, null, SupportedProviders.Spotify ) );

        List<RedisKey[]?> capturedKeys = [];
        List<RedisValue[]?> capturedValues = [];
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback( ( string _, RedisKey[]? keys, RedisValue[]? values, CommandFlags _ ) => {
                capturedKeys.Add( keys );
                capturedValues.Add( values );
            } )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"9999999999-1" ) );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert — each source is transferred to the generic background stream atomically.
        _dbMock.Verify( d => d.ScriptEvaluateAsync(
            It.IsAny<string>( ),
            It.Is<RedisKey[]?>( keys => keys != null
                && keys[0] == TrackStream
                && keys[1] == SpotifyConstants.BackgroundStream ),
            It.IsAny<RedisValue[]?>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Exactly( ExpectedCount ),
            "Every message in the rejected batch must use the atomic transfer script" );
        Assert.HasCount( ExpectedCount, capturedKeys );
        Assert.HasCount( ExpectedCount, capturedValues );
        QueuedLookupRequest? forwarded = JsonSerializer.Deserialize<QueuedLookupRequest>(
            capturedValues[0]![2].ToString( ), s_jsonOptions );
        Assert.AreEqual( QueueEnqueueOrigin.Requeue, forwarded!.EnqueueOrigin );
        Assert.IsTrue( forwarded.BypassBulkRouting );
        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        // Assert — no saga state written on the 4xx path
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );

        // Assert — cooldown not armed: no stream re-add (RequeueAllAsync not called)
        _dbMock.Verify( d => d.StreamAddAsync(
            (RedisKey)TrackStream,
            It.IsAny<NameValueEntry[]>( ),
            It.IsAny<RedisValue?>( ),
            It.IsAny<long?>( ),
            It.IsAny<bool>( ),
            It.IsAny<long?>( ),
            It.IsAny<StreamTrimMode>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Never,
            "4xx rejection must not re-add items to the bulk stream" );
    }

    /// <summary>
    /// Verifies the album-path parity for 4xx rejection: when the album lookup service throws
    /// <see cref="SpotifyBulkRejectedException"/>, every album message is re-enqueued at
    /// Interactive atomically, and no saga state is written.
    /// </summary>
    /// <remarks>Test was red before HandleBulkRejectionAsync existed; green after.</remarks>
    [TestMethod]
    public async Task ProcessBulkAlbums_WhenBulkRejected4xx_ShouldAtomicallyTransferAllToInteractive( ) {
        const string TestAlbumId = "6WdSsBrH5QtofaTTqgwxOV";
        const int ExpectedCount = 1;

        // Arrange — album stream at threshold
        _ = _dbMock.Setup( d => d.StreamLengthAsync( AlbumStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( SpotifyConstants.MaxAlbumsPerBatchLookup );
        _ = _dbMock.Setup( d => d.StreamLengthAsync( TrackStream, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( 0L );

        QueuedLookupRequest albumRequest = new( ) {
            RequestId = Guid.NewGuid( ).ToString( "N" ),
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.AlbumIdLookup,
            LookupValue = TestAlbumId,
            SagaId = TestSagaId,
            IsAlbum = true,
            OriginPriority = QueuePriority.Bulk,
            AttemptCount = 0
        };
        StreamEntry albumEntry = BuildStreamEntryFromRequest( albumRequest );

        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                AlbumStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [albumEntry] );

        _ = _lookupServiceMock.Setup( s => s.GetAlbumsByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new SpotifyBulkRejectedException( 400, null, SupportedProviders.Spotify ) );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkAlbumLookupsAsync( TestContext.CancellationToken );

        // Assert — atomically transferred to the single-item background stream.
        _dbMock.Verify( d => d.ScriptEvaluateAsync(
            It.IsAny<string>( ),
            It.Is<RedisKey[]?>( keys => keys != null
                && keys[0] == AlbumStream
                && keys[1] == SpotifyConstants.BackgroundStream ),
            It.IsAny<RedisValue[]?>( ),
            It.IsAny<CommandFlags>( ) ),
            Times.Exactly( ExpectedCount ),
            "Every album message in the rejected batch must use the atomic transfer script" );
        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        // Assert — no saga state written
        _sagaManagerMock.Verify( m => m.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies that AttemptCount is NOT incremented on the 4xx re-enqueue path. The 4xx is a
    /// route change (batch → individual), not a failed attempt; good ids must not be penalized
    /// by having their retry count consumed.
    /// </summary>
    /// <remarks>
    /// Contrasts with the empty-dict (5xx/transient) path where <c>RequeueAsync</c> increments
    /// AttemptCount as part of the re-add. Test was red before HandleBulkRejectionAsync preserved
    /// the original payload unchanged; green after.
    /// </remarks>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenBulkRejected4xx_ShouldNotIncrementAttemptCount( ) {
        const int OriginalAttemptCount = 2;

        // Arrange — message with a non-zero attempt count
        QueuedLookupRequest request = CreateRequest( TestTrackId, attemptCount: OriginalAttemptCount );
        StreamEntry entry = BuildStreamEntryFromRequest( request );

        _ = _dbMock.Setup( d => d.StreamReadGroupAsync(
                TrackStream,
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ),
                It.IsAny<bool>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [entry] );

        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ThrowsAsync( new SpotifyBulkRejectedException( 400, null, SupportedProviders.Spotify ) );

        RedisValue[]? capturedValues = null;
        _ = _dbMock.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback( ( string _, RedisKey[]? _, RedisValue[]? values, CommandFlags _ ) => capturedValues = values )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"9999999999-1" ) );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        // Assert — AttemptCount carried unchanged
        Assert.IsNotNull( capturedValues, "The atomic transfer script must receive the re-enqueued payload" );
        QueuedLookupRequest? capturedRequest = JsonSerializer.Deserialize<QueuedLookupRequest>(
            capturedValues[2].ToString( ), s_jsonOptions );
        Assert.IsNotNull( capturedRequest );
        Assert.AreEqual(
            OriginalAttemptCount,
            capturedRequest.AttemptCount,
            "AttemptCount must be preserved unchanged on a 4xx re-enqueue (route change, not a failed attempt)" );
    }

    /// <summary>
    /// Characterizes the current behavior for a valid 200 response with an empty track body: the
    /// processor takes the cooldown-and-rebatch path (empty-dict branch). This characterizes
    /// CURRENT behavior — the empty-200 case is a known out-of-scope gap and is not asserted as
    /// correct.
    /// </summary>
    [TestMethod]
    public async Task ProcessBulkTracks_WhenLookupServiceReturnsEmptyDict_TakesCooldownRebatchPath( ) {
        // Arrange — lookup service returns empty dict (no exception: could be empty-200 or 5xx)
        _ = _lookupServiceMock.Setup( s => s.GetTracksByIdsAsync( It.IsAny<IEnumerable<string>>( ) ) )
            .ReturnsAsync( [] );

        SpotifyBulkProcessorService service = CreateService( );

        // Act
        await service.ProcessBulkTrackLookupsAsync( TestContext.CancellationToken );

        VerifyAtomicRequeue( TrackStream, Times.AtLeastOnce( ) );

        _requestQueueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ),
            QueuePriority.Interactive,
            It.IsAny<CancellationToken>( ) ),
            Times.Never,
            "Empty-dict must not trigger Interactive re-enqueue (current behavior characterization)" );
    }

    private void VerifyAtomicRequeue( string stream, Times times ) =>
        _dbMock.Verify( database => database.ScriptEvaluateAsync(
            It.IsAny<string>( ),
            It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 1 && keys[0] == stream ),
            It.Is<RedisValue[]?>( arguments => arguments != null && arguments.Length == 7 ),
            It.IsAny<CommandFlags>( ) ), times );

    private void VerifyAtomicRemoval( string stream, Times times ) =>
        _dbMock.Verify( database => database.ScriptEvaluateAsync(
            It.IsAny<string>( ),
            It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 1 && keys[0] == stream ),
            It.Is<RedisValue[]?>( arguments => arguments != null && arguments.Length == 2 ),
            It.IsAny<CommandFlags>( ) ), times );
}
