using System.Net.Http.Json;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records.WorkerApi;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Domain.Providers.Common;

/// <summary>
/// Thin <see cref="IMusicLookupService"/> implementation that forwards every lookup to a remote
/// provider worker over HTTP rather than calling the provider's own API.
/// </summary>
/// <remarks>
/// This is the proxy half of the two <see cref="IMusicLookupService"/> implementation families. It
/// posts each request to a named worker (one of the <c>/lookup/*</c> routes) and unwraps the worker's
/// <see cref="ProviderLookupResponse"/> envelope. Status-bearing responses are mapped to typed
/// exceptions while a successful no-match envelope maps to <see langword="null"/>.
/// Direct, in-process provider services derive instead from <see cref="MusicLookupServiceBase"/>.
/// </remarks>
/// <param name="provider">The provider this proxy represents, surfaced through <see cref="Provider"/>.</param>
/// <param name="httpClientFactory">Factory used to create the named worker HTTP client.</param>
/// <param name="httpClientName">Name of the configured worker client to resolve from <paramref name="httpClientFactory"/>.</param>
/// <param name="logger">Logger for worker communication diagnostics.</param>
public partial class HttpMusicLookupService(
    SupportedProviders provider,
    IHttpClientFactory httpClientFactory,
    string httpClientName,
    ILogger<HttpMusicLookupService> logger
) : IMusicLookupService {

    /// <summary>Gets the provider this proxy forwards requests for.</summary>
    public SupportedProviders Provider => provider;

    /// <summary>Resolves a track or album by its provider URL via the worker's <c>/lookup/url</c> route.</summary>
    /// <param name="uri">The provider URL to resolve.</param>
    /// <returns>The resolved result, or <see langword="null"/> if the worker found nothing or reported an error.</returns>
    /// <exception cref="HttpRequestException">Thrown (after logging) when the worker call fails at the transport level.</exception>
    public async Task<MusicLookupResult?> GetInfoAsync( string uri ) {
        LookupByUrlRequest request = new( uri );
        return await PostAsync( "/lookup/url", request );
    }

    /// <summary>Resolves a track by ISRC via the worker's <c>/lookup/isrc</c> route.</summary>
    /// <param name="isrc">The International Standard Recording Code to resolve.</param>
    /// <returns>The resolved result, or <see langword="null"/> if the worker found nothing or reported an error.</returns>
    /// <exception cref="HttpRequestException">Thrown (after logging) when the worker call fails at the transport level.</exception>
    public async Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc ) {
        LookupByIsrcRequest request = new( isrc );
        return await PostAsync( "/lookup/isrc", request );
    }

    /// <summary>Resolves an album by UPC via the worker's <c>/lookup/upc</c> route.</summary>
    /// <param name="upc">The Universal Product Code to resolve.</param>
    /// <returns>The resolved result, or <see langword="null"/> if the worker found nothing or reported an error.</returns>
    /// <exception cref="HttpRequestException">Thrown (after logging) when the worker call fails at the transport level.</exception>
    public async Task<MusicLookupResult?> GetInfoByUPCAsync( string upc ) {
        LookupByUpcRequest request = new( upc );
        return await PostAsync( "/lookup/upc", request );
    }

    /// <summary>Resolves an entity by its provider id via the worker's <c>/lookup/id</c> route.</summary>
    /// <param name="providerId">The provider-native entity id.</param>
    /// <param name="isAlbum"><see langword="true"/> to resolve an album, <see langword="false"/> for a track.</param>
    /// <returns>The resolved result, or <see langword="null"/> if the worker found nothing or reported an error.</returns>
    /// <exception cref="HttpRequestException">Thrown (after logging) when the worker call fails at the transport level.</exception>
    public async Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum ) {
        LookupByIdRequest request = new( providerId, isAlbum );
        return await PostAsync( "/lookup/id", request );
    }

    /// <summary>Resolves an entity by title and artist via the worker's <c>/lookup/metadata</c> route.</summary>
    /// <param name="title">The track or album title to match.</param>
    /// <param name="artist">The artist name to match.</param>
    /// <returns>The resolved result, or <see langword="null"/> if the worker found nothing or reported an error.</returns>
    /// <exception cref="HttpRequestException">Thrown (after logging) when the worker call fails at the transport level.</exception>
    public async Task<MusicLookupResult?> GetInfoAsync( string title, string artist ) {
        LookupByMetadataRequest request = new( title, artist );
        return await PostAsync( "/lookup/metadata", request );
    }

    /// <summary>
    /// Re-resolves an existing result on this provider via the worker's <c>/lookup/from-result</c> route,
    /// used to back-fill a provider that is missing from a combined result.
    /// </summary>
    /// <param name="lookup">The already-resolved result whose artist, title, optional external id, and album flag drive the lookup.</param>
    /// <returns>The resolved result, or <see langword="null"/> if the worker found nothing or reported an error.</returns>
    /// <exception cref="HttpRequestException">Thrown (after logging) when the worker call fails at the transport level.</exception>
    public async Task<MusicLookupResult?> GetInfoAsync( MusicLookupResult lookup ) {
        LookupFromResultRequest request = new(
            lookup.Artist,
            lookup.Title,
            string.IsNullOrWhiteSpace( lookup.ExternalId ) ? null : lookup.ExternalId,
            lookup.IsAlbum
        );
        return await PostAsync( "/lookup/from-result", request );
    }

    /// <summary>
    /// Posts a typed request to a worker route and unwraps the <see cref="ProviderLookupResponse"/> envelope.
    /// </summary>
    /// <typeparam name="TRequest">The worker request record type being serialized.</typeparam>
    /// <param name="endpoint">The worker route (for example <c>/lookup/url</c>) to post to.</param>
    /// <param name="request">The request payload to serialize as JSON.</param>
    /// <returns>
    /// The <see cref="ProviderLookupResponse.Result"/> on success; <see langword="null"/> for an empty
    /// response body or the worker's successful no-match envelope.
    /// </returns>
    /// <exception cref="ProviderRateLimitException">Thrown for a worker 429 response with a valid retry delay.</exception>
    /// <exception cref="HttpRequestException">Thrown (after logging) for a worker error status, an error envelope,
    /// an invalid success body, or a transport-level failure.</exception>
    private async Task<MusicLookupResult?> PostAsync<TRequest>( string endpoint, TRequest request ) {
        try {
            using HttpClient client = httpClientFactory.CreateClient( httpClientName );
            using HttpResponseMessage response = await client.PostAsJsonAsync( endpoint, request );

            ProviderLookupResponse? result = null;
            try {
                result = await response.Content.ReadFromJsonAsync<ProviderLookupResponse>( );
            } catch (JsonException) when (!response.IsSuccessStatusCode) {
                // Status-bearing failures do not require a response envelope.
                LogWorkerHttpError( logger, provider, (int)response.StatusCode, endpoint );
            } catch (JsonException ex) {
                throw new HttpRequestException( "Provider worker returned an invalid response.", ex, response.StatusCode );
            }

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                // The envelope preserves sub-second precision; Retry-After is integer-rounded for
                // HTTP interoperability and is only a fallback for non-envelope responses.
                double? retryAfter = result?.RetryAfterSeconds ?? ParseRetryAfterSeconds( response );
                double? threshold = result?.RetryThresholdSeconds;
                if (!retryAfter.HasValue || !IsValidRetrySeconds( retryAfter.Value )) {
                    throw new HttpRequestException( "Worker rate-limit response was missing retry metadata.", null, response.StatusCode );
                }
                TimeSpan retryDelay = TimeSpan.FromSeconds( retryAfter.Value );
                if (threshold.HasValue && !IsValidRetrySeconds( threshold.Value )) {
                    throw new HttpRequestException( "Worker rate-limit response contained invalid threshold metadata.", null, response.StatusCode );
                }
                if (threshold is >= 0 && retryAfter.Value > threshold.Value) {
                    throw new RetryAfterExceededException( retryDelay, TimeSpan.FromSeconds( threshold.Value ), null, provider );
                }
                throw new ProviderRateLimitException( retryDelay, null, provider );
            }

            if (!response.IsSuccessStatusCode) {
                LogWorkerHttpError( logger, provider, (int)response.StatusCode, endpoint );
                throw new HttpRequestException( "Provider worker request failed.", null, response.StatusCode );
            }

            if (result == null) {
                LogWorkerNullResponse( logger, provider, endpoint );
                return null;
            }

            if (!result.Success) {
                const string Sanitized = "Provider worker reported a lookup failure.";
                LogWorkerError( logger, provider, endpoint, Sanitized );
                throw new HttpRequestException( Sanitized, null, System.Net.HttpStatusCode.ServiceUnavailable );
            }

            return result.Result;
        } catch (ProviderRateLimitException) {
            throw;
        } catch (HttpRequestException ex) {
            LogHttpRequestError( logger, ex, provider, endpoint );
            throw; // Re-throw to let the caller handle worker unavailability
        } catch (Exception ex) {
            LogUnexpectedError( logger, ex, provider, endpoint );
            throw;
        }
    }

    private static double? ParseRetryAfterSeconds( HttpResponseMessage response ) {
        if (!response.Headers.TryGetValues( "Retry-After", out IEnumerable<string>? values )) {
            return null;
        }

        string? value = values.FirstOrDefault( );
        if (double.TryParse( value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds )
            && double.IsFinite( seconds )
            && seconds >= 0
            && seconds <= TimeSpan.MaxValue.TotalSeconds) {
            return seconds;
        }

        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTimeOffset date )) {
            return Math.Max( 0, (date - DateTimeOffset.UtcNow).TotalSeconds );
        }

        return null;
    }

    private static bool IsValidRetrySeconds( double seconds )
        => double.IsFinite( seconds ) && seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds;

    #region LoggerMessage Methods

    /// <summary>Logs a warning when a worker returns an unsuccessful HTTP status code.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider whose worker was called.</param>
    /// <param name="statusCode">The HTTP status code returned by the worker.</param>
    /// <param name="endpoint">The worker route that was called.</param>
    [LoggerMessage(
        EventId = LogEventIds.Providers.Common.WorkerHttpError,
        Level = LogLevel.Warning,
        Message = "Worker {Provider} returned HTTP {StatusCode} for {Endpoint}" )]
    internal static partial void LogWorkerHttpError( ILogger logger, SupportedProviders provider, int statusCode, string endpoint );

    /// <summary>Logs a warning when a worker returns a body that deserializes to a null envelope.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider whose worker was called.</param>
    /// <param name="endpoint">The worker route that was called.</param>
    [LoggerMessage(
        EventId = LogEventIds.Providers.Common.WorkerNullResponse,
        Level = LogLevel.Warning,
        Message = "Worker {Provider} returned null response for {Endpoint}" )]
    internal static partial void LogWorkerNullResponse( ILogger logger, SupportedProviders provider, string endpoint );

    /// <summary>Logs a warning when a worker envelope reports an error message.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider whose worker was called.</param>
    /// <param name="endpoint">The worker route that was called.</param>
    /// <param name="errorMessage">The error message carried in the worker envelope.</param>
    [LoggerMessage(
        EventId = LogEventIds.Providers.Common.WorkerError,
        Level = LogLevel.Warning,
        Message = "Worker {Provider} returned error for {Endpoint}: {ErrorMessage}" )]
    internal static partial void LogWorkerError( ILogger logger, SupportedProviders provider, string endpoint, string errorMessage );

    /// <summary>Logs a transport-level HTTP failure while communicating with a worker.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="provider">The provider whose worker was called.</param>
    /// <param name="endpoint">The worker route that was called.</param>
    [LoggerMessage(
        EventId = LogEventIds.Providers.Common.HttpRequestError,
        Level = LogLevel.Error,
        Message = "HTTP error communicating with {Provider} worker at {Endpoint}" )]
    internal static partial void LogHttpRequestError( ILogger logger, Exception ex, SupportedProviders provider, string endpoint );

    /// <summary>Logs an unexpected (non-HTTP) failure while communicating with a worker.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that occurred.</param>
    /// <param name="provider">The provider whose worker was called.</param>
    /// <param name="endpoint">The worker route that was called.</param>
    [LoggerMessage(
        EventId = LogEventIds.Providers.Common.UnexpectedError,
        Level = LogLevel.Error,
        Message = "Unexpected error communicating with {Provider} worker at {Endpoint}" )]
    internal static partial void LogUnexpectedError( ILogger logger, Exception ex, SupportedProviders provider, string endpoint );

    #endregion LoggerMessage Methods
}
