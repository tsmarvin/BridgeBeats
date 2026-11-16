using System.Security.Claims;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Web.Controllers;
using BridgeBeats.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="StatisticsController"/>, the MVC controller that renders the lookup
/// statistics page and triggers refreshes. Covers:
/// <list type="bullet">
///   <item>Index action's four-way state selection driven by a single
///   <see cref="IStatisticsService.GetStatus"/> read: DATA (snapshot present), GENERATING (no
///   snapshot, no error), ERROR (no snapshot, error set), and DATA-stale-with-error (snapshot plus
///   error); plus the null-service / error-view guard, the live bootstrap status overlay, and that
///   the in-progress flag is the real running flag (a non-running status yields no spinner).</item>
///   <item>Refresh action: publishes via <see cref="IStatisticsService.RequestRefresh"/> and
///   enforces a per-admin 1-minute throttle.</item>
/// </list>
/// </summary>
[TestClass]
public class StatisticsControllerTests {

    /// <summary>Mock statistics service the controller reads cached stats and bootstrap status from.</summary>
    private Mock<IStatisticsService> _statisticsServiceMock = null!;
    /// <summary>Mock logger for the controller.</summary>
    private Mock<ILogger<StatisticsController>> _loggerMock = null!;
    /// <summary>Real IMemoryCache (in-process) used for throttle tests.</summary>
    private IMemoryCache _memoryCache = null!;
    /// <summary>Fake time provider so throttle expiry can be advanced in tests.</summary>
    private FakeTimeProvider _timeProvider = null!;

    /// <summary>
    /// Creates fresh mocks and a real IMemoryCache before each test. Defaults the live bootstrap
    /// status to null so tests that do not opt into an overlay see no bootstrap status.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _statisticsServiceMock = new Mock<IStatisticsService>( );
        _loggerMock = new Mock<ILogger<StatisticsController>>( );
        _memoryCache = new MemoryCache( new MemoryCacheOptions( ) );
        _timeProvider = new FakeTimeProvider( );

        // Default: live status returns null (no bootstrap has run yet)
        _ = _statisticsServiceMock
            .Setup( x => x.GetLiveBootstrapStatusAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (CacheBootstrapStatus?)null );
    }

    [TestCleanup]
    public void Cleanup( ) {
        _memoryCache.Dispose( );
    }

    // ── Index tests ──────────────────────────────────────────────────────────

    /// <summary>
    /// DATA state: when the status carries a snapshot and is not running, Index returns the default
    /// view with the statistics model and an <c>IsRefreshing</c> view-data flag of false.
    /// </summary>
    [TestMethod]
    public async Task Index_WithSnapshot_ReturnsDataViewWithStats( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = CreateTestStatistics( ), IsRunning = false } );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        LookupStatistics model = Assert.IsInstanceOfType<LookupStatistics>( viewResult.Model );
        Assert.AreEqual( 100, model.TotalRecords );
        Assert.IsFalse( (bool)(viewResult.ViewData["IsRefreshing"] ?? false) );
    }

    /// <summary>
    /// GENERATING state: when no snapshot exists, no error is recorded, and a run IS in progress,
    /// Index returns the <c>Generating</c> view with a <see cref="StatisticsPageViewModel"/> whose
    /// <c>IsRefreshing</c> is true (the spinner shows) and no error set.
    /// </summary>
    [TestMethod]
    public async Task Index_NoSnapshotRunning_ReturnsGeneratingViewWithSpinner( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = null, IsRunning = true, LastError = null } );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        Assert.AreEqual( "Generating", viewResult.ViewName );
        StatisticsPageViewModel model = Assert.IsInstanceOfType<StatisticsPageViewModel>( viewResult.Model );
        Assert.IsTrue( model.IsRefreshing );
        Assert.IsNull( model.LastError );
    }

    /// <summary>
    /// GENERATING state — the §9.3 fix discriminator: when no snapshot exists, no error is recorded,
    /// and NO run is in progress (the honest "queued" case), Index returns the <c>Generating</c>
    /// view with <c>IsRefreshing</c> false — no spinner. The pre-fix controller hard-coded this to
    /// true regardless; pairing this with the running case proves the flag is now the real one.
    /// </summary>
    [TestMethod]
    public async Task Index_NoSnapshotNotRunning_ReturnsGeneratingViewWithoutSpinner( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = null, IsRunning = false, LastError = null } );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        Assert.AreEqual( "Generating", viewResult.ViewName );
        StatisticsPageViewModel model = Assert.IsInstanceOfType<StatisticsPageViewModel>( viewResult.Model );
        Assert.IsFalse( model.IsRefreshing );
        Assert.IsNull( model.LastError );
    }

    /// <summary>
    /// ERROR (no snapshot) state: when no snapshot exists and the last run failed, Index returns the
    /// <c>Generating</c> view carrying the sanitized <c>LastError</c> / <c>LastErrorTime</c> so the
    /// view renders the error panel instead of a perpetual spinner; <c>IsRefreshing</c> is false
    /// because a failed run is not in progress.
    /// </summary>
    [TestMethod]
    public async Task Index_NoSnapshotWithError_ReturnsGeneratingViewWithError( ) {
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes( -2 );
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus {
                Snapshot = null,
                IsRunning = false,
                LastError = "Redis timeout during aggregation",
                LastErrorTime = failedAt
            } );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        Assert.AreEqual( "Generating", viewResult.ViewName );
        StatisticsPageViewModel model = Assert.IsInstanceOfType<StatisticsPageViewModel>( viewResult.Model );
        Assert.AreEqual( "Redis timeout during aggregation", model.LastError );
        Assert.AreEqual( failedAt, model.LastErrorTime );
        Assert.IsFalse( model.IsRefreshing );
    }

    /// <summary>
    /// DATA-stale-with-error state: when a snapshot exists AND the last refresh failed, Index returns
    /// the default data view (last-good snapshot) and surfaces the error via
    /// <c>ViewBag.LastError</c> / <c>ViewBag.LastErrorTime</c> so the view shows the stale-with-error
    /// banner above the data — never a blank or stuck page.
    /// </summary>
    [TestMethod]
    public async Task Index_SnapshotWithError_ReturnsDataViewWithErrorBanner( ) {
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes( -3 );
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus {
                Snapshot = CreateTestStatistics( ),
                IsRunning = false,
                LastError = "Provider API unavailable",
                LastErrorTime = failedAt
            } );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        LookupStatistics model = Assert.IsInstanceOfType<LookupStatistics>( viewResult.Model );
        Assert.AreEqual( 100, model.TotalRecords );
        Assert.AreEqual( "Provider API unavailable", viewResult.ViewData["LastError"] );
        Assert.AreEqual( failedAt, viewResult.ViewData["LastErrorTime"] );
    }

    /// <summary>
    /// DATA state: when the status carries a snapshot and IS running, the data view's
    /// <c>IsRefreshing</c> flag is true so the existing badge shows alongside the data. Pairs with
    /// <see cref="Index_WithSnapshot_ReturnsDataViewWithStats"/> to prove the data-view flag is also
    /// the real running flag, not a constant.
    /// </summary>
    [TestMethod]
    public async Task Index_SnapshotRunning_DataViewIsRefreshingTrue( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = CreateTestStatistics( ), IsRunning = true } );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        _ = Assert.IsInstanceOfType<LookupStatistics>( viewResult.Model );
        Assert.IsTrue( (bool)(viewResult.ViewData["IsRefreshing"] ?? false) );
    }

    /// <summary>
    /// Verifies that when the controller was constructed without a statistics service, Index returns
    /// the <c>Error</c> view with an <see cref="ErrorViewModel"/> whose message reports the service
    /// is not configured.
    /// </summary>
    [TestMethod]
    public async Task Index_WithNullService_ReturnsErrorView( ) {
        StatisticsController controller = new(
            null,
            _memoryCache,
            _timeProvider,
            _loggerMock.Object
        );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        Assert.AreEqual( "Error", viewResult.ViewName );
        ErrorViewModel model = Assert.IsInstanceOfType<ErrorViewModel>( viewResult.Model );
        Assert.IsNotNull( model.Message );
        Assert.Contains( "not configured", model.Message );
    }

    /// <summary>
    /// Verifies that a live cache-bootstrap status is overlaid onto the snapshot model: the
    /// returned model carries the running flag and last-success count from the live status while the
    /// snapshot totals (record count) remain unchanged.
    /// </summary>
    [TestMethod]
    public async Task Index_WithLiveBootstrapStatus_OverlaysStatusOntoSnapshot( ) {
        CacheBootstrapStatus liveStatus = new( ) {
            IsRunning = true,
            LastRunTime = DateTimeOffset.UtcNow.AddHours( -1 ),
            LastSuccessCount = 247404
        };

        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = CreateTestStatistics( ), IsRunning = false } );
        _ = _statisticsServiceMock
            .Setup( x => x.GetLiveBootstrapStatusAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( liveStatus );

        StatisticsController controller = CreateController( WithAdminClaim( "bootstrap-admin" ) );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        LookupStatistics model = Assert.IsInstanceOfType<LookupStatistics>( viewResult.Model );
        Assert.IsNotNull( model.CacheBootstrapStatus );
        Assert.IsTrue( model.CacheBootstrapStatus.IsRunning );
        Assert.AreEqual( 247404, model.CacheBootstrapStatus.LastSuccessCount );
        Assert.AreEqual( 100, model.TotalRecords );
    }

    [TestMethod]
    public async Task Index_WithAuthenticatedLowRole_DoesNotFetchOrExposeBootstrapStatus( ) {
        CacheBootstrapStatus sentinel = new( ) { IsRunning = true, LastSuccessCount = 987654 };
        _ = _statisticsServiceMock.Setup( x => x.GetLiveBootstrapStatusAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( sentinel );
        LookupStatistics cached = CreateTestStatistics( ).WithBootstrapStatus( new CacheBootstrapStatus { LastSuccessCount = 111111 } );
        _ = _statisticsServiceMock.Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = cached } );

        ClaimsPrincipal lowRole = new( new ClaimsIdentity(
            [new Claim( ClaimTypes.Name, "reader" ), new Claim( ClaimTypes.Role, "Reader" )], "test" ) );
        StatisticsController controller = CreateController( lowRole );

        ViewResult result = Assert.IsInstanceOfType<ViewResult>( await controller.Index( ) );
        LookupStatistics model = Assert.IsInstanceOfType<LookupStatistics>( result.Model );
        Assert.IsNull( model.CacheBootstrapStatus );
        Assert.AreNotEqual( 111111, model.CacheBootstrapStatus?.LastSuccessCount );
        _statisticsServiceMock.Verify( x => x.GetLiveBootstrapStatusAsync( It.IsAny<CancellationToken>( ) ), Times.Never );
    }

    /// <summary>
    /// Verifies that when no live bootstrap status is available, the returned model's bootstrap
    /// status is null while the snapshot totals are still surfaced.
    /// </summary>
    [TestMethod]
    public async Task Index_WhenLiveStatusReturnsNull_ModelHasNullBootstrapStatus( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.GetStatus( ) )
            .Returns( new StatisticsStatus { Snapshot = CreateTestStatistics( ), IsRunning = false } );
        _ = _statisticsServiceMock
            .Setup( x => x.GetLiveBootstrapStatusAsync( It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( (CacheBootstrapStatus?)null );

        StatisticsController controller = CreateController( );

        IActionResult result = await controller.Index( );

        ViewResult viewResult = Assert.IsInstanceOfType<ViewResult>( result );
        LookupStatistics model = Assert.IsInstanceOfType<LookupStatistics>( viewResult.Model );
        Assert.IsNull( model.CacheBootstrapStatus );
        Assert.AreEqual( 100, model.TotalRecords );
    }

    // ── Refresh throttle tests ───────────────────────────────────────────────

    /// <summary>
    /// First Refresh call within the window publishes via <c>RequestRefresh</c> and
    /// redirects to Index.
    /// Failure-first evidence: before implementing the throttle, this test fails because
    /// <c>StatisticsController.Refresh</c> called <c>TriggerRefresh()</c> (removed from
    /// interface) — CS1061 prevented compilation, and after fixing the method name the
    /// throttle logic and IMemoryCache/TimeProvider constructor arguments were absent,
    /// causing CS7036. The test now passes against the implementation.
    /// </summary>
    [TestMethod]
    public void Refresh_FirstCall_PublishesAndRedirects( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.RequestRefresh( ) )
            .Returns( true );

        StatisticsController controller = CreateController( WithAdminClaim( "admin-1" ) );

        IActionResult result = controller.Refresh( );

        RedirectToActionResult redirect = Assert.IsInstanceOfType<RedirectToActionResult>( result );
        Assert.AreEqual( "Index", redirect.ActionName );
        _statisticsServiceMock.Verify( x => x.RequestRefresh( ), Times.Once( ) );
    }

    /// <summary>
    /// A second Refresh call from the same admin within the 1-minute window does NOT
    /// publish again and still redirects to Index without erroring.
    /// Failure-first evidence: without the throttle the service would be called twice; this
    /// assertion fails before the throttle guard is in place.
    /// </summary>
    [TestMethod]
    public void Refresh_SecondCallWithinWindow_NotPublishedAgain( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.RequestRefresh( ) )
            .Returns( true );

        StatisticsController controller = CreateController( WithAdminClaim( "admin-1" ) );

        // First call sets the throttle.
        _ = controller.Refresh( );
        // Second call within the window should be suppressed.
        IActionResult result = controller.Refresh( );

        RedirectToActionResult redirect = Assert.IsInstanceOfType<RedirectToActionResult>( result );
        Assert.AreEqual( "Index", redirect.ActionName );
        // RequestRefresh must have been called only once.
        _statisticsServiceMock.Verify( x => x.RequestRefresh( ), Times.Once( ) );
    }

    /// <summary>
    /// After advancing the fake clock past the 1-minute throttle window, the next
    /// Refresh call publishes again.
    /// Failure-first evidence: without TimeProvider-aware throttle, advancing the fake
    /// clock has no effect and the second call would still be suppressed; the Times.Exactly(2)
    /// assertion fails.
    /// </summary>
    [TestMethod]
    public void Refresh_AfterWindowExpiry_PublishesAgain( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.RequestRefresh( ) )
            .Returns( true );

        StatisticsController controller = CreateController( WithAdminClaim( "admin-1" ) );

        // First call sets throttle.
        _ = controller.Refresh( );

        // Advance fake clock past the 1-minute window.
        _timeProvider.Advance( TimeSpan.FromMinutes( 1 ) + TimeSpan.FromSeconds( 1 ) );

        // Call after expiry should publish again.
        IActionResult result = controller.Refresh( );

        RedirectToActionResult redirect = Assert.IsInstanceOfType<RedirectToActionResult>( result );
        Assert.AreEqual( "Index", redirect.ActionName );
        _statisticsServiceMock.Verify( x => x.RequestRefresh( ), Times.Exactly( 2 ) );
    }

    /// <summary>
    /// Negative control — a DIFFERENT admin is NOT blocked by another admin's throttle.
    /// Failure-first evidence: without a per-user key both admins would share the same throttle
    /// entry; the second admin's call would be suppressed and Times.Exactly(2) would fail.
    /// </summary>
    [TestMethod]
    public void Refresh_DifferentAdmin_NotBlockedByOtherAdminsThrottle( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.RequestRefresh( ) )
            .Returns( true );

        // Admin 1 triggers a refresh.
        StatisticsController controller1 = CreateController( WithAdminClaim( "admin-1" ) );
        _ = controller1.Refresh( );

        // Admin 2 triggers a refresh on a different controller instance (different identity).
        StatisticsController controller2 = CreateController( WithAdminClaim( "admin-2" ) );
        _ = controller2.Refresh( );

        // Both should have published.
        _statisticsServiceMock.Verify( x => x.RequestRefresh( ), Times.Exactly( 2 ) );
    }

    /// <summary>
    /// When <see cref="IStatisticsService.RequestRefresh"/> returns false (publish
    /// suppressed or failed inside the service), the throttle is NOT set, so the admin can
    /// retry immediately and the second call is also forwarded to the service.
    /// Failure-first evidence: before the fix the throttle was recorded unconditionally before
    /// calling RequestRefresh, so a false return would still suppress the second call and the
    /// Times.Exactly(2) assertion would fail.
    /// </summary>
    [TestMethod]
    public void Refresh_FailedDispatch_DoesNotSetThrottle_AdminCanRetryImmediately( ) {
        _ = _statisticsServiceMock
            .Setup( x => x.RequestRefresh( ) )
            .Returns( false );

        StatisticsController controller = CreateController( WithAdminClaim( "admin-1" ) );

        // First call — dispatch fails (returns false).
        IActionResult first = controller.Refresh( );

        // Second call immediately — must NOT be blocked by a throttle from the failed first call.
        IActionResult second = controller.Refresh( );

        RedirectToActionResult redirect = Assert.IsInstanceOfType<RedirectToActionResult>( second );
        Assert.AreEqual( "Index", redirect.ActionName );
        // Both calls reached the service because no throttle was set.
        _statisticsServiceMock.Verify( x => x.RequestRefresh( ), Times.Exactly( 2 ) );
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a controller wired to the mock statistics service, the shared IMemoryCache,
    /// the shared FakeTimeProvider, and the mock logger.
    /// </summary>
    private StatisticsController CreateController( ClaimsPrincipal? user = null ) {
        StatisticsController controller = new(
            _statisticsServiceMock.Object,
            _memoryCache,
            _timeProvider,
            _loggerMock.Object
        );

        controller.ControllerContext = new ControllerContext {
            HttpContext = new DefaultHttpContext {
                User = user ?? new ClaimsPrincipal( new ClaimsIdentity( ) )
            }
        };

        // Refresh writes a one-shot notice to TempData; back it with an in-memory provider so the
        // controller does not reach for RequestServices (null in unit tests).
        controller.TempData = new TempDataDictionary(
            controller.HttpContext,
            Mock.Of<ITempDataProvider>( )
        );

        return controller;
    }

    /// <summary>
    /// Returns a <see cref="ClaimsPrincipal"/> carrying a <c>NameIdentifier</c> claim with the
    /// given value, simulating an authenticated admin.
    /// </summary>
    private static ClaimsPrincipal WithAdminClaim( string adminId ) {
        return new ClaimsPrincipal( new ClaimsIdentity(
            [new Claim( ClaimTypes.NameIdentifier, adminId ), new Claim( ClaimTypes.Role, Roles.AspireDashboardAccess )],
            authenticationType: "Test"
        ) );
    }

    /// <summary>
    /// Builds a representative <see cref="LookupStatistics"/> fixture (100 records, sample provider
    /// counts, one recent entry, and populated timestamps) used as the cached-stats payload.
    /// </summary>
    private static LookupStatistics CreateTestStatistics( ) {
        return new LookupStatistics {
            TotalRecords = 100,
            AlbumCount = 25,
            TrackCount = 75,
            ProviderCounts = new Dictionary<string, int> {
                ["Spotify"] = 90,
                ["AppleMusic"] = 85,
                ["Tidal"] = 50
            },
            RecentEntries = [
                new RecentLookupEntry {
                    AtUri = "at://test/1",
                    IsAlbum = false,
                    Artist = "Test Artist",
                    Title = "Test Track",
                    LookedUpAt = DateTimeOffset.UtcNow,
                    CardId = "abc123"
                }
            ],
            EarliestLookup = DateTimeOffset.UtcNow.AddDays( -30 ),
            LatestLookup = DateTimeOffset.UtcNow,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}

#pragma warning restore CS1591
