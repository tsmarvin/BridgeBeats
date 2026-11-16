using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Contracts.Records.WorkerApi;
using BridgeBeats.Core.Domain.Extensions;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Services.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace BridgeBeats.Tests.Integration;

/// <summary>End-to-end contract tests through the production worker endpoint mapper and proxy.</summary>
[TestClass]
public sealed class WorkerEndpointContractIntegrationTests {
    /// <summary>MSTest cancellation token for HTTP requests.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Non-JSON rate-limit responses use numeric or HTTP-date Retry-After metadata.</summary>
    [TestMethod]
    [DataRow( "10" )]
    public async Task ProductionProxy_NonJson429_ParsesRetryMetadataIntoBaseException( string retryAfter ) {
        using HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        _ = response.Headers.TryAddWithoutValidation( "Retry-After", retryAfter );
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => new HttpMusicLookupService( SupportedProviders.Spotify, CreateStaticFactory( response ), "spotify", NullLogger<HttpMusicLookupService>.Instance )
                .GetInfoByISRCAsync( "US-429" ) );
        Assert.IsGreaterThanOrEqualTo( 0, exception.RetryAfterValue.TotalSeconds );
    }

    /// <summary>HTTP-date Retry-After metadata is accepted on a non-JSON 429 response.</summary>
    [TestMethod]
    public async Task ProductionProxy_NonJson429_HttpDateParsesIntoBaseException( ) {
        using HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        _ = response.Headers.TryAddWithoutValidation( "Retry-After", DateTimeOffset.UtcNow.AddMinutes( 10 ).ToString( "R" ) );
        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => new HttpMusicLookupService( SupportedProviders.Spotify, CreateStaticFactory( response ), "spotify", NullLogger<HttpMusicLookupService>.Instance )
                .GetInfoByISRCAsync( "US-429-DATE" ) );
        Assert.IsGreaterThan( 0, exception.RetryAfterValue.TotalSeconds );
    }

    /// <summary>Missing or invalid rate-limit metadata remains a status-bearing failure.</summary>
    [TestMethod]
    [DataRow( null )]
    [DataRow( "not-a-date" )]
    public async Task ProductionProxy_429MissingOrInvalidRetryMetadata_ThrowsStatusBearingFailure( string? retryAfter ) {
        using HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        if (retryAfter is not null) _ = response.Headers.TryAddWithoutValidation( "Retry-After", retryAfter );
        HttpRequestException exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            ( ) => new HttpMusicLookupService( SupportedProviders.Spotify, CreateStaticFactory( response ), "spotify", NullLogger<HttpMusicLookupService>.Instance )
                .GetInfoByISRCAsync( "US-429" ) );
        Assert.AreEqual( HttpStatusCode.TooManyRequests, exception.StatusCode );
    }

    /// <summary>The precise envelope delay wins over the ceiling-rounded Retry-After header.</summary>
    [TestMethod]
    public async Task ProductionProxy_429EnvelopePrecisionPreventsFalseThresholdExceeded( ) {
        using HttpResponseMessage response = new( HttpStatusCode.TooManyRequests ) {
            Content = JsonContent.Create( ProviderLookupResponse.Error(
                "Provider rate limit exceeded.", retryAfterSeconds: 30.2, retryThresholdSeconds: 30.5 ) )
        };
        _ = response.Headers.TryAddWithoutValidation( "Retry-After", "31" );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => new HttpMusicLookupService(
                    SupportedProviders.Spotify,
                    CreateStaticFactory( response ),
                    "spotify",
                    NullLogger<HttpMusicLookupService>.Instance )
                .GetInfoByISRCAsync( "US-PRECISE-RETRY" ) );

        Assert.AreEqual( 30.2, exception.RetryAfterValue.TotalSeconds, 0.001 );
    }

    /// <summary>Non-JSON 503 and 200 responses become status-bearing protocol failures.</summary>
    [TestMethod]
    [DataRow( HttpStatusCode.ServiceUnavailable )]
    [DataRow( HttpStatusCode.OK )]
    public async Task ProductionProxy_NonJsonResponse_ThrowsStatusBearingProtocolFailure( HttpStatusCode status ) {
        using HttpResponseMessage response = new( status ) { Content = new StringContent( "not-json" ) };
        HttpRequestException exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            ( ) => new HttpMusicLookupService( SupportedProviders.Spotify, CreateStaticFactory( response ), "spotify", NullLogger<HttpMusicLookupService>.Instance )
                .GetInfoByISRCAsync( "US-PROTOCOL" ) );
        Assert.AreEqual( status, exception.StatusCode );
    }

    private static IHttpClientFactory CreateStaticFactory( HttpResponseMessage response ) {
        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( value => value.CreateClient( It.IsAny<string>( ) ) )
            .Returns( new HttpClient( new StaticResponseHandler( response ) ) { BaseAddress = new Uri( "https://worker.test" ) } );
        return factory.Object;
    }

    private sealed class StaticResponseHandler( HttpResponseMessage response ) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
            => Task.FromResult( response );
    }

    /// <summary>Production proxy success traverses the mapped worker endpoint.</summary>
    [TestMethod]
    public async Task CurrentContractSuccess_TraversesEndpointAndProductionProxy( ) {
        await using WebApplication app = await StartAsync( FakeLookupMode.Success );
        Mock<IHttpClientFactory> factory = CreateFactory( app );
        HttpMusicLookupService proxy = new( SupportedProviders.Spotify, factory.Object, "spotify", NullLogger<HttpMusicLookupService>.Instance );

        MusicLookupResult? result = await proxy.GetInfoByISRCAsync( "US-TEST" );

        Assert.IsNotNull( result );
        Assert.AreEqual( "US-TEST", result!.ExternalId );
    }

    /// <summary>Current rate-limit responses carry retry metadata and no upstream secret.</summary>
    [TestMethod]
    public async Task CurrentContractRateLimit_ExposesCanonicalMetadataAndSanitizesSecrets( ) {
        await using WebApplication app = await StartAsync( FakeLookupMode.RateLimited );
        using HttpClient client = app.GetTestClient( );
        using HttpRequestMessage request = new( HttpMethod.Post, "/lookup/isrc" ) {
            Content = JsonContent.Create( new LookupByIsrcRequest( "US-SECRET" ) )
        };
        using HttpResponseMessage response = await client.SendAsync( request, TestContext.CancellationToken );
        string body = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.TooManyRequests, response.StatusCode );
        Assert.AreEqual( "10", response.Headers.GetValues( "Retry-After" ).Single( ) );
        ProviderLookupResponse? envelope = await response.Content.ReadFromJsonAsync<ProviderLookupResponse>( TestContext.CancellationToken );
        Assert.IsNotNull( envelope );
        Assert.AreEqual( "Provider rate limit exceeded.", envelope.ErrorMessage );
        Assert.AreEqual( 10d, envelope.RetryAfterSeconds );
        Assert.AreEqual( 5d, envelope.RetryThresholdSeconds );
        Assert.IsFalse( body.Contains( "secret-upstream-uri", StringComparison.Ordinal ) );
    }

    /// <summary>Retry-After values beyond the header's integer range are clamped safely.</summary>
    [TestMethod]
    public async Task CurrentContractRateLimit_ClampsHugeRetryAfterHeaderToIntMax( ) {
        await using WebApplication app = await StartAsync( FakeLookupMode.HugeRateLimited );
        using HttpRequestMessage request = new( HttpMethod.Post, "/lookup/isrc" ) {
            Content = JsonContent.Create( new LookupByIsrcRequest( "US-HUGE-RETRY" ) )
        };

        using HttpResponseMessage response = await app.GetTestClient( ).SendAsync( request, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.TooManyRequests, response.StatusCode );
        Assert.AreEqual( int.MaxValue.ToString( CultureInfo.InvariantCulture ),
            response.Headers.GetValues( "Retry-After" ).Single( ) );
    }

    /// <summary>The current contract distinguishes a successful no-match from a thrown not-found failure.</summary>
    [TestMethod]
    public async Task CurrentContractNullResult_Returns200SuccessNotFoundEnvelope( ) {
        await using WebApplication app = await StartAsync( FakeLookupMode.NotFound );
        using HttpClient client = app.GetTestClient( );
        using HttpRequestMessage request = new( HttpMethod.Post, "/lookup/isrc" ) {
            Content = JsonContent.Create( new LookupByIsrcRequest( "US-MISSING" ) )
        };
        using HttpResponseMessage response = await client.SendAsync( request, TestContext.CancellationToken );
        ProviderLookupResponse? envelope = await response.Content.ReadFromJsonAsync<ProviderLookupResponse>( TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        Assert.IsTrue( envelope!.Success );
        Assert.IsNull( envelope.Result );
    }

    /// <summary>The current contract maps classified provider failures to status-bearing responses.</summary>
    [TestMethod]
    [DataRow( FakeLookupMode.Upstream, HttpStatusCode.ServiceUnavailable )]
    [DataRow( FakeLookupMode.Timeout, HttpStatusCode.GatewayTimeout )]
    [DataRow( FakeLookupMode.Unexpected, HttpStatusCode.InternalServerError )]
    public async Task CurrentContractFailureMapping_UsesSanitizedStatus( FakeLookupMode mode, HttpStatusCode expectedStatus ) {
        await using WebApplication app = await StartAsync( mode );
        using HttpClient client = app.GetTestClient( );
        using HttpRequestMessage request = new( HttpMethod.Post, "/lookup/isrc" ) {
            Content = JsonContent.Create( new LookupByIsrcRequest( "US-FAIL" ) )
        };
        using HttpResponseMessage response = await client.SendAsync( request, TestContext.CancellationToken );
        string body = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );

        Assert.AreEqual( expectedStatus, response.StatusCode );
        Assert.IsFalse( body.Contains( "secret-upstream-uri", StringComparison.Ordinal ) );
        Assert.IsFalse( body.Contains( "secret-exception", StringComparison.Ordinal ) );
    }

    /// <summary>Unexpected endpoint failures log one exception-bearing entry while returning sanitized 500.</summary>
    [TestMethod]
    public async Task CurrentContractUnexpectedFailure_LogsExceptionOnce( ) {
        CapturingLoggerProvider provider = new( );
        await using WebApplication app = await StartAsync( FakeLookupMode.Unexpected, provider );
        using HttpRequestMessage request = new( HttpMethod.Post, "/lookup/isrc" ) {
            Content = JsonContent.Create( new LookupByIsrcRequest( "US-UNEXPECTED" ) )
        };
        using HttpResponseMessage response = await app.GetTestClient( ).SendAsync( request, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.InternalServerError, response.StatusCode );
        _ = Assert.ContainsSingle( provider.Exceptions );
        _ = Assert.IsInstanceOfType<InvalidOperationException>( provider.Exceptions[0] );
    }

    /// <summary>The production proxy maps current-contract not-found to null and 503 to a status-bearing exception.</summary>
    [TestMethod]
    public async Task ProductionProxy_MapsCurrentNotFoundAndUnavailable( ) {
        await using WebApplication notFoundApp = await StartAsync( FakeLookupMode.NotFound );
        MusicLookupResult? notFound = await new HttpMusicLookupService(
            SupportedProviders.Spotify, CreateFactory( notFoundApp ).Object, "spotify", NullLogger<HttpMusicLookupService>.Instance )
            .GetInfoByISRCAsync( "US-MISSING" );
        Assert.IsNull( notFound );

        await using WebApplication unavailableApp = await StartAsync( FakeLookupMode.Upstream );
        HttpRequestException error = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            ( ) => new HttpMusicLookupService(
                SupportedProviders.Spotify, CreateFactory( unavailableApp ).Object, "spotify", NullLogger<HttpMusicLookupService>.Instance )
                .GetInfoByISRCAsync( "US-UNAVAILABLE" ) );
        Assert.AreEqual( HttpStatusCode.ServiceUnavailable, error.StatusCode );
    }

    /// <summary>
    /// Runs the real queue processor against the production HTTP proxy and mapped worker endpoint.
    /// The terminal action proves that null, rate-limit, and transient status responses travel
    /// through the current contract into the queue processor's corresponding paths.
    /// </summary>
    [TestMethod]
    [DataRow( FakeLookupMode.NotFound, "update" )]
    [DataRow( FakeLookupMode.RateLimited, "requeue" )]
    [DataRow( FakeLookupMode.Upstream, "dlq" )]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task QueueProcessor_TraversesCurrentWorkerContract( FakeLookupMode mode, string expectedAction ) {
        await using WebApplication app = await StartAsync( mode );
        Mock<IHttpClientFactory> factory = CreateFactory( app );
        HttpMusicLookupService proxy = new( SupportedProviders.Spotify, factory.Object, "spotify", NullLogger<HttpMusicLookupService>.Instance );

        Mock<IConnectionMultiplexer> redis = new( );
        Mock<ISubscriber> subscriber = new( );
        _ = redis.Setup( value => value.GetSubscriber( It.IsAny<object>( ) ) ).Returns( subscriber.Object );
        Mock<IRequestQueue<QueuedLookupRequest>> queue = new( );
        Mock<IRateLimitTracker> rateLimits = new( );
        Mock<ISagaStateManager> sagas = new( );
        string sagaId = ISagaStateManager.GenerateSagaId( "IsrcLookup:US-CONTRACT" );
        QueuedLookupRequest request = new( ) {
            RequestId = $"contract-request-{Guid.NewGuid( ):N}",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US-CONTRACT",
            SagaId = sagaId,
            SagaInstanceToken = "contract-instance",
            AttemptCount = mode == FakeLookupMode.Upstream ? LookupConstants.MaxQueueRetryAttempts : 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        QueuedMessage<QueuedLookupRequest> message = new( "contract-message", request, DateTimeOffset.UtcNow ) {
            Priority = QueuePriority.Interactive
        };
        LookupSagaState saga = new( ) {
            SagaId = sagaId,
            LookupKey = "IsrcLookup:US-CONTRACT",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US-CONTRACT",
            InstanceToken = "contract-instance"
        };
        _ = sagas.Setup( value => value.GetOrCreateAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<LookupRequestType>( ), It.IsAny<string>( ),
                It.IsAny<QueuePriority?>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( saga );
        _ = sagas.Setup( value => value.GetAsync( It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( saga );
        _ = sagas.Setup( value => value.TryInitializeProviderStatesAsync(
                It.IsAny<string>( ), It.IsAny<IEnumerable<SupportedProviders>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = sagas.Setup( value => value.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = sagas.Setup( value => value.TrySetIsPartialAsync(
                It.IsAny<string>( ), It.IsAny<bool>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = sagas.Setup( value => value.TrySetRateLimitInfoAsync(
                It.IsAny<string>( ), It.IsAny<List<ProviderRateLimitInfo>>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( true );
        _ = rateLimits.Setup( value => value.GetStateAsync(
                It.IsAny<SupportedProviders>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new RateLimitState( false, null, null ) );

        TaskCompletionSource<bool> terminalAction = new( TaskCreationOptions.RunContinuationsAsynchronously );
        _ = sagas.Setup( value => value.TryUpdateProviderStateAsync(
                It.IsAny<string>( ), It.IsAny<ProviderLookupState>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => {
                if (expectedAction == "update") {
                    _ = terminalAction.TrySetResult( true );
                }
            } )
            .ReturnsAsync( true );
        _ = queue.Setup( value => value.EnqueueAsync(
                It.IsAny<QueuedLookupRequest>( ), It.IsAny<QueuePriority>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => {
                if (expectedAction == "requeue") {
                    _ = terminalAction.TrySetResult( true );
                }
            } );
        _ = queue.Setup( value => value.RequeueAsync(
                It.IsAny<string>( ), It.IsAny<TimeSpan?>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => { } );
        _ = queue.Setup( value => value.MoveToDlqAsync(
                It.IsAny<string>( ), It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ) )
            .Callback( ( ) => terminalAction.TrySetResult( true ) );

        int dequeueCount = 0;
        _ = queue.Setup( value => value.DequeueAsync( It.IsAny<IRateLimitTracker>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( ( ) => Interlocked.Increment( ref dequeueCount ) == 1 ? message : null );

        QueueProcessorBackgroundService processor = new(
            redis.Object,
            queue.Object,
            rateLimits.Object,
            sagas.Object,
            proxy,
            SupportedProviders.Spotify,
            NullLogger<QueueProcessorBackgroundService>.Instance );
        await processor.StartAsync( CancellationToken.None );
        _ = await terminalAction.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
        await processor.StopAsync( CancellationToken.None );

        switch (expectedAction) {
            case "update":
                sagas.Verify( value => value.TryUpdateProviderStateAsync(
                    sagaId, It.Is<ProviderLookupState>( state => state.IsComplete && !state.IsSuccess ),
                    It.IsAny<string>( ),
                    It.IsAny<CancellationToken>( ) ), Times.Once );
                break;
            case "requeue":
                queue.Verify( value => value.EnqueueAsync(
                    It.Is<QueuedLookupRequest>( value => value.AttemptCount == 1 && value.EnqueueOrigin == QueueEnqueueOrigin.Requeue ),
                    QueuePriority.Background, It.IsAny<CancellationToken>( ) ), Times.Once );
                break;
            case "dlq":
                queue.Verify( value => value.MoveToDlqAsync( "contract-message", It.IsAny<string>( ), It.IsAny<CancellationToken>( ) ), Times.Once );
                break;
        }
    }

    private static Mock<IHttpClientFactory> CreateFactory( WebApplication app ) {
        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( f => f.CreateClient( "spotify" ) )
            .Returns( ( ) => new HttpClient( app.GetTestServer( ).CreateHandler( ) ) { BaseAddress = new Uri( "http://localhost" ) } );
        return factory;
    }

    private static async Task<WebApplication> StartAsync( FakeLookupMode mode, ILoggerProvider? loggerProvider = null ) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder( new WebApplicationOptions {
            ApplicationName = typeof( WorkerEndpointContractIntegrationTests ).Assembly.FullName
        } );
        _ = builder.WebHost.UseTestServer( );
        if (loggerProvider is not null) {
            _ = builder.Logging.ClearProviders( ).AddProvider( loggerProvider );
        }
        _ = builder.Services.AddSingleton( new FakeLookupService( mode ) );
        WebApplication app = builder.Build( );
        _ = app.MapProviderLookupEndpoints<FakeLookupService>( );
        await app.StartAsync( );
        return app;
    }

    /// <summary>Behavior selected by the test lookup implementation.</summary>
    public enum FakeLookupMode {
        /// <summary>Return a successful result.</summary>
        Success,
        /// <summary>Throw a rate-limit exception.</summary>
        RateLimited,
        /// <summary>Throw a rate-limit exception whose Retry-After exceeds the header integer range.</summary>
        HugeRateLimited,
        /// <summary>Return no result.</summary>
        NotFound,
        /// <summary>Throw an upstream HTTP exception.</summary>
        Upstream,
        /// <summary>Throw a timeout.</summary>
        Timeout,
        /// <summary>Throw an unexpected exception.</summary>
        Unexpected
    }

    private sealed class FakeLookupService( FakeLookupMode mode ) : IMusicLookupService {
        public static SupportedProviders Provider => SupportedProviders.Spotify;
        public Task<MusicLookupResult?> GetInfoAsync( string uri ) => ResolveAsync( );
        public Task<MusicLookupResult?> GetInfoAsync( string title, string artist ) => ResolveAsync( );
        public Task<MusicLookupResult?> GetInfoAsync( MusicLookupResult lookup ) => ResolveAsync( );
        public Task<MusicLookupResult?> GetInfoByIDAsync( string providerId, bool isAlbum ) => ResolveAsync( );
        public Task<MusicLookupResult?> GetInfoByUPCAsync( string upc ) => ResolveAsync( );
        public Task<MusicLookupResult?> GetInfoByISRCAsync( string isrc ) => ResolveAsync( );

        private Task<MusicLookupResult?> ResolveAsync( ) => mode switch {
            FakeLookupMode.Success => Task.FromResult<MusicLookupResult?>( new MusicLookupResult { ExternalId = "US-TEST", Artist = "Artist", Title = "Title" } ),
            FakeLookupMode.NotFound => Task.FromResult<MusicLookupResult?>( null ),
            FakeLookupMode.Upstream => Task.FromException<MusicLookupResult?>( new HttpRequestException( "secret-upstream-uri", null, HttpStatusCode.BadGateway ) ),
            FakeLookupMode.Timeout => Task.FromException<MusicLookupResult?>( new TimeoutException( "secret-exception" ) ),
            FakeLookupMode.Unexpected => Task.FromException<MusicLookupResult?>( new InvalidOperationException( "secret-exception" ) ),
            FakeLookupMode.HugeRateLimited => Task.FromException<MusicLookupResult?>( new RetryAfterExceededException(
                TimeSpan.FromSeconds( (double)int.MaxValue + 1000 ), TimeSpan.FromSeconds( 5 ), new Uri( "https://secret-upstream-uri" ), SupportedProviders.Spotify ) ),
            _ => Task.FromException<MusicLookupResult?>( new RetryAfterExceededException(
                TimeSpan.FromSeconds( 10 ), TimeSpan.FromSeconds( 5 ), new Uri( "https://secret-upstream-uri" ), SupportedProviders.Spotify ) )
        };
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider {
        public List<Exception> Exceptions { get; } = [];
        public ILogger CreateLogger( string categoryName ) => new CapturingLogger( Exceptions );
        public void Dispose( ) { }
    }

    private sealed class CapturingLogger( List<Exception> exceptions ) : ILogger {
        public IDisposable BeginScope<TState>( TState state ) where TState : notnull => NullScope.Instance;
        public bool IsEnabled( LogLevel logLevel ) => true;
        public void Log<TState>( LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter ) {
            if (exception is not null) exceptions.Add( exception );
        }
        private sealed class NullScope : IDisposable {
            public static NullScope Instance { get; } = new( );
            public void Dispose( ) { }
        }
    }
}
