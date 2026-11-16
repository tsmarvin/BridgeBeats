using System.Text.RegularExpressions;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Infrastructure.Logging;
using BridgeBeats.Core.Infrastructure.Utilities;

namespace BridgeBeats.Services.LinkResolver;

/// <summary>
/// Coordinates the distributed lookup path: cache check, request deduplication, saga creation,
/// provider-queue enqueue, and ATProto-backed result retrieval. This is the orchestrated counterpart
/// to the in-process resolver; the web app drives it through <see cref="ILookupOrchestrator"/> and the
/// per-provider <see cref="BridgeBeats.Core.Domain.Services.Queue.QueueProcessorBackgroundService"/>
/// workers fulfill the queued requests, communicating back through the shared saga state.
/// </summary>
/// <remarks>
/// Every entry point funnels through a single private flow. Per lookup the orchestrator: checks
/// the cache and returns a fresh hit immediately; acquires a deduplication lock, or — if a
/// request is already in flight — reads the in-flight saga's stored result or waits on the dedup
/// signal; gets or creates a saga and initializes provider states (all enabled providers for
/// metadata/ISRC/UPC, or just the originating provider for URL/provider-id lookups); enqueues the
/// first provider's request at interactive priority; waits up to the wait budget for completion,
/// honoring the rate-limited sentinel; and resolves a final or partial result by reading the saga
/// and fetching the stored <see cref="MediaLinkResult"/> from ATProto storage. A partial result is
/// returned only when a provider is rate-limited or the interactive time budget is exhausted. The
/// orchestrator never mutates provider results itself — the workers do.
/// </remarks>
/// <param name="cache">Repository checked first for a fresh cached result.</param>
/// <param name="deduplicator">Coordinates concurrent identical lookups via an acquire/wait/signal protocol.</param>
/// <param name="sagaManager">Creates and reads the durable saga that tracks the lookup across providers.</param>
/// <param name="queueResolver">Resolves the per-provider request queue to enqueue work onto.</param>
/// <param name="atProtoStorage">Reads stored partial/final results by their ATProto record URI.</param>
/// <param name="enabledProviders">The set of providers a metadata/ISRC/UPC lookup fans out to.</param>
/// <param name="logger">The logger for orchestration diagnostics.</param>
/// <param name="settings">Queue settings supplying the interactive wait timeout; defaults are used when null.</param>
public sealed partial class LookupOrchestrator(
    IMediaLinkCacheRepository cache,
    IRequestDeduplicator deduplicator,
    ISagaStateManager sagaManager,
    IProviderQueueResolver<QueuedLookupRequest> queueResolver,
    IATProtoStorageService atProtoStorage,
    HashSet<SupportedProviders> enabledProviders,
    ILogger<LookupOrchestrator> logger,
    QueueSettings? settings = null
) : ILookupOrchestrator {
    /// <summary>Repository checked first for a fresh cached result.</summary>
    private readonly IMediaLinkCacheRepository _cache = cache
                                                        ?? throw new ArgumentNullException( nameof( cache ) );
    /// <summary>Coordinates concurrent identical lookups via an acquire/wait/signal protocol.</summary>
    private readonly IRequestDeduplicator _deduplicator = deduplicator
                                                        ?? throw new ArgumentNullException( nameof( deduplicator ) );
    /// <summary>Creates and reads the durable saga tracking the lookup across providers.</summary>
    private readonly ISagaStateManager _sagaManager = sagaManager
                                                    ?? throw new ArgumentNullException( nameof( sagaManager ) );
    /// <summary>Resolves the per-provider request queue to enqueue work onto.</summary>
    private readonly IProviderQueueResolver<QueuedLookupRequest> _queueResolver = queueResolver
                                                                                ?? throw new ArgumentNullException( nameof( queueResolver ) );
    /// <summary>Reads stored partial/final results by their ATProto record URI.</summary>
    private readonly IATProtoStorageService _atProtoStorage = atProtoStorage
                                                            ?? throw new ArgumentNullException( nameof( atProtoStorage ) );
    /// <summary>The set of providers a metadata/ISRC/UPC lookup fans out to.</summary>
    private readonly HashSet<SupportedProviders> _enabledProviders = enabledProviders
                                                                   ?? throw new ArgumentNullException( nameof( enabledProviders ) );
    /// <summary>The logger for orchestration diagnostics.</summary>
    private readonly ILogger<LookupOrchestrator> _logger = logger
                                                         ?? throw new ArgumentNullException( nameof( logger ) );

    /// <summary>
    /// The default per-lookup interactive wait timeout, from
    /// <see cref="QueueSettings.InteractiveWaitSeconds"/>. Bounds how long a single lookup waits for
    /// completion before returning a partial.
    /// </summary>
    private readonly TimeSpan _interactiveWaitTimeout = TimeSpan.FromSeconds( (settings ?? new QueueSettings( )).InteractiveWaitSeconds );

    /// <summary>
    /// The deduplication lock TTL: how long an acquired lookup holds the dedup lock before it is
    /// considered abandoned. Hard-coded at 5 minutes.
    /// </summary>
    private static readonly TimeSpan s_deduplicationLockDuration = TimeSpan.FromMinutes( 5 );

    /// <summary>
    /// Target wait budget across all links of a single content lookup, hard-coded at 90 seconds. It
    /// is divided evenly per link (and never below <see cref="s_minPerLinkWaitBudget"/>), so a
    /// message with many links does not wait the full interactive timeout on each. The budget is
    /// kept under the global 120s resilience AttemptTimeout so the server-side budget, not the
    /// transport, is the binding constraint (the Discord worker's HTTP transport backstop is 130s,
    /// above the AttemptTimeout, so the resilience pipeline fires first). The per-link floor takes
    /// precedence, so messages with more than 18 links can exceed this target (and beyond ~24 links
    /// may approach the transport backstop).
    /// </summary>
    private static readonly TimeSpan s_maxContentWaitBudget = TimeSpan.FromSeconds( 90 );

    /// <summary>
    /// Floor for the scaled per-link wait budget so every link gets a usable wait window, hard-coded
    /// at 5 seconds. Even when the shared content budget divides to less than this, each link waits
    /// at least this long; this takes precedence over <see cref="s_maxContentWaitBudget"/> for very
    /// link-heavy messages.
    /// </summary>
    private static readonly TimeSpan s_minPerLinkWaitBudget = TimeSpan.FromSeconds( 5 );

    /// <summary>
    /// Extracts every supported provider link from free-text <paramref name="content"/>, deduplicates
    /// them, filters to enabled providers, divides the shared content wait budget across them, and
    /// resolves each in turn — streaming one <see cref="LookupResult"/> per link as it completes.
    /// </summary>
    /// <param name="content">Free text that may contain one or more provider links.</param>
    /// <returns>An async stream of per-link lookup outcomes; empty when no supported links are found.</returns>
    public async IAsyncEnumerable<LookupResult> LookupByContentAsync( string content ) {
        if (string.IsNullOrWhiteSpace( content )) {
            yield break;
        }

        // Collect unique links for enabled providers up front so the per-link wait
        // budget can be scaled to the number of links
        HashSet<string> processedLinks = new( StringComparer.OrdinalIgnoreCase );
        List<(string Link, SupportedProviders Provider)> links = [];

        foreach (string link in ValidHttpsLink( ).GetGroupValues( content, "Url" )) {
            if (!processedLinks.Add( link )) {
                continue;
            }

            // Determine provider from URL
            SupportedProviders? provider = DetermineProviderFromUrl( link );
            if (provider is null || !_enabledProviders.Contains( provider.Value )) {
                continue;
            }

            links.Add( (link, provider.Value) );
        }

        if (links.Count == 0) {
            yield break;
        }

        // Links resolve sequentially, each waiting up to the interactive budget; scale
        // the per-link budget so the total stays under s_maxContentWaitBudget (a Discord
        // multi-link message must finish before the bot's HTTP transport gives up)
        TimeSpan perLinkBudget = TimeSpan.FromTicks(
            Math.Min( _interactiveWaitTimeout.Ticks, s_maxContentWaitBudget.Ticks / links.Count )
        );
        if (perLinkBudget < s_minPerLinkWaitBudget) {
            perLinkBudget = s_minPerLinkWaitBudget;
        }

        foreach ((string link, SupportedProviders provider) in links) {
            LookupResult result = await LookupByUrlAsync( link, provider, perLinkBudget );
            yield return result;
        }
    }

    /// <summary>
    /// Resolves a track by title and artist through the full orchestration flow, fanning the lookup
    /// out to all enabled providers. Returns an empty, non-partial result when either input is blank.
    /// </summary>
    /// <param name="title">The track or album title to search for.</param>
    /// <param name="artist">The artist name to search for.</param>
    /// <returns>The lookup outcome, possibly partial.</returns>
    public async Task<LookupResult> LookupByMetadataAsync( string title, string artist ) {
        if (string.IsNullOrWhiteSpace( title ) || string.IsNullOrWhiteSpace( artist )) {
            return new LookupResult { Result = null };
        }

        string lookupKey = LookupKeyBuilder.MetadataKey( title, artist );

        return await PerformLookupAsync(
            lookupKey: lookupKey,
            lookupType: LookupRequestType.SongLookup,
            lookupValue: $"{title}|{artist}",
            cacheCheck: ( ) => _cache.TryGetCachedResultByMetadataAsync( title, artist ),
            title: title,
            artist: artist,
            isAlbum: false
        );
    }

    /// <summary>
    /// Resolves a track by ISRC through the full orchestration flow, fanning the lookup out to all
    /// enabled providers. Returns an empty, non-partial result when the ISRC is blank.
    /// </summary>
    /// <param name="isrc">The International Standard Recording Code identifying the track.</param>
    /// <returns>The lookup outcome, possibly partial.</returns>
    public async Task<LookupResult> LookupByIsrcAsync( string isrc ) {
        if (string.IsNullOrWhiteSpace( isrc )) {
            return new LookupResult { Result = null };
        }

        string normalizedIsrc = isrc.Trim( ).ToUpperInvariant( );
        string lookupKey = LookupKeyBuilder.IsrcKey( isrc );

        return await PerformLookupAsync(
            lookupKey: lookupKey,
            lookupType: LookupRequestType.IsrcLookup,
            lookupValue: normalizedIsrc,
            cacheCheck: ( ) => _cache.TryGetCachedResultByISRCAsync( normalizedIsrc ),
            isAlbum: false
        );
    }

    /// <summary>
    /// Resolves an album by UPC through the full orchestration flow, fanning the lookup out to all
    /// enabled providers. Returns an empty, non-partial result when the UPC is blank.
    /// </summary>
    /// <param name="upc">The Universal Product Code identifying the album.</param>
    /// <returns>The lookup outcome, possibly partial.</returns>
    public async Task<LookupResult> LookupByUpcAsync( string upc ) {
        if (string.IsNullOrWhiteSpace( upc )) {
            return new LookupResult { Result = null };
        }

        string normalizedUpc = upc.Trim( );
        string lookupKey = LookupKeyBuilder.UpcKey( upc );

        return await PerformLookupAsync(
            lookupKey: lookupKey,
            lookupType: LookupRequestType.UpcLookup,
            lookupValue: normalizedUpc,
            cacheCheck: ( ) => _cache.TryGetCachedResultByUPCAsync( normalizedUpc ),
            isAlbum: true
        );
    }

    /// <summary>
    /// Resolves an entity by a provider's own id through the full orchestration flow. Because the
    /// originating provider is known, only that provider is initialized as the saga's starting point
    /// (set as the initial provider); other providers are back-filled by the workers. Returns an
    /// empty, non-partial result when the id is blank.
    /// </summary>
    /// <param name="providerId">The entity id within the originating provider.</param>
    /// <param name="provider">The provider that <paramref name="providerId"/> belongs to.</param>
    /// <param name="isAlbum"><see langword="true"/> when the id refers to an album; otherwise a track.</param>
    /// <returns>The lookup outcome, possibly partial.</returns>
    public async Task<LookupResult> LookupByProviderIdAsync( string providerId, SupportedProviders provider, bool isAlbum ) {
        if (string.IsNullOrWhiteSpace( providerId )) {
            return new LookupResult { Result = null };
        }

        string normalizedId = providerId.Trim( );
        LookupRequestType lookupType = isAlbum ? LookupRequestType.AlbumIdLookup : LookupRequestType.SongIdLookup;
        string lookupKey = LookupKeyBuilder.TypedKey( lookupType, provider, normalizedId );

        return await PerformLookupAsync(
            lookupKey: lookupKey,
            lookupType: lookupType,
            lookupValue: normalizedId,
            cacheCheck: ( ) => _cache.TryGetCachedResultByProviderIdAsync( normalizedId, provider, isAlbum ),
            isAlbum: isAlbum,
            initialProvider: provider
        );
    }

    /// <summary>
    /// Resolves a single provider URL through the orchestration flow, with the originating provider
    /// known. Used by <see cref="LookupByContentAsync"/> for each extracted link, passing the
    /// per-link slice of the shared content wait budget.
    /// </summary>
    /// <param name="url">The provider URL to resolve.</param>
    /// <param name="provider">The provider the URL belongs to.</param>
    /// <param name="waitBudget">The per-link wait budget; falls back to the interactive timeout when null.</param>
    /// <returns>The lookup outcome, possibly partial.</returns>
    private async Task<LookupResult> LookupByUrlAsync( string url, SupportedProviders provider, TimeSpan? waitBudget = null ) {
        string lookupKey = LookupKeyBuilder.UrlKey( url );

        LookupResult lookupResult = await PerformLookupAsync(
            lookupKey: lookupKey,
            lookupType: LookupRequestType.UriLookup,
            lookupValue: url,
            cacheCheck: ( ) => _cache.TryGetCachedResultAsync( url ),
            isAlbum: false, // Will be determined by the lookup
            initialProvider: provider,
            waitBudget: waitBudget
        );

        // Input links are intentionally absent from PDS records. Restore the submitted URL on
        // the response so Web can reconstruct the same saga key for its render-time progress
        // probe, and so caching-path results preserve the same input-link contract as the direct
        // resolver path.
        if (lookupResult.Result is not null) {
            // The controller probes InputLinks[0], so make the URL for this specific lookup the
            // leading entry even if a reused in-memory result already carries another alias.
            _ = lookupResult.Result.InputLinks.RemoveAll(
                input => input.Equals( url, StringComparison.OrdinalIgnoreCase ) );
            lookupResult.Result.InputLinks.Insert( 0, url );
        }

        return lookupResult;
    }

    /// <summary>
    /// The shared lookup flow every entry point funnels through. In order: run
    /// <paramref name="cacheCheck"/> and return a fresh hit; try to acquire the deduplication lock
    /// — if a request is already in flight, read the in-flight saga's stored result, else wait on the
    /// dedup signal (honoring the rate-limited sentinel) and re-check cache; if acquired, create
    /// the saga, queue the first provider, wait for completion, and resolve a final or partial result.
    /// On any failure during creation the dedup lock is released and the exception rethrown.
    /// </summary>
    /// <param name="lookupKey">The stable key identifying this logical lookup (used for dedup and saga id).</param>
    /// <param name="lookupType">The kind of lookup being performed.</param>
    /// <param name="lookupValue">The normalized lookup value.</param>
    /// <param name="cacheCheck">A delegate that checks the cache for this lookup, returning the result, its record URI, and staleness.</param>
    /// <param name="isAlbum"><see langword="true"/> when the lookup targets an album.</param>
    /// <param name="title">The title, for metadata lookups; otherwise null.</param>
    /// <param name="artist">The artist, for metadata lookups; otherwise null.</param>
    /// <param name="initialProvider">The originating provider for URL/provider-id lookups; null for fan-out lookups.</param>
    /// <param name="waitBudget">The wait budget for this lookup; falls back to the interactive timeout when null.</param>
    /// <returns>The lookup outcome: a cached, final, or partial result.</returns>
    private async Task<LookupResult> PerformLookupAsync(
        string lookupKey,
        LookupRequestType lookupType,
        string lookupValue,
        Func<Task<(MediaLinkResult result, string recordUri, bool isStale)?>> cacheCheck,
        bool isAlbum = false,
        string? title = null,
        string? artist = null,
        SupportedProviders? initialProvider = null,
        TimeSpan? waitBudget = null
    ) {
        TimeSpan waitTimeout = waitBudget ?? _interactiveWaitTimeout;

        // Check cache. Freshness is age-only (RedisMediaLinkCache.CheckRecordFreshness checks
        // LookedUpAt against the configured window). PDS/cache reads carry no persisted partial
        // flag and are assumed final. A partial saga generation can be visible briefly before its
        // deterministic record is replaced by the final generation; that bounded consistency
        // window is an accepted tradeoff for avoiding a saga read on every fresh cache hit.
        (MediaLinkResult result, string recordUri, bool isStale)? cached = await cacheCheck( );

        if (cached.HasValue && !cached.Value.isStale) {
            LogCacheHit( _logger, lookupKey );
            return new LookupResult {
                Result = cached.Value.result
            };
        }

        // Check deduplication - is another request in-flight?
        DeduplicationResult dedup = await _deduplicator.TryAcquireAsync( lookupKey, s_deduplicationLockDuration );

        if (!dedup.Acquired && dedup.AlreadyInFlight) {
            LogRequestAlreadyInFlight( _logger, lookupKey );

            string inFlightSagaId = ISagaStateManager.GenerateSagaId( lookupKey );
            DateTimeOffset deadline = DateTimeOffset.UtcNow + waitTimeout;

            // When the in-flight saga already stored a result, skip the blind wait and go
            // straight to the final-result wait, which honors the rate-limit escape hatch
            // and time budget (during a rate-limit window no publish would ever arrive)
            LookupSagaState? inFlightSaga = await _sagaManager.GetAsync( inFlightSagaId );
            if (inFlightSaga is not null) {
                await PromotePendingSagaAsync(
                    inFlightSaga,
                    lookupType,
                    lookupValue,
                    isAlbum,
                    title,
                    artist,
                    initialProvider );
            }
            string? storedResultUri = inFlightSaga?.FinalResultUri ?? inFlightSaga?.PartialResultUri;
            if (!string.IsNullOrEmpty( storedResultUri )) {
                LogInFlightSagaHasStoredResult( _logger, inFlightSagaId );
                return await WaitForFinalResultAsync( lookupKey, inFlightSagaId, storedResultUri, deadline );
            }

            // Wait for the other instance to complete
            string? resultUri = await _deduplicator.WaitForCompletionAsync( lookupKey, waitTimeout );

            // Check for rate-limited sentinel - return any stored partial data
            // alongside the rate-limit info instead of dropping it
            if (resultUri == LookupConstants.RateLimitedSentinel) {
                return await BuildRateLimitedPartialAsync( inFlightSagaId );
            }

            if (!string.IsNullOrEmpty( resultUri )) {
                // The published result may be partial (secondary lookups pending) -
                // keep waiting for the final with the remaining time budget
                return await WaitForFinalResultAsync( lookupKey, inFlightSagaId, resultUri, deadline );
            }

            // Timeout or failure - check cache again, might have been populated.
            // A cache-served result is always final; no live saga reference is attached.
            cached = await cacheCheck( );
            if (cached.HasValue) {
                return new LookupResult {
                    Result = cached.Value.result
                };
            }

            // Still nothing, return null
            return new LookupResult { Result = null };
        }

        try {
            // Create saga and queue initial lookup
            return await CreateSagaAndQueueLookupAsync(
                lookupKey,
                lookupType,
                lookupValue,
                isAlbum,
                title,
                artist,
                initialProvider,
                waitTimeout
            );
        } catch (Exception ex) {
            LogLookupError( _logger, ex, lookupKey );

            // Release the lock on error
            await _deduplicator.ReleaseAsync( lookupKey, null );

            throw;
        }
    }

    /// <summary>
    /// Atomically promotes an existing maintenance generation and publishes interactive copies of
    /// its unfinished provider legs. Only the caller that changes the persisted priority publishes,
    /// so concurrent manual lookups cannot create an interactive fan-out storm.
    /// </summary>
    private async Task PromotePendingSagaAsync(
        LookupSagaState saga,
        LookupRequestType lookupType,
        string lookupValue,
        bool isAlbum,
        string? title,
        string? artist,
        SupportedProviders? initialProvider
    ) {
        if (string.IsNullOrWhiteSpace( saga.InstanceToken )
            || !await _sagaManager.TryPromoteToInteractiveAsync( saga.SagaId, saga.InstanceToken )) {
            return;
        }

        IEnumerable<SupportedProviders> candidates = initialProvider.HasValue
            ? [initialProvider.Value]
            : _enabledProviders;
        foreach (SupportedProviders provider in candidates) {
            if (saga.ProviderStates.TryGetValue( provider, out ProviderLookupState? state ) && state.IsComplete) {
                continue;
            }

            IRequestQueue<QueuedLookupRequest> queue = _queueResolver.GetQueue( provider );
            await queue.EnqueueAsync( new QueuedLookupRequest {
                RequestId = Guid.NewGuid( ).ToString( "N" ),
                Provider = provider,
                LookupType = lookupType,
                LookupValue = lookupValue,
                SagaId = saga.SagaId,
                SagaInstanceToken = saga.InstanceToken,
                IsAlbum = isAlbum,
                Title = title,
                Artist = artist,
                OriginPriority = QueuePriority.Interactive
            }, QueuePriority.Interactive );
        }
    }

    /// <summary>
    /// Creates (or resumes) the saga for an acquired lookup and drives it to a result. Generates the
    /// saga id from the lookup key, gets-or-creates the saga, and — if it already carries a stored
    /// result — releases the dedup lock with that URI and waits for the final result. Otherwise it
    /// initializes provider states (all enabled providers, or just the initial provider for
    /// URL/provider-id lookups), enqueues the first provider's request at interactive priority, waits
    /// up to the timeout for completion (honoring the rate-limited sentinel), and resolves the final
    /// or partial result by reading the saga and fetching the stored result from ATProto storage.
    /// </summary>
    /// <param name="lookupKey">The stable key identifying this logical lookup.</param>
    /// <param name="lookupType">The kind of lookup being performed.</param>
    /// <param name="lookupValue">The normalized lookup value.</param>
    /// <param name="isAlbum"><see langword="true"/> when the lookup targets an album.</param>
    /// <param name="title">The title, for metadata lookups; otherwise null.</param>
    /// <param name="artist">The artist, for metadata lookups; otherwise null.</param>
    /// <param name="initialProvider">The originating provider for URL/provider-id lookups; null for fan-out lookups.</param>
    /// <param name="waitTimeout">How long to wait for completion before returning a partial.</param>
    /// <returns>The lookup outcome: a final or partial result.</returns>
    private async Task<LookupResult> CreateSagaAndQueueLookupAsync(
        string lookupKey,
        LookupRequestType lookupType,
        string lookupValue,
        bool isAlbum,
        string? title,
        string? artist,
        SupportedProviders? initialProvider,
        TimeSpan waitTimeout
    ) {
        // Generate deterministic saga ID from lookup key
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

        // Create a saga, or resume one already started for this deterministic lookup key.
        LookupSagaState saga = await _sagaManager.GetOrCreateAsync(
            sagaId, lookupKey, lookupType, lookupValue, QueuePriority.Interactive );

        DateTimeOffset deadline = DateTimeOffset.UtcNow + waitTimeout;

        // Resumed saga that already stored a result: resolve it directly instead of re-queueing
        string? knownResultUri = saga.FinalResultUri ?? saga.PartialResultUri;
        if (!string.IsNullOrEmpty( knownResultUri )) {
            LogSagaResumedWithResult( _logger, sagaId );

            // Release the just-acquired in-flight lock before waiting: this path enqueues
            // no work, so nothing else would release it (the coordinator runs in another
            // process and its release is ownership-checked). Publishing the known URI
            // hands the stored result to callers already blocked in WaitForCompletionAsync.
            await _deduplicator.ReleaseAsync( lookupKey, knownResultUri );

            return await WaitForFinalResultAsync( lookupKey, sagaId, knownResultUri, deadline );
        }

        // Determine which provider to queue first
        SupportedProviders firstProvider = initialProvider ?? _enabledProviders.First();
        if (!string.IsNullOrWhiteSpace( saga.InstanceToken )) {
            _ = await _sagaManager.TryPromoteToInteractiveAsync( sagaId, saga.InstanceToken );
        }

        // For direct lookups (ISRC/UPC without initialProvider), initialize all enabled providers
        // For URL lookups (with initialProvider), only initialize the first provider
        // The saga coordinator adds the remaining providers to the SAME saga once the
        // initial result yields an external ID for secondary lookups
        List<SupportedProviders> providersToInitialize = initialProvider.HasValue
            ? [firstProvider]
            : [.. _enabledProviders];

        if (string.IsNullOrWhiteSpace( saga.InstanceToken )
            || !await _sagaManager.TryInitializeProviderStatesAsync( sagaId, providersToInitialize, saga.InstanceToken )) {
            throw new InvalidOperationException( $"Saga instance was replaced before provider initialization for {sagaId}." );
        }

        // If we have an initial provider (from URL lookup), set it
        if (initialProvider.HasValue) {
            if (!await _sagaManager.TrySetInitialProviderAsync( sagaId, initialProvider.Value, saga.InstanceToken )) {
                throw new InvalidOperationException( $"Saga instance was replaced before initial-provider registration for {sagaId}." );
            }
        }

        // Direct external-id lookups own one independent leg per enabled provider. URL lookups
        // intentionally start with only the URL's provider and let the coordinator derive
        // secondary identifiers. When a maintenance saga is promoted, enqueue an interactive copy
        // for every still-pending direct leg; the generation fence makes the old bulk copy stale
        // after the first successful commit.
        IReadOnlyList<SupportedProviders> providersToEnqueue = initialProvider.HasValue
            ? [firstProvider]
            : providersToInitialize;
        foreach (SupportedProviders provider in providersToEnqueue) {
            bool providerMissing = !saga.ProviderStates.TryGetValue(
                provider, out ProviderLookupState? queuedProviderState );
            if (!providerMissing && queuedProviderState!.IsComplete) {
                continue;
            }

            QueuedLookupRequest request = new( ) {
                RequestId = Guid.NewGuid( ).ToString( "N" ),
                Provider = provider,
                LookupType = lookupType,
                LookupValue = lookupValue,
                SagaId = sagaId,
                SagaInstanceToken = saga.InstanceToken,
                IsAlbum = isAlbum,
                Title = title,
                Artist = artist,
                // Interactive origin is persisted into the saga so secondary lookups
                // spawned by the coordinator inherit the caller's urgency
                OriginPriority = QueuePriority.Interactive
            };

            IRequestQueue<QueuedLookupRequest> queue = _queueResolver.GetQueue( provider );
            await queue.EnqueueAsync( request, QueuePriority.Interactive );

            LogSagaCreated( _logger, sagaId, provider, lookupType, lookupValue );
        }

        // Wait for initial result via deduplicator subscription
        string? resultUri = await _deduplicator.WaitForCompletionAsync( lookupKey, waitTimeout );

        // Check for rate-limited sentinel - return any stored partial data alongside the
        // rate-limit info (the partial write may land moments after the sentinel)
        if (resultUri == LookupConstants.RateLimitedSentinel) {
            return await BuildRateLimitedPartialAsync( sagaId );
        }

        if (!string.IsNullOrEmpty( resultUri )) {
            // Initial lookup completed - the published result may be partial (secondary
            // lookups pending), so keep waiting for the final with the remaining budget
            return await WaitForFinalResultAsync( lookupKey, sagaId, resultUri, deadline );
        }

        // Timeout - check if we have any result. ISRC/UPC direct lookups never write a
        // partial, so the final result URI must be honored too.
        LookupSagaState? saga2 = await _sagaManager.GetAsync( sagaId );

        string? storedResultUri = saga2?.FinalResultUri ?? saga2?.PartialResultUri;
        if (storedResultUri is not null) {
            MediaLinkResult? storedResult = await _atProtoStorage.GetMediaLinkResultAsync( storedResultUri );
            return !string.IsNullOrEmpty( saga2!.FinalResultUri )
                ? new LookupResult { Result = storedResult }
                : CreatePartialResult( storedResult, sagaId, saga2 );
        }

        // No result yet
        return new LookupResult {
            Result = null,
            SagaId = sagaId,
            RateLimitedProviders = GetActiveRateLimits( saga2 )
        };
    }

    /// <summary>
    /// Resolves a known result URI to either a final result or the best available partial. Loops
    /// reading the saga state and fetching the stored <see cref="MediaLinkResult"/> from ATProto
    /// storage: returns final when the saga has a final result URI or is complete-and-not-partial;
    /// returns a partial when all pending providers are rate-limited (waiting cannot help) or the
    /// deadline passes; otherwise waits on the dedup final-completion signal (which may surface a
    /// newer URI, the rate-limited sentinel, or nothing) and repeats until resolved.
    /// </summary>
    /// <param name="lookupKey">The lookup key whose final-completion signal is awaited.</param>
    /// <param name="sagaId">The saga whose state is polled.</param>
    /// <param name="resultUri">The currently known stored result URI to fetch.</param>
    /// <param name="deadline">The absolute instant after which a partial is returned.</param>
    /// <returns>The final result, or the best available partial when the budget is exhausted.</returns>
    private async Task<LookupResult> WaitForFinalResultAsync(
        string lookupKey,
        string sagaId,
        string resultUri,
        DateTimeOffset deadline
    ) {
        while (true) {
            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId );

            // Prefer the final result URI once available (it may differ from the partial's)
            if (!string.IsNullOrEmpty( saga?.FinalResultUri )) {
                resultUri = saga.FinalResultUri;
            }

            MediaLinkResult? result = await _atProtoStorage.GetMediaLinkResultAsync( resultUri );

            // Saga state missing (expired/deleted) - no saga means no in-progress partial; result is served as final
            if (saga is null) {
                return new LookupResult {
                    Result = result
                };
            }

            bool isFinal = !string.IsNullOrEmpty( saga.FinalResultUri )
                        || (saga.IsComplete && !saga.IsPartial);

            if (isFinal) {
                return new LookupResult {
                    Result = result
                };
            }

            // When every pending provider is rate-limited, waiting cannot help -
            // return the partial with rate-limit info immediately (approved escape hatch).
            // Expired rate-limit entries are ignored so an imminent full result is still
            // waited for instead of returning a stale partial.
            if (AllPendingProvidersRateLimited( saga )) {
                LogReturningRateLimitedPartial( _logger, sagaId );
                return CreatePartialResult( result, sagaId, saga );
            }

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) {
                LogWaitBudgetExhausted( _logger, sagaId );
                return CreatePartialResult( result, sagaId, saga );
            }

            LogWaitingForFinalResult( _logger, sagaId, remaining.TotalSeconds );

            // Subscribe before re-reading saga state (inside the deduplicator) so a final
            // result or rate-limit sentinel published in the gap is never missed
            string? nextUri = await _deduplicator.WaitForFinalCompletionAsync(
                lookupKey,
                remaining,
                async ( ) => {
                    LookupSagaState? gapSaga = await _sagaManager.GetAsync( sagaId );
                    if (!string.IsNullOrEmpty( gapSaga?.FinalResultUri )) {
                        return gapSaga.FinalResultUri;
                    }

                    // A rate-limit condition arising in the gap is equally unrecoverable
                    // by waiting - surface the sentinel so the escape hatch applies
                    return gapSaga is not null && AllPendingProvidersRateLimited( gapSaga )
                        ? LookupConstants.RateLimitedSentinel
                        : null;
                }
            );

            if (nextUri == LookupConstants.RateLimitedSentinel) {
                return await BuildRateLimitedPartialAsync( sagaId, result );
            }

            if (string.IsNullOrEmpty( nextUri )) {
                // Timed out - re-check once in case the final landed without a notification
                saga = await _sagaManager.GetAsync( sagaId );
                if (!string.IsNullOrEmpty( saga?.FinalResultUri )) {
                    resultUri = saga.FinalResultUri;
                    continue;
                }

                LogWaitBudgetExhausted( _logger, sagaId );
                return CreatePartialResult( result, sagaId, saga );
            }

            resultUri = nextUri;
        }
    }

    /// <summary>
    /// Resolves a rate-limit sentinel into the best available partial response: the saga's
    /// stored result (final preferred over partial) is fetched so available provider links
    /// are returned alongside the rate-limit info instead of being dropped. A known result is
    /// reused when provided rather than re-fetched.
    /// </summary>
    /// <param name="sagaId">The deterministic saga ID for the lookup.</param>
    /// <param name="knownResult">An already-fetched result to reuse instead of re-fetching.</param>
    /// <returns>A partial <see cref="LookupResult"/> carrying any active rate-limit info.</returns>
    private async Task<LookupResult> BuildRateLimitedPartialAsync( string sagaId, MediaLinkResult? knownResult = null ) {
        LookupSagaState? saga = await _sagaManager.GetAsync( sagaId );

        MediaLinkResult? result = knownResult;
        string? storedResultUri = saga?.FinalResultUri ?? saga?.PartialResultUri;
        if (result is null && !string.IsNullOrEmpty( storedResultUri )) {
            result = await _atProtoStorage.GetMediaLinkResultAsync( storedResultUri );
        }

        LogReturningRateLimitedPartial( _logger, sagaId );
        return CreatePartialResult( result, sagaId, saga );
    }

    /// <summary>
    /// Creates a partial <see cref="LookupResult"/> tagged with the saga id and carrying the saga's
    /// unexpired rate-limit info.
    /// </summary>
    /// <param name="result">The best available result, or null.</param>
    /// <param name="sagaId">The saga the partial belongs to.</param>
    /// <param name="saga">The saga state, used to extract active rate limits; may be null.</param>
    /// <returns>A partial lookup result.</returns>
    private static LookupResult CreatePartialResult( MediaLinkResult? result, string sagaId, LookupSagaState? saga )
        => new( ) {
            Result = result,
            SagaId = sagaId,
            RateLimitedProviders = GetActiveRateLimits( saga )
        };

    /// <summary>
    /// Filters the saga's rate-limit info down to entries whose retry-after has not yet
    /// lapsed, so expired limits neither trigger the escape hatch nor produce stale
    /// "retry after" user messaging. Returns <see langword="null"/> when nothing is actively
    /// rate-limited (or the saga is null).
    /// </summary>
    /// <param name="saga">The saga whose rate-limit info is filtered; may be null.</param>
    /// <returns>The still-active rate-limit windows, or <see langword="null"/> when none remain.</returns>
    private static List<ProviderRateLimitInfo>? GetActiveRateLimits( LookupSagaState? saga ) {
        List<ProviderRateLimitInfo>? activeRateLimits = saga?.RateLimitInfo?
            .Where( r => r.RetryAfter > DateTimeOffset.UtcNow )
            .ToList( );

        return activeRateLimits is { Count: > 0 } ? activeRateLimits : null;
    }

    /// <summary>
    /// Determines whether every still-pending provider is covered by an unexpired rate limit,
    /// meaning waiting longer cannot produce additional results. This is the condition under which
    /// the orchestrator returns a rate-limited partial rather than waiting.
    /// </summary>
    /// <param name="saga">The saga whose pending providers are checked.</param>
    /// <returns><see langword="true"/> when all pending providers have an active rate-limit window.</returns>
    private static bool AllPendingProvidersRateLimited( LookupSagaState saga ) {
        List<ProviderRateLimitInfo>? activeRateLimits = GetActiveRateLimits( saga );
        return activeRateLimits is not null
            && saga.PendingProviders.All( p => activeRateLimits.Any( r => r.Provider == p ) );
    }

    /// <summary>
    /// Infers the provider for a URL by a fast, case-insensitive substring match against the entire
    /// URL string (apple/spotify/tidal) — not just the host, so a token may match anywhere in the path
    /// or query. This is a loose pre-filter used to skip non-provider links before the stricter
    /// per-provider parsing performed downstream; it is intentionally more permissive than a full URL parse.
    /// </summary>
    /// <param name="url">The URL to classify.</param>
    /// <returns>The matched provider, or <see langword="null"/> when the URL is blank or matches none.</returns>
    private static SupportedProviders? DetermineProviderFromUrl( string url ) {
        if (string.IsNullOrWhiteSpace( url )) {
            return null;
        }

        string lowerUrl = url.ToLowerInvariant( );

        return lowerUrl.Contains( "apple" ) || lowerUrl.Contains( "music.apple" )
            ? SupportedProviders.AppleMusic
            : lowerUrl.Contains( "spotify" ) || lowerUrl.Contains( "open.spotify" )
            ? SupportedProviders.Spotify
            : lowerUrl.Contains( "tidal" ) ? SupportedProviders.Tidal : null;
    }

    /// <summary>
    /// Source-generated regex that extracts apple.com / spotify.com / tidal.com links from free text,
    /// capturing the full match as <c>Url</c>. A 1000 ms match timeout guards against ReDoS on
    /// pathological input.
    /// </summary>
    /// <returns>The compiled provider-link regex.</returns>
    [GeneratedRegex(
        @"(?<Url>(?<Link>(?:https?://)?(?:www\.)?((?:[a-z0-9-]+\.)?(?:apple\.com|spotify\.com|tidal\.com))[^\s""'<>]*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ValidHttpsLink( );

    #region LoggerMessage Definitions

    /// <summary>Logs (Debug) that a fresh cached result satisfied the lookup.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="lookupKey">The lookup key that hit the cache.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorCacheHit,
        Level = LogLevel.Debug,
        Message = "Cache hit for {LookupKey}" )]
    private static partial void LogCacheHit( ILogger logger, string lookupKey );

    /// <summary>Logs (Debug) that an identical request is already in flight, so this call waits for it.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="lookupKey">The lookup key already being processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorRequestAlreadyInFlight,
        Level = LogLevel.Debug,
        Message = "Request {LookupKey} already in-flight, waiting for completion" )]
    private static partial void LogRequestAlreadyInFlight( ILogger logger, string lookupKey );

    /// <summary>Logs (Error) that performing a lookup failed; the dedup lock is released before rethrow.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception raised during the lookup.</param>
    /// <param name="lookupKey">The lookup key that failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorLookupError,
        Level = LogLevel.Error,
        Message = "Error performing lookup for {LookupKey}" )]
    private static partial void LogLookupError( ILogger logger, Exception ex, string lookupKey );

    /// <summary>Logs (Information) that a saga was created and the first provider's lookup queued.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The created saga id.</param>
    /// <param name="provider">The first provider queued.</param>
    /// <param name="lookupType">The lookup type.</param>
    /// <param name="lookupValue">The normalized lookup value.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorSagaCreated,
        Level = LogLevel.Information,
        Message = "Created saga {SagaId} and queued initial lookup for {Provider} ({LookupType}:{LookupValue})" )]
    private static partial void LogSagaCreated( ILogger logger, string sagaId, SupportedProviders provider, LookupRequestType lookupType, string lookupValue );

    /// <summary>Logs (Debug) that an in-flight saga already has a stored result, so the call waits for the final result directly.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The in-flight saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorInFlightSagaHasStoredResult,
        Level = LogLevel.Debug,
        Message = "In-flight saga {SagaId} already stored a result, waiting for the final result directly" )]
    private static partial void LogInFlightSagaHasStoredResult( ILogger logger, string sagaId );

    /// <summary>Logs (Debug) that a saga was resumed with an existing stored result, resolved directly.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The resumed saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorSagaResumedWithResult,
        Level = LogLevel.Debug,
        Message = "Resumed saga {SagaId} with an existing stored result, resolving directly" )]
    private static partial void LogSagaResumedWithResult( ILogger logger, string sagaId );

    /// <summary>Logs (Debug) that a saga is still partial and the call is waiting for the final result.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="remainingSeconds">The seconds remaining in the wait budget.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorWaitingForFinalResult,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} is partial, waiting up to {RemainingSeconds:F1}s for the final result" )]
    private static partial void LogWaitingForFinalResult( ILogger logger, string sagaId, double remainingSeconds );

    /// <summary>Logs (Information) that all pending providers are rate-limited, so a partial is returned.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorReturningRateLimitedPartial,
        Level = LogLevel.Information,
        Message = "All pending providers for saga {SagaId} are rate-limited, returning partial result" )]
    private static partial void LogReturningRateLimitedPartial( ILogger logger, string sagaId );

    /// <summary>Logs (Information) that the wait budget was exhausted, so the best available partial is returned.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Services.LinkResolver.OrchestratorWaitBudgetExhausted,
        Level = LogLevel.Information,
        Message = "Wait budget exhausted for saga {SagaId}, returning best available partial result" )]
    private static partial void LogWaitBudgetExhausted( ILogger logger, string sagaId );

    #endregion LoggerMessage Definitions
}
