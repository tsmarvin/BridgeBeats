using System.Diagnostics;
using System.Diagnostics.Metrics;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Infrastructure.Queue;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="QueueMetrics"/>, the static <c>BridgeBeats.Queue</c> meter and its
/// counters, histograms, and record helpers. Verify the meter name/version, each instrument's
/// name and unit, that instruments accept tagged measurements, and that the <c>Record*</c> helpers
/// emit the right value and lower-cased provider/priority/endpoint tags. Measurements are observed
/// with an in-process <see cref="System.Diagnostics.Metrics.MeterListener"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class QueueMetricsTests {

    /// <summary>Queue ActivitySource spans retain the required provider and priority tags.</summary>
    [TestMethod]
    public void ActivitySource_EmitsQueueSpanWithProviderAndPriorityTags( ) {
        Activity? observed = null;
        using ActivityListener listener = new( ) {
            ShouldListenTo = source => source.Name == QueueMetrics.MeterName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => observed = activity
        };
        ActivitySource.AddActivityListener( listener );
        using (Activity? activity = QueueMetrics.ActivitySource.StartActivity( "queue.message.wall" )) {
            _ = (activity?.SetTag( QueueMetricTags.Provider, "spotify" ));
            _ = (activity?.SetTag( QueueMetricTags.Priority, "bulk" ));
        }
        Assert.IsNotNull( observed );
        Assert.AreEqual( "spotify", observed!.GetTagItem( QueueMetricTags.Provider ) );
        Assert.AreEqual( "bulk", observed.GetTagItem( QueueMetricTags.Priority ) );
    }

    #region Meter Tests

    /// <summary>The meter is named <c>BridgeBeats.Queue</c>.</summary>
    [TestMethod]
    public void Meter_HasCorrectName( ) {
        // Arrange
        const string ExpectedName = "BridgeBeats.Queue";

        // Assert
        Assert.AreEqual( ExpectedName, QueueMetrics.Meter.Name );
    }

    /// <summary>The meter reports version <c>1.0.0</c>.</summary>
    [TestMethod]
    public void Meter_HasCorrectVersion( ) {
        // Assert
        Assert.AreEqual( "1.0.0", QueueMetrics.Meter.Version );
    }

    #endregion

    #region Counter Tests

    /// <summary>The enqueued-total counter is registered (non-null).</summary>
    [TestMethod]
    public void EnqueuedTotal_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.EnqueuedTotal );
    }

    /// <summary>The enqueued-total counter is named <c>bridgebeats.queue.enqueued.total</c>.</summary>
    [TestMethod]
    public void EnqueuedTotal_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.queue.enqueued.total", QueueMetrics.EnqueuedTotal.Name );
    }

    /// <summary>The dequeued-total counter is registered (non-null).</summary>
    [TestMethod]
    public void DequeuedTotal_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.DequeuedTotal );
    }

    /// <summary>The dequeued-total counter is named <c>bridgebeats.queue.dequeued.total</c>.</summary>
    [TestMethod]
    public void DequeuedTotal_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.queue.dequeued.total", QueueMetrics.DequeuedTotal.Name );
    }

    /// <summary>The delivery-removed counter is registered (non-null).</summary>
    [TestMethod]
    public void DeliveryRemovedTotal_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.DeliveryRemovedTotal );
    }

    /// <summary>The delivery-removed counter has its semantic name.</summary>
    [TestMethod]
    public void DeliveryRemovedTotal_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.queue.delivery.removed.total", QueueMetrics.DeliveryRemovedTotal.Name );
        Assert.AreEqual( "bridgebeats.queue.dlq.total", QueueMetrics.DlqTotal.Name );
    }

    /// <summary>The rollout counters and timing instruments expose stable semantic names.</summary>
    [TestMethod]
    public void OperationalHealthInstruments_HaveExpectedNames( ) {
        Assert.AreEqual( "bridgebeats.queue.terminal.total", QueueMetrics.TerminalOutcomeTotal.Name );
        Assert.AreEqual( "bridgebeats.queue.saga.lifecycle.total", QueueMetrics.SagaLifecycleTotal.Name );
        Assert.AreEqual( "bridgebeats.queue.saga.result_not_persisted.total", QueueMetrics.ResultNotPersistedTotal.Name );
        Assert.AreEqual( "bridgebeats.queue.refresh.outcome.total", QueueMetrics.MaintenanceOutcomeTotal.Name );
        Assert.AreEqual( "bridgebeats.queue.refresh.leg.enqueued.total", QueueMetrics.RefreshLegEnqueuedTotal.Name );
        Assert.AreEqual( "bridgebeats.queue.sojourn.duration", QueueMetrics.QueueSojournDuration.Name );
        Assert.AreEqual( "bridgebeats.queue.ratelimit.discovery.duration", QueueMetrics.RateLimitDiscoveryDuration.Name );
    }

    /// <summary>The requeued-total counter is registered (non-null).</summary>
    [TestMethod]
    public void RequeuedTotal_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.RequeuedTotal );
    }

    /// <summary>The requeued-total counter is named <c>bridgebeats.queue.requeued.total</c>.</summary>
    [TestMethod]
    public void RequeuedTotal_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.queue.requeued.total", QueueMetrics.RequeuedTotal.Name );
    }

    /// <summary>The rate-limit-events counter is registered (non-null).</summary>
    [TestMethod]
    public void RateLimitEventsTotal_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.RateLimitEventsTotal );
    }

    /// <summary>The rate-limit-events counter is named <c>bridgebeats.ratelimit.events.total</c>.</summary>
    [TestMethod]
    public void RateLimitEventsTotal_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.ratelimit.events.total", QueueMetrics.RateLimitEventsTotal.Name );
    }

    /// <summary>The deduplicated-total counter is registered (non-null).</summary>
    [TestMethod]
    public void DeduplicatedTotal_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.DeduplicatedTotal );
    }

    /// <summary>The deduplicated-total counter is named <c>bridgebeats.dedup.prevented.total</c>.</summary>
    [TestMethod]
    public void DeduplicatedTotal_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.dedup.prevented.total", QueueMetrics.DeduplicatedTotal.Name );
    }

    #endregion

    #region Histogram Tests

    /// <summary>The processing-duration histogram is registered (non-null).</summary>
    [TestMethod]
    public void ProcessingDuration_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.ProcessingDuration );
    }

    /// <summary>The processing-duration histogram is named <c>bridgebeats.queue.processing.duration</c>.</summary>
    [TestMethod]
    public void ProcessingDuration_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.queue.processing.duration", QueueMetrics.ProcessingDuration.Name );
    }

    /// <summary>The processing-duration histogram is measured in seconds (<c>s</c>).</summary>
    [TestMethod]
    public void ProcessingDuration_HasCorrectUnit( ) {
        // Assert
        Assert.AreEqual( "s", QueueMetrics.ProcessingDuration.Unit );
    }

    /// <summary>The rate-limit-duration histogram is registered (non-null).</summary>
    [TestMethod]
    public void RateLimitDuration_IsNotNull( ) {
        // Assert
        Assert.IsNotNull( QueueMetrics.RateLimitDuration );
    }

    /// <summary>The rate-limit-duration histogram is named <c>bridgebeats.ratelimit.duration</c>.</summary>
    [TestMethod]
    public void RateLimitDuration_HasCorrectName( ) {
        // Assert
        Assert.AreEqual( "bridgebeats.ratelimit.duration", QueueMetrics.RateLimitDuration.Name );
    }

    /// <summary>The rate-limit-duration histogram is measured in seconds (<c>s</c>).</summary>
    [TestMethod]
    public void RateLimitDuration_HasCorrectUnit( ) {
        // Assert
        Assert.AreEqual( "s", QueueMetrics.RateLimitDuration.Unit );
    }

    #endregion

    #region Counter Recording Tests

    /// <summary>The enqueued-total counter accepts an <c>Add</c> with provider and priority tags.</summary>
    [TestMethod]
    public void EnqueuedTotal_CanRecordWithTags( ) {
        // Arrange & Act - Should not throw
        QueueMetrics.EnqueuedTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, "interactive" )
        );

        // Assert - No exception thrown means success
        Assert.IsNotNull( QueueMetrics.EnqueuedTotal );
    }

    /// <summary>Enqueue origins are exposed as a bounded telemetry dimension.</summary>
    [TestMethod]
    public void EnqueueOrigin_UsesTypedContract( ) {
        QueueMetrics.RecordEnqueue( SupportedProviders.Spotify, QueuePriority.Bulk, QueueEnqueueOrigin.RefreshSweep );
        Assert.AreEqual( "bridgebeats.queue.enqueued.total", QueueMetrics.EnqueuedTotal.Name );
    }

    /// <summary>The dequeued-total counter accepts an <c>Add</c> with provider and priority tags.</summary>
    [TestMethod]
    public void DequeuedTotal_CanRecordWithTags( ) {
        // Arrange & Act - Should not throw
        QueueMetrics.DequeuedTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "applemusic" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, "background" )
        );

        // Assert - No exception thrown means success
        Assert.IsNotNull( QueueMetrics.DequeuedTotal );
    }

    /// <summary>The rate-limit-events counter accepts an <c>Add</c> with provider and endpoint tags.</summary>
    [TestMethod]
    public void RateLimitEventsTotal_CanRecordWithTags( ) {
        // Arrange & Act - Should not throw
        QueueMetrics.RateLimitEventsTotal.Add( 1,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "tidal" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Endpoint, "/v1/tracks" )
        );

        // Assert - No exception thrown means success
        Assert.IsNotNull( QueueMetrics.RateLimitEventsTotal );
    }

    /// <summary>The deduplicated-total counter accepts an untagged <c>Add</c>.</summary>
    [TestMethod]
    public void DeduplicatedTotal_CanRecordWithoutTags( ) {
        // Arrange & Act - Should not throw
        QueueMetrics.DeduplicatedTotal.Add( 1 );

        // Assert - No exception thrown means success
        Assert.IsNotNull( QueueMetrics.DeduplicatedTotal );
    }

    #endregion

    #region Histogram Recording Tests

    /// <summary>
    /// The processing-duration histogram accepts a <c>Record</c> with provider, lookup-type, and
    /// status tags.
    /// </summary>
    [TestMethod]
    public void ProcessingDuration_CanRecordWithTags( ) {
        // Arrange & Act - Should not throw
        QueueMetrics.ProcessingDuration.Record( 1.5,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
            new KeyValuePair<string, object?>( QueueMetricTags.LookupType, "IsrcLookup" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Status, "success" )
        );

        // Assert - No exception thrown means success
        Assert.IsNotNull( QueueMetrics.ProcessingDuration );
    }

    /// <summary>
    /// The rate-limit-duration histogram accepts a <c>Record</c> with provider and endpoint tags.
    /// </summary>
    [TestMethod]
    public void RateLimitDuration_CanRecordWithTags( ) {
        // Arrange & Act - Should not throw
        QueueMetrics.RateLimitDuration.Record( 60.0,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Endpoint, "/v1/albums" )
        );

        // Assert - No exception thrown means success
        Assert.IsNotNull( QueueMetrics.RateLimitDuration );
    }

    #endregion

    #region MeterListener Verification Tests

    /// <summary>
    /// A <see cref="System.Diagnostics.Metrics.MeterListener"/> observes the enqueued-total counter
    /// receiving the recorded value (5) along with its <c>spotify</c>/<c>interactive</c> tags.
    /// </summary>
    [TestMethod]
    public void EnqueuedTotal_RecordsCorrectValueWithListener( ) {
        // Arrange
        long recordedValue = 0;
        string? recordedProvider = null;
        string? recordedPriority = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.enqueued.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    recordedPriority = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.EnqueuedTotal.Add( 5,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "spotify" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Priority, "interactive" )
        );

        // Assert
        Assert.AreEqual( 5, recordedValue );
        Assert.AreEqual( "spotify", recordedProvider );
        Assert.AreEqual( "interactive", recordedPriority );
    }

    /// <summary>
    /// A <see cref="System.Diagnostics.Metrics.MeterListener"/> observes the processing-duration
    /// histogram receiving the recorded value (2.5) along with its <c>status</c> tag.
    /// </summary>
    [TestMethod]
    public void ProcessingDuration_RecordsCorrectValueWithListener( ) {
        // Arrange
        double recordedValue = 0;
        string? recordedStatus = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.processing.duration") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<double>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Status) {
                    recordedStatus = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.ProcessingDuration.Record( 2.5,
            new KeyValuePair<string, object?>( QueueMetricTags.Provider, "applemusic" ),
            new KeyValuePair<string, object?>( QueueMetricTags.LookupType, "UpcLookup" ),
            new KeyValuePair<string, object?>( QueueMetricTags.Status, "success" )
        );

        // Assert
        Assert.AreEqual( 2.5, recordedValue, 0.001 );
        Assert.AreEqual( "success", recordedStatus );
    }

    #endregion

    #region Counter Unit Tests

    /// <summary>The enqueued-total counter uses the <c>{requests}</c> unit.</summary>
    [TestMethod]
    public void EnqueuedTotal_HasCorrectUnit( ) {
        Assert.AreEqual( "{requests}", QueueMetrics.EnqueuedTotal.Unit );
    }

    /// <summary>The dequeued-total counter uses the <c>{requests}</c> unit.</summary>
    [TestMethod]
    public void DequeuedTotal_HasCorrectUnit( ) {
        Assert.AreEqual( "{requests}", QueueMetrics.DequeuedTotal.Unit );
    }

    /// <summary>The delivery-removed counter uses the <c>{requests}</c> unit.</summary>
    [TestMethod]
    public void DeliveryRemovedTotal_HasCorrectUnit( ) {
        Assert.AreEqual( "{requests}", QueueMetrics.DeliveryRemovedTotal.Unit );
    }

    /// <summary>The requeued-total counter uses the <c>{requests}</c> unit.</summary>
    [TestMethod]
    public void RequeuedTotal_HasCorrectUnit( ) {
        Assert.AreEqual( "{requests}", QueueMetrics.RequeuedTotal.Unit );
    }

    /// <summary>The rate-limit-events counter uses the <c>{events}</c> unit.</summary>
    [TestMethod]
    public void RateLimitEventsTotal_HasCorrectUnit( ) {
        Assert.AreEqual( "{events}", QueueMetrics.RateLimitEventsTotal.Unit );
    }

    /// <summary>The deduplicated-total counter uses the <c>{requests}</c> unit.</summary>
    [TestMethod]
    public void DeduplicatedTotal_HasCorrectUnit( ) {
        Assert.AreEqual( "{requests}", QueueMetrics.DeduplicatedTotal.Unit );
    }

    #endregion

    #region Helper Method Tests

    /// <summary>
    /// <c>RecordEnqueue</c> adds 1 to the enqueued-total counter, tagging with the lower-cased
    /// provider (<c>spotify</c>) and priority (<c>interactive</c>).
    /// </summary>
    [TestMethod]
    public void RecordEnqueue_RecordsWithLowercaseProviderAndPriority( ) {
        // Arrange
        long recordedValue = 0;
        string? recordedProvider = null;
        string? recordedPriority = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.enqueued.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    recordedPriority = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordEnqueue( SupportedProviders.Spotify, QueuePriority.Interactive );

        // Assert
        Assert.AreEqual( 1, recordedValue );
        Assert.AreEqual( "spotify", recordedProvider );
        Assert.AreEqual( "interactive", recordedPriority );
    }

    /// <summary>
    /// <c>RecordDequeue</c> adds 1 to the dequeued-total counter, tagging with the lower-cased
    /// provider (<c>applemusic</c>) and priority (<c>background</c>).
    /// </summary>
    [TestMethod]
    public void RecordDequeue_RecordsWithLowercaseProviderAndPriority( ) {
        // Arrange
        long recordedValue = 0;
        string? recordedProvider = null;
        string? recordedPriority = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.dequeued.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    recordedPriority = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordDequeue( SupportedProviders.AppleMusic, QueuePriority.Background );

        // Assert
        Assert.AreEqual( 1, recordedValue );
        Assert.AreEqual( "applemusic", recordedProvider );
        Assert.AreEqual( "background", recordedPriority );
    }

    /// <summary>
    /// <c>RecordDeliveryRemoved</c> adds 1 to the delivery-removed counter, tagging with the
    /// lower-cased provider and source priority.
    /// </summary>
    [TestMethod]
    public void RecordDeliveryRemoved_RecordsProviderAndPriority( ) {
        // Arrange
        long recordedValue = 0;
        string? recordedProvider = null;
        string? recordedPriority = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.delivery.removed.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Priority) {
                    recordedPriority = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordDeliveryRemoved( SupportedProviders.Tidal, QueuePriority.Background );

        // Assert
        Assert.AreEqual( 1, recordedValue );
        Assert.AreEqual( "tidal", recordedProvider );
        Assert.AreEqual( "background", recordedPriority );
    }

    /// <summary>
    /// <c>RecordRequeue</c> adds 1 to the requeued-total counter, tagging with the lower-cased
    /// provider (<c>spotify</c>).
    /// </summary>
    [TestMethod]
    public void RecordRequeue_RecordsWithLowercaseProvider( ) {
        // Arrange
        long recordedValue = 0;
        string? recordedProvider = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.requeued.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordRequeue( SupportedProviders.Spotify );

        // Assert
        Assert.AreEqual( 1, recordedValue );
        Assert.AreEqual( "spotify", recordedProvider );
    }

    /// <summary>
    /// <c>RecordRateLimitEvent</c> increments the rate-limit-events counter (by 1) and records the
    /// rate-limit-duration histogram (120.5s), both tagged with the lower-cased provider and the
    /// endpoint.
    /// </summary>
    [TestMethod]
    public void RecordRateLimitEvent_RecordsCounterAndHistogram( ) {
        // Arrange
        long counterValue = 0;
        double histogramValue = 0;
        string? counterProvider = null;
        string? counterEndpoint = null;
        string? histogramProvider = null;
        string? histogramEndpoint = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName &&
                (instrument.Name == "bridgebeats.ratelimit.events.total" ||
                 instrument.Name == "bridgebeats.ratelimit.duration")) {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            counterValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    counterProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Endpoint) {
                    counterEndpoint = tag.Value?.ToString( );
                }
            }
        } );

        listener.SetMeasurementEventCallback<double>( ( instrument, measurement, tags, state ) => {
            histogramValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    histogramProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Endpoint) {
                    histogramEndpoint = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordRateLimitEvent( SupportedProviders.AppleMusic, "/v1/catalog/us/songs", 120.5 );

        // Assert - Counter
        Assert.AreEqual( 1, counterValue );
        Assert.AreEqual( "applemusic", counterProvider );
        Assert.AreEqual( "/v1/catalog/us/songs", counterEndpoint );

        // Assert - Histogram
        Assert.AreEqual( 120.5, histogramValue, 0.001 );
        Assert.AreEqual( "applemusic", histogramProvider );
        Assert.AreEqual( "/v1/catalog/us/songs", histogramEndpoint );
    }

    /// <summary><c>RecordDeduplicated</c> adds 1 to the deduplicated-total counter.</summary>
    [TestMethod]
    public void RecordDeduplicated_RecordsCounter( ) {
        // Arrange
        long recordedValue = 0;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.dedup.prevented.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordDeduplicated( );

        // Assert
        Assert.AreEqual( 1, recordedValue );
    }

    /// <summary>
    /// <c>RecordProcessingDuration</c> records the histogram value (3.75s) tagged with the
    /// lower-cased provider, the lookup type, and the status.
    /// </summary>
    [TestMethod]
    public void RecordProcessingDuration_RecordsWithCorrectTags( ) {
        // Arrange
        double recordedValue = 0;
        string? recordedProvider = null;
        string? recordedLookupType = null;
        string? recordedStatus = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.queue.processing.duration") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<double>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.LookupType) {
                    recordedLookupType = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Status) {
                    recordedStatus = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordProcessingDuration(
            SupportedProviders.Tidal,
            LookupRequestType.IsrcLookup,
            "success",
            3.75,
            QueuePriority.Background
        );

        // Assert
        Assert.AreEqual( 3.75, recordedValue, 0.001 );
        Assert.AreEqual( "tidal", recordedProvider );
        Assert.AreEqual( "IsrcLookup", recordedLookupType );
        Assert.AreEqual( "success", recordedStatus );
    }

    /// <summary>
    /// <c>RecordInteractiveRetry</c> adds 1 to the interactive retry counter, tagging with the
    /// lower-cased provider and endpoint.
    /// </summary>
    [TestMethod]
    public void RecordInteractiveRetry_RecordsWithLowercaseProviderAndEndpoint( ) {
        // Arrange
        long recordedValue = 0;
        string? recordedProvider = null;
        string? recordedEndpoint = null;

        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, meterListener ) => {
            if (instrument.Meter.Name == QueueMetrics.MeterName && instrument.Name == "bridgebeats.ratelimit.interactive_retry.total") {
                meterListener.EnableMeasurementEvents( instrument );
            }
        };

        listener.SetMeasurementEventCallback<long>( ( instrument, measurement, tags, state ) => {
            recordedValue = measurement;
            foreach (KeyValuePair<string, object?> tag in tags) {
                if (tag.Key == QueueMetricTags.Provider) {
                    recordedProvider = tag.Value?.ToString( );
                } else if (tag.Key == QueueMetricTags.Endpoint) {
                    recordedEndpoint = tag.Value?.ToString( );
                }
            }
        } );

        listener.Start( );

        // Act
        QueueMetrics.RecordInteractiveRetry( SupportedProviders.Spotify, "tracks" );

        // Assert
        Assert.AreEqual( 1, recordedValue );
        Assert.AreEqual( "spotify", recordedProvider );
        Assert.AreEqual( "tracks", recordedEndpoint );
    }

    /// <summary>Verifies enqueue emits exactly one measurement with all bounded tags.</summary>
    [TestMethod]
    public void RecordEnqueue_EmitsOriginProviderPriorityExactlyOnce( ) {
        long count = 0;
        Dictionary<string, string?> tagsSeen = [];
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, ml ) => {
            if (instrument.Name == "bridgebeats.queue.enqueued.total") ml.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<long>( ( _, value, tags, _ ) => {
            count += value;
            foreach (KeyValuePair<string, object?> tag in tags) tagsSeen[tag.Key] = tag.Value?.ToString( );
        } );
        listener.Start( );
        QueueMetrics.RecordEnqueue( SupportedProviders.AppleMusic, QueuePriority.Bulk, QueueEnqueueOrigin.RefreshSweep );
        Assert.AreEqual( 1, count );
        Assert.AreEqual( "applemusic", tagsSeen[QueueMetricTags.Provider] );
        Assert.AreEqual( "bulk", tagsSeen[QueueMetricTags.Priority] );
        Assert.AreEqual( "refresh_sweep", tagsSeen[QueueMetricTags.Origin] );
    }

    #endregion
}
