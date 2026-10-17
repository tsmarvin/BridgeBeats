using System.Diagnostics;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Services;
using BridgeBeats.Worker.Maintenance.Interfaces;
using BridgeBeats.Worker.Maintenance.Logging;
using StackExchange.Redis;

namespace BridgeBeats.Worker.Maintenance;

/// <summary>
/// Rebuilds the Redis lookup index from the ATProto PDS, which is the durable source of truth. Redis
/// is treated as a disposable cache: after a flush or restart it may be empty, and this service
/// re-hydrates it. It also computes the <see cref="StatisticsStatus"/> snapshot as a second fold on
/// the same record enumeration, so one CAR download feeds both the lookup-cache rebuild and statistics.
/// It runs once at startup and then on a <see cref="System.Threading.PeriodicTimer"/>
/// driven by <see cref="CacheBootstrapSettings.BootstrapInterval"/>.
/// </summary>
/// <remarks>
/// Each run streams every <see cref="Contracts.DTOs.MediaLinkResult"/> record from the user's PDS via
/// <see cref="Contracts.Interfaces.IATProtoStorageService.ListAllRecordsAsync(System.Uri, string, System.Threading.CancellationToken, bool)"/>
/// and re-registers each record's input-link to record-URI pointers through
/// <see cref="Contracts.Interfaces.IMediaLinkCacheRepository.AddInputLinksAsync(string, Contracts.DTOs.MediaLinkResult)"/>.
/// Before-and-after Redis key counts are recorded, and a
/// <see cref="Contracts.DTOs.CacheBootstrapStatus"/> document is written to Redis (under
/// <see cref="Contracts.DTOs.CacheBootstrapStatus.RedisKey"/>, with a one-day TTL) so the Web layer
/// can surface bootstrap progress. A single record that fails to cache is logged and skipped; the run
/// continues. Statistics are accumulated for every record regardless of whether the cache write
/// succeeded, and published to <see cref="StatisticsStatus.RedisKey"/> at the end of each pass.
/// </remarks>
/// <param name="atProtoStorage">Streams the user's records from the ATProto PDS.</param>
/// <param name="cacheRepository">Re-registers each record's input-link pointers into Redis.</param>
/// <param name="redis">The Redis connection used to read key counts and publish status.</param>
/// <param name="settings">The PDS, user DID, and run interval for the rebuild pass.</param>
/// <param name="refreshTrigger">Coalescing guard shared with the subscriber service.</param>
/// <param name="logger">The logger for this service.</param>
/// <param name="statisticsRetryInterval">
/// How long to wait before retrying a failed statistics pass. Defaults to 30 seconds when
/// <see cref="TimeSpan.Zero"/> is supplied (the DI default for an unregistered <see cref="TimeSpan"/>).
/// </param>
public sealed partial class CacheBootstrapBackgroundService(
    IATProtoStorageService atProtoStorage,
    IMediaLinkCacheRepository cacheRepository,
    IConnectionMultiplexer redis,
    CacheBootstrapSettings settings,
    IStatisticsRefreshTrigger refreshTrigger,
    ILogger<CacheBootstrapBackgroundService> logger,
    TimeSpan statisticsRetryInterval = default
) : BackgroundService {

    private static readonly TimeSpan s_defaultStatisticsRetryInterval = TimeSpan.FromSeconds( 30 );

    /// <summary>The effective statistics retry interval: supplied value when non-zero, otherwise 30 s.</summary>
    private TimeSpan StatisticsRetryInterval =>
        statisticsRetryInterval > TimeSpan.Zero ? statisticsRetryInterval : s_defaultStatisticsRetryInterval;

    /// <summary>Serializer options used when writing the bootstrap status document to Redis.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) { WriteIndented = false };

    /// <summary>Serializer options used when reading the bootstrap status document back from Redis.</summary>
    private static readonly JsonSerializerOptions s_jsonReadOptions = new( ) { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Runs an immediate bootstrap pass, then repeats on the configured interval until the host stops.
    /// A failure in a periodic pass is logged and does not stop the loop; cancellation ends it cleanly.
    /// The first, startup pass is wrapped in the same <see cref="OperationCanceledException"/> guard
    /// as the periodic loop below; that is the guard's real contribution — without it, cancelling
    /// during this first, otherwise-unguarded call would let the exception escape <c>ExecuteAsync</c>
    /// entirely, leaving the host's underlying task in the <c>Canceled</c> state instead of
    /// <c>RanToCompletion</c> on an ordinary shutdown. The startup pass's <c>catch (Exception)</c>
    /// branch is defense-in-depth rather than the primary rationale: <see cref="RunBootstrapAsync"/>'s
    /// own internal handlers already absorb transient dependency failures (e.g. the PDS not yet
    /// reachable right after a single-container host reboot) and record them in the written status
    /// document. A Redis-unreachable failure is absorbed the same way but is not recorded in that
    /// document — the status write is itself what fails in that case, so it is logged only (see
    /// <see cref="UpdateStatusAsync"/>). Either way, little besides a genuinely unanticipated failure
    /// is expected to reach this catch — it is kept so such a failure is logged rather than
    /// preventing the periodic loop below from starting.
    /// </summary>
    /// <param name="stoppingToken">Signals when the host is shutting down.</param>
    /// <returns>A task that completes when the service stops.</returns>
    protected override async Task ExecuteAsync( CancellationToken stoppingToken ) {
        // Run bootstrap immediately on startup, under the loop's own cancellation/exception handling
        // (see the periodic loop below) rather than unguarded — see the remarks above.
        try {
            await RunBootstrapAsync( stoppingToken );
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            LogBootstrapShuttingDown( logger );
            return;
        } catch (Exception ex) {
            LogBootstrapStartupError( logger, ex );
        }

        // Then run periodically at the configured interval
        using PeriodicTimer timer = new( settings.BootstrapInterval );

        while (!stoppingToken.IsCancellationRequested) {
            try {
                _ = await timer.WaitForNextTickAsync( stoppingToken );
                await RunBootstrapAsync( stoppingToken );
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // Normal shutdown, exit gracefully
                break;
            } catch (Exception ex) {
                LogBootstrapPeriodicError( logger, ex );
            }
        }

        LogBootstrapShuttingDown( logger );
    }

    /// <summary>
    /// Triggers an out-of-band statistics-refresh pass (driven by a manual Pub/Sub request). Uses the
    /// coalescing guard: if a pass is already running the trigger is dropped and
    /// <see cref="LogEventIds.RefreshCoalesced"/> (5581) is logged.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the pass if it starts.</param>
    /// <returns>A task that completes when the triggered pass finishes (or was coalesced).</returns>
    public async Task TriggerStatisticsRefreshAsync( CancellationToken cancellationToken ) {
        if (!refreshTrigger.TryAcquire( )) {
            LogRefreshCoalesced( logger );
            return;
        }
        try {
            await RunStatisticsPassAsync( forceRefresh: true, cancellationToken );
        } finally {
            refreshTrigger.Release( );
        }
    }

    // -------------------------------------------------------------------------
    // Bootstrap status helpers (bootstrap doc, one-day TTL, existing contract)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes the current bootstrap status to Redis under
    /// <see cref="CacheBootstrapStatus.RedisKey"/> with a one-day TTL so the Web layer can surface it.
    /// Failures are logged and swallowed; status reporting never aborts a run.
    /// </summary>
    /// <param name="status">The status snapshot to publish.</param>
    /// <returns>A task that completes when the status has been written (or the write failed and was logged).</returns>
    private async Task UpdateStatusAsync( CacheBootstrapStatus status ) {
        try {
            IDatabase db = redis.GetDatabase( );
            string json = JsonSerializer.Serialize( status, s_jsonOptions );
            _ = await db.StringSetAsync( CacheBootstrapStatus.RedisKey, json, TimeSpan.FromDays( 1 ) );
        } catch (Exception ex) {
            LogStatusUpdateError( logger, ex );
        }
    }

    /// <summary>
    /// Reads the last-published bootstrap status from Redis so a new run can carry forward the prior
    /// run's counts while marking itself in progress. Failures are logged and treated as no status.
    /// </summary>
    /// <returns>
    /// The previously published <see cref="CacheBootstrapStatus"/>, or <see langword="null"/> if none
    /// exists or the read failed.
    /// </returns>
    private async Task<CacheBootstrapStatus?> GetCacheBootstrapStatusAsync( ) {
        try {
            IDatabase db = redis.GetDatabase( );
            RedisValue value = await db.StringGetAsync( CacheBootstrapStatus.RedisKey );
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<CacheBootstrapStatus>( value.ToString( ), s_jsonReadOptions );
        } catch (Exception ex) {
            LogStatusReadError( logger, ex );
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Statistics status helpers (statistics doc, NO TTL)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes the statistics status document to Redis under <see cref="StatisticsStatus.RedisKey"/>
    /// with <strong>no expiry</strong>. The document is the page's only data source; a TTL would
    /// re-strand the statistics page between worker cycles. Failures are logged and swallowed.
    /// </summary>
    /// <param name="status">The statistics status snapshot to publish.</param>
    private async Task UpdateStatisticsStatusAsync( StatisticsStatus status ) {
        try {
            IDatabase db = redis.GetDatabase( );
            string json = JsonSerializer.Serialize( status, s_jsonOptions );
            _ = await db.StringSetAsync( StatisticsStatus.RedisKey, json );
        } catch (Exception ex) {
            LogStatisticsStatusUpdateError( logger, ex );
        }
    }

    /// <summary>
    /// Reads the last-published statistics status from Redis. Failures are logged and treated as no
    /// status so a Redis hiccup does not affect the lifecycle writes.
    /// </summary>
    private async Task<StatisticsStatus?> GetStatisticsStatusAsync( ) {
        try {
            IDatabase db = redis.GetDatabase( );
            RedisValue value = await db.StringGetAsync( StatisticsStatus.RedisKey );
            return value.IsNullOrEmpty
                ? null
                : JsonSerializer.Deserialize<StatisticsStatus>( value.ToString( ), s_jsonReadOptions );
        } catch (Exception ex) {
            LogStatisticsStatusReadError( logger, ex );
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Statistics status write helpers — single source for each lifecycle shape
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reads the prior statistics status and writes an in-progress document that carries forward the
    /// previous snapshot and error fields so a running indicator never clears the last known results.
    /// </summary>
    private async Task WriteRunningStatusAsync( ) {
        StatisticsStatus? prior = await GetStatisticsStatusAsync( );
        await UpdateStatisticsStatusAsync( new StatisticsStatus {
            IsRunning = true,
            Snapshot = prior?.Snapshot,
            LastRunTime = prior?.LastRunTime,
            LastError = prior?.LastError,
            LastErrorTime = prior?.LastErrorTime,
            NextScheduledRun = prior?.NextScheduledRun
        } );
    }

    /// <summary>
    /// Writes a healthy-completion document with the supplied snapshot. Clears any prior error fields.
    /// </summary>
    private async Task WriteHealthyStatusAsync( LookupStatistics snapshot ) {
        await UpdateStatisticsStatusAsync( new StatisticsStatus {
            IsRunning = false,
            Snapshot = snapshot,
            LastRunTime = DateTimeOffset.UtcNow,
            NextScheduledRun = DateTimeOffset.UtcNow.Add( settings.BootstrapInterval ),
            LastError = null,
            LastErrorTime = null
        } );
    }

    /// <summary>
    /// Reads the prior statistics status and writes an error-completion document that carries forward
    /// the previous snapshot and run-time fields. The <paramref name="sanitizedError"/> must already
    /// be sanitized; no internal exception detail is written here.
    /// </summary>
    private async Task WriteErrorStatusAsync( string sanitizedError ) {
        StatisticsStatus? prior = await GetStatisticsStatusAsync( );
        await UpdateStatisticsStatusAsync( new StatisticsStatus {
            IsRunning = false,
            Snapshot = prior?.Snapshot,
            LastRunTime = prior?.LastRunTime,
            LastError = sanitizedError,
            LastErrorTime = DateTimeOffset.UtcNow,
            NextScheduledRun = DateTimeOffset.UtcNow.Add( settings.BootstrapInterval )
        } );
    }

    // -------------------------------------------------------------------------
    // Bootstrap pass
    // -------------------------------------------------------------------------

    /// <summary>
    /// Performs one full cache-rebuild pass: marks the status in progress, measures the Redis key count,
    /// streams every record from the PDS and re-registers its input-link pointers, then records timing,
    /// success/error counts, and the before/after key counts in the status document. Simultaneously
    /// folds each record into a <see cref="StatisticsAccumulator"/> as a second sink on the same
    /// enumeration, then publishes the statistics snapshot at the end of the pass. A fatal error
    /// aborts the pass and is recorded; per-record failures are counted and skipped.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the pass.</param>
    /// <returns>A task that completes when the pass finishes or aborts.</returns>
    /// <exception cref="OperationCanceledException">
    /// Propagated when <paramref name="cancellationToken"/> is cancelled mid-pass.
    /// </exception>
    private async Task RunBootstrapAsync( CancellationToken cancellationToken ) {
        // Preserve previously completed-run fields when signalling that a run has started.
        CacheBootstrapStatus? previous = await GetCacheBootstrapStatusAsync( );
        await UpdateStatusAsync( new CacheBootstrapStatus {
            IsRunning = true,
            NextScheduledRun = null,
            LastRunTime = previous?.LastRunTime,
            LastSuccessCount = previous?.LastSuccessCount,
            LastErrorCount = previous?.LastErrorCount,
            LastDurationSeconds = previous?.LastDurationSeconds,
            RedisKeyCount = previous?.RedisKeyCount
        } );

        // Get Redis key count before bootstrap for comparison
        long keyCountBefore = 0;
        try {
            foreach (System.Net.EndPoint endpoint in redis.GetEndPoints( )) {
                IServer server = redis.GetServer( endpoint );
                keyCountBefore = server.DatabaseSize( );
                if (logger.IsEnabled( LogLevel.Information )) {
                    string endpointStr = endpoint.ToString( ) ?? "unknown";
                    LogRedisKeyCountBefore( logger, keyCountBefore, endpointStr );
                }
            }
        } catch (Exception ex) {
            LogRedisKeyCountBeforeError( logger, ex );
        }

        if (logger.IsEnabled( LogLevel.Information )) {
            string pdsUriStr = settings.PdsUri.ToString( );
            string userDidStr = settings.UserDid;
            LogBootstrapStarting( logger, pdsUriStr, userDidStr );
        }

        Stopwatch stopwatch = Stopwatch.StartNew( );
        int successCount = 0;
        int errorCount = 0;
        StatisticsAccumulator accumulator = new( );

        try {
            await foreach ((string atUri, MediaLinkResult result) in
                atProtoStorage.ListAllRecordsAsync( settings.PdsUri, settings.UserDid, cancellationToken )) {
                try {
                    // Populate Redis cache indices for this record
                    await cacheRepository.AddInputLinksAsync( atUri, result );
                    successCount++;

                    if (successCount % 100 == 0) {
                        LogBootstrapProgress( logger, successCount );
                    }
                } catch (Exception ex) {
                    errorCount++;
                    LogCacheRecordError( logger, ex, atUri );
                }

                // Fold this record into statistics regardless of whether the cache write succeeded.
                accumulator.Add( atUri, result );
            }
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            LogBootstrapCancelled( logger );
            throw;
        } catch (Exception ex) {
            LogBootstrapFatalError( logger, ex );
            stopwatch.Stop( );

            // Preserve the last successful run's fields; only update lifecycle fields.
            // Do NOT fall through to the normal completion write — that would record a zero-count
            // run as a completed run and clobber the previous successful run's stats.
            CacheBootstrapStatus? prior = await GetCacheBootstrapStatusAsync( );
            await UpdateStatusAsync( new CacheBootstrapStatus {
                IsRunning = false,
                NextScheduledRun = DateTimeOffset.UtcNow.Add( settings.BootstrapInterval ),
                LastRunTime = prior?.LastRunTime,
                LastSuccessCount = prior?.LastSuccessCount,
                LastErrorCount = prior?.LastErrorCount,
                LastDurationSeconds = prior?.LastDurationSeconds,
                RedisKeyCount = prior?.RedisKeyCount
            } );
            return;
        }

        stopwatch.Stop( );

        // Get Redis key count after bootstrap
        long keyCountAfter = 0;
        try {
            foreach (System.Net.EndPoint endpoint in redis.GetEndPoints( )) {
                IServer server = redis.GetServer( endpoint );
                keyCountAfter = server.DatabaseSize( );
                if (logger.IsEnabled( LogLevel.Information )) {
                    string endpointStr = endpoint.ToString( ) ?? "unknown";
                    LogRedisKeyCountAfter( logger, keyCountAfter, endpointStr );
                }
            }
        } catch (Exception ex) {
            LogRedisKeyCountAfterError( logger, ex );
        }

        LogBootstrapCompleted(
            logger,
            stopwatch.Elapsed.TotalSeconds,
            successCount,
            errorCount,
            keyCountBefore,
            keyCountAfter,
            keyCountAfter - keyCountBefore
        );

        // Update bootstrap status with completed run information
        await UpdateStatusAsync( new CacheBootstrapStatus {
            IsRunning = false,
            LastRunTime = DateTimeOffset.UtcNow,
            NextScheduledRun = DateTimeOffset.UtcNow.Add( settings.BootstrapInterval ),
            LastSuccessCount = successCount,
            LastErrorCount = errorCount,
            LastDurationSeconds = stopwatch.Elapsed.TotalSeconds,
            RedisKeyCount = keyCountAfter
        } );

        // Publish statistics — guarded by the coalescing trigger so a concurrent manual refresh
        // and the scheduled cycle cannot both write status:statistics simultaneously.
        await ComputeAndPublishStatisticsAsync( accumulator, cancellationToken );
    }

    /// <summary>
    /// Runs one complete bootstrap pass without starting the periodic background loop. This entry
    /// point exists for the manual live-PDS scale test, which exercises the production bootstrap
    /// implementation against an isolated Redis instance.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the pass.</param>
    /// <returns>A task that completes when the bootstrap and statistics writes finish.</returns>
    internal Task RunBootstrapOnceAsync( CancellationToken cancellationToken ) =>
        RunBootstrapAsync( cancellationToken );

    // -------------------------------------------------------------------------
    // Statistics pass — retry, coalescing guard, force-refresh variant
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts to publish the statistics snapshot built from <paramref name="accumulator"/>. On the
    /// happy path this is a single write. If the write or the build fails, logs the error, writes an
    /// error status document first, waits <see cref="StatisticsRetryInterval"/>, then retries once by
    /// re-reading the records from the PDS (which will be served from the shared CAR cache within the
    /// 5-minute TTL). A second failure writes the final error document and returns, letting the
    /// periodic timer serve as the backstop.
    /// </summary>
    /// <param name="accumulator">The already-folded accumulator from the bootstrap enumeration.</param>
    /// <param name="cancellationToken">Cancels the wait and the retry.</param>
    private async Task ComputeAndPublishStatisticsAsync(
        StatisticsAccumulator accumulator,
        CancellationToken cancellationToken ) {

        if (!refreshTrigger.TryAcquire( )) {
            LogRefreshCoalesced( logger );
            return;
        }

        try {
            await TryPublishStatisticsFromAccumulatorAsync( accumulator, cancellationToken );
        } finally {
            refreshTrigger.Release( );
        }
    }

    /// <summary>
    /// Writes the statistics snapshot built from <paramref name="accumulator"/>. On failure,
    /// writes an error status doc, waits the retry interval, then retries once via
    /// <see cref="RunStatisticsPassCoreAsync"/>. A second failure writes the final error doc.
    /// </summary>
    private async Task TryPublishStatisticsFromAccumulatorAsync(
        StatisticsAccumulator accumulator,
        CancellationToken cancellationToken ) {

        if (logger.IsEnabled( LogLevel.Information )) {
            LogPassStarting( logger );
        }

        await WriteRunningStatusAsync( );

        try {
            LookupStatistics snapshot = accumulator.Build( );
            await WriteHealthyStatusAsync( snapshot );
            LogPassCompleted( logger, snapshot.TotalRecords );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            await HandleStatisticsPassFailureAndRetryAsync(
                ex,
                isFirstAttempt: true,
                cancellationToken );
        }
    }

    /// <summary>
    /// Writes an error status document, waits the retry interval, then retries via
    /// <see cref="RunStatisticsPassCoreAsync"/>. A second failure writes the final error document
    /// and returns, letting the periodic timer serve as the backstop.
    /// </summary>
    private async Task HandleStatisticsPassFailureAndRetryAsync(
        Exception firstEx,
        bool isFirstAttempt,
        CancellationToken cancellationToken ) {

        string sanitizedError = ClassifyStatisticsError( firstEx );
        LogPassError( logger, firstEx, sanitizedError );

        // Write error status BEFORE the retry wait so the page shows the error immediately.
        await WriteErrorStatusAsync( sanitizedError );

        if (!isFirstAttempt) {
            // Second failure: fall back to the periodic window.
            LogRetryExhausted( logger );
            return;
        }

        // Wait the retry interval, observing the stopping token.
        try {
            await Task.Delay( StatisticsRetryInterval, cancellationToken );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }

        // Retry once via the core body (not RunStatisticsPassAsync, which would re-enter
        // Handle with isFirstAttempt:true and restart the retry cycle unboundedly). Calling
        // the core directly lets a second failure propagate to the catch below, which re-enters
        // Handle with isFirstAttempt:false — the terminal path that logs RetryExhausted.
        try {
            await RunStatisticsPassCoreAsync( forceRefresh: false, cancellationToken );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception retryEx) {
            await HandleStatisticsPassFailureAndRetryAsync(
                retryEx,
                isFirstAttempt: false,
                cancellationToken );
        }
    }

    /// <summary>
    /// Core statistics pass body: marks status running, enumerates all records (cache-served when
    /// within the 5-minute TTL), folds them into a <see cref="StatisticsAccumulator"/>, and
    /// publishes the snapshot. All exceptions propagate to the caller; no self-handling is
    /// performed here. Callers are responsible for error-doc writes and retry orchestration.
    /// </summary>
    /// <param name="forceRefresh">
    /// When <see langword="true"/>, bypasses the CAR cache TTL and forces a fresh download
    /// (used by the manual admin trigger).
    /// </param>
    /// <param name="cancellationToken">Cancels the enumeration and the status write.</param>
    private async Task RunStatisticsPassCoreAsync( bool forceRefresh, CancellationToken cancellationToken ) {
        if (logger.IsEnabled( LogLevel.Information )) {
            LogPassStarting( logger );
        }

        await WriteRunningStatusAsync( );

        StatisticsAccumulator acc = new( );
        await foreach ((string atUri, MediaLinkResult result) in
            atProtoStorage.ListAllRecordsAsync(
                settings.PdsUri, settings.UserDid, cancellationToken, forceRefresh: forceRefresh )) {
            acc.Add( atUri, result );
        }

        LookupStatistics snapshot = acc.Build( );
        await WriteHealthyStatusAsync( snapshot );
        LogPassCompleted( logger, snapshot.TotalRecords );
    }

    /// <summary>
    /// Runs a full statistics-only pass: wraps <see cref="RunStatisticsPassCoreAsync"/> with
    /// error-doc-on-failure semantics so the manual-trigger path never leaves
    /// <c>status:statistics</c> stuck at <c>IsRunning=true</c>. On failure,
    /// <see cref="HandleStatisticsPassFailureAndRetryAsync"/> writes an error status document,
    /// waits the retry interval, retries once via the core, and — on a second consecutive failure
    /// — writes the final error document and returns.
    /// </summary>
    /// <param name="forceRefresh">
    /// When <see langword="true"/>, bypasses the CAR cache TTL and forces a fresh download
    /// (used by the manual admin trigger).
    /// </param>
    /// <param name="cancellationToken">Cancels the enumeration and the status write.</param>
    internal async Task RunStatisticsPassAsync( bool forceRefresh, CancellationToken cancellationToken ) {
        try {
            await RunStatisticsPassCoreAsync( forceRefresh, cancellationToken );
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            // Route failures through the shared error-doc-first + one-retry path so the manual
            // trigger path has the same error-reporting semantics as the scheduled path. Without
            // this catch, a non-OCE exception propagates to the subscriber's Task.Run catch and
            // leaves status:statistics stuck at IsRunning=true.
            await HandleStatisticsPassFailureAndRetryAsync(
                ex,
                isFirstAttempt: true,
                cancellationToken );
        }
    }

    /// <summary>
    /// Returns a short, sanitized description of the error that is safe for operator display.
    /// Never surfaces exception messages or internal details.
    /// </summary>
    private static string ClassifyStatisticsError( Exception ex ) =>
        ex switch {
            OperationCanceledException => "Statistics computation cancelled.",
            _ => "Statistics computation failed; see worker logs."
        };

    #region LoggerMessage Methods

    /// <summary>Logs that a periodic bootstrap run failed and will retry at the next interval.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapPeriodicError,
        Level = LogLevel.Error,
        Message = "Error during periodic cache bootstrap, will retry at next interval" )]
    private static partial void LogBootstrapPeriodicError( ILogger logger, Exception ex );

    /// <summary>Logs that the startup (first, immediate) bootstrap run failed; the periodic loop still starts.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapStartupError,
        Level = LogLevel.Error,
        Message = "Error during startup cache bootstrap; the periodic loop will still start" )]
    private static partial void LogBootstrapStartupError( ILogger logger, Exception ex );

    /// <summary>Logs that the bootstrap service is shutting down.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapShuttingDown,
        Level = LogLevel.Information,
        Message = "Cache bootstrap service is shutting down" )]
    private static partial void LogBootstrapShuttingDown( ILogger logger );

    /// <summary>Logs the Redis key count measured before a bootstrap run.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="keyCount">The number of keys present before the run.</param>
    /// <param name="endpoint">The Redis endpoint that was measured.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisKeyCountBefore,
        Level = LogLevel.Information,
        Message = "Redis key count BEFORE bootstrap: {KeyCount} (endpoint: {Endpoint})" )]
    private static partial void LogRedisKeyCountBefore( ILogger logger, long keyCount, string endpoint );

    /// <summary>Logs that measuring the Redis key count before a run failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisKeyCountBeforeError,
        Level = LogLevel.Warning,
        Message = "Failed to get Redis key count before bootstrap" )]
    private static partial void LogRedisKeyCountBeforeError( ILogger logger, Exception ex );

    /// <summary>Logs that a bootstrap run is starting against the configured PDS and DID.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="pdsUri">The PDS URI being read from.</param>
    /// <param name="userDid">The user DID whose records are rebuilt.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapStarting,
        Level = LogLevel.Information,
        Message = "Starting cache bootstrap from ATProto PDS: {PdsUri}, DID: {UserDid}" )]
    private static partial void LogBootstrapStarting( ILogger logger, string pdsUri, string userDid );

    /// <summary>Logs a progress update reporting how many records have been processed so far.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of records processed.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapProgress,
        Level = LogLevel.Information,
        Message = "Bootstrap progress: {Count} records processed" )]
    private static partial void LogBootstrapProgress( ILogger logger, int count );

    /// <summary>Logs that caching a single PDS record failed; the run continues with the next record.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    /// <param name="atUri">The AT-URI of the record that failed to cache.</param>
    [LoggerMessage(
        EventId = LogEventIds.CacheRecordError,
        Level = LogLevel.Warning,
        Message = "Failed to cache record: {AtUri}" )]
    private static partial void LogCacheRecordError( ILogger logger, Exception ex, string atUri );

    /// <summary>Logs that the bootstrap run was cancelled by host shutdown.</summary>
    /// <param name="logger">The logger to write to.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapCancelled,
        Level = LogLevel.Information,
        Message = "Cache bootstrap was cancelled" )]
    private static partial void LogBootstrapCancelled( ILogger logger );

    /// <summary>Logs a fatal error that aborted the bootstrap run.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapFatalError,
        Level = LogLevel.Error,
        Message = "Fatal error during cache bootstrap" )]
    private static partial void LogBootstrapFatalError( ILogger logger, Exception ex );

    /// <summary>Logs the Redis key count measured after a bootstrap run.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="keyCount">The number of keys present after the run.</param>
    /// <param name="endpoint">The Redis endpoint that was measured.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisKeyCountAfter,
        Level = LogLevel.Information,
        Message = "Redis key count AFTER bootstrap: {KeyCount} (endpoint: {Endpoint})" )]
    private static partial void LogRedisKeyCountAfter( ILogger logger, long keyCount, string endpoint );

    /// <summary>Logs that measuring the Redis key count after a run failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.RedisKeyCountAfterError,
        Level = LogLevel.Warning,
        Message = "Failed to get Redis key count after bootstrap" )]
    private static partial void LogRedisKeyCountAfterError( ILogger logger, Exception ex );

    /// <summary>Logs that a bootstrap run completed, with timing, counts, and the key-count delta.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="elapsedSeconds">The run duration in seconds.</param>
    /// <param name="successCount">The number of records cached successfully.</param>
    /// <param name="errorCount">The number of records that failed to cache.</param>
    /// <param name="keysBefore">The Redis key count before the run.</param>
    /// <param name="keysAfter">The Redis key count after the run.</param>
    /// <param name="netChange">The net change in Redis key count across the run.</param>
    [LoggerMessage(
        EventId = LogEventIds.BootstrapCompleted,
        Level = LogLevel.Information,
        Message = "Cache bootstrap completed in {ElapsedSeconds:F1}s. Success: {SuccessCount}, Errors: {ErrorCount}, Keys before: {KeysBefore}, Keys after: {KeysAfter}, Net change: {NetChange}" )]
    private static partial void LogBootstrapCompleted(
        ILogger logger,
        double elapsedSeconds,
        int successCount,
        int errorCount,
        long keysBefore,
        long keysAfter,
        long netChange );

    /// <summary>Logs that writing the bootstrap status document to Redis failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.StatusUpdateError,
        Level = LogLevel.Warning,
        Message = "Failed to update cache bootstrap status in Redis" )]
    private static partial void LogStatusUpdateError( ILogger logger, Exception ex );

    /// <summary>Logs that reading the bootstrap status document from Redis failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.StatusReadError,
        Level = LogLevel.Warning,
        Message = "Failed to read cache bootstrap status from Redis" )]
    private static partial void LogStatusReadError( ILogger logger, Exception ex );

    // ---- Statistics log messages ----

    /// <summary>Logs that reading the statistics status document from Redis failed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="ex">The exception that was thrown.</param>
    [LoggerMessage(
        EventId = LogEventIds.StatisticsStatusReadError,
        Level = LogLevel.Warning,
        Message = "Failed to read statistics status from Redis" )]
    private static partial void LogStatisticsStatusReadError( ILogger logger, Exception ex );

    /// <summary>Logs that a statistics computation pass is starting.</summary>
    [LoggerMessage(
        EventId = LogEventIds.PassStarting,
        Level = LogLevel.Information,
        Message = "Starting statistics computation pass" )]
    private static partial void LogPassStarting( ILogger logger );

    /// <summary>Logs that a statistics computation pass completed, reporting the total record count.</summary>
    [LoggerMessage(
        EventId = LogEventIds.PassCompleted,
        Level = LogLevel.Information,
        Message = "Statistics computation pass completed: {TotalRecords} records" )]
    private static partial void LogPassCompleted( ILogger logger, int totalRecords );

    /// <summary>Logs that writing the statistics status document to Redis failed.</summary>
    [LoggerMessage(
        EventId = LogEventIds.StatisticsStatusUpdateError,
        Level = LogLevel.Warning,
        Message = "Failed to update statistics status in Redis" )]
    private static partial void LogStatisticsStatusUpdateError( ILogger logger, Exception ex );

    /// <summary>Logs that a statistics computation pass failed.</summary>
    [LoggerMessage(
        EventId = LogEventIds.PassError,
        Level = LogLevel.Error,
        Message = "Statistics computation pass failed ({SanitizedError}); will retry" )]
    private static partial void LogPassError( ILogger logger, Exception ex, string sanitizedError );

    /// <summary>Logs that both the initial and retry statistics passes failed; falling back to the periodic window.</summary>
    [LoggerMessage(
        EventId = LogEventIds.RetryExhausted,
        Level = LogLevel.Error,
        Message = "Statistics computation retry exhausted; falling back to the periodic window" )]
    private static partial void LogRetryExhausted( ILogger logger );

    /// <summary>Logs that a manual statistics refresh trigger was coalesced because a pass is already running.</summary>
    [LoggerMessage(
        EventId = LogEventIds.RefreshCoalesced,
        Level = LogLevel.Information,
        Message = "Statistics refresh trigger coalesced — a pass is already running" )]
    private static partial void LogRefreshCoalesced( ILogger logger );

    #endregion
}
