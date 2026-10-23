using System.Globalization;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Redis-backed implementation of <see cref="ISagaStateManager"/> that stores lookup saga state
/// with separate keys for provider states to enable efficient partial updates: a core hash per
/// saga, a hash per provider, and a pending-saga index.
/// </summary>
/// <param name="redis">Redis connection used for all saga operations.</param>
/// <param name="logger">Logger for saga diagnostics.</param>
/// <param name="settings">Queue configuration, used for the per-write TTL.</param>
/// <remarks>
/// A saga's core fields live in hash <c>saga:{sagaId}</c>; each provider's progress lives in
/// <c>saga:{sagaId}:provider:{provider}</c>; sagas awaiting finalization are tracked in the set
/// <c>saga:pending</c>. Every write refreshes the saga's TTL to the configured job-expiration
/// window, so a stalled saga self-expires and can be retried. Saga completeness is not stored: it
/// is computed from the per-provider hashes at read time, so a saga reads as complete when all of
/// its initialized providers report complete. Redis here is transport and working state, not the
/// system of record — final results live in the ATProto PDS, referenced by the URIs stored here.
/// </remarks>
public sealed partial class RedisSagaStateManager(
    IConnectionMultiplexer redis,
    ILogger<RedisSagaStateManager> logger,
    IOptions<QueueSettings> settings
    ) : ISagaStateManager {

    /// <summary>Redis connection used for all saga operations.</summary>
    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );

    /// <summary>Logger for saga diagnostics.</summary>
    private readonly ILogger<RedisSagaStateManager> _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );

    /// <summary>Queue configuration, consulted for the per-write job-expiration TTL.</summary>
    private readonly QueueSettings _settings = settings?.Value ?? throw new ArgumentNullException( nameof( settings ) );

    /// <summary>Key prefix for saga hashes. Literal value: <c>"saga:"</c>.</summary>
    private const string SagaPrefix = "saga:";

    /// <summary>Infix between saga id and provider in a provider key. Literal value: <c>":provider:"</c>.</summary>
    private const string ProviderSuffix = ":provider:";

    /// <summary>Key of the set indexing sagas awaiting finalization. Literal value: <c>"saga:pending"</c>.</summary>
    private const string PendingSagaSetKey = "saga:pending";

    // Hash field names for saga state

    /// <summary>Core hash field: the lookup key. Literal: <c>"lookupKey"</c>.</summary>
    private const string FieldLookupKey = "lookupKey";

    /// <summary>Core hash field: the lookup type. Literal: <c>"lookupType"</c>.</summary>
    private const string FieldLookupType = "lookupType";

    /// <summary>Core hash field: the lookup value. Literal: <c>"lookupValue"</c>.</summary>
    private const string FieldLookupValue = "lookupValue";

    /// <summary>Core hash field: saga creation time (ISO-8601). Literal: <c>"createdAt"</c>.</summary>
    private const string FieldCreatedAt = "createdAt";

    /// <summary>Core hash field: URI of the partial result written while awaiting secondaries. Literal: <c>"partialResultUri"</c>.</summary>
    private const string FieldPartialResultUri = "partialResultUri";

    /// <summary>Core hash field: URI of the final result. Literal: <c>"finalResultUri"</c>.</summary>
    private const string FieldFinalResultUri = "finalResultUri";

    /// <summary>Core hash field: whether the current result is partial. Literal: <c>"isPartial"</c>.</summary>
    private const string FieldIsPartial = "isPartial";

    /// <summary>Core hash field: the provider that first resolved the lookup. Literal: <c>"initialProvider"</c>.</summary>
    private const string FieldInitialProvider = "initialProvider";

    /// <summary>Core hash field: serialized per-provider rate-limit info. Literal: <c>"rateLimitInfo"</c>.</summary>
    private const string FieldRateLimitInfo = "rateLimitInfo";

    /// <summary>Core hash field: the priority the lookup originated at. Literal: <c>"originPriority"</c>.</summary>
    private const string FieldOriginPriority = "originPriority";

    /// <summary>Core hash field: single-winner marker that secondary lookups were queued. Literal: <c>"secondariesQueued"</c>.</summary>
    private const string FieldSecondariesQueued = "secondariesQueued";

    /// <summary>Core hash field: single-winner marker that finalization has been claimed. Literal: <c>"finalizeClaimed"</c>.</summary>
    private const string FieldFinalizeClaimed = "finalizeClaimed";
    private const string FieldFinalizeClaimedAt = "finalizeClaimedAt";

    /// <summary>Core hash field: the highest provider-count already durably written to the PDS. Literal: <c>"writeGeneration"</c>.</summary>
    private const string FieldWriteGeneration = "writeGeneration";
    private const string FieldInstanceToken = "instanceToken";
    private const string TokenGuardedUriScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "redis.call('hset', KEYS[1], ARGV[3], ARGV[4]); redis.call('expire', KEYS[1], tonumber(ARGV[5])); return 1";
    private const string TokenGuardedFinalUriScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "local current = redis.call('hget', KEYS[1], ARGV[3]); " +
        "if current and current ~= '' then if current == ARGV[4] then return 2 else return -1 end end; " +
        "redis.call('hset', KEYS[1], ARGV[3], ARGV[4], ARGV[5], 'False'); " +
        "redis.call('expire', KEYS[1], tonumber(ARGV[6])); return 1";
    private const string TokenGuardedDeleteScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "for i = 1, #KEYS - 1 do redis.call('del', KEYS[i]) end; redis.call('srem', KEYS[#KEYS], ARGV[3]); return 1";
    private const string TokenGuardedReleaseScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "local removed = redis.call('hdel', KEYS[1], ARGV[3]); redis.call('hdel', KEYS[1], ARGV[4]); " +
        "if removed == 1 then redis.call('expire', KEYS[1], tonumber(ARGV[5])) end; return removed";
    private const string TokenGuardedResetScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[4]) ~= ARGV[5] then return 0 end; " +
        "local cur = tonumber(redis.call('hget', KEYS[1], ARGV[1]) or '0'); if cur == tonumber(ARGV[2]) then " +
        "redis.call('hset', KEYS[1], ARGV[1], ARGV[3]); redis.call('expire', KEYS[1], tonumber(ARGV[6])); return 1 end; return 0";
    private const string CreateSagaScript =
        "if redis.call('exists', KEYS[1]) == 1 then " +
        "if ARGV[6] ~= '' and redis.call('hexists', KEYS[1], 'originPriority') == 0 then redis.call('hset', KEYS[1], 'originPriority', ARGV[6]) end; " +
        "redis.call('expire', KEYS[1], tonumber(ARGV[7])); return 0; end; " +
        "redis.call('hset', KEYS[1], 'lookupKey', ARGV[1], 'lookupType', ARGV[2], 'lookupValue', ARGV[3], 'createdAt', ARGV[4], 'partialResultUri', '', 'instanceToken', ARGV[5]); " +
        "if ARGV[6] ~= '' then redis.call('hset', KEYS[1], 'originPriority', ARGV[6]) end; " +
        "redis.call('expire', KEYS[1], tonumber(ARGV[7])); redis.call('sadd', KEYS[2], ARGV[8]); return 1";
    private const string TokenGuardedProviderInitScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "if redis.call('exists', KEYS[2]) == 0 then redis.call('hset', KEYS[2], 'isComplete', 'False', 'isSuccess', 'False', 'resultJson', '', 'completedAt', '', 'errorMessage', ''); end; " +
        "redis.call('expire', KEYS[1], tonumber(ARGV[3])); redis.call('expire', KEYS[2], tonumber(ARGV[3])); return 1";
    private const string TokenGuardedProviderUpdateScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "redis.call('hset', KEYS[2], 'isComplete', ARGV[3], 'isSuccess', ARGV[4], 'resultJson', ARGV[5], 'completedAt', ARGV[6], 'errorMessage', ARGV[7]); " +
        "redis.call('expire', KEYS[1], tonumber(ARGV[8])); redis.call('expire', KEYS[2], tonumber(ARGV[8])); return 1";
    private const string TokenGuardedInitialProviderScript =
        "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
        "redis.call('hset', KEYS[1], ARGV[3], ARGV[4]); redis.call('expire', KEYS[1], tonumber(ARGV[5])); return 1";
    private const string ReplaceLeftoverScript =
        "if redis.call('exists', KEYS[1]) == 0 then return 0 end; " +
        "if redis.call('hget', KEYS[1], 'finalizeClaimed') ~= 'True' and redis.call('hget', KEYS[1], 'finalizeClaimed') ~= 'true' then return 0 end; " +
        "local final = redis.call('hget', KEYS[1], 'finalResultUri'); if final and final ~= '' then return 0 end; " +
        "local claimedAt = redis.call('hget', KEYS[1], 'finalizeClaimedAt'); local claimedAtMs = tonumber(claimedAt or ''); if claimedAtMs and claimedAtMs > tonumber(ARGV[10]) then return 0 end; " +
        "local current = redis.call('hget', KEYS[1], ARGV[1]); if not current or current == '' or current ~= ARGV[2] then return 0 end; " +
        "for i = 2, #KEYS - 1 do redis.call('del', KEYS[i]) end; " +
        "local token = ARGV[3]; redis.call('del', KEYS[1]); redis.call('hset', KEYS[1], 'lookupKey', ARGV[4], 'lookupType', ARGV[5], 'lookupValue', ARGV[6], 'createdAt', ARGV[7], 'partialResultUri', '', 'instanceToken', token); " +
        "if ARGV[8] ~= '' then redis.call('hset', KEYS[1], 'originPriority', ARGV[8]) end; redis.call('expire', KEYS[1], tonumber(ARGV[9])); redis.call('sadd', KEYS[#KEYS], ARGV[11]); return token";
    private static readonly TimeSpan s_finalizerLease = TimeSpan.FromMinutes( 15 );

    // Hash field names for provider state

    /// <summary>Provider hash field: whether the provider has finished. Literal: <c>"isComplete"</c>.</summary>
    private const string FieldIsComplete = "isComplete";

    /// <summary>Provider hash field: whether the provider succeeded. Literal: <c>"isSuccess"</c>.</summary>
    private const string FieldIsSuccess = "isSuccess";

    /// <summary>Provider hash field: serialized provider result. Literal: <c>"resultJson"</c>.</summary>
    private const string FieldResultJson = "resultJson";

    /// <summary>Provider hash field: when the provider completed (ISO-8601). Literal: <c>"completedAt"</c>.</summary>
    private const string FieldCompletedAt = "completedAt";

    /// <summary>Provider hash field: an error message if the provider failed. Literal: <c>"errorMessage"</c>.</summary>
    private const string FieldErrorMessage = "errorMessage";

    /// <summary>
    /// Returns the existing saga for an id, refreshing its TTL, or creates a new one.
    /// </summary>
    /// <param name="sagaId">The deterministic saga id for the lookup.</param>
    /// <param name="lookupKey">The lookup key the saga tracks.</param>
    /// <param name="lookupType">The lookup type.</param>
    /// <param name="lookupValue">The lookup value.</param>
    /// <param name="originPriority">
    /// The priority the lookup originated at. On an existing saga this is set only if not already
    /// present (so the original origin survives a later background re-enqueue); on a new saga it is
    /// stored, defaulting to background when omitted.
    /// </param>
    /// <param name="cancellationToken">Token forwarded to nested reads.</param>
    /// <returns>The existing or newly created <see cref="LookupSagaState"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sagaId"/>, <paramref name="lookupKey"/>, or <paramref name="lookupValue"/> is null or whitespace.</exception>
    public async Task<LookupSagaState> GetOrCreateAsync(
        string sagaId,
        string lookupKey,
        LookupRequestType lookupType,
        string lookupValue,
        QueuePriority? originPriority = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( lookupKey );
        ArgumentException.ThrowIfNullOrWhiteSpace( lookupValue );

        string key = GetSagaKey( sagaId );
        IDatabase db = _redis.GetDatabase( );
        TimeSpan ttl = TimeSpan.FromMinutes( _settings.JobExpirationMinutes );

        string instanceToken = Guid.NewGuid( ).ToString( "N" );
        RedisResult created = await db.ScriptEvaluateAsync(
            CreateSagaScript,
            [key, PendingSagaSetKey],
            [lookupKey, lookupType.ToString( ), lookupValue, DateTimeOffset.UtcNow.ToString( "O" ), instanceToken,
                originPriority?.ToString( ) ?? "", (long)ttl.TotalSeconds, sagaId] );

        if ((int)created == 0) {
            RedisKey[] replacementKeys = [key, .. Enum.GetValues<SupportedProviders>( ).Select( provider => (RedisKey)GetProviderKey( sagaId, provider ) ), PendingSagaSetKey];
            string replacementToken = Guid.NewGuid( ).ToString( "N" );
            RedisResult replacement = await db.ScriptEvaluateAsync(
                ReplaceLeftoverScript,
                replacementKeys,
                [FieldInstanceToken, (RedisValue)(await db.HashGetAsync( key, FieldInstanceToken )), replacementToken,
                    lookupKey, lookupType.ToString( ), lookupValue, DateTimeOffset.UtcNow.ToString( "O" ),
                    originPriority?.ToString( ) ?? "", (long)ttl.TotalSeconds,
                    DateTimeOffset.UtcNow.Subtract( s_finalizerLease ).ToUnixTimeMilliseconds( ), sagaId] );
            if (string.Equals( replacement.ToString( ), replacementToken, StringComparison.Ordinal )) {
                QueueMetrics.RecordSagaLifecycleOutcome( "stale_saga_replaced" );
            }

            LookupSagaState? existingState = await GetAsync( sagaId, cancellationToken );

            if (existingState is not null) {
                LogSagaResumed( _logger, sagaId );
                return existingState;
            }
        }

        // Create new saga. finalResultUri is intentionally not pre-populated so the
        // final URI compare-and-set can enforce first-writer-wins.
        // CreateSagaScript returned "already exists" or created a hash that became unreadable
        // before the hot read. Either way, retrying the same deterministic delivery cannot
        // repair the persisted state and would pin the provider's PEL forever.
        LookupSagaState? createdState = await GetAsync( sagaId, cancellationToken ) ?? throw new UnreadableSagaStateException( sagaId );
        LogSagaCreated( _logger, sagaId, lookupType, lookupValue );
        return createdState;
    }

    /// <summary>
    /// Reads a saga's full state, including each provider's progress.
    /// </summary>
    /// <param name="sagaId">The saga id to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The reconstructed <see cref="LookupSagaState"/>, or null if the saga hash is absent or its
    /// required core fields are missing or invalid.
    /// </returns>
    /// <remarks>
    /// Origin priority defaults to background when the stored value is missing or unparseable. The
    /// returned state's completeness is derived from the loaded provider states, not read from a
    /// stored flag.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sagaId"/> is null or whitespace.</exception>
    public async Task<LookupSagaState?> GetAsync( string sagaId, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );

        string key = GetSagaKey( sagaId );
        IDatabase db = _redis.GetDatabase( );

        HashEntry[] sagaHash = await db.HashGetAllAsync( key );

        if (sagaHash.Length == 0) {
            return null;
        }

        Dictionary<string, string> fields = sagaHash.ToDictionary(
            h => h.Name.ToString( ),
            h => h.Value.ToString( )
        );

        // Parse core saga state
        if (!fields.TryGetValue( FieldLookupKey, out string? lookupKey ) ||
            !fields.TryGetValue( FieldLookupType, out string? lookupTypeStr ) ||
            !fields.TryGetValue( FieldLookupValue, out string? lookupValue )) {
            LogIncompleteCoreState( _logger, sagaId );
            throw new UnreadableSagaStateException( sagaId );
        }

        if (!Enum.TryParse<LookupRequestType>( lookupTypeStr, out LookupRequestType lookupType )) {
            LogInvalidLookupType( _logger, sagaId, lookupTypeStr );
            throw new UnreadableSagaStateException( sagaId );
        }

        _ = fields.TryGetValue( FieldCreatedAt, out string? createdAtStr );
        _ = fields.TryGetValue( FieldPartialResultUri, out string? partialUri );
        _ = fields.TryGetValue( FieldFinalResultUri, out string? finalUri );
        _ = fields.TryGetValue( FieldIsPartial, out string? isPartialStr );
        _ = fields.TryGetValue( FieldInitialProvider, out string? initialProviderStr );
        _ = fields.TryGetValue( FieldRateLimitInfo, out string? rateLimitInfoJson );
        _ = fields.TryGetValue( FieldOriginPriority, out string? originPriorityStr );
        _ = fields.TryGetValue( FieldWriteGeneration, out string? writeGenerationStr );
        if (!fields.TryGetValue( FieldInstanceToken, out string? instanceToken )
            || string.IsNullOrWhiteSpace( instanceToken )) {
            LogIncompleteCoreState( _logger, sagaId );
            throw new UnreadableSagaStateException( sagaId );
        }

        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty( createdAtStr )
            && !DateTimeOffset.TryParse(
                createdAtStr,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out createdAt )) {
            LogIncompleteCoreState( _logger, sagaId );
            throw new UnreadableSagaStateException( sagaId );
        }

        _ = bool.TryParse( isPartialStr, out bool isPartial );

        SupportedProviders? initialProvider = !string.IsNullOrEmpty( initialProviderStr ) &&
            Enum.TryParse<SupportedProviders>( initialProviderStr, out SupportedProviders parsedProvider )
                ? parsedProvider
                : null;

        List<ProviderRateLimitInfo>? rateLimitInfo = !string.IsNullOrEmpty( rateLimitInfoJson )
            ? System.Text.Json.JsonSerializer.Deserialize<List<ProviderRateLimitInfo>>( rateLimitInfoJson )
            : null;

        // Missing field (sagas persisted before this field existed) defaults to Background
        // so old sagas are never promoted to interactive.
        QueuePriority originPriority = !string.IsNullOrEmpty( originPriorityStr ) &&
            Enum.TryParse<QueuePriority>( originPriorityStr, out QueuePriority parsedPriority )
                ? parsedPriority
                : QueuePriority.Background;

        // Pre-existing sagas that have no writeGeneration field default to 0, so the first
        // write against them advances normally.
        _ = int.TryParse( writeGenerationStr, out int writeGeneration );

        // Load provider states
        Dictionary<SupportedProviders, ProviderLookupState> providerStates = await LoadProviderStatesAsync( db, sagaId );

        return new LookupSagaState {
            InstanceToken = instanceToken,
            SagaId = sagaId,
            LookupKey = lookupKey,
            LookupType = lookupType,
            LookupValue = lookupValue,
            CreatedAt = createdAt,
            ProviderStates = providerStates,
            PartialResultUri = string.IsNullOrEmpty( partialUri ) ? null : partialUri,
            FinalResultUri = string.IsNullOrEmpty( finalUri ) ? null : finalUri,
            IsPartial = isPartial,
            InitialProvider = initialProvider,
            RateLimitInfo = rateLimitInfo,
            OriginPriority = originPriority,
            WriteGeneration = writeGeneration
        };
    }

    /// <inheritdoc/>
    public async Task<bool> TryUpdateProviderStateAsync( string sagaId, ProviderLookupState state, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        ArgumentNullException.ThrowIfNull( state );
        long ttl = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedProviderUpdateScript,
            [GetSagaKey( sagaId ), GetProviderKey( sagaId, state.Provider )],
            [FieldInstanceToken, expectedInstanceToken, state.IsComplete.ToString( ), state.IsSuccess.ToString( ), state.ResultJson ?? "", state.CompletedAt?.ToString( "O" ) ?? "", state.ErrorMessage ?? "", ttl] );
        return (int)result == 1;
    }

    /// <summary>Stores a partial URI only when the saga instance token still matches.</summary>
    public async Task<bool> TrySetPartialResultUriAsync( string sagaId, string uri, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( uri );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedUriScript,
            [GetSagaKey( sagaId )],
            [FieldInstanceToken, expectedInstanceToken, FieldPartialResultUri, uri,
                (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds] );
        return (int)result == 1;
    }

    /// <summary>Stores a final URI only when the saga instance token still matches.</summary>
    public async Task<SagaFinalResultWriteOutcome> TrySetFinalResultUriAsync(
        string sagaId,
        string uri,
        string expectedInstanceToken,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( uri );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedFinalUriScript,
            [GetSagaKey( sagaId )],
            [FieldInstanceToken, expectedInstanceToken, FieldFinalResultUri, uri, FieldIsPartial,
                (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds] );
        SagaFinalResultWriteOutcome outcome = (SagaFinalResultWriteOutcome)(int)result;
        if (outcome is SagaFinalResultWriteOutcome.Stored or SagaFinalResultWriteOutcome.Idempotent) {
            LogFinalResultUriSet( _logger, sagaId, uri );
        }
        return outcome;
    }

    /// <summary>Deletes a saga only when its instance token still matches.</summary>
    public async Task<bool> TryDeleteAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        RedisKey[] keys = [GetSagaKey( sagaId ), .. Enum.GetValues<SupportedProviders>( ).Select( provider => (RedisKey)GetProviderKey( sagaId, provider ) ), PendingSagaSetKey];
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedDeleteScript,
            keys,
            [FieldInstanceToken, expectedInstanceToken, sagaId] );
        return (int)result == 1;
    }

    /// <summary>Sets partial state only when the saga instance token still matches.</summary>
    public async Task<bool> TrySetIsPartialAsync( string sagaId, bool isPartial, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedUriScript,
            [GetSagaKey( sagaId )],
            [FieldInstanceToken, expectedInstanceToken, FieldIsPartial, isPartial.ToString( ),
                (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds] );
        return (int)result == 1;
    }

    /// <summary>Sets the initial provider only while the saga instance token still owns the hash.</summary>
    public async Task<bool> TrySetInitialProviderAsync(
        string sagaId,
        SupportedProviders provider,
        string expectedInstanceToken,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedInitialProviderScript,
            [GetSagaKey( sagaId )],
            [FieldInstanceToken, expectedInstanceToken, FieldInitialProvider, provider.ToString( ), ttlSeconds] );
        return (int)result == 1;
    }

    /// <summary>Stores rate-limit information only when the saga instance token still matches.</summary>
    public async Task<bool> TrySetRateLimitInfoAsync( string sagaId, List<ProviderRateLimitInfo> rateLimitInfo, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentNullException.ThrowIfNull( rateLimitInfo );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        string json = System.Text.Json.JsonSerializer.Serialize( rateLimitInfo );
        TimeSpan ttl = TimeSpan.FromMinutes( _settings.JobExpirationMinutes );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; redis.call('hset', KEYS[1], ARGV[3], ARGV[4]); redis.call('expire', KEYS[1], ARGV[5]); return 1",
            [GetSagaKey( sagaId )], [FieldInstanceToken, expectedInstanceToken, FieldRateLimitInfo, json, (long)ttl.TotalSeconds] );
        bool updated = (int)result == 1;
        if (updated) LogRateLimitInfoSet( _logger, sagaId, rateLimitInfo.Count );
        return updated;
    }

    /// <summary>Marks secondary fan-out only when the saga instance token still matches.</summary>
    public async Task<bool> TryMarkSecondariesQueuedAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
            "if redis.call('hexists', KEYS[1], ARGV[3]) == 1 then return 0 end; " +
            "redis.call('hset', KEYS[1], ARGV[3], 'True'); redis.call('expire', KEYS[1], tonumber(ARGV[4])); return 1",
            [GetSagaKey( sagaId )], [FieldInstanceToken, expectedInstanceToken, FieldSecondariesQueued, ttlSeconds] );
        return (int)result == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> TryPromoteToInteractiveAsync(
        string sagaId,
        string expectedInstanceToken,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            "if redis.call('exists', KEYS[1]) == 0 or redis.call('hget', KEYS[1], ARGV[1]) ~= ARGV[2] then return 0 end; " +
            "if redis.call('hget', KEYS[1], ARGV[3]) == ARGV[4] then return 0 end; " +
            "redis.call('hset', KEYS[1], ARGV[3], ARGV[4]); redis.call('expire', KEYS[1], ARGV[5]); return 1",
            [GetSagaKey( sagaId )],
            [FieldInstanceToken, expectedInstanceToken, FieldOriginPriority,
                QueuePriority.Interactive.ToString( ),
                (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds] );
        return (int)result == 1;
    }

    /// <summary>Claims finalization against an expected saga instance token.</summary>
    public async Task<bool> TryClaimFinalizeAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        return await TryClaimFinalizeCoreAsync( sagaId, expectedInstanceToken, cancellationToken );
    }

    private async Task<bool> TryClaimFinalizeCoreAsync( string sagaId, RedisValue token, CancellationToken cancellationToken ) {
        cancellationToken.ThrowIfCancellationRequested( );
        string key = GetSagaKey( sagaId );
        IDatabase db = _redis.GetDatabase( );
        TimeSpan ttl = TimeSpan.FromMinutes( _settings.JobExpirationMinutes );
        RedisResult claimResult = await db.ScriptEvaluateAsync(
            "if redis.call('exists', KEYS[1]) == 0 then return 0 end; " +
            "local current = redis.call('hget', KEYS[1], ARGV[1]); if not current or current == '' or current ~= ARGV[2] then return 0 end; " +
            "if redis.call('hexists', KEYS[1], ARGV[3]) == 1 then return 0 end; " +
            "redis.call('hset', KEYS[1], ARGV[3], 'True'); redis.call('hset', KEYS[1], ARGV[4], ARGV[5]); " +
            "redis.call('expire', KEYS[1], ARGV[6]); return 1",
            [key], [FieldInstanceToken, token, FieldFinalizeClaimed, FieldFinalizeClaimedAt,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (long)ttl.TotalSeconds]);
        bool acquired = (int)claimResult == 1;

        LogFinalizeClaimMarker( _logger, sagaId, acquired );

        return acquired;
    }

    /// <summary>Releases finalization only when the saga instance token still matches.</summary>
    public async Task<bool> TryReleaseFinalizeClaimAsync( string sagaId, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
            TokenGuardedReleaseScript,
            [GetSagaKey( sagaId )],
            [FieldInstanceToken, expectedInstanceToken, FieldFinalizeClaimed, FieldFinalizeClaimedAt, ttlSeconds] );
        return (int)result == 1;
    }

    /// <summary>Advances write generation against an expected saga instance token.</summary>
    public async Task<bool> TryAdvanceWriteGenerationAsync( string sagaId, int generation, string expectedInstanceToken, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        ArgumentOutOfRangeException.ThrowIfLessThan( generation, 1 );
        return await TryAdvanceWriteGenerationCoreAsync( sagaId, generation, expectedInstanceToken, cancellationToken );
    }

    private async Task<bool> TryAdvanceWriteGenerationCoreAsync( string sagaId, int generation, RedisValue token, CancellationToken ct ) {
        ct.ThrowIfCancellationRequested( );
        string key = GetSagaKey(sagaId);
        IDatabase db = _redis.GetDatabase( );
        TimeSpan ttl = TimeSpan.FromMinutes(_settings.JobExpirationMinutes);
        RedisResult result = await db.ScriptEvaluateAsync(
            AdvanceWriteGenerationScript,
            keys: [key],
            values: [FieldWriteGeneration, generation, FieldInstanceToken, token]
        );

        bool advanced = (int)result == 1;

        if (advanced) {
            _ = await db.KeyExpireAsync( key, ttl );
            LogWriteGenerationAdvanced( _logger, sagaId, generation );
        }

        return advanced;
    }

    /// <summary>
    /// Lua script that advances the write-generation field only when the stored value is strictly
    /// less than the requested generation. Returns 1 on advance, 0 when the stored value is
    /// already at or above the requested generation.
    /// </summary>
    private const string AdvanceWriteGenerationScript =
        "if redis.call('exists', KEYS[1]) == 0 then return 0 end; " +
        "local token = redis.call('hget', KEYS[1], ARGV[3]); if not token or token == '' or token ~= ARGV[4] then return 0 end; " +
        "local cur = tonumber(redis.call('hget', KEYS[1], ARGV[1]) or '0'); " +
        "if cur < tonumber(ARGV[2]) then redis.call('hset', KEYS[1], ARGV[1], ARGV[2]); return 1 " +
        "else return 0 end";

    /// <summary>Resets write generation only when the saga instance token still matches.</summary>
    public async Task<bool> TryResetWriteGenerationAsync( string sagaId, int advancedTo, int priorGeneration, string expectedInstanceToken, CancellationToken ct = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        ArgumentOutOfRangeException.ThrowIfLessThan( advancedTo, 1 );
        ArgumentOutOfRangeException.ThrowIfLessThan( priorGeneration, 0 );
        RedisResult result = await _redis.GetDatabase( ).ScriptEvaluateAsync(
        TokenGuardedResetScript,
        [GetSagaKey( sagaId )],
        [FieldWriteGeneration, advancedTo, priorGeneration, FieldInstanceToken, expectedInstanceToken,
            (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds] );
        return (int)result == 1;
    }

    /// <inheritdoc/>
    public async Task<bool> TryInitializeProviderStatesAsync(
        string sagaId,
        IEnumerable<SupportedProviders> providers,
        string expectedInstanceToken,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );
        ArgumentException.ThrowIfNullOrWhiteSpace( expectedInstanceToken );
        ArgumentNullException.ThrowIfNull( providers );
        IDatabase db = _redis.GetDatabase( );
        long ttlSeconds = (long)TimeSpan.FromMinutes( _settings.JobExpirationMinutes ).TotalSeconds;
        foreach (SupportedProviders provider in providers.Distinct( )) {
            RedisResult result = await db.ScriptEvaluateAsync(
                TokenGuardedProviderInitScript,
                [GetSagaKey( sagaId ), GetProviderKey( sagaId, provider )],
                [FieldInstanceToken, expectedInstanceToken, ttlSeconds] );
            if ((int)result != 1) {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Loads the per-provider states for a saga by reading each provider's hash.
    /// </summary>
    /// <param name="db">The Redis database to read from.</param>
    /// <param name="sagaId">The saga whose provider states are loaded.</param>
    /// <returns>A map of provider to its loaded state; providers with no hash are omitted.</returns>
    private static async Task<Dictionary<SupportedProviders, ProviderLookupState>> LoadProviderStatesAsync(
        IDatabase db,
        string sagaId
    ) {
        Dictionary<SupportedProviders, ProviderLookupState> states = [];

        foreach (SupportedProviders provider in Enum.GetValues<SupportedProviders>( )) {
            string key = GetProviderKey( sagaId, provider );
            HashEntry[] hash = await db.HashGetAllAsync( key );

            if (hash.Length == 0) {
                continue;
            }

            Dictionary<string, string> fields = hash.ToDictionary(
                h => h.Name.ToString( ),
                h => h.Value.ToString( )
            );

            _ = bool.TryParse( fields.GetValueOrDefault( FieldIsComplete, "false" ), out bool isComplete );
            _ = bool.TryParse( fields.GetValueOrDefault( FieldIsSuccess, "false" ), out bool isSuccess );

            string? resultJson = fields.GetValueOrDefault( FieldResultJson );
            string? completedAtStr = fields.GetValueOrDefault( FieldCompletedAt );
            string? errorMessage = fields.GetValueOrDefault( FieldErrorMessage );

            DateTimeOffset? completedAt = null;
            if (!string.IsNullOrEmpty( completedAtStr )) {
                if (!DateTimeOffset.TryParse(
                    completedAtStr,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedCompletedAt )) {
                    throw new UnreadableSagaStateException( sagaId );
                }
                completedAt = parsedCompletedAt;
            }

            states[provider] = new ProviderLookupState(
                Provider: provider,
                IsComplete: isComplete,
                IsSuccess: isSuccess,
                ResultJson: string.IsNullOrEmpty( resultJson ) ? null : resultJson,
                CompletedAt: completedAt,
                ErrorMessage: string.IsNullOrEmpty( errorMessage ) ? null : errorMessage
            );
        }

        return states;
    }

    /// <summary>
    /// Sweeps the pending index for sagas that have completed but were never finalized, pruning
    /// stale index entries as it goes.
    /// </summary>
    /// <param name="minimumAge">Only sagas older than this are returned, to avoid racing in-flight work.</param>
    /// <param name="limit">Maximum number of sagas to return.</param>
    /// <param name="cancellationToken">Token forwarded to nested reads.</param>
    /// <returns>Completed-but-unfinalized sagas, up to <paramref name="limit"/>.</returns>
    /// <remarks>
    /// This is the coordinator's safety net for completion events dropped by Redis Pub/Sub (which is
    /// at-most-once). Index entries whose saga hash no longer exists, and entries that are already
    /// no longer exist are removed during the sweep. Finalized sagas remain indexed until durable
    /// waiter notification succeeds, so that post-durability recovery is independently retryable.
    /// </remarks>
    public async Task<IReadOnlyList<LookupSagaState>> GetPendingReconciliationAsync(
        TimeSpan minimumAge,
        int limit = 100,
        CancellationToken cancellationToken = default
    ) {
        IDatabase db = _redis.GetDatabase( );
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - minimumAge;
        List<LookupSagaState> result = [];

        // Get all saga IDs from the pending set
        RedisValue[] pendingSagaIds = await db.SetMembersAsync( PendingSagaSetKey );

        foreach (RedisValue sagaIdValue in pendingSagaIds) {
            if (result.Count >= limit) {
                break;
            }

            string sagaId = sagaIdValue.ToString( );
            LookupSagaState? saga;
            try {
                saga = await GetAsync( sagaId, cancellationToken );
            } catch (UnreadableSagaStateException) {
                // A poison hash cannot be reconciled, but it must not abort the remainder of the
                // safety-net sweep. GetAsync already logs the malformed state; remove only the
                // index membership so healthy sagas continue to make progress.
                _ = await db.SetRemoveAsync( PendingSagaSetKey, sagaId );
                continue;
            }

            if (saga is null) {
                // Saga expired or was deleted - clean up the index
                _ = await db.SetRemoveAsync( PendingSagaSetKey, sagaId );
                continue;
            }

            // A finalized saga intentionally remains indexed until its deduplication completion
            // notification has been published. Returning it lets the coordinator retry that
            // independently idempotent post-durability step after a crash or Redis interruption.
            if (!string.IsNullOrEmpty( saga.FinalResultUri )) {
                if (saga.CreatedAt <= cutoff) {
                    result.Add( saga );
                }
                continue;
            }

            // Skip if too young (avoid race with Pub/Sub)
            if (saga.CreatedAt > cutoff) {
                continue;
            }

            // Check if all providers are complete
            if (!saga.IsComplete) {
                continue;
            }

            result.Add( saga );
        }

        if (result.Count > 0) {
            LogUnfinalizedSagasFound( _logger, result.Count );
        }

        return result;
    }

    /// <summary>
    /// Adds a saga to the pending-finalization index.
    /// </summary>
    /// <param name="sagaId">The saga to index.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>A task that completes once the saga is added to the index.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sagaId"/> is null or whitespace.</exception>
    public async Task AddToPendingIndexAsync( string sagaId, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );

        IDatabase db = _redis.GetDatabase( );
        _ = await db.SetAddAsync( PendingSagaSetKey, sagaId );

        LogAddedToPendingIndex( _logger, sagaId );
    }

    /// <summary>
    /// Removes a saga from the pending-finalization index.
    /// </summary>
    /// <param name="sagaId">The saga to de-index.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>A task that completes once the saga is removed from the index.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sagaId"/> is null or whitespace.</exception>
    public async Task RemoveFromPendingIndexAsync( string sagaId, CancellationToken cancellationToken = default ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( sagaId );

        IDatabase db = _redis.GetDatabase( );
        _ = await db.SetRemoveAsync( PendingSagaSetKey, sagaId );

        LogRemovedFromPendingIndex( _logger, sagaId );
    }

    /// <summary>Builds the core saga hash key: <c>saga:{sagaId}</c>.</summary>
    /// <param name="sagaId">The saga id.</param>
    /// <returns>The core saga key.</returns>
    private static string GetSagaKey( string sagaId ) => $"{SagaPrefix}{sagaId}";

    /// <summary>Builds a provider hash key: <c>saga:{sagaId}:provider:{provider}</c>.</summary>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="provider">The provider.</param>
    /// <returns>The provider key.</returns>
    private static string GetProviderKey( string sagaId, SupportedProviders provider ) =>
        $"{SagaPrefix}{sagaId}{ProviderSuffix}{provider}";

    #region LoggerMessage Methods

    /// <summary>Logs that an existing saga was resumed rather than created.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The resumed saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerResumed,
        Level = LogLevel.Debug,
        Message = "Resumed existing saga {SagaId}" )]
    internal static partial void LogSagaResumed( ILogger logger, string sagaId );

    /// <summary>Logs that a new saga was created.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The new saga id.</param>
    /// <param name="lookupType">The lookup type.</param>
    /// <param name="lookupValue">The lookup value.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerCreated,
        Level = LogLevel.Information,
        Message = "Created new saga {SagaId} for lookup {LookupType}:{LookupValue}" )]
    internal static partial void LogSagaCreated( ILogger logger, string sagaId, LookupRequestType lookupType, string lookupValue );

    /// <summary>Logs that a saga hash was missing required core fields and could not be read.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The affected saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerIncompleteCoreState,
        Level = LogLevel.Warning,
        Message = "Saga {SagaId} has incomplete core state" )]
    internal static partial void LogIncompleteCoreState( ILogger logger, string sagaId );

    /// <summary>Logs that a saga stored an unparseable lookup type.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The affected saga id.</param>
    /// <param name="type">The invalid lookup-type string.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerInvalidLookupType,
        Level = LogLevel.Warning,
        Message = "Saga {SagaId} has invalid lookup type: {Type}" )]
    internal static partial void LogInvalidLookupType( ILogger logger, string sagaId, string type );

    /// <summary>Logs that a provider's state was updated.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="provider">The provider updated.</param>
    /// <param name="complete">Whether the provider is now complete.</param>
    /// <param name="success">Whether the provider succeeded.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerProviderStateUpdated,
        Level = LogLevel.Debug,
        Message = "Updated provider state for saga {SagaId}, provider {Provider}: complete={Complete}, success={Success}" )]
    internal static partial void LogProviderStateUpdated( ILogger logger, string sagaId, SupportedProviders provider, bool complete, bool success );

    /// <summary>Logs that the partial-result URI was set.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="uri">The partial-result URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerPartialResultUriSet,
        Level = LogLevel.Information,
        Message = "Set partial result URI for saga {SagaId}: {Uri}" )]
    internal static partial void LogPartialResultUriSet( ILogger logger, string sagaId, string uri );

    /// <summary>Logs that the final-result URI was set.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="uri">The final-result URI.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerFinalResultUriSet,
        Level = LogLevel.Information,
        Message = "Set final result URI for saga {SagaId}: {Uri}" )]
    internal static partial void LogFinalResultUriSet( ILogger logger, string sagaId, string uri );

    /// <summary>Logs that a saga was deleted.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The deleted saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerDeleted,
        Level = LogLevel.Information,
        Message = "Deleted saga {SagaId}" )]
    internal static partial void LogSagaDeleted( ILogger logger, string sagaId );

    /// <summary>Logs that the partial flag was set.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="isPartial">The new partial flag value.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerIsPartialSet,
        Level = LogLevel.Debug,
        Message = "Set isPartial={IsPartial} for saga {SagaId}" )]
    internal static partial void LogIsPartialSet( ILogger logger, bool isPartial, string sagaId );

    /// <summary>Logs that the initial provider was set.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The initial provider.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerInitialProviderSet,
        Level = LogLevel.Debug,
        Message = "Set initialProvider={Provider} for saga {SagaId}" )]
    internal static partial void LogInitialProviderSet( ILogger logger, SupportedProviders provider, string sagaId );

    /// <summary>Logs that rate-limit info was stored.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="count">The number of rate-limited providers recorded.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerRateLimitInfoSet,
        Level = LogLevel.Debug,
        Message = "Set rateLimitInfo for saga {SagaId} with {Count} rate-limited providers" )]
    internal static partial void LogRateLimitInfoSet( ILogger logger, string sagaId, int count );

    /// <summary>Logs that provider states were initialized for a saga.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="count">The number of providers initialized.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerProviderStatesInitialized,
        Level = LogLevel.Debug,
        Message = "Initialized provider states for saga {SagaId} with {Count} providers" )]
    internal static partial void LogProviderStatesInitialized( ILogger logger, string sagaId, int count );

    /// <summary>Logs how many completed-but-unfinalized sagas the polling sweep found.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="count">The number of sagas found.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerUnfinalizedSagasFound,
        Level = LogLevel.Information,
        Message = "Found {Count} completed but unfinalized sagas during polling" )]
    internal static partial void LogUnfinalizedSagasFound( ILogger logger, int count );

    /// <summary>Logs that a saga was added to the pending index.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The indexed saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerAddedToPendingIndex,
        Level = LogLevel.Debug,
        Message = "Added saga {SagaId} to pending index" )]
    internal static partial void LogAddedToPendingIndex( ILogger logger, string sagaId );

    /// <summary>Logs that a saga was removed from the pending index.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The de-indexed saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerRemovedFromPendingIndex,
        Level = LogLevel.Debug,
        Message = "Removed saga {SagaId} from pending index" )]
    internal static partial void LogRemovedFromPendingIndex( ILogger logger, string sagaId );

    /// <summary>Logs that the origin priority was set on first write.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="priority">The origin priority.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerOriginPrioritySet,
        Level = LogLevel.Debug,
        Message = "Set originPriority={Priority} for saga {SagaId}" )]
    internal static partial void LogOriginPrioritySet( ILogger logger, QueuePriority priority, string sagaId );

    /// <summary>Logs the outcome of the atomic secondaries-queued claim.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="acquired">Whether this caller won the claim.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerSecondariesQueuedMarker,
        Level = LogLevel.Debug,
        Message = "Secondaries-queued marker for saga {SagaId}: acquired={Acquired}" )]
    internal static partial void LogSecondariesQueuedMarker( ILogger logger, string sagaId, bool acquired );

    /// <summary>Logs the outcome of the atomic finalize claim attempt.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="acquired">Whether this caller won the claim.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerFinalizeClaimMarker,
        Level = LogLevel.Debug,
        Message = "Finalize claim for saga {SagaId}: acquired={Acquired}" )]
    internal static partial void LogFinalizeClaimMarker( ILogger logger, string sagaId, bool acquired );

    /// <summary>Logs that a finalize claim was released so a retry can re-finalize.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerFinalizeClaimReleased,
        Level = LogLevel.Debug,
        Message = "Finalize claim released for saga {SagaId}" )]
    internal static partial void LogFinalizeClaimReleased( ILogger logger, string sagaId );

    /// <summary>Logs that the write generation was advanced for a saga.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="generation">The generation advanced to.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerWriteGenerationAdvanced,
        Level = LogLevel.Debug,
        Message = "Write generation advanced to {Generation} for saga {SagaId}" )]
    internal static partial void LogWriteGenerationAdvanced( ILogger logger, string sagaId, int generation );

    /// <summary>Logs the outcome of a conditional write-generation reset attempt after a pre-durability failure.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="sagaId">The saga id.</param>
    /// <param name="advancedTo">The generation this caller had advanced to (the expected stored value).</param>
    /// <param name="priorGeneration">The generation to restore.</param>
    /// <param name="reset">Whether the conditional reset succeeded (false means a concurrent handler advanced further).</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisSagaStateManagerWriteGenerationReset,
        Level = LogLevel.Debug,
        Message = "Write generation conditional reset from {AdvancedTo} to {PriorGeneration} for saga {SagaId}: reset={Reset}" )]
    internal static partial void LogWriteGenerationReset( ILogger logger, string sagaId, int advancedTo, int priorGeneration, bool reset );

    #endregion
}
