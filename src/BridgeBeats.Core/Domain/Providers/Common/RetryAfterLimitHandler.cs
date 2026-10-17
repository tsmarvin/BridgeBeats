using System.Net;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Domain.Providers.Common {
    /// <summary>
    /// <see cref="DelegatingHandler"/> that converts provider backpressure into the typed signal
    /// consumed by the durable queue before Polly retries it or counts it toward a circuit breaker.
    /// </summary>
    /// <remarks>
    /// On <see cref="System.Net.HttpStatusCode.TooManyRequests"/>, the handler reads the
    /// <c>Retry-After</c> header in both delta-seconds and HTTP-date forms. When the wait exceeds
    /// <paramref name="maxRetryAfterSeconds"/> it throws <see cref="RetryAfterExceededException"/>;
    /// otherwise it throws <see cref="ProviderRateLimitException"/>. When a shared tracker is
    /// supplied, it also rejects requests covered by an existing distributed cooldown before
    /// provider I/O and is the sole writer of newly observed provider cooldowns. Tracker failures
    /// are logged and deliberately fail open so Redis availability cannot suppress provider calls.
    /// Register this handler inside the resilience handler so provider backpressure bypasses
    /// in-process retries and circuits.
    /// </remarks>
    /// <param name="maxRetryAfterSeconds">The maximum tolerable <c>Retry-After</c> wait, in seconds, before failing fast.</param>
    /// <param name="queueSettings">Configured fallback and minimum durable deferral windows.</param>
    /// <param name="logger">Logger used to record fail-fast decisions.</param>
    /// <param name="rateLimitTracker">Optional shared tracker used to gate and publish endpoint cooldowns.</param>
    public partial class RetryAfterLimitHandler(
        int maxRetryAfterSeconds,
        QueueSettings queueSettings,
        ILogger<RetryAfterLimitHandler> logger,
        IRateLimitTracker? rateLimitTracker = null
    ) : DelegatingHandler {
        private readonly TimeSpan _maxRetryAfter = TimeSpan.FromSeconds( maxRetryAfterSeconds );
        private readonly QueueSettings _queueSettings = queueSettings
            ?? throw new ArgumentNullException( nameof( queueSettings ) );

        /// <summary>
        /// Sends the request and converts provider backpressure into a durable-queue deferral.
        /// </summary>
        /// <param name="request">The outbound request.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The response from the inner handler when it does not represent provider backpressure.</returns>
        /// <exception cref="RetryAfterExceededException">
        /// Thrown when the response is HTTP 429 and its <c>Retry-After</c> exceeds the configured threshold.
        /// </exception>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            Uri? requestUri = request.RequestUri;
            Contracts.Enums.SupportedProviders? provider = RetryAfterExceededException.DetermineProviderFromUri( requestUri );
            string endpoint = ProviderRateLimitEndpoint.FromRequest( provider, requestUri );

            if (rateLimitTracker is not null && provider is not null) {
                try {
                    IReadOnlyList<RateLimitedEndpoint> activeLimits =
                        await rateLimitTracker.GetAllRateLimitedAsync( provider.Value, cancellationToken );
                    RateLimitedEndpoint? activeLimit = activeLimits
                        .Where( limit => ProviderRateLimitPolicy.Covers(
                            provider.Value,
                            limit.Endpoint,
                            endpoint ) )
                        .OrderByDescending( limit => limit.RetryAfter )
                        .FirstOrDefault( );

                    if (activeLimit is not null) {
                        TimeSpan remaining = activeLimit.RetryAfter - DateTimeOffset.UtcNow;
                        if (remaining > TimeSpan.Zero) {
                            throw new ProviderRateLimitException( remaining, requestUri, provider, endpoint );
                        }
                    }
                } catch (ProviderRateLimitException) {
                    throw;
                } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    LogRateLimitTrackerUnavailable( logger, ex, provider.Value.ToString( ), endpoint, "read" );
                }
            }

            HttpResponseMessage response = await base.SendAsync( request, cancellationToken );

            if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                TimeSpan observedRetryAfter = GetRetryAfterValue( response )
                    ?? _queueSettings.RateLimitDefaultRetryAfter;
                TimeSpan retryAfter = _queueSettings.ClampRateLimitRetryAfter( observedRetryAfter );
                response.Dispose( );

                ProviderRateLimitException rateLimitException;
                if (observedRetryAfter > _maxRetryAfter) {
                    LogRateLimitExceeded(
                        logger,
                        provider?.ToString( ) ?? "Unknown",
                        observedRetryAfter.TotalSeconds,
                        _maxRetryAfter.TotalSeconds,
                        requestUri
                    );

                    rateLimitException = new RetryAfterExceededException(
                        retryAfter,
                        _maxRetryAfter,
                        requestUri,
                        provider,
                        endpoint
                    );
                } else {
                    rateLimitException = new ProviderRateLimitException( retryAfter, requestUri, provider, endpoint );
                }

                if (rateLimitTracker is not null && provider is not null) {
                    try {
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        TimeSpan boundedRetry = TimeSpan.FromTicks(
                            Math.Min( retryAfter.Ticks, (DateTimeOffset.MaxValue - now).Ticks ) );
                        await rateLimitTracker.SetRateLimitedAsync(
                            provider.Value,
                            endpoint,
                            now.Add( boundedRetry ),
                            cancellationToken );
                    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                        // During host shutdown the pending delivery remains recoverable. Do not
                        // publish a cooldown with a cancelled token or replace cancellation with
                        // the typed rate-limit exception.
                        throw;
                    } catch (Exception ex) {
                        LogRateLimitTrackerUnavailable( logger, ex, provider.Value.ToString( ), endpoint, "write" );
                    }
                }

                throw rateLimitException;
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

        [LoggerMessage(
            EventId = LogEventIds.Providers.Common.RateLimitTrackerUnavailable,
            Level = LogLevel.Warning,
            Message = "Rate-limit tracker {Operation} failed for {Provider} endpoint {Endpoint}; continuing with typed provider handling." )]
        internal static partial void LogRateLimitTrackerUnavailable(
            ILogger logger,
            Exception exception,
            string provider,
            string endpoint,
            string operation );

        #endregion LoggerMessage Methods
    }
}
