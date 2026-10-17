using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="RedisRateLimitTracker"/> against a real Redis instance (the shared
/// Testcontainers Redis). Verifies storing and reading policy-derived provider/endpoint rate-limit state with
/// TTL, that already-expired limits are not stored, clearing, listing all rate-limited scopes for a
/// provider, cross-provider isolation, and automatic expiry. Requires Docker to be running on the host
/// machine.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
public class RedisRateLimitTrackerTests {

    /// <summary>The shared Redis connection used by the tracker under test.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>Mock logger captured for the tracker under test.</summary>
    private Mock<ILogger<RedisRateLimitTracker>> _mockLogger = null!;
    /// <summary>The tracker under test, recreated for each test.</summary>
    private RedisRateLimitTracker _tracker = null!;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Requires the shared Redis container and opens a connection to it for the test class.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
    }

    /// <summary>
    /// Closes and disposes the Redis connection after the class completes.
    /// </summary>
    [ClassCleanup]
    public static async Task ClassCleanup( ) {
        if (s_redis is not null) {
            await s_redis.CloseAsync( );
            s_redis.Dispose( );
        }
    }

    /// <summary>
    /// Clears leftover <c>ratelimit:*</c> keys and constructs a fresh tracker before each test.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        // Clear only rate limit-related keys before each test
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        await foreach (RedisKey key in server.KeysAsync( pattern: "ratelimit:*" )) {
            _ = await db.KeyDeleteAsync( key );
        }

        _mockLogger = new Mock<ILogger<RedisRateLimitTracker>>( );

        _tracker = new RedisRateLimitTracker(
            s_redis,
            Options.Create( new QueueSettings( ) ),
            _mockLogger.Object
        );
    }

    /// <summary>
    /// Verifies the state for an endpoint with no stored limit reports not rate limited with no
    /// retry-after or time-remaining.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetStateAsync_ReturnsNotRateLimited_WhenNoEntry( ) {
        // Act
        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );

        // Assert
        Assert.IsFalse( state.IsRateLimited );
        Assert.IsNull( state.RetryAfter );
        Assert.IsNull( state.TimeRemaining );
    }

    /// <summary>
    /// Verifies setting a future rate limit stores it so the state reports rate limited with a
    /// retry-after close to the supplied value.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetRateLimitedAsync_StoresRateLimitWithTtl( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddSeconds( 30 );

        // Act
        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            retryAfter,
            TestContext.CancellationToken
        );

        // Assert
        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );

        Assert.IsTrue( state.IsRateLimited );
        Assert.IsNotNull( state.RetryAfter );
        Assert.IsNotNull( state.TimeRemaining );

        // RetryAfter should be close to what we set (within a second due to timing)
        TimeSpan diff = (retryAfter - state.RetryAfter.Value).Duration( );
        Assert.IsLessThan( TimeSpan.FromSeconds( 1 ), diff, $"RetryAfter diff was {diff}" );
    }

    /// <summary>An extreme provider retry window is bounded before it enters durable shared state.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetRateLimitedAsync_ClampsExtremeFutureWindow( ) {
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            DateTimeOffset.MaxValue,
            TestContext.CancellationToken );

        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify, "/v1/search", TestContext.CancellationToken );
        Assert.IsTrue( state.IsRateLimited );
        Assert.IsNotNull( state.RetryAfter );
        Assert.IsLessThanOrEqualTo(
            before.Add( QueueSettings.DefaultMaximumRateLimitRetryAfter ).AddSeconds( 1 ),
            state.RetryAfter.Value );
    }

    /// <summary>Legacy far-future tracker state is removed instead of suppressing dequeue indefinitely.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetStateAsync_RemovesLegacyFarFutureWindow( ) {
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddYears( 10 );
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.StringSetAsync(
            "ratelimit:Spotify:provider", retryAfter.ToString( "O" ), TimeSpan.FromHours( 2 ) );
        _ = await db.SortedSetAddAsync(
            "ratelimit:active:spotify", "provider", retryAfter.ToUnixTimeMilliseconds( ) );

        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify, "/v1/search", TestContext.CancellationToken );

        Assert.IsFalse( state.IsRateLimited );
        Assert.IsFalse( await db.KeyExistsAsync( "ratelimit:Spotify:provider" ) );
    }

    /// <summary>A shorter concurrent observation cannot reduce an active provider cooldown.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetRateLimitedAsync_ShorterWindowAfterLonger_ShouldPreserveLongerWindow( ) {
        DateTimeOffset longer = DateTimeOffset.UtcNow.AddMinutes( 6 );
        DateTimeOffset shorter = DateTimeOffset.UtcNow.AddMinutes( 2 );

        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify, "/v1/tracks", longer, TestContext.CancellationToken );
        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify, "/v1/search", shorter, TestContext.CancellationToken );

        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify, "/v1/albums", TestContext.CancellationToken );

        Assert.IsTrue( state.IsRateLimited );
        Assert.IsNotNull( state.RetryAfter );
        Assert.IsLessThan( TimeSpan.FromMilliseconds( 2 ), (state.RetryAfter.Value - longer).Duration( ) );
    }

    /// <summary>
    /// Verifies setting a rate limit whose retry-after is already in the past stores nothing, so the
    /// state reports not rate limited.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SetRateLimitedAsync_DoesNotStore_WhenAlreadyExpired( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddSeconds( -10 );

        // Act
        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            retryAfter,
            TestContext.CancellationToken
        );

        // Assert - should not be stored
        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );

        Assert.IsFalse( state.IsRateLimited );
    }

    /// <summary>
    /// Verifies clearing a stored rate limit removes it so the state reports not rate limited.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ClearAsync_RemovesRateLimit( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );
        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            retryAfter,
            TestContext.CancellationToken
        );

        // Verify it's set
        RateLimitState stateBefore = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );
        Assert.IsTrue( stateBefore.IsRateLimited );

        // Act
        await _tracker.ClearAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );

        // Assert
        RateLimitState stateAfter = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );
        Assert.IsFalse( stateAfter.IsRateLimited );
    }

    /// <summary>
    /// Verifies listing all rate-limited endpoints returns only the endpoints stored for the requested
    /// provider.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetAllRateLimitedAsync_ReturnsAllEndpointsForProvider( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );

        await _tracker.SetRateLimitedAsync( SupportedProviders.Spotify, "/v1/search", retryAfter, TestContext.CancellationToken );
        await _tracker.SetRateLimitedAsync( SupportedProviders.Spotify, "/v1/tracks", retryAfter.AddMinutes( 1 ), TestContext.CancellationToken );
        await _tracker.SetRateLimitedAsync( SupportedProviders.AppleMusic, "/v1/catalog", retryAfter, TestContext.CancellationToken );

        // Act
        IReadOnlyList<RateLimitedEndpoint> spotifyEndpoints =
            await _tracker.GetAllRateLimitedAsync( SupportedProviders.Spotify, TestContext.CancellationToken );

        IReadOnlyList<RateLimitedEndpoint> appleEndpoints =
            await _tracker.GetAllRateLimitedAsync( SupportedProviders.AppleMusic, TestContext.CancellationToken );

        // Assert
        Assert.HasCount( 1, spotifyEndpoints );
        Assert.HasCount( 1, appleEndpoints );

        Assert.Contains<RateLimitedEndpoint>( e => e.Endpoint == "provider"
            && Math.Abs( (e.RetryAfter - retryAfter.AddMinutes( 1 )).TotalMilliseconds ) < 2,
            spotifyEndpoints );
        Assert.Contains<RateLimitedEndpoint>( e => e.Endpoint == "/v1/catalog", appleEndpoints );
    }

    /// <summary>Only canonical endpoint members participate in active rate-limit state.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task GetStateAsync_NoncanonicalMember_IsIgnoredAndRemovedFromIndex( ) {
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 2 );
        IDatabase db = s_redis!.GetDatabase( );
        _ = await db.StringSetAsync(
            "ratelimit:Spotify:bulktracks",
            retryAfter.ToString( "O" ),
            TimeSpan.FromMinutes( 2 ) );
        _ = await db.SortedSetAddAsync(
            "ratelimit:active:spotify",
            "bulktracks",
            retryAfter.ToUnixTimeMilliseconds( ) );

        RateLimitState state = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "tracks/:id",
            TestContext.CancellationToken );

        Assert.IsFalse( state.IsRateLimited );
        double? legacyScore = await db.SortedSetScoreAsync(
            "ratelimit:active:spotify", "bulktracks" );
        Assert.IsNull( legacyScore );
        Assert.IsTrue( await db.KeyExistsAsync( "ratelimit:Spotify:bulktracks" ),
            "A noncanonical backing key should be left to its existing TTL." );
    }

    /// <summary>
    /// Verifies a rate limit set for one provider's endpoint does not leak into the same endpoint path on
    /// other providers.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DifferentProviders_HaveIsolatedRateLimits( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );

        await _tracker.SetRateLimitedAsync( SupportedProviders.Spotify, "/search", retryAfter, TestContext.CancellationToken );

        // Act
        RateLimitState spotifyState = await _tracker.GetStateAsync( SupportedProviders.Spotify, "/search", TestContext.CancellationToken );
        RateLimitState appleState = await _tracker.GetStateAsync( SupportedProviders.AppleMusic, "/search", TestContext.CancellationToken );
        RateLimitState tidalState = await _tracker.GetStateAsync( SupportedProviders.Tidal, "/search", TestContext.CancellationToken );

        // Assert - only Spotify should be rate limited
        Assert.IsTrue( spotifyState.IsRateLimited );
        Assert.IsFalse( appleState.IsRateLimited );
        Assert.IsFalse( tidalState.IsRateLimited );
    }

    /// <summary>
    /// Verifies a short-lived rate limit expires on its own via Redis TTL so the state reports not rate
    /// limited after the window elapses.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RateLimit_ExpiresAutomatically( ) {
        // Arrange - set a very short TTL
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMilliseconds( 500 );

        await _tracker.SetRateLimitedAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            retryAfter,
            TestContext.CancellationToken
        );

        // Verify it's set
        RateLimitState stateBefore = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );
        Assert.IsTrue( stateBefore.IsRateLimited );

        // Wait for expiration
        await Task.Delay( 600, TestContext.CancellationToken );

        // Act
        RateLimitState stateAfter = await _tracker.GetStateAsync(
            SupportedProviders.Spotify,
            "/v1/search",
            TestContext.CancellationToken
        );

        // Assert - should be expired
        Assert.IsFalse( stateAfter.IsRateLimited );
    }
}
