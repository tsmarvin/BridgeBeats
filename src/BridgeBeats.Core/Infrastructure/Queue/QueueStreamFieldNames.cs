namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Canonical Redis stream field names for the queue wire format.
/// All queue producers and consumers (<c>RedisRequestQueue</c>,
/// <c>SpotifyBulkQueueDecorator</c>, <c>SpotifyBatchQueueHelper</c>) must read
/// and write these exact names so that entries written by one component can be
/// read by another.
/// </summary>
/// <remarks>
/// These literals are a wire contract: the values must not change without migrating in-flight
/// stream entries. A queued stream entry carries a <see cref="Payload"/> field (the serialized
/// request), an <see cref="EnqueuedAt"/> field (when it was added), and optionally a
/// <see cref="DispatchOutboxKey"/> field tying a durable dispatch to its recovery record. Dead-letter entries add
/// further fields that are defined privately on the request queue rather than here. Keep stream
/// NAMES (e.g. <c>SpotifyConstants.BulkTrackIdStream</c>) in their provider-specific constants
/// class; only the field names — which are generic queue wire format — live here.
/// </remarks>
public static class QueueStreamFieldNames {

    /// <summary>
    /// Stream entry field that carries the JSON-serialized request payload. Literal value: <c>"payload"</c>.
    /// </summary>
    public const string Payload = "payload";

    /// <summary>
    /// Stream entry field that carries the ISO-8601 ("O" round-trip) UTC enqueue timestamp.
    /// Used by size-OR-age flush policies to determine how long an entry has waited.
    /// Literal value: <c>"enqueuedAt"</c>.
    /// </summary>
    public const string EnqueuedAt = "enqueuedAt";

    /// <summary>
    /// Optional stream entry field containing the durable lookup-dispatch outbox key. Delivery
    /// transitions carry this value forward so acknowledgement and quarantine can retire the
    /// exact recovery record without reconstructing identity from a potentially malformed payload.
    /// </summary>
    public const string DispatchOutboxKey = "dispatchOutboxKey";
}
