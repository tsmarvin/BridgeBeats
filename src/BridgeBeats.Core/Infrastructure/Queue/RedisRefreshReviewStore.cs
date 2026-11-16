using System.Text.Json;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Redis-backed refresh review store. Pending saga context is short-lived; unresolved review
/// entries remain until an operator explicitly removes them.
/// </summary>
public sealed partial class RedisRefreshReviewStore(
    IConnectionMultiplexer redis,
    IOptions<QueueSettings> settings,
    ILogger<RedisRefreshReviewStore> logger
) : IRefreshReviewStore {
    private const string PendingPrefix = "cache:refresh:pending:";
    private const string PendingSagaIndexKey = "cache:refresh:pending:sagas";
    private const string PendingSagaSetPrefix = "cache:refresh:pending:saga:";
    private const string UnresolvedKey = "cache:refresh:unresolved";
    private const string UnresolvedSagaIndexKey = "cache:refresh:unresolved:sagas";
    private const string CorruptKey = "cache:refresh:unresolved:corrupt";
    private const string SweepAttemptsPrefix = "cache:refresh:attempts:";
    private const string IncrementSweepAttemptScript = """
        local value = redis.call('INCR', KEYS[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[1])
        return value
        """;
    private const string RegisterPendingScript = """
        local previous = redis.call('GET', KEYS[1])
        if previous then
            local previousOk, previousEntry = pcall(cjson.decode, previous)
            if previousOk then
                local previousCreatedAt = tonumber(previousEntry['createdAtUnixMilliseconds'])
                local incomingCreatedAt = tonumber(ARGV[5])
                if previousCreatedAt and incomingCreatedAt and previousCreatedAt > incomingCreatedAt then
                    return 0
                end
                if previousEntry['sagaId'] and previousEntry['sagaId'] ~= ARGV[2] then
                    if redis.call('HGET', KEYS[2], previousEntry['sagaId']) == ARGV[3] then
                        redis.call('HDEL', KEYS[2], previousEntry['sagaId'])
                    end
                end
            end
        end
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[4])
        redis.call('HSETNX', KEYS[2], ARGV[2], ARGV[3])
        redis.call('SADD', KEYS[3], ARGV[3])
        redis.call('PEXPIRE', KEYS[3], ARGV[4])
        return 1
        """;
    private const string PromoteDirectScript = """
        local previous = redis.call('HGET', KEYS[1], ARGV[1])
        if previous then
            local ok, decoded = pcall(cjson.decode, previous)
            if ok and decoded['sagaId'] and decoded['sagaId'] ~= ARGV[3] then
                redis.call('HDEL', KEYS[2], decoded['sagaId'])
            end
        end
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('HSET', KEYS[2], ARGV[3], ARGV[1])
        redis.call('DEL', KEYS[3])
        return 1
        """;
    private const string DeleteUnresolvedScript = """
        local json = redis.call('HGET', KEYS[1], ARGV[1])
        if not json then
            return 0
        end
        local ok, entry = pcall(cjson.decode, json)
        if not ok then
            return -1
        end
        if entry['sagaId'] ~= ARGV[2] or entry['sourceRecordCid'] ~= ARGV[3] then
            return 0
        end
        redis.call('HDEL', KEYS[1], ARGV[1])
        redis.call('HDEL', KEYS[2], ARGV[2])
        return 1
        """;
    private const string MarkUnresolvedRecordScript = """
        local pending = redis.call('GET', KEYS[1])
        if not pending then return 0 end
        local ok, entry = pcall(cjson.decode, pending)
        if not ok or entry['sagaId'] ~= ARGV[1] or entry['instanceToken'] ~= ARGV[2] then return 0 end
        local previous = redis.call('HGET', KEYS[2], ARGV[3])
        local shouldPromote = true
        if previous then
            local previousOk, previousEntry = pcall(cjson.decode, previous)
            local sameInstance = previousOk and previousEntry['sagaId'] == ARGV[1] and previousEntry['instanceToken'] == ARGV[2]
            local previousCreatedAt = previousOk and tonumber(previousEntry['createdAtUnixMilliseconds'])
            local olderAttempt = previousCreatedAt and previousCreatedAt < tonumber(ARGV[5])
            shouldPromote = sameInstance or olderAttempt
            if shouldPromote and previousOk and previousEntry['sagaId'] then
                redis.call('HDEL', KEYS[3], previousEntry['sagaId'])
            end
        end
        if shouldPromote then
            redis.call('HSET', KEYS[2], ARGV[3], ARGV[4])
            redis.call('HSET', KEYS[3], ARGV[1], ARGV[3])
        end
        redis.call('DEL', KEYS[1])
        if redis.call('HGET', KEYS[4], ARGV[1]) == ARGV[3] then redis.call('HDEL', KEYS[4], ARGV[1]) end
        redis.call('SREM', KEYS[5], ARGV[3])
        redis.call('DEL', KEYS[6])
        return shouldPromote and 1 or 0
        """;
    private const string CompleteRecordScript = """
        local pending = redis.call('GET', KEYS[1])
        if not pending then return 0 end
        local ok, entry = pcall(cjson.decode, pending)
        if not ok or entry['sagaId'] ~= ARGV[1] or entry['instanceToken'] ~= ARGV[2] then return 0 end
        local unresolved = redis.call('HGET', KEYS[2], ARGV[3])
        if unresolved then
            local unresolvedOk, unresolvedEntry = pcall(cjson.decode, unresolved)
            local sameInstance = unresolvedOk and unresolvedEntry['sagaId'] == ARGV[1] and unresolvedEntry['instanceToken'] == ARGV[2]
            local unresolvedCreatedAt = unresolvedOk and tonumber(unresolvedEntry['createdAtUnixMilliseconds'])
            local olderAttempt = unresolvedCreatedAt and unresolvedCreatedAt < tonumber(ARGV[4])
            if sameInstance or olderAttempt then
                redis.call('HDEL', KEYS[2], ARGV[3])
                if redis.call('HGET', KEYS[3], unresolvedEntry['sagaId']) == ARGV[3] then redis.call('HDEL', KEYS[3], unresolvedEntry['sagaId']) end
            end
        end
        redis.call('DEL', KEYS[1])
        if redis.call('HGET', KEYS[4], ARGV[1]) == ARGV[3] then redis.call('HDEL', KEYS[4], ARGV[1]) end
        redis.call('SREM', KEYS[5], ARGV[3])
        redis.call('DEL', KEYS[6])
        return 1
        """;

    private static readonly JsonSerializerOptions s_jsonOptions = new( JsonSerializerDefaults.Web );
    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
    private readonly ILogger<RedisRefreshReviewStore> _logger = logger
        ?? throw new ArgumentNullException( nameof( logger ) );
    private readonly TimeSpan _pendingTtl = TimeSpan.FromMinutes(
        settings?.Value.JobExpirationMinutes ?? throw new ArgumentNullException( nameof( settings ) )
    );

    /// <inheritdoc/>
    public async Task RegisterPendingAsync( RefreshReviewEntry entry, CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( entry );
        ArgumentException.ThrowIfNullOrWhiteSpace( entry.InstanceToken );
        cancellationToken.ThrowIfCancellationRequested( );

        string json = JsonSerializer.Serialize( entry, s_jsonOptions );
        _ = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            RegisterPendingScript,
            [PendingPrefix + entry.SourceRecordUri, PendingSagaIndexKey, PendingSagaSetPrefix + entry.SagaId],
            [json, entry.SagaId, entry.SourceRecordUri, Math.Max( 1L, (long)_pendingTtl.TotalMilliseconds ),
                entry.CreatedAtUnixMilliseconds] );
    }

    /// <inheritdoc/>
    public async Task<RefreshReviewEntry?> GetPendingAsync( string sourceRecordUri, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sourceRecordUri );
        cancellationToken.ThrowIfCancellationRequested( );
        return TryDeserialize( await _redis.GetDatabase( ).StringGetAsync( PendingPrefix + sourceRecordUri ) );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RefreshReviewEntry>> GetPendingForSagaAsync( string sagaId, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        cancellationToken.ThrowIfCancellationRequested( );
        IDatabase db = _redis.GetDatabase( );
        RedisValue[] uris = await db.SetMembersAsync( PendingSagaSetPrefix + sagaId ) ?? [];
        List<RefreshReviewEntry> entries = [];
        foreach (RedisValue uri in uris) {
            RefreshReviewEntry? entry = TryDeserialize( await db.StringGetAsync( PendingPrefix + uri ) );
            if (entry is not null && string.Equals( entry.SagaId, sagaId, StringComparison.Ordinal )) {
                entries.Add( entry );
            } else {
                // Payload TTLs can expire before the saga-set/hash metadata. Prune those
                // tombstones lazily so retry scans do not retain unbounded stale membership.
                _ = await db.SetRemoveAsync( PendingSagaSetPrefix + sagaId, uri );
                RedisValue indexed = await db.HashGetAsync( PendingSagaIndexKey, sagaId );
                if (indexed == uri) {
                    _ = await db.HashDeleteAsync( PendingSagaIndexKey, sagaId );
                }
            }
        }
        if (entries.Count == 0) {
            _ = await db.HashDeleteAsync( PendingSagaIndexKey, sagaId );
            _ = await db.KeyDeleteAsync( PendingSagaSetPrefix + sagaId );
        }
        return entries;
    }

    /// <inheritdoc/>
    public Task MarkUnresolvedAsync( RefreshReviewEntry entry, string reason, CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( entry );
        ArgumentException.ThrowIfNullOrWhiteSpace( entry.InstanceToken );
        return MarkUnresolvedRecordAsync( entry, reason, cancellationToken );
    }

    /// <inheritdoc/>
    public async Task PromoteDirectAsync( RefreshReviewEntry entry, string reason, CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( entry );
        ArgumentException.ThrowIfNullOrWhiteSpace( reason );
        cancellationToken.ThrowIfCancellationRequested( );

        RefreshReviewEntry unresolved = entry with {
            FailedAt = DateTimeOffset.UtcNow,
            FailureReason = reason
        };
        string json = JsonSerializer.Serialize( unresolved, s_jsonOptions );
        _ = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            PromoteDirectScript,
            keys: [UnresolvedKey, UnresolvedSagaIndexKey, SweepAttemptsPrefix + entry.SourceRecordUri],
            values: [entry.SourceRecordUri, json, entry.SagaId] );
        QueueMetrics.RecordMaintenanceOutcome( "promoted_to_review" );
    }

    private async Task MarkUnresolvedRecordAsync( RefreshReviewEntry entry, string reason, CancellationToken cancellationToken ) {
        ArgumentNullException.ThrowIfNull( entry );
        ArgumentException.ThrowIfNullOrWhiteSpace( reason );
        cancellationToken.ThrowIfCancellationRequested( );
        RefreshReviewEntry unresolved = entry with { FailedAt = DateTimeOffset.UtcNow, FailureReason = reason };
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            MarkUnresolvedRecordScript,
            [PendingPrefix + entry.SourceRecordUri, UnresolvedKey, UnresolvedSagaIndexKey, PendingSagaIndexKey,
                PendingSagaSetPrefix + entry.SagaId, SweepAttemptsPrefix + entry.SourceRecordUri],
            [entry.SagaId, entry.InstanceToken, entry.SourceRecordUri, JsonSerializer.Serialize( unresolved, s_jsonOptions ),
                entry.CreatedAtUnixMilliseconds] );
        if ((int)result == 1) {
            QueueMetrics.RecordMaintenanceOutcome( "promoted_to_review" );
        }
    }

    /// <inheritdoc/>
    public async Task CompleteAsync( RefreshReviewEntry entry, CancellationToken cancellationToken = default ) {
        ArgumentNullException.ThrowIfNull( entry );
        ArgumentException.ThrowIfNullOrWhiteSpace( entry.InstanceToken );
        cancellationToken.ThrowIfCancellationRequested( );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            CompleteRecordScript,
            [PendingPrefix + entry.SourceRecordUri, UnresolvedKey, UnresolvedSagaIndexKey, PendingSagaIndexKey,
                PendingSagaSetPrefix + entry.SagaId, SweepAttemptsPrefix + entry.SourceRecordUri],
            [entry.SagaId, entry.InstanceToken, entry.SourceRecordUri, entry.CreatedAtUnixMilliseconds] );
        if ((int)result == 1) {
            QueueMetrics.RecordMaintenanceOutcome( "terminal_completion" );
        }
    }

    /// <inheritdoc/>
    public async Task CompleteAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        IReadOnlyList<RefreshReviewEntry> entries = await GetPendingForSagaAsync( sagaId, cancellationToken );
        foreach (RefreshReviewEntry entry in entries.Where( e => e.InstanceToken == expectedInstanceToken )) {
            await CompleteAsync( entry, cancellationToken );
        }
    }

    /// <inheritdoc/>
    public async Task<int> IncrementSweepAttemptAsync( string sourceRecordUri, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sourceRecordUri );
        cancellationToken.ThrowIfCancellationRequested( );
        RedisResult value = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            IncrementSweepAttemptScript,
            [SweepAttemptsPrefix + sourceRecordUri],
            [Math.Max( 1L, (long)_pendingTtl.TotalMilliseconds )] );
        return (int)value;
    }

    /// <inheritdoc/>
    public async Task ClearSweepAttemptsAsync( string sourceRecordUri, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sourceRecordUri );
        cancellationToken.ThrowIfCancellationRequested( );
        _ = await _redis.GetDatabase( ).KeyDeleteAsync( SweepAttemptsPrefix + sourceRecordUri );
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RefreshReviewEntry>> GetUnresolvedAsync( CancellationToken cancellationToken = default ) {
        cancellationToken.ThrowIfCancellationRequested( );
        HashEntry[] entries = await _redis.GetDatabase( ).HashGetAllAsync( UnresolvedKey );

        List<RefreshReviewEntry> results = [];
        foreach (HashEntry entry in entries) {
            try {
                RefreshReviewEntry? result = JsonSerializer.Deserialize<RefreshReviewEntry>( entry.Value.ToString( ), s_jsonOptions );
                if (result is not null) {
                    results.Add( result );
                }
            } catch (JsonException ex) {
                LogCorruptEntryQuarantined( _logger, ex, entry.Name.ToString( ) );
                _ = await _redis.GetDatabase( ).HashSetAsync( CorruptKey, entry.Name, entry.Value );
                _ = await _redis.GetDatabase( ).HashDeleteAsync( UnresolvedKey, entry.Name );
            }
        }

        return [..
            results
                .OrderByDescending( entry => entry.FailedAt )
                .ThenBy( entry => entry.SourceRecordUri, StringComparer.Ordinal )
        ];
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteUnresolvedAsync(
        string sourceRecordUri,
        string expectedSagaId,
        string expectedSourceRecordCid,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sourceRecordUri );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedSagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedSourceRecordCid );
        cancellationToken.ThrowIfCancellationRequested( );

        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            DeleteUnresolvedScript,
            keys: [UnresolvedKey, UnresolvedSagaIndexKey],
            values: [sourceRecordUri, expectedSagaId, expectedSourceRecordCid] );
        long outcome = (long)result;
        if (outcome < 0) {
            throw new InvalidOperationException(
                $"Refresh-review entry for '{sourceRecordUri}' is corrupt and could not be revision-checked." );
        }
        return outcome == 1;
    }

    private static RefreshReviewEntry? TryDeserialize( RedisValue json ) {
        if (json.IsNullOrEmpty) {
            return null;
        }

        try {
            return JsonSerializer.Deserialize<RefreshReviewEntry>( json.ToString( ), s_jsonOptions );
        } catch (JsonException) {
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Quarantined corrupt refresh-review entry {SourceRecordUri}" )]
    private static partial void LogCorruptEntryQuarantined(
        ILogger logger,
        Exception ex,
        string sourceRecordUri );
}
