using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Web.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Web.Services;

/// <summary>
/// Reads lookup statistics and Maintenance worker status from Redis, and publishes manual
/// refresh requests to the Maintenance worker via Pub/Sub. No statistics computation is
/// performed in this class; all aggregation is done by the worker.
/// </summary>
/// <remarks>
/// <c>GetCachedStatistics</c> and <see cref="IsRefreshing"/> are backed by the
/// <c>status:statistics</c> Redis document written by the Maintenance worker. Reads are
/// served from an in-process memo refreshed at most once per <see cref="s_memoWindow"/> to avoid
/// Redis round-trips on page bursts. <c>GetLiveBootstrapStatusAsync</c> reads
/// <c>status:cache-bootstrap</c> directly on every call (its own short-lived nature is managed
/// by the caller). <see cref="RequestRefresh"/> publishes to
/// <see cref="RedisChannels.StatisticsRefreshRequested"/>; throttling is the controller's
/// responsibility.
/// </remarks>
/// <param name="redis">The Redis connection.</param>
/// <param name="timeProvider">
/// The time provider used for memo expiry. Inject <see cref="TimeProvider.System"/> in
/// production and a <c>FakeTimeProvider</c> in tests.
/// </param>
/// <param name="logger">Logger for Redis read failures.</param>
public sealed partial class RedisStatisticsReader(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    ILogger<RedisStatisticsReader> logger
) : IStatisticsService {

    /// <summary>Case-insensitive JSON options used for all Redis document deserializations.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) { PropertyNameCaseInsensitive = true };

    /// <summary>How long a <c>status:statistics</c> read result is served from the in-process memo.</summary>
    private static readonly TimeSpan s_memoWindow = TimeSpan.FromSeconds( 5 );

    /// <summary>Guards memo state against concurrent reads.</summary>
    private readonly Lock _memoLock = new( );

    /// <summary>The last deserialized <c>status:statistics</c> document, or null before the first read.</summary>
    private StatisticsStatus? _memoValue;

    /// <summary>The time after which <see cref="_memoValue"/> is considered stale and Redis must be re-read.</summary>
    private DateTimeOffset _memoExpiry = DateTimeOffset.MinValue;

    /// <summary>
    /// Returns the full <c>status:statistics</c> document, or <see langword="null"/> before the
    /// first worker run. Served from the in-process memo, falling back to a single synchronous
    /// Redis read that refreshes the memo. No new Redis round-trip beyond the one
    /// <see cref="GetCachedStatistics"/> and <see cref="IsRefreshing"/> already share.
    /// </summary>
    /// <returns>
    /// The current <see cref="StatisticsStatus"/>, or <see langword="null"/> when no status
    /// document has been published yet.
    /// </returns>
    public StatisticsStatus? GetStatus( ) => GetMemoOrNull( ) ?? FetchStatisticsStatusSync( );

    /// <summary>
    /// Returns the most recently computed statistics snapshot from the worker, or
    /// <see langword="null"/> before the first worker run. Served from an in-process memo;
    /// calls Redis at most once per <see cref="s_memoWindow"/>. A projection over
    /// <see cref="GetStatus"/> (its snapshot field).
    /// </summary>
    /// <returns>
    /// The <see cref="LookupStatistics"/> from the worker snapshot, or <see langword="null"/>
    /// when no snapshot exists.
    /// </returns>
    public LookupStatistics? GetCachedStatistics( ) {
        StatisticsStatus? status = GetMemoOrNull( );
        if (status is not null) {
            return status.Snapshot;
        }

        // Synchronously fetch and update the memo so the caller gets a value on first call.
        status = FetchStatisticsStatusSync( );
        return status?.Snapshot;
    }

    /// <summary>
    /// <see langword="true"/> when the Maintenance worker is currently running a statistics
    /// computation, as reported by the <c>IsRunning</c> field of <c>status:statistics</c>.
    /// Served from the in-process memo. A projection over <see cref="GetStatus"/> (its running flag).
    /// </summary>
    public bool IsRefreshing {
        get {
            StatisticsStatus? status = GetMemoOrNull( );
            if (status is not null) {
                return status.IsRunning;
            }

            status = FetchStatisticsStatusSync( );
            return status?.IsRunning ?? false;
        }
    }

    /// <summary>
    /// Publishes a manual refresh request to the Maintenance worker via
    /// <see cref="RedisChannels.StatisticsRefreshRequested"/>. Returns immediately.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the publish call completed without error;
    /// <see langword="false"/> on a Redis failure.
    /// </returns>
    public bool RequestRefresh( ) {
        try {
            ISubscriber subscriber = redis.GetSubscriber( );
            _ = subscriber.Publish(
                RedisChannel.Literal( RedisChannels.StatisticsRefreshRequested ),
                ""
            );
            return true;
        } catch (Exception ex) {
            LogPublishError( logger, ex );
            return false;
        }
    }

    /// <summary>
    /// Reads the current cache-bootstrap worker status directly from Redis, bypassing the memo.
    /// Returns <see langword="null"/> when the key is absent or on a Redis error.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>
    /// The deserialized <see cref="CacheBootstrapStatus"/>, or <see langword="null"/> when
    /// absent or unreadable.
    /// </returns>
    public async Task<CacheBootstrapStatus?> GetLiveBootstrapStatusAsync( CancellationToken cancellationToken = default ) {
        try {
            IDatabase db = redis.GetDatabase( );
            RedisValue value = await db.StringGetAsync( CacheBootstrapStatus.RedisKey )
                .WaitAsync( cancellationToken );
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<CacheBootstrapStatus>( value.ToString( ), s_jsonOptions );
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogBootstrapStatusReadError( logger, ex );
            return null;
        }
    }

    /// <summary>
    /// Returns the memo value when it is still within the <see cref="s_memoWindow"/>, or
    /// <see langword="null"/> when the memo is stale or empty.
    /// </summary>
    private StatisticsStatus? GetMemoOrNull( ) {
        lock (_memoLock) {
            return _memoValue is not null && timeProvider.GetUtcNow( ) < _memoExpiry
                ? _memoValue
                : null;
        }
    }

    /// <summary>
    /// Synchronously fetches the <c>status:statistics</c> document from Redis, updates the
    /// memo, and returns the result. Swallows errors and returns <see langword="null"/> on
    /// failure so a Redis hiccup does not surface to the caller.
    /// </summary>
    private StatisticsStatus? FetchStatisticsStatusSync( ) {
        try {
            IDatabase db = redis.GetDatabase( );
            RedisValue value = db.StringGet( StatisticsStatus.RedisKey );
            StatisticsStatus? status = value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<StatisticsStatus>( value.ToString( ), s_jsonOptions );
            lock (_memoLock) {
                _memoValue = status;
                _memoExpiry = timeProvider.GetUtcNow( ) + s_memoWindow;
            }
            return status;
        } catch (Exception ex) {
            LogStatusReadError( logger, ex );
            return null;
        }
    }

    #region LoggerMessage Definitions

    /// <summary>Logs a warning when the <c>status:statistics</c> key cannot be read from Redis.</summary>
    [LoggerMessage(
        EventId = LogEventIds.Services.RedisStatisticsReaderStatusReadError,
        Level = LogLevel.Warning,
        Message = "Failed to read statistics status from Redis" )]
    private static partial void LogStatusReadError( ILogger logger, Exception ex );

    /// <summary>Logs a warning when the <c>status:cache-bootstrap</c> key cannot be read from Redis.</summary>
    [LoggerMessage(
        EventId = LogEventIds.Services.RedisStatisticsReaderBootstrapStatusReadError,
        Level = LogLevel.Warning,
        Message = "Failed to read cache bootstrap status from Redis" )]
    private static partial void LogBootstrapStatusReadError( ILogger logger, Exception ex );

    /// <summary>Logs a warning when the Pub/Sub publish fails.</summary>
    [LoggerMessage(
        EventId = LogEventIds.Services.RedisStatisticsReaderPublishError,
        Level = LogLevel.Warning,
        Message = "Failed to publish statistics refresh request" )]
    private static partial void LogPublishError( ILogger logger, Exception ex );

    #endregion LoggerMessage Definitions
}
