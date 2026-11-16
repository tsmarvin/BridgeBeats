using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Core.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="QueueProcessorBackgroundService"/>, the worker-side queue consumer
/// (one instance per provider). Drive the dequeue-process-ack loop against mocked queue, rate-limit
/// tracker, saga manager, lookup service, and Redis subscriber dependencies, and verify:
/// constructor null-guards; endpoint rate-limit pre-check and delayed requeue; <c>LookupRequestType</c>
/// routing onto the right <see cref="IMusicLookupService"/> method; saga-state update, ack, and
/// <c>saga:completed</c> publication on success; <see cref="RetryAfterExceededException"/> handling
/// (mark partial, merge rate-limit info, publish sentinel, re-enqueue at Background); the
/// interactive-to-background deferral log; and generic-exception retry-then-DLQ semantics.
/// </summary>
[TestClass]
[DoNotParallelize]
public class QueueProcessorBackgroundServiceTests {
    /// <summary>Mocked Redis multiplexer; returns <see cref="_subscriberMock"/> for pub/sub.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    /// <summary>Mocked Redis subscriber used to verify completion-event publication.</summary>
    private Mock<ISubscriber> _subscriberMock = null!;
    /// <summary>Mocked request queue (dequeue, ack, requeue, enqueue, DLQ).</summary>
    private Mock<IRequestQueue<QueuedLookupRequest>> _queueMock = null!;
    /// <summary>Mocked rate-limit tracker driving the endpoint pre-check.</summary>
    private Mock<IRateLimitTracker> _rateLimitTrackerMock = null!;
    /// <summary>Mocked saga-state manager (get/create, provider-state update, rate-limit info).</summary>
    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    /// <summary>Mocked per-provider lookup service whose method calls the routing tests assert.</summary>
    private Mock<IMusicLookupService> _lookupServiceMock = null!;
    /// <summary>Mocked logger used to assert the interactive-deferral log event.</summary>
    private Mock<ILogger<QueueProcessorBackgroundService>> _loggerMock = null!;

    /// <summary>The provider this service instance is bound to for all tests (<see cref="SupportedProviders.Spotify"/>).</summary>
    private const SupportedProviders TestProvider = SupportedProviders.Spotify;

    /// <summary>MSTest-injected context, used here for per-test cancellation tokens.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Shared camelCase serializer options matching the saga result-JSON wire format.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly ConcurrentDictionary<string, (string LookupKey, LookupRequestType LookupType, string LookupValue)> s_requestIdentities = new( );

    /// <summary>Builds fresh dependency mocks and wires the subscriber before each test.</summary>
    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _subscriberMock = new Mock<ISubscriber>( );
        _queueMock = new Mock<IRequestQueue<QueuedLookupRequest>>( );
        _rateLimitTrackerMock = new Mock<IRateLimitTracker>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _lookupServiceMock = new Mock<IMusicLookupService>( );
        _loggerMock = new Mock<ILogger<QueueProcessorBackgroundService>>( );

        _ = _sagaManagerMock.Setup( s => s.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ),
                It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( string sagaId, string lookupKey, LookupRequestType lookupType, string lookupValue, QueuePriority? priority, CancellationToken _ ) =>
                new LookupSagaState {
                    SagaId = sagaId,
                    LookupKey = lookupKey,
                    LookupType = lookupType,
                    LookupValue = lookupValue,
                    InstanceToken = "test-instance",
                    OriginPriority = priority ?? QueuePriority.Background
                } );
        _ = _sagaManagerMock.Setup( s => s.GetAsync(
                It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( string sagaId, CancellationToken _ ) => {
                (string lookupKey, LookupRequestType lookupType, string lookupValue) = s_requestIdentities.GetValueOrDefault(
                    sagaId, ($"{LookupRequestType.IsrcLookup}:test-value", LookupRequestType.IsrcLookup, "test-value") );
                return new LookupSagaState {
                    SagaId = sagaId,
                    LookupKey = lookupKey,
                    LookupType = lookupType,
                    LookupValue = lookupValue,
                    InstanceToken = "test-instance",
                    ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                        [TestProvider] = new( TestProvider, false, false, null, null, null )
                    }
                };
            } );
        _ = _sagaManagerMock.Setup( s => s.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( s => s.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( s => s.TrySetIsPartialAsync(
                It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = _sagaManagerMock.Setup( s => s.TrySetRateLimitInfoAsync(
                It.IsAny<string>( ), It.IsAny<List<ProviderRateLimitInfo>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        _ = _redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) ).Returns( _subscriberMock.Object );
    }

    #region Constructor Tests

    /// <summary>The constructor builds an instance when all dependencies are supplied.</summary>
    [TestMethod]
    public void Constructor_WithValidDependencies_ShouldCreateInstance( ) {
        // Act
        QueueProcessorBackgroundService service = CreateService( );

        // Assert
        Assert.IsNotNull( service );
    }

    /// <summary>A null Redis multiplexer throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullRedis_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new QueueProcessorBackgroundService(
                null!,
                _queueMock.Object,
                _rateLimitTrackerMock.Object,
                _sagaManagerMock.Object,
                _lookupServiceMock.Object,
                TestProvider,
                _loggerMock.Object
            )
        );
    }

    /// <summary>A null queue throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullQueue_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new QueueProcessorBackgroundService(
                _redisMock.Object,
                null!,
                _rateLimitTrackerMock.Object,
                _sagaManagerMock.Object,
                _lookupServiceMock.Object,
                TestProvider,
                _loggerMock.Object
            )
        );
    }

    /// <summary>A null rate-limit tracker throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullRateLimitTracker_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new QueueProcessorBackgroundService(
                _redisMock.Object,
                _queueMock.Object,
                null!,
                _sagaManagerMock.Object,
                _lookupServiceMock.Object,
                TestProvider,
                _loggerMock.Object
            )
        );
    }

    /// <summary>A null saga-state manager throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullSagaManager_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new QueueProcessorBackgroundService(
                _redisMock.Object,
                _queueMock.Object,
                _rateLimitTrackerMock.Object,
                null!,
                _lookupServiceMock.Object,
                TestProvider,
                _loggerMock.Object
            )
        );
    }

    /// <summary>A null lookup service throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullLookupService_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new QueueProcessorBackgroundService(
                _redisMock.Object,
                _queueMock.Object,
                _rateLimitTrackerMock.Object,
                _sagaManagerMock.Object,
                null!,
                TestProvider,
                _loggerMock.Object
            )
        );
    }

    /// <summary>A null logger throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new QueueProcessorBackgroundService(
                _redisMock.Object,
                _queueMock.Object,
                _rateLimitTrackerMock.Object,
                _sagaManagerMock.Object,
                _lookupServiceMock.Object,
                TestProvider,
                null!
            )
        );
    }

    #endregion

    #region Message Processing Tests

    /// <summary>
    /// When the endpoint is not rate-limited, processing a dequeued message invokes the matching
    /// lookup-service method (here <c>GetInfoByISRCAsync</c>) exactly once.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenEndpointNotRateLimited_ShouldCallLookupService( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupLookupSuccess( );
        SetupSagaNotComplete( request.SagaId );

        // Setup queue to return one message then null (to exit loop)
        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act - Start and quickly cancel
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken ); // Give time for processing
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _lookupServiceMock.Verify(
            l => l.GetInfoByISRCAsync( "US1234567890" ),
            Times.Once
        );
    }

    /// <summary>A missing native id falls back to the external id within the same provider leg.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_NativeIdNotFound_UsesExternalIdFallback( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.SongIdLookup, "stale-native-id" ) with {
            FallbackLookupType = LookupRequestType.IsrcLookup,
            FallbackLookupValue = "USRC12345678"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( service => service.GetInfoByIDAsync( "stale-native-id", false ) )
            .ReturnsAsync( (MusicLookupResult?)null );
        _ = _lookupServiceMock.Setup( service => service.GetInfoByISRCAsync( "USRC12345678" ) )
            .ReturnsAsync( CreateLookupResult( ) );

        int dequeueCount = 0;
        _ = _queueMock.Setup( queue => queue.DequeueAsync(
                It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => dequeueCount++ == 0 ? message : null );

        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 1200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _lookupServiceMock.Verify( lookup => lookup.GetInfoByIDAsync( "stale-native-id", false ), Times.Once );
        _lookupServiceMock.Verify( lookup => lookup.GetInfoByISRCAsync( "USRC12345678" ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            request.SagaId,
            It.Is<ProviderLookupState>( state => state.IsComplete && state.IsSuccess ),
            It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>The stored storefront is applied to both the native attempt and its fallback.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithStorefront_UsesStorefrontCapabilityForBothAttempts( ) {
        Mock<IStorefrontMusicLookupService> storefrontLookup = new( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.SongIdLookup, "old-id" ) with {
            Storefront = "jp",
            FallbackLookupType = LookupRequestType.IsrcLookup,
            FallbackLookupValue = "JPABC1234567"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = storefrontLookup.Setup( lookup => lookup.GetInfoByIDAsync( "old-id", false, "jp" ) )
            .ReturnsAsync( (MusicLookupResult?)null );
        _ = storefrontLookup.Setup( lookup => lookup.GetInfoByISRCAsync( "JPABC1234567", "jp" ) )
            .ReturnsAsync( CreateLookupResult( ) );

        int dequeueCount = 0;
        _ = _queueMock.Setup( queue => queue.DequeueAsync(
                It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => dequeueCount++ == 0 ? message : null );

        QueueProcessorBackgroundService service = CreateService( storefrontLookup.Object );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        storefrontLookup.Verify( lookup => lookup.GetInfoByIDAsync( "old-id", false, "jp" ), Times.Once );
        storefrontLookup.Verify( lookup => lookup.GetInfoByISRCAsync( "JPABC1234567", "jp" ), Times.Once );
        storefrontLookup.Verify( lookup => lookup.GetInfoByIDAsync( It.IsAny<string>( ), It.IsAny<bool>( ) ), Times.Never );
    }

    /// <summary>
    /// A request carrying <see cref="QueuePriority.Bulk"/> origin priority forwards that priority to
    /// <c>GetOrCreateAsync</c> so the saga records its origin lane.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithBulkOriginPriority_ShouldPassOriginPriorityToSagaCreation( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            OriginPriority = QueuePriority.Bulk
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupLookupSuccess( );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _sagaManagerMock.Verify( s => s.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ), Times.AtLeastOnce );
        _sagaManagerMock.Verify( s => s.GetOrCreateAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ),
            It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// When the endpoint pre-check reports the provider rate-limited, the message is requeued with
    /// the remaining delay and the lookup service is never called.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenEndpointRateLimited_ShouldRequeueWithDelay( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        TimeSpan timeRemaining = TimeSpan.FromSeconds( 30 );
        SetupRateLimited( timeRemaining );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _queueMock.Verify(
            q => q.RequeueAsync( message.MessageId, timeRemaining, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        // Should NOT call lookup service
        _lookupServiceMock.Verify(
            l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ),
            Times.Never
        );
    }

    /// <summary>
    /// A successful lookup writes a complete, successful provider state into the saga via
    /// <c>UpdateProviderStateAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithSuccessfulLookup_ShouldUpdateSagaState( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupLookupSuccess( );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                request.SagaId,
                It.Is<ProviderLookupState>( state =>
                    state.Provider == TestProvider &&
                    state.IsComplete &&
                    state.IsSuccess
                ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
    }

    /// <summary>Enabling queue metrics changes observations only, never business Redis/queue calls.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SuccessfulLeg_MetricsListenerOnAndOff_HasIdenticalBusinessCallCounts( ) {
        SetupNotRateLimited( );
        SetupLookupSuccess( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        SetupSagaNotComplete( request.SagaId );
        bool deliver = true;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                if (!deliver) return null;
                deliver = false;
                return CreateMessage( request );
            } );

        static int Count( Mock mock, string method ) => mock.Invocations.Count( invocation => invocation.Method.Name == method );
        async Task<(int Gets, int Updates, int Acks, int Enqueues, int Publishes)> RunAsync( bool listen ) {
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
            int acks = Count( _queueMock, nameof( IRequestQueue<QueuedLookupRequest>.AcknowledgeAsync ) );
            int enqueues = Count( _queueMock, nameof( IRequestQueue<QueuedLookupRequest>.EnqueueAsync ) );
            int publishes = Count( _subscriberMock, nameof( ISubscriber.PublishAsync ) );
            deliver = true;
            QueueProcessorBackgroundService service = CreateService( );
            using CancellationTokenSource cts = new( );
            _ = service.StartAsync( cts.Token );
            await Task.Delay( 200, TestContext.CancellationToken );
            await cts.CancelAsync( );
            await service.StopAsync( CancellationToken.None );
            return (Count( _sagaManagerMock, nameof( ISagaStateManager.GetAsync ) ) - gets,
                Count( _sagaManagerMock, nameof( ISagaStateManager.TryUpdateProviderStateAsync ) ) - updates,
                Count( _queueMock, nameof( IRequestQueue<QueuedLookupRequest>.AcknowledgeAsync ) ) - acks,
                Count( _queueMock, nameof( IRequestQueue<QueuedLookupRequest>.EnqueueAsync ) ) - enqueues,
                Count( _subscriberMock, nameof( ISubscriber.PublishAsync ) ) - publishes);
        }

        (int Gets, int Updates, int Acks, int Enqueues, int Publishes) disabled = await RunAsync( false );
        (int Gets, int Updates, int Acks, int Enqueues, int Publishes) enabled = await RunAsync( true );
        Assert.AreEqual( disabled.Gets, enabled.Gets );
        Assert.AreEqual( disabled.Acks, enabled.Acks );
        Assert.AreEqual( disabled.Enqueues, enabled.Enqueues );
        Assert.AreEqual( disabled.Publishes, enabled.Publishes );
        Assert.AreEqual( 1, disabled.Gets );
        Assert.AreEqual( 1, disabled.Updates );
        Assert.AreEqual( 1, disabled.Acks );
    }

    /// <summary>A successful lookup acknowledges the originating message exactly once.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithSuccessfulLookup_ShouldAcknowledgeMessage( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupLookupSuccess( );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _queueMock.Verify(
            q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// An ACK failure after a successful provider-state commit leaves the original delivery
    /// pending, without retry mutation or DLQ promotion, even at the terminal attempt boundary.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_AckFailureAfterCommittedSuccess_LeavesPelPendingWithoutRetryMutation( ) {
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "ACK-AFTER-COMMIT" ) with {
            AttemptCount = LookupConstants.MaxQueueRetryAttempts - 1
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        SetupLookupSuccess( );
        SetupSagaNotComplete( request.SagaId );
        _ = _queueMock.Setup( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new RedisServerException( "XACK unavailable" ) );

        int dequeueCalls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                _ = Interlocked.Increment( ref dequeueCalls );
                return message;
            } );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        Assert.AreEqual( 1, dequeueCalls, "ACK recovery must not re-run the committed delivery." );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            request.SagaId,
            It.Is<ProviderLookupState>( state => state.IsComplete && state.IsSuccess ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, It.IsAny<TimeSpan?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( q => q.MoveToDlqAsync(
            message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// After an ACK loss, the committed delivery is acknowledged by its control-plane recovery
    /// loop without a second provider call or terminal retry mutation.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_AckLossThenCompletedChildRedelivery_AcknowledgesWithoutProviderRetry( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "ACK-CHILD-RECOVERY" ) with {
            AttemptCount = LookupConstants.MaxQueueRetryAttempts - 1
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        SetupLookupSuccess( );
        LookupSagaState initialSaga = new( ) {
            SagaId = request.SagaId,
            LookupKey = $"{request.LookupType}:{request.LookupValue}",
            LookupType = request.LookupType,
            LookupValue = request.LookupValue,
            InstanceToken = "test-instance",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [TestProvider] = new( TestProvider, false, false, null, null, null )
            }
        };
        LookupSagaState completedChildSaga = initialSaga with {
            LookupKey = "different-child-root",
            LookupType = LookupRequestType.UpcLookup,
            LookupValue = "OTHER-CHILD",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [TestProvider] = new( TestProvider, true, true, "{}", DateTimeOffset.UtcNow, null )
            }
        };
        bool committed = false;
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => committed ? completedChildSaga : initialSaga );
        _ = _sagaManagerMock.Setup( manager => manager.TryUpdateProviderStateAsync(
                request.SagaId, It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => committed = true )
            .ReturnsAsync( true );

        int ackCalls = 0;
        TaskCompletionSource<bool> recovered = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _queueMock.Setup( queue => queue.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ) )
            .Returns( ( ) => {
                if (Interlocked.Increment( ref ackCalls ) == 1) {
                    return Task.FromException( new RedisServerException( "XACK unavailable" ) );
                }
                _ = recovered.TrySetResult( true );
                return Task.CompletedTask;
            } );
        int dequeueCalls = 0;
        _ = _queueMock.Setup( queue => queue.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => Interlocked.Increment( ref dequeueCalls ) == 1 ? message : null );

        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        _ = await recovered.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        Assert.AreEqual( 2, ackCalls );
        _lookupServiceMock.Verify( lookup => lookup.GetInfoByISRCAsync( request.LookupValue ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            request.SagaId, It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.RequeueAsync(
            message.MessageId, It.IsAny<TimeSpan?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.MoveToDlqAsync(
            message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// If both the initial post-commit ACK and its first scheduled recovery ACK fail, the worker
    /// retains the active delivery without dequeuing it again or applying retry mutation.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_AckLossThenCompletedChildRecoveryAckLoss_LeavesPelPending( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "ACK-CHILD-RECOVERY-FAIL" ) with {
            AttemptCount = LookupConstants.MaxQueueRetryAttempts - 1
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        SetupLookupSuccess( );
        LookupSagaState initialSaga = new( ) {
            SagaId = request.SagaId,
            LookupKey = $"{request.LookupType}:{request.LookupValue}",
            LookupType = request.LookupType,
            LookupValue = request.LookupValue,
            InstanceToken = "test-instance",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [TestProvider] = new( TestProvider, false, false, null, null, null )
            }
        };
        LookupSagaState completedChildSaga = initialSaga with {
            LookupKey = "different-child-root",
            LookupType = LookupRequestType.UpcLookup,
            LookupValue = "OTHER-CHILD",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [TestProvider] = new( TestProvider, true, true, "{}", DateTimeOffset.UtcNow, null )
            }
        };
        bool committed = false;
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => committed ? completedChildSaga : initialSaga );
        _ = _sagaManagerMock.Setup( manager => manager.TryUpdateProviderStateAsync(
                request.SagaId, It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => committed = true )
            .ReturnsAsync( true );

        int ackCalls = 0;
        TaskCompletionSource<bool> recoveryAttempted = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _queueMock.Setup( queue => queue.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ) )
            .Returns( ( ) => {
                if (Interlocked.Increment( ref ackCalls ) == 2) {
                    _ = recoveryAttempted.TrySetResult( true );
                }
                return Task.FromException( new RedisServerException( "XACK unavailable" ) );
            } );
        int dequeueCalls = 0;
        _ = _queueMock.Setup( queue => queue.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => Interlocked.Increment( ref dequeueCalls ) <= 2 ? message : null );

        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        _ = await recoveryAttempted.Task.WaitAsync( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        Assert.AreEqual( 2, ackCalls );
        Assert.AreEqual( 1, dequeueCalls, "ACK recovery must not re-run the committed delivery." );
        _lookupServiceMock.Verify( lookup => lookup.GetInfoByISRCAsync( request.LookupValue ), Times.Once );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            request.SagaId, It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( queue => queue.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.RequeueAsync(
            message.MessageId, It.IsAny<TimeSpan?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( queue => queue.MoveToDlqAsync(
            message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// A redelivered message whose provider leg is already complete is acknowledged idempotently
    /// without invoking the provider or publishing a duplicate completion event.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenSagaCompletes_ShouldAcknowledgeWithoutDuplicateProcessing( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupSagaComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Once );
        _lookupServiceMock.Verify( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ), Times.Never );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _subscriberMock.Verify( s => s.PublishAsync(
            It.IsAny<RedisChannel>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    #endregion

    #region Lookup Type Routing Tests

    /// <summary>
    /// A <see cref="LookupRequestType.UriLookup"/> request routes to <c>GetInfoAsync(url)</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithUriLookup_ShouldCallGetInfoAsyncWithUri( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        string testUrl = "https://open.spotify.com/track/123";
        QueuedLookupRequest request = CreateRequest( LookupRequestType.UriLookup, testUrl );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( testUrl ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _lookupServiceMock.Verify( l => l.GetInfoAsync( testUrl ), Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.UpcLookup"/> request routes to <c>GetInfoByUPCAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithUpcLookup_ShouldCallGetInfoByUPCAsync( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        string testUpc = "123456789012";
        QueuedLookupRequest request = CreateRequest( LookupRequestType.UpcLookup, testUpc );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByUPCAsync( testUpc ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _lookupServiceMock.Verify( l => l.GetInfoByUPCAsync( testUpc ), Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.SongIdLookup"/> request routes to <c>GetInfoByIDAsync(id, false)</c>
    /// (<c>isAlbum: false</c>).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithSongIdLookup_ShouldCallGetInfoByIDAsync( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        string testId = "spotify-track-id";
        QueuedLookupRequest request = CreateRequest( LookupRequestType.SongIdLookup, testId );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByIDAsync( testId, false ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _lookupServiceMock.Verify( l => l.GetInfoByIDAsync( testId, false ), Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.AlbumIdLookup"/> request routes to <c>GetInfoByIDAsync(id, true)</c>
    /// (<c>isAlbum: true</c>).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithAlbumIdLookup_ShouldCallGetInfoByIDAsyncWithIsAlbumTrue( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        string testId = "spotify-album-id";
        QueuedLookupRequest request = CreateRequest( LookupRequestType.AlbumIdLookup, testId );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByIDAsync( testId, true ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _lookupServiceMock.Verify( l => l.GetInfoByIDAsync( testId, true ), Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.SongLookup"/> request routes to <c>GetInfoAsync(title, artist)</c>,
    /// drawing the title and artist from the request rather than its lookup value.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WithSongLookup_ShouldCallGetInfoAsyncWithTitleArtist( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.SongLookup, "ignored" ) with {
            Title = "Test Song",
            Artist = "Test Artist"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( "Test Song", "Test Artist" ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _lookupServiceMock.Verify( l => l.GetInfoAsync( "Test Song", "Test Artist" ), Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.ArtistLookup"/> request routes to <c>GetInfoAsync(title, artist)</c>,
    /// drawing the title and artist from the request.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PerformLookup_WithArtistLookup_ShouldCallGetInfoAsyncWithTitleArtist( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.ArtistLookup, "ignored" ) with {
            Title = "Test Album",
            Artist = "Test Artist"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( "Test Album", "Test Artist" ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert: ArtistLookup arm routes to GetInfoAsync(title, artist)
        _lookupServiceMock.Verify( l => l.GetInfoAsync( "Test Album", "Test Artist" ), Times.Once );
        // Negative discriminator: URI lookup is not called
        _lookupServiceMock.Verify( l => l.GetInfoAsync( It.IsAny<string>( ) ), Times.Never );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.ArtistAlbumLookup"/> request with title and artist present
    /// routes to <c>GetInfoAsync(title, artist)</c>, which performs the artist-to-album expansion
    /// internally. This verifies that the arm dispatches through the title+artist path and does not
    /// throw.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PerformLookup_WithArtistAlbumLookupAndTitleArtist_ShouldCallGetInfoAsyncWithTitleArtist( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.ArtistAlbumLookup, "ignored" ) with {
            Title = "Test Album",
            Artist = "Test Artist"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( "Test Album", "Test Artist" ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act: test failed before implementation (threw NotImplementedException); passes after
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert: dispatches to GetInfoAsync(title, artist) — does not error-path
        _lookupServiceMock.Verify( l => l.GetInfoAsync( "Test Album", "Test Artist" ), Times.Once );
        // Discriminator: single-arg URI overload must not be called (only title+artist overload is used)
        _lookupServiceMock.Verify( l => l.GetInfoAsync( It.IsAny<string>( ) ), Times.Never );
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                request.SagaId,
                It.Is<ProviderLookupState>( state => state.IsSuccess ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.AlbumTrackLookup"/> request with title and artist present
    /// routes to <c>GetInfoAsync(title, artist)</c>, which performs the album-to-track expansion
    /// internally. This verifies that the arm dispatches through the title+artist path and does not
    /// throw.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PerformLookup_WithAlbumTrackLookupAndTitleArtist_ShouldCallGetInfoAsyncWithTitleArtist( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.AlbumTrackLookup, "ignored" ) with {
            Title = "Test Track",
            Artist = "Test Artist"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( "Test Track", "Test Artist" ) )
            .ReturnsAsync( CreateLookupResult( ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act: test failed before implementation (threw NotImplementedException); passes after
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert: dispatches to GetInfoAsync(title, artist) — does not error-path
        _lookupServiceMock.Verify( l => l.GetInfoAsync( "Test Track", "Test Artist" ), Times.Once );
        // Discriminator: single-arg URI overload must not be called (only title+artist overload is used)
        _lookupServiceMock.Verify( l => l.GetInfoAsync( It.IsAny<string>( ) ), Times.Never );
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                request.SagaId,
                It.Is<ProviderLookupState>( state => state.IsSuccess ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.ArtistAlbumLookup"/> request missing title or artist falls
    /// through to the <see cref="InvalidOperationException"/> arm, records a failed provider state,
    /// and does not call any lookup method.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PerformLookup_WithArtistAlbumLookupMissingTitleArtist_ShouldRecordFailedState( ) {
        // Arrange: no Title or Artist on the request
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.ArtistAlbumLookup, "some-value" ) with {
            AttemptCount = 1  // below max so it goes through HandleProcessingExceptionAsync
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert: falls through to error handling — IsSuccess == false, ErrorMessage != null
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                request.SagaId,
                It.Is<ProviderLookupState>( state =>
                    !state.IsSuccess &&
                    state.ErrorMessage != null
                ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once );
        // The lookup service is never called
        _lookupServiceMock.Verify( l => l.GetInfoAsync( It.IsAny<string>( ), It.IsAny<string>( ) ), Times.Never );
        _queueMock.Verify( q => q.MoveToDlqAsync( message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// A <see cref="LookupRequestType.AlbumTrackLookup"/> request missing title or artist falls
    /// through to the <see cref="InvalidOperationException"/> arm and records a failed provider state.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PerformLookup_WithAlbumTrackLookupMissingTitleArtist_ShouldRecordFailedState( ) {
        // Arrange: no Title or Artist on the request
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.AlbumTrackLookup, "some-value" ) with {
            AttemptCount = 1
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert: falls through to error handling
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                request.SagaId,
                It.Is<ProviderLookupState>( state =>
                    !state.IsSuccess &&
                    state.ErrorMessage != null
                ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once );
        _lookupServiceMock.Verify( l => l.GetInfoAsync( It.IsAny<string>( ), It.IsAny<string>( ) ), Times.Never );
    }

    #endregion

    #region Rate Limit Exception Handling Tests

    /// <summary>
    /// A <see cref="RetryAfterExceededException"/> from the lookup sets a rate-limit window in the
    /// tracker for this provider.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitExceptionThrown_ShouldRecordRateLimit( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _rateLimitTrackerMock.Verify(
            r => r.SetRateLimitedAsync(
                TestProvider,
                It.IsAny<string>( ), // Uses LookupType.ToString() as endpoint
                It.IsAny<DateTimeOffset>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
    }

    /// <summary>
    /// A <see cref="RetryAfterExceededException"/> acknowledges the original message and re-enqueues
    /// the request at <see cref="QueuePriority.Background"/> with an incremented attempt count and the
    /// rate-limited endpoint recorded.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitExceptionThrown_ShouldRequeueMessage( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert - Should acknowledge original and re-enqueue (rate-limit-aware dequeue will skip until limit expires)
        _queueMock.Verify(
            q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _queueMock.Verify(
            q => q.EnqueueAsync(
                It.Is<QueuedLookupRequest>( r =>
                    r.LookupType == request.LookupType &&
                    r.LookupValue == request.LookupValue &&
                    r.AttemptCount == request.AttemptCount + 1 &&
                    r.RateLimitedEndpoint == request.LookupType.ToString( )
                ),
                QueuePriority.Background,
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
    }

    /// <summary>
    /// A <see cref="RetryAfterExceededException"/> marks the saga partial via <c>SetIsPartialAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitExceptionThrown_ShouldMarkSagaAsPartial( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert - Should mark the saga as partial
        _sagaManagerMock.Verify(
            s => s.TrySetIsPartialAsync( request.SagaId, true, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// A <see cref="RetryAfterExceededException"/> records a single <see cref="ProviderRateLimitInfo"/>
    /// (this provider, with an endpoint) into the saga via <c>SetRateLimitInfoAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitExceptionThrown_ShouldRecordRateLimitInfoInSaga( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert - Should record rate limit info with correct provider
        _sagaManagerMock.Verify(
            s => s.TrySetRateLimitInfoAsync(
                request.SagaId,
                It.Is<List<ProviderRateLimitInfo>>( info =>
                    info.Count == 1 &&
                    info[0].Provider == TestProvider &&
                    !string.IsNullOrEmpty( info[0].Endpoint )
                ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
    }

    /// <summary>
    /// When the saga already holds rate-limit info for another provider, a new
    /// <see cref="RetryAfterExceededException"/> merges this provider's info with the existing entry
    /// rather than replacing it, so <c>SetRateLimitInfoAsync</c> receives both providers.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitExceptionThrown_ShouldMergeRateLimitInfoWithOtherProviders( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        // Saga already carries rate limit info from a different provider
        ProviderRateLimitInfo existingInfo = new(
            SupportedProviders.AppleMusic,
            DateTimeOffset.UtcNow.AddMinutes( 5 ),
            "IsrcLookup"
        );
        LookupSagaState saga = new( ) {
            SagaId = request.SagaId,
            LookupKey = $"{request.LookupType}:{request.LookupValue}",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US1234567890",
            InstanceToken = "test-instance",
            ProviderStates = [],
            RateLimitInfo = [existingInfo]
        };
        _ = _sagaManagerMock.Setup( s => s.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( saga );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert - Both the existing AppleMusic entry and the new Spotify entry are present
        _sagaManagerMock.Verify(
            s => s.TrySetRateLimitInfoAsync(
                request.SagaId,
                It.Is<List<ProviderRateLimitInfo>>( info =>
                    info.Count == 2 &&
                    info.Any( r => r.Provider == SupportedProviders.AppleMusic ) &&
                    info.Any( r => r.Provider == TestProvider )
                ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
    }

    /// <summary>
    /// A <see cref="RetryAfterExceededException"/> publishes a lookup-completion wakeup (the
    /// rate-limited sentinel) on a <c>complete:</c> channel so synchronous waiters can return a partial.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitExceptionThrown_ShouldPublishLookupCompletion( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId ); // Need saga for PublishLookupCompletionAsync to get lookup key
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert - Should publish lookup completion for partial result handling
        _subscriberMock.Verify(
            s => s.PublishAsync(
                It.Is<RedisChannel>( c => c.ToString( ).StartsWith( "complete:", StringComparison.Ordinal ) ),
                It.IsAny<RedisValue>( ),
                It.IsAny<CommandFlags>( )
            ),
            Times.Once
        );
    }

    #endregion

    #region Interactive Origin Rate-Limit Deferral Tests

    /// <summary>
    /// An interactive-origin request that hits a rate limit is acknowledged, re-enqueued at
    /// <see cref="QueuePriority.Background"/> with an incremented attempt count, and logs the
    /// interactive-deferred-to-background warning (EventId 3018) exactly once.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitAndInteractiveOrigin_PreservesInteractiveLane( ) {
        // Arrange
        // [LoggerMessage] source-generated code gates every call with IsEnabled().
        // Without this setup Moq returns false and Log() is never invoked, making
        // the Times.Once assertion spuriously fail and Times.Never vacuously true.
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            OriginPriority = QueuePriority.Interactive
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request ) with { Priority = QueuePriority.Interactive };
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        long interactiveDeferredMeasurements = 0;
        using MeterListener interactiveMeterListener = new( );
        interactiveMeterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && instrument.Name == "bridgebeats.ratelimit.interactive_retry.total") {
                listener.EnableMeasurementEvents( instrument );
            }
        };
        interactiveMeterListener.SetMeasurementEventCallback<long>(
            ( _, measurement, _, _ ) => Interlocked.Add( ref interactiveDeferredMeasurements, measurement ) );
        interactiveMeterListener.Start( );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert — enqueue precedes acknowledgement at Background priority.
        _queueMock.Verify(
            q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _queueMock.Verify(
            q => q.EnqueueAsync(
                It.Is<QueuedLookupRequest>( r =>
                    r.LookupType == request.LookupType &&
                    r.AttemptCount == request.AttemptCount + 1
                ),
                QueuePriority.Interactive,
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );

        // Assert — the interactive-deferral log fires exactly once.
        // Disambiguated on EventId 3018 (InteractiveDeferredToBackground) to avoid ambiguity with
        // other Warning logs on this path (LogRateLimitEncountered/3006, LogSagaMarkedPartial/3007).
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.Services.Queue.InteractiveDeferredToBackground ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
        Assert.AreEqual( 1L, interactiveDeferredMeasurements );
    }

    /// <summary>A caught rate-limit replacement failure leaves the original delivery pending.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitReplacementEnqueueFails_DoesNotAcknowledge( ) {
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US429-ENQUEUE-FAIL" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request ) with { Priority = QueuePriority.Interactive };
        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                TimeSpan.FromSeconds( 60 ), TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ), SupportedProviders.Spotify ) );
        _ = _queueMock.Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new RedisServerException( "XADD failed" ) );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );

        long deferredMeasurements = 0;
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && instrument.Name == "bridgebeats.ratelimit.interactive_retry.total") {
                listener.EnableMeasurementEvents( instrument );
            }
        };
        meterListener.SetMeasurementEventCallback<long>( ( _, measurement, _, _ ) => Interlocked.Add( ref deferredMeasurements, measurement ) );
        meterListener.Start( );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 300, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _loggerMock.Verify( l => l.Log(
            LogLevel.Warning,
            new EventId( LogEventIds.Services.Queue.InteractiveDeferredToBackground ),
            It.IsAny<It.IsAnyType>( ), It.IsAny<Exception?>( ), It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ), Times.Never );
        Assert.AreEqual( 0L, deferredMeasurements );
    }

    /// <summary>
    /// A background-origin request that hits a rate limit is acknowledged and re-enqueued at
    /// <see cref="QueuePriority.Background"/>, but does <em>not</em> log the interactive-deferred
    /// warning (EventId 3018), since it did not originate as interactive.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenRateLimitAndBackgroundOrigin_ShouldRequeueAtBackground( ) {
        // Arrange
        // [LoggerMessage] source-generated code gates every call with IsEnabled().
        // Setting it true so a mis-fired LogInteractiveDeferredToBackground would reach Log()
        // and be caught by the Times.Never assertion below (without this, Times.Never would be
        // vacuously true even if the gate ran on the wrong branch).
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            OriginPriority = QueuePriority.Background
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        TimeSpan retryAfter = TimeSpan.FromSeconds( 60 );

        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new RetryAfterExceededException(
                retryAfter,
                TimeSpan.FromSeconds( 30 ),
                new Uri( "https://api.spotify.com/v1/tracks" ),
                SupportedProviders.Spotify
            ) );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert — same requeue-at-Background path; no divergence from background-origin behavior
        _queueMock.Verify(
            q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
        _queueMock.Verify(
            q => q.EnqueueAsync(
                It.Is<QueuedLookupRequest>( r =>
                    r.LookupType == request.LookupType &&
                    r.AttemptCount == request.AttemptCount + 1
                ),
                QueuePriority.Background,
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );

        // Assert — the interactive-deferral log must NOT fire for a Background-origin request.
        // Disambiguated on EventId 3018 (InteractiveDeferredToBackground). The IsEnabled(true)
        // setup above makes this a meaningful negative control rather than a vacuously true check.
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.Services.Queue.InteractiveDeferredToBackground ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never,
            "LogInteractiveDeferredToBackground (Warning, EventId 3018) must NOT fire for a Background-origin request" );
    }

    #endregion

    #region General Exception Handling Tests

    /// <summary>HTTP 500 remains incomplete and durably enqueues replacement before acknowledgement.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_Http500_RequeuesTransientFailure( ) {
        QueueProcessorBackgroundService service = CreateService();
        QueuedLookupRequest request = CreateRequest(LookupRequestType.IsrcLookup, "US500") with { AttemptCount = 1 };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage(request);
        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new HttpRequestException( "server", null, System.Net.HttpStatusCode.InternalServerError ) );
        SetupSagaNotComplete( request.SagaId );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        using CancellationTokenSource cts = new();
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync( request.SagaId, It.Is<ProviderLookupState>( state => !state.IsComplete ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, null, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>HTTP 404 is a permanent refusal: fail the leg and move the delivery to the DLQ immediately.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_Http404_CompletesFailedAndMovesToDlqWithoutRequeue( ) {
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US404" ) with { AttemptCount = 0 };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new HttpRequestException( "not found", null, System.Net.HttpStatusCode.NotFound ) );
        SetupSagaNotComplete( request.SagaId );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            request.SagaId,
            It.Is<ProviderLookupState>( state => state.IsComplete && !state.IsSuccess && state.ErrorMessage != null ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.MoveToDlqAsync( message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>Ordinary transient retries preserve the delivered interactive or bulk lane.</summary>
    [TestMethod]
    [DataRow( QueuePriority.Interactive )]
    [DataRow( QueuePriority.Bulk )]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_Http500_PreservesInteractiveAndBulkLane( QueuePriority priority ) {
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, $"US500-{priority}" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request ) with { Priority = priority };
        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new HttpRequestException( "server", null, System.Net.HttpStatusCode.InternalServerError ) );
        SetupSagaNotComplete( request.SagaId );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, null, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>A transient retry is scheduled for later delivery without sleeping in the consumer loop.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_Http500_SchedulesReplacementAndAcknowledgesOriginal( ) {
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US500-CANCEL" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new HttpRequestException( "server", null, System.Net.HttpStatusCode.InternalServerError ) );
        SetupSagaNotComplete( request.SagaId );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, null, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>Transient replacement failure leaves the original delivery pending.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_Http500_WhenReplacementEnqueueFails_DoesNotAcknowledge( ) {
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US500-ENQUEUE-FAIL" ) with { AttemptCount = 1 };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new HttpRequestException( "server", null, System.Net.HttpStatusCode.InternalServerError ) );
        SetupSagaNotComplete( request.SagaId );
        _ = _queueMock.Setup( q => q.RequeueAsync(
                message.MessageId, null, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new RedisServerException( "XADD failed" ) );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>Timeouts are transient and are requeued below the retry ceiling.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_Timeout_RequeuesTransientFailure( ) {
        QueueProcessorBackgroundService service = CreateService();
        QueuedLookupRequest request = CreateRequest(LookupRequestType.IsrcLookup, "USTIMEOUT") with { AttemptCount = 1 };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage(request);
        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) ).ThrowsAsync( new TimeoutException( "timeout" ) );
        SetupSagaNotComplete( request.SagaId );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        using CancellationTokenSource cts = new();
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync( request.SagaId, It.Is<ProviderLookupState>( state => !state.IsComplete ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, null, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>Provider task cancellation without host cancellation is treated as transient.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_TaskCanceledWithoutHostCancellation_RequeuesTransientFailure( ) {
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "TASKCANCEL001" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        SetupSagaNotComplete( request.SagaId );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new TaskCanceledException( "provider timeout" ) );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );

        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, null, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>
    /// A statusless transport exception below the retry ceiling leaves the provider leg incomplete
    /// with an error message via <c>UpdateProviderStateAsync</c>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenExceptionThrown_BelowMaxRetries_ShouldUpdateSagaWithError( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            AttemptCount = 1 // Below max of 5
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Test error" ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _sagaManagerMock.Verify(
            s => s.TryUpdateProviderStateAsync(
                request.SagaId,
                It.Is<ProviderLookupState>( state =>
                    state.Provider == TestProvider &&
                    !state.IsComplete &&
                    !state.IsSuccess &&
                    state.ErrorMessage != null
                ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ),
            Times.Once
        );
    }

    /// <summary>
    /// A non-rate-limit transient exception below the retry ceiling re-enqueues the replacement before acknowledging the message.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenExceptionThrown_BelowMaxRetries_ShouldRequeueThenAcknowledgeMessage( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            AttemptCount = 1
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Test error" ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 2500, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _queueMock.Verify( q => q.RequeueAsync(
            message.MessageId, null, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>
    /// A non-rate-limit exception at the max retry count moves the message to the dead-letter queue.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenExceptionThrown_AtMaxRetries_ShouldMoveToDlq( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            AttemptCount = 4 // Fifth and terminal execution (initial attempt is zero)
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Test error" ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert
        _queueMock.Verify(
            q => q.MoveToDlqAsync( message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// A non-rate-limit exception at the max retry count does not acknowledge the message (the DLQ
    /// move owns its lifecycle instead).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_WhenExceptionThrown_AtMaxRetries_ShouldNotAcknowledge( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "US1234567890" ) with {
            AttemptCount = 4
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );

        SetupNotRateLimited( );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Test error" ) );
        SetupSagaNotComplete( request.SagaId );

        int callCount = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => callCount++ == 0 ? message : null );

        // Act
        using CancellationTokenSource cts = new( );
        Task serviceTask = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        // Assert - Should NOT acknowledge when moving to DLQ
        _queueMock.Verify(
            q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ),
            Times.Never
        );
    }

    #endregion

    #region No Message Handling Tests

    /// <summary>
    /// When the queue returns no messages, the service keeps polling (the dequeue call is made
    /// repeatedly rather than the loop stopping).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WhenNoMessages_ShouldContinuePolling( ) {
        // Arrange
        QueueProcessorBackgroundService service = CreateService( );
        CountdownEvent pollCountdown = new( 2 ); // Wait for 2 poll calls

        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                if (!pollCountdown.IsSet) {
                    _ = pollCountdown.Signal( );
                }
                return null;
            } );

        // Act - StartAsync triggers ExecuteAsync but doesn't block
        _ = service.StartAsync( CancellationToken.None );
        bool reachedTarget = pollCountdown.Wait( TimeSpan.FromSeconds( 2 ), TestContext.CancellationToken );
        await service.StopAsync( CancellationToken.None );
        pollCountdown.Dispose( );

        // Assert
        Assert.IsTrue( reachedTarget, "Service did not poll at least 2 times within timeout" );
    }

    /// <summary>Generic NOGROUP repair failure is delayed and retried without stopping the host.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WhenGroupRepairFails_RetriesAndSurvives( ) {
        int dequeueCalls = 0;
        int ensureCalls = 0;
        TaskCompletionSource<bool> resumed = new( TaskCreationOptions.RunContinuationsAsynchronously );
        AssuringQueue queue = new( ) {
            DequeueRate = ct => {
                int call = Interlocked.Increment( ref dequeueCalls );
                if (call <= 2) return Task.FromException<QueuedMessage<QueuedLookupRequest>?>( new RedisServerException( "NOGROUP" ) );
                _ = resumed.TrySetResult( true );
                return Task.FromResult<QueuedMessage<QueuedLookupRequest>?>( null );
            },
            Ensure = _ => {
                if (Interlocked.Increment( ref ensureCalls ) == 1) return Task.FromException( new InvalidOperationException( "repair failed" ) );
                return Task.CompletedTask;
            }
        };
        QueueProcessorBackgroundService service = new(
            _redisMock.Object, queue, _rateLimitTrackerMock.Object, _sagaManagerMock.Object,
            _lookupServiceMock.Object, TestProvider, _loggerMock.Object );

        await service.StartAsync( TestContext.CancellationToken );
        Task completed = await Task.WhenAny( resumed.Task, Task.Delay( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken ) );
        await service.StopAsync( TestContext.CancellationToken );

        Assert.AreSame( resumed.Task, completed );
        Assert.IsGreaterThanOrEqualTo( 2, ensureCalls );
    }

    /// <summary>Provider calls overlap up to the configured bound while dequeue remains serialized.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WithConcurrencyTwo_ProcessesTwoProviderCallsInParallel( ) {
        QueuedMessage<QueuedLookupRequest>[] messages = [
            CreateMessage( CreateRequest( LookupRequestType.IsrcLookup, "CONCURRENT-1" ) ),
            CreateMessage( CreateRequest( LookupRequestType.IsrcLookup, "CONCURRENT-2" ) )
        ];
        SetupNotRateLimited( );
        int nextMessage = -1;
        _ = _queueMock.Setup( queue => queue.DequeueAsync(
                It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                int index = Interlocked.Increment( ref nextMessage );
                return index < messages.Length ? messages[index] : null;
            } );

        int activeCalls = 0;
        int maximumActiveCalls = 0;
        TaskCompletionSource<bool> bothStarted = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource<bool> releaseCalls = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _lookupServiceMock.Setup( lookup => lookup.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .Returns( async ( string isrc ) => {
                int active = Interlocked.Increment( ref activeCalls );
                _ = Interlocked.Exchange( ref maximumActiveCalls, Math.Max( maximumActiveCalls, active ) );
                if (active == 2) _ = bothStarted.TrySetResult( true );
                _ = await releaseCalls.Task;
                _ = Interlocked.Decrement( ref activeCalls );
                return null;
            } );

        QueueProcessorBackgroundService service = CreateService( concurrency: 2 );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        Task observed = await Task.WhenAny(
            bothStarted.Task,
            Task.Delay( TimeSpan.FromSeconds( 5 ), TestContext.CancellationToken ) );
        Assert.AreSame( bothStarted.Task, observed, "The second provider call did not start while the first was in flight." );
        _ = releaseCalls.TrySetResult( true );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        Assert.AreEqual( 2, maximumActiveCalls );
        _queueMock.Verify( queue => queue.AcknowledgeAsync(
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Exactly( 2 ) );
    }

    #endregion

    /// <summary>An absent saga is acknowledged as stale without worker-side recreation.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_AbsentSagaIdentityMismatch_AcknowledgesStaleWithoutProviderMutation( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "MISMATCH" ) with { SagaId = "not-the-canonical-saga" };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        _ = _sagaManagerMock.Setup( s => s.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (LookupSagaState?)null );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => {
                _ = Interlocked.Increment( ref calls );
                return calls == 1 ? message : null;
            } );

        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( q => q.MoveToDlqAsync( message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Once );
        _sagaManagerMock.Verify( s => s.GetOrCreateAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ),
            It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( q => q.EnqueueAsync(
            It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>An existing saga with an initialized incomplete provider leg is accepted as a legitimate child.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessMessage_ExistingInitializedChild_AllowsProviderUpdate( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "CHILD" ) with {
            SagaInstanceToken = "child-instance"
        };
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        LookupSagaState child = new( ) {
            SagaId = request.SagaId,
            LookupKey = "different-root",
            LookupType = LookupRequestType.UpcLookup,
            LookupValue = "OTHER",
            InstanceToken = "child-instance",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [TestProvider] = new( TestProvider, false, false, null, null, null )
            }
        };
        _ = _sagaManagerMock.Setup( s => s.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) ).ReturnsAsync( child );
        SetupNotRateLimited( );
        SetupLookupSuccess( );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 250, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            request.SagaId, It.Is<ProviderLookupState>( state => state.IsComplete && state.IsSuccess ),
            "child-instance", It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.MoveToDlqAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A delivery fenced to a replaced saga generation is acknowledged as stale.</summary>
    [TestMethod]
    public async Task ProcessMessage_ExistingMismatchedUninitializedSaga_AcknowledgesStale( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "MISMATCHED" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        _ = _sagaManagerMock.Setup( s => s.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new LookupSagaState {
                SagaId = request.SagaId,
                LookupKey = "other-root",
                LookupType = LookupRequestType.UpcLookup,
                LookupValue = "OTHER",
                InstanceToken = "other-instance",
                ProviderStates = []
            } );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );
        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.MoveToDlqAsync( message.MessageId, It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( s => s.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>An absent canonical saga is not recreated by a worker delivery.</summary>
    [TestMethod]
    public async Task ProcessMessage_AbsentCanonicalSaga_AcknowledgesStale( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "ABSENT-CANONICAL" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        _ = _sagaManagerMock.Setup( s => s.GetAsync( request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (LookupSagaState?)null );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );
        _sagaManagerMock.Verify( s => s.GetOrCreateAsync(
            request.SagaId, It.IsAny<string>( ), request.LookupType, request.LookupValue,
            It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _sagaManagerMock.Verify( s => s.TryInitializeProviderStatesAsync(
            request.SagaId, It.IsAny<IEnumerable<SupportedProviders>>( ), "test-instance", It.IsAny<CancellationToken>( ) ), Times.Never );
        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.MoveToDlqAsync( It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A matching deterministic delivery whose persisted saga is unreadable is quarantined once.</summary>
    [TestMethod]
    public async Task ProcessMessage_MatchingUnreadableSaga_MovesDeliveryToDlq( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "CORRUPT-MATCHING" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        _ = _sagaManagerMock.Setup( manager => manager.GetAsync(
                request.SagaId, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new UnreadableSagaStateException( request.SagaId ) );
        int calls = 0;
        _ = _queueMock.Setup( queue => queue.DequeueAsync(
                It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );

        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );

        _queueMock.Verify( queue => queue.MoveToDlqAsync(
            message.MessageId,
            It.Is<string>( reason => reason.Contains( "unreadable", StringComparison.OrdinalIgnoreCase ) ),
            It.IsAny<CancellationToken>( ) ), Times.Once );
        _lookupServiceMock.VerifyNoOtherCalls( );
        _sagaManagerMock.Verify( manager => manager.TryUpdateProviderStateAsync(
            It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A provider update that loses the instance CAS acknowledges the stale delivery without retry.</summary>
    [TestMethod]
    public async Task ProcessMessage_WhenInstanceReplacedDuringProviderUpdate_AcknowledgesAndDrops( ) {
        QueuedLookupRequest request = CreateRequest( LookupRequestType.IsrcLookup, "REPLACED" );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage( request );
        SetupNotRateLimited( );
        SetupLookupSuccess( );
        SetupSagaNotComplete( request.SagaId );
        _ = _sagaManagerMock.Setup( s => s.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );
        int calls = 0;
        _ = _queueMock.Setup( q => q.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => calls++ == 0 ? message : null );
        List<string> processingStatuses = [];
        using MeterListener meterListener = new( );
        meterListener.InstrumentPublished = ( instrument, listener ) => {
            if (instrument.Name == "bridgebeats.queue.processing.duration") listener.EnableMeasurementEvents( instrument );
        };
        meterListener.SetMeasurementEventCallback<double>( ( _, _, tags, _ ) => {
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == "status") processingStatuses.Add( tag.Value?.ToString( ) ?? string.Empty );
            }
        } );
        meterListener.Start( );
        QueueProcessorBackgroundService service = CreateService( );
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await Task.Delay( 200, TestContext.CancellationToken );
        await cts.CancelAsync( );
        await service.StopAsync( CancellationToken.None );
        _queueMock.Verify( q => q.AcknowledgeAsync( message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Once );
        _queueMock.Verify( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        _subscriberMock.Verify( s => s.PublishAsync( It.IsAny<RedisChannel>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
        Assert.Contains( "stale", processingStatuses, "A provider-state CAS loss must not be reported as processing success." );
    }

    /// <summary>Post-commit acknowledgement recovery is capped and releases the local delivery fence.</summary>
    [TestMethod]
    public async Task RecoverCommittedAcknowledgement_WhenBrokerStaysUnavailable_IsBounded( ) {
        Mock<IQueueDeliveryTracker> tracker = _queueMock.As<IQueueDeliveryTracker>( );
        QueuedMessage<QueuedLookupRequest> message = CreateMessage(
            CreateRequest( LookupRequestType.IsrcLookup, "USRC12345678" ) );
        _ = _queueMock.Setup( queue => queue.AcknowledgeAsync(
                message.MessageId, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new RedisConnectionException(
                ConnectionFailureType.UnableToConnect,
                "ack unavailable" ) );
        List<TimeSpan> delays = [];
        QueueProcessorBackgroundService service = CreateService( );

        await service.RecoverCommittedAcknowledgementAsync(
            message,
            TestContext.CancellationToken,
            ( delay, _ ) => {
                delays.Add( delay );
                return Task.CompletedTask;
            } );

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds( 1 ), TimeSpan.FromSeconds( 2 ),
                TimeSpan.FromSeconds( 4 ), TimeSpan.FromSeconds( 8 ) },
            delays );
        _queueMock.Verify( queue => queue.AcknowledgeAsync(
            message.MessageId, It.IsAny<CancellationToken>( ) ), Times.Exactly( 4 ) );
        tracker.Verify( queue => queue.ReleaseDelivery( message.MessageId ), Times.Once );
    }

    #region Helper Methods

    /// <summary>
    /// Builds a <see cref="QueueProcessorBackgroundService"/> bound to <see cref="TestProvider"/> from
    /// the current dependency mocks.
    /// </summary>
    private QueueProcessorBackgroundService CreateService( IMusicLookupService? lookupService = null, int concurrency = 1 ) =>
        new(
            _redisMock.Object,
            _queueMock.Object,
            _rateLimitTrackerMock.Object,
            _sagaManagerMock.Object,
            lookupService ?? _lookupServiceMock.Object,
            TestProvider,
            _loggerMock.Object,
            new QueueSettings { DefaultProviderConcurrency = concurrency }
        );

    /// <summary>Queue seam exposing consumer-group assurance for hosted-loop recovery tests.</summary>
    private sealed class AssuringQueue : IRequestQueue<QueuedLookupRequest>, IConsumerGroupAssurance {
        public Func<CancellationToken, Task<QueuedMessage<QueuedLookupRequest>?>> DequeueRate { get; init; } = _ => Task.FromResult<QueuedMessage<QueuedLookupRequest>?>( null );
        public Func<CancellationToken, Task> Ensure { get; init; } = _ => Task.CompletedTask;
        public Task EnsureConsumerGroupsAsync( CancellationToken cancellationToken = default ) => Ensure( cancellationToken );
        public Task EnqueueAsync( QueuedLookupRequest request, QueuePriority priority, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<QueuedMessage<QueuedLookupRequest>?> DequeueAsync( CancellationToken cancellationToken = default ) => DequeueRate( cancellationToken );
        public Task<QueuedMessage<QueuedLookupRequest>?> DequeueAsync( IRateLimitTracker tracker, CancellationToken cancellationToken = default ) => DequeueRate( cancellationToken );
        public Task AcknowledgeAsync( string messageId, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task RequeueAsync( string messageId, TimeSpan? delay = null, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<QueueDepth> GetDepthAsync( CancellationToken cancellationToken = default ) => Task.FromResult( new QueueDepth( 0, 0, 0, 0 ) );
        public Task<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>> GetDlqMessagesAsync( int limit, CancellationToken cancellationToken = default ) => Task.FromResult<IReadOnlyList<QueuedMessage<QueuedLookupRequest>>>( [] );
        public Task RequeueFromDlqAsync( string messageId, QueuePriority priority, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task MoveToDlqAsync( string messageId, string reason, CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public Task<bool> DeleteFromDlqAsync( string messageId, CancellationToken cancellationToken = default ) => Task.FromResult( false );
    }

    /// <summary>
    /// Builds a <see cref="QueuedLookupRequest"/> for <see cref="TestProvider"/> with fresh request
    /// and saga ids, the given lookup type, and lookup value.
    /// </summary>
    private static QueuedLookupRequest CreateRequest( LookupRequestType lookupType, string lookupValue ) {
        string lookupKey = lookupType switch {
            LookupRequestType.SongIdLookup or LookupRequestType.AlbumIdLookup => LookupKeyBuilder.TypedKey( lookupType, TestProvider, lookupValue ),
            LookupRequestType.UriLookup => LookupKeyBuilder.UrlKey( lookupValue ),
            _ => $"{lookupType}:{lookupValue}"
        };
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );
        s_requestIdentities[sagaId] = (lookupKey, lookupType, lookupValue);
        return new( ) {
            RequestId = $"req-{Guid.NewGuid( ):N}",
            Provider = TestProvider,
            LookupType = lookupType,
            LookupValue = lookupValue,
            SagaId = sagaId,
            SagaInstanceToken = "test-instance",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Wraps a request in a <see cref="QueuedMessage{T}"/> with a fresh message id and the current
    /// enqueue time.
    /// </summary>
    private static QueuedMessage<QueuedLookupRequest> CreateMessage( QueuedLookupRequest request ) =>
        new( $"msg-{Guid.NewGuid( ):N}", request, DateTimeOffset.UtcNow );

    /// <summary>Builds a representative successful <see cref="MusicLookupResult"/> for lookup stubs.</summary>
    private static MusicLookupResult CreateLookupResult( ) => new( ) {
        ExternalId = "USRC12345678",
        Artist = "Test Artist",
        Title = "Test Song",
        URL = "https://example.com/track/123",
        ArtUrl = "https://example.com/art.jpg",
        IsAlbum = false,
        MarketRegion = "us"
    };

    /// <summary>Stubs the rate-limit tracker to report the provider/endpoint as not rate-limited.</summary>
    private void SetupNotRateLimited( ) {
        _ = _rateLimitTrackerMock.Setup( r => r.GetStateAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( new RateLimitState( false, null, null ) );
    }

    /// <summary>
    /// Stubs the rate-limit tracker to report the provider/endpoint as rate-limited, with the given
    /// time remaining and a corresponding retry-after instant.
    /// </summary>
    private void SetupRateLimited( TimeSpan timeRemaining ) {
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.Add( timeRemaining );
        _ = _rateLimitTrackerMock.Setup( r => r.GetStateAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<CancellationToken>( )
            ) )
            .ReturnsAsync( new RateLimitState( true, retryAfter, timeRemaining ) );
    }

    /// <summary>Stubs every lookup-service method to return a successful result.</summary>
    private void SetupLookupSuccess( ) {
        MusicLookupResult result = CreateLookupResult( );

        // Setup based on lookup type
        _ = _lookupServiceMock.Setup( l => l.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( result );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByUPCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( result );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( result );
        _ = _lookupServiceMock.Setup( l => l.GetInfoByIDAsync( It.IsAny<string>( ), It.IsAny<bool>( ) ) )
            .ReturnsAsync( result );
        _ = _lookupServiceMock.Setup( l => l.GetInfoAsync( It.IsAny<string>( ), It.IsAny<string>( ) ) )
            .ReturnsAsync( result );
    }

    /// <summary>
    /// Stubs the saga manager to return a saga where only Spotify is complete, so the saga is not
    /// yet whole (no <c>saga:completed</c> publication is expected).
    /// </summary>
    private void SetupSagaNotComplete( string sagaId ) {
        (string lookupKey, LookupRequestType lookupType, string lookupValue) = s_requestIdentities.GetValueOrDefault(
            sagaId, ($"{LookupRequestType.IsrcLookup}:test-value", LookupRequestType.IsrcLookup, "test-value") );
        LookupSagaState saga = new( ) {
            SagaId = sagaId,
            LookupKey = lookupKey,
            LookupType = lookupType,
            LookupValue = lookupValue,
            InstanceToken = "test-instance",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                // The provider leg is pending; the worker owns this delivery.
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify,
                    IsComplete: false,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: null,
                    ErrorMessage: null
                )
            }
        };
        _ = _sagaManagerMock.Setup( s => s.GetAsync( sagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( saga );
    }

    /// <summary>
    /// Stubs the saga manager to return a saga where all three providers are complete and successful,
    /// so the saga is whole (a <c>saga:completed</c> publication is expected).
    /// </summary>
    private void SetupSagaComplete( string sagaId ) {
        (string lookupKey, LookupRequestType lookupType, string lookupValue) = s_requestIdentities.GetValueOrDefault(
            sagaId, ($"{LookupRequestType.IsrcLookup}:test-value", LookupRequestType.IsrcLookup, "test-value") );
        LookupSagaState saga = new( ) {
            SagaId = sagaId,
            LookupKey = lookupKey,
            LookupType = lookupType,
            LookupValue = lookupValue,
            InstanceToken = "test-instance",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new(
                    SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: JsonSerializer.Serialize( CreateLookupResult( ), s_jsonOptions ),
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                ),
                [SupportedProviders.AppleMusic] = new(
                    SupportedProviders.AppleMusic,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: JsonSerializer.Serialize( CreateLookupResult( ), s_jsonOptions ),
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                ),
                [SupportedProviders.Tidal] = new(
                    SupportedProviders.Tidal,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: JsonSerializer.Serialize( CreateLookupResult( ), s_jsonOptions ),
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            }
        };
        _ = _sagaManagerMock.Setup( s => s.GetAsync( sagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( saga );
    }

    #endregion
}
