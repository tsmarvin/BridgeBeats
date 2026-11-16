using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.DTOs;

/// <summary>
/// The central in-memory aggregate of a cross-provider lookup, mutated as each provider's result
/// arrives during assembly. This is the mutable working form; its immutable, PDS-persisted twin is
/// <see cref="Records.MediaLinkResultRecord"/>. Contrast the in-memory per-provider item
/// <see cref="MusicLookupResult"/> with the persisted per-provider row
/// <see cref="Records.ProviderResultRecord"/>.
/// </summary>
public sealed class MediaLinkResult {

    /// <summary>
    /// The input link(s) that resolved to this result. Tracked so the cache can register them as
    /// aliases of the stored record (see
    /// <see cref="Interfaces.IMediaLinkCacheRepository.AddInputLinksAsync"/>). Visible only to the
    /// projects granted internal access (Core, Web, Tests).
    /// </summary>
    internal readonly List<string> InputLinks = [];

    /// <summary>
    /// The resolved per-provider results, keyed by the <see cref="SupportedProviders"/> enum.
    /// Populated incrementally as each provider responds.
    /// </summary>
    public Dictionary<SupportedProviders, MusicLookupResult> Results { get; set; } = [];

    /// <summary>
    /// Optional informational messages accumulated during assembly, or <see langword="null"/> when
    /// there are none.
    /// </summary>
    public List<string>? Messages { get; set; }

    /// <summary>
    /// When this aggregate was looked up.
    /// </summary>
    public DateTime LookedUpAt { get; init; }

    /// <summary>
    /// <see langword="true"/> when this API result was returned before its coordinating saga
    /// finalized and a more complete provider set may follow. This is transient response metadata:
    /// it is derived from saga state and is never persisted to the PDS record.
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Providers currently rate-limited and therefore not yet resolved, or <see langword="null"/>
    /// when none are rate-limited.
    /// </summary>
    public List<SupportedProviders>? RateLimitedProviders { get; set; }

    /// <summary>
    /// Determines equality against another <see cref="MediaLinkResult"/>. Equality is intentionally
    /// narrow: it compares <see cref="InputLinks"/> and <see cref="Results"/> by reference (the same
    /// list and dictionary instances), not by element value. Two separately assembled aggregates
    /// with equal contents are therefore not equal.
    /// </summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>
    /// <see langword="true"/> only when <paramref name="obj"/> is a <see cref="MediaLinkResult"/>
    /// whose <see cref="InputLinks"/> and <see cref="Results"/> are the same instances as this one's.
    /// </returns>
    public override bool Equals( object? obj ) {
        if (
            obj is not null &&
            obj.GetType( ) == typeof( MediaLinkResult )
        ) {
            MediaLinkResult objCast = (MediaLinkResult)obj;
            return objCast.InputLinks == InputLinks &&
                    objCast.Results == Results;
        }
        return false;
    }

    /// <summary>
    /// Returns a hash code derived from <see cref="Results"/> only (the reference hash of the
    /// dictionary instance). Deliberately narrower than <see cref="Equals(object?)"/>, which also
    /// considers <see cref="InputLinks"/>.
    /// </summary>
    /// <returns>The reference hash code of <see cref="Results"/>.</returns>
    public override int GetHashCode( )
        => Results.GetHashCode( );
}
