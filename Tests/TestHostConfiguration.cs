using System.Security.Cryptography;
using BridgeBeats.Contracts.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace BridgeBeats.Tests;

/// <summary>
/// Builds the configuration dictionaries handed to <see cref="CustomWebApplicationFactory"/>, so each
/// fixture states the settings its tests actually depend on instead of copying whatever
/// <c>appsettings.json</c>, user secrets, and the environment happen to hold on the machine running the
/// tests.
/// </summary>
/// <remarks>
/// <para>
/// Copying the ambient configuration made a test host's behavior depend on the developer's own
/// credentials: a key path for a <c>.p8</c> file that exists on one machine and not another, or worker
/// flags exported in one shell, changed which providers a host registered. Those hosts then failed with
/// errors that named neither the setting nor the test that depended on it. Supplying only intentional
/// values keeps each host a function of its own test.
/// </para>
/// <para>
/// A live provider credential is described by <see cref="LiveCredentialState"/>, which separates three
/// situations that a single boolean would conflate: no credential is configured (a property of the
/// machine, reported as inconclusive), a credential is configured and usable (the test runs), and a
/// credential is configured but unusable (a configuration error, reported as a failure so it cannot
/// hide behind a skip).
/// </para>
/// </remarks>
internal static class TestHostConfiguration {

    /// <summary>
    /// Describes what this machine supplies for a provider: no credential, a usable one, or one that is
    /// configured but cannot be used.
    /// </summary>
    internal enum LiveCredentialState {
        /// <summary>No credential is configured for the provider.</summary>
        Absent,
        /// <summary>A complete, usable credential is configured.</summary>
        Usable,
        /// <summary>A credential is configured but cannot be used as it stands.</summary>
        Unusable,
    }

    /// <summary>
    /// The settings a host needs to start deterministically: the external integrations an in-memory host
    /// cannot run are disabled, worker wiring stays in direct mode, and no provider credential is
    /// supplied, so the enabled provider set is exactly the one the calling test's assertions describe.
    /// </summary>
    /// <returns>A new configuration dictionary for a deterministic test host.</returns>
    public static Dictionary<string, string?> Isolated( ) => new( ) {
        ["BridgeBeats:DiscordToken"] = "",
        ["BridgeBeats:Workers:UseWorkerServices"] = "false",
        ["BridgeBeats:ATProtoIdentifier"] = "",
        ["BridgeBeats:ATProtoPassword"] = "",
        ["BridgeBeats:ATProtoUserDID"] = "",
    };

    /// <summary>
    /// The settings a host needs to perform live provider lookups: the isolated settings plus whichever
    /// real provider credentials this machine supplies through <see cref="TestConfiguration"/>.
    /// </summary>
    /// <remarks>
    /// Only the provider credential keys are carried over; the rest of the ambient configuration is not
    /// copied, so an unrelated setting cannot decide which providers register. Each provider's settings
    /// are written explicitly — either its real credential or empty — because the factory's defaults
    /// carry placeholder values for a deterministic host, and leaving a placeholder in place here would
    /// make a live lookup run against it and report the empty result as rate limiting.
    /// </remarks>
    /// <returns>A new configuration dictionary for a live-provider test host.</returns>
    public static Dictionary<string, string?> LiveProviders( ) {
        Dictionary<string, string?> config = Isolated( );
        ApplyOrClear(
            config,
            ["AppleTeamId", "AppleKeyId", "AppleKeyPath"],
            GetLiveCredentialState( SupportedProviders.AppleMusic ) );
        ApplyOrClear(
            config,
            ["SpotifyClientId", "SpotifyClientSecret"],
            GetLiveCredentialState( SupportedProviders.Spotify ) );
        ApplyOrClear(
            config,
            ["TidalClientId", "TidalClientSecret"],
            GetLiveCredentialState( SupportedProviders.Tidal ) );
        return config;
    }

    /// <summary>
    /// The settings for a class-level host that performs live lookups, falling back to the deterministic
    /// settings when this machine supplies no usable provider credential.
    /// </summary>
    /// <remarks>
    /// A class that builds its host once in <c>ClassInitialize</c> cannot defer the decision to the
    /// individual tests, and the application refuses to start with no provider registered at all. The
    /// fallback keeps such a class runnable on a machine with no credentials: the deterministic settings
    /// carry the factory's placeholder provider so the host starts, and each live test still reports the
    /// missing credential through <see cref="RequireLiveCredentials"/> before it runs.
    /// </remarks>
    /// <returns>Settings for a live host, or the deterministic settings when no credential is usable.</returns>
    public static Dictionary<string, string?> LiveProvidersOrDeterministic( ) =>
        LiveProviders( ) is { } live && AnyLiveCredentials( )
            ? live
            : Isolated( );

    /// <summary>
    /// Reports whether this machine supplies a usable credential for any provider.
    /// </summary>
    /// <returns><c>true</c> when at least one provider credential is usable.</returns>
    private static bool AnyLiveCredentials( ) => Enum.GetValues<SupportedProviders>( ).Any( HasLiveCredentials );

    /// <summary>
    /// Reports whether this machine supplies a real, usable credential for the named provider, which is
    /// the precondition for a live lookup against it.
    /// </summary>
    /// <param name="provider">The provider a live test intends to exercise.</param>
    /// <returns><c>true</c> when a usable credential for that provider is configured.</returns>
    public static bool HasLiveCredentials( SupportedProviders provider ) =>
        GetLiveCredentialState( provider ) == LiveCredentialState.Usable;

    /// <summary>
    /// Reports what this machine supplies for the named provider.
    /// </summary>
    /// <param name="provider">The provider a live test intends to exercise.</param>
    /// <returns>The credential state for that provider.</returns>
    public static LiveCredentialState GetLiveCredentialState( SupportedProviders provider ) => provider switch {
        SupportedProviders.Spotify => GetPairState( "SpotifyClientId", "SpotifyClientSecret" ),
        SupportedProviders.Tidal => GetPairState( "TidalClientId", "TidalClientSecret" ),
        SupportedProviders.AppleMusic => GetAppleState( ),
        _ => LiveCredentialState.Absent,
    };

    /// <summary>
    /// Marks the calling test inconclusive unless this machine supplies a usable credential for the
    /// named provider, and fails it when the configured credential cannot be used.
    /// </summary>
    /// <remarks>
    /// Call this before building a live host. Absent credentials are a property of the machine, so the
    /// test reports inconclusive rather than failing; an unusable credential is a configuration error,
    /// and skipping it would let a broken setup pass the suite.
    /// </remarks>
    /// <param name="provider">The provider the calling test needs.</param>
    public static void RequireLiveCredentials( SupportedProviders provider ) =>
        ReportCredentialState( [provider], GetLiveCredentialState( provider ) );

    /// <summary>
    /// Marks the calling test inconclusive unless this machine supplies a usable credential for at least
    /// one of the named providers, for a test whose request may be served by any of them.
    /// </summary>
    /// <param name="providers">The providers any of which the calling test can use.</param>
    public static void RequireAnyLiveCredentials( params SupportedProviders[] providers ) {
        if (providers.Any( HasLiveCredentials )) {
            return;
        }

        // No provider is usable. Report a configuration error if any of them was configured but
        // unusable; otherwise report the plain absence, which is the honest outcome.
        SupportedProviders[] unusable = [.. providers.Where(
            provider => GetLiveCredentialState( provider ) == LiveCredentialState.Unusable )];
        if (unusable.Length > 0) {
            Assert.Fail(
                $"{string.Join( " and ", unusable )} is configured but unusable: " +
                $"{string.Join( " ", unusable.Select( DescribeUnusable ) )} Correct the setting, or " +
                "remove those credentials to let this test report inconclusive." );
        }

        Assert.Inconclusive(
            $"This machine supplies no usable credentials for any of {string.Join( ", ", providers )}, " +
            "so this test cannot perform a live lookup. Provide one of those providers' settings in " +
            "user secrets or appsettings.json to run it." );
    }

    /// <summary>
    /// Asserts that the test host registered the named provider, failing when it did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this from a fixture that supplies the provider's settings itself. A host that does not
    /// register a provider its own configuration enables is a defect in the host or in the provider's
    /// registration path, and skipping it would report that defect as not-run, so this fails instead.
    /// Use <see cref="RequireLiveCredentials"/> for a provider whose credential comes from the machine:
    /// its absence is a property of the machine and stays inconclusive.
    /// </para>
    /// <para>
    /// Without this check a test that needs a provider can pass by exercising a host that silently
    /// registered a different provider, or fail with an error that does not identify the cause. Both
    /// outcomes misrepresent the run.
    /// </para>
    /// </remarks>
    /// <param name="services">The test host's service provider.</param>
    /// <param name="provider">The provider the calling test requires the host to have registered.</param>
    public static void RequireRegistered( IServiceProvider services, SupportedProviders provider ) {
        HashSet<SupportedProviders> enabled = services.GetRequiredService<HashSet<SupportedProviders>>( );
        if (!enabled.Contains( provider )) {
            Assert.Fail(
                $"The test host did not register {provider}, even though this test supplies the settings " +
                $"that enable it. That is a defect in the host or in {provider}'s registration path — not " +
                "a credential missing from this machine — so this test fails rather than reporting " +
                "inconclusive." );
        }
    }

    /// <summary>
    /// Marks the calling test inconclusive or failing according to the supplied credential state,
    /// naming the provider and, for an unusable credential, the setting to correct.
    /// </summary>
    /// <param name="providers">The providers the calling test needs.</param>
    /// <param name="state">What this machine supplies for those providers.</param>
    private static void ReportCredentialState( SupportedProviders[] providers, LiveCredentialState state ) {
        string names = string.Join( ", ", providers );
        switch (state) {
            case LiveCredentialState.Usable:
                return;
            case LiveCredentialState.Unusable:
                Assert.Fail(
                    $"{names} is configured but unusable: {string.Join( " ", providers.Select( DescribeUnusable ) )} " +
                    "Correct the setting, or remove those credentials to let this test report inconclusive." );
                return;
            default:
                Assert.Inconclusive(
                    $"This machine supplies no usable {names} credentials, so this test cannot perform a " +
                    "live lookup. Provide those settings in user secrets or appsettings.json to run it." );
                return;
        }
    }

    /// <summary>
    /// Classifies a client-id/client-secret pair: usable when both halves are configured, unusable when
    /// only one is, absent when neither is.
    /// </summary>
    /// <param name="idSetting">The client-id setting name, without its section prefix.</param>
    /// <param name="secretSetting">The client-secret setting name, without its section prefix.</param>
    /// <returns>The credential state for the pair.</returns>
    private static LiveCredentialState GetPairState( string idSetting, string secretSetting ) =>
        ClassifyPair(
            TestConfiguration.GetValue( $"BridgeBeats:{idSetting}" ),
            TestConfiguration.GetValue( $"BridgeBeats:{secretSetting}" ) );

    /// <summary>
    /// Classifies a client-id/client-secret pair from the supplied values.
    /// </summary>
    /// <param name="id">The configured client id, or null when unset or blank.</param>
    /// <param name="secret">The configured client secret, or null when unset or blank.</param>
    /// <returns>The credential state for the pair.</returns>
    internal static LiveCredentialState ClassifyPair( string? id, string? secret ) {
        if (id is not null && secret is not null) {
            return LiveCredentialState.Usable;
        }
        return id is null && secret is null
            ? LiveCredentialState.Absent
            : LiveCredentialState.Unusable;
    }

    /// <summary>
    /// Classifies the Apple Music credential tuple: usable when all three settings are configured and
    /// the signing key is readable, unusable when the tuple is complete but the key is missing, and
    /// absent when the tuple is incomplete.
    /// </summary>
    /// <returns>The credential state for Apple Music.</returns>
    private static LiveCredentialState GetAppleState( ) =>
        ClassifyApple(
            TestConfiguration.GetValue( "BridgeBeats:AppleTeamId" ),
            TestConfiguration.GetValue( "BridgeBeats:AppleKeyId" ),
            TestConfiguration.GetValue( "BridgeBeats:AppleKeyPath" ),
            File.Exists );

    /// <summary>
    /// Classifies the Apple Music credential tuple from the supplied values.
    /// </summary>
    /// <param name="teamId">The configured team id, or null when unset or blank.</param>
    /// <param name="keyId">The configured key id, or null when unset or blank.</param>
    /// <param name="keyPath">The configured signing key path, or null when unset or blank.</param>
    /// <param name="keyFileExists">Reports whether a signing key path resolves to a readable file.</param>
    /// <returns>The credential state for the tuple.</returns>
    internal static LiveCredentialState ClassifyApple(
        string? teamId,
        string? keyId,
        string? keyPath,
        Func<string, bool> keyFileExists
    ) {
        if (teamId is null || keyId is null || keyPath is null) {
            return LiveCredentialState.Absent;
        }
        return keyFileExists( keyPath ) ? LiveCredentialState.Usable : LiveCredentialState.Unusable;
    }

    /// <summary>
    /// Describes why the named provider's configured credential cannot be used, naming the offending
    /// setting so the diagnostic is actionable.
    /// </summary>
    /// <remarks>
    /// The diagnostic names the setting, never its value: <c>BridgeBeats:AppleKeyPath</c> is a
    /// filesystem path that a container image or CI job may build from environment-specific layout, and
    /// assertion messages are copied into test logs.
    /// </remarks>
    /// <param name="provider">The provider whose credential is unusable.</param>
    /// <returns>A sentence fragment describing the problem.</returns>
    private static string DescribeUnusable( SupportedProviders provider ) => provider switch {
        SupportedProviders.AppleMusic =>
            "BridgeBeats:AppleKeyPath does not resolve to a readable signing key, so Apple Music " +
            "cannot register.",
        SupportedProviders.Spotify =>
            "only one of BridgeBeats:SpotifyClientId and BridgeBeats:SpotifyClientSecret is set; both are required.",
        SupportedProviders.Tidal =>
            "only one of BridgeBeats:TidalClientId and BridgeBeats:TidalClientSecret is set; both are required.",
        _ => "its credential is incomplete.",
    };

    /// <summary>
    /// Writes the named settings into the configuration, using the machine's real values when the
    /// credential is usable and empty strings otherwise, so no factory placeholder survives.
    /// </summary>
    /// <param name="config">The configuration dictionary to update.</param>
    /// <param name="settings">The setting names that make up this provider's credential.</param>
    /// <param name="state">What this machine supplies for the provider.</param>
    private static void ApplyOrClear(
        Dictionary<string, string?> config,
        string[] settings,
        LiveCredentialState state
    ) {
        foreach (string setting in settings) {
            config[$"BridgeBeats:{setting}"] = state == LiveCredentialState.Usable
                ? TestConfiguration.GetValue( $"BridgeBeats:{setting}" )
                : "";
        }
    }

    /// <summary>
    /// Adds an Apple Music credential tuple whose signing key is generated locally, so the provider is
    /// enabled through the application's normal provider registration without an Apple account.
    /// </summary>
    /// <remarks>
    /// Tests that assert request validation, authorization, or the absence of an outbound call need the
    /// provider registered, not a real Apple credential: the signing key is never presented to Apple
    /// because those tests stop before an authenticated call. The key is a real P-256 private key
    /// imported by the same registration path a production key uses; only its provenance differs.
    /// </remarks>
    internal sealed class GeneratedAppleSigningKey : IDisposable {
        /// <summary>The placeholder team id supplied alongside the generated key.</summary>
        private const string TeamId = "TESTTEAMID";
        /// <summary>The placeholder key id supplied alongside the generated key.</summary>
        private const string KeyId = "TESTKEYID";
        /// <summary>Filesystem path of the generated signing key, deleted on dispose.</summary>
        private readonly string _keyPath;

        /// <summary>
        /// Generates a P-256 signing key in a temporary file and adds the Apple Music settings to the
        /// supplied configuration dictionary.
        /// </summary>
        /// <param name="config">The configuration dictionary to add the Apple Music settings to.</param>
        internal GeneratedAppleSigningKey( Dictionary<string, string?> config ) {
            _keyPath = Path.Combine( Path.GetTempPath( ), $"bb-test-apple-{Guid.NewGuid( ):N}.p8" );
            using (ECDsa ecdsa = ECDsa.Create( ECCurve.NamedCurves.nistP256 )) {
                File.WriteAllText( _keyPath, ecdsa.ExportECPrivateKeyPem( ) );
            }
            config["BridgeBeats:AppleTeamId"] = TeamId;
            config["BridgeBeats:AppleKeyId"] = KeyId;
            config["BridgeBeats:AppleKeyPath"] = _keyPath;
        }

        /// <summary>Deletes the generated signing key, ignoring cleanup failures.</summary>
        public void Dispose( ) {
            try {
                File.Delete( _keyPath );
            } catch {
                // A leftover temp file is harmless; the test result is already decided.
            }
        }
    }
}
