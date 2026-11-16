using System.Text.Json.Serialization;
using BridgeBeats.Contracts.Enums;

namespace BridgeBeats.Contracts.Records;

/// <summary>
/// Configuration settings for the queue system: retry budgets, job expiry, interactive wait
/// and aging cadence, and bulk-lane flush thresholds. The defaults here are load-bearing and
/// chosen to match the queue's runtime behavior.
/// </summary>
public sealed record QueueSettings {

    /// <summary>
    /// The threshold for re-queuing rate-limited requests. If a Retry-After value exceeds this
    /// threshold, the request is queued for later processing. Defaults to 2 minutes.
    /// </summary>
    [JsonPropertyName( "rateLimitRetryThreshold" )]
    public TimeSpan RateLimitRetryThreshold { get; init; } = TimeSpan.FromMinutes( 2 );

    /// <summary>
    /// The number of minutes before an incomplete job or saga expires. Defaults to <c>2880</c>
    /// (48 hours).
    /// </summary>
    /// <remarks>
    /// Must exceed the Spotify bulk-batch linger backstop (<c>BridgeBeats:Spotify:Batch:LingerMs</c>,
    /// default 24 h / 1 440 min) so that a saga created before a bulk flush is not evicted before
    /// the flush writes its result. The default of 2 880 min (48 h) is 2× the 24 h backstop, giving
    /// visible margin. See <c>docs/SPOTIFY_BATCH_AND_ROUTING.md</c> ("Saga time-to-live reconciliation")
    /// for the trade-off.
    /// </remarks>
    [JsonPropertyName( "jobExpirationMinutes" )]
    public int JobExpirationMinutes { get; init; } = 2880;

    /// <summary>
    /// The total time budget (in seconds) an interactive caller waits for a complete lookup
    /// result before returning the best available partial result. Defaults to <c>30</c>.
    /// </summary>
    [JsonPropertyName( "interactiveWaitSeconds" )]
    public int InteractiveWaitSeconds { get; init; } = 30;

    /// <summary>
    /// The number of dequeue-ordering calls between aging slots. Every <c>InteractiveAgingInterval</c>
    /// calls the dequeue ordering promotes a lower-priority tier (background or bulk) to the head of
    /// the order so that interactive load cannot starve background and bulk streams indefinitely.
    /// Defaults to <c>8</c>.
    /// </summary>
    /// <remarks>
    /// Values of 1 or less are treated as a misconfiguration by the queue, which logs a warning and
    /// falls back to the default.
    /// </remarks>
    [JsonPropertyName( "interactiveAgingInterval" )]
    public int InteractiveAgingInterval { get; init; } = 8;

    /// <summary>
    /// The priority aging configuration; see <see cref="PriorityWeights"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="PriorityWeights"/> values no longer drive weighted-random selection. They are
    /// retained for backwards-compatible configuration and documentation purposes. Queue ordering is
    /// now deterministic: interactive-first with a bounded aging escape hatch controlled by
    /// <see cref="InteractiveAgingInterval"/>.
    /// </remarks>
    [JsonPropertyName( "weights" )]
    public PriorityWeights Weights { get; init; } = new( );

    /// <summary>
    /// The minimum number of messages required in the bulk queue before bulk priority requests
    /// are processed. This enables batching optimization for providers that support bulk lookup
    /// endpoints. Defaults to <c>5</c>.
    /// </summary>
    /// <remarks>
    /// Below this threshold bulk messages are skipped during dequeue; set to 0 or 1 to disable batching.
    /// </remarks>
    [JsonPropertyName( "defaultMinBulkQueueThreshold" )]
    public int DefaultMinBulkQueueThreshold { get; init; } = 5;

    /// <summary>
    /// Per-provider minimum bulk queue thresholds. Providers not specified here fall back to
    /// <see cref="DefaultMinBulkQueueThreshold"/>.
    /// </summary>
    [JsonPropertyName( "providerMinBulkThresholds" )]
    public Dictionary<SupportedProviders, int> ProviderMinBulkThresholds { get; init; } = [];

    /// <summary>Maximum number of provider lookups processed concurrently by one queue worker.</summary>
    [JsonPropertyName( "defaultProviderConcurrency" )]
    public int DefaultProviderConcurrency { get; init; } = 4;

    /// <summary>Optional per-provider overrides for <see cref="DefaultProviderConcurrency"/>.</summary>
    [JsonPropertyName( "providerConcurrency" )]
    public Dictionary<SupportedProviders, int> ProviderConcurrency { get; init; } = [];

    /// <summary>
    /// Returns the minimum bulk queue threshold for the given provider, falling back to
    /// <see cref="DefaultMinBulkQueueThreshold"/> when no per-provider override is configured.
    /// </summary>
    /// <param name="provider">The provider whose bulk queue threshold to resolve.</param>
    /// <returns>The minimum bulk queue depth required before processing bulk requests for that provider.</returns>
    public int GetMinBulkThreshold( SupportedProviders provider )
        => ProviderMinBulkThresholds.TryGetValue( provider, out int threshold )
            ? threshold
            : DefaultMinBulkQueueThreshold;

    /// <summary>Returns a validated per-provider concurrency bound.</summary>
    public int GetProviderConcurrency( SupportedProviders provider ) {
        int configured = ProviderConcurrency.TryGetValue( provider, out int value )
            ? value
            : DefaultProviderConcurrency;
        return Math.Clamp( configured, 1, 32 );
    }
}
