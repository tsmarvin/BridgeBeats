using BridgeBeats.Core.Infrastructure.Utilities;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Format-equality drift guards for the <see cref="LookupKeyBuilder"/> methods.
/// Each test pins the exact key format against the legacy literal strings that were inlined in
/// <c>LookupOrchestrator</c> before the extraction, ensuring the builder and the orchestrator
/// produce byte-identical output for the same inputs.
/// </summary>
/// <remarks>
/// Failure-first evidence: these tests were added after the builder methods were introduced.
/// The assertions pin to exact string literals (<c>"IsrcLookup:USRC17600943"</c>,
/// <c>"UpcLookup:abc123"</c>, <c>"SongLookup:BLINDING LIGHTS:THE WEEKND"</c>, etc.) so any
/// change to the key format produces a red test at the format-pinning level before it can
/// silently break saga-id derivation. For example, <c>UpcKey_DoesNotUpperCase</c> asserts
/// exactly <c>"UpcLookup:abc123"</c>, which fails immediately if upper-casing is accidentally
/// added.
/// </remarks>
[TestClass]
public class LookupKeyBuilderTests {

    #region IsrcKey format equality

    /// <summary>
    /// <see cref="LookupKeyBuilder.IsrcKey"/> must produce the exact legacy format
    /// <c>IsrcLookup:{ISRC}</c> where the ISRC is trimmed and upper-cased.
    /// </summary>
    [TestMethod]
    public void IsrcKey_MatchesLegacyInlineFormat( ) {
        const string Isrc = "usrc17600943";
        string expected = $"IsrcLookup:{Isrc.Trim( ).ToUpperInvariant( )}";

        string actual = LookupKeyBuilder.IsrcKey( Isrc );

        Assert.AreEqual( expected, actual );
    }

    /// <summary>
    /// <see cref="LookupKeyBuilder.IsrcKey"/> trims surrounding whitespace before upper-casing,
    /// matching the orchestrator's normalization sequence.
    /// </summary>
    [TestMethod]
    public void IsrcKey_TrimsAndUpperCases( ) {
        string actual = LookupKeyBuilder.IsrcKey( "  usRc17600943  " );

        Assert.AreEqual( "IsrcLookup:USRC17600943", actual );
    }

    #endregion

    #region UpcKey format equality

    /// <summary>
    /// <see cref="LookupKeyBuilder.UpcKey"/> must produce the exact legacy format
    /// <c>UpcLookup:{UPC}</c> where the UPC is trimmed ONLY (no upper-casing).
    /// </summary>
    [TestMethod]
    public void UpcKey_MatchesLegacyInlineFormat( ) {
        const string Upc = "00602547087683";
        string expected = $"UpcLookup:{Upc.Trim( )}";

        string actual = LookupKeyBuilder.UpcKey( Upc );

        Assert.AreEqual( expected, actual );
    }

    /// <summary>
    /// <see cref="LookupKeyBuilder.UpcKey"/> does NOT upper-case — distinguishing it from
    /// <see cref="LookupKeyBuilder.IsrcKey"/>. This guards against accidentally adding
    /// upper-casing that would break the saga-id round-trip for UPC lookups.
    /// </summary>
    [TestMethod]
    public void UpcKey_DoesNotUpperCase( ) {
        string actual = LookupKeyBuilder.UpcKey( "  abc123  " );

        Assert.AreEqual( "UpcLookup:abc123", actual );
    }

    #endregion

    #region MetadataKey format equality

    /// <summary>
    /// <see cref="LookupKeyBuilder.MetadataKey"/> must produce the exact legacy format
    /// <c>SongLookup:{TITLE}:{ARTIST}</c> where both title and artist are trimmed and upper-cased.
    /// </summary>
    [TestMethod]
    public void MetadataKey_MatchesLegacyInlineFormat( ) {
        const string Title = "Blinding Lights";
        const string Artist = "The Weeknd";
        string expected =
            $"SongLookup:{Title.Trim( ).ToUpperInvariant( )}:{Artist.Trim( ).ToUpperInvariant( )}";

        string actual = LookupKeyBuilder.MetadataKey( Title, Artist );

        Assert.AreEqual( expected, actual );
    }

    /// <summary>
    /// <see cref="LookupKeyBuilder.MetadataKey"/> trims and upper-cases both title and artist
    /// independently, confirming neither is omitted.
    /// </summary>
    [TestMethod]
    public void MetadataKey_TrimsAndUpperCasesBothParts( ) {
        string actual = LookupKeyBuilder.MetadataKey( "  blinding lights  ", "  the weeknd  " );

        Assert.AreEqual( "SongLookup:BLINDING LIGHTS:THE WEEKND", actual );
    }

    #endregion

    #region UrlKey round-trip

    /// <summary>
    /// Guards the round-trip between the orchestrator's URL saga key and the probe key derived from
    /// <c>MediaLinkResult.InputLinks[0]</c> in <c>HomeController</c>. The two inputs here are
    /// genuinely different strings: the orchestrator receives the raw submitted URL, which may include
    /// a <c>www.</c> subdomain and a Spotify <c>?si=</c> tracking parameter; the probe builds its key
    /// from a clean canonical form of the same track URL with neither of those variations.
    /// <see cref="HashUtility.HashUrl"/> reconciles them via
    /// <see cref="BridgeBeats.Core.Domain.Providers.Common.LinkNormalizer"/> (strips scheme, strips
    /// <c>www.</c>, removes tracking parameters) so both forms hash to the same saga key.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: replacing <see cref="LookupKeyBuilder.UrlKey"/> on the orchestrator
    /// side with <see cref="HashUtility.ComputeSha256Base32"/> (raw SHA-256 with no normalization)
    /// produces a different hash from the probe side, because the raw strings differ in <c>www.</c>
    /// prefix, scheme form, and query parameter content. The <c>Assert.AreEqual</c> assertion fails.
    /// A constant-returning stub for <c>UrlKey</c> would pass this equality test but fail the
    /// discriminator test below (<see cref="UrlKey_DifferentTracks_ProduceDifferentKeys"/>).
    /// </remarks>
    [TestMethod]
    public void UrlKey_RoundTrip_NormalizationReconcilesWwwPrefixAndTrackingParam( ) {
        // Orchestrator path: receives the URL as submitted, possibly with www. and a tracking param.
        const string SubmittedUrl = "https://www.open.spotify.com/track/4cODK2wGLETKBW3PvgPWqT?si=tracking";
        string orchestratorKey = LookupKeyBuilder.UrlKey( SubmittedUrl );

        // Probe path: InputLinks[0] is built as $"https://{linkGroupCapture}" where linkGroupCapture
        // is the "Link" regex-group value from MediaLinkServiceBase applied to the submitted URL.
        // A submitter of the same track without www. or the tracking param produces a genuinely
        // different string — the clean canonical form. Both must map to the same saga key so the
        // probe can locate the active saga regardless of which URL form reached the orchestrator.
        const string ProbeLinkFromInputLinks = "https://open.spotify.com/track/4cODK2wGLETKBW3PvgPWqT";
        string probeKey = LookupKeyBuilder.UrlKey( ProbeLinkFromInputLinks );

        Assert.AreEqual( orchestratorKey, probeKey,
            "UrlKey must produce the same key for URLs that differ only in www. prefix and " +
            "tracking parameters; HashUrl normalization (via LinkNormalizer) must absorb them." );
    }

    /// <summary>
    /// Discriminator guard: two URLs pointing at genuinely different Spotify tracks must produce
    /// different <see cref="LookupKeyBuilder.UrlKey"/> values. Without this check a
    /// constant-returning stub for <c>UrlKey</c> would pass
    /// <see cref="UrlKey_RoundTrip_NormalizationReconcilesWwwPrefixAndTrackingParam"/> for the
    /// wrong reason.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: a stub <c>UrlKey</c> that returns a constant string passes the
    /// round-trip equality test but fails here immediately on the inequality assertion.
    /// </remarks>
    [TestMethod]
    public void UrlKey_DifferentTracks_ProduceDifferentKeys( ) {
        string keyA = LookupKeyBuilder.UrlKey( "https://open.spotify.com/track/4cODK2wGLETKBW3PvgPWqT" );
        string keyB = LookupKeyBuilder.UrlKey( "https://open.spotify.com/track/DIFFERENTTRACKID0000000" );

        Assert.AreNotEqual( keyA, keyB, "Different track IDs must produce different URL lookup keys." );
    }

    #endregion
}
