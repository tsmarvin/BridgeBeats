using System.Net;
using Microsoft.Extensions.Configuration;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for the health endpoint's accessibility, running the real web app in-memory via
/// <see cref="CustomWebApplicationFactory"/> against the shared Redis container. Verifies the
/// <c>/health</c> endpoint returns 200 OK with the expected JSON for the test client, and that the
/// health middleware does not affect other routes.
/// </summary>
[TestClass]
[TestCategory( "Integration" )]
public class HealthEndpointAuthorizationTests {
    /// <summary>The shared web application factory hosting the app for this test class.</summary>
    private static CustomWebApplicationFactory? s_factory;
    /// <summary>The HTTP client connected to the test host.</summary>
    private static HttpClient? s_client;

    /// <summary>
    /// Builds the test host with worker services disabled and external integrations blanked, creates a
    /// client, and migrates the identity database.
    /// </summary>
    /// <param name="_">The MSTest class context (unused).</param>
    [ClassInitialize]
    public static async Task Setup( TestContext _ ) {
        // These assertions are about endpoint authorization, which does not depend on provider
        // credentials; only the settings that keep the host deterministic are supplied.
        Dictionary<string, string?> configData = TestHostConfiguration.Isolated( );

        s_factory = new CustomWebApplicationFactory( configData );
        s_client = s_factory.CreateClient( );

        // Initialize databases after services are configured
        await s_factory.InitializeDatabasesAsync( );
    }

    /// <summary>
    /// Disposes the test host after the class completes.
    /// </summary>
    [ClassCleanup]
    public static void Cleanup( ) {
        s_factory?.Dispose( );
    }

    /// <summary>
    /// Verifies the health endpoint returns 200 OK for the test client (the test environment appears as
    /// localhost). In production, this would be restricted to internal Docker network IPs.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HealthEndpoint_FromTestClient_ReturnsOk( ) {
        // Arrange
        // The test client appears as localhost/internal to the middleware

        // Act
        HttpResponseMessage response = await s_client!.GetAsync( "/health", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
    }

    /// <summary>
    /// Verifies the health endpoint returns 200 OK with JSON containing the health status and a
    /// timestamp.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task HealthEndpoint_ReturnsValidJson( ) {
        // Arrange & Act
        HttpResponseMessage response = await s_client!.GetAsync( "/health", TestContext.CancellationToken );
        string content = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.Contains( "healthy", content );
        Assert.Contains( "timestamp", content );
    }

    /// <summary>
    /// Verifies a non-health route (the home page) is unaffected by the health middleware and returns
    /// 200 OK.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task NonHealthEndpoint_NotAffectedByMiddleware( ) {
        // Arrange & Act
        HttpResponseMessage response = await s_client!.GetAsync( "/", TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
    }

    /// <summary>The MSTest-injected test context.</summary>
    public TestContext TestContext { get; set; } = null!;
}
