using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Contracts.Interfaces;

/// <summary>
/// Tracks stale-record refresh context and exposes terminal zero-result refreshes for review.
/// </summary>
public interface IRefreshReviewStore {
    /// <summary>Registers the PDS source record for a refresh saga until that saga completes.</summary>
    Task RegisterPendingAsync( RefreshReviewEntry entry, CancellationToken cancellationToken = default );

    /// <summary>Returns the pending context for one source record, if any.</summary>
    Task<RefreshReviewEntry?> GetPendingAsync( string sourceRecordUri, CancellationToken cancellationToken = default );
    /// <summary>Returns every record context currently associated with a saga id.</summary>
    Task<IReadOnlyList<RefreshReviewEntry>> GetPendingForSagaAsync( string sagaId, CancellationToken cancellationToken = default );

    /// <summary>Promotes the supplied record context without relying on a deterministic saga id.</summary>
    Task MarkUnresolvedAsync( RefreshReviewEntry entry, string reason, CancellationToken cancellationToken = default );

    /// <summary>
    /// Promotes a concrete source-record context directly into the unresolved review list.
    /// This path is used when no matching pending owner exists; it never changes pending saga
    /// context and is idempotent for the source-record URI.
    /// </summary>
    Task PromoteDirectAsync( RefreshReviewEntry entry, string reason, CancellationToken cancellationToken = default );

    /// <summary>Clears one source record's pending context after completion.</summary>
    Task CompleteAsync( RefreshReviewEntry entry, CancellationToken cancellationToken = default );

    /// <summary>Clears only review contexts owned by the expected saga generation.</summary>
    Task CompleteAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default );

    /// <summary>Increments the sweep observation count for a source record and returns the new value.</summary>
    Task<int> IncrementSweepAttemptAsync( string sourceRecordUri, CancellationToken cancellationToken = default );

    /// <summary>Clears the sweep observation count after a successful refresh.</summary>
    Task ClearSweepAttemptsAsync( string sourceRecordUri, CancellationToken cancellationToken = default );

    /// <summary>Lists all records currently awaiting operator review.</summary>
    Task<IReadOnlyList<RefreshReviewEntry>> GetUnresolvedAsync( CancellationToken cancellationToken = default );

    /// <summary>Removes a reviewed entry only while its displayed saga and source CID still match.</summary>
    Task<bool> DeleteUnresolvedAsync(
        string sourceRecordUri,
        string expectedSagaId,
        string expectedSourceRecordCid,
        CancellationToken cancellationToken = default );
}
