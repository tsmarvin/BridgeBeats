using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Storage;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using Microsoft.Extensions.Logging;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>Direct contract tests for revision-guarded destructive PDS disposition.</summary>
[TestClass]
public sealed class ATProtoStorageServiceDeleteTests : IDisposable {
    private const string TestDid = "did:plc:testuser12345";
    private const string RecordUri = "at://did:plc:testuser12345/link.bridgebeats.lookup/track:USRC12345678";
    private const string ExpectedCid = "bafyreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku";

    private RecordingHandler _handler = null!;
    private HttpClient _agentHttpClient = null!;
    private BlueskyAgent _agent = null!;
    private ATProtoStorageService _service = null!;

    /// <summary>Creates an authenticated agent backed by a deterministic in-memory HTTP handler.</summary>
    [TestInitialize]
    public async Task Initialize( ) {
        _handler = new RecordingHandler( );
        _agentHttpClient = new HttpClient( _handler );
        Mock<IHttpClientFactory> agentHttpFactory = new( );
        _ = agentHttpFactory.Setup( factory => factory.CreateClient( It.IsAny<string>( ) ) )
            .Returns( _agentHttpClient );

        _agent = new BlueskyAgent( agentHttpFactory.Object, new BlueskyAgentOptions( ) );
        AccessCredentials credentials = new(
            new Uri( "https://pds.test.example" ),
            AuthenticationType.UsernamePassword,
            CreateUnsignedJwt( TestDid ),
            "refresh-token" );
        _ = await _agent.Login( credentials, CancellationToken.None );

        Mock<IATProtoSessionManager> sessionManager = new( );
        _ = sessionManager.Setup( manager => manager.GetAuthenticatedAgentAsync(
                It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( _agent );

        Mock<IHttpClientFactory> carHttpFactory = new( );
        _ = carHttpFactory.Setup( factory => factory.CreateClient( It.IsAny<string>( ) ) )
            .Returns( new HttpClient( new RecordingHandler( ) ) );

        _service = new ATProtoStorageService(
            sessionManager.Object,
            Mock.Of<ILogger<ATProtoStorageService>>( ),
            carHttpFactory.Object,
            TimeSpan.FromHours( 1 ) );

        _handler.Reset( );
    }

    /// <inheritdoc/>
    public void Dispose( ) {
        _agent?.Dispose( );
        _agentHttpClient?.Dispose( );
    }

    /// <summary>A successful CAS delete sends the expected repo, collection, rkey, and CID.</summary>
    [TestMethod]
    public async Task Delete_MatchingRevision_ReturnsDeletedAndSendsCasRequest( ) {
        _handler.ResponseStatus = HttpStatusCode.OK;
        _handler.ResponseBody = $"{{\"commit\":{{\"cid\":\"{ExpectedCid}\",\"rev\":\"3jzfcijpj2z2a\"}}}}";

        MediaLinkDeleteOutcome outcome = await _service.DeleteMediaLinkResultAsync(
            RecordUri, ExpectedCid, CancellationToken.None );

        Assert.AreEqual( MediaLinkDeleteOutcome.Deleted, outcome );
        Assert.AreEqual( "/xrpc/com.atproto.repo.deleteRecord", _handler.LastRequestUri?.AbsolutePath );
        using JsonDocument body = JsonDocument.Parse( _handler.LastRequestBody! );
        Assert.AreEqual( TestDid, body.RootElement.GetProperty( "repo" ).GetString( ) );
        Assert.AreEqual( "link.bridgebeats.lookup", body.RootElement.GetProperty( "collection" ).GetString( ) );
        Assert.AreEqual( "track:USRC12345678", body.RootElement.GetProperty( "rkey" ).GetString( ) );
        Assert.AreEqual( ExpectedCid, body.RootElement.GetProperty( "swapRecord" ).GetString( ) );
    }

    /// <summary>A successful delete evicts both the CAR snapshot and its record-CID entry.</summary>
    [TestMethod]
    public async Task Delete_MatchingRevision_InvalidatesCarAndCidCaches( ) {
        FieldInfo carRecordsField = typeof( ATProtoStorageService ).GetField(
            "_cachedCarRecords", BindingFlags.Instance | BindingFlags.NonPublic )!;
        FieldInfo cidField = typeof( ATProtoStorageService ).GetField(
            "_cachedRecordCids", BindingFlags.Instance | BindingFlags.NonPublic )!;
        carRecordsField.SetValue( _service, new List<(string AtUri, BridgeBeats.Contracts.DTOs.MediaLinkResult Result)> {
            (RecordUri, new BridgeBeats.Contracts.DTOs.MediaLinkResult( ))
        } );
        cidField.SetValue( _service, new Dictionary<string, string>( StringComparer.Ordinal ) {
            [RecordUri] = ExpectedCid
        } );
        _handler.ResponseStatus = HttpStatusCode.OK;
        _handler.ResponseBody = $"{{\"commit\":{{\"cid\":\"{ExpectedCid}\",\"rev\":\"3jzfcijpj2z2a\"}}}}";

        _ = await _service.DeleteMediaLinkResultAsync( RecordUri, ExpectedCid, CancellationToken.None );

        Assert.IsNull( carRecordsField.GetValue( _service ) );
        Dictionary<string, string> cachedCids = (Dictionary<string, string>)cidField.GetValue( _service )!;
        Assert.IsFalse( cachedCids.ContainsKey( RecordUri ) );
    }

    /// <summary>ATProto not-found is an idempotent terminal outcome.</summary>
    [TestMethod]
    public async Task Delete_RecordNotFound_ReturnsNotFound( ) {
        _handler.ResponseStatus = HttpStatusCode.NotFound;
        _handler.ResponseBody = "{\"error\":\"RecordNotFound\",\"message\":\"missing\"}";

        MediaLinkDeleteOutcome outcome = await _service.DeleteMediaLinkResultAsync(
            RecordUri, ExpectedCid, CancellationToken.None );

        Assert.AreEqual( MediaLinkDeleteOutcome.NotFound, outcome );
    }

    /// <summary>ATProto compare-and-swap rejection preserves the newer revision.</summary>
    [TestMethod]
    public async Task Delete_InvalidSwap_ReturnsRevisionConflict( ) {
        _handler.ResponseStatus = HttpStatusCode.BadRequest;
        _handler.ResponseBody = "{\"error\":\"InvalidSwap\",\"message\":\"changed\"}";

        MediaLinkDeleteOutcome outcome = await _service.DeleteMediaLinkResultAsync(
            RecordUri, ExpectedCid, CancellationToken.None );

        Assert.AreEqual( MediaLinkDeleteOutcome.RevisionConflict, outcome );
    }

    /// <summary>HTTP conflict is treated as a revision conflict even without a typed error body.</summary>
    [TestMethod]
    public async Task Delete_HttpConflict_ReturnsRevisionConflict( ) {
        _handler.ResponseStatus = HttpStatusCode.Conflict;
        _handler.ResponseBody = "{\"error\":\"Conflict\",\"message\":\"changed\"}";

        MediaLinkDeleteOutcome outcome = await _service.DeleteMediaLinkResultAsync(
            RecordUri, ExpectedCid, CancellationToken.None );

        Assert.AreEqual( MediaLinkDeleteOutcome.RevisionConflict, outcome );
    }

    /// <summary>Only BridgeBeats lookup records are eligible for destructive disposition.</summary>
    [TestMethod]
    public async Task Delete_WrongCollection_RejectsBeforeTransport( ) {
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>( ( ) =>
            _service.DeleteMediaLinkResultAsync(
                "at://did:plc:testuser12345/app.bsky.feed.post/abc",
                ExpectedCid,
                CancellationToken.None ) );

        Assert.IsNull( _handler.LastRequestUri );
    }

    /// <summary>The authenticated agent cannot delete a similarly-keyed record in another repo.</summary>
    [TestMethod]
    public async Task Delete_DifferentRepository_RejectsBeforeTransport( ) {
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>( ( ) =>
            _service.DeleteMediaLinkResultAsync(
                "at://did:plc:someoneelse/link.bridgebeats.lookup/track:USRC12345678",
                ExpectedCid,
                CancellationToken.None ) );

        Assert.IsNull( _handler.LastRequestUri );
    }

    /// <summary>A malformed compare-and-swap CID is rejected before any destructive request.</summary>
    [TestMethod]
    public async Task Delete_MalformedCid_RejectsBeforeTransport( ) {
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>( ( ) =>
            _service.DeleteMediaLinkResultAsync( RecordUri, "not-a-cid", CancellationToken.None ) );

        Assert.IsNull( _handler.LastRequestUri );
    }

    /// <summary>A targeted update rejects malformed AT URIs before transport.</summary>
    [TestMethod]
    public async Task StoreAtUri_MalformedUri_RejectsBeforeTransport( ) {
        _ = await Assert.ThrowsExactlyAsync<InvalidTargetRecordException>( ( ) =>
            _service.StoreMediaLinkResultAtUriAsync(
                CreateMediaLinkResult( ), "not-an-at-uri", CancellationToken.None ) );

        Assert.IsNull( _handler.LastRequestUri );
    }

    /// <summary>A targeted update cannot overwrite a record in another collection.</summary>
    [TestMethod]
    public async Task StoreAtUri_WrongCollection_RejectsBeforeTransport( ) {
        _ = await Assert.ThrowsExactlyAsync<InvalidTargetRecordException>( ( ) =>
            _service.StoreMediaLinkResultAtUriAsync(
                CreateMediaLinkResult( ),
                $"at://{TestDid}/app.bsky.feed.post/track:USRC12345678",
                CancellationToken.None ) );

        Assert.IsNull( _handler.LastRequestUri );
    }

    /// <summary>A targeted update cannot overwrite a similarly keyed record in another repository.</summary>
    [TestMethod]
    public async Task StoreAtUri_DifferentRepository_RejectsBeforeTransport( ) {
        _ = await Assert.ThrowsExactlyAsync<InvalidTargetRecordException>( ( ) =>
            _service.StoreMediaLinkResultAtUriAsync(
                CreateMediaLinkResult( ),
                "at://did:plc:someoneelse/link.bridgebeats.lookup/track:USRC12345678",
                CancellationToken.None ) );

        Assert.IsNull( _handler.LastRequestUri );
    }

    /// <summary>A valid targeted update uses the source record key rather than regenerated identity.</summary>
    [TestMethod]
    public async Task StoreAtUri_OwnedMediaRecord_UpdatesExactRecordKey( ) {
        _handler.ResponseStatus = HttpStatusCode.OK;
        _handler.ResponseBody =
            $"{{\"uri\":\"{RecordUri}\",\"cid\":\"{ExpectedCid}\",\"commit\":{{\"cid\":\"{ExpectedCid}\",\"rev\":\"3jzfcijpj2z2a\"}}}}";

        string storedUri = await _service.StoreMediaLinkResultAtUriAsync(
            CreateMediaLinkResult( ), RecordUri, CancellationToken.None );

        Assert.AreEqual( RecordUri, storedUri );
        Assert.AreEqual( "/xrpc/com.atproto.repo.putRecord", _handler.LastRequestUri?.AbsolutePath );
        using JsonDocument body = JsonDocument.Parse( _handler.LastRequestBody! );
        Assert.AreEqual( TestDid, body.RootElement.GetProperty( "repo" ).GetString( ) );
        Assert.AreEqual( "link.bridgebeats.lookup", body.RootElement.GetProperty( "collection" ).GetString( ) );
        Assert.AreEqual( "track:USRC12345678", body.RootElement.GetProperty( "rkey" ).GetString( ) );
    }

    /// <summary>A handle-form AT URI is accepted only after it resolves to the authenticated DID.</summary>
    [TestMethod]
    public async Task StoreAtUri_OwnedHandleAuthority_ResolvesAndUpdatesExactRecordKey( ) {
        _handler.ResponseFactory = request => request.RequestUri?.AbsolutePath switch {
            "/.well-known/atproto-did" => (
                HttpStatusCode.OK,
                TestDid),
            "/xrpc/com.atproto.identity.resolveHandle" => (
                HttpStatusCode.OK,
                $"{{\"did\":\"{TestDid}\"}}"),
            "/xrpc/com.atproto.repo.putRecord" => (
                HttpStatusCode.OK,
                $"{{\"uri\":\"{RecordUri}\",\"cid\":\"{ExpectedCid}\",\"commit\":{{\"cid\":\"{ExpectedCid}\",\"rev\":\"3jzfcijpj2z2a\"}}}}"),
            _ => (HttpStatusCode.NotFound, "{}")
        };

        string storedUri = await _service.StoreMediaLinkResultAtUriAsync(
            CreateMediaLinkResult( ),
            "at://test.example/link.bridgebeats.lookup/track:USRC12345678",
            CancellationToken.None );

        Assert.AreEqual( RecordUri, storedUri );
        Assert.AreEqual( "/xrpc/com.atproto.repo.putRecord", _handler.LastRequestUri?.AbsolutePath );
    }

    private static MediaLinkResult CreateMediaLinkResult( ) => new( ) {
        Results = {
            [SupportedProviders.Spotify] = new MusicLookupResult {
                ExternalId = "DIFFERENT-ID",
                Title = "Test Song",
                Artist = "Test Artist",
                URL = "https://open.spotify.com/track/test",
                IsAlbum = false
            }
        }
    };

    private static string CreateUnsignedJwt( string did ) {
        string header = Base64UrlEncode( "{\"alg\":\"none\",\"typ\":\"JWT\"}" );
        string payload = Base64UrlEncode( JsonSerializer.Serialize( new {
            sub = did,
            exp = DateTimeOffset.UtcNow.AddHours( 1 ).ToUnixTimeSeconds( )
        } ) );
        return $"{header}.{payload}.signature";
    }

    private static string Base64UrlEncode( string value ) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes( value ) )
        .TrimEnd( '=' )
        .Replace( '+', '-' )
        .Replace( '/', '_' );

    private sealed class RecordingHandler : HttpMessageHandler {
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """
            {"did":"did:plc:testuser12345","handle":"test.example","active":true}
            """;
        public Func<HttpRequestMessage, (HttpStatusCode Status, string Body)>? ResponseFactory { get; set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void Reset( ) {
            LastRequestUri = null;
            LastRequestBody = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync( cancellationToken );
            (HttpStatusCode status, string body) = ResponseFactory?.Invoke( request )
                ?? (ResponseStatus, ResponseBody);
            return new HttpResponseMessage( status ) {
                Content = new StringContent( body, Encoding.UTF8, "application/json" )
            };
        }
    }
}
