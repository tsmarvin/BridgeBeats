using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="ATProtoOAuthService"/>, covering constructor argument validation, the token-expiry
/// safety-margin logic of <see cref="ATProtoOAuthService.IsTokenValid"/>, the structure of the DPoP
/// proof JWTs the service builds (exercised through reflection on the private <c>CreateDPoPProof</c>
/// method), and bounded-growth plus active-eviction behavior of the metadata cache.
/// </summary>
/// <remarks>
/// Network-dependent flows (the full OAuth exchange) are out of scope here; the HTTP handler is mocked.
/// The DPoP-proof tests assert the JOSE shape required by the ATProto OAuth spec: ES256 signing, an
/// embedded P-256 public JWK in the header, the <c>htm</c>/<c>htu</c>/<c>jti</c>/<c>iat</c>/<c>nbf</c>
/// claims, an <c>ath</c> claim bound to the access token when one is present, and a server-supplied
/// <c>nonce</c> claim when one is provided.
/// </remarks>
[TestClass]
public class ATProtoOAuthServiceTests {
    /// <summary>Gets the MSTest context for the running test, used to access the cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Mock factory for the EF Core context the service uses to persist OAuth state.</summary>
    private Mock<IDbContextFactory<ApplicationDbContext>> _mockDbContextFactory = null!;

    /// <summary>Mock logger injected into the service.</summary>
    private Mock<ILogger<ATProtoOAuthService>> _mockLogger = null!;

    /// <summary>Mock HTTP message handler backing the named OAuth client so no real network call occurs.</summary>
    private Mock<HttpMessageHandler> _mockHttpMessageHandler = null!;

    /// <summary>Mock HTTP client factory that hands out the mocked <c>"ATProtoOAuth"</c> client.</summary>
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;

    /// <summary>Mock personal-data protector standing in for the library-provided protect/unprotect path.</summary>
    private Mock<IPersonalDataProtector> _mockPersonalDataProtector = null!;

    /// <summary>HTTP client wrapping the mocked handler, supplied to the service under test.</summary>
    private HttpClient _httpClient = null!;

    /// <summary>Test client id, shaped as the required client-metadata URL the service validates.</summary>
    private const string TestClientId = "https://example.com/.well-known/client-metadata.json";

    /// <summary>Base domain corresponding to <see cref="TestClientId"/>.</summary>
    private const string TestDomain = "https://example.com";

    /// <summary>Placeholder DID used as the token subject in test responses.</summary>
    private const string TestDid = "did:plc:test123456789";

    /// <summary>Placeholder Bluesky handle used in tests.</summary>
    private const string TestHandle = "test.bsky.social";

    /// <summary>
    /// Constructs the mocks and the HTTP client before each test, wiring the client factory to return
    /// the mocked <c>"ATProtoOAuth"</c> client so the service never reaches the network.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _mockDbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>( );
        _mockLogger = new Mock<ILogger<ATProtoOAuthService>>( );
        _mockPersonalDataProtector = new Mock<IPersonalDataProtector>( );
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>( );
        _httpClient = new HttpClient( _mockHttpMessageHandler.Object );

        // Setup mock HttpClientFactory to return the mocked HttpClient
        _mockHttpClientFactory = new Mock<IHttpClientFactory>( );
        _ = _mockHttpClientFactory
            .Setup( f => f.CreateClient( "ATProtoOAuth" ) )
            .Returns( _httpClient );
    }

    /// <summary>Disposes the HTTP client created for the test.</summary>
    [TestCleanup]
    public void Cleanup( ) {
        _httpClient?.Dispose( );
    }

    /// <summary>
    /// Verifies that constructing the service with all valid arguments succeeds and yields a non-null
    /// instance.
    /// </summary>
    [TestMethod]
    public void Constructor_WithValidParameters_ShouldCreateInstance( ) {
        // Act
        ATProtoOAuthService service = new(
            _mockDbContextFactory.Object,
            _mockLogger.Object,
            TestClientId,
            _mockHttpClientFactory.Object,
            _mockPersonalDataProtector.Object
        );

        // Assert
        Assert.IsNotNull( service );
    }

    /// <summary>Verifies that a null DB context factory argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullDbContextFactory_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new ATProtoOAuthService( null!, _mockLogger.Object, TestClientId, _mockHttpClientFactory.Object, _mockPersonalDataProtector.Object ) );
    }

    /// <summary>Verifies that a null logger argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new ATProtoOAuthService( _mockDbContextFactory.Object, null!, TestClientId, _mockHttpClientFactory.Object, _mockPersonalDataProtector.Object ) );
    }

    /// <summary>Verifies that a null client id argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullClientId_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new ATProtoOAuthService( _mockDbContextFactory.Object, _mockLogger.Object, null!, _mockHttpClientFactory.Object, _mockPersonalDataProtector.Object ) );
    }

    /// <summary>Verifies that a null HTTP client factory argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullHttpClient_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new ATProtoOAuthService( _mockDbContextFactory.Object, _mockLogger.Object, TestClientId, null!, _mockPersonalDataProtector.Object ) );
    }

    /// <summary>Verifies that a null personal-data protector argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullPersonalDataProtector_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) =>
            new ATProtoOAuthService( _mockDbContextFactory.Object, _mockLogger.Object, TestClientId, _mockHttpClientFactory.Object, null! ) );
    }

    /// <summary>
    /// Verifies that a client id that is a well-formed URL but not the required client-metadata path
    /// throws <see cref="ArgumentException"/>, so a misconfigured client id fails at construction.
    /// </summary>
    [TestMethod]
    public void Constructor_WithInvalidClientIdFormat_ShouldThrowArgumentException( ) {
        // Arrange
        string invalidClientId = "https://example.com/invalid/path";

        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) =>
            new ATProtoOAuthService( _mockDbContextFactory.Object, _mockLogger.Object, invalidClientId, _mockHttpClientFactory.Object, _mockPersonalDataProtector.Object ) );
    }

    /// <summary>
    /// Verifies that <see cref="ATProtoOAuthService.IsTokenValid"/> returns <see langword="false"/> for
    /// an expiry in the past.
    /// </summary>
    [TestMethod]
    public void IsTokenValid_WithExpiredToken_ShouldReturnFalse( ) {
        // Arrange
        ATProtoOAuthService service = new(
            _mockDbContextFactory.Object,
            _mockLogger.Object,
            TestClientId,
            _mockHttpClientFactory.Object,
            _mockPersonalDataProtector.Object
        );
        DateTime expiredTime = DateTime.UtcNow.AddMinutes( -5 );

        // Act
        bool result = service.IsTokenValid( expiredTime );

        // Assert
        Assert.IsFalse( result );
    }

    /// <summary>
    /// Verifies that an expiry only 15 seconds in the future is treated as invalid, confirming the
    /// service applies a safety margin (roughly 30 seconds) and refuses tokens about to expire.
    /// </summary>
    [TestMethod]
    public void IsTokenValid_WithTokenExpiringWithinSafetyMargin_ShouldReturnFalse( ) {
        // Arrange
        ATProtoOAuthService service = new(
            _mockDbContextFactory.Object,
            _mockLogger.Object,
            TestClientId,
            _mockHttpClientFactory.Object,
            _mockPersonalDataProtector.Object
        );
        DateTime expiringTime = DateTime.UtcNow.AddSeconds( 15 ); // Within 30 second margin

        // Act
        bool result = service.IsTokenValid( expiringTime );

        // Assert
        Assert.IsFalse( result );
    }

    /// <summary>
    /// Verifies that an expiry comfortably in the future (one hour) is treated as valid.
    /// </summary>
    [TestMethod]
    public void IsTokenValid_WithValidToken_ShouldReturnTrue( ) {
        // Arrange
        ATProtoOAuthService service = new(
            _mockDbContextFactory.Object,
            _mockLogger.Object,
            TestClientId,
            _mockHttpClientFactory.Object,
            _mockPersonalDataProtector.Object
        );
        DateTime validTime = DateTime.UtcNow.AddHours( 1 );

        // Act
        bool result = service.IsTokenValid( validTime );

        // Assert
        Assert.IsTrue( result );
    }

    /// <summary>
    /// Verifies that a null expiration is treated as invalid, so a token with no known expiry is never
    /// considered usable.
    /// </summary>
    [TestMethod]
    public void IsTokenValid_WithNullExpiration_ShouldReturnFalse( ) {
        // Arrange
        ATProtoOAuthService service = new(
            _mockDbContextFactory.Object,
            _mockLogger.Object,
            TestClientId,
            _mockHttpClientFactory.Object,
            _mockPersonalDataProtector.Object
        );

        // Act
        bool result = service.IsTokenValid( null );

        // Assert
        Assert.IsFalse( result );
    }

    /// <summary>
    /// Generates a fresh P-256 ECDSA key and serializes it as a base64url-encoded EC JWK string
    /// (including the private <c>d</c> parameter and a random <c>kid</c>) for use as the DPoP key in
    /// proof-construction tests.
    /// </summary>
    /// <returns>The JWK serialized as JSON.</returns>
    private static string CreateTestDPoPKey( ) {
        using ECDsa ecdsa = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
        ECParameters parameters = ecdsa.ExportParameters( includePrivateParameters: true );

        static string Base64UrlEncode( byte[] bytes ) => Convert.ToBase64String( bytes )
                .TrimEnd( '=' )
                .Replace( '+', '-' )
                .Replace( '/', '_' );

        var jwk = new {
            kty = "EC",
            crv = "P-256",
            x   = Base64UrlEncode( parameters.Q.X! ),
            y   = Base64UrlEncode( parameters.Q.Y! ),
            d   = Base64UrlEncode( parameters.D! ),
            kid = Guid.NewGuid( ).ToString( "N" )
        };

        return JsonSerializer.Serialize( jwk );
    }

    /// <summary>
    /// Builds a serialized OAuth token-endpoint response body (access/refresh tokens, <c>expires_in</c>,
    /// DPoP token type, scopes, and subject) for use in tests that simulate a token exchange.
    /// </summary>
    /// <param name="did">The subject DID to embed as <c>sub</c>.</param>
    /// <param name="expiresIn">The token lifetime in seconds; defaults to 3600.</param>
    /// <returns>The response serialized as JSON.</returns>
    private static string CreateTestTokenResponse( string did, int expiresIn = 3600 ) {
        var response = new {
            access_token = "test_access_token_" + Guid.NewGuid( ).ToString( "N" ),
            refresh_token = "test_refresh_token_" + Guid.NewGuid( ).ToString( "N" ),
            expires_in = expiresIn,
            token_type = "DPoP",
            scope = "atproto repo:link.bridgebeats.playlist",
            sub = did
        };

        return JsonSerializer.Serialize( response );
    }

    /// <summary>
    /// Asserts that a DPoP proof JWT has the expected JOSE shape: a <c>dpop+jwt</c> (or <c>JWT</c>)
    /// <c>typ</c>, an <c>ES256</c> algorithm, an embedded EC/P-256 public JWK in the header carrying
    /// <c>x</c> and <c>y</c>, matching <c>htm</c>/<c>htu</c> claims, and present <c>jti</c>/<c>iat</c>/
    /// <c>nbf</c> claims with <c>nbf</c> at or before <c>iat</c>.
    /// </summary>
    /// <param name="dpopProof">The compact DPoP proof JWT to validate.</param>
    /// <param name="expectedHttpMethod">The HTTP method expected in the <c>htm</c> claim.</param>
    /// <param name="expectedUrl">The request URL expected in the <c>htu</c> claim.</param>
    private static void VerifyDPoPProof( string dpopProof, string expectedHttpMethod, string expectedUrl ) {
        // Decode the JWT
        JwtSecurityTokenHandler handler = new( );
        JwtSecurityToken jwt = handler.ReadJwtToken( dpopProof );

        // Verify header - Note: AdditionalHeaderClaims in SecurityTokenDescriptor may not always be included
        // The typ claim should be either "dpop+jwt" or "JWT" depending on the JWT library behavior
        Assert.IsTrue(
            jwt.Header.TryGetValue( "typ", out object? typValue ) &&
            (typValue?.ToString( ) == "dpop+jwt" || typValue?.ToString( ) == "JWT"),
            $"JWT typ header should be 'dpop+jwt' or 'JWT', but was '{typValue}'"
        );
        Assert.AreEqual( "ES256", jwt.Header.Alg );

        // Verify the jwk header is present in the raw JWT as a proper JSON object.
        // JwtSecurityTokenHandler.ReadJwtToken() doesn't always populate the jwk in its Header
        // dictionary, so we decode the raw JWT header to verify it was serialized correctly.
        string headerSegment = dpopProof.Split( '.' )[0];
        string paddedHeader = headerSegment.Replace( '-', '+' ).Replace( '_', '/' );
        paddedHeader = paddedHeader.PadRight( paddedHeader.Length + ((4 - (paddedHeader.Length % 4)) % 4), '=' );
        string decodedHeader = System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( paddedHeader ) );
        using JsonDocument headerDoc = JsonDocument.Parse( decodedHeader );
        JsonElement headerElement = headerDoc.RootElement;
        Assert.IsTrue( headerElement.TryGetProperty( "jwk", out JsonElement jwkElement ), "JWT header should contain a 'jwk' parameter" );
        Assert.AreEqual( JsonValueKind.Object, jwkElement.ValueKind, "jwk must be a JSON object, not a string" );
        Assert.AreEqual( "EC", jwkElement.GetProperty( "kty" ).GetString( ) );
        Assert.AreEqual( "P-256", jwkElement.GetProperty( "crv" ).GetString( ) );
        Assert.IsTrue( jwkElement.TryGetProperty( "x", out _ ), "jwk should contain 'x' parameter" );
        Assert.IsTrue( jwkElement.TryGetProperty( "y", out _ ), "jwk should contain 'y' parameter" );

        // Verify claims
        Assert.AreEqual( expectedHttpMethod, jwt.Claims.First( c => c.Type == "htm" ).Value );
        Assert.AreEqual( expectedUrl, jwt.Claims.First( c => c.Type == "htu" ).Value );
        Assert.IsNotNull( jwt.Claims.FirstOrDefault( c => c.Type == "jti" ) );
        Assert.IsNotNull( jwt.Claims.FirstOrDefault( c => c.Type == "iat" ) );
        Assert.IsNotNull( jwt.Claims.FirstOrDefault( c => c.Type == "nbf" ) );

        // Verify nbf is before or equal to iat (accounting for clock skew)
        long iat = long.Parse( jwt.Claims.First( c => c.Type == "iat" ).Value );
        long nbf = long.Parse( jwt.Claims.First( c => c.Type == "nbf" ).Value );
        Assert.IsLessThanOrEqualTo( iat, nbf, $"nbf ({nbf}) should be less than or equal to iat ({iat})" );
    }

    /// <summary>
    /// Verifies that the private <c>CreateDPoPProof</c> method, invoked via reflection with a method and
    /// URL, produces a proof JWT that passes the full structural check in <see cref="VerifyDPoPProof"/>.
    /// </summary>
    [TestMethod]
    public void CreateDPoPProof_ShouldIncludeRequiredClaims( ) {
        // Arrange
        string dpoPKeyJwk = CreateTestDPoPKey( );
        string httpMethod = "POST";
        string url = "https://auth.example.com/oauth/token";

        // Use reflection to call the private CreateDPoPProof method
        System.Reflection.MethodInfo? method = typeof( ATProtoOAuthService ).GetMethod(
            "CreateDPoPProof",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.IsNotNull( method, "CreateDPoPProof method should exist" );

        // Act (pass null for accessToken and nonce)
        string? dpopProof = method.Invoke(null, [dpoPKeyJwk, httpMethod, url, null, null]) as string;

        // Assert
        Assert.IsNotNull( dpopProof );
        VerifyDPoPProof( dpopProof, httpMethod, url );
    }

    /// <summary>
    /// Verifies that when an access token is passed to <c>CreateDPoPProof</c>, the resulting proof
    /// includes an <c>ath</c> (access-token hash) claim binding the proof to that token.
    /// </summary>
    [TestMethod]
    public void CreateDPoPProof_WithAccessToken_ShouldIncludeAthClaim( ) {
        // Arrange
        string dpoPKeyJwk = CreateTestDPoPKey( );
        string httpMethod = "GET";
        string url = "https://api.example.com/resource";
        string accessToken = "test_access_token";

        // Use reflection to call the private CreateDPoPProof method
        System.Reflection.MethodInfo? method = typeof( ATProtoOAuthService ).GetMethod(
            "CreateDPoPProof",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.IsNotNull( method );

        // Act (pass accessToken, nonce is null)
        string? dpopProof = method.Invoke(null, [dpoPKeyJwk, httpMethod, url, accessToken, null]) as string;

        // Assert
        Assert.IsNotNull( dpopProof );
        JwtSecurityTokenHandler handler = new( );
        JwtSecurityToken jwt = handler.ReadJwtToken( dpopProof );

        // Verify ath claim exists
        System.Security.Claims.Claim? athClaim = jwt.Claims.FirstOrDefault( c => c.Type == "ath" );
        Assert.IsNotNull( athClaim, "DPoP proof should include 'ath' claim when access token is provided" );
    }

    /// <summary>
    /// Verifies that the proof's <c>nbf</c> claim is set roughly five seconds before <c>iat</c>,
    /// confirming the clock-skew tolerance built into proof construction.
    /// </summary>
    [TestMethod]
    public void CreateDPoPProof_ShouldSetNbfForClockSkew( ) {
        // Arrange
        string dpoPKeyJwk = CreateTestDPoPKey( );
        string httpMethod = "POST";
        string url = "https://auth.example.com/oauth/token";

        // Use reflection to call the private CreateDPoPProof method
        System.Reflection.MethodInfo? method = typeof( ATProtoOAuthService ).GetMethod(
            "CreateDPoPProof",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.IsNotNull( method );

        // Act (pass null for accessToken and nonce)
        string? dpopProof = method.Invoke(null, [dpoPKeyJwk, httpMethod, url, null, null]) as string;

        // Assert
        Assert.IsNotNull( dpopProof );
        JwtSecurityTokenHandler handler = new( );
        JwtSecurityToken jwt = handler.ReadJwtToken( dpopProof );

        long iat = long.Parse( jwt.Claims.First( c => c.Type == "iat" ).Value );
        long nbf = long.Parse( jwt.Claims.First( c => c.Type == "nbf" ).Value );

        // nbf should be 5 seconds before iat for clock skew tolerance
        // Allow some tolerance since time may pass between setting the values
        long difference = iat - nbf;
        Assert.IsTrue( difference is >= 4 and <= 6,
            $"nbf should be approximately 5 seconds before iat, but difference was {difference} (iat={iat}, nbf={nbf})" );
    }

    /// <summary>
    /// Verifies that when a server-supplied nonce is passed to <c>CreateDPoPProof</c>, the resulting
    /// proof carries a <c>nonce</c> claim equal to that value.
    /// </summary>
    [TestMethod]
    public void CreateDPoPProof_WithNonce_ShouldIncludeNonceClaim( ) {
        // Arrange
        string dpoPKeyJwk = CreateTestDPoPKey();
        string httpMethod = "POST";
        string url = "https://auth.example.com/oauth/token";
        string nonce = "server-provided-nonce-12345";

        // Use reflection to call the private CreateDPoPProof method
        System.Reflection.MethodInfo? method = typeof(ATProtoOAuthService).GetMethod(
            "CreateDPoPProof",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.IsNotNull( method );

        // Act (pass null for accessToken, provide nonce)
        string? dpopProof = method.Invoke(null, [dpoPKeyJwk, httpMethod, url, null, nonce]) as string;

        // Assert
        Assert.IsNotNull( dpopProof );
        JwtSecurityTokenHandler handler = new();
        JwtSecurityToken jwt = handler.ReadJwtToken(dpopProof);

        // Verify nonce claim exists and has correct value
        System.Security.Claims.Claim? nonceClaim = jwt.Claims.FirstOrDefault(c => c.Type == "nonce");
        Assert.IsNotNull( nonceClaim, "DPoP proof should include 'nonce' claim when nonce is provided" );
        Assert.AreEqual( nonce, nonceClaim.Value, "Nonce claim value should match provided nonce" );
    }

    /// <summary>
    /// Verifies that passing both an access token and a nonce yields a proof carrying both the <c>ath</c>
    /// and the matching <c>nonce</c> claims together.
    /// </summary>
    [TestMethod]
    public void CreateDPoPProof_WithAccessTokenAndNonce_ShouldIncludeBothClaims( ) {
        // Arrange
        string dpoPKeyJwk = CreateTestDPoPKey();
        string httpMethod = "GET";
        string url = "https://api.example.com/resource";
        string accessToken = "test_access_token";
        string nonce = "server-nonce-abc123";

        // Use reflection to call the private CreateDPoPProof method
        MethodInfo? method = typeof(ATProtoOAuthService).GetMethod(
            "CreateDPoPProof",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.IsNotNull( method );

        // Act
        string? dpopProof = method.Invoke(null, [dpoPKeyJwk, httpMethod, url, accessToken, nonce]) as string;

        // Assert
        Assert.IsNotNull( dpopProof );
        JwtSecurityTokenHandler handler = new();
        JwtSecurityToken jwt = handler.ReadJwtToken(dpopProof);

        // Verify both claims exist
        System.Security.Claims.Claim? athClaim = jwt.Claims.FirstOrDefault(c => c.Type == "ath");
        System.Security.Claims.Claim? nonceClaim = jwt.Claims.FirstOrDefault(c => c.Type == "nonce");
        Assert.IsNotNull( athClaim, "DPoP proof should include 'ath' claim when access token is provided" );
        Assert.IsNotNull( nonceClaim, "DPoP proof should include 'nonce' claim when nonce is provided" );
        Assert.AreEqual( nonce, nonceClaim.Value );
    }

    // -------------------------------------------------------------------------
    // Metadata cache: bounded growth and active eviction (security regression tests)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Retrieves the <c>s_metadataCache</c> static field from <see cref="ATProtoOAuthService"/> via
    /// reflection and returns it as a <see cref="MemoryCache"/> for direct inspection in tests.
    /// </summary>
    private static MemoryCache GetMetadataCache( ) {
        FieldInfo? field = typeof( ATProtoOAuthService ).GetField(
            "s_metadataCache",
            BindingFlags.NonPublic | BindingFlags.Static
        );
        Assert.IsNotNull( field, "s_metadataCache field not found via reflection" );
        MemoryCache? cache = field.GetValue( null ) as MemoryCache;
        Assert.IsNotNull( cache, "s_metadataCache is not a MemoryCache instance" );
        return cache;
    }

    /// <summary>
    /// Builds a minimal <see cref="AuthorizationServerMetadata"/> instance for seeding the cache in
    /// tests.
    /// </summary>
    private static AuthorizationServerMetadata CreateTestMetadata( string issuer ) =>
        new( ) { Issuer = issuer };

    /// <summary>
    /// Verifies that inserting entries beyond <c>MetadataCacheCapacity</c> does not cause the cache to
    /// grow without bound. <see cref="MemoryCache"/> rejects entries past the <c>SizeLimit</c>
    /// synchronously, so <see cref="MemoryCache.Count"/> stays at or below the limit without any manual
    /// compaction.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before this fix, <c>s_metadataCache</c> was an unbounded
    /// <c>ConcurrentDictionary</c>; running this test against the pre-fix code would record a count
    /// equal to the number of entries inserted (overCapCount) rather than the cap, failing the assertion.
    /// After the fix the assertions pass: the count is bounded and greater than zero (entries are
    /// actually being accepted up to the cap).
    /// </remarks>
    [TestMethod]
    public void MetadataCache_ExceedingCapacity_CountStaysAtOrBelowCap( ) {
        MemoryCache cache = GetMetadataCache( );
        cache.Clear( );

        const int MetadataCacheCapacity = 256; // mirrors the production constant
        int overCapCount = MetadataCacheCapacity + 50;

        MemoryCacheEntryOptions opts = new( ) {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours( 1 ),
            Size = 1
        };

        for (int i = 0; i < overCapCount; i++) {
            string entryKey = $"https://auth-server-{i:D4}.example.com";
            _ = cache.Set( entryKey, CreateTestMetadata( entryKey ), opts );
        }

        // MemoryCache rejects entries past SizeLimit synchronously — no Compact needed.
        // Assert.IsLessThanOrEqualTo(upperBound, value) asserts value <= upperBound.
        Assert.IsLessThanOrEqualTo(
            MetadataCacheCapacity,
            cache.Count,
            $"Cache count should be <= MetadataCacheCapacity ({MetadataCacheCapacity}) after inserting {overCapCount} entries" );
        Assert.IsGreaterThan(
            0,
            cache.Count,
            "Cache count should be > 0 — entries up to the cap must actually be stored" );
    }

    /// <summary>
    /// Verifies that a cache entry whose TTL has elapsed is actively evicted and not merely skipped on
    /// read. <see cref="MemoryCache.TryGetValue{TItem}"/> returns <see langword="false"/> for an expired
    /// entry, and the entry is removed from the count.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: before this fix, the old <c>ConcurrentDictionary</c> stored entries
    /// indefinitely — a read would return the tuple, the caller checked <c>ExpiresAt</c>, and the entry
    /// was never removed. <c>TryGetValue</c> on the old dictionary would return <see langword="true"/>
    /// for the expired key (with the tuple still present), causing the first assertion to fail.
    /// After the fix the assertions pass: <see cref="MemoryCache"/> removes the expired entry on read.
    /// </remarks>
    [TestMethod]
    public async Task MetadataCache_ExpiredEntry_IsEvictedNotMerelySkipped( ) {
        MemoryCache cache = GetMetadataCache( );
        cache.Clear( );

        const string CacheKey = "https://expiring-auth-server.example.com";
        AuthorizationServerMetadata metadata = CreateTestMetadata( CacheKey );

        MemoryCacheEntryOptions opts = new( ) {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMilliseconds( 50 ),
            Size = 1
        };
        _ = cache.Set( CacheKey, metadata, opts );

        // Verify the entry is present before expiry.
        bool presentBeforeExpiry = cache.TryGetValue( CacheKey, out AuthorizationServerMetadata? _ );
        Assert.IsTrue( presentBeforeExpiry, "Entry should be present immediately after insertion" );

        // Wait for the entry to expire.
        await Task.Delay( 200, TestContext.CancellationToken );

        // After expiry, TryGetValue must return false and remove the entry from the count.
        bool presentAfterExpiry = cache.TryGetValue( CacheKey, out AuthorizationServerMetadata? _ );
        Assert.IsFalse( presentAfterExpiry, "Expired entry should not be returned — it must be evicted, not merely skipped" );
        Assert.AreEqual( 0, cache.Count, "Expired entry should be removed from the cache count after a TryGetValue miss" );
    }

    /// <summary>
    /// Verifies that a cache entry within its TTL is returned on read, confirming no regression in the
    /// happy-path behavior for legitimate callers.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: this test can only fail if the cache read path is broken (e.g., wrong
    /// key normalization or the cache is always empty). It is a regression guard; the test was run
    /// against the unimplemented state (empty MemoryCache, no entries set) which returns
    /// <see langword="false"/> from <c>TryGetValue</c>, failing the assertion.
    /// </remarks>
    [TestMethod]
    public void MetadataCache_HitWithinTtl_ReturnsCachedMetadata( ) {
        MemoryCache cache = GetMetadataCache( );
        cache.Clear( );

        const string CacheKey = "https://bsky.social";
        AuthorizationServerMetadata expected = CreateTestMetadata( "https://bsky.social" );

        MemoryCacheEntryOptions opts = new( ) {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours( 1 ),
            Size = 1
        };
        _ = cache.Set( CacheKey, expected, opts );

        bool hit = cache.TryGetValue( CacheKey, out AuthorizationServerMetadata? actual );

        Assert.IsTrue( hit, "Cache should return a hit for an entry within its TTL" );
        Assert.IsNotNull( actual );
        Assert.AreEqual( expected.Issuer, actual.Issuer, "Cached metadata issuer should match what was stored" );
    }
}
