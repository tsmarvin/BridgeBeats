using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Domain.Services.Queue;
using BridgeBeats.Core.Infrastructure.Cache;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Storage;
using Serilog;
using StackExchange.Redis;

namespace BridgeBeats.Worker.SagaCoordinator;

/// <summary>
/// Entry point and composition root for the SagaCoordinator worker process. The coordinator is a
/// headless background host (no HTTP endpoint): it builds a generic host, registers the Redis
/// client, ATProto storage, the provider queues it needs for secondary fan-out, and the
/// <see cref="SagaCoordinatorBackgroundService"/> that does the finalization work.
/// </summary>
public static class Program {

    /// <summary>
    /// Builds and runs the coordinator host. Configuration is validated during service registration,
    /// so a missing ATProto credential surfaces as a startup failure rather than a runtime error.
    /// </summary>
    /// <param name="args">Command-line arguments forwarded to the host builder.</param>
    public static void Main( string[] args ) {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder( args );

        ConfigureServices( builder );

        IHost app = builder.Build( );

        LogEnabledProviders( app );

        try {
            app.Run( );
        } finally {
            Log.CloseAndFlush( );
        }
    }

    /// <summary>
    /// Logs the set of providers enabled for secondary lookups once the logging pipeline is live.
    /// Must run after <c>builder.Build()</c>: <see cref="AspireServiceExtensions.ConfigureFileLogging(IHostApplicationBuilder, string)"/>
    /// defers Serilog logger construction to <c>Build()</c>, so a pre-Build log call would silently
    /// hit Serilog's no-op logger instead of reaching the configured sinks.
    /// </summary>
    /// <param name="app">The built host whose logger factory and enabled-provider set are resolved.</param>
    private static void LogEnabledProviders( IHost app ) {
        ILoggerFactory loggerFactory = app.Services.GetRequiredService<ILoggerFactory>( );
        Microsoft.Extensions.Logging.ILogger logger = loggerFactory.CreateLogger( "SagaCoordinator.Startup" );

        HashSet<SupportedProviders> enabledProviders = app.Services.GetRequiredService<HashSet<SupportedProviders>>( );
        string providersStr = enabledProviders.Count > 0
            ? string.Join( ", ", enabledProviders )
            : "(none)";

        ProgramLog.LogEnabledProviders( logger, providersStr );
    }

    /// <summary>
    /// Registers every service the coordinator depends on: file logging, Aspire service defaults,
    /// the Redis client, ATProto session and storage, the media-link cache, the
    /// <see cref="SagaResultCombiner"/>, all provider queues (so the coordinator can enqueue
    /// secondary lookups to any provider), and the hosted
    /// <see cref="SagaCoordinatorBackgroundService"/>. Validates configuration and detects the set
    /// of enabled providers before wiring dependent services.
    /// </summary>
    /// <param name="builder">The host application builder being configured.</param>
    private static void ConfigureServices( HostApplicationBuilder builder ) {
        // Configure file logging
        _ = builder.ConfigureFileLogging( "SagaCoordinator" );

        // Add Aspire service defaults (health checks, telemetry, resilience)
        _ = builder.AddServiceDefaults( );

        // Add Redis client from Aspire
        builder.AddRedisClient( "redis" );

        // Lane B: Data Protection — must use the same app name and key path as Web so
        // values encrypted in one process (Web, SagaCoordinator, Maintenance) can be
        // decrypted by any other.
        string dataProtectionKeyPath = builder.Configuration["BridgeBeats:DataProtectionKeyPath"] ?? DataProtectionExtensions.DefaultKeyPath;
        _ = builder.Services.AddBridgeBeatsDataProtection( dataProtectionKeyPath );

        // Read and validate credentials
        (string atProtoIdentifier, string atProtoPassword, string atProtoUserDID, int cacheDays) =
            ValidateConfiguration( builder );

        // Determine which providers are enabled based on configuration
        HashSet<SupportedProviders> enabledProviders = DetectEnabledProviders(builder);

        // Register enabled providers as a singleton
        _ = builder.Services.AddSingleton( enabledProviders );

        // Register queue infrastructure services
        _ = builder.Services.AddQueueInfrastructure( );

        // Register all provider queues for secondary lookups.
        // AddAllProviderQueues automatically applies SpotifyBulkQueueDecorator for QueuedLookupRequest,
        // routing Spotify SongIdLookup/AlbumIdLookup to the type-specific bulk streams.
        _ = builder.Services.AddAllProviderQueues<QueuedLookupRequest>( );

        // Register ATProto session manager and storage service (centralized authentication)
        int sessionTtlDays = builder.Configuration.GetValue( "BridgeBeats:ATProtoSessionTtlDays", 45 );
        _ = builder.Services.AddATProtoSessionManager( atProtoIdentifier, atProtoPassword, sessionTtlDays );
        _ = builder.Services.AddATProtoStorage( );

        // Register the cache repository
        _ = builder.Services.AddSingleton<IMediaLinkCacheRepository>( sp =>
            new RedisMediaLinkCache(
                sp.GetRequiredService<IConnectionMultiplexer>( ),
                sp.GetRequiredService<IATProtoStorageService>( ),
                sp.GetRequiredService<ILogger<RedisMediaLinkCache>>( ),
                cacheDays,
                atProtoUserDID
            )
        );

        // Register the result combiner
        _ = builder.Services.AddSingleton<SagaResultCombiner>( );

        // Register the coordinator background service
        _ = builder.Services.AddHostedService( sp => new SagaCoordinatorBackgroundService(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<ISagaStateManager>( ),
            sp.GetRequiredService<IATProtoStorageService>( ),
            sp.GetRequiredService<IMediaLinkCacheRepository>( ),
            sp.GetRequiredService<IRequestDeduplicator>( ),
            sp.GetRequiredService<SagaResultCombiner>( ),
            sp.GetRequiredService<IProviderQueueResolver<QueuedLookupRequest>>( ),
            sp.GetRequiredService<HashSet<SupportedProviders>>( ),
            sp.GetRequiredService<ILogger<SagaCoordinatorBackgroundService>>( ),
            sp.GetRequiredService<IRefreshReviewStore>( )
        ) );
    }

    /// <summary>
    /// Reads and validates the ATProto credentials and cache-retention setting from configuration.
    /// The user DID is additionally validated with
    /// <see cref="Core.Infrastructure.Storage.ATProtoUriHelper.ValidateDid(string, string)"/>.
    /// </summary>
    /// <param name="builder">The host application builder whose configuration is read.</param>
    /// <returns>
    /// A tuple of the ATProto identifier, app password, user DID, and the configured cache-retention
    /// window in days (defaulting to 30 when unset).
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when any of the required ATProto credentials are missing or blank.
    /// </exception>
    private static (string AtProtoIdentifier, string AtProtoPassword, string AtProtoUserDID, int CacheDays)
        ValidateConfiguration( HostApplicationBuilder builder ) {
        string? atProtoIdentifier = builder.Configuration["BridgeBeats:ATProtoIdentifier"];
        string? atProtoPassword = builder.Configuration["BridgeBeats:ATProtoPassword"];
        string? atProtoUserDID = builder.Configuration["BridgeBeats:ATProtoUserDID"];
        int cacheDays = builder.Configuration.GetValue("BridgeBeats:CacheDays", 30);

        if (string.IsNullOrWhiteSpace( atProtoIdentifier ) ||
            string.IsNullOrWhiteSpace( atProtoPassword ) ||
            string.IsNullOrWhiteSpace( atProtoUserDID )) {
            throw new InvalidOperationException(
                "ATProto credentials are required. Set BridgeBeats:ATProtoIdentifier, " +
                "BridgeBeats:ATProtoPassword, and BridgeBeats:ATProtoUserDID."
            );
        }

        // Validate DID format (must start with did:plc: or did:web:)
        ATProtoUriHelper.ValidateDid( atProtoUserDID, "BridgeBeats:ATProtoUserDID" );

        return (atProtoIdentifier, atProtoPassword, atProtoUserDID, cacheDays);
    }

    /// <summary>
    /// Determines which providers participate in secondary fan-out. The explicit
    /// <c>BridgeBeats:EnabledProviders</c> CSV (set by the AppHost from the actually-enabled workers)
    /// takes precedence; when it is absent the method falls back to detecting providers from their
    /// per-provider credentials. The returned set drives which other providers the coordinator
    /// queues secondary ISRC/UPC lookups to.
    /// </summary>
    /// <param name="builder">The host application builder whose configuration is read.</param>
    /// <returns>The set of <see cref="SupportedProviders"/> enabled for secondary lookups.</returns>
    private static HashSet<SupportedProviders> DetectEnabledProviders( HostApplicationBuilder builder ) {
        HashSet<SupportedProviders> enabledProviders = [];

        // First, try to read the EnabledProviders list from configuration (set by AppHost)
        string? enabledProvidersList = builder.Configuration["BridgeBeats:EnabledProviders"];
        if (!string.IsNullOrWhiteSpace( enabledProvidersList )) {
            foreach (string providerName in enabledProvidersList.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )) {
                if (Enum.TryParse<SupportedProviders>( providerName, ignoreCase: true, out SupportedProviders provider )) {
                    _ = enabledProviders.Add( provider );
                }
            }
            return enabledProviders;
        }

        // Fallback: Check for credentials directly (for standalone deployment)
        if (!string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:SpotifyClientId"] ) &&
            !string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:SpotifyClientSecret"] )) {
            _ = enabledProviders.Add( SupportedProviders.Spotify );
        }

        if (!string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:AppleTeamId"] ) &&
            !string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:AppleKeyId"] ) &&
            !string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:AppleKeyPath"] )) {
            _ = enabledProviders.Add( SupportedProviders.AppleMusic );
        }

        if (!string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:TidalClientId"] ) &&
            !string.IsNullOrWhiteSpace( builder.Configuration["BridgeBeats:TidalClientSecret"] )) {
            _ = enabledProviders.Add( SupportedProviders.Tidal );
        }

        return enabledProviders;
    }
}
