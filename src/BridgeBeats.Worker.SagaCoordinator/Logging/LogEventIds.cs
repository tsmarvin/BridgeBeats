namespace BridgeBeats.Worker.SagaCoordinator.Logging;

/// <summary>
/// Stable numeric event identifiers for the SagaCoordinator worker's structured log messages (5000-5249).
/// Extends <see cref="Core.Infrastructure.Logging.LogEventIds"/> with SagaCoordinator-specific EventIds.
/// Each constant is the <c>EventId</c> assigned to a corresponding
/// <see cref="Microsoft.Extensions.Logging.LoggerMessageAttribute"/> method. The
/// <see cref="SagaCoordinatorBackgroundService"/> messages occupy the 5000–5099 range, and the
/// <see cref="Program"/> startup messages occupy 5100–5124; values are part of the logging
/// contract and must not be changed once shipped.
/// </summary>
public static class LogEventIds {
    /// <summary>The coordinator host has started and is wiring up its subscriptions and polling loop.</summary>
    public const int Starting = 5000;

    /// <summary>A saga-level completion event was received on the <c>saga:completed</c> Pub/Sub channel.</summary>
    public const int ReceivedSagaCompletionEvent = 5001;

    /// <summary>An error occurred while handling a saga-level completion event.</summary>
    public const int SagaCompletionError = 5002;

    /// <summary>The coordinator subscribed to the <c>saga:completed</c> literal channel.</summary>
    public const int SubscribedToChannel = 5003;

    /// <summary>A per-lookup completion event was received on a <c>complete:*</c> pattern channel.</summary>
    public const int ReceivedLookupCompletionEvent = 5004;

    /// <summary>A received channel did not match the expected <c>complete:</c> prefix and was ignored.</summary>
    public const int IgnoringChannel = 5005;

    /// <summary>An error occurred while handling a per-lookup completion event.</summary>
    public const int LookupCompletionError = 5006;

    /// <summary>The coordinator subscribed to the <c>complete:*</c> channel pattern.</summary>
    public const int SubscribedToPattern = 5007;

    /// <summary>All subscriptions are established and the coordinator is entering its polling loop.</summary>
    public const int FullyInitialized = 5008;

    /// <summary>A new polling cycle of the pending-saga sweep is beginning.</summary>
    public const int StartingPollingCycle = 5009;

    /// <summary>An error occurred during a polling cycle.</summary>
    public const int PollingError = 5010;

    /// <summary>A polling cycle finished and the coordinator is sleeping until the next interval.</summary>
    public const int CompletedPollingCycle = 5011;

    /// <summary>The coordinator is stopping and unsubscribing from its channels.</summary>
    public const int Stopping = 5012;

    /// <summary>A saga-level completion event is being processed for finalization.</summary>
    public const int ProcessingSagaCompletion = 5013;

    /// <summary>The saga referenced by a completion event could not be found.</summary>
    public const int SagaNotFound = 5014;

    /// <summary>Diagnostic snapshot of a saga's completeness, final-result URI, and provider count.</summary>
    public const int SagaState = 5015;

    /// <summary>The saga is not yet complete, so finalization was skipped.</summary>
    public const int SagaNotComplete = 5016;

    /// <summary>The saga already carries a final-result URI and was skipped as already finalized.</summary>
    public const int SagaAlreadyFinalized = 5017;

    /// <summary>Processing of a saga completion event failed unexpectedly.</summary>
    public const int FailedToProcessSaga = 5018;

    /// <summary>A per-lookup completion event is being processed.</summary>
    public const int ProcessingLookupCompletion = 5019;

    /// <summary>The saga derived from a lookup-completion channel could not be found.</summary>
    public const int SagaNotFoundForLookup = 5020;

    /// <summary>Diagnostic snapshot of a saga's state during lookup-completion handling.</summary>
    public const int SagaStateForLookup = 5021;

    /// <summary>Processing of a lookup completion event failed unexpectedly.</summary>
    public const int FailedLookupCompletion = 5022;

    /// <summary>The polling sweep is querying for completed-but-unfinalized sagas.</summary>
    public const int PollingForSagas = 5023;

    /// <summary>The polling sweep found no unfinalized sagas this cycle.</summary>
    public const int NoUnfinalizedSagas = 5024;

    /// <summary>The polling sweep found one or more unfinalized sagas to process.</summary>
    public const int FoundUnfinalizedSagas = 5025;

    /// <summary>A partial result is being written for a saga discovered during the polling sweep.</summary>
    public const int WritingPartialDuringPolling = 5026;

    /// <summary>A saga discovered during the polling sweep is being finalized.</summary>
    public const int FinalizingDuringPolling = 5027;

    /// <summary>Processing of a saga during the polling sweep failed; it is retried next cycle.</summary>
    public const int FailedDuringPolling = 5028;

    /// <summary>An error occurred while querying for unfinalized sagas during the polling sweep.</summary>
    public const int QueryError = 5029;

    /// <summary>The final combined result is being assembled from per-provider saga state.</summary>
    public const int AssemblingFinalResult = 5030;

    /// <summary>The saga completed with no successful provider results and is being cleaned up.</summary>
    public const int NoSuccessfulResults = 5031;

    /// <summary>The final result was written to the ATProto PDS.</summary>
    public const int WroteFinalToAtProto = 5032;

    /// <summary>The final result was written into the media-link cache.</summary>
    public const int CachedFinalResult = 5033;

    /// <summary>A saga was successfully finalized.</summary>
    public const int SuccessfullyFinalized = 5034;

    /// <summary>Writing the final result for a saga failed.</summary>
    public const int FailedToWriteFinal = 5035;

    /// <summary>An interim partial result is being assembled while secondary lookups are still pending.</summary>
    public const int AssemblingPartialResult = 5036;

    /// <summary>The saga has no successful provider results yet, so no partial result was written.</summary>
    public const int NoSuccessfulPartialResults = 5037;

    /// <summary>A partial result was written to the ATProto PDS.</summary>
    public const int WrotePartialToAtProto = 5038;

    /// <summary>A partial result was successfully written.</summary>
    public const int SuccessfullyWrotePartial = 5039;

    /// <summary>Writing a partial result for a saga failed.</summary>
    public const int FailedToWritePartial = 5040;

    /// <summary>The first provider result carried no external id (ISRC/UPC), so secondary fan-out was skipped.</summary>
    public const int NoExternalId = 5041;

    /// <summary>No other enabled providers remained, so secondary fan-out was skipped.</summary>
    public const int NoOtherProviders = 5042;

    /// <summary>A cache hit was found for the external id, supplying results for one or more providers.</summary>
    public const int CacheHitForExternalId = 5043;

    /// <summary>A secondary lookup was enqueued to another provider using the discovered external id.</summary>
    public const int QueuedSecondaryLookup = 5046;

    /// <summary>Enqueuing a secondary lookup to a provider failed.</summary>
    public const int FailedToQueueSecondary = 5047;

    /// <summary>All other providers were already present in the external-id cache; no secondary lookups were needed.</summary>
    public const int AllProvidersInCache = 5048;

    /// <summary>Additional providers were initialized on an existing saga for secondary lookups.</summary>
    public const int AddedProvidersToSaga = 5049;

    /// <summary>Finalization is being deferred while secondary lookups complete.</summary>
    public const int WaitingForSecondaryLookups = 5050;

    /// <summary>A cached provider result was materialized directly into the saga, avoiding a re-query.</summary>
    public const int MaterializedCachedProvider = 5051;

    /// <summary>Secondary lookups were already queued by a concurrent handler; finalization is deferred to it.</summary>
    public const int SecondariesAlreadyQueued = 5052;

    /// <summary>No secondary lookups could be enqueued; finalization is deferred so waiters get a partial result and the saga is retried after it expires.</summary>
    public const int NoSecondariesEnqueued = 5053;

    /// <summary>The finalize claim for a saga was already held by another handler; this handler is skipping finalization.</summary>
    public const int FinalizationClaimLost = 5054;

    /// <summary>Post-release cache indexing failed; the result is already written and waiters are released, so the failure is swallowed.</summary>
    public const int PostReleaseIndexFailed = 5055;

    /// <summary>Persisting a terminal zero-result saga for refresh review failed; terminal cleanup continues.</summary>
    public const int RefreshReviewPersistenceFailed = 5056;

    #region Program Startup (5100-5124)

    /// <summary>The set of providers enabled for secondary lookups, logged once at startup after the logging pipeline is live.</summary>
    public const int EnabledProviders = 5100;

    #endregion
}
