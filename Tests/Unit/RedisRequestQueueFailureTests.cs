using System.Diagnostics.Metrics;
using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>Failure-boundary tests for loss-safe Redis queue replacement.</summary>
[TestClass]
[DoNotParallelize]
public sealed class RedisRequestQueueFailureTests {
    /// <summary>An atomic replacement failure leaves the source pending and records no enqueue.</summary>
    [TestMethod]
    public async Task RequeueAsync_WhenAtomicMoveFails_RecordsNoAcceptedEnqueue( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        QueuedLookupRequest request = new( ) {
            RequestId = "request",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US-FAIL",
            SagaId = "saga"
        };
        StreamEntry original = new( (RedisValue)"1-0", [
            new NameValueEntry( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request ) ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ] );
        _ = db.Setup( d => d.StreamRangeAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ), It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( [original] );
        _ = db.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "atomic move failed" ) );

        long accepted = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, consumer ) => {
            if (instrument.Name == "bridgebeats.queue.enqueued.total") {
                consumer.EnableMeasurementEvents( instrument );
            }
        };
        listener.SetMeasurementEventCallback<long>( ( _, measurement, _, _ ) => Interlocked.Add( ref accepted, measurement ) );
        listener.Start( );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );

        _ = await Assert.ThrowsExactlyAsync<RedisServerException>(
            ( ) => queue.RequeueAsync( "queue:spotify:interactive:1-0", cancellationToken: CancellationToken.None ) );
        listener.RecordObservableInstruments( );
        Assert.AreEqual( 0, accepted );
    }

    /// <summary>An XADD failure leaves the original stream delivery untouched.</summary>
    [TestMethod]
    public async Task RequeueAsync_WhenReplacementAddFails_LeavesOriginalPending( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        QueuedLookupRequest request = new( ) {
            RequestId = "request",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US-FAIL",
            SagaId = "saga"
        };
        NameValueEntry[] fields = [
            new( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request ) ),
            new( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ];
        StreamEntry original = new( (RedisValue)"1-0", fields );
        _ = db.Setup( d => d.StreamRangeAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ), It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [original] );
        _ = db.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "XADD failed" ) );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );

        long accepted = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, consumer ) => {
            if (instrument.Name == "bridgebeats.queue.enqueued.total") consumer.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<long>( ( _, measurement, _, _ ) => Interlocked.Add( ref accepted, measurement ) );
        listener.Start( );

        _ = await Assert.ThrowsExactlyAsync<RedisServerException>(
            ( ) => queue.RequeueAsync( "queue:spotify:interactive:1-0", cancellationToken: CancellationToken.None ) );

        listener.RecordObservableInstruments( );
        Assert.AreEqual( 0, accepted, "A failed XADD is not an accepted enqueue" );

        db.Verify( d => d.StreamAcknowledgeAsync(
            It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
        db.Verify( d => d.StreamDeleteAsync(
            It.IsAny<RedisKey>( ), It.IsAny<RedisValue[]>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>An accepted ordinary enqueue records exactly one enqueue metric after XADD succeeds.</summary>
    [TestMethod]
    public async Task EnqueueAsync_WhenStreamAddReturnsId_RecordsAcceptedEnqueue( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        _ = db.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)"3-0" ) );

        long accepted = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, consumer ) => {
            if (instrument.Name == "bridgebeats.queue.enqueued.total") consumer.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<long>( ( _, measurement, _, _ ) => Interlocked.Add( ref accepted, measurement ) );
        listener.Start( );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );
        await queue.EnqueueAsync( new QueuedLookupRequest {
            RequestId = "request",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US-ACCEPTED",
            SagaId = "saga"
        }, QueuePriority.Interactive, CancellationToken.None );

        listener.RecordObservableInstruments( );
        Assert.AreEqual( 1, accepted, "A non-null XADD id must count exactly one accepted enqueue" );
    }

    /// <summary>A value-less XADD result is reported as an enqueue failure.</summary>
    [TestMethod]
    public async Task EnqueueAsync_WhenStreamAddReturnsNoId_ThrowsAndRecordsNoMetric( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        _ = db.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( RedisValue.Null ) );

        long accepted = 0;
        using MeterListener listener = new( );
        listener.InstrumentPublished = ( instrument, consumer ) => {
            if (instrument.Name == "bridgebeats.queue.enqueued.total") consumer.EnableMeasurementEvents( instrument );
        };
        listener.SetMeasurementEventCallback<long>( ( _, measurement, _, _ ) => Interlocked.Add( ref accepted, measurement ) );
        listener.Start( );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>( ( ) => queue.EnqueueAsync(
            new QueuedLookupRequest {
                RequestId = "request",
                Provider = SupportedProviders.Spotify,
                LookupType = LookupRequestType.IsrcLookup,
                LookupValue = "US-NOT-ACCEPTED",
                SagaId = "saga"
            }, QueuePriority.Interactive, CancellationToken.None ) );

        listener.RecordObservableInstruments( );
        Assert.AreEqual( 0, accepted );
    }

    /// <summary>A value-less DLQ replacement leaves the only source copy intact.</summary>
    [TestMethod]
    public async Task RequeueFromDlqAsync_WhenReplacementReturnsNoId_DoesNotDeleteSource( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        QueuedLookupRequest request = new( ) {
            RequestId = "request",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US-DLQ",
            SagaId = "saga"
        };
        StreamEntry original = new( (RedisValue)"1-0", [
            new NameValueEntry( QueueStreamFieldNames.Payload, JsonSerializer.Serialize( request ) ),
            new NameValueEntry( QueueStreamFieldNames.EnqueuedAt, DateTimeOffset.UtcNow.ToString( "O" ) )
        ] );
        _ = db.Setup( d => d.StreamRangeAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
                It.IsAny<int?>( ), It.IsAny<Order>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [original] );
        _ = db.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( RedisValue.Null ) );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>( ( ) => queue.RequeueFromDlqAsync(
            "queue:spotify:dlq:1-0", QueuePriority.Background, CancellationToken.None ) );

        db.Verify( d => d.StreamDeleteAsync(
            It.IsAny<RedisKey>( ), It.IsAny<RedisValue[]>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>Unknown-command XAUTOCLAIM errors fall back to own-PEL/new scanning.</summary>
    [TestMethod]
    public async Task DequeueAsync_WhenAutoClaimIsUnsupported_FallsBackToOtherStages( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        Mock<IRateLimitTracker> tracker = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( Array.Empty<RateLimitedEndpoint>( ) );
        _ = db.Setup( d => d.StreamLengthAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( 0 );
        _ = db.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<long>( ),
                It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "ERR unknown command 'XAUTOCLAIM'" ) );
        _ = db.Setup( d => d.StreamPendingMessagesAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<int>( ), It.IsAny<RedisValue>( ),
                It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( Array.Empty<StreamPendingMessageInfo>( ) );
        _ = db.Setup( d => d.StreamReadGroupAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue?>( ),
                It.IsAny<int?>( ), It.IsAny<bool>( ), It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( Array.Empty<StreamEntry>( ) );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );

        Assert.IsNull( await queue.DequeueAsync( tracker.Object, CancellationToken.None ) );
    }

    /// <summary>Permission errors from XAUTOCLAIM propagate instead of being treated as unsupported.</summary>
    [TestMethod]
    public async Task DequeueAsync_WhenAutoClaimIsForbidden_PropagatesRedisError( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        Mock<IRateLimitTracker> tracker = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        _ = tracker.Setup( t => t.GetAllRateLimitedAsync( SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( Array.Empty<RateLimitedEndpoint>( ) );
        _ = db.Setup( d => d.StreamLengthAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( 0 );
        _ = db.Setup( d => d.StreamAutoClaimAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ), It.IsAny<long>( ),
                It.IsAny<RedisValue>( ), It.IsAny<int?>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new RedisServerException( "NOPERM this user has no permissions" ) );

        RedisRequestQueue<QueuedLookupRequest> queue = new(
            redis.Object,
            Mock.Of<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            Options.Create( new QueueSettings( ) ),
            SupportedProviders.Spotify );

        _ = await Assert.ThrowsAsync<RedisServerException>(
            ( ) => queue.DequeueAsync( tracker.Object, CancellationToken.None ) );
    }
}
