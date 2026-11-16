using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>Tests pending-to-unresolved promotion in the Redis refresh review store.</summary>
[TestClass]
public class RedisRefreshReviewStoreTests {
    private static readonly JsonSerializerOptions s_jsonOptions = new( JsonSerializerDefaults.Web );

    /// <summary>MSTest cancellation token for asynchronous store operations.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Sweep attempts increment atomically and are deleted by the success reset.</summary>
    [TestMethod]
    public async Task SweepAttempts_IncrementThenClear( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> database = new( );
        _ = redis.Setup( c => c.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( database.Object );
        _ = database.SetupSequence( db => db.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]>( ), It.IsAny<RedisValue[]>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)1 ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)2 ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)3 ) );
        _ = database.Setup( db => db.KeyDeleteAsync( "cache:refresh:attempts:saga", It.IsAny<CommandFlags>( ) ) ).ReturnsAsync( true );
        RedisRefreshReviewStore store = new(redis.Object, Options.Create(new QueueSettings { JobExpirationMinutes = 60 }), Mock.Of<ILogger<RedisRefreshReviewStore>>());
        Assert.AreEqual( 1, await store.IncrementSweepAttemptAsync( "saga", TestContext.CancellationToken ) );
        Assert.AreEqual( 2, await store.IncrementSweepAttemptAsync( "saga", TestContext.CancellationToken ) );
        Assert.AreEqual( 3, await store.IncrementSweepAttemptAsync( "saga", TestContext.CancellationToken ) );
        await store.ClearSweepAttemptsAsync( "saga", TestContext.CancellationToken );
        database.Verify( db => db.KeyDeleteAsync( "cache:refresh:attempts:saga", It.IsAny<CommandFlags>( ) ), Times.Once );
    }

    /// <summary>The review list deserializes entries and returns newest failures first.</summary>
    [TestMethod]
    public async Task GetUnresolved_ReturnsNewestFirst( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> database = new( );
        _ = redis.Setup( connection => connection.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( database.Object );

        RefreshReviewEntry older = CreateEntry( ) with {
            SourceRecordUri = "at://did:plc:test/link.bridgebeats.lookup/track:older",
            FailedAt = DateTimeOffset.UtcNow.AddMinutes( -5 )
        };
        RefreshReviewEntry newer = CreateEntry( ) with { FailedAt = DateTimeOffset.UtcNow };
        _ = database.Setup( db => db.HashGetAllAsync(
                "cache:refresh:unresolved", It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [
                new HashEntry( older.SourceRecordUri, JsonSerializer.Serialize( older, s_jsonOptions ) ),
                new HashEntry( newer.SourceRecordUri, JsonSerializer.Serialize( newer, s_jsonOptions ) )
            ] );

        RedisRefreshReviewStore store = new(
            redis.Object,
            Options.Create( new QueueSettings( ) ),
            Mock.Of<ILogger<RedisRefreshReviewStore>>( ) );
        IReadOnlyList<RefreshReviewEntry> entries = await store.GetUnresolvedAsync( TestContext.CancellationToken );

        Assert.HasCount( 2, entries );
        Assert.AreEqual( newer.SourceRecordUri, entries[0].SourceRecordUri );
    }

    /// <summary>A corrupt hash value is quarantined without hiding healthy review entries.</summary>
    [TestMethod]
    public async Task GetUnresolved_CorruptEntry_QuarantinesAndContinues( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> database = new( );
        _ = redis.Setup( connection => connection.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( database.Object );
        RefreshReviewEntry healthy = CreateEntry( ) with { FailedAt = DateTimeOffset.UtcNow };
        _ = database.Setup( db => db.HashGetAllAsync(
                "cache:refresh:unresolved", It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [
                new HashEntry( "corrupt-uri", "{not-json" ),
                new HashEntry( healthy.SourceRecordUri, JsonSerializer.Serialize( healthy, s_jsonOptions ) )
            ] );

        RedisRefreshReviewStore store = new(
            redis.Object,
            Options.Create( new QueueSettings( ) ),
            Mock.Of<ILogger<RedisRefreshReviewStore>>( ) );
        IReadOnlyList<RefreshReviewEntry> entries = await store.GetUnresolvedAsync( TestContext.CancellationToken );

        Assert.HasCount( 1, entries );
        Assert.AreEqual( healthy.SourceRecordUri, entries[0].SourceRecordUri );
        database.Verify( db => db.HashSetAsync(
            "cache:refresh:unresolved:corrupt", "corrupt-uri", "{not-json",
            It.IsAny<When>( ), It.IsAny<CommandFlags>( ) ), Times.Once );
        database.Verify( db => db.HashDeleteAsync(
            "cache:refresh:unresolved", "corrupt-uri", It.IsAny<CommandFlags>( ) ), Times.Once );
    }

    /// <summary>Explicit cleanup passes the displayed saga and CID through the atomic Redis CAS.</summary>
    [TestMethod]
    public async Task DeleteUnresolved_MatchingRevision_ReturnsTrue( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> database = new( );
        _ = redis.Setup( connection => connection.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( database.Object );
        _ = database.Setup( db => db.ScriptEvaluateAsync(
                It.IsAny<string>( ),
                It.Is<RedisKey[]?>( keys => keys != null && keys.Length == 2 ),
                It.Is<RedisValue[]?>( values => values != null
                    && values.Length == 3
                    && values[0] == CreateEntry( ).SourceRecordUri
                    && values[1] == CreateEntry( ).SagaId
                    && values[2] == CreateEntry( ).SourceRecordCid ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)1 ) );

        RedisRefreshReviewStore store = new(
            redis.Object,
            Options.Create( new QueueSettings( ) ),
            Mock.Of<ILogger<RedisRefreshReviewStore>>( ) );

        bool deleted = await store.DeleteUnresolvedAsync(
            CreateEntry( ).SourceRecordUri,
            CreateEntry( ).SagaId,
            CreateEntry( ).SourceRecordCid!,
            TestContext.CancellationToken );

        Assert.IsTrue( deleted );
    }

    /// <summary>A Redis CAS mismatch preserves the replacement review entry.</summary>
    [TestMethod]
    public async Task DeleteUnresolved_RevisionMismatch_ReturnsFalse( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> database = new( );
        _ = redis.Setup( connection => connection.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( database.Object );
        _ = database.Setup( db => db.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)0 ) );

        RedisRefreshReviewStore store = new(
            redis.Object,
            Options.Create( new QueueSettings( ) ),
            Mock.Of<ILogger<RedisRefreshReviewStore>>( ) );

        bool deleted = await store.DeleteUnresolvedAsync(
            CreateEntry( ).SourceRecordUri,
            "stale-saga",
            "bafyreistale",
            TestContext.CancellationToken );

        Assert.IsFalse( deleted );
    }

    /// <summary>
    /// Entry-scoped review mutations use one Redis script each, rather than a client-side read,
    /// compare, and write sequence that could interleave with a replacement refresh.
    /// </summary>
    [TestMethod]
    public async Task EntryScopedReviewMutations_UseAtomicScriptsWithoutClientSideReadModifyWrite( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> database = new( );
        _ = redis.Setup( connection => connection.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) )
            .Returns( database.Object );
        _ = database.Setup( db => db.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)0 ) );

        RefreshReviewEntry entry = CreateEntry( ) with { InstanceToken = "instance-x" };
        RedisRefreshReviewStore store = new(
            redis.Object,
            Options.Create( new QueueSettings { JobExpirationMinutes = 60 } ),
            Mock.Of<ILogger<RedisRefreshReviewStore>>( ) );

        await store.MarkUnresolvedAsync( entry, "No provider result.", TestContext.CancellationToken );
        await store.CompleteAsync( entry, TestContext.CancellationToken );

        database.Verify( db => db.ScriptEvaluateAsync(
            It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ), It.IsAny<CommandFlags>( ) ),
            Times.Exactly( 2 ) );
        database.Verify( db => db.StringGetAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
        database.Verify( db => db.HashGetAsync( It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
        database.Verify( db => db.HashDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    private static RefreshReviewEntry CreateEntry( ) => new( ) {
        SourceRecordUri = "at://did:plc:test/link.bridgebeats.lookup/track:USRC12345678",
        SourceRecordCid = "bafyreihash",
        SagaId = "refresh-saga",
        LookupType = LookupRequestType.IsrcLookup,
        LookupValue = "USRC12345678"
    };
}
