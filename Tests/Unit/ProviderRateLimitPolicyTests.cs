using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Domain.Providers.Common;

namespace BridgeBeats.Tests.Unit;

/// <summary>Contract tests for the single provider rate-limit granularity policy.</summary>
[TestClass]
public sealed class ProviderRateLimitPolicyTests {
    /// <summary>Tracking-key normalization follows provider granularity.</summary>
    [TestMethod]
    [DataRow( SupportedProviders.Spotify, "tracks/:id", ProviderEndpointConstants.ProviderWide )]
    [DataRow( SupportedProviders.Spotify, "BulkAlbums", ProviderEndpointConstants.ProviderWide )]
    [DataRow( SupportedProviders.Spotify, ProviderEndpointConstants.AuthToken, ProviderEndpointConstants.AuthToken )]
    [DataRow( SupportedProviders.AppleMusic, "songs/:id", "songs/:id" )]
    [DataRow( SupportedProviders.Tidal, "albums", "albums" )]
    public void ToTrackingKey_ReturnsProviderPolicyKey(
        SupportedProviders provider,
        string endpoint,
        string expected
    ) {
        Assert.AreEqual( expected, ProviderRateLimitPolicy.ToTrackingKey( provider, endpoint ) );
    }

    /// <summary>A provider-wide data window covers data requests but not authentication.</summary>
    [TestMethod]
    public void Covers_ProviderWideDataLimit_DoesNotCoverAuthentication( ) {
        Assert.IsTrue( ProviderRateLimitPolicy.Covers(
            SupportedProviders.Spotify,
            ProviderEndpointConstants.ProviderWide,
            "albums/:id" ) );
        Assert.IsFalse( ProviderRateLimitPolicy.Covers(
            SupportedProviders.Spotify,
            ProviderEndpointConstants.ProviderWide,
            ProviderEndpointConstants.AuthToken ) );
    }

    /// <summary>Admission uses a fallback scope only for providers whose data quota is global.</summary>
    [TestMethod]
    public void GetAdmissionKey_UsesProviderWideOnlyWhereConcreteEndpointIsUnavailable( ) {
        Assert.AreEqual(
            ProviderEndpointConstants.ProviderWide,
            ProviderRateLimitPolicy.GetAdmissionKey( SupportedProviders.Spotify, null ) );
        Assert.IsNull( ProviderRateLimitPolicy.GetAdmissionKey( SupportedProviders.AppleMusic, null ) );
        Assert.AreEqual(
            "songs/:id",
            ProviderRateLimitPolicy.GetAdmissionKey( SupportedProviders.AppleMusic, "songs/:id" ) );
    }

    /// <summary>A non-covering endpoint set does not block a different endpoint family.</summary>
    [TestMethod]
    public void IsBlocked_NonCoveringEndpointSet_DoesNotBlockRequest( ) {
        Assert.IsFalse( ProviderRateLimitPolicy.IsBlocked(
            SupportedProviders.AppleMusic,
            new HashSet<string>( ["songs/:id"], StringComparer.Ordinal ),
            deferredEndpoint: "albums/:id" ) );
        Assert.IsFalse( ProviderRateLimitPolicy.IsBlocked(
            SupportedProviders.Spotify,
            new HashSet<string>( [ProviderEndpointConstants.ProviderWide], StringComparer.Ordinal ),
            ProviderEndpointConstants.AuthToken ) );
    }

    /// <summary>Matching provider policy keys block deferred data work.</summary>
    [TestMethod]
    public void IsBlocked_MatchingPolicyKeys_BlocksDeferredRequest( ) {
        Assert.IsTrue( ProviderRateLimitPolicy.IsBlocked(
            SupportedProviders.Spotify,
            new HashSet<string>( [ProviderEndpointConstants.ProviderWide], StringComparer.Ordinal ),
            ProviderEndpointConstants.Tracks ) );
        Assert.IsTrue( ProviderRateLimitPolicy.IsBlocked(
            SupportedProviders.AppleMusic,
            new HashSet<string>( ["songs/:id"], StringComparer.Ordinal ),
            "songs/:id" ) );
        Assert.IsFalse( ProviderRateLimitPolicy.IsBlocked(
            SupportedProviders.AppleMusic,
            new HashSet<string>( ["songs/:id"], StringComparer.Ordinal ),
            deferredEndpoint: null ) );
    }
}
