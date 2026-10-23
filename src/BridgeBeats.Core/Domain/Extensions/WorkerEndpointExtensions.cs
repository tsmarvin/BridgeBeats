using System.Globalization;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records.WorkerApi;
using BridgeBeats.Core.Infrastructure.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace BridgeBeats.Core.Domain.Extensions;

/// <summary>
/// Maps the worker-side HTTP server for the per-provider lookup contract. These endpoints are the
/// server counterpart to the <c>HttpMusicLookupService</c> proxy used by the web app: the proxy
/// posts to <c>/lookup/*</c>, a worker handles the request with a direct
/// <see cref="BridgeBeats.Contracts.Interfaces.IMusicLookupService"/>, and the result is returned in
/// a <see cref="BridgeBeats.Contracts.Records.WorkerApi.ProviderLookupResponse"/> envelope.
/// </summary>
public static partial class WorkerEndpointExtensions {

    /// <summary>
    /// Maps the six <c>/lookup/*</c> minimal-API routes (<c>url</c>, <c>isrc</c>, <c>upc</c>,
    /// <c>id</c>, <c>metadata</c>, <c>from-result</c>) onto a direct
    /// <typeparamref name="TService"/> resolved per request from the request services. Each route
    /// binds its request record, calls the matching lookup method, and wraps the outcome in a
    /// <see cref="BridgeBeats.Contracts.Records.WorkerApi.ProviderLookupResponse"/>.
    /// </summary>
    /// <typeparam name="TService">
    /// The concrete direct lookup service that handles the requests; resolved from request services
    /// on each call.
    /// </typeparam>
    /// <param name="app">The endpoint route builder to map the routes onto.</param>
    /// <returns>The same <paramref name="app"/>, to allow call chaining.</returns>
    /// <remarks>
    /// Every route returns HTTP 200 for success and no-match, or a sanitized status-bearing error.
    /// </remarks>
    public static IEndpointRouteBuilder MapProviderLookupEndpoints<TService>(
        this IEndpointRouteBuilder app
    ) where TService : class, IMusicLookupService {
        // Resolve service per-request instead of at startup to avoid lifecycle issues
        _ = app.MapPost( "/lookup/url", async ( LookupByUrlRequest request, HttpContext context ) => {
            TService service = context.RequestServices.GetRequiredService<TService>( );
            return await ExecuteLookupAsync( ( ) => service.GetInfoAsync( request.Url ), context );
        } );

        _ = app.MapPost( "/lookup/isrc", async ( LookupByIsrcRequest request, HttpContext context ) => {
            TService service = context.RequestServices.GetRequiredService<TService>( );
            return await ExecuteLookupAsync( ( ) => service.GetInfoByISRCAsync( request.Isrc ), context );
        } );

        _ = app.MapPost( "/lookup/upc", async ( LookupByUpcRequest request, HttpContext context ) => {
            TService service = context.RequestServices.GetRequiredService<TService>( );
            return await ExecuteLookupAsync( ( ) => service.GetInfoByUPCAsync( request.Upc ), context );
        } );

        _ = app.MapPost( "/lookup/id", async ( LookupByIdRequest request, HttpContext context ) => {
            TService service = context.RequestServices.GetRequiredService<TService>( );
            return await ExecuteLookupAsync( ( ) => service.GetInfoByIDAsync( request.ProviderId, request.IsAlbum ), context );
        } );

        _ = app.MapPost( "/lookup/metadata", async ( LookupByMetadataRequest request, HttpContext context ) => {
            TService service = context.RequestServices.GetRequiredService<TService>( );
            return await ExecuteLookupAsync( ( ) => service.GetInfoAsync( request.Title, request.Artist ), context );
        } );

        _ = app.MapPost( "/lookup/from-result", async ( LookupFromResultRequest request, HttpContext context ) => {
            TService service = context.RequestServices.GetRequiredService<TService>( );
            MusicLookupResult lookup = new( ) {
                Artist = request.Artist,
                Title = request.Title,
                ExternalId = request.ExternalId ?? string.Empty,
                IsAlbum = request.IsAlbum
            };
            return await ExecuteLookupAsync( ( ) => service.GetInfoAsync( lookup ), context );
        } );

        return app;
    }

    /// <summary>
    /// Runs a single lookup and returns the current status-bearing worker contract.
    /// </summary>
    /// <param name="lookupFunc">The lookup to invoke.</param>
    /// <param name="context">The current request, used for cancellation.</param>
    /// <returns>A successful envelope or a sanitized status-bearing error.</returns>
    private static async Task<IResult> ExecuteLookupAsync( Func<Task<MusicLookupResult?>> lookupFunc, HttpContext context ) {
        try {
            MusicLookupResult? result = await lookupFunc( );
            return Results.Ok( ProviderLookupResponse.Ok( result ) );
        } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException) {
            return Results.Json( ProviderLookupResponse.Error( "Provider lookup timed out." ), statusCode: StatusCodes.Status504GatewayTimeout );
        } catch (ProviderRateLimitException ex) {
            double retrySeconds = Math.Max( 0, ex.RetryAfterValue.TotalSeconds );
            int retryHeaderSeconds = retrySeconds >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Ceiling( retrySeconds );
            context.Response.Headers.RetryAfter = retryHeaderSeconds.ToString( CultureInfo.InvariantCulture );
            ProviderLookupResponse envelope = ex is RetryAfterExceededException exceeded
                ? ProviderLookupResponse.Error(
                    "Provider rate limit exceeded.",
                    retrySeconds,
                    Math.Max( 0, exceeded.Threshold.TotalSeconds ) )
                : ProviderLookupResponse.Error( "Provider rate limit exceeded.", retrySeconds );
            return Results.Json(
                envelope,
                statusCode: StatusCodes.Status429TooManyRequests );
        } catch (HttpRequestException) {
            return Results.Json( ProviderLookupResponse.Error( "Provider lookup failed." ), statusCode: StatusCodes.Status503ServiceUnavailable );
        } catch (IOException) {
            return Results.Json( ProviderLookupResponse.Error( "Provider lookup failed." ), statusCode: StatusCodes.Status503ServiceUnavailable );
        } catch (TimeoutException) {
            return Results.Json( ProviderLookupResponse.Error( "Provider lookup timed out." ), statusCode: StatusCodes.Status504GatewayTimeout );
        } catch (Exception ex) {
            ILogger<WorkerEndpointLogCategory> logger =
                context.RequestServices.GetRequiredService<ILogger<WorkerEndpointLogCategory>>( );
            LogUnexpectedProviderLookupFailure( logger, ex );
            return Results.Json( ProviderLookupResponse.Error( "Provider lookup failed." ), statusCode: StatusCodes.Status500InternalServerError );
        }
    }

    [LoggerMessage(
        EventId = LogEventIds.Providers.Common.UnexpectedWorkerLookupFailure,
        Level = LogLevel.Error,
        Message = "Unexpected provider lookup failure." )]
    private static partial void LogUnexpectedProviderLookupFailure( ILogger logger, Exception ex );
}

/// <summary>Typed logging category for worker HTTP endpoint execution.</summary>
internal sealed class WorkerEndpointLogCategory;
