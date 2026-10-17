using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Extends PDS storage with an identity-preserving write used by maintenance refreshes.
/// </summary>
public interface ITargetedATProtoStorageService : IATProtoStorageService {
    /// <summary>
    /// Updates the exact BridgeBeats record identified by <paramref name="targetRecordUri"/>,
    /// regardless of identifier drift in the refreshed provider payload.
    /// </summary>
    Task<string> StoreMediaLinkResultAtUriAsync(
        MediaLinkResult result,
        string targetRecordUri,
        CancellationToken cancellationToken = default );
}
