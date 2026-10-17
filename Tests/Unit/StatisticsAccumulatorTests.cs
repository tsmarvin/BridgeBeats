using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Domain.Services;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="StatisticsAccumulator"/>, which folds a stream of <see cref="MediaLinkResult"/>
/// records into a <see cref="LookupStatistics"/> snapshot. The projection logic must match
/// <c>StatisticsService.ComputeStatisticsAsync</c> exactly.
/// </summary>
[TestClass]
public class StatisticsAccumulatorTests {

    /// <summary>MSTest-injected context for cancellation token propagation.</summary>
    public TestContext TestContext { get; set; } = null!;

    #region T2 — Accumulator characterization (golden-master + cross-check)

    /// <summary>
    /// T2a: An empty accumulator produces a zero-count snapshot with null earliest/latest and an
    /// empty recent-entries list.
    /// </summary>
    [TestMethod]
    public void Build_WithNoRecords_ReturnsZeroSnapshot( ) {
        StatisticsAccumulator accumulator = new( );

        LookupStatistics snapshot = accumulator.Build( );

        Assert.AreEqual( 0, snapshot.TotalRecords );
        Assert.AreEqual( 0, snapshot.AlbumCount );
        Assert.AreEqual( 0, snapshot.TrackCount );
        Assert.IsNull( snapshot.EarliestLookup );
        Assert.IsNull( snapshot.LatestLookup );
        Assert.IsNotNull( snapshot.RecentEntries );
        Assert.IsEmpty( snapshot.RecentEntries );
        Assert.IsNull( snapshot.CacheBootstrapStatus, "CacheBootstrapStatus must be null; Web layer overlays at read time" );
    }

    /// <summary>
    /// T2b: A single track record is counted correctly: TotalRecords=1, TrackCount=1, AlbumCount=0,
    /// one provider, EarliestLookup==LatestLookup, and one recent entry.
    /// </summary>
    [TestMethod]
    public void Build_WithOneTrack_CountsCorrectly( ) {
        DateTime lookedUpAt = new( 2024, 3, 15, 10, 0, 0, DateTimeKind.Utc );
        MediaLinkResult result = BuildResult( isAlbum: false, lookedUpAt: lookedUpAt,
            (SupportedProviders.Spotify, "T1") );

        StatisticsAccumulator accumulator = new( );
        accumulator.Add( "at://did:plc:x/app.bsky.feed.post/track1", result );

        LookupStatistics snapshot = accumulator.Build( );

        Assert.AreEqual( 1, snapshot.TotalRecords );
        Assert.AreEqual( 0, snapshot.AlbumCount );
        Assert.AreEqual( 1, snapshot.TrackCount );
        Assert.AreEqual( new DateTimeOffset( lookedUpAt, TimeSpan.Zero ), snapshot.EarliestLookup );
        Assert.AreEqual( new DateTimeOffset( lookedUpAt, TimeSpan.Zero ), snapshot.LatestLookup );
        Assert.HasCount( 1, snapshot.RecentEntries );
        Assert.IsTrue( snapshot.ProviderCounts.ContainsKey( "Spotify" ) );
        Assert.AreEqual( 1, snapshot.ProviderCounts["Spotify"] );
    }

    /// <summary>
    /// T2c: Album records increment AlbumCount; track records increment TrackCount. Mixed input
    /// produces correct split.
    /// </summary>
    [TestMethod]
    public void Build_WithMixedAlbumsAndTracks_SplitsCorrectly( ) {
        StatisticsAccumulator accumulator = new( );

        for (int i = 0; i < 3; i++) {
            accumulator.Add( $"at://x/album/{i}", BuildResult( isAlbum: true,
                lookedUpAt: new DateTime( 2024, 1, 1, 0, 0, i, DateTimeKind.Utc ),
                (SupportedProviders.Spotify, $"A{i}") ) );
        }
        for (int i = 0; i < 5; i++) {
            accumulator.Add( $"at://x/track/{i}", BuildResult( isAlbum: false,
                lookedUpAt: new DateTime( 2024, 1, 2, 0, 0, i, DateTimeKind.Utc ),
                (SupportedProviders.AppleMusic, $"T{i}") ) );
        }

        LookupStatistics snapshot = accumulator.Build( );

        Assert.AreEqual( 8, snapshot.TotalRecords );
        Assert.AreEqual( 3, snapshot.AlbumCount );
        Assert.AreEqual( 5, snapshot.TrackCount );
    }

    /// <summary>
    /// T2d: Provider counts are keyed on <c>provider.ToString()</c> and sorted descending by count.
    /// </summary>
    [TestMethod]
    public void Build_ProviderCounts_AreSortedDescendingByCount( ) {
        StatisticsAccumulator accumulator = new( );

        for (int i = 0; i < 5; i++) {
            accumulator.Add( $"at://x/spotify/{i}", BuildResult( isAlbum: false,
                lookedUpAt: new DateTime( 2024, 4, 1, 0, 0, i, DateTimeKind.Utc ),
                (SupportedProviders.Spotify, $"S{i}") ) );
        }
        for (int i = 0; i < 2; i++) {
            accumulator.Add( $"at://x/am/{i}", BuildResult( isAlbum: false,
                lookedUpAt: new DateTime( 2024, 4, 2, 0, 0, i, DateTimeKind.Utc ),
                (SupportedProviders.AppleMusic, $"AM{i}") ) );
        }
        accumulator.Add( "at://x/tidal/0", BuildResult( isAlbum: false,
            lookedUpAt: new DateTime( 2024, 4, 3, 0, 0, 0, DateTimeKind.Utc ),
            (SupportedProviders.Tidal, "Ti0") ) );

        LookupStatistics snapshot = accumulator.Build( );

        // Provider counts should be sorted descending: Spotify(5), AppleMusic(2), Tidal(1).
        List<(string Name, int Count)> ordered = [..
            snapshot.ProviderCounts.Select( kv => (kv.Key, kv.Value) )];

        Assert.HasCount( 3, ordered );
        Assert.AreEqual( "Spotify", ordered[0].Name );
        Assert.AreEqual( 5, ordered[0].Count );
        Assert.AreEqual( "AppleMusic", ordered[1].Name );
        Assert.AreEqual( 2, ordered[1].Count );
        Assert.AreEqual( "Tidal", ordered[2].Name );
        Assert.AreEqual( 1, ordered[2].Count );
    }

    /// <summary>
    /// T2e: EarliestLookup and LatestLookup track the extremes across all records.
    /// </summary>
    [TestMethod]
    public void Build_EarliestAndLatestLookup_TracksExtremes( ) {
        StatisticsAccumulator accumulator = new( );
        DateTime early = new( 2023, 1, 1, 0, 0, 0, DateTimeKind.Utc );
        DateTime mid = new( 2024, 6, 15, 12, 0, 0, DateTimeKind.Utc );
        DateTime late = new( 2025, 12, 31, 23, 59, 0, DateTimeKind.Utc );

        // Add in non-chronological order.
        accumulator.Add( "at://x/mid", BuildResult( isAlbum: false, lookedUpAt: mid, (SupportedProviders.Spotify, "M") ) );
        accumulator.Add( "at://x/late", BuildResult( isAlbum: false, lookedUpAt: late, (SupportedProviders.Spotify, "L") ) );
        accumulator.Add( "at://x/early", BuildResult( isAlbum: false, lookedUpAt: early, (SupportedProviders.Spotify, "E") ) );

        LookupStatistics snapshot = accumulator.Build( );

        Assert.AreEqual( new DateTimeOffset( early, TimeSpan.Zero ), snapshot.EarliestLookup );
        Assert.AreEqual( new DateTimeOffset( late, TimeSpan.Zero ), snapshot.LatestLookup );
    }

    /// <summary>
    /// T2f: RecentEntries returns the top 5 most-recent records by LookedUpAt, capped at 5.
    /// </summary>
    [TestMethod]
    public void Build_RecentEntries_ReturnsTop5MostRecentByLookedUpAt( ) {
        StatisticsAccumulator accumulator = new( );

        for (int i = 0; i < 10; i++) {
            accumulator.Add( $"at://x/rkey{i:D3}", BuildResult( isAlbum: false,
                lookedUpAt: new DateTime( 2024, 1, 1, 0, 0, i, DateTimeKind.Utc ),
                (SupportedProviders.Spotify, $"T{i}") ) );
        }

        LookupStatistics snapshot = accumulator.Build( );

        Assert.HasCount( 5, snapshot.RecentEntries );
        // Top 5 most recent are indices 9,8,7,6,5.
        for (int rank = 0; rank < 5; rank++) {
            int expectedIdx = 9 - rank;
            Assert.AreEqual( $"at://x/rkey{expectedIdx:D3}", snapshot.RecentEntries[rank].AtUri,
                $"Rank {rank}: expected rkey{expectedIdx:D3}" );
        }
    }

    /// <summary>
    /// T2g: The bounded-buffer trim fires when more than 100 records accumulate; after trimming,
    /// <c>_recentCandidates</c> is kept to 10. Final Build still produces at most 5 entries.
    /// </summary>
    [TestMethod]
    public void Build_BoundedBufferTrim_FiresAt101Records( ) {
        StatisticsAccumulator accumulator = new( );

        for (int i = 0; i < 101; i++) {
            accumulator.Add( $"at://x/rkey{i:D3}", BuildResult( isAlbum: false,
                lookedUpAt: new DateTime( 2024, 1, 1, 0, 0, 0, DateTimeKind.Utc ).AddSeconds( i ),
                (SupportedProviders.Spotify, $"T{i}") ) );
        }

        LookupStatistics snapshot = accumulator.Build( );

        // After 101 adds, the buffer has been trimmed to 10; Build returns top 5 of those 10.
        Assert.HasCount( 5, snapshot.RecentEntries );
        Assert.AreEqual( 101, snapshot.TotalRecords );
    }

    /// <summary>
    /// T2h: <see cref="LookupStatistics.GeneratedAt"/> is set to a UTC time approximately equal to
    /// the time of the Build call, and <see cref="LookupStatistics.CacheBootstrapStatus"/> is null.
    /// </summary>
    [TestMethod]
    public void Build_GeneratedAtIsUtcNow_AndCacheBootstrapStatusIsNull( ) {
        StatisticsAccumulator accumulator = new( );

        DateTimeOffset before = DateTimeOffset.UtcNow;
        LookupStatistics snapshot = accumulator.Build( );
        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.IsTrue( snapshot.GeneratedAt >= before && snapshot.GeneratedAt <= after,
            $"GeneratedAt ({snapshot.GeneratedAt}) must be between {before} and {after}" );
        Assert.IsNull( snapshot.CacheBootstrapStatus );
    }

    /// <summary>
    /// T2 cross-check: <see cref="StatisticsAccumulator"/> and <c>StatisticsService.ComputeStatisticsAsync</c>
    /// produce identical field values for the same input records. Both are fed the same deterministic
    /// fixture and the numeric/structural fields are compared directly (GeneratedAt excluded because
    /// both capture UtcNow at slightly different instants).
    /// </summary>
    [TestMethod]
    public void Build_CrossCheck_MatchesStatisticsServiceProjection( ) {
        // Build a deterministic fixture: 3 tracks (Spotify+AppleMusic), 2 albums (Tidal), 1 partial.
        List<(string AtUri, MediaLinkResult Result)> fixture = BuildCrossCheckFixture( );

        // Run the accumulator.
        StatisticsAccumulator accumulator = new( );
        foreach ((string atUri, MediaLinkResult result) in fixture) {
            accumulator.Add( atUri, result );
        }
        LookupStatistics accSnap = accumulator.Build( );

        // Run the reference projection (the same logic in StatisticsService, inlined here to avoid
        // the async streaming surface and Redis dependency).
        LookupStatistics refSnap = ComputeReferenceSnapshot( fixture );

        // Compare all numeric/structural fields.
        Assert.AreEqual( refSnap.TotalRecords, accSnap.TotalRecords, "TotalRecords" );
        Assert.AreEqual( refSnap.AlbumCount, accSnap.AlbumCount, "AlbumCount" );
        Assert.AreEqual( refSnap.TrackCount, accSnap.TrackCount, "TrackCount" );
        Assert.AreEqual( refSnap.EarliestLookup, accSnap.EarliestLookup, "EarliestLookup" );
        Assert.AreEqual( refSnap.LatestLookup, accSnap.LatestLookup, "LatestLookup" );
        CollectionAssert.AreEqual(
            refSnap.ProviderCounts.Keys.ToList( ),
            accSnap.ProviderCounts.Keys.ToList( ),
            "ProviderCounts keys (sorted descending)" );
        CollectionAssert.AreEqual(
            refSnap.ProviderCounts.Values.ToList( ),
            accSnap.ProviderCounts.Values.ToList( ),
            "ProviderCounts values" );
        Assert.HasCount( refSnap.RecentEntries.Count, accSnap.RecentEntries, "RecentEntries count" );
        for (int i = 0; i < refSnap.RecentEntries.Count; i++) {
            Assert.AreEqual( refSnap.RecentEntries[i].AtUri, accSnap.RecentEntries[i].AtUri,
                $"RecentEntries[{i}].AtUri" );
            Assert.AreEqual( refSnap.RecentEntries[i].IsAlbum, accSnap.RecentEntries[i].IsAlbum,
                $"RecentEntries[{i}].IsAlbum" );
            Assert.AreEqual( refSnap.RecentEntries[i].Artist, accSnap.RecentEntries[i].Artist,
                $"RecentEntries[{i}].Artist" );
        }
        Assert.IsNull( accSnap.CacheBootstrapStatus, "CacheBootstrapStatus must be null" );
    }

    #endregion

    #region Helper methods

    private static MediaLinkResult BuildResult(
        bool isAlbum,
        DateTime lookedUpAt,
        params (SupportedProviders Provider, string Isrc)[] providers ) {
        MediaLinkResult result = new( ) { LookedUpAt = lookedUpAt };
        foreach ((SupportedProviders provider, string isrc) in providers) {
            result.Results.Add( provider, new MusicLookupResult {
                Artist = $"Artist-{isrc}",
                Title = $"Title-{isrc}",
                IsAlbum = isAlbum,
                ExternalId = isrc,
                URL = $"https://example.com/{isrc}"
            } );
        }
        return result;
    }

    private static List<(string AtUri, MediaLinkResult Result)> BuildCrossCheckFixture( ) {
        List<(string, MediaLinkResult)> records = [];
        // 3 tracks: Spotify+AppleMusic
        records.Add( ("at://did:plc:x/app.bsky.feed.post/aaaaaaa",
            BuildResult( false, new DateTime( 2024, 1, 1, 0, 0, 10, DateTimeKind.Utc ),
                (SupportedProviders.Spotify, "ISRC001"),
                (SupportedProviders.AppleMusic, "AMC001") )) );
        records.Add( ("at://did:plc:x/app.bsky.feed.post/bbbbbbb",
            BuildResult( false, new DateTime( 2024, 1, 1, 0, 0, 20, DateTimeKind.Utc ),
                (SupportedProviders.Spotify, "ISRC002") )) );
        records.Add( ("at://did:plc:x/app.bsky.feed.post/ccccccc",
            BuildResult( false, new DateTime( 2024, 1, 1, 0, 0, 5, DateTimeKind.Utc ),
                (SupportedProviders.Spotify, "ISRC003"),
                (SupportedProviders.Tidal, "TI003") )) );
        // 2 albums: Tidal only
        records.Add( ("at://did:plc:x/app.bsky.feed.post/ddddddd",
            BuildResult( true, new DateTime( 2024, 1, 1, 0, 0, 30, DateTimeKind.Utc ),
                (SupportedProviders.Tidal, "TI004") )) );
        records.Add( ("at://did:plc:x/app.bsky.feed.post/eeeeeee",
            BuildResult( true, new DateTime( 2024, 1, 1, 0, 0, 1, DateTimeKind.Utc ),
                (SupportedProviders.Tidal, "TI005") )) );
        return records;
    }

    /// <summary>
    /// Computes a reference snapshot using the same logic as <c>StatisticsService.ComputeStatisticsAsync</c>
    /// inlined here (no Redis, no async streaming). Used as the golden-master in T2 cross-check.
    /// </summary>
    private static LookupStatistics ComputeReferenceSnapshot(
        IEnumerable<(string AtUri, MediaLinkResult Result)> records ) {
        int totalCount = 0;
        int albumCount = 0;
        int trackCount = 0;
        Dictionary<string, int> providerCounts = [];
        List<(string AtUri, MediaLinkResult Result, DateTimeOffset LookedUpAt)> recentCandidates = [];
        DateTimeOffset? earliestLookup = null;
        DateTimeOffset? latestLookup = null;

        foreach ((string atUri, MediaLinkResult result) in records) {
            totalCount++;

            bool isAlbum = result.Results.Values.FirstOrDefault( )?.IsAlbum ?? false;
            if (isAlbum) {
                albumCount++;
            } else {
                trackCount++;
            }

            foreach (SupportedProviders provider in result.Results.Keys) {
                string providerName = provider.ToString( );
                providerCounts[providerName] = providerCounts.GetValueOrDefault( providerName ) + 1;
            }

            DateTimeOffset lookedUpAt = result.LookedUpAt;
            if (earliestLookup is null || lookedUpAt < earliestLookup) {
                earliestLookup = lookedUpAt;
            }
            if (latestLookup is null || lookedUpAt > latestLookup) {
                latestLookup = lookedUpAt;
            }

            recentCandidates.Add( (atUri, result, lookedUpAt) );
            if (recentCandidates.Count > 100) {
                List<(string, MediaLinkResult, DateTimeOffset)> trimmed = [..
                    recentCandidates.OrderByDescending( x => x.LookedUpAt ).Take( 10 )];
                recentCandidates.Clear( );
                recentCandidates.AddRange( trimmed );
            }
        }

        List<RecentLookupEntry> recentEntries = [..
            recentCandidates
            .OrderByDescending( x => x.LookedUpAt )
            .Take( 5 )
            .Select( x => CreateRecentEntry( x.AtUri, x.Result ) )
        ];

        return new LookupStatistics {
            TotalRecords = totalCount,
            AlbumCount = albumCount,
            TrackCount = trackCount,
            ProviderCounts = providerCounts.OrderByDescending( x => x.Value ).ToDictionary( x => x.Key, x => x.Value ),
            RecentEntries = recentEntries,
            EarliestLookup = earliestLookup,
            LatestLookup = latestLookup,
            GeneratedAt = DateTimeOffset.UtcNow,
            CacheBootstrapStatus = null
        };
    }

    private static RecentLookupEntry CreateRecentEntry( string atUri, MediaLinkResult result ) {
        MusicLookupResult? firstResult = result.Results.Values.FirstOrDefault( );
        string[] uriParts = atUri.Split( '/' );
        string? cardId = null;
        if (uriParts.Length >= 5) {
            string rkey = uriParts[^1];
            cardId = BridgeBeats.Core.Infrastructure.Storage.RecordKeyGenerator.GenerateCardId( rkey );
        }
        return new RecentLookupEntry {
            AtUri = atUri,
            IsAlbum = firstResult?.IsAlbum ?? false,
            Artist = firstResult?.Artist ?? "Unknown",
            Title = firstResult?.Title ?? "Unknown",
            LookedUpAt = result.LookedUpAt,
            CardId = cardId
        };
    }

    #endregion
}
