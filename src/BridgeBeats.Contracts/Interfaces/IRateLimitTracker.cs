using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Tracks provider rate-limit windows at the provider's configured granularity: read the current
/// state, mark a tracking key limited until an instant, clear it, and list active limits.
/// </summary>
/// <remarks>
/// Implemented in <c>BridgeBeats.Core</c> by <c>RedisRateLimitTracker</c>
/// (<c>Infrastructure/Queue/RedisRateLimitTracker.cs</c>), so state is shared across all service
/// instances. Most providers retain endpoint-level independence. Spotify data endpoints currently
/// collapse to a provider-wide tracking key while token acquisition remains independent. The
/// <c>retryAfter</c> values are absolute instants ("retry no earlier than"), not durations.
/// </remarks>
public interface IRateLimitTracker {

    /// <summary>
    /// Reads the current rate-limit state for a provider endpoint.
    /// </summary>
    /// <param name="provider">The provider to query.</param>
    /// <param name="endpoint">The API endpoint path within the provider to query (for example <c>/v1/tracks</c>).</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the current <see cref="RateLimitState"/> for the endpoint.</returns>
    Task<RateLimitState> GetStateAsync( SupportedProviders provider, string endpoint, CancellationToken cancellationToken = default );

    /// <summary>
    /// Marks a provider endpoint as rate-limited until the given absolute instant.
    /// </summary>
    /// <param name="provider">The provider that returned the rate limit.</param>
    /// <param name="endpoint">The API endpoint that was rate-limited.</param>
    /// <param name="retryAfter">The absolute instant before which the endpoint should not be retried.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the limit has been recorded.</returns>
    Task SetRateLimitedAsync( SupportedProviders provider, string endpoint, DateTimeOffset retryAfter, CancellationToken cancellationToken = default );

    /// <summary>
    /// Clears any recorded rate limit for a provider endpoint, for example after a successful
    /// request.
    /// </summary>
    /// <param name="provider">The provider whose limit is cleared.</param>
    /// <param name="endpoint">The API endpoint whose limit is cleared.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the limit has been cleared.</returns>
    Task ClearAsync( SupportedProviders provider, string endpoint, CancellationToken cancellationToken = default );

    /// <summary>
    /// Lists every endpoint currently rate-limited for a provider, with each endpoint's
    /// retry-after instant.
    /// </summary>
    /// <param name="provider">The provider to list active limits for.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the currently rate-limited endpoints; an empty list when none are
    /// limited.
    /// </returns>
    Task<IReadOnlyList<RateLimitedEndpoint>> GetAllRateLimitedAsync( SupportedProviders provider, CancellationToken cancellationToken = default );
}
