using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Priority request queue for a single provider, covering enqueue by priority, dequeue
/// (plain and rate-limit-aware), acknowledgement, requeue, depth reporting, and the full
/// dead-letter queue (DLQ) surface.
/// </summary>
/// <typeparam name="T">The queued request type. Must be a reference type implementing <see cref="IQueueableRequest"/>.</typeparam>
/// <remarks>
/// Implemented in <c>BridgeBeats.Core</c> by <c>RedisRequestQueue&lt;T&gt;</c>
/// (<c>Infrastructure/Queue/RedisRequestQueue.cs</c>), using Redis Streams with consumer groups
/// for reliable delivery, and decorated by <c>SpotifyBulkQueueDecorator</c> for Spotify bulk
/// lanes. Dequeue ordering is interactive-first with a bounded aging escape hatch (see
/// <see cref="QueueSettings.InteractiveAgingInterval"/>) that visits the background and bulk
/// lanes at least once every N dequeue calls to prevent starvation under sustained interactive
/// load. Dequeued items are returned wrapped in a <c>QueuedMessage&lt;T&gt;</c> carrying the
/// broker message id; that message id is what the acknowledge, requeue, and DLQ methods operate
/// on.
/// </remarks>
public interface IRequestQueue<T> where T : class, IQueueableRequest {
    /// <summary>
    /// Enqueues a request onto the given priority lane.
    /// </summary>
    /// <param name="request">The request to enqueue.</param>
    /// <param name="priority">The priority lane to place the request on.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the request has been enqueued.</returns>
    Task EnqueueAsync( T request, QueuePriority priority, CancellationToken cancellationToken = default );

    /// <summary>
    /// Dequeues the next request according to priority, without considering rate limits.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the next <see cref="QueuedMessage{T}"/>, or <see langword="null"/>
    /// when no request is available.
    /// </returns>
    Task<QueuedMessage<T>?> DequeueAsync( CancellationToken cancellationToken = default );

    /// <summary>
    /// Dequeues the next request according to priority, skipping items whose target endpoint is
    /// currently rate-limited per the supplied tracker. Skipped items remain in the stream until
    /// the limit expires.
    /// </summary>
    /// <param name="rateLimitTracker">The tracker consulted to skip requests for rate-limited endpoints.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the next dequeuable <see cref="QueuedMessage{T}"/>, or
    /// <see langword="null"/> when no non-rate-limited request is available.
    /// </returns>
    Task<QueuedMessage<T>?> DequeueAsync( IRateLimitTracker rateLimitTracker, CancellationToken cancellationToken = default );

    /// <summary>
    /// Acknowledges a dequeued message as successfully processed, removing it from the in-flight
    /// set.
    /// </summary>
    /// <param name="messageId">The broker message id from the dequeued <see cref="QueuedMessage{T}"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the message has been acknowledged.</returns>
    Task AcknowledgeAsync( string messageId, CancellationToken cancellationToken = default );

    /// <summary>
    /// Returns a dequeued message to the queue for another attempt (for example after a transient
    /// failure or rate limit).
    /// </summary>
    /// <param name="messageId">The broker message id of the message to requeue.</param>
    /// <param name="delay">
    /// An optional provider eligibility delay. When omitted, implementations may apply their
    /// ordinary transient-retry schedule and increment retry bookkeeping.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the message has been requeued.</returns>
    Task RequeueAsync( string messageId, TimeSpan? delay = null, CancellationToken cancellationToken = default );

    /// <summary>
    /// Reports the current queue depth broken down by priority lane.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is a <see cref="QueueDepth"/> snapshot of per-lane and total counts.</returns>
    Task<QueueDepth> GetDepthAsync( CancellationToken cancellationToken = default );

    /// <summary>
    /// Reads up to <paramref name="limit"/> messages currently in the dead-letter queue, for
    /// inspecting failed messages during debugging or manual intervention.
    /// </summary>
    /// <remarks>
    /// Messages are moved to the DLQ after exceeding the maximum retry attempts or encountering a
    /// non-recoverable error.
    /// </remarks>
    /// <param name="limit">The maximum number of DLQ messages to return.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is the DLQ messages; an empty list when the DLQ is empty.
    /// </returns>
    Task<IReadOnlyList<QueuedMessage<T>>> GetDlqMessagesAsync( int limit, CancellationToken cancellationToken = default );

    /// <summary>
    /// Moves a message out of the dead-letter queue and back onto a live priority lane for
    /// reprocessing, for example once a transient cause (provider API restored, configuration
    /// fixed) has been resolved.
    /// </summary>
    /// <param name="messageId">The broker message id of the DLQ message to requeue.</param>
    /// <param name="priority">The priority lane to place the requeued message on.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the message has been requeued from the DLQ.</returns>
    Task RequeueFromDlqAsync( string messageId, QueuePriority priority, CancellationToken cancellationToken = default );

    /// <summary>
    /// Moves a failed message to the dead-letter queue, recording why it was dead-lettered, so it
    /// is preserved for inspection.
    /// </summary>
    /// <param name="messageId">The broker message id of the message to dead-letter.</param>
    /// <param name="reason">A human-readable reason the message was moved to the DLQ.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the message has been moved to the DLQ.</returns>
    Task MoveToDlqAsync( string messageId, string reason, CancellationToken cancellationToken = default );

    /// <summary>
    /// Permanently deletes a message from the dead-letter queue, for example after inspecting it
    /// and deciding to discard it, or after successfully reprocessing via
    /// <see cref="RequeueFromDlqAsync"/>.
    /// </summary>
    /// <param name="messageId">The broker message id of the DLQ message to delete.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// A task whose result is <see langword="true"/> when a message was deleted, or
    /// <see langword="false"/> when no such message was found in the DLQ.
    /// </returns>
    Task<bool> DeleteFromDlqAsync( string messageId, CancellationToken cancellationToken = default );
}
