using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using idunno.AtProto.Authentication;
using System.Reflection;
using System.Text.Json;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Storage;
using idunno.AtProto;
using idunno.AtProto.Events;
using idunno.Bluesky;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RedisATProtoSessionManager"/>, the single-service-account ATProto
/// session manager. Drive the agent lifecycle against a mocked Redis database (lock acquire/release,
/// stored-credential read, persist, and clear) and a <see cref="DisposableTrackingAgent"/> test
/// double, reaching private members via reflection seams. Cover: agent disposal when
/// <c>RefreshCredentials</c> throws during restore; the current/stale/orphan agent handling and
/// credential clears on <c>TokenRefreshFailed</c> and <c>Unauthenticated</c> events; the
/// clear-cooldown window; the dead-token-vs-transient boundary predicate across HTTP status and
/// error-name combinations; the fail-closed lock-wait timeout; cancellation propagation during a
/// Redis read; and the default-factory guarantee that agents disable background token refresh.
/// Several success-path disposal scenarios that require a live rotating PDS are marked inconclusive
/// here and covered at the AppHost integration tier.
/// </summary>
[TestClass]
public class RedisATProtoSessionManagerTests {

    /// <summary>Test service-account handle.</summary>
    private const string TestIdentifier = "test.bsky.social";
    /// <summary>Test service-account app password.</summary>
    private const string TestPassword = "test-app-password";
    /// <summary>Test service-account DID used in persisted credentials and event args.</summary>
    private const string TestDid = "did:plc:testuser12345678";

    /// <summary>Shared camelCase serializer options matching the persisted-credential wire format.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Reflection handle for the private <c>_agent</c> field (the current agent reference).</summary>
    private static readonly FieldInfo s_agentField =
        typeof( RedisATProtoSessionManager )
            .GetField( "_agent", BindingFlags.NonPublic | BindingFlags.Instance )
        ?? throw new InvalidOperationException( "_agent field not found on RedisATProtoSessionManager" );

    /// <summary>Reflection handle for the private <c>SubscribeToAgentEvents</c> method.</summary>
    private static readonly MethodInfo s_subscribeToAgentEvents =
        typeof( RedisATProtoSessionManager )
            .GetMethod( "SubscribeToAgentEvents", BindingFlags.NonPublic | BindingFlags.Instance )
        ?? throw new InvalidOperationException( "SubscribeToAgentEvents not found on RedisATProtoSessionManager" );

    /// <summary>Reflection handle for the private <c>TryRestoreSessionAsync</c> method.</summary>
    private static readonly MethodInfo s_tryRestoreSessionAsync =
        typeof( RedisATProtoSessionManager )
            .GetMethod( "TryRestoreSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance )
        ?? throw new InvalidOperationException( "TryRestoreSessionAsync not found on RedisATProtoSessionManager" );

    /// <summary>Mocked Redis multiplexer returning <see cref="_dbMock"/> for the session database.</summary>
    private Mock<IConnectionMultiplexer> _redisMock = null!;
    /// <summary>Mocked Redis database backing lock, stored-credential, persist, and clear operations.</summary>
    private Mock<IDatabase> _dbMock = null!;
    /// <summary>Mocked logger (enabled at all levels) for the manager under test.</summary>
    private Mock<ILogger<RedisATProtoSessionManager>> _loggerMock = null!;

    /// <summary>MSTest-injected context, used for per-test cancellation tokens.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds dependency mocks and installs default stubs (lock acquired, no stored credential,
    /// persist and clear succeed) before each test.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _redisMock = new Mock<IConnectionMultiplexer>( );
        _dbMock = new Mock<IDatabase>( );
        _loggerMock = new Mock<ILogger<RedisATProtoSessionManager>>( );

        // Logger must report IsEnabled=true so [LoggerMessage]-generated calls don't short-circuit.
        _ = _loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        // Wire IConnectionMultiplexer → IDatabase
        _ = _redisMock
            .Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object?>( ) ) )
            .Returns( _dbMock.Object );

        // Default: lock acquire succeeds immediately
        SetupLockAcquire( true );

        // Default: lock release succeeds
        SetupLockRelease( );

        // Default: no stored credentials in Redis
        SetupStoredCredentials( RedisValue.Null );

        // Default: persist/clear succeed
        SetupPersist( );
        SetupClear( );
    }

    /// <summary>Credential updates await durable persistence and surface Redis failures to the SDK.</summary>
    [TestMethod]
    public async Task CredentialUpdates_AwaitPersistence_AndSurfaceFailures( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        using DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );
        Assert.IsNotNull( agent.CredentialsUpdatedAsync );
        TaskCompletionSource<bool> stored = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = _dbMock.Setup( d => d.StringSetAsync( It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
            It.IsAny<Expiration>( ), It.IsAny<ValueCondition>( ), It.IsAny<CommandFlags>( ) ) ).Returns( stored.Task );
        string jwt = new JwtSecurityTokenHandler( ).WriteToken( new JwtSecurityToken(
            claims: [new Claim( "sub", TestDid )], expires: DateTime.UtcNow.AddHours( 1 ) ) );
        AccessCredentials credentials = new( new Uri( "https://pds.example" ), AuthenticationType.UsernamePassword, jwt, "rotated-refresh" );
        CredentialsUpdatedEventArgs args = new( credentials.Did, credentials.Service, credentials );
        Task persistence = agent.CredentialsUpdatedAsync( args, TestContext.CancellationToken );
        Assert.IsFalse( persistence.IsCompleted, "The SDK must wait for the Redis write." );
        stored.SetResult( true );
        await persistence;
        _ = _dbMock.Setup( d => d.StringSetAsync( It.IsAny<RedisKey>( ), It.IsAny<RedisValue>( ),
            It.IsAny<Expiration>( ), It.IsAny<ValueCondition>( ), It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Redis unavailable" ) );
        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            ( ) => agent.CredentialsUpdatedAsync( args, TestContext.CancellationToken ) );
    }

    /// <summary>
    /// Builds a <see cref="RedisATProtoSessionManager"/> from the current mocks with the optional
    /// agent factory seam.
    /// </summary>
    private RedisATProtoSessionManager CreateManager( Func<BlueskyAgent>? factory = null ) =>
        new(
            _redisMock.Object,
            _loggerMock.Object,
            TestIdentifier,
            TestPassword,
            protector: null,
            sessionTtlDays: 45,
            agentFactory: factory
        );

    /// <summary>Sets the manager's private <c>_agent</c> field via reflection.</summary>
    private void SetAgent( RedisATProtoSessionManager manager, BlueskyAgent? agent ) =>
        s_agentField.SetValue( manager, agent );

    /// <summary>Reads the manager's private <c>_agent</c> field via reflection.</summary>
    private BlueskyAgent? GetAgent( RedisATProtoSessionManager manager ) =>
        (BlueskyAgent?)s_agentField.GetValue( manager );

    /// <summary>Invokes the manager's private <c>SubscribeToAgentEvents</c> for the given agent.</summary>
    private void SubscribeToAgentEvents( RedisATProtoSessionManager manager, BlueskyAgent agent ) =>
        s_subscribeToAgentEvents.Invoke( manager, [agent] );

    /// <summary>
    /// Invokes the manager's private <c>TryRestoreSessionAsync</c> via reflection and awaits its result.
    /// </summary>
    private async Task<bool> TryRestoreSessionAsync(
        RedisATProtoSessionManager manager,
        CancellationToken ct = default ) {
        Task<bool> task = (Task<bool>)s_tryRestoreSessionAsync.Invoke( manager, [ct] )!;
        return await task;
    }

    /// <summary>Stubs the distributed-lock acquire (the <c>SET NX</c> on the auth-lock key) to return <paramref name="result"/>.</summary>
    private void SetupLockAcquire( bool result ) {
        _ = _dbMock
            .Setup( d => d.StringSetAsync(
                It.Is<RedisKey>( k => ((string)k!).Contains( ":auth:lock:" ) ),
                It.IsAny<RedisValue>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<When>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( result );
    }

    /// <summary>Stubs the compare-and-delete Lua lock-release script to report success.</summary>
    private void SetupLockRelease( ) {
        _ = _dbMock
            .Setup( d => d.ScriptEvaluateAsync(
                It.IsAny<string>( ),
                It.IsAny<RedisKey[]?>( ),
                It.IsAny<RedisValue[]?>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( (RedisResult)RedisResult.Create( (RedisValue)1 ) );
    }

    /// <summary>Stubs the read of the persisted session credential to return <paramref name="value"/>.</summary>
    private void SetupStoredCredentials( RedisValue value ) {
        _ = _dbMock
            .Setup( d => d.StringGetAsync(
                It.Is<RedisKey>( k => ((string)k!).Contains( "atproto:session:" ) ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( value );
    }

    /// <summary>Stubs the persistence of session credentials to succeed.</summary>
    private void SetupPersist( ) {
        _ = _dbMock
            .Setup( d => d.StringSetAsync(
                It.Is<RedisKey>( k => ((string)k!).Contains( "atproto:session:" ) ),
                It.IsAny<RedisValue>( ),
                It.IsAny<TimeSpan?>( ),
                It.IsAny<When>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( true );
    }

    /// <summary>Stubs the credential-clear (key delete) to succeed.</summary>
    private void SetupClear( ) {
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync(
                It.IsAny<RedisKey>( ),
                It.IsAny<CommandFlags>( ) ) )
            .ReturnsAsync( true );
    }

    /// <summary>
    /// Serializes a persisted credential whose <c>Service</c> points at a connection-refusing local
    /// address, so an attempted credential refresh during restore fails fast.
    /// </summary>
    private static string BuildRefusingServiceCredentialJson( ) {
        ATProtoPersistedCredentials creds = new( ) {
            RefreshToken = "test-refresh-token",
            Service = "http://127.0.0.1:1", // closed port → instant connection refused
            Did = TestDid,
            Handle = TestIdentifier,
            AuthenticationType = "UsernamePassword",
            PersistedAt = DateTimeOffset.UtcNow.AddMinutes( -5 )
        };
        return JsonSerializer.Serialize( creds, s_jsonOptions );
    }

    /// <summary>
    /// When <c>RefreshCredentials</c> throws during session restore, the agent created for that
    /// attempt is disposed and the manager's <c>_agent</c> field is left null (restore returns false).
    /// Guards against the agent leak that drives monotonic hourly-burst growth.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task TryRestoreSession_WhenRefreshCredentialsThrows_DisposesCreatedAgent( ) {
        // Arrange — stored credential points to a closed port.
        // Connection refused on 127.0.0.1:1 is instant (OS immediately rejects the TCP SYN).
        SetupStoredCredentials( BuildRefusingServiceCredentialJson( ) );

        DisposableTrackingAgent? createdAgent = null;
        Func<BlueskyAgent> factory = ( ) => {
            createdAgent = new DisposableTrackingAgent( );
            return createdAgent;
        };

        using RedisATProtoSessionManager manager = CreateManager( factory );

        // Act — call TryRestoreSessionAsync directly (bypasses PerformFreshLoginAsync so no
        // second network call hangs the test).
        bool restored = await TryRestoreSessionAsync( manager, TestContext.CancellationToken );

        // Assert
        Assert.IsFalse( restored, "Restore must return false when RefreshCredentials throws" );
        Assert.IsNotNull( createdAgent, "Factory must have been called" );
        Assert.IsTrue( createdAgent.WasDisposed,
            "Agent created during TryRestoreSessionAsync must be disposed when RefreshCredentials throws " +
            "(Directive 1 — stops the agent leak that drives the monotonic hourly-burst growth). " +
            "Pre-fix: removing agent?.Dispose() from the catch block leaves WasDisposed=false." );
        Assert.IsNull( GetAgent( manager ),
            "_agent must not be set when restore fails via exception (Directive 1)" );
    }

    /// <summary>
    /// Placeholder for the restore-false disposal regression; marked inconclusive because it requires
    /// a live rotating PDS and is covered at the AppHost integration tier.
    /// </summary>
    [TestMethod]
    public void RestoreReturnsFalse_DisposalPath_Regression( ) {
        Assert.Inconclusive( "Restore-false / success-path disposal requires a live rotating PDS; covered at the AppHost integration tier, not unit. See brief §4." );
    }

    /// <summary>
    /// When the current agent fires <c>TokenRefreshFailed</c> with an unrecoverable status, that agent
    /// is disposed and <c>_agent</c> is nulled, so the next authenticated-agent request does not return
    /// a disposed agent.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task TokenRefreshFailed_OnCurrentAgent_DisposesAgentAndNullsReference( ) {
        // Arrange — install a current agent directly via the reflection seam.
        using RedisATProtoSessionManager manager = CreateManager( );

        DisposableTrackingAgent currentAgent = new( );
        SetAgent( manager, currentAgent );
        SubscribeToAgentEvents( manager, currentAgent );

        // Act — fire TokenRefreshFailed with 401 Unauthorized (unrecoverable auth rejection)
        currentAgent.FireTokenRefreshFailed( HttpStatusCode.Unauthorized );

        // The handler runs asynchronously via Task.Run. Allow it to complete.
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( currentAgent.WasDisposed,
            "Current agent must be disposed after TokenRefreshFailed fires (Directive 2). " +
            "Pre-fix: no dispose call → WasDisposed=false → agent's timer keeps firing." );

        Assert.IsNull( GetAgent( manager ),
            "_agent must be null after TokenRefreshFailed disposes the current agent (Directive 2). " +
            "Pre-fix: _agent non-null → next GetAuthenticatedAgentAsync returns the disposed (fast-path) agent." );
    }

    /// <summary>
    /// When a stale (non-current) agent fires <c>TokenRefreshFailed</c>, the reference-equality guard
    /// leaves the current agent untouched while disposing the orphaned stale agent.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task TokenRefreshFailed_OnStaleAgent_DoesNotTouchCurrentAgent( ) {
        // Arrange
        using RedisATProtoSessionManager manager = CreateManager( );

        // Install a current agent
        DisposableTrackingAgent currentAgent = new( );
        SetAgent( manager, currentAgent );
        SubscribeToAgentEvents( manager, currentAgent );

        // Create a stale agent (different instance, also subscribed)
        DisposableTrackingAgent staleAgent = new( );
        SubscribeToAgentEvents( manager, staleAgent );

        // Act — fire on the stale agent (not the current _agent)
        staleAgent.FireTokenRefreshFailed( HttpStatusCode.Unauthorized );

        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        // Assert — _agent must still be the currentAgent (not nulled or swapped)
        Assert.AreSame( currentAgent, GetAgent( manager ),
            "_agent must not be changed when a stale (non-current) agent fires TokenRefreshFailed " +
            "(ReferenceEquals guard). Pre-fix: missing guard would null the current valid session." );

        // The stale agent should be disposed (it is an orphan; the stale-agent disposal branch runs)
        Assert.IsTrue( staleAgent.WasDisposed,
            "Stale agent must be disposed when it fires TokenRefreshFailed (it is an orphan)." );
    }

    /// <summary>
    /// Two unrecoverable <c>TokenRefreshFailed</c> events within the 5-second cooldown window clear the
    /// shared credential at most once: the first event clears and nulls the agent; a second event on a
    /// new agent is suppressed by the cooldown guard.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task TokenRefreshFailed_RepeatedUnrecoverable_ClearsAtMostOncePerCooldownWindow( ) {
        // Arrange
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;

        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        // agentA is current; fire 401 → should clear once and null _agent
        DisposableTrackingAgent agentA = new( );
        SetAgent( manager, agentA );
        SubscribeToAgentEvents( manager, agentA );

        agentA.FireTokenRefreshFailed( HttpStatusCode.Unauthorized );

        // Wait for the async handler to complete before inspecting state
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 1, clearCount, "First 401 event must produce exactly one credential clear." );
        Assert.IsNull( GetAgent( manager ), "_agent must be nulled after the first 401 event." );

        // install agentB as current within the 5-second cooldown window
        DisposableTrackingAgent agentB = new( );
        SetAgent( manager, agentB );
        SubscribeToAgentEvents( manager, agentB );

        // Fire again — cooldown window (~5s) has NOT elapsed (~300ms since first clear)
        agentB.FireTokenRefreshFailed( HttpStatusCode.Unauthorized );

        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        // Assert — cooldown must suppress the second clear; total remains 1
        Assert.AreEqual( 1, clearCount,
            $"Cooldown guard must suppress the second credential clear within the 5-second window. " +
            $"Got {clearCount} clear(s); expected exactly 1. " +
            "If this is 2, the cooldown guard is missing or not reached (mutation failure target)." );
    }

    /// <summary>
    /// Placeholder for the successful-restore prior-agent disposal regression; marked inconclusive
    /// because it requires a live rotating PDS and is covered at the AppHost integration tier.
    /// </summary>
    [TestMethod]
    public void SuccessfulRestore_DisposePriorAgent_Regression( ) {
        Assert.Inconclusive( "Restore-false / success-path disposal requires a live rotating PDS; covered at the AppHost integration tier, not unit. See brief §4." );
    }

    /// <summary>
    /// The private static <c>CreateDefaultAgent</c> factory produces agents with
    /// <c>EnableBackgroundTokenRefresh = false</c>, so the library's background timer does not drive
    /// refresh (the manager drives it instead). Reflected via <see cref="FindBackgroundRefreshOption"/>.
    /// </summary>
    [TestMethod]
    public void DefaultFactory_CreatesAgentsWithBackgroundRefreshDisabled( ) {
        // Arrange — access the private static CreateDefaultAgent method
        MethodInfo? createMethod = typeof( RedisATProtoSessionManager )
            .GetMethod( "CreateDefaultAgent", BindingFlags.NonPublic | BindingFlags.Static );

        Assert.IsNotNull( createMethod, "Private static CreateDefaultAgent must exist" );

        // Act — invoke the factory to get a real agent
        using BlueskyAgent? agent = (BlueskyAgent?)createMethod.Invoke( null, null );

        Assert.IsNotNull( agent, "Default factory must return a non-null BlueskyAgent" );

        // Assert — verify EnableBackgroundTokenRefresh=false via reflection on the agent's _options field.
        // BlueskyAgent stores its options internally; traverse the inheritance chain to find it.
        bool? bgRefreshEnabled = FindBackgroundRefreshOption( agent );

        Assert.IsNotNull( bgRefreshEnabled,
            "EnableBackgroundTokenRefresh option must be reflectable — a null result means the Cause-A linchpin guard silently stopped running (idunno field layout changed)." );

        if (bgRefreshEnabled.HasValue) {
            Assert.IsFalse( bgRefreshEnabled.Value,
                "Default factory must produce agents with EnableBackgroundTokenRefresh=false (Directive 3). " +
                "Pre-fix: agents used default options (background refresh ON) → Cause-A cascade." );
        }
        // If the option is not accessible via reflection (private deep field), the test still
        // verifies that the default factory method exists and returns a non-null agent, and the
        // source code audit (compilation) confirms EnableBackgroundTokenRefresh=false is set.
    }

    /// <summary>
    /// A supplied agent factory is invoked during session restore, confirming the factory seam is
    /// wired into the restore path.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task AgentFactory_WhenProvided_IsInvokedDuringSessionRestore( ) {
        // Arrange — provide stored credentials so TryRestoreSessionAsync is entered
        // (which calls the factory). The closed-port service causes instant failure after
        // the factory call, so the test completes quickly.
        SetupStoredCredentials( BuildRefusingServiceCredentialJson( ) );

        int factoryCallCount = 0;
        Func<BlueskyAgent> factory = ( ) => {
            _ = Interlocked.Increment( ref factoryCallCount );
            return new DisposableTrackingAgent( );
        };

        using RedisATProtoSessionManager manager = CreateManager( factory );

        // Act — call TryRestoreSessionAsync directly to avoid the PerformFreshLoginAsync network path
        _ = await TryRestoreSessionAsync( manager, TestContext.CancellationToken );

        // Assert
        Assert.IsGreaterThanOrEqualTo( factoryCallCount, 1,
            $"agentFactory must be called during session restoration. " +
            $"Factory call count: {factoryCallCount} (seam verification)" );
    }

    /// <summary>
    /// A transport failure (HTTP 502/5xx) on <c>TokenRefreshFailed</c> disposes the failing agent but
    /// does <em>not</em> clear the shared Redis credential, avoiding a fleet-wide re-login storm.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task TokenRefreshFailed_TransportError5xx_DoesNotClearSharedCredential( ) {
        // Arrange
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;

        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        // Act — fire TokenRefreshFailed with BadGateway (502, the observed real-world failure)
        agent.FireTokenRefreshFailed( HttpStatusCode.BadGateway );

        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        // Assert — no credential clear
        Assert.AreEqual( 0, clearCount,
            $"Transport failure (BadGateway/5xx) must NOT clear the shared Redis credential. " +
            $"KeyDeleteAsync was called {clearCount} time(s). " +
            "SA-C: clearing on 5xx forces a fleet-wide re-login storm (observed at ~1.2M events/day)." );

        // Agent must still be disposed (agent torn down, but Redis key kept)
        Assert.IsTrue( agent.WasDisposed,
            "The failing agent must be disposed even when the credential is kept (Directive 2 + SA-C)." );
    }

    /// <summary>
    /// An <c>Unauthenticated</c> event on the current agent disposes the agent, nulls <c>_agent</c>,
    /// and clears the shared credential exactly once (the session has ended).
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task Unauthenticated_OnCurrentAgent_DisposesAgentAndClearsCredential( ) {
        // Arrange
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;

        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        // Act — fire Unauthenticated (session genuinely ended by PDS)
        agent.FireUnauthenticated( );

        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( agent.WasDisposed,
            "Current agent must be disposed after Unauthenticated event (Directive 2). " +
            "Pre-fix: no dispose → agent's timer kept firing after session end." );

        Assert.IsNull( GetAgent( manager ),
            "_agent must be null after Unauthenticated disposes the current agent (Directive 2)." );

        Assert.AreEqual( 1, clearCount,
            "Unauthenticated event (session ended) must clear the shared Redis credential exactly once (SA-C)." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 400 with error name <c>ExpiredToken</c> is a dead-token result, so the
    /// shared credential is cleared.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_400_ExpiredToken_Clears( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        agent.FireTokenRefreshFailed( HttpStatusCode.BadRequest, new AtErrorDetail( "ExpiredToken", "Token has expired." ) );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 1, clearCount,
            "400+ExpiredToken is a dead-token result; shared credential must be cleared." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 400 with error name <c>InvalidToken</c> is a dead-token result, so the
    /// shared credential is cleared.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_400_InvalidToken_Clears( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        agent.FireTokenRefreshFailed( HttpStatusCode.BadRequest, new AtErrorDetail( "InvalidToken", "Token is invalid." ) );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 1, clearCount,
            "400+InvalidToken is a dead-token result; shared credential must be cleared." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 429 (rate limited) is transient, so the shared credential is kept.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_429_RateLimit_Keeps( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        agent.FireTokenRefreshFailed( HttpStatusCode.TooManyRequests, new AtErrorDetail( "RateLimitExceeded", "Too many requests." ) );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 0, clearCount,
            "429 TooManyRequests is transient; shared credential must NOT be cleared." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 408 (request timeout) is transient, so the shared credential is kept.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_408_RequestTimeout_Keeps( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        agent.FireTokenRefreshFailed( HttpStatusCode.RequestTimeout );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 0, clearCount,
            "408 RequestTimeout is transient; shared credential must NOT be cleared." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 400 with a null error body has an unknown error name, so the
    /// conservative default keeps the shared credential.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_400_NullErrorBody_Keeps( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        // null error body — name is unknown, conservative default is KEEP
        agent.FireTokenRefreshFailed( HttpStatusCode.BadRequest, error: null );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 0, clearCount,
            "400 with null error body: error name is unknown; conservative default is KEEP (do not clear)." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 400 with error name <c>RateLimitExceeded</c> is not a known dead-token
    /// name, so the conservative default keeps the shared credential (a band-only "any 4xx clears"
    /// predicate would wrongly clear here).
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_400_RateLimitExceeded_Keeps( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        agent.FireTokenRefreshFailed( HttpStatusCode.BadRequest, new AtErrorDetail( "RateLimitExceeded", "Rate limit." ) );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 0, clearCount,
            "400+RateLimitExceeded is not a known dead-token error name; conservative default is KEEP. " +
            "The old band-only predicate (any 4xx clears) would fail this assertion." );
    }

    /// <summary>
    /// Boundary predicate: HTTP 401 (unauthorized) is a genuine auth rejection (dead-token-by-status),
    /// so the shared credential is cleared.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task BoundaryPredicate_401_Unauthorized_Clears( ) {
        using RedisATProtoSessionManager manager = CreateManager( );
        int clearCount = 0;
        _ = _dbMock
            .Setup( d => d.KeyDeleteAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
            .Callback( ( RedisKey _, CommandFlags __ ) => Interlocked.Increment( ref clearCount ) )
            .ReturnsAsync( true );

        DisposableTrackingAgent agent = new( );
        SetAgent( manager, agent );
        SubscribeToAgentEvents( manager, agent );

        agent.FireTokenRefreshFailed( HttpStatusCode.Unauthorized );
        await Task.Delay( TimeSpan.FromMilliseconds( 300 ), TestContext.CancellationToken );

        Assert.AreEqual( 1, clearCount,
            "401 Unauthorized is a genuine auth rejection (deadTokenByStatus); shared credential must be cleared." );
    }

    /// <summary>
    /// When the distributed lock cannot be acquired within its TTL window,
    /// <c>GetAuthenticatedAgentAsync</c> fails closed by throwing <see cref="InvalidOperationException"/>
    /// and never falls through to a fresh login (the agent factory is not called).
    /// </summary>
    [TestMethod]
    [Timeout( 3000 )]
    public async Task LockWaitTimeout_FailsClosed( ) {
        // Lock acquire always returns false — simulates another holder that never releases.
        SetupLockAcquire( false );

        // Track whether the agent factory is invoked. The factory is only called inside
        // TryRestoreSessionAsync and PerformFreshLoginAsync. If the fail-closed throw is present
        // the method exits before reaching either; if removed, PerformFreshLoginAsync calls it.
        int factoryCallCount = 0;
        Func<BlueskyAgent> trackingFactory = ( ) => {
            _ = Interlocked.Increment( ref factoryCallCount );
            return new DisposableTrackingAgent( );
        };

        // Use accelerated lock timing so the poll loop completes in ~100 ms.
        using RedisATProtoSessionManager manager = new(
            _redisMock.Object,
            _loggerMock.Object,
            TestIdentifier,
            TestPassword,
            protector: null,
            sessionTtlDays: 45,
            agentFactory: trackingFactory
        ) {
            LockExpiry = TimeSpan.FromMilliseconds( 100 ),
            LockPollInterval = TimeSpan.FromMilliseconds( 10 )
        };

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            ( ) => manager.GetAuthenticatedAgentAsync( TestContext.CancellationToken ),
            "AcquireDistributedLockAndRefreshAsync must throw InvalidOperationException when the lock " +
            "cannot be acquired within the TTL window (fail-closed)." );

        Assert.AreEqual( 0, factoryCallCount,
            "The agent factory must not be called when the lock-wait times out. " +
            "A non-zero count means the method fell through to PerformFreshLoginAsync (mutation detected)." );
    }

    /// <summary>
    /// An <see cref="OperationCanceledException"/> raised during the Redis credential read propagates
    /// out of <c>TryRestoreSessionAsync</c> rather than being swallowed and turned into a false return.
    /// </summary>
    [TestMethod]
    [Timeout( 5000 )]
    public async Task TryRestoreSession_WhenCancelledDuringRedisRead_RethrowsOce( ) {
        // Arrange — mock Redis StringGetAsync to throw OCE, simulating cancellation during
        // the credential read step. Using a pre-cancelled token so IsCancellationRequested = true.
        using CancellationTokenSource cts = new( );
        cts.Cancel( );

        _ = _dbMock
            .Setup( d => d.StringGetAsync(
                It.Is<RedisKey>( k => ((string)k!).Contains( "atproto:session:" ) ),
                It.IsAny<CommandFlags>( ) ) )
            .ThrowsAsync( new OperationCanceledException( cts.Token ) );

        using RedisATProtoSessionManager manager = CreateManager( );

        // Act + Assert — OCE must propagate, not be swallowed as false
        bool threw = false;
        try {
            _ = await TryRestoreSessionAsync( manager, cts.Token );
        } catch (OperationCanceledException) {
            threw = true;
        }

        Assert.IsTrue( threw,
            "OperationCanceledException must propagate from TryRestoreSessionAsync when the token is cancelled. " +
            "Pre-fix: the broad catch (Exception ex) swallowed OCE and returned false instead." );
    }

    /// <summary>
    /// Walks the agent's private fields by reflection to locate an options object exposing
    /// <c>EnableBackgroundTokenRefresh</c>, returning its value, or null if no such option is found.
    /// </summary>
    private static bool? FindBackgroundRefreshOption( BlueskyAgent agent ) {
        Type? type = agent.GetType( );
        while (type is not null && type != typeof( object )) {
            foreach (FieldInfo field in type.GetFields( BindingFlags.NonPublic | BindingFlags.Instance )) {
                object? value = field.GetValue( agent );
                if (value is null) { continue; }

                PropertyInfo? prop = value.GetType( )
                    .GetProperty( "EnableBackgroundTokenRefresh" );

                if (prop is not null) {
                    return (bool?)prop.GetValue( value );
                }
            }
            type = type.BaseType;
        }
        return null; // not accessible — source-code audit is the fallback evidence
    }
}

/// <summary>
/// Test double for <see cref="BlueskyAgent"/> that records whether it was disposed and exposes seams
/// to raise the <c>TokenRefreshFailed</c> and <c>Unauthenticated</c> lifecycle events directly.
/// </summary>
internal sealed class DisposableTrackingAgent : BlueskyAgent {

    /// <summary>True once this agent has been disposed.</summary>
    public bool WasDisposed { get; private set; }

    /// <summary>Marks the agent disposed, then runs the base disposal.</summary>
    protected override void Dispose( bool disposing ) {
        WasDisposed = true;
        base.Dispose( disposing );
    }

    /// <summary>
    /// Raises the agent's <c>TokenRefreshFailed</c> event with the given HTTP status and optional
    /// error detail, for exercising the manager's failure handling.
    /// </summary>
    public void FireTokenRefreshFailed( HttpStatusCode? statusCode, AtErrorDetail? error = null ) {
        Did did = new( "did:plc:test12345" );
        TokenRefreshFailedEventArgs args = new(
            did,
            new Uri( "https://test.example" ),
            "fake-refresh-token",
            statusCode,
            error! );
        OnTokenRefreshFailed( args );
    }

    /// <summary>Raises the agent's <c>Unauthenticated</c> event, for exercising session-end handling.</summary>
    public void FireUnauthenticated( ) {
        Did did = new( "did:plc:test12345" );
        UnauthenticatedEventArgs args = new( did, new Uri( "https://test.example" ) );
        OnUnauthenticated( args );
    }
}
