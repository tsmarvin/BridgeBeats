using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Storage;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Regression guard verifying that the <c>link.bridgebeats.lookup</c> PDS record shape never
/// serializes an <c>isPartial</c> key, regardless of the in-memory <see cref="MediaLinkResult"/>
/// partiality flag. Partiality is owned by the saga and must not persist to the PDS.
/// </summary>
[TestClass]
public class MediaLinkResultRecordTests {

    private static readonly JsonSerializerOptions s_webJsonOptions = new( JsonSerializerDefaults.Web );

    /// <summary>
    /// The private static <c>ConvertToRecord</c> method on <see cref="ATProtoStorageService"/>,
    /// resolved via reflection once for the class.
    /// </summary>
    private static readonly MethodInfo s_convertToRecord =
        typeof( ATProtoStorageService ).GetMethod(
            "ConvertToRecord",
            BindingFlags.NonPublic | BindingFlags.Static
        ) ?? throw new InvalidOperationException( "ATProtoStorageService.ConvertToRecord not found via reflection." );

    /// <summary>
    /// Verifies that a <see cref="MediaLinkResult"/> with <c>IsPartial = true</c> and a provider
    /// subset produces a <see cref="MediaLinkResultRecord"/> whose JSON serialization contains no
    /// <c>isPartial</c> key. Also asserts that <c>results</c> and <c>lookedUpAt</c> are present
    /// (positive control: an empty serializer cannot pass for the wrong reason).
    /// </summary>
    /// <remarks>
    /// The GOTCHA the QA plan calls out: <c>[JsonIgnore(WhenWritingDefault)]</c> would suppress a
    /// <c>false</c> value anyway, so the test must drive <c>IsPartial = true</c> all the way through
    /// the pipeline to prove the property is absent for the right reason — it was removed — not merely
    /// suppressed by its default.
    /// </remarks>
    [TestMethod]
    public void ConvertToRecord_DoesNotSerializeIsPartialKey( ) {
        // Arrange — an explicitly partial API DTO with one provider result
        MediaLinkResult dto = new( ) {
            IsPartial = true,
            Results = new Dictionary<SupportedProviders, MusicLookupResult> {
                [SupportedProviders.Spotify] = new MusicLookupResult {
                    Artist = "Test Artist",
                    Title = "Test Track",
                    ExternalId = "USRC12345678",
                    URL = "https://open.spotify.com/track/test",
                    ArtUrl = string.Empty,
                    IsAlbum = false
                }
            }
        };

        // Failure-first evidence: before the IsPartial property was removed from MediaLinkResultRecord,
        // invoking ConvertToRecord with IsPartial=true and serializing would have produced a JSON object
        // containing "isPartial":true. Now it must not.

        // Act — run through the private pipeline under test
        MediaLinkResultRecord record = (MediaLinkResultRecord)(
            s_convertToRecord.Invoke( null, [dto] )
                ?? throw new InvalidOperationException( "ConvertToRecord returned null." )
        );

        string json = JsonSerializer.Serialize( record );

        // Assert — primary: no isPartial key in the serialized output
        JsonNode? node = JsonNode.Parse( json );
        Assert.IsNotNull( node, "Serialized record must parse as valid JSON." );
        JsonObject? obj = node.AsObject( );
        Assert.IsNotNull( obj, "Serialized record must be a JSON object." );

        Assert.IsFalse(
            obj.ContainsKey( "isPartial" ),
            "The serialized PDS record must not contain an 'isPartial' key. " +
            "Partiality is owned by the saga, not the persisted record."
        );

        // Assert — positive control: required fields must be present so an empty serializer cannot pass
        Assert.IsTrue( obj.ContainsKey( "results" ), "Serialized record must contain 'results'." );
        Assert.IsTrue( obj.ContainsKey( "lookedUpAt" ), "Serialized record must contain 'lookedUpAt'." );
    }

    /// <summary>
    /// Verifies the public API representation retains <c>isPartial</c> even though the persisted
    /// PDS record deliberately omits it. This pins the boundary between transient saga-derived
    /// response metadata and durable record content.
    /// </summary>
    [TestMethod]
    public void ApiSerialization_IncludesIsPartialKey( ) {
        MediaLinkResult dto = new( ) { IsPartial = true };

        JsonObject obj = JsonNode.Parse( JsonSerializer.Serialize( dto, s_webJsonOptions ) )!.AsObject( );

        Assert.IsTrue( obj.ContainsKey( "isPartial" ), "API JSON must retain the isPartial contract." );
        Assert.IsTrue( obj["isPartial"]!.GetValue<bool>( ) );
    }
}
