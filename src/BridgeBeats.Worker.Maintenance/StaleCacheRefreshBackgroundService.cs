using System.Globalization;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Utilities;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Worker.Maintenance.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Worker.Maintenance;

/// <summary>
/// Periodically selects the oldest stale cached media-link records and bulk-enqueues them for
/// re-lookup, keeping the cache from drifting permanently out of date for records that are never
/// re-requested by users. The sweep schedule is durable across restarts via a Redis last-run
/// marker; a fixed startup grace period prevents hammering the PDS scan on rapid restart cycles.
/// </summary>
/// <remarks>
/// The service operates as an independent failure domain from
/// <see cref="CacheBootstrapBackgroundService"/>. Per-record failures are counted and skipped;
/// the remaining records are still enqueued. A fatal error during a pass causes a short retry
/// backoff (configured via <see cref="CacheBootstrapSettings.RefreshRetryInterval"/>) before the
/// next pass attempt; the last-run marker is not advanced on failure so the next pass treats itself
/// as due-now.
/// </remarks>
/// <param name="atProtoStorage">Streams every stored record from the user's ATProto PDS.</param>
/// <param name="sagaManager">Creates and initializes sagas for re-lookup jobs.</param>
/// <param name="dispatchOutbox">Atomically stages and relays refresh provider legs.</param>
/// <param name="redis">The Redis connection used to read the durable schedule marker.</param>
/// <param name="enabledProviders">The set of providers that participate in re-lookups.</param>
/// <param name="settings">Configuration: PDS URI, user DID, cache freshness window, refresh interval, max records per run, and retry interval.</param>
/// <param name="logger">The logger for this service.</param>
/// <param name="refreshReviewStore">Store used to suppress and review unresolved refreshes.</param>
public sealed partial class StaleCacheRefreshBackgroundService(
    IATProtoStorageService atProtoStorage,
    ISagaStateManager sagaManager,
    ILookupDispatchOutbox dispatchOutbox,
    IConnectionMultiplexer redis,
    HashSet<SupportedProviders> enabledProviders,
    CacheBootstrapSettings settings,
    ILogger<StaleCacheRefreshBackgroundService> logger,
    IRefreshReviewStore refreshReviewStore
) : BackgroundService {

    /// <summary>Redis key that records the UTC instant the most recent refresh pass started.</summary>
    private const string LastRunMarkerKey = "cache:refresh:last-run";

    /// <summary>Number of stale selections without a completed refresh before direct review quarantine.</summary>
    internal const int MaxRefreshSweepAttemptsBeforeReview = 3;

    /// <summary>Fixed grace period at startup before the first scan is allowed to run.</summary>
    private static readonly TimeSpan s_startupGrace = TimeSpan.FromSeconds( 120 );

    /// <summary>Upper bound of the per-pass uniform jitter applied to de-synchronize the sweep from the bootstrap scan.</summary>
    private static readonly TimeSpan s_maxJitter = TimeSpan.FromSeconds( 300 );

    /// <summary>
    /// Startup grace duration used by <see cref="ExecuteAsync"/>. Defaults to
    /// <see cref="s_startupGrace"/> (120 s). Tests set this to <see cref="TimeSpan.Zero"/> to drive
    /// <see cref="ExecuteAsync"/> without waiting.
    /// </summary>
    internal TimeSpan _startupGrace = s_startupGrace;

    /// <summary>
    /// Maximum per-pass jitter used by <see cref="ExecuteAsync"/>. Defaults to
    /// <see cref="s_maxJitter"/> (300 s). Tests set this to <see cref="TimeSpan.Zero"/> to make
    /// schedule waits deterministic.
    /// </summary>
    internal TimeSpan _maxJitter = s_maxJitter;

    /// <summary>
    /// Waits a fixed startup grace, then loops on the durable schedule: reads the last-run marker
    /// to determine elapsed time, waits the remainder plus jitter, runs the refresh pass, then
    /// writes the marker on success only. On failure, waits the configured retry interval before
    /// trying again; the marker is not advanced so the next pass treats itself as due-now.
    /// Cancellation ends the loop cleanly from any wait point.
    /// </summary>
    /// <param name="stoppingToken">Signals when the host is shutting down.</param>
    /// <returns>A task that completes when the service stops.</returns>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        try {
            await Task.Delay( _startupGrace, stoppingToken );
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            LogRefreshShuttingDown( logger );
            return;
        }

        while (!stoppingToken.IsCancellationRequested) {
            try {
                DateTimeOffset? lastRun = await ReadLastRunMarkerAsync( );
                TimeSpan elapsed = lastRun.HasValue
                    ? DateTimeOffset.UtcNow - lastRun.Value
                    : TimeSpan.MaxValue;

                // A future-dated marker (e.g. clock skew or an invalid write) would produce a
                // negative elapsed, making the wait exceed a full interval and stalling the sweep.
                // Clamp so any future-dated marker is treated as due-now.
                if (elapsed < TimeSpan.Zero) {
                    elapsed = TimeSpan.MaxValue;
                }

                TimeSpan jitter = TimeSpan.FromMilliseconds(
                    Random.Shared.NextDouble( ) * _maxJitter.TotalMilliseconds );

                TimeSpan wait = elapsed >= settings.RefreshInterval
                    ? jitter
                    : (settings.RefreshInterval - elapsed) + jitter;

                if (wait > TimeSpan.Zero) {
                    await Task.Delay( wait, stoppingToken );
                }

                await RunRefreshPassAsync( stoppingToken );
                await WriteLastRunMarkerAsync( );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                LogRefreshPassFailedRetrying( logger, ex, settings.RefreshRetryInterval );
                try {
                    await Task.Delay( settings.RefreshRetryInterval, stoppingToken );
                } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                    break;
                }
            }
        }

        LogRefreshShuttingDown( logger );
    }

    /// <summary>
    /// Performs one stale-cache refresh pass: streams the PDS to identify the oldest stale records
    /// up to the configured maximum and bulk-enqueues each one. Per-record failures are counted and
    /// skipped; a fatal enumeration error propagates to the caller.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the pass.</param>
    /// <returns>A task that completes when the pass finishes or aborts.</returns>
    internal async Task RunRefreshPassAsync( CancellationToken cancellationToken ) {
        if (enabledProviders.Count == 0) {
            LogRefreshNoEnabledProviders( logger );
            return;
        }

        List<(string AtUri, MediaLinkResult Result)> selected = await SelectStalestRecordsAsync( cancellationToken );

        if (selected.Count == 0) {
            return;
        }

        LogRefreshStarting( logger, selected.Count );

        int enqueued = 0;
        int skipped = 0;
        int errors = 0;

        foreach ((string atUri, MediaLinkResult result) in selected) {
            QueueMetrics.RecordMaintenanceOutcome( "selected" );
            try {
                IReadOnlyList<RefreshLeg> legs = DeriveRefreshLegs( result, enabledProviders );

                if (legs.Count == 0) {
                    int sweepAttempt = await refreshReviewStore.IncrementSweepAttemptAsync( atUri, cancellationToken );
                    if (sweepAttempt >= MaxRefreshSweepAttemptsBeforeReview) {
                        string lookupKey = LookupKeyBuilder.UrlKey( atUri );
                        string? sourceRecordCid = await atProtoStorage.GetMediaLinkRecordCidAsync( atUri, cancellationToken );
                        bool anchorResolved = TryResolveAnchor(
                            result, out _, out bool resolvedIsAlbum, out _, out _ );
                        bool? recordIsAlbum = anchorResolved
                            ? resolvedIsAlbum
                            : result.Results.Values
                                .Select( value => value.IsAlbum )
                                .FirstOrDefault( value => value.HasValue );
                        await refreshReviewStore.PromoteDirectAsync(
                            new RefreshReviewEntry {
                                SourceRecordUri = atUri,
                                SourceRecordCid = sourceRecordCid,
                                SagaId = ISagaStateManager.GenerateSagaId( lookupKey ),
                                LookupType = LookupRequestType.UriLookup,
                                LookupValue = atUri,
                                IsAlbum = recordIsAlbum,
                                SourceResult = result
                            },
                            "No completed refresh after three sweeps.",
                            cancellationToken );
                    }
                    LogRefreshRecordSkipped( logger, atUri );
                    QueueMetrics.RecordMaintenanceOutcome( "no_legs" );
                    skipped++;
                    continue;
                }

                bool didEnqueue = await EnqueueRecordAsync( atUri, result, legs, cancellationToken );
                if (didEnqueue) {
                    enqueued++;
                    QueueMetrics.RecordMaintenanceOutcome( "dispatched" );
                } else {
                    skipped++;
                }
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                errors++;
                QueueMetrics.RecordMaintenanceOutcome( "enqueue_error" );
                LogRefreshEnqueueError( logger, ex, atUri );
            }
        }

        LogRefreshCompleted( logger, enqueued, skipped, errors );
    }

    /// <summary>
    /// Streams the PDS and returns the oldest stale records (up to
    /// <see cref="CacheBootstrapSettings.MaxRecordsPerRun"/>). A record is stale when its
    /// <see cref="MediaLinkResult.LookedUpAt"/> is older than the configured cache window (age-only;
    /// partial-ness is not a staleness trigger). The selection uses a size-bounded sorted set so the
    /// full corpus is never materialized in memory.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the enumeration.</param>
    /// <returns>The oldest stale records, sorted oldest-first.</returns>
    private async Task<List<(string AtUri, MediaLinkResult Result)>> SelectStalestRecordsAsync(
        CancellationToken cancellationToken
    ) {
        int maxN = settings.MaxRecordsPerRun;
        DateTime utcNow = DateTime.UtcNow;
        HashSet<string> unresolvedUris = (await refreshReviewStore.GetUnresolvedAsync( cancellationToken ))
            .Select( entry => entry.SourceRecordUri )
            .ToHashSet( StringComparer.Ordinal );

        // A SortedSet sorted ascending by (LookedUpAt, AtUri): the Max entry is the newest stale
        // record and is evicted when the set exceeds maxN, leaving only the oldest maxN entries.
        SortedSet<(DateTime LookedUpAt, string AtUri, MediaLinkResult Result)> oldest =
            new( OldestFirstComparer.Instance );

        try {
            await foreach ((string atUri, MediaLinkResult result) in
                atProtoStorage.ListAllRecordsAsync( settings.PdsUri, settings.UserDid, cancellationToken )) {
                if (!CacheFreshness.IsStale( result.LookedUpAt, settings.CacheDays, utcNow )) {
                    continue;
                }

                if (unresolvedUris.Contains( atUri )) {
                    continue;
                }

                _ = oldest.Add( (result.LookedUpAt, atUri, result) );

                if (oldest.Count > maxN) {
                    _ = oldest.Remove( oldest.Max );
                }
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            LogRefreshCancelled( logger );
            throw;
        }

        return [.. oldest.Select( e => (e.AtUri, e.Result) )];
    }

    /// <summary>
    /// Seeds one saga keyed on the record's external id, initializes exactly the enqueued-leg
    /// providers, and enqueues every leg to its own provider queue. Individual leg enqueue failures
    /// are handled per-leg: the failing leg is marked complete-as-failed in the saga so the saga
    /// can still reach <see cref="LookupSagaState.IsComplete"/> via the coordinator poll backstop.
    /// </summary>
    /// <param name="atUri">The AT-URI of the record being refreshed (used for logging).</param>
    /// <param name="result">The stale media-link result.</param>
    /// <param name="legs">The pre-derived non-empty leg list from <see cref="DeriveRefreshLegs"/>.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the record was dispatched and the saga left finalizable
    /// (individual legs may have failed and been marked complete-as-failed); <see langword="false"/>
    /// when the leg list is empty.
    /// </returns>
    private async Task<bool> EnqueueRecordAsync(
        string atUri,
        MediaLinkResult result,
        IReadOnlyList<RefreshLeg> legs,
        CancellationToken cancellationToken
    ) {
        if (legs.Count == 0) {
            LogRefreshRecordSkipped( logger, atUri );
            return false;
        }

        // Determine saga identity from the record's external id so this saga dedups with an
        // interactive lookup for the same entity and the coordinator's secondary fan-out is
        // suppressed (the IsrcLookup/UpcLookup origin type is the suppression key).
        string sagaLookupKey;
        LookupRequestType sagaLookupType;
        string sagaLookupValue;

        bool anchorResolved = TryResolveAnchor(
            result, out string recordExternalId, out bool resolvedIsAlbum, out _, out _ );
        bool? recordIsAlbum = anchorResolved
            ? resolvedIsAlbum
            : result.Results.Values.Select( value => value.IsAlbum ).FirstOrDefault( value => value.HasValue );

        if (anchorResolved
            && !string.IsNullOrEmpty( recordExternalId )) {
            if (resolvedIsAlbum) {
                sagaLookupValue = recordExternalId;
                sagaLookupKey = $"{LookupRequestType.UpcLookup}:{sagaLookupValue}";
                sagaLookupType = LookupRequestType.UpcLookup;
            } else {
                sagaLookupValue = recordExternalId.ToUpperInvariant( );
                sagaLookupKey = $"{LookupRequestType.IsrcLookup}:{sagaLookupValue}";
                sagaLookupType = LookupRequestType.IsrcLookup;
            }
        } else {
            // No external id: key the saga on the first native leg in deterministic provider order
            // so the id is stable across passes for the same record.
            RefreshLeg firstLeg = legs
                .OrderBy( l => (int)l.Provider )
                .First( );
            sagaLookupKey = LookupKeyBuilder.TypedKey( firstLeg.LookupType, firstLeg.Provider, firstLeg.LookupValue );
            sagaLookupType = firstLeg.LookupType;
            sagaLookupValue = firstLeg.LookupValue;
        }

        string sagaId = ISagaStateManager.GenerateSagaId( sagaLookupKey );
        string? sourceRecordCid = await atProtoStorage.GetMediaLinkRecordCidAsync( atUri, cancellationToken );
        RefreshReviewEntry recordContext = new( ) {
            SourceRecordUri = atUri,
            SourceRecordCid = sourceRecordCid,
            SagaId = sagaId,
            LookupType = sagaLookupType,
            LookupValue = sagaLookupValue,
            IsAlbum = recordIsAlbum,
            SourceResult = result
        };

        LookupSagaState? activeSaga = await sagaManager.GetAsync( sagaId, cancellationToken );
        if (activeSaga is { IsComplete: false }) {
            int activeAttempt = await refreshReviewStore.IncrementSweepAttemptAsync( atUri, cancellationToken );
            if (activeAttempt > 1) QueueMetrics.RecordMaintenanceOutcome( "reselected" );
            if (activeAttempt >= MaxRefreshSweepAttemptsBeforeReview) {
                RefreshReviewEntry? activeContext = await refreshReviewStore.GetPendingAsync( atUri, cancellationToken );
                if (activeContext is not null
                    && activeContext.SagaId == sagaId
                    && activeContext.InstanceToken == activeSaga.InstanceToken) {
                    // The active owner still has a live refresh attempt. Promote its existing
                    // context directly so the review entry suppresses reselection without
                    // deleting the pending slot needed by terminal completion.
                    await refreshReviewStore.PromoteDirectAsync( activeContext, "No completed refresh after three sweeps.", cancellationToken );
                } else {
                    // This record shares an active saga owned by another source record. Promote its
                    // own context at the threshold, but never delete the shared saga or overwrite the
                    // owner's pending slot.
                    await refreshReviewStore.PromoteDirectAsync(
                        recordContext with { InstanceToken = activeSaga.InstanceToken },
                        "No completed refresh after three sweeps.",
                        cancellationToken );
                }
            }
            LogRefreshRecordSkipped( logger, atUri );
            QueueMetrics.RecordMaintenanceOutcome( "active_saga_suppressed" );
            return false;
        }

        int sweepAttempt = await refreshReviewStore.IncrementSweepAttemptAsync( atUri, cancellationToken );
        if (sweepAttempt > 1) QueueMetrics.RecordMaintenanceOutcome( "reselected" );
        if (sweepAttempt >= MaxRefreshSweepAttemptsBeforeReview) {
            RefreshReviewEntry? pending = await refreshReviewStore.GetPendingAsync( atUri, cancellationToken );
            if (pending is not null && pending.SagaId == sagaId) {
                await refreshReviewStore.MarkUnresolvedAsync( pending, "No completed refresh after three sweeps.", cancellationToken );
            } else {
                await refreshReviewStore.PromoteDirectAsync(
                    recordContext,
                    "No completed refresh after three sweeps.",
                    cancellationToken );
            }
            return false;
        }

        LookupSagaState seededSaga = await sagaManager.GetOrCreateAsync(
            sagaId,
            sagaLookupKey,
            sagaLookupType,
            sagaLookupValue,
            originPriority: QueuePriority.Bulk,
            cancellationToken: cancellationToken
        );
        if (string.IsNullOrWhiteSpace( seededSaga.InstanceToken )) {
            LogRefreshRecordSkipped( logger, atUri );
            return false;
        }
        string instanceToken = seededSaga.InstanceToken;

        List<QueuedLookupRequest> requests = [.. legs.Select( leg => new QueuedLookupRequest {
                RequestId = Guid.NewGuid( ).ToString( "N" ),
                Provider = leg.Provider,
                LookupType = leg.LookupType,
                LookupValue = leg.LookupValue,
                SagaId = sagaId,
                SagaInstanceToken = instanceToken,
                IsAlbum = leg.IsAlbum,
                Title = leg.Title,
                Artist = leg.Artist,
                OriginPriority = QueuePriority.Bulk,
                Storefront = leg.Storefront,
                FallbackLookupType = leg.FallbackLookupType,
                FallbackLookupValue = leg.FallbackLookupValue,
                EnqueueOrigin = QueueEnqueueOrigin.RefreshSweep
            } )];

        IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome> outcomes =
            await dispatchOutbox.StageRefreshBatchAsync(
                requests,
                QueuePriority.Bulk,
                recordContext with { InstanceToken = instanceToken },
                cancellationToken );
        if (outcomes.Values.Any(
            outcome => outcome == ProviderDispatchStageOutcome.SagaInstanceMismatch )) {
            LogRefreshRecordSkipped( logger, atUri );
            return false;
        }

        foreach ((SupportedProviders provider, ProviderDispatchStageOutcome outcome) in outcomes) {
            if (outcome != ProviderDispatchStageOutcome.Staged) {
                continue;
            }
            QueueMetrics.RecordRefreshLegEnqueued( provider, sweepAttempt > 1 );
            try {
                _ = await dispatchOutbox.DispatchAsync( sagaId, provider, cancellationToken );
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                // The durable outbox record remains pending for the coordinator-owned relay.
                LogRefreshLegEnqueueFailed( logger, ex, atUri, provider );
                QueueMetrics.RecordMaintenanceOutcome( "leg_immediate_relay_failure", provider );
            }
        }

        return true;
    }

    /// <summary>
    /// Decomposes a stale record into the set of provider legs that will refresh it: a native-id
    /// leg for each present provider whose URL yields a parseable native id, plus an external-id
    /// fallback leg for every enabled provider that is missing from the record or whose URL is
    /// unparseable. Returns an empty list when no leg can be derived (record is skipped).
    /// </summary>
    /// <param name="record">The stale record to decompose.</param>
    /// <param name="providers">The set of enabled providers.</param>
    /// <returns>
    /// The ordered leg list, or an empty list when the record has no parseable URL and no external
    /// id to fall back on.
    /// </returns>
    internal static IReadOnlyList<RefreshLeg> DeriveRefreshLegs(
        MediaLinkResult record,
        IReadOnlyCollection<SupportedProviders> providers
    ) {
        List<RefreshLeg> legs = [];

        _ = TryResolveAnchor( record, out string recordExternalId, out bool recordIsAlbum,
            out string? anchorTitle, out string? anchorArtist );

        LookupRequestType? externalLookupType = string.IsNullOrEmpty( recordExternalId )
            ? null
            : recordIsAlbum ? LookupRequestType.UpcLookup : LookupRequestType.IsrcLookup;
        string? externalLookupValue = string.IsNullOrEmpty( recordExternalId )
            ? null
            : recordIsAlbum ? recordExternalId : recordExternalId.ToUpperInvariant( );

        // Step 2: native-id leg per present provider that is still enabled.
        // Providers absent from enabledProviders are skipped entirely: no native leg is emitted
        // and no fallback is registered — the provider drops off the record on the next write.
        foreach ((SupportedProviders provider, MusicLookupResult providerResult) in record.Results) {
            if (!providers.Contains( provider )) {
                continue;
            }

            string? id = ProviderUrlParser.ExtractId( provider, providerResult.URL ?? string.Empty );
            if (!string.IsNullOrWhiteSpace( id )) {
                bool isAlbum = providerResult.IsAlbum ?? false;
                legs.Add( new RefreshLeg(
                    provider,
                    isAlbum ? LookupRequestType.AlbumIdLookup : LookupRequestType.SongIdLookup,
                    id.Trim( ),
                    isAlbum,
                    providerResult.Title,
                    providerResult.Artist,
                    ResolveProviderStorefront( provider, providerResult ),
                    externalLookupType,
                    externalLookupValue
                ) );
            }
        }

        // Step 3: track which providers got a native-id leg.
        HashSet<SupportedProviders> covered = [.. legs.Select( l => l.Provider )];

        // Step 4: external-id fallback for missing and unparseable-present providers.
        foreach (SupportedProviders provider in providers) {
            if (covered.Contains( provider )) {
                continue;
            }

            if (!string.IsNullOrEmpty( recordExternalId )) {
                LookupRequestType fallbackType;
                string fallbackValue;
                if (recordIsAlbum) {
                    fallbackType = LookupRequestType.UpcLookup;
                    fallbackValue = recordExternalId;
                } else {
                    fallbackType = LookupRequestType.IsrcLookup;
                    fallbackValue = recordExternalId.ToUpperInvariant( );
                }
                legs.Add( new RefreshLeg(
                    provider,
                    fallbackType,
                    fallbackValue,
                    recordIsAlbum,
                    anchorTitle,
                    anchorArtist,
                    ResolveFallbackStorefront( record, provider ),
                    null,
                    null
                ) );
            }
            // If recordExternalId is blank, no fallback can be emitted for this provider.
        }

        // Step 5: empty list means no usable identifier; caller logs and counts the skip.
        return legs;
    }

    /// <summary>
    /// Resolves the record-level external id and album discriminant from the first provider result
    /// that carries a non-blank <see cref="MusicLookupResult.ExternalId"/>, matching the precedence
    /// <c>RecordKeyGenerator.GenerateRkey</c> uses. Returns <see langword="true"/> when an anchor
    /// was found.
    /// </summary>
    private static bool TryResolveAnchor(
        MediaLinkResult record,
        out string recordExternalId,
        out bool recordIsAlbum,
        out string? anchorTitle,
        out string? anchorArtist
    ) {
        foreach (MusicLookupResult r in record.Results.Values) {
            if (!string.IsNullOrWhiteSpace( r.ExternalId )) {
                recordExternalId = r.ExternalId.Trim( );
                recordIsAlbum = r.IsAlbum ?? false;
                anchorTitle = r.Title;
                anchorArtist = r.Artist;
                return true;
            }
        }

        recordExternalId = string.Empty;
        recordIsAlbum = false;
        anchorTitle = null;
        anchorArtist = null;
        return false;
    }

    /// <summary>
    /// Returns the provider's own stored storefront when valid, falling back to a storefront encoded
    /// in its URL. Invalid persisted data is discarded so provider workers never turn it into a
    /// transport failure.
    /// </summary>
    private static string? ResolveProviderStorefront(
        SupportedProviders provider,
        MusicLookupResult result
    ) => NormalizeStorefrontForProvider( provider, result.MarketRegion )
        ?? NormalizeStorefrontForProvider(
            provider,
            ProviderUrlParser.ExtractStorefront( provider, result.URL ?? string.Empty ) );

    /// <summary>
    /// Finds a storefront from any existing provider result that is valid for the target provider.
    /// Storefront is resolved independently from the external-id anchor because providers such as
    /// Spotify do not stamp a market even when another result carries one.
    /// </summary>
    private static string? ResolveFallbackStorefront(
        MediaLinkResult record,
        SupportedProviders targetProvider
    ) {
        foreach ((SupportedProviders sourceProvider, MusicLookupResult result) in record.Results) {
            // Spotify's DTO carries the generic "us" default but Spotify has no storefront-scoped
            // catalog capability. It must not override a real Apple Music or Tidal market.
            if (sourceProvider == SupportedProviders.Spotify) {
                continue;
            }

            string? storefront = NormalizeStorefrontForProvider( targetProvider, result.MarketRegion );
            if (storefront is not null) {
                return storefront;
            }

            string? sourceUrlStorefront = ProviderUrlParser.ExtractStorefront(
                sourceProvider,
                result.URL ?? string.Empty );
            storefront = NormalizeStorefrontForProvider( targetProvider, sourceUrlStorefront );
            if (storefront is not null) {
                return storefront;
            }
        }

        return null;
    }

    private static string? NormalizeStorefrontForProvider(
        SupportedProviders provider,
        string? storefront
    ) {
        if (string.IsNullOrWhiteSpace( storefront ) || provider == SupportedProviders.Spotify) {
            return null;
        }

        string normalized = storefront.Trim( );
        if (!normalized.All( char.IsAsciiLetter )) {
            return null;
        }

        return provider switch {
            SupportedProviders.AppleMusic when normalized.Length is >= 2 and <= 3 =>
                normalized.ToLowerInvariant( ),
            SupportedProviders.Tidal when normalized.Length == 2 =>
                normalized.ToUpperInvariant( ),
            _ => null
        };
    }

    /// <summary>
    /// Reads the durable last-run marker from Redis. Returns <see langword="null"/> when the key is
    /// absent or the read fails (treated as due-now by the caller).
    /// </summary>
    private async Task<DateTimeOffset?> ReadLastRunMarkerAsync( ) {
        try {
            IDatabase db = redis.GetDatabase( );
            RedisValue value = await db.StringGetAsync( LastRunMarkerKey );
            if (value.IsNullOrEmpty) {
                return null;
            }

            if (DateTimeOffset.TryParse(
                    value.ToString( ),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed )) {
                return parsed;
            }

            return null;
        } catch (Exception ex) {
            LogRefreshMarkerReadError( logger, ex );
            return null;
        }
    }

    /// <summary>
    /// Writes the current UTC instant as the durable last-run marker. A write failure is logged at
    /// Warning and the pass proceeds; on the next start the stale marker causes the service to treat
    /// itself as due-now, which may produce one extra pass.
    /// </summary>
    private async Task WriteLastRunMarkerAsync( ) {
        try {
            IDatabase db = redis.GetDatabase( );
            _ = await db.StringSetAsync(
                LastRunMarkerKey,
                DateTimeOffset.UtcNow.ToString( "O" )
            );
        } catch (Exception ex) {
            LogRefreshMarkerWriteError( logger, ex );
        }
    }

    /// <summary>
    /// Compares stale-record entries by <c>LookedUpAt</c> ascending then by AT-URI as a tiebreaker.
    /// When stored in a <see cref="SortedSet{T}"/>, removing <c>Max</c> always evicts the newest
    /// entry, retaining the oldest N items under bounded-size conditions.
    /// </summary>
    private sealed class OldestFirstComparer : IComparer<(DateTime LookedUpAt, string AtUri, MediaLinkResult Result)> {
        /// <summary>The singleton instance of this comparer.</summary>
        internal static readonly OldestFirstComparer Instance = new( );

        /// <inheritdoc/>
        public int Compare(
            (DateTime LookedUpAt, string AtUri, MediaLinkResult Result) x,
            (DateTime LookedUpAt, string AtUri, MediaLinkResult Result) y
        ) {
            int cmp = x.LookedUpAt.CompareTo( y.LookedUpAt );
            return cmp != 0 ? cmp : string.Compare( x.AtUri, y.AtUri, StringComparison.Ordinal );
        }
    }

    /// <summary>
    /// A worker-internal record struct describing one provider leg of a refresh operation. Never
    /// serialized and never crosses a process boundary.
    /// </summary>
    internal readonly record struct RefreshLeg(
        SupportedProviders Provider,
        LookupRequestType LookupType,
        string LookupValue,
        bool IsAlbum,
        string? Title,
        string? Artist,
        string? Storefront,
        LookupRequestType? FallbackLookupType,
        string? FallbackLookupValue
    );

    #region LoggerMessage Methods

    [LoggerMessage(
        EventId = LogEventIds.RefreshShuttingDown,
        Level = LogLevel.Information,
        Message = "Stale-cache refresh service is shutting down" )]
    private static partial void LogRefreshShuttingDown( ILogger logger );

    [LoggerMessage(
        EventId = LogEventIds.RefreshPassFailedRetrying,
        Level = LogLevel.Warning,
        Message = "Stale-cache refresh pass failed; retrying in {RetryInterval}" )]
    private static partial void LogRefreshPassFailedRetrying( ILogger logger, Exception ex, TimeSpan retryInterval );

    [LoggerMessage(
        EventId = LogEventIds.RefreshStarting,
        Level = LogLevel.Information,
        Message = "Stale-cache refresh starting: {SelectedCount} records selected for re-lookup" )]
    private static partial void LogRefreshStarting( ILogger logger, int selectedCount );

    [LoggerMessage(
        EventId = LogEventIds.RefreshEnqueueError,
        Level = LogLevel.Warning,
        Message = "Failed to enqueue stale record for re-lookup: {AtUri}" )]
    private static partial void LogRefreshEnqueueError( ILogger logger, Exception ex, string atUri );

    [LoggerMessage(
        EventId = LogEventIds.RefreshCompleted,
        Level = LogLevel.Information,
        Message = "Stale-cache refresh completed: {Enqueued} enqueued, {Skipped} skipped (no identifier), {Errors} errors" )]
    private static partial void LogRefreshCompleted( ILogger logger, int enqueued, int skipped, int errors );

    [LoggerMessage(
        EventId = LogEventIds.RefreshRecordSkipped,
        Level = LogLevel.Debug,
        Message = "Stale record skipped (no usable lookup identifier): {AtUri}" )]
    private static partial void LogRefreshRecordSkipped( ILogger logger, string atUri );

    [LoggerMessage(
        EventId = LogEventIds.RefreshCancelled,
        Level = LogLevel.Information,
        Message = "Stale-cache refresh was cancelled" )]
    private static partial void LogRefreshCancelled( ILogger logger );

    [LoggerMessage(
        EventId = LogEventIds.RefreshNoEnabledProviders,
        Level = LogLevel.Warning,
        Message = "Stale-cache refresh skipped: no enabled providers are configured" )]
    private static partial void LogRefreshNoEnabledProviders( ILogger logger );

    [LoggerMessage(
        EventId = LogEventIds.RefreshMarkerWriteError,
        Level = LogLevel.Warning,
        Message = "Failed to write durable last-run marker to Redis; pass will proceed and marker may be stale" )]
    private static partial void LogRefreshMarkerWriteError( ILogger logger, Exception ex );

    [LoggerMessage(
        EventId = LogEventIds.RefreshMarkerReadError,
        Level = LogLevel.Warning,
        Message = "Failed to read durable last-run marker from Redis; treating as due-now and running" )]
    private static partial void LogRefreshMarkerReadError( ILogger logger, Exception ex );

    [LoggerMessage(
        EventId = LogEventIds.RefreshLegEnqueueFailed,
        Level = LogLevel.Warning,
        Message = "Failed to enqueue refresh leg for {AtUri} to {Provider}; leg marked complete-as-failed in saga" )]
    private static partial void LogRefreshLegEnqueueFailed( ILogger logger, Exception ex, string atUri, SupportedProviders provider );

    #endregion
}
