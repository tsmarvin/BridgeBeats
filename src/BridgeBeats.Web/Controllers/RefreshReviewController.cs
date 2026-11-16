using System.Security.Claims;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BridgeBeats.Web.Controllers;

/// <summary>Administrator review surface for stale PDS records that no provider can refresh.</summary>
[Authorize( Roles = Roles.AspireDashboardAccess )]
public sealed class RefreshReviewController(
    IRefreshReviewStore reviewStore,
    IRefreshReviewDispositionService dispositionService
) : Controller {

    /// <summary>Lists every unresolved stale-record refresh.</summary>
    [HttpGet]
    public async Task<IActionResult> Index( CancellationToken cancellationToken ) {
        IReadOnlyList<RefreshReviewEntry> entries = await reviewStore.GetUnresolvedAsync( cancellationToken );
        return View( entries );
    }

    /// <summary>
    /// Permanently deletes the selected source record from the PDS, then removes its review entry.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize( Roles = Roles.PdsRecordAdministrator )]
    public async Task<IActionResult> Delete(
        string sourceRecordUri,
        string expectedSagaId,
        string expectedSourceRecordCid,
        CancellationToken cancellationToken
    ) {
        if (string.IsNullOrWhiteSpace( sourceRecordUri )
            || string.IsNullOrWhiteSpace( expectedSagaId )
            || string.IsNullOrWhiteSpace( expectedSourceRecordCid )) {
            return BadRequest( );
        }

        string actorId = User.FindFirst( ClaimTypes.NameIdentifier )?.Value
            ?? User.Identity?.Name
            ?? "unknown-authenticated-user";
        RefreshReviewDispositionOutcome outcome = await dispositionService.DeleteAsync(
            sourceRecordUri,
            expectedSagaId,
            expectedSourceRecordCid,
            actorId,
            cancellationToken );

        if (outcome == RefreshReviewDispositionOutcome.ReviewEntryNotFound) {
            return NotFound( );
        }
        if (outcome == RefreshReviewDispositionOutcome.RevisionUnavailable) {
            return Conflict( "The review entry has no source revision and cannot be deleted safely." );
        }
        if (outcome == RefreshReviewDispositionOutcome.ReviewEntryChanged) {
            return Conflict( "The review entry changed after this page was loaded. Reload and review the current revision." );
        }

        TempData["RefreshReviewNotice"] = outcome switch {
            RefreshReviewDispositionOutcome.RevisionChanged =>
                "The PDS record changed after review; the newer revision was preserved and the obsolete review entry was cleared.",
            RefreshReviewDispositionOutcome.AlreadyAbsent =>
                "The PDS record was already absent and its review entry was cleared.",
            _ => "The reviewed PDS record and its review entry were deleted."
        };
        return RedirectToAction( nameof( Index ) );
    }
}
