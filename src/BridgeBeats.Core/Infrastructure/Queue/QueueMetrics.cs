using System.Diagnostics;
using System.Diagnostics.Metrics;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// OpenTelemetry-compatible metric instruments for the BridgeBeats queue system and rate limiter.
/// </summary>
/// <remarks>
/// All instruments are created on a single <see cref="System.Diagnostics.Metrics.Meter"/>
/// named <see cref="MeterName"/> (instruments are prefixed with <c>bridgebeats.queue.</c> and
/// <c>bridgebeats.ratelimit.</c>); register with <c>.AddMeter("BridgeBeats.Queue")</c>. Counters
/// and histograms are recorded through the helper methods in this class; the observable gauges poll
/// live Redis stream lengths each time the metrics system collects, and are registered once via
/// <see cref="RegisterQueueDepthGauges(StackExchange.Redis.IConnectionMultiplexer)"/>.
/// </remarks>
public static class QueueMetrics {

    /// <summary>
    /// Name of the meter that owns every instrument in this class.
    /// Literal value: <c>"BridgeBeats.Queue"</c>; subscribe to this name to collect the metrics.
    /// </summary>
    public const string MeterName = "BridgeBeats.Queue";

    /// <summary>Name of the activity source that owns queue operational spans.</summary>
    public const string ActivitySourceName = MeterName;

    /// <summary>
    /// The shared meter (version 1.0.0) on which all queue instruments are created.
    /// </summary>
    public static readonly Meter Meter = new( MeterName, "1.0.0" );
    /// <summary>Activity source for queue operational spans.</summary>
    public static readonly ActivitySource ActivitySource = new( ActivitySourceName );

    #region Counters

    /// <summary>
    /// Counts requests enqueued onto any priority lane, tagged by provider and priority.
    /// </summary>
    public static readonly Counter<long> EnqueuedTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.enqueued.total",
        unit: "{requests}",
        description: "Total number of requests enqueued"
    );

    /// <summary>
    /// Counts requests dequeued for processing, tagged by provider and the priority lane
    /// the message was pulled from.
    /// </summary>
    public static readonly Counter<long> DequeuedTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.dequeued.total",
        unit: "{requests}",
        description: "Total number of requests dequeued for processing"
    );

    /// <summary>
    /// Counts live-stream source deliveries removed with XACK + XDEL. This includes successful,
    /// stale, and retry-replaced deliveries; terminal processing outcomes are recorded separately.
    /// </summary>
    public static readonly Counter<long> DeliveryRemovedTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.delivery.removed.total",
        unit: "{requests}",
        description: "Total source deliveries acknowledged and removed from live streams"
    );

    /// <summary>Counts source deliveries moved into a dead-letter stream.</summary>
    public static readonly Counter<long> DlqTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.dlq.total",
        unit: "{requests}",
        description: "Total source deliveries moved to a dead-letter stream" );

    /// <summary>
    /// Counts requests returned to the queue for retry, tagged by provider.
    /// </summary>
    public static readonly Counter<long> RequeuedTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.requeued.total",
        unit: "{requests}",
        description: "Total number of requests returned to queue for retry"
    );

    /// <summary>
    /// Counts rate-limit events encountered, tagged by provider and endpoint.
    /// </summary>
    public static readonly Counter<long> RateLimitEventsTotal = Meter.CreateCounter<long>(
        "bridgebeats.ratelimit.events.total",
        unit: "{events}",
        description: "Total number of rate limit events encountered"
    );

    /// <summary>
    /// Counts duplicate requests prevented by the single-flight deduplicator (untagged).
    /// </summary>
    public static readonly Counter<long> DeduplicatedTotal = Meter.CreateCounter<long>(
        "bridgebeats.dedup.prevented.total",
        unit: "{requests}",
        description: "Total number of duplicate requests prevented"
    );

    /// <summary>
    /// Counts interactive-origin lookups that remain on the interactive single-item lane after a
    /// provider rate limit, tagged by provider and endpoint.
    /// </summary>
    public static readonly Counter<long> InteractiveRetryTotal = Meter.CreateCounter<long>(
        "bridgebeats.ratelimit.interactive_retry.total",
        unit: "{requests}",
        description: "Total interactive-origin lookups preserved on the interactive lane after rate limiting"
    );

    /// <summary>Counts refresh provider legs accepted for enqueue.</summary>
    public static readonly Counter<long> RefreshLegEnqueuedTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.refresh.leg.enqueued.total",
        unit: "{legs}",
        description: "Total number of refresh provider legs accepted for enqueue" );
    /// <summary>Counts completed saga legs.</summary>
    public static readonly Counter<long> SagaLegCompletedTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.saga.leg.completed.total",
        unit: "{legs}",
        description: "Total number of completed saga provider legs" );
    /// <summary>Counts delivery terminal outcomes.</summary>
    public static readonly Counter<long> TerminalOutcomeTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.terminal.total", unit: "{deliveries}",
        description: "Total terminal delivery outcomes" );
    /// <summary>Counts saga lifecycle outcomes relevant to finalization and fencing.</summary>
    public static readonly Counter<long> SagaLifecycleTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.saga.lifecycle.total", unit: "{events}",
        description: "Total saga lifecycle and fencing outcomes" );
    /// <summary>Counts maintenance refresh selection and disposition outcomes.</summary>
    public static readonly Counter<long> MaintenanceOutcomeTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.refresh.outcome.total", unit: "{records}",
        description: "Total maintenance refresh record outcomes" );
    /// <summary>Counts failures while collecting live Redis queue gauges.</summary>
    public static readonly Counter<long> GaugeCollectionFailuresTotal = Meter.CreateCounter<long>(
        "bridgebeats.queue.gauge.collection.failure.total", unit: "{failures}",
        description: "Failures while collecting Redis-backed queue gauges" );

    #endregion

    #region Histograms

    /// <summary>
    /// Records, in seconds, the post-dequeue processing time for a queued request, tagged by
    /// provider, lookup type, and status. This intentionally starts after <c>DequeueAsync</c> and
    /// excludes dequeue and PEL-scan latency; use <c>queue.message.wall.duration</c> for the
    /// end-to-end dequeue-to-completion measurement.
    /// </summary>
    public static readonly Histogram<double> ProcessingDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.processing.duration",
        unit: "s",
        description: "Post-dequeue processing time for a queued request (excludes DequeueAsync); use queue.message.wall.duration for end-to-end time"
    );

    /// <summary>
    /// Records, in seconds, the Retry-After window of each rate-limit event, tagged by
    /// provider and endpoint.
    /// </summary>
    public static readonly Histogram<double> RateLimitDuration = Meter.CreateHistogram<double>(
        "bridgebeats.ratelimit.duration",
        unit: "s",
        description: "Duration of rate limit Retry-After periods"
    );
    /// <summary>Whole dequeue duration.</summary>
    public static readonly Histogram<double> DequeueDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.dequeue.duration", unit: "s", description: "Duration of a complete queue dequeue attempt" );
    /// <summary>PEL scan duration.</summary>
    public static readonly Histogram<double> PelScanDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.dequeue.pel_scan.duration", unit: "s", description: "Duration of pending-entry recovery scans" );
    /// <summary>PEL entries scanned.</summary>
    public static readonly Counter<long> PelScanEntries = Meter.CreateCounter<long>(
        "bridgebeats.queue.dequeue.pel_scan.entries", unit: "{entries}", description: "Pending entries examined during dequeue recovery" );
    /// <summary>Redis group read duration.</summary>
    public static readonly Histogram<double> ReadGroupDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.dequeue.read_group.duration", unit: "s", description: "Duration of Redis consumer-group reads" );
    /// <summary>Queue depth probe duration.</summary>
    public static readonly Histogram<double> DepthProbeDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.dequeue.depth_probe.duration", unit: "s", description: "Duration of queue-depth probes used for dequeue scheduling" );
    /// <summary>Saga update duration.</summary>
    public static readonly Histogram<double> SagaUpdateDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.saga.update.duration", unit: "s", description: "Duration of saga state updates for queued requests" );
    /// <summary>Provider HTTP duration.</summary>
    public static readonly Histogram<double> ProviderHttpDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.provider_http.duration", unit: "s", description: "Duration of provider HTTP calls made for queued requests" );
    /// <summary>End-to-end message wall duration.</summary>
    public static readonly Histogram<double> MessageWallDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.message.wall.duration", unit: "s", description: "End-to-end wall duration from dequeue through terminal handling" );
    /// <summary>Time a delivery waited in a live stream before dequeue.</summary>
    public static readonly Histogram<double> QueueSojournDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.sojourn.duration", unit: "s", description: "Time from stream enqueue to dequeue" );
    /// <summary>Duration of active rate-limit discovery before dequeue.</summary>
    public static readonly Histogram<double> RateLimitDiscoveryDuration = Meter.CreateHistogram<double>(
        "bridgebeats.queue.ratelimit.discovery.duration", unit: "s",
        description: "Duration of provider active-rate-limit discovery" );

    #endregion

    #region Observable Gauges

    /// <summary>Guards against creating the observable gauge instruments more than once.</summary>
    private static bool s_gaugesRegistered;

    /// <summary>Serializes gauge registration and updates to <see cref="s_redis"/>.</summary>
    private static readonly Lock s_gaugesLock = new( );

    /// <summary>
    /// The Redis connection the gauge callbacks currently read from. Updated on every call to
    /// <see cref="RegisterQueueDepthGauges"/>, not just the first, so a later caller's live
    /// connection always supersedes an earlier caller's — see the remarks below.
    /// </summary>
    private static volatile IConnectionMultiplexer? s_redis;

    /// <summary>
    /// Registers observable gauges for queue depth metrics, which poll live Redis stream lengths on
    /// each metrics collection.
    /// </summary>
    /// <param name="redis">The Redis connection used to read stream lengths.</param>
    /// <remarks>
    /// Call this once during application startup after Redis is connected; the gauges automatically
    /// poll queue depths on each metrics collection. Thread-safe. The observable gauge instruments
    /// are created only once per process (they cannot be re-created on the shared <see cref="Meter"/>
    /// without duplicate-instrument warnings), but the Redis connection the gauge callbacks read from
    /// is updated on <em>every</em> call, including calls after the first. This matters when several
    /// independent hosts share this process (for example, multiple <c>WebApplicationFactory</c>
    /// instances in a test run) and register with their own connection: without updating the
    /// connection on each call, the gauges would stay bound to whichever caller registered first, and
    /// silently report zero forever once that caller's connection is disposed. Depth gauges cover
    /// every generic provider/priority stream and both dedicated Spotify bulk streams. Consumer-group
    /// gauges report live pending count, oldest-pending age, and lag using the same stream tags.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="redis"/> is null.</exception>
    public static void RegisterQueueDepthGauges( IConnectionMultiplexer redis ) {
        ArgumentNullException.ThrowIfNull( redis );

        lock (s_gaugesLock) {
            bool connectionChanged = !ReferenceEquals( s_redis, redis );
            s_redis = redis;
            if (connectionChanged) {
                lock (s_consumerGroupSnapshotLock) {
                    s_consumerGroupSnapshotValidUntil = DateTimeOffset.MinValue;
                }
            }

            if (s_gaugesRegistered) {
                return;
            }

            // Create a single observable gauge that returns measurements for all provider/priority combinations
            _ = Meter.CreateObservableGauge(
                "bridgebeats.queue.depth",
                ( ) => GetQueueDepthMeasurements( s_redis ),
                unit: "{requests}",
                description: "Current number of requests in queue"
            );

            // Spotify type-specific bulk streams sit outside the generic priority layout and need separate gauges.
            _ = Meter.CreateObservableGauge(
                "bridgebeats.queue.spotify.bulk.track.depth",
                ( ) => GetSpotifyBulkStreamDepth( s_redis, SpotifyConstants.BulkTrackIdStream ),
                unit: "{requests}",
                description: "Current depth of the Spotify bulk track-id stream (queue:spotify:bulk:track-id)"
            );

            _ = Meter.CreateObservableGauge(
                "bridgebeats.queue.spotify.bulk.album.depth",
                ( ) => GetSpotifyBulkStreamDepth( s_redis, SpotifyConstants.BulkAlbumIdStream ),
                unit: "{requests}",
                description: "Current depth of the Spotify bulk album-id stream (queue:spotify:bulk:album-id)"
            );

            _ = Meter.CreateObservableGauge(
                "bridgebeats.queue.pending",
                ( ) => GetConsumerGroupMeasurements( s_redis, ConsumerGroupMeasurement.Pending ),
                unit: "{requests}",
                description: "Current consumer-group pending-entry count" );

            _ = Meter.CreateObservableGauge(
                "bridgebeats.queue.pending.oldest_age",
                ( ) => GetConsumerGroupMeasurements( s_redis, ConsumerGroupMeasurement.OldestPendingAge ),
                unit: "s",
                description: "Idle age of the oldest pending consumer-group delivery" );

            _ = Meter.CreateObservableGauge(
                "bridgebeats.queue.consumer.lag",
                ( ) => GetConsumerGroupMeasurements( s_redis, ConsumerGroupMeasurement.Lag ),
                unit: "{requests}",
                description: "Consumer-group lag reported by Redis" );

            s_gaugesRegistered = true;
        }
    }

    /// <summary>
    /// Yields one queue-depth measurement per provider and priority lane (interactive,
    /// background, bulk) by reading the length of each <c>queue:{provider}:{priority}</c> stream.
    /// A read failure for any single stream yields a depth of zero rather than throwing. If a
    /// database cannot be acquired from the current connection, no measurements are yielded.
    /// </summary>
    /// <param name="redis">The Redis connection used to read stream lengths, if one is available.</param>
    /// <returns>One measurement per provider/priority combination, tagged with provider and priority.</returns>
    private static IEnumerable<Measurement<long>> GetQueueDepthMeasurements( IConnectionMultiplexer? redis ) {
        if (redis is null) {
            yield break;
        }

        IDatabase db;
        try {
            db = redis.GetDatabase( );
        } catch {
            // Observable callbacks must remain non-fatal during connection teardown or outages.
            yield break;
        }

        string[] priorities = ["interactive", "background", "bulk"];

        foreach (SupportedProviders provider in Enum.GetValues<SupportedProviders>( )) {
            string providerName = provider.ToString( ).ToLowerInvariant( );

            foreach (string priority in priorities) {
                string streamKey = $"queue:{providerName}:{priority}";

                long length;
                try {
                    length = db.StreamLength( streamKey );
                } catch {
                    // Stream may not exist yet
                    length = 0;
                }

                yield return new Measurement<long>(
                    length,
                    new KeyValuePair<string, object?>( QueueMetricTags.Provider, providerName ),
                    new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority )
                );
            }
        }
    }

    /// <summary>
    /// Yields a single depth measurement for one Spotify type-specific bulk stream. A read failure
    /// yields a depth of zero rather than throwing. If a database cannot be acquired from the
    /// current connection, no measurement is yielded.
    /// </summary>
    /// <param name="redis">The Redis connection used to read the stream length, if one is available.</param>
    /// <param name="stream">The bulk stream key to measure (track-id or album-id stream).</param>
    /// <returns>One measurement tagged with provider "spotify" and the stream key.</returns>
    private static IEnumerable<Measurement<long>> GetSpotifyBulkStreamDepth( IConnectionMultiplexer? redis, string stream ) {
        if (redis is null) {
            yield break;
        }

        IDatabase db;
        try {
            db = redis.GetDatabase( );
        } catch {
            // Observable callbacks must remain non-fatal during connection teardown or outages.
            yield break;
        }

        long length;
        try {
            length = db.StreamLength( stream );
        } catch {
            length = 0;
        }

        yield return new Measurement<long>(
            length,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream )
        );
    }

    private enum ConsumerGroupMeasurement { Pending, OldestPendingAge, Lag }
    private sealed record ConsumerGroupSnapshot(
        string Provider,
        string Priority,
        string Stream,
        long Pending,
        long OldestPendingAge,
        long Lag );
    private static readonly Lock s_consumerGroupSnapshotLock = new( );
    private static DateTimeOffset s_consumerGroupSnapshotValidUntil;
    private static IReadOnlyList<ConsumerGroupSnapshot> s_consumerGroupSnapshot = [];
    private static readonly TimeSpan s_consumerGroupSnapshotDuration = TimeSpan.FromSeconds( 5 );
    private static bool s_consumerGroupRefreshInProgress;

    /// <summary>Returns live PEL and group-lag measurements for every production queue stream.</summary>
    private static IEnumerable<Measurement<long>> GetConsumerGroupMeasurements(
        IConnectionMultiplexer? redis,
        ConsumerGroupMeasurement measurement
    ) {
        if (redis is null) yield break;

        foreach (ConsumerGroupSnapshot snapshot in GetConsumerGroupSnapshot( redis )) {
            long value = measurement switch {
                ConsumerGroupMeasurement.Pending => snapshot.Pending,
                ConsumerGroupMeasurement.Lag => snapshot.Lag,
                ConsumerGroupMeasurement.OldestPendingAge => snapshot.OldestPendingAge,
                _ => 0
            };
            yield return new Measurement<long>( value,
                new KeyValuePair<string, object?>( QueueMetricTags.Provider, snapshot.Provider ),
                new KeyValuePair<string, object?>( QueueMetricTags.Priority, snapshot.Priority ),
                new KeyValuePair<string, object?>( QueueMetricTags.Stream, snapshot.Stream ) );
        }
    }

    private static IReadOnlyList<ConsumerGroupSnapshot> GetConsumerGroupSnapshot( IConnectionMultiplexer redis ) {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (s_consumerGroupSnapshotLock) {
            if (now < s_consumerGroupSnapshotValidUntil) return s_consumerGroupSnapshot;
            if (!s_consumerGroupRefreshInProgress) {
                s_consumerGroupRefreshInProgress = true;
                _ = Task.Run( ( ) => RefreshConsumerGroupSnapshotAsync( redis ) );
            }
            return s_consumerGroupSnapshot;
        }
    }

    private static async Task RefreshConsumerGroupSnapshotAsync( IConnectionMultiplexer redis ) {
        List<ConsumerGroupSnapshot> snapshots = [];
        try {
            IDatabase db = redis.GetDatabase( );
            foreach (SupportedProviders provider in Enum.GetValues<SupportedProviders>( )) {
                string providerName = provider.ToString( ).ToLowerInvariant( );
                string group = $"{providerName}-workers";
                foreach (QueuePriority priority in Enum.GetValues<QueuePriority>( )) {
                    snapshots.Add( await ReadConsumerGroupSnapshotAsync(
                        db,
                        QueueStreamKeys.For( provider, priority ),
                        group,
                        providerName,
                        priority.ToString( ).ToLowerInvariant( ) ) );
                }
            }
            snapshots.Add( await ReadConsumerGroupSnapshotAsync(
                db, QueueStreamKeys.SpotifyBulkFor( isTrack: true ),
                SpotifyConstants.ConsumerGroup, "spotify", "bulk" ) );
            snapshots.Add( await ReadConsumerGroupSnapshotAsync(
                db, QueueStreamKeys.SpotifyBulkFor( isTrack: false ),
                SpotifyConstants.ConsumerGroup, "spotify", "bulk" ) );
        } catch {
            // Connection acquisition and teardown failures preserve the last good snapshot.
        } finally {
            lock (s_consumerGroupSnapshotLock) {
                if (snapshots.Count > 0) s_consumerGroupSnapshot = snapshots;
                s_consumerGroupSnapshotValidUntil = DateTimeOffset.UtcNow + s_consumerGroupSnapshotDuration;
                s_consumerGroupRefreshInProgress = false;
            }
        }
    }

    private static async Task<ConsumerGroupSnapshot> ReadConsumerGroupSnapshotAsync(
        IDatabase db, string stream, string group, string provider, string priority ) {
        try {
            StreamGroupInfo? groupInfo = (await db.StreamGroupInfoAsync( stream ))
                .FirstOrDefault( candidate => candidate.Name == group );
            if (groupInfo is null) return new( provider, priority, stream, 0, 0, 0 );
            long pending = groupInfo.Value.PendingMessageCount;
            return new( provider, priority, stream, pending,
                pending == 0 ? 0 : await ReadOldestPendingAgeSecondsAsync( db, stream, group ),
                Math.Max( 0, groupInfo.Value.Lag.GetValueOrDefault( ) ) );
        } catch {
            GaugeCollectionFailuresTotal.Add( 1,
                new KeyValuePair<string, object?>( QueueMetricTags.Stream, stream ) );
            return new( provider, priority, stream, 0, 0, 0 );
        }
    }

    private static async Task<long> ReadOldestPendingAgeSecondsAsync( IDatabase db, string stream, string group ) {
        StreamPendingMessageInfo? oldest = (await db.StreamPendingMessagesAsync(
            stream, group, 1, RedisValue.Null )).FirstOrDefault( );
        return oldest is null ? 0 : Math.Max( 0, oldest.Value.IdleTimeInMilliseconds / 1000 );
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Records a queue enqueue event: increments <see cref="EnqueuedTotal"/> with standard tags.
    /// </summary>
    /// <param name="provider">The provider the request was enqueued for.</param>
    /// <param name="priority">The priority lane the request was enqueued to.</param>
    /// <param name="origin">The enqueue origin.</param>
    public static void RecordEnqueue( SupportedProviders provider, QueuePriority priority, QueueEnqueueOrigin origin = QueueEnqueueOrigin.New ) {
        string originName = origin switch { QueueEnqueueOrigin.Requeue => "requeue", QueueEnqueueOrigin.RefreshSweep => "refresh_sweep", _ => "new" };
        EnqueuedTotal.Add(
            1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Origin, originName )
        );
    }

    /// <summary>Records a refresh provider leg accepted for enqueue.</summary>
    public static void RecordRefreshLegEnqueued( SupportedProviders provider, bool reselected ) =>
        RefreshLegEnqueuedTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Reselected, reselected ) );

    /// <summary>
    /// Records a queue dequeue event: increments <see cref="DequeuedTotal"/> with standard tags.
    /// </summary>
    /// <param name="provider">The provider the request was dequeued from.</param>
    /// <param name="priority">The priority lane the request was dequeued from.</param>
    public static void RecordDequeue( SupportedProviders provider, QueuePriority priority ) {
        DequeuedTotal.Add(
            1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) )
        );
    }

    /// <summary>Records one completed saga leg with its authoritative saga state.</summary>
    public static void RecordSagaLegCompleted( SupportedProviders provider, QueuePriority priority, string sagaState ) {
        SagaLegCompletedTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.SagaState, sagaState ) );
    }

    /// <summary>
    /// Records removal of a source delivery: increments <see cref="DeliveryRemovedTotal"/> with standard tags.
    /// </summary>
    /// <param name="provider">The provider the message was acknowledged for.</param>
    /// <param name="priority">The source priority lane.</param>
    public static void RecordDeliveryRemoved( SupportedProviders provider, QueuePriority priority ) {
        DeliveryRemovedTotal.Add(
            1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) )
        );
    }

    /// <summary>Records one dead-letter move.</summary>
    public static void RecordDlq( SupportedProviders provider, QueuePriority priority ) =>
        DlqTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ) );

    /// <summary>Records one terminal delivery outcome.</summary>
    public static void RecordTerminalOutcome( SupportedProviders provider, QueuePriority priority, string outcome ) =>
        TerminalOutcomeTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Outcome, outcome ) );

    /// <summary>Records one saga lifecycle outcome.</summary>
    public static void RecordSagaLifecycleOutcome( string outcome ) =>
        SagaLifecycleTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Outcome, outcome ) );

    /// <summary>Records one maintenance record outcome.</summary>
    public static void RecordMaintenanceOutcome( string outcome, SupportedProviders? provider = null ) {
        TagList tags = new( ) { { QueueMetricTags.Outcome, outcome } };
        if (provider is { } value) {
            tags.Add( QueueMetricTags.Provider, value.ToString( ).ToLowerInvariant( ) );
        }
        MaintenanceOutcomeTotal.Add( 1, tags );
    }

    /// <summary>Records queue waiting time when a delivery is returned to a worker.</summary>
    public static void RecordQueueSojourn( SupportedProviders provider, QueuePriority priority, DateTimeOffset enqueuedAt ) {
        double seconds = Math.Max( 0, (DateTimeOffset.UtcNow - enqueuedAt).TotalSeconds );
        QueueSojournDuration.Record( seconds,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) ) );
    }

    /// <summary>Records active rate-limit discovery duration.</summary>
    public static void RecordRateLimitDiscoveryDuration( SupportedProviders provider, double seconds ) =>
        RateLimitDiscoveryDuration.Record( seconds,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ) );

    /// <summary>
    /// Records a message requeue event: increments <see cref="RequeuedTotal"/> with standard tags.
    /// </summary>
    /// <param name="provider">The provider the message was requeued for.</param>
    public static void RecordRequeue( SupportedProviders provider ) {
        RequeuedTotal.Add(
            1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) )
        );
    }

    /// <summary>
    /// Records a rate-limit event: increments <see cref="RateLimitEventsTotal"/> and records the
    /// Retry-After window on <see cref="RateLimitDuration"/>, both with standard tags.
    /// </summary>
    /// <param name="provider">The provider that encountered the rate limit.</param>
    /// <param name="endpoint">The endpoint that was rate limited.</param>
    /// <param name="retryAfterSeconds">The Retry-After duration, in seconds.</param>
    public static void RecordRateLimitEvent( SupportedProviders provider, string endpoint, double retryAfterSeconds ) {
        string providerName = provider.ToString( ).ToLowerInvariant( );

        RateLimitEventsTotal.Add(
            1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, providerName ),
            new KeyValuePair<string, object?>( QueueMetricTags.Endpoint, endpoint )
        );

        RateLimitDuration.Record(
            retryAfterSeconds,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, providerName ),
            new KeyValuePair<string, object?>( QueueMetricTags.Endpoint, endpoint )
        );
    }

    /// <summary>
    /// Records a deduplication event: increments <see cref="DeduplicatedTotal"/>.
    /// </summary>
    public static void RecordDeduplicated( ) {
        DeduplicatedTotal.Add( 1 );
    }

    /// <summary>
    /// Records the post-dequeue processing duration for a queued request on
    /// <see cref="ProcessingDuration"/>. The stopwatch begins after <c>DequeueAsync</c>; dequeue
    /// and PEL-scan time are excluded. Use <c>queue.message.wall.duration</c> for end-to-end time.
    /// </summary>
    /// <param name="provider">The provider that processed the request.</param>
    /// <param name="lookupType">The type of lookup that was performed.</param>
    /// <param name="status">The processing status (success, failure, rate_limited, stale).</param>
    /// <param name="durationSeconds">The processing duration, in seconds.</param>
    /// <param name="priority">The actual queue lane from which the message was delivered.</param>
    public static void RecordProcessingDuration(
        SupportedProviders provider,
        LookupRequestType lookupType,
        string status,
        double durationSeconds,
        QueuePriority priority
    ) {
        ProcessingDuration.Record(
            durationSeconds,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, provider.ToString( ).ToLowerInvariant( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.LookupType, lookupType.ToString( ) ),
            new KeyValuePair<string, object?>( QueueMetricTags.Status, status ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, priority.ToString( ).ToLowerInvariant( ) )
        );
    }

    /// <summary>
    /// Records an interactive-origin lookup preserved on the interactive retry lane.
    /// </summary>
    /// <param name="provider">The provider that encountered the rate limit.</param>
    /// <param name="endpoint">The endpoint that was rate limited.</param>
    public static void RecordInteractiveRetry( SupportedProviders provider, string endpoint ) {
        string providerName = provider.ToString( ).ToLowerInvariant( );

        InteractiveRetryTotal.Add(
            1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, providerName ),
            new KeyValuePair<string, object?>( QueueMetricTags.Endpoint, endpoint )
        );
    }

    #endregion
}
