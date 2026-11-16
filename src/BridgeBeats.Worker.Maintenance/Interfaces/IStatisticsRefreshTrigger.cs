namespace BridgeBeats.Worker.Maintenance.Interfaces;

/// <summary>
/// Coalescing gate that ensures at most one statistics-refresh pass runs at a time. Both the
/// subscriber-driven manual trigger and the scheduled bootstrap cycle route through this guard.
/// </summary>
public interface IStatisticsRefreshTrigger {

    /// <summary>
    /// Attempts to acquire the refresh slot. Returns <see langword="true"/> when the slot was
    /// acquired (the caller owns the pass and must call <see cref="Release"/> when done); returns
    /// <see langword="false"/> when a pass is already in progress and the trigger should be
    /// coalesced (dropped without starting a new pass).
    /// </summary>
    bool TryAcquire( );

    /// <summary>
    /// Releases the refresh slot acquired by <see cref="TryAcquire"/>.
    /// </summary>
    void Release( );
}
