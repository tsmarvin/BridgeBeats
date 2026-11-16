using System.Text.Json.Serialization;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// The durable state of a multi-provider lookup saga: the request that started it, the
/// per-provider progress, the AT-URIs of any persisted results, and scheduling context.
/// Persisted and updated by the saga state manager as each provider leg reports in.
/// </summary>
public sealed record LookupSagaState {

    /// <summary>Redis-only instance token used to guard delete/recreate races.</summary>
    [JsonIgnore]
    public string? InstanceToken { get; init; }

    /// <summary>The unique identifier of this saga. Derived deterministically from the lookup key.</summary>
    [JsonPropertyName( "sagaId" )]
    [JsonRequired]
    public required string SagaId { get; init; }

    /// <summary>The canonical key identifying the thing being looked up across providers.</summary>
    [JsonPropertyName( "lookupKey" )]
    [JsonRequired]
    public required string LookupKey { get; init; }

    /// <summary>The lookup strategy that initiated this saga.</summary>
    [JsonPropertyName( "lookupType" )]
    [JsonRequired]
    public required LookupRequestType LookupType { get; init; }

    /// <summary>The raw lookup value (for example, an ISRC, UPC, URL, or metadata string) for this saga.</summary>
    [JsonPropertyName( "lookupValue" )]
    [JsonRequired]
    public required string LookupValue { get; init; }

    /// <summary>The absolute instant the saga was created. Defaults to the current UTC time.</summary>
    [JsonPropertyName( "createdAt" )]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Per-provider progress for this saga, keyed by provider. Each entry tracks one provider's
    /// leg; see <see cref="ProviderLookupState"/>. An empty dictionary means no providers have
    /// been registered yet.
    /// </summary>
    [JsonPropertyName( "providerStates" )]
    public Dictionary<SupportedProviders, ProviderLookupState> ProviderStates { get; init; } = [];

    /// <summary>
    /// The AT-URI (<c>at://…</c>) of the persisted partial result, when one has been written;
    /// otherwise <see langword="null"/>.
    /// </summary>
    [JsonPropertyName( "partialResultUri" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? PartialResultUri { get; init; }

    /// <summary>
    /// The AT-URI (<c>at://…</c>) of the persisted final result, when the saga has finalized;
    /// otherwise <see langword="null"/>.
    /// </summary>
    [JsonPropertyName( "finalResultUri" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? FinalResultUri { get; init; }

    /// <summary><see langword="true"/> when the saga's current result is partial rather than complete.</summary>
    [JsonPropertyName( "isPartial" )]
    public bool IsPartial { get; init; }

    /// <summary>
    /// The highest provider-count already durably written to the PDS for this saga. Guards
    /// against redundant writes: a write for generation <c>k</c> proceeds only when the stored
    /// generation is strictly less than <c>k</c>, ensuring each provider-count level is written
    /// at most once and the write count is bounded by the number of providers.
    /// </summary>
    [JsonPropertyName( "writeGeneration" )]
    public int WriteGeneration { get; init; }

    /// <summary>The provider that seeded this lookup (the source URL's provider), when known.</summary>
    [JsonPropertyName( "initialProvider" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public SupportedProviders? InitialProvider { get; init; }

    /// <summary>Rate-limit windows recorded against providers during this saga, for user notification, when any.</summary>
    [JsonPropertyName( "rateLimitInfo" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public List<ProviderRateLimitInfo>? RateLimitInfo { get; init; }

    /// <summary>
    /// The scheduling priority the saga inherits from the request that originated it. Secondary
    /// lookups inherit this so bulk-origin sagas (for example, the JetStream firehose) never
    /// compete with interactive lookups. Defaults to <see cref="QueuePriority.Background"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="QueuePriority.Background"/> for sagas persisted before this field
    /// existed, so they are never promoted to interactive.
    /// </remarks>
    [JsonPropertyName( "originPriority" )]
    public QueuePriority OriginPriority { get; init; } = QueuePriority.Background;

    /// <summary>
    /// The providers whose legs are not yet complete. Derived from <see cref="ProviderStates"/>
    /// at read time and not persisted.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<SupportedProviders> PendingProviders =>
        ProviderStates.Where( kv => !kv.Value.IsComplete ).Select( kv => kv.Key );

    /// <summary>
    /// <see langword="true"/> when at least one provider is registered and every provider leg
    /// is complete. Derived from <see cref="ProviderStates"/> at read time and not persisted.
    /// </summary>
    /// <remarks>
    /// Deliberately <see langword="false"/> when no providers are registered: an empty saga is
    /// not treated as complete.
    /// </remarks>
    [JsonIgnore]
    public bool IsComplete => ProviderStates.Count > 0 && ProviderStates.Values.All( s => s.IsComplete );
}
