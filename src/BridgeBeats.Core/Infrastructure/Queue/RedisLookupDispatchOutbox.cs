using System.Globalization;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Redis transactional outbox for initial lookup-provider dispatches. The stage script creates the
/// provider leg and outbox entry atomically. The relay script atomically appends the payload to the
/// provider stream and marks the leg dispatched. Published envelopes remain durable until the saga
/// leg completes and track their current stream entry and eligibility time. Recovery only re-drives
/// a missing eligible entry, and time-ordered indexes reserve relay capacity for both new dispatches
/// and recovery checks.
/// </summary>
public sealed partial class RedisLookupDispatchOutbox(
    IConnectionMultiplexer redis,
    IOptions<QueueSettings> settings,
    ILogger<RedisLookupDispatchOutbox> logger
) : ILookupDispatchOutbox {
    internal const string PendingIndexKey = "outbox:lookup-dispatch:pending-due";
    internal const string PublishedRecoveryIndexKey = "outbox:lookup-dispatch:published-recovery-due";
    internal const string DispatchStateField = "dispatchState";
    private const string DispatchPending = "pending";
    internal const string DispatchPublished = "published";
    internal const string DispatchPublishedAtField = "dispatchPublishedAt";
    private const string OutboxPrefix = "outbox:lookup-dispatch:";
    internal const string CurrentMessageIdField = "currentMessageId";
    internal const string NotBeforeField = "notBefore";
    internal static readonly TimeSpan RedriveAfter = TimeSpan.FromMinutes( 5 );

    private static readonly string s_stageBatchScript = $$"""
        local count = tonumber(ARGV[1])
        if redis.call('exists', KEYS[1]) == 0
            or redis.call('hget', KEYS[1], ARGV[2]) ~= ARGV[3] then
            local mismatches = {}
            for i = 1, count do mismatches[i] = 0 end
            return mismatches
        end

        local outcomes = {}
        local ttl = tonumber(ARGV[7])
        if ARGV[10] ~= '' then
            local previous = redis.call('GET', KEYS[4])
            if previous then
                local previousOk, previousEntry = pcall(cjson.decode, previous)
                if previousOk then
                    local previousCreatedAt = tonumber(previousEntry['createdAtUnixMilliseconds'])
                    local incomingCreatedAt = tonumber(ARGV[14])
                    if previousCreatedAt and incomingCreatedAt and previousCreatedAt > incomingCreatedAt then
                        local mismatches = {}
                        for i = 1, count do mismatches[i] = 0 end
                        return mismatches
                    end
                    if previousEntry['sagaId'] and previousEntry['sagaId'] ~= ARGV[11] then
                        if redis.call('HGET', KEYS[5], previousEntry['sagaId']) == ARGV[12] then
                            redis.call('HDEL', KEYS[5], previousEntry['sagaId'])
                        end
                    end
                end
            end
            redis.call('SET', KEYS[4], ARGV[10], 'PX', ARGV[13])
            redis.call('HSETNX', KEYS[5], ARGV[11], ARGV[12])
            redis.call('SADD', KEYS[6], ARGV[12])
            redis.call('PEXPIRE', KEYS[6], ARGV[13])
        end

        for i = 1, count do
            local providerKey = KEYS[5 + (i * 2)]
            local outboxKey = KEYS[6 + (i * 2)]
            local offset = 15 + ((i - 1) * 7)
            local complete = redis.call('hget', providerKey, '{{RedisSagaStateManager.FieldIsComplete}}')
            local dispatchState = redis.call('hget', providerKey, ARGV[4])
            local needsDispatch = dispatchState ~= ARGV[5]
                or redis.call('exists', outboxKey) == 0

            if complete == 'True' or complete == 'true' then
                redis.call('del', outboxKey)
                redis.call('zrem', KEYS[2], outboxKey)
                redis.call('zrem', KEYS[3], outboxKey)
                outcomes[i] = 2
            elseif needsDispatch then
                if redis.call('exists', providerKey) == 0 then
                    redis.call('hset', providerKey,
                        '{{RedisSagaStateManager.FieldIsComplete}}', 'False',
                        '{{RedisSagaStateManager.FieldIsSuccess}}', 'False',
                        '{{RedisSagaStateManager.FieldResultJson}}', '',
                        '{{RedisSagaStateManager.FieldCompletedAt}}', '',
                        '{{RedisSagaStateManager.FieldErrorMessage}}', '')
                end
                redis.call('hset', providerKey, ARGV[4], ARGV[9])
                redis.call('hdel', providerKey, ARGV[6])
                redis.call('hdel', outboxKey, '{{CurrentMessageIdField}}', '{{NotBeforeField}}')
                redis.call('hset', outboxKey,
                    'sagaId', ARGV[offset + 4],
                    'provider', ARGV[offset],
                    'instanceToken', ARGV[3],
                    'payload', ARGV[offset + 1],
                    'targetStream', ARGV[offset + 2],
                    'workChannel', ARGV[offset + 3],
                    'priority', ARGV[offset + 5],
                    'enqueueOrigin', ARGV[offset + 6])
                redis.call('zadd', KEYS[2], tonumber(ARGV[8]), outboxKey)
                redis.call('zrem', KEYS[3], outboxKey)
                redis.call('expire', outboxKey, ttl)
                outcomes[i] = 1
            else
                outcomes[i] = 2
            end
            redis.call('expire', providerKey, ttl)
        end
        return outcomes
        """;

    private static readonly string s_dispatchScript = $$"""
        if redis.call('exists', KEYS[3]) == 0 then
            redis.call('zrem', KEYS[4], KEYS[3])
            redis.call('zrem', KEYS[5], KEYS[3])
            return false
        end
        local outboxToken = redis.call('hget', KEYS[3], 'instanceToken')
        if outboxToken ~= ARGV[2] or redis.call('hget', KEYS[3], 'targetStream') ~= KEYS[6] then
            return false
        end
        if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= outboxToken then
            redis.call('del', KEYS[3])
            redis.call('zrem', KEYS[4], KEYS[3])
            redis.call('zrem', KEYS[5], KEYS[3])
            return false
        end
        local complete = redis.call('hget', KEYS[2], '{{RedisSagaStateManager.FieldIsComplete}}')
        if complete == 'True' or complete == 'true' then
            redis.call('hset', KEYS[2], ARGV[3], ARGV[4])
            redis.call('del', KEYS[3])
            redis.call('zrem', KEYS[4], KEYS[3])
            redis.call('zrem', KEYS[5], KEYS[3])
            return false
        end
        local dispatchState = redis.call('hget', KEYS[2], ARGV[3])
        if dispatchState ~= ARGV[12] then
            redis.call('zrem', KEYS[4], KEYS[3])
            if dispatchState == ARGV[4] then
                redis.call('zadd', KEYS[5], tonumber(ARGV[13]), KEYS[3])
            else
                redis.call('del', KEYS[3])
                redis.call('zrem', KEYS[5], KEYS[3])
            end
            return false
        end
        local payload = redis.call('hget', KEYS[3], 'payload')
        if not payload or payload == '' then
            redis.call('del', KEYS[3])
            redis.call('zrem', KEYS[4], KEYS[3])
            redis.call('zrem', KEYS[5], KEYS[3])
            return false
        end
        local messageId = redis.call(
            'xadd', KEYS[6], '*', ARGV[5], payload, ARGV[6], ARGV[7], ARGV[14], KEYS[3])
        redis.call('hset', KEYS[2], ARGV[3], ARGV[4], ARGV[10], ARGV[11])
        redis.call('hset', KEYS[3], '{{CurrentMessageIdField}}', messageId)
        redis.call('hdel', KEYS[3], '{{NotBeforeField}}')
        redis.call('expire', KEYS[2], tonumber(ARGV[8]))
        redis.call('expire', KEYS[3], tonumber(ARGV[8]))
        redis.call('zrem', KEYS[4], KEYS[3])
        redis.call('zadd', KEYS[5], tonumber(ARGV[13]), KEYS[3])
        redis.call('publish', ARGV[9], messageId)
        return messageId
        """;

    private static readonly string s_redriveScript = $$"""
        if redis.call('exists', KEYS[3]) == 0 then
            redis.call('zrem', KEYS[4], KEYS[3])
            return false
        end
        local outboxToken = redis.call('hget', KEYS[3], 'instanceToken')
        if outboxToken ~= ARGV[2] then
            return false
        end
        if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= outboxToken then
            redis.call('del', KEYS[3])
            redis.call('zrem', KEYS[4], KEYS[3])
            return false
        end
        local complete = redis.call('hget', KEYS[2], '{{RedisSagaStateManager.FieldIsComplete}}')
        if complete == 'True' or complete == 'true' then
            redis.call('del', KEYS[3])
            redis.call('zrem', KEYS[4], KEYS[3])
            return false
        end
        if redis.call('hget', KEYS[2], ARGV[3]) ~= ARGV[4] then
            redis.call('zrem', KEYS[4], KEYS[3])
            return false
        end
        if redis.call('hget', KEYS[3], 'targetStream') ~= KEYS[5] then
            redis.call('zadd', KEYS[4], tonumber(ARGV[12]), KEYS[3])
            return false
        end
        local currentMessageId = redis.call('hget', KEYS[3], '{{CurrentMessageIdField}}')
        if currentMessageId then
            local current = redis.call('xrange', KEYS[5], currentMessageId, currentMessageId, 'COUNT', 1)
            if #current > 0 then
                redis.call('zadd', KEYS[4], tonumber(ARGV[12]), KEYS[3])
                return false
            end
        end
        local notBefore = tonumber(redis.call('hget', KEYS[3], '{{NotBeforeField}}') or '')
        if notBefore and notBefore > tonumber(ARGV[9]) then
            redis.call('zadd', KEYS[4], notBefore, KEYS[3])
            return false
        end
        local payload = redis.call('hget', KEYS[3], 'payload')
        if not payload or payload == '' then
            redis.call('del', KEYS[3])
            redis.call('zrem', KEYS[4], KEYS[3])
            return false
        end
        local messageId = redis.call(
            'xadd', KEYS[5], '*', ARGV[6], payload, ARGV[7], ARGV[8], ARGV[13], KEYS[3])
        redis.call('hset', KEYS[2], ARGV[5], ARGV[9])
        redis.call('hset', KEYS[3], '{{CurrentMessageIdField}}', messageId)
        redis.call('hdel', KEYS[3], '{{NotBeforeField}}')
        redis.call('expire', KEYS[2], tonumber(ARGV[10]))
        redis.call('expire', KEYS[3], tonumber(ARGV[10]))
        redis.call('zadd', KEYS[4], tonumber(ARGV[12]), KEYS[3])
        redis.call('publish', ARGV[11], messageId)
        return messageId
        """;

    private static readonly string s_promoteScript = $$"""
        local count = tonumber(ARGV[1])
        if redis.call('EXISTS', KEYS[1]) == 0
            or redis.call('HGET', KEYS[1], '{{RedisSagaStateManager.FieldInstanceToken}}') ~= ARGV[2] then
            return {0, 0}
        end

        redis.call('HSET', KEYS[1], '{{RedisSagaStateManager.FieldOriginPriority}}', ARGV[3])
        redis.call('EXPIRE', KEYS[1], tonumber(ARGV[8]))
        local moved = 0

        for i = 1, count do
            local keyOffset = 3 + ((i - 1) * 3)
            local argOffset = 9 + ((i - 1) * 2)
            local providerKey = KEYS[keyOffset + 1]
            local recoveryKey = KEYS[keyOffset + 2]
            local interactiveStream = KEYS[keyOffset + 3]
            local consumerGroup = ARGV[argOffset]
            local workChannel = ARGV[argOffset + 1]

            local complete = redis.call('HGET', providerKey, '{{RedisSagaStateManager.FieldIsComplete}}')
            local recoveryToken = redis.call('HGET', recoveryKey, 'instanceToken')
            if complete ~= 'True' and complete ~= 'true' and recoveryToken == ARGV[2] then
                local payload = redis.call('HGET', recoveryKey, 'payload')
                local payloadOk, request = pcall(cjson.decode, payload or '')
                if payloadOk and type(request) == 'table' then
                    request['originPriority'] = tonumber(ARGV[4])
                    payload = cjson.encode(request)
                    local dispatchState = redis.call('HGET', providerKey, '{{DispatchStateField}}')

                    if dispatchState == '{{DispatchPending}}' then
                        redis.call('HSET', recoveryKey,
                            'payload', payload,
                            'targetStream', interactiveStream,
                            'workChannel', workChannel,
                            'priority', ARGV[4])
                        redis.call('ZADD', KEYS[2], tonumber(ARGV[6]), recoveryKey)
                        redis.call('ZREM', KEYS[3], recoveryKey)
                    elseif dispatchState == '{{DispatchPublished}}' then
                        local currentMessageId = redis.call('HGET', recoveryKey, '{{CurrentMessageIdField}}')
                        local sourceStream = redis.call('HGET', recoveryKey, 'targetStream')
                        local claimed = false
                        local currentExists = false

                        if currentMessageId and sourceStream then
                            local rangeOk, current = pcall(
                                redis.call, 'XRANGE', sourceStream,
                                currentMessageId, currentMessageId, 'COUNT', 1)
                            if not rangeOk then
                                claimed = true
                            elseif #current > 0 then
                                currentExists = true
                                local pendingOk, pending = pcall(
                                    redis.call, 'XPENDING', sourceStream, consumerGroup,
                                    currentMessageId, currentMessageId, 1)
                                if pendingOk and #pending > 0 then claimed = true end
                            end
                        end

                        if sourceStream == interactiveStream and currentExists then
                            local due = tonumber(ARGV[7])
                            local notBefore = tonumber(redis.call('HGET', recoveryKey, '{{NotBeforeField}}') or '')
                            if notBefore and notBefore > due then due = notBefore end
                            redis.call('HSET', recoveryKey, 'payload', payload, 'priority', ARGV[4])
                            redis.call('ZADD', KEYS[3], due, recoveryKey)
                        elseif not claimed then
                            local replacement = redis.call(
                                'XADD', interactiveStream, '*',
                                '{{QueueStreamFieldNames.Payload}}', payload,
                                '{{QueueStreamFieldNames.EnqueuedAt}}', ARGV[5],
                                '{{QueueStreamFieldNames.DispatchOutboxKey}}', recoveryKey)
                            if currentExists then
                                redis.call('XDEL', sourceStream, currentMessageId)
                            end
                            redis.call('HSET', recoveryKey,
                                'payload', payload,
                                'targetStream', interactiveStream,
                                'workChannel', workChannel,
                                'priority', ARGV[4],
                                'currentMessageId', replacement,
                                '{{DispatchPublishedAtField}}', ARGV[6])
                            local due = tonumber(ARGV[7])
                            local notBefore = tonumber(redis.call('HGET', recoveryKey, '{{NotBeforeField}}') or '')
                            if notBefore and notBefore > due then due = notBefore end
                            redis.call('ZREM', KEYS[2], recoveryKey)
                            redis.call('ZADD', KEYS[3], due, recoveryKey)
                            redis.call('PUBLISH', workChannel, replacement)
                            moved = moved + 1
                        end
                    end
                    redis.call('EXPIRE', providerKey, tonumber(ARGV[8]))
                    redis.call('EXPIRE', recoveryKey, tonumber(ARGV[8]))
                end
            end
        end
        return {1, moved}
        """;

    private readonly IConnectionMultiplexer _redis = redis
        ?? throw new ArgumentNullException( nameof( redis ) );
    private readonly QueueSettings _settings = (settings
        ?? throw new ArgumentNullException( nameof( settings ) )).Value;
    private readonly ILogger<RedisLookupDispatchOutbox> _logger = logger
        ?? throw new ArgumentNullException( nameof( logger ) );
    private readonly JsonSerializerOptions _jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private int _singleItemDispatchCounter;

    /// <inheritdoc/>
    public async Task<ProviderDispatchStageOutcome> StageAsync(
        QueuedLookupRequest request,
        QueuePriority priority,
        CancellationToken cancellationToken = default
    ) {
        IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome> outcomes =
            await StageBatchAsync( [request], priority, cancellationToken );
        return outcomes[request.Provider];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>> StageBatchAsync(
        IReadOnlyList<QueuedLookupRequest> requests,
        QueuePriority priority,
        CancellationToken cancellationToken = default
    ) => await StageBatchCoreAsync( requests, priority, null, cancellationToken );

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>> StageRefreshBatchAsync(
        IReadOnlyList<QueuedLookupRequest> requests,
        QueuePriority priority,
        RefreshReviewEntry reviewEntry,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull( reviewEntry );
        return await StageBatchCoreAsync( requests, priority, reviewEntry, cancellationToken );
    }

    private async Task<IReadOnlyDictionary<SupportedProviders, ProviderDispatchStageOutcome>> StageBatchCoreAsync(
        IReadOnlyList<QueuedLookupRequest> requests,
        QueuePriority priority,
        RefreshReviewEntry? reviewEntry,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull( requests );
        if (requests.Count == 0) {
            throw new ArgumentException( "At least one provider dispatch is required.", nameof( requests ) );
        }
        cancellationToken.ThrowIfCancellationRequested( );

        QueuedLookupRequest first = requests[0]
            ?? throw new ArgumentException( "Provider dispatches cannot contain null requests.", nameof( requests ) );
        ArgumentException.ThrowIfNullOrWhiteSpace( first.SagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( first.SagaInstanceToken );
        if (requests.Any( request => request is null
            || !string.Equals( request.SagaId, first.SagaId, StringComparison.Ordinal )
            || !string.Equals( request.SagaInstanceToken, first.SagaInstanceToken, StringComparison.Ordinal ) )) {
            throw new ArgumentException( "Every provider dispatch must belong to the same saga generation.", nameof( requests ) );
        }
        if (requests.Select( request => request.Provider ).Distinct( ).Count( ) != requests.Count) {
            throw new ArgumentException( "Provider dispatches must contain unique providers.", nameof( requests ) );
        }
        if (reviewEntry is not null
            && (!string.Equals( reviewEntry.SagaId, first.SagaId, StringComparison.Ordinal )
                || !string.Equals( reviewEntry.InstanceToken, first.SagaInstanceToken, StringComparison.Ordinal ))) {
            throw new ArgumentException(
                "Refresh review context must belong to the same saga generation as its dispatches.",
                nameof( reviewEntry ) );
        }

        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<RedisKey> keys = [
            RedisSagaStateManager.GetSagaKey( first.SagaId ),
            PendingIndexKey,
            PublishedRecoveryIndexKey,
            RedisRefreshReviewStore.GetPendingKey( reviewEntry?.SourceRecordUri ?? "unused" ),
            RedisRefreshReviewStore.PendingSagaIndexKey,
            RedisRefreshReviewStore.GetPendingSagaSetKey( reviewEntry?.SagaId ?? "unused" )
        ];
        string reviewJson = reviewEntry is null
            ? string.Empty
            : JsonSerializer.Serialize( reviewEntry, RedisRefreshReviewStore.JsonOptions );
        long ttlMilliseconds = Math.Max( 1L, (long)TimeSpan.FromMinutes(
            _settings.JobExpirationMinutes ).TotalMilliseconds );
        List<RedisValue> values = [
            requests.Count,
            RedisSagaStateManager.FieldInstanceToken,
            first.SagaInstanceToken,
            DispatchStateField,
            DispatchPublished,
            DispatchPublishedAtField,
            ttlSeconds,
            now.ToUnixTimeMilliseconds( ),
            DispatchPending,
            reviewJson,
            reviewEntry?.SagaId ?? string.Empty,
            reviewEntry?.SourceRecordUri ?? string.Empty,
            ttlMilliseconds,
            reviewEntry?.CreatedAtUnixMilliseconds ?? 0
        ];

        foreach (QueuedLookupRequest request in requests) {
            (string targetStream, string workChannel, QueuePriority effectivePriority) =
                ResolveDispatchTarget( request, priority );
            keys.Add( RedisSagaStateManager.GetProviderKey( request.SagaId, request.Provider ) );
            keys.Add( GetOutboxKey( request.SagaId, request.Provider ) );
            values.Add( ((int)request.Provider).ToString( CultureInfo.InvariantCulture ) );
            values.Add( JsonSerializer.Serialize( request, _jsonOptions ) );
            values.Add( targetStream );
            values.Add( workChannel );
            values.Add( request.SagaId );
            values.Add( ((int)effectivePriority).ToString( CultureInfo.InvariantCulture ) );
            values.Add( ((int)request.EnqueueOrigin).ToString( CultureInfo.InvariantCulture ) );
        }

        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            s_stageBatchScript, [.. keys], [.. values] );
        RedisResult[] rawOutcomes = (RedisResult[])result!;
        if (rawOutcomes.Length != requests.Count) {
            throw new InvalidOperationException( "Redis returned an invalid provider-dispatch outcome count." );
        }

        Dictionary<SupportedProviders, ProviderDispatchStageOutcome> outcomes = [];
        for (int index = 0; index < requests.Count; index++) {
            QueuedLookupRequest request = requests[index];
            ProviderDispatchStageOutcome outcome = (ProviderDispatchStageOutcome)(int)rawOutcomes[index];
            outcomes.Add( request.Provider, outcome );
            if (outcome == ProviderDispatchStageOutcome.Staged) {
                LogDispatchStaged( _logger, request.SagaId, request.Provider );
            }
        }
        return outcomes;
    }

    /// <inheritdoc/>
    public async Task<bool> DispatchAsync(
        string sagaId,
        SupportedProviders provider,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        cancellationToken.ThrowIfCancellationRequested( );
        return await DispatchOutboxKeyAsync( GetOutboxKey( sagaId, provider ), cancellationToken );
    }

    /// <inheritdoc/>
    public async Task<bool> PromoteToInteractiveAsync(
        string sagaId,
        string instanceToken,
        IReadOnlyCollection<SupportedProviders> pendingProviders,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( instanceToken );
        ArgumentNullException.ThrowIfNull( pendingProviders );
        cancellationToken.ThrowIfCancellationRequested( );

        SupportedProviders[] providers = [.. pendingProviders.Distinct( )];
        List<RedisKey> keys = [
            RedisSagaStateManager.GetSagaKey( sagaId ),
            PendingIndexKey,
            PublishedRecoveryIndexKey
        ];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        List<RedisValue> values = [
            providers.Length,
            instanceToken,
            QueuePriority.Interactive.ToString( ),
            ((int)QueuePriority.Interactive).ToString( CultureInfo.InvariantCulture ),
            now.ToString( "O", CultureInfo.InvariantCulture ),
            now.ToUnixTimeMilliseconds( ),
            now.Add( RedriveAfter ).ToUnixTimeMilliseconds( ),
            ttlSeconds
        ];

        foreach (SupportedProviders provider in providers) {
            keys.Add( RedisSagaStateManager.GetProviderKey( sagaId, provider ) );
            keys.Add( GetOutboxKey( sagaId, provider ) );
            keys.Add( QueueStreamKeys.For( provider, QueuePriority.Interactive ) );
            values.Add( provider == SupportedProviders.Spotify
                ? SpotifyConstants.ConsumerGroup
                : $"{provider.ToString( ).ToLowerInvariant( )}-workers" );
            values.Add( QueueStreamKeys.WorkSignalFor( provider ) );
        }

        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            s_promoteScript, [.. keys], [.. values] );
        RedisResult[] outcome = (RedisResult[])result!;
        return outcome.Length == 2 && (int)outcome[0] == 1;
    }

    /// <inheritdoc/>
    public async Task<int> DispatchPendingAsync( int limit, CancellationToken cancellationToken = default ) {
        if (limit <= 0) {
            throw new ArgumentOutOfRangeException( nameof( limit ) );
        }
        cancellationToken.ThrowIfCancellationRequested( );

        if (limit == 1) {
            bool recoverFirst = Interlocked.Increment( ref _singleItemDispatchCounter ) % 2 == 0;
            (int singleDispatched, int visited) = await DispatchDueAsync(
                recoverFirst ? PublishedRecoveryIndexKey : PendingIndexKey,
                1,
                recoverFirst ? RedriveOutboxKeyAsync : DispatchOutboxKeyAsync,
                cancellationToken );
            if (visited == 0) {
                (singleDispatched, _) = await DispatchDueAsync(
                    recoverFirst ? PendingIndexKey : PublishedRecoveryIndexKey,
                    1,
                    recoverFirst ? DispatchOutboxKeyAsync : RedriveOutboxKeyAsync,
                    cancellationToken );
            }
            return singleDispatched;
        }

        int pendingBudget = (limit + 1) / 2;
        int recoveryBudget = limit - pendingBudget;
        (int pendingDispatched, int pendingVisited) = await DispatchDueAsync(
            PendingIndexKey, pendingBudget, DispatchOutboxKeyAsync, cancellationToken );
        (int recoveryDispatched, int recoveryVisited) = await DispatchDueAsync(
            PublishedRecoveryIndexKey, recoveryBudget, RedriveOutboxKeyAsync, cancellationToken );
        int dispatched = pendingDispatched + recoveryDispatched;

        if (pendingVisited < pendingBudget) {
            (int extra, _) = await DispatchDueAsync(
                PublishedRecoveryIndexKey,
                pendingBudget - pendingVisited,
                RedriveOutboxKeyAsync,
                cancellationToken );
            dispatched += extra;
        } else if (recoveryVisited < recoveryBudget) {
            (int extra, _) = await DispatchDueAsync(
                PendingIndexKey,
                recoveryBudget - recoveryVisited,
                DispatchOutboxKeyAsync,
                cancellationToken );
            dispatched += extra;
        }
        return dispatched;
    }

    private async Task<(int Dispatched, int Visited)> DispatchDueAsync(
        RedisKey indexKey,
        int limit,
        Func<string, CancellationToken, Task<bool>> dispatch,
        CancellationToken cancellationToken
    ) {
        if (limit == 0) {
            return (0, 0);
        }
        RedisValue[] members = await _redis.GetDatabase( ).SortedSetRangeByScoreAsync(
            indexKey,
            stop: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds( ),
            take: limit );
        int dispatched = 0;
        foreach (RedisValue member in members) {
            cancellationToken.ThrowIfCancellationRequested( );
            if (await dispatch( member.ToString( ), cancellationToken )) {
                dispatched++;
            }
        }
        return (dispatched, members.Length);
    }

    private async Task<bool> DispatchOutboxKeyAsync(
        string outboxKey,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested( );
        IDatabase db = _redis.GetDatabase( );
        DispatchMetadata? metadata = await ReadDispatchMetadataAsync( db, outboxKey );
        if (metadata is null) {
            return false;
        }

        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        RedisResult result = await db.ScriptEvaluateAsync(
            s_dispatchScript,
            [
                RedisSagaStateManager.GetSagaKey( metadata.SagaId ),
                RedisSagaStateManager.GetProviderKey( metadata.SagaId, metadata.Provider ),
                outboxKey,
                PendingIndexKey,
                PublishedRecoveryIndexKey,
                metadata.TargetStream
            ],
            [
                "instanceToken",
                metadata.InstanceToken,
                DispatchStateField,
                DispatchPublished,
                QueueStreamFieldNames.Payload,
                QueueStreamFieldNames.EnqueuedAt,
                now.ToString( "O", CultureInfo.InvariantCulture ),
                ttlSeconds,
                metadata.WorkChannel,
                DispatchPublishedAtField,
                now.ToUnixTimeMilliseconds( ),
                DispatchPending,
                now.Add( RedriveAfter ).ToUnixTimeMilliseconds( ),
                QueueStreamFieldNames.DispatchOutboxKey
            ] );
        RedisValue messageId = (RedisValue)result;
        if (!messageId.HasValue) {
            return false;
        }

        QueueMetrics.RecordEnqueue( metadata.Provider, metadata.Priority, metadata.EnqueueOrigin );

        if (_logger.IsEnabled( LogLevel.Debug )) {
            string publishedMessageId = messageId.ToString( );
            LogDispatchPublished( _logger, metadata.SagaId, metadata.Provider, publishedMessageId );
        }
        return true;
    }

    private async Task<bool> RedriveOutboxKeyAsync(
        string outboxKey,
        CancellationToken cancellationToken
    ) {
        cancellationToken.ThrowIfCancellationRequested( );
        IDatabase db = _redis.GetDatabase( );
        DispatchMetadata? metadata = await ReadDispatchMetadataAsync( db, outboxKey );
        if (metadata is null) {
            return false;
        }

        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RedisResult result = await db.ScriptEvaluateAsync(
            s_redriveScript,
            [
                RedisSagaStateManager.GetSagaKey( metadata.SagaId ),
                RedisSagaStateManager.GetProviderKey( metadata.SagaId, metadata.Provider ),
                outboxKey,
                PublishedRecoveryIndexKey,
                metadata.TargetStream
            ],
            [
                RedisSagaStateManager.FieldInstanceToken,
                metadata.InstanceToken,
                DispatchStateField,
                DispatchPublished,
                DispatchPublishedAtField,
                QueueStreamFieldNames.Payload,
                QueueStreamFieldNames.EnqueuedAt,
                now.ToString( "O", CultureInfo.InvariantCulture ),
                now.ToUnixTimeMilliseconds( ),
                ttlSeconds,
                metadata.WorkChannel,
                now.Add( RedriveAfter ).ToUnixTimeMilliseconds( ),
                QueueStreamFieldNames.DispatchOutboxKey
            ] );
        RedisValue messageId = (RedisValue)result;
        if (!messageId.HasValue) {
            return false;
        }

        QueueMetrics.RecordEnqueue(
            metadata.Provider,
            metadata.Priority,
            QueueEnqueueOrigin.Requeue );
        if (_logger.IsEnabled( LogLevel.Debug )) {
            string publishedMessageId = messageId.ToString( );
            LogDispatchPublished(
                _logger, metadata.SagaId, metadata.Provider, publishedMessageId );
        }
        return true;
    }

    private static async Task<DispatchMetadata?> ReadDispatchMetadataAsync(
        IDatabase db,
        string outboxKey
    ) {
        RedisValue[] values = await db.HashGetAsync(
            outboxKey,
            ["sagaId", "provider", "instanceToken", "targetStream", "workChannel", "priority", "enqueueOrigin"] );
        if (values.Length == 7
            && values.All( value => !value.IsNullOrEmpty )
            && int.TryParse( values[1].ToString( ), CultureInfo.InvariantCulture, out int providerValue )
            && Enum.IsDefined( (SupportedProviders)providerValue )
            && int.TryParse( values[5].ToString( ), CultureInfo.InvariantCulture, out int priorityValue )
            && Enum.IsDefined( (QueuePriority)priorityValue )
            && int.TryParse( values[6].ToString( ), CultureInfo.InvariantCulture, out int originValue )
            && Enum.IsDefined( (QueueEnqueueOrigin)originValue )) {
            return new DispatchMetadata(
                values[0].ToString( ),
                (SupportedProviders)providerValue,
                values[2].ToString( ),
                values[3].ToString( ),
                values[4].ToString( ),
                (QueuePriority)priorityValue,
                (QueueEnqueueOrigin)originValue );
        }

        _ = await db.KeyDeleteAsync( outboxKey );
        _ = await db.SortedSetRemoveAsync( PendingIndexKey, outboxKey );
        _ = await db.SortedSetRemoveAsync( PublishedRecoveryIndexKey, outboxKey );
        return null;
    }

    private sealed record DispatchMetadata(
        string SagaId,
        SupportedProviders Provider,
        string InstanceToken,
        string TargetStream,
        string WorkChannel,
        QueuePriority Priority,
        QueueEnqueueOrigin EnqueueOrigin );

    internal static string GetOutboxKey( string sagaId, SupportedProviders provider ) =>
        $"{OutboxPrefix}{sagaId}:{provider}";

    private static (string Stream, string WorkChannel, QueuePriority EffectivePriority)
        ResolveDispatchTarget( QueuedLookupRequest request, QueuePriority priority ) {
        if (request.Provider == SupportedProviders.Spotify
            && priority != QueuePriority.Interactive
            && !request.BypassBulkRouting
            && request.LookupType is LookupRequestType.SongIdLookup or LookupRequestType.AlbumIdLookup) {
            return (
                QueueStreamKeys.SpotifyBulkFor(
                    isTrack: request.LookupType == LookupRequestType.SongIdLookup ),
                QueueStreamKeys.SpotifyBulkWorkSignal( ),
                QueuePriority.Bulk );
        }

        return (
            QueueStreamKeys.For( request.Provider, priority ),
            QueueStreamKeys.WorkSignalFor( request.Provider ),
            priority );
    }

    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.LookupDispatchOutboxStaged,
        Level = LogLevel.Debug,
        Message = "Staged lookup dispatch outbox item for saga {SagaId}, provider {Provider}" )]
    private static partial void LogDispatchStaged(
        ILogger logger,
        string sagaId,
        SupportedProviders provider );

    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.LookupDispatchOutboxPublished,
        Level = LogLevel.Debug,
        Message = "Published lookup dispatch outbox item for saga {SagaId}, provider {Provider}, message {MessageId}" )]
    private static partial void LogDispatchPublished(
        ILogger logger,
        string sagaId,
        SupportedProviders provider,
        string messageId );
}
