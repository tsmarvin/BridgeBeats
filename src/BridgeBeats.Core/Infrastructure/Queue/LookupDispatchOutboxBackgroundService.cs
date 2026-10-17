using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>Continuously relays lookup dispatch outbox entries left behind by interrupted callers.</summary>
internal sealed partial class LookupDispatchOutboxBackgroundService(
    ILookupDispatchOutbox outbox,
    ILogger<LookupDispatchOutboxBackgroundService> logger
) : BackgroundService {
    private static TimeSpan GetIdleDelay( ) => TimeSpan.FromMilliseconds(
        Random.Shared.Next( 750, 1251 ) );

    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        while (!stoppingToken.IsCancellationRequested) {
            try {
                int dispatched = await outbox.DispatchPendingAsync( 100, stoppingToken );
                if (dispatched == 0) {
                    await Task.Delay( GetIdleDelay( ), stoppingToken );
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                LogRelayFailure( logger, ex );
                await Task.Delay( GetIdleDelay( ), stoppingToken );
            }
        }
    }

    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.LookupDispatchOutboxRelayFailure,
        Level = LogLevel.Error,
        Message = "Lookup dispatch outbox relay failed; pending entries will be retried" )]
    private static partial void LogRelayFailure( ILogger logger, Exception exception );
}
