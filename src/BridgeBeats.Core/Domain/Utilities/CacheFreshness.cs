namespace BridgeBeats.Core.Domain.Utilities;

/// <summary>
/// Defines the age-only staleness rule used by the bulk refresh sweep. The interactive read cache
/// applies the equivalent age comparison in <c>RedisMediaLinkCache.CheckRecordFreshness</c> rather
/// than calling this helper directly. A record's partial-ness is deliberately NOT a staleness
/// trigger in either path, so a record can never be re-attempted more than once per freshness
/// window (see remarks).
/// </summary>
/// <remarks>
/// Partial-ness was previously an <c>||</c> term here. It was removed because a record a provider can
/// never complete (the provider genuinely lacks the track) stays partial forever, and a
/// partial-triggers-stale rule would re-attempt it every cycle in perpetuity — a growing, unbounded
/// drain on the budget and the rate-limited external-id search path. Age-only freshness bounds every
/// record to at most one re-attempt per window; the dropped-provider case still self-heals, just
/// once-per-window rather than every-cycle.
/// </remarks>
public static class CacheFreshness {
    /// <summary>
    /// A record is stale when its last successful lookup is older than the configured freshness
    /// window. Partial-ness is intentionally not consulted.
    /// </summary>
    /// <param name="lookedUpAt">The record's last-write timestamp (UTC).</param>
    /// <param name="cacheDays">The freshness window in days; records older than this are stale.</param>
    /// <param name="utcNow">The current UTC instant (inject for testability).</param>
    public static bool IsStale( DateTime lookedUpAt, int cacheDays, DateTime utcNow )
        => lookedUpAt < utcNow.AddDays( -cacheDays );
}
