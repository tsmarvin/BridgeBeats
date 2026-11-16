#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Verifies how <see cref="TestHostConfiguration"/> classifies the provider credentials a machine
/// supplies, because the classification decides whether a live test runs, reports itself inconclusive,
/// or fails.
/// </summary>
/// <remarks>
/// The distinction that matters is between credentials that are absent and credentials that are present
/// but unusable. Absent credentials are a property of the machine, so the live tests that need them
/// report inconclusive; a credential the application cannot use is a configuration error, and reporting
/// it as inconclusive would let a broken setup pass the suite.
/// </remarks>
[TestClass]
public class TestHostCredentialStateTests {

    /// <summary>A complete pair is usable.</summary>
    [TestMethod]
    public void CompletePair_IsUsable( ) {
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Usable,
            TestHostConfiguration.ClassifyPair( "client-id", "client-secret" ) );
    }

    /// <summary>A pair with neither half set is absent, not unusable.</summary>
    [TestMethod]
    public void AbsentPair_IsAbsent( ) {
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Absent,
            TestHostConfiguration.ClassifyPair( null, null ) );
    }

    /// <summary>
    /// Half a pair is unusable: the application cannot authenticate with it, so the live test must fail
    /// rather than skip and hide the incomplete configuration.
    /// </summary>
    [TestMethod]
    public void PartialPair_IsUnusable( ) {
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Unusable,
            TestHostConfiguration.ClassifyPair( "client-id", null ),
            "an id without its secret cannot authenticate" );
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Unusable,
            TestHostConfiguration.ClassifyPair( null, "client-secret" ),
            "a secret without its id cannot authenticate" );
    }

    /// <summary>A complete Apple tuple whose signing key exists is usable.</summary>
    [TestMethod]
    public void CompleteAppleTuple_WithReadableKey_IsUsable( ) {
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Usable,
            TestHostConfiguration.ClassifyApple( "TEAM", "KEY", "/keys/AuthKey.p8", _ => true ) );
    }

    /// <summary>
    /// A complete Apple tuple whose signing key is missing is unusable. Apple Music registration opens
    /// the key and aborts start-up when it is absent, so this cannot be treated as an absent credential.
    /// </summary>
    [TestMethod]
    public void CompleteAppleTuple_WithMissingKey_IsUnusable( ) {
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Unusable,
            TestHostConfiguration.ClassifyApple( "TEAM", "KEY", "/keys/AuthKey.p8", _ => false ) );
    }

    /// <summary>An incomplete Apple tuple is absent, so live Apple tests report inconclusive.</summary>
    [TestMethod]
    public void IncompleteAppleTuple_IsAbsent( ) {
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Absent,
            TestHostConfiguration.ClassifyApple( "TEAM", null, "/keys/AuthKey.p8", _ => true ),
            "a team id alone is not a credential" );
        Assert.AreEqual(
            TestHostConfiguration.LiveCredentialState.Absent,
            TestHostConfiguration.ClassifyApple( null, null, null, _ => true ),
            "no Apple settings at all is not a credential" );
    }
}
#pragma warning restore CS1591
