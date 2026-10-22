using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.AtProto.Events;
using idunno.Bluesky;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Storage;

/// <summary>
/// Maintains a single authenticated atproto agent for the BridgeBeats service account, caching its
/// session in Redis so it survives across instances and process restarts. This centralizes ATProto
/// session management for all service instances.
/// </summary>
/// <remarks>
/// This is the writer identity for the PDS, distinct from end-user OAuth sessions. Mutating the
/// shared session is guarded twice: an in-process semaphore serializes local callers, and a
/// distributed Redis lock (<c>SET NX</c> with a TTL, released by a compare-and-delete Lua script so
/// only the owner can release) serializes across instances. The agent's credentials are persisted to
/// Redis after every authentication and refresh, and cleared when an unrecoverable auth failure is
/// observed. A short cooldown prevents repeated clears from competing failure events. Token refresh
/// is driven by this class's event handlers rather than the agent's background timer. Session key
/// pattern: <c>atproto:session:{identifier}</c>.
/// </remarks>
public sealed partial class RedisATProtoSessionManager : IATProtoSessionManager, IDisposable {

    /// <summary>
    /// Purpose string for the service-account ATProto session protector. Standalone and versioned so
    /// it is independent of the key-ring version and the user-token purpose string.
    /// </summary>
    internal const string SessionProtectorPurpose = "BridgeBeats.ServiceAccount.ATProtoSession.v1";

    /// <summary>Redis key prefix for the persisted session, completed with the account identifier.</summary>
    private const string SessionKeyPrefix = "atproto:session:";

    /// <summary>Redis key prefix for the distributed credential-mutation lock, completed with the account identifier.</summary>
    private const string LockKeyPrefix = "atproto:auth:lock:";

    // s_refreshTimeout < s_lockExpiry / 2 — worst-case refresh+login pair (2×12s=24s) must stay under lock TTL (30s).
    /// <summary>Default time-to-live of the distributed lock (30 seconds), after which it auto-expires.</summary>
    private static readonly TimeSpan s_lockExpiry = TimeSpan.FromSeconds( 30 );

    /// <summary>Timeout applied to the default agent's HTTP client, bounding refresh and login calls (12 seconds).</summary>
    private static readonly TimeSpan s_refreshTimeout = TimeSpan.FromSeconds( 12 );

    /// <summary>Interval between attempts to acquire the distributed lock while another instance holds it (200&#160;ms).</summary>
    private static readonly TimeSpan s_lockPollInterval = TimeSpan.FromMilliseconds( 200 );

    /// <summary>Minimum interval between credential clears, suppressing clear storms from concurrent failure events (5 seconds).</summary>
    private static readonly TimeSpan s_cooldownWindow = TimeSpan.FromSeconds( 5 );

    // Test seam: overridable lock timing. Production defaults match the static fields above.
    /// <summary>Gets the distributed lock TTL. Settable at construction to support testing.</summary>
    internal TimeSpan LockExpiry { get; init; } = s_lockExpiry;

    /// <summary>Gets the lock-acquisition poll interval. Settable at construction to support testing.</summary>
    internal TimeSpan LockPollInterval { get; init; } = s_lockPollInterval;

    /// <summary>
    /// Lua script that releases the distributed lock only when its stored value matches the caller's
    /// token, so an instance can never release a lock another instance acquired after the first one's
    /// TTL expired. Returns 1 if the key was deleted (we still owned it), 0 otherwise.
    /// </summary>
    private const string ReleaseLockScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    /// <summary>In-process semaphore serializing local access to the shared agent.</summary>
    private readonly SemaphoreSlim _agentLock = new( 1, 1 );

    /// <summary>The Redis connection used for session persistence and the distributed lock.</summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>The logger for session lifecycle events.</summary>
    private readonly ILogger<RedisATProtoSessionManager> _logger;

    /// <summary>The service-account identifier (handle) used to authenticate.</summary>
    private readonly string _identifier;

    /// <summary>The service-account password used for fresh logins.</summary>
    private readonly string _password;

    /// <summary>The fully qualified Redis key for this account's persisted session.</summary>
    private readonly string _sessionKey;

    /// <summary>The fully qualified Redis key for this account's distributed lock.</summary>
    private readonly string _lockKey;

    /// <summary>JSON options used to serialize and deserialize the persisted credentials.</summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Protector used to encrypt/decrypt the session JSON before writing to or reading from Redis.
    /// When <see langword="null"/> the value is stored without encryption (test seam).
    /// </summary>
    private readonly IDataProtector? _protector;

    /// <summary>
    /// Time-to-live applied to every Redis persist operation. Reset on each write so an active
    /// session never expires; a dormant one ages out after this window.
    /// </summary>
    private readonly TimeSpan _sessionTtl;

    /// <summary>Factory that creates a new agent, allowing the default construction to be overridden for testing.</summary>
    private readonly Func<BlueskyAgent> _agentFactory;

    /// <summary>The current authenticated agent, or <see langword="null"/> when none is established.</summary>
    private BlueskyAgent? _agent;

    /// <summary>The time of the most recent credential clear, used to enforce the cooldown window.</summary>
    private DateTimeOffset _lastClearAt = DateTimeOffset.MinValue;

    /// <summary>Whether this instance has been disposed.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new session manager for a single service account.
    /// </summary>
    /// <param name="redis">The Redis connection used for session persistence and the distributed lock.</param>
    /// <param name="logger">The logger for session lifecycle events.</param>
    /// <param name="identifier">The service-account identifier (handle or DID) to authenticate as.</param>
    /// <param name="password">The service-account app password used for fresh logins.</param>
    /// <param name="protector">
    /// An <see cref="IDataProtector"/> used to encrypt the session JSON before writing to Redis and to
    /// decrypt it on read. When <see langword="null"/> no encryption is applied (test seam). In
    /// production this is always supplied via <see cref="BridgeBeats.Core.Infrastructure.Extensions.StorageServiceExtensions.AddATProtoSessionManager"/>.
    /// </param>
    /// <param name="sessionTtlDays">
    /// Number of days before a dormant (never-refreshed) Redis session key expires. Defaults to 45.
    /// Every successful persist call resets the TTL, so an active session never expires.
    /// </param>
    /// <param name="agentFactory">An optional factory for creating agents; when null, a default agent with background token refresh disabled and an <see cref="s_refreshTimeout"/>-bounded HTTP timeout is used.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="redis"/>, <paramref name="logger"/>, <paramref name="identifier"/>, or <paramref name="password"/> is null.</exception>
    public RedisATProtoSessionManager(
        IConnectionMultiplexer redis,
        ILogger<RedisATProtoSessionManager> logger,
        string identifier,
        string password,
        IDataProtector? protector = null,
        int sessionTtlDays = 45,
        Func<BlueskyAgent>? agentFactory = null
    ) {
        _redis = redis ?? throw new ArgumentNullException( nameof( redis ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
        _identifier = identifier ?? throw new ArgumentNullException( nameof( identifier ) );
        _password = password ?? throw new ArgumentNullException( nameof( password ) );

        _sessionKey = $"{SessionKeyPrefix}{identifier}";
        _lockKey = $"{LockKeyPrefix}{identifier}";

        _jsonOptions = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        _protector = protector;
        _sessionTtl = TimeSpan.FromDays( sessionTtlDays > 0 ? sessionTtlDays : 45 );
        _agentFactory = agentFactory ?? CreateDefaultAgent;
    }

    /// <summary>
    /// Returns an authenticated agent for the service account, reusing the current one when possible
    /// and otherwise restoring or establishing a session under the local and distributed locks.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait and any authentication work.</param>
    /// <returns>An authenticated agent.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the distributed lock cannot be acquired within its TTL or login fails.</exception>
    public async Task<BlueskyAgent> GetAuthenticatedAgentAsync( CancellationToken cancellationToken = default ) {
        ObjectDisposedException.ThrowIf( _disposed, this );

        // Fast path: return existing authenticated agent (no lock needed for read)
        BlueskyAgent? currentAgent = _agent;
        if (currentAgent is { IsAuthenticated: true }) {
            return currentAgent;
        }

        // Slow path: acquire in-process lock first, then the distributed Redis lock.
        // Lock ordering: _agentLock (in-process) → distributed Redis lock (cross-process).
        // This ordering is consistent everywhere both locks are held.
        await _agentLock.WaitAsync( cancellationToken );
        try {
            // Double-check after acquiring in-process lock
            currentAgent = _agent;
            if (currentAgent is { IsAuthenticated: true }) {
                return currentAgent;
            }

            // Acquire the distributed lock and perform credential mutation under it.
            return await AcquireDistributedLockAndRefreshAsync( cancellationToken );
        } finally {
            _ = _agentLock.Release( );
        }
    }

    /// <summary>
    /// Discards the current agent and any stored credentials and performs a fresh authentication.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait and the re-authentication.</param>
    /// <returns>A freshly authenticated agent.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the distributed lock cannot be acquired within its TTL or login fails.</exception>
    public async Task<BlueskyAgent> ForceReauthenticateAsync( CancellationToken cancellationToken = default ) {
        ObjectDisposedException.ThrowIf( _disposed, this );

        LogForceReauthRequested( _identifier );

        // Acquire in-process lock before the distributed lock (consistent ordering).
        await _agentLock.WaitAsync( cancellationToken );
        try {
            // Clear stored credentials
            await ClearStoredCredentialsAsync( );

            // Dispose existing agent if any
            BlueskyAgent? oldAgent = _agent;
            _agent = null;
            oldAgent?.Dispose( );

            // Acquire the distributed lock and perform a fresh login.
            return await AcquireDistributedLockAndRefreshAsync( cancellationToken );
        } finally {
            _ = _agentLock.Release( );
        }
    }

    /// <summary>
    /// Acquires the distributed Redis lock (polling until the TTL elapses if another instance holds
    /// it), then re-checks the agent, restores the session from Redis, or performs a fresh login,
    /// releasing the lock on completion. Lock-losers block-acquire and rotate sequentially.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the lock wait and the authentication work.</param>
    /// <returns>An authenticated agent.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the lock cannot be acquired within its TTL or login fails.</exception>
    private async Task<BlueskyAgent> AcquireDistributedLockAndRefreshAsync( CancellationToken cancellationToken ) {
        IDatabase db = _redis.GetDatabase( );
        string lockValue = Guid.NewGuid( ).ToString( );

        // Block-acquire the distributed lock: try immediately, then poll until acquired
        // or the lock TTL elapses (covers the crash-while-holding case).
        bool lockAcquired = await db.StringSetAsync( _lockKey, lockValue, LockExpiry, When.NotExists );

        if (!lockAcquired) {
            LogWaitingForLock( _identifier );
            TimeSpan waited = TimeSpan.Zero;
            while (!lockAcquired && waited < LockExpiry) {
                await Task.Delay( LockPollInterval, cancellationToken );
                waited += LockPollInterval;
                lockAcquired = await db.StringSetAsync( _lockKey, lockValue, LockExpiry, When.NotExists );
            }

            if (!lockAcquired) {
                LogLockWaitTimeout( _identifier );
                throw new InvalidOperationException(
                    $"Could not acquire the credential-mutation lock for '{_identifier}' within the TTL window." );
            }
        }

        try {
            // Double-check after acquiring the distributed lock: another process may have
            // refreshed and the in-process _agent may have been updated via the awaited credential callback.
            if (_agent is { IsAuthenticated: true }) {
                return _agent;
            }

            // Try to restore session from Redis (includes calling RefreshCredentials under the lock).
            if (await TryRestoreSessionAsync( cancellationToken )) {
                return _agent!;
            }

            // No stored session or restoration failed — perform a fresh login under the lock.
            return await PerformFreshLoginAsync( cancellationToken );
        } finally {
            if (lockAcquired) {
                await ReleaseLockAsync( db, lockValue );
            }
        }
    }

    /// <summary>
    /// Attempts to restore the session from Redis: deserializes the persisted credentials, rebuilds
    /// the agent's credential, and refreshes it; on success, persists the refreshed credentials and
    /// adopts the agent. Callers must hold the distributed Redis lock before calling this method.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the restore.</param>
    /// <returns><see langword="true"/> when a session was restored and refreshed; otherwise <see langword="false"/>.</returns>
    private async Task<bool> TryRestoreSessionAsync( CancellationToken cancellationToken ) {
        BlueskyAgent? agent = null;
        try {
            IDatabase db = _redis.GetDatabase( );
            RedisValue storedValue = await db.StringGetAsync( _sessionKey );

            if (storedValue.IsNullOrEmpty) {
                LogNoStoredCredentials( _identifier );
                return false;
            }

            // Decrypt the stored payload. A CryptographicException means the key ring has rotated
            // or the value is legacy plaintext (first deploy before any encrypted persist).
            // Both cases degrade to fresh login — the catch block below handles them.
            string payload = storedValue.ToString( );
            if (_protector is not null) {
                payload = _protector.Unprotect( payload );
            }

            ATProtoPersistedCredentials? credentials = JsonSerializer.Deserialize<ATProtoPersistedCredentials>(
                payload,
                _jsonOptions
            );

            if (credentials is null) {
                LogDeserializeFailed( _identifier );
                return false;
            }

            LogRestoringSession( _identifier, credentials.PersistedAt );

            // Create a new agent (background token refresh disabled) and attempt session restoration.
            agent = _agentFactory( );
            SubscribeToAgentEvents( agent );

            // Parse authentication type
            if (!Enum.TryParse<AuthenticationType>( credentials.AuthenticationType, out AuthenticationType authType )) {
                authType = AuthenticationType.UsernamePassword;
            }

            // Create credential for session restoration
            AtProtoCredential restoredCredential = AtProtoCredential.Create(
                service: new Uri( credentials.Service ),
                authenticationType: authType,
                refreshToken: credentials.RefreshToken,
                dPoPProofKey: credentials.DPoPProofKey,
                dPoPNonce: credentials.DPoPNonce
            );

            // RefreshCredentials rotates the refresh token (single-use); this call runs under the
            // distributed lock so only one process rotates at a time.
            bool refreshResult = await agent.RefreshCredentials( restoredCredential, cancellationToken );

            if (refreshResult) {
                LogSessionRestored( _identifier );
                // Persist the rotated token before transferring ownership so lock-losers reuse the new token.
                await PersistCredentialsFromAgentAsync( agent );
                _agent?.Dispose( );
                _agent = agent;
                agent = null; // ownership transferred — suppress disposal in finally
                return true;
            }

            LogRestoreFailed( _identifier );
            agent.Dispose( );
            agent = null;
            return false;
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            agent?.Dispose( );
            throw;
        } catch (CryptographicException ex) {
            // Key-ring mismatch or legacy plaintext value present before first encrypted persist.
            // Degrade to fresh login — the next persist will write ciphertext.
            LogRestoreCryptoMismatch( ex, _identifier );
            agent?.Dispose( );
            return false;
        } catch (Exception ex) {
            LogRestoreException( ex, _identifier );
            agent?.Dispose( );
            return false;
        }
    }

    /// <summary>
    /// Performs a fresh login with the service-account identifier and password, persisting the
    /// resulting credentials and adopting the new agent. Callers must hold the distributed Redis lock
    /// before calling this method.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the login.</param>
    /// <returns>The newly authenticated agent.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the PDS login does not succeed.</exception>
    private async Task<BlueskyAgent> PerformFreshLoginAsync( CancellationToken cancellationToken ) {
        BlueskyAgent? agent = null;
        try {
            agent = _agentFactory( );
            SubscribeToAgentEvents( agent );

            LogFreshLogin( _identifier );

            AtProtoHttpResult<bool> loginResult = await agent.Login( _identifier, _password, cancellationToken: cancellationToken );

            if (!loginResult.Succeeded) {
                string errorMsg = loginResult.AtErrorDetail?.Message ?? $"HTTP {loginResult.StatusCode}";
                agent.Dispose( );
                agent = null;
                throw new InvalidOperationException( $"Failed to authenticate to PDS: {errorMsg}" );
            }

            LogAuthenticated( _identifier );

            // Persist the newly authenticated credentials before leaving the login gate.
            await PersistCredentialsFromAgentAsync( agent );

            _agent?.Dispose( );
            _agent = agent;
            agent = null; // ownership transferred

            return _agent!;
        } catch {
            agent?.Dispose( );
            throw;
        }
    }

    /// <summary>
    /// Subscribes to the agent's authentication lifecycle events so credentials are persisted on
    /// authentication and update, and cleared on refresh failure or unauthentication.
    /// </summary>
    /// <param name="agent">The agent to subscribe to.</param>
    private void SubscribeToAgentEvents( BlueskyAgent agent ) {
        agent.CredentialsUpdatedAsync = async ( args, cancellationToken ) => {
            // Pending agents are persisted explicitly by login/restore while this gate is held.
            if (!ReferenceEquals( agent, _agent ) || args.AccessCredentials is null) { return; }
            await _agentLock.WaitAsync( cancellationToken );
            try {
                if (ReferenceEquals( agent, _agent )) {
                    await PersistCredentialsAsync( args.AccessCredentials );
                }
            } finally {
                _ = _agentLock.Release( );
            }
        };
        agent.TokenRefreshFailed += OnTokenRefreshFailed;
        agent.Unauthenticated += OnUnauthenticated;
    }

    /// <summary>
    /// Handles the agent's token-refresh-failed event. Drops the current agent and, when the failure
    /// is unrecoverable (error name <c>ExpiredToken</c>, <c>InvalidToken</c>, or <c>AccountTakedown</c>,
    /// or HTTP 401/403), clears the stored credentials, subject to the cooldown window. A transient
    /// failure drops the agent without clearing, so a subsequent call re-attempts a refresh.
    /// </summary>
    /// <param name="sender">The agent that raised the event.</param>
    /// <param name="e">The event arguments carrying the error and status code.</param>
    private void OnTokenRefreshFailed( object? sender, TokenRefreshFailedEventArgs e ) {
        // Classify the refresh failure: clear the SHARED credential only when the refresh
        // token is genuinely dead. Decide on the XRPC error NAME first (it is authoritative
        // and the dead-token cases are HTTP 400, not 401); fall back to status band only when
        // no error body is present.
        // Dead-token error names per com.atproto.server.refreshSession lexicon.
        string? errorName = e.Error?.Error;
        int? status = e.StatusCode.HasValue ? (int)e.StatusCode.Value : null;

        bool deadTokenByName =
            string.Equals( errorName, "ExpiredToken",    StringComparison.OrdinalIgnoreCase ) ||
            string.Equals( errorName, "InvalidToken",    StringComparison.OrdinalIgnoreCase ) ||
            string.Equals( errorName, "AccountTakedown", StringComparison.OrdinalIgnoreCase );

        // 401/403 with no/unknown error name are still genuine auth rejections.
        bool deadTokenByStatus = status is 401 or 403;

        // Everything else is transient and must NOT clear the shared credential:
        //   - 408 (timeout), 429 (rate limit), any 5xx, null status (transport failure)
        //   - any 400 whose error name is NOT a known dead-token name (conservative default: KEEP).
        bool isUnrecoverableAuthFailure = deadTokenByName || deadTokenByStatus;

        LogTokenRefreshFailed( _identifier, e.StatusCode );

        if (sender is not BlueskyAgent failingAgent) {
            return;
        }

        _ = Task.Run( async ( ) => {
            try {
                await _agentLock.WaitAsync( );
                try {
                    if (!ReferenceEquals( failingAgent, _agent )) {
                        // Stale agent — dispose it silently; do not touch _agent or Redis
                        failingAgent.Dispose( );
                        return;
                    }

                    // The failing agent is the current agent — dispose and null it so the next
                    // request takes the clean restore/login path
                    _agent = null;
                    failingAgent.Dispose( );

                    if (isUnrecoverableAuthFailure) {
                        // Genuine auth rejection — clear the shared Redis credential so all
                        // processes will re-login rather than retrying a dead token
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        if (now - _lastClearAt >= s_cooldownWindow) {
                            _lastClearAt = now;
                            await ClearStoredCredentialsAsync( );
                        } else {
                            LogClearSuppressedByCooldown( _identifier );
                        }
                    }
                    // Transport failure (5xx / null status): keep the Redis credential — it is
                    // still valid. The next request will retry under the lock with a fresh agent.
                } finally {
                    _ = _agentLock.Release( );
                }
            } catch (Exception ex) {
                LogClearAfterRefreshFailed( ex, _identifier );
            }
        } );
    }

    /// <summary>
    /// Handles the agent's unauthenticated event (the PDS has explicitly ended the session) by
    /// dropping the current agent and clearing the stored credentials, subject to the cooldown window.
    /// </summary>
    /// <param name="sender">The agent that raised the event.</param>
    /// <param name="e">The event arguments.</param>
    private void OnUnauthenticated( object? sender, UnauthenticatedEventArgs e ) {
        LogUnauthenticated( _identifier );

        if (sender is not BlueskyAgent failingAgent) {
            return;
        }

        _ = Task.Run( async ( ) => {
            try {
                await _agentLock.WaitAsync( );
                try {
                    if (!ReferenceEquals( failingAgent, _agent )) {
                        failingAgent.Dispose( );
                        return;
                    }

                    _agent = null;
                    failingAgent.Dispose( );

                    // Session genuinely ended — clear the shared credential
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (now - _lastClearAt >= s_cooldownWindow) {
                        _lastClearAt = now;
                        await ClearStoredCredentialsAsync( );
                    } else {
                        LogClearSuppressedByCooldown( _identifier );
                    }
                } finally {
                    _ = _agentLock.Release( );
                }
            } catch (Exception ex) {
                LogClearAfterUnauthFailed( ex, _identifier );
            }
        } );
    }

    /// <summary>
    /// Serializes the agent's current credentials (including the refresh token and, when present, the
    /// DPoP proof key and nonce) and stores them in Redis under the session key. No-ops when the agent
    /// is not authenticated.
    /// </summary>
    /// <param name="agent">The authenticated agent whose credentials are persisted.</param>
    private async Task PersistCredentialsFromAgentAsync( BlueskyAgent agent ) {
        if (!agent.IsAuthenticated || agent.Credentials is null) {
            LogCannotPersist( );
            return;
        }

        await PersistCredentialsAsync( agent.Credentials );
    }

    private async Task PersistCredentialsAsync( AccessCredentials credentials ) {

        // Extract DPoP fields if available
        string? dPoPProofKey = null;
        string? dPoPNonce = null;

        if (credentials is DPoPAccessCredentials dPoPCredentials) {
            dPoPProofKey = dPoPCredentials.DPoPProofKey;
            dPoPNonce = dPoPCredentials.DPoPNonce;
        }

        ATProtoPersistedCredentials persistedCredentials = new( ) {
            RefreshToken = credentials.RefreshToken,
            DPoPProofKey = dPoPProofKey,
            DPoPNonce = dPoPNonce,
            Service = credentials.Service.ToString( ),
            Did = credentials.Did?.ToString( ) ?? string.Empty,
            Handle = _identifier,
            AuthenticationType = credentials.AuthenticationType.ToString( ),
            PersistedAt = DateTimeOffset.UtcNow
        };

        await PersistPayloadAsync( persistedCredentials );
    }

    /// <summary>
    /// Serializes, optionally encrypts, and writes the given credentials to Redis under the session
    /// key with the configured TTL. Extracted as an internal seam so tests can drive the
    /// serialize→protect→write path directly without requiring a live authenticated agent.
    /// </summary>
    /// <param name="persistedCredentials">The credentials record to persist.</param>
    internal async Task PersistPayloadAsync( ATProtoPersistedCredentials persistedCredentials ) {
        string json = JsonSerializer.Serialize( persistedCredentials, _jsonOptions );

        // Encrypt the JSON before writing to Redis. When no protector is supplied (test seam)
        // the raw JSON is stored as a convenience.
        string payload = _protector is not null ? _protector.Protect( json ) : json;

        IDatabase db = _redis.GetDatabase( );
        _ = await db.StringSetAsync( _sessionKey, payload, _sessionTtl );

        LogPersisted( _identifier );
    }

    /// <summary>
    /// Deletes the persisted session from Redis, forcing the next authentication to perform a fresh login.
    /// </summary>
    private async Task ClearStoredCredentialsAsync( ) {
        IDatabase db = _redis.GetDatabase( );
        _ = await db.KeyDeleteAsync( _sessionKey );
        LogCleared( _identifier );
    }

    /// <summary>
    /// Releases the distributed lock using the compare-and-delete Lua script, so the lock is only
    /// deleted when its stored value still matches this caller's token (preventing deletion of a lock
    /// another process re-acquired after TTL expiry). Failures are logged and swallowed.
    /// </summary>
    /// <param name="db">The Redis database to run the release script against.</param>
    /// <param name="lockValue">The caller's unique lock token, compared before deletion.</param>
    private async Task ReleaseLockAsync( IDatabase db, string lockValue ) {
        try {
            _ = await db.ScriptEvaluateAsync(
                ReleaseLockScript,
                keys: [_lockKey],
                values: [lockValue]
            );
        } catch (Exception ex) {
            LogLockReleaseFailed( ex, _identifier );
        }
    }

    /// <summary>
    /// Creates the default agent used when no factory is supplied: background token refresh is
    /// disabled (refresh is driven by this class instead) and the HTTP client uses the refresh timeout.
    /// </summary>
    /// <returns>A new agent configured for service-account use.</returns>
    private static BlueskyAgent CreateDefaultAgent( ) =>
        new( new BlueskyAgentOptions {
            EnableBackgroundTokenRefresh = false,
            HttpClientOptions = new HttpClientOptions( timeout: s_refreshTimeout )
        } );

    /// <summary>
    /// Disposes the current agent and the in-process lock. Idempotent; safe to call more than once.
    /// </summary>
    public void Dispose( ) {
        if (_disposed) { return; }

        _agent?.Dispose( );
        _agent = null;
        _agentLock.Dispose( );
        _disposed = true;
    }

}
