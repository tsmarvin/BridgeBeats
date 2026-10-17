using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Domain.Providers.Spotify;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Worker.Spotify.Metrics;
using Serilog;

namespace BridgeBeats.Worker.Spotify;

/// <summary>
/// Entry point and composition root for the Spotify provider worker process.
/// </summary>
/// <remarks>
/// Unlike the Apple Music and Tidal workers, the Spotify worker is asymmetric: it runs the
/// shared per-message queue processor <em>and</em> two additional hosted services that exist
/// only here, because Spotify's API supports multi-id batch fetches:
/// <list type="bullet">
///   <item>the shared queue-processing background service (via
///   <c>AddQueueProcessor&lt;SpotifyLookupService&gt;</c>) for per-message lookups;</item>
///   <item><see cref="SpotifyBulkProcessorService"/>, which drains single-id track/album
///   lookups that were diverted to dedicated bulk streams and resolves them in batches; and</item>
///   <item><see cref="SpotifyArtistGenreService"/>, a long-cadence background refresh of
///   artist-to-genre data.</item>
/// </list>
/// The worker also maps the synchronous WorkerApi lookup endpoints (via
/// <c>MapProviderLookupEndpoints&lt;SpotifyLookupService&gt;</c>) and connects to the shared
/// Redis backbone.
/// </remarks>
public static class Program {

    /// <summary>
    /// Builds, configures, and runs the Spotify worker web application.
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
    /// Registers the Spotify worker's services, including the genre cache, batch settings, the
    /// bulk-stream queue helper, and the two Spotify-only hosted services on top of the shared
    /// queue processor.
    /// </summary>
    /// <param name="builder">The web application builder whose service collection is populated.</param>
    /// <remarks>
    /// Beyond the wiring shared with the other provider workers (file logging, service defaults,
    /// Redis client, provider services, JSON options, and the shared queue processor), this
    /// method also registers:
    /// <list type="bullet">
    ///   <item>the genre cache (<c>AddGenreCache</c>) used by artist-genre resolution;</item>
    ///   <item><see cref="SpotifyBatchSettings"/> bound from configuration section
    ///   <see cref="SpotifyBatchSettings.SectionKey"/>;</item>
    ///   <item><see cref="SpotifyBatchQueueHelper"/> as a singleton, plus
    ///   <see cref="SpotifyBulkProcessorService"/> as a hosted service; and</item>
    ///   <item><see cref="SpotifyArtistGenreService"/> as a hosted service.</item>
    /// </list>
    /// Credentials are validated up front via <see cref="ValidateConfiguration"/>; a missing
    /// credential aborts startup before any service is registered.
    /// </remarks>
    private static void ConfigureServices( WebApplicationBuilder builder ) {
        // Configure file logging
        _ = builder.ConfigureFileLogging( "Spotify" );

        // Add Aspire service defaults (health checks, telemetry, resilience)
        _ = builder.AddServiceDefaults( );

        // Add Redis client from Aspire (for queue processing)
        builder.AddRedisClient( "redis" );

        _ = builder.Services.AddValidatedQueueSettings( builder.Configuration );

        // Read and validate credentials
        (string clientId, string clientSecret, int maxRetryAfterSeconds) = ValidateConfiguration( builder );

        // Register Spotify services
        HashSet<SupportedProviders> enabledProviders = [];
        _ = builder.Services.AddSpotifyServices( clientId, clientSecret, enabledProviders, maxRetryAfterSeconds );

        // Register JSON serializer options (required by SpotifyLookupService)
        _ = builder.Services.AddSingleton( new JsonSerializerOptions { WriteIndented = true } );

        // Register genre cache for artist genre processing
        _ = builder.Services.AddGenreCache( );

        // Register queue processor background service for consuming from Redis streams
        _ = builder.Services.AddQueueProcessor<SpotifyLookupService>( SupportedProviders.Spotify );

        // Configure Spotify batch settings (linger, configurable under BridgeBeats:Spotify:Batch)
        _ = builder.Services.Configure<SpotifyBatchSettings>(
            builder.Configuration.GetSection( SpotifyBatchSettings.SectionKey )
        );

        // Register Spotify-specific batch queue helper and bulk processor service
        _ = builder.Services.AddSingleton<SpotifyBatchQueueHelper>( );
        _ = builder.Services.AddHostedService<SpotifyBulkProcessorService>( );

        // Register Spotify artist genre service for scheduled genre fetching
        _ = builder.Services.AddHostedService<SpotifyArtistGenreService>( );
    }

    /// <summary>
    /// Reads and validates the Spotify credentials and resilience settings from configuration.
    /// </summary>
    /// <param name="builder">The web application builder whose configuration is read.</param>
    /// <returns>
    /// A tuple of the Spotify client id, client secret, and the maximum honored
    /// <c>Retry-After</c> value in seconds (defaulting to 120 when unset).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when either <c>BridgeBeats:SpotifyClientId</c> or <c>BridgeBeats:SpotifyClientSecret</c>
    /// is missing or blank.
    /// </exception>
    private static (string ClientId, string ClientSecret, int MaxRetryAfterSeconds) ValidateConfiguration(
        WebApplicationBuilder builder
    ) {
        string? clientId = builder.Configuration["BridgeBeats:SpotifyClientId"];
        string? clientSecret = builder.Configuration["BridgeBeats:SpotifyClientSecret"];
        int maxRetryAfterSeconds = builder.Configuration.GetValue("BridgeBeats:Resilience:MaxRetryAfterSeconds", 120);

        return string.IsNullOrWhiteSpace( clientId ) || string.IsNullOrWhiteSpace( clientSecret )
            ? throw new InvalidOperationException(
                "Spotify credentials are required. Set BridgeBeats:SpotifyClientId and BridgeBeats:SpotifyClientSecret."
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
        _ = app.MapProviderLookupEndpoints<SpotifyLookupService>( );
    }
}
