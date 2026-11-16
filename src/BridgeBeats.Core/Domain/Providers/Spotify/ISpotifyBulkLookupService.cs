using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Core.Domain.Providers.Spotify;

/// <summary>
/// Spotify-local contract for batch ("several at once") lookups against Spotify's bulk endpoints,
/// implemented by the sealed <see cref="SpotifyLookupService"/>.
/// </summary>
/// <remarks>
/// This is distinct from the per-provider <see cref="BridgeBeats.Contracts.Interfaces.IMusicLookupService"/> single-entity contract:
/// it resolves many ids in one request to reduce round-trips. Batch sizes are bounded by Spotify's
/// limits, and a 429 surfaces as a rate-limit exception rather than a partial result. The interface is
/// the testability seam for <c>SpotifyBulkProcessorService</c>, since the concrete implementation is
/// constructed with HTTP/token dependencies that make direct instantiation in unit tests impractical.
/// </remarks>
public interface ISpotifyBulkLookupService {

    /// <summary>Resolves a single track by ISRC after a batched native-id miss.</summary>
    Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc );

    /// <summary>Resolves a single album by UPC after a batched native-id miss.</summary>
    Task<MusicLookupResult?> GetInfoByUPCAsync( string upc );

    /// <summary>
    /// Performs a bulk lookup of tracks by their Spotify IDs.
    /// </summary>
    /// <param name="trackIds">The Spotify track IDs to look up (max 50).</param>
    /// <returns>
    /// A dictionary keyed by the requested id; each value is the resolved track, or
    /// <see langword="null"/> when Spotify returned no track for that id. An empty dictionary indicates a
    /// request-level failure (auth error, network error); callers should requeue without writing saga state.
    /// </returns>
    Task<Dictionary<string, MusicLookupResult?>> GetTracksByIdsAsync( IEnumerable<string> trackIds );

    /// <summary>
    /// Performs a bulk lookup of albums by their Spotify IDs.
    /// </summary>
    /// <param name="albumIds">The Spotify album IDs to look up (max 20).</param>
    /// <returns>
    /// A dictionary keyed by the requested id; each value is the resolved album, or
    /// <see langword="null"/> when Spotify returned no album for that id. An empty dictionary indicates a
    /// request-level failure; callers should requeue without writing saga state.
    /// </returns>
    Task<Dictionary<string, MusicLookupResult?>> GetAlbumsByIdsAsync( IEnumerable<string> albumIds );
}
