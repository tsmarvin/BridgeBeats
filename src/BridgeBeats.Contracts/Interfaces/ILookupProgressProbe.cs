namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Reports whether a non-finalized saga exists for a given lookup key. Used at render time to
/// decide whether to show a "lookup in progress" indicator on a result card.
/// </summary>
public interface ILookupProgressProbe {

    /// <summary>
    /// Returns <see langword="true"/> when a non-finalized saga exists for the key, indicating
    /// the lookup is still in progress.
    /// </summary>
    /// <param name="lookupKey">The normalized lookup key to probe (for example <c>IsrcLookup:USRC12345678</c>).</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> when a saga for the key is active and not yet finalized;
    /// <see langword="false"/> otherwise.
    /// </returns>
    Task<bool> IsActiveAsync( string lookupKey, CancellationToken ct = default );
}
