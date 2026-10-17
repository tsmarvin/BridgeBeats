using System.Diagnostics;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Cache;
using BridgeBeats.Core.Infrastructure.Queue;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Worker.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Manual production-scale test that downloads a real PDS repository CAR, rebuilds the complete
/// media-link cache in an isolated Redis Testcontainer, and measures rate-limit admission latency
/// against the resulting keyspace. It never connects to or mutates production Redis.
/// </summary>
/// <remarks>
/// The explicit run switch prevents an accidentally configured developer environment from starting
/// a large bootstrap during the normal test suite. See <c>docs/LOCAL_DEVELOPMENT.md</c> for the
/// required variables and command.
/// </remarks>
[TestClass]
[TestCategory( "Manual" )]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
[DoNotParallelize]
public sealed class LivePdsCacheBootstrapScaleTests {
    private const string RunEnvVar = "BRIDGEBEATS_RUN_MANUAL_CACHE_BOOTSTRAP";
    private const string PdsUriEnvVar = "BRIDGEBEATS_TEST_PDS_URI";
    private const string UserDidEnvVar = "BRIDGEBEATS_TEST_USER_DID";
    private const int Samples = 2000;
    private const int ConcurrentSamples = 2048;
    private const int Concurrency = 32;
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>The MSTest context used for cancellation and benchmark output.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Populates isolated Redis through the real bootstrap path, verifies the record/key counts, and
    /// reports sequential cold, cached, and concurrent cold admission latency percentiles.
    /// </summary>
    [TestMethod]
    [Timeout( 3600000, CooperativeCancellation = true )]
    public async Task BootstrapRealPds_ThenMeasureRateLimitAdmissionAtPopulatedScale( ) {
        if (!string.Equals(
                Environment.GetEnvironmentVariable( RunEnvVar ),
                "1",
                StringComparison.Ordinal )) {
            Assert.Inconclusive( $"Manual scale test disabled; set {RunEnvVar}=1 to run it." );
            return;
        }

        string? pdsUriValue = Environment.GetEnvironmentVariable( PdsUriEnvVar );
        string? userDid = Environment.GetEnvironmentVariable( UserDidEnvVar );
        if (!Uri.TryCreate( pdsUriValue, UriKind.Absolute, out Uri? pdsUri )
            || string.IsNullOrWhiteSpace( userDid )) {
            Assert.Inconclusive(
                $"Set valid {PdsUriEnvVar} and {UserDidEnvVar} values before running the manual scale test." );
            return;
        }

        SharedTestInfrastructure.RequireRedis( );
        ConfigurationOptions redisOptions = ConfigurationOptions.Parse(
            SharedTestInfrastructure.RedisConnectionString );
        redisOptions.AllowAdmin = true;
        using ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync( redisOptions );
        IServer server = redis.GetServer( redis.GetEndPoints( )[0] );
        await server.FlushDatabaseAsync( );

        using HttpClient carClient = new( ) { Timeout = TimeSpan.FromMinutes( 10 ) };
        Mock<IHttpClientFactory> httpClientFactory = new( );
        _ = httpClientFactory.Setup( factory => factory.CreateClient(
                ATProtoStorageService.ATProtoSyncHttpClientName ) )
            .Returns( carClient );
        ATProtoStorageService storage = new(
            Mock.Of<IATProtoSessionManager>( ),
            NullLogger<ATProtoStorageService>.Instance,
            httpClientFactory.Object,
            carCacheTtl: TimeSpan.Zero );
        RedisMediaLinkCache cache = new(
            redis,
            storage,
            NullLogger<RedisMediaLinkCache>.Instance,
            cacheDays: 30,
            userDid );
        CacheBootstrapBackgroundService bootstrap = new(
            storage,
            cache,
            redis,
            new CacheBootstrapSettings(
                pdsUri,
                userDid,
                BootstrapInterval: TimeSpan.FromDays( 1 ),
                CacheDays: 30,
                RefreshInterval: TimeSpan.FromDays( 1 ),
                MaxRecordsPerRun: 1,
                RefreshRetryInterval: TimeSpan.FromMinutes( 1 ) ),
            new StatisticsRefreshTrigger( ),
            NullLogger<CacheBootstrapBackgroundService>.Instance );

        Stopwatch bootstrapTimer = Stopwatch.StartNew( );
        await bootstrap.RunBootstrapOnceAsync( TestContext.CancellationToken );
        bootstrapTimer.Stop( );

        IDatabase database = redis.GetDatabase( );
        CacheBootstrapStatus? bootstrapStatus = Deserialize<CacheBootstrapStatus>(
            await database.StringGetAsync( CacheBootstrapStatus.RedisKey ) );
        StatisticsStatus? statisticsStatus = Deserialize<StatisticsStatus>(
            await database.StringGetAsync( StatisticsStatus.RedisKey ) );
        Assert.IsNotNull( bootstrapStatus );
        Assert.IsFalse( bootstrapStatus.IsRunning );
        Assert.IsNotNull( bootstrapStatus.LastSuccessCount,
            "Bootstrap did not complete; inspect CAR/bootstrap logs for the fatal error." );
        Assert.IsGreaterThan( 0, bootstrapStatus.LastSuccessCount.Value );
        Assert.IsNotNull( statisticsStatus?.Snapshot );

        long redisKeyCount = server.DatabaseSize( );
        LookupStatistics snapshot = statisticsStatus.Snapshot;
        TestContext.WriteLine(
            $"bootstrap: elapsed={bootstrapTimer.Elapsed} records={snapshot.TotalRecords:N0} " +
            $"albums={snapshot.AlbumCount:N0} tracks={snapshot.TrackCount:N0} " +
            $"indexed={bootstrapStatus.LastSuccessCount:N0} errors={bootstrapStatus.LastErrorCount:N0} " +
            $"redisKeys={redisKeyCount:N0}" );

        LatencySummary emptyIndexCold = await MeasureForcedColdAsync(
            redis, Samples, maxConcurrency: 1, TestContext.CancellationToken );

        RedisRateLimitTracker seedTracker = new(
            redis, Options.Create( new QueueSettings( ) ), NullLogger<RedisRateLimitTracker>.Instance );
        await seedTracker.SetRateLimitedAsync(
            SupportedProviders.Spotify,
            ProviderEndpointConstants.ProviderWide,
            DateTimeOffset.UtcNow.AddMinutes( 10 ),
            TestContext.CancellationToken );

        LatencySummary activeIndexCold = await MeasureForcedColdAsync(
            redis, Samples, maxConcurrency: 1, TestContext.CancellationToken );
        LatencySummary activeIndexConcurrent = await MeasureForcedColdAsync(
            redis, ConcurrentSamples, Concurrency, TestContext.CancellationToken );
        LatencySummary cacheHit = await MeasureCacheHitsAsync(
            redis, Samples, TestContext.CancellationToken );

        WriteSummary( "populated/empty-index/cold", emptyIndexCold );
        WriteSummary( "populated/active-index/cold", activeIndexCold );
        WriteSummary( $"populated/active-index/cold/concurrency-{Concurrency}", activeIndexConcurrent );
        WriteSummary( "populated/active-index/cache-hit", cacheHit );
    }

    private static T? Deserialize<T>( RedisValue value ) where T : class =>
        value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<T>( value.ToString( ), s_jsonOptions );

    private static async Task<LatencySummary> MeasureForcedColdAsync(
        IConnectionMultiplexer redis,
        int samples,
        int maxConcurrency,
        CancellationToken cancellationToken
    ) {
        double[] microseconds = new double[samples];
        await Parallel.ForEachAsync(
            Enumerable.Range( 0, samples ),
            new ParallelOptions {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async ( index, ct ) => {
                RedisRateLimitTracker tracker = new(
                    redis, Options.Create( new QueueSettings( ) ), NullLogger<RedisRateLimitTracker>.Instance );
                long started = Stopwatch.GetTimestamp( );
                _ = await tracker.GetAllRateLimitedAsync( SupportedProviders.Spotify, ct );
                microseconds[index] = Stopwatch.GetElapsedTime( started ).TotalMicroseconds;
            } );
        return LatencySummary.From( microseconds );
    }

    private static async Task<LatencySummary> MeasureCacheHitsAsync(
        IConnectionMultiplexer redis,
        int samples,
        CancellationToken cancellationToken
    ) {
        RedisRateLimitTracker tracker = new(
            redis, Options.Create( new QueueSettings( ) ), NullLogger<RedisRateLimitTracker>.Instance );
        _ = await tracker.GetAllRateLimitedAsync(
            SupportedProviders.Spotify, cancellationToken );
        double[] microseconds = new double[samples];
        for (int i = 0; i < samples; i++) {
            long started = Stopwatch.GetTimestamp( );
            _ = await tracker.GetAllRateLimitedAsync(
                SupportedProviders.Spotify, cancellationToken );
            microseconds[i] = Stopwatch.GetElapsedTime( started ).TotalMicroseconds;
        }
        return LatencySummary.From( microseconds );
    }

    private void WriteSummary( string scenario, LatencySummary summary ) =>
        TestContext.WriteLine(
            $"{scenario}: samples={summary.Samples:N0} mean={summary.MeanMicroseconds:F2}us " +
            $"p50={summary.P50Microseconds:F2}us p95={summary.P95Microseconds:F2}us " +
            $"p99={summary.P99Microseconds:F2}us max={summary.MaxMicroseconds:F2}us" );

    private sealed record LatencySummary(
        int Samples,
        double MeanMicroseconds,
        double P50Microseconds,
        double P95Microseconds,
        double P99Microseconds,
        double MaxMicroseconds
    ) {
        internal static LatencySummary From( double[] values ) {
            Array.Sort( values );
            return new LatencySummary(
                values.Length,
                values.Average( ),
                Percentile( values, 0.50 ),
                Percentile( values, 0.95 ),
                Percentile( values, 0.99 ),
                values[^1] );
        }

        private static double Percentile( double[] sorted, double percentile ) =>
            sorted[Math.Min( sorted.Length - 1, (int)Math.Ceiling( sorted.Length * percentile ) - 1 )];
    }
}
