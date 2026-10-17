using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Configuration;
using idunno.AtProto.Authentication;
using idunno.Bluesky.AspNet.Authentication;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

[TestClass]
public class BlueskySigningKeyConfigurationTests {
    [TestMethod]
    public void ExistingJwkAndPreviousKey_ArePublishedWithoutPrivateMaterial( ) {
        string activePath = Path.GetTempFileName( );
        string previousPath = Path.GetTempFileName( );
        try {
            string originalJwk = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
            File.WriteAllText( activePath, originalJwk );
            File.WriteAllText( previousPath, ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
            using JsonDocument source = JsonDocument.Parse( originalJwk );
            OAuthClientSigningKey active = BlueskyAuthenticationConfiguration.LoadSigningKey( activePath, optional: false )!;
            Assert.AreEqual( source.RootElement.GetProperty( "kid" ).GetString( ), active.KeyId );
            OAuthOptions options = new( "https://bridgebeats.example/oauth-client-metadata.json",
                new Uri( "https://bridgebeats.example/account/atproto-callback" ), ["atproto", "repo:link.bridgebeats.playlist"] ) {
                ClientSigningKey = active
            };
            options.AdditionalClientSigningKeys.Add( BlueskyAuthenticationConfiguration.LoadSigningKey( previousPath, optional: false )! );
            string metadata = new BlueskyOAuthClientMetadataOptions( ).GenerateJson( options );
            using JsonDocument json = JsonDocument.Parse( metadata );
            Assert.AreEqual( "private_key_jwt", json.RootElement.GetProperty( "token_endpoint_auth_method" ).GetString( ) );
            JsonElement keys = json.RootElement.GetProperty( "jwks" ).GetProperty( "keys" );
            Assert.AreEqual( 2, keys.GetArrayLength( ) );
            foreach (JsonElement key in keys.EnumerateArray( )) {
                Assert.IsFalse( key.TryGetProperty( "d", out _ ) );
            }
            Assert.DoesNotContain( source.RootElement.GetProperty( "d" ).GetString( )!, metadata );
        } finally {
            File.Delete( activePath );
            File.Delete( previousPath );
        }
    }

    [TestMethod]
    public void PemKey_IsAccepted_AndMissingPreviousKeyFails( ) {
        string path = Path.GetTempFileName( );
        try {
            using ECDsa key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
            File.WriteAllText( path, key.ExportPkcs8PrivateKeyPem( ) );
            Assert.IsNotNull( BlueskyAuthenticationConfiguration.LoadSigningKey( path, optional: false ) );
            File.Delete( path );
            _ = Assert.ThrowsExactly<FileNotFoundException>( ( ) => BlueskyAuthenticationConfiguration.LoadSigningKey( path, optional: false ) );
            Assert.IsNull( BlueskyAuthenticationConfiguration.LoadSigningKey( path, optional: true ) );
        } finally { File.Delete( path ); }
    }
}
#pragma warning restore CS1591
