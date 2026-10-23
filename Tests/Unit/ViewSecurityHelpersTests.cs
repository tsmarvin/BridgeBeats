using System.Text.Encodings.Web;
using System.Text.Json;
using BridgeBeats.Web;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ViewSecurityHelpers"/>. Covers the href scheme allowlist
/// (<see cref="ViewSecurityHelpers.IsHttpOrHttps"/>) and the HTML-safe JSON encoder pinning
/// (<see cref="ViewSecurityHelpers.HtmlSafeJsonOptions"/>).
/// </summary>
[TestClass]
public class ViewSecurityHelpersTests {

    // -----------------------------------------------------------------------
    // IsHttpOrHttps — scheme allowlist (Finding 1 negative controls)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that an <c>https:</c> URL is accepted by the allowlist.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_HttpsUrl_ReturnsTrue( ) {
        Assert.IsTrue( ViewSecurityHelpers.IsHttpOrHttps( "https://open.spotify.com/track/abc123" ) );
    }

    /// <summary>
    /// Verifies that a plain <c>http:</c> URL is accepted by the allowlist.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_HttpUrl_ReturnsTrue( ) {
        Assert.IsTrue( ViewSecurityHelpers.IsHttpOrHttps( "http://music.apple.com/us/album/abc/123" ) );
    }

    /// <summary>
    /// Negative control: a <c>javascript:</c>-scheme URL must be rejected so that a provider
    /// URL containing this scheme never reaches the rendered <c>href</c> attribute.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_JavascriptScheme_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "javascript:alert(1)" ) );
    }

    /// <summary>
    /// Negative control: a <c>data:</c>-scheme URI must be rejected.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_DataScheme_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "data:text/html,<script>alert(1)</script>" ) );
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> value is rejected.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_Null_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( null ) );
    }

    /// <summary>
    /// Verifies that an empty string is rejected.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_EmptyString_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( string.Empty ) );
    }

    /// <summary>
    /// Verifies that a whitespace-only string is rejected.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_WhitespaceString_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "   " ) );
    }

    /// <summary>
    /// Verifies that a relative path is rejected (not an absolute URI).
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_RelativePath_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "/card/abc123" ) );
    }

    /// <summary>
    /// Verifies that a <c>ftp:</c>-scheme URI is rejected.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_FtpScheme_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "ftp://files.example.com/track.mp3" ) );
    }

    /// <summary>
    /// Negative control (Bundle E): a scheme-relative URL (<c>//evil.com/path</c>) must be
    /// rejected. Scheme-relative URLs have no explicit scheme; <c>Uri.TryCreate</c>
    /// with <see cref="UriKind.Absolute"/> does not accept them, so they never reach the
    /// scheme comparison and return <see langword="false"/>.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_SchemeRelativeUrl_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "//evil.com/path" ) );
    }

    /// <summary>
    /// Negative control (Bundle E): a <c>mailto:</c>-scheme URI must be rejected because
    /// its scheme is neither <c>http</c> nor <c>https</c>.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_MailtoScheme_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "mailto:foo@example.com" ) );
    }

    /// <summary>
    /// Negative control (Bundle E): a <c>vbscript:</c>-scheme URI must be rejected.
    /// This pins the named attack vector alongside the existing <c>javascript:</c> test.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_VbscriptScheme_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( "vbscript:msgbox(1)" ) );
    }

    /// <summary>
    /// Negative control (Bundle E): a UNC-style backslash path (<c>\\evil.com</c>) must be
    /// rejected. <c>Uri.TryCreate</c> with <see cref="UriKind.Absolute"/> does not
    /// treat backslash-prefixed strings as absolute URIs with an http/https scheme.
    /// </summary>
    [TestMethod]
    public void IsHttpOrHttps_BackslashUncPath_ReturnsFalse( ) {
        Assert.IsFalse( ViewSecurityHelpers.IsHttpOrHttps( @"\\evil.com" ) );
    }

    // -----------------------------------------------------------------------
    // HtmlSafeJsonOptions — encoder-pinning (Finding 2 negative controls)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that the encoder on <see cref="ViewSecurityHelpers.HtmlSafeJsonOptions"/> is the
    /// strict HTML-safe default (<see cref="JavaScriptEncoder.Default"/>), not a relaxed variant.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_Encoder_IsStrictDefault( ) {
        Assert.AreEqual(
            JavaScriptEncoder.Default,
            ViewSecurityHelpers.HtmlSafeJsonOptions.Encoder );
    }

    /// <summary>
    /// Negative control: a title containing an HTML script break-out sequence
    /// (<c>&lt;/script&gt;&lt;img src=x onerror=alert(1)&gt;</c>) must be fully escaped
    /// by the pinned encoder — the emitted JSON string must not contain any live tag characters.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_ScriptBreakoutTitle_IsEscapedInOutput( ) {
        string maliciousTitle = "</script><img src=x onerror=alert(1)>";

        string serialized = JsonSerializer.Serialize( maliciousTitle, ViewSecurityHelpers.HtmlSafeJsonOptions );

        // JavaScriptEncoder.Default escapes '<' as < and '>' as >.
        // The emitted bytes must not contain literal '<' or '>' characters.
        Assert.DoesNotContain( "<", serialized );
        Assert.DoesNotContain( ">", serialized );
        // The six-character Unicode escape sequences must be present in the output.
        Assert.IsTrue( serialized.Contains( "\\u003C", StringComparison.Ordinal ),
            $"Expected \\u003C in: {serialized}" );
        Assert.IsTrue( serialized.Contains( "\\u003E", StringComparison.Ordinal ),
            $"Expected \\u003E in: {serialized}" );
    }

    /// <summary>
    /// Verifies that a benign title serializes to a valid JSON string that round-trips correctly,
    /// confirming the pinned encoder does not corrupt normal values.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_BenignTitle_RoundTripsCorrectly( ) {
        string title = "My Favourite Song";

        string serialized = JsonSerializer.Serialize( title, ViewSecurityHelpers.HtmlSafeJsonOptions );
        string? deserialized = JsonSerializer.Deserialize<string>( serialized );

        Assert.AreEqual( title, deserialized );
    }

    /// <summary>
    /// Verifies that the same <see cref="ViewSecurityHelpers.HtmlSafeJsonOptions"/> instance is
    /// returned on repeated accesses (it is a static field, not a factory method).
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_IsSingletonInstance( ) {
        JsonSerializerOptions firstAccess = ViewSecurityHelpers.HtmlSafeJsonOptions;
        JsonSerializerOptions secondAccess = ViewSecurityHelpers.HtmlSafeJsonOptions;

        Assert.AreSame( firstAccess, secondAccess );
    }

    // -----------------------------------------------------------------------
    // data-title XSS negative controls (SEC-E-001)
    //
    // The fix changes the HTML attribute delimiter from double-quote to single-
    // quote: data-title='<serialized-json>'.  JavaScriptEncoder.Default escapes
    // any literal single-quote (U+0027) in the JSON output as ', so a
    // payload cannot close the attribute early.  The tests below verify both
    // that the XSS payloads are contained and that benign values round-trip.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Negative control (stored XSS): a title that embeds an event-handler payload
    /// (<c>x onfocus=alert(1) autofocus </c>) must NOT produce a literal single-quote
    /// in the serialized output, so it cannot close a single-quoted HTML attribute and
    /// forge a new attribute on the element.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_XssEventHandlerPayload_ContainsNoLiteralSingleQuote( ) {
        string xssTitle = "x onfocus=alert(1) autofocus ";

        string serialized = JsonSerializer.Serialize( xssTitle, ViewSecurityHelpers.HtmlSafeJsonOptions );

        // The serialized value must not contain a raw single-quote; if it did,
        // placing it inside a single-quoted attribute would close the attribute early.
        Assert.IsFalse(
            serialized.Contains( '\'', StringComparison.Ordinal ),
            $"Serialized value must not contain a literal single-quote; got: {serialized}" );

        // The JSON must still be a valid parseable string (content is preserved for the client).
        string? deserialized = JsonSerializer.Deserialize<string>( serialized );
        Assert.AreEqual( xssTitle, deserialized );
    }

    /// <summary>
    /// Negative control: a title containing an interior single-quote (apostrophe) must have
    /// that character escaped in the serialized output, so it cannot close the single-quoted
    /// HTML attribute delimiter and forge event-handler attributes.
    /// <para>
    /// <see cref="JavaScriptEncoder.Default"/> escapes U+0027 as <c>'</c>; the
    /// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> variant does NOT — which is
    /// why this project pins <see cref="JavaScriptEncoder.Default"/> in
    /// <see cref="ViewSecurityHelpers.HtmlSafeJsonOptions"/>.
    /// </para>
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_TitleWithApostrophe_SingleQuoteIsEscapedInOutput( ) {
        string title = "it's a great song";

        string serialized = JsonSerializer.Serialize( title, ViewSecurityHelpers.HtmlSafeJsonOptions );

        // No literal single-quote must survive into the output.
        Assert.IsFalse(
            serialized.Contains( '\'', StringComparison.Ordinal ),
            $"Literal single-quote found in serialized output; got: {serialized}" );

        // The escape sequence ' must be present, proving the encoder handled the apostrophe.
        Assert.IsTrue(
            serialized.Contains( "\\u0027", StringComparison.Ordinal ),
            $"Expected \\u0027 escape sequence in: {serialized}" );

        // Round-trip correctness: JSON.parse on the client receives the original string.
        string? deserialized = JsonSerializer.Deserialize<string>( serialized );
        Assert.AreEqual( title, deserialized );
    }

    /// <summary>
    /// Confirms that a double-quoted attribute delimiter (the OLD behaviour before SEC-E-001) would
    /// emit the serialized JSON with its own surrounding double-quotes unescaped, collapsing the
    /// attribute value to empty.  This test documents WHY the old pattern was unsafe — and asserts
    /// the property of the encoder that the fix relies on.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_SerializedStringBeginsAndEndsWithDoubleQuote( ) {
        string title = "My Playlist";

        string serialized = JsonSerializer.Serialize( title, ViewSecurityHelpers.HtmlSafeJsonOptions );

        // JsonSerializer.Serialize for a string produces a JSON string token including the
        // surrounding double-quotes, e.g. "My Playlist".  Placing that verbatim inside a
        // double-quoted HTML attribute (data-title="...") collapses the attribute to empty
        // because the JSON's leading double-quote immediately closes the HTML attribute.
        // Placing it inside a single-quoted attribute (data-title='...') avoids the collapse.
        Assert.IsTrue( serialized.StartsWith( '"' ), $"Expected serialized output to start with a double-quote; got: {serialized}" );
        Assert.IsTrue( serialized.EndsWith( '"' ), $"Expected serialized output to end with a double-quote; got: {serialized}" );
    }

    /// <summary>
    /// Benign title: a normal playlist title must not contain a literal single-quote in the
    /// serialized output and must round-trip through JSON correctly, confirming the fix does
    /// not break normal content delivery to the client.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_BenignPlaylistTitle_RoundTripsWithNoSingleQuote( ) {
        string title = "My Playlist";

        string serialized = JsonSerializer.Serialize( title, ViewSecurityHelpers.HtmlSafeJsonOptions );

        Assert.IsFalse(
            serialized.Contains( '\'', StringComparison.Ordinal ),
            $"Benign title must not produce a literal single-quote; got: {serialized}" );

        string? deserialized = JsonSerializer.Deserialize<string>( serialized );
        Assert.AreEqual( title, deserialized );
    }

    /// <summary>
    /// Benign title containing an ampersand and angle brackets: these must be escaped by
    /// <see cref="JavaScriptEncoder.Default"/> and still round-trip correctly.
    /// </summary>
    [TestMethod]
    public void HtmlSafeJsonOptions_TitleWithHtmlSpecials_RoundTripsCorrectly( ) {
        string title = "Rock &amp; Roll <Greatest Hits>";

        string serialized = JsonSerializer.Serialize( title, ViewSecurityHelpers.HtmlSafeJsonOptions );

        // No raw angle-brackets or ampersands may appear unescaped.
        Assert.IsFalse( serialized.Contains( '<', StringComparison.Ordinal ), $"Literal < found; got: {serialized}" );
        Assert.IsFalse( serialized.Contains( '>', StringComparison.Ordinal ), $"Literal > found; got: {serialized}" );
        Assert.IsFalse( serialized.Contains( '\'', StringComparison.Ordinal ), $"Literal ' found; got: {serialized}" );

        string? deserialized = JsonSerializer.Deserialize<string>( serialized );
        Assert.AreEqual( title, deserialized );
    }
}
