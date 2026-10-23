using Testcontainers.Redis;

namespace BridgeBeats.Tests;

/// <summary>
/// Assembly-wide test fixture that starts a single Redis container (via Testcontainers) once per test
/// run and shares its connection string across every integration test. Initialization is lazy and
/// thread-safe; when Docker is unavailable the container is skipped and Redis-dependent tests are made
/// inconclusive rather than failed.
/// </summary>
[TestClass]
public static class SharedTestInfrastructure {
    /// <summary>The shared Redis Testcontainer, or <c>null</c> when Docker is unavailable.</summary>
    private static RedisContainer? s_redisContainer;
    /// <summary>Guards lazy, thread-safe initialization of the shared container.</summary>
    private static readonly Lock s_lock = new( );
    /// <summary>Tracks whether initialization has already run.</summary>
    private static bool s_initialized;
    /// <summary>Holds the Docker error message when container startup fails, used in skip messages.</summary>
    private static string? s_dockerError;

    /// <summary>
    /// The connection string for the shared Redis container, or an empty string when Docker is
    /// unavailable.
    /// </summary>
    public static string RedisConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Indicates whether the shared Redis container started successfully and is available for tests.
    /// </summary>
    public static bool IsRedisAvailable { get; private set; }

    /// <summary>
    /// Lazily starts the shared Redis container exactly once, recording the connection string on
    /// success or the Docker error on failure. Safe to call from multiple test classes concurrently.
    /// </summary>
    private static void EnsureInitialized( ) {
        if (s_initialized) {
            return;
        }

        lock (s_lock) {
            if (s_initialized) {
                return;
            }

            try {
                s_redisContainer = new RedisBuilder( "redis:8.10.2-alpine@sha256:0bf6577d7774e592b29d093e3ac9390eb91e13d72c21e506afce736de5e0eeb0" )
                    .Build( );

                // Start synchronously to ensure container is ready
                s_redisContainer.StartAsync( ).GetAwaiter( ).GetResult( );
                RedisConnectionString = s_redisContainer.GetConnectionString( );
                IsRedisAvailable = true;
            } catch (DotNet.Testcontainers.Builders.DockerUnavailableException ex) {
                IsRedisAvailable = false;
                RedisConnectionString = string.Empty;
                s_dockerError = ex.Message;
            }

            s_initialized = true;
        }
    }

    /// <summary>
    /// MSTest assembly-initialize hook that starts the shared Redis container before any tests run.
    /// </summary>
    /// <param name="_">The MSTest assembly-level test context (unused).</param>
    [AssemblyInitialize]
    public static void AssemblyInitialize( TestContext _ ) {
        // Trigger initialization early during assembly setup
        EnsureInitialized( );
    }

    /// <summary>
    /// MSTest assembly-cleanup hook that stops and disposes the shared Redis container after the run.
    /// </summary>
    [AssemblyCleanup]
    public static async Task AssemblyCleanup( ) {
        if (s_redisContainer is not null) {
            await s_redisContainer.StopAsync( CancellationToken.None );
            await s_redisContainer.DisposeAsync( );
        }
    }

    /// <summary>
    /// Ensures the shared Redis container is running and marks the calling test inconclusive (with
    /// guidance to start Docker Desktop) when it is not. Call from any test that depends on Redis.
    /// </summary>
    public static void RequireRedis( ) {
        EnsureInitialized( );

        if (!IsRedisAvailable) {
            Assert.Inconclusive(
                $"Docker is not available. These tests require Docker Desktop to be running. " +
                $"Please start Docker Desktop and re-run the tests. Error: {s_dockerError}"
            );
        }
    }
}
