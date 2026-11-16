using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;

namespace BridgeBeats.Core.Domain.Services.LinkResolver;

/// <summary>
/// Orchestrated implementation of <see cref="IMediaLinkService"/>. Delegates every lookup to
/// <see cref="ILookupOrchestrator"/> (the cache → dedup → saga → provider-queue → ATProto-storage
/// path), then translates the orchestrator's <see cref="LookupResult"/> into a
/// <see cref="MediaLinkResult"/>, attaching user-facing messages when the result is partial or
/// providers are rate-limited.
/// </summary>
/// <remarks>
/// The orchestrator handles Redis cache checking, request deduplication, queue-based provider
/// lookups, saga management for partial results, and ATProto storage for results. This is the
/// distributed counterpart to <see cref="DefaultMediaLinkService"/>, which resolves in-process
/// without a cache or saga. Exactly one of the two is registered, selected by the
/// <c>useCaching</c> flag at composition time.
/// </remarks>
/// <param name="orchestrator">The lookup orchestrator that performs the cached, queued resolution.</param>
/// <param name="logger">The logger for partial-result diagnostics.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="orchestrator"/> or <paramref name="logger"/> is null.</exception>
public sealed partial class CachingMediaLinkService(
    ILookupOrchestrator orchestrator,
    ILogger<CachingMediaLinkService> logger
) : IMediaLinkService {

    /// <summary>The orchestrator that performs the cached, deduplicated, queued lookup.</summary>
    private readonly ILookupOrchestrator _orchestrator = orchestrator
                                                       ?? throw new ArgumentNullException( nameof( orchestrator ) );
    /// <summary>The logger for partial-result diagnostics.</summary>
    private readonly ILogger<CachingMediaLinkService> _logger = logger
                                                              ?? throw new ArgumentNullException( nameof( logger ) );

    /// <summary>Resolves a track by title and artist through the orchestrator, attaching partial-result messages.</summary>
    /// <param name="title">The track or album title to search for.</param>
    /// <param name="artist">The artist name to search for.</param>
    /// <returns>The resolved result with any partial/rate-limited messages, or <see langword="null"/> when nothing is available.</returns>
    public async Task<MediaLinkResult?> GetInfoAsync( string title, string artist ) {
        LookupResult result = await _orchestrator.LookupByMetadataAsync( title, artist );
        return AddPartialMessage( result );
    }

    /// <summary>Resolves a track by ISRC through the orchestrator, attaching partial-result messages.</summary>
    /// <param name="isrc">The International Standard Recording Code identifying the track.</param>
    /// <returns>The resolved result with any partial/rate-limited messages, or <see langword="null"/> when nothing is available.</returns>
    public async Task<MediaLinkResult?> GetInfoByISRCAsync( string isrc ) {
        LookupResult result = await _orchestrator.LookupByIsrcAsync( isrc );
        return AddPartialMessage( result );
    }

    /// <summary>Resolves an album by UPC through the orchestrator, attaching partial-result messages.</summary>
    /// <param name="upc">The Universal Product Code identifying the album.</param>
    /// <returns>The resolved result with any partial/rate-limited messages, or <see langword="null"/> when nothing is available.</returns>
    public async Task<MediaLinkResult?> GetInfoByUPCAsync( string upc ) {
        LookupResult result = await _orchestrator.LookupByUpcAsync( upc );
        return AddPartialMessage( result );
    }

    /// <summary>Resolves an entity by a provider's own id through the orchestrator, attaching partial-result messages.</summary>
    /// <param name="providerId">The entity id within the originating provider.</param>
    /// <param name="provider">The provider that <paramref name="providerId"/> belongs to.</param>
    /// <param name="isAlbum"><see langword="true"/> when the id refers to an album; otherwise a track.</param>
    /// <returns>The resolved result with any partial/rate-limited messages, or <see langword="null"/> when nothing is available.</returns>
    public async Task<MediaLinkResult?> GetInfoByProviderIdAsync(
        string providerId,
        SupportedProviders provider,
        bool isAlbum
    ) {
        LookupResult result = await _orchestrator.LookupByProviderIdAsync( providerId, provider, isAlbum );
        return AddPartialMessage( result );
    }

    /// <summary>
    /// Resolves every supported link in free-text <paramref name="content"/> through the orchestrator,
    /// streaming each non-null result (with partial-result messages attached) as it is produced.
    /// </summary>
    /// <param name="content">Free text that may contain one or more provider links.</param>
    /// <returns>An async stream of resolved results, one per link the orchestrator produced a result for.</returns>
    public async IAsyncEnumerable<MediaLinkResult> GetInfoAsync( string content ) {
        await foreach (LookupResult result in _orchestrator.LookupByContentAsync( content )) {
            MediaLinkResult? mediaResult = AddPartialMessage( result );
            if (mediaResult is not null) {
                yield return mediaResult;
            }
        }
    }

    /// <summary>
    /// Translates an orchestrator <see cref="LookupResult"/> into a user-facing
    /// <see cref="MediaLinkResult"/>, attaching messages that explain a partial outcome. When the
    /// result has no payload but providers are rate-limited, a results-less placeholder carrying
    /// rate-limit messages is returned; when there is no payload and no rate limits, returns
    /// <see langword="null"/>. When a payload exists and the lookup is partial, it is annotated with
    /// either per-provider rate-limit messages or a generic "still fetching" note.
    /// </summary>
    /// <param name="lookupResult">The orchestrator outcome to translate.</param>
    /// <returns>The annotated result, or <see langword="null"/> when there is nothing to return.</returns>
    private MediaLinkResult? AddPartialMessage( LookupResult lookupResult ) {
        if (lookupResult.Result is null) {
            // If rate-limited with no data, create a lightweight result carrying only messages
            if (lookupResult.IsPartial && lookupResult.RateLimitedProviders is { Count: > 0 }) {
                MediaLinkResult rateLimitResult = new( ) {
                    Messages = [],
                    IsPartial = true,
                    RateLimitedProviders = [.. lookupResult.RateLimitedProviders.Select( r => r.Provider )]
                };

                foreach (ProviderRateLimitInfo rateLimitInfo in lookupResult.RateLimitedProviders) {
                    string message = $"{rateLimitInfo.Provider} is temporarily rate-limited. Results will be available shortly (retry after {rateLimitInfo.RetryAfter:u}).";
                    rateLimitResult.Messages.Add( message );

                    LogPartialResultReturned( _logger, lookupResult.SagaId ?? string.Empty, rateLimitInfo.Provider, rateLimitInfo.RetryAfter );
                }

                return rateLimitResult;
            }

            return null;
        }

        if (lookupResult.IsPartial) {
            // IsPartial is API-only response metadata. ATProtoStorageService deliberately omits
            // it when mapping MediaLinkResult to the persisted PDS record.
            lookupResult.Result.IsPartial = true;
            lookupResult.Result.Messages ??= [];

            if (lookupResult.RateLimitedProviders is { Count: > 0 }) {
                foreach (ProviderRateLimitInfo rateLimitInfo in lookupResult.RateLimitedProviders) {
                    string message = $"{rateLimitInfo.Provider} is temporarily unavailable. Results will be updated when available (retry after {rateLimitInfo.RetryAfter:u}).";
                    lookupResult.Result.Messages.Add( message );

                    LogPartialResultReturned( _logger, lookupResult.SagaId ?? string.Empty, rateLimitInfo.Provider, rateLimitInfo.RetryAfter );
                }
            } else {
                // Partial because secondary provider lookups are still pending
                lookupResult.Result.Messages.Add( "Additional provider results are still being fetched. Look up this link again shortly for the complete set of links." );

                LogPendingPartialReturned( _logger, lookupResult.SagaId ?? string.Empty );
            }
        }

        return lookupResult.Result;
    }

    #region LoggerMessage Definitions

    /// <summary>Logs (Information) that a partial result was returned because a provider is rate-limited.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga the partial result belongs to.</param>
    /// <param name="provider">The rate-limited provider.</param>
    /// <param name="retryAfter">The instant after which the provider may be retried.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.PartialResultReturned,
        Level = LogLevel.Information,
        Message = "Partial result returned for saga {SagaId}. {Provider} rate-limited until {RetryAfter}" )]
    private static partial void LogPartialResultReturned( ILogger logger, string sagaId, SupportedProviders provider, DateTimeOffset retryAfter );

    /// <summary>Logs (Information) that a partial result was returned while secondary provider lookups are still pending.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga the partial result belongs to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.PendingPartialResultReturned,
        Level = LogLevel.Information,
        Message = "Partial result returned for saga {SagaId} with secondary provider lookups still pending" )]
    private static partial void LogPendingPartialReturned( ILogger logger, string sagaId );

    #endregion LoggerMessage Definitions
}
