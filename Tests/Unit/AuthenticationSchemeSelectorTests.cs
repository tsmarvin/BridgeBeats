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
            ServiceCollection services = new( );
            _ = services.AddLogging( );
            AppSettings settings = new( ) {
                ApiKeySalt = "selector-test-salt",
                InternalServiceKey = "selector-test-key",
                Domain = domainEnabled ? "bridgebeats.example" : " "
            };
            StartupExtensions.ConfigureApiKeyAuth( services, settings );
            using ServiceProvider provider = services.BuildServiceProvider( );
            PolicySchemeOptions selector = provider.GetRequiredService<IOptionsMonitor<PolicySchemeOptions>>( ).Get( "MultiScheme" );
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
}
#pragma warning restore CS1591
