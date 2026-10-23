namespace BridgeBeats.Contracts.Enums;

/// <summary>Result of a revision-guarded media-link record deletion.</summary>
public enum MediaLinkDeleteOutcome {
    /// <summary>The expected record revision was deleted.</summary>
    Deleted,

    /// <summary>The record was already absent, so the requested final state is satisfied.</summary>
    NotFound,

    /// <summary>The record exists but no longer has the expected CID.</summary>
    RevisionConflict
}
