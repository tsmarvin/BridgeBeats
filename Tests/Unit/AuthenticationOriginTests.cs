using BridgeBeats.Web.Configuration;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

[TestClass]
public class AuthenticationOriginTests {
    [TestMethod]
    [DataRow( "bridgebeats.example", "https://bridgebeats.example", ".bridgebeats.example" )]
    [DataRow( "bridgebeats.example.", "https://bridgebeats.example", ".bridgebeats.example" )]
    [DataRow( "https://BridgeBeats.example:443/", "https://bridgebeats.example", ".bridgebeats.example" )]
    [DataRow( "  bridgebeats.example  ", "https://bridgebeats.example", ".bridgebeats.example" )]
    [DataRow( "https://localhost", "https://localhost", null )]
    [DataRow( "localhost", "https://localhost", null )]
    [DataRow( "https://127.0.0.1", "https://127.0.0.1", null )]
    [DataRow( "https://[::1]", "https://[::1]", null )]
    public void Parse_AcceptsConfiguredHttpsOriginsAndComputesCookieDomain( string input, string origin, string? cookieDomain ) {
        AuthenticationOrigin parsed = AuthenticationOrigin.Parse( input )!;

        Assert.AreEqual( origin, parsed.Origin.GetLeftPart( UriPartial.Authority ) );
        Assert.AreEqual( cookieDomain, parsed.CookieDomain );
        Assert.AreEqual( origin, AppSettings.NormalizeDomain( input ) );
    }

    [TestMethod]
    [DataRow( "http://bridgebeats.example" )]
    [DataRow( "https://user@bridgebeats.example" )]
    [DataRow( "https://bridgebeats.example/path" )]
    [DataRow( "https://bridgebeats.example?returnUrl=/" )]
    [DataRow( "https://bridgebeats.example#fragment" )]
    [DataRow( "https://bridgebeats.example:70000" )]
    [DataRow( "https://bridgebeats.example:8443" )]
    [DataRow( "https://localhost:8443" )]
    [DataRow( "https://127.0.0.1:8443" )]
    [DataRow( "https://[::1]:8443" )]
    [DataRow( "https://bridgebeats.example:" )]
    [DataRow( "https://bridgebeats.example:abc" )]
    [DataRow( "https://bad host.example" )]
    public void Parse_RejectsValuesThatAreNotAPlainHttpsOrigin( string input ) =>
        _ = Assert.ThrowsExactly<InvalidOperationException>( ( ) => AuthenticationOrigin.Parse( input ) );

    [TestMethod]
    public void Parse_ReturnsNullForMissingDomain( ) {
        Assert.IsNull( AuthenticationOrigin.Parse( null ) );
        Assert.IsNull( AuthenticationOrigin.Parse( "  " ) );
        Assert.IsNull( AppSettings.NormalizeDomain( null ) );
    }
}
#pragma warning restore CS1591
