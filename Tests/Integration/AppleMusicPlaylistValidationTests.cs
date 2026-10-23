using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the <c>POST applemusic/process-playlist</c> endpoint's input-validation
/// behavior, exercising the <see cref="BridgeBeats.Contracts.Records.ProcessPlaylistRequest"/>
/// <c>[RegularExpression]</c> annotation and the controller's <c>ModelState.IsValid</c> guard.
/// </summary>
/// <remarks>
/// Security context: the action builds a credential-bearing URL (Music-User-Token) from the
/// <c>PlaylistId</c> field. These tests assert that injection-style ids (path traversal, encoded
/// separators, raw slashes) are rejected with HTTP 400 before any outbound Apple API call is
/// attempted, and that a well-formed id clears model validation (the action may still return a
/// non-200 status for auth reasons, but it must not return 400).
/// </remarks>
[TestClass]
[TestCategory( "Integration" )]
public class AppleMusicPlaylistValidationTests : IDisposable {

    /// <summary>The test web application factory with the stub authentication scheme installed.</summary>
    private AppleMusicTestWebApplicationFactory? _factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private HttpClient? _client;

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds the test host with worker services disabled, creates a client, and migrates the
    /// identity database before each test.
    /// </summary>
    [TestInitialize]
    public async Task Setup( ) {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile( Path.Combine( "src", "BridgeBeats.Web", "appsettings.json" ), optional: true )
            .AddUserSecrets<Web.Program>( optional: true )
            .AddEnvironmentVariables()
            .Build();

        Dictionary<string, string?> configData = configuration
            .AsEnumerable()
            .Where( kv => kv.Value is not null )
            .Where( kv => !kv.Key.EndsWith( "ConnectionString", StringComparison.OrdinalIgnoreCase ) )
            .ToDictionary();

        configData["BridgeBeats:DiscordToken"] = string.Empty;
        configData["BridgeBeats:Workers:UseWorkerServices"] = "false";
        configData["BridgeBeats:ATProtoIdentifier"] = string.Empty;
        configData["BridgeBeats:ATProtoPassword"] = string.Empty;
        configData["BridgeBeats:ATProtoUserDID"] = string.Empty;

        _factory = new AppleMusicTestWebApplicationFactory( configData );
        _factory.SetTestUser( "test-user-id", "appleMusicTest@example.com" );
        _client = _factory.CreateClient( );
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue( "TestScheme" );

        await _factory.InitializeDatabasesAsync( );
    }

    /// <summary>Tears down the test host after each test.</summary>
    [TestCleanup]
    public void Cleanup( ) {
        Dispose( );
    }

    /// <inheritdoc/>
    public void Dispose( ) {
        _client?.Dispose( );
        _client = null;

        if (_factory != null) {
            try {
                using IServiceScope scope = _factory.Services.CreateScope();
                ApplicationDbContext dbContext =
                    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                _ = dbContext.Database.EnsureDeleted( );
            } catch (ObjectDisposedException) {
                // Factory already disposed.
            }
            _factory.Dispose( );
            _factory = null;
        }

        GC.SuppressFinalize( this );
    }

    /// <summary>
    /// Verifies that a <c>PlaylistId</c> containing a raw slash returns HTTP 400 from model
    /// validation, with no outbound Apple Music API call made. A raw slash in the path segment
    /// would otherwise redirect the token-bearing request to a different Apple endpoint.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before the <c>[RegularExpression]</c> annotation was added to
    /// <c>ProcessPlaylistRequest.PlaylistId</c>, model validation passed for this value and the
    /// action proceeded to the user-token check (returning 401 for a no-token user rather than 400).
    /// After the annotation, <c>ModelState.IsValid</c> is false and the action returns 400 before
    /// any Apple API call is attempted.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdWithSlash_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "pl.123/../../evil" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation rejects the id; no outbound call is ever made
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Verifies that a <c>PlaylistId</c> containing a percent-encoded path separator
    /// (<c>..%2f..</c>) returns HTTP 400 from model validation, with no outbound Apple Music API
    /// call made. Without this guard, the URL-decoded separator could traverse to a different Apple
    /// path segment, redirecting the Music-User-Token to an unintended resource.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before the annotation was added, <c>SanitizeForLogging</c> stripped
    /// only Unicode control characters, leaving <c>%</c>, <c>.</c>, and <c>/</c> in the value that
    /// was interpolated into the URL. The annotation now catches this at the model-binding layer.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdWithEncodedPathSeparator_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "..%2f..%2fevil" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation rejects the id; no outbound call is ever made
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Verifies that a well-formed Apple library-playlist id (e.g. <c>p.ABCdef1234</c>) passes model
    /// validation. The action then proceeds to the user-token checks; because the test user has no
    /// stored Apple Music token the endpoint returns 401, not 400.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_WellFormedPlaylistId_PassesModelValidation( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "p.ABCdef1234" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation passes; the action then fails at the user-token check (401),
        // confirming the well-formed id did NOT trigger a 400 from ModelState.
        Assert.AreNotEqual(
            HttpStatusCode.BadRequest, response.StatusCode,
            "A well-formed playlist id must not be rejected by model validation."
        );
    }

    /// <summary>
    /// Verifies that a <c>PlaylistId</c> of <c>".."</c> (dot-dot path traversal segment) returns HTTP
    /// 400 from model validation before any outbound Apple Music API call is attempted. Without the
    /// tightened regex, <c>Uri.EscapeDataString</c> does not escape dots, so <c>".."</c> would reach
    /// the outbound URL and normalize to a different Apple API path.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>".."</c>; model
    /// validation passed and the action returned 401 (no Apple token) rather than 400. After
    /// tightening to <c>^[A-Za-z0-9]+([._-][A-Za-z0-9]+)*$</c>, <c>ModelState.IsValid</c> is false
    /// and the action returns 400 before any outbound call.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdDotDot_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( ".." );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation rejects the dot-dot id; no outbound call is ever made
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Verifies that a <c>PlaylistId</c> of <c>"."</c> (standalone dot) returns HTTP 400 from model
    /// validation before any outbound Apple Music API call is attempted.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>"."</c>; model
    /// validation passed and the action returned 401 rather than 400. The tightened regex rejects it
    /// because the value does not start with an alphanumeric character.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdSingleDot_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "." );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation rejects the standalone dot; no outbound call is ever made
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Verifies that a <c>PlaylistId</c> containing a doubled separator (<c>"a..b"</c>) returns HTTP
    /// 400 from model validation before any outbound Apple Music API call is attempted. Consecutive
    /// separators can degenerate into traversal-style sequences when interpolated into a URL.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>"a..b"</c>;
    /// model validation passed and the action returned 401 rather than 400. The tightened regex
    /// requires a single separator followed by at least one alphanumeric character and therefore
    /// rejects doubled separators.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdDoubledSeparator_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "a..b" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation rejects the doubled-separator id; no outbound call is ever made
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Negative control (PlaylistId leading-dot): a <c>PlaylistId</c> of <c>".p"</c> (separator
    /// first, then alphanumeric) must be rejected with HTTP 400. The tightened regex
    /// <c>\A[A-Za-z0-9]+([._-][A-Za-z0-9]+)*\z</c> requires the value to begin with an
    /// alphanumeric character, so a leading separator is never valid.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>".p"</c>;
    /// model validation passed and the action returned 401 rather than 400. The tightened regex
    /// rejects it because it does not start with an alphanumeric character.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdLeadingDot_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( ".p" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - leading-dot id must be rejected by model validation
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Negative control (PlaylistId trailing-dot): a <c>PlaylistId</c> of <c>"p."</c>
    /// (alphanumeric followed by a separator with nothing after) must be rejected with HTTP 400.
    /// The tightened regex requires each separator to be followed by at least one alphanumeric
    /// character, so a trailing separator is never valid.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>"p."</c>;
    /// model validation passed and the action returned 401 rather than 400. The tightened regex
    /// rejects it because the trailing dot is not followed by an alphanumeric character.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdTrailingDot_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "p." );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - trailing-dot id must be rejected by model validation
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Negative control (PlaylistId doubled-hyphen): a <c>PlaylistId</c> of <c>"a--b"</c> must be
    /// rejected with HTTP 400. Doubled separators can degenerate into traversal-style sequences
    /// when interpolated into a URL; the tightened regex rejects them by requiring exactly one
    /// separator between each pair of alphanumeric runs.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>"a--b"</c>;
    /// model validation passed and the action returned 401 rather than 400. The tightened regex
    /// rejects consecutive separators.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdDoubledHyphen_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "a--b" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - doubled-hyphen id must be rejected by model validation
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Negative control (PlaylistId doubled-underscore): a <c>PlaylistId</c> of <c>"a__b"</c>
    /// must be rejected with HTTP 400. Doubled separators are not permitted by the tightened
    /// grammar <c>\A[A-Za-z0-9]+([._-][A-Za-z0-9]+)*\z</c>.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: the prior regex <c>^[A-Za-z0-9._-]+$</c> accepted <c>"a__b"</c>;
    /// model validation passed and the action returned 401 rather than 400. The tightened regex
    /// rejects consecutive separators.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdDoubledUnderscore_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "a__b" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - doubled-underscore id must be rejected by model validation
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// Verifies that an <c>i.</c>-prefixed Apple library item id (e.g. <c>i.e5gmPS6rZ856</c>) passes
    /// model validation. The action then proceeds to the user-token checks; because the test user has
    /// no stored Apple Music token the endpoint returns 401, not 400.
    /// </summary>
    /// <remarks>
    /// Regression guard: this positive case confirms the tightened regex does not over-restrict
    /// well-formed Apple library item ids. Both the old and new regexes accept this form; no
    /// meaningful failure-first ordering exists for a purely positive guard.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_LibraryItemPrefixedId_PassesModelValidation( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "i.e5gmPS6rZ856" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation passes; the action then fails at the user-token check (401),
        // confirming the well-formed id did NOT trigger a 400 from ModelState.
        Assert.AreNotEqual(
            HttpStatusCode.BadRequest, response.StatusCode,
            "An i.-prefixed Apple library item id must not be rejected by model validation."
        );
    }

    /// <summary>
    /// Verifies that a <c>pl.u-</c>-prefixed Apple catalog playlist id (e.g. <c>pl.u-abc123</c>)
    /// passes model validation. The action then proceeds to the user-token checks; because the test
    /// user has no stored Apple Music token the endpoint returns 401, not 400.
    /// </summary>
    /// <remarks>
    /// Regression guard: this positive case confirms the tightened regex correctly handles ids that
    /// contain both a dot and a hyphen as separators between alphanumeric runs. Both the old and new
    /// regexes accept this form; no meaningful failure-first ordering exists for a purely positive guard.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_CatalogPlaylistHyphenatedId_PassesModelValidation( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        ProcessPlaylistRequest request = new( "pl.u-abc123" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation passes; the action then fails at the user-token check (401),
        // confirming the well-formed id did NOT trigger a 400 from ModelState.
        Assert.AreNotEqual(
            HttpStatusCode.BadRequest, response.StatusCode,
            "A pl.u--prefixed Apple catalog playlist id must not be rejected by model validation."
        );
    }

    /// <summary>
    /// Verifies that a <c>PlaylistId</c> with a trailing newline (<c>"p.ABCdef1234\n"</c>) returns HTTP 400
    /// from model validation before any outbound Apple Music API call is attempted. This pins the
    /// "must end with alphanumeric / full-string match" rule: the <c>\A</c>/<c>\z</c> anchors reject the
    /// trailing <c>\n</c> where the <c>^</c>/<c>$</c> anchors previously allowed it because <c>$</c>
    /// matches before a trailing newline in .NET multiline semantics.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before the anchor change (<c>^...$</c> form), this test failed — model
    /// validation accepted <c>"p.ABCdef1234\n"</c> and the action returned 401 (no Apple token) rather
    /// than 400. After changing to <c>\A...\z</c>, <c>ModelState.IsValid</c> is false and the action
    /// returns 400 before any outbound call.
    /// </remarks>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProcessPlaylist_PlaylistIdWithTrailingNewline_Returns400BeforeOutboundCall( ) {
        // Arrange
        string antiforgeryToken = await AntiforgeryTestHelper.GetAntiforgeryTokenAsync(
            _client!, TestContext.CancellationToken
        );
        // A syntactically valid id plus a trailing newline. The \A...\z anchors reject this;
        // the old ^...$ anchors accepted it because $ matches before a terminal \n in .NET.
        ProcessPlaylistRequest request = new( "p.ABCdef1234\n" );

        // Act
        HttpResponseMessage response = await AntiforgeryTestHelper.PostWithAntiforgeryAsync(
            _client!,
            "applemusic/process-playlist",
            JsonContent.Create( request ),
            antiforgeryToken,
            TestContext.CancellationToken
        );

        // Assert - model validation rejects the trailing-newline id; no outbound call is ever made
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        SpyHttpMessageHandler spy = _factory!.Services.GetRequiredService<SpyHttpMessageHandler>( );
        Assert.AreEqual( 0, spy.SendCount, "No outbound call must reach the musickit-api client when model validation rejects the request" );
    }

    /// <summary>
    /// A <see cref="CustomWebApplicationFactory"/> variant that installs a stub <c>TestScheme</c>
    /// authentication handler so these tests can present an authenticated principal without a real
    /// login flow. The Apple Music token is intentionally omitted from the test user so the action
    /// returns 401 at the token check rather than attempting an outbound API call.
    /// </summary>
    /// <param name="configOverrides">Configuration overrides forwarded to the base factory.</param>
    private sealed class AppleMusicTestWebApplicationFactory(
        Dictionary<string, string?>? configOverrides
    ) : CustomWebApplicationFactory( configOverrides ) {

        /// <summary>The identity id presented by the stub authentication handler.</summary>
        private string? _testUserId;
        /// <summary>The email presented by the stub authentication handler.</summary>
        private string? _testUserEmail;

        /// <summary>
        /// Configures the stub authentication principal the handler will present on the next request.
        /// </summary>
        /// <param name="userId">The user id to place on the principal's NameIdentifier claim.</param>
        /// <param name="email">The email to place on the principal's Name and Email claims.</param>
        public void SetTestUser( string userId, string email ) {
            _testUserId = userId;
            _testUserEmail = email;
        }

        /// <summary>
        /// Extends the base host configuration by registering the <c>TestScheme</c> authentication
        /// handler, exposing this factory to it for principal construction, and installing the
        /// <see cref="SpyHttpMessageHandler"/> as the primary transport for the
        /// <c>musickit-api</c> named HTTP client so tests can assert zero outbound Apple calls.
        /// </summary>
        /// <param name="builder">The web host builder supplied by the test host.</param>
        protected override void ConfigureWebHost( IWebHostBuilder builder ) {
            base.ConfigureWebHost( builder );

            _ = builder.ConfigureServices( services => {
                _ = services.AddAuthentication( options => {
                    options.DefaultAuthenticateScheme = "TestScheme";
                    options.DefaultChallengeScheme = "TestScheme";
                } )
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>( "TestScheme", _ => { } );

                _ = services.AddSingleton( this );

                _ = services.AddSingleton<SpyHttpMessageHandler>( );
                _ = services.AddHttpClient( "musickit-api" )
                    .ConfigurePrimaryHttpMessageHandler<SpyHttpMessageHandler>( );
            } );
        }

        /// <summary>
        /// Stub authentication handler for the <c>TestScheme</c>. Returns no result when no
        /// Authorization header is present; otherwise authenticates a principal carrying only the
        /// configured user id, name, and email claims (no Apple Music token state).
        /// </summary>
        private sealed class TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            AppleMusicTestWebApplicationFactory factory
        ) : AuthenticationHandler<AuthenticationSchemeOptions>( options, logger, encoder ) {

            /// <summary>
            /// Returns an authenticated principal when the Authorization header is present; otherwise
            /// returns no result so the <c>[Authorize]</c> filter can challenge the request.
            /// </summary>
            protected override Task<AuthenticateResult> HandleAuthenticateAsync( ) {
                if (!Request.Headers.ContainsKey( "Authorization" )) {
                    return Task.FromResult( AuthenticateResult.NoResult( ) );
                }

                if (string.IsNullOrEmpty( factory._testUserId ) || string.IsNullOrEmpty( factory._testUserEmail )) {
                    return Task.FromResult( AuthenticateResult.Fail( "No test user configured" ) );
                }

                List<Claim> claims = [
                    new Claim( ClaimTypes.NameIdentifier, factory._testUserId ),
                    new Claim( ClaimTypes.Name, factory._testUserEmail ),
                    new Claim( ClaimTypes.Email, factory._testUserEmail ),
                ];

                ClaimsIdentity identity = new( claims, "TestScheme" );
                ClaimsPrincipal principal = new( identity );
                AuthenticationTicket ticket = new( principal, "TestScheme" );

                return Task.FromResult( AuthenticateResult.Success( ticket ) );
            }
        }
    }

    /// <summary>
    /// A counting <see cref="HttpMessageHandler"/> that records how many times
    /// <see cref="SendAsync"/> is invoked. Registered as the primary transport for the
    /// <c>musickit-api</c> named HTTP client in the test host so 400-path tests can
    /// assert that zero outbound Apple Music API calls are made when model validation
    /// rejects the request before the action body runs.
    /// </summary>
    private sealed class SpyHttpMessageHandler : HttpMessageHandler {

        private int _sendCount;

        /// <summary>Gets the number of times <see cref="SendAsync"/> was called.</summary>
        public int SendCount => _sendCount;

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            _ = Interlocked.Increment( ref _sendCount );
            return Task.FromResult( new HttpResponseMessage( HttpStatusCode.ServiceUnavailable ) );
        }
    }
}
