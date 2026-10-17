using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Web.Authentication;
using BridgeBeats.Web.Configuration;
using BridgeBeats.Web.Controllers;
using idunno.AtProto;
using idunno.AtProto.Authentication;
using idunno.Bluesky;
using idunno.Bluesky.AspNet.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Moq;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Integration;

[TestClass]
public class BlueskyAuthenticationIntegrationTests {
    private static readonly string s_scheme = BlueskyAuthenticationDefaults.AuthenticationScheme;
    private const string DidValue = "did:plc:kx2mxhedzbeuqethywrzdexz";
    private string _service = null!;
    private WebApplication _authorizationServer = null!;
    private string _directory = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Mock<IDidHandleResolver> _handles = null!;
    private string _proofKey = null!;
    private readonly OAuthHandler _oauth = new( );
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task Initialize( ) {
        _directory = Path.Combine( Path.GetTempPath( ), $"bridgebeats-bluesky-{Guid.NewGuid( ):N}" );
        _ = Directory.CreateDirectory( _directory );
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        WebApplicationBuilder authorizationBuilder = WebApplication.CreateBuilder( );
        authorizationBuilder.Configuration["AllowedHosts"] = "*";
        authorizationBuilder.Logging.ClearProviders( );
        _ = authorizationBuilder.WebHost.UseUrls( "http://127.0.0.1:0" );
        _authorizationServer = authorizationBuilder.Build( );
        _authorizationServer.Run( async context => {
            using HttpMessageInvoker invoker = new( _oauth, disposeHandler: false );
            using HttpRequestMessage request = new( new HttpMethod( context.Request.Method ), _service + context.Request.Path + context.Request.QueryString );
            if (context.Request.ContentLength > 0) {
                request.Content = new StreamContent( context.Request.Body );
                _ = request.Content.Headers.TryAddWithoutValidation( "Content-Type", context.Request.ContentType );
            }
            foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in context.Request.Headers) {
                _ = request.Headers.TryAddWithoutValidation( header.Key, header.Value.ToArray( ) );
            }
            using HttpResponseMessage response = await invoker.SendAsync( request, context.RequestAborted );
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers) {
                context.Response.Headers[header.Key] = header.Value.ToArray( );
            }
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString( );
            await context.Response.WriteAsync( await response.Content.ReadAsStringAsync( context.RequestAborted ), context.RequestAborted );
        } );
        await _authorizationServer.StartAsync( TestContext.CancellationToken );
        _service = _authorizationServer.Urls.Single( ).TrimEnd( '/' );
        _oauth.Service = _service;
        _oauth.TokenFactory = ( ) => CreateCredentials( DateTime.UtcNow.AddHours( 1 ), "rotated-refresh" ).AccessJwt;
        WebApplicationBuilder builder = WebApplication.CreateBuilder( new WebApplicationOptions {
            WebRootPath = Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "../../../../src/BridgeBeats.Web/wwwroot" ) )
        } );
        builder.Configuration["AllowedHosts"] = "*";
        builder.Logging.ClearProviders( );
        _ = builder.Logging.AddConsole( );
        _ = builder.Services.PostConfigure<LoggerFilterOptions>( options => {
            options.Rules.Clear( );
            options.MinLevel = LogLevel.Warning;
        } );
        _ = builder.WebHost.UseTestServer( );
        string connection = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        _ = builder.Services.AddDbContextFactory<ApplicationDbContext>( options => options.UseSqlite( connection ) );
        _ = builder.Services.AddBridgeBeatsIdentity( Path.Combine( _directory, "keys" ) );
        _ = builder.Services.AddSingleton( new ApiKeyHasher( "test-salt" ) );
        _ = builder.Services.AddControllersWithViews( ).AddApplicationPart( typeof( AccountController ).Assembly );
        _ = builder.Services.AddAntiforgery( options => options.HeaderName = "X-XSRF-TOKEN" );
        _ = builder.Services.AddAuthentication( "TestPolicy" )
            .AddPolicyScheme( "TestPolicy", null, options => options.ForwardDefaultSelector = context =>
                context.Request.Cookies.ContainsKey( BlueskyAuthenticationConfiguration.CookieName )
                    ? s_scheme : IdentityConstants.ApplicationScheme )
            .AddIdentityCookies( );
        _ = builder.Services.AddAuthorization( );
        _handles = new Mock<IDidHandleResolver>( );
        _ = _handles.Setup( h => h.ResolveHandleAsync( It.IsAny<Did>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new Handle( "first.example" ) );
        _ = builder.Services.AddSingleton( _handles.Object );
        AppSettings settings = new( ) { IdentityConnectionString = connection, Domain = "bridgebeats.example" };
        _ = builder.Services.AddBridgeBeatsBluesky( settings,
            "https://bridgebeats.example" + BlueskyAuthenticationConfiguration.ClientMetadataPath, "https://bridgebeats.example" );
        _ = builder.Services.PostConfigure<BlueskyAgentOptions>( options => {
            options.OAuthOptions!.AllowInsecureProtocols = true;
            options.OAuthOptions.AllowLoopback = true;
        } );
        // The OAuth transport is deterministic; all application middleware, stores, DP, events and Identity run normally.
        builder.Services.RemoveAll<IHttpClientFactory>( );
        _ = builder.Services.AddSingleton<IHttpClientFactory>( new TestHttpClientFactory( _oauth ) );
        _app = builder.Build( );
        _ = _app.UseBlueskyOAuthClientMetadata( );
        _ = _app.UseStaticFiles( );
        // Metadata is generated from the production URLs above. The SDK's discovery development mode
        // additionally requires an HTTP callback when talking to the local HTTP authorization server.
        _app.Services.GetRequiredService<IOptionsMonitor<BlueskyAgentOptions>>( ).CurrentValue.OAuthOptions!.ReturnUri = new Uri( _service + "/account/atproto-callback" );
        _app.Services.GetRequiredService<IOptions<BlueskyAgentOptions>>( ).Value.OAuthOptions!.ReturnUri = new Uri( _service + "/account/atproto-callback" );
        _ = _app.UseAuthentication( );
        _ = _app.UseAuthorization( );
        _app.MapPost( "/test/signin", async ( HttpContext context ) => {
            ClaimsIdentity identity = IIdentityStore.BuildClaimsIdentity( CreateCredentials( DateTime.UtcNow.AddHours( 1 ) ) );
            await context.SignInAsync( s_scheme, new ClaimsPrincipal( identity ) );
            await context.Response.WriteAsync( "signed in", TestContext.CancellationToken );
        } );
        _app.MapGet( "/test/who", ( HttpContext context, BlueskyAgent agent ) => Results.Json( new {
            authenticated = context.User.Identity?.IsAuthenticated == true,
            userId = context.User.FindFirstValue( System.Security.Claims.ClaimTypes.NameIdentifier ),
            did = context.User.FindFirstValue( AtProtoClaims.Did ),
            agentAuthenticated = agent.IsAuthenticated
        } ) );
        _ = _app.MapControllers( );
        _ = _app.MapDefaultControllerRoute( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            await db.Database.MigrateAsync( TestContext.CancellationToken );
        }
        await _app.StartAsync( TestContext.CancellationToken );
        _client = _app.GetTestClient( );
        _client.BaseAddress = new Uri( "https://bridgebeats.example" );
    }

    [TestCleanup]
    public async Task Cleanup( ) {
        _client?.Dispose( );
        if (_app is not null) { await _app.DisposeAsync( ); }
        if (_authorizationServer is not null) { await _authorizationServer.DisposeAsync( ); }
        SqliteConnection.ClearAllPools( );
        if (_directory is not null && Directory.Exists( _directory )) { Directory.Delete( _directory, true ); }
    }

    [TestMethod]
    public async Task SignIn_MapsStableDidToLocalAccount_AndKeepsCredentialsServerSide( ) {
        HttpResponseMessage login = await SignInAsync( );
        string id = await LocalUserIdAsync( );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.AreEqual( id, who.GetProperty( "userId" ).GetString( ) );
        Assert.AreEqual( DidValue, who.GetProperty( "did" ).GetString( ) );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        BlueskyAuthenticationOptions options = Options( );
        string cookie = login.Headers.GetValues( "Set-Cookie" ).Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) );
        string value = cookie.Split( ';' )[0].Split( '=', 2 )[1];
        AuthenticationTicket ticket = options.TicketDataFormat.Unprotect( value )!;
        Assert.HasCount( 1, ticket.Principal.Claims );
        Assert.AreEqual( AtProtoClaims.Did, ticket.Principal.Claims.Single( ).Type );
        Assert.AreEqual( id, ticket.Properties.GetString( BlueskyAccountEvents.UserIdProperty ) );
        Assert.IsTrue( cookie.Contains( "secure", StringComparison.OrdinalIgnoreCase ) );
        Assert.IsTrue( cookie.Contains( "httponly", StringComparison.OrdinalIgnoreCase ) );
        Assert.IsFalse( cookie.Contains( "expires=", StringComparison.OrdinalIgnoreCase ), "Login remains a browser session cookie." );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        ApplicationUser savedUser = await db.Users.SingleAsync( TestContext.CancellationToken );
        Assert.IsNotNull( savedUser.SecurityStamp );
        Assert.AreEqual( savedUser.SecurityStamp, ticket.Properties.GetString( BlueskyAccountEvents.SecurityStampProperty ) );
        await db.Database.OpenConnectionAsync( TestContext.CancellationToken );
        using System.Data.Common.DbCommand command = db.Database.GetDbConnection( ).CreateCommand( );
        command.CommandText = "SELECT Identity FROM idunno_bluesky_identities";
        byte[] bytes = (byte[])(await command.ExecuteScalarAsync( TestContext.CancellationToken ))!;
        Assert.DoesNotContain( "initial-refresh", System.Text.Encoding.UTF8.GetString( bytes ) );
        Assert.IsNull( (await db.Users.SingleAsync( TestContext.CancellationToken )).ApiKeyHash );
    }

    [TestMethod]
    public async Task RenamedHandle_ReusesUserIdAndApiKey( ) {
        _ = await SignInAsync( );
        string id = await LocalUserIdAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = (await users.FindByIdAsync( id ))!;
            user.ApiKeyHash = "existing-api-key-hash";
            _ = await users.UpdateAsync( user );
        }
        _ = _handles.Setup( h => h.ResolveHandleAsync( It.IsAny<Did>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new Handle( "renamed.example" ) );
        _ = await SignInAsync( );
        Assert.AreEqual( id, await LocalUserIdAsync( ) );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        ApplicationDbContext db = check.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        ApplicationUser saved = await db.Users.SingleAsync( TestContext.CancellationToken );
        Assert.AreEqual( "renamed.example", saved.AtProtoHandle );
        Assert.AreEqual( "existing-api-key-hash", saved.ApiKeyHash );
    }

    [TestMethod]
    public async Task ExpiredAccessToken_IsRefreshedAndReplacementIsPersisted( ) {
        _ = await SignInAsync( );
        await Options( ).IdentityStore!.Update( CreateCredentials( DateTime.UtcNow.AddMinutes( -1 ) ), TestContext.CancellationToken );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsGreaterThan( 0, _oauth.Refreshes );
        ClaimsIdentity identity = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "rotated-refresh", identity.FindFirst( AtProtoClaims.RefreshToken )?.Value );
    }

    [TestMethod]
    public async Task Logout_RevokesAndRemovesStoredSession( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    [TestMethod]
    public async Task CallbackWithoutCorrelation_DoesNotCreateUser( ) {
        using HttpResponseMessage result = await _client.GetAsync( "/account/atproto-callback?code=forged&state=forged&iss=https://pds.example", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, result.StatusCode );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.CountAsync( TestContext.CancellationToken ) );
    }

    [TestMethod]
    [DataRow( false )]
    [DataRow( true )]
    public async Task LoginStartAndCallback_CompleteOAuthAndConsumeSqliteCorrelationState( bool existingAccount ) {
        string? existingId = null;
        if (existingAccount) {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            ApplicationUser user = new( ) { UserName = "existing-bluesky-user", AtProtoDid = DidValue, ApiKeyHash = "existing-api-key-hash" };
            Assert.IsTrue( (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).CreateAsync( user )).Succeeded );
            existingId = user.Id;
        }
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage start = await _client.PostAsJsonAsync( "/account/login-atproto",
            new { handle = " @first.example " }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, start.StatusCode, await start.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        JsonElement data = await start.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Uri authorizationUri = new( data.GetProperty( "authorizationUrl" ).GetString( )! );
        Assert.AreEqual( _service + "/authorize", authorizationUri.GetLeftPart( UriPartial.Path ) );
        Assert.AreEqual( 1, _oauth.PushedAuthorizations );
        Assert.AreEqual( "first.example", _oauth.LoginHint );
        Assert.AreEqual( 1L, await CorrelationStateCountAsync( ) );
        string correlationCookie = start.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( Options( ).CorrelationCookie.Name + ".", StringComparison.Ordinal ) ).Split( ';' )[0];
        string requestCookies = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( ) + "; " + correlationCookie;
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", requestCookies );

        using HttpClient browser = new( new HttpClientHandler { AllowAutoRedirect = false } );
        using HttpResponseMessage authorization = await browser.GetAsync( authorizationUri, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, authorization.StatusCode );
        // Route the loopback provider's callback through the real application controller in TestServer.
        string callback = authorization.Headers.Location!.PathAndQuery;
        using HttpResponseMessage result = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, result.StatusCode );
        Assert.AreEqual( "/", result.Headers.Location?.ToString( ) );
        Assert.AreEqual( 1, _oauth.CodeExchanges, "The provider must validate the single-use code and PKCE verifier." );
        Assert.AreEqual( 0L, await CorrelationStateCountAsync( ) );
        string cookie = result.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        string id = await LocalUserIdAsync( );
        if (existingAccount) {
            Assert.AreEqual( existingId, id );
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            ApplicationUser saved = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).FindByIdAsync( id ))!;
            Assert.AreEqual( "existing-api-key-hash", saved.ApiKeyHash );
        }
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( id, who.GetProperty( "userId" ).GetString( ) );
        Assert.AreEqual( DidValue, who.GetProperty( "did" ).GetString( ) );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        // Even a saved copy of the original correlation cookie cannot reuse consumed login state.
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", requestCookies );
        using HttpResponseMessage replay = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreNotEqual( "/", replay.Headers.Location?.ToString( ) );
        Assert.AreEqual( 1, _oauth.CodeExchanges );
        Assert.AreEqual( id, await LocalUserIdAsync( ) );
    }

    [TestMethod]
    public async Task Metadata_DeclaresIdentityAndOnlyPlaylistPermissions( ) {
        JsonElement metadata = await _client.GetFromJsonAsync<JsonElement>( "/oauth-client-metadata.json", TestContext.CancellationToken );
        Assert.AreEqual( "https://bridgebeats.example/oauth-client-metadata.json", metadata.GetProperty( "client_id" ).GetString( ) );
        Assert.AreEqual( "BridgeBeats", metadata.GetProperty( "client_name" ).GetString( ) );
        Assert.AreEqual( "https://bridgebeats.example", metadata.GetProperty( "client_uri" ).GetString( ) );
        Assert.AreEqual( "atproto repo:link.bridgebeats.playlist", metadata.GetProperty( "scope" ).GetString( ) );
        Assert.AreEqual( "none", metadata.GetProperty( "token_endpoint_auth_method" ).GetString( ) );
        Assert.AreEqual( "https://bridgebeats.example/account/atproto-callback", metadata.GetProperty( "redirect_uris" )[0].GetString( ) );
        using HttpResponseMessage head = await _client.SendAsync( new HttpRequestMessage( HttpMethod.Head, "/oauth-client-metadata.json" ), TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, head.StatusCode );
    }

    [TestMethod]
    public async Task Metadata_AdvertisesReachableLogoAndDistinctLegalPages( ) {
        JsonElement metadata = await _client.GetFromJsonAsync<JsonElement>( "/oauth-client-metadata.json", TestContext.CancellationToken );
        Uri logo = new( metadata.GetProperty( "logo_uri" ).GetString( )! );
        Assert.AreEqual( "https://bridgebeats.example/public/Icons/BridgeBeats_FullColor.svg", logo.AbsoluteUri );
        using HttpResponseMessage image = await _client.GetAsync( logo.PathAndQuery, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, image.StatusCode );
        Assert.AreEqual( "image/svg+xml", image.Content.Headers.ContentType?.MediaType );
        Assert.Contains( "<svg", await image.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Uri terms = new( metadata.GetProperty( "tos_uri" ).GetString( )! );
        Uri privacy = new( metadata.GetProperty( "policy_uri" ).GetString( )! );
        Assert.AreEqual( "/Home/Tos", terms.AbsolutePath );
        Assert.AreEqual( "/Home/Privacy", privacy.AbsolutePath );
        using HttpResponseMessage termsPage = await _client.GetAsync( terms.PathAndQuery, TestContext.CancellationToken );
        using HttpResponseMessage privacyPage = await _client.GetAsync( privacy.PathAndQuery, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, termsPage.StatusCode );
        Assert.AreEqual( HttpStatusCode.OK, privacyPage.StatusCode );
    }

    [TestMethod]
    public async Task DeletedLocalUser_CannotAuthenticateUsingOldBlueskyCookie( ) {
        _ = await SignInAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            _ = await db.Users.ExecuteDeleteAsync( TestContext.CancellationToken );
        }
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.AreEqual( 0, _oauth.Revocations, "A stale reference cannot authorize session cleanup; account deletion performs that cleanup explicitly." );
    }

    [TestMethod]
    [DataRow( false, false )]
    [DataRow( true, false )]
    [DataRow( false, true )]
    [DataRow( true, true )]
    public async Task DeletedAndRecreatedAccount_RejectsOldCookieWithoutRevokingReplacementSession( bool expired, bool logout ) {
        using HttpResponseMessage originalLogin = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string originalId = await LocalUserIdAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage deletion = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, deletion.StatusCode );
        _client.DefaultRequestHeaders.Remove( "Cookie" );

        using HttpResponseMessage replacementLogin = await SignInAsync( );
        string replacementCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string replacementId = await LocalUserIdAsync( );
        Assert.AreNotEqual( originalId, replacementId );
        int revocations = _oauth.Revocations;
        int refreshes = _oauth.Refreshes;
        await Options( ).IdentityStore!.Update( CreateCredentials( DateTime.UtcNow.AddMinutes( -1 ) ), TestContext.CancellationToken );

        if (expired) {
            AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( oldCookie.Split( '=', 2 )[1] )!;
            ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes( -1 );
            oldCookie = BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket );
        }

        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", oldCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsNull( replay.GetProperty( "userId" ).GetString( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        if (logout) {
            // Keep replaying the original request cookie even after the response clears it.
            await AddAntiforgeryHeaderAsync( );
            using HttpResponseMessage result = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
        }
        Assert.AreEqual( revocations, _oauth.Revocations, "An obsolete cookie must not revoke the replacement account's credentials." );
        Assert.AreEqual( refreshes, _oauth.Refreshes, "An obsolete cookie must not refresh the replacement account's credentials." );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", replacementCookie );
        JsonElement replacement = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( replacement.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( replacementId, replacement.GetProperty( "userId" ).GetString( ) );
    }

    [TestMethod]
    [DataRow( false, false )]
    [DataRow( true, false )]
    [DataRow( false, true )]
    [DataRow( true, true )]
    public async Task ChangedSecurityStamp_RejectsOldCookieAndAllowsFreshSignIn( bool expired, bool logout ) {
        using HttpResponseMessage originalLogin = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string id = await LocalUserIdAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = (await users.FindByIdAsync( id ))!;
            Assert.IsTrue( (await users.UpdateSecurityStampAsync( user )).Succeeded );
        }
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );

        using HttpResponseMessage freshLogin = await SignInAsync( );
        string freshCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        int revocations = _oauth.Revocations;
        if (expired) {
            AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( oldCookie.Split( '=', 2 )[1] )!;
            ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes( -1 );
            oldCookie = BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket );
        }
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", oldCookie );
        JsonElement obsolete = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( obsolete.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( obsolete.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        if (logout) {
            await AddAntiforgeryHeaderAsync( );
            using HttpResponseMessage result = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
        }
        Assert.AreEqual( revocations, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", freshCookie );
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( current.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( id, current.GetProperty( "userId" ).GetString( ) );
    }

    [TestMethod]
    public async Task ExpiredCookie_RejectsWithoutRevokingNewerSessionForSameAccount( ) {
        using HttpResponseMessage login = await SignInAsync( );
        string cookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( cookie.Split( '=', 2 )[1] )!;
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes( -1 );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        using HttpResponseMessage freshLogin = await SignInAsync( );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    [TestMethod]
    [DataRow( false )]
    [DataRow( true )]
    public async Task DashboardSubdomain_ReceivesBlueskyCookieAndRequiresDashboardRole( bool hasRole ) {
        using HttpResponseMessage login = await SignInAsync( );
        string cookie = login.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) );
        CookieContainer browser = new( );
        browser.SetCookies( _client.BaseAddress!, cookie );
        Uri dashboard = new( "https://dashboard.bridgebeats.example/api/dashboard/authorize" );
        string dashboardCookies = browser.GetCookieHeader( dashboard );
        Assert.StartsWith( "__Secure-", BlueskyAuthenticationConfiguration.CookieName );
        Assert.Contains( BlueskyAuthenticationConfiguration.CookieName + "=", dashboardCookies );
        Assert.AreEqual( "", browser.GetCookieHeader( new Uri( "https://unrelated.example" ) ) );
        if (hasRole) {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            RoleManager<IdentityRole> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>( );
            Assert.IsTrue( (await roles.CreateAsync( new IdentityRole( Roles.AspireDashboardAccess ) )).Succeeded );
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = (await users.FindByIdAsync( await LocalUserIdAsync( ) ))!;
            Assert.IsTrue( (await users.AddToRoleAsync( user, Roles.AspireDashboardAccess )).Succeeded );
        }
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        using HttpRequestMessage request = new( HttpMethod.Get, dashboard );
        request.Headers.Add( "Cookie", dashboardCookies );
        using HttpResponseMessage authorization = await _client.SendAsync( request, TestContext.CancellationToken );
        Assert.AreEqual( hasRole ? HttpStatusCode.OK : HttpStatusCode.Forbidden, authorization.StatusCode );
    }

    [TestMethod]
    [DataRow( "BridgeBeats.UserId", null )]
    [DataRow( "BridgeBeats.UserId", "another-user" )]
    [DataRow( "BridgeBeats.SecurityStamp", null )]
    [DataRow( "BridgeBeats.SecurityStamp", "obsolete-stamp" )]
    public async Task MissingOrMismatchedBinding_RejectsAndClearsCookieWithoutRenewing( string property, string? value ) {
        using HttpResponseMessage login = await SignInAsync( );
        string cookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( cookie.Split( '=', 2 )[1] )!;
        ticket.Properties.SetString( property, value );
        ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddDays( -5 );
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays( 2 );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );
        using HttpResponseMessage result = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        JsonElement who = await result.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsFalse( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        string expired = result.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) );
        Assert.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", expired );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task SlidingCookieRenewal_PreservesCurrentAccountBinding( ) {
        using HttpResponseMessage login = await SignInAsync( );
        string cookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        AuthenticationTicket original = Options( ).TicketDataFormat.Unprotect( cookie.Split( '=', 2 )[1] )!;
        original.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddDays( -5 );
        original.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays( 2 );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( original ) );
        using HttpResponseMessage result = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        JsonElement who = await result.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        string renewedCookie = result.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        AuthenticationTicket renewed = Options( ).TicketDataFormat.Unprotect( renewedCookie.Split( '=', 2 )[1] )!;
        Assert.AreEqual( original.Properties.GetString( BlueskyAccountEvents.UserIdProperty ), renewed.Properties.GetString( BlueskyAccountEvents.UserIdProperty ) );
        Assert.AreEqual( original.Properties.GetString( BlueskyAccountEvents.SecurityStampProperty ), renewed.Properties.GetString( BlueskyAccountEvents.SecurityStampProperty ) );
        Assert.IsGreaterThan( original.Properties.IssuedUtc!.Value, renewed.Properties.IssuedUtc!.Value );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", renewedCookie );
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( current.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( who.GetProperty( "userId" ).GetString( ), current.GetProperty( "userId" ).GetString( ) );
    }

    [TestMethod]
    public async Task Upgrade_PreservesExistingUserKeysAndPlaylists_AndRemovesLegacyTokens( ) {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>( )
            .UseSqlite( $"Data Source={Path.Combine( _directory, "upgrade.db" )}" ).Options;
        await using ApplicationDbContext db = new( options );
        Microsoft.EntityFrameworkCore.Migrations.IMigrator migrator =
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>( db );
        await migrator.MigrateAsync( "20260614235334_NullTokensAndRelink", TestContext.CancellationToken );
        ApplicationUser user = new( ) {
            Id = "existing-user",
            UserName = "existing-user",
            AtProtoDid = DidValue,
            ApiKeyHash = "existing-api-key",
            AppleMusicUserToken = "existing-apple-token"
        };
        _ = db.Users.Add( user );
        _ = db.Playlists.Add( new BridgeBeats.Core.Infrastructure.Playlists.PlaylistEntry {
            PlaylistId = "existing-playlist",
            UserId = user.Id,
            CardIds = "card",
            CardRkeys = "rkey"
        } );
        _ = await db.SaveChangesAsync( TestContext.CancellationToken );
        _ = await db.Database.ExecuteSqlRawAsync( "UPDATE AspNetUsers SET AtProtoRefreshToken = 'legacy-refresh-token'", TestContext.CancellationToken );
        await db.Database.MigrateAsync( TestContext.CancellationToken );
        db.ChangeTracker.Clear( );
        ApplicationUser saved = await db.Users.SingleAsync( TestContext.CancellationToken );
        Assert.AreEqual( user.Id, saved.Id );
        Assert.AreEqual( "existing-api-key", saved.ApiKeyHash );
        Assert.AreEqual( "existing-apple-token", saved.AppleMusicUserToken );
        Assert.AreEqual( user.Id, (await db.Playlists.SingleAsync( TestContext.CancellationToken )).UserId );
        await db.Database.OpenConnectionAsync( TestContext.CancellationToken );
        using System.Data.Common.DbCommand command = db.Database.GetDbConnection( ).CreateCommand( );
        command.CommandText = "SELECT count(*) FROM pragma_table_info('AspNetUsers') WHERE name = 'AtProtoRefreshToken'";
        Assert.AreEqual( 0L, await command.ExecuteScalarAsync( TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task DeleteAccount_RevokesSessionAndDeletesLocalUser( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage result = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.CountAsync( TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    [TestMethod]
    public async Task RemoveLinkedSession_RevokesCredentialsWithoutBrowserCookie( ) {
        _ = await SignInAsync( );
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).RemoveAsync( DidValue );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Register_WithMalformedEmail_Returns400( ) {
        await AddAntiforgeryHeaderAsync( );

        StringContent content = new(
            JsonSerializer.Serialize( new { email = "notanemail", password = "ValidPassword123!" } ),
            Encoding.UTF8,
            "application/json"
        );

        HttpResponseMessage response = await _client.PostAsync( "/account/register", content, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode, "Malformed email must produce 400" );
    }

    /// <summary>
    /// Register with an email already in use must return 400 DuplicateEmail.
    ///
    /// Failure-first evidence: the pre-existing user is created with a DIFFERENT UserName so that
    /// Identity's username-uniqueness check does not produce the 400 — only the explicit
    /// FindByEmailAsync check in Register discriminates. Verified by commenting out the
    /// FindByEmailAsync check: the second POST /account/register returns 200 (CreateAsync succeeds
    /// because RequireUniqueEmail=false and the username is unique). With the check in place, it
    /// returns 400 with Identity's DuplicateEmail description.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Register_WithDuplicateEmail_Returns400( ) {
        // Arrange — create the first user via UserManager with a DIFFERENT UserName than the email.
        // This is deliberate: Identity always enforces username uniqueness, so if UserName == email,
        // a 400 fires even without the explicit FindByEmailAsync check — giving false coverage.
        // Using a distinct UserName ensures only the email-uniqueness path discriminates.
        using IServiceScope scope = _app.Services.CreateScope( );
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );

        string existingEmail = "duplicate@example.com";
        ApplicationUser existing = new( ) {
            UserName = "existing-user-different-username",
            Email = existingEmail,
            EmailConfirmed = true
        };
        IdentityResult createResult = await userManager.CreateAsync( existing, "TestPassword123!" );
        Assert.IsTrue( createResult.Succeeded, "Setup: first user creation should succeed" );

        // Arrange — try to register a second user with the same email but a fresh UserName (== email,
        // as Register always sets UserName = request.Email).
        await AddAntiforgeryHeaderAsync( );

        StringContent content = new(
            JsonSerializer.Serialize( new { email = existingEmail, password = "AnotherPassword123!" } ),
            Encoding.UTF8,
            "application/json"
        );

        // Act
        HttpResponseMessage response = await _client.PostAsync( "/account/register", content, TestContext.CancellationToken );

        // Assert
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        string body = await response.Content.ReadAsStringAsync( TestContext.CancellationToken );
        Assert.IsTrue(
            body.Contains( "already taken", StringComparison.OrdinalIgnoreCase ) ||
            body.Contains( "DuplicateEmail", StringComparison.OrdinalIgnoreCase ),
            $"Expected duplicate-email wording in: {body}"
        );
    }


    [TestMethod]
    public async Task LegacyOAuthIdentityCookie_RequiresNewLoginWithoutDeletingAccount( ) {
        _ = await SignInAsync( );
        string id = await LocalUserIdAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).FindByIdAsync( id ))!;
            ClaimsPrincipal principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>( ).CreateAsync( user );
            DefaultHttpContext context = new( ) { RequestServices = scope.ServiceProvider };
            context.Request.Scheme = "https";
            await context.SignInAsync( IdentityConstants.ApplicationScheme, principal );
            _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", context.Response.Headers.SetCookie[0]!.Split( ';' )[0] );
        }
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( id, await LocalUserIdAsync( ) );
    }

    [TestMethod]
    public async Task PasswordRegistration_StillAuthenticatesWithLocalIdentityCookie( ) {
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage result = await _client.PostAsJsonAsync( "/account/register",
            new { email = "password@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, result.StatusCode );
        string cookieName = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        string cookie = result.Headers.GetValues( "Set-Cookie" ).Single( c => c.StartsWith( cookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( await LocalUserIdAsync( ), who.GetProperty( "userId" ).GetString( ) );
        Assert.IsFalse( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task RevocationUnavailable_StillRemovesLinkedSession( ) {
        _ = await SignInAsync( );
        _oauth.RevocationFails = true;
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).RemoveAsync( DidValue );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    private async Task AddAntiforgeryHeaderAsync( ) {
        // Antiforgery's cookie is separate from the SDK reference cookie.
        using HttpResponseMessage tokenResponse = await _client.GetAsync( "/account/antiforgery-token", TestContext.CancellationToken );
        JsonElement data = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        string cookies = _client.DefaultRequestHeaders.TryGetValues( "Cookie", out IEnumerable<string>? existing )
            ? existing.Single( ) + "; " : "";
        cookies += tokenResponse.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( ".AspNetCore.Antiforgery.", StringComparison.Ordinal ) ).Split( ';' )[0];
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookies );
        _client.DefaultRequestHeaders.Remove( "X-XSRF-TOKEN" );
        _client.DefaultRequestHeaders.Add( "X-XSRF-TOKEN", data.GetProperty( "token" ).GetString( ) );
    }

    private BlueskyAuthenticationOptions Options( ) => _app.Services.GetRequiredService<IOptionsMonitor<BlueskyAuthenticationOptions>>( ).Get( s_scheme );
    private async Task<long> CorrelationStateCountAsync( ) {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        await db.Database.OpenConnectionAsync( TestContext.CancellationToken );
        using System.Data.Common.DbCommand command = db.Database.GetDbConnection( ).CreateCommand( );
        command.CommandText = "SELECT count(*) FROM idunno_bluesky_correlation_states";
        return (long)(await command.ExecuteScalarAsync( TestContext.CancellationToken ))!;
    }
    private async Task<string> LocalUserIdAsync( ) {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.SingleAsync( TestContext.CancellationToken )).Id;
    }
    private async Task<HttpResponseMessage> SignInAsync( ) {
        HttpResponseMessage response = await _client.PostAsync( "/test/signin", null, TestContext.CancellationToken );
        response.EnsureSuccessStatusCode( );
        string cookie = response.Headers.GetValues( "Set-Cookie" ).Last( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        return response;
    }
    private DPoPAccessCredentials CreateCredentials( DateTime expires, string refresh = "initial-refresh" ) {
        string jwt = new JwtSecurityTokenHandler( ).WriteToken( new JwtSecurityToken(
            issuer: _service, audience: $"did:web:{new Uri( _service ).Host}", claims: [new Claim( "sub", DidValue ), new Claim( "scope", "atproto repo:link.bridgebeats.playlist" )],
            notBefore: expires.AddHours( -1 ), expires: expires ) );
        return new DPoPAccessCredentials( new Uri( _service ), jwt, refresh, _proofKey, "test-nonce" );
    }
    private sealed class TestHttpClientFactory( HttpMessageHandler handler ) : IHttpClientFactory {
        public HttpClient CreateClient( string name ) => new( handler, disposeHandler: false );
    }
    private sealed class OAuthHandler : HttpMessageHandler {
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _authorizations = new( );
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _codes = new( );
        public string Service = null!;
        public Func<string> TokenFactory { get; set; } = null!;
        public int Refreshes;
        public int Revocations;
        public int PushedAuthorizations;
        public int CodeExchanges;
        public string? LoginHint;
        public bool RevocationFails;
        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken ) {
            string path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/.well-known/atproto-did") {
                return new HttpResponseMessage( HttpStatusCode.OK ) { Content = new StringContent( DidValue ) };
            } else if (path == "/" + DidValue) {
                body = new {
                    id = DidValue,
                    alsoKnownAs = new[] { "at://first.example" },
                    service = new[] { new { id = "#atproto_pds", type = "AtprotoPersonalDataServer", serviceEndpoint = Service } }
                };
            } else if (path == "/par") {
                Dictionary<string, string> parameters = ParseParameters( await request.Content!.ReadAsStringAsync( cancellationToken ) );
                LoginHint = parameters["login_hint"];
                string requestUri = "urn:ietf:params:oauth:request_uri:" + Guid.NewGuid( ).ToString( "N" );
                _authorizations[requestUri] = parameters;
                _ = Interlocked.Increment( ref PushedAuthorizations );
                return new HttpResponseMessage( HttpStatusCode.Created ) {
                    Content = JsonContent.Create( new { request_uri = requestUri, expires_in = 90 } )
                };
            } else if (path == "/authorize") {
                Dictionary<string, string> query = ParseParameters( request.RequestUri.Query );
                if (!query.TryGetValue( "request_uri", out string? requestUri ) || !_authorizations.TryRemove( requestUri, out Dictionary<string, string>? parameters )) {
                    return new HttpResponseMessage( HttpStatusCode.BadRequest );
                }
                string code = Guid.NewGuid( ).ToString( "N" );
                _codes[code] = parameters;
                HttpResponseMessage redirect = new( HttpStatusCode.Redirect );
                redirect.Headers.Location = new Uri( QueryHelpers.AddQueryString( parameters["redirect_uri"],
                    new Dictionary<string, string?> { ["code"] = code, ["state"] = parameters["state"], ["iss"] = Service } ) );
                return redirect;
            } else if (path == "/token") {
                Dictionary<string, string> parameters = ParseParameters( await request.Content!.ReadAsStringAsync( cancellationToken ) );
                if (parameters["grant_type"] == "authorization_code") {
                    if (!parameters.TryGetValue( "code", out string? code ) || !_codes.TryRemove( code, out Dictionary<string, string>? authorization ) ||
                        !parameters.TryGetValue( "code_verifier", out string? verifier ) ||
                        authorization["code_challenge"] != Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode( SHA256.HashData( Encoding.ASCII.GetBytes( verifier ) ) ) ||
                        authorization["code_challenge_method"] != "S256" || authorization["redirect_uri"] != parameters["redirect_uri"] ||
                        authorization["client_id"] != parameters["client_id"] || !request.Headers.Contains( "DPoP" )) {
                        return new HttpResponseMessage( HttpStatusCode.BadRequest ) { Content = JsonContent.Create( new { error = "invalid_grant" } ) };
                    }
                    _ = Interlocked.Increment( ref CodeExchanges );
                } else {
                    _ = Interlocked.Increment( ref Refreshes );
                }
                body = new { access_token = TokenFactory( ), refresh_token = "rotated-refresh", token_type = "DPoP", expires_in = 3600, scope = "atproto repo:link.bridgebeats.playlist", sub = DidValue };
            } else if (path == "/revoke") {
                _ = Interlocked.Increment( ref Revocations );
                if (RevocationFails) {
                    return new HttpResponseMessage( HttpStatusCode.ServiceUnavailable ) { Content = JsonContent.Create( new { error = "temporarily_unavailable" } ) };
                }
                body = new { };
            } else if (path.EndsWith( "oauth-protected-resource", StringComparison.Ordinal )) {
                body = new { resource = Service, authorization_servers = new[] { Service } };
            } else if (path.EndsWith( "describeServer", StringComparison.Ordinal )) {
                body = new { did = $"did:web:{new Uri( Service ).Host}", availableUserDomains = new[] { ".example" } };
            } else if (path == "/jwks") {
                body = new { keys = Array.Empty<object>( ) };
            } else if (path.EndsWith( "oauth-authorization-server", StringComparison.Ordinal )) {
                body = new {
                    issuer = Service,
                    authorization_endpoint = Service + "/authorize",
                    token_endpoint = Service + "/token",
                    pushed_authorization_request_endpoint = Service + "/par",
                    require_pushed_authorization_requests = true,
                    revocation_endpoint = Service + "/revoke",
                    jwks_uri = Service + "/jwks",
                    response_types_supported = new[] { "code" },
                    grant_types_supported = new[] { "authorization_code", "refresh_token" },
                    subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "ES256" },
                    token_endpoint_auth_methods_supported = new[] { "none" },
                    code_challenge_methods_supported = new[] { "S256" },
                    authorization_response_iss_parameter_supported = true
                };
            } else {
                return new HttpResponseMessage( HttpStatusCode.NotFound );
            }
            HttpResponseMessage response = new( HttpStatusCode.OK ) { Content = JsonContent.Create( body ) };
            response.Headers.Add( "DPoP-Nonce", "test-nonce" );
            return response;
        }

        private static Dictionary<string, string> ParseParameters( string query ) =>
            QueryHelpers.ParseQuery( query ).ToDictionary( item => item.Key, item => item.Value.ToString( ) );
    }
}
#pragma warning restore CS1591
