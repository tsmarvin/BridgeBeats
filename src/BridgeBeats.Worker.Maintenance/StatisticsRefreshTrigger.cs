using BridgeBeats.Worker.Maintenance.Interfaces;

namespace BridgeBeats.Worker.Maintenance;

/// <summary>
/// <see cref="IStatisticsRefreshTrigger"/> implementation backed by a <see cref="SemaphoreSlim"/>
/// with count one. <see cref="TryAcquire"/> is non-blocking: it returns <see langword="false"/>
/// immediately when a pass is already running rather than waiting for the slot to free.
/// </summary>
public sealed class StatisticsRefreshTrigger : IStatisticsRefreshTrigger {

    private readonly SemaphoreSlim _semaphore = new( 1, 1 );

    /// <inheritdoc/>
    public bool TryAcquire( ) => _semaphore.Wait( 0 );

    /// <inheritdoc/>
    public void Release( ) => _semaphore.Release( );
}
