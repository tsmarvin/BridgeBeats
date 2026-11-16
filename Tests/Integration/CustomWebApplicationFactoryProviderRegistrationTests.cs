using System.Security.Cryptography;
using System.Text;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Domain.Providers.AppleMusic;
using BridgeBeats.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Checks that a test host built by <see cref="CustomWebApplicationFactory"/> registers exactly the
/// providers its configuration calls for, and that credentials present only in the machine's
/// environment — a developer's own user secrets, or process variables — cannot change what the host
/// registers. The factory supplies isolated credential values at higher precedence than the
/// environment, so these tests assert on the resulting provider registrations and on the exact
/// service registrations that the live Apple credentials would have produced.
/// </summary>
/// <remarks>
/// The credentials planted in the environment by these tests point at a key file that does not
/// exist, so a factory that failed to suppress them would fail host start-up outright rather than
/// quietly register an extra provider. Each planted variable is removed again in a <c>finally</c>
/// block, and the tests run serially ([DoNotParallelize]) because the process environment is shared.
/// </remarks>
[TestClass]
[DoNotParallelize]
[TestCategory( "Integration" )]
[TestCategory( "Docker" )]
public class CustomWebApplicationFactoryProviderRegistrationTests {

    private const string AppleTeamIdKey = "BridgeBeats__AppleTeamId";
    private const string AppleKeyIdKey = "BridgeBeats__AppleKeyId";
    private const string AppleKeyPathKey = "BridgeBeats__AppleKeyPath";
    private const string SpotifyClientIdKey = "BridgeBeats__SpotifyClientId";
    private const string SpotifyClientSecretKey = "BridgeBeats__SpotifyClientSecret";
    private const string TidalClientIdKey = "BridgeBeats__TidalClientId";
    private const string TidalClientSecretKey = "BridgeBeats__TidalClientSecret";
    private const string ATProtoIdentifierKey = "BridgeBeats__ATProtoIdentifier";
    private const string ATProtoPasswordKey = "BridgeBeats__ATProtoPassword";
    private const string ATProtoUserDidKey = "BridgeBeats__ATProtoUserDID";
    private const string SigningKeyPathKey = "BridgeBeats__ATProtoOAuthSigningKeyPath";
    private const string UseWorkerServicesKey = "BridgeBeats__Workers__UseWorkerServices";
    private const string SpotifyWorkerEnabledKey = "BridgeBeats__Workers__SpotifyWorkerEnabled";
    private const string AppleMusicWorkerEnabledKey = "BridgeBeats__Workers__AppleMusicWorkerEnabled";
    private const string TidalWorkerEnabledKey = "BridgeBeats__Workers__TidalWorkerEnabled";

    private static readonly string[] s_credentialKeys = [
        AppleTeamIdKey, AppleKeyIdKey, AppleKeyPathKey,
        SpotifyClientIdKey, SpotifyClientSecretKey,
        TidalClientIdKey, TidalClientSecretKey,
        ATProtoIdentifierKey, ATProtoPasswordKey, ATProtoUserDidKey, SigningKeyPathKey,
        UseWorkerServicesKey, SpotifyWorkerEnabledKey, AppleMusicWorkerEnabledKey, TidalWorkerEnabledKey,
    ];

    private readonly Dictionary<string, string?> _ambientSnapshot = [];

    /// <summary>Captures the ambient values of the credential variables these tests may plant.</summary>
    [TestInitialize]
    public void SnapshotAmbient( ) {
        foreach (string key in s_credentialKeys) {
            _ambientSnapshot[key] = Environment.GetEnvironmentVariable( key );
        }
    }

    /// <summary>Restores the ambient credential variables after each test.</summary>
    [TestCleanup]
    public void RestoreAmbient( ) {
        foreach (string key in s_credentialKeys) {
            Environment.SetEnvironmentVariable( key, _ambientSnapshot.TryGetValue( key, out string? v ) ? v : null );
        }
    }

    /// <summary>
    /// With every provider credential planted in the environment — the Apple path pointing at a file
    /// this machine does not have — the host still starts and registers only Spotify, the single
    /// provider its own configuration supplies.
    /// </summary>
    [TestMethod]
    public void AmbientCredentials_DoNotAddProvidersToTheRegisteredSet( ) {
        PlantAmbientCredentials( );

        using CustomWebApplicationFactory factory = new( );
        using HttpClient client = factory.CreateClient( );

        HashSet<SupportedProviders> enabled = factory.Services.GetRequiredService<HashSet<SupportedProviders>>( );
        Assert.IsNotNull( client, "the host must start despite the ambient Apple key path being unresolvable" );
        CollectionAssert.AreEquivalent(
            new[] { SupportedProviders.Spotify }, enabled.ToArray( ),
            "only the provider the factory configured may be registered" );
        Assert.IsNull(
            factory.Services.GetService<AppleJwtHandler>( ),
            "the ambient Apple credentials must not register the Apple developer-token handler" );
    }

    /// <summary>
    /// Worker enablement planted in the environment does not change what a default factory registers.
    /// When worker services are enabled the enabled provider set comes from the per-provider worker
    /// flags rather than from provider credentials, so an ambient <c>true</c> for those flags would
    /// silently register providers a test never asked for.
    /// </summary>
    [TestMethod]
    public void AmbientWorkerFlags_DoNotChangeTheRegisteredProviderSet( ) {
        Environment.SetEnvironmentVariable( UseWorkerServicesKey, "true" );
        Environment.SetEnvironmentVariable( SpotifyWorkerEnabledKey, "true" );
        Environment.SetEnvironmentVariable( AppleMusicWorkerEnabledKey, "true" );
        Environment.SetEnvironmentVariable( TidalWorkerEnabledKey, "true" );

        using CustomWebApplicationFactory factory = new( );
        using HttpClient client = factory.CreateClient( );

        HashSet<SupportedProviders> enabled = factory.Services.GetRequiredService<HashSet<SupportedProviders>>( );
        Assert.IsNotNull( client );
        CollectionAssert.AreEquivalent(
            new[] { SupportedProviders.Spotify }, enabled.ToArray( ),
            "ambient worker flags must not add providers to a default factory's registered set" );
    }

    /// <summary>
    /// A host built from the isolated settings plus a domain — the configuration the card-store
    /// authorization fixture uses — starts and registers only Spotify even when the machine's
    /// environment carries Apple identifiers pointing at a key file it does not have.
    /// </summary>
    /// <remarks>
    /// Failure-first evidence: while the fixtures copied the ambient configuration into their hosts,
    /// this ambient Apple set produced <c>FileNotFoundException: Missing .p8 file at: …</c> during
    /// provider registration and failed the whole card-store authorization class.
    /// </remarks>
    [TestMethod]
    public void AmbientStaleAppleKeyPath_DoesNotBreakAnIsolatedHost( ) {
        Environment.SetEnvironmentVariable( AppleTeamIdKey, "AMBIENT_TEAM" );
        Environment.SetEnvironmentVariable( AppleKeyIdKey, "AMBIENT_KEY" );
        Environment.SetEnvironmentVariable(
            AppleKeyPathKey, Path.Combine( Path.GetTempPath( ), $"AMBIENT_ABSENT_{Guid.NewGuid( ):N}.p8" ) );

        Dictionary<string, string?> config = TestHostConfiguration.Isolated( );
        config["BridgeBeats:Domain"] = "bridgebeats.link";
        using CustomWebApplicationFactory factory = new( config );
        using HttpClient client = factory.CreateClient( );

        IConfiguration resolved = factory.Services.GetRequiredService<IConfiguration>( );
        Assert.IsNotNull( client, "the host must start despite the ambient Apple key path being unresolvable" );
        Assert.IsEmpty( resolved["BridgeBeats:AppleKeyPath"] ?? "",
            "the ambient stale Apple key path must not reach the host's configuration" );
        CollectionAssert.AreEquivalent(
            new[] { SupportedProviders.Spotify },
            factory.Services.GetRequiredService<HashSet<SupportedProviders>>( ).ToArray( ) );
    }

    /// <summary>
    /// A caller that deliberately selects the worker branch still gets it: the per-provider worker
    /// flags it supplies drive the enabled provider set, so pinning the defaults does not remove the
    /// production worker path from the tests that need it.
    /// </summary>
    [TestMethod]
    public void ExplicitWorkerFlags_SelectTheWorkerBranch( ) {
        using CustomWebApplicationFactory factory = new( new Dictionary<string, string?> {
            ["BridgeBeats:Workers:UseWorkerServices"] = "true",
            ["BridgeBeats:Workers:SpotifyWorkerEnabled"] = "true",
            ["BridgeBeats:Workers:AppleMusicWorkerEnabled"] = "true",
            ["BridgeBeats:Workers:TidalWorkerEnabled"] = "false",
        } );
        using HttpClient client = factory.CreateClient( );

        HashSet<SupportedProviders> enabled = factory.Services.GetRequiredService<HashSet<SupportedProviders>>( );
        Assert.IsNotNull( client );
        CollectionAssert.AreEquivalent(
            new[] { SupportedProviders.Spotify, SupportedProviders.AppleMusic }, enabled.ToArray( ),
            "explicit worker flags must select the worker branch that they, not the credentials, drive" );
    }

    /// <summary>
    /// Apple credentials that point at a real key file still register the Apple provider through the
    /// application's normal provider registration, and the registered handler is built from that key.
    /// This is the discriminating control for the test above: it proves the Apple path is exercised by
    /// the same code rather than being unreachable, which is what makes an empty registration
    /// meaningful.
    /// </summary>
    [TestMethod]
    public void ConfiguredAppleCredentials_RegisterAppleMusicThroughNormalRegistration( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-apple-{Guid.NewGuid( ):N}.p8" );
        string keyPem = CreateEcdsaPem( );
        File.WriteAllText( keyPath, keyPem );
        try {
            using CustomWebApplicationFactory factory = new( new Dictionary<string, string?> {
                ["BridgeBeats:AppleTeamId"] = "TEAMID1234",
                ["BridgeBeats:AppleKeyId"] = "KEYID12345",
                ["BridgeBeats:AppleKeyPath"] = keyPath,
            } );
            using HttpClient client = factory.CreateClient( );

            HashSet<SupportedProviders> enabled = factory.Services.GetRequiredService<HashSet<SupportedProviders>>( );
            CollectionAssert.AreEquivalent(
                new[] { SupportedProviders.Spotify, SupportedProviders.AppleMusic }, enabled.ToArray( ),
                "configured Apple credentials must enable Apple Music alongside Spotify" );
            Assert.IsNotNull(
                factory.Services.GetRequiredService<AppleJwtHandler>( ),
                "configured Apple credentials must register the Apple developer-token handler" );
        } finally {
            File.Delete( keyPath );
        }
    }

    /// <summary>
    /// A provider credential the environment supplies is replaced, not merged: with Spotify planted in
    /// the environment and the factory overriding both halves of the pair with its own values, the
    /// registered credentials are the configured ones and the host still starts.
    /// </summary>
    [TestMethod]
    public void ConfiguredCredentials_OutrankAmbientEnvironmentValues( ) {
        Environment.SetEnvironmentVariable( SpotifyClientIdKey, "AMBIENT_SPOTIFY_ID" );
        Environment.SetEnvironmentVariable( SpotifyClientSecretKey, "AMBIENT_SPOTIFY_SECRET" );

        using CustomWebApplicationFactory factory = new( );
        using HttpClient client = factory.CreateClient( );

        IConfiguration config = factory.Services.GetRequiredService<IConfiguration>( );
        Assert.IsNotNull( client );
        Assert.AreEqual( "test", config["BridgeBeats:SpotifyClientId"],
            "the configured value must outrank the environment value" );
        Assert.AreEqual( "test", config["BridgeBeats:SpotifyClientSecret"],
            "the configured value must outrank the environment value" );
    }

    /// <summary>
    /// Two factories built over different provider configurations hold independent registrations when
    /// their hosts are alive at the same time. Each instance supplies its own credentials, so creating
    /// one factory cannot change what an already-running one registered.
    /// </summary>
    [TestMethod]
    public void OverlappingFactories_KeepIndependentProviderRegistrations( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-apple-{Guid.NewGuid( ):N}.p8" );
        File.WriteAllText( keyPath, CreateEcdsaPem( ) );
        try {
            using CustomWebApplicationFactory spotifyOnly = new( );
            using CustomWebApplicationFactory withApple = new( new Dictionary<string, string?> {
                ["BridgeBeats:AppleTeamId"] = "TEAMID1234",
                ["BridgeBeats:AppleKeyId"] = "KEYID12345",
                ["BridgeBeats:AppleKeyPath"] = keyPath,
            } );

            // Force both hosts to start while the other is still alive.
            using HttpClient spotifyClient = spotifyOnly.CreateClient( );
            using HttpClient appleClient = withApple.CreateClient( );

            CollectionAssert.AreEquivalent(
                new[] { SupportedProviders.Spotify },
                spotifyOnly.Services.GetRequiredService<HashSet<SupportedProviders>>( ).ToArray( ) );
            CollectionAssert.AreEquivalent(
                new[] { SupportedProviders.Spotify, SupportedProviders.AppleMusic },
                withApple.Services.GetRequiredService<HashSet<SupportedProviders>>( ).ToArray( ) );
        } finally {
            File.Delete( keyPath );
        }
    }

    /// <summary>
    /// Environment variables are left exactly as the test found them, whether or not a host was ever
    /// built: constructing a factory neither sets nor restores credential variables.
    /// </summary>
    [TestMethod]
    public void ConstructingFactory_LeavesEnvironmentUntouched( ) {
        Environment.SetEnvironmentVariable( AppleTeamIdKey, "AMBIENT_TEAM" );
        Environment.SetEnvironmentVariable( SigningKeyPathKey, "/ambient/signing-key.json" );

        using (CustomWebApplicationFactory factory = new( )) {
            Assert.AreEqual( "AMBIENT_TEAM", Environment.GetEnvironmentVariable( AppleTeamIdKey ),
                "constructing a factory must not rewrite the process environment" );
            Assert.AreEqual( "/ambient/signing-key.json", Environment.GetEnvironmentVariable( SigningKeyPathKey ),
                "constructing a factory must not rewrite the process environment" );
        }

        Assert.AreEqual( "AMBIENT_TEAM", Environment.GetEnvironmentVariable( AppleTeamIdKey ),
            "disposing a factory must not rewrite the process environment" );
        Assert.AreEqual( "/ambient/signing-key.json", Environment.GetEnvironmentVariable( SigningKeyPathKey ),
            "disposing a factory must not rewrite the process environment" );
    }

    /// <summary>
    /// Plants a developer-style credential set in the process environment, with the Apple key path
    /// pointing at a file that does not exist.
    /// </summary>
    private static void PlantAmbientCredentials( ) {
        Environment.SetEnvironmentVariable( AppleTeamIdKey, "AMBIENT_TEAM" );
        Environment.SetEnvironmentVariable( AppleKeyIdKey, "AMBIENT_KEY" );
        Environment.SetEnvironmentVariable(
            AppleKeyPathKey, Path.Combine( Path.GetTempPath( ), $"AMBIENT_ABSENT_{Guid.NewGuid( ):N}.p8" ) );
        Environment.SetEnvironmentVariable( SpotifyClientIdKey, "AMBIENT_SPOTIFY_ID" );
        Environment.SetEnvironmentVariable( SpotifyClientSecretKey, "AMBIENT_SPOTIFY_SECRET" );
        Environment.SetEnvironmentVariable( TidalClientIdKey, "AMBIENT_TIDAL_ID" );
        Environment.SetEnvironmentVariable( TidalClientSecretKey, "AMBIENT_TIDAL_SECRET" );
        Environment.SetEnvironmentVariable( ATProtoIdentifierKey, "AMBIENT_IDENTIFIER" );
        Environment.SetEnvironmentVariable( ATProtoPasswordKey, "AMBIENT_PASSWORD" );
        Environment.SetEnvironmentVariable( ATProtoUserDidKey, "did:plc:ambiguous" );
        Environment.SetEnvironmentVariable( SigningKeyPathKey, "/ambient/signing-key.json" );
    }

    /// <summary>Creates a throwaway ES256 private key in PEM form for provider registration.</summary>
    private static string CreateEcdsaPem( ) {
        using ECDsa ecdsa = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
        StringBuilder pem = new( ecdsa.ExportECPrivateKeyPem( ) );
        return pem.ToString( );
    }
}
#pragma warning restore CS1591
