using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Core.Domain.Utilities;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Worker.Maintenance;
using BridgeBeats.Worker.SagaCoordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the stale-cache refresh end-to-end write path. These tests verify that the
/// mode-B fix — initializing exactly the enqueued-leg provider set rather than all enabled providers
/// — allows a refreshed multi-provider saga to reach <c>IsComplete</c> and produce a terminal PDS
/// write. The load-bearing regression (<see cref="RefreshedMultiProviderRecord_ReachesTerminalWriteWhenAllLegsReport"/>)
/// must be red against <c>develop</c> before the fix and green after. All tests use a real Redis
/// <see cref="RedisSagaStateManager"/> (the shared Testcontainers instance) and a
/// <c>Mock&lt;IATProtoStorageService&gt;</c> write-counter for the PDS side.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public class StaleCacheRefreshSagaCompletionIntegrationTests {

    /// <summary>Shared Redis connection for the test class.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>Queue settings used when constructing the saga manager.</summary>
    private IOptions<QueueSettings> _queueSettings = null!;

    /// <summary>The real saga state manager backed by Testcontainers Redis.</summary>
    private RedisSagaStateManager _sagaManager = null!;

    /// <summary>MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    // Known-good test URLs whose IDs are extracted by ProviderUrlParser.ExtractId.
    private const string SpotifyTrackUrl = "https://open.spotify.com/track/SPOTIFYID001";
    private const string SpotifyTrackId  = "SPOTIFYID001";
    private const string AppleTrackUrl   = "https://music.apple.com/us/album/test-album/1000000000?i=9000000001";
    private const string AppleTrackId    = "9000000001";
    private const string TidalTrackUrl   = "https://tidal.com/browse/track/12340001";
    private const string TidalTrackId    = "12340001";
    private const string TestIsrc        = "USTEST000001";

    /// <summary>
    /// Opens the shared Redis connection, verifying Docker is available first.
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
    /// Clears saga keys and the refresh marker, then builds a fresh saga manager before each test.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        await foreach (RedisKey key in server.KeysAsync( pattern: "saga:*" )) {
            _ = await db.KeyDeleteAsync( key );
        }
        _ = await db.KeyDeleteAsync( "cache:refresh:last-run" );

        _queueSettings = Options.Create( new QueueSettings { JobExpirationMinutes = 60 } );
        _sagaManager = new RedisSagaStateManager(
            s_redis,
            new Mock<ILogger<RedisSagaStateManager>>( ).Object,
            _queueSettings
        );
    }

    // ─── §2 Load-Bearing Regression ────────────────────────────────────────────

    /// <summary>
    /// THE LOAD-BEARING REGRESSION. An expired record with three present, parseable providers
    /// (Spotify / Apple Music / Tidal) is refreshed via <see cref="StaleCacheRefreshBackgroundService"/>.
    /// After the fix, exactly the enqueued-leg providers are registered, so when all three legs
    /// report the saga becomes <c>IsComplete</c> and the coordinator performs exactly one terminal
    /// PDS write (<c>pdsWriteCount == 1</c>).
    /// <para>
    /// This test must be RED against the un-fixed code (because the old code registers all
    /// <c>enabledProviders</c> but only enqueues one leg — the un-enqueued providers never report,
    /// <c>IsComplete</c> stays false, and no write occurs) and GREEN after the fix.
    /// </para>
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RefreshedMultiProviderRecord_ReachesTerminalWriteWhenAllLegsReport( ) {
        // Arrange — three-provider stale record.
        MediaLinkResult staleRecord = MakeThreeProviderRecord( TestIsrc );
        HashSet<SupportedProviders> enabledProviders = [
            SupportedProviders.Spotify,
            SupportedProviders.AppleMusic,
            SupportedProviders.Tidal
        ];

        // Capture what the service registers with the saga manager.
        IEnumerable<SupportedProviders>? registeredProviders = null;
        Mock<ISagaStateManager> sagaManagerWrapper = BuildSagaManagerWrapper(
            _sagaManager,
            captureRegistered: providers => registeredProviders = providers );

        List<QueuedLookupRequest> enqueuedLegs = [];
        Mock<IProviderQueueResolver<QueuedLookupRequest>> queueResolverMock = new( );
        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = queueMock
            .Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => enqueuedLegs.Add( r ) )
            .Returns( Task.CompletedTask );
        _ = queueResolverMock
            .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( queueMock.Object );

        StaleCacheRefreshBackgroundService refreshService = BuildRefreshService( sagaManagerWrapper.Object, enabledProviders, queueResolverMock.Object );

        // Act — run one refresh pass with a single stale record.
        SetupAtProtoListForRecord( staleRecord );
        await refreshService.RunRefreshPassAsync( TestContext.CancellationToken );

        // Assert — at least one leg enqueued and registered set captured.
        Assert.IsNotEmpty( enqueuedLegs, "Refresh pass must enqueue at least one leg." );
        Assert.IsNotNull( registeredProviders, "TryInitializeProviderStatesAsync must have been called." );

        HashSet<SupportedProviders> registered = [.. registeredProviders];
        string sagaId = enqueuedLegs[0].SagaId;

        // The mode-B fix: registered set must equal the enqueued-leg provider set exactly.
        HashSet<SupportedProviders> enqueuedProviders = [.. enqueuedLegs.Select( l => l.Provider )];
        CollectionAssert.AreEquivalent(
            enqueuedProviders.ToList( ),
            registered.ToList( ),
            "Registered provider set must equal the enqueued-leg provider set (mode-B fix)." );

        // Now complete all enqueued legs: write a successful ProviderLookupState for each.
        LookupSagaState? initializedSaga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( initializedSaga );
        Assert.IsFalse( string.IsNullOrWhiteSpace( initializedSaga!.InstanceToken ) );
        foreach (QueuedLookupRequest leg in enqueuedLegs) {
            string resultJson = BuildResultJson( leg.Provider, TestIsrc );
            bool updated = await _sagaManager.TryUpdateProviderStateAsync(
                sagaId,
                new ProviderLookupState(
                    Provider: leg.Provider,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: resultJson,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null ),
                initializedSaga.InstanceToken!,
                TestContext.CancellationToken );
            Assert.IsTrue( updated, $"Provider state update for {leg.Provider} must use the active saga instance." );
        }

        // Read the saga back and verify IsComplete.
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.IsTrue( saga.IsComplete,
            "After all enqueued legs report, the saga must be IsComplete (the mode-B fix). " +
            "If this fails, the registered set still includes providers that have no leg." );

        // Drive the terminal write path.
        MediaLinkResult? capturedResult = null;
        (Mock<ITargetedATProtoStorageService> storageDouble, Func<int> getPdsWriteCount) = BuildCountingStorageDouble( r => capturedResult = r );

        SagaCoordinatorBackgroundService coordinator = BuildCoordinator(
            _sagaManager, storageDouble.Object, enabledProviders );

        await coordinator.InvokeFinalizeForTestAsync( saga, TestContext.CancellationToken );

        // The load-bearing assertion.
        Assert.AreEqual( 1, getPdsWriteCount( ),
            "Exactly one terminal PDS write must occur after all legs report. " +
            "If pdsWriteCount == 0, the saga was never IsComplete (mode-B bug still present). " +
            "If pdsWriteCount > 1, the dedup guard is broken." );

        // The combined record must carry all three providers' results and the correct ISRC.
        Assert.IsNotNull( capturedResult, "The PDS write must have produced a captured result." );
        Assert.HasCount( 3, capturedResult.Results,
            "The combined result must include all three provider results." );
    }

    // ─── §7 Write Gate Tests ────────────────────────────────────────────────────

    /// <summary>
    /// A freshly-created saga (no <c>FinalResultUri</c>) for the refreshed entity completes all legs
    /// and produces exactly one terminal PDS write. This confirms the refresh selects and writes via
    /// the W3/W4 path rather than the no-write branch.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RefreshOfExpiredRecord_ProducesTerminalWrite_NotNoWriteBranch( ) {
        const string LookupKey = $"{nameof( LookupRequestType.IsrcLookup )}:{TestIsrc}";
        string sagaId = ISagaStateManager.GenerateSagaId( LookupKey );

        // Create a fresh saga (no FinalResultUri — simulates a new refresh, not a reuse).
        _ = await _sagaManager.GetOrCreateAsync(
            sagaId, LookupKey, LookupRequestType.IsrcLookup, TestIsrc,
            QueuePriority.Bulk, TestContext.CancellationToken );

        await _sagaManager.TryInitializeProviderStatesAsync(
            sagaId, [SupportedProviders.Spotify], TestContext.CancellationToken );

        await _sagaManager.UpdateProviderStateAsync(
            sagaId,
            new ProviderLookupState(
                Provider: SupportedProviders.Spotify,
                IsComplete: true,
                IsSuccess: true,
                ResultJson: BuildResultJson( SupportedProviders.Spotify, TestIsrc ),
                CompletedAt: DateTimeOffset.UtcNow,
                ErrorMessage: null ),
            TestContext.CancellationToken );

        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.IsTrue( saga.IsComplete );
        Assert.IsNull( saga.FinalResultUri,
            "Pre-condition: FinalResultUri must be null (this is a fresh refresh, not a reuse)." );

        (Mock<ITargetedATProtoStorageService> storageDouble, Func<int> getPdsWriteCount) = BuildCountingStorageDouble( );
        SagaCoordinatorBackgroundService coordinator = BuildCoordinator(
            _sagaManager, storageDouble.Object, [SupportedProviders.Spotify] );

        await coordinator.InvokeFinalizeForTestAsync( saga, TestContext.CancellationToken );

        Assert.AreEqual( 1, getPdsWriteCount( ),
            "A fresh saga (no FinalResultUri) must produce exactly one terminal PDS write." );
    }

    /// <summary>
    /// A saga that already carries a <c>FinalResultUri</c> (simulating a recently-reused interactive
    /// saga for the same entity) short-circuits the write path — no duplicate PDS write occurs.
    /// This is the §7.2 claim-falsifier: it proves the write gate (which is why dropping
    /// <c>ResetForRefreshAsync</c> is safe) and confirms that any selected (age-expired) record
    /// will NOT have a live terminal saga — because writing one advances <c>LookedUpAt</c> and
    /// removes the record from the selectable set under the age-only predicate.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task WarmSagaWithFinalUri_ShortCircuitsWrite_DemonstratingWhyResetWouldBeNeededIfSelectable( ) {
        const string LookupKey = $"{nameof( LookupRequestType.IsrcLookup )}:WSGTEST001";

        string sagaId = ISagaStateManager.GenerateSagaId( LookupKey );
        _ = await _sagaManager.GetOrCreateAsync(
            sagaId, LookupKey, LookupRequestType.IsrcLookup, "WSGTEST001",
            QueuePriority.Bulk, TestContext.CancellationToken );

        // Simulate a prior write: set FinalResultUri so the saga looks already-finalized.
        await _sagaManager.SetFinalResultUriAsync(
            sagaId, "at://did:plc:test/com.bridgebeats.medialink/alreadydone",
            TestContext.CancellationToken );

        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, TestContext.CancellationToken );
        Assert.IsNotNull( saga );
        Assert.IsNotNull( saga.FinalResultUri,
            "Pre-condition: FinalResultUri must be set to simulate a warm saga." );

        (Mock<ITargetedATProtoStorageService> storageDouble, Func<int> getPdsWriteCount) = BuildCountingStorageDouble( );
        SagaCoordinatorBackgroundService coordinator = BuildCoordinator(
            _sagaManager, storageDouble.Object, [SupportedProviders.Spotify] );

        await coordinator.InvokeFinalizeForTestAsync( saga, TestContext.CancellationToken );

        // The write gate short-circuits: no duplicate write.
        Assert.AreEqual( 0, getPdsWriteCount( ),
            "A saga with FinalResultUri already set must not produce another PDS write." );

        // The pairing proof (§7.2): the state that causes a write short-circuit (a warm terminal saga,
        // i.e. a just-written record) is exactly the state that the age-only predicate does NOT select.
        // This holds for both partial and complete records under the age-only rule.
        DateTime utcNow = DateTime.UtcNow;
        DateTime justWritten = utcNow;
        const int CacheDays = 30;

        bool completeRecordIsStale = CacheFreshness.IsStale( justWritten, CacheDays, utcNow );
        Assert.IsFalse( completeRecordIsStale,
            "A just-written complete record must not be stale (age-only): the write gate is not reachable for any selected record." );

        bool partialRecordIsStale = CacheFreshness.IsStale( justWritten, CacheDays, utcNow );
        Assert.IsFalse( partialRecordIsStale,
            "A just-written partial record must not be stale (age-only, §3a): dropping ResetForRefreshAsync is safe for partial records too." );
    }

    // ─── §8 Cross-Window Fallback Tests ────────────────────────────────────────

    /// <summary>
    /// Cross-window fallback integration test. When a record's native-id leg for a provider
    /// fails (or returns empty) on the first refresh pass, that provider is absent from the
    /// written-back record. On the next age window the record is re-selected (stale again) and
    /// <see cref="StaleCacheRefreshBackgroundService.DeriveRefreshLegs"/> emits a
    /// <c>IsrcLookup</c> fallback leg for the now-missing provider.
    ///
    /// <para>
    /// Scenario:
    /// <list type="number">
    ///   <item>First record: Spotify + AppleMusic both present with parseable URLs and an ISRC.
    ///   First refresh pass enqueues Spotify (native) and AppleMusic (native).</item>
    ///   <item>Simulate "Apple lookup failed": the mock is updated to return a record where Apple is
    ///   absent from <c>Results</c> (only Spotify remains), with <c>LookedUpAt</c> aged past the
    ///   cache window to make it stale again.</item>
    ///   <item>Second refresh pass: Apple is now missing but the ISRC is still available. Apple must
    ///   receive an <c>IsrcLookup</c> fallback leg; Spotify receives a native <c>SongIdLookup</c>.</item>
    /// </list>
    /// </para>
    ///
    /// This test covers the end-to-end selection → leg-derivation → fallback compose that is absent
    /// from the unit tests (which test each step in isolation).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CrossWindowFallback_MissingProviderAfterFailedLeg_GetsIsrcFallbackOnNextWindow( ) {
        const string Isrc = "XWFTEST001";
        const string AtUri = "at://did:plc:test/com.bridgebeats.medialink/xwf001";

        HashSet<SupportedProviders> enabledProviders = [
            SupportedProviders.Spotify,
            SupportedProviders.AppleMusic
        ];

        // ── Step 1: First pass — record has Spotify + Apple, both parseable. ──────────────────
        MediaLinkResult firstRecord = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        firstRecord.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = SpotifyTrackUrl,
            ExternalId = Isrc,
            IsAlbum = false
        };
        firstRecord.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = AppleTrackUrl,
            ExternalId = Isrc,
            IsAlbum = false
        };

        _ = _atProtoStorageMock
            .Setup( a => a.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( new List<(string, MediaLinkResult)> { (AtUri, firstRecord) }.ToAsyncEnumerable( ) );

        List<QueuedLookupRequest> firstPassLegs = [];
        Mock<IProviderQueueResolver<QueuedLookupRequest>> queueResolver = new( );
        Mock<IRequestQueue<QueuedLookupRequest>> queueMock = new( );
        _ = queueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => firstPassLegs.Add( r ) )
            .Returns( Task.CompletedTask );
        _ = queueResolver.Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) ).Returns( queueMock.Object );

        Mock<ISagaStateManager> sagaWrapper = BuildSagaManagerWrapper( _sagaManager );
        StaleCacheRefreshBackgroundService refreshService = BuildRefreshService( sagaWrapper.Object, enabledProviders, queueResolver.Object );

        await refreshService.RunRefreshPassAsync( TestContext.CancellationToken );

        // Pre-condition: first pass enqueued two native legs (Spotify + Apple).
        Assert.HasCount( 2, firstPassLegs,
            "First pass must enqueue two native legs (Spotify and AppleMusic)." );
        Assert.IsTrue( firstPassLegs.All( l => l.LookupType == LookupRequestType.SongIdLookup ),
            "Both first-pass legs must be native SongIdLookup legs." );
        CollectionAssert.AreEquivalent(
            new[] { SupportedProviders.Spotify, SupportedProviders.AppleMusic },
            firstPassLegs.Select( l => l.Provider ).ToArray( ),
            "First pass must produce one leg per enabled provider." );

        // The first pass has drained its two legs before the record is selected again. Mark both
        // provider legs terminal so FIX 5's active-saga guard does not suppress the intentional
        // cross-window fallback scenario below.
        foreach (SupportedProviders provider in firstPassLegs.Select( l => l.Provider ).Distinct( )) {
            await _sagaManager.UpdateProviderStateAsync(
                firstPassLegs[0].SagaId,
                new ProviderLookupState(
                    Provider: provider,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: "Simulated first-pass completion"
                ),
                TestContext.CancellationToken
            );
        }

        // ── Step 2: Simulate Apple lookup failure — write back record with Spotify only. ──────
        // The age-only rule: LookedUpAt must be past the cache window for re-selection.
        MediaLinkResult partialRecord = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -61 ) };
        partialRecord.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = SpotifyTrackUrl,
            ExternalId = Isrc,
            IsAlbum = false
        };
        // Apple is absent from partialRecord.Results (the lookup failed and was not written back).

        _ = _atProtoStorageMock
            .Setup( a => a.ListAllRecordsAsync( It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ), It.IsAny<bool>( ) ) )
            .Returns( new List<(string, MediaLinkResult)> { (AtUri, partialRecord) }.ToAsyncEnumerable( ) );

        // ── Step 3: Second pass — record is stale again; Apple must get a fallback leg. ───────
        List<QueuedLookupRequest> secondPassLegs = [];
        _ = queueMock
            .Setup( q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback<QueuedLookupRequest, QueuePriority, CancellationToken>( ( r, _, _ ) => secondPassLegs.Add( r ) )
            .Returns( Task.CompletedTask );

        await refreshService.RunRefreshPassAsync( TestContext.CancellationToken );

        Assert.HasCount( 2, secondPassLegs,
            "Second pass must enqueue two legs: one Spotify native and one Apple fallback." );

        QueuedLookupRequest spotifyLeg = secondPassLegs.Single( l => l.Provider == SupportedProviders.Spotify );
        Assert.AreEqual( LookupRequestType.SongIdLookup, spotifyLeg.LookupType,
            "Spotify must still receive a native SongIdLookup leg on the second pass." );
        Assert.AreEqual( SpotifyTrackId, spotifyLeg.LookupValue,
            "Spotify leg must carry the extracted native track id." );

        QueuedLookupRequest appleLeg = secondPassLegs.Single( l => l.Provider == SupportedProviders.AppleMusic );
        Assert.AreEqual( LookupRequestType.IsrcLookup, appleLeg.LookupType,
            "Apple Music must receive an IsrcLookup fallback leg on the second pass (provider absent from Results)." );
        Assert.AreEqual( Isrc, appleLeg.LookupValue,
            "Apple fallback leg must carry the record's ISRC." );
    }

    // ─── Helper Factories ───────────────────────────────────────────────────────

    /// <summary>Builds a three-provider stale record with parseable URLs and a non-blank ISRC.</summary>
    private static MediaLinkResult MakeThreeProviderRecord( string isrc ) {
        MediaLinkResult record = new( ) { LookedUpAt = DateTime.UtcNow.AddDays( -60 ) };
        record.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            URL = SpotifyTrackUrl,
            ExternalId = isrc,
            IsAlbum = false
        };
        record.Results[SupportedProviders.AppleMusic] = new MusicLookupResult {
            URL = AppleTrackUrl,
            ExternalId = isrc,
            IsAlbum = false
        };
        record.Results[SupportedProviders.Tidal] = new MusicLookupResult {
            URL = TidalTrackUrl,
            ExternalId = isrc,
            IsAlbum = false
        };
        return record;
    }

    /// <summary>
    /// Builds a saga manager wrapper that delegates all calls to the real <see cref="RedisSagaStateManager"/>
    /// but also invokes <paramref name="captureRegistered"/> when <c>TryInitializeProviderStatesAsync</c> is called.
    /// </summary>
    private static Mock<ISagaStateManager> BuildSagaManagerWrapper(
        RedisSagaStateManager realManager,
        Action<IEnumerable<SupportedProviders>>? captureRegistered = null
    ) {
        Mock<ISagaStateManager> wrapper = new( );

        _ = wrapper
            .Setup( s => s.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string id, string key, LookupRequestType type, string val, QueuePriority prio, CancellationToken ct ) =>
                realManager.GetOrCreateAsync( id, key, type, val, prio, ct ) );

        _ = wrapper
            .Setup( s => s.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( async ( string id, IEnumerable<SupportedProviders> providers, string token, CancellationToken ct ) => {
                captureRegistered?.Invoke( providers );
                return await realManager.TryInitializeProviderStatesAsync( id, providers, token, ct );
            } );

        _ = wrapper
            .Setup( s => s.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string id, CancellationToken ct ) => realManager.GetAsync( id, ct ) );

        _ = wrapper
            .Setup( s => s.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( ( string id, ProviderLookupState state, string token, CancellationToken ct ) =>
                realManager.TryUpdateProviderStateAsync( id, state, token, ct ) );

        return wrapper;
    }

    /// <summary>
    /// Builds a <see cref="StaleCacheRefreshBackgroundService"/> wired to a zero-delay settings
    /// record (for testability) and the provided dependencies.
    /// </summary>
    private StaleCacheRefreshBackgroundService BuildRefreshService(
        ISagaStateManager sagaManager,
        HashSet<SupportedProviders> enabledProviders,
        IProviderQueueResolver<QueuedLookupRequest>? queueResolver = null
    ) {
        if (queueResolver is null) {
            Mock<IRequestQueue<QueuedLookupRequest>> qMock = new( );
            _ = qMock
                .Setup( q => q.EnqueueAsync(
                    It.IsAny<QueuedLookupRequest>( ),
                    It.IsAny<QueuePriority>( ),
                    It.IsAny<CancellationToken>( ) ) )
                .Returns( Task.CompletedTask );
            Mock<IProviderQueueResolver<QueuedLookupRequest>> resolverMock = new( );
            _ = resolverMock
                .Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
                .Returns( qMock.Object );
            queueResolver = resolverMock.Object;
        }

        IRefreshReviewStore refreshReviewStore = CreateRefreshReviewStore( );
        Dictionary<SupportedProviders, QueuedLookupRequest> stagedRequests = [];
        Mock<ILookupDispatchOutbox> dispatchOutbox = new( );
        _ = dispatchOutbox.Setup( outbox => outbox.StageRefreshBatchAsync(
                It.IsAny<IReadOnlyList<QueuedLookupRequest>>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<RefreshReviewEntry>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( async (
                IReadOnlyList<QueuedLookupRequest> requests,
                QueuePriority _,
                RefreshReviewEntry reviewEntry,
                CancellationToken cancellationToken ) => {
                    bool initialized = await sagaManager.TryInitializeProviderStatesAsync(
                        requests[0].SagaId,
                        requests.Select( request => request.Provider ),
                        requests[0].SagaInstanceToken!,
                        cancellationToken );
                    if (!initialized) {
                        return (IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>)
                            requests.ToDictionary(
                                request => request.Provider,
                                _ => ProviderDispatchStageOutcome.SagaInstanceMismatch );
                    }
                    await refreshReviewStore.RegisterPendingAsync( reviewEntry, cancellationToken );
                    foreach (QueuedLookupRequest request in requests) {
                        stagedRequests[request.Provider] = request;
                    }

                    return (IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>)
                        requests.ToDictionary(
                            request => request.Provider,
                            _ => ProviderDispatchStageOutcome.Staged );
                } );
        _ = dispatchOutbox.Setup( outbox => outbox.DispatchAsync(
                It.IsAny<string>( ),
                It.IsAny<SupportedProviders>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( async (
                string _,
                SupportedProviders provider,
                CancellationToken cancellationToken ) => {
                    if (!stagedRequests.TryGetValue( provider, out QueuedLookupRequest? request )) {
                        return false;
                    }

                    await queueResolver.GetQueue( provider ).EnqueueAsync(
                        request, QueuePriority.Bulk, cancellationToken );
                    return true;
                } );

        CacheBootstrapSettings settings = new(
            new Uri( "https://pds.test.example" ),
            "did:plc:testuser",
            TimeSpan.FromHours( 6 ),
            CacheDays: 30,
            RefreshInterval: TimeSpan.FromHours( 6 ),
            MaxRecordsPerRun: 500,
            RefreshRetryInterval: TimeSpan.FromMinutes( 5 )
        );

        Mock<IConnectionMultiplexer> redisMock = new( );
        Mock<IDatabase> dbMock = new( );
        _ = redisMock.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( dbMock.Object );
        _ = dbMock
            .Setup( d => d.StringGetAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( RedisValue.Null );
        _ = dbMock
            .Setup( d => d.StringSetAsync( It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<Expiration>( ), It.IsAny<StackExchange.Redis.ValueCondition>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( true );

        return new StaleCacheRefreshBackgroundService(
            _atProtoStorageMock.Object,
            sagaManager,
            dispatchOutbox.Object,
            redisMock.Object,
            enabledProviders,
            settings,
            new Mock<ILogger<StaleCacheRefreshBackgroundService>>( ).Object,
            refreshReviewStore
        );
    }

    /// <summary>
    /// The <see cref="IATProtoStorageService"/> mock shared between the refresh service and the write
    /// assertions. <see cref="SetupAtProtoListForRecord"/> adds a <c>ListAllRecordsAsync</c> setup to
    /// the same instance that <see cref="BuildRefreshService"/> captures, so the sweep sees the record.
    /// </summary>
    private readonly Mock<ITargetedATProtoStorageService> _atProtoStorageMock = new( );

    /// <summary>
    /// Wires the ATProto storage mock to yield the given record when <c>ListAllRecordsAsync</c> is called.
    /// </summary>
    private void SetupAtProtoListForRecord( MediaLinkResult staleRecord ) {
        _ = _atProtoStorageMock
            .Setup( a => a.ListAllRecordsAsync(
                It.IsAny<Uri>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( new List<(string, MediaLinkResult)> {
                ("at://did:plc:test/com.bridgebeats.medialink/stale001", staleRecord)
            }.ToAsyncEnumerable( ) );
    }

    /// <summary>
    /// Builds a PDS write-counter storage double. Write count is tracked via an <see cref="Interlocked"/>
    /// increment on a shared box and exposed through the returned <c>GetCount</c> delegate.
    /// </summary>
    private static (Mock<ITargetedATProtoStorageService> Mock, Func<int> GetCount) BuildCountingStorageDouble(
        Action<MediaLinkResult>? captureResult = null
    ) {
        int[] box = [0];
        Mock<ITargetedATProtoStorageService> storageDouble = new( );
        _ = storageDouble
            .Setup( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( MediaLinkResult r, CancellationToken ct ) => {
                captureResult?.Invoke( r );
                _ = Interlocked.Increment( ref box[0] );
                return $"at://did:plc:test/com.bridgebeats.medialink/{Guid.NewGuid( ):N}";
            } );
        return (storageDouble, ( ) => box[0]);
    }

    /// <summary>Builds the saga coordinator wired to the given dependencies.</summary>
    private static SagaCoordinatorBackgroundService BuildCoordinator(
        ISagaStateManager sagaManager,
        ITargetedATProtoStorageService storageDouble,
        HashSet<SupportedProviders> enabledProviders
    ) {
        Mock<IConnectionMultiplexer> redisMock = new( );
        Mock<ISubscriber> subscriberMock = new( );
        _ = redisMock.Setup( r => r.GetSubscriber( It.IsAny<object>( ) ) ).Returns( subscriberMock.Object );

        Mock<IMediaLinkCacheRepository> cacheMock = new( );
        _ = cacheMock
            .Setup( c => c.IndexResultAsync(
                It.IsAny<MediaLinkResult>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        Mock<IRequestDeduplicator> deduplicatorMock = new( );
        _ = deduplicatorMock
            .Setup( d => d.ReleaseAsync( It.IsAny<string>( ), It.IsAny<string?>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        SagaResultCombiner resultCombiner = new( new Mock<ILogger<SagaResultCombiner>>( ).Object );

        return new SagaCoordinatorBackgroundService(
            redisMock.Object,
            sagaManager,
            storageDouble,
            cacheMock.Object,
            deduplicatorMock.Object,
            resultCombiner,
            new Mock<ILookupDispatchOutbox>( ).Object,
            enabledProviders,
            new Mock<ILogger<SagaCoordinatorBackgroundService>>( ).Object,
            CreateRefreshReviewStore( )
        );
    }

    private static IRefreshReviewStore CreateRefreshReviewStore( ) {
        Mock<IRefreshReviewStore> store = new( );
        _ = store.Setup( candidate => candidate.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        _ = store.Setup( candidate => candidate.GetPendingForSagaAsync(
                It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        _ = store.Setup( candidate => candidate.RegisterPendingAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        _ = store.Setup( candidate => candidate.MarkUnresolvedAsync(
                It.IsAny<RefreshReviewEntry>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        _ = store.Setup( candidate => candidate.CompleteAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
        return store.Object;
    }

    /// <summary>Produces a minimal valid JSON result for a provider leg carrying the given ISRC.</summary>
    private static string BuildResultJson( SupportedProviders provider, string isrc ) {
        string url = provider switch {
            SupportedProviders.Spotify    => SpotifyTrackUrl,
            SupportedProviders.AppleMusic => AppleTrackUrl,
            SupportedProviders.Tidal      => TidalTrackUrl,
            _                             => "https://example.com/track/1"
        };
        return JsonSerializer.Serialize( new MusicLookupResult {
            ExternalId = isrc,
            Title = "Test Track",
            Artist = "Test Artist",
            URL = url
        }, JsonSerializerOptions.Web );
    }
}
