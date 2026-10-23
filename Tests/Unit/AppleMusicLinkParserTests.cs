using BridgeBeats.Providers.AppleMusic;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="AppleMusicLinkParser.TryParseUri"/>, which parses an Apple Music web URL into an
/// API request path, a storefront code, and an album-versus-track classification.
/// </summary>
/// <remarks>
/// The load-bearing invariant exercised throughout: an <c>?i=</c> query parameter selects a track
/// within an album, so its presence yields a <c>songs/{id}</c> request and <c>isAlbum = false</c>,
/// while its absence yields an <c>albums/{id}</c> request and <c>isAlbum = true</c>. The parser also
/// extracts the storefront segment (e.g. <c>us</c>, <c>cl</c>) and strips all other query parameters
/// from the resulting request path.
/// </remarks>
[TestClass]
public class AppleMusicLinkParserTests {

    /// <summary>
    /// Verifies that a URL carrying an <c>?i=</c> track selector followed by a bare <c>ls</c> flag
    /// parses as a track: the <c>i</c> value becomes the song id, the storefront is extracted, and the
    /// trailing query is dropped from the request path.
    /// </summary>
    [TestMethod]
    public void TryParseUri_WithTrackIdAndLsQueryParam_ExtractsTrackIdCorrectly( ) {
        // Arrange - URL from issue #1
        string link = "https://music.apple.com/cl/album/rundgang-um-die-transzendentale-saule-der-singularitat/286930912?i=286931431&ls";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsFalse( isAlbum, "Should recognize this as a track (not album) due to ?i= parameter" );
        Assert.AreEqual( "cl", storefront, "Should extract 'cl' as storefront" );
        Assert.AreEqual( "cl/songs/286931431", requestUri, "Should extract song ID '286931431' without query parameters" );
    }

    /// <summary>
    /// Verifies that a URL with an <c>?i=</c> track selector plus a <c>uo</c> query parameter parses as
    /// a track, taking the song id from <c>i</c> and discarding the <c>uo</c> parameter.
    /// </summary>
    [TestMethod]
    public void TryParseUri_WithTrackIdAndUoQueryParam_ExtractsTrackIdCorrectly( ) {
        // Arrange - URL from issue #2
        string link = "https://music.apple.com/au/album/apricots/1531704818?i=1531704822&uo=4";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsFalse( isAlbum, "Should recognize this as a track (not album) due to ?i= parameter" );
        Assert.AreEqual( "au", storefront, "Should extract 'au' as storefront" );
        Assert.AreEqual( "au/songs/1531704822", requestUri, "Should extract song ID '1531704822' without query parameters" );
    }

    /// <summary>
    /// Verifies that a URL whose only query parameter is the <c>?i=</c> track selector parses as a
    /// track, producing a <c>songs/{id}</c> request from the <c>i</c> value.
    /// </summary>
    [TestMethod]
    public void TryParseUri_WithTrackIdWithoutQueryParams_ExtractsTrackIdCorrectly( ) {
        // Arrange - URL without additional query parameters
        string link = "https://music.apple.com/us/album/test-album/123456?i=789012";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsFalse( isAlbum, "Should recognize this as a track (not album) due to ?i= parameter" );
        Assert.AreEqual( "us", storefront, "Should extract 'us' as storefront" );
        Assert.AreEqual( "us/songs/789012", requestUri, "Should extract song ID '789012'" );
    }

    /// <summary>
    /// Verifies that a URL with query parameters but no <c>?i=</c> selector parses as an album, taking
    /// the path id as the album id and dropping the unrelated query parameters.
    /// </summary>
    [TestMethod]
    public void TryParseUri_WithAlbumIdAndQueryParams_ExtractsAlbumIdCorrectly( ) {
        // Arrange - Album URL with query parameters (no ?i= so should treat as album)
        string link = "https://music.apple.com/us/album/test-album/123456?l=en&app=music";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsTrue( isAlbum, "Should recognize this as an album (no ?i= parameter)" );
        Assert.AreEqual( "us", storefront, "Should extract 'us' as storefront" );
        Assert.AreEqual( "us/albums/123456", requestUri, "Should extract album ID '123456' without query parameters" );
    }

    /// <summary>
    /// Verifies that, given several query parameters including <c>?i=</c>, the parser still classifies
    /// the URL as a track and extracts the <c>i</c> value as the song id, ignoring all other parameters.
    /// </summary>
    [TestMethod]
    public void TryParseUri_WithMultipleQueryParams_ExtractsIdCorrectly( ) {
        // Arrange - URL with multiple query parameters
        string link = "https://music.apple.com/jp/album/test/999?i=111&at=1000l3&app=music&ls=1";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsFalse( isAlbum, "Should recognize this as a track due to ?i= parameter" );
        Assert.AreEqual( "jp", storefront, "Should extract 'jp' as storefront" );
        Assert.AreEqual( "jp/songs/111", requestUri, "Should extract song ID '111' without any query parameters" );
    }

    /// <summary>
    /// Verifies that an album URL whose query consists only of non-selector parameters (<c>ls</c>,
    /// <c>app</c>) parses as an album, keeping the path id as the album id.
    /// </summary>
    [TestMethod]
    public void TryParseUri_AlbumWithQueryParamsOnly_ExtractsAlbumIdCorrectly( ) {
        // Arrange - Album URL with query parameters but no ?i= (should be treated as album)
        string link = "https://music.apple.com/us/album/test-album/286930912?ls=1&app=music";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsTrue( isAlbum, "Should recognize this as an album (no ?i= parameter)" );
        Assert.AreEqual( "us", storefront, "Should extract 'us' as storefront" );
        Assert.AreEqual( "us/albums/286930912", requestUri, "Should extract album ID '286930912' without query parameters" );
    }

    /// <summary>
    /// Verifies that an album URL carrying an affiliate <c>at</c> parameter (and <c>uo</c>) but no
    /// <c>?i=</c> selector parses as an album, taking the path id as the album id.
    /// </summary>
    [TestMethod]
    public void TryParseUri_AlbumWithAtQueryParam_ExtractsAlbumIdCorrectly( ) {
        // Arrange - Album URL with affiliate token query parameter
        string link = "https://music.apple.com/gb/album/test/1234567890?at=1000l3Hc&uo=4";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsTrue( isAlbum, "Should recognize this as an album (no ?i= parameter)" );
        Assert.AreEqual( "gb", storefront, "Should extract 'gb' as storefront" );
        Assert.AreEqual( "gb/albums/1234567890", requestUri, "Should extract album ID '1234567890' without query parameters" );
    }

    /// <summary>
    /// Verifies that a direct <c>/song/</c> URL (rather than an album URL with an <c>?i=</c> selector)
    /// parses as a track, taking the path id as the song id and stripping the query parameters.
    /// </summary>
    [TestMethod]
    public void TryParseUri_DirectSongUrlWithQueryParams_ExtractsSongIdCorrectly( ) {
        // Arrange - Direct song URL (not album with ?i=) with query parameters
        string link = "https://music.apple.com/us/song/test-song/987654321?at=1000l3Hc&app=music";

        // Act
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        // Assert
        Assert.IsTrue( result, "Should successfully parse the Apple Music URL" );
        Assert.IsFalse( isAlbum, "Should recognize this as a song" );
        Assert.AreEqual( "us", storefront, "Should extract 'us' as storefront" );
        Assert.AreEqual( "us/songs/987654321", requestUri, "Should extract song ID '987654321' without query parameters" );
    }

    /// <summary>
    /// Verifies that <see cref="AppleMusicLinkParser.TryParseUri"/> returns <see langword="false"/>
    /// and does NOT throw when a URL matches the album URL regex (via the loose <c>\w+</c> storefront
    /// capture) but carries a storefront value that fails strict validation. Before the fix,
    /// <c>TryParseUri</c> called <c>GetAlbumsURI</c> directly, which threw
    /// <see cref="ArgumentException"/> on the invalid storefront—breaking the <c>bool Try*</c>
    /// contract and propagating the exception into the queue-processor loop.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before the guard was added to <c>TryParseUri</c>, this call threw
    /// <see cref="ArgumentException"/> (validator rejection inside <c>GetAlbumsURI</c>); it now
    /// returns <see langword="false"/> without throwing.
    /// </remarks>
    [TestMethod]
    public void TryParseUri_RegexMatchButInvalidStorefront_ReturnsFalseWithoutThrowing( ) {
        // Arrange - storefront "foo_bar" matches the loose \w+ album regex but fails ValidateStorefront
        // (underscore is not a letter; length exceeds 3). The id "abc123" also fails ValidateCatalogId
        // (non-numeric). Either failure is sufficient; the storefront check is the first guard.
        string link = "https://music.apple.com/foo_bar/album/test-album/abc123";

        // Act / Assert - must not throw; must return false
        bool result = AppleMusicLinkParser.TryParseUri( link, out string requestUri, out string storefront, out bool isAlbum );

        Assert.IsFalse( result, "TryParseUri must return false for an invalid storefront, not throw" );
        Assert.AreEqual( string.Empty, requestUri, "requestUri must be empty on a false return" );
        Assert.AreEqual( string.Empty, storefront, "storefront must be empty on a false return" );
        Assert.IsFalse( isAlbum, "isAlbum must be false on a false return" );
    }

    /// <summary>
    /// Verifies that URL builder methods reject a storefront value containing URL injection characters
    /// (<c>&amp;</c>, <c>/</c>, <c>?</c>, and a percent-encoded separator). Before the validation fix,
    /// these builders performed raw string substitution, so an attacker-controlled storefront could
    /// inject extra query parameters or path segments into the JWT-bearing outbound Apple Music API
    /// request. Post-fix, each builder validates against the expected 2–3-letter alphabetic pattern
    /// and throws <see cref="ArgumentException"/>, ensuring no injected character survives into the
    /// built URL.
    /// </summary>
    [TestMethod]
    public void UrlBuilders_StorefrontWithInjectionCharacters_ThrowArgumentException( ) {
        // Arrange - storefronts containing characters that could alter URL structure
        string[] maliciousStorefronts = [
            "us&evil=1",         // query-parameter injection via &
            "us/../../etc",      // path traversal via /
            "us?extra=param",    // query string injection via ?
            "us%2fevil",         // percent-encoded path separator
        ];

        foreach (string badStorefront in maliciousStorefronts) {
            // Assert - every builder must reject the bad storefront; no injection survives into any URL.
            // Failure-first: before validation was added, these methods performed bare .Replace()
            // and returned a URL containing the unescaped injection; they did NOT throw.
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetSongIdUri( badStorefront, "12345" )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetAlbumIdUri( badStorefront, "12345" )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetSongsIsrcURI( badStorefront, "USRC12345678" )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetAlbumUpcURI( badStorefront, "012345678901" )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetArtistAlbumsURI( badStorefront, "12345" )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetArtistSongsURI( badStorefront, "12345" )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetArtistSearchUri( badStorefront, "Test Artist" )
            );
        }
    }

    /// <summary>
    /// Verifies that URL builder methods reject an identifier value containing URL injection characters
    /// (<c>&amp;</c>, <c>/</c>, <c>?</c>, and a percent-encoded separator). Post-fix, numeric catalog
    /// ids are validated against <c>^[0-9]+$</c> and throw <see cref="ArgumentException"/> on any
    /// non-numeric content, so no injected character survives into the built URL.
    /// </summary>
    [TestMethod]
    public void UrlBuilders_IdWithInjectionCharacters_ThrowArgumentException( ) {
        // Arrange - ids containing characters that could alter URL structure
        string[] maliciousIds = [
            "12345&evil=1",      // query-parameter injection via &
            "12345/../../etc",   // path traversal via /
            "12345?extra=param", // query string injection via ?
            "12345%2fevil",      // percent-encoded path separator
        ];

        foreach (string badId in maliciousIds) {
            // Assert - song and album id builders must both reject the bad id.
            // Failure-first: before validation, these returned a URL with the raw injection; they
            // did NOT throw.
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetSongIdUri( "us", badId )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetAlbumIdUri( "us", badId )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetArtistAlbumsURI( "us", badId )
            );
            _ = Assert.ThrowsExactly<ArgumentException>(
                ( ) => AppleMusicLinkParser.GetArtistSongsURI( "us", badId )
            );
        }
    }
}
