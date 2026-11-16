using System.Security.Claims;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BridgeBeats.Web.Controllers;

/// <summary>
/// Serves the lookup statistics page and an administrator-triggered refresh. Reads cached statistics,
/// overlays the live cache-bootstrap status, and indicates when a refresh is in progress. Requires an
/// authenticated session.
/// </summary>
/// <param name="statisticsService">Optional statistics service that supplies cached statistics, live bootstrap status, and refresh control; when null, the page reports the service is not configured.</param>
/// <param name="memoryCache">In-process cache used to enforce the per-admin refresh throttle.</param>
/// <param name="timeProvider">Time provider used for throttle-window expiry calculation.</param>
/// <param name="logger">Logger for statistics availability, refresh errors, and throttle events.</param>
[Authorize]
public partial class StatisticsController(
    IStatisticsService? statisticsService,
    IMemoryCache memoryCache,
    TimeProvider timeProvider,
    ILogger<StatisticsController> logger
) : Controller {

    /// <summary>How long each admin's refresh button is suppressed after a successful dispatch.</summary>
    private static readonly TimeSpan s_throttleWindow = TimeSpan.FromMinutes( 1 );

    /// <summary>
    /// Renders the statistics page, combining cached statistics with the live cache-bootstrap status. Shows a
    /// "generating" view when no statistics are cached yet.
    /// </summary>
    /// <returns>
    /// The statistics view with the combined data on success; the <c>Generating</c> view while statistics are
    /// being produced; the <c>Error</c> view when the service is not configured or retrieval fails. Requires
    /// an authenticated session via <c>[Authorize]</c>.
    /// </returns>
    public async Task<IActionResult> Index( ) {
        if (statisticsService is null) {
            LogNotAvailable( );
            return View( "Error", new ErrorViewModel { Message = "Statistics service is not configured." } );
        }

        try {
            // Single read of the worker status document: snapshot presence, the real running
            // flag, and the last-failure fields all come from this one value.
            StatisticsStatus? status = statisticsService.GetStatus( );
            LookupStatistics? snapshot = status?.Snapshot;
            bool isRunning = status?.IsRunning ?? false;

            if (snapshot is null) {
                // No snapshot yet: the view selects GENERATING vs ERROR (no snapshot) from
                // LastError. IsRefreshing is the real running flag, never hard-coded.
                return View( "Generating", new StatisticsPageViewModel {
                    IsRefreshing = isRunning,
                    LastError = status?.LastError,
                    LastErrorTime = status?.LastErrorTime,
                    NextScheduledRun = status?.NextScheduledRun
                } );
            }

            // Overlay live bootstrap status so the page reflects the current run state
            // without waiting for the full statistics cache to expire.
            CacheBootstrapStatus? liveStatus = User.IsInRole(Roles.AspireDashboardAccess)
                ? await statisticsService.GetLiveBootstrapStatusAsync( HttpContext?.RequestAborted ?? CancellationToken.None )
                : null;
            LookupStatistics displayStats = snapshot.WithBootstrapStatus( liveStatus );

            ViewBag.IsRefreshing = isRunning;
            ViewBag.LastError = status?.LastError;
            ViewBag.LastErrorTime = status?.LastErrorTime;
            return View( displayStats );
        } catch (Exception ex) {
            LogRetrieveError( ex );
            return View( "Error", new ErrorViewModel { Message = "Unable to retrieve statistics. Please try again later." } );
        }
    }

    /// <summary>
    /// Publishes a manual refresh request to the Maintenance worker, then redirects to the statistics
    /// page. Applies a per-admin 1-minute throttle so bursts from the same admin do not result in repeated
    /// publishes. Restricted to users in the <see cref="BridgeBeats.Contracts.Constants.Roles.AspireDashboardAccess"/> role.
    /// </summary>
    /// <returns>
    /// HTTP POST. A redirect to <see cref="Index"/>. Requires the dashboard-access role and a valid
    /// anti-forgery token.
    /// </returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize( Roles = Roles.AspireDashboardAccess )]
    public IActionResult Refresh( ) {
        if (statisticsService is null) {
            return RedirectToAction( nameof( Index ) );
        }

        // Per-admin throttle: keyed on the admin's NameIdentifier claim, falling back to
        // Identity.Name. When the key is absent, the stored value is the expiry instant so
        // the throttle window can be advanced via TimeProvider in tests.
        string? adminId = User.FindFirst( ClaimTypes.NameIdentifier )?.Value
            ?? User.Identity?.Name;

        string? cacheKey = adminId is not null ? $"stats:refresh-throttle:{adminId}" : null;

        if (cacheKey is not null) {
            if (memoryCache.TryGetValue( cacheKey, out DateTimeOffset throttleUntil )
                && timeProvider.GetUtcNow( ) < throttleUntil) {
                LogRefreshThrottled( );
                TempData["RefreshNotice"] = "Refresh requested too recently — try again in a moment.";
                return RedirectToAction( nameof( Index ) );
            }
        }

        try {
            if (statisticsService.RequestRefresh( )) {
                if (cacheKey is not null) {
                    // Record the throttle expiry so a FakeTimeProvider can advance past it in tests.
                    DateTimeOffset expiry = timeProvider.GetUtcNow( ) + s_throttleWindow;
                    _ = memoryCache.Set( cacheKey, expiry,
                        new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = s_throttleWindow } );
                }
                TempData["RefreshNotice"] = "Refresh requested. Statistics will update shortly.";
            } else {
                TempData["RefreshNotice"] = "Couldn't request a refresh right now — please try again.";
            }
        } catch (Exception ex) {
            LogRefreshError( ex );
            TempData["RefreshNotice"] = "Couldn't request a refresh right now — please try again.";
        }

        return RedirectToAction( nameof( Index ) );
    }
}
