using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Infrastructure.Storage;

namespace BridgeBeats.Core.Domain.Services;

/// <summary>
/// Per-record accumulator that folds a stream of <see cref="MediaLinkResult"/> records into a
/// <see cref="LookupStatistics"/> snapshot. Call <see cref="Add"/> for each record during the
/// enumeration pass, then call <see cref="Build"/> once to produce the finished snapshot.
/// </summary>
/// <remarks>
/// This is a plain accumulator with no interface and no DI registration. One instance is created
/// before the enumeration loop, <see cref="Add"/> is called for every record, and <see cref="Build"/>
/// is called once at the end. The projection logic covers: album/track split, per-provider counts
/// keyed on <c>provider.ToString()</c>, earliest/latest <c>LookedUpAt</c>, top-5 recent entries via
/// a bounded-buffer trim (>100 → keep 10), and <c>ProviderCounts</c> sorted descending. The
/// <c>Build</c> method sets <c>GeneratedAt = DateTimeOffset.UtcNow</c> and leaves
/// <c>CacheBootstrapStatus = null</c> — the Web layer overlays bootstrap status at read time.
/// </remarks>
public sealed class StatisticsAccumulator {

    private int _totalCount;
    private int _albumCount;
    private int _trackCount;
    private readonly Dictionary<string, int> _providerCounts = [];
    private readonly List<(string AtUri, MediaLinkResult Result, DateTimeOffset LookedUpAt)> _recentCandidates = [];
    private DateTimeOffset? _earliestLookup;
    private DateTimeOffset? _latestLookup;

    /// <summary>
    /// Folds one record into the running totals. Safe to call zero or more times before
    /// <see cref="Build"/>. A record that failed to be cached upstream is still counted here —
    /// statistics accumulation is independent of the cache write.
    /// </summary>
    /// <param name="atUri">The AT-URI of the record.</param>
    /// <param name="result">The record's multi-provider result; treated as read-only.</param>
    public void Add( string atUri, MediaLinkResult result ) {
        _totalCount++;

        bool isAlbum = result.Results.Values.FirstOrDefault( )?.IsAlbum ?? false;
        if (isAlbum) {
            _albumCount++;
        } else {
            _trackCount++;
        }

        foreach (SupportedProviders provider in result.Results.Keys) {
            string providerName = provider.ToString( );
            _providerCounts[providerName] = _providerCounts.GetValueOrDefault( providerName ) + 1;
        }

        DateTimeOffset lookedUpAt = result.LookedUpAt;
        if (_earliestLookup is null || lookedUpAt < _earliestLookup) {
            _earliestLookup = lookedUpAt;
        }
        if (_latestLookup is null || lookedUpAt > _latestLookup) {
            _latestLookup = lookedUpAt;
        }

        _recentCandidates.Add( (atUri, result, lookedUpAt) );
        if (_recentCandidates.Count > 100) {
            List<(string AtUri, MediaLinkResult Result, DateTimeOffset LookedUpAt)> trimmed = [..
                _recentCandidates
                .OrderByDescending( x => x.LookedUpAt )
                .Take( 10 )];
            _recentCandidates.Clear( );
            _recentCandidates.AddRange( trimmed );
        }
    }

    /// <summary>
    /// Produces the final <see cref="LookupStatistics"/> snapshot from all accumulated records.
    /// Sets <see cref="LookupStatistics.GeneratedAt"/> to <see cref="DateTimeOffset.UtcNow"/> and
    /// leaves <see cref="LookupStatistics.CacheBootstrapStatus"/> <see langword="null"/> — the
    /// Web layer overlays the bootstrap status at read time.
    /// </summary>
    /// <returns>The completed statistics snapshot.</returns>
    public LookupStatistics Build( ) {
        List<RecentLookupEntry> recentEntries = [..
            _recentCandidates
            .OrderByDescending( x => x.LookedUpAt )
            .Take( 5 )
            .Select( x => CreateRecentEntry( x.AtUri, x.Result ) )
        ];

        return new LookupStatistics {
            TotalRecords = _totalCount,
            AlbumCount = _albumCount,
            TrackCount = _trackCount,
            ProviderCounts = _providerCounts.OrderByDescending( x => x.Value ).ToDictionary( x => x.Key, x => x.Value ),
            RecentEntries = recentEntries,
            EarliestLookup = _earliestLookup,
            LatestLookup = _latestLookup,
            GeneratedAt = DateTimeOffset.UtcNow,
            CacheBootstrapStatus = null
        };
    }

    /// <summary>
    /// Projects a stored record into a <see cref="RecentLookupEntry"/> for the "recent lookups"
    /// list. Owned by <see cref="StatisticsAccumulator.Build"/>.
    /// </summary>
    private static RecentLookupEntry CreateRecentEntry( string atUri, MediaLinkResult result ) {
        MusicLookupResult? firstResult = result.Results.Values.FirstOrDefault( );
        string? cardId = null;

        string[] uriParts = atUri.Split( '/' );
        if (uriParts.Length >= 5) {
            string rkey = uriParts[^1];
            cardId = RecordKeyGenerator.GenerateCardId( rkey );
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
}
