using BridgeBeats.Contracts.Constants;
using BridgeBeats.Worker.Maintenance.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Worker.Maintenance;

/// <summary>
/// Subscribes to the <see cref="RedisChannels.StatisticsRefreshRequested"/> Redis Pub/Sub channel
/// and triggers an out-of-band statistics-refresh pass when a message arrives. Each message is
/// handled in a fire-and-forget <c>Task.Run</c> with a try/catch so an exception in the
/// handler cannot bring down the host. The coalescing guard is enforced inside
/// <c>CacheBootstrapBackgroundService.TriggerStatisticsRefreshAsync</c>.
/// </summary>
/// <param name="redis">The Redis connection used to subscribe.</param>
/// <param name="bootstrapService">The bootstrap service whose statistics pass is triggered.</param>
/// <param name="logger">The logger for this service.</param>
public sealed partial class StatisticsRefreshSubscriberService(
    IConnectionMultiplexer redis,
    CacheBootstrapBackgroundService bootstrapService,
    ILogger<StatisticsRefreshSubscriberService> logger
) : BackgroundService {

    /// <summary>
    /// Subscribes to the statistics-refresh channel, then idles until cancellation. On shutdown,
    /// unsubscribes from the channel.
    /// </summary>
    /// <param name="stoppingToken">Signals when the host is shutting down.</param>
    /// <returns>A task that completes when the service stops and has unsubscribed.</returns>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        ISubscriber subscriber = redis.GetSubscriber( );

        await subscriber.SubscribeAsync(
            RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ),
            ( _, _ ) => {
                if (logger.IsEnabled( LogLevel.Information )) {
                    LogRefreshRequested( logger );
                }

                _ = Task.Run( async ( ) => {
                    try {
                        await bootstrapService.TriggerStatisticsRefreshAsync( stoppingToken );
                    } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                        // Normal shutdown — no log.
                    } catch (Exception ex) {
                        LogRefreshHandlerError( logger, ex );
                    }
                }, stoppingToken );
            }
        );

        try {
            await Task.Delay( Timeout.InfiniteTimeSpan, stoppingToken );
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Normal shutdown.
        }

        await subscriber.UnsubscribeAsync(
            RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested )
        );
    }

    #region LoggerMessage Methods

    /// <summary>Logs that a manual statistics refresh was requested via Pub/Sub.</summary>
    [LoggerMessage(
        EventId = LogEventIds.RefreshRequested,
        Level = LogLevel.Information,
        Message = "Manual statistics refresh requested via Pub/Sub" )]
    private static partial void LogRefreshRequested( ILogger logger );

    /// <summary>Logs that the handler for a manual statistics refresh trigger threw an unhandled exception.</summary>
    [LoggerMessage(
        EventId = LogEventIds.RefreshHandlerError,
        Level = LogLevel.Error,
        Message = "Unhandled exception in statistics refresh handler" )]
    private static partial void LogRefreshHandlerError( ILogger logger, Exception ex );

    #endregion
}
