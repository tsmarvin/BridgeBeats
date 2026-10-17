using System.Diagnostics.Metrics;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="QueueMetrics"/> observable gauges against a real Redis instance
/// (the shared Testcontainers Redis). Uses a <see cref="System.Diagnostics.Metrics.MeterListener"/> to
/// capture the queue-depth gauge and verifies it reports zero for empty streams, the correct depth for
/// populated streams, per-provider depths, and the full set of provider/priority combinations.
/// Requires Docker to be running on the host machine for the Redis Testcontainer.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
// [DoNotParallelize]: the registration-supersession fix's correctness depends on no other host
// registering or disposing a connection concurrently with these assertions.
[DoNotParallelize]
public class QueueMetricsIntegrationTests {

    /// <summary>The shared Redis connection used by the gauges and the test setup.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>
    /// Per-run token used to namespace all <c>queue:*</c> keys this class touches, so parallel
    /// class runs cannot delete each other's in-flight stream data.
    /// </summary>
    private static string s_runToken = string.Empty;

    /// <summary>
    /// Requires the shared Redis container, opens a connection, and generates a stable per-run key
    /// prefix token so every key created by this class is isolated from other concurrent classes.
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
    /// Clears this run's prefixed <c>queue:{runToken}:*</c> keys and the specific production-format
    /// queue keys written by the gauge-depth tests within this class before each test, so tests do
    /// not accumulate stream entries from prior runs within the session. The cleanup uses explicit
    /// known-key deletion for the production-format keys — not a bare <c>queue:*</c> wipe — so keys
    /// owned by concurrently executing test classes are never touched.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        await CleanupOwnKeysAsync( );
    }

    /// <summary>
    /// Deletes this run's own prefixed <c>queue:{runToken}:*</c> keys and the explicit set of
    /// production-format keys written by the gauge-depth tests. Uses known-key deletion only —
    /// no bare wildcard wipe — so foreign-class keys are never touched.
    /// </summary>
    private async Task CleanupOwnKeysAsync( ) {
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );

        // Clean this run's own namespaced keys
        string ownPattern = $"queue:{s_runToken}:*";
        await foreach (RedisKey key in server.KeysAsync( pattern: ownPattern )) {
            _ = await db.KeyDeleteAsync( key );
        }

        // Clean the specific production-format keys written by gauge-depth tests in this class.
        // Using explicit key names, not a wildcard wipe, so no foreign keys are touched.
        string[] productionKeys = [
            "queue:spotify:interactive",
            "queue:spotify:background",
            "queue:spotify:bulk",
            "queue:applemusic:interactive",
            "queue:applemusic:background",
            "queue:applemusic:bulk",
            "queue:tidal:interactive",
            "queue:tidal:background",
            "queue:tidal:bulk",
        ];
        foreach (string key in productionKeys) {
            _ = await db.KeyDeleteAsync( key );
        }
    }

    /// <summary>Returns a stream key namespaced under this class's per-run token.</summary>
    private static string QueueKey( string provider, string priority ) =>
        $"queue:{s_runToken}:{provider}:{priority}";

    #region Observable Gauge Tests

    /// <summary>
    /// Verifies registering the queue-depth gauges with a null Redis connection throws
    /// <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void RegisterQueueDepthGauges_ThrowsOnNullRedis( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            QueueMetrics.RegisterQueueDepthGauges( null! )
        );
    }

    /// <summary>
    /// Verifies registering the queue-depth gauges with a valid Redis connection succeeds.
    /// </summary>
    [TestMethod]
    public void RegisterQueueDepthGauges_DoesNotThrow( ) {
        // Act - Should not throw
        QueueMetrics.RegisterQueueDepthGauges( s_redis! );

        // Assert - No exception means success
        Assert.IsNotNull( s_redis );
    }

    /// <summary>Consumer-group pending, oldest-age, and lag gauges are all published.</summary>
    [TestMethod]
    public void RegisterQueueDepthGauges_PublishesConsumerHealthGauges( ) {
        QueueMetrics.RegisterQueueDepthGauges( s_redis! );
        HashSet<string> published = [];
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName) {
                _ = published.Add( instrument.Name );
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.Start( );
        listener.RecordObservableInstruments( );

        Assert.Contains( "bridgebeats.queue.pending", published );
        Assert.Contains( "bridgebeats.queue.pending.oldest_age", published );
        Assert.Contains( "bridgebeats.queue.consumer.lag", published );
    }

    /// <summary>
    /// Verifies observable gauge collection remains non-fatal when the current Redis multiplexer
    /// cannot provide a database, as can happen while a host-owned connection is being torn down.
    /// </summary>
    [TestMethod]
    public void QueueDepthGauges_WhenDatabaseAcquisitionFails_DoNotThrow( ) {
        // Arrange: make every gauge callback encounter a connection-level failure before it can
        // read an individual stream. MeterListener aggregates exceptions escaping observable
        // callbacks, so RecordObservableInstruments below is the regression assertion.
        Mock<IConnectionMultiplexer> unavailableRedis = new( MockBehavior.Strict );
        _ = unavailableRedis
            .Setup( redis => redis.GetDatabase( It.IsAny<int>( ), It.IsAny<object?>( ) ) )
            .Throws( new ObjectDisposedException( nameof( IConnectionMultiplexer ) ) );

        QueueMetrics.RegisterQueueDepthGauges( unavailableRedis.Object );

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName
                && instrument.Name is "bridgebeats.queue.depth"
                    or "bridgebeats.queue.spotify.bulk.track.depth"
                    or "bridgebeats.queue.spotify.bulk.album.depth") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };
        listener.SetMeasurementEventCallback<long>( ( _, _, _, _ ) => { } );
        listener.Start( );

        try {
            // Act & Assert: no AggregateException should escape metric collection.
            listener.RecordObservableInstruments( );
            unavailableRedis.Verify(
                redis => redis.GetDatabase( It.IsAny<int>( ), It.IsAny<object?>( ) ),
                Times.Exactly( 3 ) );
        } finally {
            // Restore the class's live connection for subsequent tests sharing the static gauges.
            QueueMetrics.RegisterQueueDepthGauges( s_redis! );
        }
    }

    /// <summary>
    /// Verifies the queue-depth gauge reports a depth of zero for every provider/priority when all
    /// streams are empty.
    /// </summary>
    [TestMethod]
    public async Task QueueDepthGauge_ReportsZeroForEmptyStreams( ) {
        // Arrange
        QueueMetrics.RegisterQueueDepthGauges( s_redis! );

        List<Measurement<long>> measurements = [];

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.depth") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            string? provider = null;
            string? priority = null;

            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    provider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    priority = tag.Value?.ToString( );
                }
            }

            measurements.Add( new Measurement<long>( measurement,
                new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider ),
                new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority )
            ) );
        } );

        listener.Start( );

        // Act - Trigger observable gauge collection
        listener.RecordObservableInstruments( );

        // Assert - All measurements should be 0 for empty/non-existent streams
        Assert.IsNotEmpty( measurements, "Should have recorded measurements" );
        foreach (Measurement<long> measurement in measurements) {
            Assert.AreEqual( 0, measurement.Value, $"Empty stream should report 0 depth" );
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Adds five messages to one stream and verifies the gauge reports a depth of five for that
    /// provider/priority.
    /// </summary>
    [TestMethod]
    public async Task QueueDepthGauge_ReportsCorrectDepthForPopulatedStream( ) {
        // Arrange
        QueueMetrics.RegisterQueueDepthGauges( s_redis! );
        IDatabase db = s_redis!.GetDatabase( );

        // Add messages to a specific stream
        string streamKey = "queue:spotify:interactive";
        for (int i = 0; i < 5; i++) {
            _ = await db.StreamAddAsync( streamKey, "data", $"message-{i}" );
        }

        long spotifyInteractiveDepth = 0;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.depth") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            string? provider = null;
            string? priority = null;

            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    provider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    priority = tag.Value?.ToString( );
                }
            }

            if (provider == "spotify" && priority == "interactive") {
                spotifyInteractiveDepth = measurement;
            }
        } );

        listener.Start( );

        // Act - Trigger observable gauge collection
        listener.RecordObservableInstruments( );

        // Assert
        Assert.AreEqual( 5, spotifyInteractiveDepth, "Stream with 5 messages should report depth of 5" );
    }

    /// <summary>
    /// Populates streams for several provider/priority combinations and verifies the gauge reports the
    /// correct depth for each.
    /// </summary>
    [TestMethod]
    public async Task QueueDepthGauge_ReportsMultipleProviderDepths( ) {
        // Arrange
        QueueMetrics.RegisterQueueDepthGauges( s_redis! );
        IDatabase db = s_redis!.GetDatabase( );

        // Add different numbers of messages to different streams
        _ = await db.StreamAddAsync( "queue:spotify:interactive", "data", "msg1" );
        _ = await db.StreamAddAsync( "queue:spotify:interactive", "data", "msg2" );
        _ = await db.StreamAddAsync( "queue:spotify:interactive", "data", "msg3" );

        _ = await db.StreamAddAsync( "queue:applemusic:background", "data", "msg1" );
        _ = await db.StreamAddAsync( "queue:applemusic:background", "data", "msg2" );

        _ = await db.StreamAddAsync( "queue:tidal:bulk", "data", "msg1" );

        Dictionary<string, long> depths = [];

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.depth") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            string? provider = null;
            string? priority = null;

            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    provider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    priority = tag.Value?.ToString( );
                }
            }

            if (provider is not null && priority is not null) {
                depths[$"{provider}:{priority}"] = measurement;
            }
        } );

        listener.Start( );

        // Act - Trigger observable gauge collection
        listener.RecordObservableInstruments( );

        // Assert
        Assert.AreEqual( 3, depths.GetValueOrDefault( "spotify:interactive" ), "Spotify interactive should have 3 messages" );
        Assert.AreEqual( 2, depths.GetValueOrDefault( "applemusic:background" ), "Apple Music background should have 2 messages" );
        Assert.AreEqual( 1, depths.GetValueOrDefault( "tidal:bulk" ), "Tidal bulk should have 1 message" );
    }

    /// <summary>
    /// Verifies the gauge emits a measurement for all nine provider/priority combinations (three
    /// providers by three priorities).
    /// </summary>
    [TestMethod]
    public void QueueDepthGauge_IncludesAllProviderPriorityCombinations( ) {
        // Arrange
        QueueMetrics.RegisterQueueDepthGauges( s_redis! );

        HashSet<string> recordedCombinations = [];

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.depth") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            string? provider = null;
            string? priority = null;

            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    provider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    priority = tag.Value?.ToString( );
                }
            }

            if (provider is not null && priority is not null) {
                _ = recordedCombinations.Add( $"{provider}:{priority}" );
            }
        } );

        listener.Start( );

        // Act - Trigger observable gauge collection
        listener.RecordObservableInstruments( );

        // Assert - Should have all provider/priority combinations (3 providers x 3 priorities = 9)
        string[] expectedProviders = ["spotify", "applemusic", "tidal"];
        string[] expectedPriorities = ["interactive", "background", "bulk"];

        foreach (string provider in expectedProviders) {
            foreach (string priority in expectedPriorities) {
                string combination = $"{provider}:{priority}";
                Assert.Contains( combination, recordedCombinations,
                    $"Expected combination '{combination}' not found in measurements" );
            }
        }

        Assert.HasCount( 9, recordedCombinations, "Should have exactly 9 provider/priority combinations" );
    }

    /// <summary>
    /// Regression guard for the registration-supersession fix in
    /// <see cref="QueueMetrics.RegisterQueueDepthGauges"/>: registers the gauges with one connection,
    /// disposes it, then registers again with a second, live connection — reproducing the shape of
    /// several independent hosts sharing this process, where an earlier host's factory-backed
    /// connection is disposed after a later host registers its own. The gauge must read from the
    /// most recently registered (live) connection, not silently keep reading a disposed one.
    /// </summary>
    /// <remarks>
    /// Pre-fix, the gauge closures captured whichever connection first won the once-only
    /// registration latch and never updated on subsequent calls: if that connection was later
    /// disposed, every following read threw inside the gauge callback, was swallowed by the
    /// callback's own <c>catch { length = 0; }</c>, and the gauge silently reported zero forever —
    /// regardless of a later caller's live connection. Mutation-verified under a single-test filter
    /// (this test as the process's first registrant): reverting <see cref="QueueMetrics"/>'s
    /// <c>s_redis</c> reassignment to its pre-fix shape (no <c>s_redis</c> field; gauge closures
    /// capture the <c>redis</c> parameter directly and the once-only early return never updates it)
    /// reproduces the pre-fix failure (actual: 0) for this test. That evidence does not hold under a
    /// whole-class run: an earlier test in this class registers first and wins the once-only latch,
    /// and because connection A and B here share the same connection string and keyspace, the
    /// pre-fix latch still reads the entries this test writes — the test passes either way in that
    /// shape. Re-verify with a single-test filter, not a class-scoped run.
    /// </remarks>
    [TestMethod]
    public async Task RegisterQueueDepthGauges_SupersedesEarlierDisposedConnection( ) {
        IConnectionMultiplexer? connectionA = null;
        IConnectionMultiplexer? connectionB = null;
        try {
            // Arrange: register with connection A, then dispose it — mirroring a factory-backed
            // host whose connection is torn down after a later host takes over the registration.
            connectionA = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
            QueueMetrics.RegisterQueueDepthGauges( connectionA );
            await connectionA.CloseAsync( );
            connectionA.Dispose( );

            connectionB = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );

            // Act: a later caller registers its own live connection, which must supersede A.
            QueueMetrics.RegisterQueueDepthGauges( connectionB );

            IDatabase db = connectionB.GetDatabase( );
            for (int i = 0; i < 5; i++) {
                _ = await db.StreamAddAsync( "queue:spotify:interactive", "data", $"message-{i}" );
            }

            long spotifyInteractiveDepth = -1;

            using MeterListener listener = new( );
            listener.InstrumentPublished = ( instrument, meterListener ) => {
                if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.depth") {
                    meterListener.EnableMeasurementEvents( instrument );
                }
            };

            listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
                string? provider = null;
                string? priority = null;

                foreach (KeyValuePair<string, object?> tag in tags) {
                    if (tag.Key == QueueMetricTags.Provider) {
                        provider = tag.Value?.ToString( );
                    } else if (tag.Key == QueueMetricTags.Priority) {
                        priority = tag.Value?.ToString( );
                    }
                }

                if (provider == "spotify" && priority == "interactive") {
                    spotifyInteractiveDepth = measurement;
                }
            } );

            listener.Start( );

            // Act - Trigger observable gauge collection
            listener.RecordObservableInstruments( );

            // Assert - the gauge must read connection B's live stream depth, not a disposed
            // connection A's swallowed-to-zero read.
            Assert.AreEqual( 5, spotifyInteractiveDepth,
                "Gauge must report the most recently registered (live) connection's stream depth, "
                + "not a stale or disposed connection's swallowed-to-zero read" );
        } finally {
            if (connectionB is not null) {
                await connectionB.CloseAsync( );
                connectionB.Dispose( );
            }

            // Restore class state: point the shared gauges back at this class's own long-lived
            // connection so later tests in this class are unaffected by this test's connections,
            // even if an earlier step above (e.g. connecting B) failed and left the gauges bound
            // to A after it was disposed.
            QueueMetrics.RegisterQueueDepthGauges( s_redis! );
            _ = await s_redis!.GetDatabase( ).KeyDeleteAsync( "queue:spotify:interactive" );
        }
    }

    #endregion

    #region Alarm Observability Tests

    /// <summary>
    /// Verifies that <see cref="QueueServiceExtensions.AddQueueInfrastructure"/> with
    /// <c>registerMetrics: true</c> (the default) registers a <see cref="QueueMetricsRegistration"/>
    /// hosted service. Starting that service invokes <see cref="QueueMetrics.RegisterQueueDepthGauges"/>
    /// and the queue-depth instrument is visible to a <see cref="MeterListener"/> without any direct
    /// call to <see cref="QueueMetrics.RegisterQueueDepthGauges"/> in this test.
    /// </summary>
    [TestMethod]
    public async Task HostBuild_WithMetricsEnabled_RegistersHostedServiceThatEmitsGauge( ) {
        // Arrange - build a minimal service collection the same way the hosts do
        ServiceCollection services = new( );
        _ = services.AddSingleton( s_redis! );
        _ = services.AddLogging( );
        _ = services.Configure<QueueSettings>( _ => { } );
        _ = services.AddQueueInfrastructure( registerMetrics: true );

        await using ServiceProvider provider = services.BuildServiceProvider( );

        // Verify a QueueMetricsRegistration hosted service was registered
        IEnumerable<IHostedService> hostedServices = provider.GetServices<IHostedService>( );
        bool hasMetricsService = hostedServices.Any( svc => svc is QueueMetricsRegistration );
        Assert.IsTrue( hasMetricsService,
            "AddQueueInfrastructure(registerMetrics: true) must register a QueueMetricsRegistration IHostedService" );

        // Start the hosted service so the gauges are registered. Instrument creation is
        // once-per-process (already done by earlier tests in this class), but StartAsync rebinds
        // the connection the gauge callbacks read from to this call's connection — last writer
        // wins, per QueueMetricsRegistration's remarks — and must not throw.
        QueueMetricsRegistration metricsService = (QueueMetricsRegistration)hostedServices.First( svc => svc is QueueMetricsRegistration );
        await metricsService.StartAsync( CancellationToken.None );

        // Verify the queue-depth gauge is present on the meter
        bool gaugeFound = false;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, _ ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.depth") {
                gaugeFound = true;
            }
        };
        listener.Start( );

        Assert.IsTrue( gaugeFound,
            "The queue-depth gauge must be visible on the BridgeBeats.Queue meter after the hosted service starts" );
    }

    /// <summary>
    /// Negative control: verifies that <see cref="QueueServiceExtensions.AddQueueInfrastructure"/>
    /// with <c>registerMetrics: false</c> does NOT register a <see cref="QueueMetricsRegistration"/>
    /// hosted service. Prevents the gauge from being registered in hosts that opt out of metrics.
    /// </summary>
    [TestMethod]
    public void HostBuild_WithMetricsDisabled_DoesNotRegisterMetricsHostedService( ) {
        // Arrange
        ServiceCollection services = new( );
        _ = services.AddSingleton( s_redis! );
        _ = services.AddLogging( );
        _ = services.Configure<QueueSettings>( _ => { } );
        _ = services.AddQueueInfrastructure( registerMetrics: false );

        using ServiceProvider provider = services.BuildServiceProvider( );

        // Assert - no QueueMetricsRegistration in the service collection
        IEnumerable<IHostedService> hostedServices = provider.GetServices<IHostedService>( );
        bool hasMetricsService = hostedServices.Any( svc => svc is QueueMetricsRegistration );
        Assert.IsFalse( hasMetricsService,
            "AddQueueInfrastructure(registerMetrics: false) must NOT register a QueueMetricsRegistration IHostedService" );
    }

    /// <summary>The dispatch relay is opt-in so non-owner hosts do not poll the shared outbox.</summary>
    [TestMethod]
    public void HostBuild_DispatchRelayRegistration_IsExplicit( ) {
        ServiceCollection withoutRelay = new( );
        _ = withoutRelay.AddSingleton( s_redis! );
        _ = withoutRelay.AddLogging( );
        _ = withoutRelay.Configure<QueueSettings>( _ => { } );
        _ = withoutRelay.AddQueueInfrastructure( registerMetrics: false );
        using ServiceProvider withoutRelayProvider = withoutRelay.BuildServiceProvider( );
        Assert.IsEmpty( withoutRelayProvider.GetServices<IHostedService>( )
            .Where( service => service is LookupDispatchOutboxBackgroundService ) );

        ServiceCollection withRelay = new( );
        _ = withRelay.AddSingleton( s_redis! );
        _ = withRelay.AddLogging( );
        _ = withRelay.Configure<QueueSettings>( _ => { } );
        _ = withRelay.AddQueueInfrastructure(
            registerMetrics: false,
            registerDispatchRelay: true );
        using ServiceProvider withRelayProvider = withRelay.BuildServiceProvider( );
        Assert.HasCount( 1, withRelayProvider.GetServices<IHostedService>( )
            .Where( service => service is LookupDispatchOutboxBackgroundService ) );
    }

    #endregion

    #region Test-Isolation Guard

    /// <summary>
    /// Regression guard: verifies that <see cref="CleanupOwnKeysAsync"/> never touches keys outside
    /// this run's own prefix. Two sentinels are seeded to cover distinct bypass vectors: a bare
    /// <c>queue:*</c> wipe catches a revert to an unnamespaced pattern, and a foreign run-token-shaped
    /// key catches a namespaced-but-foreign-class wipe. Both must survive the cleanup call.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TestIsolation_ForeignPrefixKeys_AreNeverTouched( ) {
        IDatabase db = s_redis!.GetDatabase( );

        // Sentinel 1: bare queue: prefix — catches a revert to an unnamespaced queue:* wipe
        string bareKey = "queue:__sentinel__:metrics-guard";
        _ = await db.StringSetAsync( bareKey, "exists", TimeSpan.FromMinutes( 5 ) );

        // Sentinel 2: foreign run-token-shaped key — catches a namespaced-foreign-class wipe
        string foreignRunKey = "queue:ffffffff:spotify:interactive";
        _ = await db.StringSetAsync( foreignRunKey, "exists", TimeSpan.FromMinutes( 5 ) );

        // Exercise the real cleanup method (same code path as TestInitialize)
        await CleanupOwnKeysAsync( );

        // Both sentinels must survive: the cleanup must be scoped to this run's own prefix only
        bool bareKeyStillExists = await db.KeyExistsAsync( bareKey );
        Assert.IsTrue( bareKeyStillExists,
            "CleanupOwnKeysAsync touched a key outside this class's prefix (bare queue: sentinel gone); isolation is broken." );

        bool foreignRunKeyStillExists = await db.KeyExistsAsync( foreignRunKey );
        Assert.IsTrue( foreignRunKeyStillExists,
            "CleanupOwnKeysAsync touched a foreign run-token-shaped key; isolation is broken." );

        // Cleanup: remove both sentinels
        _ = await db.KeyDeleteAsync( bareKey );
        _ = await db.KeyDeleteAsync( foreignRunKey );
    }

    #endregion
}
