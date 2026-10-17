using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Contracts.Records.WorkerApi;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RetryAfterLimitHandler"/>, the delegating handler that converts provider
/// backpressure into a typed queue deferral before resilience policies retry or count it. Verify that
/// non-backpressure responses pass through; ordinary waits throw <see cref="ProviderRateLimitException"/>;
/// excessive waits throw <see cref="RetryAfterExceededException"/>; and provider, timing, and logging
/// details are preserved.
/// </summary>
[TestClass]
public class RetryAfterLimitHandlerTests {
    /// <summary>Mocked logger (enabled at all levels) used to assert the fail-fast warning.</summary>
    private Mock<ILogger<RetryAfterLimitHandler>> _loggerMock = null!;
    /// <summary>The default threshold (seconds) handlers are constructed with for these tests.</summary>
    private const int DefaultMaxRetryAfterSeconds = 120;

    /// <summary>Builds a fresh logger mock (enabled at all levels) before each test.</summary>
    [TestInitialize]
    public void Initialize( ) {
        _loggerMock = new Mock<ILogger<RetryAfterLimitHandler>>( );
        // Enable logging for all log levels so LoggerMessage source-generated methods invoke Log
        _ = _loggerMock.Setup( x => x.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );
    }

    #region Constructor Tests

    /// <summary>The constructor builds an instance from a valid threshold and logger.</summary>
    [TestMethod]
    public void Constructor_WithValidParameters_ShouldCreateInstance( ) {
        // Act
        RetryAfterLimitHandler handler = new( DefaultMaxRetryAfterSeconds, new QueueSettings( ), _loggerMock.Object );

        // Assert
        Assert.IsNotNull( handler );
    }

    /// <summary>The constructor accepts a zero threshold and builds an instance.</summary>
    [TestMethod]
    public void Constructor_WithZeroThreshold_ShouldCreateInstance( ) {
        // Act - Zero means any Retry-After will exceed threshold
        RetryAfterLimitHandler handler = new( 0, new QueueSettings( ), _loggerMock.Object );

        // Assert
        Assert.IsNotNull( handler );
    }

    #endregion

    #region SendAsync Tests - Non-429 Responses

    /// <summary>A 200 response passes through unchanged.</summary>
    [TestMethod]
    public async Task SendAsync_WithSuccessResponse_ShouldPassThrough( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage successResponse = new( HttpStatusCode.OK );
        SetupInnerHandler( handler, successResponse );

        // Act
        HttpResponseMessage result = await SendRequestAsync( handler );

        // Assert
        Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
    }

    /// <summary>A 500 response passes through unchanged (non-429 errors are not intercepted).</summary>
    [TestMethod]
    public async Task SendAsync_With500Error_ShouldPassThrough( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage errorResponse = new( HttpStatusCode.InternalServerError );
        SetupInnerHandler( handler, errorResponse );

        // Act
        HttpResponseMessage result = await SendRequestAsync( handler );

        // Assert
        Assert.AreEqual( HttpStatusCode.InternalServerError, result.StatusCode );
    }

    /// <summary>A 404 response passes through unchanged.</summary>
    [TestMethod]
    public async Task SendAsync_With404Error_ShouldPassThrough( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage errorResponse = new( HttpStatusCode.NotFound );
        SetupInnerHandler( handler, errorResponse );

        // Act
        HttpResponseMessage result = await SendRequestAsync( handler );

        // Assert
        Assert.AreEqual( HttpStatusCode.NotFound, result.StatusCode );
    }

    /// <summary>A 503 remains an outage response for the standard resilience pipeline.</summary>
    [TestMethod]
    public async Task SendAsync_With503AndRetryAfter_ShouldPassThrough( ) {
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage response = new( HttpStatusCode.ServiceUnavailable );
        response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 45 ) );
        SetupInnerHandler( handler, response );

        HttpResponseMessage result = await SendRequestAsync( handler );

        Assert.AreEqual( HttpStatusCode.ServiceUnavailable, result.StatusCode );
    }

    #endregion

    #region SendAsync Tests - 429 Without Retry-After

    /// <summary>
    /// A 429 with no <c>Retry-After</c> header uses the safe default deferral.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_NoRetryAfterHeader_ShouldUseDefaultTypedDelay( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        // No Retry-After header set
        SetupInnerHandler( handler, rateLimitResponse );

        // Act
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler ) );

        // Assert
        Assert.AreEqual( TimeSpan.FromMinutes( 1 ), exception.RetryAfterValue );
    }

    #endregion

    #region SendAsync Tests - 429 With Retry-After Below Threshold

    /// <summary>
    /// A 429 whose <c>Retry-After</c> is below the threshold becomes a typed deferral.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_RetryAfterBelowThreshold_ShouldThrowTypedRateLimit( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 60 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler ) );

        // Assert
        Assert.AreEqual( TimeSpan.FromSeconds( 60 ), exception.RetryAfterValue );
    }

    /// <summary>
    /// A 429 whose <c>Retry-After</c> (120s) exactly equals the threshold passes through (the boundary
    /// is inclusive; only values above the threshold throw).
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_RetryAfterExactlyAtThreshold_ShouldThrowTypedRateLimit( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 120 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler ) );

        // Assert - Exactly at threshold is a normal typed deferral (not the exceeded subtype)
        Assert.AreEqual( TimeSpan.FromSeconds( 120 ), exception.RetryAfterValue );
    }

    #endregion

    #region SendAsync Tests - 429 With Retry-After Exceeding Threshold

    /// <summary>
    /// A 429 whose <c>Retry-After</c> (300s) exceeds the threshold throws
    /// <see cref="RetryAfterExceededException"/> carrying the retry-after value and threshold.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_RetryAfterExceedsThreshold_ShouldThrowException( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 300 ) ); // 5 minutes
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler )
        );

        Assert.AreEqual( TimeSpan.FromSeconds( 300 ), exception.RetryAfterValue );
        Assert.AreEqual( TimeSpan.FromSeconds( DefaultMaxRetryAfterSeconds ), exception.Threshold );
    }

    /// <summary>
    /// A 429 whose <c>Retry-After</c> (121s) is just one second above the threshold throws, with the
    /// retry-after value preserved.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_RetryAfterSlightlyExceedsThreshold_ShouldThrowException( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 121 ) ); // Just over threshold
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler )
        );

        Assert.AreEqual( TimeSpan.FromSeconds( 121 ), exception.RetryAfterValue );
    }

    /// <summary>
    /// A 429 with a very long <c>Retry-After</c> (1 hour) throws, with the full retry-after value
    /// preserved.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_VeryLongRetryAfter_ShouldThrowException( ) {
        // Arrange - Simulate 1 hour Retry-After (common for severe rate limiting)
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromHours( 1 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler )
        );

        Assert.AreEqual( TimeSpan.FromHours( 1 ), exception.RetryAfterValue );
    }

    #endregion

    #region SendAsync Tests - HTTP-Date Format Retry-After

    /// <summary>
    /// A 429 whose <c>Retry-After</c> is an HTTP-date roughly 10 minutes in the future exceeds the
    /// threshold and throws, with the computed retry-after falling in the expected 500–700 second range.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_HttpDateRetryAfterExceedsThreshold_ShouldThrowException( ) {
        // Arrange - Use HTTP-date format (future date)
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        DateTimeOffset futureDate = DateTimeOffset.UtcNow.AddMinutes( 10 ); // 10 minutes in future
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( futureDate );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler )
        );

        // Should be approximately 10 minutes (600 seconds), allow some tolerance
        Assert.IsGreaterThan( 500, exception.RetryAfterValue.TotalSeconds );
        Assert.IsLessThan( 700, exception.RetryAfterValue.TotalSeconds );
    }

    /// <summary>
    /// A 429 whose <c>Retry-After</c> is an HTTP-date about 30 seconds in the future is below the
    /// threshold and passes through.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_With429_HttpDateRetryAfterBelowThreshold_ShouldThrowTypedRateLimit( ) {
        // Arrange - Use HTTP-date format (near future)
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        DateTimeOffset futureDate = DateTimeOffset.UtcNow.AddSeconds( 30 ); // 30 seconds in future
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( futureDate );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler ) );

        // Assert - 30 seconds is below threshold
        Assert.IsGreaterThan( 0, exception.RetryAfterValue.TotalSeconds );
        Assert.IsLessThanOrEqualTo( 30, exception.RetryAfterValue.TotalSeconds );
    }

    /// <summary>An expired HTTP-date is clamped to a short delay so it cannot hot-loop the queue.</summary>
    [TestMethod]
    public async Task SendAsync_With429_ExpiredHttpDate_ShouldUseMinimumTypedDelay( ) {
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( DateTimeOffset.UtcNow.AddMinutes( -1 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler ) );

        Assert.AreEqual( TimeSpan.FromSeconds( 5 ), exception.RetryAfterValue );
    }

    #endregion

    #region Provider Detection Tests

    /// <summary>Provider URI templates map to stable, endpoint-specific protocol keys.</summary>
    [TestMethod]
    [DataRow( SupportedProviders.Spotify, "https://accounts.spotify.com/api/token", "auth/token" )]
    [DataRow( SupportedProviders.Spotify, "https://api.spotify.com/v1/tracks?ids=1,2", "tracks" )]
    [DataRow( SupportedProviders.Spotify, "https://api.spotify.com/v1/tracks/123", "tracks/:id" )]
    [DataRow( SupportedProviders.Spotify, "https://api.spotify.com/v1/artists/123/top-tracks?market=US", "artists/:id/top-tracks" )]
    [DataRow( SupportedProviders.Spotify, "https://api.spotify.com/v1/albums/123/tracks", "albums/:id/tracks" )]
    [DataRow( SupportedProviders.Spotify, "https://api.spotify.com/v1/search?q=test&type=track%2Cartist", "search" )]
    [DataRow( SupportedProviders.Spotify, "https://api.spotify.com/v1/search?q=test&type=track", "search:track" )]
    [DataRow( SupportedProviders.AppleMusic, "https://api.music.apple.com/v1/catalog/us/songs/123", "songs/:id" )]
    [DataRow( SupportedProviders.AppleMusic, "https://api.music.apple.com/v1/catalog/us/artists/123/albums", "artists/:id/albums" )]
    [DataRow( SupportedProviders.AppleMusic, "https://api.music.apple.com/v1/catalog/us/search?term=test&types=songs", "search" )]
    [DataRow( SupportedProviders.AppleMusic, "https://api.music.apple.com/v1/catalog/us", ProviderEndpointConstants.Unknown )]
    [DataRow( SupportedProviders.Tidal, "https://auth.tidal.com/v1/oauth2/token", "auth/token" )]
    [DataRow( SupportedProviders.Tidal, "https://openapi.tidal.com/v2/searchResults/abc", "search-results" )]
    [DataRow( SupportedProviders.Tidal, "https://openapi.tidal.com/v2/artists/123/relationships/tracks", "artists/:id/relationships/tracks" )]
    public void ProviderRateLimitEndpoint_FromRequest_ReturnsExpectedTemplate(
        SupportedProviders provider,
        string requestUri,
        string expected
    ) {
        string endpoint = ProviderRateLimitEndpoint.FromRequest( provider, new Uri( requestUri ) );

        Assert.AreEqual( expected, endpoint );
    }

    /// <summary>Legacy bulk endpoint keys normalize to the shared collection vocabulary.</summary>
    [TestMethod]
    [DataRow( "BulkTracks", "tracks" )]
    [DataRow( "bulktracks", "tracks" )]
    [DataRow( "BulkAlbums", "albums" )]
    [DataRow( "BulkArtists", "artists" )]
    [DataRow( "tracks/:id", "tracks/:id" )]
    public void ProviderEndpointConstants_Normalize_ReturnsCanonicalKey( string input, string expected ) {
        Assert.AreEqual( expected, ProviderEndpointConstants.Normalize( input ) );
    }

    /// <summary>Effective endpoint resolution prefers concrete reports and otherwise uses its fallback.</summary>
    [TestMethod]
    [DataRow( "tracks/:id", "albums/:id", "tracks/:id" )]
    [DataRow( ProviderEndpointConstants.Unknown, "albums/:id", "albums/:id" )]
    [DataRow( null, null, ProviderEndpointConstants.Unknown )]
    public void ProviderEndpointConstants_ResolveEffective_ReturnsCanonicalEndpoint(
        string? reported,
        string? fallback,
        string expected
    ) {
        Assert.AreEqual( expected, ProviderEndpointConstants.ResolveEffective( reported, fallback ) );
    }

    /// <summary>
    /// A fail-fast exception for a Spotify request URI carries <see cref="SupportedProviders.Spotify"/>
    /// and the originating request URI.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WithSpotifyUri_ShouldIncludeProviderInException( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 300 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler, new Uri( "https://api.spotify.com/v1/tracks/123" ) )
        );

        Assert.AreEqual( SupportedProviders.Spotify, exception.Provider );
        Assert.AreEqual( "tracks/:id", exception.Endpoint );
        Assert.IsNotNull( exception.RequestUri );
        Assert.Contains( "spotify", exception.RequestUri.Host );
    }

    /// <summary>
    /// A fail-fast exception for an Apple Music request URI carries
    /// <see cref="SupportedProviders.AppleMusic"/>.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WithAppleMusicUri_ShouldIncludeProviderInException( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 300 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler, new Uri( "https://api.music.apple.com/v1/catalog/us/songs/123" ) )
        );

        Assert.AreEqual( SupportedProviders.AppleMusic, exception.Provider );
    }

    /// <summary>
    /// A fail-fast exception for a Tidal request URI carries <see cref="SupportedProviders.Tidal"/>.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WithTidalUri_ShouldIncludeProviderInException( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 300 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler, new Uri( "https://openapi.tidal.com/v2/tracks/123" ) )
        );

        Assert.AreEqual( SupportedProviders.Tidal, exception.Provider );
    }

    /// <summary>
    /// A fail-fast exception for an unrecognized request host carries a null provider.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WithUnknownUri_ShouldHaveNullProvider( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 300 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler, new Uri( "https://unknown-api.example.com/endpoint" ) )
        );

        Assert.IsNull( exception.Provider );
    }

    /// <summary>A 429 publishes the concrete endpoint cooldown to the shared tracker.</summary>
    [TestMethod]
    public async Task SendAsync_With429_ShouldPublishConcreteEndpointCooldown( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Tidal, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 45 ) );
        SetupInnerHandler( handler, response );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler, new Uri(
                "https://openapi.tidal.com/v2/artists/123/relationships/tracks?countryCode=US" ) ) );

        Assert.AreEqual( "artists/:id/relationships/tracks", exception.Endpoint );
        tracker.Verify( value => value.SetRateLimitedAsync(
            SupportedProviders.Tidal,
            "artists/:id/relationships/tracks",
            It.Is<DateTimeOffset>( retryAfter => retryAfter > DateTimeOffset.UtcNow ),
            It.IsAny<CancellationToken>( ) ), Times.Once );
    }

    /// <summary>A cooldown for one endpoint does not suppress a different endpoint on the provider.</summary>
    [TestMethod]
    public async Task SendAsync_WhenDifferentEndpointIsLimited_ShouldSendRequest( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Tidal, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint( "tracks", DateTimeOffset.UtcNow.AddMinutes( 1 ) )] );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        SetupInnerHandler( handler, new HttpResponseMessage( HttpStatusCode.OK ) );

        HttpResponseMessage result = await SendRequestAsync(
            handler,
            new Uri( "https://openapi.tidal.com/v2/tracks/123?countryCode=US" ) );

        Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
    }

    /// <summary>An active cooldown for the exact endpoint blocks the request before provider I/O.</summary>
    [TestMethod]
    public async Task SendAsync_WhenSameEndpointIsLimited_ShouldFailBeforeInnerHandler( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Tidal, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint(
                "tracks/:id", DateTimeOffset.UtcNow.AddMinutes( 1 ) )] );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        handler.InnerHandler = new UnexpectedCallHandler( );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync(
                handler,
                new Uri( "https://openapi.tidal.com/v2/tracks/123?countryCode=US" ) ) );

        Assert.AreEqual( "tracks/:id", exception.Endpoint );
    }

    /// <summary>A Spotify provider-wide cooldown blocks every data endpoint before provider I/O.</summary>
    [TestMethod]
    public async Task SendAsync_WhenSpotifyProviderIsLimited_ShouldBlockSingleItemLane( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint(
                ProviderEndpointConstants.ProviderWide,
                DateTimeOffset.UtcNow.AddMinutes( 1 ) )] );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        handler.InnerHandler = new UnexpectedCallHandler( );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync(
                handler,
                new Uri( "https://api.spotify.com/v1/tracks/123" ) ) );

        Assert.AreEqual( "tracks/:id", exception.Endpoint );
    }

    /// <summary>A Spotify data-API cooldown does not suppress independent token acquisition.</summary>
    [TestMethod]
    public async Task SendAsync_WhenSpotifyProviderIsLimited_ShouldStillAllowAuthTokenRequest( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Spotify, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [new RateLimitedEndpoint(
                ProviderEndpointConstants.ProviderWide,
                DateTimeOffset.UtcNow.AddMinutes( 1 ) )] );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        SetupInnerHandler( handler, new HttpResponseMessage( HttpStatusCode.OK ) );

        HttpResponseMessage result = await SendRequestAsync(
            handler,
            new Uri( "https://accounts.spotify.com/api/token" ) );

        Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
    }

    /// <summary>A tracker read outage is best-effort and does not suppress provider I/O.</summary>
    [TestMethod]
    public async Task SendAsync_WhenCooldownReadFails_ShouldStillSendRequest( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Tidal, It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Redis unavailable" ) );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        SetupInnerHandler( handler, new HttpResponseMessage( HttpStatusCode.OK ) );

        HttpResponseMessage result = await SendRequestAsync(
            handler,
            new Uri( "https://openapi.tidal.com/v2/tracks/123?countryCode=US" ) );

        Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
    }

    /// <summary>A tracker write outage cannot mask the provider's typed 429 signal.</summary>
    [TestMethod]
    public async Task SendAsync_WhenCooldownWriteFails_ShouldStillThrowTypedRateLimit( ) {
        Mock<IRateLimitTracker> tracker = new( );
        _ = tracker.Setup( value => value.GetAllRateLimitedAsync(
                SupportedProviders.Tidal, It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( [] );
        _ = tracker.Setup( value => value.SetRateLimitedAsync(
                SupportedProviders.Tidal,
                It.IsAny<string>( ),
                It.IsAny<DateTimeOffset>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "Redis unavailable" ) );
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds, tracker.Object );
        HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        response.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 45 ) );
        SetupInnerHandler( handler, response );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync(
                handler,
                new Uri( "https://openapi.tidal.com/v2/tracks/123?countryCode=US" ) ) );

        Assert.AreEqual( TimeSpan.FromSeconds( 45 ), exception.RetryAfterValue );
        Assert.AreEqual( "tracks/:id", exception.Endpoint );
    }

    #endregion

    #region Logging Tests

    /// <summary>
    /// The fail-fast path logs a warning carrying the rate-limit-exceeded event id exactly once.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WhenThrowingException_ShouldLogWarning( ) {
        // Arrange
        RetryAfterLimitHandler handler = CreateHandler( DefaultMaxRetryAfterSeconds );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 300 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act
        try {
            _ = await SendRequestAsync( handler, new Uri( "https://api.spotify.com/v1/tracks/123" ) );
        } catch (RetryAfterExceededException) {
            // Expected
        }

        // Assert - Verify warning was logged with the correct EventId
        // LoggerMessage source-generated methods pass a LoggerMessageState struct to Log,
        // so we verify by EventId instead of checking message content directly
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.Is<EventId>( e => e.Id == LogEventIds.Providers.Common.RateLimitExceeded ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception?>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( )
            ),
            Times.Once
        );
    }

    #endregion

    #region Edge Cases

    /// <summary>
    /// With a zero threshold, any positive <c>Retry-After</c> (1s) exceeds it and throws.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WithZeroThreshold_AnyRetryAfterShouldThrow( ) {
        // Arrange - Zero threshold means any Retry-After exceeds it
        RetryAfterLimitHandler handler = CreateHandler( 0 );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromSeconds( 1 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act & Assert
        _ = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => SendRequestAsync( handler )
        );
    }

    /// <summary>
    /// With a large threshold, a below-threshold wait remains a normal typed deferral.
    /// </summary>
    [TestMethod]
    public async Task SendAsync_WithLargeThreshold_ShouldThrowTypedRateLimit( ) {
        // Arrange - Very large threshold (1 hour)
        RetryAfterLimitHandler handler = CreateHandler( 3600 );
        HttpResponseMessage rateLimitResponse = new( HttpStatusCode.TooManyRequests );
        rateLimitResponse.Headers.RetryAfter = new RetryConditionHeaderValue( TimeSpan.FromMinutes( 30 ) );
        SetupInnerHandler( handler, rateLimitResponse );

        // Act
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => SendRequestAsync( handler ) );

        // Assert
        Assert.AreEqual( TimeSpan.FromMinutes( 30 ), exception.RetryAfterValue );
    }

    /// <summary>An extreme worker envelope saturates to a typed duration instead of overflowing.</summary>
    [TestMethod]
    public async Task WorkerHandler_WithExtremeEnvelopeDuration_ShouldThrowTypedRateLimit( ) {
        HttpResponseMessage response = new( HttpStatusCode.TooManyRequests ) {
            Content = JsonContent.Create( ProviderLookupResponse.Error(
                "rate limited",
                retryAfterSeconds: double.MaxValue,
                retryThresholdSeconds: 5,
                rateLimitedEndpoint: ProviderEndpointConstants.ProviderWide ) )
        };
        WorkerRateLimitHandler handler = new( SupportedProviders.Spotify, new QueueSettings( ) ) {
            InnerHandler = new TestDelegatingHandler( response )
        };
        using HttpMessageInvoker invoker = new( handler );

        RetryAfterExceededException exception = await Assert.ThrowsExactlyAsync<RetryAfterExceededException>(
            ( ) => invoker.SendAsync(
                new HttpRequestMessage( HttpMethod.Get, "https://worker.example/lookup/isrc/test" ),
                CancellationToken.None ) );

        Assert.AreEqual( QueueSettings.DefaultMaximumRateLimitRetryAfter, exception.RetryAfterValue );
        Assert.AreEqual( TimeSpan.FromSeconds( 5 ), exception.Threshold );
    }

    #endregion

    #region Helper Methods

    /// <summary>Builds a <see cref="RetryAfterLimitHandler"/> with the given threshold and the test logger.</summary>
    private RetryAfterLimitHandler CreateHandler(
        int maxRetryAfterSeconds,
        IRateLimitTracker? rateLimitTracker = null
    ) {
        return new RetryAfterLimitHandler(
            maxRetryAfterSeconds,
            new QueueSettings( ),
            _loggerMock.Object,
            rateLimitTracker );
    }

    /// <summary>
    /// Sets the handler's inner handler to a stub that always returns <paramref name="response"/>.
    /// </summary>
    private static void SetupInnerHandler( RetryAfterLimitHandler handler, HttpResponseMessage response ) {
        TestDelegatingHandler innerHandler = new( response );
        handler.InnerHandler = innerHandler;
    }

    /// <summary>
    /// Sends a GET request (to the given URI or a default) through the handler and returns the response.
    /// </summary>
    private static async Task<HttpResponseMessage> SendRequestAsync( RetryAfterLimitHandler handler, Uri? requestUri = null ) {
        using HttpMessageInvoker invoker = new( handler );
        HttpRequestMessage request = new( HttpMethod.Get, requestUri ?? new Uri( "https://test.example.com/api" ) );
        return await invoker.SendAsync( request, CancellationToken.None );
    }

    /// <summary>Inner-handler stub that returns a fixed <see cref="HttpResponseMessage"/> for any request.</summary>
    private class TestDelegatingHandler( HttpResponseMessage response ) : HttpMessageHandler {
        /// <summary>Returns the fixed response without contacting any network.</summary>
        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken ) {
            return Task.FromResult( response );
        }
    }

    /// <summary>Fails a test if the request reaches provider I/O.</summary>
    private sealed class UnexpectedCallHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException( "The inner handler must not be called." );
    }

    #endregion
}
