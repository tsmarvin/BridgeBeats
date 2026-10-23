namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Event-driven wake-up seam for queue consumers. A version captured before dequeue closes the
/// race between observing an empty queue and subscribing to the next notification.
/// </summary>
public interface IQueueWorkSignal {
    /// <summary>Subscribes to the broker notification channel before consumption starts.</summary>
    Task InitializeWorkSignalAsync( CancellationToken cancellationToken = default );

    /// <summary>Captures the notification generation immediately before dequeue.</summary>
    long CaptureWorkVersion( );

    /// <summary>
    /// Waits for work published after <paramref name="observedVersion"/> or for a known endpoint
    /// rate limit to reach <paramref name="scheduledWake"/>. If the captured version is already
    /// stale on entry, returns immediately. Implementations merge the supplied wake with any
    /// internally known retry/reclaim deadline and wait for the earliest one; a null wake means
    /// there is no caller-supplied deadline, not that internal deadlines should be ignored.
    /// </summary>
    Task WaitForWorkAsync(
        long observedVersion,
        DateTimeOffset? scheduledWake,
        CancellationToken cancellationToken = default );
}
