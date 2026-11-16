namespace BridgeBeats.Contracts.Constants;

/// <summary>
/// Well-known Redis Pub/Sub channel names shared across deployable projects. Both the publishing
/// and subscribing sides must agree on these literals; placing them here ensures they stay in sync
/// without duplication.
/// </summary>
public static class RedisChannels {

    /// <summary>
    /// Literal Pub/Sub channel on which Web requests a manual statistics refresh and the
    /// Maintenance worker subscribes. Payload is unused — presence is the signal.
    /// </summary>
    public const string StatisticsRefreshRequested = "stats:refresh-requested";
}
