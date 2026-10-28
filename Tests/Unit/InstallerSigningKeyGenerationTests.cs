using System.Diagnostics;
using BridgeBeats.Web.Configuration;

#pragma warning disable CS1591
namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Verifies the Linux installer's ATProto signing key generator produces a key the application
/// actually loads.
/// </summary>
/// <remarks>
/// The installer writes <c>secrets/atproto_oauth_key.json</c> and the application refuses to start
/// when that file is not a usable ES256 key, so a generator that emits an unloadable key would only
/// surface at container start. This test runs the real generator function from
/// <c>containers/install.sh</c> and feeds its output to the application's loader.
/// </remarks>
[TestClass]
public class InstallerSigningKeyGenerationTests {

    private const string GeneratorFunction = "generate_es256_jwk";

    /// <summary>
    /// Verifies the generator output is accepted by <see cref="BlueskyAuthenticationConfiguration.LoadSigningKey"/>.
    /// </summary>
    [TestMethod]
    public void InstallShGeneratedJwk_LoadsThroughApplicationLoader( ) {
        if (!OperatingSystem.IsLinux( ) && !OperatingSystem.IsMacOS( )) {
            Assert.Inconclusive( "The installer signing key generator requires a POSIX host with bash and openssl." );
        }

        string script = "set -e\n"
            + GeneratorFunction + "() {\n"
            + ExtractFunctionBody( "containers/install.sh", GeneratorFunction ) + "\n}\n"
            + GeneratorFunction;
        string scriptPath = Path.Combine( Path.GetTempPath( ), $"gen-signing-key-{Guid.NewGuid( ):N}.sh" );
        string keyPath = Path.GetTempFileName( );
        try {
            File.WriteAllText( scriptPath, script );

            ProcessStartInfo startInfo = new( "bash", [scriptPath] ) {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using Process process = Process.Start( startInfo )!;
            string output = process.StandardOutput.ReadToEnd( );
            string errors = process.StandardError.ReadToEnd( );
            process.WaitForExit( );
            Assert.AreEqual( 0, process.ExitCode, $"{GeneratorFunction} failed: {errors}" );
            Assert.IsNotEmpty( output.Trim( ), $"{GeneratorFunction} produced no output" );

            File.WriteAllText( keyPath, output );
            var signingKey = BlueskyAuthenticationConfiguration.LoadSigningKey( keyPath );

            Assert.IsNotNull( signingKey, "the generated signing key did not load" );
            Assert.IsFalse( string.IsNullOrWhiteSpace( signingKey.KeyId ), "the generated signing key has no key id" );
        } finally {
            File.Delete( scriptPath );
            File.Delete( keyPath );
        }
    }

    private static string ExtractFunctionBody( string relativePath, string functionName ) {
        string path = Path.Combine( FindRepositoryRoot( ), relativePath );
        Assert.IsTrue( File.Exists( path ), $"expected {relativePath} in the repository root" );

        List<string> body = [];
        bool inside = false;
        foreach (string line in File.ReadAllLines( path )) {
            if (!inside) {
                if (line.StartsWith( functionName + "() {", StringComparison.Ordinal )) { inside = true; }
                continue;
            }
            if (line == "}") { break; }
            body.Add( line );
        }

        Assert.IsNotEmpty( body, $"could not extract {functionName} from {relativePath}" );
        return string.Join( "\n", body );
    }

    private static string FindRepositoryRoot( ) {
        DirectoryInfo? directory = new( AppContext.BaseDirectory );
        while (directory is not null && !File.Exists( Path.Combine( directory.FullName, "BridgeBeats.sln" ) )) {
            directory = directory.Parent;
        }
        Assert.IsNotNull( directory, "could not locate the repository root from the test output directory" );
        return directory.FullName;
    }
}
#pragma warning restore CS1591
