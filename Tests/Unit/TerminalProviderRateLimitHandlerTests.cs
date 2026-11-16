using System.Net;
using System.Net.Http.Headers;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Domain.Providers.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Verifies terminal provider-rate-limit classification and its ordering outside Polly's standard
/// resilience pipeline.
/// </summary>
[TestClass]
public class TerminalProviderRateLimitHandlerTests {
    /// <summary>MSTest context supplying cooperative cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>A terminal 429 preserves the provider's Retry-After delay and provider identity.</summary>
    [TestMethod]
    public async Task SendAsync_WhenFinalResponseIs429_ThrowsTypedRateLimit( ) {
        using HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 42 ) );
        using HttpMessageInvoker invoker = CreateInvoker( response, SupportedProviders.AppleMusic );
        using HttpRequestMessage request = new( HttpMethod.Get, "https://api.music.apple.com/v1/catalog/us/songs" );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => invoker.SendAsync( request, CancellationToken.None )
        );

        Assert.AreEqual( TimeSpan.FromSeconds( 42 ), exception.RetryAfterValue );
        Assert.AreEqual( SupportedProviders.AppleMusic, exception.Provider );
        Assert.AreEqual( request.RequestUri, exception.RequestUri );
    }

    /// <summary>A terminal 429 without Retry-After uses the conservative queue delay.</summary>
    [TestMethod]
    public async Task SendAsync_WhenRetryAfterIsMissing_UsesThirtySecondFallback( ) {
        using HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        using HttpMessageInvoker invoker = CreateInvoker( response, SupportedProviders.Tidal );
        using HttpRequestMessage request = new( HttpMethod.Get, "https://openapi.tidal.com/v2/tracks/123" );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => invoker.SendAsync( request, CancellationToken.None )
        );

        Assert.AreEqual( TimeSpan.FromSeconds( 30 ), exception.RetryAfterValue );
    }

    /// <summary>
    /// The production provider registration places terminal classification outside Polly: Polly
    /// performs all configured retries before the typed queue signal is raised.
    /// </summary>
    [TestMethod]
    public async Task SpotifyApiPipeline_RetriesWithPollyBeforeClassifyingTerminal429( ) {
        ServiceCollection services = new( );
        _ = services.AddLogging( );
        _ = services.ConfigureHttpClientDefaults( http => {
            _ = http.AddStandardResilienceHandler( options => {
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds( 1 );
                options.Retry.UseJitter = false;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds( 5 );
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds( 30 );
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds( 10 );
            } );
        } );

        HashSet<SupportedProviders> enabledProviders = [];
        _ = services.AddSpotifyServices( "client", "secret", enabledProviders );
        CountingRateLimitHandler primaryHandler = new( );
        _ = services.AddHttpClient( "spotify-api" )
            .ConfigurePrimaryHttpMessageHandler( ( ) => primaryHandler );

        using ServiceProvider provider = services.BuildServiceProvider( );
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>( ).CreateClient( "spotify-api" );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => client.GetAsync( "tracks/test", TestContext.CancellationToken )
        );

        Assert.AreEqual( 3, primaryHandler.CallCount, "One initial attempt plus two Polly retries were expected." );
        Assert.AreEqual( TimeSpan.FromSeconds( 30 ), exception.RetryAfterValue );
        Assert.AreEqual( SupportedProviders.Spotify, exception.Provider );
    }

    private static HttpMessageInvoker CreateInvoker(
        HttpResponseMessage response,
        SupportedProviders provider
    ) {
        TerminalProviderRateLimitHandler handler = new( provider ) {
            InnerHandler = new FixedResponseHandler( response )
        };
        return new HttpMessageInvoker( handler );
    }

    private sealed class FixedResponseHandler( HttpResponseMessage response ) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult( response );
    }

    private sealed class CountingRateLimitHandler : HttpMessageHandler {
        private int _callCount;

        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            _ = Interlocked.Increment( ref _callCount );
            return Task.FromResult( new HttpResponseMessage( HttpStatusCode.TooManyRequests ) {
                RequestMessage = request
            } );
        }
    }
}
