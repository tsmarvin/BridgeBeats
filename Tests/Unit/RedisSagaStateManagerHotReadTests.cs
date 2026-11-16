using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Unit;

/// <summary>Guards the hot saga-read path against accidental token-adoption writes.</summary>
[TestClass]
public sealed class RedisSagaStateManagerHotReadTests {
    /// <summary>Existing token reads use hash reads only; listener-on/off operation count is stable.</summary>
    [TestMethod]
    public async Task GetAsync_WithExistingInstanceToken_DoesNotEvaluateAdoptionScriptOrWrite( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        _ = db.Setup( d => d.HashGetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)"stable-token" );
        _ = db.Setup( d => d.HashGetAllAsync(
                It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( ( RedisKey key, CommandFlags _ ) => key.ToString( ).StartsWith( "saga:" ) && !key.ToString( ).Contains( ":provider:" )
                ? [
                    new HashEntry( "lookupKey", "isrc:HOT" ),
                    new HashEntry( "lookupType", "IsrcLookup" ),
                    new HashEntry( "lookupValue", "HOT" ),
                    new HashEntry( "instanceToken", "stable-token" ) ]
                : [] );

        RedisSagaStateManager manager = new(
            redis.Object,
            Mock.Of<ILogger<RedisSagaStateManager>>( ),
            Options.Create( new QueueSettings { JobExpirationMinutes = 60 } ) );

        LookupSagaState? result = await manager.GetAsync( "hot-saga", TestContext.CancellationToken );

        Assert.IsNotNull( result );
        db.Verify( d => d.ScriptEvaluateAsync(
            It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
        db.Verify( d => d.HashSetAsync(
            It.IsAny<RedisKey>( ), It.IsAny<HashEntry[]>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>A deletion after the create script cannot be followed by an origin-only zombie write.</summary>
    [TestMethod]
    public async Task GetOrCreate_WhenExistingSagaDisappearsAfterScript_DoesNotWriteAfterDeletion( ) {
        Mock<IConnectionMultiplexer> redis = new( );
        Mock<IDatabase> db = new( );
        _ = redis.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object>( ) ) ).Returns( db.Object );
        _ = db.Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ), It.IsAny<RedisKey[]?>( ), It.IsAny<RedisValue[]?>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)0 ) );
        _ = db.Setup( d => d.HashGetAsync(
                It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisValue)"deleted-token" );
        _ = db.Setup( d => d.HashGetAllAsync(
                It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( [] );

        RedisSagaStateManager manager = new(
            redis.Object,
            Mock.Of<ILogger<RedisSagaStateManager>>( ),
            Options.Create( new QueueSettings { JobExpirationMinutes = 60 } ) );

        _ = await Assert.ThrowsExactlyAsync<UnreadableSagaStateException>( async ( ) => await manager.GetOrCreateAsync(
            "disappeared-saga",
            "isrc:DISAPPEARED",
            LookupRequestType.IsrcLookup,
            "DISAPPEARED",
            QueuePriority.Interactive,
            TestContext.CancellationToken ) );

        db.Verify( d => d.KeyExpireAsync(
            It.IsAny<RedisKey>( ), It.IsAny<TimeSpan?>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
        db.Verify( d => d.HashSetAsync(
            It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ), It.IsAny<RedisValue>( ),
            It.IsAny<When>( ), It.IsAny<CommandFlags>( ) ), Times.Never );
    }

    /// <summary>MSTest context used for cancellation-aware read verification.</summary>
    public TestContext TestContext { get; set; } = null!;
}
