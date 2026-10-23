using BridgeBeats.Core.Domain.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Regression coverage for forwarding Serilog events to registered logging providers.
/// </summary>
/// <remarks>
/// [DoNotParallelize]: <c>ConfigureFileLogging</c> configures Serilog with
/// <c>preserveStaticLogger: false</c>; the process-global <see cref="Serilog.Log.Logger"/> is
/// reassigned when this test calls <c>Build()</c>. Under class-level parallelization this class
/// would mutate that shared static concurrently with other test classes.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed partial class SerilogOpenTelemetryForwardingTests {
    /// <summary>
    /// A structured event written through ILogger reaches another provider, which is
    /// the mechanism used by the OpenTelemetry logging provider.
    /// </summary>
    [TestMethod]
    public void ConfigureFileLogging_ForwardsStructuredEventsToProviders( ) {
        string logDirectory = Path.Combine( Path.GetTempPath( ), $"bridgebeats-otel-{Guid.NewGuid( )}" );
        try {
            WebApplicationBuilder builder = WebApplication.CreateBuilder( new WebApplicationOptions {
                EnvironmentName = "Development",
                ApplicationName = typeof(SerilogOpenTelemetryForwardingTests).Assembly.FullName
            } );
            builder.Configuration["BridgeBeats:LogDirPath"] = logDirectory;

            CaptureLoggerProvider capture = new( );
            _ = builder.Logging.AddProvider( capture );
            _ = builder.ConfigureFileLogging( "TelemetryTest" );

            using WebApplication app = builder.Build( );
            LogTelemetryProbe( app.Logger, "track-123" );

            CapturedLog entry = capture.Entries.Single( item => item.EventId.Id == 42 );
            Assert.AreEqual( "track-123", entry.Properties["TrackId"] );
            Assert.AreEqual( "Probe {TrackId}", entry.Properties["{OriginalFormat}"] );
        } finally {
            TryDeleteLogDirectory( logDirectory );
        }
    }

    [LoggerMessage( EventId = 42, Level = LogLevel.Information, Message = "Probe {TrackId}" )]
    private static partial void LogTelemetryProbe( ILogger logger, string trackId );

    /// <summary>
    /// Mirrors <see cref="ConfigureFileLogging_ForwardsStructuredEventsToProviders"/> for the
    /// <see cref="IHostApplicationBuilder"/>/<c>AddSerilog</c> overload of
    /// <see cref="AspireServiceExtensions.ConfigureFileLogging(IHostApplicationBuilder, string)"/> —
    /// the generic-host path used by workers such as SagaCoordinator, as opposed to the
    /// <see cref="WebApplicationBuilder"/>/<c>UseSerilog</c> overload covered above. A structured
    /// event written through the host's <see cref="ILogger"/> after <c>Build()</c> must still reach
    /// a registered logging provider.
    /// </summary>
    [TestMethod]
    public void ConfigureFileLogging_GenericHost_ForwardsStructuredEventsToProviders( ) {
        string logDirectory = Path.Combine( Path.GetTempPath( ), $"bridgebeats-otel-host-{Guid.NewGuid( )}" );
        try {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder( new HostApplicationBuilderSettings {
                EnvironmentName = "Development",
                ApplicationName = typeof(SerilogOpenTelemetryForwardingTests).Assembly.FullName
            } );
            builder.Configuration["BridgeBeats:LogDirPath"] = logDirectory;

            CaptureLoggerProvider capture = new( );
            _ = builder.Logging.AddProvider( capture );
            _ = builder.ConfigureFileLogging( "TelemetryHostTest" );

            using IHost app = builder.Build( );
            ILogger logger = app.Services.GetRequiredService<ILoggerFactory>( ).CreateLogger( "GenericHostProbe" );
            LogGenericHostTelemetryProbe( logger, "track-456" );

            CapturedLog entry = capture.Entries.Single( item => item.EventId.Id == 4300 );
            Assert.AreEqual( "track-456", entry.Properties["TrackId"] );
            Assert.AreEqual( "Generic host probe {TrackId}", entry.Properties["{OriginalFormat}"] );
        } finally {
            TryDeleteLogDirectory( logDirectory );
        }
    }

    [LoggerMessage( EventId = 4300, Level = LogLevel.Information, Message = "Generic host probe {TrackId}" )]
    private static partial void LogGenericHostTelemetryProbe( ILogger logger, string trackId );

    /// <summary>
    /// Deletes the test's temporary log directory on a best-effort basis. A Serilog file-sink handle
    /// held transiently at test teardown can make <see cref="Directory.Delete(string, bool)"/> throw
    /// even though the test's assertions have already completed; that cleanup failure must not fail
    /// an otherwise-passing test.
    /// </summary>
    private static void TryDeleteLogDirectory( string logDirectory ) {
        if (!Directory.Exists( logDirectory )) {
            return;
        }

        try {
            Directory.Delete( logDirectory, recursive: true );
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Best-effort cleanup only; swallow transient file-lock failures.
        }
    }

    private sealed record CapturedLog( EventId EventId, IReadOnlyDictionary<string, object?> Properties );

    private sealed class CaptureLoggerProvider : ILoggerProvider {
        public List<CapturedLog> Entries { get; } = [];

        public ILogger CreateLogger( string categoryName ) => new CaptureLogger( Entries );

        public void Dispose( ) { }
    }

    private sealed class CaptureLogger( List<CapturedLog> entries ) : ILogger {
        public IDisposable? BeginScope<TState>( TState state ) where TState : notnull => null;

        public bool IsEnabled( LogLevel logLevel ) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) {
            Dictionary<string, object?> properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary( pair => pair.Key, pair => pair.Value )
                : [];
            entries.Add( new CapturedLog( eventId, properties ) );
        }
    }
}
