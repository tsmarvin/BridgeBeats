using System.Net;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Domain.Providers.Common {
    /// <summary>
    /// <see cref="DelegatingHandler"/> that fails fast when a provider's <c>Retry-After</c> on an HTTP 429
    /// response exceeds a configured threshold, instead of waiting out a long rate-limit window.
    /// </summary>
    /// <remarks>
    /// On <see cref="System.Net.HttpStatusCode.TooManyRequests"/>, the handler reads the
    /// <c>Retry-After</c> header in both delta-seconds and HTTP-date forms. When the wait exceeds
    /// <paramref name="maxRetryAfterSeconds"/> it throws <see cref="RetryAfterExceededException"/>;
    /// otherwise the original response flows through unchanged. Register this handler inside the
    /// resilience handler so every Polly attempt can fail fast when the requested wait is excessive.
    /// </remarks>
    /// <param name="maxRetryAfterSeconds">The maximum tolerable <c>Retry-After</c> wait, in seconds, before failing fast.</param>
    /// <param name="logger">Logger used to record fail-fast decisions.</param>
    public partial class RetryAfterLimitHandler(
        int maxRetryAfterSeconds,
        ILogger<RetryAfterLimitHandler> logger
    ) : DelegatingHandler {
        private readonly TimeSpan _maxRetryAfter = TimeSpan.FromSeconds( maxRetryAfterSeconds );

        /// <summary>
        /// Sends the request and inspects the response for a rate-limit that exceeds the threshold.
        /// </summary>
        /// <param name="request">The outbound request.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The response from the inner handler when the rate limit is within the threshold.</returns>
        /// <exception cref="RetryAfterExceededException">
        /// Thrown when the response is HTTP 429 and its <c>Retry-After</c> exceeds the configured threshold.
        /// </exception>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            HttpResponseMessage response = await base.SendAsync( request, cancellationToken );

            if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                TimeSpan? retryAfter = GetRetryAfterValue( response );

                if (retryAfter.HasValue && retryAfter.Value > _maxRetryAfter) {
                    Uri? requestUri = request.RequestUri;
                    Contracts.Enums.SupportedProviders? provider = RetryAfterExceededException.DetermineProviderFromUri( requestUri );

                    LogRateLimitExceeded(
                        logger,
                        provider?.ToString( ) ?? "Unknown",
                        retryAfter.Value.TotalSeconds,
                        _maxRetryAfter.TotalSeconds,
                        requestUri
                    );

                    throw new RetryAfterExceededException(
                        retryAfter.Value,
                        _maxRetryAfter,
                        requestUri,
                        provider
                    );
                }
            }

            return response;
        }

        /// <summary>
        /// Reads the <c>Retry-After</c> wait from a response, supporting both delta-seconds and
        /// HTTP-date forms.
        /// </summary>
        /// <param name="response">The HTTP response to inspect.</param>
        /// <returns>
        /// The wait duration; <see cref="TimeSpan.Zero"/> when a date-form value is already in the past;
        /// or <see langword="null"/> when no <c>Retry-After</c> header is present.
        /// </returns>
        internal static TimeSpan? GetRetryAfterValue( HttpResponseMessage response ) {
            // Check for delta seconds (e.g., "Retry-After: 120")
            if (response.Headers.RetryAfter?.Delta.HasValue == true) {
                return response.Headers.RetryAfter.Delta;
            }

            // Check for HTTP-date format (e.g., "Retry-After: Wed, 21 Oct 2015 07:28:00 GMT")
            if (response.Headers.RetryAfter?.Date.HasValue == true) {
                DateTimeOffset retryDate = response.Headers.RetryAfter.Date.Value;
                TimeSpan delta = retryDate - DateTimeOffset.UtcNow;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            return null;
        }

        #region LoggerMessage Methods

        /// <summary>Logs that a rate-limit wait exceeded the threshold and the request will fail fast.</summary>
        /// <param name="logger">The logger to write to.</param>
        /// <param name="provider">The provider name, or <c>Unknown</c>.</param>
        /// <param name="retryAfterSeconds">The provider's requested wait, in seconds.</param>
        /// <param name="thresholdSeconds">The configured maximum wait, in seconds.</param>
        /// <param name="requestUri">The rate-limited request URI, if known.</param>
        [LoggerMessage(
            EventId = LogEventIds.Providers.Common.RateLimitExceeded,
            Level = LogLevel.Warning,
            Message = "Rate limit exceeded threshold for {Provider}. Retry-After: {RetryAfterSeconds}s, Threshold: {ThresholdSeconds}s. Request URI: {RequestUri}. Failing fast instead of waiting." )]
        internal static partial void LogRateLimitExceeded( ILogger logger, string provider, double retryAfterSeconds, double thresholdSeconds, Uri? requestUri );

        #endregion LoggerMessage Methods
    }
}
