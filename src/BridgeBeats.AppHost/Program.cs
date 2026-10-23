using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

// BridgeBeats Aspire AppHost — the composition root and topology map for the whole system.
//
// This .NET Aspire orchestration host declares every BridgeBeats process and the dependencies
// between them. It is the single best file for understanding the runtime topology, so the wiring
// is described here in prose rather than only in cref links (the AppHost references the other
// projects as Aspire resources, not as code types).
//
// Resources provisioned and wired:
//   - redis: added as a CONNECTION-STRING resource only (AddConnectionString). Aspire does not
//     provision Redis here; an external Redis must exist and ConnectionStrings:redis must be set.
//     Redis is the shared backbone for every component below (work queue, saga state, Pub/Sub).
//   - Provider workers (spotify-worker :5100, applemusic-worker :5101, tidal-worker :5102): each
//     added only when its credentials are present. They expose HTTP for the synchronous WorkerApi
//     and consume the Redis work queue. Spotify additionally runs bulk and genre services.
//   - discord-worker: gateway consumer; no HTTP endpoint. References redis and the Web service.
//   - saga-coordinator: orchestrator/finalizer; no HTTP endpoint. References redis; receives the
//     ATProto credentials and the EnabledProviders CSV that drives secondary fan-out.
//   - jetstream-watcher: ATProto firehose consumer; no HTTP endpoint. References redis.
//   - maintenance: rebuilds the Redis lookup index from the PDS; no HTTP endpoint. References
//     redis and the ATProto credentials/PDS URI.
//   - bridgebeats (Web :10000, unproxied): public API and UI. References redis and every enabled
//     provider worker so it can fan interactive lookups out over the WorkerApi.
//
// Two run modes, branched on builder.Environment.IsProduction():
//   - Development: each component is an Aspire project resource (AddProject) with WithReference
//     wiring, so Aspire injects services__* service-discovery environment variables automatically.
//   - Production: each component is an executable resource (AddExecutable via the local
//     AddProductionExecutable helper) running a published DLL, and service-discovery variables are
//     hand-wired to localhost ports. Production therefore assumes all processes share one host.
//
// Configuration and secrets are threaded in as Aspire Parameters and environment variables using
// the BridgeBeats__Section__Key (double-underscore) convention. The set of enabled provider workers
// is joined into BridgeBeats__EnabledProviders (CSV) and passed to the saga coordinator and Web.

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder( args );

// Aspire Store Path Override.
// Aspire 13.4+ persists local state (IAspireStore) under the AppHost project's build-time
// intermediate output path (AppHostProjectBaseIntermediateOutputPath assembly metadata), which
// resolves to /build in the published container and does not exist at runtime. The builder seeds
// "Aspire:Store:Path" via an in-memory source added after the environment variable providers, so a
// plain environment variable cannot override it. Set it explicitly when provided.
string? aspireStorePath = Environment.GetEnvironmentVariable( "ASPIRE_STORE_PATH" );
if (!string.IsNullOrWhiteSpace( aspireStorePath )) {
    builder.Configuration["Aspire:Store:Path"] = aspireStorePath;
}

// Environment detection. In production (Docker) we use AddExecutable() with pre-published DLLs;
// in development we use AddProject<T>() for hot reload and debugging.
bool isProduction = builder.Environment.IsProduction();

// Configuration source. Aspire parameters can be provided via:
//   - Environment variables prefixed with "Parameters__" (e.g., Parameters__SpotifyClientId)
//   - Configuration section "Parameters:" in appsettings.json or user secrets
//   - Command line arguments (e.g., --Parameters:SpotifyClientId=value)
// The builder.Configuration object merges all sources, enabling flexible testing.
IConfiguration config = builder.Configuration;

// Music Provider Parameters - at least one music provider is required for the application to function.
IResourceBuilder<ParameterResource> spotifyClientId     = builder.AddParameter( "SpotifyClientId", secret: true );
IResourceBuilder<ParameterResource> spotifyClientSecret = builder.AddParameter( "SpotifyClientSecret", secret: true );
IResourceBuilder<ParameterResource> appleTeamId         = builder.AddParameter( "AppleTeamId", secret: true );
IResourceBuilder<ParameterResource> appleKeyId          = builder.AddParameter( "AppleKeyId", secret: true );
IResourceBuilder<ParameterResource> appleKeyPath        = builder.AddParameter( "AppleKeyPath" );
IResourceBuilder<ParameterResource> tidalClientId       = builder.AddParameter( "TidalClientId", secret: true );
IResourceBuilder<ParameterResource> tidalClientSecret   = builder.AddParameter( "TidalClientSecret", secret: true );

// Discord Bot Parameter.
IResourceBuilder<ParameterResource> discordToken = builder.AddParameter( "DiscordToken", secret: true );

// ATProto (Bluesky) Parameters.
IResourceBuilder<ParameterResource> atProtoIdentifier = builder.AddParameter( "ATProtoIdentifier", secret: true );
IResourceBuilder<ParameterResource> atProtoPassword   = builder.AddParameter( "ATProtoPassword", secret: true );
IResourceBuilder<ParameterResource> atProtoUserDID    = builder.AddParameter( "ATProtoUserDID", secret: true );
IResourceBuilder<ParameterResource> atProtoPdsUri     = builder.AddParameter( "ATProtoPdsUri" );

// Security Parameters.
IResourceBuilder<ParameterResource> apiKeySalt         = builder.AddParameter( "ApiKeySalt", secret: true );
IResourceBuilder<ParameterResource> internalServiceKey = builder.AddParameter( "InternalServiceKey", secret: true );

// Additional configuration parameters.
IResourceBuilder<ParameterResource> nodeNumber               = builder.AddParameter( "NodeNumber" );
IResourceBuilder<ParameterResource> domain                   = builder.AddParameter( "Domain" );
IResourceBuilder<ParameterResource> rateLimitRequestsPerHour = builder.AddParameter( "RateLimitRequestsPerHour" );
IResourceBuilder<ParameterResource> cacheDays                = builder.AddParameter( "CacheDays" );
IResourceBuilder<ParameterResource> identityConnectionString = builder.AddParameter( "IdentityConnectionString" );
IResourceBuilder<ParameterResource> logDirPath               = builder.AddParameter( "LogDirPath" );
IResourceBuilder<ParameterResource> cardCacheExpirationHours = builder.AddParameter( "CardCacheExpirationHours" );
IResourceBuilder<ParameterResource> cardCacheCleanupInterval = builder.AddParameter( "CardCacheCleanupInterval" );
IResourceBuilder<ParameterResource> cardCacheMaxEntries      = builder.AddParameter( "CardCacheMaxEntries" );
IResourceBuilder<ParameterResource> dataProtectionKeyPath    = builder.AddParameter( "DataProtectionKeyPath" );

// Resilience configuration parameters.
IResourceBuilder<ParameterResource> resilienceMaxRetryAfterSeconds  = builder.AddParameter( "ResilienceMaxRetryAfterSeconds" );
IResourceBuilder<ParameterResource> resilienceMaxRetryAttempts      = builder.AddParameter( "ResilienceMaxRetryAttempts" );
IResourceBuilder<ParameterResource> resilienceTotalTimeoutMinutes   = builder.AddParameter( "ResilienceTotalTimeoutMinutes" );
IResourceBuilder<ParameterResource> resilienceAttemptTimeoutSeconds = builder.AddParameter( "ResilienceAttemptTimeoutSeconds" );

// Stale-cache refresh parameters — optional; fall back to the worker's code defaults when unset.
IResourceBuilder<ParameterResource> refreshIntervalHours = builder.AddParameter( "RefreshIntervalHours",
    () => config["Parameters:RefreshIntervalHours"] ?? "6" );
IResourceBuilder<ParameterResource> maxRecordsPerRun = builder.AddParameter( "MaxRecordsPerRun",
    () => config["Parameters:MaxRecordsPerRun"] ?? "500" );
IResourceBuilder<ParameterResource> refreshRetryMinutes = builder.AddParameter( "RefreshRetryMinutes",
    () => config["Parameters:RefreshRetryMinutes"] ?? "5" );

// Redis is added as a connection-string resource only: Aspire does NOT provision it here. An external
// Redis must already exist and ConnectionStrings:redis must be set. Every component references this
// resource. If the connection string is missing the host writes an error to stderr but does not abort.
// In Docker, Redis availability is ensured by Docker Compose depends_on; in development it must be running.
IResourceBuilder<IResourceWithConnectionString> redis = builder.AddConnectionString( "redis" );

// Validate Redis connection string is configured.
if (string.IsNullOrWhiteSpace( config["ConnectionStrings:redis"] )) {
    Console.Error.WriteLine( "ERROR: Redis connection string is required but not configured." );
    Console.Error.WriteLine( "       Set ConnectionStrings:redis in appsettings.json or ConnectionStrings__redis environment variable." );
}

// Credential-presence checks. A provider/Discord worker is only added to the topology when its
// credentials are configured; SagaCoordinator, JetStreamWatcher, Maintenance, and Web are always
// added. These gates drive both the conditional resource registration and the EnabledProviders CSV.
// Configuration merges environment variables, appsettings, user secrets, and command line args.
bool HasSpotifyCredentials( ) =>
    !string.IsNullOrWhiteSpace( config["Parameters:SpotifyClientId"] ) &&
    !string.IsNullOrWhiteSpace( config["Parameters:SpotifyClientSecret"] );
bool HasAppleMusicCredentials( ) =>
    !string.IsNullOrWhiteSpace( config["Parameters:AppleTeamId"] ) &&
    !string.IsNullOrWhiteSpace( config["Parameters:AppleKeyId"] ) &&
    !string.IsNullOrWhiteSpace( config["Parameters:AppleKeyPath"] );
bool HasTidalCredentials( ) =>
    !string.IsNullOrWhiteSpace( config["Parameters:TidalClientId"] ) &&
    !string.IsNullOrWhiteSpace( config["Parameters:TidalClientSecret"] );
bool HasDiscordCredentials( ) => !string.IsNullOrWhiteSpace( config["Parameters:DiscordToken"] );

// Adds a production component as an Aspire executable resource that runs a published DLL, referencing
// the shared Redis resource and optionally exposing an HTTP endpoint. Used in the production run mode
// in place of the project resources used during development; the DLL is located under "/src".
//   name             - the Aspire resource name (for example "spotify-worker").
//   projectName      - the project/assembly name, used to build the DLL path under "/src".
//   httpPort         - the HTTP target port to expose, or null for a headless background host.
//   workingDirectory - the working directory for the executable; defaults to "/app/data".
// Returns the executable resource builder for further configuration.
IResourceBuilder<ExecutableResource> AddProductionExecutable(
    string name,
    string projectName,
    int? httpPort = null,
    string workingDirectory = "/app/data"
) {
    string dllPath = $"/src/{projectName}/{projectName}.dll";
    IResourceBuilder<ExecutableResource> resource = builder.AddExecutable( name, "dotnet", workingDirectory, dllPath )
        .WithReference(redis)
        .WithOtlpExporter( );

    if (httpPort.HasValue) {
        resource = resource.WithHttpEndpoint( targetPort: httpPort.Value, name: "http" );
    }

    return resource;
}

// Worker tracking for service discovery. Track references for WithReference() calls in both
// development and production.
IResourceBuilder<ProjectResource>? spotifyWorkerProject = null;
IResourceBuilder<ProjectResource>? appleMusicWorkerProject = null;
IResourceBuilder<ProjectResource>? tidalWorkerProject = null;
IResourceBuilder<ProjectResource>? discordWorkerProject = null;

IResourceBuilder<ExecutableResource>? discordWorkerExe = null;
IResourceBuilder<ExecutableResource>? bridgebeatsWebExe = null;

// Track which workers are enabled for web app configuration.
bool spotifyWorkerEnabled = HasSpotifyCredentials();
bool appleMusicWorkerEnabled = HasAppleMusicCredentials();
bool tidalWorkerEnabled = HasTidalCredentials();

// Spotify Worker.
if (spotifyWorkerEnabled) {
    if (isProduction) {
        _ = AddProductionExecutable( "spotify-worker", "BridgeBeats.Worker.Spotify", httpPort: 5100 )
            .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "5100" )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__SpotifyClientId", spotifyClientId )
            .WithEnvironment( "BridgeBeats__SpotifyClientSecret", spotifyClientSecret )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
            .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
            .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
    } else {
        spotifyWorkerProject = builder.AddProject<Projects.BridgeBeats_Worker_Spotify>( "spotify-worker" )
            .WithHttpEndpoint( targetPort: 5100, name: "http" );
        _ = spotifyWorkerProject
            .WithReference( redis )
            .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "5100" )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__SpotifyClientId", spotifyClientId )
            .WithEnvironment( "BridgeBeats__SpotifyClientSecret", spotifyClientSecret )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
            .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
            .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
    }
}

// Apple Music Worker.
if (appleMusicWorkerEnabled) {
    if (isProduction) {
        _ = AddProductionExecutable( "applemusic-worker", "BridgeBeats.Worker.AppleMusic", httpPort: 5101 )
            .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "5101" )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__AppleTeamId", appleTeamId )
            .WithEnvironment( "BridgeBeats__AppleKeyId", appleKeyId )
            .WithEnvironment( "BridgeBeats__AppleKeyPath", appleKeyPath )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
            .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
            .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
    } else {
        appleMusicWorkerProject = builder.AddProject<Projects.BridgeBeats_Worker_AppleMusic>( "applemusic-worker" )
            .WithHttpEndpoint( targetPort: 5101, name: "http" );
        _ = appleMusicWorkerProject
            .WithReference( redis )
            .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "5101" )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__AppleTeamId", appleTeamId )
            .WithEnvironment( "BridgeBeats__AppleKeyId", appleKeyId )
            .WithEnvironment( "BridgeBeats__AppleKeyPath", appleKeyPath )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
            .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
            .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
    }
}

// Tidal Worker.
if (tidalWorkerEnabled) {
    if (isProduction) {
        _ = AddProductionExecutable( "tidal-worker", "BridgeBeats.Worker.Tidal", httpPort: 5102 )
            .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "5102" )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__TidalClientId", tidalClientId )
            .WithEnvironment( "BridgeBeats__TidalClientSecret", tidalClientSecret )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
            .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
            .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
    } else {
        tidalWorkerProject = builder.AddProject<Projects.BridgeBeats_Worker_Tidal>( "tidal-worker" )
            .WithHttpEndpoint( targetPort: 5102, name: "http" );
        _ = tidalWorkerProject
            .WithReference( redis )
            .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "5102" )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__TidalClientId", tidalClientId )
            .WithEnvironment( "BridgeBeats__TidalClientSecret", tidalClientSecret )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
            .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
            .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
            .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
    }
}

// Discord Worker. Handles Discord gateway events, detecting music links and calling the
// BridgeBeats Web API for lookups and card generation.
if (HasDiscordCredentials( )) {
    if (isProduction) {
        discordWorkerExe = AddProductionExecutable( "discord-worker", "BridgeBeats.Worker.Discord" );
        _ = discordWorkerExe
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__DiscordToken", discordToken )
            .WithEnvironment( "BridgeBeats__NodeNumber", nodeNumber )
            .WithEnvironment( "BridgeBeats__Domain", domain )
            .WithEnvironment( "BridgeBeats__InternalServiceKey", internalServiceKey );
    } else {
        discordWorkerProject = builder.AddProject<Projects.BridgeBeats_Worker_Discord>( "discord-worker" );
        _ = discordWorkerProject
            .WithReference( redis )
            .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
            .WithEnvironment( "BridgeBeats__DiscordToken", discordToken )
            .WithEnvironment( "BridgeBeats__NodeNumber", nodeNumber )
            .WithEnvironment( "BridgeBeats__Domain", domain )
            .WithEnvironment( "BridgeBeats__InternalServiceKey", internalServiceKey );
    }
}

// Saga Coordinator Worker. The saga coordinator monitors for completed multi-provider lookups and
// writes final results to ATProto. It uses Redis Pub/Sub for event-driven coordination and polling
// as a fallback.
//
// Provider fan-out list. The set of actually-enabled provider workers is joined into a CSV and passed
// as BridgeBeats__EnabledProviders to the saga coordinator (which uses it to drive secondary-lookup
// fan-out) and is surfaced to Web as per-provider Workers__*WorkerEnabled flags below.
List<string> enabledProvidersList = [];
if (spotifyWorkerEnabled) {
    enabledProvidersList.Add( "Spotify" );
}
if (appleMusicWorkerEnabled) {
    enabledProvidersList.Add( "AppleMusic" );
}
if (tidalWorkerEnabled) {
    enabledProvidersList.Add( "Tidal" );
}
string enabledProvidersValue = string.Join( ",", enabledProvidersList );

if (isProduction) {
    _ = AddProductionExecutable( "saga-coordinator", "BridgeBeats.Worker.SagaCoordinator" )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
        .WithEnvironment( "BridgeBeats__ATProtoIdentifier", atProtoIdentifier )
        .WithEnvironment( "BridgeBeats__ATProtoPassword", atProtoPassword )
        .WithEnvironment( "BridgeBeats__ATProtoUserDID", atProtoUserDID )
        .WithEnvironment( "BridgeBeats__CacheDays", cacheDays )
        .WithEnvironment( "BridgeBeats__EnabledProviders", enabledProvidersValue )
        .WithEnvironment( "BridgeBeats__DataProtectionKeyPath", dataProtectionKeyPath );
} else {
    _ = builder.AddProject<Projects.BridgeBeats_Worker_SagaCoordinator>( "saga-coordinator" )
        .WithReference( redis )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
        .WithEnvironment( "BridgeBeats__ATProtoIdentifier", atProtoIdentifier )
        .WithEnvironment( "BridgeBeats__ATProtoPassword", atProtoPassword )
        .WithEnvironment( "BridgeBeats__ATProtoUserDID", atProtoUserDID )
        .WithEnvironment( "BridgeBeats__CacheDays", cacheDays )
        .WithEnvironment( "BridgeBeats__EnabledProviders", enabledProvidersValue )
        .WithEnvironment( "BridgeBeats__DataProtectionKeyPath", dataProtectionKeyPath );
}

// JetStream Watcher Worker. Monitors Bluesky Jetstream for music links and submits them to provider
// queues at bulk priority. Fire-and-forget: no credentials needed.
if (isProduction) {
    _ = AddProductionExecutable( "jetstream-watcher", "BridgeBeats.Worker.JetStreamWatcher" )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath );
} else {
    _ = builder.AddProject<Projects.BridgeBeats_Worker_JetStreamWatcher>( "jetstream-watcher" )
        .WithReference( redis )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath );
}

// Cache Bootstrap Worker. Bootstraps Redis cache from ATProto public records on startup and
// periodically (default: every 6 hours). Uses unauthenticated access for public records.
if (isProduction) {
    _ = AddProductionExecutable( "maintenance", "BridgeBeats.Worker.Maintenance" )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
        .WithEnvironment( "BridgeBeats__ATProtoIdentifier", atProtoIdentifier )
        .WithEnvironment( "BridgeBeats__ATProtoPassword", atProtoPassword )
        .WithEnvironment( "BridgeBeats__ATProtoUserDID", atProtoUserDID )
        .WithEnvironment( "BridgeBeats__ATProtoPdsUri", atProtoPdsUri )
        .WithEnvironment( "BridgeBeats__CacheDays", cacheDays )
        .WithEnvironment( "BridgeBeats__DataProtectionKeyPath", dataProtectionKeyPath )
        .WithEnvironment( "BridgeBeats__EnabledProviders", enabledProvidersValue )
        .WithEnvironment( "BridgeBeats__RefreshIntervalHours", refreshIntervalHours )
        .WithEnvironment( "BridgeBeats__MaxRecordsPerRun", maxRecordsPerRun )
        .WithEnvironment( "BridgeBeats__RefreshRetryMinutes", refreshRetryMinutes );
} else {
    _ = builder.AddProject<Projects.BridgeBeats_Worker_Maintenance>( "maintenance" )
        .WithReference( redis )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
        .WithEnvironment( "BridgeBeats__ATProtoIdentifier", atProtoIdentifier )
        .WithEnvironment( "BridgeBeats__ATProtoPassword", atProtoPassword )
        .WithEnvironment( "BridgeBeats__ATProtoUserDID", atProtoUserDID )
        .WithEnvironment( "BridgeBeats__ATProtoPdsUri", atProtoPdsUri )
        .WithEnvironment( "BridgeBeats__CacheDays", cacheDays )
        .WithEnvironment( "BridgeBeats__DataProtectionKeyPath", dataProtectionKeyPath )
        .WithEnvironment( "BridgeBeats__EnabledProviders", enabledProvidersValue )
        .WithEnvironment( "BridgeBeats__RefreshIntervalHours", refreshIntervalHours )
        .WithEnvironment( "BridgeBeats__MaxRecordsPerRun", maxRecordsPerRun )
        .WithEnvironment( "BridgeBeats__RefreshRetryMinutes", refreshRetryMinutes );
}

// Main Web Application. In production, service discovery uses resource names for endpoint resolution.
// Worker references are registered via WithReference() for service discovery.
IResourceBuilder<ProjectResource>? bridgebeatsWebProject = null;

if (isProduction) {
    bridgebeatsWebExe = AddProductionExecutable( "bridgebeats", "BridgeBeats.Web", workingDirectory: "/src/BridgeBeats.Web" );
    _ = bridgebeatsWebExe
        .WithHttpEndpoint( port: 10000, targetPort: 10000, name: "bridgebeats-http", isProxied: false )
        .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "10000" )
        .WithEnvironment( "BridgeBeats__Workers__UseWorkerServices", "true" )
        .WithEnvironment( "BridgeBeats__Workers__SpotifyWorkerEnabled", spotifyWorkerEnabled ? "true" : "false" )
        .WithEnvironment( "BridgeBeats__Workers__AppleMusicWorkerEnabled", appleMusicWorkerEnabled ? "true" : "false" )
        .WithEnvironment( "BridgeBeats__Workers__TidalWorkerEnabled", tidalWorkerEnabled ? "true" : "false" )
        .WithEnvironment( "BridgeBeats__ATProtoIdentifier", atProtoIdentifier )
        .WithEnvironment( "BridgeBeats__ATProtoPassword", atProtoPassword )
        .WithEnvironment( "BridgeBeats__ATProtoUserDID", atProtoUserDID )
        .WithEnvironment( "BridgeBeats__ATProtoPdsUri", atProtoPdsUri )
        .WithEnvironment( "BridgeBeats__ApiKeySalt", apiKeySalt )
        .WithEnvironment( "BridgeBeats__InternalServiceKey", internalServiceKey )
        .WithEnvironment( "BridgeBeats__Domain", domain )
        .WithEnvironment( "BridgeBeats__RateLimitRequestsPerHour", rateLimitRequestsPerHour )
        .WithEnvironment( "BridgeBeats__CacheDays", cacheDays )
        .WithEnvironment( "BridgeBeats__IdentityConnectionString", identityConnectionString )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
        .WithEnvironment( "BridgeBeats__DataProtectionKeyPath", dataProtectionKeyPath )
        .WithEnvironment( "BridgeBeats__CardCacheExpirationHours", cardCacheExpirationHours )
        .WithEnvironment( "BridgeBeats__CardCacheCleanupInterval", cardCacheCleanupInterval )
        .WithEnvironment( "BridgeBeats__CardCacheMaxEntries", cardCacheMaxEntries )
        .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
        .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
        .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
        .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
} else {
    bridgebeatsWebProject = builder.AddProject<Projects.BridgeBeats_Web>( "bridgebeats" )
        .WithHttpEndpoint( port: 10000, targetPort: 10000, name: "bridgebeats-http", isProxied: false );
    _ = bridgebeatsWebProject
        .WithReference( redis )
        .WithEnvironment( "ASPNETCORE_HTTP_PORTS", "10000" )
        .WithEnvironment( "BridgeBeats__Workers__UseWorkerServices", "true" )
        .WithEnvironment( "BridgeBeats__Workers__SpotifyWorkerEnabled", spotifyWorkerEnabled ? "true" : "false" )
        .WithEnvironment( "BridgeBeats__Workers__AppleMusicWorkerEnabled", appleMusicWorkerEnabled ? "true" : "false" )
        .WithEnvironment( "BridgeBeats__Workers__TidalWorkerEnabled", tidalWorkerEnabled ? "true" : "false" )
        .WithEnvironment( "BridgeBeats__ATProtoIdentifier", atProtoIdentifier )
        .WithEnvironment( "BridgeBeats__ATProtoPassword", atProtoPassword )
        .WithEnvironment( "BridgeBeats__ATProtoUserDID", atProtoUserDID )
        .WithEnvironment( "BridgeBeats__ATProtoPdsUri", atProtoPdsUri )
        .WithEnvironment( "BridgeBeats__ApiKeySalt", apiKeySalt )
        .WithEnvironment( "BridgeBeats__InternalServiceKey", internalServiceKey )
        .WithEnvironment( "BridgeBeats__Domain", domain )
        .WithEnvironment( "BridgeBeats__RateLimitRequestsPerHour", rateLimitRequestsPerHour )
        .WithEnvironment( "BridgeBeats__CacheDays", cacheDays )
        .WithEnvironment( "BridgeBeats__IdentityConnectionString", identityConnectionString )
        .WithEnvironment( "BridgeBeats__LogDirPath", logDirPath )
        .WithEnvironment( "BridgeBeats__DataProtectionKeyPath", dataProtectionKeyPath )
        .WithEnvironment( "BridgeBeats__CardCacheExpirationHours", cardCacheExpirationHours )
        .WithEnvironment( "BridgeBeats__CardCacheCleanupInterval", cardCacheCleanupInterval )
        .WithEnvironment( "BridgeBeats__CardCacheMaxEntries", cardCacheMaxEntries )
        .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAfterSeconds", resilienceMaxRetryAfterSeconds )
        .WithEnvironment( "BridgeBeats__Resilience__MaxRetryAttempts", resilienceMaxRetryAttempts )
        .WithEnvironment( "BridgeBeats__Resilience__TotalTimeoutMinutes", resilienceTotalTimeoutMinutes )
        .WithEnvironment( "BridgeBeats__Resilience__AttemptTimeoutSeconds", resilienceAttemptTimeoutSeconds );
}

// Service discovery references. Wire up service discovery between web app and workers.
//   Development: uses WithReference() to inject services__<name>__http__0 environment variables.
//   Production: uses WithEnvironment() to directly set localhost URLs with known ports.
// Either way: Web references the enabled provider workers (it fans interactive lookups out to their
// WorkerApi endpoints) and the Discord worker references Web (it calls Web over internal HTTP).
if (isProduction) {
    // Production: hand-wire service discovery to fixed localhost ports (all processes share a host).
    // Web discovers the provider workers; the Discord worker discovers Web.
    if (bridgebeatsWebExe is not null) {
        _ = bridgebeatsWebExe
            .WithEnvironment( "services__spotify-worker__http__0", "http://localhost:5100" )
            .WithEnvironment( "services__applemusic-worker__http__0", "http://localhost:5101" )
            .WithEnvironment( "services__tidal-worker__http__0", "http://localhost:5102" );
    }
    if (discordWorkerExe is not null) {
        _ = discordWorkerExe
            .WithEnvironment( "services__bridgebeats__http__0", "http://localhost:10000" );
    }
} else {
    // Development: Aspire injects service-discovery variables from WithReference wiring.
    if (bridgebeatsWebProject is not null) {
        if (spotifyWorkerProject is not null) {
            _ = bridgebeatsWebProject.WithReference( spotifyWorkerProject );
        }
        if (appleMusicWorkerProject is not null) {
            _ = bridgebeatsWebProject.WithReference( appleMusicWorkerProject );
        }
        if (tidalWorkerProject is not null) {
            _ = bridgebeatsWebProject.WithReference( tidalWorkerProject );
        }
    }
    if (discordWorkerProject is not null && bridgebeatsWebProject is not null) {
        _ = discordWorkerProject.WithReference( bridgebeatsWebProject );
    }
}

builder.Build( ).Run( );
