using System.Security.Claims;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Web.Controllers;
using BridgeBeats.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>Tests MVC translation around the refresh-review disposition service.</summary>
[TestClass]
public class RefreshReviewControllerTests {
    private const string SourceUri = "at://did:plc:test/link.bridgebeats.lookup/track:USRC12345678";

    /// <summary>The index action returns the current unresolved review entries as its model.</summary>
    [TestMethod]
    public async Task Index_ReturnsReviewEntries( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        RefreshReviewEntry entry = new( ) {
            SourceRecordUri = SourceUri,
            SourceRecordCid = "bafyreihash",
            SagaId = "refresh-saga",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678"
        };
        _ = reviewStore.Setup( store => store.GetUnresolvedAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [entry] );
        RefreshReviewController controller = new(
            reviewStore.Object,
            Mock.Of<IRefreshReviewDispositionService>( ) );

        IActionResult result = await controller.Index( CancellationToken.None );

        ViewResult view = Assert.IsInstanceOfType<ViewResult>( result );
        IReadOnlyList<RefreshReviewEntry> model = Assert.IsInstanceOfType<IReadOnlyList<RefreshReviewEntry>>( view.Model );
        Assert.AreSame( entry, model.Single( ) );
    }

    /// <summary>Missing displayed revision fields are rejected before disposition is invoked.</summary>
    [TestMethod]
    public async Task Delete_MissingDisplayedRevision_ReturnsBadRequest( ) {
        Mock<IRefreshReviewDispositionService> disposition = new( );
        RefreshReviewController controller = new( Mock.Of<IRefreshReviewStore>( ), disposition.Object );

        IActionResult result = await controller.Delete(
            SourceUri, string.Empty, string.Empty, CancellationToken.None );

        _ = Assert.IsInstanceOfType<BadRequestResult>( result );
        disposition.Verify( service => service.DeleteAsync(
            It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<string>( ),
            It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>A known review entry is delegated with the authenticated actor id.</summary>
    [TestMethod]
    public async Task Delete_KnownEntry_DelegatesWithActor( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IRefreshReviewDispositionService> disposition = new( );
        _ = disposition.Setup( service => service.DeleteAsync(
                SourceUri, "refresh-saga", "bafyreihash", "admin-123", It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( RefreshReviewDispositionOutcome.Deleted );

        RefreshReviewController controller = new( reviewStore.Object, disposition.Object ) {
            TempData = new TempDataDictionary(
                new Microsoft.AspNetCore.Http.DefaultHttpContext( ),
                Mock.Of<ITempDataProvider>( ) ),
            ControllerContext = new ControllerContext {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext {
                    User = new ClaimsPrincipal( new ClaimsIdentity(
                        [new Claim( ClaimTypes.NameIdentifier, "admin-123" )], "test" ) )
                }
            }
        };

        IActionResult result = await controller.Delete(
            SourceUri, "refresh-saga", "bafyreihash", CancellationToken.None );

        _ = Assert.IsInstanceOfType<RedirectToActionResult>( result );
        disposition.Verify( service => service.DeleteAsync(
            SourceUri, "refresh-saga", "bafyreihash", "admin-123", It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>An arbitrary AT-URI not present in review cannot be deleted through this action.</summary>
    [TestMethod]
    public async Task Delete_UnknownEntry_ReturnsNotFound( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IRefreshReviewDispositionService> disposition = new( );
        _ = disposition.Setup( service => service.DeleteAsync(
                SourceUri, "refresh-saga", "bafyreihash", It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( RefreshReviewDispositionOutcome.ReviewEntryNotFound );

        RefreshReviewController controller = new( reviewStore.Object, disposition.Object ) {
            ControllerContext = new ControllerContext {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext( )
            }
        };
        IActionResult result = await controller.Delete(
            SourceUri, "refresh-saga", "bafyreihash", CancellationToken.None );

        _ = Assert.IsInstanceOfType<NotFoundResult>( result );
    }

    /// <summary>A replacement review entry is surfaced as a conflict so the operator must reload.</summary>
    [TestMethod]
    public async Task Delete_ReviewEntryChanged_ReturnsConflict( ) {
        Mock<IRefreshReviewStore> reviewStore = new( );
        Mock<IRefreshReviewDispositionService> disposition = new( );
        _ = disposition.Setup( service => service.DeleteAsync(
                SourceUri, "refresh-saga", "bafyreihash", It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( RefreshReviewDispositionOutcome.ReviewEntryChanged );

        RefreshReviewController controller = new( reviewStore.Object, disposition.Object ) {
            ControllerContext = new ControllerContext {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext( )
            }
        };

        IActionResult result = await controller.Delete(
            SourceUri, "refresh-saga", "bafyreihash", CancellationToken.None );

        _ = Assert.IsInstanceOfType<ConflictObjectResult>( result );
    }
}
