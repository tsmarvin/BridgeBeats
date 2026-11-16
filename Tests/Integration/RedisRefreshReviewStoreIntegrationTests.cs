using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>Real-Redis coverage for refresh-review promotion, TTL, and revision-guarded cleanup.</summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public sealed class RedisRefreshReviewStoreIntegrationTests {
    private const string SagaId = "refresh-review-integration-saga";
    private const string SourceUri = "at://did:plc:test/link.bridgebeats.lookup/track:INTEGRATION";
    private const string SourceCid = "bafyreihash";
    private static IConnectionMultiplexer? s_redis;
    private RedisRefreshReviewStore _store = null!;

    /// <summary>MSTest cancellation token for Redis operations.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Connects to the shared Testcontainers Redis instance.</summary>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext _ ) {
        SharedTestInfrastructure.RequireRedis( );
        s_redis = await ConnectionMultiplexer.ConnectAsync( SharedTestInfrastructure.RedisConnectionString );
    }

    /// <summary>Closes the shared Redis connection.</summary>
    [ClassCleanup]
    public static async Task ClassCleanup( ) {
        if (s_redis is not null) {
            await s_redis.CloseAsync( );
            s_redis.Dispose( );
        }
    }

    /// <summary>Clears refresh-review keys and creates the store before each test.</summary>
    [TestInitialize]
    public async Task Initialize( ) {
        IDatabase db = s_redis!.GetDatabase( );
        IServer server = s_redis.GetServer( s_redis.GetEndPoints( )[0] );
        await foreach (RedisKey key in server.KeysAsync( pattern: "cache:refresh:*" )) {
            _ = await db.KeyDeleteAsync( key );
        }

        _store = new RedisRefreshReviewStore(
            s_redis,
            Options.Create( new QueueSettings { JobExpirationMinutes = 60 } ),
            Mock.Of<ILogger<RedisRefreshReviewStore>>( ) );
    }

    /// <summary>Promotion is durable and cleanup removes only the matching displayed revision.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task MarkAndDelete_RoundTripsWithRevisionCas( ) {
        RefreshReviewEntry entry = CreateEntry( );
        await _store.RegisterPendingAsync( entry, TestContext.CancellationToken );

        TimeSpan? ttl = await s_redis!.GetDatabase( ).KeyTimeToLiveAsync(
            $"cache:refresh:pending:{SourceUri}" );
        Assert.IsNotNull( ttl );
        Assert.IsGreaterThan( TimeSpan.Zero, ttl.Value );
        Assert.IsLessThanOrEqualTo( TimeSpan.FromMinutes( 60 ), ttl.Value );

        await _store.MarkUnresolvedAsync(
            entry, "No provider result.", TestContext.CancellationToken );
        Assert.HasCount( 1, await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );

        bool staleDelete = await _store.DeleteUnresolvedAsync(
            SourceUri, "stale-saga", "bafyreistale", TestContext.CancellationToken );
        Assert.IsFalse( staleDelete );
        Assert.HasCount( 1, await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );

        bool matchingDelete = await _store.DeleteUnresolvedAsync(
            SourceUri, SagaId, SourceCid, TestContext.CancellationToken );
        Assert.IsTrue( matchingDelete );
        Assert.IsEmpty( await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );
    }

    /// <summary>A successful completion removes pending context before it can become reviewable.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Complete_PendingEntry_RemovesContext( ) {
        RefreshReviewEntry entry = CreateEntry( );
        await _store.RegisterPendingAsync( entry, TestContext.CancellationToken );

        await _store.CompleteAsync( SagaId, entry.InstanceToken!, TestContext.CancellationToken );
        await _store.MarkUnresolvedAsync(
            entry, "late failure", TestContext.CancellationToken );

        Assert.IsEmpty( await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );
    }

    /// <summary>Direct quarantine preserves a shared saga owner's pending context and clears attempts.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PromoteDirect_PreservesSharedPendingOwner_AndIsIdempotent( ) {
        const string OwnerUri = SourceUri + ":owner";
        const string PromotedUri = SourceUri + ":promoted";
        const string SharedSaga = SagaId + ":shared";
        RefreshReviewEntry owner = CreateEntry( ) with {
            SourceRecordUri = OwnerUri,
            SagaId = SharedSaga,
            InstanceToken = "owner-token"
        };
        RefreshReviewEntry promoted = CreateEntry( ) with {
            SourceRecordUri = PromotedUri,
            SagaId = SharedSaga,
            InstanceToken = "owner-token"
        };

        await _store.RegisterPendingAsync( owner, TestContext.CancellationToken );
        _ = await _store.IncrementSweepAttemptAsync( PromotedUri, TestContext.CancellationToken );
        _ = await _store.IncrementSweepAttemptAsync( PromotedUri, TestContext.CancellationToken );
        await _store.PromoteDirectAsync( promoted, "No completed refresh after three sweeps.", TestContext.CancellationToken );
        await _store.PromoteDirectAsync( promoted, "No completed refresh after three sweeps.", TestContext.CancellationToken );

        RefreshReviewEntry? pendingOwner = await _store.GetPendingAsync( OwnerUri, TestContext.CancellationToken );
        Assert.IsNotNull( pendingOwner );
        Assert.AreEqual( OwnerUri, pendingOwner!.SourceRecordUri );
        Assert.HasCount( 1, await _store.GetPendingForSagaAsync( SharedSaga, TestContext.CancellationToken ) );
        Assert.AreEqual( 1, await _store.IncrementSweepAttemptAsync( PromotedUri, TestContext.CancellationToken ) );

        IReadOnlyList<RefreshReviewEntry> unresolved = await _store.GetUnresolvedAsync( TestContext.CancellationToken );
        Assert.HasCount( 1, unresolved );
        Assert.AreEqual( PromotedUri, unresolved[0].SourceRecordUri );
    }

    /// <summary>Promoting an active owner's own context retains pending correlation until completion clears both views.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PromoteDirect_SameOwnerThenComplete_ClearsPendingAndUnresolved( ) {
        RefreshReviewEntry owner = CreateEntry( ) with { SagaId = SagaId + ":owner", InstanceToken = "owner-token" };
        await _store.RegisterPendingAsync( owner, TestContext.CancellationToken );
        await _store.PromoteDirectAsync( owner, "No completed refresh after three sweeps.", TestContext.CancellationToken );
        Assert.IsNotNull( await _store.GetPendingAsync( owner.SourceRecordUri, TestContext.CancellationToken ) );
        Assert.HasCount( 1, await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );

        await _store.CompleteAsync( owner.SagaId, owner.InstanceToken!, TestContext.CancellationToken );

        Assert.IsNull( await _store.GetPendingAsync( owner.SourceRecordUri, TestContext.CancellationToken ) );
        Assert.IsEmpty( await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );
    }

    /// <summary>Sibling pending entries retain order and are promoted idempotently as separate records.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SiblingPendingEntries_PreserveBothAndPromoteIdempotently( ) {
        RefreshReviewEntry first = CreateEntry( ) with { SourceRecordUri = SourceUri + ":first" };
        RefreshReviewEntry second = CreateEntry( ) with { SourceRecordUri = SourceUri + ":second" };
        await _store.RegisterPendingAsync( first, TestContext.CancellationToken );
        await _store.RegisterPendingAsync( second, TestContext.CancellationToken );

        IReadOnlyList<RefreshReviewEntry> pending = await _store.GetPendingForSagaAsync(
            SagaId, TestContext.CancellationToken );
        CollectionAssert.AreEquivalent( new[] { first.SourceRecordUri, second.SourceRecordUri },
            pending.Select( entry => entry.SourceRecordUri ).ToArray( ) );

        await _store.MarkUnresolvedAsync( first, "No completed refresh.", TestContext.CancellationToken );
        await _store.MarkUnresolvedAsync( second, "No completed refresh.", TestContext.CancellationToken );
        await _store.MarkUnresolvedAsync( first, "No completed refresh.", TestContext.CancellationToken );
        await _store.MarkUnresolvedAsync( second, "No completed refresh.", TestContext.CancellationToken );
        IReadOnlyList<RefreshReviewEntry> unresolved = await _store.GetUnresolvedAsync( TestContext.CancellationToken );
        CollectionAssert.AreEquivalent( new[] { first.SourceRecordUri, second.SourceRecordUri },
            unresolved.Select( entry => entry.SourceRecordUri ).ToArray( ) );
    }

    /// <summary>Stale instance cleanup cannot delete a replacement pending entry for the same source.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ReplacementPendingEntry_SurvivesStaleMarkAndComplete( ) {
        DateTimeOffset older = DateTimeOffset.UtcNow.AddMinutes( -2 );
        DateTimeOffset newer = DateTimeOffset.UtcNow.AddMinutes( -1 );
        RefreshReviewEntry stale = CreateEntry( ) with { InstanceToken = "stale-x", CreatedAt = older };
        RefreshReviewEntry replacement = stale with { InstanceToken = "current-y", CreatedAt = newer };

        await _store.RegisterPendingAsync( stale, TestContext.CancellationToken );
        await _store.RegisterPendingAsync( replacement, TestContext.CancellationToken );
        await _store.RegisterPendingAsync( stale, TestContext.CancellationToken );
        Assert.AreEqual( replacement.InstanceToken,
            (await _store.GetPendingAsync( SourceUri, TestContext.CancellationToken ))!.InstanceToken,
            "Retrying an older registration must not replace the newer saga generation." );
        await _store.MarkUnresolvedAsync( stale, "stale", TestContext.CancellationToken );
        Assert.AreEqual( replacement.InstanceToken,
            (await _store.GetPendingAsync( SourceUri, TestContext.CancellationToken ))!.InstanceToken );
        Assert.IsEmpty( await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );

        await _store.RegisterPendingAsync( stale, TestContext.CancellationToken );
        await _store.PromoteDirectAsync( stale, "stale", TestContext.CancellationToken );
        await _store.RegisterPendingAsync( replacement, TestContext.CancellationToken );
        await _store.CompleteAsync( stale, TestContext.CancellationToken );
        Assert.AreEqual( replacement.InstanceToken,
            (await _store.GetPendingAsync( SourceUri, TestContext.CancellationToken ))!.InstanceToken );
        Assert.HasCount( 1, await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );
        await _store.CompleteAsync( replacement, TestContext.CancellationToken );
        Assert.IsNull( await _store.GetPendingAsync( SourceUri, TestContext.CancellationToken ) );
        Assert.IsEmpty( await _store.GetUnresolvedAsync( TestContext.CancellationToken ) );
    }

    /// <summary>Completing one saga instance cannot remove an unresolved replacement for another instance.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Complete_StalePendingInstance_PreservesDifferentUnresolvedReplacement( ) {
        DateTimeOffset olderAcrossPositiveOffset = new DateTimeOffset( 2026, 1, 1, 10, 0, 0, TimeSpan.Zero )
            .ToOffset( TimeSpan.FromHours( 14 ) );
        DateTimeOffset newerAcrossNegativeOffset = new DateTimeOffset( 2026, 1, 1, 11, 0, 0, TimeSpan.Zero )
            .ToOffset( TimeSpan.FromHours( -12 ) );
        RefreshReviewEntry pendingOwner = CreateEntry( ) with {
            SagaId = SagaId + ":owner",
            InstanceToken = "owner-token",
            CreatedAt = olderAcrossPositiveOffset
        };
        RefreshReviewEntry unresolvedReplacement = pendingOwner with {
            SagaId = SagaId + ":replacement",
            InstanceToken = "replacement-token",
            CreatedAt = newerAcrossNegativeOffset
        };

        await _store.RegisterPendingAsync( pendingOwner, TestContext.CancellationToken );
        await _store.PromoteDirectAsync(
            unresolvedReplacement,
            "No completed refresh after three sweeps.",
            TestContext.CancellationToken );
        await _store.CompleteAsync( pendingOwner, TestContext.CancellationToken );

        Assert.IsNull( await _store.GetPendingAsync( SourceUri, TestContext.CancellationToken ) );
        IReadOnlyList<RefreshReviewEntry> unresolved = await _store.GetUnresolvedAsync( TestContext.CancellationToken );
        Assert.HasCount( 1, unresolved );
        Assert.AreEqual( unresolvedReplacement.SagaId, unresolved[0].SagaId );
        Assert.AreEqual( unresolvedReplacement.InstanceToken, unresolved[0].InstanceToken );
        IDatabase db = s_redis!.GetDatabase( );
        Assert.AreEqual( SourceUri, (await db.HashGetAsync( "cache:refresh:unresolved:sagas", unresolvedReplacement.SagaId )).ToString( ) );
        Assert.IsFalse( await db.HashExistsAsync( "cache:refresh:pending:sagas", pendingOwner.SagaId ) );
        Assert.IsFalse( await db.SetContainsAsync( $"cache:refresh:pending:saga:{pendingOwner.SagaId}", SourceUri ) );
        Assert.IsFalse( await db.KeyExistsAsync( $"cache:refresh:pending:{SourceUri}" ) );
    }

    /// <summary>Marking an older pending saga cannot overwrite a newer unresolved replacement.</summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Mark_StalePendingInstance_PreservesDifferentUnresolvedReplacement( ) {
        DateTimeOffset olderAcrossPositiveOffset = new DateTimeOffset( 2026, 1, 1, 10, 0, 0, TimeSpan.Zero )
            .ToOffset( TimeSpan.FromHours( 14 ) );
        DateTimeOffset newerAcrossNegativeOffset = new DateTimeOffset( 2026, 1, 1, 11, 0, 0, TimeSpan.Zero )
            .ToOffset( TimeSpan.FromHours( -12 ) );
        RefreshReviewEntry pendingOwner = CreateEntry( ) with {
            SagaId = SagaId + ":owner",
            InstanceToken = "owner-token",
            CreatedAt = olderAcrossPositiveOffset
        };
        RefreshReviewEntry unresolvedReplacement = pendingOwner with {
            SagaId = SagaId + ":replacement",
            InstanceToken = "replacement-token",
            CreatedAt = newerAcrossNegativeOffset
        };

        await _store.RegisterPendingAsync( pendingOwner, TestContext.CancellationToken );
        await _store.PromoteDirectAsync(
            unresolvedReplacement,
            "No completed refresh after three sweeps.",
            TestContext.CancellationToken );
        await _store.MarkUnresolvedAsync( pendingOwner, "Older terminal failure.", TestContext.CancellationToken );

        Assert.IsNull( await _store.GetPendingAsync( SourceUri, TestContext.CancellationToken ) );
        IReadOnlyList<RefreshReviewEntry> unresolved = await _store.GetUnresolvedAsync( TestContext.CancellationToken );
        Assert.HasCount( 1, unresolved );
        Assert.AreEqual( unresolvedReplacement.SagaId, unresolved[0].SagaId );
        Assert.AreEqual( unresolvedReplacement.InstanceToken, unresolved[0].InstanceToken );
        IDatabase db = s_redis!.GetDatabase( );
        Assert.AreEqual( SourceUri, (await db.HashGetAsync( "cache:refresh:unresolved:sagas", unresolvedReplacement.SagaId )).ToString( ) );
        Assert.IsFalse( await db.HashExistsAsync( "cache:refresh:pending:sagas", pendingOwner.SagaId ) );
        Assert.IsFalse( await db.SetContainsAsync( $"cache:refresh:pending:saga:{pendingOwner.SagaId}", SourceUri ) );
        Assert.IsFalse( await db.KeyExistsAsync( $"cache:refresh:pending:{SourceUri}" ) );
    }

    private static RefreshReviewEntry CreateEntry( ) => new( ) {
        SourceRecordUri = SourceUri,
        SourceRecordCid = SourceCid,
        SagaId = SagaId,
        InstanceToken = "integration-token",
        LookupType = LookupRequestType.IsrcLookup,
        LookupValue = "INTEGRATION"
    };
}
