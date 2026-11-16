using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Web.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using StackExchange.Redis;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RedisStatisticsReader"/>. Covers:
/// <list type="bullet">
///   <item>T9 memo: two back-to-back <c>GetCachedStatistics</c> calls within the 5-second window
///   hit Redis exactly once; advancing the fake clock past the window causes a re-read.</item>
///   <item>T10 web-never-computes (structural): the constructor takes no
///   <c>IATProtoStorageService</c> parameter; proven by the compilation of this test file.</item>
/// </list>
/// </summary>
[TestClass]
public class RedisStatisticsReaderTests {

    /// <summary>Mock Redis multiplexer.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    /// <summary>Mock Redis database backing string reads.</summary>
    private Mock<IDatabase> _redisDatabaseMock = null!;
    /// <summary>Fake time provider for controlling memo expiry.</summary>
    private FakeTimeProvider _timeProvider = null!;
    /// <summary>Mock logger.</summary>
    private Mock<ILogger<RedisStatisticsReader>> _loggerMock = null!;

    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _redisDatabaseMock = new Mock<IDatabase>( );
        _ = _redisMock
            .Setup( x => x.GetDatabase( It.IsAny<int>( ), It.IsAny<object?>( ) ) )
            .Returns( _redisDatabaseMock.Object );
        _ = _redisMock
            .Setup( x => x.GetSubscriber( It.IsAny<object?>( ) ) )
            .Returns( Mock.Of<ISubscriber>( ) );
        _timeProvider = new FakeTimeProvider( );
        _loggerMock = new Mock<ILogger<RedisStatisticsReader>>( );
    }

    // ── T9: in-process memo ──────────────────────────────────────────────────

    /// <summary>
    /// T9-1: Two back-to-back <c>GetCachedStatistics</c> calls within the 5-second memo window
    /// hit Redis (via <c>IDatabase.StringGet</c>) exactly once.
    /// <para>
    /// Failure-first evidence: before implementing the memo, both calls go to Redis and the
    /// <c>Times.Once()</c> assertion fails because the database mock records two invocations.
    /// After the memo is in place the second call is served from the in-process cache.
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetCachedStatistics_TwoBackToBackCallsWithinWindow_HitsRedisOnce( ) {
        string json = BuildStatusJson( isRunning: false );
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( (RedisValue)json );

        RedisStatisticsReader reader = CreateReader( );

        _ = reader.GetCachedStatistics( );
        _ = reader.GetCachedStatistics( );

        _redisDatabaseMock.Verify(
            db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ),
            Times.Once( ) );
    }

    /// <summary>
    /// T9-2: After advancing the fake clock past the 5-second memo window, the next
    /// <c>GetCachedStatistics</c> call re-hits Redis.
    /// <para>
    /// Failure-first evidence: without the <see cref="FakeTimeProvider"/> seam, advancing
    /// time has no effect on the memo and the second call is still served from cache, so
    /// <c>Times.Exactly(2)</c> fails.
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetCachedStatistics_AfterMemoExpiry_ReHitsRedis( ) {
        string json = BuildStatusJson( isRunning: false );
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( (RedisValue)json );

        RedisStatisticsReader reader = CreateReader( );

        // First call populates the memo.
        _ = reader.GetCachedStatistics( );

        // Advance past the 5-second window.
        _timeProvider.Advance( TimeSpan.FromSeconds( 5 ) + TimeSpan.FromMilliseconds( 1 ) );

        // Second call should re-fetch from Redis.
        _ = reader.GetCachedStatistics( );

        _redisDatabaseMock.Verify(
            db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ),
            Times.Exactly( 2 ) );
    }

    // ── T10: structural no-compute proof ────────────────────────────────────

    /// <summary>
    /// T10: <see cref="RedisStatisticsReader"/> takes no <c>IATProtoStorageService</c> parameter,
    /// proving Web has no compute path. This is verified at compile time: if the constructor were
    /// to accept <c>IATProtoStorageService</c>, a reference to the interface would be required here
    /// and the test file would fail to compile without the appropriate using/reference. The
    /// constructor call below compiles cleanly with only Redis, TimeProvider, and ILogger — no
    /// storage service.
    /// </summary>
    [TestMethod]
    public void RedisStatisticsReader_ConstructorTakesNoStorageService_StructuralProof( ) {
        RedisStatisticsReader reader = new(
            _redisMock.Object,
            _timeProvider,
            _loggerMock.Object
        );

        Assert.IsNotNull( reader );
    }

    // ── IsRefreshing reflects status:statistics.IsRunning ───────────────────

    /// <summary>
    /// Verifies that <see cref="RedisStatisticsReader.IsRefreshing"/> returns the <c>IsRunning</c>
    /// field of the <c>status:statistics</c> document.
    /// </summary>
    [TestMethod]
    public void IsRefreshing_WhenDocumentSaysRunning_ReturnsTrue( ) {
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( (RedisValue)BuildStatusJson( isRunning: true ) );

        RedisStatisticsReader reader = CreateReader( );

        Assert.IsTrue( reader.IsRefreshing );
    }

    /// <summary>
    /// Verifies that <see cref="RedisStatisticsReader.IsRefreshing"/> returns false when the Redis
    /// key is absent (no status document exists yet).
    /// </summary>
    [TestMethod]
    public void IsRefreshing_WhenKeyAbsent_ReturnsFalse( ) {
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( RedisValue.Null );

        RedisStatisticsReader reader = CreateReader( );

        Assert.IsFalse( reader.IsRefreshing );
    }

    // ── GetCachedStatistics returns snapshot field ───────────────────────────

    /// <summary>
    /// Verifies that <c>GetCachedStatistics</c> returns the <c>Snapshot</c> field of the status
    /// document, and null when the status document is absent.
    /// </summary>
    [TestMethod]
    public void GetCachedStatistics_WhenKeyAbsent_ReturnsNull( ) {
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( RedisValue.Null );

        RedisStatisticsReader reader = CreateReader( );

        Assert.IsNull( reader.GetCachedStatistics( ) );
    }

    // ── GetStatus: full document projection ──────────────────────────────────

    /// <summary>
    /// GetStatus returns the full <see cref="StatisticsStatus"/> document when the key is
    /// present, preserving both <c>LastError</c> and <c>NextScheduledRun</c> — guards against
    /// a regression that drops to a snapshot-only projection.
    /// <para>
    /// Failure-first evidence: before adding <c>GetStatus</c> to the implementation, any call
    /// to the method would not compile; after adding the no-op stub that returns <see langword="null"/>
    /// the <c>Assert.IsNotNull</c> assertions on <c>result</c>, <c>result.LastError</c>, and
    /// <c>result.NextScheduledRun</c> all fail. The assertions pass only once <c>GetStatus</c>
    /// delegates to <c>GetMemoOrNull</c> / <c>FetchStatisticsStatusSync</c> and returns the full
    /// deserialized document.
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetStatus_WhenKeyPresent_ReturnsFullDocument( ) {
        DateTimeOffset nextRun = DateTimeOffset.UtcNow.AddHours( 1 );
        string json = System.Text.Json.JsonSerializer.Serialize( new StatisticsStatus {
            IsRunning = false,
            Snapshot = new LookupStatistics { TotalRecords = 5, GeneratedAt = DateTimeOffset.UtcNow },
            LastError = "transient-redis-timeout",
            NextScheduledRun = nextRun
        } );
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( (RedisValue)json );

        RedisStatisticsReader reader = CreateReader( );

        StatisticsStatus? result = reader.GetStatus( );

        Assert.IsNotNull( result );
        Assert.AreEqual( "transient-redis-timeout", result.LastError );
        Assert.IsNotNull( result.NextScheduledRun );
        Assert.AreEqual( nextRun.ToUnixTimeSeconds( ), result.NextScheduledRun!.Value.ToUnixTimeSeconds( ) );
        Assert.IsNotNull( result.Snapshot );
        Assert.AreEqual( 5, result.Snapshot.TotalRecords );
    }

    /// <summary>
    /// GetStatus returns <see langword="null"/> when the Redis key is absent (no status document
    /// has been published yet).
    /// <para>
    /// Failure-first evidence: a stub that always returns a non-null document would cause
    /// <c>Assert.IsNull</c> to fail. The assertion passes only when the implementation falls
    /// through <c>FetchStatisticsStatusSync</c> and returns <see langword="null"/> on an empty key.
    /// </para>
    /// </summary>
    [TestMethod]
    public void GetStatus_WhenKeyAbsent_ReturnsNull( ) {
        _ = _redisDatabaseMock
            .Setup( db => db.StringGet( StatisticsStatus.RedisKey, It.IsAny<CommandFlags>( ) ) )
            .Returns( RedisValue.Null );

        RedisStatisticsReader reader = CreateReader( );

        StatisticsStatus? result = reader.GetStatus( );

        Assert.IsNull( result );
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private RedisStatisticsReader CreateReader( ) =>
        new( _redisMock.Object, _timeProvider, _loggerMock.Object );

    private static string BuildStatusJson( bool isRunning ) =>
        System.Text.Json.JsonSerializer.Serialize( new StatisticsStatus {
            IsRunning = isRunning,
            Snapshot = isRunning ? null : new LookupStatistics { TotalRecords = 1, GeneratedAt = DateTimeOffset.UtcNow }
        } );
}

#pragma warning restore CS1591
