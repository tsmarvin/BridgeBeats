using System.Globalization;
using BridgeBeats.Contracts.Enums;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Executes the canonical Redis transactions that replace or terminally remove queue deliveries.
/// This component owns the Redis key layout, recovery scheduling, Lua argument order, and result
/// interpretation shared by ordinary and Spotify bulk queues.
/// </summary>
public sealed class RedisQueueDeliveryTransitions( IConnectionMultiplexer redis ) {
    private readonly IConnectionMultiplexer _redis = redis
        ?? throw new ArgumentNullException( nameof( redis ) );

    private const string ReplaceScript = """
        local source = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #source == 0 then return 0 end
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        local groupExists = false
        for i = 1, #groups do
            for j = 1, #groups[i], 2 do
                if groups[i][j] == 'name' and groups[i][j + 1] == ARGV[2] then
                    groupExists = true
                end
            end
        end
        if not groupExists then
            return redis.error_reply('NOGROUP consumer group does not exist')
        end
        local values = source[1][2]
        local recoveryKey = false
        for i = 1, #values, 2 do
            if values[i] == ARGV[7] then recoveryKey = values[i + 1] end
        end
        if recoveryKey and recoveryKey ~= '' then
            if redis.call('EXISTS', recoveryKey) == 0 then
                return 4
            end
            if redis.call('HGET', recoveryKey, 'currentMessageId') ~= ARGV[1] then
                redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
                redis.call('XDEL', KEYS[1], ARGV[1])
                return 2
            end
            if redis.call('HGET', recoveryKey, 'instanceToken') ~= ARGV[9] then
                redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
                redis.call('XDEL', KEYS[1], ARGV[1])
                return 3
            end
        end
        local replacement
        if recoveryKey and recoveryKey ~= '' then
            replacement = redis.call(
                'XADD', KEYS[2], '*', ARGV[3], ARGV[4], ARGV[5], ARGV[6], ARGV[7], recoveryKey)
            redis.call('HSET', recoveryKey,
                'payload', ARGV[4],
                'targetStream', KEYS[2],
                'workChannel', ARGV[12],
                'priority', ARGV[11],
                'enqueueOrigin', ARGV[13],
                'currentMessageId', replacement)
            if ARGV[10] == '' then
                redis.call('HDEL', recoveryKey, 'notBefore')
            else
                redis.call('HSET', recoveryKey, 'notBefore', ARGV[10])
            end
            redis.call('HSET', recoveryKey, 'dispatchPublishedAt', ARGV[14])
            redis.call('ZREM', KEYS[3], recoveryKey)
            redis.call('ZADD', KEYS[4], ARGV[15], recoveryKey)
        else
            replacement = redis.call(
                'XADD', KEYS[2], '*', ARGV[3], ARGV[4], ARGV[5], ARGV[6])
        end
        redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
        redis.call('XDEL', KEYS[1], ARGV[1])
        redis.call('PUBLISH', ARGV[8], replacement)
        return 1
        """;

    private const string AcknowledgeScript = """
        local source = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #source > 0 then
            local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
            local groupExists = false
            for i = 1, #groups do
                for j = 1, #groups[i], 2 do
                    if groups[i][j] == 'name' and groups[i][j + 1] == ARGV[2] then
                        groupExists = true
                    end
                end
            end
            if not groupExists then
                return redis.error_reply('NOGROUP consumer group does not exist')
            end
            local values = source[1][2]
            local recoveryKey = false
            for i = 1, #values, 2 do
                if values[i] == ARGV[3] then recoveryKey = values[i + 1] end
            end
            if recoveryKey and recoveryKey ~= ''
                and redis.call('HGET', recoveryKey, 'currentMessageId') == ARGV[1] then
                redis.call('DEL', recoveryKey)
                redis.call('ZREM', KEYS[2], recoveryKey)
                redis.call('ZREM', KEYS[3], recoveryKey)
            end
        end
        redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
        return redis.call('XDEL', KEYS[1], ARGV[1])
        """;

    internal const string MoveToDlqScript = """
        local source = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #source == 0 then return false end
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        local groupExists = false
        for i = 1, #groups do
            for j = 1, #groups[i], 2 do
                if groups[i][j] == 'name' and groups[i][j + 1] == ARGV[2] then
                    groupExists = true
                end
            end
        end
        if not groupExists then
            return redis.error_reply('NOGROUP consumer group does not exist')
        end
        local values = source[1][2]
        local payload = false
        local enqueuedAt = false
        local recoveryKey = false
        for i = 1, #values, 2 do
            if values[i] == ARGV[3] then payload = values[i + 1] end
            if values[i] == ARGV[4] then enqueuedAt = values[i + 1] end
            if values[i] == ARGV[5] then recoveryKey = values[i + 1] end
        end
        if not payload or not enqueuedAt then return false end
        local replacement
        if recoveryKey and recoveryKey ~= '' then
            replacement = redis.call(
                'XADD', KEYS[2], '*', ARGV[3], payload, ARGV[4], enqueuedAt, ARGV[5], recoveryKey,
                ARGV[6], KEYS[1], ARGV[7], ARGV[8], ARGV[9], ARGV[10])
            if redis.call('HGET', recoveryKey, 'currentMessageId') == ARGV[1] then
                redis.call('DEL', recoveryKey)
                redis.call('ZREM', KEYS[3], recoveryKey)
                redis.call('ZREM', KEYS[4], recoveryKey)
            end
        else
            replacement = redis.call(
                'XADD', KEYS[2], '*', ARGV[3], payload, ARGV[4], enqueuedAt,
                ARGV[6], KEYS[1], ARGV[7], ARGV[8], ARGV[9], ARGV[10])
        end
        redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
        redis.call('XDEL', KEYS[1], ARGV[1])
        return replacement
        """;

    internal const string MovePoisonToDlqScript = """
        local source = redis.call('XRANGE', KEYS[1], ARGV[1], ARGV[1], 'COUNT', 1)
        if #source == 0 then return false end
        local groups = redis.call('XINFO', 'GROUPS', KEYS[1])
        local groupExists = false
        for i = 1, #groups do
            for j = 1, #groups[i], 2 do
                if groups[i][j] == 'name' and groups[i][j + 1] == ARGV[2] then
                    groupExists = true
                end
            end
        end
        if not groupExists then
            return redis.error_reply('NOGROUP consumer group does not exist')
        end
        local values = source[1][2]
        local payload = ''
        local enqueuedAt = ARGV[10]
        local recoveryKey = false
        for i = 1, #values, 2 do
            if values[i] == ARGV[3] then payload = values[i + 1] end
            if values[i] == ARGV[4] then enqueuedAt = values[i + 1] end
            if values[i] == ARGV[5] then recoveryKey = values[i + 1] end
        end
        local replacement
        if recoveryKey and recoveryKey ~= '' then
            replacement = redis.call(
                'XADD', KEYS[2], '*', ARGV[3], payload, ARGV[4], enqueuedAt, ARGV[5], recoveryKey,
                ARGV[6], KEYS[1], ARGV[7], ARGV[8], ARGV[9], ARGV[10])
            if redis.call('HGET', recoveryKey, 'currentMessageId') == ARGV[1] then
                redis.call('DEL', recoveryKey)
                redis.call('ZREM', KEYS[3], recoveryKey)
                redis.call('ZREM', KEYS[4], recoveryKey)
            end
        else
            replacement = redis.call(
                'XADD', KEYS[2], '*', ARGV[3], payload, ARGV[4], enqueuedAt,
                ARGV[6], KEYS[1], ARGV[7], ARGV[8], ARGV[9], ARGV[10])
        end
        redis.call('XACK', KEYS[1], ARGV[2], ARGV[1])
        redis.call('XDEL', KEYS[1], ARGV[1])
        return replacement
        """;

    /// <summary>Atomically replaces one delivery and updates its recovery record when present.</summary>
    public async Task<DeliveryReplacementOutcome> ReplaceAsync(
        string sourceStream,
        string destinationStream,
        string messageId,
        string consumerGroup,
        string payload,
        DateTimeOffset enqueuedAt,
        string workSignalChannel,
        string instanceToken,
        DateTimeOffset? notBefore,
        QueuePriority priority,
        QueueEnqueueOrigin enqueueOrigin,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested( );
        long recoveryAt = GetRecoveryAtUnixMilliseconds( notBefore, enqueuedAt );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            ReplaceScript,
            [
                sourceStream,
                destinationStream,
                RedisLookupDispatchOutbox.PendingIndexKey,
                RedisLookupDispatchOutbox.PublishedRecoveryIndexKey
            ],
            [
                messageId,
                consumerGroup,
                QueueStreamFieldNames.Payload,
                payload,
                QueueStreamFieldNames.EnqueuedAt,
                enqueuedAt.ToString( "O", CultureInfo.InvariantCulture ),
                QueueStreamFieldNames.DispatchOutboxKey,
                workSignalChannel,
                instanceToken,
                notBefore?.ToUnixTimeMilliseconds( ).ToString( CultureInfo.InvariantCulture ) ?? string.Empty,
                ((int)priority).ToString( CultureInfo.InvariantCulture ),
                workSignalChannel,
                ((int)enqueueOrigin).ToString( CultureInfo.InvariantCulture ),
                enqueuedAt.ToUnixTimeMilliseconds( ),
                recoveryAt
            ] );
        return (DeliveryReplacementOutcome)(int)result;
    }

    /// <summary>Acknowledges and deletes a delivery, retiring its matching recovery record.</summary>
    public async Task<bool> AcknowledgeAsync(
        string stream,
        string messageId,
        string consumerGroup,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested( );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            AcknowledgeScript,
            [
                stream,
                RedisLookupDispatchOutbox.PendingIndexKey,
                RedisLookupDispatchOutbox.PublishedRecoveryIndexKey
            ],
            [messageId, consumerGroup, QueueStreamFieldNames.DispatchOutboxKey] );
        return (long)result > 0;
    }

    internal static long GetRecoveryAtUnixMilliseconds(
        DateTimeOffset? notBefore,
        DateTimeOffset now
    ) {
        DateTimeOffset ordinaryRecovery = now.Add( RedisLookupDispatchOutbox.RedriveAfter );
        return (notBefore > ordinaryRecovery ? notBefore.Value : ordinaryRecovery)
            .ToUnixTimeMilliseconds( );
    }
}
