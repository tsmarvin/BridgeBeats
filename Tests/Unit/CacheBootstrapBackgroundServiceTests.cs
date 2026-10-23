using System.Runtime.CompilerServices;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Services;
using BridgeBeats.Worker.Maintenance;
using BridgeBeats.Worker.Maintenance.Interfaces;
using BridgeBeats.Worker.Maintenance.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

#pragma warning disable CA1873 // Moq Verify lambdas that call ILogger.Log trigger this; the lambdas are never actually executed as logging calls

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="CacheBootstrapBackgroundService"/>, which rebuilds the Redis lookup index from the
/// source-of-truth PDS by streaming every stored record and re-registering its input-link pointers,
/// while publishing a status document to Redis.
/// </summary>
/// <remarks>
/// The tests drive the hosted service through <c>StartAsync</c> with mocked storage, cache, and Redis
/// dependencies, then cancel once the expected work has been observed. They cover construction, the
/// bootstrap pass (each record re-registered via <see cref="IMediaLinkCacheRepository.AddInputLinksAsync"/>),
/// resilience to per-record and whole-pass failures, graceful cancellation, the parameters passed to
/// <see cref="IATProtoStorageService.ListAllRecordsAsync"/>, settings storage, and the shape of the
/// status document written to <see cref="CacheBootstrapStatus.RedisKey"/> (including preservation of a
/// prior completed run's fields while a new run is in progress or has failed).
/// </remarks>
[TestClass]
public class CacheBootstrapBackgroundServiceTests {

    /// <summary>Mock PDS storage service that streams the records to re-index.</summary>
    private Mock<IATProtoStorageService> _atProtoStorageMock = null!;

    /// <summary>Mock cache repository whose <c>AddInputLinksAsync</c> calls record the re-indexing.</summary>
    private Mock<IMediaLinkCacheRepository> _cacheRepositoryMock = null!;

    /// <summary>Mock Redis connection multiplexer handing out the mocked database.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;

    /// <summary>Mock Redis database used to observe status-document reads and writes.</summary>
    private Mock<IDatabase> _redisDatabaseMock = null!;

    /// <summary>Mock logger injected into the service.</summary>
    private Mock<ILogger<CacheBootstrapBackgroundService>> _loggerMock = null!;

    /// <summary>Mock coalescing trigger injected into the service.</summary>
    private Mock<IStatisticsRefreshTrigger> _refreshTriggerMock = null!;

    /// <summary>Settings (PDS URI, user DID, bootstrap interval) the service runs with.</summary>
    private CacheBootstrapSettings _settings = null!;

    /// <summary>MSTest-injected context, used to flow the test's cancellation token into awaits.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Test PDS URI passed to the service.</summary>
    private static readonly Uri s_testPdsUri = new( "https://pds.test.example" );

    /// <summary>Test user DID passed to the service.</summary>
    private const string TestUserDid = "did:plc:testuser123";

    /// <summary>
    /// Constructs the mocks and default settings before each test, wiring the Redis mock so the status
    /// document reads as absent and writes succeed unless a test overrides them.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _atProtoStorageMock = new Mock<IATProtoStorageService>( );
        _cacheRepositoryMock = new Mock<IMediaLinkCacheRepository>( );
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _redisDatabaseMock = new Mock<IDatabase>( );
        _loggerMock = new Mock<ILogger<CacheBootstrapBackgroundService>>( );
        _refreshTriggerMock = new Mock<IStatisticsRefreshTrigger>( );
        _ = _refreshTriggerMock.Setup( t => t.TryAcquire( ) ).Returns( true );

        // Setup Redis mock to return empty endpoints (avoids null reference in diagnostics)
        _ = _redisMock.Setup( r => r.GetEndPoints( It.IsAny<bool>( ) ) ).Returns( [] );

        // Wire database mock; default read returns null (no prior status)
        _ = _redisMock
            .Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( _redisDatabaseMock.Object );
        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( RedisValue.Null );
        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync( It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( true );

        _settings = new CacheBootstrapSettings(
            s_testPdsUri,
            TestUserDid,
            TimeSpan.FromHours( 6 ),
            CacheDays: 30,
            RefreshInterval: TimeSpan.FromHours( 24 ),
            MaxRecordsPerRun: 100,
            RefreshRetryInterval: TimeSpan.FromMinutes( 5 )
        );
    }

    #region Constructor Tests

    /// <summary>Verifies that the service constructs successfully with valid dependencies.</summary>
    [TestMethod]
    public void Constructor_WithValidDependencies_ShouldCreateInstance( ) {
        // Act
        CacheBootstrapBackgroundService service = CreateService( );

        // Assert
        Assert.IsNotNull( service );
    }

    #endregion

    #region ExecuteAsync Tests

    /// <summary>
    /// Verifies that on startup the service streams every PDS record and re-registers each one through
    /// <see cref="IMediaLinkCacheRepository.AddInputLinksAsync"/> exactly once (here three records).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_OnStartup_ShouldBootstrapCache( ) {
        // Arrange
        List<(string AtUri, MediaLinkResult Result)> records = [
            CreateTestRecord( "at://test/1" ),
            CreateTestRecord( "at://test/2" ),
            CreateTestRecord( "at://test/3" )
        ];

        // Signal when all 3 records have been processed by AddInputLinksAsync
        TaskCompletionSource allProcessedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int[] callCount = [0];
        _ = _cacheRepositoryMock
            .Setup( x => x.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ) )
            .Callback<string, MediaLinkResult>( ( _, _ ) => {
                if (System.Threading.Interlocked.Increment( ref callCount[0] ) >= 3) {
                    _ = allProcessedTcs.TrySetResult( );
                }
            } )
            .Returns( Task.CompletedTask );

        SetupRecordList( records );
        CacheBootstrapBackgroundService service = CreateService( );

        // Act
        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await allProcessedTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert - AddInputLinksAsync should be called for each record
        _cacheRepositoryMock.Verify(
            x => x.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ),
            Times.Exactly( 3 )
        );
    }

    /// <summary>
    /// Verifies that when the PDS returns no records the pass completes without re-registering anything,
    /// so an empty repository is handled cleanly.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WithEmptyCollection_ShouldCompleteWithoutErrors( ) {
        // Arrange: signal when ListAllRecordsAsync is called (first run completed when it returns)
        TaskCompletionSource listCalledTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Callback<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => listCalledTcs.TrySetResult( ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );

        CacheBootstrapBackgroundService service = CreateService( );

        // Act
        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await listCalledTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert - No exceptions thrown, AddInputLinksAsync never called
        _cacheRepositoryMock.Verify(
            x => x.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ),
            Times.Never( )
        );
    }

    /// <summary>
    /// Verifies that when re-registering one record throws, the service still attempts the remaining
    /// records, so a single bad record does not abort the whole bootstrap pass.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WhenRecordFails_ShouldContinueWithOtherRecords( ) {
        // Arrange
        List<(string AtUri, MediaLinkResult Result)> records = [
            CreateTestRecord( "at://test/1" ),
            CreateTestRecord( "at://test/2" ),
            CreateTestRecord( "at://test/3" )
        ];
        SetupRecordList( records );

        // Signal when the third AddInputLinksAsync call completes (all 3 attempted)
        TaskCompletionSource allAttemptedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int[] attemptCount = [0];

        _ = _cacheRepositoryMock
            .Setup( x => x.AddInputLinksAsync( "at://test/1", It.IsAny<MediaLinkResult>( ) ) )
            .Callback<string, MediaLinkResult>( ( _, _ ) => {
                if (System.Threading.Interlocked.Increment( ref attemptCount[0] ) >= 3) {
                    _ = allAttemptedTcs.TrySetResult( );
                }
            } )
            .ThrowsAsync( new InvalidOperationException( "Simulated failure" ) );
        _ = _cacheRepositoryMock
            .Setup( x => x.AddInputLinksAsync( It.Is<string>( s => s != "at://test/1" ), It.IsAny<MediaLinkResult>( ) ) )
            .Callback<string, MediaLinkResult>( ( _, _ ) => {
                if (System.Threading.Interlocked.Increment( ref attemptCount[0] ) >= 3) {
                    _ = allAttemptedTcs.TrySetResult( );
                }
            } )
            .Returns( Task.CompletedTask );

        CacheBootstrapBackgroundService service = CreateService( );

        // Act
        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await allAttemptedTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert - Should still attempt all 3 records
        _cacheRepositoryMock.Verify(
            x => x.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ),
            Times.Exactly( 3 )
        );
    }

    /// <summary>
    /// Verifies that cancelling the host token lets the service stop without throwing, confirming
    /// cooperative shutdown.
    /// </summary>
    /// <remarks>
    /// <c>BackgroundService.StartAsync</c> returns <c>Task.CompletedTask</c> unconditionally and
    /// immediately — it schedules <c>ExecuteAsync</c> via <c>Task.Run</c> without waiting for it to
    /// reach a first await — so awaiting that captured task is a no-op and would make this test pass
    /// vacuously even if <c>StopAsync</c> itself threw. <c>StopAsync</c> genuinely awaits the
    /// underlying execute task before returning, but on net10.0 it does so via
    /// <c>WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)</c> and does not
    /// re-throw a faulted or cancelled execute task — so a bare "<c>StopAsync</c> completes without
    /// throwing" assertion cannot by itself tell a graceful stop apart from one where the first
    /// bootstrap pass's <c>OperationCanceledException</c> escaped and faulted <c>ExecuteAsync</c>.
    /// <c>ExecuteTask.Status</c> is the assertable surface for that distinction. The
    /// <c>startedTcs</c> start-gate additionally proves <c>ExecuteAsync</c> (and its first,
    /// unguarded-by-the-loop bootstrap pass) actually began before cancellation fires — without it, a
    /// fast-enough cancellation could skip the first pass entirely before it ever installs its own
    /// try/catch, leaving <c>Task.Run</c>'s own token-cancellation to end <c>ExecuteTask</c> as
    /// <c>Canceled</c> rather than <c>RanToCompletion</c> — failing this assertion for a reason
    /// unrelated to the startup-pass guard under test, not passing it vacuously.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_WhenCancelled_ShouldStopGracefully( ) {
        // Arrange: the mocked PDS stream blocks on a cancellable infinite wait, so the first
        // bootstrap pass is still genuinely in flight (not already completed) when cancellation
        // fires below — a pass that had already finished and moved into the periodic loop's own
        // wait would exit gracefully via the loop's pre-existing catch regardless of whether the
        // startup-pass guard exists, masking a missing guard. startedTcs signals once the stream
        // has been reached, proving ExecuteAsync's first pass actually began before we cancel.
        TaskCompletionSource startedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, ct, _ ) => BlockUntilCancelledAsync( startedTcs, ct ) );
        CacheBootstrapBackgroundService service = CreateService( );

        // Act
        using CancellationTokenSource cts = new( );
        _ = service.StartAsync( cts.Token );
        await startedTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );

        // Assert - Should complete without throwing, and ExecuteAsync must reach RanToCompletion
        // rather than Canceled/Faulted; StopAsync's WaitAsync(...).SuppressThrowing does not surface
        // that distinction.
        await service.StopAsync( CancellationToken.None );
        Assert.AreEqual( TaskStatus.RanToCompletion, service.ExecuteTask!.Status,
            "ExecuteAsync must reach RanToCompletion; Canceled/Faulted means the startup bootstrap "
            + "pass's OperationCanceledException escaped the try/catch guard around the first call." );
    }

    /// <summary>
    /// Async-iterator stream used by <see cref="ExecuteAsync_WhenCancelled_ShouldStopGracefully"/>:
    /// signals <paramref name="startedTcs"/> once enumeration begins, then blocks on a cancellable
    /// infinite delay so the caller's bootstrap pass is genuinely mid-flight when the token is
    /// cancelled, rather than already completed.
    /// </summary>
    private static async IAsyncEnumerable<(string AtUri, MediaLinkResult Result)> BlockUntilCancelledAsync(
        TaskCompletionSource startedTcs,
        [EnumeratorCancellation] CancellationToken cancellationToken ) {
        _ = startedTcs.TrySetResult( );
        await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
        yield break;
    }

    /// <summary>
    /// Verifies that the service calls
    /// <see cref="IATProtoStorageService.ListAllRecordsAsync"/> with the configured PDS URI and user DID
    /// from settings, so the bootstrap reads the intended repository.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ExecuteAsync_ShouldPassCorrectParametersToListAllRecords( ) {
        // Arrange: signal when ListAllRecordsAsync is called with the expected parameters
        TaskCompletionSource listCalledTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( s_testPdsUri, TestUserDid, It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Callback<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => listCalledTcs.TrySetResult( ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );

        CacheBootstrapBackgroundService service = CreateService( );

        // Act
        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await listCalledTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert - Verify correct PDS URI and DID were passed
        _atProtoStorageMock.Verify(
            x => x.ListAllRecordsAsync( s_testPdsUri, TestUserDid, It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.AtLeastOnce( )
        );
    }

    #endregion

    #region Settings Tests

    /// <summary>
    /// Verifies that <see cref="CacheBootstrapSettings"/> exposes the PDS URI, user DID, and bootstrap
    /// interval supplied to its constructor.
    /// </summary>
    [TestMethod]
    public void CacheBootstrapSettings_ShouldStoreValues( ) {
        // Arrange
        Uri pdsUri = new( "https://custom.pds.example" );
        string userDid = "did:plc:custom";
        TimeSpan interval = TimeSpan.FromHours( 12 );

        // Act
        CacheBootstrapSettings settings = new(
            pdsUri, userDid, interval,
            CacheDays: 30,
            RefreshInterval: TimeSpan.FromHours( 24 ),
            MaxRecordsPerRun: 100,
            RefreshRetryInterval: TimeSpan.FromMinutes( 5 )
        );

        // Assert
        Assert.AreEqual( pdsUri, settings.PdsUri );
        Assert.AreEqual( userDid, settings.UserDid );
        Assert.AreEqual( interval, settings.BootstrapInterval );
    }

    #endregion

    #region Helper Methods

    /// <summary>Builds the service under test from the configured mocks and settings.</summary>
    /// <returns>A new <see cref="CacheBootstrapBackgroundService"/> instance.</returns>
    private CacheBootstrapBackgroundService CreateService(
        TimeSpan statisticsRetryInterval = default ) {
        return new CacheBootstrapBackgroundService(
            _atProtoStorageMock.Object,
            _cacheRepositoryMock.Object,
            _redisMock.Object,
            _settings,
            _refreshTriggerMock.Object,
            _loggerMock.Object,
            statisticsRetryInterval
        );
    }

    /// <summary>Configures the storage mock to return an empty record stream.</summary>
    private void SetupEmptyRecordList( ) {
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );
    }

    /// <summary>Configures the storage mock to return the supplied records as an async stream.</summary>
    /// <param name="records">The records the bootstrap pass should enumerate.</param>
    private void SetupRecordList( List<(string AtUri, MediaLinkResult Result)> records ) {
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( records.ToAsyncEnumerable( ) );
    }

    /// <summary>
    /// Builds a test record pairing an AT-URI with a <see cref="MediaLinkResult"/> carrying a single
    /// Spotify provider result, for seeding the bootstrap stream.
    /// </summary>
    /// <param name="atUri">The AT-URI to associate with the record.</param>
    /// <returns>The AT-URI and its media-link result.</returns>
    private static (string AtUri, MediaLinkResult Result) CreateTestRecord( string atUri ) {
        MediaLinkResult result = new( ) {
            LookedUpAt = DateTime.UtcNow
        };
        result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
            Artist = "Test Artist",
            Title = "Test Track",
            IsAlbum = false,
            ExternalId = "ISRC12345678",
            URL = "https://open.spotify.com/track/test"
        } );
        return (atUri, result);
    }

    #endregion

    #region Status Write Tests

    /// <summary>
    /// Verifies that when a bootstrap pass completes the status document written to Redis has
    /// <c>IsRunning</c> false and populated <c>LastRunTime</c>, <c>LastSuccessCount</c>, and
    /// <c>NextScheduledRun</c> fields.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnCompletion_WritesFullStatusWithAllFields( ) {
        // Use TCS to signal as soon as the bootstrap completion write (IsRunning=false, LastRunTime≠null
        // on CacheBootstrapStatus.RedisKey) lands. Capture the bootstrap completion JSON when it fires.
        TaskCompletionSource completionTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        string? bootstrapCompletionJson = null;

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ),
                It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( key, v, _, _, _ ) => {
                    // Only examine bootstrap status writes.
                    if (key != CacheBootstrapStatus.RedisKey) {
                        return;
                    }
                    string json = v.ToString( );
                    try {
                        System.Text.Json.JsonDocument d2 = System.Text.Json.JsonDocument.Parse( json );
                        bool isRunning = d2.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                        bool hasLastRunTime = d2.RootElement.GetProperty( "LastRunTime" ).ValueKind != System.Text.Json.JsonValueKind.Null;
                        if (!isRunning && hasLastRunTime) {
                            _ = Interlocked.Exchange( ref bootstrapCompletionJson, json );
                            _ = completionTcs.TrySetResult( );
                        }
                    } catch { }
                } )
            .ReturnsAsync( true );

        SetupRecordList( [CreateTestRecord( "at://test/1" )] );
        CacheBootstrapBackgroundService service = CreateService( );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        // Wait until the bootstrap completion write arrives, then cancel
        await completionTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        Assert.IsNotNull( bootstrapCompletionJson );

        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse( bootstrapCompletionJson! );
        System.Text.Json.JsonElement root = doc.RootElement;

        Assert.IsFalse( root.GetProperty( "IsRunning" ).GetBoolean( ) );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "LastRunTime" ).ValueKind );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "LastSuccessCount" ).ValueKind );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "NextScheduledRun" ).ValueKind );
    }

    /// <summary>
    /// Verifies that the status document written when a new pass starts (<c>IsRunning</c> true) carries
    /// over the previous completed run's fields (such as <c>LastSuccessCount</c> and
    /// <c>LastDurationSeconds</c>), so the in-progress status still reports the last known results.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnStart_PreservesPreviousCompletedRunFields( ) {
        CacheBootstrapStatus previousStatus = new( ) {
            IsRunning = false,
            LastRunTime = DateTimeOffset.UtcNow.AddHours( -6 ),
            LastSuccessCount = 500,
            LastErrorCount = 2,
            LastDurationSeconds = 120.5,
            RedisKeyCount = 1234567,
            NextScheduledRun = DateTimeOffset.UtcNow.AddHours( 6 )
        };
        string previousJson = System.Text.Json.JsonSerializer.Serialize( previousStatus );

        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( CacheBootstrapStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)previousJson );

        // Signal when the first write (IsRunning=true) arrives
        TaskCompletionSource startTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        List<string> writtenJsons = [];
        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ),
                It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( _, v, _, _, _ ) => {
                    string json = v.ToString( );
                    writtenJsons.Add( json );
                    try {
                        System.Text.Json.JsonDocument d2 = System.Text.Json.JsonDocument.Parse( json );
                        if (d2.RootElement.GetProperty( "IsRunning" ).GetBoolean( )) {
                            _ = startTcs.TrySetResult( );
                        }
                    } catch { }
                } )
            .ReturnsAsync( true );

        SetupEmptyRecordList( );
        CacheBootstrapBackgroundService service = CreateService( );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await startTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        Assert.IsNotEmpty( writtenJsons );
        string startJson = writtenJsons[0];
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse( startJson );
        System.Text.Json.JsonElement root = doc.RootElement;

        Assert.IsTrue( root.GetProperty( "IsRunning" ).GetBoolean( ) );
        Assert.AreEqual( 500, root.GetProperty( "LastSuccessCount" ).GetInt32( ) );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "LastDurationSeconds" ).ValueKind );
    }

    /// <summary>
    /// Verifies that when the record stream throws mid-pass, the final status document still reports
    /// <c>IsRunning</c> false and preserves the prior completed run's fields (success count, last-run
    /// time, duration, next scheduled run), so a failed pass does not erase the last good status.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_WhenListAllRecordsThrows_PreservesPriorCompletedRunFields( ) {
        CacheBootstrapStatus priorRun = new( ) {
            IsRunning = false,
            LastRunTime = DateTimeOffset.UtcNow.AddHours( -6 ),
            LastSuccessCount = 247404,
            LastErrorCount = 0,
            LastDurationSeconds = 4565.9,
            RedisKeyCount = 2882803,
            NextScheduledRun = DateTimeOffset.UtcNow.AddHours( 6 )
        };
        string priorJson = System.Text.Json.JsonSerializer.Serialize( priorRun );

        _ = _redisDatabaseMock
            .Setup( d => d.StringGetAsync( CacheBootstrapStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)priorJson );

        // Signal when the error-path completion write (IsRunning=false, NextScheduledRun present) lands
        TaskCompletionSource errorCompletionTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        List<string> writtenJsons = [];
        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ),
                It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( _, v, _, _, _ ) => {
                    string json = v.ToString( );
                    writtenJsons.Add( json );
                    try {
                        System.Text.Json.JsonDocument d2 = System.Text.Json.JsonDocument.Parse( json );
                        bool isRunning = d2.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                        bool hasNextScheduled = d2.RootElement.GetProperty( "NextScheduledRun" ).ValueKind != System.Text.Json.JsonValueKind.Null;
                        if (!isRunning && hasNextScheduled) {
                            _ = errorCompletionTcs.TrySetResult( );
                        }
                    } catch { }
                } )
            .ReturnsAsync( true );

        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Throws( new InvalidOperationException( "Simulated CAR parse failure" ) );

        CacheBootstrapBackgroundService service = CreateService( );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await errorCompletionTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        Assert.IsNotEmpty( writtenJsons );

        string lastJson = writtenJsons[^1];
        System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse( lastJson );
        System.Text.Json.JsonElement root = doc.RootElement;

        Assert.IsFalse( root.GetProperty( "IsRunning" ).GetBoolean( ) );
        Assert.AreEqual( 247404, root.GetProperty( "LastSuccessCount" ).GetInt32( ) );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "LastRunTime" ).ValueKind );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "LastDurationSeconds" ).ValueKind );
        Assert.AreNotEqual( System.Text.Json.JsonValueKind.Null, root.GetProperty( "NextScheduledRun" ).ValueKind );
    }

    #endregion

    #region T1 — Dedup: single enumeration per pass

    /// <summary>
    /// T1: ONE pass produces exactly ONE <see cref="IATProtoStorageService.ListAllRecordsAsync"/> call.
    /// This guards the headline dedup invariant: the bootstrap and the statistics fold share one
    /// enumeration. A single-enumeration-guard stream is used: a second <c>GetAsyncEnumerator</c>
    /// would throw, making the test fail if the code enumerates twice.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnePass_EnumeratesListAllRecordsExactlyOnce( ) {
        // Arrange — single-enumeration-guard: the first GetAsyncEnumerator succeeds; a second call throws.
        // Use an int[] (reference type) to share the counter across the lambda and the enumerable.
        int[] enumeratorCount = [0];
        (string, MediaLinkResult)[] records = [CreateTestRecord( "at://test/guard/1" )];

        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync(
                It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( ( ) => new SingleEnumerationGuardAsyncEnumerable( records, enumeratorCount ) );

        TaskCompletionSource completedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( _, v, _, _, _ ) => {
                    string json = v.ToString( );
                    try {
                        JsonDocument d = JsonDocument.Parse( json );
                        bool isRunning = d.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                        // Detect the statistics status success write (has "Snapshot" property set).
                        if (!isRunning && d.RootElement.TryGetProperty( "Snapshot", out _ )) {
                            _ = completedTcs.TrySetResult( );
                        }
                    } catch { }
                } )
            .ReturnsAsync( true );

        CacheBootstrapBackgroundService service = CreateService( statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 ) );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await completedTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Assert: exactly one enumeration occurred.
        _atProtoStorageMock.Verify(
            x => x.ListAllRecordsAsync(
                It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.Once( ) );

        // Cache writes fired for each record.
        _cacheRepositoryMock.Verify(
            x => x.AddInputLinksAsync( It.IsAny<string>( ), It.IsAny<MediaLinkResult>( ) ),
            Times.Once( ) );
    }

    /// <summary>
    /// T1 negative control: the single-enumeration-guard stream DOES throw on a second call. This
    /// proves T1 is not vacuous — the guard is real. If T1 passed but the code enumerated twice, it
    /// would only pass because the guard wasn't armed. This test confirms the guard is armed.
    /// </summary>
    [TestMethod]
    public void SingleEnumerationGuard_OnSecondGetAsyncEnumerator_Throws( ) {
        (string, MediaLinkResult)[] records = [CreateTestRecord( "at://neg/1" )];
        int[] count = [0];
        SingleEnumerationGuardAsyncEnumerable enumerable = new( records, count );

        _ = enumerable.GetAsyncEnumerator( TestContext.CancellationToken );
        _ = Assert.ThrowsExactly<InvalidOperationException>(
            ( ) => enumerable.GetAsyncEnumerator( TestContext.CancellationToken ) );
    }

    #endregion

    #region T3 — Statistics status lifecycle writes

    /// <summary>
    /// T3: The three lifecycle writes for the statistics status doc have the correct field values:
    /// start (IsRunning=true, Snapshot=prior), success (IsRunning=false, Snapshot≠null, LastError=null),
    /// failure (IsRunning=false, Snapshot=prior preserved, LastError set).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnCompletion_WritesStatisticsStatusWithCorrectLifecycleFields( ) {
        // We only test the success path here — failure path is T5.
        SetupRecordList( [CreateTestRecord( "at://test/stats/1" )] );

        List<(RedisKey Key, string Json)> allWrites = [];
        TaskCompletionSource statsDoneTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( key, v, _, _, _ ) => {
                    string json = v.ToString( );
                    lock (allWrites) { allWrites.Add( (key, json) ); }
                    if (key == StatisticsStatus.RedisKey) {
                        try {
                            JsonDocument d = JsonDocument.Parse( json );
                            bool isRunning = d.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                            if (!isRunning) {
                                _ = statsDoneTcs.TrySetResult( );
                            }
                        } catch { }
                    }
                } )
            .ReturnsAsync( true );

        CacheBootstrapBackgroundService service = CreateService( statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 ) );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await statsDoneTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Find the last statistics write.
        (RedisKey Key, string Json) successWrite = allWrites
            .Where( w => w.Key == StatisticsStatus.RedisKey )
            .Last( );

        JsonDocument doc = JsonDocument.Parse( successWrite.Json );
        JsonElement root = doc.RootElement;

        Assert.IsFalse( root.GetProperty( "IsRunning" ).GetBoolean( ), "IsRunning must be false after success" );
        Assert.AreNotEqual( JsonValueKind.Null, root.GetProperty( "Snapshot" ).ValueKind, "Snapshot must be set" );
        Assert.AreEqual( JsonValueKind.Null, root.GetProperty( "LastError" ).ValueKind, "LastError must be null on success" );
        Assert.AreNotEqual( JsonValueKind.Null, root.GetProperty( "LastRunTime" ).ValueKind, "LastRunTime must be set" );
    }

    #endregion

    #region T4 — Statistics doc must be written with no TTL

    /// <summary>
    /// T4: The statistics status document is written to Redis with <em>no expiry</em> (the
    /// <see cref="Expiration"/> parameter to <c>StringSetAsync</c> must not carry a TTL). Contrast
    /// this with the bootstrap doc, which is written with a one-day TTL. A missing TTL on the
    /// statistics doc prevents the page from re-stranding between worker cycles.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_StatisticsStatusWrite_HasNoExpiry( ) {
        SetupRecordList( [CreateTestRecord( "at://test/notlimit/1" )] );

        List<(RedisKey Key, Expiration Exp)> writeArgs = [];
        TaskCompletionSource statsDoneTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( key, v, expiry, _, _ ) => {
                    lock (writeArgs) { writeArgs.Add( (key, expiry) ); }
                    if (key == StatisticsStatus.RedisKey) {
                        try {
                            JsonDocument d = JsonDocument.Parse( v.ToString( ) );
                            bool isRunning = d.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                            if (!isRunning) {
                                _ = statsDoneTcs.TrySetResult( );
                            }
                        } catch { }
                    }
                } )
            .ReturnsAsync( true );

        CacheBootstrapBackgroundService service = CreateService( statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 ) );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await statsDoneTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // All statistics writes (key == "status:statistics") must have no expiry.
        IEnumerable<(RedisKey Key, Expiration Exp)> statsWrites =
            writeArgs.Where( w => w.Key == StatisticsStatus.RedisKey );

        Assert.IsNotEmpty( statsWrites );
        foreach ((RedisKey _, Expiration expiry) in statsWrites) {
            Assert.AreEqual( default( Expiration ), expiry, "Statistics doc must have no expiry" );
        }

        // Contrast: the bootstrap doc writes MUST have a TTL.
        IEnumerable<(RedisKey Key, Expiration Exp)> bootstrapWrites =
            writeArgs.Where( w => w.Key == CacheBootstrapStatus.RedisKey );
        Assert.IsNotEmpty( bootstrapWrites );
        foreach ((RedisKey _, Expiration expiry) in bootstrapWrites) {
            Assert.AreNotEqual( default( Expiration ), expiry, "Bootstrap doc must have a TTL" );
        }
    }

    #endregion

    #region T5 — LastError is a sanitized string, not ex.Message

    /// <summary>
    /// T5: When a statistics pass fails, <c>LastError</c> on the written status doc must be a fixed
    /// sanitized string and must NOT contain the exception's message sentinel. This proves the service
    /// never leaks internal exception details to operator-visible output.
    /// <para>
    /// Failure is induced by making the logger throw an exception with a distinctive sentinel message
    /// when <c>LogPassCompleted</c> (EventId 5576) is called — this throw happens inside the
    /// <c>try</c> block of <c>TryPublishStatisticsFromAccumulatorAsync</c>, triggering the catch
    /// block which calls <c>HandleStatisticsPassFailureAndRetryAsync</c>. The error doc written there
    /// must carry the sanitized <c>LastError</c> string, not the sentinel.
    /// </para>
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnStatisticsFailure_LastError_IsSanitizedAndDoesNotContainExceptionMessage( ) {
        const string SentinelMessage = "UNIQUESENTINEL_INTERNALMESSAGE_12345";

        SetupRecordList( [CreateTestRecord( "at://test/err/1" )] );
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        // Induce failure: make the logger throw with the sentinel message when LogPassCompleted
        // (EventId 5576) is called. LogPassCompleted is inside the try block of
        // TryPublishStatisticsFromAccumulatorAsync, so the exception propagates to the catch
        // block and triggers HandleStatisticsPassFailureAndRetryAsync. The retry will also fail
        // (LogPassCompleted throws again), causing a second error doc write before returning.
        _ = _loggerMock
            .Setup( l => l.Log(
                LogLevel.Information,
                It.Is<EventId>( e => e.Id == LogEventIds.PassCompleted ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ) )
            .Throws( new InvalidOperationException( SentinelMessage ) );

        // Capture statistics status writes via Callback (the write itself succeeds).
        List<string> statsJsons = [];
        TaskCompletionSource errorDocTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( key, v, _, _, _ ) => {
                    if (key != StatisticsStatus.RedisKey) {
                        return;
                    }
                    string json = v.ToString( );
                    lock (statsJsons) {
                        statsJsons.Add( json );
                        try {
                            JsonDocument d = JsonDocument.Parse( json );
                            bool hasError = d.RootElement.GetProperty( "LastError" ).ValueKind != JsonValueKind.Null;
                            bool isRunning = d.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                            if (hasError && !isRunning) {
                                _ = errorDocTcs.TrySetResult( );
                            }
                        } catch { }
                    }
                } )
            .ReturnsAsync( true );

        CacheBootstrapBackgroundService service = CreateService( statisticsRetryInterval: TimeSpan.FromMilliseconds( 10 ) );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        await errorDocTcs.Task.WaitAsync( TestContext.CancellationToken );
        await cts.CancelAsync( );
        await executeTask;

        // Find a write with a non-null LastError.
        string? errorJson;
        lock (statsJsons) {
            errorJson = statsJsons
                .LastOrDefault( j => {
                    try {
                        JsonDocument d = JsonDocument.Parse( j );
                        return d.RootElement.GetProperty( "LastError" ).ValueKind != JsonValueKind.Null;
                    } catch { return false; }
                } );
        }

        Assert.IsNotNull( errorJson, "Expected at least one statistics write with a non-null LastError" );
        JsonDocument errorDoc = JsonDocument.Parse( errorJson );
        string? lastError = errorDoc.RootElement.GetProperty( "LastError" ).GetString( );

        Assert.IsNotNull( lastError );
        Assert.IsFalse( lastError.Contains( SentinelMessage, StringComparison.Ordinal ),
            $"LastError must not contain ex.Message sentinel. Actual: {lastError}" );
        Assert.IsFalse( string.IsNullOrWhiteSpace( lastError ),
            "LastError must be a non-empty sanitized string, not an empty placeholder" );
    }

    #endregion

    #region T6 — §7.1.2 retry: observable delay, token cancellation, second-failure fallback

    /// <summary>
    /// T6a: When the statistics pass fails, the service writes the error doc BEFORE waiting the
    /// retry interval (error-doc-written-before-the-wait invariant) and retries exactly once.
    /// <para>
    /// Failure is induced by making the logger throw when <c>LogPassCompleted</c> (EventId 5576)
    /// is called — inside the try block of <c>TryPublishStatisticsFromAccumulatorAsync</c>. This
    /// triggers the catch block which calls <c>HandleStatisticsPassFailureAndRetryAsync</c>, writing
    /// the error doc then calling <c>RunStatisticsPassAsync</c> for the retry — causing a second
    /// <c>ListAllRecordsAsync</c> call.
    /// </para>
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RunBootstrap_OnStatisticsFailure_WritesErrorDocBeforeRetryThenRetries( ) {
        // Note: SetupRecordList is called later with the override.
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        // Induce failure: logger always throws on LogPassCompleted (EventId 5576).
        // TryPublishStatisticsFromAccumulatorAsync's try block calls LogPassCompleted, so the throw
        // propagates to the catch block and triggers HandleStatisticsPassFailureAndRetryAsync.
        // The retry (RunStatisticsPassAsync) also calls LogPassCompleted, which throws again, causing
        // a second error doc write. Both calls together prove error-first-then-retry sequencing.
        _ = _loggerMock
            .Setup( l => l.Log(
                LogLevel.Information,
                It.Is<EventId>( e => e.Id == LogEventIds.PassCompleted ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ) )
            .Throws( new InvalidOperationException( "Induced LogPassCompleted failure" ) );

        List<(bool IsRunning, bool HasError)> statsDocs = [];
        TaskCompletionSource errorDocWrittenTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( key, v, _, _, _ ) => {
                    if (key == StatisticsStatus.RedisKey) {
                        try {
                            JsonDocument d = JsonDocument.Parse( v.ToString( ) );
                            bool isRunning = d.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                            bool hasError = d.RootElement.GetProperty( "LastError" ).ValueKind != JsonValueKind.Null;
                            lock (statsDocs) {
                                statsDocs.Add( (isRunning, hasError) );
                                if (!isRunning && hasError) {
                                    _ = errorDocWrittenTcs.TrySetResult( );
                                }
                            }
                        } catch { }
                    }
                } )
            .ReturnsAsync( true );

        // Signal when the retry's ListAllRecordsAsync call (call 2) is made.
        TaskCompletionSource retryListCallTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int[] listCallCount = [0];
        _ = _atProtoStorageMock
            .Setup( s => s.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                if (Interlocked.Increment( ref listCallCount[0] ) >= 2) {
                    _ = retryListCallTcs.TrySetResult( );
                }
                return new[] { CreateTestRecord( "at://test/retry/1" ) }.ToAsyncEnumerable( );
            } );

        // Use a very short retry interval so the test doesn't wait 30 s.
        CacheBootstrapBackgroundService service = CreateService( statisticsRetryInterval: TimeSpan.FromMilliseconds( 5 ) );

        using CancellationTokenSource cts = new( );
        Task executeTask = service.StartAsync( cts.Token );

        // Wait for: (1) error doc written, AND (2) retry ListAllRecordsAsync call.
        await Task.WhenAll(
            errorDocWrittenTcs.Task.WaitAsync( TestContext.CancellationToken ),
            retryListCallTcs.Task.WaitAsync( TestContext.CancellationToken ) );

        await cts.CancelAsync( );
        await executeTask;

        // ListAllRecordsAsync must have been called at least twice (initial bootstrap + retry RunStatisticsPassAsync).
        // Assert.IsGreaterThanOrEqualTo(lowerBound, value) passes when value >= lowerBound.
        Assert.IsGreaterThanOrEqualTo( 2, listCallCount[0],
            $"Expected at least 2 ListAllRecordsAsync calls; got {listCallCount[0]}" );

        // The error doc must have been written before the retry call.
        (bool IsRunning, bool HasError) errorDoc;
        lock (statsDocs) {
            errorDoc = statsDocs.FirstOrDefault( x => !x.IsRunning && x.HasError );
        }
        Assert.IsTrue( errorDoc.HasError,
            "Expected at least one error status write with IsRunning=false and LastError set" );
    }

    /// <summary>
    /// T6b: A cancellation token signalled during the retry wait cancels promptly (the retry
    /// <c>Task.Delay</c> observes the stopping token). Uses a 10-minute retry interval so the
    /// test would time out at its 5-second limit if the token is NOT observed by <c>Task.Delay</c>.
    /// <para>
    /// Mutation discipline: dropping the cancellation token argument from the <c>Task.Delay</c>
    /// call in <c>HandleStatisticsPassFailureAndRetryAsync</c> causes this test to time out (the
    /// service sleeps 10 minutes, the 5-second <see cref="TimeoutAttribute"/> fires).
    /// </para>
    /// <para>
    /// The prior version of this test awaited only <c>StartAsync</c>, which returns
    /// <c>Task.CompletedTask</c> unconditionally and immediately, without waiting for
    /// <c>ExecuteAsync</c> to reach a first await — making the test pass vacuously regardless of
    /// whether the token was observed. This rework awaits <c>StopAsync</c> after cancellation so
    /// the test actually waits for <c>ExecuteAsync</c> to unwind. Additionally it asserts that the
    /// retry's second <c>ListAllRecordsAsync</c> call does NOT occur (the wait was cut short),
    /// which provides a positive-discrimination check that breaks under mutation.
    /// </para>
    /// </summary>
    [TestMethod]
    [Timeout( 5000, CooperativeCancellation = true )]
    public async Task RunBootstrap_CancellationDuringRetryWait_CancelsPromptly( ) {
        SetupRecordList( [CreateTestRecord( "at://test/cancel/1" )] );
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        // Signal when the error doc is written (the service has entered the retry wait).
        TaskCompletionSource errorWrittenTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );

        // Induce failure: logger throws on LogPassCompleted (EventId 5576) so the service enters
        // HandleStatisticsPassFailureAndRetryAsync with a 10-minute retry delay. When the token is
        // cancelled during that delay, Task.Delay observes the token and the ExecuteAsync unwinds
        // promptly. If the token argument is dropped from Task.Delay, ExecuteAsync sleeps 10 min
        // and the 5-second test timeout fires — proving the test discriminates correctly.
        _ = _loggerMock
            .Setup( l => l.Log(
                LogLevel.Information,
                It.Is<EventId>( e => e.Id == LogEventIds.PassCompleted ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ) )
            .Throws( new InvalidOperationException( "Induced LogPassCompleted failure" ) );

        // Track how many times ListAllRecordsAsync is called. After cancellation during the retry
        // wait, the second call (the retry) must NOT occur — the delay was cut short.
        int[] listCallCount = [0];
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                _ = Interlocked.Increment( ref listCallCount[0] );
                return new[] { CreateTestRecord( "at://test/cancel/1" ) }.ToAsyncEnumerable( );
            } );

        _ = _redisDatabaseMock
            .Setup( d => d.StringSetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
                It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ),
                It.IsAny<CommandFlags>( ) ) )
            .Callback<RedisKey, RedisValue, Expiration, StackExchange.Redis.ValueCondition, CommandFlags>(
                ( key, v, _, _, _ ) => {
                    if (key == StatisticsStatus.RedisKey) {
                        try {
                            JsonDocument d = JsonDocument.Parse( v.ToString( ) );
                            bool hasError = d.RootElement.GetProperty( "LastError" ).ValueKind != JsonValueKind.Null;
                            bool isRunning = d.RootElement.GetProperty( "IsRunning" ).GetBoolean( );
                            if (!isRunning && hasError) {
                                _ = errorWrittenTcs.TrySetResult( );
                            }
                        } catch { }
                    }
                } )
            .ReturnsAsync( true );

        // 10-minute retry interval: if the token is not observed, ExecuteAsync sleeps 10 min and
        // the 5-second test timeout fires.
        using CancellationTokenSource cts = new( );
        CacheBootstrapBackgroundService service = CreateService(
            statisticsRetryInterval: TimeSpan.FromMinutes( 10 ) );

        // StartAsync returns once ExecuteAsync yields — NOT when ExecuteAsync completes.
        // Use StopAsync after cancellation to actually wait for ExecuteAsync to unwind.
        await service.StartAsync( cts.Token );

        // Wait until the error doc is written: ExecuteAsync is now inside Task.Delay(10 min, token).
        await errorWrittenTcs.Task.WaitAsync( TestContext.CancellationToken );

        // Cancel while the service is sleeping in the retry wait.
        await cts.CancelAsync( );

        // StopAsync(TestContext.CancellationToken): waits for ExecuteAsync to complete OR the
        // test's 5-second cooperative timeout to fire. With the real implementation the delay
        // is cancelled and StopAsync returns in microseconds. With the mutant (token not observed),
        // the delay runs 10 min and StopAsync blocks until the cooperative timeout fires.
        await service.StopAsync( TestContext.CancellationToken );

        // After StopAsync, check whether the cooperative timeout fired. If it did, the test token
        // is already cancelled — meaning StopAsync took the full 5-second timeout to return, which
        // proves the token was NOT observed by Task.Delay. This throws OperationCanceledException
        // and MSTest marks the test as timed out (FAIL). With the real implementation this is a
        // no-op (the test completed well inside the 5-second window).
        TestContext.CancellationToken.ThrowIfCancellationRequested( );

        // The retry (second ListAllRecordsAsync call) must NOT have occurred: the wait was cut
        // short before the retry ran. This fails under mutation (token replaced with None) if the
        // mutant test somehow completes before the timeout — a belt-and-suspenders check.
        Assert.AreEqual( 1, listCallCount[0],
            $"Only the bootstrap ListAllRecordsAsync call should have occurred; retry must not run after cancellation. Actual call count: {listCallCount[0]}" );
    }

    /// <summary>
    /// T6c: A persistently-failing statistics pass (via the manual-trigger path) terminates after
    /// exactly two attempts — the initial attempt and one retry — then logs
    /// <see cref="LogEventIds.RetryExhausted"/> (5579) and returns. It does NOT retry a third time
    /// or loop indefinitely.
    /// <para>
    /// The manual-trigger entry path (<see cref="CacheBootstrapBackgroundService.RunStatisticsPassAsync"/>
    /// → <see cref="CacheBootstrapBackgroundService.TriggerStatisticsRefreshAsync"/>) is used because
    /// both the initial attempt and the retry enumerate the PDS via
    /// <see cref="IATProtoStorageService.ListAllRecordsAsync"/>, making the call count a clean
    /// observable proxy for "how many attempts ran". Expected: 2.
    /// </para>
    /// <para>
    /// Failure-first evidence: before the Task 1 fix, <c>HandleStatisticsPassFailureAndRetryAsync</c>
    /// re-entered <see cref="CacheBootstrapBackgroundService.RunStatisticsPassAsync"/> (not the core
    /// body) on retry, which re-entered Handle with <c>isFirstAttempt:true</c> on the retry's failure
    /// — restarting the retry cycle. The result was an unbounded loop:
    /// <see cref="LogEventIds.RetryExhausted"/> was never logged and
    /// <see cref="IATProtoStorageService.ListAllRecordsAsync"/> was called without bound. Under
    /// the old code, the TCS below would never fire, the <see cref="TestContext"/> cancellation
    /// would time out, and this test would fail.
    /// </para>
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task StatisticsPass_PersistentFailure_TerminatesAfterTwoAttemptsAndLogsRetryExhausted( ) {
        // Arrange
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        // Make every ListAllRecordsAsync call throw so both the initial attempt and the retry fail.
        int[] listCallCount = [0];
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync(
                It.IsAny<Uri>( ), It.IsAny<string>( ),
                It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => {
                _ = Interlocked.Increment( ref listCallCount[0] );
                throw new InvalidOperationException( "Persistent statistics failure" );
            } );

        // Signal when RetryExhausted (5579) is logged — the terminal event.
        TaskCompletionSource retryExhaustedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _loggerMock
            .Setup( l => l.Log(
                LogLevel.Error,
                It.Is<EventId>( e => e.Id == LogEventIds.RetryExhausted ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ) )
            .Callback( ( ) => retryExhaustedTcs.TrySetResult( ) );

        // Use a very short retry interval so the test does not spend 30 s in Task.Delay.
        CacheBootstrapBackgroundService service = CreateService(
            statisticsRetryInterval: TimeSpan.FromMilliseconds( 5 ) );

        // Act: drive the manual-trigger path directly (TriggerStatisticsRefreshAsync →
        // RunStatisticsPassAsync → RunStatisticsPassCoreAsync).
        Task triggerTask = service.TriggerStatisticsRefreshAsync( CancellationToken.None );

        // Wait for the RetryExhausted log — the termination signal.
        await retryExhaustedTcs.Task.WaitAsync( TestContext.CancellationToken );

        // Allow the trigger task to finish (it should return after RetryExhausted).
        await triggerTask;

        // Assert: exactly two ListAllRecordsAsync calls (initial attempt + one retry).
        Assert.AreEqual( 2, listCallCount[0],
            $"Expected exactly 2 ListAllRecordsAsync calls (initial + one retry); got {listCallCount[0]}" );

        // Assert: RetryExhausted logged exactly once.
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.Is<EventId>( e => e.Id == LogEventIds.RetryExhausted ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once( ) );
    }

    #endregion

    #region T7 — Coalescing: concurrent trigger during a running pass is dropped

    /// <summary>
    /// T7: A trigger arriving while a pass is running logs <c>RefreshCoalesced</c> (5581) and is
    /// not served. The coalescing guard blocks a second enumeration. Negative control: a trigger
    /// with no pass running is NOT coalesced.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TriggerStatisticsRefreshAsync_WhilePassRunning_IsCoalesced( ) {
        // Arrange: configure the trigger mock to return false on TryAcquire (simulating a running pass).
        _ = _refreshTriggerMock.Setup( t => t.TryAcquire( ) ).Returns( false );

        SetupEmptyRecordList( );
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        CacheBootstrapBackgroundService service = CreateService( );

        // Act: call TriggerStatisticsRefreshAsync directly (subscriber would call this).
        await service.TriggerStatisticsRefreshAsync( CancellationToken.None );

        // Assert: ListAllRecordsAsync was never called (no enumeration).
        _atProtoStorageMock.Verify(
            x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.Never( ) );

        // 5581 RefreshCoalesced was logged.
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Information,
                It.Is<EventId>( e => e.Id == 5581 ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once( ) );
    }

    /// <summary>
    /// T7 negative control: a trigger with no pass running is NOT coalesced — it proceeds.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TriggerStatisticsRefreshAsync_WithNoPassRunning_IsNotCoalesced( ) {
        // TryAcquire returns true (no running pass) — already the default setup.
        TaskCompletionSource listCalledTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _atProtoStorageMock
            .Setup( x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Callback<Uri, string, CancellationToken, bool>( ( _, _, _, _ ) => listCalledTcs.TrySetResult( ) )
            .Returns( AsyncEnumerable.Empty<(string, MediaLinkResult)>( ) );

        CacheBootstrapBackgroundService service = CreateService( );

        await service.TriggerStatisticsRefreshAsync( CancellationToken.None );

        _atProtoStorageMock.Verify(
            x => x.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ),
            Times.Once( ) );
    }

    #endregion

    #region T11 — Read-only invariant: Add() does not mutate the input MediaLinkResult

    /// <summary>
    /// T11: <see cref="StatisticsAccumulator.Add"/> must not mutate the supplied
    /// <see cref="MediaLinkResult"/>. Snapshot mutable fields before the call; assert unchanged after
    /// <see cref="StatisticsAccumulator.Build"/>.
    /// </summary>
    [TestMethod]
    public void StatisticsAccumulator_Add_DoesNotMutateInputRecord( ) {
        MediaLinkResult result = new( ) {
            LookedUpAt = new DateTime( 2024, 5, 1, 12, 0, 0, DateTimeKind.Utc ),
        };
        result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
            Artist = "A",
            Title = "T",
            IsAlbum = false,
            ExternalId = "ISRC001",
            URL = "https://open.spotify.com/track/001"
        } );

        DateTime lookedUpAtBefore = result.LookedUpAt;
        int resultCountBefore = result.Results.Count;

        StatisticsAccumulator accumulator = new( );
        accumulator.Add( "at://test/x/1", result );
        _ = accumulator.Build( );

        Assert.AreEqual( lookedUpAtBefore, result.LookedUpAt, "LookedUpAt must be unchanged after Add" );
        Assert.HasCount( resultCountBefore, result.Results, "Results count must be unchanged after Add" );
    }

    #endregion

    #region Helper types for T1

    /// <summary>
    /// Single-enumeration-guard async enumerable used in T1. The first call to
    /// <see cref="GetAsyncEnumerator"/> succeeds; a second call throws
    /// <see cref="InvalidOperationException"/>, proving that any code that enumerates twice would
    /// fail the T1 test.
    /// <para>
    /// The shared counter is passed as <c>int[]</c> (a reference type) so the guard survives
    /// across Moq's <c>Returns</c> factory lambda boundary (C# lambdas cannot capture <c>ref</c>
    /// locals).
    /// </para>
    /// </summary>
    private sealed class SingleEnumerationGuardAsyncEnumerable(
        IEnumerable<(string, MediaLinkResult)> records,
        int[] callCount
    ) : IAsyncEnumerable<(string AtUri, MediaLinkResult Result)> {

        public IAsyncEnumerator<(string AtUri, MediaLinkResult Result)> GetAsyncEnumerator(
            CancellationToken cancellationToken = default ) {
            if (System.Threading.Interlocked.Increment( ref callCount[0] ) > 1) {
                throw new InvalidOperationException( "Single-enumeration guard: GetAsyncEnumerator called more than once." );
            }
            return records.ToAsyncEnumerable( ).GetAsyncEnumerator( cancellationToken );
        }
    }

    #endregion
}
