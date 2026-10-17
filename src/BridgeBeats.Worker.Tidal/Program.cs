using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Domain.Providers.Tidal;
using BridgeBeats.Core.Infrastructure.Extensions;
using Serilog;

namespace BridgeBeats.Worker.Tidal;

/// <summary>
/// Entry point and composition root for the Tidal provider worker process.
/// </summary>
/// <remarks>
/// The Tidal worker is intentionally minimal. It hosts two things and no bespoke
/// processing logic of its own:
/// <list type="bullet">
///   <item>the shared queue-processing background service (registered via
///   <c>AddQueueProcessor&lt;TidalLookupService&gt;</c>), which consumes the asynchronous
///   Redis Streams work queue; and</item>
///   <item>the synchronous WorkerApi lookup endpoints (mapped via
///   <c>MapProviderLookupEndpoints&lt;TidalLookupService&gt;</c>), which the Web layer
///   calls directly for interactive lookups.</item>
/// </list>
/// All actual Tidal lookup behavior lives in <c>TidalLookupService</c> (in
/// BridgeBeats.Core); this host only wires it up. The worker listens on its configured
/// HTTP port and connects to the shared Redis backbone.
/// </remarks>
public static class Program {

    /// <summary>
    /// Builds, configures, and runs the Tidal worker web application.
    /// </summary>
    /// <param name="args">Command-line arguments passed through to the host builder.</param>
    /// <remarks>
    /// Serilog is flushed in the <c>finally</c> block so buffered log entries are written
    /// even when the host shuts down or faults.
    /// </remarks>
    public static void Main( string[] args ) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        ConfigureServices( builder );

        WebApplication app = builder.Build();

        ConfigureEndpoints( app );

        try {
            app.Run( );
        } finally {
            Log.CloseAndFlush( );
        }
    }

    /// <summary>
    /// Registers the worker's services: file logging, Aspire service defaults, the Redis
    /// client, the Tidal provider services, JSON options, and the shared queue processor
    /// for the Tidal provider.
    /// </summary>
    /// <param name="builder">The web application builder whose service collection is populated.</param>
    /// <remarks>
    /// Provider credentials are validated up front via <see cref="ValidateConfiguration"/>;
    /// a missing credential aborts startup before any service is registered. The
    /// <c>enabledProviders</c> set is left empty here because secondary fan-out across
    /// providers is the coordinator's concern, not this single-provider worker's.
    /// </remarks>
    private static void ConfigureServices( WebApplicationBuilder builder ) {
        // Configure file logging
        _ = builder.ConfigureFileLogging( "Tidal" );

        // Add Aspire service defaults (health checks, telemetry, resilience)
        _ = builder.AddServiceDefaults( );

        // Add Redis client from Aspire (for queue processing)
        builder.AddRedisClient( "redis" );

        _ = builder.Services.AddValidatedQueueSettings( builder.Configuration );

        // Read and validate credentials
        (string clientId, string clientSecret, int maxRetryAfterSeconds) = ValidateConfiguration( builder );

        // Register Tidal services
        HashSet<SupportedProviders> enabledProviders = [];
        _ = builder.Services.AddTidalServices( clientId, clientSecret, enabledProviders, maxRetryAfterSeconds );

        // Register JSON serializer options (required by TidalLookupService)
        _ = builder.Services.AddSingleton( new JsonSerializerOptions { WriteIndented = true } );

        // Register the track genre cache used by TidalLookupService
        _ = builder.Services.AddGenreCache( );

        // Register queue processor background service for consuming from Redis streams
        _ = builder.Services.AddQueueProcessor<TidalLookupService>( SupportedProviders.Tidal );
    }

    /// <summary>
    /// Reads and validates the Tidal credentials and resilience settings from configuration.
    /// </summary>
    /// <param name="builder">The web application builder whose configuration is read.</param>
    /// <returns>
    /// A tuple of the Tidal client id, client secret, and the maximum honored
    /// <c>Retry-After</c> value in seconds (defaulting to 120 when unset).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when either <c>BridgeBeats:TidalClientId</c> or <c>BridgeBeats:TidalClientSecret</c>
    /// is missing or blank.
    /// </exception>
    private static (string ClientId, string ClientSecret, int MaxRetryAfterSeconds) ValidateConfiguration(
        WebApplicationBuilder builder
    ) {
        string? clientId = builder.Configuration["BridgeBeats:TidalClientId"];
        string? clientSecret = builder.Configuration["BridgeBeats:TidalClientSecret"];
        int maxRetryAfterSeconds = builder.Configuration.GetValue("BridgeBeats:Resilience:MaxRetryAfterSeconds", 120);

        return string.IsNullOrWhiteSpace( clientId ) || string.IsNullOrWhiteSpace( clientSecret )
            ? throw new InvalidOperationException(
                "Tidal credentials are required. Set BridgeBeats:TidalClientId and BridgeBeats:TidalClientSecret."
            )
            : ((string ClientId, string ClientSecret, int MaxRetryAfterSeconds))(clientId, clientSecret, maxRetryAfterSeconds);
    }

    /// <summary>
    /// Maps the worker's HTTP endpoints: the Aspire default health/metrics endpoints and the
    /// WorkerApi provider-lookup routes (<c>POST /lookup/{url,isrc,upc,id,metadata,from-result}</c>).
    /// </summary>
    /// <param name="app">The built web application to map endpoints onto.</param>
    /// <remarks>
    /// The mapped lookup endpoints follow the WorkerApi convention of returning HTTP 200 even
    /// on failure, carrying the outcome in the response envelope; callers inspect the
    /// success flag and error message rather than the status code.
    /// </remarks>
    private static void ConfigureEndpoints( WebApplication app ) {
        // Map Aspire health check endpoints
        _ = app.MapDefaultEndpoints( );

        // Map provider lookup endpoints using shared extension
        _ = app.MapProviderLookupEndpoints<TidalLookupService>( );
    }
}
