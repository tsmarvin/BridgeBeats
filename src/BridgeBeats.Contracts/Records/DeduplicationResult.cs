using System.Text.Json.Serialization;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// Outcome of an attempt to acquire the single-flight lock for a request key, returned by
/// the request deduplicator. Distinguishes the caller that won the lock from one that found
/// the same work already in progress.
/// </summary>
/// <param name="Acquired"><see langword="true"/> when this caller won the lock and is responsible for processing the request.</param>
/// <param name="AlreadyInFlight"><see langword="true"/> when another caller is already processing the same request key.</param>
/// <param name="RequestKey">The deduplication key identifying the logical request.</param>
/// <param name="LeaseToken">Unique token required to release an acquired lock, or null for a waiter.</param>
public sealed record DeduplicationResult(
    [property: JsonPropertyName( "acquired" )] bool Acquired,
    [property: JsonPropertyName( "alreadyInFlight" )] bool AlreadyInFlight,
    [property: JsonPropertyName( "requestKey" )] string RequestKey,
    [property: JsonPropertyName( "leaseToken" )] string? LeaseToken = null
);
