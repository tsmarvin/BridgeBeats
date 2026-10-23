using System.Reflection;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.Retry;
using Serilog;

namespace BridgeBeats.Core.Domain.Extensions;

/// <summary>
/// .NET Aspire service-defaults wiring shared by the web app and the worker hosts. Configures service
/// discovery, health checks, the standard HTTP resilience pipeline (exponential backoff with jitter,
/// circuit breaker, and total/attempt timeouts), OpenTelemetry traces/metrics/logs, and Serilog file
/// logging. The <see cref="WebApplicationBuilder"/> and <see cref="IHostApplicationBuilder"/> overloads
/// are near-duplicates serving the two host shapes.
/// </summary>
public static class AspireServiceExtensions {
    /// <summary>The health-check endpoint path mapped by <see cref="MapDefaultEndpoints"/>.</summary>
    private const string HealthPath = "/health";

    /// <summary>
    /// Default AttemptTimeout in seconds, consumed by both AddServiceDefaults overloads.
    /// Exposed as internal so tests can assert against the canonical value rather than restating it.
    /// </summary>
    internal const int DefaultAttemptTimeoutSeconds = 120;

    /// <summary>
    /// Adds the Aspire service defaults to a web-application host: service discovery, health checks,
    /// the standard HTTP resilience pipeline, and OpenTelemetry. The resilience pipeline uses
    /// exponential backoff with jitter, honors server <c>Retry-After</c> headers, and is explicitly
    /// configured <b>not</b> to retry <see cref="BridgeBeats.Contracts.Exceptions.ProviderRateLimitException"/>
    /// (including its provider fail-fast subtype). Retry/timeout values are read from <c>BridgeBeats:Resilience:*</c>
    /// configuration, falling back to built-in defaults.
    /// </summary>
    /// <param name="builder">The web-application host builder to configure.</param>
    /// <returns>The same <paramref name="builder"/>, to allow call chaining.</returns>
    public static WebApplicationBuilder AddServiceDefaults( this WebApplicationBuilder builder ) {
        _ = builder.Services.AddServiceDiscovery( );
        _ = builder.Services.AddHealthChecks( );

        // Read resilience configuration with defaults
        int maxRetryAttempts = builder.Configuration.GetValue( "BridgeBeats:Resilience:MaxRetryAttempts", 5 );
        int totalTimeoutMinutes = builder.Configuration.GetValue( "BridgeBeats:Resilience:TotalTimeoutMinutes", 10 );
        int attemptTimeoutSeconds = builder.Configuration.GetValue( "BridgeBeats:Resilience:AttemptTimeoutSeconds", DefaultAttemptTimeoutSeconds );

        _ = builder.Services.ConfigureHttpClientDefaults( http => {
            _ = http.AddServiceDiscovery( );

            IHttpStandardResiliencePipelineBuilder resilienceBuilder = http.AddStandardResilienceHandler( options => {
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;
                options.Retry.MaxRetryAttempts = maxRetryAttempts;
                options.Retry.Delay = TimeSpan.FromSeconds( 3 );
                options.Retry.MaxDelay = TimeSpan.FromMinutes( 5 );
                options.Retry.ShouldRetryAfterHeader = true;
                options.Retry.DisableForUnsafeHttpMethods( );

                // Exclude ProviderRateLimitException (including RetryAfterExceededException) from
                // retry logic; these are handled by the provider queue's rate-limit path.
                Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> originalShouldHandle = options.Retry.ShouldHandle;
                options.Retry.ShouldHandle = args => {
                    // ProviderRateLimitException is intentionally handled by the queue consumer.
                    return args.Outcome.Exception is ProviderRateLimitException ? ValueTask.FromResult( false ) : originalShouldHandle( args );
                };

                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes( totalTimeoutMinutes );
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds( attemptTimeoutSeconds );
                // Standard-handler validator requires SamplingDuration >= 2 x AttemptTimeout; 30s floor matches the option default.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds( Math.Max( 2 * attemptTimeoutSeconds, 30 ) );
            } );

            _ = resilienceBuilder.SelectPipelineByAuthority( ).Configure( ( options, sp ) => {
                Microsoft.Extensions.Logging.ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("HttpResilience");
                options.Retry.OnRetry = args => {
                    TimeSpan? retryAfter = args.Outcome.Result?.Headers.RetryAfter?.Delta;
                    double retryAfterSeconds = retryAfter?.TotalSeconds ?? 0;

                    if (retryAfterSeconds > 0) {
                        AspireServiceExtensionsLog.LogRetryWithRetryAfterHeader(
                            logger,
                            args.AttemptNumber,
                            options.Retry.MaxRetryAttempts,
                            retryAfterSeconds,
                            args.Outcome.Result?.RequestMessage?.RequestUri
                        );
                    } else {
                        AspireServiceExtensionsLog.LogRetryWithExponentialBackoff(
                            logger,
                            args.AttemptNumber,
                            options.Retry.MaxRetryAttempts,
                            args.Outcome.Result?.RequestMessage?.RequestUri
                        );
                    }

                    return default;
                };
            } );
        } );

        ConfigureOpenTelemetry( builder );

        return builder;
    }

    /// <summary>
    /// Maps the default <c>/health</c> health-check endpoint. The predicate is set to exclude all
    /// registered checks, so the endpoint reports liveness only (it returns healthy without running
    /// individual readiness checks).
    /// </summary>
    /// <param name="app">The web application to map the endpoint onto.</param>
    /// <returns>The same <paramref name="app"/>, to allow call chaining.</returns>
    public static WebApplication MapDefaultEndpoints( this WebApplication app ) {
        _ = app.MapHealthChecks( HealthPath, new HealthCheckOptions {
            Predicate = _ => false
        } );

        return app;
    }

    /// <summary>
    /// Adds the Aspire service defaults to a generic host (worker services): service discovery, the
    /// standard HTTP resilience pipeline, and OpenTelemetry. This is the worker counterpart to the
    /// <see cref="AddServiceDefaults(WebApplicationBuilder)"/> overload and shares the same resilience
    /// configuration, including not retrying
    /// <see cref="BridgeBeats.Contracts.Exceptions.ProviderRateLimitException"/>. Health checks are
    /// not registered here because generic hosts do not expose the HTTP health endpoint.
    /// </summary>
    /// <param name="builder">The generic host builder to configure.</param>
    /// <returns>The same <paramref name="builder"/>, to allow call chaining.</returns>
    public static IHostApplicationBuilder AddServiceDefaults( this IHostApplicationBuilder builder ) {
        _ = builder.Services.AddServiceDiscovery( );

        // Read resilience configuration with defaults
        int maxRetryAttempts = builder.Configuration.GetValue( "BridgeBeats:Resilience:MaxRetryAttempts", 5 );
        int totalTimeoutMinutes = builder.Configuration.GetValue( "BridgeBeats:Resilience:TotalTimeoutMinutes", 10 );
        int attemptTimeoutSeconds = builder.Configuration.GetValue( "BridgeBeats:Resilience:AttemptTimeoutSeconds", DefaultAttemptTimeoutSeconds );

        _ = builder.Services.ConfigureHttpClientDefaults( http => {
            _ = http.AddServiceDiscovery( );

            IHttpStandardResiliencePipelineBuilder resilienceBuilder = http.AddStandardResilienceHandler( options => {
                options.Retry.BackoffType = DelayBackoffType.Exponential;
                options.Retry.UseJitter = true;
                options.Retry.MaxRetryAttempts = maxRetryAttempts;
                options.Retry.Delay = TimeSpan.FromSeconds( 3 );
                options.Retry.MaxDelay = TimeSpan.FromMinutes( 5 );
                options.Retry.ShouldRetryAfterHeader = true;
                options.Retry.DisableForUnsafeHttpMethods( );

                // Exclude ProviderRateLimitException (including RetryAfterExceededException) from
                // retry logic; these are handled by the provider queue's rate-limit path.
                Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> originalShouldHandle = options.Retry.ShouldHandle;
                options.Retry.ShouldHandle = args => {
                    // ProviderRateLimitException is intentionally handled by the queue consumer.
                    return args.Outcome.Exception is ProviderRateLimitException ? ValueTask.FromResult( false ) : originalShouldHandle( args );
                };

                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes( totalTimeoutMinutes );
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds( attemptTimeoutSeconds );
                // Standard-handler validator requires SamplingDuration >= 2 x AttemptTimeout; 30s floor matches the option default.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds( Math.Max( 2 * attemptTimeoutSeconds, 30 ) );
            } );

            _ = resilienceBuilder.SelectPipelineByAuthority( ).Configure( ( options, sp ) => {
                Microsoft.Extensions.Logging.ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("HttpResilience");
                options.Retry.OnRetry = args => {
                    TimeSpan? retryAfter = args.Outcome.Result?.Headers.RetryAfter?.Delta;
                    double retryAfterSeconds = retryAfter?.TotalSeconds ?? 0;

                    if (retryAfterSeconds > 0) {
                        AspireServiceExtensionsLog.LogRetryWithRetryAfterHeader(
                            logger,
                            args.AttemptNumber,
                            options.Retry.MaxRetryAttempts,
                            retryAfterSeconds,
                            args.Outcome.Result?.RequestMessage?.RequestUri
                        );
                    } else {
                        AspireServiceExtensionsLog.LogRetryWithExponentialBackoff(
                            logger,
                            args.AttemptNumber,
                            options.Retry.MaxRetryAttempts,
                            args.Outcome.Result?.RequestMessage?.RequestUri
                        );
                    }

                    return default;
                };
            } );
        } );

        ConfigureOpenTelemetryForHost( builder );

        return builder;
    }

    /// <summary>
    /// Configures OpenTelemetry for a web-application host. Wires logging, tracing, and metrics
    /// exporters to an OTLP endpoint (when one is configured), enabling ASP.NET Core, HTTP-client, and
    /// runtime instrumentation and registering the <c>BridgeBeats.Queue</c>,
    /// <c>BridgeBeats.Providers</c>, and <c>BridgeBeats.Spotify.Batch</c> meters. Tracing and metrics
    /// are each gated on the <c>OpenTelemetry:EnableTracing</c> and <c>OpenTelemetry:EnableMetrics</c>
    /// settings (both default on).
    /// </summary>
    /// <param name="builder">The web-application host builder to configure.</param>
    private static void ConfigureOpenTelemetry( WebApplicationBuilder builder ) {
        string? otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        string? otlpHeaders = builder.Configuration["OpenTelemetry:OtlpHeaders"];
        bool enableTracing = builder.Configuration.GetValue( "OpenTelemetry:EnableTracing", true );
        bool enableMetrics = builder.Configuration.GetValue( "OpenTelemetry:EnableMetrics", true );

        bool hasCustomEndpoint = TryGetOtlpEndpoint( otlpEndpoint, out Uri? otlpUri );

        ResourceBuilder resourceBuilder = CreateResourceBuilder(
            builder.Configuration,
            builder.Environment.ApplicationName );

        _ = builder.Logging.AddOpenTelemetry( options => {
            _ = options.SetResourceBuilder( resourceBuilder );
            _ = options.AddOtlpExporter( otlpOptions => {
                if (hasCustomEndpoint) {
                    otlpOptions.Endpoint = otlpUri!;
                }

                if (!string.IsNullOrWhiteSpace( otlpHeaders )) {
                    otlpOptions.Headers = otlpHeaders;
                }
            } );
        } );

        OpenTelemetryBuilder openTelemetryBuilder = builder.Services.AddOpenTelemetry( );

        if (enableTracing) {
            _ = openTelemetryBuilder.WithTracing( tracing => {
                _ = tracing
                    .SetResourceBuilder( resourceBuilder )
                    .AddAspNetCoreInstrumentation( )
                    .AddHttpClientInstrumentation( )
                    .AddSource( QueueMetrics.ActivitySourceName );

                _ = tracing.AddOtlpExporter( otlpOptions => {
                    if (hasCustomEndpoint) {
                        otlpOptions.Endpoint = otlpUri!;
                    }

                    if (!string.IsNullOrWhiteSpace( otlpHeaders )) {
                        otlpOptions.Headers = otlpHeaders;
                    }
                } );
            } );
        }

        if (enableMetrics) {
            _ = openTelemetryBuilder.WithMetrics( metrics => {
                _ = metrics
                    .SetResourceBuilder( resourceBuilder )
                    .AddAspNetCoreInstrumentation( )
                    .AddHttpClientInstrumentation( )
                    .AddRuntimeInstrumentation( )
                    .AddMeter( QueueMetrics.MeterName )
                    .AddMeter( "BridgeBeats.Providers" )
                    .AddMeter( "BridgeBeats.Spotify.Batch" );

                _ = metrics.AddOtlpExporter( otlpOptions => {
                    if (hasCustomEndpoint) {
                        otlpOptions.Endpoint = otlpUri!;
                    }

                    if (!string.IsNullOrWhiteSpace( otlpHeaders )) {
                        otlpOptions.Headers = otlpHeaders;
                    }
                } );
            } );
        }
    }

    /// <summary>
    /// Configures OpenTelemetry for a generic host (worker services). Mirrors
    /// <see cref="ConfigureOpenTelemetry"/> but omits ASP.NET Core instrumentation, since worker hosts
    /// serve no inbound HTTP. Wires logging, tracing, and metrics exporters to a configured OTLP
    /// endpoint, enables HTTP-client and runtime instrumentation, and registers the
    /// <c>BridgeBeats.Queue</c>, <c>BridgeBeats.Providers</c>, and <c>BridgeBeats.Spotify.Batch</c>
    /// meters.
    /// </summary>
    /// <param name="builder">The generic host builder to configure.</param>
    private static void ConfigureOpenTelemetryForHost( IHostApplicationBuilder builder ) {
        string? otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        string? otlpHeaders = builder.Configuration["OpenTelemetry:OtlpHeaders"];
        bool enableTracing = builder.Configuration.GetValue( "OpenTelemetry:EnableTracing", true );
        bool enableMetrics = builder.Configuration.GetValue( "OpenTelemetry:EnableMetrics", true );

        bool hasCustomEndpoint = TryGetOtlpEndpoint( otlpEndpoint, out Uri? otlpUri );

        ResourceBuilder resourceBuilder = CreateResourceBuilder(
            builder.Configuration,
            builder.Environment.ApplicationName );

        _ = builder.Logging.AddOpenTelemetry( options => {
            _ = options.SetResourceBuilder( resourceBuilder );
            _ = options.AddOtlpExporter( otlpOptions => {
                if (hasCustomEndpoint) {
                    otlpOptions.Endpoint = otlpUri!;
                }

                if (!string.IsNullOrWhiteSpace( otlpHeaders )) {
                    otlpOptions.Headers = otlpHeaders;
                }
            } );
        } );

        OpenTelemetryBuilder openTelemetryBuilder = builder.Services.AddOpenTelemetry( );

        if (enableTracing) {
            _ = openTelemetryBuilder.WithTracing( tracing => {
                // Note: No AddAspNetCoreInstrumentation() for non-web hosts
                _ = tracing
                    .SetResourceBuilder( resourceBuilder )
                    .AddHttpClientInstrumentation( )
                    .AddSource( QueueMetrics.ActivitySourceName );

                _ = tracing.AddOtlpExporter( otlpOptions => {
                    if (hasCustomEndpoint) {
                        otlpOptions.Endpoint = otlpUri!;
                    }

                    if (!string.IsNullOrWhiteSpace( otlpHeaders )) {
                        otlpOptions.Headers = otlpHeaders;
                    }
                } );
            } );
        }

        if (enableMetrics) {
            _ = openTelemetryBuilder.WithMetrics( metrics => {
                // Note: No AddAspNetCoreInstrumentation() for non-web hosts
                _ = metrics
                    .SetResourceBuilder( resourceBuilder )
                    .AddHttpClientInstrumentation( )
                    .AddRuntimeInstrumentation( )
                    .AddMeter( QueueMetrics.MeterName )
                    .AddMeter( "BridgeBeats.Providers" )
                    .AddMeter( "BridgeBeats.Spotify.Batch" );

                _ = metrics.AddOtlpExporter( otlpOptions => {
                    if (hasCustomEndpoint) {
                        otlpOptions.Endpoint = otlpUri!;
                    }

                    if (!string.IsNullOrWhiteSpace( otlpHeaders )) {
                        otlpOptions.Headers = otlpHeaders;
                    }
                } );
            } );
        }
    }

    /// <summary>
    /// Parses a configured OTLP endpoint string into an absolute <see cref="Uri"/>. A blank value
    /// yields no endpoint (use the exporter default); a non-blank but malformed value logs a warning to
    /// the console and disables custom-endpoint export.
    /// </summary>
    /// <param name="otlpEndpoint">The configured endpoint string, or <see langword="null"/>.</param>
    /// <param name="otlpUri">
    /// On return, the parsed absolute URI, or <see langword="null"/> when none was usable.
    /// </param>
    /// <returns><see langword="true"/> when a valid custom endpoint was parsed; otherwise <see langword="false"/>.</returns>
    private static bool TryGetOtlpEndpoint( string? otlpEndpoint, out Uri? otlpUri ) {
        otlpUri = null;

        if (string.IsNullOrWhiteSpace( otlpEndpoint )) {
            return false;
        }

        if (!Uri.TryCreate( otlpEndpoint, UriKind.Absolute, out Uri? uri )) {
            Console.WriteLine( $"Warning: Invalid OTLP endpoint URL '{otlpEndpoint}' - OpenTelemetry export disabled" );
            return false;
        }

        otlpUri = uri;
        return true;
    }

    /// <summary>
    /// Creates a common OpenTelemetry resource for all signals. Aspire supplies
    /// <c>OTEL_SERVICE_NAME</c> for each resource; standalone processes fall back to
    /// the entry application's name. <see cref="ResourceBuilder.CreateDefault"/>
    /// retains standard <c>OTEL_RESOURCE_ATTRIBUTES</c>, including Aspire's instance id.
    /// </summary>
    private static ResourceBuilder CreateResourceBuilder(
        IConfiguration configuration,
        string fallbackServiceName
    ) {
        string serviceName = configuration["OTEL_SERVICE_NAME"]?.Trim( ) ?? string.Empty;
        if (string.IsNullOrWhiteSpace( serviceName )) {
            serviceName = fallbackServiceName;
        }

        return ResourceBuilder.CreateDefault( )
            .AddService(
                serviceName: serviceName,
                serviceVersion: GetServiceVersion( ) );
    }

    /// <summary>
    /// Returns the entry assembly's version string for the OpenTelemetry resource, falling back to
    /// <c>"0.0.1"</c> when no entry assembly or version is available.
    /// </summary>
    /// <returns>The service version string.</returns>
    private static string GetServiceVersion( ) {
        Assembly? assembly = Assembly.GetEntryAssembly( );
        return assembly?.GetName( ).Version?.ToString( ) ?? "0.0.1";
    }

    /// <summary>
    /// Configures Serilog console and rolling-file logging for a web-application host. Log files roll
    /// daily and at 50&#160;MB, keeping the five most recent. Health-check noise is filtered out:
    /// <c>Information</c>-level events from ASP.NET Core MVC/routing whose action or message mentions
    /// "Health", and successful (200) requests to the health/alive endpoints, are excluded. If the log
    /// directory cannot be created, a warning is written to the console and logging is left unconfigured.
    /// </summary>
    /// <param name="builder">The web-application host builder to configure.</param>
    /// <param name="projectName">The project name used as the log-file name prefix.</param>
    /// <returns>The same <paramref name="builder"/>, to allow call chaining.</returns>
    public static WebApplicationBuilder ConfigureFileLogging( this WebApplicationBuilder builder, string projectName ) {
        // Use LogDirPath from configuration or default to ./logs
        string logDir = builder.Configuration["BridgeBeats:LogDirPath"] ?? "./logs";
        string logPath = Path.Combine(logDir, $"{projectName}-.log");

        try {
            if (!string.IsNullOrEmpty( logDir ) && !Directory.Exists( logDir )) {
                _ = Directory.CreateDirectory( logDir );
            }
        } catch (IOException ex) {
            Console.WriteLine( $"Warning: Failed to validate/create log directory: {ex.Message}" );
            // Fall back to not configuring file logging
            return builder;
        } catch (UnauthorizedAccessException ex) {
            Console.WriteLine( $"Warning: Failed to validate/create log directory due to insufficient permissions: {ex.Message}" );
            // Fall back to not configuring file logging
            return builder;
        }

        _ = builder.Host.UseSerilog( ( context, services, loggerConfiguration ) => loggerConfiguration
            .ReadFrom.Configuration( context.Configuration )
            .ReadFrom.Services( services )
            .Filter.ByExcluding( logEvent => {
                // Exclude successful health check requests from logs (but keep failures)
                // This filters out Information level logs for /health endpoint
                if (logEvent.Level != Serilog.Events.LogEventLevel.Information) {
                    return false; // Don't exclude warnings, errors, etc.
                }

                // Check if this is a log event related to health endpoint
                string? sourceContext = logEvent.Properties.TryGetValue("SourceContext", out Serilog.Events.LogEventPropertyValue? sourceValue)
                    ? sourceValue.ToString().Trim('"')
                    : null;

                // Filter MVC controller action execution logs for health endpoint
                if (sourceContext != null &&
                    (sourceContext.Contains( "Microsoft.AspNetCore.Mvc" ) ||
                     sourceContext.Contains( "Microsoft.AspNetCore.Routing" ))) {

                    if (logEvent.Properties.TryGetValue( "ActionName", out Serilog.Events.LogEventPropertyValue? actionValue )) {
                        string actionName = actionValue.ToString();
                        if (actionName.Contains( "Health", StringComparison.OrdinalIgnoreCase )) {
                            return true; // Exclude health check related MVC logs
                        }
                    }

                    string messageText = logEvent.RenderMessage();
                    if (messageText.Contains( "Health", StringComparison.OrdinalIgnoreCase )) {
                        return true; // Exclude health check related MVC logs
                    }
                }

                // Filter HTTP request completion logs for successful health checks
                if (logEvent.Properties.TryGetValue( "RequestPath", out Serilog.Events.LogEventPropertyValue? pathValue ) &&
                    (pathValue.ToString( ).Trim( '"' ).Equals( EndpointPaths.Health, StringComparison.OrdinalIgnoreCase ) ||
                     pathValue.ToString( ).Trim( '"' ).Equals( EndpointPaths.Alive, StringComparison.OrdinalIgnoreCase )) &&
                    logEvent.Properties.TryGetValue( "StatusCode", out Serilog.Events.LogEventPropertyValue? statusValue ) &&
                    statusValue.ToString( ) == "200") {
                    return true; // Exclude successful health check HTTP logs
                }

                return false;
            } )
            .WriteTo.Console( )
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 50 * 1024 * 1024, // 50MB
                retainedFileCountLimit: 5, // 5 days retention
                rollOnFileSizeLimit: true,
                shared: false
            ),
            preserveStaticLogger: false,
            writeToProviders: true );

        return builder;
    }

    /// <summary>
    /// Configures Serilog console and rolling-file logging for a generic host (worker services). Log
    /// files roll daily and at 50&#160;MB, keeping the five most recent. Unlike the
    /// <see cref="ConfigureFileLogging(WebApplicationBuilder, string)"/> overload, no health-check noise
    /// filter is applied, since worker hosts do not serve the health endpoints. If the log directory
    /// cannot be created, a warning is written to the console and logging is left unconfigured.
    /// </summary>
    /// <param name="builder">The generic host builder to configure.</param>
    /// <param name="projectName">The project name used as the log-file name prefix.</param>
    /// <returns>The same <paramref name="builder"/>, to allow call chaining.</returns>
    public static IHostApplicationBuilder ConfigureFileLogging( this IHostApplicationBuilder builder, string projectName ) {
        // Use LogDirPath from configuration or default to ./logs
        string logDir = builder.Configuration["BridgeBeats:LogDirPath"] ?? "./logs";
        string logPath = Path.Combine(logDir, $"{projectName}-.log");

        try {
            if (!string.IsNullOrEmpty( logDir ) && !Directory.Exists( logDir )) {
                _ = Directory.CreateDirectory( logDir );
            }
        } catch (IOException ex) {
            Console.WriteLine( $"Warning: Failed to validate/create log directory: {ex.Message}" );
            // Fall back to not configuring file logging
            return builder;
        } catch (UnauthorizedAccessException ex) {
            Console.WriteLine( $"Warning: Failed to validate/create log directory due to insufficient permissions: {ex.Message}" );
            // Fall back to not configuring file logging
            return builder;
        }

        _ = builder.Services.AddSerilog( ( services, loggerConfiguration ) => loggerConfiguration
            .ReadFrom.Configuration( builder.Configuration )
            .ReadFrom.Services( services )
            .WriteTo.Console( )
            .WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 50 * 1024 * 1024, // 50MB
                retainedFileCountLimit: 5, // 5 days retention
                rollOnFileSizeLimit: true,
                shared: false
            ),
            preserveStaticLogger: false,
            writeToProviders: true );

        return builder;
    }
}
