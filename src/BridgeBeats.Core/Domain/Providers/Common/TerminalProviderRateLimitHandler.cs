using System.Net;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;

namespace BridgeBeats.Core.Domain.Providers.Common;

/// <summary>
/// Classifies an HTTP 429 that remains after the inner resilience pipeline has exhausted its retry
/// policy, translating it into the typed signal consumed by the provider queue.
/// </summary>
/// <remarks>
/// Register this handler as the outermost provider-API handler. Polly remains responsible for all
/// retry attempts, backoff, jitter, timeouts, and circuit breaking; this handler observes only the
/// terminal response returned by that pipeline.
/// </remarks>
/// <param name="provider">The provider represented by the named HTTP client.</param>
internal sealed class TerminalProviderRateLimitHandler( SupportedProviders provider ) : DelegatingHandler {
    private static readonly TimeSpan s_defaultRetryAfter = TimeSpan.FromSeconds( 30 );

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        HttpResponseMessage response = await base.SendAsync( request, cancellationToken );
        if (response.StatusCode != HttpStatusCode.TooManyRequests) {
            return response;
        }

        TimeSpan retryAfter = RetryAfterLimitHandler.GetRetryAfterValue( response ) ?? s_defaultRetryAfter;
        response.Dispose( );
        throw new ProviderRateLimitException( retryAfter, request.RequestUri, provider );
    }
}
