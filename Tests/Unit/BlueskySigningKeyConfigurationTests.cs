using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Configuration;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
            OAuthClientSigningKey active = BlueskyAuthenticationConfiguration.LoadSigningKey( activePath );
            Assert.AreEqual( source.RootElement.GetProperty( "kid" ).GetString( ), active.KeyId );
            OAuthOptions options = new( "https://bridgebeats.example/oauth-client-metadata.json",
                new Uri( "https://bridgebeats.example/account/atproto-callback" ), ["atproto", "repo:link.bridgebeats.playlist"] ) {
                ClientSigningKey = active
            };
            options.AdditionalClientSigningKeys.Add( BlueskyAuthenticationConfiguration.LoadSigningKey( previousPath ) );
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
            Assert.IsNotNull( BlueskyAuthenticationConfiguration.LoadSigningKey( path ) );
            File.Delete( path );
            _ = Assert.ThrowsExactly<FileNotFoundException>( ( ) => BlueskyAuthenticationConfiguration.LoadSigningKey( path ) );
        } finally { File.Delete( path ); }
    }

    [TestMethod]
    public void MissingOrPlaceholderKey_FailsConfiguration( ) {
        ServiceCollection services = new( );
        AppSettings settings = new( ) { Domain = "bridgebeats.example" };
        _ = Assert.ThrowsExactly<InvalidOperationException>( ( ) => services.AddBridgeBeatsBluesky( settings,
            "https://bridgebeats.example/oauth-client-metadata.json", "https://bridgebeats.example" ) );
        string path = Path.GetTempFileName( );
        try {
            File.WriteAllText( path, "{}" );
            _ = Assert.ThrowsExactly<InvalidDataException>( ( ) => BlueskyAuthenticationConfiguration.LoadSigningKey( path ) );
        } finally { File.Delete( path ); }
    }

    /// <summary>
    /// The installers regenerate the signing key for exactly these shapes rather than accepting
    /// them. If the loader ever starts accepting one, the installers are discarding a key the
    /// application would have used.
    /// </summary>
    [TestMethod]
    public void UnusableSigningKeyShapes_AreRejectedByTheLoader( ) {
        string path = Path.GetTempFileName( );
        try {
            foreach (string unusable in new[] { "", "   \n\t\n", "{}", "{ }", "your_atproto_oauth_key_here" }) {
                File.WriteAllText( path, unusable );
                bool rejected = false;
                try {
                    _ = BlueskyAuthenticationConfiguration.LoadSigningKey( path );
                } catch (Exception) {
                    rejected = true;
                }
                Assert.IsTrue( rejected, $"Signing key content '{unusable.Replace( "\n", "\\n" )}' loaded successfully, "
                    + "but the installers treat it as unusable and replace it." );

                // Positive control: the same loop must accept a usable key, so the assertions above
                // cannot pass merely because every load attempt throws.
                File.WriteAllText( path, ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
                Assert.IsNotNull( BlueskyAuthenticationConfiguration.LoadSigningKey( path ) );
            }
        } finally { File.Delete( path ); }
    }

    [TestMethod]
    public void ProductionBlueskyConfiguration_KeepsHttpsNonLoopbackTransportRestrictionsEnabled( ) {
        string path = Path.GetTempFileName( );
        try {
            File.WriteAllText( path, ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( ) );
            ServiceCollection services = new( );
            _ = services.AddLogging( );
            AppSettings settings = new( ) {
                Domain = "bridgebeats.example",
                IdentityConnectionString = "Data Source=unused.db",
                ATProtoOAuthSigningKeyPath = path
            };
            _ = services.AddBridgeBeatsBluesky( settings,
                "https://bridgebeats.example/oauth-client-metadata.json", "https://bridgebeats.example" );
            using ServiceProvider provider = services.BuildServiceProvider( );

            OAuthOptions oauth = provider.GetRequiredService<IOptionsMonitor<BlueskyAgentOptions>>( ).CurrentValue.OAuthOptions!;
            Assert.AreEqual( "https://bridgebeats.example/account/atproto-callback", oauth.ReturnUri!.AbsoluteUri );
            Assert.IsNotNull( oauth.ClientSigningKey );
            Assert.IsFalse( oauth.AllowInsecureProtocols );
            Assert.IsFalse( oauth.AllowLoopback );
        } finally { File.Delete( path ); }
    }

}
#pragma warning restore CS1591
