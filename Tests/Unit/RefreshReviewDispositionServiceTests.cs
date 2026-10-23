using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Web.Logging;
using BridgeBeats.Web.Services;
using Microsoft.Extensions.Logging;
using Moq;

#pragma warning disable CA1873 // Moq Verify lambdas that call ILogger.Log trigger this; the lambdas are never actually executed as logging calls

namespace BridgeBeats.Tests.Unit;

/// <summary>Tests revision-safe, recoverable refresh-review disposition.</summary>
[TestClass]
public class RefreshReviewDispositionServiceTests {
    private const string SourceUri = "at://did:plc:test/link.bridgebeats.lookup/track:USRC12345678";
    private const string SourceCid = "bafyreihash";

    /// <summary>A matching CID is deleted before review state is cleared.</summary>
    [TestMethod]
    public async Task Delete_MatchingRevision_DeletesThenClearsReview( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IATProtoStorageService> storage = new( );
        Mock<ILogger<RefreshReviewDispositionService>> logger = new( );
        // Loose default IsEnabled(false) would make the Times.Never assertion below vacuously
        // true even if LogCleanupSkipped mis-fired on this path; forcing it true makes the
        // negative control meaningful.
        _ = logger.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );
        int sequence = 0;
        int pdsOrder = 0;
        int reviewOrder = 0;
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [CreateEntry( )] );
        _ = storage.Setup( service => service.DeleteMediaLinkResultAsync(
                SourceUri, SourceCid, It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => pdsOrder = Interlocked.Increment( ref sequence ) )
            .ReturnsAsync( MediaLinkDeleteOutcome.Deleted );
        _ = reviewStore.Setup( store => store.DeleteUnresolvedAsync(
                SourceUri, "refresh-saga", SourceCid, It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => reviewOrder = Interlocked.Increment( ref sequence ) )
            .ReturnsAsync( true );

        RefreshReviewDispositionOutcome outcome = await CreateService( reviewStore, storage, logger ).DeleteAsync(
            SourceUri, "refresh-saga", SourceCid, "admin", CancellationToken.None );

        Assert.AreEqual( RefreshReviewDispositionOutcome.Deleted, outcome );
        Assert.IsLessThan( reviewOrder, pdsOrder );

        // Negative control — cleanup succeeded on the first try, so the skipped-cleanup log
        // (EventId 4327) must not fire on this path.
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                new EventId( LogEventIds.Controllers.RefreshReviewDispositionCleanupSkipped ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Never,
            "LogCleanupSkipped (Information, EventId 4327) must NOT fire when cleanup succeeds on the first attempt" );
    }

    /// <summary>
    /// When the CAS-guarded review-entry cleanup finds the entry no longer matches the expected
    /// saga/CID (already removed or replaced by a different actor or worker between this
    /// method's read and its cleanup call), the disposition still completes and reports the
    /// underlying delete outcome rather than throwing or surfacing the skipped cleanup as a
    /// failure.
    /// </summary>
    [TestMethod]
    public async Task Delete_CleanupCasMismatch_StillCompletesAndReturnsDeleted( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IATProtoStorageService> storage = new( );
        Mock<ILogger<RefreshReviewDispositionService>> logger = new( );
        // [LoggerMessage] source-generated code gates every call with IsEnabled(); loose-mock
        // default IsEnabled(false) would make the Times.Once assertion below fail even though
        // LogCleanupSkipped is reached, since the gate would suppress the underlying Log() call.
        _ = logger.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [CreateEntry( )] );
        _ = storage.Setup( service => service.DeleteMediaLinkResultAsync(
                SourceUri, SourceCid, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( MediaLinkDeleteOutcome.Deleted );
        // Redundant with Moq's loose default (a bare Mock<T> already returns false for an
        // unconfigured bool-returning member), but kept explicit: with the log assertion below,
        // this line documents that "false" is the CAS-mismatch scenario under test, not an
        // incidental default.
        _ = reviewStore.Setup( store => store.DeleteUnresolvedAsync(
                SourceUri, "refresh-saga", SourceCid, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        RefreshReviewDispositionOutcome outcome = await CreateService( reviewStore, storage, logger ).DeleteAsync(
            SourceUri, "refresh-saga", SourceCid, "admin", CancellationToken.None );

        Assert.AreEqual( RefreshReviewDispositionOutcome.Deleted, outcome );

        // Assert — the skipped-cleanup log fires exactly once.
        // Disambiguated on EventId 4327 (RefreshReviewDispositionCleanupSkipped) to avoid
        // ambiguity with the Information-level LogDisposition calls (EventId 4325) also on this path.
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                new EventId( LogEventIds.Controllers.RefreshReviewDispositionCleanupSkipped ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once,
            "LogCleanupSkipped (Information, EventId 4327) must fire when the CAS-guarded cleanup finds no matching review entry to remove" );
    }

    /// <summary>A changed revision is preserved while its obsolete review entry is cleared.</summary>
    [TestMethod]
    public async Task Delete_RevisionChanged_PreservesCurrentRecordAndClearsReview( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IATProtoStorageService> storage = new( );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [CreateEntry( )] );
        _ = storage.Setup( service => service.DeleteMediaLinkResultAsync(
                SourceUri, SourceCid, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( MediaLinkDeleteOutcome.RevisionConflict );
        _ = reviewStore.Setup( store => store.DeleteUnresolvedAsync(
                SourceUri, "refresh-saga", SourceCid, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        RefreshReviewDispositionOutcome outcome = await CreateService( reviewStore, storage ).DeleteAsync(
            SourceUri, "refresh-saga", SourceCid, "admin", CancellationToken.None );

        Assert.AreEqual( RefreshReviewDispositionOutcome.RevisionChanged, outcome );
        reviewStore.Verify( store => store.DeleteUnresolvedAsync(
            SourceUri, "refresh-saga", SourceCid, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>An already-absent PDS record still permits recovery of leftover Redis review state.</summary>
    [TestMethod]
    public async Task Delete_RecordAlreadyAbsent_ClearsReview( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IATProtoStorageService> storage = new( );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [CreateEntry( )] );
        _ = storage.Setup( service => service.DeleteMediaLinkResultAsync(
                SourceUri, SourceCid, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( MediaLinkDeleteOutcome.NotFound );
        _ = reviewStore.Setup( store => store.DeleteUnresolvedAsync(
                SourceUri, "refresh-saga", SourceCid, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        RefreshReviewDispositionOutcome outcome = await CreateService( reviewStore, storage ).DeleteAsync(
            SourceUri, "refresh-saga", SourceCid, "admin", CancellationToken.None );

        Assert.AreEqual( RefreshReviewDispositionOutcome.AlreadyAbsent, outcome );
        reviewStore.Verify( store => store.DeleteUnresolvedAsync(
            SourceUri, "refresh-saga", SourceCid, It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>Legacy review entries without a CID cannot authorize an unsafe unconditional delete.</summary>
    [TestMethod]
    public async Task Delete_MissingRevision_RefusesDeletion( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IATProtoStorageService> storage = new( );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [CreateEntry( ) with { SourceRecordCid = null }] );

        RefreshReviewDispositionOutcome outcome = await CreateService( reviewStore, storage ).DeleteAsync(
            SourceUri, "refresh-saga", SourceCid, "admin", CancellationToken.None );

        Assert.AreEqual( RefreshReviewDispositionOutcome.RevisionUnavailable, outcome );
        storage.Verify( service => service.DeleteMediaLinkResultAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A stale form cannot dispose a replacement review entry for the same source URI.</summary>
    [TestMethod]
    public async Task Delete_ReviewEntryChanged_PreservesReplacement( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IATProtoStorageService> storage = new( );
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [CreateEntry( ) with {
                SagaId = "replacement-saga",
                SourceRecordCid = "bafyreinewer"
            }] );

        RefreshReviewDispositionOutcome outcome = await CreateService( reviewStore, storage ).DeleteAsync(
            SourceUri, "refresh-saga", SourceCid, "admin", CancellationToken.None );

        Assert.AreEqual( RefreshReviewDispositionOutcome.ReviewEntryChanged, outcome );
        storage.Verify( service => service.DeleteMediaLinkResultAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
        reviewStore.Verify( store => store.DeleteUnresolvedAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<string>( ),
            It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    private static RefreshReviewDispositionService CreateService(
        Mock<IRefreshReviewStore> reviewStore,
        Mock<IATProtoStorageService> storage,
        Mock<ILogger<RefreshReviewDispositionService>>? logger = null
    ) => new( reviewStore.Object, storage.Object, logger?.Object ?? Mock.Of<ILogger<RefreshReviewDispositionService>>( ) );

    private static RefreshReviewEntry CreateEntry( ) => new( ) {
        SourceRecordUri = SourceUri,
        SourceRecordCid = SourceCid,
        SagaId = "refresh-saga",
        LookupType = LookupRequestType.IsrcLookup,
        LookupValue = "USRC12345678",
        FailedAt = DateTimeOffset.UtcNow
    };
}
