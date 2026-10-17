using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Infrastructure.Extensions;
using Serilog;

namespace BridgeBeats.Worker.JetStreamWatcher;

/// <summary>
/// Entry point and composition root for the JetStream watcher. Builds a background host (the worker
/// exposes no HTTP endpoint), wires the Redis client and provider queues, registers the
/// <see cref="JetStreamWatcherService"/> hosted service, and runs until shutdown.
/// </summary>
public static class Program {

    /// <summary>
    /// Builds and runs the JetStream watcher host, flushing Serilog on exit.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the host builder.</param>
    public static void Main( string[] args ) {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder( args );

        ConfigureServices( builder );

        IHost app = builder.Build();

        try {
            app.Run( );
        } finally {
            Log.CloseAndFlush( );
        }
    }

    /// <summary>
    /// Registers the worker's services: file logging, Aspire service defaults, the Redis client, the
    /// queue infrastructure and all provider queues (so discovered links can be enqueued to any
    /// provider), and the <see cref="JetStreamWatcherService"/> hosted service.
    /// </summary>
    /// <param name="builder">The host application builder being configured.</param>
    private static void ConfigureServices( HostApplicationBuilder builder ) {
        // Configure file logging
        _ = builder.ConfigureFileLogging( "JetStreamWatcher" );

        // Add Aspire service defaults (telemetry, resilience)
        _ = builder.AddServiceDefaults( );

        // Add Redis client from Aspire (for queue submission)
        builder.AddRedisClient( "redis" );

        _ = builder.Services.AddValidatedQueueSettings( builder.Configuration );

        // Register queue infrastructure for submitting to provider queues.
        // AddAllProviderQueues automatically applies SpotifyBulkQueueDecorator for QueuedLookupRequest,
        // routing Spotify SongIdLookup/AlbumIdLookup to the type-specific bulk streams.
        _ = builder.Services.AddQueueInfrastructure( );
        _ = builder.Services.AddAllProviderQueues<QueuedLookupRequest>( );

        // Register JetStream watcher background service
        _ = builder.Services.AddHostedService<JetStreamWatcherService>( );
    }
}
