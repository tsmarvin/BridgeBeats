#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Verifies the pinned Bluesky SDK schema is copied to the test output directory.
/// </summary>
/// <remarks>
/// The schema is referenced from the NuGet package cache by path in the test project file. The
/// integration suite opens it relative to the output directory, so a path that does not resolve
/// silently removes the file the migration comparison depends on.
/// </remarks>
[TestClass]
public class PinnedSdkSchemaAssetTests {

    private const string SchemaFileName = "bluesky-sdk-schema.sql";

    /// <summary>
    /// Verifies the schema asset is present next to the test assembly.
    /// </summary>
    [TestMethod]
    public void SdkSchema_IsCopiedToTestOutputDirectory( ) {
        string schemaPath = Path.Combine( AppContext.BaseDirectory, SchemaFileName );

        Assert.IsTrue( File.Exists( schemaPath ),
            $"{SchemaFileName} was not found in the test output directory ({AppContext.BaseDirectory}). "
            + "The schema Include path in BridgeBeats.Tests.csproj did not resolve." );
    }

    /// <summary>
    /// Verifies the copied asset is the SDK schema rather than a placeholder or empty file.
    /// </summary>
    [TestMethod]
    public void SdkSchema_ContainsTableDefinitions( ) {
        string schemaPath = Path.Combine( AppContext.BaseDirectory, SchemaFileName );
        string schema = File.ReadAllText( schemaPath );

        Assert.IsTrue( schema.Contains( "CREATE TABLE", StringComparison.OrdinalIgnoreCase ),
            $"{SchemaFileName} does not contain any CREATE TABLE statements." );
    }
}

#pragma warning restore CS1591
