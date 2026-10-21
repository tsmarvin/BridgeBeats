using BridgeBeats.Web.Configuration;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

[TestClass]
public class BlueskyOAuthTransportTests {
    [TestMethod]
    [DataRow( "https://169.254.169.254/latest/meta-data/" )]
    [DataRow( "https://10.0.0.1/" )]
    [DataRow( "https://127.0.0.1/" )]
    [Timeout( 10_000 )]
    public async Task ProductionOAuthTransport_RejectsPrivateOrLoopbackServiceBeforeNetworkAccess( string serviceValue ) {
        ServiceCollection services = new( );
        _ = services.AddLogging( );
        AppSettings settings = new( ) {
            Domain = "bridgebeats.example",
            IdentityConnectionString = "Data Source=unused.db"
        };
        _ = services.AddBridgeBeatsBluesky( settings,
            "https://bridgebeats.example/oauth-client-metadata.json", "https://bridgebeats.example" );
        using ServiceProvider provider = services.BuildServiceProvider( );

        BlueskyAgentOptions options = provider.GetRequiredService<IOptionsMonitor<BlueskyAgentOptions>>( ).CurrentValue;
        using BlueskyAgent agent = new( options );
        OAuthClient client = agent.CreateOAuthClient( );
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>( ( ) => client.BuildOAuth2LoginUri(
            service: new Uri( "https://pds.example" ),
            authority: new Uri( serviceValue ),
            returnUri: new Uri( "https://bridgebeats.example/account/atproto-callback" ),
            "https://bridgebeats.example/oauth-client-metadata.json",
            ["atproto"],
            new Handle( "alice.example" ),
            [],
            new Dictionary<string, string>( ),
            CancellationToken.None ) );

        string messages = GetExceptionMessages( exception );
        Assert.IsTrue( messages.Contains( "unsafe", StringComparison.OrdinalIgnoreCase ),
            $"Expected the production OAuth transport to reject {serviceValue} as unsafe, but got: {messages}" );
    }

    private static string GetExceptionMessages( Exception exception ) {
        List<string> messages = [];
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            messages.Add( current.Message );
        }
        return string.Join( Environment.NewLine, messages );
    }
}
#pragma warning restore CS1591
