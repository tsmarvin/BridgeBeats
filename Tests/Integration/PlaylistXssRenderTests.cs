using System.Net;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Render-level regression tests for the SEC-E-001 XSS fix. These tests drive a real
/// <see cref="CustomWebApplicationFactory"/> GET of <c>playlist/{id}</c> and assert that the
/// rendered <c>data-title</c> attribute:
/// <list type="bullet">
///   <item>is NON-EMPTY (the old double-quote delimiter bug caused the attribute to be empty), and</item>
///   <item>contains the payload as escaped/inert content with no forged <c>onerror</c> or
///         <c>onfocus</c> HTML attribute injected onto the element.</item>
/// </list>
/// </summary>
/// <remarks>
/// Failure-first evidence — these tests MUST fail under either of the two regression conditions
/// the fix targets:
/// <list type="number">
///   <item>If a <c>data-title</c> sink reverts to a double-quote delimiter, the assertion
///         <c>html.Contains("data-title='")</c> fails immediately.</item>
///   <item>If <see cref="BridgeBeats.Web.ViewSecurityHelpers.HtmlSafeJsonOptions"/> is swapped
///         to <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>,
///         single quotes are no longer escaped; the apostrophe test detects this because a literal
///         <c>'</c> appears in the extracted attribute value, and the script-breakout test detects
///         it because a literal <c>&lt;</c> appears in the attribute value.</item>
/// </list>
/// </remarks>
[TestClass]
[TestCategory( "Integration" )]
public class PlaylistXssRenderTests : IDisposable {

    /// <summary>The test web application factory hosting the app for this test class.</summary>
    private PlaylistRenderWebApplicationFactory? _factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private HttpClient? _client;
    /// <summary>The test playlist id used in every test; the mock service returns a playlist for this id.</summary>
    private const string TestPlaylistId = "xss-test-id";

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Tears down the test host after each test.</summary>
    [TestCleanup]
    public void Cleanup( ) {
        Dispose( );
    }

    /// <inheritdoc/>
    public void Dispose( ) {
        _client?.Dispose( );
        _client = null;

        _factory?.Dispose( );
        _factory = null;

        GC.SuppressFinalize( this );
    }

    /// <summary>
    /// Creates a <see cref="PlaylistRenderWebApplicationFactory"/> whose mock
    /// <see cref="IPlaylistService"/> returns a playlist with the specified title.
    /// </summary>
    /// <param name="title">The playlist title to return from the mock service.</param>
    private void SetupFactory( string title ) {
        Dictionary<string, string?> configData = new( ) {
            ["BridgeBeats:DiscordToken"] = string.Empty,
            ["BridgeBeats:Workers:UseWorkerServices"] = "false",
            ["BridgeBeats:ATProtoIdentifier"] = string.Empty,
            ["BridgeBeats:ATProtoPassword"] = string.Empty,
            ["BridgeBeats:ATProtoUserDID"] = string.Empty,
            ["BridgeBeats:Domain"] = "bridgebeats.link",
            ["BridgeBeats:SpotifyClientId"] = "test",
            ["BridgeBeats:SpotifyClientSecret"] = "test",
        };

        PlaylistEntryDto playlistDto = new( ) {
            PlaylistId = TestPlaylistId,
            Title = title,
            CardIds = string.Empty,
            CardRkeys = string.Empty,
            CreatedAt = DateTime.UtcNow,
        };

        Mock<IPlaylistService> playlistServiceMock = new( );
        _ = playlistServiceMock.Setup( s => s.IsEnabled ).Returns( true );
        _ = playlistServiceMock.Setup( s => s.Domain ).Returns( "bridgebeats.link" );
        _ = playlistServiceMock.Setup( s => s.GetPlaylistAsync( TestPlaylistId ) )
            .ReturnsAsync( playlistDto );

        _factory = new PlaylistRenderWebApplicationFactory( configData, playlistServiceMock.Object );
        _client = _factory.CreateClient( new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions {
            AllowAutoRedirect = false,
        } );
    }

    /// <summary>
    /// Verifies that a playlist title containing an XSS script break-out sequence
    /// (<c>"></c><c>/script></c><c>&lt;img src=x onerror=alert(1)></c>) is rendered with
    /// the <c>data-title</c> attribute enclosed in single quotes, non-empty, and with
    /// <c>&lt;</c> and <c>></c> escaped so no live script or event-handler tag is injected.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence:
    /// <list type="bullet">
    ///   <item>With a double-quote delimiter the check <c>html.Contains("data-title='")</c> fails.</item>
    ///   <item>With <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    ///         the attribute value would contain a literal <c>&lt;</c> and the assertion
    ///         <c>Assert.IsFalse(attrValue.Contains('&lt;'))</c> would fail.</item>
    /// </list>
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PlaylistRender_XssScriptBreakoutTitle_DataTitleAttributeIsEscaped( ) {
        // Arrange
        string xssTitle = "\"></script><img src=x onerror=alert(1)>";
        SetupFactory( xssTitle );

        // Act
        HttpResponseMessage response = await _client!.GetAsync(
            $"playlist/{TestPlaylistId}",
            TestContext.CancellationToken
        );

        // Assert — the page renders (not 404 or 500)
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        string html = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // (a) The data-title attribute uses a single-quote delimiter (fix in place)
        Assert.IsTrue( html.Contains( "data-title='", StringComparison.Ordinal ),
            "data-title must use single-quote delimiters; if this fails the sink has reverted to double-quote." );

        // (a) The attribute is non-empty — the old double-quote bug rendered an empty attribute
        Assert.IsFalse( html.Contains( "data-title=''", StringComparison.Ordinal ),
            "data-title must not be empty; an empty attribute indicates the old delimiter-collapse bug." );

        // (b) Extract the attribute value between the single quotes and verify the payload is escaped
        string attrValue = ExtractSingleQuotedAttributeValue( html, "data-title='" );
        Assert.IsFalse( string.IsNullOrEmpty( attrValue ),
            "Extracted data-title attribute value must not be empty." );

        // The < and > from the XSS payload must be Unicode-escaped (not literal tag characters)
        Assert.IsFalse( attrValue.Contains( '<', StringComparison.Ordinal ),
            $"data-title value must not contain a literal '<'; got: {attrValue}" );
        Assert.IsFalse( attrValue.Contains( '>', StringComparison.Ordinal ),
            $"data-title value must not contain a literal '>'; got: {attrValue}" );

        // JavaScriptEncoder.Default emits < as < and > as >
        Assert.IsTrue( attrValue.Contains( "\\u003C", StringComparison.Ordinal ),
            $"Expected \\u003C (escaped '<') in data-title value; got: {attrValue}" );
        Assert.IsTrue( attrValue.Contains( "\\u003E", StringComparison.Ordinal ),
            $"Expected \\u003E (escaped '>') in data-title value; got: {attrValue}" );
    }

    /// <summary>
    /// Verifies that a playlist title containing a single quote / apostrophe (e.g.
    /// <c>it's a great song</c>) is rendered with the apostrophe escaped in the single-quoted
    /// <c>data-title</c> attribute, so it cannot close the attribute early and inject content.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence:
    /// <list type="bullet">
    ///   <item>With a double-quote delimiter the check <c>html.Contains("data-title='")</c> fails.</item>
    ///   <item>With <see cref="System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    ///         the single quote is left unescaped; <see cref="ExtractSingleQuotedAttributeValue"/>
    ///         stops at the first interior <c>'</c> and returns a truncated value, causing the
    ///         <c>Assert.IsTrue(attrValue.Contains("\\u0027"))</c> assertion to fail.
    ///         Additionally, the raw injection would be detectable as a <c>>'</c> sequence ending
    ///         the attribute early.</item>
    /// </list>
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PlaylistRender_ApostropheTitle_SingleQuoteIsEscapedInDataTitle( ) {
        // Arrange
        string apostropheTitle = "it's a great song";
        SetupFactory( apostropheTitle );

        // Act
        HttpResponseMessage response = await _client!.GetAsync(
            $"playlist/{TestPlaylistId}",
            TestContext.CancellationToken
        );

        // Assert — the page renders (not 404 or 500)
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        string html = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // The data-title attribute uses single-quote delimiters (fix in place)
        Assert.IsTrue( html.Contains( "data-title='", StringComparison.Ordinal ),
            "data-title must use single-quote delimiters; if this fails the sink has reverted to double-quote." );

        // Extract attribute value: JavaScriptEncoder.Default escapes ' as ', so the
        // next literal ' after data-title=' is the closing delimiter, not interior content.
        string attrValue = ExtractSingleQuotedAttributeValue( html, "data-title='" );
        Assert.IsFalse( string.IsNullOrEmpty( attrValue ),
            "Extracted data-title attribute value must not be empty." );

        // No literal single quote may appear inside the attribute value — if it did,
        // it would close the attribute early and allow injection.
        Assert.IsFalse( attrValue.Contains( '\'', StringComparison.Ordinal ),
            $"data-title value must not contain a literal single quote; got: {attrValue}" );

        // The apostrophe must be present as the ' escape sequence, proving the encoder ran.
        // If UnsafeRelaxedJsonEscaping were used, ' would not appear and this assertion fails.
        Assert.IsTrue( attrValue.Contains( "\\u0027", StringComparison.Ordinal ),
            $"Expected \\u0027 (escaped apostrophe) in data-title value; got: {attrValue}" );

        // The JSON must round-trip correctly: the escaped value decodes to the original title.
        string jsonToken = attrValue;
        string? deserialized = JsonSerializer.Deserialize<string>( jsonToken );
        Assert.AreEqual( apostropheTitle, deserialized,
            "The escaped title in data-title must round-trip to the original value." );
    }

    /// <summary>
    /// Extracts the content of a single-quoted HTML attribute from the rendered HTML. Locates the
    /// first occurrence of <paramref name="attributePrefix"/> (e.g. <c>data-title='</c>) and
    /// returns the substring up to the next literal single quote, which is the closing delimiter.
    /// This extraction is sound because <see cref="BridgeBeats.Web.ViewSecurityHelpers.HtmlSafeJsonOptions"/>
    /// uses <see cref="System.Text.Encodings.Web.JavaScriptEncoder.Default"/>, which encodes all
    /// interior single quotes as <c>'</c>, guaranteeing no literal <c>'</c> appears inside
    /// the attribute value.
    /// </summary>
    /// <param name="html">The full rendered HTML response body.</param>
    /// <param name="attributePrefix">The opening prefix including the opening single quote, e.g. <c>data-title='</c>.</param>
    /// <returns>The attribute value between the single quotes, or an empty string when not found.</returns>
    private static string ExtractSingleQuotedAttributeValue( string html, string attributePrefix ) {
        int prefixIdx = html.IndexOf( attributePrefix, StringComparison.Ordinal );
        if (prefixIdx < 0) {
            return string.Empty;
        }

        int valueStart = prefixIdx + attributePrefix.Length;
        int closingQuoteIdx = html.IndexOf( '\'', valueStart );
        if (closingQuoteIdx < 0) {
            return string.Empty;
        }

        return html[valueStart..closingQuoteIdx];
    }

    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> variant that replaces the registered
    /// <see cref="IPlaylistService"/> singleton with a caller-supplied mock, so the
    /// <c>GET playlist/{id}</c> endpoint can be driven with controlled titles without
    /// touching the real database.
    /// </summary>
    /// <param name="configOverrides">Configuration overrides forwarded to the base factory.</param>
    /// <param name="playlistServiceMock">The mock <see cref="IPlaylistService"/> to substitute.</param>
    private sealed class PlaylistRenderWebApplicationFactory(
        Dictionary<string, string?>? configOverrides,
        IPlaylistService playlistServiceMock
    ) : CustomWebApplicationFactory( configOverrides ) {

        /// <inheritdoc/>
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );

            _ = builder.ConfigureServices( services => {
                // Replace the real PlaylistService singleton with the test mock so that
                // GetPlaylistAsync returns the crafted test playlist without DB access.
                _ = services.RemoveAll<IPlaylistService>( );
                _ = services.AddSingleton( playlistServiceMock );
            } );
        }
    }
}
