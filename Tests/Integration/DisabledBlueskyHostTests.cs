using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BridgeBeats.Web.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BridgeBeats.Tests.Integration;

/// <summary>Verifies the password entry points keep their original host behavior when OAuth is off.</summary>
[TestClass]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
public class DisabledBlueskyHostTests {
    /// <summary>The alias host this test sends requests on, added to the host's allow-list below.</summary>
    private const string AliasHost = "alias.example";

    /// <summary>Requests on an alias host are served locally and the OAuth route reports its disabled state.</summary>
    [TestMethod]
    public async Task NoSigningKey_DoesNotEnforceCanonicalHostForPasswordPages( ) {
        // The host's default AllowedHosts admits only localhost and 127.0.0.1, so the alias host
        // must be allowed explicitly. The value is narrowed to the alias rather than set to "*"
        // so this test cannot mask host-filtering regressions for other host names.
        using CustomWebApplicationFactory factory = new( new Dictionary<string, string?> {
            ["AllowedHosts"] = $"localhost;127.0.0.1;{AliasHost}",
        } );
        using HttpClient client = factory.CreateClient( new WebApplicationFactoryClientOptions { AllowAutoRedirect = false } );
        Assert.IsFalse( factory.Services.GetRequiredService<AuthenticationFeatureState>( ).BlueskyEnabled );

        using HttpResponseMessage login = await client.GetAsync( $"https://{AliasHost}/account/login", TestContext.CancellationToken );
        using HttpResponseMessage register = await client.GetAsync( $"https://{AliasHost}/account/register", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, login.StatusCode );
        Assert.AreEqual( HttpStatusCode.OK, register.StatusCode );

        using HttpResponseMessage antiforgery = await client.GetAsync(
            $"https://{AliasHost}/account/antiforgery-token", TestContext.CancellationToken );
        JsonElement token = await antiforgery.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        string cookies = string.Join( "; ", antiforgery.Headers.GetValues( "Set-Cookie" ).Select( value => value.Split( ';' )[0] ) );
        using HttpRequestMessage start = new( HttpMethod.Post, $"https://{AliasHost}/account/login-atproto" ) {
            Content = JsonContent.Create( new { handle = "first.example" } )
        };
        start.Headers.Add( "Cookie", cookies );
        start.Headers.Add( "X-XSRF-TOKEN", token.GetProperty( "token" ).GetString( )! );

        using HttpResponseMessage disabled = await client.SendAsync( start, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.BadRequest, disabled.StatusCode );
        JsonElement result = await disabled.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.AreEqual( "ATProto OAuth is not configured on this server.", result.GetProperty( "message" ).GetString( ) );
    }

    /// <summary>The test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;
}
