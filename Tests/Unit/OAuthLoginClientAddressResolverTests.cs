using System.Net;
using BridgeBeats.Web.Authentication;
using Microsoft.AspNetCore.Http;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

/// <summary>Checks that forwarded login addresses are used only for the configured proxy.</summary>
[TestClass]
public class OAuthLoginClientAddressResolverTests {
    [TestMethod]
    public async Task TrustedProxy_UsesForwardedClientAddress( ) {
        using OAuthLoginClientAddressResolver resolver = new( "192.0.2.10" );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        Assert.AreEqual( "198.51.100.5", resolver.GetPartitionKey( context ) );
    }

    [TestMethod]
    public async Task UntrustedPeer_CannotChooseRateLimitBucket( ) {
        using OAuthLoginClientAddressResolver resolver = new( "192.0.2.10" );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "203.0.113.7", "198.51.100.5" );

        Assert.AreEqual( "203.0.113.7", resolver.GetPartitionKey( context ) );
    }

    [TestMethod]
    public async Task TrustedProxy_RejectsAmbiguousForwardingChain( ) {
        using OAuthLoginClientAddressResolver resolver = new( "192.0.2.10" );
        await resolver.RefreshAsync( CancellationToken.None );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5, 203.0.113.7" );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );
    }

    [TestMethod]
    public void NoTrustedProxy_IgnoresForwardedAddress( ) {
        using OAuthLoginClientAddressResolver resolver = new( null );
        DefaultHttpContext context = NewContext( "192.0.2.10", "198.51.100.5" );

        Assert.AreEqual( "192.0.2.10", resolver.GetPartitionKey( context ) );
    }

    private static DefaultHttpContext NewContext( string peerAddress, string forwardedAddress ) {
        DefaultHttpContext context = new( );
        context.Connection.RemoteIpAddress = IPAddress.Parse( peerAddress );
        context.Request.Headers["X-Forwarded-For"] = forwardedAddress;
        return context;
    }
}
