using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.LinkResolver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="LookupProgressProbe"/> and <see cref="NullLookupProgressProbe"/>.
/// The probe predicate has three ANDed conditions — saga not null, not complete, no FinalResultUri —
/// and each is tested independently so no single condition is silently masked by another.
/// </summary>
/// <remarks>
/// Failure-first evidence for each new test is documented inline. Case (b) is the canonical
/// failure-first case: a naive <c>saga != null</c> predicate returns <see langword="true"/> for a
/// complete saga whose PDS write is still in flight, but the correct predicate returns
/// <see langword="false"/>. The five probe tests were written against a naive
/// <c>return saga is not null;</c> stub, which fails cases (b) and (c). Case (c2) isolates the
/// <c>FinalResultUri</c> clause: case (c) already has <c>IsComplete = true</c>, which causes the
/// <c>!saga.IsComplete</c> arm to short-circuit before reaching <c>string.IsNullOrEmpty</c>; case
/// (c2) reverses that so the <c>FinalResultUri</c> clause is the only failing condition and thus has
/// its own independent negative control. Case (d) does not distinguish the naive from the correct
/// predicate (both return <see langword="false"/> for a null saga); it is included as a completeness
/// guard.
/// </remarks>
[TestClass]
public class LookupProgressProbeTests {

    private const string TestLookupKey = "IsrcLookup:USRC12345678";

    private Mock<ISagaStateManager> _sagaManagerMock = null!;
    private LookupProgressProbe _probe = null!;

    /// <summary>Provides <see cref="TestContext.CancellationToken"/> for cooperative cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Initializes mocks and the probe under test before each test method.</summary>
    [TestInitialize]
    public void Setup( ) {
        _sagaManagerMock = new Mock<ISagaStateManager>( );
        _probe = new LookupProgressProbe( _sagaManagerMock.Object, NullLogger<LookupProgressProbe>.Instance );
    }

    #region Constructor guard

    /// <summary>Passing <see langword="null"/> for the saga manager throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_NullSagaManager_Throws( ) {
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) => new LookupProgressProbe( null!, NullLogger<LookupProgressProbe>.Instance ) );
    }

    #endregion

    #region Probe predicate cases

    /// <summary>
    /// Case (a): active saga (not complete, no FinalResultUri) → <see langword="true"/>.
    /// Failure-first: against naive <c>saga != null</c> this would also return true — the test
    /// passes for the wrong reason. The correct predicate passes for the right reason (all three
    /// conditions hold). Both predicates agree on this case; see case (b) for the divergence.
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaIsActiveAndNonComplete_ReturnsTrue( ) {
        LookupSagaState saga = BuildSaga( isComplete: false, finalResultUri: null );
        SetupGetAsync( TestLookupKey, saga );

        bool result = await _probe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsTrue( result );
    }

    /// <summary>
    /// Case (b): all providers have reported in (<see cref="LookupSagaState.IsComplete"/> is
    /// <see langword="true"/>) but the PDS write is still in flight so
    /// <see cref="LookupSagaState.FinalResultUri"/> is <see langword="null"/> — the finalize race.
    /// Correct predicate → <see langword="false"/>. Failure-first evidence: a naive
    /// <c>saga != null</c> predicate returns <see langword="true"/> here (false-positive).
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaIsCompleteFinalizeRace_ReturnsFalse( ) {
        LookupSagaState saga = BuildSaga( isComplete: true, finalResultUri: null );
        SetupGetAsync( TestLookupKey, saga );

        bool result = await _probe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsFalse( result,
            "A saga that is IsComplete must not trigger the in-progress indicator, " +
            "even when FinalResultUri has not yet been set (finalize race)." );
    }

    /// <summary>
    /// Case (c): saga has a <see cref="LookupSagaState.FinalResultUri"/> set → lookup is done,
    /// indicator must not show. Correct predicate → <see langword="false"/>.
    /// Failure-first: a naive <c>saga != null</c> returns <see langword="true"/> here.
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaIsFinalizedWithUri_ReturnsFalse( ) {
        LookupSagaState saga = BuildSaga( isComplete: true, finalResultUri: "at://did/collection/rkey" );
        SetupGetAsync( TestLookupKey, saga );

        bool result = await _probe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsFalse( result,
            "A saga that has a FinalResultUri must not trigger the in-progress indicator." );
    }

    /// <summary>
    /// Case (c2): saga is not yet complete but already has a
    /// <see cref="LookupSagaState.FinalResultUri"/> — an atypical race where the finalize step
    /// wrote the URI without setting <see cref="LookupSagaState.IsComplete"/>. The
    /// <c>string.IsNullOrEmpty(saga.FinalResultUri)</c> clause must independently reject this saga.
    /// Correct predicate → <see langword="false"/>.
    /// Failure-first: removing <c>&amp;&amp; string.IsNullOrEmpty(saga.FinalResultUri)</c> from the
    /// predicate causes this test to return <see langword="true"/> (<see cref="LookupSagaState.IsComplete"/>
    /// is <see langword="false"/>, so only the null-check fires); all other cases remain green because
    /// every other false-returning case is already handled by a different clause.
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaIncompleteButHasFinalResultUri_ReturnsFalse( ) {
        LookupSagaState saga = BuildSaga( isComplete: false, finalResultUri: "at://did/collection/rkey" );
        SetupGetAsync( TestLookupKey, saga );

        bool result = await _probe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsFalse( result,
            "A saga that has a FinalResultUri must not trigger the in-progress indicator, " +
            "even when IsComplete is false." );
    }

    /// <summary>
    /// Case (d): <see cref="ISagaStateManager.GetAsync"/> returns <see langword="null"/> (saga
    /// does not exist or has expired) → <see langword="false"/>.
    /// Failure-first: a naive <c>saga != null</c> returns <see langword="false"/> here, so this
    /// case does not distinguish the two predicates. Included as a completeness guard.
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaDoesNotExist_ReturnsFalse( ) {
        SetupGetAsync( TestLookupKey, null );

        bool result = await _probe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsFalse( result );
    }

    /// <summary>
    /// Case (e): <see cref="ISagaStateManager.GetAsync"/> throws (transient saga-store fault).
    /// The probe must return <see langword="false"/> — "cannot determine → not in progress" —
    /// matching the graceful-degradation intent. A render-path fault must not convert a successful
    /// lookup into an error response.
    /// Failure-first: before the try/catch was added, the exception propagated out of
    /// <see cref="LookupProgressProbe.IsActiveAsync"/> and this test threw instead of asserting
    /// <see langword="false"/>; passes after the fault-tolerance fix.
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaStoreThrows_ReturnsFalse( ) {
        string sagaId = ISagaStateManager.GenerateSagaId( TestLookupKey );
        _ = _sagaManagerMock
            .Setup( m => m.GetAsync( sagaId, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Redis connection failed" ) );

        bool result = await _probe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsFalse( result, "A saga-store fault must produce false (not in progress), not an exception." );
    }

    /// <summary>
    /// A cancelled saga-store read belongs to the request lifecycle and must degrade quietly to
    /// <see langword="false"/> rather than turning request or host shutdown into a failure.
    /// </summary>
    [TestMethod]
    public async Task IsActiveAsync_WhenSagaStoreReadIsCancelled_ReturnsFalse( ) {
        using CancellationTokenSource cts = new( );
        cts.Cancel( );
        string sagaId = ISagaStateManager.GenerateSagaId( TestLookupKey );
        _ = _sagaManagerMock
            .Setup( m => m.GetAsync( sagaId, cts.Token ) )
            .ThrowsAsync( new OperationCanceledException( cts.Token ) );

        bool result = await _probe.IsActiveAsync( TestLookupKey, cts.Token );

        Assert.IsFalse( result );
    }

    #endregion

    #region NullLookupProgressProbe

    /// <summary>
    /// <see cref="NullLookupProgressProbe.IsActiveAsync"/> always returns <see langword="false"/>
    /// regardless of the key and without calling any underlying service.
    /// </summary>
    [TestMethod]
    public async Task NullLookupProgressProbe_AlwaysReturnsFalse( ) {
        NullLookupProgressProbe nullProbe = new( );

        bool result = await nullProbe.IsActiveAsync( TestLookupKey, TestContext.CancellationToken );

        Assert.IsFalse( result );
    }

    /// <summary>
    /// <see cref="NullLookupProgressProbe"/> returns <see langword="false"/> even for an
    /// arbitrary key value, confirming the no-op contract is not key-dependent.
    /// </summary>
    [TestMethod]
    public async Task NullLookupProgressProbe_ArbitraryKey_ReturnsFalse( ) {
        NullLookupProgressProbe nullProbe = new( );

        bool result = await nullProbe.IsActiveAsync( "UpcLookup:000000000000", TestContext.CancellationToken );

        Assert.IsFalse( result );
    }

    #endregion

    #region Helpers

    private void SetupGetAsync( string lookupKey, LookupSagaState? returnValue ) {
        string sagaId = ISagaStateManager.GenerateSagaId( lookupKey );
        _ = _sagaManagerMock
            .Setup( m => m.GetAsync( sagaId, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( returnValue );
    }

    private static LookupSagaState BuildSaga( bool isComplete, string? finalResultUri ) {
        Dictionary<SupportedProviders, ProviderLookupState> providerStates = new( ) {
            [SupportedProviders.Spotify] = new ProviderLookupState(
                Provider: SupportedProviders.Spotify,
                IsComplete: isComplete,
                IsSuccess: isComplete,
                ResultJson: null,
                CompletedAt: null,
                ErrorMessage: null
            )
        };

        return new LookupSagaState {
            SagaId = ISagaStateManager.GenerateSagaId( TestLookupKey ),
            LookupKey = TestLookupKey,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "USRC12345678",
            ProviderStates = providerStates,
            FinalResultUri = finalResultUri
        };
    }

    #endregion
}
