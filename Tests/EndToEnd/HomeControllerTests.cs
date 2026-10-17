using System.Net;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Utilities;
using BridgeBeats.Web.Streaming;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace BridgeBeats.Tests.EndToEnd;

/// <summary>
/// End-to-end tests for the home controller, exercising the real web application in-memory through
/// <see cref="CustomWebApplicationFactory"/> against the shared Redis container and an isolated SQLite
/// identity database. Covers page rendering (home, privacy, error, 404) and the lookup form actions,
/// including antiforgery-protected POSTs and live provider lookups by ISRC, UPC, and title.
/// </summary>
[TestClass]
[TestCategory( "EndToEnd" )] // Mark as end-to-end tests - these are fast and don't hit external APIs
public class HomeControllerTests {
    /// <summary>The shared web application factory hosting the app for this test class.</summary>
    private static CustomWebApplicationFactory? s_factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private static HttpClient? s_client;
    /// <summary>The antiforgery token reused across the class's form-POST tests.</summary>
    private static string? s_antiforgeryToken;

    /// <summary>
    /// Builds the test host with worker services disabled and external integrations blanked, creates a
    /// client, migrates the identity database, and fetches an antiforgery token for the form tests.
    /// </summary>
    /// <param name="context">The MSTest class context, used for its cancellation token.</param>
    [ClassInitialize]
    public static async Task ClassInitialize( TestContext context ) {
        // Load configuration from appsettings.json and user secrets
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile( Path.Combine( "src", "BridgeBeats.Web", "appsettings.json" ), optional: true )
            .AddUserSecrets<Web.Program>( optional: true )
            .AddEnvironmentVariables()
            .Build();

        // Build config overrides from loaded configuration
        Dictionary<string, string?> configData = configuration
            .AsEnumerable()
            .Where( kv => kv.Value is not null )
            .Where( kv => !kv.Key.EndsWith( "ConnectionString", StringComparison.OrdinalIgnoreCase ) )
            .ToDictionary( );

        // Force Discord token to null to prevent Discord service registration
        configData["BridgeBeats:DiscordToken"] = string.Empty;
        // Disable worker services mode - use direct provider implementations
        configData["BridgeBeats:Workers:UseWorkerServices"] = "false";
        // Force ATProto credentials to empty to disable caching service
        // The caching service requires background workers that are not running in the test environment
        configData["BridgeBeats:ATProtoIdentifier"] = string.Empty;
        configData["BridgeBeats:ATProtoPassword"] = string.Empty;
        configData["BridgeBeats:ATProtoUserDID"] = string.Empty;

        s_factory = new CustomWebApplicationFactory( configData );
        s_client = s_factory.CreateClient( );

        // Initialize databases after services are configured
        await s_factory.InitializeDatabasesAsync( );

        // Fetch antiforgery token for POST requests
        s_antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( s_client, context.CancellationToken );
    }

    /// <summary>
    /// Disposes the test host (and its SQLite identity database) after the class completes.
    /// </summary>
    [ClassCleanup]
    public static void ClassCleanup( ) {
        s_factory?.Dispose( );
    }

    /// <summary>
    /// Verifies the home index returns 200 OK with an HTML content type.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task Index_ReturnsSuccessStatusCode( ) {
        // Act
        HttpResponseMessage response = await s_client!.GetAsync("/", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.AreEqual( "text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString( ) );
    }

    /// <summary>
    /// Verifies the home index contains the branding and server-owned streaming framing token.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task Index_ContainsExpectedContent( ) {
        // Act
        HttpResponseMessage response = await s_client!.GetAsync("/", TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.IsTrue( content.Contains( "BridgeBeats", StringComparison.OrdinalIgnoreCase ) );
        Assert.Contains(
            $"data-stream-item-delimiter=\"{WebUtility.HtmlEncode( StreamFraming.ItemDelimiter )}\"",
            content,
            StringComparison.Ordinal );
    }

    /// <summary>
    /// Verifies the privacy page returns 200 OK.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task Privacy_ReturnsSuccessStatusCode( ) {
        // Act
        HttpResponseMessage response = await s_client!.GetAsync("/Home/Privacy", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
    }

    /// <summary>
    /// Verifies the error page returns 200 OK.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task Error_ReturnsSuccessStatusCode( ) {
        // Act
        HttpResponseMessage response = await s_client!.GetAsync("/Home/Error", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
    }

    /// <summary>
    /// Verifies an unmapped route returns 404 Not Found.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task NonExistentRoute_ReturnsNotFound( ) {
        // Act
        HttpResponseMessage response = await s_client!.GetAsync("/NonExistent/Route", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.NotFound, response.StatusCode );
    }

    /// <summary>
    /// Posts an empty URI to the lookup action and verifies the response is 200 OK with a "URI is
    /// required" validation message.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task LookupResults_WithEmptyUri_ReturnsMessageView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["uri"] = ""
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResults", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.IsTrue( content.Contains( "URI is required", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// Posts an empty ISRC to the ISRC lookup action and verifies the response is 200 OK with an "ISRC
    /// is required" validation message.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task LookupResultsByIsrc_WithEmptyIsrc_ReturnsMessageView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["isrc"] = ""
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResultsByIsrc", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.IsTrue( content.Contains( "ISRC is required", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// Posts an empty UPC to the UPC lookup action and verifies the response is 200 OK with a "UPC is
    /// required" validation message.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task LookupResultsByUpc_WithEmptyUpc_ReturnsMessageView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["upc"] = ""
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResultsByUpc", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.IsTrue( content.Contains( "UPC is required", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// Posts an empty title and artist to the title lookup action and verifies the response is 200 OK
    /// with a "Title and artist are required" validation message.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )] // 10 second timeout - should be fast
    public async Task LookupResultsByTitle_WithEmptyTitleAndArtist_ReturnsMessageView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["title"] = "",
            ["artist"] = ""
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResultsByTitle", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.IsTrue( content.Contains( "Title and artist are required", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// Posts a valid ISRC and verifies the response is 200 OK HTML containing either an embed card or a
    /// "No results found" message (tolerating provider availability).
    /// </summary>
    [TestMethod]
    [TestCategory( "Integration" )] // Requires real API credentials
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for API call
    public async Task LookupResultsByIsrc_WithValidIsrc_ReturnsHtmlView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["isrc"] = "QMY951610010"
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResultsByIsrc", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.AreEqual( "text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString( ) );
        // Content should either contain embed-card or a "No results found" message
        Assert.IsTrue( content.Contains( "embed-card", StringComparison.OrdinalIgnoreCase ) ||
                      content.Contains( "No results found", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// Posts a valid UPC and verifies the response is 200 OK HTML containing either an embed card or a
    /// "No results found" message (tolerating provider availability).
    /// </summary>
    [TestMethod]
    [TestCategory( "Integration" )] // Requires real API credentials
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for API call
    public async Task LookupResultsByUpc_WithValidUpc_ReturnsHtmlView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["upc"] = "00602527513607"
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResultsByUpc", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.AreEqual( "text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString( ) );
        // Content should either contain embed-card or a "No results found" message
        Assert.IsTrue( content.Contains( "embed-card", StringComparison.OrdinalIgnoreCase ) ||
                      content.Contains( "No results found", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// Posts a valid title and artist and verifies the response is 200 OK HTML containing either an
    /// embed card or a "No results found" message (tolerating provider availability).
    /// </summary>
    [TestMethod]
    [TestCategory( "Integration" )] // Requires real API credentials
    [Timeout( 30000, CooperativeCancellation = true )] // 30 second timeout for API call
    public async Task LookupResultsByTitle_WithValidTitleAndArtist_ReturnsHtmlView( ) {
        // Arrange
        FormUrlEncodedContent formData = new( new Dictionary<string, string> {
            ["title"] = "The Best Part",
            ["artist"] = "Bien"
        } );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            s_client!, "/Home/LookupResultsByTitle", formData, s_antiforgeryToken!, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.AreEqual( "text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString( ) );
        // Content should either contain embed-card or a "No results found" message
        Assert.IsTrue( content.Contains( "embed-card", StringComparison.OrdinalIgnoreCase ) ||
                      content.Contains( "No results found", StringComparison.OrdinalIgnoreCase ) );
    }

    /// <summary>
    /// The MSTest-injected test context, used here to obtain the per-test cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; }
}

/// <summary>
/// Verifies result rendering behavior with stubbed media-link services, including live saga-progress
/// indicators and preservation of guidance carried by result-less placeholders.
/// </summary>
/// <remarks>
/// Failure-first evidence for each new test is documented inline. A stub
/// <see cref="ILookupProgressProbe"/> is injected via the per-test
/// <see cref="ProbeOverrideFactory"/> so that the probe return value is the ONLY variable between
/// the three cases; all other services are identical. This isolates the wiring path from the probe
/// call through to the view-model property and rendered HTML.
/// </remarks>
[TestClass]
[TestCategory( "EndToEnd" )]
public class HomeControllerRenderingTests {

    private const string TestUrl = "https://open.spotify.com/track/probetesttrack";
    private const string PlaceholderMessage = "Apple Music is temporarily unavailable. Please try again.";

    /// <summary>MSTest-injected context used to obtain the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies URL lookup placeholders retain their provider guidance instead of becoming a generic
    /// no-results response.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResults_ResultlessPlaceholder_RendersProviderMessage( ) {
        MediaLinkResult placeholder = new( ) { Messages = [PlaceholderMessage] };
        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( _ => BuildStubMediaService( placeholder ) );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["uri"] = TestUrl } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResults", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.Contains( PlaceholderMessage, content, StringComparison.Ordinal );
        Assert.DoesNotContain( "No results found", content, StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>
    /// Verifies single-result actions retain guidance from a result-less placeholder rather than
    /// replacing it with their generic no-results fallback.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsByIsrc_ResultlessPlaceholder_RendersProviderMessage( ) {
        MediaLinkResult placeholder = new( ) { Messages = [PlaceholderMessage] };
        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( _ => BuildStubMediaService( placeholder ) );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["isrc"] = "USRC12345678" } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsByIsrc", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.Contains( PlaceholderMessage, content, StringComparison.Ordinal );
        Assert.DoesNotContain( "No results found", content, StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>
    /// Verifies that when <see cref="ILookupProgressProbe.IsActiveAsync"/> returns
    /// <see langword="true"/>, the rendered view contains the "Still looking up" in-progress indicator.
    /// Failure-first: a probe stub wired to return <see langword="false"/> causes this assertion to
    /// fail (indicator absent); the correct <see langword="true"/>-returning stub makes it pass.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsByIsrc_ProbeReturnsTrue_InProgressIndicatorIsRendered( ) {
        Mock<ILookupProgressProbe> probeMock = new( );
        _ = probeMock
            .Setup( p => p.IsActiveAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( sp => BuildStubMediaService( ) );
            _ = services.RemoveAll<ILookupProgressProbe>( );
            _ = services.AddTransient<ILookupProgressProbe>( sp => probeMock.Object );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["isrc"] = "USRC12345678" } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsByIsrc", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( System.Net.HttpStatusCode.OK, response.StatusCode );
        Assert.IsTrue(
            content.Contains( "Still looking up", StringComparison.OrdinalIgnoreCase ),
            "When the probe returns true, IsLookupInProgress must be true and the view must render the in-progress indicator." );
    }

    /// <summary>
    /// Verifies that when <see cref="ILookupProgressProbe.IsActiveAsync"/> returns
    /// <see langword="false"/>, the in-progress indicator is absent from the rendered view.
    /// Failure-first: a probe stub returning <see langword="true"/> causes this assertion to fail
    /// (indicator present); the correct <see langword="false"/>-returning stub makes it pass.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsByIsrc_ProbeReturnsFalse_InProgressIndicatorIsAbsent( ) {
        Mock<ILookupProgressProbe> probeMock = new( );
        _ = probeMock
            .Setup( p => p.IsActiveAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( false );

        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( sp => BuildStubMediaService( ) );
            _ = services.RemoveAll<ILookupProgressProbe>( );
            _ = services.AddTransient<ILookupProgressProbe>( sp => probeMock.Object );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["isrc"] = "USRC12345678" } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsByIsrc", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( System.Net.HttpStatusCode.OK, response.StatusCode );
        Assert.IsFalse(
            content.Contains( "Still looking up", StringComparison.OrdinalIgnoreCase ),
            "When the probe returns false, IsLookupInProgress must be false and the in-progress indicator must not appear." );
    }

    /// <summary>
    /// Verifies that when no <see cref="ILookupProgressProbe"/> is registered (probe is
    /// <see langword="null"/>), the optional-parameter guard in the controller short-circuits and
    /// the in-progress indicator is absent from the rendered view.
    /// Failure-first: removing the <c>probe is not null</c> guard from the controller would cause
    /// a <see cref="NullReferenceException"/> during <c>IsActiveAsync</c>, turning this test into
    /// an unexpected exception rather than a failing assertion.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsByIsrc_NullProbe_InProgressIndicatorIsAbsent( ) {
        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( sp => BuildStubMediaService( ) );
            // Explicitly remove every ILookupProgressProbe registration so the controller
            // receives null for the optional probe parameter.
            _ = services.RemoveAll<ILookupProgressProbe>( );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["isrc"] = "USRC12345678" } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsByIsrc", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( System.Net.HttpStatusCode.OK, response.StatusCode );
        Assert.IsFalse(
            content.Contains( "Still looking up", StringComparison.OrdinalIgnoreCase ),
            "When no probe is registered (null), the optional-param guard must prevent the IsActiveAsync call " +
            "and IsLookupInProgress must remain false." );
    }

    /// <summary>
    /// Verifies the regular URL-result action derives and probes the URL saga key carried by the
    /// result's restored input link.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResults_UrlProbeReturnsTrue_InProgressIndicatorIsRendered( ) {
        string expectedKey = LookupKeyBuilder.UrlKey( TestUrl );
        Mock<ILookupProgressProbe> probeMock = new( );
        _ = probeMock
            .Setup( p => p.IsActiveAsync( expectedKey, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( _ => BuildStubMediaService( ) );
            _ = services.RemoveAll<ILookupProgressProbe>( );
            _ = services.AddTransient<ILookupProgressProbe>( _ => probeMock.Object );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["uri"] = TestUrl } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResults", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.Contains( "Still looking up", content, StringComparison.OrdinalIgnoreCase );
        probeMock.Verify(
            p => p.IsActiveAsync( expectedKey, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>
    /// Verifies the streaming URL-result action uses the restored input link to probe the same saga
    /// key and renders the progress indicator in its streamed card.
    /// </summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsStream_UrlProbeReturnsTrue_InProgressIndicatorIsRendered( ) {
        string expectedKey = LookupKeyBuilder.UrlKey( TestUrl );
        Mock<ILookupProgressProbe> probeMock = new( );
        _ = probeMock
            .Setup( p => p.IsActiveAsync( expectedKey, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );

        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( _ => BuildStubMediaService( ) );
            _ = services.RemoveAll<ILookupProgressProbe>( );
            _ = services.AddTransient<ILookupProgressProbe>( _ => probeMock.Object );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["uri"] = TestUrl } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsStream", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.Contains( "Still looking up", content, StringComparison.OrdinalIgnoreCase );
        int frameCount = content.Split( StreamFraming.ItemDelimiter, StringSplitOptions.None ).Length - 1;
        Assert.AreEqual( 2, frameCount,
            "A one-card response must contain exactly one card frame and one completion frame." );
        probeMock.Verify(
            p => p.IsActiveAsync( expectedKey, It.IsAny<CancellationToken>( ) ),
            Times.Once
        );
    }

    /// <summary>Provider messages are encoded so they cannot inject markup or stream delimiters.</summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsStream_ProviderMessage_IsEncodedAndCannotSplitFrames( ) {
        MediaLinkResult warningResult = new( ) {
            Messages = ["Retry later <!--bridgebeats-stream-item--><script>alert(1)</script>"]
        };
        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( _ => BuildStubMediaService( warningResult ) );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync( client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new( new Dictionary<string, string> { ["uri"] = TestUrl } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsStream", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        int frameCount = content.Split( StreamFraming.ItemDelimiter, StringSplitOptions.None ).Length - 1;
        Assert.AreEqual( 2, frameCount );
        Assert.DoesNotContain( "<script>", content, StringComparison.OrdinalIgnoreCase );
        Assert.Contains( "&lt;script&gt;", content, StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>A terminal stream error preserves the count from rate-limit frames already emitted.</summary>
    [TestMethod]
    [Timeout( 15000, CooperativeCancellation = true )]
    public async Task LookupResultsStream_AfterRateLimitThenFailure_CompletionPreservesRateLimitCount( ) {
        Mock<IMediaLinkService> mediaService = new( );
        _ = mediaService
            .Setup( service => service.GetInfoAsync( It.IsAny<string>( ) ) )
            .Returns( YieldRateLimitThenThrow( ) );
        using ProbeOverrideFactory factory = new( BuildBaseConfig( ), services => {
            _ = services.RemoveAll<IMediaLinkService>( );
            _ = services.AddTransient<IMediaLinkService>( _ => mediaService.Object );
        } );
        await factory.InitializeDatabasesAsync( );
        using HttpClient client = factory.CreateClient( );

        string token = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            client, TestContext.CancellationToken );
        FormUrlEncodedContent formData = new(
            new Dictionary<string, string> { ["uri"] = TestUrl } );
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            client, "/Home/LookupResultsStream", formData, token, TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.Contains( "data-errors=\"1\"", content, StringComparison.Ordinal );
        Assert.Contains( "data-rate-limited=\"1\"", content, StringComparison.Ordinal );
    }

    /// <summary>
    /// Builds the minimal configuration overrides for the probe-wiring test host. Workers and
    /// ATProto are disabled so the host can start without external infrastructure beyond the shared
    /// Redis container.
    /// </summary>
    private static Dictionary<string, string?> BuildBaseConfig( ) => new( ) {
        ["BridgeBeats:DiscordToken"] = string.Empty,
        ["BridgeBeats:Workers:UseWorkerServices"] = "false",
        ["BridgeBeats:ATProtoIdentifier"] = string.Empty,
        ["BridgeBeats:ATProtoPassword"] = string.Empty,
        ["BridgeBeats:ATProtoUserDID"] = string.Empty,
    };

    /// <summary>
    /// Builds a stub <see cref="IMediaLinkService"/> that returns a single Spotify result for
    /// any ISRC, so the controller's <c>CreateViewModelFromResult</c> always produces at least
    /// one view-model item regardless of what ISRC is submitted. The stub is disposable: a new
    /// instance is created per-test factory.
    /// </summary>
    private static IMediaLinkService BuildStubMediaService( MediaLinkResult? result = null ) {
        Mock<IMediaLinkService> mock = new( );
        MediaLinkResult stubResult = result ?? new MediaLinkResult {
            Results = new Dictionary<SupportedProviders, MusicLookupResult> {
                [SupportedProviders.Spotify] = new MusicLookupResult {
                    Artist = "Probe Test Artist",
                    Title = "Probe Test Track",
                    ExternalId = "USRC12345678",
                    URL = "https://open.spotify.com/track/probetesttrack",
                    ArtUrl = string.Empty,
                    IsAlbum = false
                }
            }
        };
        stubResult.InputLinks.Add( TestUrl );
        _ = mock
            .Setup( s => s.GetInfoByISRCAsync( It.IsAny<string>( ) ) )
            .ReturnsAsync( stubResult );
        _ = mock
            .Setup( s => s.GetInfoAsync( It.IsAny<string>( ) ) )
            .Returns( ( string _ ) => YieldResult( stubResult ) );
        return mock.Object;
    }

    private static async IAsyncEnumerable<MediaLinkResult> YieldResult( MediaLinkResult result ) {
        await Task.CompletedTask;
        yield return result;
    }

    private static async IAsyncEnumerable<MediaLinkResult> YieldRateLimitThenThrow( ) {
        yield return new MediaLinkResult { Messages = ["Provider retry pending"] };
        await Task.Yield( );
        throw new InvalidOperationException( "Stream failed after its rate-limit frame." );
    }

    /// <summary>
    /// Extends <see cref="CustomWebApplicationFactory"/> with a per-test service-override callback
    /// so probe-wiring tests can inject stub implementations without modifying the shared factory.
    /// The override callback runs after the parent's <see cref="CustomWebApplicationFactory.ConfigureWebHost"/>
    /// so <c>RemoveAll&lt;T&gt;</c> correctly replaces any service the parent registered.
    /// </summary>
    private sealed class ProbeOverrideFactory(
        Dictionary<string, string?> config,
        Action<IServiceCollection> serviceOverrides
    ) : CustomWebApplicationFactory( config ) {
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );
            _ = builder.ConfigureServices( serviceOverrides );
        }
    }
}
