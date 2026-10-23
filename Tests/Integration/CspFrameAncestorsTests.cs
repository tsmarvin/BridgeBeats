using Microsoft.Extensions.Configuration;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests that verify the CSP <c>frame-ancestors</c> directive is emitted correctly for
/// card and playlist routes. Full pages (<c>/card/{id}</c>, <c>/playlist/{id}</c>) must include
/// <c>frame-ancestors 'self'</c> to prevent clickjacking. Embed surfaces (<c>/card/{id}/embed</c>,
/// <c>/card/{id}/embed/qr</c>, <c>/playlist/{id}/embed</c>) must omit <c>frame-ancestors</c> entirely
/// so they can be framed from any origin, including non-network schemes that the <c>'*'</c> wildcard
/// does not cover per the CSP specification.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
public class CspFrameAncestorsTests {
    /// <summary>The shared web application factory hosting the app for this test class.</summary>
    private static CustomWebApplicationFactory? s_factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private static HttpClient? s_client;

    /// <summary>
    /// Builds the test host with worker services disabled and external integrations blanked, and
    /// creates a client. Database initialization is not needed because the CSP middleware runs before
    /// routing and writes its header even when the underlying action returns 404.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task Setup( TestContext _ ) {
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

        s_factory = new CustomWebApplicationFactory( configData );
        s_client = s_factory.CreateClient( );

        await s_factory.InitializeDatabasesAsync( );
    }

    /// <summary>Disposes the test host after the class completes.</summary>
    [ClassCleanup]
    public static void Cleanup( ) {
        s_factory?.Dispose( );
    }

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;

    // -----------------------------------------------------------------------
    // /card/{id} — full page: must carry frame-ancestors 'self'
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a request to a full card page (<c>/card/{id}</c>) includes
    /// <c>frame-ancestors 'self'</c> in the CSP header, protecting it from clickjacking.
    /// The card does not need to exist; the CSP middleware writes the header before the action runs.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CardFullPage_Csp_ContainsFrameAncestorsSelf( ) {
        HttpResponseMessage response = await s_client!.GetAsync( "/card/nonexistent-id", TestContext.CancellationToken );

        string csp = GetCspHeader( response );
        Assert.IsTrue(
            csp.Contains( "frame-ancestors", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP to contain 'frame-ancestors' on /card/{{id}} but got: {csp}"
        );
        Assert.IsTrue(
            csp.Contains( "'self'", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP frame-ancestors to include 'self' on /card/{{id}} but got: {csp}"
        );
    }

    // -----------------------------------------------------------------------
    // /card/{id}/embed — embed surface: must NOT carry frame-ancestors
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a request to the embed surface (<c>/card/{id}/embed</c>) omits
    /// <c>frame-ancestors</c> from the CSP header, allowing the page to be framed from any origin.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CardEmbedPage_Csp_OmitsFrameAncestors( ) {
        HttpResponseMessage response = await s_client!.GetAsync( "/card/nonexistent-id/embed", TestContext.CancellationToken );

        string csp = GetCspHeader( response );
        Assert.IsFalse(
            csp.Contains( "frame-ancestors", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP to omit 'frame-ancestors' on /card/{{id}}/embed but got: {csp}"
        );
    }

    /// <summary>
    /// Verifies that a request to the QR embed surface (<c>/card/{id}/embed/qr</c>) omits
    /// <c>frame-ancestors</c> from the CSP header, allowing the page to be framed from any origin.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task CardEmbedQrPage_Csp_OmitsFrameAncestors( ) {
        HttpResponseMessage response = await s_client!.GetAsync( "/card/nonexistent-id/embed/qr", TestContext.CancellationToken );

        string csp = GetCspHeader( response );
        Assert.IsFalse(
            csp.Contains( "frame-ancestors", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP to omit 'frame-ancestors' on /card/{{id}}/embed/qr but got: {csp}"
        );
    }

    // -----------------------------------------------------------------------
    // /playlist/{id} — full page: must carry frame-ancestors 'self'
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a request to a full playlist page (<c>/playlist/{id}</c>) includes
    /// <c>frame-ancestors 'self'</c> in the CSP header, protecting it from clickjacking.
    /// The playlist does not need to exist; the CSP middleware writes the header before the action runs.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PlaylistFullPage_Csp_ContainsFrameAncestorsSelf( ) {
        HttpResponseMessage response = await s_client!.GetAsync( "/playlist/nonexistent-id", TestContext.CancellationToken );

        string csp = GetCspHeader( response );
        Assert.IsTrue(
            csp.Contains( "frame-ancestors", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP to contain 'frame-ancestors' on /playlist/{{id}} but got: {csp}"
        );
        Assert.IsTrue(
            csp.Contains( "'self'", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP frame-ancestors to include 'self' on /playlist/{{id}} but got: {csp}"
        );
    }

    // -----------------------------------------------------------------------
    // /playlist/{id}/embed — embed surface: must NOT carry frame-ancestors
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a request to the playlist embed surface (<c>/playlist/{id}/embed</c>) omits
    /// <c>frame-ancestors</c> from the CSP header, allowing the page to be framed from any origin.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task PlaylistEmbedPage_Csp_OmitsFrameAncestors( ) {
        HttpResponseMessage response = await s_client!.GetAsync( "/playlist/nonexistent-id/embed", TestContext.CancellationToken );

        string csp = GetCspHeader( response );
        Assert.IsFalse(
            csp.Contains( "frame-ancestors", StringComparison.OrdinalIgnoreCase ),
            $"Expected CSP to omit 'frame-ancestors' on /playlist/{{id}}/embed but got: {csp}"
        );
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Retrieves the <c>Content-Security-Policy</c> header value from the response, asserting that
    /// it is present and non-empty.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    /// <returns>The raw CSP header string.</returns>
    private static string GetCspHeader( HttpResponseMessage response ) {
        Assert.IsTrue(
            response.Headers.TryGetValues( "Content-Security-Policy", out IEnumerable<string>? values ),
            "Expected a Content-Security-Policy response header but none was present."
        );
        string csp = string.Join( " ", values! );
        Assert.IsFalse(
            string.IsNullOrWhiteSpace( csp ),
            "Content-Security-Policy header was present but empty."
        );
        return csp;
    }
}
