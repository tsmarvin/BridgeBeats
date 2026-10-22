using System.Security.Cryptography;
using System.Text.Json;

namespace BridgeBeats.Core.Infrastructure.Identity;

/// <summary>
/// Provides the persistent P-256 ECDSA signing key used to sign ATProto OAuth confidential-client
/// assertions. This key is shared across all sessions and is distinct from the per-session DPoP keys.
/// </summary>
/// <remarks>
/// Constructed from a JWK JSON string supplied at startup. The SDK publishes the public JWKS
/// and handles active and retained signing keys.
/// </remarks>
public sealed class ATProtoSigningKeyProvider : IDisposable {

    /// <summary>Gets the ECDSA signing key, including its private parameters, used to sign client assertions.</summary>
    public ECDsa SigningKey { get; }

    /// <summary>Gets the key identifier (<c>kid</c>) matching the published JWKS.</summary>
    public string KeyId { get; }

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ATProtoSigningKeyProvider"/> class from a JWK
    /// JSON string containing an ES256 private key.
    /// </summary>
    /// <param name="jwkJson">
    /// The signing key as a JWK JSON string. Must describe an EC key on curve P-256 and include the
    /// <c>kty</c>, <c>crv</c>, <c>x</c>, <c>y</c>, <c>d</c>, and <c>kid</c> parameters.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="jwkJson"/> is null/whitespace, is not an EC P-256 key, or is missing
    /// any required parameter.
    /// </exception>
    public ATProtoSigningKeyProvider( string jwkJson ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( jwkJson );

        using JsonDocument doc = JsonDocument.Parse( jwkJson );
        JsonElement jwk = doc.RootElement;

        // Validate required JWK parameters
        if (!jwk.TryGetProperty( "kty", out JsonElement ktyProp ) || ktyProp.GetString( ) != "EC") {
            throw new ArgumentException( "ATProto OAuth signing key JWK must have 'kty' set to 'EC'.", nameof( jwkJson ) );
        }
        if (!jwk.TryGetProperty( "crv", out JsonElement crvProp ) || crvProp.GetString( ) != "P-256") {
            throw new ArgumentException( "ATProto OAuth signing key JWK must have 'crv' set to 'P-256'.", nameof( jwkJson ) );
        }
        if (!jwk.TryGetProperty( "x", out JsonElement xProp ) || string.IsNullOrWhiteSpace( xProp.GetString( ) )) {
            throw new ArgumentException( "ATProto OAuth signing key JWK does not contain required parameter 'x'.", nameof( jwkJson ) );
        }
        if (!jwk.TryGetProperty( "y", out JsonElement yProp ) || string.IsNullOrWhiteSpace( yProp.GetString( ) )) {
            throw new ArgumentException( "ATProto OAuth signing key JWK does not contain required parameter 'y'.", nameof( jwkJson ) );
        }
        if (!jwk.TryGetProperty( "d", out JsonElement dProp ) || string.IsNullOrWhiteSpace( dProp.GetString( ) )) {
            throw new ArgumentException( "ATProto OAuth signing key JWK does not contain required private key parameter 'd'.", nameof( jwkJson ) );
        }
        if (!jwk.TryGetProperty( "kid", out JsonElement kidProp ) || string.IsNullOrWhiteSpace( kidProp.GetString( ) )) {
            throw new ArgumentException( "ATProto OAuth signing key JWK does not contain required parameter 'kid'.", nameof( jwkJson ) );
        }

        string x = xProp.GetString( )!;
        string y = yProp.GetString( )!;
        string d = dProp.GetString( )!;

        KeyId = kidProp.GetString( )!;

        // Create the ECDsa key from JWK parameters
        byte[] xBytes = Base64UrlDecode( x );
        byte[] yBytes = Base64UrlDecode( y );
        byte[] dBytes = Base64UrlDecode( d );

        SigningKey = ECDsa.Create( new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = xBytes, Y = yBytes },
            D = dBytes
        } );
    }

    /// <summary>
    /// Generates a new ES256 signing key as a JWK JSON string suitable for persisting to a secret file.
    /// Call this once during installation to create the key, then load it via the constructor.
    /// </summary>
    /// <returns>
    /// A serialized JWK for a new P-256 key with a randomly generated <c>kid</c>, including the private
    /// parameter <c>d</c> and the <c>alg</c>/<c>use</c> hints (<c>ES256</c>/<c>sig</c>). Intended for
    /// provisioning a new key out of band.
    /// </returns>
    public static string GenerateNewSigningKeyJwk( ) {
        using ECDsa ecdsa = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
        ECParameters parameters = ecdsa.ExportParameters( includePrivateParameters: true );

        var jwk = new {
            kty = "EC",
            crv = "P-256",
            x = Base64UrlEncode( parameters.Q.X! ),
            y = Base64UrlEncode( parameters.Q.Y! ),
            d = Base64UrlEncode( parameters.D! ),
            kid = Guid.NewGuid( ).ToString( "N" ),
            alg = "ES256",
            use = "sig"
        };

        return JsonSerializer.Serialize( jwk );
    }

    /// <summary>
    /// Releases the underlying <see cref="ECDsa"/> signing key.
    /// </summary>
    public void Dispose( ) {
        if (!_disposed) {
            SigningKey.Dispose( );
            _disposed = true;
        }
    }

    /// <summary>
    /// Decodes a base64url-encoded string to its raw bytes, restoring standard padding.
    /// </summary>
    /// <param name="base64Url">The base64url-encoded input.</param>
    /// <returns>The decoded bytes.</returns>
    private static byte[] Base64UrlDecode( string base64Url ) {
        string padded = base64Url.PadRight( base64Url.Length + ((4 - (base64Url.Length % 4)) % 4), '=' );
        string base64 = padded.Replace( '-', '+' ).Replace( '_', '/' );
        return Convert.FromBase64String( base64 );
    }

    /// <summary>
    /// Encodes bytes as a base64url string with padding removed.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>The base64url-encoded string.</returns>
    private static string Base64UrlEncode( byte[] bytes ) {
        return Convert.ToBase64String( bytes )
            .TrimEnd( '=' )
            .Replace( '+', '-' )
            .Replace( '/', '_' );
    }
}
