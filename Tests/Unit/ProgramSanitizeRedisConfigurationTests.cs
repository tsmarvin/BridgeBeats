using System.Text.RegularExpressions;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="BridgeBeats.Worker.Maintenance.Program.SanitizeRedisConfiguration"/>, which
/// masks the Redis password in connection strings before they are emitted to structured logs.
/// </summary>
[TestClass]
public partial class ProgramSanitizeRedisConfigurationTests {

    /// <summary>
    /// A configuration string with a plaintext password, SSL enabled, and a non-default port.
    /// </summary>
    private const string ConfigWithPassword = "localhost:6379,password=SuperSecret123,ssl=true";

    /// <summary>
    /// Source-generated regex matching the literal password value used in <see cref="ConfigWithPassword"/>,
    /// used to assert the password does not appear in the sanitized output.
    /// </summary>
    /// <returns>A compiled <see cref="Regex"/> matching <c>SuperSecret123</c>.</returns>
    [GeneratedRegex( "SuperSecret123" )]
    private static partial Regex SuperSecretPasswordRegex( );

    /// <summary>
    /// Verifies that a connection string carrying a plaintext password is rendered with the password
    /// replaced by asterisks and that the host:port and other options are preserved.
    /// </summary>
    [TestMethod]
    public void SanitizeRedisConfiguration_WithPassword_MasksPassword( ) {
        string result = BridgeBeats.Worker.Maintenance.Program.SanitizeRedisConfiguration( ConfigWithPassword );

        StringAssert.DoesNotMatch(
            result,
            SuperSecretPasswordRegex( ),
            "Password must not appear in the sanitized output." );

        StringAssert.Contains(
            result,
            "*****",
            "Sanitized output must contain the asterisk mask in place of the password." );

        StringAssert.Contains(
            result,
            "localhost:6379",
            "Host and port must be preserved in the sanitized output." );
    }

    /// <summary>
    /// Verifies that a <see langword="null"/> configuration returns <c>"(unavailable)"</c> without
    /// throwing an exception.
    /// </summary>
    [TestMethod]
    public void SanitizeRedisConfiguration_Null_ReturnsUnavailable( ) {
        string result = BridgeBeats.Worker.Maintenance.Program.SanitizeRedisConfiguration( null );

        Assert.AreEqual( "(unavailable)", result,
            "Null configuration must return the sentinel string without throwing." );
    }

    /// <summary>
    /// Verifies that an empty-string configuration returns <c>"(unavailable)"</c> without throwing
    /// an exception (calling <c>ConfigurationOptions.Parse</c> on an empty string throws).
    /// </summary>
    [TestMethod]
    public void SanitizeRedisConfiguration_Empty_ReturnsUnavailable( ) {
        string result = BridgeBeats.Worker.Maintenance.Program.SanitizeRedisConfiguration( string.Empty );

        Assert.AreEqual( "(unavailable)", result,
            "Empty configuration must return the sentinel string without throwing." );
    }
}
