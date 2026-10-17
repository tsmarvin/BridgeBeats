namespace BridgeBeats.Contracts.Constants;

/// <summary>
/// Shared constants used by the lookup and queue pipeline.
/// </summary>
public static class LookupConstants {

    /// <summary>
    /// Sentinel value <c>"RATE_LIMITED"</c> published to the completion channel when a provider is
    /// rate-limited. The queue processor publishes it so the lookup orchestrator can return a partial
    /// result immediately rather than waiting for the full timeout.
    /// </summary>
    public const string RateLimitedSentinel = "RATE_LIMITED";

    /// <summary>
    /// Sentinel value <c>"NO_RESULT"</c> published when an in-flight lookup releases its waiters
    /// without a durable result URI. The deduplicator consumes this control value and returns
    /// <see langword="null"/> to callers immediately.
    /// </summary>
    public const string NoResultSentinel = "NO_RESULT";

    /// <summary>
    /// Sentinel published when lookup processing succeeded but no result could be persisted to an
    /// accepted durable target.
    /// </summary>
    public const string ResultNotPersistedSentinel = "RESULT_NOT_PERSISTED";

    /// <summary>
    /// Non-terminal notification published when an owned single-flight lease is released without
    /// declaring the lookup complete. Waiters must re-read durable state and continue using their
    /// own time budget.
    /// </summary>
    public const string StateChangedSentinel = "STATE_CHANGED";

    /// <summary>Redis channel prefix used for lookup completion notifications.</summary>
    public const string CompletionChannelPrefix = "complete:";

    /// <summary>
    /// Maximum number of queue processing attempts (<c>5</c>) before a message is moved to the
    /// dead-letter queue. Enforced by both <c>QueueProcessorBackgroundService</c> and
    /// <c>SpotifyBatchQueueHelper</c> for attempt-consuming failures. Durable rate-limit deferrals
    /// preserve the current attempt count and are instead bounded by the job-expiration deadline.
    /// </summary>
    public const int MaxQueueRetryAttempts = 5;
}
