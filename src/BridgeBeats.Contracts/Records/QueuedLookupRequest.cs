using System.Text.Json.Serialization;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// The queue payload for one provider's leg of a lookup. Carries everything a worker needs
/// to perform the lookup plus the saga and retry bookkeeping the queue uses to route and
/// re-deliver it. Implements <see cref="IQueueableRequest"/>.
/// </summary>
public sealed record QueuedLookupRequest : IQueueableRequest {
    /// <summary>Telemetry origin for this queue payload.</summary>
    [JsonPropertyName( "enqueueOrigin" )]
    public QueueEnqueueOrigin EnqueueOrigin { get; init; } = QueueEnqueueOrigin.New;

    /// <summary>The unique identifier of this queued request.</summary>
    [JsonPropertyName( "requestId" )]
    [JsonRequired]
    public required string RequestId { get; init; }

    /// <summary>The provider this leg targets.</summary>
    [JsonPropertyName( "provider" )]
    [JsonRequired]
    public required SupportedProviders Provider { get; init; }

    /// <summary>The lookup strategy to apply.</summary>
    [JsonPropertyName( "lookupType" )]
    [JsonRequired]
    public required LookupRequestType LookupType { get; init; }

    /// <summary>The raw lookup value (for example, an ISRC, UPC, URL, or id) for this leg.</summary>
    [JsonPropertyName( "lookupValue" )]
    [JsonRequired]
    public required string LookupValue { get; init; }

    /// <summary>The artist name for metadata lookups, when the lookup carries one; otherwise <see langword="null"/>.</summary>
    [JsonPropertyName( "artist" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? Artist { get; init; }

    /// <summary>The title for metadata lookups, when the lookup carries one; otherwise <see langword="null"/>.</summary>
    [JsonPropertyName( "title" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? Title { get; init; }

    /// <summary><see langword="true"/> when the lookup targets an album rather than a track.</summary>
    [JsonPropertyName( "isAlbum" )]
    public bool IsAlbum { get; init; }

    /// <summary>The identifier of the saga (job) this leg belongs to.</summary>
    [JsonPropertyName( "sagaId" )]
    [JsonRequired]
    public required string SagaId { get; init; }

    /// <summary>
    /// Saga generation this delivery was created for. Every producer must capture it after creating
    /// or loading the saga; workers reject the delivery if the saga has since been replaced.
    /// </summary>
    [JsonPropertyName( "sagaInstanceToken" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? SagaInstanceToken { get; init; }

    /// <summary>The absolute instant the request was originally created. Defaults to the current UTC time.</summary>
    [JsonPropertyName( "createdAt" )]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The number of times delivery of this request has been attempted.</summary>
    [JsonPropertyName( "attemptCount" )]
    public int AttemptCount { get; init; }

    /// <summary>
    /// Earliest instant at which a consumer may retry this delivery. A null value is immediately
    /// eligible. Transient provider failures use this to avoid occupying a worker slot while the
    /// retry backoff elapses.
    /// </summary>
    [JsonPropertyName( "notBefore" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>
    /// Routes this typed Spotify id request through the generic single-item queue instead of
    /// returning it to bulk batching. Set by bulk-rejection and interactive retry paths.
    /// </summary>
    [JsonPropertyName( "bypassBulkRouting" )]
    public bool BypassBulkRouting { get; init; }

    /// <summary>The original endpoint that triggered the rate limit on a prior attempt, for tracking; otherwise <see langword="null"/>.</summary>
    [JsonPropertyName( "rateLimitedEndpoint" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? RateLimitedEndpoint { get; init; }

    /// <summary>
    /// The scheduling priority the request inherits from its originating lookup, persisted into
    /// the saga so secondary lookups inherit the origin's urgency. Defaults to
    /// <see cref="QueuePriority.Background"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="QueuePriority.Background"/> so in-flight messages serialized before
    /// this field existed are never promoted to interactive.
    /// </remarks>
    [JsonPropertyName( "originPriority" )]
    public QueuePriority OriginPriority { get; init; } = QueuePriority.Background;

    /// <summary>
    /// Catalog storefront to use for providers whose catalogs vary by country. A blank value lets
    /// the provider apply its normal default.
    /// </summary>
    [JsonPropertyName( "storefront" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? Storefront { get; init; }

    /// <summary>
    /// Lookup strategy to try within this provider leg when the native id no longer resolves.
    /// </summary>
    [JsonPropertyName( "fallbackLookupType" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public LookupRequestType? FallbackLookupType { get; init; }

    /// <summary>The ISRC or UPC paired with <see cref="FallbackLookupType"/>.</summary>
    [JsonPropertyName( "fallbackLookupValue" )]
    [JsonIgnore( Condition = JsonIgnoreCondition.WhenWritingNull )]
    public string? FallbackLookupValue { get; init; }
}
