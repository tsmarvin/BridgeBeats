using System.Collections.Specialized;
using System.Web;
using BridgeBeats.Providers.Tidal;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="TidalLinkParser"/> URI builders, verifying that all request templates
/// emit a single comma-separated <c>include</c> query parameter rather than duplicate parameters.
/// The TIDAL Open API is JSON:API-compliant; JSON:API requires the <c>include</c> value to be a
/// single comma-separated list. Duplicate <c>include</c> keys are non-conformant and cause HTTP 400.
/// Also verifies that the non-<c>include</c> parameters survive template substitution unchanged.
/// </summary>
[TestClass]
public class TidalLinkParserTests {

    /// <summary>
    /// Verifies that <c>GetTracksIsrcURI</c> emits exactly one <c>include</c> parameter whose value
    /// is the comma-separated set of albums, artists, and genres, and that the ISRC filter and country code
    /// parameters are preserved.
    /// </summary>
    [TestMethod]
    public void GetTracksIsrcURI_ShouldEmitSingleIncludeParameter( ) {
        // Arrange
        string storefront = "US";
        string isrc = "USRC12345678";

        // Act
        string uri = TidalLinkParser.GetTracksIsrcURI( storefront, isrc );

        // Assert: parse the query string and count 'include' keys
        NameValueCollection parsed = ParseQueryString( uri );
        string[] includeValues = parsed.GetValues( "include" ) ?? [];

        // Exactly one 'include' key — duplicate keys violate JSON:API
        Assert.HasCount( 1, includeValues,
            $"Expected exactly 1 'include' key in '{uri}'. Duplicate include keys violate JSON:API." );

        // The value must be comma-separated and contain all required relationships
        string[] parts = includeValues[0].Split( ',' );
        CollectionAssert.AreEquivalent(
            new[] { "albums", "artists", "genres" },
            parts,
            $"Expected include value to contain exactly albums, artists, and genres; got '{includeValues[0]}'" );

        // The other params must survive the template substitution
        Assert.AreEqual( isrc, parsed["filter[isrc]"],
            "filter[isrc] must be preserved after collapsing include params" );
        Assert.AreEqual( storefront, parsed["countryCode"],
            "countryCode must be preserved after collapsing include params" );
    }

    /// <summary>
    /// Verifies that <c>GetAlbumUpcURI</c> emits exactly one <c>include</c> parameter whose value
    /// is the comma-separated set of artists and coverArt, and that the UPC filter and country code
    /// parameters are preserved.
    /// </summary>
    [TestMethod]
    public void GetAlbumUpcURI_ShouldEmitSingleIncludeParameter( ) {
        // Arrange
        string storefront = "US";
        string upc = "123456789012";

        // Act
        string uri = TidalLinkParser.GetAlbumUpcURI( storefront, upc );

        // Assert: parse the query string and count 'include' keys
        NameValueCollection parsed = ParseQueryString( uri );
        string[] includeValues = parsed.GetValues( "include" ) ?? [];

        Assert.HasCount( 1, includeValues,
            $"Expected exactly 1 'include' key in '{uri}'. Duplicate include keys violate JSON:API." );

        string[] parts = includeValues[0].Split( ',' );
        CollectionAssert.AreEquivalent(
            new[] { "artists", "coverArt" },
            parts,
            $"Expected include value to contain exactly artists and coverArt; got '{includeValues[0]}'" );

        Assert.AreEqual( upc, parsed["filter[barcodeId]"],
            "filter[barcodeId] must be preserved after collapsing include params" );
        Assert.AreEqual( storefront, parsed["countryCode"],
            "countryCode must be preserved after collapsing include params" );
    }

    /// <summary>
    /// Verifies that <c>GetTrackIdURI</c> emits exactly one <c>include</c> parameter whose value
    /// is the comma-separated set of albums, artists, and genres, and that the country code is preserved.
    /// </summary>
    [TestMethod]
    public void GetTrackIdURI_ShouldEmitSingleIncludeParameter( ) {
        // Arrange
        string storefront = "US";
        string trackId = "12345678";

        // Act
        string uri = TidalLinkParser.GetTrackIdURI( storefront, trackId );

        // Assert
        NameValueCollection parsed = ParseQueryString( uri );
        string[] includeValues = parsed.GetValues( "include" ) ?? [];

        Assert.HasCount( 1, includeValues,
            $"Expected exactly 1 'include' key in '{uri}'. Duplicate include keys violate JSON:API." );

        string[] parts = includeValues[0].Split( ',' );
        CollectionAssert.AreEquivalent(
            new[] { "albums", "artists", "genres" },
            parts,
            $"Expected include value to contain exactly albums, artists, and genres; got '{includeValues[0]}'" );

        Assert.AreEqual( storefront, parsed["countryCode"],
            "countryCode must be preserved after collapsing include params" );
    }

    /// <summary>
    /// Verifies that <c>GetAlbumIdURI</c> emits exactly one <c>include</c> parameter whose value
    /// is the comma-separated set of artists and coverArt, and that the country code is preserved.
    /// </summary>
    [TestMethod]
    public void GetAlbumIdURI_ShouldEmitSingleIncludeParameter( ) {
        // Arrange
        string storefront = "GB";
        string albumId = "87654321";

        // Act
        string uri = TidalLinkParser.GetAlbumIdURI( storefront, albumId );

        // Assert
        NameValueCollection parsed = ParseQueryString( uri );
        string[] includeValues = parsed.GetValues( "include" ) ?? [];

        Assert.HasCount( 1, includeValues,
            $"Expected exactly 1 'include' key in '{uri}'. Duplicate include keys violate JSON:API." );

        string[] parts = includeValues[0].Split( ',' );
        CollectionAssert.AreEquivalent(
            new[] { "artists", "coverArt" },
            parts,
            $"Expected include value to contain exactly artists and coverArt; got '{includeValues[0]}'" );

        Assert.AreEqual( storefront, parsed["countryCode"],
            "countryCode must be preserved after collapsing include params" );
    }

    /// <summary>
    /// Suite-wide guard: every Tidal URI builder that emits an <c>include</c> parameter must emit
    /// at most one, with a comma-separated value. This catches the class of bug (duplicate
    /// <c>include</c> keys) across all builders so a future edit cannot silently re-introduce it.
    /// </summary>
    [TestMethod]
    public void AllTidalIncludeUris_ShouldHaveAtMostOneIncludeKey( ) {
        // Arrange: representative arguments for each builder that uses include
        (string label, string uri)[] uris = [
            ("GetTracksIsrcURI", TidalLinkParser.GetTracksIsrcURI( "US", "USRC12345678" )),
            ("GetAlbumUpcURI", TidalLinkParser.GetAlbumUpcURI( "US", "123456789012" )),
            ("GetTrackIdURI", TidalLinkParser.GetTrackIdURI( "US", "12345678" )),
            ("GetAlbumIdURI", TidalLinkParser.GetAlbumIdURI( "US", "87654321" )),
            ("GetArtistSearchUri", TidalLinkParser.GetArtistSearchUri( "US", "Test Artist" )),
            ("GetArtistTracksUri", TidalLinkParser.GetArtistTracksUri( "US", "11111" )),
            ("GetArtistAlbumsUri", TidalLinkParser.GetArtistAlbumsUri( "US", "11111" )),
        ];

        // Assert: each URI that has an include key must have exactly one
        foreach ((string label, string uri) in uris) {
            NameValueCollection parsed = ParseQueryString( uri );
            string[]? values = parsed.GetValues( "include" );

            if (values is null) {
                continue; // No include key — fine
            }

            Assert.HasCount( 1, values,
                $"{label}: expected at most 1 'include' key; got {values.Length} in '{uri}'" );
        }
    }

    /// <summary>
    /// Verifies that a well-formed ISRC produces a URI whose query string round-trips cleanly:
    /// the ISRC value is present and the URI path begins with "tracks". This guards that
    /// collapsing the duplicate include params did not corrupt the rest of the template.
    /// </summary>
    [TestMethod]
    public void GetTracksIsrcURI_WithKnownIsrc_ShouldProduceParseableRequest( ) {
        // Arrange
        string storefront = "CA";
        string isrc = "CAB771234567";

        // Act
        string uri = TidalLinkParser.GetTracksIsrcURI( storefront, isrc );

        // Assert: URI starts with 'tracks' path
        Assert.IsTrue( uri.StartsWith( "tracks", StringComparison.Ordinal ),
            $"Expected URI to begin with 'tracks'; got '{uri}'" );

        // Assert: ISRC value is present in the parsed query
        NameValueCollection parsed = ParseQueryString( uri );
        Assert.AreEqual( isrc, parsed["filter[isrc]"],
            $"Expected filter[isrc]={isrc} in parsed query; got '{parsed["filter[isrc]"]}'" );

        Assert.AreEqual( storefront, parsed["countryCode"],
            $"Expected countryCode={storefront}; got '{parsed["countryCode"]}'" );
    }

    /// <summary>Storefront data cannot inject additional query parameters.</summary>
    [TestMethod]
    public void GetTracksIsrcURI_WithInvalidStorefront_Throws( ) {
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) =>
            TidalLinkParser.GetTracksIsrcURI( "US&include=evil", "USRC12345678" ) );
    }

    /// <summary>External identifiers are escaped before insertion into the query string.</summary>
    [TestMethod]
    public void GetTracksIsrcURI_WithReservedCharacters_EscapesValue( ) {
        string uri = TidalLinkParser.GetTracksIsrcURI( "us", "ABC&countryCode=ZZ" );

        Assert.Contains( "ABC%26countryCode%3DZZ", uri );
        NameValueCollection parsed = ParseQueryString( uri );
        Assert.AreEqual( "ABC&countryCode=ZZ", parsed["filter[isrc]"] );
        Assert.AreEqual( "US", parsed["countryCode"] );
    }

    /// <summary>Native ids are restricted to the numeric shape returned by Tidal URLs.</summary>
    [TestMethod]
    public void GetTrackIdURI_WithNonNumericId_Throws( ) {
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) =>
            TidalLinkParser.GetTrackIdURI( "US", "123/path" ) );
    }

    /// <summary>
    /// Parses the query string from a relative URI (splitting off the path at '?') and decodes
    /// percent-encoded parameter names such as <c>filter%5Bisrc%5D</c> → <c>filter[isrc]</c>.
    /// </summary>
    private static NameValueCollection ParseQueryString( string relativeUri ) {
        int queryStart = relativeUri.IndexOf( '?' );
        string queryPart = queryStart >= 0 ? relativeUri[(queryStart + 1)..] : relativeUri;
        return HttpUtility.ParseQueryString( queryPart );
    }
}

#pragma warning restore CS1591
