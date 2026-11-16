using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Contracts.Records.WorkerApi;

/// <summary>
/// The single response envelope a provider worker returns from any of its <c>/lookup/*</c>
/// endpoints, wrapping the resolved <see cref="MusicLookupResult"/> with status information. A
/// successful lookup and a successful no-match return HTTP 200; typed failures use status-bearing
/// HTTP responses and this envelope for machine-readable error metadata.
/// </summary>
/// <param name="Success">Whether the lookup succeeded.</param>
/// <param name="Result">The resolved item, or <see langword="null"/> when there is none.</param>
/// <param name="ErrorMessage">
/// A human-readable failure reason, or <see langword="null"/> when no error message is set.
/// </param>
/// <param name="RetryAfterSeconds">The provider retry delay in seconds, when rate limited.</param>
/// <param name="RetryThresholdSeconds">The retry threshold in seconds, when rate limited.</param>
/// <remarks>Construct instances through the factory methods rather than the primary constructor.</remarks>
public sealed record ProviderLookupResponse(
    bool Success,
    MusicLookupResult? Result,
    string? ErrorMessage,
    double? RetryAfterSeconds = null,
    double? RetryThresholdSeconds = null
) {
    /// <summary>
    /// Creates a successful response from a lookup result. A null result has the same successful
    /// no-match semantics as <see cref="NotFound"/>.
    /// </summary>
    /// <param name="result">The resolved item, or <see langword="null"/> when nothing was found.</param>
    /// <returns>
    /// A successful response containing <paramref name="result"/>, with no error message.
    /// </returns>
    public static ProviderLookupResponse Ok( MusicLookupResult? result )
        => new( true, result, null );

    /// <summary>
    /// Creates a failed response carrying an error reason.
    /// </summary>
    /// <param name="errorMessage">The human-readable reason the lookup failed.</param>
    /// <param name="retryAfterSeconds">The retry delay in seconds, when rate limited.</param>
    /// <param name="retryThresholdSeconds">The retry threshold in seconds, when rate limited.</param>
    /// <returns>
    /// A response with <see cref="Success"/> set to <see langword="false"/>, no result, and the
    /// supplied <paramref name="errorMessage"/>.
    /// </returns>
    public static ProviderLookupResponse Error(
        string errorMessage,
        double? retryAfterSeconds = null,
        double? retryThresholdSeconds = null
    ) => new( false, null, errorMessage, retryAfterSeconds, retryThresholdSeconds );

    /// <summary>
    /// Creates a "nothing matched" response: the lookup ran without error but found no item.
    /// </summary>
    /// <returns>
    /// A response with <see cref="Success"/> set to <see langword="true"/>, a <see langword="null"/>
    /// result, and no error message.
    /// </returns>
    public static ProviderLookupResponse NotFound( )
        => new( true, null, null );
}
