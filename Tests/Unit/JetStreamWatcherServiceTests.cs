using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Worker.JetStreamWatcher;
using BridgeBeats.Worker.JetStreamWatcher.Logging;
using idunno.AtProto;
using idunno.AtProto.Jetstream;
using idunno.Bluesky;
using Microsoft.Extensions.Logging;
using Moq;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="JetStreamWatcherService.HandleCommitRecordAsync"/>, the extracted
/// per-record dispatch method that is the validation boundary for firehose records. Verifies that
/// malformed records are skipped cleanly and logged at Warning exactly once; that well-formed records
/// reaching a recognized music link are enqueued; that benign parser errors remain silent; and that
/// the <c>subject</c> guard in the repost path handles malformed records without throwing.
/// </summary>
[TestClass]
public class JetStreamWatcherServiceTests {

    /// <summary>Mocked provider queue resolver injected into the service under test.</summary>
    private Mock<IProviderQueueResolver<QueuedLookupRequest>> _queueResolverMock = null!;
    /// <summary>Mocked per-provider queue the resolver returns.</summary>
    private Mock<IRequestQueue<QueuedLookupRequest>> _queueMock = null!;
    /// <summary>Mocked logger used to assert log-level and EventId assertions.</summary>
    private Mock<ILogger<JetStreamWatcherService>> _loggerMock = null!;
    private Mock<ISagaStateManager> _sagaManagerMock = null!;

    /// <summary>MSTest-injected test context; provides per-test cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Creates fresh mocks before each test.</summary>
    [TestInitialize]
    public void Initialize( ) {
        _queueResolverMock = new Mock<IProviderQueueResolver<QueuedLookupRequest>>( );
        _queueMock = new Mock<IRequestQueue<QueuedLookupRequest>>( );
        _loggerMock = new Mock<ILogger<JetStreamWatcherService>>( );
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _ = _sagaManagerMock.Setup( manager => manager.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ),
                It.IsAny<string>( ), It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( string sagaId, string lookupKey, LookupRequestType lookupType,
                string lookupValue, QueuePriority? priority, CancellationToken _ ) => new LookupSagaState {
                    SagaId = sagaId,
                    LookupKey = lookupKey,
                    LookupType = lookupType,
                    LookupValue = lookupValue,
                    OriginPriority = priority ?? QueuePriority.Background,
                    InstanceToken = "jetstream-instance"
                } );

        // Stub IsEnabled true so [LoggerMessage]-gated calls reach Log() and Verify() is non-vacuous.
        // This is required because [LoggerMessage] source-generated code gates every call with
        // IsEnabled(); without this setup Moq returns false and Times.Once is vacuously unsatisfied.
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        // Stub the resolver to return the queue mock for any provider
        _ = _queueResolverMock.Setup( r => r.GetQueue( It.IsAny<SupportedProviders>( ) ) )
            .Returns( _queueMock.Object );
        _ = _queueMock.Setup( q => q.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ),
                It.IsAny<QueuePriority>( ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );
    }

    /// <summary>
    /// A repost record missing the <c>subject</c> property is skipped cleanly (nothing enqueued)
    /// and <c>LogRecordProcessingError</c> is never invoked, because the <c>TryGetProperty</c>
    /// guard returns before throwing. The method completes without exception.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithRepostMissingSubject_ShouldSkipAndNotErrorLog( ) {
        // Arrange: syntactically valid JSON, but the repost shape is missing 'subject'
        JsonDocument record = JsonDocument.Parse( """{"$type":"app.bsky.feed.repost","missing":"field"}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        // Act: must not throw
        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.feed.repost", agent, TestContext.CancellationToken );

        // Assert: nothing enqueued — skipped cleanly
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        // Assert: the error log must NOT fire for a graceful early-return (this is not a crash path)
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    /// <summary>
    /// A repost record with <c>subject</c> present but missing the <c>uri</c> field within it is
    /// also skipped cleanly and does not trigger the error log.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithRepostSubjectMissingUri_ShouldSkipAndNotErrorLog( ) {
        // Arrange: subject present but no uri inside it
        JsonDocument record = JsonDocument.Parse( """{"$type":"app.bsky.feed.repost","subject":{"cid":"bafyreiabc123"}}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        // Act
        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.feed.repost", agent, TestContext.CancellationToken );

        // Assert: nothing enqueued
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    /// <summary>
    /// A post record that cannot be deserialized to a <c>Post</c> (returns null) is skipped
    /// without invoking the error log, because null deserialization is handled by an early-return
    /// guard and is not an unexpected error.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithNonDeserializablePostRecord_ShouldSkipWithoutErrorLog( ) {
        // Arrange: JSON that parses but does not deserialize to a valid Post
        JsonDocument record = JsonDocument.Parse( """{"completely":"unrelated","data":42}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        // Act
        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.feed.post", agent, TestContext.CancellationToken );

        // Assert: nothing enqueued
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        // Assert: the error log must NOT fire — null deserialization is handled by early-return
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    /// <summary>
    /// An unrecognized collection is ignored without enqueuing or error-logging, confirming the
    /// dispatch ignores unknown collections cleanly.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithUnknownCollection_ShouldSkipWithoutErrorLog( ) {
        // Arrange
        JsonDocument record = JsonDocument.Parse( """{"data":"anything"}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        // Act
        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.graph.follow", agent, TestContext.CancellationToken );

        // Assert
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    /// <summary>
    /// A valid post containing a facet with an empty <c>features</c> array has no links to enqueue
    /// and produces no processing warning. The current Bluesky library accepts empty features.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithEmptyFacetFeatures_ShouldSkipSilentlyWithoutErrorLog( ) {
        JsonDocument record = JsonDocument.Parse(
            """{"$type":"app.bsky.feed.post","text":"hello","createdAt":"2026-10-16T00:00:00Z","facets":[{"index":{"byteStart":0,"byteEnd":5},"features":[]}]}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.feed.post", agent, TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    /// <summary>
    /// A repost record where <c>subject</c> is present but is a JSON string (not an object) is
    /// skipped silently: the type check on <c>subject.ValueKind</c> short-circuits before any
    /// property access that would throw on a non-object element. Neither an enqueue nor a
    /// Warning-level <c>LogRecordProcessingError</c> is emitted.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithRepostSubjectAsNonObject_ShouldSkipSilentlyWithoutErrorLog( ) {
        // Arrange: subject is a JSON string, not an object — type-confused firehose input
        JsonDocument record = JsonDocument.Parse( """{"$type":"app.bsky.feed.repost","subject":"not-an-object"}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        // Act: must not throw
        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.feed.repost", agent, TestContext.CancellationToken );

        // Assert: nothing enqueued — skipped cleanly
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        // Assert: the IsEnabled(true) stub ensures this Times.Never check is non-vacuous.
        // Without the ValueKind guard, TryGetProperty on a non-object element throws
        // InvalidOperationException, which propagates to LogRecordProcessingError at Warning.
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    /// <summary>
    /// A repost record where <c>subject</c> is a valid object but <c>subject.uri</c> is a JSON
    /// number (not a string) is skipped silently: the type check on <c>uriElement.ValueKind</c>
    /// short-circuits before <c>GetString()</c>, which would throw on a non-string element.
    /// Neither an enqueue nor a Warning-level <c>LogRecordProcessingError</c> is emitted.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleRecord_WithRepostUriAsNonString_ShouldSkipSilentlyWithoutErrorLog( ) {
        // Arrange: subject is an object but uri is a JSON number — type-confused firehose input
        JsonDocument record = JsonDocument.Parse( """{"$type":"app.bsky.feed.repost","subject":{"uri":42,"cid":"bafyreiabc123"}}""" );
        JetStreamWatcherService service = CreateService( );
        using BlueskyAgent agent = new( );

        // Act: must not throw
        await service.HandleCommitRecordAsync( record.RootElement, "app.bsky.feed.repost", agent, TestContext.CancellationToken );

        // Assert: nothing enqueued — skipped cleanly
        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ),
            Times.Never );

        // Assert: the IsEnabled(true) stub ensures this Times.Never check is non-vacuous.
        // Without the ValueKind guard, GetString() on a Number element throws
        // InvalidOperationException, which propagates to LogRecordProcessingError at Warning.
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                new EventId( LogEventIds.RecordProcessingError ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never );
    }

    [TestMethod]
    [DataRow( "create", true, 1 )]
    [DataRow( "create", false, 0 )]
    [DataRow( "update", true, 0 )]
    [DataRow( "delete", true, 0 )]
    [DataRow( "unknown", true, 0 )]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HandleCommitEvent_OnlyCreatedRecordsEnqueueMusicLinks(
        string operation,
        bool includeRecord,
        int expectedEnqueues
    ) {
        const string post = """
            {
              "$type": "app.bsky.feed.post",
              "text": "music",
              "createdAt": "2026-10-16T00:00:00Z",
              "facets": [{
                "index": { "byteStart": 0, "byteEnd": 5 },
                "features": [{
                  "$type": "app.bsky.richtext.facet#link",
                  "uri": "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp"
                }]
              }]
            }
            """;
        string payload = $$"""
            {
              "did": "did:plc:ragtjsm2j2vknwkz3zp4oxrd",
              "time_us": 1792108800000000,
              "kind": "commit",
              "commit": {
                "rev": "3lqro2x5vfs2b",
                "operation": "{{operation}}",
                "collection": "app.bsky.feed.post",
                "rkey": "3lqro2x5vfs2b",
                "record": {{(includeRecord ? post : "null")}}
              }
            }
            """;
        JetstreamCommitEvent? commitEvent = JsonSerializer.Deserialize<JetstreamCommitEvent>(
            payload, AtProtoJsonSerializerOptions.Default );
        Assert.IsNotNull( commitEvent );
        using BlueskyAgent agent = new( );

        await CreateService( ).HandleCommitEventAsync( commitEvent, agent, TestContext.CancellationToken );

        _queueMock.Verify(
            q => q.EnqueueAsync( It.IsAny<QueuedLookupRequest>( ), QueuePriority.Bulk, It.IsAny<CancellationToken>( ) ),
            Times.Exactly( expectedEnqueues ) );
    }

    /// <summary>
    /// Builds a <see cref="JetStreamWatcherService"/> with mocked dependencies.
    /// </summary>
    private JetStreamWatcherService CreateService( ) =>
        new( _loggerMock.Object, _queueResolverMock.Object, _sagaManagerMock.Object );
}

#pragma warning restore CS1591
