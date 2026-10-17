using System.Collections.Concurrent;
using System.Diagnostics;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Infrastructure.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Queue;

/// <summary>
/// Redis-backed implementation of <see cref="IRateLimitTracker"/> that tracks policy-derived
/// rate-limit windows with TTL-based automatic expiration.
/// </summary>
/// <param name="redis">Redis connection used to read and write rate-limit keys.</param>
/// <param name="settings">Queue settings that bound provider-controlled cooldown windows.</param>
/// <param name="logger">Logger for rate-limit diagnostics.</param>
/// <remarks>
/// Each rate-limit scope is stored under key <c>ratelimit:{provider}:{tracking-key}</c>. Tracking
/// keys are normalized and lower-cased; Spotify data endpoints collapse to the provider key while
/// its authentication endpoint remains separate. The value is the ISO-8601 ("O" round-trip) Retry-After
/// timestamp and the key's TTL is set to the remaining window, so entries self-clear when the
/// limit expires; <see cref="ClearAsync"/> clears them early after a successful request. The
/// tracker is the source consulted by the rate-limit-aware dequeue to skip messages whose
/// endpoint is currently throttled.
/// </remarks>
public sealed partial class RedisRateLimitTracker(
    IConnectionMultiplexer redis,
    IOptions<QueueSettings> settings,
    ILogger<RedisRateLimitTracker> logger
) : IRateLimitTracker {

    /// <summary>Redis connection used for all rate-limit key operations.</summary>
    private readonly IConnectionMultiplexer _redis = redis
                                                   ?? throw new ArgumentNullException( nameof( redis ) );

    /// <summary>Logger for rate-limit diagnostics.</summary>
    private readonly ILogger<RedisRateLimitTracker> _logger = logger
                                                            ?? throw new ArgumentNullException( nameof( logger ) );
    private readonly QueueSettings _settings = (settings
        ?? throw new ArgumentNullException( nameof( settings ) )).Value;

    /// <summary>Key prefix for all rate-limit entries. Literal value: <c>"ratelimit:"</c>.</summary>
    private const string RateLimitPrefix = "ratelimit:";
    private const string RateLimitIndexPrefix = "ratelimit:active:";
    private const string SetRateLimitScript = """
        local current = redis.call('ZSCORE', KEYS[2], ARGV[3])
        if current and tonumber(current) >= tonumber(ARGV[4]) then
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        redis.call('ZADD', KEYS[2], ARGV[4], ARGV[3])
        return 1
        """;
    private const string ClearRateLimitScript = """
        local deleted = redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        return deleted
        """;
    private static readonly TimeSpan s_activeEndpointCacheDuration = TimeSpan.FromMilliseconds( 250 );
    private readonly ConcurrentDictionary<SupportedProviders, ActiveEndpointCache> _activeEndpointCache = [];
    private sealed record ActiveEndpointCache(
        DateTimeOffset ValidUntil,
        IReadOnlyList<RateLimitedEndpoint> Endpoints );

    /// <summary>
    /// Reads the current rate-limit state for one provider endpoint.
    /// </summary>
    /// <param name="provider">The provider to query.</param>
    /// <param name="endpoint">The endpoint to query.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>
    /// The current <see cref="RateLimitState"/>. Not rate-limited when no key exists, the stored
    /// value is unparseable (in which case the bad key is deleted), or the window has already
    /// elapsed (in which case the expired key is deleted).
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is null or whitespace.</exception>
    public async Task<RateLimitState> GetStateAsync(
        SupportedProviders provider,
        string endpoint,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( endpoint );

        string normalizedEndpoint = NormalizeEndpoint( provider, endpoint );
        string key = GetKey( provider, normalizedEndpoint );
        IDatabase db = _redis.GetDatabase( );

        RedisValue value = await db.StringGetAsync( key );

        if (value.IsNullOrEmpty) {
            // A covering policy key may differ from the concrete requested endpoint. Resolve the
            // active provider index before declaring the request eligible.
            RateLimitedEndpoint? coveringState = (await GetAllRateLimitedAsync( provider, cancellationToken ))
                .Where( item => ProviderRateLimitPolicy.Covers(
                    provider,
                    item.Endpoint,
                    normalizedEndpoint ) )
                .OrderByDescending( item => item.RetryAfter )
                .FirstOrDefault( );
            if (coveringState is not null) {
                TimeSpan coveringRemaining = coveringState.RetryAfter - DateTimeOffset.UtcNow;
                if (coveringRemaining > TimeSpan.Zero) {
                    return new RateLimitState( true, coveringState.RetryAfter, coveringRemaining );
                }
            }
            return new RateLimitState(
                IsRateLimited: false,
                RetryAfter: null,
                TimeRemaining: null
            );
        }

        if (!DateTimeOffset.TryParse( value.ToString( ), out DateTimeOffset retryAfter )) {
            // Invalid value, clean it up
            _ = await db.ScriptEvaluateAsync(
                ClearRateLimitScript,
                [key, GetIndexKey( provider )],
                [normalizedEndpoint] );
            _ = _activeEndpointCache.TryRemove( provider, out _ );
            LogInvalidValueRemoved( _logger, provider, endpoint );

            return new RateLimitState(
                IsRateLimited: false,
                RetryAfter: null,
                TimeRemaining: null
            );
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        TimeSpan remaining = retryAfter - nowUtc;

        if (remaining > _settings.RateLimitMaximumRetryAfter) {
            _ = await db.ScriptEvaluateAsync(
                ClearRateLimitScript,
                [key, GetIndexKey( provider )],
                [normalizedEndpoint] );
            _ = _activeEndpointCache.TryRemove( provider, out _ );
            LogInvalidValueRemoved( _logger, provider, endpoint );
            return new RateLimitState( false, null, null );
        }

        if (remaining <= TimeSpan.Zero) {
            // Expired but TTL hasn't cleaned up yet, remove manually
            _ = await db.ScriptEvaluateAsync(
                ClearRateLimitScript,
                [key, GetIndexKey( provider )],
                [normalizedEndpoint] );
            _ = _activeEndpointCache.TryRemove( provider, out _ );

            return new RateLimitState(
                IsRateLimited: false,
                RetryAfter: null,
                TimeRemaining: null
            );
        }

        return new RateLimitState(
            IsRateLimited: true,
            RetryAfter: retryAfter,
            TimeRemaining: remaining
        );
    }

    /// <summary>
    /// Marks an endpoint rate-limited until a given time.
    /// </summary>
    /// <param name="provider">The provider whose endpoint is limited.</param>
    /// <param name="endpoint">The endpoint that is limited.</param>
    /// <param name="retryAfter">The time at which the endpoint may be retried.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>A task that completes once the key is written, or immediately if already expired.</returns>
    /// <remarks>
    /// The key TTL is set to the remaining window so it self-clears. If
    /// <paramref name="retryAfter"/> is already in the past, nothing is written. A rate-limit
    /// metric is recorded for each set.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is null or whitespace.</exception>
    public async Task SetRateLimitedAsync(
        SupportedProviders provider,
        string endpoint,
        DateTimeOffset retryAfter,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( endpoint );

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        TimeSpan requested = retryAfter - nowUtc;
        if (requested <= TimeSpan.Zero) {
            LogExpiredNotSet( _logger, provider, endpoint );
            return;
        }
        // Callers that parse provider headers apply the configured minimum. The tracker preserves
        // legitimate shorter internal windows while enforcing the security-relevant upper bound.
        if (requested > _settings.RateLimitMaximumRetryAfter) {
            retryAfter = nowUtc.Add( _settings.RateLimitMaximumRetryAfter );
        }
        TimeSpan ttl = retryAfter - nowUtc;

        string normalizedEndpoint = NormalizeEndpoint( provider, endpoint );
        string key = GetKey( provider, normalizedEndpoint );
        IDatabase db = _redis.GetDatabase( );

        RedisResult result = await db.ScriptEvaluateAsync(
            SetRateLimitScript,
            [key, GetIndexKey( provider )],
            [retryAfter.ToString( "O" ), Math.Max( 1L, (long)ttl.TotalMilliseconds ),
                normalizedEndpoint, retryAfter.ToUnixTimeMilliseconds( )] );
        _ = _activeEndpointCache.TryRemove( provider, out _ );

        if ((int)result == 0) {
            return;
        }

        // Record rate limit metrics
        QueueMetrics.RecordRateLimitEvent( provider, normalizedEndpoint, ttl.TotalSeconds );

        LogRateLimitSet( _logger, provider, normalizedEndpoint, retryAfter, ttl );
    }

    /// <summary>
    /// Clears any rate-limit entry for an endpoint ahead of its natural expiry.
    /// </summary>
    /// <param name="provider">The provider whose endpoint should be cleared.</param>
    /// <param name="endpoint">The endpoint to clear.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>A task that completes once the key has been deleted (if it existed).</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is null or whitespace.</exception>
    public async Task ClearAsync(
        SupportedProviders provider,
        string endpoint,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( endpoint );

        string normalizedEndpoint = NormalizeEndpoint( provider, endpoint );
        string key = GetKey( provider, normalizedEndpoint );
        IDatabase db = _redis.GetDatabase( );

        RedisResult result = await db.ScriptEvaluateAsync(
            ClearRateLimitScript,
            [key, GetIndexKey( provider )],
            [normalizedEndpoint] );
        bool deleted = (int)result == 1;
        _ = _activeEndpointCache.TryRemove( provider, out _ );

        if (deleted) {
            LogRateLimitCleared( _logger, provider, normalizedEndpoint );
        }
    }

    /// <summary>
    /// Returns every currently rate-limited endpoint for a provider.
    /// </summary>
    /// <param name="provider">The provider to query.</param>
    /// <param name="cancellationToken">A cancellation token (not currently observed).</param>
    /// <returns>The endpoints that are still within their Retry-After window, with their retry times.</returns>
    /// <remarks>
    /// Reads a provider-scoped sorted-set index rather than scanning the Redis keyspace. Expired
    /// members are pruned before the active range is returned, keeping dequeue cost proportional
    /// to the number of throttled endpoints rather than the total Redis key count.
    /// </remarks>
    public async Task<IReadOnlyList<RateLimitedEndpoint>> GetAllRateLimitedAsync(
        SupportedProviders provider,
        CancellationToken cancellationToken = default
    ) {
        cancellationToken.ThrowIfCancellationRequested( );
        Stopwatch stopwatch = Stopwatch.StartNew( );
        try {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if (_activeEndpointCache.TryGetValue( provider, out ActiveEndpointCache? cached )
                && cached.ValidUntil > nowUtc) {
                return cached.Endpoints;
            }

            IDatabase db = _redis.GetDatabase( );
            string indexKey = GetIndexKey( provider );
            long now = nowUtc.ToUnixTimeMilliseconds( );
            long maximum = nowUtc.Add( _settings.RateLimitMaximumRetryAfter ).ToUnixTimeMilliseconds( );
            _ = await db.SortedSetRemoveRangeByScoreAsync( indexKey, double.NegativeInfinity, now );
            SortedSetEntry[] overlong = await db.SortedSetRangeByScoreWithScoresAsync(
                indexKey, maximum, double.PositiveInfinity, Exclude.Start );
            foreach (SortedSetEntry invalid in overlong) {
                string storedEndpoint = invalid.Element.ToString( );
                if (IsCanonicalEndpoint( provider, storedEndpoint )) {
                    _ = await db.ScriptEvaluateAsync(
                        ClearRateLimitScript,
                        [GetKey( provider, storedEndpoint ), indexKey],
                        [storedEndpoint] );
                } else {
                    _ = await db.SortedSetRemoveAsync( indexKey, invalid.Element );
                }
                LogInvalidValueRemoved( _logger, provider, storedEndpoint );
            }
            SortedSetEntry[] active = await db.SortedSetRangeByScoreWithScoresAsync(
                indexKey, now, maximum );

            List<RateLimitedEndpoint> endpoints = [];
            foreach (SortedSetEntry entry in active) {
                string storedEndpoint = entry.Element.ToString( );
                if (!IsCanonicalEndpoint( provider, storedEndpoint )) {
                    _ = await db.SortedSetRemoveAsync( indexKey, entry.Element );
                    continue;
                }
                endpoints.Add( new RateLimitedEndpoint(
                    storedEndpoint,
                    DateTimeOffset.FromUnixTimeMilliseconds( checked((long)entry.Score) ) ) );
            }
            _activeEndpointCache[provider] = new ActiveEndpointCache(
                nowUtc + s_activeEndpointCacheDuration, endpoints );
            return endpoints;
        } finally {
            stopwatch.Stop( );
            QueueMetrics.RecordRateLimitDiscoveryDuration( provider, stopwatch.Elapsed.TotalSeconds );
        }
    }

    /// <summary>Builds the Redis key for a provider endpoint: <c>ratelimit:{provider}:{endpoint}</c> (endpoint trimmed and lower-cased).</summary>
    /// <param name="provider">The provider component of the key.</param>
    /// <param name="endpoint">The endpoint component of the key.</param>
    /// <returns>The composed rate-limit key.</returns>
    private static string GetKey( SupportedProviders provider, string endpoint ) =>
        $"{RateLimitPrefix}{provider}:{NormalizeEndpoint( provider, endpoint )}";

    private static string GetIndexKey( SupportedProviders provider ) =>
        $"{RateLimitIndexPrefix}{provider.ToString( ).ToLowerInvariant( )}";

    private static string NormalizeEndpoint( SupportedProviders provider, string endpoint ) {
        return ProviderRateLimitPolicy.ToTrackingKey( provider, endpoint );
    }

    private static bool IsCanonicalEndpoint( SupportedProviders provider, string endpoint ) =>
        endpoint.Equals( NormalizeEndpoint( provider, endpoint ), StringComparison.Ordinal );

    #region LoggerMessage Methods

    /// <summary>Logs that an unparseable rate-limit value was found and removed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider of the affected key.</param>
    /// <param name="endpoint">The endpoint of the affected key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRateLimitTrackerInvalidValueRemoved,
        Level = LogLevel.Warning,
        Message = "Invalid rate limit value for {Provider}:{Endpoint}, removed" )]
    internal static partial void LogInvalidValueRemoved( ILogger logger, SupportedProviders provider, string endpoint );

    /// <summary>Logs that a rate limit was not set because its window had already elapsed.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider of the endpoint.</param>
    /// <param name="endpoint">The endpoint that would have been limited.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRateLimitTrackerExpiredNotSet,
        Level = LogLevel.Debug,
        Message = "Rate limit for {Provider}:{Endpoint} already expired, not setting" )]
    internal static partial void LogExpiredNotSet( ILogger logger, SupportedProviders provider, string endpoint );

    /// <summary>Logs that a rate limit was set for an endpoint until a given time.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider of the endpoint.</param>
    /// <param name="endpoint">The endpoint that was limited.</param>
    /// <param name="retryAfter">When the endpoint may be retried.</param>
    /// <param name="ttl">The TTL applied to the key.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRateLimitTrackerRateLimitSet,
        Level = LogLevel.Information,
        Message = "Set rate limit for {Provider}:{Endpoint} until {RetryAfter} (TTL: {Ttl})" )]
    internal static partial void LogRateLimitSet( ILogger logger, SupportedProviders provider, string endpoint, DateTimeOffset retryAfter, TimeSpan ttl );

    /// <summary>Logs that a rate limit was cleared for an endpoint.</summary>
    /// <param name="logger">The logger to write to.</param>
    /// <param name="provider">The provider of the endpoint.</param>
    /// <param name="endpoint">The endpoint that was cleared.</param>
    [LoggerMessage(
        EventId = LogEventIds.Infrastructure.Queue.RedisRateLimitTrackerCleared,
        Level = LogLevel.Debug,
        Message = "Cleared rate limit for {Provider}:{Endpoint}" )]
    internal static partial void LogRateLimitCleared( ILogger logger, SupportedProviders provider, string endpoint );

    #endregion
}
