using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <c>RateLimitingMiddleware</c>, the per-user hourly request limiter. Back the
/// middleware with a real in-memory SQLite <see cref="ApplicationDbContext"/> and Identity
/// <see cref="UserManager{ApplicationUser}"/> so the atomic per-user counter UPDATE runs against an
/// actual database. Cover the fresh-window first request, the at-cap boundary (allowed),
/// over-cap rejection with a 429 (the count still increments), expired-window reset, two concurrent
/// requests at the boundary (exactly one passes), API-key-scheme user resolution, and the
/// startup-recovery SQL that releases users pinned by an expired window.
/// </summary>
[TestClass]
public class RateLimitingMiddlewareTests {

    /// <summary>MSTest-injected context, used for per-test cancellation tokens.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The per-hour request cap the middleware is configured with for these tests.</summary>
    private const int MaxRequestsPerHour = 5;

    /// <summary>The open in-memory SQLite connection backing the test database for its lifetime.</summary>
    private SqliteConnection _connection = null!;
    /// <summary>The EF Core context over the in-memory database.</summary>
    private ApplicationDbContext _dbContext = null!;
    /// <summary>Identity user manager the middleware uses to resolve the current user.</summary>
    private UserManager<ApplicationUser> _userManager = null!;

    /// <summary>Opens an in-memory SQLite database, creates the schema, and builds the user manager.</summary>
    [TestInitialize]
    public void Initialize( ) {
        // Open a shared in-process SQLite connection so the schema persists for the lifetime of
        // each test. ":memory:" with a shared connection gives us a real SQLite store.
        _connection = new SqliteConnection( "Data Source=:memory:" );
        _connection.Open( );

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>( )
            .UseSqlite( _connection )
            .Options;

        _dbContext = new ApplicationDbContext( options );
        _ = _dbContext.Database.EnsureCreated( );

        // Minimal UserManager wired to the same DbContext
        UserStore<ApplicationUser> store = new( _dbContext );
        _userManager = new UserManager<ApplicationUser>(
            store,
            null!, null!, null!, null!, null!,
            new IdentityErrorDescriber( ),
            null!, null! );
    }

    /// <summary>Disposes the user manager, context, and SQLite connection after each test.</summary>
    [TestCleanup]
    public void Cleanup( ) {
        _userManager.Dispose( );
        _dbContext.Dispose( );
        _connection.Dispose( );
    }

    /// <summary>
    /// Inserts an <see cref="ApplicationUser"/> with the given user name, request count, and
    /// rate-limit window start, and returns it.
    /// </summary>
    private async Task<ApplicationUser> SeedUserAsync(
        ApplicationDbContext ctx,
        string userName = "testuser",
        int requestCount = 0,
        DateTime? windowStart = null
    ) {
        ApplicationUser user = new( ) {
            Id = Guid.NewGuid( ).ToString( ),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant( ),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName}@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid( ).ToString( ),
            RequestCount = requestCount,
            RateLimitWindowStart = windowStart
        };
        _ = ctx.Users.Add( user );
        _ = await ctx.SaveChangesAsync( TestContext.CancellationToken );
        return user;
    }

    /// <summary>
    /// Builds an authenticated <see cref="HttpContext"/> for a <c>POST /music/lookup/isrc</c> request
    /// whose principal carries the given user name.
    /// </summary>
    private static HttpContext BuildAuthenticatedContext( string userName ) {
        DefaultHttpContext ctx = new( );
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/music/lookup/isrc";
        System.Security.Claims.ClaimsIdentity identity = new(
            [new System.Security.Claims.Claim( System.Security.Claims.ClaimTypes.Name, userName )],
            "TestScheme"
        );
        ctx.User = new System.Security.Claims.ClaimsPrincipal( identity );
        return ctx;
    }

    /// <summary>Builds a fresh in-memory cache for the middleware's per-user lookups.</summary>
    private static IMemoryCache BuildCache( ) =>
        new MemoryCache( new MemoryCacheOptions( ) );

    /// <summary>
    /// Invokes the middleware for the given user and returns the resulting HTTP status code plus
    /// whether the downstream delegate ran (passed) or was short-circuited (rejected).
    /// </summary>
    private async Task<(int StatusCode, bool NextCalled)> InvokeAsync(
        ApplicationUser user,
        ApplicationDbContext dbCtx,
        IMemoryCache? cache = null
    ) {
        cache ??= BuildCache( );

        bool nextCalled = false;
        RequestDelegate next = _ => {
            nextCalled = true;
            return Task.CompletedTask;
        };

        RateLimitingMiddleware mw = new( next, NullLogger<RateLimitingMiddleware>.Instance, MaxRequestsPerHour );

        HttpContext httpCtx = BuildAuthenticatedContext( user.UserName! );
        await mw.InvokeAsync( httpCtx, _userManager, dbCtx, cache );

        return (httpCtx.Response.StatusCode, nextCalled);
    }

    /// <summary>
    /// The first request from a user with no active window passes (200), the downstream delegate runs,
    /// and the persisted count is 1 with a freshly set window start.
    /// </summary>
    [TestMethod]
    public async Task T1_FirstRequestFreshWindow_PassesAndPersistsCountOf1( ) {
        // Arrange
        ApplicationUser user = await SeedUserAsync( _dbContext, requestCount: 0, windowStart: null );

        // Act
        (int statusCode, bool nextCalled) = await InvokeAsync( user, _dbContext );

        // Assert — response
        Assert.AreEqual( 200, statusCode );
        Assert.IsTrue( nextCalled );

        // Assert — persisted state
        ApplicationUser persisted = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == user.Id, TestContext.CancellationToken );
        Assert.AreEqual( 1, persisted.RequestCount );
        Assert.IsNotNull( persisted.RateLimitWindowStart );
    }

    /// <summary>
    /// A request from a user one below the cap (within an active window) passes (200) and persists
    /// the count at exactly the maximum.
    /// </summary>
    [TestMethod]
    public async Task T2_AtCapBoundaryAllowed_PassesAndPersistsCountAtMax( ) {
        // Arrange — RequestCount = max - 1, active window
        DateTime windowStart = DateTime.UtcNow.AddMinutes( -30 );
        ApplicationUser user = await SeedUserAsync( _dbContext, requestCount: MaxRequestsPerHour - 1, windowStart: windowStart );

        // Act
        (int statusCode, bool nextCalled) = await InvokeAsync( user, _dbContext );

        // Assert — response
        Assert.AreEqual( 200, statusCode );
        Assert.IsTrue( nextCalled );

        // Assert — persisted count is exactly max
        ApplicationUser persisted = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == user.Id, TestContext.CancellationToken );
        Assert.AreEqual( MaxRequestsPerHour, persisted.RequestCount );
    }

    /// <summary>
    /// A request from a user already at the cap (active window) is rejected with 429 and the
    /// downstream delegate does not run, yet the atomic UPDATE still increments the persisted count
    /// to max + 1.
    /// </summary>
    [TestMethod]
    public async Task T3_OverCapActiveWindow_Returns429AndPersistsIncrementedCount( ) {
        // Arrange — RequestCount = max, active window (30 minutes ago, within the 1-hour window)
        DateTime windowStart = DateTime.UtcNow.AddMinutes( -30 );
        ApplicationUser user = await SeedUserAsync( _dbContext, requestCount: MaxRequestsPerHour, windowStart: windowStart );

        // Act
        (int statusCode, bool nextCalled) = await InvokeAsync( user, _dbContext );

        // Assert — response is 429
        Assert.AreEqual( 429, statusCode );
        Assert.IsFalse( nextCalled );

        // Assert — PERSISTED store: count incremented to max+1 (atomic CASE ran, result > max → rejected)
        ApplicationUser persisted = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == user.Id, TestContext.CancellationToken );
        Assert.AreEqual( MaxRequestsPerHour + 1, persisted.RequestCount,
            "The atomic UPDATE must have incremented the count even though the request was rejected." );
    }

    /// <summary>
    /// A request from a user at the cap but whose window has expired passes (200), resets the count
    /// to 1, and sets a new window start later than the old expired one.
    /// </summary>
    [TestMethod]
    public async Task T4_ExpiredWindowAtCap_PassesAndResetsWindow( ) {
        // Arrange — RequestCount = max, window expired 61 minutes ago
        DateTime expiredWindowStart = DateTime.UtcNow.AddMinutes( -61 );
        ApplicationUser user = await SeedUserAsync( _dbContext, requestCount: MaxRequestsPerHour, windowStart: expiredWindowStart );

        // Act
        (int statusCode, bool nextCalled) = await InvokeAsync( user, _dbContext );

        // Assert — response passes
        Assert.AreEqual( 200, statusCode );
        Assert.IsTrue( nextCalled );

        // Assert — persisted state: count reset to 1, window reset to approx now
        ApplicationUser persisted = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == user.Id, TestContext.CancellationToken );
        Assert.AreEqual( 1, persisted.RequestCount );
        Assert.IsNotNull( persisted.RateLimitWindowStart );
        Assert.IsTrue( persisted.RateLimitWindowStart > expiredWindowStart,
            "Window start must have been reset to a time after the old expired window." );
    }

    /// <summary>
    /// Two concurrent requests from a user one below the cap (sharing a cache but using separate DB
    /// contexts) resolve to exactly one pass (200) and one rejection (429), with the persisted count
    /// landing at max + 1 — confirming the atomic UPDATE serializes the boundary.
    /// </summary>
    [TestMethod]
    public async Task T5_ConcurrentRequestsAtBoundary_ExactlyOnePassesOneRejected( ) {
        // Arrange — RequestCount = max - 1, active window
        DateTime windowStart = DateTime.UtcNow.AddMinutes( -30 );
        ApplicationUser user = await SeedUserAsync( _dbContext, requestCount: MaxRequestsPerHour - 1, windowStart: windowStart );

        // Use a shared cache so both requests use the same cached user
        IMemoryCache sharedCache = BuildCache( );

        // Build two DbContexts on the same connection (shared in-process SQLite)
        DbContextOptions<ApplicationDbContext> opts = new DbContextOptionsBuilder<ApplicationDbContext>( )
            .UseSqlite( _connection )
            .Options;
        await using ApplicationDbContext dbCtx1 = new( opts );
        await using ApplicationDbContext dbCtx2 = new( opts );

        // Act — fire two requests concurrently, each with its own DbContext
        Task<(int StatusCode, bool NextCalled)> req1 = InvokeAsync( user, dbCtx1, sharedCache );
        Task<(int StatusCode, bool NextCalled)> req2 = InvokeAsync( user, dbCtx2, sharedCache );

        (int status1, bool next1) = await req1;
        (int status2, bool next2) = await req2;

        // Assert — exactly one 200, one 429
        int passCount = (status1 == 200 ? 1 : 0) + (status2 == 200 ? 1 : 0);
        int rejectCount = (status1 == 429 ? 1 : 0) + (status2 == 429 ? 1 : 0);
        Assert.AreEqual( 1, passCount, $"Exactly one request must pass. Got statuses: {status1}, {status2}" );
        Assert.AreEqual( 1, rejectCount, $"Exactly one request must be rejected. Got statuses: {status1}, {status2}" );

        // Assert — persisted count is max+1 (first wrote max, second wrote max+1 → rejected)
        await using ApplicationDbContext readCtx = new( opts );
        ApplicationUser persisted = await readCtx.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == user.Id, TestContext.CancellationToken );
        Assert.AreEqual( MaxRequestsPerHour + 1, persisted.RequestCount,
            "After two concurrent requests from max-1, count must be max+1." );
    }

    /// <summary>
    /// A request whose principal carries an API-key-scheme user name resolves the user by name and
    /// enforces the same limit, passing (200) and persisting a count of 1 on first use.
    /// </summary>
    [TestMethod]
    public async Task T6_ApiKeyScheme_ResolvesUserByUserNameAndEnforcesLimit( ) {
        // Arrange — user with UserName that matches the principal Name claim
        ApplicationUser user = await SeedUserAsync( _dbContext, userName: "apiuser", requestCount: 0, windowStart: null );

        // Act — principal Name = UserName (ApiKey scheme common path)
        (int statusCode, bool nextCalled) = await InvokeAsync( user, _dbContext );

        // Assert — resolved and rate-limited correctly
        Assert.AreEqual( 200, statusCode );
        Assert.IsTrue( nextCalled );

        // Assert — persisted by PK (Id), not by Name claim string
        ApplicationUser persisted = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == user.Id, TestContext.CancellationToken );
        Assert.AreEqual( 1, persisted.RequestCount );
    }

    /// <summary>
    /// The startup-recovery UPDATE (reset count and window where the window is older than one hour)
    /// releases a user pinned at the cap by an expired window while leaving a user with an active
    /// window untouched, and is idempotent on a second pass.
    /// </summary>
    [TestMethod]
    public async Task T7_StartupRecovery_ReleasesPinnedUserWithExpiredWindow( ) {
        // Arrange — seed a user pinned at max with a window expired 2 hours ago
        DateTime expiredWindow = DateTime.UtcNow.AddHours( -2 );
        ApplicationUser pinned = await SeedUserAsync(
            _dbContext,
            userName: "pinneduser",
            requestCount: MaxRequestsPerHour,
            windowStart: expiredWindow );

        // Seed a second user with an ACTIVE window (must not be touched by recovery)
        DateTime activeWindow = DateTime.UtcNow.AddMinutes( -30 );
        ApplicationUser active = await SeedUserAsync(
            _dbContext,
            userName: "activeuser",
            requestCount: 2,
            windowStart: activeWindow );

        // Act — run the same recovery SQL used by InitializeDatabaseAsync
        DateTime windowFloor = DateTime.UtcNow.AddHours( -1 );
        _ = await _dbContext.Database.ExecuteSqlAsync(
            $@"UPDATE AspNetUsers
               SET RequestCount = 0,
                   RateLimitWindowStart = NULL
               WHERE RateLimitWindowStart IS NOT NULL
                 AND RateLimitWindowStart <= {windowFloor}",
            TestContext.CancellationToken );

        // Assert — pinned user released
        ApplicationUser pinnedAfter = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == pinned.Id, TestContext.CancellationToken );
        Assert.AreEqual( 0, pinnedAfter.RequestCount,
            "Pinned user with expired window must be released (RequestCount = 0)." );
        Assert.IsNull( pinnedAfter.RateLimitWindowStart,
            "Pinned user with expired window must have RateLimitWindowStart set to NULL." );

        // Assert — active user untouched
        ApplicationUser activeAfter = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == active.Id, TestContext.CancellationToken );
        Assert.AreEqual( 2, activeAfter.RequestCount,
            "User with active window must not be modified by the recovery pass." );
        Assert.IsNotNull( activeAfter.RateLimitWindowStart );

        // Assert — idempotency: running again produces the same result (no error, no thrash)
        _ = await _dbContext.Database.ExecuteSqlAsync(
            $@"UPDATE AspNetUsers
               SET RequestCount = 0,
                   RateLimitWindowStart = NULL
               WHERE RateLimitWindowStart IS NOT NULL
                 AND RateLimitWindowStart <= {windowFloor}",
            TestContext.CancellationToken );

        ApplicationUser pinnedAfter2 = await _dbContext.Users.AsNoTracking( )
            .FirstAsync( u => u.Id == pinned.Id, TestContext.CancellationToken );
        Assert.AreEqual( 0, pinnedAfter2.RequestCount );
        Assert.IsNull( pinnedAfter2.RateLimitWindowStart );
    }
}
