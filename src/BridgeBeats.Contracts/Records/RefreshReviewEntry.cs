using System.Text.Json.Serialization;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// Review metadata for a stale PDS record whose refresh completed without any provider result.
/// </summary>
public sealed record RefreshReviewEntry {
    /// <summary>The AT-URI of the source PDS record.</summary>
    [JsonPropertyName( "sourceRecordUri" )]
    public required string SourceRecordUri { get; init; }

    /// <summary>
    /// The source record CID observed when the refresh was registered. Destructive disposition uses
    /// this value as an ATProto compare-and-swap guard so a newer record revision cannot be deleted.
    /// </summary>
    [JsonPropertyName( "sourceRecordCid" )]
    public string? SourceRecordCid { get; init; }

    /// <summary>The refresh saga that attempted to resolve the record.</summary>
    [JsonPropertyName( "sagaId" )]
    public required string SagaId { get; init; }

    /// <summary>Redis-only saga instance token fencing this review context to one generation.</summary>
    [JsonPropertyName( "instanceToken" )]
    public string? InstanceToken { get; init; }

    /// <summary>The canonical lookup type used to identify the refresh saga.</summary>
    [JsonPropertyName( "lookupType" )]
    public required LookupRequestType LookupType { get; init; }

    /// <summary>The canonical lookup value used to identify the refresh saga.</summary>
    [JsonPropertyName( "lookupValue" )]
    public required string LookupValue { get; init; }

    /// <summary>Whether the source record represents an album.</summary>
    [JsonPropertyName( "isAlbum" )]
    public bool? IsAlbum { get; init; }

    /// <summary>The last-known provider results from the source PDS record.</summary>
    [JsonPropertyName( "sourceResult" )]
    public MediaLinkResult? SourceResult { get; init; }

    /// <summary>When the refresh was first registered.</summary>
    [JsonPropertyName( "createdAt" )]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Numeric form of <see cref="CreatedAt"/> used by Redis Lua guards for chronological
    /// comparisons. ISO-8601 strings with different offsets are not lexically time-ordered.
    /// </summary>
    [JsonPropertyName( "createdAtUnixMilliseconds" )]
    public long CreatedAtUnixMilliseconds => CreatedAt.ToUnixTimeMilliseconds( );

    /// <summary>When the refresh became reviewable; null while it is still pending.</summary>
    [JsonPropertyName( "failedAt" )]
    public DateTimeOffset? FailedAt { get; init; }

    /// <summary>Why the record was placed in the review queue.</summary>
    [JsonPropertyName( "failureReason" )]
    public string? FailureReason { get; init; }
}
