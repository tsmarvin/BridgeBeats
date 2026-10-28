using AspNetCore.Authentication.ApiKey;
using BridgeBeats.Web.Authentication;
using BridgeBeats.Web.Configuration;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

[TestClass]
public class AuthenticationSchemeSelectorTests {
    [TestMethod]
    public void ProductionSelector_AppliesConfiguredSchemePrecedence( ) {
        (string Scenario, bool DomainEnabled, string ExpectedScheme)[] cases = [
            ("internal", true, InternalServiceDefaults.AuthenticationScheme),
            ("api", true, ApiKeyDefaults.AuthenticationScheme),
            ("bluesky", true, BlueskyAuthenticationDefaults.AuthenticationScheme),
            ("fallback", true, IdentityConstants.ApplicationScheme),
            ("bluesky", false, IdentityConstants.ApplicationScheme)
        ];
        foreach ((string scenario, bool domainEnabled, string expectedScheme) in cases) {
            AppSettings settings = new( ) {
                ApiKeySalt = "selector-test-salt",
                InternalServiceKey = "selector-test-key",
                Domain = domainEnabled ? "bridgebeats.example" : " ",
                // The helper mirrors production enablement, which requires the signing key as well as
                // the domain. No file is read during registration, so a placeholder path is enough.
                ATProtoOAuthSigningKeyPath = domainEnabled ? "/selector-test/signing-key.json" : string.Empty
            };
            PolicySchemeOptions selector = SelectorFor( settings );
            DefaultHttpContext context = new( );
            if (scenario == "internal") {
                context.Request.Headers[InternalServiceDefaults.HeaderName] = "selector-test-key";
            }
            if (scenario is "internal" or "api") {
                context.Request.Headers["X-API-Key"] = "api-key";
            }
            string cookie = scenario switch {
                "internal" or "api" or "bluesky" when domainEnabled => $"{BlueskyAuthenticationConfiguration.CookieName}=session; .AspNetCore.Identity.Application=identity",
                "bluesky" => $"{BlueskyAuthenticationConfiguration.CookieName}=session",
                _ => ".AspNetCore.Identity.Application=identity"
            };
            context.Request.Headers["Cookie"] = cookie;

            Assert.AreEqual( expectedScheme, selector.ForwardDefaultSelector!( context ), scenario );
        }
    }

    /// <summary>
    /// A domain with no signing key leaves Bluesky disabled in the host, so the selector must not
    /// route a Bluesky cookie to a scheme that was never registered.
    /// </summary>
    [TestMethod]
    public void Selector_WithDomainButNoSigningKey_DoesNotRouteToBluesky( ) {
        PolicySchemeOptions selector = SelectorFor( new AppSettings {
            ApiKeySalt = "selector-test-salt",
            InternalServiceKey = "selector-test-key",
            Domain = "bridgebeats.example",
            ATProtoOAuthSigningKeyPath = string.Empty
        } );
        DefaultHttpContext context = new( );
        context.Request.Headers["Cookie"] = $"{BlueskyAuthenticationConfiguration.CookieName}=session";

        Assert.AreEqual( IdentityConstants.ApplicationScheme, selector.ForwardDefaultSelector!( context ) );
    }

    private static PolicySchemeOptions SelectorFor( AppSettings settings ) {
        ServiceCollection services = new( );
        _ = services.AddLogging( );
        StartupExtensions.ConfigureApiKeyAuth( services, settings );
        using ServiceProvider provider = services.BuildServiceProvider( );
        return provider.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>( ).Get( "MultiScheme" );
    }
}
#pragma warning restore CS1591
