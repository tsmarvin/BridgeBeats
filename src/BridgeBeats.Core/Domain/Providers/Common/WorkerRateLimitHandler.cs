using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Contracts.Records.WorkerApi;

namespace BridgeBeats.Core.Domain.Providers.Common;

/// <summary>
/// Converts worker-proxy HTTP 429 envelopes into the typed provider signal excluded by the shared
/// resilience pipeline. It deliberately does not publish to <c>IRateLimitTracker</c>: the direct
/// provider handler inside the worker is the single writer for the shared cooldown window.
/// </summary>
internal sealed class WorkerRateLimitHandler(
    SupportedProviders provider,
    QueueSettings queueSettings
) : DelegatingHandler {
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        HttpResponseMessage response = await base.SendAsync( request, cancellationToken );
        if (response.StatusCode != HttpStatusCode.TooManyRequests) {
            return response;
        }

        try {
            ProviderLookupResponse? envelope = null;
            try {
                envelope = await response.Content.ReadFromJsonAsync<ProviderLookupResponse>( cancellationToken );
            } catch (JsonException) {
                // Retry-After remains a valid fallback for workers that cannot serialize an envelope.
            } catch (NotSupportedException) {
                // Treat an unsupported error content type the same as an absent envelope.
            }

            TimeSpan observedRetryAfter = TryGetDuration( envelope?.RetryAfterSeconds, out TimeSpan envelopeRetryAfter )
                ? envelopeRetryAfter
                : RetryAfterLimitHandler.GetRetryAfterValue( response )
                    ?? queueSettings.RateLimitDefaultRetryAfter;
            TimeSpan retryAfter = queueSettings.ClampRateLimitRetryAfter( observedRetryAfter );

            string? endpoint = envelope?.RateLimitedEndpoint;
            if (TryGetDuration( envelope?.RetryThresholdSeconds, out TimeSpan threshold )
                && observedRetryAfter > threshold) {
                throw new RetryAfterExceededException(
                    retryAfter,
                    threshold,
                    request.RequestUri,
                    provider,
                    endpoint );
            }

            throw new ProviderRateLimitException(
                retryAfter,
                request.RequestUri,
                provider,
                endpoint );
        } finally {
            response.Dispose( );
        }
    }

    private static bool TryGetDuration( double? seconds, out TimeSpan duration ) {
        duration = default;
        if (!seconds.HasValue || !double.IsFinite( seconds.Value ) || seconds.Value < 0) {
            return false;
        }

        // TimeSpan.MaxValue.TotalSeconds is rounded as a double and can itself overflow when passed
        // back to FromSeconds. Saturate before conversion so malformed or hostile worker envelopes
        // still produce the typed rate-limit signal this handler promises.
        double maxConvertibleSeconds = Math.Floor( TimeSpan.MaxValue.TotalSeconds );
        duration = seconds.Value >= maxConvertibleSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds( seconds.Value );
        return true;
    }
}
