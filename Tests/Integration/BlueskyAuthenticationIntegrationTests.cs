using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
using idunno.Bluesky.AspNet.Authentication.Events;
using idunno.Bluesky.AspNet.Authentication.SQLite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Integration;

[TestClass]
public class BlueskyAuthenticationIntegrationTests {
    private static readonly string s_scheme = BlueskyAuthenticationDefaults.AuthenticationScheme;
    private const string DidValue = "did:plc:kx2mxhedzbeuqethywrzdexz";
    private const string OtherDidValue = "did:plc:aaaaaaaaaaaaaaaaaaaaaaaa";
    private string _configuredDomain = "bridgebeats.example";
    private string _service = null!;
    private WebApplication _authorizationServer = null!;
    private string _directory = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Mock<IDidHandleResolver> _handles = null!;
    private string _proofKey = null!;
    private string _clientSigningKeyPath = null!;
    private string _previousClientSigningKeyPath = null!;
    private string _clientSigningKeyId = null!;
    private string _previousClientSigningKeyId = null!;
    private readonly OAuthHandler _oauth = new( );
    private readonly ActionExceptionObserver _actionExceptionObserver = new( );
    private CancellationTokenSource? _serverRequestAborted;
    private bool _blueskyEnabled = true;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task Initialize( ) {
        _directory = Path.Combine( Path.GetTempPath( ), $"bridgebeats-bluesky-{Guid.NewGuid( ):N}" );
        _ = Directory.CreateDirectory( _directory );
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        _clientSigningKeyPath = Path.Combine( _directory, "client-signing-key.jwk" );
        _previousClientSigningKeyPath = Path.Combine( _directory, "previous-client-signing-key.jwk" );
        string clientSigningJwk = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        string previousClientSigningJwk = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        File.WriteAllText( _clientSigningKeyPath, clientSigningJwk );
        File.WriteAllText( _previousClientSigningKeyPath, previousClientSigningJwk );
        using JsonDocument clientSigningKeyDocument = JsonDocument.Parse( clientSigningJwk );
        using JsonDocument previousClientSigningKeyDocument = JsonDocument.Parse( previousClientSigningJwk );
        _clientSigningKeyId = clientSigningKeyDocument.RootElement.GetProperty( "kid" ).GetString( )!;
        _previousClientSigningKeyId = previousClientSigningKeyDocument.RootElement.GetProperty( "kid" ).GetString( )!;
        WebApplicationBuilder authorizationBuilder = WebApplication.CreateBuilder( );
        authorizationBuilder.Configuration["AllowedHosts"] = "*";
        _ = authorizationBuilder.Logging.ClearProviders( );
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
                _ = request.Headers.TryAddWithoutValidation( header.Key, [.. header.Value] );
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
        await StartApplicationAsync( );
    }

    private async Task StartApplicationAsync( ) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder( new WebApplicationOptions {
            WebRootPath = Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "../../../../src/BridgeBeats.Web/wwwroot" ) )
        } );
        builder.Configuration["AllowedHosts"] = "*";
        _ = builder.Logging.ClearProviders( );
        _ = builder.Logging.AddConsole( );
        _ = builder.Services.PostConfigure<LoggerFilterOptions>( options => {
            options.Rules.Clear( );
            options.MinLevel = LogLevel.Warning;
        } );
        _ = builder.WebHost.UseTestServer( );
        string connection = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        _ = builder.Services.AddDbContextFactory<ApplicationDbContext>( options => options.UseSqlite( connection ) );
        _ = builder.Services.AddControllersWithViews( )
            .AddApplicationPart( typeof( AccountController ).Assembly )
            .AddMvcOptions( options => options.Filters.Add( _actionExceptionObserver ) );
        _ = builder.Services.AddAntiforgery( options => options.HeaderName = "X-XSRF-TOKEN" );
        AppSettings settings = new( ) {
            IdentityConnectionString = connection,
            DataProtectionKeyPath = Path.Combine( _directory, "keys" ),
            Domain = _configuredDomain,
            ApiKeySalt = "test-salt",
            InternalServiceKey = "test-service-key",
            ATProtoOAuthSigningKeyPath = _clientSigningKeyPath,
            ATProtoOAuthAdditionalSigningKeyPaths = [_previousClientSigningKeyPath]
        };
        StartupExtensions.ConfigureIdentity( builder.Services, settings );
        StartupExtensions.ConfigureApiKeyAuth( builder.Services, settings );
        _handles = new Mock<IDidHandleResolver>( );
        _ = _handles.Setup( h => h.ResolveHandleAsync( It.IsAny<Did>( ), It.IsAny<CancellationToken>( ) ) )
            .ReturnsAsync( new Handle( "first.example" ) );
        _ = builder.Services.AddSingleton( _handles.Object );
        if (_blueskyEnabled) {
            _ = builder.Services.AddBridgeBeatsBluesky( settings,
                "https://bridgebeats.example" + BlueskyAuthenticationConfiguration.ClientMetadataPath, "https://bridgebeats.example" );
            _ = builder.Services.PostConfigure<BlueskyAgentOptions>( options => {
                options.OAuthOptions!.AllowInsecureProtocols = true;
                options.OAuthOptions.AllowLoopback = true;
            } );
        }
        // The OAuth transport is deterministic; all application middleware, stores, DP, events and Identity run normally.
        _ = builder.Services.RemoveAll<IHttpClientFactory>( );
        _ = builder.Services.AddSingleton<IHttpClientFactory>( new TestHttpClientFactory( _oauth ) );
        _app = builder.Build( );
        _ = _app.Use( async ( context, next ) => {
            if (_serverRequestAborted is not null) {
                context.RequestAborted = _serverRequestAborted.Token;
            }
            await next( );
        } );
        _ = _app.UseRouting( );
        _ = _app.UseCanonicalAuthenticationOrigin( AuthenticationOrigin.Parse( settings.Domain ) );
        if (_blueskyEnabled) {
            // The login-start limiter is only mounted when OAuth is enabled, matching the production pipeline.
            // Without it the policy registered by AddBridgeBeatsBluesky is never applied to a request.
            _ = _app.UseRateLimiter( );
            _ = _app.UseBlueskyOAuthClientMetadata( );
        }
        _ = _app.UseStaticFiles( );
        if (_blueskyEnabled) {
            // Metadata is generated from the production URLs above. The SDK's discovery development mode
            // additionally requires an HTTP callback when talking to the local HTTP authorization server.
            _app.Services.GetRequiredService<IOptionsMonitor<BlueskyAgentOptions>>( ).CurrentValue.OAuthOptions!.ReturnUri = new Uri( _service + "/account/atproto-callback" );
            _app.Services.GetRequiredService<IOptions<BlueskyAgentOptions>>( ).Value.OAuthOptions!.ReturnUri = new Uri( _service + "/account/atproto-callback" );
        }
        _ = _app.UseAuthentication( );
        _ = _app.UseAuthorization( );
        if (_blueskyEnabled) {
            _ = _app.MapPost( "/test/signin", async ( HttpContext context ) => {
                ClaimsIdentity identity = IIdentityStore.BuildClaimsIdentity( CreateCredentials( DateTime.UtcNow.AddHours( 1 ) ) );
                await context.SignInAsync( s_scheme, new ClaimsPrincipal( identity ) );
                await context.Response.WriteAsync( "signed in", TestContext.CancellationToken );
            } );
            _ = _app.MapGet( "/test/who", ( HttpContext context, BlueskyAgent agent ) => Results.Json( new {
                authenticated = context.User.Identity?.IsAuthenticated == true,
                userId = context.User.FindFirstValue( System.Security.Claims.ClaimTypes.NameIdentifier ),
                did = context.User.FindFirstValue( AtProtoClaims.Did ),
                agentAuthenticated = agent.IsAuthenticated
            } ) );
        }
        _ = _app.MapControllers( );
        _ = _app.MapDefaultControllerRoute( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            await db.Database.MigrateAsync( TestContext.CancellationToken );
        }
        await _app.StartAsync( TestContext.CancellationToken );
        _client = _app.GetTestClient( );
        _client.BaseAddress = new Uri( "https://bridgebeats.example" );
        if (_blueskyEnabled) {
            _oauth.MetadataRetriever = async cancellationToken =>
                await _client.GetFromJsonAsync<JsonElement>( "/oauth-client-metadata.json", cancellationToken );
        }
    }

    [TestCleanup]
    public async Task Cleanup( ) {
        _serverRequestAborted?.Dispose( );
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
        await ExpireStoredAccessTokenAsync( );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsGreaterThan( 0, _oauth.Refreshes );
        ClaimsIdentity identity = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "rotated-refresh", identity.FindFirst( AtProtoClaims.RefreshToken )?.Value );
    }

    [TestMethod]
    public async Task SuccessfulCredentialWrite_CleansExpiredIdentitiesAndRefreshLocks( ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, X'00', @expired, 'old');
                               INSERT INTO "idunno_bluesky_refresh_locks" ("Did", "LockToken", "ExpiresAtUtcTicks")
                               VALUES (@did, 'old', @expired);
                               """;
            _ = seed.Parameters.AddWithValue( "@did", OtherDidValue );
            _ = seed.Parameters.AddWithValue( "@expired", DateTime.UtcNow.AddMinutes( -1 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }

        _ = await SignInAsync( );

        await using SqliteConnection check = new( connectionString );
        await check.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand count = check.CreateCommand( );
        count.CommandText = """
                            SELECT (SELECT COUNT(*) FROM "idunno_bluesky_identities" WHERE "Did" = @did) +
                                   (SELECT COUNT(*) FROM "idunno_bluesky_refresh_locks" WHERE "Did" = @did);
                            """;
        _ = count.Parameters.AddWithValue( "@did", OtherDidValue );
        Assert.AreEqual( 0L, await count.ExecuteScalarAsync( TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Logout_RevokesAndRemovesStoredSession( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    [TestMethod]
    public async Task Logout_RejectsStaleAccountUpdateAndKeepsOldCookieInvalidAfterNewLogin( ) {
        _ = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        await using AsyncServiceScope staleRequest = _app.Services.CreateAsyncScope( );
        UserManager<ApplicationUser> users = staleRequest.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
        ApplicationUser staleUser = await users.Users.SingleAsync( TestContext.CancellationToken );
        string? originalSecurityStamp = staleUser.SecurityStamp;
        string? originalConcurrencyStamp = staleUser.ConcurrencyStamp;

        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        staleUser.AtProtoHandle = "stale-update.example";
        IdentityResult update = await users.UpdateAsync( staleUser );
        Assert.IsFalse( update.Succeeded );
        Assert.Contains( nameof( IdentityErrorDescriber.ConcurrencyFailure ), update.Errors.Select( error => error.Code ) );

        await using (AsyncServiceScope check = _app.Services.CreateAsyncScope( )) {
            ApplicationUser persisted = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
                .SingleAsync( TestContext.CancellationToken );
            Assert.AreEqual( originalSecurityStamp, persisted.SecurityStamp );
            Assert.AreNotEqual( originalConcurrencyStamp, persisted.ConcurrencyStamp );
            Assert.AreNotEqual( staleUser.AtProtoHandle, persisted.AtProtoHandle );
        }

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( current.GetProperty( "authenticated" ).GetBoolean( ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", oldCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task LateRefreshWrite_CannotRestoreLoggedOutSessionOrReplaceNewLogin( ) {
        _ = await SignInAsync( );
        IIdentityStore store = Options( ).IdentityStore!;
        ClaimsIdentity oldIdentity = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );

        await store.Update( oldIdentity, TestContext.CancellationToken );
        Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        _ = await SignInAsync( );
        ClaimsIdentity newIdentity = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreNotEqual( oldIdentity.FindFirst( AtProtoClaims.DPoPProof )?.Value,
            newIdentity.FindFirst( AtProtoClaims.DPoPProof )?.Value );
        await store.Update( oldIdentity, TestContext.CancellationToken );
        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( newIdentity.FindFirst( AtProtoClaims.DPoPProof )?.Value,
            persisted.FindFirst( AtProtoClaims.DPoPProof )?.Value );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    [DataRow( false )]
    [DataRow( true )]
    public async Task NewBlueskyLogin_PreservesOtherBrowsersUnlessPreviouslySignedOut( bool logoutFirst ) {
        using HttpResponseMessage originalLogin = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string userId = await LocalUserIdAsync( );
        if (logoutFirst) {
            await AddAntiforgeryHeaderAsync( );
            using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
            Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        }

        if (!logoutFirst) { _ = _client.DefaultRequestHeaders.Remove( "Cookie" ); }
        using HttpResponseMessage replacementLogin = await SignInAsync( );
        string replacementCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        Assert.AreEqual( userId, await LocalUserIdAsync( ) );
        int revocations = _oauth.Revocations;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", oldCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        if (!logoutFirst) {
            Assert.IsTrue( replay.GetProperty( "authenticated" ).GetBoolean( ) );
            Assert.IsTrue( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
            Assert.AreEqual( userId, replay.GetProperty( "userId" ).GetString( ) );
            Assert.AreEqual( revocations, _oauth.Revocations );
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", replacementCookie );
            JsonElement otherBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
            Assert.IsTrue( otherBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
            return;
        }
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );

        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage staleLogout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, staleLogout.StatusCode );
        Assert.AreEqual( revocations, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", replacementCookie );
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( current.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( userId, current.GetProperty( "userId" ).GetString( ) );
    }

    [TestMethod]
    public async Task LogoutOnOneBrowser_KeepsOtherBrowserSignedIn( ) {
        _ = await SignInAsync( );
        string firstCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", firstCookie );
        JsonElement firstBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( firstBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( firstBrowser.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task NewOAuthProofKey_RevokesReplacedGrantAndKeepsOtherBrowserSignedIn( ) {
        using HttpResponseMessage first = await SignInAsync( );
        string firstCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );

        using HttpResponseMessage second = await SignInAsync( );

        Assert.IsGreaterThan( 0, _oauth.Revocations );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", firstCookie );
        JsonElement firstBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( firstBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( firstBrowser.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task CredentialWrite_WithoutPreparedBrowserSession_RollsBackReplacement( ) {
        using HttpResponseMessage signedIn = await SignInAsync( );
        IIdentityStore store = Options( ).IdentityStore!;
        ClaimsIdentity original = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        ClaimsIdentity replacement = IIdentityStore.BuildClaimsIdentity( CreateCredentials( DateTime.UtcNow.AddHours( 1 ) ) );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        ApplicationUser user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .SingleAsync( TestContext.CancellationToken );
        IHttpContextAccessor accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>( );
        HttpContext? previousContext = accessor.HttpContext;
        DefaultHttpContext request = new( ) { RequestServices = scope.ServiceProvider };
        request.Items[BridgeBeatsIdentityStore.ExpectedStampKey] = user.SecurityStamp!;
        accessor.HttpContext = request;
        try {
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>( ( ) =>
                store.Add( replacement, TestContext.CancellationToken ) );
        } finally {
            accessor.HttpContext = previousContext;
        }

        ClaimsIdentity saved = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( original.FindFirst( AtProtoClaims.DPoPProof )?.Value,
            saved.FindFirst( AtProtoClaims.DPoPProof )?.Value );
    }

    [TestMethod]
    public async Task StaleBrowserLogout_CannotRevokeNewLogin( ) {
        _ = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        AuthenticationTicket oldTicket = Options( ).TicketDataFormat.Unprotect( oldCookie.Split( '=', 2 )[1] )!;
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        int revocations = _oauth.Revocations;

        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndReplacedSessionAsync( oldTicket );

        Assert.AreEqual( revocations, _oauth.Revocations );
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( current.GetProperty( "authenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task Logout_WhenProtectedResourceDiscoveryFails_RemovesStoredSessionAndRejectsCookieReplay( ) {
        _ = await SignInAsync( );
        string cookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        await ExpireStoredAccessTokenAsync( );
        _oauth.ProtectedResourceDiscoveryFails = true;

        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        string expiredCookie = logout.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) );
        Assert.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", expiredCookie );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "A current cookie must still authorize local credential cleanup when token refresh discovery is unavailable." );

        _oauth.ProtectedResourceDiscoveryFails = false;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task Logout_WithUnprotectableCookiePreservesStoredIdentityAndSkipsRevocation( ) {
        _ = await SignInAsync( );
        string validCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=not-a-valid-ticket" );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        string expiredCookie = logout.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) );
        Assert.Contains( "expires=", expiredCookie, StringComparison.OrdinalIgnoreCase );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", validCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task PasswordLogin_WhenProtectedResourceDiscoveryFails_ClearsBlueskySessionAndSignsInPasswordAccount( ) {
        string passwordUserId;
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = new( ) { UserName = "switch@example.com", Email = "switch@example.com" };
            IdentityResult created = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( )
                .CreateAsync( user, "ValidPassword123!" );
            Assert.IsTrue( created.Succeeded );
            passwordUserId = user.Id;
        }
        _ = await SignInAsync( );
        await ExpireStoredAccessTokenAsync( );
        _oauth.ProtectedResourceDiscoveryFails = true;
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage login = await _client.PostAsJsonAsync( "/account/login",
            new { email = "switch@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, login.StatusCode, await login.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        string expiredBlueskyCookie = login.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) );
        Assert.Contains( "expires=", expiredBlueskyCookie, StringComparison.OrdinalIgnoreCase );
        string identityCookieName = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        string identityCookie = login.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( identityCookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", identityCookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( passwordUserId, who.GetProperty( "userId" ).GetString( ) );
        Assert.IsFalse( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task PasswordLoginOnOneBrowser_KeepsOtherBlueskyBrowserSignedIn( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = new( ) { UserName = "password@example.com", Email = "password@example.com" };
            Assert.IsTrue( (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( )
                .CreateAsync( user, "ValidPassword123!" )).Succeeded );
        }
        _ = await SignInAsync( );
        string firstCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage login = await _client.PostAsJsonAsync( "/account/login",
            new { email = "password@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, login.StatusCode );
        Assert.AreEqual( 0, _oauth.Revocations );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", firstCookie );
        JsonElement firstBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( firstBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( firstBrowser.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task LastBlueskyLogout_KeepsLinkedPasswordBrowserSignedIn( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = new( ) {
                UserName = "linked@example.com",
                Email = "linked@example.com",
                AtProtoDid = DidValue
            };
            Assert.IsTrue( (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( )
                .CreateAsync( user, "ValidPassword123!" )).Succeeded );
        }
        _ = await SignInAsync( );
        string blueskyCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage passwordLogin = await _client.PostAsJsonAsync( "/account/login",
            new { email = "linked@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, passwordLogin.StatusCode );
        string identityName = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        string passwordCookie = passwordLogin.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( identityName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", blueskyCookie );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage blueskyLogout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, blueskyLogout.StatusCode );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", passwordCookie );
        JsonElement passwordBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( passwordBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( passwordBrowser.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task PasswordLogin_ForLinkedAccount_UsesRotatedStampAndPersistsNewApiKey( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = new( ) {
                UserName = "linked@example.com",
                Email = "linked@example.com",
                AtProtoDid = DidValue
            };
            IdentityResult created = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( )
                .CreateAsync( user, "ValidPassword123!" );
            Assert.IsTrue( created.Succeeded );
        }
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage login = await _client.PostAsJsonAsync( "/account/login",
            new { email = "linked@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, login.StatusCode );
        JsonElement result = await login.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        ApplicationUser persisted = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .SingleAsync( TestContext.CancellationToken );
        ApiKeyHasher hasher = check.ServiceProvider.GetRequiredService<ApiKeyHasher>( );
        Assert.AreEqual( hasher.HashApiKey( result.GetProperty( "apiKey" ).GetString( )! ), persisted.ApiKeyHash );
        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions cookieOptions =
            _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
                .Get( IdentityConstants.ApplicationScheme );
        string identityCookie = login.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( cookieOptions.Cookie.Name + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        AuthenticationTicket ticket = cookieOptions.TicketDataFormat.Unprotect( identityCookie.Split( '=', 2 )[1] )!;
        string stampClaimType = check.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>( ).Value.ClaimsIdentity.SecurityStampClaimType;
        Assert.AreEqual( persisted.SecurityStamp, ticket.Principal.FindFirstValue( stampClaimType ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", identityCookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( persisted.Id, who.GetProperty( "userId" ).GetString( ) );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", requestCookies );

        using HttpClient browser = new( new HttpClientHandler { AllowAutoRedirect = false } );
        using HttpResponseMessage authorization = await browser.GetAsync( authorizationUri, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, authorization.StatusCode );
        // Route the loopback provider's callback through the real application controller in TestServer.
        string callback = authorization.Headers.Location!.PathAndQuery;
        using HttpResponseMessage result = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, result.StatusCode );
        Assert.AreEqual( "/", new Uri( _client.BaseAddress!, result.Headers.Location! ).AbsolutePath, _oauth.LastAssertionValidationFailure );
        Assert.AreEqual( 1, _oauth.CodeExchanges, "The provider must validate the single-use code and PKCE verifier." );
        Assert.AreEqual( 0L, await CorrelationStateCountAsync( ) );
        string cookie = result.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        Assert.IsTrue( _oauth.ClientAssertionKeyIds.TryPeek( out string? firstKey ) );
        Assert.AreEqual( _clientSigningKeyId, firstKey, "New sessions must use the active signing key." );
        string id = await LocalUserIdAsync( );
        if (existingAccount) {
            Assert.AreEqual( existingId, id );
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            ApplicationUser saved = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).FindByIdAsync( id ))!;
            Assert.AreEqual( "existing-api-key-hash", saved.ApiKeyHash );
        }
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( id, who.GetProperty( "userId" ).GetString( ) );
        Assert.AreEqual( DidValue, who.GetProperty( "did" ).GetString( ) );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );

        // Even a saved copy of the original correlation cookie cannot reuse consumed login state.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", requestCookies );
        using HttpResponseMessage replay = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, replay.StatusCode );
        Uri replayLocation = new( _client.BaseAddress!, replay.Headers.Location! );
        Assert.AreEqual( "/account/login", replayLocation.AbsolutePath );
        Assert.AreEqual( "Bluesky login failed. Please try again.",
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery( replayLocation.Query )["error"].ToString( ) );
        Assert.AreEqual( 1, _oauth.CodeExchanges );
        Assert.AreEqual( id, await LocalUserIdAsync( ) );
    }

    [TestMethod]
    public async Task Callback_RejectsTokenSubjectWhoseDidDocumentNamesAnotherPds( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser victim = new( ) { UserName = "victim", AtProtoDid = OtherDidValue };
            Assert.IsTrue( (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).CreateAsync( victim )).Succeeded );
        }
        _oauth.TokenSubjectDid = OtherDidValue;
        _oauth.TokenFactory = ( ) => CreateCredentials( DateTime.UtcNow.AddHours( 1 ), did: OtherDidValue ).AccessJwt;
        string callback = await PrepareOAuthCallbackAsync( );

        using HttpResponseMessage response = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.Redirect, response.StatusCode );
        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, response.Headers.Location! ).AbsolutePath );
        Assert.IsFalse( response.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                        cookies.Any( cookie => cookie.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ) );
        Assert.AreEqual( 0L, await CorrelationStateCountAsync( ) );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        ApplicationUser saved = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.SingleAsync( TestContext.CancellationToken );
        Assert.AreEqual( OtherDidValue, saved.AtProtoDid );
        Assert.IsNull( saved.AtProtoHandle );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( OtherDidValue ), TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Callback_RejectsIssuerNoLongerAuthorizedBySubjectsPds( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = new( ) { UserName = "existing-bluesky-user", AtProtoDid = DidValue };
            Assert.IsTrue( (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).CreateAsync( user )).Succeeded );
        }
        string callback = await PrepareOAuthCallbackAsync( );
        _oauth.AuthoritativeServer = "https://other-authorization-server.example";

        using HttpResponseMessage response = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.Redirect, response.StatusCode );
        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, response.Headers.Location! ).AbsolutePath );
        Assert.IsFalse( response.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                        cookies.Any( cookie => cookie.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ) );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        ApplicationUser saved = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.SingleAsync( TestContext.CancellationToken );
        Assert.IsNull( saved.AtProtoHandle );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task RejectedCallback_CannotReplaceOrRevokeExistingVictimSession( ) {
        _ = await SignInAsync( );
        string victimCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string victimId = await LocalUserIdAsync( );
        ClaimsIdentity before = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        string callback = await PrepareOAuthCallbackAsync( );
        _oauth.AuthoritativeServer = "https://attacker-issuer.example";

        using HttpResponseMessage rejection = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, rejection.Headers.Location! ).AbsolutePath );
        ClaimsIdentity after = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( before.FindFirst( AtProtoClaims.DPoPProof )?.Value,
            after.FindFirst( AtProtoClaims.DPoPProof )?.Value );
        Assert.AreEqual( before.FindFirst( AtProtoClaims.RefreshToken )?.Value,
            after.FindFirst( AtProtoClaims.RefreshToken )?.Value );
        Assert.AreEqual( 0, _oauth.Revocations );

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", victimCookie );
        JsonElement victim = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( victim.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( victimId, victim.GetProperty( "userId" ).GetString( ) );
    }

    [TestMethod]
    public async Task RejectedDifferentDidCallback_PreservesCurrentBrowserSession( ) {
        _ = await SignInAsync( );
        string firstCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _oauth.TokenSubjectDid = OtherDidValue;
        _oauth.TokenFactory = ( ) => CreateCredentials( DateTime.UtcNow.AddHours( 1 ), did: OtherDidValue ).AccessJwt;
        string callback = await PrepareOAuthCallbackAsync( );

        using HttpResponseMessage rejected = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, rejected.Headers.Location! ).AbsolutePath );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", firstCookie );
        JsonElement firstBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( firstBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task SwitchingAccountsOnOneBrowser_KeepsOtherBrowsersSession( ) {
        _ = await SignInAsync( );
        string firstCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        string replacedCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _oauth.OtherDidPds = _service;
        _oauth.TokenSubjectDid = OtherDidValue;
        _oauth.TokenFactory = ( ) => CreateCredentials( DateTime.UtcNow.AddHours( 1 ), did: OtherDidValue ).AccessJwt;
        string callback = await PrepareOAuthCallbackAsync( );

        using HttpResponseMessage switched = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.Redirect, switched.StatusCode );
        Assert.AreEqual( "/", new Uri( _client.BaseAddress!, switched.Headers.Location! ).AbsolutePath );
        string newCookie = switched.Headers.GetValues( "Set-Cookie" )
            .Single( cookie => cookie.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        Assert.AreEqual( 0, _oauth.Revocations );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", firstCookie );
        JsonElement firstBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( firstBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( DidValue, firstBrowser.GetProperty( "did" ).GetString( ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", replacedCookie );
        JsonElement replacedBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replacedBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", newCookie );
        JsonElement newBrowser = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( newBrowser.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( OtherDidValue, newBrowser.GetProperty( "did" ).GetString( ) );
    }

    [TestMethod]
    public async Task LogoutBetweenCallbackBindingAndStoreWrite_RejectsInFlightLogin( ) {
        _ = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string callback = await PrepareOAuthCallbackAsync( );
        Options( ).IdentityStore!.Events.OnStoring = async _ => {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( )
                .EndSessionAsync( null, DidValue );
        };

        using HttpResponseMessage response = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, response.Headers.Location! ).AbsolutePath );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", oldCookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( who.GetProperty( "authenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task BrowserLogoutDuringCallback_RejectsStaleReplacementWithoutRotatingAccountStamp( ) {
        _ = await SignInAsync( );
        string oldCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        AuthenticationTicket oldTicket = Options( ).TicketDataFormat.Unprotect( oldCookie.Split( '=', 2 )[1] )!;
        string originalStamp;
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            originalStamp = (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
                .SingleAsync( TestContext.CancellationToken )).SecurityStamp!;
        }
        string callback = await PrepareOAuthCallbackAsync( );
        Options( ).IdentityStore!.Events.OnStoring = async _ => {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndReplacedSessionAsync( oldTicket );
        };

        using HttpResponseMessage response = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, response.Headers.Location! ).AbsolutePath );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        ApplicationUser user = await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .SingleAsync( TestContext.CancellationToken );
        Assert.AreEqual( originalStamp, user.SecurityStamp );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    /// <summary>
    /// A callback that has bound its accounts but not yet written its credential must not survive a deletion
    /// that commits first. The credential write is conditioned on the local user row, so once the deletion has
    /// removed that row the callback's write cannot match and is rejected rather than recreating credentials
    /// for an account that no longer exists.
    ///
    /// The ordering is forced, not raced. A sign-in leaves one stored credential; the callback is then run and
    /// parked inside its credential write — the barrier sits on <see cref="IdentityStoreEvents.OnStoring"/>,
    /// which the store raises after the account binding is verified and before the row is written. The deletion
    /// runs to completion while the callback is parked, and only then is the callback released. Without the
    /// barrier the callback would usually win and this would assert nothing about the ordering.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task DeletionCommittedWhileCallbackIsBinding_RevokesItsGrantAndBlocksTheCallback( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string storedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        string callback = await PrepareOAuthCallbackAsync( );
        TaskCompletionSource callbackStoring = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource releaseCallback = new( TaskCreationOptions.RunContinuationsAsynchronously );
        Func<IdentityStoreSettingContext, Task>? previousOnStoring = store.Events.OnStoring;
        store.Events.OnStoring = setting => {
            _ = callbackStoring.TrySetResult( );
            return releaseCallback.Task.WaitAsync( TimeSpan.FromSeconds( 20 ), TestContext.CancellationToken );
        };
        try {
            // The callback reaches its credential write and parks there, holding the account it bound.
            Task<HttpResponseMessage> inFlight = _client.GetAsync( callback, TestContext.CancellationToken );
            await callbackStoring.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            // The deletion runs to completion while the callback is parked.
            using HttpResponseMessage deleted = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.OK, deleted.StatusCode,
                await deleted.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
            releaseCallback.SetResult( );
            using HttpResponseMessage callbackResponse = await inFlight.WaitAsync( TimeSpan.FromSeconds( 20 ), TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.Redirect, callbackResponse.StatusCode,
                "A callback whose account was deleted underneath it must be rejected." );
            Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, callbackResponse.Headers.Location! ).AbsolutePath );
        } finally {
            _ = releaseCallback.TrySetResult( );
            store.Events.OnStoring = previousOnStoring;
        }

        Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "The rejected callback must not recreate a credential row for the deleted account." );
        Assert.AreEqual( 0, await BlueskyRowCountAsync( ), "No OAuth row may remain for the deleted DID." );
        // The deletion committed while the callback held a grant it had not stored, so the only grant there
        // was to revoke is the one the deletion removed.
        Assert.Contains( storedGrant, _oauth.RevokedRefreshTokens,
            "The deletion must revoke the exact grant whose row it removed." );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 0, await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( )
            .Users.CountAsync( TestContext.CancellationToken ) );
    }

    /// <summary>
    /// The reverse order: a callback that commits its credential before the deletion must not shield the
    /// credential from it. The deletion removes the row the callback stored, revokes that exact grant, and
    /// leaves no row in any of the three OAuth tables.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task DeletionAfterCallbackCommitted_RevokesTheStoredGrantAndLeavesNoRows( ) {
        string callback = await PrepareOAuthCallbackAsync( );
        using HttpResponseMessage callbackResponse = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, callbackResponse.StatusCode );
        string blueskyCookie = callbackResponse.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string committedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        // The identity the callback established is a different one from the before-sign-in state, so a fresh
        // anti-forgery token is needed for the deletion and only the callback's cookie is carried into it.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", blueskyCookie );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage deleted = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, deleted.StatusCode,
            await deleted.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Assert.Contains( committedGrant, _oauth.RevokedRefreshTokens,
            "The deletion must revoke the exact grant the committed callback stored." );
        Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.AreEqual( 0, await BlueskyRowCountAsync( ),
            "Account deletion must remove every OAuth row, including the one the callback stored." );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 0, await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( )
            .Users.CountAsync( TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Callback_RejectsLockedOutLocalAccount( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = new( ) { UserName = "locked-bluesky-user", AtProtoDid = DidValue };
            Assert.IsTrue( (await users.CreateAsync( user )).Succeeded );
            Assert.IsTrue( (await users.SetLockoutEndDateAsync( user, DateTimeOffset.UtcNow.AddHours( 1 ) )).Succeeded );
        }
        string callback = await PrepareOAuthCallbackAsync( );

        using HttpResponseMessage response = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.Redirect, response.StatusCode );
        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, response.Headers.Location! ).AbsolutePath );
        Assert.IsFalse( response.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                        cookies.Any( cookie => cookie.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ) );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 1, await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.CountAsync( TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations, "A rejected grant must be revoked after a trusted authority check." );
    }

    [TestMethod]
    public async Task LockedOutAccount_CannotReuseExistingBlueskySession( ) {
        using HttpResponseMessage login = await SignInAsync( );
        string userId = await LocalUserIdAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = (await users.FindByIdAsync( userId ))!;
            Assert.IsTrue( (await users.SetLockoutEndDateAsync( user, DateTimeOffset.UtcNow.AddHours( 1 ) )).Succeeded );
        }

        using HttpResponseMessage response = await _client.GetAsync( "/test/who", TestContext.CancellationToken );

        JsonElement who = await response.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsFalse( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.Contains( ( string value ) => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ),
            response.Headers.GetValues( "Set-Cookie" ) );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task ConcurrentFirstLoginsForSameDid_UseOneLocalAccount( ) {
        TaskCompletionSource release = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int reached = 0;
        _ = _handles.Setup( h => h.ResolveHandleAsync( It.IsAny<Did>( ), It.IsAny<CancellationToken>( ) ) )
            .Returns( async ( ) => {
                if (Interlocked.Increment( ref reached ) == 2) { release.SetResult( ); }
                await release.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
                return new Handle( "first.example" );
            } );

        Task<HttpResponseMessage> first = _client.PostAsync( "/test/signin", null, TestContext.CancellationToken );
        Task<HttpResponseMessage> second = _client.PostAsync( "/test/signin", null, TestContext.CancellationToken );
        using HttpResponseMessage firstResponse = await first.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
        using HttpResponseMessage secondResponse = await second.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, firstResponse.StatusCode );
        Assert.AreEqual( HttpStatusCode.OK, secondResponse.StatusCode );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 1, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( )
            .Users.CountAsync( user => user.AtProtoDid == DidValue, TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task OAuthCorrelationAndSessionCookie_SurviveApplicationRestart( ) {
        string callback = await PrepareOAuthCallbackAsync( );
        string correlationCookies = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        await RestartApplicationAsync( );
        _client.DefaultRequestHeaders.Add( "Cookie", correlationCookies );

        using HttpResponseMessage signIn = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, signIn.StatusCode );
        Assert.AreEqual( "/", new Uri( _client.BaseAddress!, signIn.Headers.Location! ).AbsolutePath );
        string sessionCookie = signIn.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        Assert.AreEqual( 0L, await CorrelationStateCountAsync( ) );
        string userId = await LocalUserIdAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = (await users.FindByIdAsync( userId ))!;
            user.ApiKeyHash = "retained-api-key-hash";
            Assert.IsTrue( (await users.UpdateAsync( user )).Succeeded );
        }
        await ExpireStoredAccessTokenAsync( );
        int refreshes = _oauth.Refreshes;

        await RestartApplicationAsync( );
        _client.DefaultRequestHeaders.Add( "Cookie", correlationCookies );
        using HttpResponseMessage replay = await _client.GetAsync( callback, TestContext.CancellationToken );
        Assert.AreEqual( "/account/login", new Uri( _client.BaseAddress!, replay.Headers.Location! ).AbsolutePath );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", sessionCookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( userId, who.GetProperty( "userId" ).GetString( ) );
        Assert.AreEqual( DidValue, who.GetProperty( "did" ).GetString( ) );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.IsGreaterThan( refreshes, _oauth.Refreshes );
        ClaimsIdentity persisted = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "rotated-refresh", persisted.FindFirst( AtProtoClaims.RefreshToken )?.Value );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        ApplicationUser saved = (await check.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).FindByIdAsync( userId ))!;
        Assert.AreEqual( "retained-api-key-hash", saved.ApiKeyHash );
    }

    [TestMethod]
    public async Task ConfidentialClient_UsesServedActiveAndRetainedKeysForRefreshAndRevocation( ) {
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage start = await _client.PostAsJsonAsync( "/account/login-atproto",
            new { handle = "first.example" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, start.StatusCode, await start.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        JsonElement startData = await start.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Uri authorizationUri = new( startData.GetProperty( "authorizationUrl" ).GetString( )! );
        string correlationCookie = start.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( Options( ).CorrelationCookie.Name + ".", StringComparison.Ordinal ) ).Split( ';' )[0];
        string requestCookies = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( ) + "; " + correlationCookie;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", requestCookies );
        using HttpClient browser = new( new HttpClientHandler { AllowAutoRedirect = false } );
        using HttpResponseMessage authorization = await browser.GetAsync( authorizationUri, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, authorization.StatusCode );
        using HttpResponseMessage callback = await _client.GetAsync( authorization.Headers.Location!.PathAndQuery, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, callback.StatusCode );
        Assert.AreEqual( "/", new Uri( _client.BaseAddress!, callback.Headers.Location! ).AbsolutePath );
        string sessionCookie = callback.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", sessionCookie );
        Assert.IsGreaterThanOrEqualTo( 2, _oauth.ClientAssertionKeyIds.Count );
        Assert.IsTrue( _oauth.ClientAssertionKeyIds.All( keyId => keyId == _clientSigningKeyId ),
            "The pushed authorization and code exchange for a new session must both use the active key." );

        ClaimsIdentity storedIdentity = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( _clientSigningKeyId, storedIdentity.FindFirst( AtProtoClaims.OAuthClientSigningKeyId )?.Value );
        Claim currentAccessToken = storedIdentity.FindFirst( AtProtoClaims.AccessToken )!;
        Assert.IsNotNull( currentAccessToken );
        storedIdentity.RemoveClaim( currentAccessToken );
        storedIdentity.AddClaim( new Claim( AtProtoClaims.AccessToken, CreateCredentials( DateTime.UtcNow.AddMinutes( -1 ), "expired-refresh" ).AccessJwt ) );

        using HttpResponseMessage rotatedMetadataResponse = await _client.GetAsync( "/oauth-client-metadata.json", TestContext.CancellationToken );
        JsonElement rotatedMetadata = await rotatedMetadataResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        CollectionAssert.AreEquivalent( new[] { _clientSigningKeyId, _previousClientSigningKeyId },
            rotatedMetadata.GetProperty( "jwks" ).GetProperty( "keys" ).EnumerateArray( )
                .Select( key => key.GetProperty( "kid" ).GetString( ) ).ToArray( ) );
        Claim clientSigningKeyId = storedIdentity.FindFirst( AtProtoClaims.OAuthClientSigningKeyId )!;
        Assert.IsNotNull( clientSigningKeyId );
        // Model a persisted session created before rotation; the loaded configuration retains its signing key.
        storedIdentity.RemoveClaim( clientSigningKeyId );
        storedIdentity.AddClaim( new Claim( AtProtoClaims.OAuthClientSigningKeyId, _previousClientSigningKeyId ) );
        await Options( ).IdentityStore!.Update( storedIdentity, TestContext.CancellationToken );

        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsGreaterThan( 0, _oauth.Refreshes );
        Assert.AreEqual( _previousClientSigningKeyId, _oauth.ClientAssertionKeyIds.Last( ),
            "A session bound to the retained previous key must keep using it during refresh." );

        await AddAntiforgeryHeaderAsync( );
        int assertionCountBeforeRevocation = _oauth.ClientAssertionKeyIds.Count;
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
        Assert.AreEqual( _previousClientSigningKeyId, _oauth.ClientAssertionKeyIds.Last( ),
            "Revocation for a session bound to the retained previous key must keep using that key." );
        Assert.IsGreaterThan( assertionCountBeforeRevocation, _oauth.ClientAssertionKeyIds.Count,
            "A new validated client assertion must be observed for the revocation request." );
    }

    [TestMethod]
    public async Task AuthenticationEntryPoints_UseConfiguredOrigin_AndAllowCanonicalHostBehindHttpProxy( ) {
        using HttpResponseMessage wwwLogin = await _client.GetAsync(
            "https://www.bridgebeats.example/account/login?returnUrl=%2Fplaylists", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, wwwLogin.StatusCode );
        Assert.AreEqual( "https://bridgebeats.example/account/login?returnUrl=%2Fplaylists", wwwLogin.Headers.Location?.ToString( ) );
        using HttpResponseMessage wwwLoginSlash = await _client.GetAsync(
            "https://www.bridgebeats.example/account/login/?returnUrl=%2Fplaylists", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, wwwLoginSlash.StatusCode );
        Assert.AreEqual( "https://bridgebeats.example/account/login/?returnUrl=%2Fplaylists", wwwLoginSlash.Headers.Location?.ToString( ) );

        using HttpResponseMessage shortDomainRegister = await _client.GetAsync(
            "https://bridgebeats.link/account/register", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, shortDomainRegister.StatusCode );
        Assert.AreEqual( "https://bridgebeats.example/account/register", shortDomainRegister.Headers.Location?.ToString( ) );
        using HttpResponseMessage shortDomainRegisterSlash = await _client.GetAsync(
            "https://bridgebeats.link/account/register/", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, shortDomainRegisterSlash.StatusCode );
        Assert.AreEqual( "https://bridgebeats.example/account/register/", shortDomainRegisterSlash.Headers.Location?.ToString( ) );

        using HttpResponseMessage proxyLogin = await _client.GetAsync(
            "http://bridgebeats.example/account/login", TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, proxyLogin.StatusCode,
            "The reverse proxy terminates TLS before forwarding the canonical host over HTTP." );

        using HttpResponseMessage wwwPost = await _client.PostAsync(
            "https://www.bridgebeats.example/account/login-atproto", JsonContent.Create( new { handle = "first.example" } ), TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.BadRequest, wwwPost.StatusCode );
        JsonElement response = await wwwPost.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.AreEqual( "https://bridgebeats.example/account/login", response.GetProperty( "loginUrl" ).GetString( ) );

        foreach (string path in new[] { "/account/login/", "/account/register/", "/account/login-atproto/" }) {
            using HttpResponseMessage trailingSlashPost = await _client.PostAsync(
                "https://www.bridgebeats.example" + path, JsonContent.Create( new { handle = "first.example" } ), TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.BadRequest, trailingSlashPost.StatusCode, path );
            JsonElement trailingSlashResponse = await trailingSlashPost.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
            Assert.AreEqual( "https://bridgebeats.example/account/login", trailingSlashResponse.GetProperty( "loginUrl" ).GetString( ), path );
        }
    }

    [TestMethod]
    public async Task BrowserCookieContainer_CarriesSessionAndCorrelationAcrossAliasToOAuthCallback( ) {
        using HttpResponseMessage existingLogin = await SignInAsync( );
        CookieContainer browser = new( );
        AddResponseCookies( browser, _client.BaseAddress!, existingLogin );
        Uri alias = new( "https://www.bridgebeats.example/account/login" );
        Assert.Contains( BlueskyAuthenticationConfiguration.CookieName + "=", browser.GetCookieHeader( alias ) );

        using HttpRequestMessage aliasRequest = new( HttpMethod.Get, alias );
        aliasRequest.Headers.Add( "Cookie", browser.GetCookieHeader( alias ) );
        using HttpResponseMessage aliasResponse = await _client.SendAsync( aliasRequest, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, aliasResponse.StatusCode );
        IEnumerable<string> aliasCookies = aliasResponse.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? returnedCookies )
            ? returnedCookies
            : [];
        string returnedCookieHeaders = string.Join( "\n", aliasCookies );
        Assert.DoesNotContain( Options( ).CorrelationCookie.Name + ".", returnedCookieHeaders, StringComparison.Ordinal );
        Assert.DoesNotContain( ".AspNetCore.Antiforgery.", returnedCookieHeaders, StringComparison.Ordinal );
        Uri canonical = new( _client.BaseAddress!, aliasResponse.Headers.Location! );
        Assert.AreEqual( "bridgebeats.example", canonical.Host );
        using HttpRequestMessage canonicalRequest = new( HttpMethod.Get, canonical );
        canonicalRequest.Headers.Add( "Cookie", browser.GetCookieHeader( canonical ) );
        using HttpResponseMessage canonicalResponse = await _client.SendAsync( canonicalRequest, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, canonicalResponse.StatusCode );

        Uri antiforgeryUri = new( _client.BaseAddress!, "/account/antiforgery-token" );
        using HttpRequestMessage antiforgeryRequest = new( HttpMethod.Get, antiforgeryUri );
        antiforgeryRequest.Headers.Add( "Cookie", browser.GetCookieHeader( antiforgeryUri ) );
        using HttpResponseMessage antiforgeryResponse = await _client.SendAsync( antiforgeryRequest, TestContext.CancellationToken );
        AddResponseCookies( browser, antiforgeryUri, antiforgeryResponse );
        JsonElement antiforgery = await antiforgeryResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );

        Uri startUri = new( _client.BaseAddress!, "/account/login-atproto" );
        using HttpRequestMessage startRequest = new( HttpMethod.Post, startUri ) {
            Content = JsonContent.Create( new { handle = "first.example" } )
        };
        startRequest.Headers.Add( "Cookie", browser.GetCookieHeader( startUri ) );
        startRequest.Headers.Add( "X-XSRF-TOKEN", antiforgery.GetProperty( "token" ).GetString( )! );
        using HttpResponseMessage startResponse = await _client.SendAsync( startRequest, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, startResponse.StatusCode, await startResponse.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        string correlationCookie = startResponse.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( Options( ).CorrelationCookie.Name + ".", StringComparison.Ordinal ) );
        string[] correlationAttributes = correlationCookie.Split( ';', StringSplitOptions.TrimEntries );
        Assert.DoesNotContain( "Domain=", string.Join( ";", correlationAttributes ), StringComparison.OrdinalIgnoreCase,
            "OAuth correlation cookies must remain host-only." );
        Assert.Contains( "Secure", correlationAttributes, StringComparer.OrdinalIgnoreCase );
        Assert.Contains( "HttpOnly", correlationAttributes, StringComparer.OrdinalIgnoreCase );
        AddResponseCookies( browser, startUri, startResponse );
        JsonElement start = await startResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Uri authorizationUri = new( start.GetProperty( "authorizationUrl" ).GetString( )! );

        using HttpClient provider = new( new HttpClientHandler { AllowAutoRedirect = false } );
        using HttpResponseMessage authorization = await provider.GetAsync( authorizationUri, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, authorization.StatusCode );
        Uri callbackUri = new( _client.BaseAddress!, authorization.Headers.Location!.PathAndQuery );
        using HttpRequestMessage callbackRequest = new( HttpMethod.Get, callbackUri );
        callbackRequest.Headers.Add( "Cookie", browser.GetCookieHeader( callbackUri ) );
        using HttpResponseMessage callback = await _client.SendAsync( callbackRequest, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, callback.StatusCode );
        Assert.AreEqual( "/", new Uri( _client.BaseAddress!, callback.Headers.Location! ).AbsolutePath );
        AddResponseCookies( browser, callbackUri, callback );

        Uri whoUri = new( _client.BaseAddress!, "/test/who" );
        using HttpRequestMessage whoRequest = new( HttpMethod.Get, whoUri );
        whoRequest.Headers.Add( "Cookie", browser.GetCookieHeader( whoUri ) );
        using HttpResponseMessage whoResponse = await _client.SendAsync( whoRequest, TestContext.CancellationToken );
        JsonElement who = await whoResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    /// <summary>
    /// Login start is rate limited to 60 requests per address per minute, so the 61st request from the same
    /// address is rejected with 429. The boundary is asserted between the two requests rather than by sending a
    /// burst: the limit is exact, and a test that only checks "some request eventually gets a 429" passes even
    /// when the limiter is set far from its configured value.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task LoginStart_IsRateLimitedToSixtyRequestsPerMinute( ) {
        await AddAntiforgeryHeaderAsync( );
        for (int request = 1; request <= 60; request++) {
            using HttpResponseMessage allowed = await _client.PostAsJsonAsync( "/account/login-atproto",
                new { handle = "first.example" }, TestContext.CancellationToken );
            Assert.AreEqual( HttpStatusCode.OK, allowed.StatusCode,
                $"Request {request} is within the 60-per-minute allowance and must succeed, not be rejected " +
                $"for any other reason: {await allowed.Content.ReadAsStringAsync( TestContext.CancellationToken )}" );
        }

        using HttpResponseMessage limited = await _client.PostAsJsonAsync( "/account/login-atproto",
            new { handle = "first.example" }, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.TooManyRequests, limited.StatusCode,
            "The 61st login start from one address within the window must be rejected by the rate limiter." );
    }

    [TestMethod]
    public async Task LoginStart_TimeoutCancellationReturnsControlledFailure( ) {
        Options( ).CorrelationStateCache!.Events.OnStoring = _ =>
            Task.FromException( new OperationCanceledException( "The OAuth operation timed out." ) );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage result = await _client.PostAsJsonAsync( "/account/login-atproto",
            new { handle = "first.example" }, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.BadRequest, result.StatusCode );
        JsonElement response = await result.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.AreEqual( "Failed to start login. Please check your handle and try again.", response.GetProperty( "message" ).GetString( ) );
        Assert.IsEmpty( _actionExceptionObserver.Exceptions );
    }

    [TestMethod]
    public async Task LoginStart_ClientAbortPropagatesCancellation( ) {
        _serverRequestAborted = new CancellationTokenSource( );
        Options( ).CorrelationStateCache!.Events.OnStoring = _ => {
            _serverRequestAborted.Cancel( );
            return Task.FromException( new OperationCanceledException( "The client disconnected." ) );
        };
        await AddAntiforgeryHeaderAsync( );
        Task<HttpResponseMessage> request = _client.PostAsJsonAsync( "/account/login-atproto",
            new { handle = "first.example" }, TestContext.CancellationToken );

        await ObserveAbortedRequestAsync( request );
        _serverRequestAborted.Dispose( );
        _serverRequestAborted = null;
    }

    [TestMethod]
    public async Task Callback_TimeoutCancellationReturnsControlledFailure( ) {
        string callback = await PrepareOAuthCallbackAsync( );
        Options( ).CorrelationStateCache!.Events.OnRetrieved = _ =>
            Task.FromException( new OperationCanceledException( "The OAuth callback operation timed out." ) );

        using HttpResponseMessage result = await _client.GetAsync( callback, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.Redirect, result.StatusCode );
        Uri callbackFailureLocation = new( _client.BaseAddress!, result.Headers.Location! );
        Assert.AreEqual( "/account/login", callbackFailureLocation.AbsolutePath );
        Assert.AreEqual( "Bluesky login failed. Please try again.",
            QueryHelpers.ParseQuery( callbackFailureLocation.Query )["error"].ToString( ) );
        Assert.IsEmpty( _actionExceptionObserver.Exceptions );
        Assert.IsFalse( result.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                        cookies.Any( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ) );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.CountAsync( TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Callback_ClientAbortPropagatesCancellation( ) {
        string callback = await PrepareOAuthCallbackAsync( );
        _serverRequestAborted = new CancellationTokenSource( );
        Options( ).CorrelationStateCache!.Events.OnRetrieved = _ => {
            _serverRequestAborted.Cancel( );
            return Task.FromException( new OperationCanceledException( "The client disconnected." ) );
        };
        Task<HttpResponseMessage> request = _client.GetAsync( callback, TestContext.CancellationToken );

        Exception actionException = await _actionExceptionObserver.WaitForExceptionAsync( ).WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
        _ = Assert.IsInstanceOfType<OperationCanceledException>( actionException );
        await ObserveAbortedRequestAsync( request );
        _serverRequestAborted.Dispose( );
        _serverRequestAborted = null;
    }

    [TestMethod]
    public async Task Metadata_DeclaresIdentityAndOnlyPlaylistPermissions( ) {
        JsonElement metadata = await _client.GetFromJsonAsync<JsonElement>( "/oauth-client-metadata.json", TestContext.CancellationToken );
        Assert.AreEqual( "https://bridgebeats.example/oauth-client-metadata.json", metadata.GetProperty( "client_id" ).GetString( ) );
        Assert.AreEqual( "BridgeBeats", metadata.GetProperty( "client_name" ).GetString( ) );
        Assert.AreEqual( "https://bridgebeats.example", metadata.GetProperty( "client_uri" ).GetString( ) );
        Assert.AreEqual( "atproto repo:link.bridgebeats.playlist", metadata.GetProperty( "scope" ).GetString( ) );
        Assert.AreEqual( "private_key_jwt", metadata.GetProperty( "token_endpoint_auth_method" ).GetString( ) );
        Assert.AreEqual( "https://bridgebeats.example/account/atproto-callback", metadata.GetProperty( "redirect_uris" )[0].GetString( ) );
        JsonElement keys = metadata.GetProperty( "jwks" ).GetProperty( "keys" );
        Assert.AreEqual( 2, keys.GetArrayLength( ) );
        CollectionAssert.AreEquivalent( new[] { _clientSigningKeyId, _previousClientSigningKeyId },
            keys.EnumerateArray( ).Select( key => key.GetProperty( "kid" ).GetString( ) ).ToArray( ) );
        foreach (JsonElement key in keys.EnumerateArray( )) {
            Assert.IsFalse( key.TryGetProperty( "d", out _ ), "Client metadata must never publish private key material." );
        }
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );

        using HttpResponseMessage replacementLogin = await SignInAsync( );
        string replacementCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string replacementId = await LocalUserIdAsync( );
        Assert.AreNotEqual( originalId, replacementId );
        int revocations = _oauth.Revocations;
        int refreshes = _oauth.Refreshes;
        await ExpireStoredAccessTokenAsync( );

        if (expired) {
            AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( oldCookie.Split( '=', 2 )[1] )!;
            ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes( -1 );
            oldCookie = BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket );
        }

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        using HttpResponseMessage freshLogin = await SignInAsync( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        Assert.Contains( BlueskyAuthenticationConfiguration.CookieName + "=", browser.GetCookieHeader( new Uri( "https://www.bridgebeats.example/" ) ) );
        Assert.AreEqual( "", browser.GetCookieHeader( new Uri( "https://unrelated.example" ) ) );
        if (hasRole) {
            await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
            RoleManager<IdentityRole> roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>( );
            Assert.IsTrue( (await roles.CreateAsync( new IdentityRole( Roles.AspireDashboardAccess ) )).Succeeded );
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser user = (await users.FindByIdAsync( await LocalUserIdAsync( ) ))!;
            Assert.IsTrue( (await users.AddToRoleAsync( user, Roles.AspireDashboardAccess )).Succeeded );
        }
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", renewedCookie );
        JsonElement current = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( current.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( who.GetProperty( "userId" ).GetString( ), current.GetProperty( "userId" ).GetString( ) );
    }

    [TestMethod]
    public async Task Logout_WhenCookieIsDueForRenewal_DoesNotRestoreEndedSession( ) {
        using HttpResponseMessage signedIn = await SignInAsync( );
        string cookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( cookie.Split( '=', 2 )[1] )!;
        ticket.Properties.IssuedUtc = DateTimeOffset.UtcNow.AddDays( -5 );
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays( 2 );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        string[] blueskyCookies = [.. logout.Headers.GetValues( "Set-Cookie" )
            .Where( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) )];
        Assert.IsNotEmpty( blueskyCookies );
        Assert.IsTrue( blueskyCookies.All( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
            "No renewed Bluesky cookie may follow the deletion." );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( who.GetProperty( "authenticated" ).GetBoolean( ) );
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
        string[] pending = [.. await db.Database.GetPendingMigrationsAsync( TestContext.CancellationToken )];
        CollectionAssert.AreEqual( new[] { "20261026053018_UseBlueskyAuthenticationStores" }, pending );
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
        command.CommandText = """
                              SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN
                              ('idunno_bluesky_identities', 'idunno_bluesky_refresh_locks',
                               'idunno_bluesky_correlation_states', 'bridgebeats_bluesky_sessions')
                              """;
        Assert.AreEqual( 4L, await command.ExecuteScalarAsync( TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Migration_SdkTablesMatchPinnedPackageSchema( ) {
        Assert.AreEqual( new Version( 8, 0, 0, 0 ), typeof( SqliteIdentityStore ).Assembly.GetName( ).Version,
            "A new SDK version needs an explicit schema and credential-store review." );
        string sdkSchema = await File.ReadAllTextAsync( Path.Combine( AppContext.BaseDirectory, "bluesky-sdk-schema.sql" ),
            TestContext.CancellationToken );
        await using SqliteConnection expected = new( "Data Source=:memory:" );
        await expected.OpenAsync( TestContext.CancellationToken );
        await using (SqliteCommand create = expected.CreateCommand( )) {
            create.CommandText = sdkSchema;
            _ = await create.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        await db.Database.OpenConnectionAsync( TestContext.CancellationToken );
        SqliteConnection actual = (SqliteConnection)db.Database.GetDbConnection( );

        async Task<Dictionary<string, string>> ReadSdkObjectsAsync( SqliteConnection connection ) {
            Dictionary<string, string> objects = new( StringComparer.Ordinal );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE name LIKE 'idunno_bluesky_%' AND sql IS NOT NULL";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync( TestContext.CancellationToken );
            while (await reader.ReadAsync( TestContext.CancellationToken )) {
                string sql = string.Join( ' ', reader.GetString( 1 ).Split( (char[]?)null, StringSplitOptions.RemoveEmptyEntries ) );
                objects.Add( reader.GetString( 0 ), sql );
            }
            return objects;
        }

        Dictionary<string, string> packageObjects = await ReadSdkObjectsAsync( expected );
        Dictionary<string, string> migrationObjects = await ReadSdkObjectsAsync( actual );
        Assert.HasCount( packageObjects.Count, migrationObjects );
        foreach ((string name, string packageSql) in packageObjects) {
            Assert.IsTrue( migrationObjects.TryGetValue( name, out string? migrationSql ), $"Missing SDK object: {name}" );
            if (name == "idunno_bluesky_identities") {
                migrationSql = migrationSql!.Replace( "\"ProofKeyHash\" TEXT COLLATE BINARY, ", string.Empty, StringComparison.Ordinal );
            }
            Assert.AreEqual( packageSql, migrationSql, $"The migration diverged from the SDK schema for {name}." );
        }
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
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndSessionAsync( null, DidValue );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    /// <summary>
    /// The grant presented for revocation must be the one whose row the removal deleted, not a value an
    /// earlier read of the DID happened to return. A credential stored between such a read and the delete
    /// would otherwise keep working while an already-replaced grant was revoked in its place.
    ///
    /// The removal performs no separate read to race against: it deletes the row with one statement and
    /// reports what that statement removed, so there is no read-then-delete window for a writer to slip into.
    /// The replacement here is stored in full before the removal runs, so the two do not overlap, and what
    /// this establishes is that the removal reports the row standing when it ran rather than a value captured
    /// before the replacement — the earlier value is what a read-then-delete shape would have reported, and
    /// the assertion below rejects it if so. A removal forced to overlap the storing of a replacement is
    /// covered by <see cref="EndAllSessions_KeepsAReplacementStoredWhileTheRemovalWasInFlight"/>, and the
    /// revocation the reported row drives is checked exactly in
    /// <see cref="RemoveLinkedSession_RevokesTheDeletedRowExactlyAndNotTheReplacedGrant"/>.
    /// </summary>
    [TestMethod]
    public async Task RemoveCurrentAsync_ReportsTheCredentialRowItDeleted( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string replacedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        // The row the DID holds when the removal runs, which is not the row any earlier read would have seen.
        ClaimsIdentity replacement = IIdentityStore.BuildClaimsIdentity(
            CreateCredentials( DateTime.UtcNow.AddHours( 2 ), "replacement-grant" ) );
        await store.Update( replacement, TestContext.CancellationToken );

        ClaimsIdentity? removed = await store.ReadCredentialAsync(
            await store.RemoveCurrentAsync( new Did( DidValue ), TestContext.CancellationToken ), DidValue );

        Assert.IsNotNull( removed, "Removal must report the row it deleted." );
        Assert.AreEqual( "replacement-grant", removed.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "Removal must report the row it deleted, not an earlier read of the same DID." );
        Assert.AreNotEqual( replacedGrant, removed.FindFirst( AtProtoClaims.RefreshToken )!.Value );
        Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    /// <summary>
    /// Ending the session for a DID whose credential row was replaced must revoke the replacement grant and
    /// only that one. The replacement is stored in full before the session ends, so this fixes which grant is
    /// current when the removal runs without overlapping the two. It is the negative control for the
    /// reported-row assertions above: it checks the tokens that actually reached the revocation endpoint, so a
    /// removal that reported the right row but revoked a different grant — or revoked both — is caught here
    /// even though the reported bytes looked correct. A replacement stored while the removal is in flight is
    /// covered by <see cref="EndAllSessions_KeepsAReplacementStoredWhileTheRemovalWasInFlight"/>.
    /// </summary>
    [TestMethod]
    public async Task RemoveLinkedSession_RevokesTheDeletedRowExactlyAndNotTheReplacedGrant( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string replacedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 2 ), "replacement-grant" );

        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndSessionAsync( null, DidValue );

        Assert.Contains( "replacement-grant", _oauth.RevokedRefreshTokens,
            "Revocation must present the grant of the row the removal deleted." );
        Assert.DoesNotContain( replacedGrant, _oauth.RevokedRefreshTokens,
            "Revocation must not present the grant the removal replaced." );
        Assert.HasCount( 1, _oauth.RevokedRefreshTokens,
            "The removal deleted one row, so exactly one grant may be revoked for it." );
    }

    /// <summary>
    /// A replacement credential stored while the global session removal is in flight must survive both that
    /// removal and the revocation the removal drives. The row the removal deletes belongs to the grant that
    /// was stored when it ran; a grant stored after that is a later, still-valid session, and deleting or
    /// revoking it signs its browser out for nothing.
    ///
    /// The ordering is forced, not raced. The removal is parked inside the restore of the row it deleted —
    /// the barrier is a one-shot gate on <see cref="IdentityStoreEvents.OnRetrieved"/>, which the store raises
    /// when it turns the returned bytes back into a credential, after the deleting statement has committed —
    /// the replacement is then stored in full with its own browser session, and only then is the removal
    /// released to revoke. A removal that read the DID before deleting it instead deletes the row this test
    /// stores as the replacement, which the row assertions below reject.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EndAllSessions_KeepsAReplacementStoredWhileTheRemovalWasInFlight( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        string deletedGrant = await StoreReplacementWhileRemovalIsInFlightAsync( store, ( ) =>
            scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndSessionAsync( null, DidValue ) );

        AssertRevocationTargetedOnlyTheDeletedRow( deletedGrant );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The replacement credential row must remain stored after the removal completed." );
        ClaimsIdentity? persisted = await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken );
        Assert.IsNotNull( persisted, "The replacement stored while the removal was in flight must remain stored." );
        Assert.AreEqual( "replacement-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The credential left stored must be the replacement, not the row the removal deleted." );
    }

    /// <summary>
    /// The same interleaving through the path a browser takes. Ending the last browser's session removes its
    /// session row and, with no other active browser left, the credential row shared by the DID. This is a
    /// separate removal from the global one — the session row it deletes is chosen by session, and the
    /// credential row goes only when no active browser remains — so it is forced through the same barrier.
    /// The replacement's browser session is asserted to be in place afterwards, because the row and the
    /// session are what leave the storing browser usable.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task EndBrowserSession_KeepsAReplacementStoredWhileTheRemovalWasInFlight( ) {
        using HttpResponseMessage signedIn = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect(
            _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( ).Split( '=', 2 )[1] )!;
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        string deletedGrant = await StoreReplacementWhileRemovalIsInFlightAsync( store, ( ) =>
            scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndReplacedSessionAsync( ticket ) );

        AssertRevocationTargetedOnlyTheDeletedRow( deletedGrant );
        string? sessionId = await StoredBrowserSessionIdAsync( );
        Assert.IsNotNull( sessionId, "The browser session that stored the replacement must remain stored." );
        Assert.IsTrue( await store.IsBrowserSessionCurrentAsync( new Did( DidValue ), sessionId, TestContext.CancellationToken ),
            "The browser that stored the replacement must still hold a current session." );
        ClaimsIdentity? persisted = await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken );
        Assert.IsNotNull( persisted, "The replacement stored while the removal was in flight must remain stored." );
        Assert.AreEqual( "replacement-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The credential left stored must be the replacement, not the row the removal deleted." );
    }

    [TestMethod]
    public async Task RemoveBrowserSessionAsync_ReportsTheCredentialRowItDeleted( ) {
        using HttpResponseMessage signedIn = await SignInAsync( );
        AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect(
            _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( ).Split( '=', 2 )[1] )!;
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string storedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;

        ClaimsIdentity? removed = await store.ReadCredentialAsync(
            await store.RemoveBrowserSessionAsync( new Did( DidValue ),
                ticket.Properties.GetString( BlueskyAccountEvents.BrowserSessionProperty )!, TestContext.CancellationToken ),
            DidValue );

        Assert.IsNotNull( removed, "Ending the last browser must report the shared credential it deleted." );
        Assert.AreEqual( storedGrant, removed.FindFirst( AtProtoClaims.RefreshToken )!.Value );
        Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    /// <summary>Ending one browser session must not report the shared credential as removed.</summary>
    [TestMethod]
    public async Task RemoveBrowserSessionAsync_ReportsNothingWhileAnotherBrowserRemains( ) {
        _ = await SignInAsync( );
        string firstSession = Options( ).TicketDataFormat.Unprotect(
            _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( ).Split( '=', 2 )[1] )!
            .Properties.GetString( BlueskyAccountEvents.BrowserSessionProperty )!;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;

        byte[]? removed = await store.RemoveBrowserSessionAsync( new Did( DidValue ), firstSession, TestContext.CancellationToken );

        Assert.IsNull( removed, "A browser that still shares the credential must not trigger revocation." );
        Assert.IsNotNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
    }

    /// <summary>
    /// Logout must present the refresh token of the stored row, so the test asserts the exact grant rather
    /// than that some revocation occurred.
    /// </summary>
    [TestMethod]
    public async Task Logout_RevokesTheStoredGrantExactly( ) {
        _ = await SignInAsync( );
        string storedGrant = (await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        await AddAntiforgeryHeaderAsync( );

        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        Assert.Contains( storedGrant, _oauth.RevokedRefreshTokens,
            "Logout must revoke the grant held for the removed row." );
    }

    /// <summary>
    /// A caller holding older tokens must not overwrite newer stored credentials. AT Proto refresh tokens
    /// are single use, so restoring the older pair would reinstate a spent refresh token and sign the user
    /// out at the next refresh.
    /// </summary>
    [TestMethod]
    public async Task UpdateIfNewer_DoesNotReplaceNewerStoredCredentials( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Assert.IsTrue( await store.UpdateIfNewer(
            CreateCredentials( DateTime.UtcNow.AddHours( 2 ), "newer-grant" ), TestContext.CancellationToken ) );

        bool written = await store.UpdateIfNewer(
            CreateCredentials( DateTime.UtcNow.AddHours( 1 ), "stale-grant" ), TestContext.CancellationToken );

        Assert.IsFalse( written, "A write carrying older credentials must report that nothing was written." );
        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "newer-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value );
    }

    /// <summary>
    /// Credentials written through the store's own update path are refused when the stored row belongs to
    /// a different OAuth grant. A caller holding tokens from before a replacement grant was stored carries
    /// the same DID, and a long enough expiry it can read from the store, so neither the DID nor the expiry
    /// distinguishes it from the current row. Without the proof-key comparison such a write returns true
    /// while reinstating credentials whose refresh token the service has already spent.
    /// </summary>
    [TestMethod]
    public async Task UpdateIfNewer_RefusesCredentialsFromAReplacedGrant( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string replacedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        string replacedProofKey = _proofKey;
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 3 ), "replacement-grant" );
        // The superseded grant's tokens, presented late but carrying an expiry the stored row has not passed,
        // so the proof-key comparison is the only rule that can refuse this write.
        DPoPAccessCredentials stale = CreateCredentials(
            DateTime.UtcNow.AddHours( 9 ), replacedGrant, proofKey: replacedProofKey );

        bool written = await store.UpdateIfNewer( stale, TestContext.CancellationToken );

        Assert.IsFalse( written, "Credentials from a replaced grant must not be written." );
        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "replacement-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The replacement grant must survive a late write from the grant it replaced." );
    }

    /// <summary>
    /// The agent raises credential updates through the interface member rather than the store method, so a
    /// write from a replaced grant must be refused there too. This exercises the path the SDK calls.
    /// </summary>
    [TestMethod]
    public async Task OnCredentialsUpdated_RefusesCredentialsFromAReplacedGrant( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        string replacedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        string replacedProofKey = _proofKey;
        _proofKey = ATProtoSigningKeyProvider.GenerateNewSigningKeyJwk( );
        await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 3 ), "replacement-grant" );
        IIdentityStore sdkStore = store;
        DPoPAccessCredentials stale = CreateCredentials(
            DateTime.UtcNow.AddHours( 9 ), replacedGrant, proofKey: replacedProofKey );

        await sdkStore.OnCredentialsUpdated(
            new idunno.AtProto.Events.CredentialsUpdatedEventArgs( new Did( DidValue ), new Uri( _service ), stale ),
            TestContext.CancellationToken );

        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "replacement-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The credential-update path the agent calls must not reinstate a replaced grant." );
    }

    /// <summary>
    /// The SDK delivers credential updates through <see cref="IIdentityStore.OnCredentialsUpdated"/>, so a
    /// stale update must be refused on that path too. This exercises the interface member the agent calls
    /// rather than the store method directly.
    /// </summary>
    [TestMethod]
    public async Task OnCredentialsUpdated_DoesNotReplaceNewerStoredCredentials( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Assert.IsTrue( await store.UpdateIfNewer(
            CreateCredentials( DateTime.UtcNow.AddHours( 2 ), "newer-grant" ), TestContext.CancellationToken ) );
        IIdentityStore sdkStore = store;
        DPoPAccessCredentials stale = CreateCredentials( DateTime.UtcNow.AddHours( 1 ), "stale-grant" );

        await sdkStore.OnCredentialsUpdated(
            new idunno.AtProto.Events.CredentialsUpdatedEventArgs( new Did( DidValue ), new Uri( _service ), stale ),
            TestContext.CancellationToken );

        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "newer-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The credential-update path the agent calls must not reinstate older tokens." );
    }

    /// <summary>
    /// The SDK raises a credential update for a rotated DPoP nonce as well as for a token refresh, and each
    /// such update carries an expiry the stored row has already reached. One carrying an expiry equal to the
    /// stored row's must still be stored, so the comparison that refuses a stale write cannot refuse on
    /// "not later than" alone. This writes a distinct grant at the stored row's exact expiry, which is the
    /// expiry relation a nonce rotation presents; the rotation itself is covered through the credential-update
    /// path the agent calls by
    /// <see cref="OnCredentialsUpdated_WritesNonceOnlyRotationCarryingTheSameExpiry"/>.
    /// </summary>
    [TestMethod]
    public async Task UpdateIfNewer_WritesCredentialsCarryingTheSameExpiry( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        ClaimsIdentity stored = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.IsTrue( AtProtoCredential.TryCreate( stored, out DPoPAccessCredentials? current ) );
        DPoPAccessCredentials sameExpiry = CreateCredentials(
            current!.ExpiresOn.UtcDateTime, "same-expiry-grant" );

        bool written = await store.UpdateIfNewer( sameExpiry, TestContext.CancellationToken );

        Assert.IsTrue( written, "An update carrying the same expiry must still be stored." );
        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "same-expiry-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value );
    }

    /// <summary>
    /// A nonce-only rotation arrives through the same store member as a token refresh, carrying the very same
    /// access token and expiry and differing only in the DPoP nonce. The other tests call the store's
    /// <c>UpdateIfNewer</c> directly, but the agent raises these updates through
    /// <see cref="IIdentityStore.OnCredentialsUpdated"/>, so this covers the path the SDK actually calls and
    /// asserts the rotated nonce reaches the stored row rather than being dropped.
    /// </summary>
    [TestMethod]
    public async Task OnCredentialsUpdated_WritesNonceOnlyRotationCarryingTheSameExpiry( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Assert.IsTrue( AtProtoCredential.TryCreate(
            (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!,
            out DPoPAccessCredentials? current ) );
        DPoPAccessCredentials rotatedNonce = new(
            current!.Service, current.AccessJwt, current.RefreshToken, current.DPoPProofKey, "rotated-nonce" );
        IIdentityStore sdkStore = store;

        await sdkStore.OnCredentialsUpdated(
            new idunno.AtProto.Events.CredentialsUpdatedEventArgs( new Did( DidValue ), new Uri( _service ), rotatedNonce ),
            TestContext.CancellationToken );

        // The persisted nonce proves the update reached the stored row: it differs from the nonce the row started
        // from, so the row has to be re-read to observe the change at all. The unchanged refresh token proves the
        // nonce-only rotation preserved the grant it was issued for rather than replacing it.
        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.IsTrue( AtProtoCredential.TryCreate( persisted, out DPoPAccessCredentials? saved ) );
        Assert.AreEqual( "rotated-nonce", saved!.DPoPNonce,
            "A nonce-only rotation delivered through the credential-update path must reach the stored row." );
        Assert.AreEqual( current.RefreshToken, saved.RefreshToken,
            "A nonce-only rotation must not replace the grant it was issued for." );
        Assert.AreEqual( current.ExpiresOn, saved.ExpiresOn,
            "A nonce-only rotation carries the same expiry." );
    }

    /// <summary>
    /// A writer that read the stored row and then had a newer credential committed under it must not put its
    /// own row back. The two writers here share a proof key, so the proof-key comparison cannot separate them
    /// and the decision rests entirely on the store's compare-and-set: the write names the row it compared, and
    /// the transaction is what evaluates that name.
    ///
    /// The interleaving is forced rather than waited for. The older writer is parked inside its comparison —
    /// the barrier sits on <see cref="IdentityStoreEvents.OnRetrieved"/>, which the store raises after it has
    /// read the row, with the row bytes already in hand — the newer credential is committed in full, and only
    /// then is the older writer released to attempt its write against the row it no longer matches. Without the
    /// barrier the older writer would usually finish first and overwrite, and the assertion below would be
    /// measuring the race rather than the guard.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task UpdateIfNewer_CannotReplaceARowThatChangedAfterItWasRead( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        TaskCompletionSource comparedOldRow = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource releaseOlderWriter = new( TaskCreationOptions.RunContinuationsAsynchronously );
        // Set only while the older writer is the sole writer in flight, and cleared by the gate itself on the
        // first call, so the newer writer's comparison is never held by this barrier.
        bool olderWriterIsComparing = false;
        // Both writers carry an expiry later than the stored row's, so the stored-expiry comparison cannot
        // refuse either of them and the row-identity guard is the only rule left that can. The older writer
        // holds the row it compared; the newer one commits over it.
        DPoPAccessCredentials older = CreateCredentials( DateTime.UtcNow.AddHours( 9 ), "stale-same-proof-grant" );
        DPoPAccessCredentials newer = CreateCredentials( DateTime.UtcNow.AddHours( 10 ), "newer-same-proof-grant" );
        Func<IdentityStoreRetrievedContext, Task>? previousOnRetrieved = store.Events.OnRetrieved;
        store.Events.OnRetrieved = _ => {
            if (!olderWriterIsComparing) { return Task.CompletedTask; }
            olderWriterIsComparing = false;
            comparedOldRow.SetResult( );
            return releaseOlderWriter.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
        };
        try {
            olderWriterIsComparing = true;
            Task<bool> olderWrite = store.UpdateIfNewer( older, TestContext.CancellationToken );
            await comparedOldRow.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );

            // The newer credential commits while the older writer holds the row it compared above.
            Assert.IsTrue( await store.UpdateIfNewer( newer, TestContext.CancellationToken ),
                "The newer credential must be stored while the older writer is parked." );
            releaseOlderWriter.SetResult( );

            Assert.IsFalse( await olderWrite.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken ),
                "A write whose comparison row was replaced mid-flight must report that nothing was written." );
        } finally {
            store.Events.OnRetrieved = previousOnRetrieved;
        }

        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "newer-same-proof-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The credential committed after the older writer's comparison must survive it." );
        Assert.IsTrue( AtProtoCredential.TryCreate( persisted, out DPoPAccessCredentials? saved ) );
        Assert.AreEqual( newer.ExpiresOn, saved!.ExpiresOn,
            "The exact credential stored must be the newer one, not the older writer's pair." );
    }

    /// <summary>
    /// Two credential writes that overlap must leave the later expiry stored. The barrier here sits on
    /// <see cref="IdentityStoreEvents.OnStoring"/>, which the store raises before it reads the stored row, so
    /// this overlaps the two writers without fixing which of them reads its comparison row first. The
    /// interleaving that actually needs forcing — an older writer that has already read the row it intends to
    /// replace, with a newer writer committing in between — is covered by
    /// <see cref="UpdateIfNewer_CannotReplaceARowThatChangedAfterItWasRead"/>.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ConcurrentCredentialWrites_LeaveTheLaterExpiryStored( ) {
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        TaskCompletionSource bothReading = new( TaskCreationOptions.RunContinuationsAsynchronously );
        int reached = 0;
        Options( ).IdentityStore!.Events.OnStoring = async _ => {
            if (Interlocked.Increment( ref reached ) == 2) { bothReading.SetResult( ); }
            await bothReading.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
        };

        Task<bool> newer = store.UpdateIfNewer( CreateCredentials( DateTime.UtcNow.AddHours( 3 ), "newer-grant" ), TestContext.CancellationToken );
        Task<bool> stale = store.UpdateIfNewer( CreateCredentials( DateTime.UtcNow.AddHours( 1 ), "stale-grant" ), TestContext.CancellationToken );
        _ = await Task.WhenAll( newer, stale ).WaitAsync( TimeSpan.FromSeconds( 15 ), TestContext.CancellationToken );

        ClaimsIdentity persisted = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Assert.AreEqual( "newer-grant", persisted.FindFirst( AtProtoClaims.RefreshToken )!.Value,
            "The later expiry must win whichever write reached the store first." );
    }

    /// <summary>
    /// A failed account deletion must leave the stored credential in place and must not revoke it. The
    /// credential rows and the user row commit or roll back together, and revocation follows the commit.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DeleteAccount_WhenUserRowDeletionFails_RollsBackCredentialsAndSkipsRevocation( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            _ = await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_user_deletion BEFORE DELETE ON AspNetUsers
                BEGIN SELECT RAISE(ABORT, 'injected user deletion failure'); END;
                """, TestContext.CancellationToken );
        }

        try {
            using HttpResponseMessage response = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );
            Assert.AreNotEqual( HttpStatusCode.OK, response.StatusCode,
                "A deletion whose user row could not be removed must not report success." );
        } catch (Exception exception) when (ContainsInjectedDeletionFailure( exception )) {
            // TestServer can surface the application exception directly. EF reports the aborted
            // DELETE as a DbUpdateException wrapping the SQLite error.
        }

        // "Not OK" alone does not show the trigger was reached: a request rejected before the action runs also
        // reports "not OK". The recorded failure message is what proves the DELETE was attempted and aborted.
        Assert.Contains( ContainsInjectedDeletionFailure, _actionExceptionObserver.Exceptions,
            "The deletion request must reach the injected trigger; otherwise this test asserts nothing about " +
            "the rollback." );

        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "A failed deletion must leave the stored credential in place." );
        Assert.AreEqual( 0, _oauth.Revocations,
            "A failed deletion must not revoke a credential that is still in use." );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 1, await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( )
            .Users.CountAsync( TestContext.CancellationToken ) );
    }

    /// <summary>
    /// After a successful deletion the DID must have no credential row, and no further credential can be
    /// stored for it because the store conditions the write on the local user row.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task DeleteAccount_LeavesNoCredentialRowAndBlocksLaterStoresForTheDid( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage deletion = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, deletion.StatusCode );

        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        IHttpContextAccessor accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>( );
        HttpContext? previousContext = accessor.HttpContext;
        DefaultHttpContext request = new( ) { RequestServices = scope.ServiceProvider };
        request.Items[BridgeBeatsIdentityStore.ExpectedStampKey] = "stamp-for-a-deleted-account";
        request.Items[BridgeBeatsIdentityStore.PendingBrowserSessionKey] =
            new BridgeBeatsIdentityStore.PendingBrowserSession( DidValue, "late-callback-session", DateTimeOffset.UtcNow.AddDays( 7 ) );
        accessor.HttpContext = request;
        try {
            _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>( ( ) => store.Add(
                IIdentityStore.BuildClaimsIdentity( CreateCredentials( DateTime.UtcNow.AddHours( 1 ) ) ), TestContext.CancellationToken ) );
        } finally {
            accessor.HttpContext = previousContext;
        }

        Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "A callback arriving after deletion must not leave a credential row behind." );
    }

    /// <summary>
    /// A host with Bluesky sign-in disabled still deletes the stored OAuth rows for a linked DID. The rows
    /// can predate disabling the feature, and the resolver for the OAuth service returns nothing here, so a
    /// deletion path that only removes rows when that service is registered leaves live credentials behind
    /// for an account that no longer exists.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task DeleteAccount_WithoutOAuthService_RemovesEveryRowForTheLinkedDid( ) {
        await StartApplicationWithoutOAuthAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage registration = await _client.PostAsJsonAsync( "/account/register",
            new { email = "linked@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, registration.StatusCode );
        SetIdentityCookie( registration );
        // The token issued before sign-in is bound to the anonymous request, so a fresh one is requested
        // once the registration cookie is in place.
        await AddAntiforgeryHeaderAsync( );
        await SeedLinkedBlueskyRowsAsync( );

        using HttpResponseMessage deletion = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, deletion.StatusCode,
            await deletion.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Assert.AreEqual( 0, await BlueskyRowCountAsync( ) );
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 0, await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( )
            .Users.CountAsync( TestContext.CancellationToken ) );
    }

    /// <summary>
    /// The same deletion with the user row refusing to be removed must leave every OAuth row in place, so a
    /// failed deletion cannot strip credentials that are still in use.
    ///
    /// The failure has to be demonstrated rather than assumed. A deletion request whose anti-forgery token no
    /// longer matches the signed-in identity is rejected with 400 before the action runs, and that 400 satisfies
    /// a plain "not OK" assertion while the credential rows were never touched — the test would pass with the
    /// rollback removed. So the anti-forgery token is reissued after the identity cookie is set, the injected
    /// trigger is confirmed to abort the DELETE inside the transaction, and each of the three OAuth tables is
    /// counted separately rather than as one total.
    /// </summary>
    [TestMethod]
    [Timeout( 60000, CooperativeCancellation = true )]
    public async Task DeleteAccount_WithoutOAuthService_RollsBackRowsWhenUserDeletionFails( ) {
        await StartApplicationWithoutOAuthAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage registration = await _client.PostAsJsonAsync( "/account/register",
            new { email = "linked@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, registration.StatusCode );
        SetIdentityCookie( registration );
        // SetIdentityCookie replaces the cookie collection with the identity cookie, which drops the
        // anti-forgery cookie issued for the anonymous request. A token bound to that identity has to be
        // issued now, or the deletion request is rejected with 400 before the action runs and the row
        // assertions below never see the rollback they exist to check.
        await AddAntiforgeryHeaderAsync( );
        await SeedLinkedBlueskyRowsAsync( );
        await AssertLinkedBlueskyRowsAsync( "before the deletion" );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            _ = await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_user_deletion BEFORE DELETE ON AspNetUsers
                BEGIN SELECT RAISE(ABORT, 'injected user deletion failure'); END;
                """, TestContext.CancellationToken );
        }

        HttpResponseMessage? deletion = null;
        try {
            deletion = await _client.PostAsync( "/account/delete", null, TestContext.CancellationToken );
            Assert.AreNotEqual( HttpStatusCode.OK, deletion.StatusCode,
                "A deletion whose user row could not be removed must not report success." );
        } catch (Exception exception) when (ContainsInjectedDeletionFailure( exception )) {
            // TestServer can surface the application exception from the request itself.
        } finally {
            deletion?.Dispose( );
        }

        // The trigger must actually have aborted the DELETE. A request rejected before the action runs — a
        // 400 from anti-forgery validation, for instance — also reports "not OK" without ever opening the
        // account-deletion transaction, so the failure message itself is the evidence that the rollback ran.
        // The action filter records what the action threw; authorization filters run before it.
        Assert.Contains( ContainsInjectedDeletionFailure, _actionExceptionObserver.Exceptions,
            "The deletion request must reach the injected trigger. A request rejected before the action runs " +
            "(for example by anti-forgery validation) leaves the rollback untested however 'not OK' it looks." );

        // Each table is counted on its own: the credential row is what the rollback protects, and the browser
        // session and refresh lock are the rows a partial removal would strand. A combined total could hide a
        // credential row that came back while a session row was lost.
        await AssertLinkedBlueskyRowsAsync( "after the rolled-back deletion" );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.AreEqual( 1, await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( )
            .Users.CountAsync( TestContext.CancellationToken ) );
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

    [TestMethod]
    [DataRow( "new@example.com", "short" )]
    [DataRow( "new!account@example.com", "ValidPassword123!" )]
    public async Task Registration_WhenIdentityValidationFails_PreservesBlueskySession( string email, string password ) {
        _ = await SignInAsync( );
        string userId = await LocalUserIdAsync( );
        await AddAntiforgeryHeaderAsync( );
        int revocations = _oauth.Revocations;

        using HttpResponseMessage response = await _client.PostAsJsonAsync( "/account/register",
            new { email, password }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.BadRequest, response.StatusCode );
        Assert.AreEqual( revocations, _oauth.Revocations );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.AreEqual( userId, who.GetProperty( "userId" ).GetString( ) );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.IsFalse( await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .AnyAsync( user => user.Email == email, TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Registration_WhenBlueskyRemovalFails_DoesNotCommitNewAccount( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            _ = await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_bluesky_removal BEFORE DELETE ON idunno_bluesky_identities
                BEGIN SELECT RAISE(ABORT, 'injected identity-store removal failure'); END;
                """, TestContext.CancellationToken );
        }

        try {
            using HttpResponseMessage response = await _client.PostAsJsonAsync( "/account/register",
                new { email = "should-not-exist@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
            Assert.AreNotEqual( HttpStatusCode.OK, response.StatusCode );
        } catch (SqliteException exception) when (exception.Message.Contains( "injected identity-store removal failure", StringComparison.Ordinal )) {
            // TestServer can surface the application exception directly.
        }

        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.IsFalse( await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .AnyAsync( user => user.Email == "should-not-exist@example.com", TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task Registration_WhenBlueskyReadFails_RemovesSessionAndCreatesAccount( ) {
        _ = await SignInAsync( );
        await AddAntiforgeryHeaderAsync( );
        var originalOnRetrieved = Options( ).IdentityStore!.Events.OnRetrieved;
        _actionExceptionObserver.BeforeAction = ( ) =>
            Options( ).IdentityStore!.Events.OnRetrieved = _ =>
                Task.FromException( new InvalidOperationException( "injected identity-store read failure" ) );

        using HttpResponseMessage response = await _client.PostAsJsonAsync( "/account/register",
            new { email = "registered-after-read-failure@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, response.StatusCode );
        _actionExceptionObserver.BeforeAction = null;
        Options( ).IdentityStore!.Events.OnRetrieved = originalOnRetrieved;
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        await using AsyncServiceScope check = _app.Services.CreateAsyncScope( );
        Assert.IsTrue( await check.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .AnyAsync( user => user.Email == "registered-after-read-failure@example.com", TestContext.CancellationToken ) );
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
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            string identityName = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
                .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
            _client.DefaultRequestHeaders.Add( "Cookie", context.Response.Headers.SetCookie
                .Last( value => value!.StartsWith( identityName + "=", StringComparison.Ordinal ) )!.Split( ';' )[0] );
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
        Assert.AreEqual( BlueskyAuthenticationConfiguration.LegacyIdentityCookieName, cookieName );
        string cookie = result.Headers.GetValues( "Set-Cookie" ).Last( c => c.StartsWith( cookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        var cookieOptions = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme );
        AuthenticationTicket issuedTicket = cookieOptions.TicketDataFormat.Unprotect( cookie.Split( '=', 2 )[1] )!;
        Assert.AreEqual( "1", issuedTicket.Properties.GetString( "BridgeBeats.ValidatedPasswordCookie" ) );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        using HttpResponseMessage firstRequest = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        JsonElement who = await firstRequest.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( await LocalUserIdAsync( ), who.GetProperty( "userId" ).GetString( ) );
        Assert.IsFalse( who.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.IsFalse( firstRequest.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? firstUpdates ) &&
                        firstUpdates.Any( value => value.StartsWith( cookieName + "=", StringComparison.Ordinal ) ) );
        using HttpResponseMessage nextRequest = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( nextRequest.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? updates ) &&
                        updates.Any( value => value.StartsWith( cookieName + "=", StringComparison.Ordinal ) ) );
    }

    [TestMethod]
    public async Task IdentityLogout_DeletesLegacyHostOnlyAndCurrentDomainCookies( ) {
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage registration = await _client.PostAsJsonAsync( "/account/register",
            new { email = "legacy-cookie@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, registration.StatusCode );
        string name = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        string issued = registration.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( name + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        CookieContainer browser = new( );
        AddResponseCookies( browser, _client.BaseAddress!, registration );
        browser.SetCookies( _client.BaseAddress!, issued + "; Path=/; Secure; HttpOnly" );
        using HttpRequestMessage tokenRequest = new( HttpMethod.Get, "/account/antiforgery-token" );
        tokenRequest.Headers.Add( "Cookie", browser.GetCookieHeader( _client.BaseAddress! ) );
        using HttpResponseMessage tokenResponse = await _client.SendAsync( tokenRequest, TestContext.CancellationToken );
        AddResponseCookies( browser, _client.BaseAddress!, tokenResponse );
        JsonElement token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        using HttpRequestMessage logoutRequest = new( HttpMethod.Post, "/account/logout" );
        logoutRequest.Headers.Add( "Cookie", browser.GetCookieHeader( _client.BaseAddress! ) );
        logoutRequest.Headers.Add( "X-XSRF-TOKEN", token.GetProperty( "token" ).GetString( )! );
        using HttpResponseMessage logout = await _client.SendAsync( logoutRequest, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode );
        string[] deletions = [.. logout.Headers.GetValues( "Set-Cookie" ).Where( value => value.StartsWith( name + "=;", StringComparison.Ordinal ) )];
        Assert.Contains( ( string value ) => value.Contains( "domain=", StringComparison.OrdinalIgnoreCase ), deletions );
        Assert.Contains( ( string value ) => !value.Contains( "domain=", StringComparison.OrdinalIgnoreCase ), deletions );
        AddResponseCookies( browser, _client.BaseAddress!, logout );
        Assert.DoesNotContain( name + "=", browser.GetCookieHeader( _client.BaseAddress! ) );
        Assert.DoesNotContain( name + "=", browser.GetCookieHeader( new Uri( "https://www.bridgebeats.example/" ) ) );
    }

    [TestMethod]
    public async Task SwitchingToBluesky_DeletesLegacyHostOnlyIdentityCookie( ) {
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage registration = await _client.PostAsJsonAsync( "/account/register",
            new { email = "switching-cookie@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, registration.StatusCode );
        string name = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        string issued = registration.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( name + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        CookieContainer browser = new( );
        AddResponseCookies( browser, _client.BaseAddress!, registration );
        browser.SetCookies( _client.BaseAddress!, issued + "; Path=/; Secure; HttpOnly" );
        using HttpRequestMessage switchRequest = new( HttpMethod.Post, "/test/signin" );
        switchRequest.Headers.Add( "Cookie", browser.GetCookieHeader( _client.BaseAddress! ) );

        using HttpResponseMessage switched = await _client.SendAsync( switchRequest, TestContext.CancellationToken );

        Assert.AreEqual( HttpStatusCode.OK, switched.StatusCode );
        string[] deletions = [.. switched.Headers.GetValues( "Set-Cookie" ).Where( value => value.StartsWith( name + "=;", StringComparison.Ordinal ) )];
        Assert.Contains( ( string value ) => value.Contains( "domain=", StringComparison.OrdinalIgnoreCase ), deletions );
        Assert.Contains( ( string value ) => !value.Contains( "domain=", StringComparison.OrdinalIgnoreCase ), deletions );
        AddResponseCookies( browser, _client.BaseAddress!, switched );
        string cookies = browser.GetCookieHeader( _client.BaseAddress! );
        Assert.DoesNotContain( name + "=", cookies );
        Assert.Contains( BlueskyAuthenticationConfiguration.CookieName + "=", cookies );
    }

    [TestMethod]
    [DataRow( "https://bridgebeats.example" )]
    [DataRow( "bridgebeats.example/" )]
    public async Task DomainUpgrade_RejectsOldIdentityCookieOnAliasHost( string configuredDomain ) {
        _configuredDomain = configuredDomain;
        await RestartApplicationAsync( );
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage registration = await _client.PostAsJsonAsync( "/account/register",
            new { email = "upgraded-cookie@example.com", password = "ValidPassword123!" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, registration.StatusCode );
        string upgradedName = _app.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        Assert.AreEqual( BlueskyAuthenticationConfiguration.UpgradedIdentityCookieName, upgradedName );
        string upgradedCookie = registration.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( upgradedName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        string oldCookie = BlueskyAuthenticationConfiguration.LegacyIdentityCookieName + "=" + upgradedCookie.Split( '=', 2 )[1];
        Uri alias = new( "https://www.bridgebeats.example/test/who" );
        using HttpRequestMessage oldRequest = new( HttpMethod.Get, alias );
        oldRequest.Headers.Add( "Cookie", oldCookie );
        using HttpResponseMessage oldResponse = await _client.SendAsync( oldRequest, TestContext.CancellationToken );
        JsonElement oldWho = await oldResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsFalse( oldWho.GetProperty( "authenticated" ).GetBoolean( ) );

        using HttpRequestMessage upgradedRequest = new( HttpMethod.Get, alias );
        upgradedRequest.Headers.Add( "Cookie", upgradedCookie );
        using HttpResponseMessage upgradedResponse = await _client.SendAsync( upgradedRequest, TestContext.CancellationToken );
        JsonElement upgradedWho = await upgradedResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsTrue( upgradedWho.GetProperty( "authenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task RevocationUnavailable_StillRemovesLinkedSession( ) {
        _ = await SignInAsync( );
        _oauth.RevocationFails = true;
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        await scope.ServiceProvider.GetRequiredService<BlueskySessionService>( ).EndSessionAsync( null, DidValue );
        Assert.IsNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.IsGreaterThan( 0, _oauth.Revocations );
    }

    private static bool ContainsInjectedDeletionFailure( Exception exception ) {
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            if (current.Message.Contains( "injected user deletion failure", StringComparison.Ordinal )) { return true; }
        }
        return false;
    }

    private async Task AddAntiforgeryHeaderAsync( ) {
        // Antiforgery's cookie is separate from the SDK reference cookie.
        using HttpResponseMessage tokenResponse = await _client.GetAsync( "/account/antiforgery-token", TestContext.CancellationToken );
        JsonElement data = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        string cookies = _client.DefaultRequestHeaders.TryGetValues( "Cookie", out IEnumerable<string>? existing )
            ? existing.Single( ) + "; " : "";
        cookies += tokenResponse.Headers.GetValues( "Set-Cookie" )
            .Single( c => c.StartsWith( ".AspNetCore.Antiforgery.", StringComparison.Ordinal ) ).Split( ';' )[0];
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookies );
        _ = _client.DefaultRequestHeaders.Remove( "X-XSRF-TOKEN" );
        _client.DefaultRequestHeaders.Add( "X-XSRF-TOKEN", data.GetProperty( "token" ).GetString( ) );
    }

    /// <summary>
    /// Adds the antiforgery header set while the request is authenticated by the internal-service scheme. The
    /// <c>X-Service-Key</c> header is left in place so the following POST is authenticated by the same scheme
    /// the antiforgery token was issued under (antiforgery binds the token to the principal name).
    /// </summary>
    private async Task AddAntiforgeryHeaderWithServiceKeyAsync( ) {
        _ = _client.DefaultRequestHeaders.Remove( "X-Service-Key" );
        _client.DefaultRequestHeaders.Add( "X-Service-Key", "test-service-key" );
        await AddAntiforgeryHeaderAsync( );
    }

    /// <summary>
    /// Signs in a second real browser and returns its cookie. The shared credential row is reused; each
    /// browser gets its own session row, so both cookies are independently usable.
    /// </summary>
    private async Task<string> SignInSecondBrowserAsync( ) {
        string existing = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        try {
            return CookieValue( await SignInAsync( ) );
        } finally {
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", existing );
        }
    }

    /// <summary>Extracts the bare Bluesky reference-cookie value from a sign-in response.</summary>
    private static string CookieValue( HttpResponseMessage response ) =>
        response.Headers.GetValues( "Set-Cookie" )
            .Last( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) )
            .Split( ';' )[0].Split( '=', 2 )[1];

    /// <summary>The browser session id a reference cookie names. Accepts the bare value or the full Name=value pair.</summary>
    private string SessionIdOf( string cookieOrValue ) {
        string value = cookieOrValue.Contains( '=' ) ? cookieOrValue.Split( '=', 2 )[1] : cookieOrValue;
        return Options( ).TicketDataFormat.Unprotect( value )!
            .Properties.GetString( BlueskyAccountEvents.BrowserSessionProperty )!;
    }

    /// <summary>The exact encrypted blob in the credential row the DID currently holds.</summary>
    private async Task<byte[]> StoredBlobAsync( ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand read = connection.CreateCommand( );
        read.CommandText = "SELECT \"Identity\" FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did;";
        _ = read.Parameters.AddWithValue( "@did", DidValue );
        return (byte[])(await read.ExecuteScalarAsync( TestContext.CancellationToken ))!;
    }

    private async Task AssertBlobEqualsAsync( byte[] expected ) {
        byte[] actual = await StoredBlobAsync( );
        Assert.AreEqual( Convert.ToHexString( expected ), Convert.ToHexString( actual ),
            "The stored credential blob must be exactly the replacement's protected bytes." );
    }

    private async Task RestartApplicationAsync( ) {
        _client.Dispose( );
        await _app.DisposeAsync( );
        await StartApplicationAsync( );
    }

    /// <summary>
    /// Rebuilds the host with Bluesky sign-in left out of the service collection, which is how a deployment
    /// that never configured OAuth signing keys runs.
    /// </summary>
    private async Task StartApplicationWithoutOAuthAsync( ) {
        _client.Dispose( );
        await _app.DisposeAsync( );
        _blueskyEnabled = false;
        await StartApplicationAsync( );
        Assert.IsNull( _app.Services.GetService<BlueskySessionService>( ),
            "The OAuth service must be absent for this test to cover the disabled-feature path." );
    }

    /// <summary>Sends the identity cookie a registration or password login issued on later requests.</summary>
    private void SetIdentityCookie( HttpResponseMessage response ) {
        string name = _app.Services
            .GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>( )
            .Get( IdentityConstants.ApplicationScheme ).Cookie.Name!;
        string cookie = response.Headers.GetValues( "Set-Cookie" )
            .Last( value => value.StartsWith( name + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        // Only the identity cookie is kept. A token requested later is bound to this identity, and the
        // antiforgery cookie issued for the anonymous request before sign-in must not shadow the fresh one.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
    }

    /// <summary>
    /// Stores <paramref name="refresh"/> as the credential row for the linked DID the way a sign-in does,
    /// so the row carries the current proof key and registers the browser session that stores it.
    /// </summary>
    private async Task AddReplacementCredentialsAsync( BridgeBeatsIdentityStore store, DateTime expires, string refresh ) {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope( );
        ApplicationUser user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
            .SingleAsync( TestContext.CancellationToken );
        // A sign-in always mints a new browser session, so this must not reuse one that already has a row.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        IHttpContextAccessor accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>( );
        HttpContext? previousContext = accessor.HttpContext;
        DefaultHttpContext request = new( ) { RequestServices = scope.ServiceProvider };
        request.Items[BridgeBeatsIdentityStore.ExpectedStampKey] = user.SecurityStamp!;
        request.Items[BridgeBeatsIdentityStore.PendingBrowserSessionKey] =
            new BridgeBeatsIdentityStore.PendingBrowserSession( DidValue, sessionId, DateTimeOffset.UtcNow.AddDays( 7 ) );
        accessor.HttpContext = request;
        try {
            await store.Add( IIdentityStore.BuildClaimsIdentity( CreateCredentials( expires, refresh ) ),
                TestContext.CancellationToken );
        } finally {
            accessor.HttpContext = previousContext;
        }
    }

    /// <summary>
    /// Writes the OAuth rows a working sign-in would have left behind, then links the local account to the
    /// same DID, reproducing a deployment whose accounts were linked while the feature was enabled.
    /// </summary>
    private async Task SeedLinkedBlueskyRowsAsync( ) {
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
            ApplicationUser user = await db.Users.SingleAsync( TestContext.CancellationToken );
            user.AtProtoDid = DidValue;
            _ = await db.SaveChangesAsync( TestContext.CancellationToken );
        }
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand seed = connection.CreateCommand( );
        seed.CommandText = """
                           INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                           VALUES (@did, X'00', @expires, @proof);
                           INSERT INTO "bridgebeats_bluesky_sessions" ("SessionId", "Did", "ExpiresAtUtcTicks")
                           VALUES ('disabled-oauth-session', @did, @expires);
                           INSERT INTO "idunno_bluesky_refresh_locks" ("Did", "LockToken", "ExpiresAtUtcTicks")
                           VALUES (@did, 'disabled-oauth-lock', @expires);
                           """;
        _ = seed.Parameters.AddWithValue( "@did", DidValue );
        _ = seed.Parameters.AddWithValue( "@proof", "disabled-oauth-proof" );
        _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
        _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
    }

    /// <summary>
    /// Runs <paramref name="endSession"/> against a credential row that is replaced while the removal is in
    /// flight, and returns the refresh token of the grant the removal deleted.
    ///
    /// The removal is parked inside the restore of the row it deleted. <see cref="IdentityStoreEvents.OnRetrieved"/>
    /// is raised there, from <c>BridgeBeatsIdentityStore.ReadCredentialAsync</c>, and it fires once for each
    /// row the removal turns back into a credential. The gate is armed for exactly one call and disarms itself
    /// on that call, so the replacement's own storing — which raises the same event while reading the row it
    /// compares — is never held by this barrier. The replacement is then committed in full, including its
    /// browser session row, and the removal is released to revoke.
    ///
    /// A removal that read the credential before deleting it would have taken the grant in place when it read,
    /// which is the grant stored before the replacement, and its deleting statement would then remove the
    /// replacement row. Both are rejected by the assertions the callers make.
    /// </summary>
    private async Task<string> StoreReplacementWhileRemovalIsInFlightAsync(
        BridgeBeatsIdentityStore store, Func<Task> endSession ) {
        string deletedGrant = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!
            .FindFirst( AtProtoClaims.RefreshToken )!.Value;
        TaskCompletionSource removalIsRestoringTheDeletedRow = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource releaseRemoval = new( TaskCreationOptions.RunContinuationsAsynchronously );
        // Armed before the removal starts and disarmed by the gate itself on its first call, so only the
        // removal waits here and the replacement's own retrieval passes straight through.
        bool armed = false;
        Func<IdentityStoreRetrievedContext, Task>? previousOnRetrieved = store.Events.OnRetrieved;
        try {
            store.Events.OnRetrieved = retrieved => {
                if (!armed) { return Task.CompletedTask; }
                armed = false;
                _ = removalIsRestoringTheDeletedRow.TrySetResult( );
                return releaseRemoval.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            };
            armed = true;
            Task removal = endSession( );
            await removalIsRestoringTheDeletedRow.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );

            // The replacement commits in full while the removal holds the row it deleted, so it is stored,
            // complete with its own browser session, before the removal is allowed to revoke.
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 2 ), "replacement-grant" );
            releaseRemoval.SetResult( );

            await removal.WaitAsync( TimeSpan.FromSeconds( 20 ), TestContext.CancellationToken );
        } finally {
            _ = releaseRemoval.TrySetResult( );
            store.Events.OnRetrieved = previousOnRetrieved;
        }
        return deletedGrant;
    }

    /// <summary>
    /// Asserts exactly one grant was revoked and that it was the one the removal deleted, not the grant the
    /// replacement carries. The revoked tokens are read from the requests the fake authorization server
    /// actually received, so a removal that reported the right row but revoked another grant, or revoked both,
    /// fails here.
    /// </summary>
    private void AssertRevocationTargetedOnlyTheDeletedRow( string deletedGrant ) {
        Assert.Contains( deletedGrant, _oauth.RevokedRefreshTokens,
            "Revocation must present the grant of the row the removal deleted." );
        Assert.DoesNotContain( "replacement-grant", _oauth.RevokedRefreshTokens,
            "The grant stored while the removal was in flight must not be revoked." );
        Assert.HasCount( 1, _oauth.RevokedRefreshTokens,
            "The removal deleted one row, so exactly one grant may be revoked for it." );
    }

    /// <summary>Reads the browser session id the DID currently has stored, or null when it has none.</summary>
    private async Task<string?> StoredBrowserSessionIdAsync( ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand read = connection.CreateCommand( );
        read.CommandText = """
                           SELECT "SessionId" FROM "bridgebeats_bluesky_sessions"
                           WHERE "Did" = @did AND "ExpiresAtUtcTicks" > @now LIMIT 1;
                           """;
        _ = read.Parameters.AddWithValue( "@did", DidValue );
        _ = read.Parameters.AddWithValue( "@now", DateTime.UtcNow.Ticks );
        return await read.ExecuteScalarAsync( TestContext.CancellationToken ) as string;
    }

    /// <summary>Reads every browser session id the DID currently has stored.</summary>
    private async Task<IReadOnlyList<string>> StoredBrowserSessionIdsAsync( ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand read = connection.CreateCommand( );
        read.CommandText = """
                           SELECT "SessionId" FROM "bridgebeats_bluesky_sessions"
                           WHERE "Did" = @did AND "ExpiresAtUtcTicks" > @now;
                           """;
        _ = read.Parameters.AddWithValue( "@did", DidValue );
        _ = read.Parameters.AddWithValue( "@now", DateTime.UtcNow.Ticks );
        List<string> ids = new( );
        await using SqliteDataReader reader = await read.ExecuteReaderAsync( TestContext.CancellationToken );
        while (await reader.ReadAsync( TestContext.CancellationToken )) {
            ids.Add( reader.GetString( 0 ) );
        }
        return ids;
    }

    /// <summary>
    /// Asserts each of the three seeded OAuth tables still holds its row for the linked DID. The rows are
    /// counted individually so a partial removal — a credential row gone while a session row remains, or the
    /// reverse — cannot pass as "the expected total is still there".
    /// </summary>
    private async Task AssertLinkedBlueskyRowsAsync( string when ) {
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            $"The credential row must be present {when}." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            $"The browser session row must be present {when}." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_refresh_locks" ),
            $"The refresh lock row must be present {when}." );
    }

    /// <summary>Counts the rows for the linked DID in one of the three OAuth tables.</summary>
    private async Task<int> LinkedBlueskyRowCountAsync( string table ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand count = connection.CreateCommand( );
        count.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE \"Did\" = @did;";
        _ = count.Parameters.AddWithValue( "@did", DidValue );
        return Convert.ToInt32( await count.ExecuteScalarAsync( TestContext.CancellationToken ), CultureInfo.InvariantCulture );
    }

    /// <summary>Counts every stored row that belongs to the linked DID across the three OAuth tables.</summary>
    private async Task<int> BlueskyRowCountAsync( ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand count = connection.CreateCommand( );
        count.CommandText = """
                            SELECT (SELECT COUNT(*) FROM "idunno_bluesky_identities" WHERE "Did" = @did) +
                                   (SELECT COUNT(*) FROM "bridgebeats_bluesky_sessions" WHERE "Did" = @did) +
                                   (SELECT COUNT(*) FROM "idunno_bluesky_refresh_locks" WHERE "Did" = @did);
                            """;
        _ = count.Parameters.AddWithValue( "@did", DidValue );
        return Convert.ToInt32( await count.ExecuteScalarAsync( TestContext.CancellationToken ), CultureInfo.InvariantCulture );
    }

    private static void AddResponseCookies( CookieContainer browser, Uri requestUri, HttpResponseMessage response ) {
        if (response.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies )) {
            foreach (string cookie in cookies) {
                browser.SetCookies( requestUri, cookie );
            }
        }
    }

    private static async Task ObserveAbortedRequestAsync( Task<HttpResponseMessage> request ) {
        try {
            using HttpResponseMessage response = await request.WaitAsync( TimeSpan.FromSeconds( 10 ) );
            Assert.AreNotEqual( HttpStatusCode.OK, response.StatusCode,
                "A server-side RequestAborted cancellation must not be converted into a successful response." );
        } catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) {
        }
    }

    private async Task<string> PrepareOAuthCallbackAsync( ) {
        await AddAntiforgeryHeaderAsync( );
        using HttpResponseMessage start = await _client.PostAsJsonAsync( "/account/login-atproto",
            new { handle = "first.example" }, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, start.StatusCode );
        JsonElement data = await start.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Uri authorizationUri = new( data.GetProperty( "authorizationUrl" ).GetString( )! );
        string correlationCookie = start.Headers.GetValues( "Set-Cookie" )
            .Single( value => value.StartsWith( Options( ).CorrelationCookie.Name + ".", StringComparison.Ordinal ) ).Split( ';' )[0];
        string requestCookies = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( ) + "; " + correlationCookie;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", requestCookies );

        using HttpClient browser = new( new HttpClientHandler { AllowAutoRedirect = false } );
        using HttpResponseMessage authorization = await browser.GetAsync( authorizationUri, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.Redirect, authorization.StatusCode );
        return authorization.Headers.Location!.PathAndQuery;
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
        _ = response.EnsureSuccessStatusCode( );
        string cookie = response.Headers.GetValues( "Set-Cookie" ).Last( c => c.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) ).Split( ';' )[0];
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        return response;
    }
    private async Task ExpireStoredAccessTokenAsync( ) {
        IIdentityStore store = Options( ).IdentityStore!;
        ClaimsIdentity identity = (await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ))!;
        Claim access = identity.FindFirst( AtProtoClaims.AccessToken )!;
        identity.RemoveClaim( access );
        identity.AddClaim( new Claim( access.Type, CreateCredentials( DateTime.UtcNow.AddMinutes( -1 ) ).AccessJwt,
            access.ValueType, access.Issuer ) );
        await store.Update( identity, TestContext.CancellationToken );
    }

    private DPoPAccessCredentials CreateCredentials( DateTime expires, string refresh = "initial-refresh",
        string did = DidValue, string? proofKey = null, string nonce = "test-nonce" ) {
        string jwt = new JwtSecurityTokenHandler( ).WriteToken( new JwtSecurityToken(
            issuer: _service, audience: $"did:web:{new Uri( _service ).Host}", claims: [new Claim( "sub", did ), new Claim( "scope", "atproto repo:link.bridgebeats.playlist" )],
            notBefore: expires.AddHours( -1 ), expires: expires ) );
        return new DPoPAccessCredentials( new Uri( _service ), jwt, refresh, proofKey ?? _proofKey, nonce );
    }

    [TestMethod]
    public async Task MixedSchemeLogout_WithServiceKeyAuthenticatedRequest_RemovesBrowserAndKeepsSharedGrant( ) {
        _ = await SignInAsync( );
        string browserOneCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        string browserOneSession = SessionIdOf( browserOneCookie );
        string browserTwoCookie = await SignInSecondBrowserAsync( );
        string browserTwoSession = SessionIdOf( browserTwoCookie );
        Assert.AreNotEqual( browserOneSession, browserTwoSession, "Each browser must hold a distinct session." );
        // Expire the shared access token and take provider discovery down. The logout's explicit Bluesky
        // AuthenticateAsync then fails at refresh (thrown discovery error), which is exactly the case where
        // the verified ticket (set before the refresh attempt) must still authorize browser removal. The SDK
        // does not Remove the identity on a thrown refresh failure, so the second browser and shared grant
        // survive; reverting the ticket gate to `result.Succeeded` must fail this test.
        await ExpireStoredAccessTokenAsync( );
        _oauth.ProtectedResourceDiscoveryFails = true;
        int revocationsBefore = _oauth.Revocations;

        // Both antiforgery acquisition and the logout POST are authenticated by the internal-service scheme,
        // not the Bluesky cookie scheme. The logged-out cookie still authorizes browser removal.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", browserOneCookie );
        await AddAntiforgeryHeaderWithServiceKeyAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        _ = _client.DefaultRequestHeaders.Remove( "X-Service-Key" );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Assert.AreEqual( revocationsBefore, _oauth.Revocations,
            "The shared credential must not be revoked while another browser still uses it." );
        // The logout response must delete the reference cookie and must not renew it (no non-empty value).
        string[] logoutCookies = [.. logout.Headers.GetValues( "Set-Cookie" )
            .Where( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) )];
        Assert.HasCount( 1, logoutCookies, "Logout must emit exactly one Bluesky Set-Cookie (the deletion)." );
        Assert.IsTrue( logoutCookies.Single( ).StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ),
            "Logout must not renew the reference cookie." );

        // The requesting browser row is gone; the other browser row and the credential remain.
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "Only the requesting browser row is removed; the other browser row remains." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ) );
        Assert.AreEqual( browserTwoSession, await StoredBrowserSessionIdAsync( ),
            "The surviving browser row must be browser two." );

        // Recovery before replay: with discovery working again, the old cookie is rejected and cannot remove
        // the remaining session; the other cookie still authenticates the agent and the local user.
        _oauth.ProtectedResourceDiscoveryFails = false;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", browserOneCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + browserTwoCookie );
        JsonElement other = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( other.GetProperty( "authenticated" ).GetBoolean( ),
            "The other browser must still authenticate after the first browser logs out." );
        Assert.IsTrue( other.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The shared credential must remain usable for the surviving browser." );
    }

    [TestMethod]
    public async Task MixedSchemeLogout_SingleBrowser_RemovesFinalLocalIdentity( ) {
        // With only one browser, the mixed-scheme logout is the final local removal: the credential row and
        // the browser row are deleted. Remote revocation is best effort and, with provider discovery down,
        // cannot complete here, so only the local removal is asserted.
        _ = await SignInAsync( );
        string cookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        await ExpireStoredAccessTokenAsync( );
        _oauth.ProtectedResourceDiscoveryFails = true;

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        await AddAntiforgeryHeaderWithServiceKeyAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        _ = _client.DefaultRequestHeaders.Remove( "X-Service-Key" );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        // Discovery is failing, so the best-effort remote revoke cannot complete. The final identity removal
        // (credential + browser rows) is what the mixed-scheme single-browser case must prove.
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The final browser removal must delete the credential row." );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "The final browser removal must delete the browser row." );
        _oauth.ProtectedResourceDiscoveryFails = false;
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsFalse( replay.GetProperty( "agentAuthenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    [DataRow( "unprotectable" )]
    [DataRow( "expired" )]
    [DataRow( "stale-stamp" )]
    [DataRow( "stale-session" )]
    public async Task MixedSchemeLogout_OldReferenceCookie_CannotRemoveNewerSession( string variant ) {
        // Negative matrix: an unprotectable, expired, or stale (stamp/session) reference cookie must not
        // remove the NEWER current session. Each variant first establishes the old-reference state, then a
        // real, distinct current sign-in, then attempts a mixed-scheme logout carrying only the old
        // reference. The newer session row, its identity, and its authentication must all survive, and no
        // grant may be revoked beyond what the newer sign-in itself already revoked.
        string cookieName = BlueskyAuthenticationConfiguration.CookieName;
        string? oldReferenceCookie = null;

        switch (variant) {
            case "unprotectable":
                // No prior state needed; the old reference is a garbage cookie.
                oldReferenceCookie = cookieName + "=not-a-valid-ticket";
                break;
            case "expired": {
                    // A real prior session whose ticket is aged past expiry.
                    _ = await SignInAsync( );
                    string priorCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
                    string priorValue = priorCookie.Split( '=', 2 )[1];
                    AuthenticationTicket ticket = Options( ).TicketDataFormat.Unprotect( priorValue )!;
                    AuthenticationProperties aged = new( ticket.Properties.Items ) {
                        IssuedUtc = DateTimeOffset.UtcNow.AddDays( -10 ),
                        ExpiresUtc = DateTimeOffset.UtcNow.AddDays( -3 )
                    };
                    oldReferenceCookie = cookieName + "=" + Options( ).TicketDataFormat.Protect(
                        new AuthenticationTicket( ticket.Principal, aged, ticket.AuthenticationScheme ) );
                    break;
                }
            case "stale-stamp": {
                    // A real prior session whose ticket predates a security-stamp rotation.
                    _ = await SignInAsync( );
                    string priorCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
                    oldReferenceCookie = priorCookie;
                    await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
                        ApplicationUser user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.SingleAsync( TestContext.CancellationToken );
                        user.SecurityStamp = Guid.NewGuid( ).ToString( "N" );
                        _ = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).UpdateAsync( user );
                    }
                    break;
                }
            case "stale-session": {
                    // A real prior cookie whose browser session row has been removed.
                    _ = await SignInAsync( );
                    string priorCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
                    string priorSession = SessionIdOf( priorCookie );
                    oldReferenceCookie = priorCookie;
                    await using (SqliteConnection connection = new( $"Data Source={Path.Combine( _directory, "identity.db" )}" )) {
                        await connection.OpenAsync( TestContext.CancellationToken );
                        await using SqliteCommand remove = connection.CreateCommand( );
                        remove.CommandText = "DELETE FROM \"bridgebeats_bluesky_sessions\" WHERE \"SessionId\" = @session AND \"Did\" = @did;";
                        _ = remove.Parameters.AddWithValue( "@session", priorSession );
                        _ = remove.Parameters.AddWithValue( "@did", DidValue );
                        _ = await remove.ExecuteNonQueryAsync( TestContext.CancellationToken );
                    }
                    break;
                }
        }

        // Establish the NEWER current session: a real sign-in distinct from the old-reference state.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        using HttpResponseMessage newerSignIn = await SignInAsync( );
        string newerCookie = CookieValue( newerSignIn );
        string newerSession = SessionIdOf( newerCookie );
        int revocationsAfterNewerSignIn = _oauth.Revocations;
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The newer sign-in must own the identity row." );

        // Mixed-scheme logout carrying ONLY the old reference cookie (X-Service-Key on GET and POST).
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", oldReferenceCookie! );
        await AddAntiforgeryHeaderWithServiceKeyAsync( );
        using HttpResponseMessage logout = await _client.PostAsync( "/account/logout", null, TestContext.CancellationToken );
        _ = _client.DefaultRequestHeaders.Remove( "X-Service-Key" );

        Assert.AreEqual( HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        Assert.AreEqual( revocationsAfterNewerSignIn, _oauth.Revocations,
            $"A {variant} reference cookie must not revoke the newer session's grant." );
        IReadOnlyList<string> survivingSessions = await StoredBrowserSessionIdsAsync( );
        Assert.Contains( newerSession, survivingSessions,
            $"The exact newer browser row ({newerSession}) must survive a {variant} reference logout; found [{string.Join( ", ", survivingSessions )}]." );
        Assert.IsNotEmpty( await StoredBlobAsync( ),
            $"The newer session's credential bytes must survive a {variant} reference logout." );
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            $"The newer session's identity must remain usable after a {variant} reference logout." );

        // The newer cookie still authenticates (agent + local) once the service header is gone.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", cookieName + "=" + newerCookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ),
            $"The newer cookie must still authenticate after a {variant} reference logout." );
        Assert.IsTrue( who.GetProperty( "agentAuthenticated" ).GetBoolean( ),
            $"The newer cookie's agent must still authenticate after a {variant} reference logout." );
    }

    [TestMethod]
    public async Task MissingCredential_WithOrphanBrowserRows_ReconcilesAndDeletesCookieInSameResponse( ) {
        // A credential row that never existed leaves orphan browser rows behind. A request carrying the
        // cookie loses authentication and the orphan rows are reconciled on that same request.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId, "orphan-two" );
        string? concurrencyStamp = await CurrentStampAsync( "ConcurrencyStamp" );
        string? securityStampBefore = await CurrentStampAsync( "SecurityStamp" );
        AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

        using HttpResponseMessage who = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        JsonElement body = await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsFalse( body.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.IsTrue( who.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                       cookies.Any( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
            "The missing credential must delete the referring cookie in the same response." );

        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "The orphan browser rows must be reconciled." );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "No credential row may be created." );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_refresh_locks" ),
            "Refresh-lock ownership must be untouched." );
        Assert.AreNotEqual( concurrencyStamp, await CurrentStampAsync( "ConcurrencyStamp" ),
            "Removing orphan rows must rotate the local account concurrency stamp." );
        Assert.AreEqual( securityStampBefore, await CurrentStampAsync( "SecurityStamp" ),
            "The security stamp must not change for a missing-credential cleanup." );
    }

    [TestMethod]
    public async Task MissingCredentialReadBeforeReplacement_CasMissPreservesReplacementAndNoCookie( ) {
        // A request observes no credential row, then a fresh grant commits before the missing reconciliation
        // writes. The reconciliation's write-time guard sees an active row, leaves it and its browser alone,
        // rotates no stamp, and publishes no cookie outcome.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Func<Task>? previousSeam = BridgeBeatsIdentityStore.BeforeMissingCleanupDelete;
        string? concurrencyStamp = await CurrentStampAsync( "ConcurrencyStamp" );
        byte[]? replacementBlob = null;
        string replacementSession = "unset";
        try {
            TaskCompletionSource gate = new( TaskCreationOptions.RunContinuationsAsynchronously );
            TaskCompletionSource replacementCommitted = new( TaskCreationOptions.RunContinuationsAsynchronously );
            BridgeBeatsIdentityStore.BeforeMissingCleanupDelete = async ( ) => {
                // Runs before the missing-reconciliation writer opens, so the replacement commits first.
                await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 2 ), "race-replacement" );
                replacementBlob = await StoredBlobAsync( );
                // The replacement registers its own browser session; capture the one that is not the ticket's.
                replacementSession = (await StoredBrowserSessionIdsAsync( )).Single( id => id != sessionId );
                _ = replacementCommitted.TrySetResult( );
                await gate.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            };
            AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

            Task<HttpResponseMessage> request = _client.GetAsync( "/test/who", TestContext.CancellationToken );
            await replacementCommitted.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            Assert.IsNotNull( replacementBlob );
            gate.SetResult( );
            using HttpResponseMessage who = await request;
            Assert.IsFalse( (await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken ))
                .GetProperty( "authenticated" ).GetBoolean( ) );
            Assert.IsTrue( who.Headers.TryGetValues( "Set-Cookie", out _ ) == false ||
                           who.Headers.GetValues( "Set-Cookie" ).All( value =>
                               !value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
                "A missing-read race that found a committed replacement must not delete the cookie." );
        } finally {
            BridgeBeatsIdentityStore.BeforeMissingCleanupDelete = previousSeam;
        }

        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The replacement row must survive the missing reconciliation." );
        Assert.AreEqual( 2, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "The ticket's seeded browser row and the replacement's browser row must both survive." );
        IReadOnlyList<string> surviving = await StoredBrowserSessionIdsAsync( );
        Assert.IsTrue( surviving.Contains( sessionId ) && surviving.Contains( replacementSession ),
            "The surviving browser rows must be the ticket's session and the replacement's distinct session." );
        await AssertBlobEqualsAsync( replacementBlob! );
        Assert.AreEqual( concurrencyStamp, await CurrentStampAsync( "ConcurrencyStamp" ),
            "No rows changed, so the concurrency stamp must not rotate." );
    }

    [TestMethod]
    public async Task OverlappingMissingCleanup_StillPublishesCookieOutcomeWhenNoActiveRowRemains( ) {
        // A prior overlapping request already removed the orphan rows: this request's rowsChanged is zero, but
        // because no active credential remains the browser-bound cookie outcome must still be published so the
        // stale reference cookie is deleted. The stamp is not rotated (this request changed no rows).
        // The session row exists during Initialize (so account binding passes there); the overlapping removal
        // lands after Initialize via the missing-cleanup seam, so the cookie deletion can only come from the
        // cleanup outcome and would be lost if the outcome were gated on rowsChanged.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Func<Task>? previousSeam = BridgeBeatsIdentityStore.BeforeMissingCleanupDelete;
        string? concurrencyStamp = await CurrentStampAsync( "ConcurrencyStamp" );
        try {
            BridgeBeatsIdentityStore.BeforeMissingCleanupDelete = async ( ) => {
                // Simulate an overlapping cleanup that removed the browser rows this request did not change.
                await using SqliteConnection connection = new( $"Data Source={Path.Combine( _directory, "identity.db" )}" );
                await connection.OpenAsync( TestContext.CancellationToken );
                await using SqliteCommand remove = connection.CreateCommand( );
                remove.CommandText = "DELETE FROM \"bridgebeats_bluesky_sessions\" WHERE \"Did\" = @did;";
                _ = remove.Parameters.AddWithValue( "@did", DidValue );
                _ = await remove.ExecuteNonQueryAsync( TestContext.CancellationToken );
            };
            AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

            using HttpResponseMessage who = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
            Assert.IsFalse( (await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken ))
                .GetProperty( "authenticated" ).GetBoolean( ) );
            Assert.IsTrue( who.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                           cookies.Any( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
                "Even with no rows changed by this request, the stale cookie must be deleted when no active row remains." );
        } finally {
            BridgeBeatsIdentityStore.BeforeMissingCleanupDelete = previousSeam;
        }
        Assert.AreEqual( concurrencyStamp, await CurrentStampAsync( "ConcurrencyStamp" ),
            "No rows changed by this request, so the concurrency stamp must not rotate." );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ) );
    }

    [TestMethod]
    public async Task CorruptCiphertext_CleanupRemovesExactRowAndBrowser_AndRotatesConcurrencyStampOnly( ) {
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        string? concurrencyStamp = await CurrentStampAsync( "ConcurrencyStamp" );
        string? securityStampBefore = await CurrentStampAsync( "SecurityStamp" );
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        byte[] corrupt = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, @identity, @expires, 'old-proof');
                               """;
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@identity", corrupt );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }

        AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

        using HttpResponseMessage who = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        JsonElement body = await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsFalse( body.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( 0, _oauth.Revocations, "A corrupt blob must never trigger remote revocation." );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The exact corrupt row must be removed." );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "Its browser rows must be removed." );
        Assert.AreNotEqual( concurrencyStamp, await CurrentStampAsync( "ConcurrencyStamp" ) );
        Assert.AreEqual( securityStampBefore, await CurrentStampAsync( "SecurityStamp" ),
            "Corrupt-row cleanup must not rotate the security stamp." );
        Assert.IsTrue( who.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) &&
                       cookies.Any( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
            "The corrupt row must delete the referring cookie in the same response." );
    }

    [TestMethod]
    public async Task ProtectedMalformedPayload_CleanupRemovesRowWithoutRevocation( ) {
        // A blob that unprotects but does not deserialize to a usable identity shape is treated as corrupt.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        byte[] malformed = [0x01, 0x02, 0x03, 0x04];
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        byte[] protectedMalformed = Options( ).DataProtectionProvider!
            .CreateProtector( idunno.Bluesky.AspNet.Authentication.Events.DataProtectingIdentityStoreEvents.ProtectorPurpose )
            .Protect( malformed );
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, @identity, @expires, 'malformed-proof');
                               """;
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@identity", protectedMalformed );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }

        AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

        using HttpResponseMessage who = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
        JsonElement body = await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
        Assert.IsFalse( body.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ) );
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ) );
    }

    [TestMethod]
    public async Task ConcurrentReplacementBeforeCorruptCas_SurvivesExactRowAndBrowser( ) {
        // A corrupt row observed by a read is replaced by a fresh grant before the corrupt cleanup's CAS
        // delete runs. The gate is a bounded TaskCompletionSource awaited by the cleanup strictly BEFORE it
        // opens any writer; the replacement commits fully before the gate releases, so the CAS delete must
        // miss (different blob) and the replacement must survive with its distinct browser session.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        byte[] corrupt = [0xAA, 0xBB, 0xCC, 0xDD];
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, @identity, @expires, 'corrupt-proof');
                               """;
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@identity", corrupt );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Func<Task>? previousSeam = BridgeBeatsIdentityStore.BeforeCorruptCleanupDelete;
        string? concurrencyStampBefore = await CurrentStampAsync( "ConcurrencyStamp" );
        byte[]? replacementBlob = null;
        string replacementSession = "unset";
        try {
            TaskCompletionSource gate = new( TaskCreationOptions.RunContinuationsAsynchronously );
            TaskCompletionSource replacementCommitted = new( TaskCreationOptions.RunContinuationsAsynchronously );
            BridgeBeatsIdentityStore.BeforeCorruptCleanupDelete = async ( ) => {
                // Runs outside any writer: land the replacement and prove it committed before releasing.
                await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 2 ), "replacement-grant" );
                replacementBlob = await StoredBlobAsync( );
                // The replacement registers its own browser session; there are now two rows (the ticket's
                // seeded session and the replacement's). Capture the one that is not the ticket's.
                replacementSession = (await StoredBrowserSessionIdsAsync( )).Single( id => id != sessionId );
                _ = replacementCommitted.TrySetResult( );
                await gate.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            };
            AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

            Task<HttpResponseMessage> request = _client.GetAsync( "/test/who", TestContext.CancellationToken );
            await replacementCommitted.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            Assert.IsNotNull( replacementBlob, "The replacement must have committed before the gate releases." );
            Assert.AreNotEqual( Convert.ToHexString( corrupt ), Convert.ToHexString( replacementBlob ),
                "The replacement blob must differ from the observed corrupt blob." );
            Assert.AreNotEqual( sessionId, replacementSession,
                "The replacement must register a distinct browser session." );
            gate.SetResult( );
            using HttpResponseMessage who = await request;
            JsonElement body = await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken );
            Assert.IsFalse( body.GetProperty( "authenticated" ).GetBoolean( ),
                "The request that observed the corrupt row is unauthenticated, but must not have deleted the replacement." );
        } finally {
            BridgeBeatsIdentityStore.BeforeCorruptCleanupDelete = previousSeam;
        }

        // The replacement and its browser row survive; no stamp change; no revocation; no cookie deletion.
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The replacement row must survive the corrupt cleanup." );
        Assert.AreEqual( 2, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "Both the ticket's seeded browser row and the replacement's browser row must survive a CAS miss." );
        IReadOnlyList<string> surviving = await StoredBrowserSessionIdsAsync( );
        Assert.IsTrue( surviving.Contains( sessionId ) && surviving.Contains( replacementSession ),
            "The surviving browser rows must be the ticket's session and the replacement's distinct session." );
        Assert.IsNotNull( replacementBlob );
        await AssertBlobEqualsAsync( replacementBlob! );
        Assert.AreEqual( concurrencyStampBefore, await CurrentStampAsync( "ConcurrencyStamp" ),
            "A CAS miss must not rotate the concurrency stamp." );
        Assert.AreEqual( 0, _oauth.Revocations );
    }

    [TestMethod]
    public async Task FreshSignInCarryingCorruptOldCookie_SucceedsWithSingleUsableNewCookie( ) {
        // A request that arrives authenticated only by a cookie whose store row is missing/corrupt performs
        // a fresh sign-in in the same request. The new cookie must win; the cleanup deletion must not.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        byte[] corrupt = [0x11, 0x22, 0x33];
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, @identity, @expires, 'corrupt-proof-2');
                               """;
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@identity", corrupt );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }
        AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

        using HttpResponseMessage signIn = await _client.PostAsync( "/test/signin", null, TestContext.CancellationToken );
        Assert.AreEqual( HttpStatusCode.OK, signIn.StatusCode, await signIn.Content.ReadAsStringAsync( TestContext.CancellationToken ) );
        string[] blueskyCookies = [.. signIn.Headers.GetValues( "Set-Cookie" ).Where( value => value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=", StringComparison.Ordinal ) )];
        Assert.HasCount( 1, blueskyCookies, "The fresh sign-in must produce exactly one nondeleted Bluesky cookie." );
        Assert.IsFalse( blueskyCookies.Single( ).StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) );

        string freshCookie = blueskyCookies.Single( ).Split( ';' )[0];
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", freshCookie );
        JsonElement who = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsTrue( who.GetProperty( "authenticated" ).GetBoolean( ) );
    }

    [TestMethod]
    public async Task CleanupCommit_FailureRollsBackRowsAndPublishesNoCookieOutcome( ) {
        // A SQLite error while the corrupt cleanup's browser deletion runs aborts the whole transaction:
        // the corrupt rows stay, the stamp stays, and no cookie outcome is published.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        byte[] corrupt = [0x41, 0x42, 0x43];
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, @identity, @expires, 'corrupt-proof-3');
                               CREATE TRIGGER fail_browser_delete BEFORE DELETE ON "bridgebeats_bluesky_sessions"
                               BEGIN SELECT RAISE(ABORT, 'injected browser delete failure'); END;
                               """;
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@identity", corrupt );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }
        string? concurrencyStamp = await CurrentStampAsync( "ConcurrencyStamp" );

        AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );
        using HttpResponseMessage who = await _client.GetAsync( "/test/who", TestContext.CancellationToken );

        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The corrupt row must survive the failed transaction." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "The browser row must survive the failed transaction." );
        Assert.AreEqual( concurrencyStamp, await CurrentStampAsync( "ConcurrencyStamp" ),
            "No stamp change may be published when the cleanup transaction fails." );
        Assert.IsFalse( who.Headers.TryGetValues( "Set-Cookie", out _ ),
            "A failed cleanup must not produce a cookie deletion outcome." );
    }

    [TestMethod]
    public async Task HealthyCredential_ExtendsExpiryAndRunsRetrievalEvents_AndLocksStayUsable( ) {
        _ = await SignInAsync( );
        IIdentityStore store = Options( ).IdentityStore!;
        Did did = new( DidValue );

        // Age the stored row by a day so a read that does NOT extend would leave it a day short. Only an
        // active extension brings it back to ~now+7 days, so the assertion discriminates extension rather
        // than tolerating whatever expiry the sign-in happened to write.
        await using (SqliteConnection connection = new( $"Data Source={Path.Combine( _directory, "identity.db" )}" )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand age = connection.CreateCommand( );
            age.CommandText = "UPDATE \"idunno_bluesky_identities\" SET \"ExpiresAtUtcTicks\" = @aged WHERE \"Did\" = @did;";
            _ = age.Parameters.AddWithValue( "@did", DidValue );
            _ = age.Parameters.AddWithValue( "@aged", DateTime.UtcNow.AddDays( 6 ).Ticks );
            _ = await age.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }

        bool onRetrievedRan = false;
        Func<IdentityStoreRetrievedContext, Task>? previousOnRetrieved = store.Events.OnRetrieved;
        store.Events.OnRetrieved = retrieved => {
            onRetrievedRan = true;
            return Task.CompletedTask;
        };
        try {
            ClaimsIdentity? identity = await store.GetIdentity( did, TestContext.CancellationToken );
            Assert.IsNotNull( identity );
            Assert.IsTrue( onRetrievedRan, "A healthy read must still raise the retrieval event." );
            // A healthy read extends the row back to roughly now+7 days while the row remains unexpired.
            long extended = await StoredExpiryTicksAsync( );
            long now = DateTime.UtcNow.Ticks;
            long days7 = TimeSpan.FromDays( 7 ).Ticks;
            Assert.IsTrue( extended > now + days7 - TimeSpan.FromMinutes( 1 ).Ticks &&
                           extended <= now + days7 + TimeSpan.FromMinutes( 1 ).Ticks,
                $"A healthy read must extend the aged row to about now+7 days; stored {extended}, now {now}." );
        } finally {
            store.Events.OnRetrieved = previousOnRetrieved;
        }

        // Refresh-lock delegation is unchanged: a healthy row can still take and release the lock.
        string? lockToken = await store.StartRefresh( did, TestContext.CancellationToken );
        Assert.IsNotNull( lockToken );
        Assert.IsTrue( await store.EndRefresh( did, lockToken, TestContext.CancellationToken ) );
    }

    [TestMethod]
    public async Task ReplacedStoreEvents_AreHonouredForBothWritesAndReads_WhileDbStaysProtected( ) {
        // Installing a custom events instance drives both write (PreStoring) and read (PostRetrieval), and
        // ReplaceIdentity made by that instance is honoured. Data protection stays the permanent outer layer:
        // the raw stored bytes remain a DP envelope that unprotects with the exact protector purpose, and
        // restoring the configured (wrapper) events leaves the row usable rather than classifying it corrupt.
        _ = await SignInAsync( );
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        IdentityStoreEvents previous = store.Events;
        PassThroughEvents custom = new( );
        store.Events = custom;
        Assert.AreSame( custom, store.Events, "The replaced events instance must be the store's active Events." );
        try {
            // A write lands through the custom PreStoring (and the wrapper then protects).
            await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
                ApplicationUser user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users
                    .SingleAsync( TestContext.CancellationToken );
                IHttpContextAccessor accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>( );
                HttpContext? previousContext = accessor.HttpContext;
                DefaultHttpContext request = new( ) { RequestServices = scope.ServiceProvider };
                request.Items[BridgeBeatsIdentityStore.ExpectedStampKey] = user.SecurityStamp!;
                request.Items[BridgeBeatsIdentityStore.PendingBrowserSessionKey] =
                    new BridgeBeatsIdentityStore.PendingBrowserSession( DidValue, Guid.NewGuid( ).ToString( "N" ), DateTimeOffset.UtcNow.AddDays( 7 ) );
                accessor.HttpContext = request;
                try {
                    await store.Add( IIdentityStore.BuildClaimsIdentity( CreateCredentials( DateTime.UtcNow.AddHours( 2 ), "replacement-via-custom-events" ) ),
                        TestContext.CancellationToken );
                } finally {
                    accessor.HttpContext = previousContext;
                }
            }
            Assert.IsTrue( custom._preStoringRan && custom._storedLength > 0,
                "The write must go through the replaced PreStoring." );

            // A read must go through the replaced PostRetrieval and see the identity this instance stored.
            ClaimsIdentity? identity = await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken );
            Assert.IsNotNull( identity, "The custom events must yield a readable identity, not a corrupt cleanup." );
            Assert.IsTrue( custom._postRetrievalRan && custom._retrievedLength == custom._storedLength,
                "The read must go through the replaced PostRetrieval and honor ReplaceIdentity." );

            // The raw DB row is still a DP-protected envelope: unprotect with the exact purpose succeeds.
            byte[] storedBlob = await StoredBlobAsync( );
            byte[] unprotected = Options( ).DataProtectionProvider!
                .CreateProtector( idunno.Bluesky.AspNet.Authentication.Events.DataProtectingIdentityStoreEvents.ProtectorPurpose )
                .Unprotect( storedBlob );
            Assert.IsNotEmpty( unprotected, "The stored row must remain DP-protected under custom events." );
        } finally {
            store.Events = previous;
        }

        // Restoring the configured (wrapper) events: the same protected row stays usable, no cleanup runs.
        Assert.AreSame( previous, store.Events, "The original events must be restored." );
        Assert.IsNotNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "The restored events must read the still-protected row without a corruption cleanup." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "Restoring the events must not delete the row." );
    }

    [TestMethod]
    public async Task RetrievalCallbackCryptographicException_IsNonCorruptionAndPreservesRow( ) {
        _ = await SignInAsync( );
        string sessionBefore = (await StoredBrowserSessionIdsAsync( )).Single( );
        IIdentityStore store = Options( ).IdentityStore!;
        Func<IdentityStoreRetrievedContext, Task>? previousOnRetrieved = store.Events.OnRetrieved;
        store.Events.OnRetrieved = _ => throw new CryptographicException( "callback failure" );
        try {
            Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
                "A failing retrieval callback must surface as a miss, not a credential." );
        } finally {
            store.Events.OnRetrieved = previousOnRetrieved;
        }
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "A callback failure must not delete or corrupt the stored row." );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "A callback CryptographicException must not be treated as corrupt-row cleanup." );
        IReadOnlyList<string> sessionsAfter = await StoredBrowserSessionIdsAsync( );
        Assert.HasCount( 1, sessionsAfter,
            "A callback CryptographicException must not remove browser rows." );
        Assert.AreEqual( sessionBefore, sessionsAfter.Single( ),
            "The exact browser row must survive a callback CryptographicException." );
    }

    [TestMethod]
    public async Task RetrievalCallbackInvalidOperationException_IsNonCorruptionAndPreservesRow( ) {
        _ = await SignInAsync( );
        string sessionBefore = (await StoredBrowserSessionIdsAsync( )).Single( );
        IIdentityStore store = Options( ).IdentityStore!;
        var previousOnRetrieved = store.Events.OnRetrieved;
        store.Events.OnRetrieved = _ => throw new InvalidOperationException( "callback failure" );
        try {
            Assert.IsNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        } finally {
            store.Events.OnRetrieved = previousOnRetrieved;
        }
        Assert.IsNotNull( await Options( ).IdentityStore!.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ) );
        Assert.AreEqual( 0, _oauth.Revocations );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "A callback InvalidOperationException must not be treated as corrupt-row cleanup." );
        IReadOnlyList<string> sessionsAfter = await StoredBrowserSessionIdsAsync( );
        Assert.HasCount( 1, sessionsAfter,
            "A callback InvalidOperationException must not remove browser rows." );
        Assert.AreEqual( sessionBefore, sessionsAfter.Single( ),
            "The exact browser row must survive a callback InvalidOperationException." );
    }

    [TestMethod]
    public async Task RetrievalCancellation_PropagatesAndPreservesState( ) {
        _ = await SignInAsync( );
        string sessionBefore = (await StoredBrowserSessionIdsAsync( )).Single( );
        IIdentityStore store = Options( ).IdentityStore!;

        // Pre-cancelled token: the read must not look like a missing user and must not alter state.
        using (CancellationTokenSource preCancelled = new( )) {
            await preCancelled.CancelAsync( );
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                ( ) => store.GetIdentity( new Did( DidValue ), preCancelled.Token ) );
        }

        // Cancellation raised from inside the retrieval callback: propagates as OCE, rows/identity preserved.
        CancellationTokenSource callbackCts = new( );
        Func<IdentityStoreRetrievedContext, Task>? previousOnRetrieved = store.Events.OnRetrieved;
        store.Events.OnRetrieved = retrieved => {
            callbackCts.Cancel( );
            return Task.CompletedTask;
        };
        try {
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                ( ) => store.GetIdentity( new Did( DidValue ), callbackCts.Token ) );
        } finally {
            store.Events.OnRetrieved = previousOnRetrieved;
            callbackCts.Dispose( );
        }

        Assert.IsNotNull( await store.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "A cancelled read must not delete or alter the stored row." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "A cancelled read must not run a corrupt-row cleanup." );
        IReadOnlyList<string> sessionsAfter = await StoredBrowserSessionIdsAsync( );
        Assert.HasCount( 1, sessionsAfter,
            "A cancelled read must not remove browser rows." );
        Assert.AreEqual( sessionBefore, sessionsAfter.Single( ),
            "The exact browser row must survive a cancelled read." );
    }

    [TestMethod]
    public async Task StoreReadError_PreservesStateAndIsNotTreatedAsCorruption( ) {
        // A real SQLite read failure (the identities table does not exist) must propagate as a database
        // error, never as a corrupt-credential cleanup. A throwaway store over an unmigrated database is
        // used so the production store and its rows are untouched.
        string tempDb = Path.Combine( _directory, "no-tables.db" );
        string connectionString = $"Data Source={tempDb}";
        await using (SqliteConnection create = new( connectionString )) {
            await create.OpenAsync( TestContext.CancellationToken );
        }
        BridgeBeatsIdentityStore brokenStore = new( connectionString, new Moq.Mock<IHttpContextAccessor>( ).Object,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            Options( ).DataProtectionProvider! );
        _ = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            ( ) => brokenStore.GetIdentity( new Did( DidValue ), TestContext.CancellationToken ),
            "A missing identities table must surface as a SQLite read error." );
        // No rows existed to clean and no cleanup can have been published (the read failed before any
        // corruption path and there is no HttpContext to carry an outcome marker).
        Assert.AreEqual( 0, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ) );
    }

    [TestMethod]
    public async Task ProviderOutageDuringOrdinaryAuth_LeavesSessionUsableAndNoDeletion( ) {
        // A provider-discovery failure during an ORDINARY authenticated request (not a logout) must not be
        // treated as credential corruption: with an expired token the SDK's thrown refresh failure returns
        // an unauthenticated result without removing the identity (verified against SDK source), so the
        // session row, credential row, and cookie must all remain and no deletion Set-Cookie is emitted.
        _ = await SignInAsync( );
        string sessionBefore = (await StoredBrowserSessionIdsAsync( )).Single( );
        await ExpireStoredAccessTokenAsync( );
        _oauth.ProtectedResourceDiscoveryFails = true;
        try {
            using HttpResponseMessage who = await _client.GetAsync( "/test/who", TestContext.CancellationToken );
            Assert.IsFalse( (await who.Content.ReadFromJsonAsync<JsonElement>( TestContext.CancellationToken ))
                .GetProperty( "authenticated" ).GetBoolean( ),
                "An unresolved provider must fail the ordinary authentication." );
            Assert.IsTrue( who.Headers.TryGetValues( "Set-Cookie", out IEnumerable<string>? cookies ) == false ||
                           cookies!.All( value => !value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
                "A provider outage must not delete the reference cookie." );
        } finally {
            _oauth.ProtectedResourceDiscoveryFails = false;
        }

        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "A provider outage must not trigger corrupt-credential cleanup." );
        IReadOnlyList<string> sessionsAfter = await StoredBrowserSessionIdsAsync( );
        Assert.HasCount( 1, sessionsAfter,
            "A provider outage must not remove browser rows." );
        Assert.AreEqual( sessionBefore, sessionsAfter.Single( ),
            "The exact browser row must survive a provider outage." );
        Assert.AreEqual( 0, _oauth.Revocations,
            "A provider outage on an ordinary request must not revoke the grant." );
        Assert.IsEmpty( _oauth.RevokedRefreshTokens );
    }

    [TestMethod]
    public async Task MissingCredentialStaleCookie_CannotRemoveFreshSignInOrRevoke( ) {
        // A cookie whose store row is missing must not revoke or remove a fresh session stored after it.
        _ = await SignInAsync( );
        string validCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        await using (SqliteConnection connection = new( $"Data Source={Path.Combine( _directory, "identity.db" )}" )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand remove = connection.CreateCommand( );
            remove.CommandText = "DELETE FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did; DELETE FROM \"bridgebeats_bluesky_sessions\" WHERE \"Did\" = @did;";
            _ = remove.Parameters.AddWithValue( "@did", DidValue );
            _ = await remove.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        int revocations = _oauth.Revocations;

        // A replay of the stale cookie after the fresh sign-in must be rejected without touching the fresh session.
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", validCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( revocations, _oauth.Revocations,
            "A stale missing-row cookie must not revoke the fresh session's grant." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ),
            "The fresh sign-in's credential row must be untouched." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "The fresh sign-in's browser row must be untouched." );
    }

    [TestMethod]
    public async Task StaleStampOrSessionCookie_CannotRemoveNewSessionOrRevoke( ) {
        // Expired-ticket / stale-stamp / stale-session cookies must not remove the new session's rows or revoke.
        _ = await SignInAsync( );
        string validCookie = _client.DefaultRequestHeaders.GetValues( "Cookie" ).Single( );
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            ApplicationUser user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( ).Users.SingleAsync( TestContext.CancellationToken );
            user.SecurityStamp = Guid.NewGuid( ).ToString( "N" );
            _ = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( ).UpdateAsync( user );
        }
        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _ = await SignInAsync( );
        int revocations = _oauth.Revocations;
        int browserRowsBeforeReplay = await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" );

        _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
        _client.DefaultRequestHeaders.Add( "Cookie", validCookie );
        JsonElement replay = await _client.GetFromJsonAsync<JsonElement>( "/test/who", TestContext.CancellationToken );
        Assert.IsFalse( replay.GetProperty( "authenticated" ).GetBoolean( ) );
        Assert.AreEqual( revocations, _oauth.Revocations );
        Assert.AreEqual( browserRowsBeforeReplay, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ),
            "A stale-stamp cookie replay must not remove the fresh session's browser row." );
        Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ) );
    }

    [TestMethod]
    public async Task CleanupDoesNotPublishCookieOutcomeWhenCasMisses( ) {
        // A corrupt row replaced by a healthy row before cleanup: CAS miss leaves no browser/stamp/cookie changes,
        // and the committed replacement's own session stays usable.
        string sessionId = Guid.NewGuid( ).ToString( "N" );
        (string userId, string securityStamp) = await SeedLinkedAccountAndBrowserAsync( sessionId );
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        byte[] corrupt = [0x77, 0x88, 0x99];
        await using (SqliteConnection connection = new( connectionString )) {
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = """
                               INSERT INTO "idunno_bluesky_identities" ("Did", "Identity", "ExpiresAtUtcTicks", "ProofKeyHash")
                               VALUES (@did, @identity, @expires, 'corrupt-proof-4');
                               """;
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@identity", corrupt );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
        }
        BridgeBeatsIdentityStore store = (BridgeBeatsIdentityStore)Options( ).IdentityStore!;
        Func<Task>? previousSeam = BridgeBeatsIdentityStore.BeforeCorruptCleanupDelete;
        string? concurrencyStamp = await CurrentStampAsync( "ConcurrencyStamp" );
        byte[]? replacementBlob = null;
        string replacementSession = "unset";
        try {
            TaskCompletionSource gate = new( TaskCreationOptions.RunContinuationsAsynchronously );
            TaskCompletionSource replacementCommitted = new( TaskCreationOptions.RunContinuationsAsynchronously );
            BridgeBeatsIdentityStore.BeforeCorruptCleanupDelete = async ( ) => {
                await AddReplacementCredentialsAsync( store, DateTime.UtcNow.AddHours( 2 ), "replacement-grant-2" );
                replacementBlob = await StoredBlobAsync( );
                replacementSession = (await StoredBrowserSessionIdAsync( ))!;
                _ = replacementCommitted.TrySetResult( );
                await gate.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            };
            AuthenticationTicket ticket = BuildTicketForDid( DidValue, sessionId, userId, securityStamp );
            _ = _client.DefaultRequestHeaders.Remove( "Cookie" );
            _client.DefaultRequestHeaders.Add( "Cookie", BlueskyAuthenticationConfiguration.CookieName + "=" + Options( ).TicketDataFormat.Protect( ticket ) );

            Task<HttpResponseMessage> request = _client.GetAsync( "/test/who", TestContext.CancellationToken );
            await replacementCommitted.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), TestContext.CancellationToken );
            Assert.IsNotNull( replacementBlob );
            Assert.AreNotEqual( Convert.ToHexString( corrupt ), Convert.ToHexString( replacementBlob ) );
            gate.SetResult( );
            using HttpResponseMessage who = await request;

            Assert.AreEqual( 1, await LinkedBlueskyRowCountAsync( "idunno_bluesky_identities" ) );
            Assert.AreEqual( concurrencyStamp, await CurrentStampAsync( "ConcurrencyStamp" ),
                "A CAS miss must not rotate the concurrency stamp." );
            Assert.IsTrue( who.Headers.TryGetValues( "Set-Cookie", out _ ) == false ||
                           who.Headers.GetValues( "Set-Cookie" ).All( value =>
                               !value.StartsWith( BlueskyAuthenticationConfiguration.CookieName + "=;", StringComparison.Ordinal ) ),
                "A CAS miss must not delete the cookie." );
        } finally {
            BridgeBeatsIdentityStore.BeforeCorruptCleanupDelete = previousSeam;
        }

        // The replacement committed with exact bytes and a distinct session; it is still usable.
        await AssertBlobEqualsAsync( replacementBlob! );
        IReadOnlyList<string> surviving = await StoredBrowserSessionIdsAsync( );
        Assert.IsTrue( surviving.Contains( sessionId ) && surviving.Contains( replacementSession ),
            "The surviving browser rows must be the ticket's session and the replacement's distinct session." );
        Assert.AreEqual( 2, await LinkedBlueskyRowCountAsync( "bridgebeats_bluesky_sessions" ) );
    }

    private AuthenticationTicket BuildTicketForDid( string did, string sessionId, string userId, string securityStamp ) {
        AuthenticationProperties properties = new( ) {
            IssuedUtc = DateTimeOffset.UtcNow.AddMinutes( -1 ),
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays( 7 )
        };
        properties.SetString( BlueskyAccountEvents.UserIdProperty, userId );
        properties.SetString( BlueskyAccountEvents.SecurityStampProperty, securityStamp );
        properties.SetString( BlueskyAccountEvents.BrowserSessionProperty, sessionId );
        ClaimsIdentity identity = new( [new Claim( AtProtoClaims.Did, did )], "Bluesky" );
        return new AuthenticationTicket( new ClaimsPrincipal( identity ), properties, s_scheme );
    }

    /// <summary>
    /// Creates the local user linked to <see cref="DidValue"/> (if absent) and seeds the given browser session
    /// rows, returning the user id and security stamp a valid reference ticket must carry. Used to build tickets
    /// that pass <see cref="BlueskyAccountEvents.HasCurrentAccountBindingAsync"/> in the handler.
    /// </summary>
    private async Task<(string UserId, string SecurityStamp)> SeedLinkedAccountAndBrowserAsync( params string[] sessionIds ) {
        string userId;
        string securityStamp;
        await using (AsyncServiceScope scope = _app.Services.CreateAsyncScope( )) {
            UserManager<ApplicationUser> users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>( );
            ApplicationUser? user = await users.Users.SingleOrDefaultAsync( u => u.AtProtoDid == DidValue, TestContext.CancellationToken );
            if (user is null) {
                user = new ApplicationUser { UserName = $"bluesky_{Guid.NewGuid( ):N}", AtProtoDid = DidValue, CreatedAt = DateTime.UtcNow };
                Assert.IsTrue( (await users.CreateAsync( user )).Succeeded );
            }
            userId = user.Id;
            securityStamp = (await users.GetSecurityStampAsync( user ))!;
        }
        if (sessionIds.Length > 0) {
            string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
            await using SqliteConnection connection = new( connectionString );
            await connection.OpenAsync( TestContext.CancellationToken );
            await using SqliteCommand seed = connection.CreateCommand( );
            seed.CommandText = "INSERT INTO \"bridgebeats_bluesky_sessions\" (\"SessionId\", \"Did\", \"ExpiresAtUtcTicks\") VALUES (@session, @did, @expires);";
            _ = seed.Parameters.Add( "@session", SqliteType.Text );
            _ = seed.Parameters.AddWithValue( "@did", DidValue );
            _ = seed.Parameters.AddWithValue( "@expires", DateTime.UtcNow.AddDays( 7 ).Ticks );
            foreach (string sessionId in sessionIds) {
                seed.Parameters["@session"].Value = sessionId;
                _ = await seed.ExecuteNonQueryAsync( TestContext.CancellationToken );
            }
        }
        return (userId, securityStamp);
    }

    private async Task<string?> CurrentStampAsync( string column ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand read = connection.CreateCommand( );
        read.CommandText = $"SELECT {column} FROM AspNetUsers WHERE AtProtoDid = @did;";
        _ = read.Parameters.AddWithValue( "@did", DidValue );
        return await read.ExecuteScalarAsync( TestContext.CancellationToken ) as string;
    }

    private async Task<long> StoredExpiryTicksAsync( ) {
        string connectionString = $"Data Source={Path.Combine( _directory, "identity.db" )}";
        await using SqliteConnection connection = new( connectionString );
        await connection.OpenAsync( TestContext.CancellationToken );
        await using SqliteCommand read = connection.CreateCommand( );
        read.CommandText = "SELECT \"ExpiresAtUtcTicks\" FROM \"idunno_bluesky_identities\" WHERE \"Did\" = @did;";
        _ = read.Parameters.AddWithValue( "@did", DidValue );
        return Convert.ToInt64( await read.ExecuteScalarAsync( TestContext.CancellationToken ), CultureInfo.InvariantCulture );
    }

    private sealed class PassThroughEvents : IdentityStoreEvents {
        internal int _storedLength = -1;
        internal int _retrievedLength = -1;
        internal bool _preStoringRan;
        internal bool _postRetrievalRan;

        public override async Task PreStoring( IdentityStoreSettingContext context ) {
            await base.PreStoring( context );
            _preStoringRan = true;
            _storedLength = context.Identity.Length;
            // ReplaceIdentity is honoured without altering the payload (a pass-through copy keeps the stored
            // data readable by the same events instance).
            context.ReplaceIdentity( context.Identity.ToArray( ) );
        }

        public override async Task PostRetrieval( IdentityStoreRetrievedContext context ) {
            await base.PostRetrieval( context );
            _postRetrievalRan = true;
            _retrievedLength = context.Identity.Length;
        }
    }

    private sealed class ActionExceptionObserver : IAsyncActionFilter {
        private readonly ConcurrentQueue<Exception> _exceptions = new( );
        private readonly TaskCompletionSource<Exception> _firstException = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public Exception[] Exceptions => [.. _exceptions];

        public Action? BeforeAction { get; set; }

        public Task<Exception> WaitForExceptionAsync( ) => _firstException.Task;

        public async Task OnActionExecutionAsync( ActionExecutingContext context, ActionExecutionDelegate next ) {
            BeforeAction?.Invoke( );
            ActionExecutedContext result;
            try {
                result = await next( );
            } catch (Exception thrown) {
                _exceptions.Enqueue( thrown );
                _ = _firstException.TrySetResult( thrown );
                throw;
            }
            if (result.Exception is Exception exception && !result.ExceptionHandled) {
                _exceptions.Enqueue( exception );
                _ = _firstException.TrySetResult( exception );
            }
        }
    }

    private sealed class TestHttpClientFactory( HttpMessageHandler handler ) : IHttpClientFactory {
        public HttpClient CreateClient( string name ) => new( handler, disposeHandler: false );
    }
    private sealed class OAuthHandler : HttpMessageHandler {
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _authorizations = new( );
        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _codes = new( );
        private int _refreshes;
        private int _revocations;
        private int _pushedAuthorizations;
        private int _codeExchanges;
        public string Service { get; set; } = null!;
        public string? AuthoritativeServer { get; set; }
        public string? OtherDidPds { get; set; }
        public Func<string> TokenFactory { get; set; } = null!;
        public string TokenSubjectDid { get; set; } = DidValue;
        public int Refreshes => _refreshes;
        public int Revocations => _revocations;
        public int PushedAuthorizations => _pushedAuthorizations;
        public int CodeExchanges => _codeExchanges;
        public string? LoginHint { get; set; }
        public bool RevocationFails { get; set; }
        public bool ProtectedResourceDiscoveryFails { get; set; }
        public Func<CancellationToken, Task<JsonElement>> MetadataRetriever { get; set; } = null!;
        public ConcurrentQueue<string> ClientAssertionKeyIds { get; } = new( );
        public string? LastAssertionValidationFailure { get; set; }
        /// <summary>Issues the refresh token for each grant. Tests replace this to make grants distinguishable.</summary>
        public Func<string> RefreshTokenFactory { get; set; } = ( ) => "rotated-refresh";
        /// <summary>Every token presented to the revocation endpoint, with the hint that accompanied it.</summary>
        public ConcurrentQueue<(string Token, string TokenTypeHint)> RevokedGrants { get; } = new( );
        /// <summary>When set, the first revocation request waits for this task before responding.</summary>
        public TaskCompletionSource? RevokeGate { get; set; }
        public IReadOnlyList<string> RevokedRefreshTokens =>
            [.. RevokedGrants.Where( grant => grant.TokenTypeHint == "refresh_token" ).Select( grant => grant.Token )];
        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken ) {
            string path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/.well-known/atproto-did") {
                return new HttpResponseMessage( HttpStatusCode.OK ) { Content = new StringContent( DidValue ) };
            } else if (path == "/" + DidValue || path == "/" + OtherDidValue) {
                string did = path == "/" + DidValue ? DidValue : OtherDidValue;
                body = new {
                    id = did,
                    alsoKnownAs = new[] { "at://first.example" },
                    service = new[] { new { id = "#atproto_pds", type = "AtprotoPersonalDataServer", serviceEndpoint = did == DidValue ? Service : OtherDidPds ?? "https://other-pds.example" } }
                };
            } else if (path == "/par") {
                Dictionary<string, string> parameters = ParseParameters( await request.Content!.ReadAsStringAsync( cancellationToken ) );
                await ValidateClientAssertionAsync( parameters, cancellationToken );
                LoginHint = parameters["login_hint"];
                string requestUri = "urn:ietf:params:oauth:request_uri:" + Guid.NewGuid( ).ToString( "N" );
                _authorizations[requestUri] = parameters;
                _ = Interlocked.Increment( ref _pushedAuthorizations );
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
                await ValidateClientAssertionAsync( parameters, cancellationToken );
                if (parameters["grant_type"] == "authorization_code") {
                    if (!parameters.TryGetValue( "code", out string? code ) || !_codes.TryRemove( code, out Dictionary<string, string>? authorization ) ||
                        !parameters.TryGetValue( "code_verifier", out string? verifier ) ||
                        authorization["code_challenge"] != Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode( SHA256.HashData( Encoding.ASCII.GetBytes( verifier ) ) ) ||
                        authorization["code_challenge_method"] != "S256" || authorization["redirect_uri"] != parameters["redirect_uri"] ||
                        authorization["client_id"] != parameters["client_id"] || !request.Headers.Contains( "DPoP" )) {
                        return new HttpResponseMessage( HttpStatusCode.BadRequest ) { Content = JsonContent.Create( new { error = "invalid_grant" } ) };
                    }
                    _ = Interlocked.Increment( ref _codeExchanges );
                } else {
                    _ = Interlocked.Increment( ref _refreshes );
                }
                body = new { access_token = TokenFactory( ), refresh_token = RefreshTokenFactory( ), token_type = "DPoP", expires_in = 3600, scope = "atproto repo:link.bridgebeats.playlist", sub = TokenSubjectDid };
            } else if (path == "/revoke") {
                _ = Interlocked.Increment( ref _revocations );
                Dictionary<string, string> revocation = ParseParameters( await request.Content!.ReadAsStringAsync( cancellationToken ) );
                RevokedGrants.Enqueue( (revocation["token"], revocation["token_type_hint"]) );
                if (RevokeGate is { } gate) {
                    RevokeGate = null;
                    await gate.Task.WaitAsync( TimeSpan.FromSeconds( 10 ), cancellationToken );
                }
                await ValidateClientAssertionAsync( revocation, cancellationToken );
                if (RevocationFails) {
                    return new HttpResponseMessage( HttpStatusCode.ServiceUnavailable ) { Content = JsonContent.Create( new { error = "temporarily_unavailable" } ) };
                }
                body = new { };
            } else if (path.EndsWith( "oauth-protected-resource", StringComparison.Ordinal )) {
                if (ProtectedResourceDiscoveryFails) {
                    throw new HttpRequestException( "Protected resource metadata is unavailable." );
                }
                body = new { resource = Service, authorization_servers = new[] { AuthoritativeServer ?? Service } };
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
                    token_endpoint_auth_methods_supported = new[] { "private_key_jwt" },
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

        private async Task ValidateClientAssertionAsync( Dictionary<string, string> parameters, CancellationToken cancellationToken ) {
            Assert.IsTrue( parameters.TryGetValue( "client_assertion_type", out string? assertionType ) );
            Assert.AreEqual( "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", assertionType );
            Assert.IsTrue( parameters.TryGetValue( "client_assertion", out string? assertion ) );
            JsonElement metadata = await MetadataRetriever( cancellationToken );
            Assert.AreEqual( "private_key_jwt", metadata.GetProperty( "token_endpoint_auth_method" ).GetString( ) );
            string clientId = metadata.GetProperty( "client_id" ).GetString( )!;
            JwtSecurityTokenHandler handler = new( );
            JwtSecurityToken token = handler.ReadJwtToken( assertion! );
            Assert.AreEqual( "ES256", token.Header.Alg );
            Assert.AreEqual( clientId, token.Issuer );
            Assert.AreEqual( clientId, token.Subject );
            string keyId = token.Header.Kid!;
            JsonElement publicKey = metadata.GetProperty( "jwks" ).GetProperty( "keys" ).EnumerateArray( )
                .Single( key => key.GetProperty( "kid" ).GetString( ) == keyId );
            using ECDsa verificationKey = ECDsa.Create( new ECParameters {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint {
                    X = Base64UrlEncoder.DecodeBytes( publicKey.GetProperty( "x" ).GetString( )! ),
                    Y = Base64UrlEncoder.DecodeBytes( publicKey.GetProperty( "y" ).GetString( )! )
                }
            } );
            string[] assertionParts = assertion!.Split( '.' );
            byte[] signingInput = Encoding.ASCII.GetBytes( assertionParts[0] + "." + assertionParts[1] );
            byte[] assertionSignature = Base64UrlEncoder.DecodeBytes( assertionParts[2] );
            Assert.IsTrue( verificationKey.VerifyData( signingInput, assertionSignature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation ), "ES256 assertion signature did not match the served public JWK." );
            try {
                _ = handler.ValidateToken( assertion!, new TokenValidationParameters {
                    ValidateIssuer = true,
                    ValidIssuer = clientId,
                    ValidateAudience = true,
                    ValidAudience = Service,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds( 5 ),
                    IssuerSigningKey = new ECDsaSecurityKey( verificationKey ) {
                        KeyId = keyId,
                        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                    }
                }, out _ );
            } catch (Exception exception) {
                LastAssertionValidationFailure = exception.ToString( );
                throw;
            }
            ClientAssertionKeyIds.Enqueue( keyId );
        }

        private static Dictionary<string, string> ParseParameters( string query ) =>
            QueryHelpers.ParseQuery( query ).ToDictionary( item => item.Key, item => item.Value.ToString( ) );
    }
}
#pragma warning restore CS1591
