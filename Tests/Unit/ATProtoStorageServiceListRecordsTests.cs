using System.Net;
using System.Net.Http;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Core.Infrastructure.Storage.Car;
using BridgeBeats.Tests.Unit.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="ATProtoStorageService.ListAllRecordsAsync"/>, which downloads a user's entire PDS
/// repository as a CAR over the named sync HTTP client and streams the records in the lookup
/// collection as <c>(AT-URI, MediaLinkResult)</c> tuples.
/// </summary>
/// <remarks>
/// The HTTP layer is mocked to return CAR bytes built with the test helper <c>TestCarBuilder</c>. The
/// tests verify that enumeration filters to the <c>link.bridgebeats.lookup</c> collection and builds
/// each AT-URI as <c>at://{did}/{collection}/{rkey}</c>; handles an empty repository; propagates an
/// HTTP error as <see cref="HttpRequestException"/> and malformed CAR bytes as
/// <see cref="CarParseException"/>; skips an individual record that fails to deserialize while still
/// yielding the good ones; rejects an invalid DID with <see cref="ArgumentException"/> before making
/// any HTTP call; honors a cancelled token; and skips records whose rkey contains invalid characters
/// (such as a control character).
/// </remarks>
[TestClass]
public class ATProtoStorageServiceListRecordsTests {

    /// <summary>MSTest-injected context, used to flow the test's cancellation token into the service.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Mock session manager dependency of the service.</summary>
    private Mock<IATProtoSessionManager> _sessionManagerMock = null!;

    /// <summary>Mock logger injected into the service.</summary>
    private Mock<ILogger<ATProtoStorageService>> _loggerMock = null!;

    /// <summary>Mock HTTP message handler backing the sync client so CAR responses can be canned.</summary>
    private Mock<HttpMessageHandler> _httpHandlerMock = null!;

    /// <summary>Mock HTTP client factory that hands out the mocked sync client.</summary>
    private Mock<IHttpClientFactory> _httpClientFactoryMock = null!;

    /// <summary>HTTP client wrapping the mocked handler, supplied to the service.</summary>
    private HttpClient _httpClient = null!;

    /// <summary>Test DID whose repository is enumerated.</summary>
    private const string TestDid = "did:plc:testuser12345";

    /// <summary>Test PDS URI the CAR is downloaded from.</summary>
    private static readonly Uri s_testPdsUri = new( "https://pds.test.example" );

    /// <summary>
    /// Constructs the mocks and HTTP client before each test, wiring the client factory to return the
    /// mocked sync client under the service's well-known client name.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _sessionManagerMock = new Mock<IATProtoSessionManager>( );
        _loggerMock = new Mock<ILogger<ATProtoStorageService>>( );
        _ = _loggerMock.Setup( x => x.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

        _httpHandlerMock = new Mock<HttpMessageHandler>( );
        _httpClient = new HttpClient( _httpHandlerMock.Object );

        _httpClientFactoryMock = new Mock<IHttpClientFactory>( );
        _ = _httpClientFactoryMock
            .Setup( f => f.CreateClient( ATProtoStorageService.ATProtoSyncHttpClientName ) )
            .Returns( _httpClient );
    }

    /// <summary>Disposes the HTTP client created for the test.</summary>
    [TestCleanup]
    public void Cleanup( ) {
        _httpClient?.Dispose( );
    }

    // ─── Happy-path tests ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a repository holding three lookup records and one playlist record yields exactly
    /// the three lookup records, each with an AT-URI under
    /// <c>at://{did}/link.bridgebeats.lookup/</c>, and that the repo is fetched with a single HTTP call.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_ThreeLookupAndOnePlaylistRecord_ReturnsThreeTuplesWithCorrectAtUris( ) {
        // Arrange: 3 lookup + 1 playlist record in the CAR.
        // The playlist record must be filtered out by the service (collection = link.bridgebeats.lookup only).
        BridgeBeats.Contracts.Records.MediaLinkResultRecord r1 = MakeRecord( "Track 1" );
        BridgeBeats.Contracts.Records.MediaLinkResultRecord r2 = MakeRecord( "Track 2" );
        BridgeBeats.Contracts.Records.MediaLinkResultRecord r3 = MakeRecord( "Track 3" );
        BridgeBeats.Contracts.Records.MediaLinkResultRecord playlist = MakeRecord( "My Playlist" );

        // Build the CAR manually to include a record in the playlist collection alongside lookups
        byte[] rec1Bytes = TestCarBuilder.BuildRecordBlock( r1 );
        byte[] rec2Bytes = TestCarBuilder.BuildRecordBlock( r2 );
        byte[] rec3Bytes = TestCarBuilder.BuildRecordBlock( r3 );
        byte[] playlistBytes = TestCarBuilder.BuildRecordBlock( playlist );

        byte[] cid1 = TestCarBuilder.ComputeCidBytes( rec1Bytes );
        byte[] cid2 = TestCarBuilder.ComputeCidBytes( rec2Bytes );
        byte[] cid3 = TestCarBuilder.ComputeCidBytes( rec3Bytes );
        byte[] plCid = TestCarBuilder.ComputeCidBytes( playlistBytes );

        // Keys sorted: link.bridgebeats.lookup/rkey1 < link.bridgebeats.lookup/rkey2 <
        //              link.bridgebeats.lookup/rkey3 < link.bridgebeats.playlist/pl1
        // Apply prefix compression with p=0 for the first, share prefix for subsequent
        string k1 = "link.bridgebeats.lookup/rkey1";
        string k2 = "link.bridgebeats.lookup/rkey2";
        string k3 = "link.bridgebeats.lookup/rkey3";
        string kpl = "link.bridgebeats.playlist/pl1";

        byte[] mstNodeBytes = TestCarBuilder.BuildMstNode(
            null,
            [
                new TestCarBuilder.MstEntry( 0, k1, cid1 ),
                new TestCarBuilder.MstEntry( CommonPrefix( k1, k2 ), k2[CommonPrefix( k1, k2 )..], cid2 ),
                new TestCarBuilder.MstEntry( CommonPrefix( k2, k3 ), k3[CommonPrefix( k2, k3 )..], cid3 ),
                new TestCarBuilder.MstEntry( CommonPrefix( k3, kpl ), kpl[CommonPrefix( k3, kpl )..], plCid )
            ]
        );
        byte[] mstCid = TestCarBuilder.ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = TestCarBuilder.BuildCommit( TestDid, mstCid );
        byte[] commitCid = TestCarBuilder.ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayKeyComparerLocal.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            [cid1] = rec1Bytes,
            [cid2] = rec2Bytes,
            [cid3] = rec3Bytes,
            [plCid] = playlistBytes
        };
        byte[] carBytes = TestCarBuilder.BuildCar( commitCid, blocks );

        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( );

        // Act
        List<(string AtUri, MediaLinkResult Result)> results = [];
        await foreach ((string atUri, MediaLinkResult result) in
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) {
            results.Add( (atUri, result) );
        }

        // Assert: exactly 3 tuples with correct AT-URI format; playlist filtered out
        Assert.HasCount( 3, results );
        Assert.IsTrue( results.All( r => r.AtUri.StartsWith( $"at://{TestDid}/link.bridgebeats.lookup/", StringComparison.Ordinal ) ) );
        string? firstCid = await service.GetMediaLinkRecordCidAsync(
            $"at://{TestDid}/link.bridgebeats.lookup/rkey1",
            TestContext.CancellationToken );
        Assert.AreEqual( new idunno.AtProto.Cid( cid1 ).ToString( ), firstCid );

        // Verify exactly one HTTP request was made
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync",
            Times.Once( ),
            ItExpr.IsAny<HttpRequestMessage>( ),
            ItExpr.IsAny<CancellationToken>( )
        );
    }

    /// <summary>
    /// Computes the length of the shared leading prefix of two keys, used to build MST
    /// prefix-compressed entries in the fixtures.
    /// </summary>
    /// <param name="a">The first key.</param>
    /// <param name="b">The second key.</param>
    /// <returns>The number of leading characters the two keys share.</returns>
    private static int CommonPrefix( string a, string b ) {
        int len = Math.Min( a.Length, b.Length );
        int i = 0;
        while (i < len && a[i] == b[i]) { i++; }
        return i;
    }

    /// <summary>Verifies that an empty repository yields no records (still fetched with one HTTP call).</summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_EmptyRepo_YieldsNoResults( ) {
        // Arrange
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords( TestDid, "link.bridgebeats.lookup", [] );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( );

        // Act
        List<(string AtUri, MediaLinkResult Result)> results = [];
        await foreach ((string atUri, MediaLinkResult result) in
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) {
            results.Add( (atUri, result) );
        }

        // Assert
        Assert.IsEmpty( results );

        _httpHandlerMock.Protected( ).Verify(
            "SendAsync",
            Times.Once( ),
            ItExpr.IsAny<HttpRequestMessage>( ),
            ItExpr.IsAny<CancellationToken>( )
        );
    }

    // ─── Error propagation tests ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that an HTTP 500 response from the repo fetch surfaces as
    /// <see cref="HttpRequestException"/> when the stream is enumerated.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_Http500Response_ThrowsHttpRequestException( ) {
        // Arrange
        SetupHttpError( HttpStatusCode.InternalServerError );
        ATProtoStorageService service = CreateService( );

        // Act & Assert: download failure must throw (no fallback, no silent empty yield)
        _ = await Assert.ThrowsExactlyAsync<HttpRequestException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) {
                // Enumerate to trigger the download
            }
        } );
    }

    /// <summary>
    /// Verifies that a response body of garbage bytes (not a valid CAR) surfaces as
    /// <see cref="CarParseException"/> after exhausting all 3 retry attempts. Each attempt
    /// receives fresh CAR bytes so the retry loop can complete without reusing a disposed response.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_MalformedCarBytes_ThrowsCarParseException( ) {
        // Arrange: return garbage bytes that won't parse as a valid CAR — 3 copies for 3 attempts.
        byte[] garbage = [0xFF, 0xFE, 0xFD, 0x00, 0x01, 0x02];
        SetupHttpSequence( garbage, garbage, garbage );
        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        // Act & Assert: all 3 attempts fail; CarParseException propagates.
        _ = await Assert.ThrowsExactlyAsync<CarParseException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) { }
        } );

        // Verify exactly 3 HTTP requests were made (one per attempt).
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync",
            Times.Exactly( 3 ),
            ItExpr.IsAny<HttpRequestMessage>( ),
            ItExpr.IsAny<CancellationToken>( )
        );
    }

    /// <summary>
    /// Verifies that a record block that cannot be deserialized into a result is skipped while the
    /// well-formed record in the same repository is still yielded, so one bad record does not abort the
    /// enumeration.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_OneBadRecordAmongGood_SkipsBadAndYieldsRest( ) {
        // Arrange: build a CAR with one good record and one block that is valid DAG-CBOR
        // but fails MediaLinkResultRecord deserialization (lookedUpAt is an integer, not an ISO 8601 string).
        BridgeBeats.Contracts.Records.MediaLinkResultRecord goodRecord = MakeRecord( "Good Track" );
        byte[] goodBytes = TestCarBuilder.BuildRecordBlock( goodRecord );
        byte[] goodCid = TestCarBuilder.ComputeCidBytes( goodBytes );

        // Build a malformed DAG-CBOR block: lookedUpAt is a number, not a string — valid CBOR, invalid record
        byte[] badBytes = BuildMalformedRecordBlock( );
        byte[] badCid = TestCarBuilder.ComputeCidBytes( badBytes );

        // Keys sorted lexicographically: "link.bridgebeats.lookup/bad_rkey_alpha" < "link.bridgebeats.lookup/good_rkey_beta"
        // They share the 24-char prefix "link.bridgebeats.lookup/"
        byte[] mstNodeBytes = TestCarBuilder.BuildMstNode(
            null,
            [
                new TestCarBuilder.MstEntry( 0, "link.bridgebeats.lookup/bad_rkey_alpha", badCid ),
                new TestCarBuilder.MstEntry( 24, "good_rkey_beta", goodCid )
            ]
        );
        byte[] mstCid = TestCarBuilder.ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = TestCarBuilder.BuildCommit( TestDid, mstCid );
        byte[] commitCid = TestCarBuilder.ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayKeyComparerLocal.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            [goodCid] = goodBytes,
            [badCid] = badBytes
        };
        byte[] carBytes = TestCarBuilder.BuildCar( commitCid, blocks );

        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( );

        // Act
        List<(string AtUri, MediaLinkResult Result)> results = [];
        await foreach ((string atUri, MediaLinkResult result) in
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) {
            results.Add( (atUri, result) );
        }

        // Assert: only the good record returned; bad record skipped via LogCarRecordSkipped path
        Assert.HasCount( 1, results );
        Assert.IsTrue( results[0].AtUri.Contains( "good_rkey_beta", StringComparison.Ordinal ) );
    }

    /// <summary>
    /// Builds a CBOR record block whose shape does not match a valid persisted lookup record (here
    /// <c>lookedUpAt</c> is an integer rather than a timestamp), used to exercise the skip-bad-record
    /// path. The block is structurally valid CBOR but fails
    /// <see cref="BridgeBeats.Contracts.Records.MediaLinkResultRecord"/> deserialization.
    /// </summary>
    /// <returns>The encoded malformed record bytes.</returns>
    private static byte[] BuildMalformedRecordBlock( ) {
        System.Formats.Cbor.CborWriter writer = new( System.Formats.Cbor.CborConformanceMode.Lax );
        writer.WriteStartMap( null );
        // lookedUpAt as integer — valid CBOR, but System.Text.Json cannot parse it as DateTimeOffset
        writer.WriteTextString( "lookedUpAt" );
        writer.WriteInt32( 9999999 );
        // results array — empty but present
        writer.WriteTextString( "results" );
        writer.WriteStartArray( null );
        writer.WriteEndArray( );
        writer.WriteEndMap( );
        return writer.Encode( );
    }

    /// <summary>
    /// Equality comparer that treats byte arrays as keys by their contents, so a CID's raw bytes can key
    /// the in-memory block dictionary used to assemble a CAR fixture.
    /// </summary>
    private sealed class ByteArrayKeyComparerLocal : System.Collections.Generic.IEqualityComparer<byte[]> {
        /// <summary>Shared singleton instance.</summary>
        internal static readonly ByteArrayKeyComparerLocal Instance = new( );

        /// <summary>Compares two byte arrays for content equality.</summary>
        /// <param name="x">The first array.</param>
        /// <param name="y">The second array.</param>
        /// <returns><see langword="true"/> when both are non-null and have identical contents.</returns>
        public bool Equals( byte[]? x, byte[]? y ) => x is not null && y is not null && x.AsSpan( ).SequenceEqual( y );

        /// <summary>Computes a content-based hash code for a byte array.</summary>
        /// <param name="obj">The array to hash.</param>
        /// <returns>A hash code derived from the array contents.</returns>
        public int GetHashCode( byte[] obj ) {
            System.HashCode hc = new( );
            hc.AddBytes( obj );
            return hc.ToHashCode( );
        }
    }

    /// <summary>
    /// Verifies that a malformed DID is rejected with <see cref="ArgumentException"/> before any HTTP
    /// request is made, confirming input validation happens up front.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_InvalidDid_ThrowsArgumentException( ) {
        // Arrange: invalid DID format
        ATProtoStorageService service = CreateService( );

        // Act & Assert: should throw before making any HTTP call
        _ = await Assert.ThrowsExactlyAsync<ArgumentException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, "not-a-valid-did", TestContext.CancellationToken )) { }
        } );

        // No HTTP call should have been made
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync",
            Times.Never( ),
            ItExpr.IsAny<HttpRequestMessage>( ),
            ItExpr.IsAny<CancellationToken>( )
        );
    }

    // ─── CancellationToken propagation ───────────────────────────────────────

    /// <summary>
    /// Verifies that enumerating with an already-cancelled token propagates
    /// <see cref="OperationCanceledException"/> (and not a <see cref="CarParseException"/> wrapping the
    /// cancellation), confirming cancellation is honored rather than swallowed by the parse catch.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_CancelledToken_PropagatesOperationCanceledException( ) {
        // Arrange: build a valid CAR so the parse path is exercised, then cancel before iteration completes.
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid,
            "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "Track 1" ))]
        );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( );

        using CancellationTokenSource cts = new( );
        cts.Cancel( ); // pre-cancelled

        // Act & Assert
        _ = await Assert.ThrowsExactlyAsync<OperationCanceledException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, cts.Token )) { }
        } );
    }

    // ─── SEC-005 regression: rkey validation ─────────────────────────────────

    /// <summary>
    /// Verifies that a record whose rkey contains an invalid character (a control character) is skipped
    /// while a record with a valid rkey is still yielded, confirming rkey validation filters bad keys
    /// without failing the whole enumeration. Without rkey validation, the invalid rkey would reach the
    /// AT-URI builder (constructing a malformed URI) and be logged verbatim (a log-injection risk).
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_RkeyWithControlChars_SkipsInvalidRkeyAndYieldsGoodRecord( ) {
        // Arrange: build a single CAR containing one good rkey and one rkey containing a control char (0x01, invalid).
        byte[] recGoodBytes = TestCarBuilder.BuildRecordBlock( MakeRecord( "Good" ) );
        byte[] recBadBytes = TestCarBuilder.BuildRecordBlock( MakeRecord( "Bad" ) );
        byte[] cidGood = TestCarBuilder.ComputeCidBytes( recGoodBytes );
        byte[] cidBad = TestCarBuilder.ComputeCidBytes( recBadBytes );

        // Keys must be sorted. "link.bridgebeats.lookup/good-rkey" < "link.bridgebeats.lookup/\x01bad"
        // is not guaranteed, so use unique keys with known sort order.
        string keyGood = "link.bridgebeats.lookup/good-rkey";
        string keyBad = "link.bridgebeats.lookup/bad\x01rkey"; // contains control char

        // Sort entries lexicographically
        (string, byte[])[] sorted = [(keyGood, cidGood), (keyBad, cidBad)];
        System.Array.Sort( sorted, ( a, b ) => string.Compare( a.Item1, b.Item1, StringComparison.Ordinal ) );

        List<TestCarBuilder.MstEntry> mstEntries = [];
        string prev = "";
        foreach ((string k, byte[] c) in sorted) {
            int p = CommonPrefix( prev, k );
            mstEntries.Add( new TestCarBuilder.MstEntry( p, k[p..], c ) );
            prev = k;
        }

        byte[] mstNodeBytes = TestCarBuilder.BuildMstNode( null, mstEntries );
        byte[] mstCid = TestCarBuilder.ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = TestCarBuilder.BuildCommit( TestDid, mstCid );
        byte[] commitCid = TestCarBuilder.ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayKeyComparerLocal.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            [cidGood] = recGoodBytes,
            [cidBad] = recBadBytes
        };
        byte[] carBytes = TestCarBuilder.BuildCar( commitCid, blocks );

        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( );

        // Act
        List<(string AtUri, MediaLinkResult Result)> results = [];
        await foreach ((string atUri, MediaLinkResult result) in
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) {
            results.Add( (atUri, result) );
        }

        // Assert: only the record with a valid rkey is returned; the one with control chars is skipped
        Assert.HasCount( 1, results );
        Assert.IsTrue( results[0].AtUri.Contains( "good-rkey", StringComparison.Ordinal ) );
    }

    // ─── Helper methods ───────────────────────────────────────────────────────

    /// <summary>Builds the service under test from the configured mocks.</summary>
    /// <param name="carCacheTtl">
    /// Cache TTL to use for the service under test. Pass <see cref="TimeSpan.Zero"/> (the default) to
    /// disable caching — every read is a miss — for tests that need to observe multiple downloads. Pass a
    /// large positive value for tests that verify cache reuse.
    /// </param>
    /// <returns>A new <see cref="ATProtoStorageService"/> instance.</returns>
    private ATProtoStorageService CreateService( TimeSpan carCacheTtl = default ) =>
        new(
            _sessionManagerMock.Object,
            _loggerMock.Object,
            _httpClientFactoryMock.Object,
            carCacheTtl
        );

    /// <summary>
    /// Configures the HTTP handler to return a fresh 200 response on every call, with the supplied CAR
    /// bytes and the IPLD CAR content type. Uses a factory so retried requests each get a new (non-disposed)
    /// <see cref="HttpResponseMessage"/>.
    /// </summary>
    /// <param name="carBytes">The CAR payload to return.</param>
    private void SetupHttpSuccess( byte[] carBytes ) {
        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .Returns( ( HttpRequestMessage _, CancellationToken _ ) => Task.FromResult(
                new HttpResponseMessage( HttpStatusCode.OK ) {
                    Content = new ByteArrayContent( carBytes ) {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/vnd.ipld.car" ) }
                    }
                }
            ) );
    }

    /// <summary>
    /// Configures the HTTP handler to return a sequence of responses for multi-attempt tests (retry
    /// scenarios). Each element in <paramref name="carBytesSequence"/> is returned on the corresponding
    /// attempt; the last element is repeated for any additional attempts beyond the sequence length.
    /// </summary>
    /// <param name="carBytesSequence">CAR byte arrays to return in sequence, one per attempt.</param>
    private void SetupHttpSequence( params byte[][] carBytesSequence ) {
        var seq = _httpHandlerMock
            .Protected( )
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            );
        foreach (byte[] carBytes in carBytesSequence) {
            byte[] captured = carBytes; // capture for lambda
            _ = seq.Returns( ( ) => Task.FromResult(
                new HttpResponseMessage( HttpStatusCode.OK ) {
                    Content = new ByteArrayContent( captured ) {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/vnd.ipld.car" ) }
                    }
                }
            ) );
        }
    }

    /// <summary>Configures the HTTP handler to return a response with the supplied error status code.</summary>
    /// <param name="statusCode">The HTTP status code to return.</param>
    private void SetupHttpError( HttpStatusCode statusCode ) {
        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .ReturnsAsync( new HttpResponseMessage( statusCode ) );
    }

    /// <summary>
    /// Builds a well-formed persisted lookup record with a single Spotify provider result, used as
    /// block content in the CAR fixtures.
    /// </summary>
    /// <param name="title">The track title to embed in the record.</param>
    /// <returns>A populated <see cref="BridgeBeats.Contracts.Records.MediaLinkResultRecord"/>.</returns>
    private static BridgeBeats.Contracts.Records.MediaLinkResultRecord MakeRecord( string title ) =>
        new(
            results: [
                new BridgeBeats.Contracts.Records.ProviderResultRecord(
                    provider: "spotify",
                    artist: "Test Artist",
                    title: title,
                    url: "https://open.spotify.com/track/x",
                    marketRegion: "US",
                    externalId: "ISRC123",
                    artUrl: null,
                    isAlbum: false
                )
            ],
            lookedUpAt: new DateTimeOffset( 2024, 3, 1, 12, 0, 0, TimeSpan.Zero )
        );

    // ─── Missing-block CAR fixture helper ────────────────────────────────────

    /// <summary>
    /// Builds a CAR whose MST references a value block that is intentionally absent from the blocks
    /// dictionary. When <see cref="CarRepoReader.EnumerateCollection"/> walks the MST it will look up
    /// the missing CID and throw <see cref="CarParseException"/>, reproducing the §2.4 production failure.
    /// </summary>
    private static byte[] BuildMissingBlockCar( ) {
        BridgeBeats.Contracts.Records.MediaLinkResultRecord record = MakeRecord( "Track A" );
        byte[] recBytes = TestCarBuilder.BuildRecordBlock( record );
        byte[] recCid = TestCarBuilder.ComputeCidBytes( recBytes );

        // MST node references recCid, but we will NOT include recBytes in the blocks dictionary.
        string key = "link.bridgebeats.lookup/rkey-missing";
        byte[] mstNodeBytes = TestCarBuilder.BuildMstNode( null,
            [new TestCarBuilder.MstEntry( 0, key, recCid )] );
        byte[] mstCid = TestCarBuilder.ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = TestCarBuilder.BuildCommit( TestDid, mstCid );
        byte[] commitCid = TestCarBuilder.ComputeCidBytes( commitBytes );

        // Deliberately omit recCid/recBytes — the MST walks to it but the block is absent.
        Dictionary<byte[], byte[]> blocks = new( ByteArrayContentComparer.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            // recCid → recBytes intentionally missing
        };
        return TestCarBuilder.BuildCar( commitCid, blocks );
    }

    /// <summary>
    /// Structural equality comparer for <c>byte[]</c> keys used when building CAR fixtures inline in the
    /// test class (without access to <c>TestCarBuilder</c>'s private comparer).
    /// </summary>
    private sealed class ByteArrayContentComparer : IEqualityComparer<byte[]> {
        internal static readonly ByteArrayContentComparer Instance = new( );
        public bool Equals( byte[]? x, byte[]? y ) =>
            x is not null && y is not null && x.AsSpan( ).SequenceEqual( y );
        public int GetHashCode( byte[] obj ) {
            HashCode hc = new( );
            hc.AddBytes( obj );
            return hc.ToHashCode( );
        }
    }

    // ─── §7.1.1 Retry tests ───────────────────────────────────────────────────

    /// <summary>
    /// Item 1: a CAR with a missing block fails attempt 1, then a good CAR succeeds on attempt 2.
    /// Asserts the full record list is returned (not a partial), and exactly 2 HTTP calls were made.
    /// Failure-first evidence: with no retry (TTL=0, no fix), the service would throw on attempt 1.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_MissingBlockThenGoodCar_RecoversOnRetry( ) {
        // Arrange: attempt 1 → missing-block CAR; attempt 2 → good CAR
        byte[] badCar = BuildMissingBlockCar( );
        byte[] goodCar = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid,
            "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "Track 1" )), ("rkey2", MakeRecord( "Track 2" ))] );
        SetupHttpSequence( badCar, goodCar );
        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        // Act
        List<(string AtUri, MediaLinkResult Result)> results = [];
        await foreach ((string atUri, MediaLinkResult result) in
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) {
            results.Add( (atUri, result) );
        }

        // Assert: full 2-record result, not empty (not partial from the failed attempt)
        Assert.HasCount( 2, results );
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync",
            Times.Exactly( 2 ),
            ItExpr.IsAny<HttpRequestMessage>( ),
            ItExpr.IsAny<CancellationToken>( )
        );
    }

    /// <summary>
    /// Item 2 (positive path): a persistently bad CAR exhausts all 3 attempts and surfaces
    /// <see cref="CarParseException"/>. Verifies exactly 3 HTTP calls and that log event 1087
    /// (retry exhausted) is emitted once while 1086 (retry attempt) is emitted twice.
    /// Failure-first evidence: without the retry loop, the test would fail because the log
    /// verification would see 0 attempts instead of 3.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_PersistentlyBadCar_ExhaustsAndSurfaces( ) {
        // Arrange: 3 bad CARs so all attempts fail
        byte[] badCar = BuildMissingBlockCar( );
        SetupHttpSequence( badCar, badCar, badCar );
        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        // Act & Assert: CarParseException propagates after exhaustion
        _ = await Assert.ThrowsExactlyAsync<CarParseException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) { }
        } );

        // Verify exactly 3 HTTP requests
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync",
            Times.Exactly( 3 ),
            ItExpr.IsAny<HttpRequestMessage>( ),
            ItExpr.IsAny<CancellationToken>( )
        );

        // Verify log event 1086 (CarRetryAttempt) emitted 2 times (before attempt 2 and 3)
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.Is<EventId>( e => e.Id == BridgeBeats.Core.Infrastructure.Logging.LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarRetryAttempt ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( )
            ),
            Times.Exactly( 2 )
        );

        // Verify log event 1087 (CarRetryExhausted) emitted exactly once
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.Is<EventId>( e => e.Id == BridgeBeats.Core.Infrastructure.Logging.LogEventIds.Infrastructure.Storage.ATProtoStorageServiceCarRetryExhausted ),
                It.IsAny<It.IsAnyType>( ),
                It.IsAny<Exception>( ),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( )
            ),
            Times.Once( )
        );
    }

    // ─── §5.1 TTL dedup tests ─────────────────────────────────────────────────

    /// <summary>
    /// Item 3a: a large TTL causes two reads to share a single download (cache hit on second read).
    /// Failure-first evidence: with no cache (TTL=0), two reads produce two downloads.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_LargeTtl_TwoReadsProduceOneDownload( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "Track 1" ))] );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( TimeSpan.FromHours( 1 ) );

        // Act: two reads within the large TTL
        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        // Assert: only one download
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Once( ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    /// <summary>
    /// Item 3b: zero TTL causes two reads to each download independently — no cache reuse.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_ZeroTtl_TwoReadsProduceTwoDownloads( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "Track 1" ))] );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Exactly( 2 ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    /// <summary>
    /// Item 3c: after cache expiry the second read gets a fresh download — the expired result is
    /// not served. Two different CARs are set up; the second read (post-expiry) must reflect the
    /// second CAR's content.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_ExpiredCacheEntry_ReflectsSecondDownload( ) {
        byte[] car1 = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey-first", MakeRecord( "First" ))] );
        byte[] car2 = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey-second", MakeRecord( "Second" ))] );
        // Two responses: first read gets car1, second gets car2
        SetupHttpSequence( car1, car2 );
        ATProtoStorageService service = CreateService( TimeSpan.Zero ); // always expired

        List<(string AtUri, MediaLinkResult Result)> first = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        List<(string AtUri, MediaLinkResult Result)> second = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        // First read has rkey-first; second read has rkey-second (fresh from car2)
        Assert.IsTrue( first[0].AtUri.Contains( "rkey-first", StringComparison.Ordinal ) );
        Assert.IsTrue( second[0].AtUri.Contains( "rkey-second", StringComparison.Ordinal ) );
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Exactly( 2 ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    // ─── Item 4: both consumers exercise retry ────────────────────────────────

    /// <summary>
    /// Item 4: the retry path is reached via the public <c>ListAllRecordsAsync</c> overload (the seam
    /// both workers call). A missing-block CAR on attempt 1 followed by a good CAR on attempt 2
    /// should yield the good result, exercising the same code path both workers use.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_PublicSeam_RetryRecovery( ) {
        byte[] badCar = BuildMissingBlockCar( );
        byte[] goodCar = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "T1" ))] );
        SetupHttpSequence( badCar, goodCar );
        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        List<(string AtUri, MediaLinkResult Result)> results = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        Assert.HasCount( 1, results );
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Exactly( 2 ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    // ─── Item 5: resident = one materialized list ─────────────────────────────

    /// <summary>
    /// Item 5a: two within-TTL reads yield the <em>same</em> <see cref="MediaLinkResult"/> element
    /// references (proving the cache holds one list, not separate materializations).
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_WithinTtl_SameElementReferences( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "T1" ))] );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( TimeSpan.FromHours( 1 ) );

        List<(string AtUri, MediaLinkResult Result)> r1 = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        List<(string AtUri, MediaLinkResult Result)> r2 = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        Assert.HasCount( 1, r1 );
        Assert.HasCount( 1, r2 );
        // Same object reference — shared cached instance
        Assert.AreSame( r1[0].Result, r2[0].Result );
    }

    /// <summary>
    /// Item 5b (negative control): after cache expiry a fresh download yields different element instances.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_PostExpiry_DifferentElementReferences( ) {
        byte[] car1 = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "T1" ))] );
        byte[] car2 = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "T1" ))] );
        SetupHttpSequence( car1, car2 );
        ATProtoStorageService service = CreateService( TimeSpan.Zero ); // always expired → fresh each read

        List<(string AtUri, MediaLinkResult Result)> r1 = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        List<(string AtUri, MediaLinkResult Result)> r2 = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        // Different object references — each read materialized a new list
        Assert.AreNotSame( r1[0].Result, r2[0].Result );
    }

    /// <summary>
    /// Item 5c: the cache field type is the materialized list, not raw bytes. Verified via reflection:
    /// no field on <see cref="ATProtoStorageService"/> has type <c>byte[]</c> or <c>ReadOnlyMemory&lt;byte&gt;</c>
    /// that persists across requests.
    /// </summary>
    [TestMethod]
    public void ATProtoStorageService_CacheField_IsListNotByteArray( ) {
        System.Reflection.FieldInfo[] fields = typeof( ATProtoStorageService )
            .GetFields( System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance );

        // No instance field should hold raw CAR bytes across requests
        foreach (System.Reflection.FieldInfo field in fields) {
            Assert.AreNotEqual( typeof( byte[] ), field.FieldType,
                $"Field '{field.Name}' is byte[] — raw CAR bytes must not be cached as an instance field." );
            Assert.AreNotEqual( typeof( ReadOnlyMemory<byte> ), field.FieldType,
                $"Field '{field.Name}' is ReadOnlyMemory<byte> — raw CAR bytes must not be cached as an instance field." );
        }
    }

    // ─── Item 6: no crash on bad-export (storage-tier scope) ─────────────────

    /// <summary>
    /// Item 6: a persistently bad CAR surfaces <see cref="CarParseException"/> after 3 attempts —
    /// not an <see cref="System.Diagnostics.UnreachableException"/> and not an unobserved escape.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_PersistentBadCar_SurfacesCarParseExceptionNotUnreachable( ) {
        byte[] badCar = BuildMissingBlockCar( );
        SetupHttpSequence( badCar, badCar, badCar );
        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        Exception? thrown = null;
        try {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) { }
        } catch (Exception ex) {
            thrown = ex;
        }

        Assert.IsNotNull( thrown );
        _ = Assert.IsInstanceOfType<CarParseException>( thrown );
        Assert.IsNotInstanceOfType<System.Diagnostics.UnreachableException>( thrown );
    }

    // ─── Item 7: shared-list immutability ─────────────────────────────────────

    /// <summary>
    /// Item 7a: no public method or property on <see cref="IATProtoStorageService"/> returns the
    /// cached <c>List</c> directly.
    /// </summary>
    [TestMethod]
    public void IATProtoStorageService_NoPublicAccessorReturnsListDirectly( ) {
        System.Reflection.MethodInfo[] methods = typeof( IATProtoStorageService ).GetMethods( );
        foreach (System.Reflection.MethodInfo method in methods) {
            Type returnType = method.ReturnType;
            // Unwrap Task<T> and IAsyncEnumerable<T>
            if (returnType.IsGenericType) {
                Type genericDef = returnType.GetGenericTypeDefinition( );
                if (genericDef == typeof( Task<> ) || genericDef == typeof( System.Collections.Generic.IAsyncEnumerable<> )) {
                    returnType = returnType.GetGenericArguments( )[0];
                }
            }
            Assert.AreNotEqual(
                typeof( List<(string AtUri, MediaLinkResult Result)> ),
                returnType,
                $"Method '{method.Name}' returns the cached List directly — the list must not escape." );
        }
    }

    /// <summary>
    /// Item 7b: mutating a yielded element's <c>Messages</c> within the TTL causes subsequent
    /// reads to observe the mutation — proving the shared-reference invariant. This documents the
    /// hazard: consumers must treat yielded elements as read-only.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_MutatedElement_VisibleAcrossReadsWithinTtl( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup",
            [("rkey1", MakeRecord( "T1" ))] );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( TimeSpan.FromHours( 1 ) );

        // First read — capture the element reference
        List<(string AtUri, MediaLinkResult Result)> r1 = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        MediaLinkResult sharedElement = r1[0].Result;

        // Mutate the element (exercising the documented hazard)
        sharedElement.Messages = ["mutated-sentinel"];

        // Second read within TTL — same cached list, same element reference
        List<(string AtUri, MediaLinkResult Result)> r2 = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        Assert.AreSame( sharedElement, r2[0].Result );
        Assert.IsNotNull( r2[0].Result.Messages );
        Assert.AreEqual( "mutated-sentinel", r2[0].Result.Messages![0],
            "The mutation is visible across reads because both reads share the same MediaLinkResult instance." );
    }

    // ─── Item 8: forced refresh ───────────────────────────────────────────────

    /// <summary>
    /// Item 8a: <c>forceRefresh:true</c> bypasses a fresh TTL and triggers a download; the control
    /// call with <c>forceRefresh:false</c> reuses the cache (one download total).
    /// Failure-first evidence: without the forceRefresh parameter, forceRefresh:true would be
    /// source-incompatible and the test would not compile against the old interface.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_ForceRefresh_BypassesFreshCache( ) {
        byte[] car1 = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup", [("rkey1", MakeRecord( "T1" ))] );
        byte[] car2 = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup", [("rkey2", MakeRecord( "T2" ))] );
        SetupHttpSequence( car1, car2 );
        ATProtoStorageService service = CreateService( TimeSpan.FromHours( 1 ) );

        // First read: populates cache
        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        // Second read: forced refresh — must download again even though cache is fresh
        List<(string AtUri, MediaLinkResult Result)> forced = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken, forceRefresh: true ) );

        Assert.IsTrue( forced[0].AtUri.Contains( "rkey2", StringComparison.Ordinal ) );
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Exactly( 2 ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    /// <summary>
    /// Item 8a (negative control): <c>forceRefresh:false</c> with a fresh cache reuses the cached result.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_NoForceRefresh_ReusesFreshCache( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup", [("rkey1", MakeRecord( "T1" ))] );
        SetupHttpSuccess( carBytes );
        ATProtoStorageService service = CreateService( TimeSpan.FromHours( 1 ) );

        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        await ConsumeAsync( service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken, forceRefresh: false ) );

        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Once( ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    /// <summary>
    /// Item 8b: single-flight — two concurrent miss readers produce exactly one download; both
    /// callers receive the result. Uses a <see cref="TaskCompletionSource"/> to block the first
    /// download until the second reader arrives, then releases.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_ConcurrentMissReaders_OneDownloadBothGetResult( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup", [("rkey1", MakeRecord( "T1" ))] );

        // Block the first HTTP call until we release it
        TaskCompletionSource<bool> gate = new( );
        int callCount = 0;

        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .Returns( async ( HttpRequestMessage _, CancellationToken _ ) => {
                _ = System.Threading.Interlocked.Increment( ref callCount );
                _ = await gate.Task;
                return new HttpResponseMessage( HttpStatusCode.OK ) {
                    Content = new ByteArrayContent( carBytes ) {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/vnd.ipld.car" ) }
                    }
                };
            } );

        ATProtoStorageService service = CreateService( TimeSpan.FromHours( 1 ) );

        // Start two concurrent reads
        Task<List<(string AtUri, MediaLinkResult Result)>> t1 = CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );
        Task<List<(string AtUri, MediaLinkResult Result)>> t2 = CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        // Wait briefly for both tasks to be in flight, then release the gate
        await Task.Delay( 100, TestContext.CancellationToken );
        gate.SetResult( true );

        List<(string AtUri, MediaLinkResult Result)>[] results = await Task.WhenAll( t1, t2 );

        // Both readers get a result
        Assert.HasCount( 1, results[0] );
        Assert.HasCount( 1, results[1] );

        // Only one HTTP call was made (single-flight)
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Once( ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    // ─── Item 9: cancellation distinction ────────────────────────────────────

    /// <summary>
    /// Item 9a: caller-token cancellation during the inter-attempt backoff delay propagates
    /// <see cref="OperationCanceledException"/> promptly without making a 2nd HTTP call.
    /// The token is scheduled to fire 50 ms after the test starts. Attempt 1's HTTP call and CAR
    /// parse complete in microseconds (in-memory mock), so the token is guaranteed cancelled before
    /// <c>Task.Delay(BackoffFor(2), cancellationToken)</c> begins its ≥ 1 s backoff window. The
    /// delay throws immediately rather than sleeping — proving the token arg on that call is genuinely
    /// load-bearing. Without it (CA2016 also enforces this at compile time) the delay would sleep
    /// the full backoff, expose a 2nd HTTP call, and eventually exhaust retries with
    /// <see cref="CarParseException"/>.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_CallerTokenCancelledDuringBackoff_PropagatesAndNoSecondAttempt( ) {
        byte[] badCar = BuildMissingBlockCar( );
        using CancellationTokenSource cts = new( );

        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .Returns( ( HttpRequestMessage _, CancellationToken _ ) =>
                Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) {
                    Content = new ByteArrayContent( badCar ) {
                        Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/vnd.ipld.car" ) }
                    }
                } ) );

        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        // Schedule cancellation 50 ms from now. The mock's in-memory HTTP round-trip + CAR parse
        // complete in microseconds, so the token is signalled well before BackoffFor(2)'s ≥ 1 s
        // backoff window begins.
        cts.CancelAfter( TimeSpan.FromMilliseconds( 50 ) );

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew( );

        // Act: Task.Delay observes the already-cancelled token and throws TaskCanceledException
        // (a subclass of OperationCanceledException) immediately — not CarParseException from exhaustion.
        _ = await Assert.ThrowsAsync<OperationCanceledException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, cts.Token )) { }
        } );

        sw.Stop( );

        // Only one HTTP call was made — the token was signalled before the 2nd attempt.
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Once( ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );

        // Elapsed time must be well under BackoffFor(2)'s 1 s floor — proving the delay was
        // cancelled promptly and did not sleep through the backoff window.
        Assert.IsLessThan( TimeSpan.FromMilliseconds( 500 ), sw.Elapsed,
            $"Expected elapsed < 500 ms (backoff was cancelled); actual {sw.Elapsed.TotalMilliseconds:F0} ms." );
    }

    /// <summary>
    /// Item 9b: an HttpClient-deadline OCE (caller token NOT signalled) is retried, producing 3
    /// total attempts.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_HttpDeadlineOce_IsRetriedThreeTimes( ) {
        // Simulate an HttpClient-deadline OperationCanceledException (not caller-token)
        using CancellationTokenSource cts = new( );
        CancellationToken callerToken = cts.Token; // NOT cancelled

        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .ThrowsAsync( new TaskCanceledException( "HttpClient deadline" ) ); // deadline OCE

        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        // Act: should exhaust all 3 attempts (not cancel early)
        _ = await Assert.ThrowsExactlyAsync<TaskCanceledException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, callerToken )) { }
        } );

        // All 3 attempts were made
        _httpHandlerMock.Protected( ).Verify(
            "SendAsync", Times.Exactly( 3 ),
            ItExpr.IsAny<HttpRequestMessage>( ), ItExpr.IsAny<CancellationToken>( ) );
    }

    // ─── BackoffFor unit tests ────────────────────────────────────────────────

    /// <summary>
    /// Verifies <see cref="ATProtoStorageService.BackoffFor"/> returns values in the expected ranges
    /// over many samples. Attempt 2 → [1s, 2s]; attempt 3 → [2s, 4s].
    /// </summary>
    [TestMethod]
    public void BackoffFor_Attempt2_ReturnsInOneToTwoSeconds( ) {
        for (int i = 0; i < 200; i++) {
            TimeSpan delay = ATProtoStorageService.BackoffFor( 2 );
            // Assert.IsGreaterThanOrEqualTo(lowerBound, value): passes when value >= lowerBound
            Assert.IsGreaterThanOrEqualTo( TimeSpan.FromSeconds( 1 ), delay,
                $"BackoffFor(2) returned {delay.TotalSeconds:F3}s < 1s on iteration {i}" );
            Assert.IsLessThanOrEqualTo( TimeSpan.FromSeconds( 2 ), delay,
                $"BackoffFor(2) returned {delay.TotalSeconds:F3}s > 2s on iteration {i}" );
        }
    }

    /// <summary>
    /// Verifies <see cref="ATProtoStorageService.BackoffFor"/> for attempt 3 returns [2s, 4s].
    /// </summary>
    [TestMethod]
    public void BackoffFor_Attempt3_ReturnsInTwoToFourSeconds( ) {
        for (int i = 0; i < 200; i++) {
            TimeSpan delay = ATProtoStorageService.BackoffFor( 3 );
            // Assert.IsGreaterThanOrEqualTo(lowerBound, value): passes when value >= lowerBound
            Assert.IsGreaterThanOrEqualTo( TimeSpan.FromSeconds( 2 ), delay,
                $"BackoffFor(3) returned {delay.TotalSeconds:F3}s < 2s on iteration {i}" );
            Assert.IsLessThanOrEqualTo( TimeSpan.FromSeconds( 4 ), delay,
                $"BackoffFor(3) returned {delay.TotalSeconds:F3}s > 4s on iteration {i}" );
        }
    }

    // ─── Truncation guard tests ───────────────────────────────────────────────

    /// <summary>
    /// Truncation guard (positive): a response with <c>Content-Length</c> set higher than the
    /// delivered bytes throws <see cref="CarParseException"/> (truncated transfer). The retry loop
    /// catches it and retries, so with 3 truncated responses the service exhausts and surfaces
    /// <see cref="CarParseException"/>.
    /// Failure-first evidence: without the truncation guard, the short read is treated as a clean
    /// EOF and the code attempts to parse the partial bytes, which either throws a different
    /// exception or returns garbage — but NOT <see cref="CarParseException"/> from the guard.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_TruncatedCarWithContentLength_ThrowsCarParseException( ) {
        // Build a valid CAR, then wrap in a content that reports a larger Content-Length
        byte[] fullCarBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup", [("rkey1", MakeRecord( "T1" ))] );
        byte[] truncatedBytes = fullCarBytes[..(fullCarBytes.Length / 2)]; // half the bytes

        int callCount = 0;
        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .Returns( ( HttpRequestMessage _, CancellationToken _ ) => {
                _ = System.Threading.Interlocked.Increment( ref callCount );
                ByteArrayContent content = new( truncatedBytes );
                // Advertise the FULL length but deliver only half — triggers the truncation guard
                content.Headers.ContentLength = fullCarBytes.Length;
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/vnd.ipld.car" );
                return Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) { Content = content } );
            } );

        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        _ = await Assert.ThrowsExactlyAsync<CarParseException>( async ( ) => {
            await foreach ((string _, MediaLinkResult _) in
                service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken )) { }
        } );

        // All 3 retry attempts were made (truncation is retryable)
        Assert.AreEqual( 3, callCount );
    }

    /// <summary>
    /// Truncation guard (negative control): a response with <c>Content-Length</c> equal to delivered
    /// bytes does NOT throw — the guard does not fire on an exact-length transfer.
    /// </summary>
    [TestMethod]
    public async Task ListAllRecordsAsync_ExactContentLength_DoesNotThrow( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            TestDid, "link.bridgebeats.lookup", [("rkey1", MakeRecord( "T1" ))] );

        _ = _httpHandlerMock
            .Protected( )
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>( ),
                ItExpr.IsAny<CancellationToken>( )
            )
            .Returns( ( HttpRequestMessage _, CancellationToken _ ) => {
                ByteArrayContent content = new( carBytes );
                content.Headers.ContentLength = carBytes.Length; // exact match
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue( "application/vnd.ipld.car" );
                return Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) { Content = content } );
            } );

        ATProtoStorageService service = CreateService( TimeSpan.Zero );

        // Should NOT throw — exact Content-Length is a clean transfer
        List<(string AtUri, MediaLinkResult Result)> results = await CollectAsync(
            service.ListAllRecordsAsync( s_testPdsUri, TestDid, TestContext.CancellationToken ) );

        Assert.HasCount( 1, results );
    }

    // ─── Shared async enumerable helpers ─────────────────────────────────────

    private static async Task ConsumeAsync( IAsyncEnumerable<(string AtUri, MediaLinkResult Result)> source ) {
        await foreach ((string _, MediaLinkResult _) in source) { }
    }

    private static async Task<List<(string AtUri, MediaLinkResult Result)>> CollectAsync(
        IAsyncEnumerable<(string AtUri, MediaLinkResult Result)> source ) {
        List<(string AtUri, MediaLinkResult Result)> list = [];
        await foreach ((string atUri, MediaLinkResult result) in source) {
            list.Add( (atUri, result) );
        }
        return list;
    }
}

#pragma warning restore CS1591
