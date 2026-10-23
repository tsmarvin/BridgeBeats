namespace BridgeBeats.Web.Services;

/// <summary>Coordinates audited, revision-safe disposition of refresh-review records.</summary>
public interface IRefreshReviewDispositionService {
    /// <summary>Deletes the reviewed revision when it is still current and clears review state.</summary>
    Task<RefreshReviewDispositionOutcome> DeleteAsync(
        string sourceRecordUri,
        string expectedSagaId,
        string expectedSourceRecordCid,
        string actorId,
        CancellationToken cancellationToken = default );
}

/// <summary>Outcome of an operator's refresh-review disposition request.</summary>
public enum RefreshReviewDispositionOutcome {
    /// <summary>The reviewed record revision was deleted.</summary>
    Deleted,

    /// <summary>The record was already absent and review state was cleared.</summary>
    AlreadyAbsent,

    /// <summary>A newer record revision was preserved and the obsolete review entry was cleared.</summary>
    RevisionChanged,

    /// <summary>No matching review entry exists.</summary>
    ReviewEntryNotFound,

    /// <summary>The review entry changed after it was displayed and was preserved.</summary>
    ReviewEntryChanged,

    /// <summary>The review entry lacks the CID required for a safe deletion.</summary>
    RevisionUnavailable
}
