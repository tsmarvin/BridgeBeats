using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Worker.SagaCoordinator.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Worker.SagaCoordinator;

/// <summary>
/// Orchestrates and finalizes lookup sagas. This service performs no provider lookups itself; it
/// watches for sagas whose per-provider results are complete, combines those results with
/// <see cref="SagaResultCombiner"/>, writes the outcome to the ATProto PDS, caches it, and releases
/// any synchronous waiters through <see cref="Contracts.Interfaces.IRequestDeduplicator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Finalization is triggered three ways, deliberately overlapping because Redis Pub/Sub is
/// at-most-once and a dropped message must never strand a saga:
/// </para>
/// <list type="number">
/// <item><description>
/// The literal <c>saga:completed</c> channel, published by a provider worker when its write makes a
/// saga complete. Each event is handled on a fire-and-forget <see cref="System.Threading.Tasks.Task"/>.
/// </description></item>
/// <item><description>
/// The <c>complete:*</c> pattern channel, a per-lookup wakeup whose suffix yields the lookup key and,
/// via <see cref="Contracts.Interfaces.ISagaStateManager.GenerateSagaId(string)"/>, the saga id.
/// </description></item>
/// <item><description>
/// A 30-second polling sweep that asks the saga state manager for completed-but-unfinalized sagas
/// (older than a 15-second minimum age). This is the safety net for completion events that were
/// never delivered; together with the per-write saga TTL it makes the pipeline self-healing rather
/// than dependent on every Pub/Sub message arriving.
/// </description></item>
/// </list>
/// <para>
/// When the first provider returns an external id (an ISRC for tracks or a UPC for albums), the
/// coordinator fans out secondary lookups to the other enabled providers so the final card spans
/// every provider. It first consults the ISRC/UPC cache and materializes any cached provider
/// results straight into the saga; only the still-missing providers are queued. Interactive sagas
/// may write an interim result while secondaries are outstanding. Maintenance refresh sagas never
/// do: they retain the currently served PDS record until all provider legs are terminal, then write
/// one result containing every successful sibling. ISRC/UPC-origin lookups skip this fan-out because
/// they are already keyed by the canonical id.
/// </para>
/// </remarks>
/// <param name="redis">The shared Redis connection used for Pub/Sub subscriptions.</param>
/// <param name="sagaManager">Reads and mutates saga state and per-provider results.</param>
/// <param name="atProtoStorage">Persists combined results to the ATProto PDS (the source of truth).</param>
/// <param name="cacheRepository">Caches final/partial results and resolves cached ISRC/UPC results.</param>
/// <param name="deduplicator">Releases the in-flight lock and notifies synchronous waiters on completion.</param>
/// <param name="resultCombiner">Combines per-provider saga state into a single media-link result.</param>
/// <param name="queueResolver">Resolves the per-provider queue used to enqueue secondary lookups.</param>
/// <param name="enabledProviders">The providers eligible for secondary fan-out.</param>
/// <param name="logger">The logger for this service.</param>
/// <param name="refreshReviewStore">Stale-refresh context and unresolved-review store.</param>
public sealed partial class SagaCoordinatorBackgroundService(
    IConnectionMultiplexer redis,
    ISagaStateManager sagaManager,
    IATProtoStorageService atProtoStorage,
    IMediaLinkCacheRepository cacheRepository,
    IRequestDeduplicator deduplicator,
    SagaResultCombiner resultCombiner,
    IProviderQueueResolver<QueuedLookupRequest> queueResolver,
    HashSet<SupportedProviders> enabledProviders,
    ILogger<SagaCoordinatorBackgroundService> logger,
    IRefreshReviewStore refreshReviewStore
    ) : BackgroundService {
    private readonly IConnectionMultiplexer _redis = redis
                                                   ?? throw new ArgumentNullException( nameof( redis ) );
    private readonly ISagaStateManager _sagaManager = sagaManager
                                                    ?? throw new ArgumentNullException( nameof( sagaManager ) );
    private readonly IATProtoStorageService _atProtoStorage = atProtoStorage
                                                            ?? throw new ArgumentNullException( nameof( atProtoStorage ) );
    private readonly IMediaLinkCacheRepository _cacheRepository = cacheRepository
                                                                ?? throw new ArgumentNullException( nameof( cacheRepository ) );
    private readonly IRequestDeduplicator _deduplicator = deduplicator
                                                        ?? throw new ArgumentNullException( nameof( deduplicator ) );
    private readonly SagaResultCombiner _resultCombiner = resultCombiner
                                                        ?? throw new ArgumentNullException( nameof( resultCombiner ) );
    private readonly IProviderQueueResolver<QueuedLookupRequest> _queueResolver = queueResolver
                                                                                ?? throw new ArgumentNullException( nameof( queueResolver ) );
    private readonly HashSet<SupportedProviders> _enabledProviders = enabledProviders
                                                                   ?? throw new ArgumentNullException( nameof( enabledProviders ) );
    private readonly ILogger<SagaCoordinatorBackgroundService> _logger = logger
                                                                       ?? throw new ArgumentNullException( nameof( logger ) );
    private readonly IRefreshReviewStore _refreshReviewStore = refreshReviewStore
                                                               ?? throw new ArgumentNullException( nameof( refreshReviewStore ) );

    private const string SagaCompletedChannel = "saga:completed";
    private const string LookupCompleteChannelPattern = "complete:*";
    private static readonly TimeSpan s_pollingInterval = TimeSpan.FromSeconds( 30 );
    private static readonly TimeSpan s_minimumSagaAge = TimeSpan.FromSeconds( 15 );
    private const int PollingBatchLimit = 100;

    /// <summary>
    /// Serializer options used when writing a cached provider result into per-provider saga state.
    /// Camel-cased and unindented so the stored JSON matches the rest of the saga payload. Must match
    /// the serialization the provider workers use for ProviderLookupState.ResultJson
    /// (QueueProcessorBackgroundService) so SagaResultCombiner can deserialize materialized cached
    /// results identically.
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Subscribes to the saga-completion and per-lookup channels, then runs the 30-second polling
    /// sweep until cancellation. The two subscriptions and the sweep are independent finalization
    /// triggers; the sweep is the backstop for completion events that Redis Pub/Sub dropped.
    /// </summary>
    /// <param name="stoppingToken">Signals when the host is shutting down.</param>
    /// <returns>A task that completes when the service stops and has unsubscribed.</returns>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        LogStarting(
            _logger,
            s_pollingInterval.TotalSeconds,
            s_minimumSagaAge.TotalSeconds
        );

        // Subscribe to saga completion events
        ISubscriber subscriber = _redis.GetSubscriber( );

        await subscriber.SubscribeAsync(
            RedisChannel.Literal( SagaCompletedChannel ),
            ( channel, message ) => {
                // Use channel to avoid analyzer warning
                if (message.HasValue && !channel.IsNullOrEmpty) {
                    if (_logger.IsEnabled( LogLevel.Information )) {
                        string channelStr = channel.ToString( );
                        string messageStr = message.ToString( );
                        LogReceivedSagaCompletionEvent(
                            _logger,
                            channelStr,
                            messageStr
                        );
                    }
                    // Fire-and-forget with exception handling since SubscribeAsync expects a void delegate
                    _ = Task.Run( async ( ) => {
                        try {
                            await ProcessSagaCompletionAsync( message.ToString( ), stoppingToken );
                        } catch (Exception ex) {
                            LogSagaCompletionError( _logger, ex, message.ToString( ) );
                        }
                    }, stoppingToken ).ConfigureAwait( false );
                }
            }
        );

        LogSubscribedToChannel( _logger, SagaCompletedChannel );

        // Subscribe to lookup complete events for partial results
        await subscriber.SubscribeAsync(
            RedisChannel.Pattern( LookupCompleteChannelPattern ),
            async ( channel, _ ) => {
                // Extract lookup key from channel (format: complete:{lookupKey})
                string channelStr = channel.ToString( );
                LogReceivedLookupCompletionEvent( _logger, channelStr );
                if (!channelStr.StartsWith( "complete:", StringComparison.Ordinal )) {
                    LogIgnoringChannel( _logger, channelStr );
                    return;
                }

                string lookupKey = channelStr["complete:".Length..];
                string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );

                try {
                    await ProcessLookupCompletionAsync( sagaId, stoppingToken );
                } catch (Exception ex) {
                    LogLookupCompletionError( _logger, ex, lookupKey );
                }
            }
        );
        LogSubscribedToPattern( _logger, LookupCompleteChannelPattern );

        LogFullyInitialized( _logger );

        // Also poll periodically for any missed completions
        int pollCycleCount = 0;
        while (!stoppingToken.IsCancellationRequested) {
            pollCycleCount++;
            LogStartingPollingCycle( _logger, pollCycleCount );

            try {
                await PollForCompletedSagasAsync( stoppingToken );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                LogPollingError( _logger, ex, pollCycleCount );
            }

            LogCompletedPollingCycle( _logger, pollCycleCount, s_pollingInterval.TotalSeconds );
            await Task.Delay( s_pollingInterval, stoppingToken );
        }

        // Unsubscribe on shutdown
        await subscriber.UnsubscribeAsync( RedisChannel.Literal( SagaCompletedChannel ) );
        await subscriber.UnsubscribeAsync( RedisChannel.Pattern( LookupCompleteChannelPattern ) );

        LogStopping( _logger );
    }

    /// <summary>
    /// Handles a saga-level completion event. Loads the saga, returns early if it is missing, not yet
    /// complete, or already finalized, otherwise combines the provider results and either defers for
    /// secondary lookups (writing a partial result) or writes the final result.
    /// </summary>
    /// <param name="sagaId">The id of the saga reported as complete.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when the saga has been processed.</returns>
    private async Task ProcessSagaCompletionAsync( string sagaId, CancellationToken ct ) {
        try {
            LogProcessingSagaCompletion( _logger, sagaId );

            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null) {
                LogSagaNotFound( _logger, sagaId );
                return;
            }

            LogSagaState(
                _logger,
                sagaId,
                saga.IsComplete,
                saga.FinalResultUri ?? "(none)",
                saga.ProviderStates.Count
            );

            if (!saga.IsComplete) {
                LogSagaNotComplete( _logger, sagaId );
                return;
            }

            if (!string.IsNullOrEmpty( saga.FinalResultUri )) {
                LogSagaAlreadyFinalized( _logger, sagaId, saga.FinalResultUri );
                return;
            }

            // Extract the result to check for external ID and queue secondary lookups
            MediaLinkResult? result = _resultCombiner.CombineResults( saga );
            if (result is not null) {
                bool queuedSecondaryLookups = await CheckCacheAndQueueSecondaryLookupsAsync( result, saga, ct );
                if (queuedSecondaryLookups) {
                    // Write initial result as partial and notify waiters immediately
                    // while secondary lookups continue in the background
                    LogWaitingForSecondaryLookups( _logger, sagaId );
                    await WriteResultAsync( saga, terminal: false, ct );
                    return;
                }
            }

            await WriteResultAsync( saga, terminal: true, ct );
        } catch (Exception ex) {
            LogFailedToProcessSaga( _logger, ex, sagaId );
        }
    }

    /// <summary>
    /// Handles a per-lookup completion event for a single saga. If the saga is now complete it follows
    /// the same combine/secondary-fan-out/finalize path as a saga-level event; if it is still only
    /// partial and no partial result has been written yet, it writes one so waiters get an interim
    /// answer.
    /// </summary>
    /// <param name="sagaId">The saga id derived from the <c>complete:</c> channel suffix.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when the lookup completion has been processed.</returns>
    private async Task ProcessLookupCompletionAsync( string sagaId, CancellationToken ct ) {
        try {
            LogProcessingLookupCompletion( _logger, sagaId );

            LookupSagaState? saga = await _sagaManager.GetAsync( sagaId, ct );

            if (saga is null) {
                LogSagaNotFoundForLookup( _logger, sagaId );
                return;
            }

            LogSagaStateForLookup(
                _logger,
                sagaId,
                saga.IsComplete,
                saga.IsPartial,
                saga.PartialResultUri ?? "(none)"
            );

            // If saga is complete, check cache and queue secondary lookups, then write final result
            if (saga.IsComplete) {
                // Extract the result from the saga to check for external ID
                MediaLinkResult? result = _resultCombiner.CombineResults(saga);

                if (result is not null) {
                    // Check cache for related data and queue secondary lookups for missing providers
                    bool queuedSecondaryLookups = await CheckCacheAndQueueSecondaryLookupsAsync( result, saga, ct );
                    if (queuedSecondaryLookups) {
                        // Write initial result as partial and notify waiters immediately
                        // while secondary lookups continue in the background
                        LogWaitingForSecondaryLookups( _logger, sagaId );
                        await WriteResultAsync( saga, terminal: false, ct );
                        return;
                    }
                }

                if (string.IsNullOrEmpty( saga.FinalResultUri )) {
                    await WriteResultAsync( saga, terminal: true, ct );
                }
                return;
            }

            // If saga is partial, handle partial result
            if (saga.IsPartial && string.IsNullOrEmpty( saga.PartialResultUri )) {
                await WriteResultAsync( saga, terminal: false, ct );
            }
        } catch (Exception ex) {
            LogFailedLookupCompletion( _logger, ex, sagaId );
        }
    }

    /// <summary>
    /// The polling-sweep backstop. Queries the saga state manager for sagas that are complete but have
    /// no final-result URI and are at least the minimum age old, then finalizes each (writing a partial
    /// result first if one is owed). A failure on a single saga is logged and skipped so the rest of the
    /// batch still runs; the saga is retried on the next cycle. This covers completion events that Redis
    /// Pub/Sub never delivered.
    /// </summary>
    /// <param name="ct">A token that cancels the sweep.</param>
    /// <returns>A task that completes when the sweep has processed the batch.</returns>
    /// <exception cref="OperationCanceledException">Propagated when <paramref name="ct"/> is cancelled.</exception>
    private async Task PollForCompletedSagasAsync( CancellationToken ct ) {
        // Check for cancellation
        ct.ThrowIfCancellationRequested( );

        LogPollingForSagas( _logger, s_minimumSagaAge.TotalSeconds, PollingBatchLimit );

        try {
            // Query for completed but unfinalized sagas using the pending index
            IReadOnlyList<LookupSagaState> unfinalizedSagas = await _sagaManager.GetPendingReconciliationAsync(
                s_minimumSagaAge,
                PollingBatchLimit,
                ct
            );

            if (unfinalizedSagas.Count == 0) {
                LogNoUnfinalizedSagas( _logger );
                return;
            }

            LogFoundUnfinalizedSagas( _logger, unfinalizedSagas.Count );

            // Process each saga - these were missed by Pub/Sub
            foreach (LookupSagaState saga in unfinalizedSagas) {
                ct.ThrowIfCancellationRequested( );

                try {
                    if (!string.IsNullOrWhiteSpace( saga.FinalResultUri )) {
                        _ = await ReleaseDurableResultAsync( saga, saga.FinalResultUri, ct );
                        continue;
                    }

                    // Run the same check as the Pub/Sub handlers - a completion missed by
                    // Pub/Sub must not finalize a one-provider result as complete while
                    // other providers still need secondary lookups
                    MediaLinkResult? result = _resultCombiner.CombineResults( saga );
                    if (result is not null) {
                        bool queuedSecondaryLookups = await CheckCacheAndQueueSecondaryLookupsAsync( result, saga, ct );
                        if (queuedSecondaryLookups) {
                            // Write initial result as partial and notify waiters immediately
                            // while secondary lookups continue in the background
                            LogWaitingForSecondaryLookups( _logger, saga.SagaId );
                            await WriteResultAsync( saga, terminal: false, ct );
                            continue;
                        }
                    }

                    // Check if saga is partial and needs partial result written first
                    if (saga.IsPartial && string.IsNullOrEmpty( saga.PartialResultUri )) {
                        LogWritingPartialDuringPolling( _logger, saga.SagaId );
                        await WriteResultAsync( saga, terminal: false, ct );
                    }

                    // Write final result
                    LogFinalizingDuringPolling( _logger, saga.SagaId );
                    await WriteResultAsync( saga, terminal: true, ct );
                } catch (Exception ex) {
                    LogFailedDuringPolling( _logger, ex, saga.SagaId );
                    // Continue with next saga - don't fail the entire polling cycle
                }
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogQueryError( _logger, ex );
        }
    }

    /// <summary>
    /// Writes the saga result to the ATProto PDS. Drives both the terminal (all-providers-complete)
    /// and non-terminal (partial, while secondaries are still outstanding) paths through a single
    /// unified sequence: combine → generation CAS → PDS write → record URI → cache → release dedup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write-generation compare-and-set ensures each provider-count level is written at most once,
    /// bounding total PDS writes to the number of providers. When the CAS fails (stored generation
    /// already at or above the requested generation) the method returns immediately with no write.
    /// </para>
    /// <para>
    /// On the terminal path, the saga finalize-claim operation provides the
    /// exactly-once guarantee for post-write bookkeeping (URI recording, partial-flag clear, dedup
    /// release). On the non-terminal path the write-generation CAS alone prevents double writes and
    /// no finalize claim is used.
    /// </para>
    /// <para>
    /// On a pre-durability PDS write failure, the write generation is reset to its prior value so
    /// the next trigger can re-advance and re-write. After the durability line the generation is
    /// retained and the finalize claim (terminal path) is not released, preventing re-entrant writes
    /// against an already-recorded URI.
    /// </para>
    /// </remarks>
    /// <param name="saga">The saga to write a result for.</param>
    /// <param name="terminal">
    /// <see langword="true"/> when every provider has reported in and the result is final;
    /// <see langword="false"/> when secondary lookups are still outstanding.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when the write (or its failure cleanup) is done.</returns>
    private async Task WriteResultAsync( LookupSagaState saga, bool terminal, CancellationToken ct ) {
        if (string.IsNullOrWhiteSpace( saga.InstanceToken )) {
            return;
        }
        string instanceToken = saga.InstanceToken;
        // A cache/materialization race may have replaced this saga after the completion trigger
        // read. Re-check the instance fence before any PDS write so the stale handler stays silent.
        LookupSagaState? currentSaga = await _sagaManager.GetAsync( saga.SagaId, ct );
        if (currentSaga is null || currentSaga.InstanceToken != instanceToken) {
            return;
        }

        // Background refreshes are finalized through the refresh review lifecycle. A partial
        // materialization would clear/overwrite the pending context while the refresh is still
        // in flight, so matching current-instance refresh contexts must wait for terminal state.
        if (!terminal) {
            IReadOnlyList<RefreshReviewEntry> pendingRefresh = await _refreshReviewStore.GetPendingForSagaAsync(
                saga.SagaId, ct ) ?? [];
            if (pendingRefresh.Any( entry => entry.InstanceToken == instanceToken )) {
                return;
            }
        }

        if (terminal) {
            LogAssemblingFinalResult( _logger, saga.SagaId, saga.ProviderStates.Count );
        } else if (_logger.IsEnabled( LogLevel.Information )) {
            int completedCount = saga.ProviderStates.Count( kv => kv.Value.IsComplete );
            int totalCount = saga.ProviderStates.Count;
            LogAssemblingPartialResult( _logger, saga.SagaId, completedCount, totalCount );
        }

        if (terminal) {
            // Every terminal path, including the no-result review path, is fenced by the
            // instance-scoped finalize claim before it can mutate review, deduplication, or saga state.
            bool claimed = await _sagaManager.TryClaimFinalizeAsync( saga.SagaId, instanceToken, ct );
            if (!claimed) {
                LogFinalizationClaimLost( _logger, saga.SagaId );
                QueueMetrics.RecordSagaLifecycleOutcome( "finalize_claim_lost" );
                return;
            }

        }

        MediaLinkResult? finalResult = _resultCombiner.CombineResults( saga, allowIncomplete: !terminal );

        if (finalResult is null) {
            if (terminal) {
                // Preserve stale-refresh context for operator review before discarding the saga.
                // The store is idempotent, so the polling backstop can safely retry this sequence.
                LogNoSuccessfulResults( _logger, saga.SagaId );
                try {
                    IReadOnlyList<RefreshReviewEntry> pending = [.. (await _refreshReviewStore.GetPendingForSagaAsync( saga.SagaId, ct ) ?? [])
                        .Where( entry => entry.InstanceToken == instanceToken )];
                    foreach (RefreshReviewEntry entry in pending) {
                        await _refreshReviewStore.MarkUnresolvedAsync(
                            entry,
                            "All provider refresh legs completed without a result.",
                            ct );
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    _ = await _sagaManager.TryReleaseFinalizeClaimAsync(
                        saga.SagaId, instanceToken, CancellationToken.None );
                    throw;
                } catch (Exception reviewEx) {
                    // Review persistence is operational bookkeeping. A Redis outage must not
                    // strand a terminal saga or leave its deduplication waiters blocked.
                    LogRefreshReviewPersistenceFailed( _logger, reviewEx, saga.SagaId );
                }
                try {
                    await _deduplicator.ReleaseAsync( saga.LookupKey, null, ct );
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    _ = await _sagaManager.TryReleaseFinalizeClaimAsync(
                        saga.SagaId, instanceToken, CancellationToken.None );
                    throw;
                } catch (Exception releaseEx) {
                    LogFailedToWriteFinal( _logger, releaseEx, saga.SagaId );
                    QueueMetrics.RecordSagaLifecycleOutcome( "dedup_release_failed" );
                    _ = await _sagaManager.TryReleaseFinalizeClaimAsync(
                        saga.SagaId, instanceToken, CancellationToken.None );
                    return;
                }
                _ = await _sagaManager.TryDeleteAsync( saga.SagaId, instanceToken, ct );
                QueueMetrics.RecordSagaLifecycleOutcome( "terminal_zero_result" );
            } else {
                // No successful results yet on the non-terminal path: nothing to write.
                // More providers are still outstanding; wait for the next trigger.
                LogNoSuccessfulPartialResults( _logger, saga.SagaId );
            }
            return;
        }

        if (terminal) {
            // Terminal path: gated above by the finalize claim only. The generation CAS is not
            // used here because the generation counts successful providers, and a saga that
            // completes via the last leg failing has the same generation it had when the
            // previous partial was written — the CAS would refuse to advance and the saga
            // would never finalize. The finalize claim (HSETNX) provides the single-winner
            // guarantee for this path.
            bool pdsWritten = false;
            bool uriRecorded = false;
            string? durableRecordUri = null;

            try {
                string recordUri = await _atProtoStorage.StoreMediaLinkResultAsync( finalResult, ct );
                durableRecordUri = recordUri;
                pdsWritten = true;

                LogWroteFinalToAtProto( _logger, saga.SagaId, recordUri );

                // Durability line: once this returns the URI is durably stored. A failure after
                // this point must NOT release the claim — re-entering would issue a second PDS
                // write against an already-recorded URI.
                SagaFinalResultWriteOutcome writeOutcome = await _sagaManager.TrySetFinalResultUriAsync(
                    saga.SagaId, recordUri, instanceToken, ct );
                if (writeOutcome == SagaFinalResultWriteOutcome.InstanceMismatch) {
                    // The PDS write belongs to the old generation. Never publish it on the shared
                    // lookup channel: waiters may now belong to the replacement saga.
                    LogFinalizationClaimLost( _logger, saga.SagaId );
                    QueueMetrics.RecordSagaLifecycleOutcome( "final_uri_instance_mismatch" );
                    return;
                }
                if (writeOutcome == SagaFinalResultWriteOutcome.Conflict) {
                    // A same-generation finalizer won with a different URI. Publish only the URI
                    // Redis says is authoritative; the duplicate PDS record is intentionally orphaned.
                    LookupSagaState? authoritative = await _sagaManager.GetAsync( saga.SagaId, ct );
                    if (authoritative?.InstanceToken == instanceToken
                        && !string.IsNullOrWhiteSpace( authoritative.FinalResultUri )) {
                        _ = await ReleaseDurableResultAsync(
                            authoritative, authoritative.FinalResultUri, ct );
                    }
                    QueueMetrics.RecordSagaLifecycleOutcome( "final_uri_conflict" );
                    return;
                }
                uriRecorded = true;

                try {
                    await _refreshReviewStore.CompleteAsync( saga.SagaId, instanceToken, ct );
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception cleanupEx) {
                    // Pending context has the saga TTL and self-expires. A cleanup failure must
                    // not turn an already-durable PDS write into a failed finalization.
                    LogRefreshReviewPersistenceFailed( _logger, cleanupEx, saga.SagaId );
                }

                if (!await ReleaseDurableResultAsync( saga, recordUri, ct )) {
                    return;
                }

                LogSuccessfullyFinalized( _logger, saga.SagaId, finalResult.Results.Count );
                QueueMetrics.RecordSagaLifecycleOutcome( "terminal_success" );

                // Best-effort: index after releasing waiters so a cache failure never delays
                // wakeup. Swallow all exceptions — the result is already durably written.
                try {
                    await _cacheRepository.IndexResultAsync( finalResult, recordUri, ct );
                    LogCachedFinalResult( _logger, saga.SagaId );
                } catch (Exception indexEx) {
                    LogPostReleaseIndexFailed( _logger, indexEx, saga.SagaId );
                }
            } catch (OperationCanceledException) {
                // Before the durability line: release the claim so the next host restart can
                // re-finalize. After the durability line: retain the claim — re-entering would
                // issue a second PDS write against an already-recorded URI. The dedup lock is
                // not released here in either case.
                if (!pdsWritten) {
                    _ = await _sagaManager.TryReleaseFinalizeClaimAsync( saga.SagaId, instanceToken, CancellationToken.None );
                } else if (durableRecordUri is not null) {
                    _ = await RecoverDurableResultAsync(
                        saga, durableRecordUri, uriRecorded, CancellationToken.None );
                }
                throw;
            } catch (Exception ex) {
                LogFailedToWriteFinal( _logger, ex, saga.SagaId );

                // Only release the claim before the durability line. After the URI is recorded,
                // retaining the claim prevents a re-entrant PDS write; releasing the dedup lock
                // with null would hand waiters an empty result for a saga that succeeded.
                if (!pdsWritten) {
                    _ = await _sagaManager.TryReleaseFinalizeClaimAsync( saga.SagaId, instanceToken, ct );
                    await _deduplicator.ReleaseAsync( saga.LookupKey, null, ct );
                } else if (durableRecordUri is not null) {
                    _ = await RecoverDurableResultAsync(
                        saga, durableRecordUri, uriRecorded, CancellationToken.None );
                }
            }
        } else {
            // Non-terminal (partial) path: gated by the write-generation CAS. This bounds the
            // total partial writes to ≤ the number of distinct successful-provider-count levels
            // observed, which is ≤ the number of providers N.
            int generation = finalResult.Results.Count;

            bool advanced = await _sagaManager.TryAdvanceWriteGenerationAsync( saga.SagaId, generation, instanceToken, ct );
            if (!advanced) {
                return;
            }

            finalResult.RateLimitedProviders = saga.RateLimitInfo?.Select( r => r.Provider ).ToList( );

            bool uriRecorded = false;

            try {
                string recordUri = await _atProtoStorage.StoreMediaLinkResultAsync( finalResult, ct );

                LogWrotePartialToAtProto( _logger, saga.SagaId, recordUri );

                bool recorded = await _sagaManager.TrySetPartialResultUriAsync( saga.SagaId, recordUri, instanceToken, ct );
                if (!recorded) {
                    return;
                }
                uriRecorded = true;

                await _deduplicator.ReleaseAsync( saga.LookupKey, recordUri, ct );

                LogSuccessfullyWrotePartial( _logger, saga.SagaId, finalResult.Results.Count );

                // Best-effort: index after releasing waiters so a cache failure never delays
                // wakeup. Swallow all exceptions — the result is already durably written.
                try {
                    await _cacheRepository.IndexResultAsync( finalResult, recordUri, ct );
                } catch (Exception indexEx) {
                    LogPostReleaseIndexFailed( _logger, indexEx, saga.SagaId );
                }
            } catch (OperationCanceledException) {
                // Before the durability line: reset the generation so the next trigger can
                // re-advance and re-write. After the durability line: retain — re-entering
                // would issue a second partial write against a URI that is already recorded.
                // The dedup lock is not released in either case.
                if (!uriRecorded) {
                    _ = await _sagaManager.TryResetWriteGenerationAsync( saga.SagaId, generation, generation - 1, instanceToken, CancellationToken.None );
                }
                throw;
            } catch (Exception ex) {
                LogFailedToWritePartial( _logger, ex, saga.SagaId );

                // Only reset the generation before the durability line. After the URI is
                // recorded, retaining the generation prevents a re-entrant duplicate partial
                // write.
                if (!uriRecorded) {
                    _ = await _sagaManager.TryResetWriteGenerationAsync( saga.SagaId, generation, generation - 1, instanceToken, ct );
                }
            }
        }
    }

    private async Task<bool> RecoverDurableResultAsync(
        LookupSagaState saga,
        string durableRecordUri,
        bool uriWasRecorded,
        CancellationToken ct
    ) {
        if (string.IsNullOrWhiteSpace( saga.InstanceToken )) {
            QueueMetrics.RecordSagaLifecycleOutcome( "durable_release_instance_mismatch" );
            return false;
        }

        string instanceToken = saga.InstanceToken;
        try {
            LookupSagaState? authoritative = await _sagaManager.GetAsync( saga.SagaId, ct );
            if (authoritative is null
                || string.IsNullOrWhiteSpace( authoritative.InstanceToken )
                || authoritative.InstanceToken != instanceToken) {
                QueueMetrics.RecordSagaLifecycleOutcome( "durable_release_instance_mismatch" );
                return false;
            }

            string? authoritativeUri = authoritative.FinalResultUri;
            if (string.IsNullOrWhiteSpace( authoritativeUri ) && !uriWasRecorded) {
                SagaFinalResultWriteOutcome retryOutcome = await _sagaManager.TrySetFinalResultUriAsync(
                    saga.SagaId, durableRecordUri, instanceToken, ct );
                if (retryOutcome == SagaFinalResultWriteOutcome.InstanceMismatch) {
                    QueueMetrics.RecordSagaLifecycleOutcome( "durable_release_instance_mismatch" );
                    return false;
                }
                if (retryOutcome == SagaFinalResultWriteOutcome.Conflict) {
                    authoritative = await _sagaManager.GetAsync( saga.SagaId, ct );
                    if (authoritative is null || authoritative.InstanceToken != instanceToken
                        || string.IsNullOrWhiteSpace( authoritative.FinalResultUri )) {
                        return false;
                    }
                    authoritativeUri = authoritative.FinalResultUri;
                } else {
                    authoritativeUri = durableRecordUri;
                }
            }

            if (string.IsNullOrWhiteSpace( authoritativeUri )) {
                return false;
            }

            return await ReleaseDurableResultAsync( authoritative, authoritativeUri, ct );
        } catch (Exception recoveryEx) {
            LogFailedToWriteFinal( _logger, recoveryEx, saga.SagaId );
            QueueMetrics.RecordSagaLifecycleOutcome( "durable_release_recovery_failed" );
            // The PDS record key is deterministic. Releasing the finalize claim lets the pending
            // reconciliation sweep safely repeat the PutRecord if Redis was unavailable for the
            // URI CAS, rather than leaving the saga permanently claimed and undiscoverable.
            try {
                _ = await _sagaManager.TryReleaseFinalizeClaimAsync(
                    saga.SagaId, instanceToken, CancellationToken.None );
            } catch (Exception claimEx) {
                LogFailedToWriteFinal( _logger, claimEx, saga.SagaId );
            }
            return false;
        }
    }

    private async Task<bool> ReleaseDurableResultAsync(
        LookupSagaState saga,
        string recordUri,
        CancellationToken ct,
        bool removePendingIndex = true
    ) {
        try {
            await _deduplicator.ReleaseAsync( saga.LookupKey, recordUri, ct );
            if (removePendingIndex) {
                await _sagaManager.RemoveFromPendingIndexAsync( saga.SagaId, ct );
            }
            QueueMetrics.RecordSagaLifecycleOutcome( "dedup_released" );
            return true;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogFailedToWriteFinal( _logger, ex, saga.SagaId );
            QueueMetrics.RecordSagaLifecycleOutcome( "dedup_release_failed" );
            return false;
        }
    }

    /// <summary>
    /// Test-only entry point that delegates directly to <see cref="WriteResultAsync"/> for the
    /// terminal (final-result) path, so integration tests can drive the finalize claim path without
    /// running the full <see cref="ExecuteAsync"/> polling loop. Not used in production code paths.
    /// </summary>
    /// <param name="saga">The saga to finalize.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when finalization (or its failure cleanup) is done.</returns>
    internal Task InvokeFinalizeForTestAsync( LookupSagaState saga, CancellationToken ct )
        => WriteResultAsync( saga, terminal: true, ct );

    /// <summary>
    /// Test-only entry point that delegates directly to <see cref="WriteResultAsync"/>, parameterized
    /// by <paramref name="terminal"/>, so tests can drive both the final-result and partial-result
    /// write paths without running the full <see cref="ExecuteAsync"/> polling loop. Not used in
    /// production code paths.
    /// </summary>
    /// <param name="saga">The saga to write a result for.</param>
    /// <param name="terminal">
    /// <see langword="true"/> to exercise the final-result path; <see langword="false"/> for the
    /// partial-result path.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when the write (or its failure cleanup) is done.</returns>
    internal Task InvokeWriteForTestAsync( LookupSagaState saga, bool terminal, CancellationToken ct )
        => WriteResultAsync( saga, terminal, ct );

    /// <summary>
    /// Publishes a saga id to the literal <c>saga:completed</c> Pub/Sub channel so the coordinator
    /// will finalize it. Exposed as a static helper so other components can signal completion without
    /// holding a reference to a running coordinator instance.
    /// </summary>
    /// <param name="redis">The Redis connection to publish through.</param>
    /// <param name="sagaId">The id of the saga to announce as complete.</param>
    /// <returns>A task that completes once the message has been published.</returns>
    public static async Task PublishSagaCompletedAsync( IConnectionMultiplexer redis, string sagaId ) {
        ISubscriber subscriber = redis.GetSubscriber( );
        _ = await subscriber.PublishAsync( RedisChannel.Literal( SagaCompletedChannel ), sagaId );
    }

    /// <summary>
    /// Decides whether to fan out secondary provider lookups and, if so, queues them. When the first
    /// provider returned an external id (ISRC for tracks, UPC for albums), the coordinator wants the
    /// other enabled providers too. It checks the ISRC/UPC cache first and materializes any cached
    /// provider results straight into the saga to avoid re-querying; only the providers that are still
    /// missing are queued as secondary <see cref="QueuedLookupRequest"/>s. A single-winner marker
    /// (<see cref="Contracts.Interfaces.ISagaStateManager.TryMarkSecondariesQueuedAsync(string, string, CancellationToken)"/>)
    /// guards against duplicate fan-out by concurrent handlers. ISRC/UPC-origin sagas are skipped
    /// because they are already keyed by the canonical id.
    /// </summary>
    /// <param name="result">The combined result whose first provider supplies the external id.</param>
    /// <param name="originalSaga">The saga being processed; its provider states may be extended in place.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> if finalization should be deferred (secondaries were queued, or another
    /// handler already queued them); <see langword="false"/> if the saga can be finalized now.
    /// </returns>
    private async Task<bool> CheckCacheAndQueueSecondaryLookupsAsync(
        MediaLinkResult result,
        LookupSagaState originalSaga,
        CancellationToken ct
    ) {
        if (string.IsNullOrWhiteSpace( originalSaga.InstanceToken )) {
            return false;
        }
        string instanceToken = originalSaga.InstanceToken;
        // Don't trigger secondary lookups from secondary lookups (ISRC/UPC lookups)
        // Only provider-specific lookups (SpotifyLookup, AppleMusicLookup, TidalLookup) should trigger secondary lookups
        if (originalSaga.LookupType is LookupRequestType.IsrcLookup or LookupRequestType.UpcLookup) {
            return false;
        }

        // Get the first result to extract external ID
        MusicLookupResult? firstResult = result.Results.Values.FirstOrDefault();
        if (firstResult is null || string.IsNullOrWhiteSpace( firstResult.ExternalId )) {
            LogNoExternalId( _logger, originalSaga.SagaId );
            return false;
        }

        string externalId = firstResult.ExternalId;
        bool isAlbum = firstResult.IsAlbum ?? false;
        SupportedProviders initialProvider = result.Results.Keys.First();

        // Determine which providers still need lookups (not the initial provider)
        List<SupportedProviders> otherProviders = [.. _enabledProviders.Where(p => p != initialProvider && !result.Results.ContainsKey(p))];

        if (otherProviders.Count == 0) {
            LogNoOtherProviders( _logger, originalSaga.SagaId );
            return false;
        }

        // Check cache for existing data using ISRC/UPC
        LookupRequestType lookupType = isAlbum ? LookupRequestType.UpcLookup : LookupRequestType.IsrcLookup;
        (MediaLinkResult cachedResult, string recordUri, bool isStale)? cached = isAlbum
            ? await _cacheRepository.TryGetCachedResultByUPCAsync(externalId)
            : await _cacheRepository.TryGetCachedResultByISRCAsync(externalId);

        // Determine which providers we already have data for (from result, cache, or already in saga)
        HashSet<SupportedProviders> providersWithData = [initialProvider];

        // Add providers already initialized in the saga
        foreach (SupportedProviders provider in originalSaga.ProviderStates.Keys) {
            _ = providersWithData.Add( provider );
        }

        // Add providers with cached data
        if (cached.HasValue && !cached.Value.isStale) {
            foreach ((SupportedProviders provider, MusicLookupResult cachedProviderResult) in cached.Value.cachedResult.Results) {
                _ = providersWithData.Add( provider );

                // Materialize the cached result into the saga as a completed provider state.
                // The final result is assembled from saga provider states only, and the
                // ATProto record (deterministic rkey) plus cache pointers are written as a
                // full replace - without this, cached providers' data would be dropped and
                // a richer existing record overwritten by a poorer one.
                if (!originalSaga.ProviderStates.ContainsKey( provider )) {
                    ProviderLookupState cachedState = new(
                        Provider: provider,
                        IsComplete: true,
                        IsSuccess: true,
                        ResultJson: JsonSerializer.Serialize( cachedProviderResult, s_jsonOptions ),
                        CompletedAt: DateTimeOffset.UtcNow,
                        ErrorMessage: null
                    );

                    bool cachedWritten = await _sagaManager.TryUpdateProviderStateAsync( originalSaga.SagaId, cachedState, instanceToken, ct );
                    if (!cachedWritten) {
                        // The saga instance was replaced while this handler was materializing
                        // cache data. Defer finalization; the replacement instance owns the next
                        // completion trigger and must not be overwritten by this stale handler.
                        return true;
                    }

                    // Keep the in-memory saga consistent so the partial/final result
                    // written by the caller includes the materialized data
                    originalSaga.ProviderStates[provider] = cachedState;

                    string providerStr = provider.ToString( );
                    LogMaterializedCachedProvider( _logger, providerStr, originalSaga.SagaId, externalId );
                }
            }

            if (_logger.IsEnabled( LogLevel.Debug )) {
                string providersStr = string.Join( ", ", providersWithData );
                LogCacheHitForExternalId( _logger, externalId, providersStr );
            }
        }

        // Find providers that need lookups (not already in saga or cache)
        List<SupportedProviders> providersToQueue = [.. otherProviders.Where(p => !providersWithData.Contains(p))];

        if (providersToQueue.Count == 0) {
            LookupSagaState? proof = await _sagaManager.GetAsync( originalSaga.SagaId, ct );
            if (proof is null || proof.InstanceToken != instanceToken) {
                return true;
            }
            LogAllProvidersInCache( _logger, originalSaga.SagaId, externalId );
            return false;
        }

        // Add the new providers to the ORIGINAL saga (don't create separate sagas) and mark
        // the saga partial so waiting callers can distinguish the upcoming partial result
        // from a final one and keep waiting for the secondary lookups. Both writes are
        // idempotent and deliberately run BEFORE the marker claim below: once either racing
        // handler reports "secondaries pending" (causing its caller to publish the partial
        // and release waiters), the saga is already guaranteed to read as incomplete and
        // partial. Otherwise the marker loser could publish while IsPartial is still false
        // and no pending states exist, letting a waiting orchestrator mistake the
        // one-provider result for a final one (isFinal = IsComplete && !IsPartial).
        bool fenced = await _sagaManager.TryInitializeProviderStatesAsync( originalSaga.SagaId, providersToQueue, instanceToken, ct );
        if (!fenced) {
            return false;
        }
        if (!await _sagaManager.TrySetIsPartialAsync( originalSaga.SagaId, true, instanceToken, ct )) {
            return false;
        }

        if (_logger.IsEnabled( LogLevel.Information )) {
            string providerNames = string.Join( ", ", providersToQueue );
            LogAddedProvidersToSaga( _logger, originalSaga.SagaId, providerNames );
        }

        // Atomically claim the right to enqueue secondaries - the only non-idempotent step.
        // The worker publishes both saga:completed and complete:{key}, so both handlers can
        // race through here and would otherwise enqueue every secondary twice. The loser
        // still reports "secondaries pending" so its caller defers finalization.
        bool markerAcquired = await _sagaManager.TryMarkSecondariesQueuedAsync( originalSaga.SagaId, instanceToken, ct );
        if (!markerAcquired) {
            LogSecondariesAlreadyQueued( _logger, originalSaga.SagaId );
            return true;
        }

        // Interactive priority only when the saga originated from an interactive caller
        // who is actively waiting for these secondary results to complete the saga (see
        // LookupOrchestrator wait-for-final logic). Bulk/background-origin sagas (e.g.
        // JetStream firehose) queue at Background so they stay out of the interactive lane.
        QueuePriority secondaryPriority = originalSaga.OriginPriority == QueuePriority.Interactive
            ? QueuePriority.Interactive
            : QueuePriority.Background;

        // Queue lookups for each provider using the ORIGINAL saga ID
        int queuedCount = 0;
        int failedCount = 0;
        foreach (SupportedProviders provider in providersToQueue) {
            try {
                // Create and queue the lookup request using the ORIGINAL saga ID
                QueuedLookupRequest secondaryRequest = new()
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    Provider = provider,
                    LookupType = lookupType,
                    LookupValue = externalId,
                    SagaId = originalSaga.SagaId,  // Use original saga ID!
                    SagaInstanceToken = instanceToken,
                    IsAlbum = isAlbum,
                    Title = firstResult.Title,
                    Artist = firstResult.Artist,
                    OriginPriority = originalSaga.OriginPriority
                };

                IRequestQueue<QueuedLookupRequest> queue = _queueResolver.GetQueue(provider);

                await queue.EnqueueAsync( secondaryRequest, secondaryPriority, ct );

                string lookupTypeStr = lookupType.ToString( );
                string providerStr = provider.ToString( );
                LogQueuedSecondaryLookup( _logger, lookupTypeStr, providerStr, externalId, originalSaga.SagaId );
                queuedCount++;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                string providerStr = provider.ToString( );
                LogFailedToQueueSecondary( _logger, ex, providerStr, externalId );

                // The one-time fan-out marker has already been claimed. Leaving this initialized
                // provider incomplete would make the saga permanently non-terminal because no
                // delivery exists to finish it and the marker prevents another fan-out. Record a
                // terminal enqueue failure for this leg before continuing with the other providers.
                ProviderLookupState enqueueFailure = new(
                    Provider: provider,
                    IsComplete: true,
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: $"Failed to enqueue {provider} secondary lookup."
                );
                if (!await _sagaManager.TryUpdateProviderStateAsync(
                    originalSaga.SagaId,
                    enqueueFailure,
                    instanceToken,
                    ct )) {
                    return true;
                }
                originalSaga.ProviderStates[provider] = enqueueFailure;
                failedCount++;
            }
        }

        if (failedCount > 0) {
            // Wake the coordinator after the failed legs have been made terminal. This is
            // essential when every enqueue failed because no worker delivery exists to emit a
            // later progress event; partial failures will also be rechecked safely.
            ISubscriber subscriber = _redis.GetSubscriber( );
            _ = await subscriber.PublishAsync(
                RedisChannel.Literal( SagaCompletedChannel ),
                originalSaga.SagaId );
        }

        if (queuedCount == 0) {
            LogNoSecondariesEnqueued( _logger, originalSaga.SagaId, externalId );
            // Every secondary leg now has a durable terminal enqueue failure, so no delivery can
            // produce another event. Let this invocation proceed directly to finalization instead
            // of writing an unnecessary partial generation first.
            return false;
        }

        // Pending provider states exist and the saga is marked partial, so the caller must
        // defer finalization while the successfully queued providers are still in flight.
        return true;
    }

    #region LoggerMessage Methods

    /// <summary>Logs that the coordinator is starting, with its polling interval and minimum saga age.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="pollingInterval">The polling-sweep interval in seconds.</param>
    /// <param name="minSagaAge">The minimum saga age, in seconds, considered by the sweep.</param>
    [LoggerMessage(
        EventId = LogEventIds.Starting,
        Level = LogLevel.Information,
        Message = "Saga coordinator starting with polling interval {PollingInterval}s and minimum saga age {MinSagaAge}s" )]
    private static partial void LogStarting( ILogger logger, double pollingInterval, double minSagaAge );

    /// <summary>Logs receipt of a saga-level completion event.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="channel">The channel the event arrived on.</param>
    /// <param name="message">The message payload (the saga id).</param>
    [LoggerMessage(
        EventId = LogEventIds.ReceivedSagaCompletionEvent,
        Level = LogLevel.Information,
        Message = "Received saga completion event on channel {Channel}: {Message}" )]
    private static partial void LogReceivedSagaCompletionEvent( ILogger logger, string channel, string message );

    /// <summary>Logs an error raised while handling a saga-level completion event.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="message">The message being processed when the error occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaCompletionError,
        Level = LogLevel.Error,
        Message = "Error processing saga completion for message {Message}" )]
    private static partial void LogSagaCompletionError( ILogger logger, Exception ex, string message );

    /// <summary>Logs that the coordinator subscribed to the saga-completion channel.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="channel">The channel that was subscribed to.</param>
    [LoggerMessage(
        EventId = LogEventIds.SubscribedToChannel,
        Level = LogLevel.Information,
        Message = "Subscribed to Redis channel {Channel} for saga completion events" )]
    private static partial void LogSubscribedToChannel( ILogger logger, string channel );

    /// <summary>Logs receipt of a per-lookup completion event.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="channel">The <c>complete:*</c> channel the event arrived on.</param>
    [LoggerMessage(
        EventId = LogEventIds.ReceivedLookupCompletionEvent,
        Level = LogLevel.Debug,
        Message = "Received lookup completion event on channel {Channel}" )]
    private static partial void LogReceivedLookupCompletionEvent( ILogger logger, string channel );

    /// <summary>Logs that a channel was ignored because it did not match the expected prefix.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="channel">The channel that was ignored.</param>
    [LoggerMessage(
        EventId = LogEventIds.IgnoringChannel,
        Level = LogLevel.Debug,
        Message = "Ignoring channel {Channel} - does not match expected pattern" )]
    private static partial void LogIgnoringChannel( ILogger logger, string channel );

    /// <summary>Logs an error raised while handling a per-lookup completion event.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="lookupKey">The lookup key being processed when the error occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.LookupCompletionError,
        Level = LogLevel.Error,
        Message = "Error processing lookup completion for {LookupKey}" )]
    private static partial void LogLookupCompletionError( ILogger logger, Exception ex, string lookupKey );

    /// <summary>Logs that the coordinator subscribed to the per-lookup channel pattern.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="pattern">The channel pattern that was subscribed to.</param>
    [LoggerMessage(
        EventId = LogEventIds.SubscribedToPattern,
        Level = LogLevel.Information,
        Message = "Subscribed to Redis channel pattern {Pattern} for lookup completion events" )]
    private static partial void LogSubscribedToPattern( ILogger logger, string pattern );

    /// <summary>Logs that all subscriptions are established and the polling loop is starting.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.FullyInitialized,
        Level = LogLevel.Information,
        Message = "Saga coordinator fully initialized, entering polling loop" )]
    private static partial void LogFullyInitialized( ILogger logger );

    /// <summary>Logs the start of a polling cycle.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="cycleCount">The 1-based cycle number.</param>
    [LoggerMessage(
        EventId = LogEventIds.StartingPollingCycle,
        Level = LogLevel.Debug,
        Message = "Starting polling cycle #{CycleCount}" )]
    private static partial void LogStartingPollingCycle( ILogger logger, int cycleCount );

    /// <summary>Logs an error raised during a polling cycle.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="cycleCount">The cycle number during which the error occurred.</param>
    [LoggerMessage(
        EventId = LogEventIds.PollingError,
        Level = LogLevel.Error,
        Message = "Error during saga completion polling in cycle #{CycleCount}" )]
    private static partial void LogPollingError( ILogger logger, Exception ex, int cycleCount );

    /// <summary>Logs the completion of a polling cycle and the interval before the next one.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="cycleCount">The cycle number that finished.</param>
    /// <param name="interval">The sleep interval, in seconds, before the next cycle.</param>
    [LoggerMessage(
        EventId = LogEventIds.CompletedPollingCycle,
        Level = LogLevel.Debug,
        Message = "Completed polling cycle #{CycleCount}, sleeping for {Interval}s" )]
    private static partial void LogCompletedPollingCycle( ILogger logger, int cycleCount, double interval );

    /// <summary>Logs that the coordinator is stopping.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Stopping,
        Level = LogLevel.Information,
        Message = "Saga coordinator stopping" )]
    private static partial void LogStopping( ILogger logger );

    /// <summary>Logs that a saga-completion event is being processed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.ProcessingSagaCompletion,
        Level = LogLevel.Information,
        Message = "Processing saga completion event for {SagaId}" )]
    private static partial void LogProcessingSagaCompletion( ILogger logger, string sagaId );

    /// <summary>Logs that a saga referenced by a completion event was not found.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id that could not be found.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaNotFound,
        Level = LogLevel.Warning,
        Message = "Saga {SagaId} not found for completion processing" )]
    private static partial void LogSagaNotFound( ILogger logger, string sagaId );

    /// <summary>Logs a diagnostic snapshot of a saga's state.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being described.</param>
    /// <param name="isComplete">Whether all initialized providers report complete.</param>
    /// <param name="finalResultUri">The final-result URI, or a placeholder when none.</param>
    /// <param name="providerCount">The number of provider states on the saga.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaState,
        Level = LogLevel.Information,
        Message = "Saga {SagaId} state: IsComplete={IsComplete}, FinalResultUri={FinalResultUri}, ProviderCount={ProviderCount}" )]
    private static partial void LogSagaState( ILogger logger, string sagaId, bool isComplete, string finalResultUri, int providerCount );

    /// <summary>Logs that a saga is not yet complete and finalization was skipped.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga that is not yet complete.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaNotComplete,
        Level = LogLevel.Information,
        Message = "Saga {SagaId} is not yet complete, skipping finalization" )]
    private static partial void LogSagaNotComplete( ILogger logger, string sagaId );

    /// <summary>Logs that a saga already has a final result and was skipped.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The already-finalized saga.</param>
    /// <param name="uri">The existing final-result URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaAlreadyFinalized,
        Level = LogLevel.Information,
        Message = "Saga {SagaId} already has final result at {Uri}, skipping" )]
    private static partial void LogSagaAlreadyFinalized( ILogger logger, string sagaId, string uri );

    /// <summary>Logs that processing a saga completion failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="sagaId">The saga that failed to process.</param>
    [LoggerMessage(
        EventId = LogEventIds.FailedToProcessSaga,
        Level = LogLevel.Error,
        Message = "Failed to process saga completion for {SagaId}" )]
    private static partial void LogFailedToProcessSaga( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that a per-lookup completion is being processed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga derived from the lookup-completion channel.</param>
    [LoggerMessage(
        EventId = LogEventIds.ProcessingLookupCompletion,
        Level = LogLevel.Information,
        Message = "Processing lookup completion for saga {SagaId}" )]
    private static partial void LogProcessingLookupCompletion( ILogger logger, string sagaId );

    /// <summary>Logs that the saga derived from a lookup-completion channel was not found.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id that could not be found.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaNotFoundForLookup,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} not found for lookup completion" )]
    private static partial void LogSagaNotFoundForLookup( ILogger logger, string sagaId );

    /// <summary>Logs a diagnostic snapshot of a saga's state during lookup-completion handling.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being described.</param>
    /// <param name="isComplete">Whether all initialized providers report complete.</param>
    /// <param name="isPartial">Whether the saga is currently in a partial state.</param>
    /// <param name="partialResultUri">The partial-result URI, or a placeholder when none.</param>
    [LoggerMessage(
        EventId = LogEventIds.SagaStateForLookup,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} state for lookup completion: IsComplete={IsComplete}, IsPartial={IsPartial}, PartialResultUri={PartialResultUri}" )]
    private static partial void LogSagaStateForLookup( ILogger logger, string sagaId, bool isComplete, bool isPartial, string partialResultUri );

    /// <summary>Logs that processing a lookup completion failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="sagaId">The saga that failed to process.</param>
    [LoggerMessage(
        EventId = LogEventIds.FailedLookupCompletion,
        Level = LogLevel.Error,
        Message = "Failed to process lookup completion for saga {SagaId}" )]
    private static partial void LogFailedLookupCompletion( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that the polling sweep is querying for completed-but-unfinalized sagas.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="minAge">The minimum saga age, in seconds, considered by the query.</param>
    /// <param name="limit">The maximum number of sagas returned per query.</param>
    [LoggerMessage(
        EventId = LogEventIds.PollingForSagas,
        Level = LogLevel.Debug,
        Message = "Polling for completed but unfinalized sagas (minimum age: {MinAge}s, batch limit: {Limit})" )]
    private static partial void LogPollingForSagas( ILogger logger, double minAge, int limit );

    /// <summary>Logs that the polling sweep found no unfinalized sagas this cycle.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoUnfinalizedSagas,
        Level = LogLevel.Debug,
        Message = "No unfinalized sagas found during polling" )]
    private static partial void LogNoUnfinalizedSagas( ILogger logger );

    /// <summary>Logs the number of unfinalized sagas the polling sweep found.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of sagas to process.</param>
    [LoggerMessage(
        EventId = LogEventIds.FoundUnfinalizedSagas,
        Level = LogLevel.Information,
        Message = "Polling found {Count} completed but unfinalized sagas to process" )]
    private static partial void LogFoundUnfinalizedSagas( ILogger logger, int count );

    /// <summary>Logs that a partial result is being written for a saga found during the sweep.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being written.</param>
    [LoggerMessage(
        EventId = LogEventIds.WritingPartialDuringPolling,
        Level = LogLevel.Debug,
        Message = "Writing partial result for saga {SagaId} discovered during polling" )]
    private static partial void LogWritingPartialDuringPolling( ILogger logger, string sagaId );

    /// <summary>Logs that a saga found during the sweep is being finalized.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being finalized.</param>
    [LoggerMessage(
        EventId = LogEventIds.FinalizingDuringPolling,
        Level = LogLevel.Debug,
        Message = "Finalizing saga {SagaId} discovered during polling" )]
    private static partial void LogFinalizingDuringPolling( ILogger logger, string sagaId );

    /// <summary>Logs that processing a saga during the sweep failed; it is retried next cycle.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="sagaId">The saga that failed to process.</param>
    [LoggerMessage(
        EventId = LogEventIds.FailedDuringPolling,
        Level = LogLevel.Error,
        Message = "Failed to process saga {SagaId} during polling, will retry next cycle" )]
    private static partial void LogFailedDuringPolling( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs an error raised while querying for unfinalized sagas during the sweep.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.QueryError,
        Level = LogLevel.Error,
        Message = "Error querying for unfinalized sagas during polling" )]
    private static partial void LogQueryError( ILogger logger, Exception ex );

    /// <summary>Logs that the final result is being assembled from per-provider state.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being finalized.</param>
    /// <param name="providerCount">The number of provider results being combined.</param>
    [LoggerMessage(
        EventId = LogEventIds.AssemblingFinalResult,
        Level = LogLevel.Information,
        Message = "Assembling final result for saga {SagaId} with {ProviderCount} provider results" )]
    private static partial void LogAssemblingFinalResult( ILogger logger, string sagaId, int providerCount );

    /// <summary>Logs that a saga completed with no successful results and is being cleaned up.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being cleaned up.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoSuccessfulResults,
        Level = LogLevel.Warning,
        Message = "Saga {SagaId} completed with no successful results, cleaning up" )]
    private static partial void LogNoSuccessfulResults( ILogger logger, string sagaId );

    /// <summary>Logs that a zero-result saga could not be persisted for operator review.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception raised by the review store.</param>
    /// <param name="sagaId">The terminal saga whose review entry was not persisted.</param>
    [LoggerMessage(
        EventId = LogEventIds.RefreshReviewPersistenceFailed,
        Level = LogLevel.Warning,
        Message = "Failed to persist zero-result saga {SagaId} for refresh review; continuing terminal cleanup" )]
    private static partial void LogRefreshReviewPersistenceFailed( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that this handler lost the finalize claim race and is deferring to the winner.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose finalize claim was already held.</param>
    [LoggerMessage(
        EventId = LogEventIds.FinalizationClaimLost,
        Level = LogLevel.Debug,
        Message = "Finalize claim for saga {SagaId} already held by another handler; skipping" )]
    private static partial void LogFinalizationClaimLost( ILogger logger, string sagaId );

    /// <summary>Logs that the final result was written to the ATProto PDS.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose final result was written.</param>
    /// <param name="recordUri">The ATProto record URI of the written result.</param>
    [LoggerMessage(
        EventId = LogEventIds.WroteFinalToAtProto,
        Level = LogLevel.Debug,
        Message = "Wrote final result for saga {SagaId} to ATProto: {RecordUri}" )]
    private static partial void LogWroteFinalToAtProto( ILogger logger, string sagaId, string recordUri );

    /// <summary>Logs that the final result was written into the media-link cache.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose final result was cached.</param>
    [LoggerMessage(
        EventId = LogEventIds.CachedFinalResult,
        Level = LogLevel.Debug,
        Message = "Cached final result for saga {SagaId}" )]
    private static partial void LogCachedFinalResult( ILogger logger, string sagaId );

    /// <summary>Logs that a saga was successfully finalized.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The finalized saga.</param>
    /// <param name="providerCount">The number of provider results in the final result.</param>
    [LoggerMessage(
        EventId = LogEventIds.SuccessfullyFinalized,
        Level = LogLevel.Information,
        Message = "Successfully finalized saga {SagaId} with {ProviderCount} provider results" )]
    private static partial void LogSuccessfullyFinalized( ILogger logger, string sagaId, int providerCount );

    /// <summary>Logs that writing a saga's final result failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="sagaId">The saga whose final write failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.FailedToWriteFinal,
        Level = LogLevel.Error,
        Message = "Failed to write final result for saga {SagaId}" )]
    private static partial void LogFailedToWriteFinal( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that an interim partial result is being assembled.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga being written.</param>
    /// <param name="completedCount">The number of providers that have completed.</param>
    /// <param name="totalCount">The total number of providers on the saga.</param>
    [LoggerMessage(
        EventId = LogEventIds.AssemblingPartialResult,
        Level = LogLevel.Information,
        Message = "Assembling partial result for saga {SagaId} with {CompletedCount}/{TotalCount} provider results" )]
    private static partial void LogAssemblingPartialResult( ILogger logger, string sagaId, int completedCount, int totalCount );

    /// <summary>Logs that no provider results are available yet, so no partial result was written.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga that has no successful results yet.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoSuccessfulPartialResults,
        Level = LogLevel.Debug,
        Message = "Saga {SagaId} has no successful provider results yet, waiting" )]
    private static partial void LogNoSuccessfulPartialResults( ILogger logger, string sagaId );

    /// <summary>Logs that a partial result was written to the ATProto PDS.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose partial result was written.</param>
    /// <param name="recordUri">The ATProto record URI of the written partial result.</param>
    [LoggerMessage(
        EventId = LogEventIds.WrotePartialToAtProto,
        Level = LogLevel.Debug,
        Message = "Wrote partial result for saga {SagaId} to ATProto: {RecordUri}" )]
    private static partial void LogWrotePartialToAtProto( ILogger logger, string sagaId, string recordUri );

    /// <summary>Logs that a partial result was successfully written.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose partial result was written.</param>
    /// <param name="providerCount">The number of provider results in the partial result.</param>
    [LoggerMessage(
        EventId = LogEventIds.SuccessfullyWrotePartial,
        Level = LogLevel.Information,
        Message = "Successfully wrote partial result for saga {SagaId} with {ProviderCount} provider results" )]
    private static partial void LogSuccessfullyWrotePartial( ILogger logger, string sagaId, int providerCount );

    /// <summary>Logs that writing a saga's partial result failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="sagaId">The saga whose partial write failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.FailedToWritePartial,
        Level = LogLevel.Error,
        Message = "Failed to write partial result for saga {SagaId}" )]
    private static partial void LogFailedToWritePartial( ILogger logger, Exception ex, string sagaId );

    /// <summary>Logs that the result had no external id, so secondary fan-out was skipped.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga with no external id.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoExternalId,
        Level = LogLevel.Information,
        Message = "No external ID in result for saga {SagaId}, skipping secondary lookups" )]
    private static partial void LogNoExternalId( ILogger logger, string sagaId );

    /// <summary>Logs that no other enabled providers remained, so secondary fan-out was skipped.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga with no other providers to query.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoOtherProviders,
        Level = LogLevel.Information,
        Message = "No other enabled providers for saga {SagaId}, skipping secondary lookups" )]
    private static partial void LogNoOtherProviders( ILogger logger, string sagaId );

    /// <summary>Logs a cache hit for an external id and which providers it covered.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="externalId">The ISRC or UPC that was matched in the cache.</param>
    /// <param name="providers">The providers whose data the cache supplied.</param>
    [LoggerMessage(
        EventId = LogEventIds.CacheHitForExternalId,
        Level = LogLevel.Debug,
        Message = "Cache hit for {ExternalId}, found data for providers: {Providers}" )]
    private static partial void LogCacheHitForExternalId( ILogger logger, string externalId, string providers );

    /// <summary>Logs that all other providers were already cached, so no secondary lookups were needed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose providers were fully cached.</param>
    /// <param name="externalId">The ISRC or UPC that resolved the cache hit.</param>
    [LoggerMessage(
        EventId = LogEventIds.AllProvidersInCache,
        Level = LogLevel.Information,
        Message = "All providers already in cache for saga {SagaId} with external ID {ExternalId}, no secondary lookups needed" )]
    private static partial void LogAllProvidersInCache( ILogger logger, string sagaId, string externalId );

    /// <summary>Logs that additional providers were added to an existing saga for secondary lookups.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga that was extended.</param>
    /// <param name="providers">The providers added to the saga.</param>
    [LoggerMessage(
        EventId = LogEventIds.AddedProvidersToSaga,
        Level = LogLevel.Information,
        Message = "Added providers [{Providers}] to existing saga {SagaId} for secondary lookups" )]
    private static partial void LogAddedProvidersToSaga( ILogger logger, string sagaId, string providers );

    /// <summary>Logs that a secondary lookup was enqueued to a provider.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="lookupType">The lookup type (ISRC or UPC) of the secondary request.</param>
    /// <param name="provider">The provider the secondary lookup was queued for.</param>
    /// <param name="externalId">The external id the secondary lookup will resolve.</param>
    /// <param name="sagaId">The saga the secondary lookup belongs to.</param>
    [LoggerMessage(
        EventId = LogEventIds.QueuedSecondaryLookup,
        Level = LogLevel.Information,
        Message = "Queued secondary {LookupType} lookup for {Provider} with external ID {ExternalId} (saga {SagaId})" )]
    private static partial void LogQueuedSecondaryLookup( ILogger logger, string lookupType, string provider, string externalId, string sagaId );

    /// <summary>Logs that enqueuing a secondary lookup to a provider failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="provider">The provider the secondary lookup was destined for.</param>
    /// <param name="externalId">The external id the secondary lookup would have resolved.</param>
    [LoggerMessage(
        EventId = LogEventIds.FailedToQueueSecondary,
        Level = LogLevel.Error,
        Message = "Failed to queue secondary lookup for {Provider} with external ID {ExternalId}" )]
    private static partial void LogFailedToQueueSecondary( ILogger logger, Exception ex, string provider, string externalId );

    /// <summary>Logs that finalization is deferred while secondary lookups complete.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose finalization is deferred.</param>
    [LoggerMessage(
        EventId = LogEventIds.WaitingForSecondaryLookups,
        Level = LogLevel.Information,
        Message = "Waiting for secondary lookups to complete for saga {SagaId}, deferring finalization" )]
    private static partial void LogWaitingForSecondaryLookups( ILogger logger, string sagaId );

    /// <summary>Logs that a cached provider result was materialized into the saga, avoiding a re-query.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider whose cached result was materialized.</param>
    /// <param name="sagaId">The saga the result was written into.</param>
    /// <param name="externalId">The external id the cached result was keyed by.</param>
    [LoggerMessage(
        EventId = LogEventIds.MaterializedCachedProvider,
        Level = LogLevel.Information,
        Message = "Materialized cached {Provider} result into saga {SagaId} for external ID {ExternalId}" )]
    private static partial void LogMaterializedCachedProvider( ILogger logger, string provider, string sagaId, string externalId );

    /// <summary>Logs that another handler already queued the secondary lookups; finalization defers to it.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga whose secondaries were already queued.</param>
    [LoggerMessage(
        EventId = LogEventIds.SecondariesAlreadyQueued,
        Level = LogLevel.Debug,
        Message = "Secondary lookups already queued for saga {SagaId} by a concurrent handler, deferring finalization" )]
    private static partial void LogSecondariesAlreadyQueued( ILogger logger, string sagaId );

    /// <summary>Logs that no secondary lookups could be enqueued; finalization is deferred so waiters get a partial result and the saga is retried after it expires.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga for which no secondaries could be enqueued.</param>
    /// <param name="externalId">The external id the secondaries would have resolved.</param>
    [LoggerMessage(
        EventId = LogEventIds.NoSecondariesEnqueued,
        Level = LogLevel.Warning,
        Message = "Failed to enqueue any secondary lookups for saga {SagaId} with external ID {ExternalId}; deferring finalization so waiters receive a partial result and the lookup is retried after the saga expires" )]
    private static partial void LogNoSecondariesEnqueued( ILogger logger, string sagaId, string externalId );

    /// <summary>Logs that cache indexing failed after the result was written and waiters were released; the failure is non-fatal.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown during indexing.</param>
    /// <param name="sagaId">The saga whose post-release cache indexing failed.</param>
    [LoggerMessage(
        EventId = LogEventIds.PostReleaseIndexFailed,
        Level = LogLevel.Warning,
        Message = "Post-release cache indexing failed for saga {SagaId}; result is written and waiters are released" )]
    private static partial void LogPostReleaseIndexFailed( ILogger logger, Exception ex, string sagaId );

    #endregion
}
