using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Exceptions {

    /// <summary>
    /// Thrown when a provider's <c>Retry-After</c> value exceeds the configured retry threshold,
    /// meaning the rate-limit wait is too long to be worth retrying. Carries the offending
    /// <c>Retry-After</c> value, the threshold it breached, and the request context.
    /// </summary>
    /// <remarks>
    /// The captured request context drives rate-limit handling and requeue of the affected work. The
    /// resilience handler is configured not to retry this exception, so it propagates immediately
    /// rather than waiting out the rate limit.
    /// </remarks>
    public class RetryAfterExceededException : ProviderRateLimitException {

        /// <summary>
        /// The maximum retry wait the caller is willing to tolerate. When
        /// <see cref="ProviderRateLimitException.RetryAfterValue"/> exceeds this, the exception is raised.
        /// </summary>
        public TimeSpan Threshold { get; }

        /// <summary>
        /// Initializes a new instance with the rate-limit context, building a descriptive message.
        /// </summary>
        /// <param name="retryAfterValue">The provider's requested <c>Retry-After</c> wait.</param>
        /// <param name="threshold">The maximum retry wait the caller will tolerate.</param>
        /// <param name="requestUri">The rate-limited request URI, or <see langword="null"/> if unknown.</param>
        /// <param name="provider">The provider that issued the limit, or <see langword="null"/> if unknown.</param>
        public RetryAfterExceededException(
            TimeSpan retryAfterValue,
            TimeSpan threshold,
            Uri? requestUri,
            SupportedProviders? provider
        ) : base( BuildExceededMessage( retryAfterValue, threshold, requestUri, provider ), retryAfterValue, requestUri, provider ) {
            Threshold = ValidateThreshold( threshold );
        }

        /// <summary>
        /// Initializes a new instance with the rate-limit context and an inner exception that
        /// caused or accompanied the rate limit.
        /// </summary>
        /// <param name="retryAfterValue">The provider's requested <c>Retry-After</c> wait.</param>
        /// <param name="threshold">The maximum retry wait the caller will tolerate.</param>
        /// <param name="requestUri">The rate-limited request URI, or <see langword="null"/> if unknown.</param>
        /// <param name="provider">The provider that issued the limit, or <see langword="null"/> if unknown.</param>
        /// <param name="innerException">The exception that is the cause of this exception.</param>
        public RetryAfterExceededException(
            TimeSpan retryAfterValue,
            TimeSpan threshold,
            Uri? requestUri,
            SupportedProviders? provider,
            Exception innerException
        ) : base( BuildExceededMessage( retryAfterValue, threshold, requestUri, provider ), retryAfterValue, requestUri, provider, innerException ) {
            Threshold = ValidateThreshold( threshold );
        }

        private static TimeSpan ValidateThreshold( TimeSpan threshold ) {
            if (threshold < TimeSpan.Zero) {
                throw new ArgumentOutOfRangeException( nameof( threshold ), "Retry threshold must be nonnegative." );
            }
            return threshold;
        }

        private static string BuildExceededMessage( TimeSpan retryAfterValue, TimeSpan threshold, Uri? requestUri, SupportedProviders? provider )
            => $"Rate limit exceeded for {provider?.ToString( ) ?? "Unknown"}; retry after {retryAfterValue.TotalSeconds:F0} seconds exceeds threshold {threshold.TotalSeconds:F0}. " +
               $"Request URI: {requestUri?.ToString( ) ?? "Unknown"}";

        /// <summary>
        /// Infers the <see cref="SupportedProviders"/> from a request URI by matching a substring of
        /// its host (<c>apple</c>, <c>spotify</c>, or <c>tidal</c>).
        /// </summary>
        /// <param name="requestUri">The request URI to inspect.</param>
        /// <returns>
        /// The matching provider, or <see langword="null"/> if <paramref name="requestUri"/> is
        /// <see langword="null"/> or its host matches no known provider.
        /// </returns>
        public static SupportedProviders? DetermineProviderFromUri( Uri? requestUri ) {
            if (requestUri == null) {
                return null;
            }

            string host = requestUri.Host.ToLowerInvariant( );
            return host switch {
                _ when host.Contains( "apple" ) => SupportedProviders.AppleMusic,
                _ when host.Contains( "spotify" ) => SupportedProviders.Spotify,
                _ when host.Contains( "tidal" ) => SupportedProviders.Tidal,
                _ => null
            };
        }
    }
}
