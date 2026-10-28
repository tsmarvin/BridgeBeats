using BridgeBeats.Contracts.Interfaces; // Added for IMediaLinkService
using BridgeBeats.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests the startup configuration validation performed by
/// <see cref="StartupExtensions.AddBridgeBeatsServices"/>, which composes the application's services
/// from configuration and fails fast when required settings are missing or invalid.
/// </summary>
/// <remarks>
/// Each test builds an in-memory configuration and asserts the specific exception (and message
/// fragment) the registration raises: a missing or empty Apple key file, no provider credentials at
/// all, and non-positive card-cache tuning values. One test confirms the happy path where only Spotify
/// credentials are present and <see cref="IMediaLinkService"/> ends up registered.
/// </remarks>
[TestClass]
public class ConfigurationValidationTests {
    /// <summary>
    /// Verifies that a configured Apple key path pointing at a nonexistent file throws
    /// <see cref="FileNotFoundException"/> whose message references the <c>.p8</c> key.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithMissingAppleKeyFile_ShouldThrowFileNotFoundException( ) {
        // Arrange - provide ALL Apple credentials (TeamId, KeyId, AND KeyPath) with missing key file
        // This ensures we hit the file validation logic
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:AppleTeamId"] = "TEAM123456",
            ["BridgeBeats:AppleKeyId"] = "KEY1234567",
            ["BridgeBeats:AppleKeyPath"] = "/nonexistent/path/key.p8",
            ["BridgeBeats:SpotifyClientId"] = string.Empty,
            ["BridgeBeats:SpotifyClientSecret"] = string.Empty,
            ["BridgeBeats:TidalClientId"] = string.Empty,
            ["BridgeBeats:TidalClientSecret"] = string.Empty,
            ["BridgeBeats:DiscordToken"] = string.Empty,
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory;Cache=Shared",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:ATProtoIdentifier"] = string.Empty,
            ["BridgeBeats:ATProtoPassword"] = string.Empty,
        };
        IServiceCollection services = new ServiceCollection( );
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

        // Act & Assert
        FileNotFoundException ex = Assert.ThrowsExactly<FileNotFoundException>( () => {
            _ = services.AddBridgeBeatsServices( config );
        } );
        Assert.Contains( ".p8", ex.Message );
    }

    /// <summary>
    /// Verifies that an Apple key file that exists but is empty throws
    /// <see cref="InvalidDataException"/> whose message notes the missing contents. The temporary file
    /// is cleaned up in a <c>finally</c> block.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithEmptyAppleKeyFile_ShouldThrowInvalidDataException( ) {
        // Arrange - create empty temp key file
        string emptyKeyPath = Path.Combine( Path.GetTempPath( ), $"empty_key_{Guid.NewGuid()}.p8" );
        File.WriteAllText( emptyKeyPath, string.Empty );

        try {
            Dictionary<string, string?> overrides = new( ) {
                ["BridgeBeats:AppleTeamId"] = "TEAM123456",
                ["BridgeBeats:AppleKeyId"] = "KEY1234567",
                ["BridgeBeats:AppleKeyPath"] = emptyKeyPath,
                ["BridgeBeats:SpotifyClientId"] = string.Empty,
                ["BridgeBeats:SpotifyClientSecret"] = string.Empty,
                ["BridgeBeats:TidalClientId"] = string.Empty,
                ["BridgeBeats:TidalClientSecret"] = string.Empty,
                ["BridgeBeats:DiscordToken"] = string.Empty,
                ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory;Cache=Shared",
                ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
                ["BridgeBeats:ATProtoIdentifier"] = string.Empty,
                ["BridgeBeats:ATProtoPassword"] = string.Empty,
            };
            IServiceCollection services = new ServiceCollection( );
            IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

            // Act & Assert
            InvalidDataException ex = Assert.ThrowsExactly<InvalidDataException>( () => {
                _ = services.AddBridgeBeatsServices( config );
            } );
            Assert.Contains( "missing contents", ex.Message );
        } finally {
            if (File.Exists( emptyKeyPath )) { File.Delete( emptyKeyPath ); }
        }
    }

    /// <summary>
    /// Verifies that supplying no provider credentials at all throws
    /// <see cref="InvalidOperationException"/> reporting that required settings are missing.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithNoProviders_ShouldThrowInvalidOperationException( ) {
        // Arrange - no Apple, Spotify, or Tidal credentials
        // Ensure Discord is also disabled so we properly hit the provider validation
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:AppleTeamId"] = string.Empty,
            ["BridgeBeats:AppleKeyId"] = string.Empty,
            ["BridgeBeats:AppleKeyPath"] = string.Empty,
            ["BridgeBeats:SpotifyClientId"] = string.Empty,
            ["BridgeBeats:SpotifyClientSecret"] = string.Empty,
            ["BridgeBeats:TidalClientId"] = string.Empty,
            ["BridgeBeats:TidalClientSecret"] = string.Empty,
            ["BridgeBeats:DiscordToken"] = string.Empty,
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:ATProtoIdentifier"] = string.Empty,
            ["BridgeBeats:ATProtoPassword"] = string.Empty,
        };
        IServiceCollection services = new ServiceCollection( );
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

        // Act & Assert
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>( () => {
            _ = services.AddBridgeBeatsServices( config );
        } );
        Assert.Contains( "Required settings are missing", ex.Message );
    }

    /// <summary>
    /// Verifies the happy path: with only Spotify credentials configured, registration succeeds and
    /// <see cref="IMediaLinkService"/> resolves from the built service provider.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithOnlySpotifyCredentials_ShouldSucceed( ) {
        // Arrange - minimal Spotify config
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:NodeNumber"] = "1",
            ["BridgeBeats:AppleTeamId"] = string.Empty,
            ["BridgeBeats:AppleKeyId"] = string.Empty,
            ["BridgeBeats:AppleKeyPath"] = string.Empty,
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:TidalClientId"] = string.Empty,
            ["BridgeBeats:TidalClientSecret"] = string.Empty,
            ["BridgeBeats:DiscordToken"] = string.Empty,
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=BridgeBeats;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:ATProtoIdentifier"] = string.Empty,
            ["BridgeBeats:ATProtoPassword"] = string.Empty,
        };

        // Act
        IServiceCollection services = new ServiceCollection( );

        // SpotifyLookupService now requires IGenreCacheService (matching the Apple/Tidal direct
        // lookup services). The real host registers it via AddGenreCache() ahead of this call; this
        // test bypasses that host wiring, so it substitutes a bare instance to satisfy the dependency.
        _ = services.AddSingleton( new Mock<IGenreCacheService>( ).Object );

        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );
        _ = services.AddBridgeBeatsServices( config );
        ServiceProvider sp = services.BuildServiceProvider( );

        // Assert
        Assert.IsNotNull( sp );
        IMediaLinkService? mediaService = sp.GetService<IMediaLinkService>();
        Assert.IsNotNull( mediaService, "IMediaLinkService should be registered with Spotify credentials" );
    }

    /// <summary>Local development leaves Bluesky disabled when no signing key is configured.</summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithLocalDomainAndNoSigningKey_SkipsBluesky( ) {
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( new Dictionary<string, string?> {
            ["BridgeBeats:Domain"] = "localhost",
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt"
        } ).Build( );
        IServiceCollection services = new ServiceCollection( );
        _ = services.AddSingleton( new Mock<IGenreCacheService>( ).Object );

        _ = services.AddBridgeBeatsServices( config );

        using ServiceProvider provider = services.BuildServiceProvider( );
        Assert.IsFalse( provider.GetRequiredService<AuthenticationFeatureState>( ).BlueskyEnabled );
    }

    /// <summary>Fail-fast applies when invalid authentication configuration would stop startup.</summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithFailFastAndDomainAndNoSigningKey_FailsFast( ) {
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( new Dictionary<string, string?> {
            ["BridgeBeats:Domain"] = "bridgebeats.example"
        } ).Build( );

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>( ( ) =>
            new ServiceCollection( ).AddBridgeBeatsServices( config, failFastOnInvalidAuthConfiguration: true ) );
        Assert.Contains( "ATProtoOAuthSigningKeyPath", error.Message );
    }

    /// <summary>
    /// Any deployed environment other than Development or Testing must fail fast, so a staging
    /// deployment cannot silently start without Bluesky sign-in. Exercises the environment rule the
    /// host itself applies rather than restating it.
    /// </summary>
    [TestMethod]
    public void RequiresStrictAuthConfiguration_OnlyRelaxesDevelopmentAndTesting( ) {
        (string Environment, bool Strict)[] cases = [
            ("Production", true),
            ("Staging", true),
            ("QA", true),
            ("Development", false),
            ("Testing", false)
        ];

        foreach ((string environmentName, bool strict) in cases) {
            StubHostEnvironment environment = new( ) { EnvironmentName = environmentName };
            Assert.AreEqual( strict, StartupExtensions.RequiresStrictAuthConfiguration( environment ), environmentName );
        }
    }

    /// <summary>
    /// A staging deployment with an invalid domain fails startup instead of skipping Bluesky,
    /// which is how the flag the host computes behaves in that environment.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_OutsideDevelopmentAndTesting_FailsFastOnInvalidDomain( ) {
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( new Dictionary<string, string?> {
            ["BridgeBeats:Domain"] = "http://invalid.example/path",
            ["BridgeBeats:ATProtoOAuthSigningKeyPath"] = "/not/a/real/key"
        } ).Build( );
        StubHostEnvironment staging = new( ) { EnvironmentName = "Staging" };

        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>( ( ) =>
            new ServiceCollection( ).AddBridgeBeatsServices(
                config,
                failFastOnInvalidAuthConfiguration: StartupExtensions.RequiresStrictAuthConfiguration( staging ) ) );
        Assert.Contains( "BridgeBeats:Domain", error.Message );
    }

    /// <summary>Development still relaxes an invalid domain instead of failing startup.</summary>
    [TestMethod]
    public void AddBridgeBeatsServices_InDevelopmentWithInvalidDomain_SkipsBluesky( ) {
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( new Dictionary<string, string?> {
            ["BridgeBeats:Domain"] = "http://invalid.example/path",
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:ATProtoOAuthSigningKeyPath"] = "/not/a/real/key"
        } ).Build( );
        IServiceCollection services = new ServiceCollection( );
        _ = services.AddSingleton( new Mock<IGenreCacheService>( ).Object );

        _ = services.AddBridgeBeatsServices( config );

        using ServiceProvider provider = services.BuildServiceProvider( );
        Assert.IsFalse( provider.GetRequiredService<AuthenticationFeatureState>( ).BlueskyEnabled );
    }

    /// <summary>
    /// Verifies that a <c>CardCacheExpirationHours</c> of zero throws
    /// <see cref="InvalidOperationException"/> requiring the value to be greater than zero.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithZeroCardCacheExpirationHours_ShouldThrowInvalidOperationException( ) {
        // Arrange - valid Spotify credentials but invalid card cache expiration
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:CardCacheExpirationHours"] = "0",
            ["BridgeBeats:CardCacheCleanupInterval"] = "500",
        };
        IServiceCollection services = new ServiceCollection( );
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

        // Act & Assert
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>( () => {
            _ = services.AddBridgeBeatsServices( config );
        } );
        Assert.Contains( "CardCacheExpirationHours must be greater than zero", ex.Message );
    }

    /// <summary>
    /// Verifies that a negative <c>CardCacheExpirationHours</c> throws
    /// <see cref="InvalidOperationException"/> requiring the value to be greater than zero.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithNegativeCardCacheExpirationHours_ShouldThrowInvalidOperationException( ) {
        // Arrange - valid Spotify credentials but invalid card cache expiration
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:CardCacheExpirationHours"] = "-1",
            ["BridgeBeats:CardCacheCleanupInterval"] = "500",
        };
        IServiceCollection services = new ServiceCollection( );
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

        // Act & Assert
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>( () => {
            _ = services.AddBridgeBeatsServices( config );
        } );
        Assert.Contains( "CardCacheExpirationHours must be greater than zero", ex.Message );
    }

    /// <summary>
    /// Verifies that a <c>CardCacheCleanupInterval</c> of zero throws
    /// <see cref="InvalidOperationException"/> requiring the value to be greater than zero.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithZeroCardCacheCleanupInterval_ShouldThrowInvalidOperationException( ) {
        // Arrange - valid Spotify credentials but invalid cleanup interval
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:CardCacheExpirationHours"] = "1",
            ["BridgeBeats:CardCacheCleanupInterval"] = "0",
        };
        IServiceCollection services = new ServiceCollection( );
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

        // Act & Assert
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>( () => {
            _ = services.AddBridgeBeatsServices( config );
        } );
        Assert.Contains( "CardCacheCleanupInterval must be greater than zero", ex.Message );
    }

    /// <summary>
    /// Verifies that a negative <c>CardCacheCleanupInterval</c> throws
    /// <see cref="InvalidOperationException"/> requiring the value to be greater than zero.
    /// </summary>
    [TestMethod]
    public void AddBridgeBeatsServices_WithNegativeCardCacheCleanupInterval_ShouldThrowInvalidOperationException( ) {
        // Arrange - valid Spotify credentials but invalid cleanup interval
        Dictionary<string, string?> overrides = new( ) {
            ["BridgeBeats:SpotifyClientId"] = "spotify_client_id",
            ["BridgeBeats:SpotifyClientSecret"] = "spotify_secret",
            ["BridgeBeats:IdentityConnectionString"] = "Data Source=Identity;Mode=Memory",
            ["BridgeBeats:ApiKeySalt"] = "api_key_salt",
            ["BridgeBeats:CardCacheExpirationHours"] = "1",
            ["BridgeBeats:CardCacheCleanupInterval"] = "-1",
        };
        IServiceCollection services = new ServiceCollection( );
        IConfiguration config = new ConfigurationBuilder( ).AddInMemoryCollection( overrides ).Build( );

        // Act & Assert
        InvalidOperationException ex = Assert.ThrowsExactly<InvalidOperationException>( () => {
            _ = services.AddBridgeBeatsServices( config );
        } );
        Assert.Contains( "CardCacheCleanupInterval must be greater than zero", ex.Message );
    }

    /// <summary>Supplies just the environment name for the host's strict-configuration rule.</summary>
    private sealed class StubHostEnvironment : IHostEnvironment {
        public string EnvironmentName { get; set; } = string.Empty;

        public string ApplicationName { get; set; } = string.Empty;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider( );
    }
}
