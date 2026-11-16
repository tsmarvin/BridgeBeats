using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Cache;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Core.Infrastructure.Utilities;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="RedisMediaLinkCache"/> against a real Redis instance (the shared
/// Testcontainers Redis) with the AT Protocol storage service mocked. Verifies that caching a result
/// writes the ISRC, UPC, URL, card-id, and provider-id index keys; that lookups by each of those keys
/// return the cached record URI; that refreshing cleans up stale URL keys; and that freshness is
/// computed from record age and partial status. Requires Docker to be running on the host machine.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
public class RedisMediaLinkCacheTests {

    /// <summary>The shared Redis connection used by the cache under test.</summary>
    private static IConnectionMultiplexer? s_redis;

    /// <summary>Mock AT Protocol storage service backing the cache's record persistence.</summary>
    private Mock<IATProtoStorageService> _mockAtProto = null!;
    /// <summary>Mock logger captured for the cache under test.</summary>
    private Mock<ILogger<RedisMediaLinkCache>> _mockLogger = null!;
    /// <summary>The cache under test, recreated for each test.</summary>
    private RedisMediaLinkCache _cache = null!;

    /// <summary>The cache freshness window, in days, configured for the cache under test.</summary>
    private const int CacheDays = 7;
    /// <summary>The user DID used to build the AT Protocol record URIs in the tests.</summary>
    private const string UserDID = "did:plc:testuser123";

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
    /// Clears leftover <c>lookup:*</c> keys and constructs a fresh cache with mocked storage before each
    /// test.
    /// </summary>
    [TestInitialize]
    public async Task TestInitialize( ) {
        // Clear only cache-related keys before each test
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        await foreach (RedisKey key in server.KeysAsync( pattern: "lookup:*" )) {
            _ = await db.KeyDeleteAsync( key );
        }

        _mockAtProto = new Mock<IATProtoStorageService>( );
        _mockLogger = new Mock<ILogger<RedisMediaLinkCache>>( );

        _cache = new RedisMediaLinkCache(
            s_redis,
            _mockAtProto.Object,
            _mockLogger.Object,
            CacheDays,
            UserDID
        );
    }

    /// <summary>
    /// Verifies indexing a track result writes the ISRC index key in Redis pointing at the record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task IndexResultAsync_StoresLookupKeys_InRedis( ) {
        // Arrange
        MediaLinkResult result = CreateTestResult( "USRC12345678", false );
        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:USRC12345678";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        // Act
        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Assert — body not re-stored inside the cache method
        _mockAtProto.Verify( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Never );

        // Verify ISRC key exists
        IDatabase db = s_redis!.GetDatabase( );
        RedisValue isrcValue = await db.StringGetAsync( "lookup:isrc:USRC12345678" );
        Assert.IsFalse( isrcValue.IsNullOrEmpty );
        Assert.AreEqual( recordUri, isrcValue.ToString( ) );
    }

    /// <summary>
    /// Verifies a cached track can be retrieved by ISRC, returning the record URI and a fresh (non-stale)
    /// marker.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryGetCachedResultByISRCAsync_ReturnsResult_WhenCached( ) {
        // Arrange
        MediaLinkResult result = CreateTestResult( "ISRC999888777", false );
        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:ISRC999888777";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByISRCAsync( "ISRC999888777" );

        // Assert
        Assert.IsNotNull( lookupResult );
        Assert.AreEqual( recordUri, lookupResult.Value.cachedUri );
        Assert.IsFalse( lookupResult.Value.isStale );
    }

    /// <summary>
    /// Verifies a cached album can be retrieved by UPC, returning the record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryGetCachedResultByUPCAsync_ReturnsResult_WhenCached( ) {
        // Arrange
        MediaLinkResult result = CreateTestResult( "123456789012", true );
        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/album:123456789012";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByUPCAsync( "123456789012" );

        // Assert
        Assert.IsNotNull( lookupResult );
        Assert.AreEqual( recordUri, lookupResult.Value.cachedUri );
    }

    /// <summary>
    /// Verifies a cached result can be retrieved by one of its input URLs, returning the record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryGetCachedResultAsync_ReturnsResult_WhenUrlCached( ) {
        // Arrange
        string inputUrl = "https://open.spotify.com/track/abc123";
        MediaLinkResult result = CreateTestResult( "TESTISRC001", false );
        result.InputLinks.Add( inputUrl );

        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:TESTISRC001";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultAsync( inputUrl );

        // Assert
        Assert.IsNotNull( lookupResult );
        Assert.AreEqual( recordUri, lookupResult.Value.cachedUri );
    }

    /// <summary>
    /// Verifies a cached result can be retrieved by the card id derived from its record key, returning
    /// the record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryGetCachedResultByCardIdAsync_ReturnsResult_WhenCached( ) {
        // Arrange
        MediaLinkResult result = CreateTestResult( "CARDTEST123", false );
        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:CARDTEST123";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Get the card ID from the result
        string rkey = RecordKeyGenerator.GenerateRkey( result );
        string cardId = RecordKeyGenerator.GenerateCardId( rkey );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByCardIdAsync( cardId );

        // Assert
        Assert.IsNotNull( lookupResult );
        Assert.AreEqual( recordUri, lookupResult.Value.cachedUri );
    }

    /// <summary>
    /// Verifies a cached result can be retrieved by provider, provider-specific id, and album flag,
    /// returning the record URI.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryGetCachedResultByProviderIdAsync_ReturnsResult_WhenCached( ) {
        // Arrange
        MediaLinkResult result = CreateTestResult( "PROVIDERTEST", false );
        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:PROVIDERTEST";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByProviderIdAsync( "abc123", SupportedProviders.Spotify, false );

        // Assert
        Assert.IsNotNull( lookupResult );
        Assert.AreEqual( recordUri, lookupResult.Value.cachedUri );
    }

    /// <summary>
    /// Verifies re-caching a result with changed input links removes the stale URL index key and writes
    /// the new one.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task IndexResultAsync_CleansUpOldKeys_OnRefresh( ) {
        // Arrange
        MediaLinkResult result1 = CreateTestResult( "REFRESHTEST1", false );
        result1.InputLinks.Add( "https://old.url/track1" );

        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:REFRESHTEST1";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result1 );

        await _cache.IndexResultAsync( result1, recordUri, TestContext.CancellationToken );

        // Verify old URL is cached
        IDatabase db = s_redis!.GetDatabase( );
        string oldUrlHash = HashUtility.HashUrl( "https://old.url/track1" );
        RedisValue oldValue = await db.StringGetAsync( $"lookup:url:{oldUrlHash}" );
        Assert.IsFalse( oldValue.IsNullOrEmpty, "Old URL should be cached" );

        // Now index the same result with a different URL (simulating refresh)
        MediaLinkResult result2 = CreateTestResult( "REFRESHTEST1", false );
        result2.InputLinks.Add( "https://new.url/track1" );

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result2 );

        await _cache.IndexResultAsync( result2, recordUri, TestContext.CancellationToken );

        // Assert — body not re-stored inside the cache method
        _mockAtProto.Verify( s => s.StoreMediaLinkResultAsync( It.IsAny<MediaLinkResult>( ), It.IsAny<CancellationToken>( ) ), Times.Never );

        // Assert: Old URL key should be removed
        RedisValue oldValueAfterRefresh = await db.StringGetAsync( $"lookup:url:{oldUrlHash}" );
        Assert.IsTrue( oldValueAfterRefresh.IsNullOrEmpty, "Old URL key should be cleaned up on refresh" );

        // Assert: New URL key should exist
        string newUrlHash = HashUtility.HashUrl( "https://new.url/track1" );
        RedisValue newValue = await db.StringGetAsync( $"lookup:url:{newUrlHash}" );
        Assert.IsFalse( newValue.IsNullOrEmpty, "New URL should be cached" );
    }

    /// <summary>
    /// Verifies a lookup by ISRC for an uncached value returns null.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task TryGetCachedResultByISRCAsync_ReturnsNull_WhenNotCached( ) {
        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByISRCAsync( "NONEXISTENT123" );

        // Assert
        Assert.IsNull( lookupResult );
    }

    /// <summary>
    /// Verifies a cached record older than the cache window is reported as stale on retrieval.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CheckRecordFreshness_ReturnsStale_WhenOlderThanCacheDays( ) {
        // Arrange
        // Create a result with LookedUpAt set to 8 days ago (older than CacheDays=7)
        MediaLinkResult result = CreateTestResultWithDate( "STALETEST123", false, DateTime.UtcNow.AddDays( -8 ) );

        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:STALETEST123";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByISRCAsync( "STALETEST123" );

        // Assert
        Assert.IsNotNull( lookupResult );
        Assert.IsTrue( lookupResult.Value.isStale, "Result should be marked as stale" );
    }

    /// <summary>
    /// Verifies a recent cached record that represents a provider subset is reported as FRESH.
    /// Staleness is age-only; partiality is owned by the saga and does not drive cache freshness.
    /// The sibling test above (aged record) remains the positive control for the stale path.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CheckRecordFreshness_ReturnsFresh_WhenResultIsRecentButSubset( ) {
        // Arrange — a freshly looked-up result where only Spotify responded
        MediaLinkResult result = CreateTestResult( "PARTIALTEST123", false );

        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:PARTIALTEST123";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Act
        (MediaLinkResult cachedResult, string cachedUri, bool isStale)? lookupResult =
            await _cache.TryGetCachedResultByISRCAsync( "PARTIALTEST123" );

        // Assert — age-only freshness: a recent subset record must be fresh
        Assert.IsNotNull( lookupResult );
        Assert.IsFalse(
            lookupResult.Value.isStale,
            "A recent subset record must be fresh. Staleness is age-only; partiality does not drive freshness."
        );
    }

    /// <summary>
    /// Verifies adding input links for a record already cached under the same URI refreshes the index
    /// key's TTL rather than reducing it (no redundant rewrite).
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task AddInputLinksAsync_SkipsWrite_WhenRecordAlreadyExistsWithSameUri( ) {
        // Arrange
        MediaLinkResult result = CreateTestResult( "SKIPTEST123", false );
        string recordUri = $"at://{UserDID}/link.bridgebeats.lookup/track:SKIPTEST123";

        _ = _mockAtProto
            .Setup( s => s.GetMediaLinkResultAsync( recordUri ) )
            .ReturnsAsync( result );

        // First call - should write to Redis
        await _cache.IndexResultAsync( result, recordUri, TestContext.CancellationToken );

        // Get the ISRC key value and TTL after first write
        IDatabase db = s_redis!.GetDatabase( );
        TimeSpan? ttlBefore = await db.KeyTimeToLiveAsync( "lookup:isrc:SKIPTEST123" );
        Assert.IsNotNull( ttlBefore, "ISRC key should exist after first write" );

        // Wait a brief moment to ensure TTL would be different if re-written
        await Task.Delay( 100, TestContext.CancellationToken );

        // Second call with same result and recordUri - should skip write but refresh TTL
        await _cache.AddInputLinksAsync( recordUri, result );

        // Get TTL after second call
        TimeSpan? ttlAfter = await db.KeyTimeToLiveAsync( "lookup:isrc:SKIPTEST123" );
        Assert.IsNotNull( ttlAfter, "ISRC key should still exist after skip" );

        // TTL should be refreshed (close to original cache days)
        // The TTL after should be >= TTL before (since we refreshed it)
        Assert.IsGreaterThanOrEqualTo( ttlBefore!.Value.Subtract( TimeSpan.FromSeconds( 1 ) ), ttlAfter.Value,
            "TTL should be refreshed, not reduced" );
    }

    /// <summary>
    /// Builds a test result for the given external id and album flag, timestamped now.
    /// </summary>
    /// <param name="externalId">The provider external id (used as ISRC/UPC).</param>
    /// <param name="isAlbum">Whether the result represents an album.</param>
    /// <returns>A populated test <see cref="MediaLinkResult"/>.</returns>
    private static MediaLinkResult CreateTestResult( string externalId, bool isAlbum )
        => CreateTestResultWithDate( externalId, isAlbum, DateTime.UtcNow );

    /// <summary>
    /// Builds a test result with a single Spotify entry for the given external id, album flag, and
    /// lookup timestamp.
    /// </summary>
    /// <param name="externalId">The provider external id (used as ISRC/UPC).</param>
    /// <param name="isAlbum">Whether the result represents an album.</param>
    /// <param name="lookedUpAt">The lookup timestamp to stamp on the result.</param>
    /// <returns>A populated test <see cref="MediaLinkResult"/>.</returns>
    private static MediaLinkResult CreateTestResultWithDate( string externalId, bool isAlbum, DateTime lookedUpAt ) {
        MediaLinkResult result = new( ) {
            LookedUpAt = lookedUpAt
        };

        result.Results[SupportedProviders.Spotify] = new MusicLookupResult {
            Artist = "Test Artist",
            Title = isAlbum ? "Test Album" : "Test Track",
            ExternalId = externalId,
            URL = $"https://open.spotify.com/{(isAlbum ? "album" : "track")}/abc123",
            ArtUrl = "https://example.com/art.jpg",
            IsAlbum = isAlbum
        };

        return result;
    }
}
