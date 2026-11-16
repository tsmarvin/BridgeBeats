using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Providers.Spotify;
using Microsoft.Extensions.Logging;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests pinning the HTTP-status-to-outcome mapping inside
/// <see cref="SpotifyLookupService"/>'s bulk methods (<c>GetTracksByIdsAsync</c> and
/// <c>GetAlbumsByIdsAsync</c>). Each test drives the concrete service through a stub
/// <see cref="HttpMessageHandler"/>; no interfaces are substituted at the lookup-service
/// boundary. The API stub includes the terminal provider-rate-limit handler used by the production
/// named client, so status classification is exercised at its transport boundary.
/// </summary>
/// <remarks>
/// Coverage panel:
/// <list type="bullet">
///   <item>400 → <see cref="SpotifyBulkRejectedException"/> (HttpStatusCode == 400)</item>
///   <item>401, 403, 404, 500, 503 → empty dictionary, no throw (negative panel)</item>
///   <item>429 → <see cref="ProviderRateLimitException"/> (rate-limit precedence)</item>
/// </list>
/// All six panels are exercised against both <c>GetTracksByIdsAsync</c> and
/// <c>GetAlbumsByIdsAsync</c>.
/// </remarks>
[TestClass]
public class SpotifyBulkStatusMappingTests {

    /// <summary>Sample Spotify track ID used in every track-path test.</summary>
    private const string TestTrackId = "3n3Ppam7vgaVa1iaRUc9Lp";
    /// <summary>Sample Spotify album ID used in every album-path test.</summary>
    private const string TestAlbumId = "6WdSsBrH5QtofaTTqgwxOV";

    // ──── 400 → SpotifyBulkRejectedException ───────────────────────────────────

    /// <summary>
    /// Verifies that a 400 response from the tracks bulk endpoint throws
    /// <see cref="SpotifyBulkRejectedException"/> whose <c>HttpStatusCode</c> property carries 400.
    /// </summary>
    [TestMethod]
    public async Task GetTracksByIdsAsync_When400_ShouldThrowSpotifyBulkRejectedException( ) {
        SpotifyLookupService svc = CreateService( new HttpResponseMessage( HttpStatusCode.BadRequest ) );

        SpotifyBulkRejectedException ex = await Assert.ThrowsExactlyAsync<SpotifyBulkRejectedException>(
            ( ) => svc.GetTracksByIdsAsync( [TestTrackId] ) );

        Assert.AreEqual( 400, ex.HttpStatusCode,
            "SpotifyBulkRejectedException must carry HttpStatusCode 400 for a 400 response" );
    }

    /// <summary>
    /// Verifies that a 400 response from the albums bulk endpoint throws
    /// <see cref="SpotifyBulkRejectedException"/> whose <c>HttpStatusCode</c> property carries 400.
    /// </summary>
    [TestMethod]
    public async Task GetAlbumsByIdsAsync_When400_ShouldThrowSpotifyBulkRejectedException( ) {
        SpotifyLookupService svc = CreateService( new HttpResponseMessage( HttpStatusCode.BadRequest ) );

        SpotifyBulkRejectedException ex = await Assert.ThrowsExactlyAsync<SpotifyBulkRejectedException>(
            ( ) => svc.GetAlbumsByIdsAsync( [TestAlbumId] ) );

        Assert.AreEqual( 400, ex.HttpStatusCode,
            "SpotifyBulkRejectedException must carry HttpStatusCode 400 for a 400 response" );
    }

    // ──── 401/403/404/500/503 → empty dictionary, no throw ─────────────────────

    /// <summary>
    /// Verifies that non-400 error responses from the tracks bulk endpoint return an empty
    /// dictionary without throwing. This is the discriminating panel: a throw predicate of
    /// <c>&gt;= 400 &amp;&amp; &lt; 500</c> would make the 401, 403, and 404 rows fail by throwing
    /// instead of returning an empty dictionary.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to simulate.</param>
    [TestMethod]
    [DataRow( 401 )]
    [DataRow( 403 )]
    [DataRow( 404 )]
    [DataRow( 500 )]
    [DataRow( 503 )]
    public async Task GetTracksByIdsAsync_WhenNon400ErrorStatus_ShouldReturnEmptyDictionaryNoThrow( int statusCode ) {
        HttpStatusCode status = (HttpStatusCode)statusCode;
        SpotifyLookupService svc = CreateService( new HttpResponseMessage( status ) );

        Dictionary<string, BridgeBeats.Contracts.DTOs.MusicLookupResult?> result =
            await svc.GetTracksByIdsAsync( [TestTrackId] );

        Assert.IsEmpty( result,
            $"GetTracksByIdsAsync must return an empty dictionary for HTTP {statusCode}, not throw" );
    }

    /// <summary>
    /// Verifies that non-400 error responses from the albums bulk endpoint return an empty
    /// dictionary without throwing. This is the discriminating panel for the album path.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to simulate.</param>
    [TestMethod]
    [DataRow( 401 )]
    [DataRow( 403 )]
    [DataRow( 404 )]
    [DataRow( 500 )]
    [DataRow( 503 )]
    public async Task GetAlbumsByIdsAsync_WhenNon400ErrorStatus_ShouldReturnEmptyDictionaryNoThrow( int statusCode ) {
        HttpStatusCode status = (HttpStatusCode)statusCode;
        SpotifyLookupService svc = CreateService( new HttpResponseMessage( status ) );

        Dictionary<string, BridgeBeats.Contracts.DTOs.MusicLookupResult?> result =
            await svc.GetAlbumsByIdsAsync( [TestAlbumId] );

        Assert.IsEmpty( result,
            $"GetAlbumsByIdsAsync must return an empty dictionary for HTTP {statusCode}, not throw" );
    }

    // ──── 429 → ProviderRateLimitException ─────────────────────────────────────

    /// <summary>
    /// Verifies that a 429 response from the tracks bulk endpoint throws
    /// <see cref="ProviderRateLimitException"/>, confirming rate-limit precedence over the 400 path.
    /// </summary>
    [TestMethod]
    public async Task GetTracksByIdsAsync_When429_ShouldThrowProviderRateLimitException( ) {
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 60 ) );
        SpotifyLookupService svc = CreateService( rateLimitResponse );

        _ = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => svc.GetTracksByIdsAsync( [TestTrackId] ) );
    }

    /// <summary>
    /// Verifies that a 429 response from the albums bulk endpoint throws
    /// <see cref="ProviderRateLimitException"/>, confirming rate-limit precedence over the 400 path.
    /// </summary>
    [TestMethod]
    public async Task GetAlbumsByIdsAsync_When429_ShouldThrowProviderRateLimitException( ) {
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 60 ) );
        SpotifyLookupService svc = CreateService( rateLimitResponse );

        _ = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => svc.GetAlbumsByIdsAsync( [TestAlbumId] ) );
    }

    /// <summary>Direct provider authentication failures remain operational failures, not no-results.</summary>
    [TestMethod]
    [DataRow( 401 )]
    [DataRow( 403 )]
    public async Task GetInfoByISRCAsync_WhenAuthenticationFails_ShouldThrow( int statusCode ) {
        SpotifyLookupService service = CreateService( new HttpResponseMessage( (HttpStatusCode)statusCode ) );

        HttpRequestException exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            ( ) => service.GetInfoByISRCAsync( "USRC17607839" ) );

        Assert.AreEqual( (HttpStatusCode)statusCode, exception.StatusCode );
    }

    /// <summary>A deterministic direct-provider 404 remains an authoritative no-result.</summary>
    [TestMethod]
    public async Task GetInfoByISRCAsync_WhenNotFound_ShouldReturnNull( ) {
        SpotifyLookupService service = CreateService( new HttpResponseMessage( HttpStatusCode.NotFound ) );

        Assert.IsNull( await service.GetInfoByISRCAsync( "USRC17607839" ) );
    }

    // ──── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="SpotifyLookupService"/> wired to a stub HTTP handler that returns
    /// <paramref name="apiResponse"/> for every request to the <c>spotify-api</c> client.
    /// A separate token-endpoint stub always returns a valid bearer token so the
    /// <see cref="SpotifyTokenHandler"/> can authenticate without network access.
    /// </summary>
    /// <param name="apiResponse">The response the fake Spotify API handler will return.</param>
    /// <returns>A service instance under test.</returns>
    private static SpotifyLookupService CreateService( HttpResponseMessage apiResponse ) {
        // Token-endpoint stub: returns a minimal Spotify token JSON for any POST to /api/token.
        // SpotifyTokenHandler calls factory.CreateClient("spotify-auth") and POSTs to api/token.
        string tokenJson = """{"access_token":"stub-token","expires_in":3600,"token_type":"Bearer"}""";
        HttpClient tokenClient = new( new FixedResponseHandler(
            HttpStatusCode.OK, tokenJson ) ) {
            BaseAddress = new Uri( "https://accounts.spotify.com/" )
        };
        Mock<IHttpClientFactory> tokenFactory = new( );
        _ = tokenFactory.Setup( f => f.CreateClient( "spotify-auth" ) ).Returns( tokenClient );

        SpotifyCredentials credentials = new( "test-id", "test-secret" );
        Mock<ILogger<SpotifyTokenHandler>> tokenLogger = new( );
        SpotifyTokenHandler tokenHandler = new( credentials, tokenFactory.Object, tokenLogger.Object );

        // API-endpoint stub: returns apiResponse for every GET to the spotify-api client.
        TerminalProviderRateLimitHandler terminalRateLimitHandler = new( SupportedProviders.Spotify ) {
            InnerHandler = new FixedResponseHandler( apiResponse )
        };
        HttpClient apiClient = new( terminalRateLimitHandler ) {
            BaseAddress = new Uri( "https://api.spotify.com/v1/" )
        };
        Mock<IHttpClientFactory> apiFactory = new( );
        _ = apiFactory.Setup( f => f.CreateClient( "spotify-api" ) ).Returns( apiClient );

        Mock<ILogger<SpotifyLookupService>> svcLogger = new( );
        Mock<IGenreCacheService> genreCache = new( );

        return new SpotifyLookupService(
            handler: tokenHandler,
            factory: apiFactory.Object,
            logger: svcLogger.Object,
            serializerOptions: new JsonSerializerOptions( ),
            genreCache: genreCache.Object
        );
    }

    /// <summary>
    /// Test HTTP handler that returns a fixed <see cref="HttpResponseMessage"/> for every request,
    /// suitable for simulating a canned Spotify API response without network access.
    /// </summary>
    private sealed class FixedResponseHandler : HttpMessageHandler {
        private readonly HttpResponseMessage _response;

        /// <summary>
        /// Initialises the handler with a pre-built <see cref="HttpResponseMessage"/>.
        /// </summary>
        /// <param name="response">The response returned for every request.</param>
        internal FixedResponseHandler( HttpResponseMessage response ) {
            _response = response;
        }

        /// <summary>
        /// Convenience constructor that creates the response from a status code and a JSON body.
        /// </summary>
        /// <param name="status">The HTTP status code to return.</param>
        /// <param name="body">The response body as a JSON string.</param>
        internal FixedResponseHandler( HttpStatusCode status, string body ) {
            _response = new HttpResponseMessage( status ) {
                Content = new StringContent( body, Encoding.UTF8, "application/json" )
            };
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult( _response );
    }
}
