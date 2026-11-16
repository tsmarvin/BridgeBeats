using System.Text.Json.Serialization;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// Orchestrator-facing envelope for a single lookup outcome. Wraps an optional resolved
/// result with the metadata callers need to interpret it: whether it is partial, the saga
/// it belongs to, and which providers were rate-limited. Produced by the lookup orchestrator,
/// which may emit several of these over the life of one lookup.
/// </summary>
public sealed record LookupResult {

    /// <summary>
    /// The resolved cross-provider result, or <see langword="null"/> when no result is
    /// available yet (for example, an early partial emission).
    /// </summary>
    [JsonPropertyName( "result" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public MediaLinkResult? Result { get; init; }

    /// <summary>
    /// <see langword="true"/> when this result is partial, meaning not every provider has
    /// responded yet and a more complete result may follow. Derived from <see cref="SagaId"/>:
    /// a result is partial if and only if it has a non-empty saga identifier.
    /// </summary>
    [JsonPropertyName( "isPartial" )]
    public bool IsPartial => !string.IsNullOrEmpty( SagaId );

    /// <summary>The identifier of the saga coordinating this lookup, when one is in play.</summary>
    [JsonPropertyName( "sagaId" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? SagaId { get; init; }

    /// <summary>
    /// The providers currently rate-limited for this lookup, when any. Each entry carries
    /// the provider's retry window; see <see cref="ProviderRateLimitInfo"/>.
    /// </summary>
    [JsonPropertyName( "rateLimitedProviders" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public IReadOnlyList<ProviderRateLimitInfo>? RateLimitedProviders { get; init; }
}

/// <summary>
/// Describes one provider's rate-limit window during a lookup: which provider is limited,
/// the absolute instant at which it may be retried, and optionally the specific endpoint.
/// </summary>
/// <param name="Provider">The provider that is rate-limited.</param>
/// <param name="RetryAfter">
/// The absolute wall-clock instant before which the provider should not be retried. This is
/// an instant, not a duration.
/// </param>
/// <param name="Endpoint">The specific provider endpoint that is rate-limited, when the limit is endpoint-scoped; otherwise <see langword="null"/>.</param>

public sealed record ProviderRateLimitInfo(
    [property: JsonPropertyName( "provider" )] SupportedProviders Provider,
    [property: JsonPropertyName( "retryAfter" )] DateTimeOffset RetryAfter,
    [property: JsonPropertyName( "endpoint" ), JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )] string? Endpoint
);
