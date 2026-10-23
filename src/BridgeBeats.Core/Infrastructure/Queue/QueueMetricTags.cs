namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Standard tag (dimension) key names attached to queue and rate-limit metrics.
/// </summary>
/// <remarks>
/// These literals are part of the observability contract: dashboards and alerts group and
/// filter on these exact tag keys, so renaming a value breaks downstream queries. The tag
/// values themselves (provider names, priorities, and so on) are lower-cased at the call
/// site before being recorded.
/// </remarks>
public static class QueueMetricTags {
    /// <summary>
    /// Tag key for the provider a measurement belongs to (spotify, applemusic, tidal).
    /// Literal value: <c>"provider"</c>.
    /// </summary>
    public const string Provider = "provider";

    /// <summary>
    /// Tag key for the queue priority lane (interactive, background, or bulk).
    /// Literal value: <c>"priority"</c>.
    /// </summary>
    public const string Priority = "priority";

    /// <summary>
    /// Tag key for the provider endpoint or lookup type a measurement relates to.
    /// Literal value: <c>"endpoint"</c>.
    /// </summary>
    public const string Endpoint = "endpoint";

    /// <summary>
    /// Tag key for the lookup request type (IsrcLookup, UpcLookup, etc.).
    /// Literal value: <c>"lookup_type"</c>.
    /// </summary>
    public const string LookupType = "lookup_type";

    /// <summary>
    /// Tag key for the outcome status of an operation (success, failure, rate_limited).
    /// Literal value: <c>"status"</c>.
    /// </summary>
    public const string Status = "status";

    /// <summary>
    /// Tag key for the HTTP status code of a provider API request.
    /// Literal value: <c>"status_code"</c>.
    /// </summary>
    public const string StatusCode = "status_code";

    /// <summary>
    /// Tag key for the HTTP method used for a provider API request.
    /// Literal value: <c>"method"</c>.
    /// </summary>
    public const string Method = "method";

    /// <summary>Tag key for enqueue origin.</summary>
    public const string Origin = "origin";

    /// <summary>Tag key for a Redis stream name.</summary>
    public const string Stream = "stream";

    /// <summary>Tag key for an operational outcome.</summary>
    public const string Outcome = "outcome";

    /// <summary>Tag key indicating whether maintenance selected a record again.</summary>
    public const string Reselected = "reselected";

    /// <summary>Tag key for the authoritative saga state observed after a leg completes.</summary>
    public const string SagaState = "saga_state";
}
