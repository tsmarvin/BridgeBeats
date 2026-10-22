using System.Security.Cryptography;
using BridgeBeats.Core.Infrastructure.Identity;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="ATProtoSigningKeyProvider"/>, which loads a P-256 ECDSA client-assertion signing
/// key from a JWK and validates it.
/// </summary>
/// <remarks>
/// The tests cover JWK validation (key type must be EC, curve P-256, with the private <c>d</c>
/// parameter and a <c>kid</c> present), key generation, signing round-trips, and idempotent
/// disposal.
/// </remarks>
[TestClass]
public class ATProtoSigningKeyProviderTests {

    /// <summary>
    /// Produces a valid signing-key JWK by minting a fresh key, giving each test a usable input without
    /// embedding any fixed key material.
    /// </summary>
    /// <returns>A freshly generated P-256 signing-key JWK as JSON.</returns>
    private static string CreateTestSigningKeyJwk( ) {
        return ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
    }

    /// <summary>
    /// Verifies that constructing the provider from a valid JWK exposes a non-null signing key, key id,
    /// and a usable public key.
    /// </summary>
    [TestMethod]
    public void Constructor_WithValidJwk_ShouldCreateInstance( ) {
        // Arrange
        string jwk = CreateTestSigningKeyJwk( );

        // Act
        using ATProtoSigningKeyProvider provider = new( jwk );

        // Assert
        Assert.IsNotNull( provider.SigningKey );
        Assert.IsNotNull( provider.KeyId );
        ECParameters publicKey = provider.SigningKey.ExportParameters( false );
        Assert.IsNotNull( publicKey.Q.X );
        Assert.IsNotNull( publicKey.Q.Y );
    }

    /// <summary>Verifies that a null JWK argument throws <see cref="ArgumentNullException"/>.</summary>
    [TestMethod]
    public void Constructor_WithNullJwk_ShouldThrow( ) {
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) => new ATProtoSigningKeyProvider( null! ) );
    }

    /// <summary>Verifies that an empty-string JWK argument throws <see cref="ArgumentException"/>.</summary>
    [TestMethod]
    public void Constructor_WithEmptyJwk_ShouldThrow( ) {
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) => new ATProtoSigningKeyProvider( "" ) );
    }

    /// <summary>
    /// Verifies that a JWK whose key type is not <c>EC</c> (here <c>RSA</c>) throws
    /// <see cref="ArgumentException"/>, since only EC keys are accepted.
    /// </summary>
    [TestMethod]
    public void Constructor_WithWrongKeyType_ShouldThrow( ) {
        string invalidJwk = """{"kty":"RSA","crv":"P-256","x":"abc","y":"def","d":"ghi","kid":"test"}""";
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) => new ATProtoSigningKeyProvider( invalidJwk ) );
    }

    /// <summary>
    /// Verifies that a JWK on a curve other than P-256 (here <c>P-384</c>) throws
    /// <see cref="ArgumentException"/>, since only the P-256 curve is accepted.
    /// </summary>
    [TestMethod]
    public void Constructor_WithWrongCurve_ShouldThrow( ) {
        string invalidJwk = """{"kty":"EC","crv":"P-384","x":"abc","y":"def","d":"ghi","kid":"test"}""";
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) => new ATProtoSigningKeyProvider( invalidJwk ) );
    }

    /// <summary>
    /// Verifies that a JWK lacking the private <c>d</c> parameter throws <see cref="ArgumentException"/>,
    /// since the provider needs a private key to sign.
    /// </summary>
    [TestMethod]
    public void Constructor_WithMissingPrivateKey_ShouldThrow( ) {
        string invalidJwk = """{"kty":"EC","crv":"P-256","x":"abc","y":"def","kid":"test"}""";
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) => new ATProtoSigningKeyProvider( invalidJwk ) );
    }

    /// <summary>
    /// Verifies that a JWK lacking a <c>kid</c> throws <see cref="ArgumentException"/>, since the key id
    /// is required to identify the key in the published JWKS.
    /// </summary>
    [TestMethod]
    public void Constructor_WithMissingKid_ShouldThrow( ) {
        string invalidJwk = """{"kty":"EC","crv":"P-256","x":"abc","y":"def","d":"ghi"}""";
        _ = Assert.ThrowsExactly<ArgumentException>( ( ) => new ATProtoSigningKeyProvider( invalidJwk ) );
    }

    /// <summary>
    /// Verifies that a JWK produced by
    /// <see cref="ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk"/> can be loaded back into a
    /// provider, exposing a non-null signing key and key id.
    /// </summary>
    [TestMethod]
    public void GenerateNewSigningKeyJwk_ShouldProduceLoadableKey( ) {
        // Act
        string jwk = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );

        // Assert - should be loadable
        using ATProtoSigningKeyProvider provider = new( jwk );
        Assert.IsNotNull( provider.SigningKey );
        Assert.IsNotNull( provider.KeyId );
    }

    /// <summary>
    /// Verifies that two calls to
    /// <see cref="ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk"/> produce different keys,
    /// confirming each generation is fresh rather than returning a fixed key.
    /// </summary>
    [TestMethod]
    public void GenerateNewSigningKeyJwk_ShouldProduceUniqueKeys( ) {
        // Act
        string jwk1 = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        string jwk2 = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );

        // Assert
        Assert.AreNotEqual( jwk1, jwk2 );
    }

    /// <summary>
    /// Verifies that the exposed <see cref="ATProtoSigningKeyProvider.SigningKey"/> can sign data and
    /// verify its own signature, confirming the loaded private key is usable end to end.
    /// </summary>
    [TestMethod]
    public void SigningKey_ShouldBeAbleToSign( ) {
        // Arrange
        string jwk = CreateTestSigningKeyJwk( );
        using ATProtoSigningKeyProvider provider = new( jwk );
        byte[] data = System.Text.Encoding.UTF8.GetBytes( "test data to sign" );

        // Act
        byte[] signature = provider.SigningKey.SignData( data, HashAlgorithmName.SHA256 );

        // Assert
        Assert.IsNotNull( signature );
        Assert.IsNotEmpty( signature );

        // Verify the signature is valid
        bool isValid = provider.SigningKey.VerifyData( data, signature, HashAlgorithmName.SHA256 );
        Assert.IsTrue( isValid );
    }

    /// <summary>
    /// Verifies that <see cref="ATProtoSigningKeyProvider.Dispose"/> can be called more than once
    /// without throwing, confirming disposal is idempotent.
    /// </summary>
    [TestMethod]
    public void Dispose_ShouldDisposeSigningKey( ) {
        // Arrange
        string jwk = CreateTestSigningKeyJwk( );
        ATProtoSigningKeyProvider provider = new( jwk );
        _ = provider.SigningKey;

        // Act
        provider.Dispose( );

        // Assert - second dispose should not throw
        provider.Dispose( );
    }
}
