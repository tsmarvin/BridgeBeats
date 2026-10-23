using System.Text.Json;
using BridgeBeats.Contracts.Constants;
using BridgeBeats.Contracts.DTOs;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests the <see cref="StatisticsStatus"/> DTO: round-trip serialization with a composed
/// <see cref="LookupStatistics"/>, case-insensitive deserialization from hand-authored lowerCamelCase
/// JSON, the null-error success path, and the <see cref="RedisChannels.StatisticsRefreshRequested"/>
/// value pin.
/// </summary>
[TestClass]
public class StatisticsStatusTests {

    /// <summary>
    /// Case-insensitive JSON options matching those used by <see cref="BridgeBeats.Web.Services.RedisStatisticsReader"/>
    /// for all Redis document deserializations, reused here so the deserialization surface matches
    /// what the application actually uses.
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonOptions =
        new( ) { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Serializes a fully populated <see cref="StatisticsStatus"/> (including a non-trivial
    /// composed <see cref="LookupStatistics"/> with entries and provider counts), deserializes it,
    /// and asserts the entire object graph round-trips intact.
    /// </summary>
    [TestMethod]
    public void StatisticsStatus_RoundTrip_ComposedLookupStatisticsGraphSurvivesIntact( ) {
        // Arrange
        DateTimeOffset t = new( 2024, 6, 1, 12, 0, 0, TimeSpan.Zero );

        LookupStatistics snapshot = new( ) {
            TotalRecords = 42,
            AlbumCount = 10,
            TrackCount = 32,
            ProviderCounts = new Dictionary<string, int> {
                ["Spotify"] = 25,
                ["AppleMusic"] = 17
            },
            RecentEntries = [
                new RecentLookupEntry {
                    AtUri = "at://did:plc:test/link.bridgebeats.lookup/abc123",
                    IsAlbum = true,
                    Artist = "Test Artist",
                    Title = "Test Album",
                    LookedUpAt = t.AddDays( -1 ),
                    CardId = "card-abc"
                }
            ],
            EarliestLookup = t.AddDays( -30 ),
            LatestLookup = t,
            GeneratedAt = t
        };

        StatisticsStatus original = new( ) {
            Snapshot = snapshot,
            IsRunning = false,
            LastRunTime = t,
            NextScheduledRun = t.AddHours( 6 ),
            LastError = null,
            LastErrorTime = null
        };

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        StatisticsStatus? deserialized = JsonSerializer.Deserialize<StatisticsStatus>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.IsNotNull( deserialized.Snapshot );
        Assert.AreEqual( 42, deserialized.Snapshot.TotalRecords );
        Assert.AreEqual( 10, deserialized.Snapshot.AlbumCount );
        Assert.AreEqual( 32, deserialized.Snapshot.TrackCount );
        Assert.HasCount( 2, deserialized.Snapshot.ProviderCounts );
        Assert.AreEqual( 25, deserialized.Snapshot.ProviderCounts["Spotify"] );
        Assert.AreEqual( 17, deserialized.Snapshot.ProviderCounts["AppleMusic"] );
        Assert.HasCount( 1, deserialized.Snapshot.RecentEntries );
        Assert.AreEqual( "at://did:plc:test/link.bridgebeats.lookup/abc123", deserialized.Snapshot.RecentEntries[0].AtUri );
        Assert.IsTrue( deserialized.Snapshot.RecentEntries[0].IsAlbum );
        Assert.AreEqual( "Test Artist", deserialized.Snapshot.RecentEntries[0].Artist );
        Assert.AreEqual( "Test Album", deserialized.Snapshot.RecentEntries[0].Title );
        Assert.AreEqual( t.AddDays( -1 ), deserialized.Snapshot.RecentEntries[0].LookedUpAt );
        Assert.AreEqual( "card-abc", deserialized.Snapshot.RecentEntries[0].CardId );
        Assert.AreEqual( t.AddDays( -30 ), deserialized.Snapshot.EarliestLookup );
        Assert.AreEqual( t, deserialized.Snapshot.LatestLookup );
        Assert.AreEqual( t, deserialized.Snapshot.GeneratedAt );
        Assert.IsFalse( deserialized.IsRunning );
        Assert.AreEqual( t, deserialized.LastRunTime );
        Assert.AreEqual( t.AddHours( 6 ), deserialized.NextScheduledRun );
        Assert.IsNull( deserialized.LastError );
        Assert.IsNull( deserialized.LastErrorTime );
    }

    /// <summary>
    /// Deserializes a hand-authored lowerCamelCase JSON string using case-insensitive options and
    /// asserts every member bound correctly. This is the authoritative case-insensitivity test:
    /// a pure serialize/deserialize round-trip is vacuous because STJ writes PascalCase by default.
    /// </summary>
    [TestMethod]
    public void StatisticsStatus_CaseInsensitiveDeserialize_AllMembersBindCorrectly( ) {
        // Arrange — hand-authored lowerCamelCase JSON; keys deliberately differ in casing from the
        // C# property names to exercise the PropertyNameCaseInsensitive path.
        DateTimeOffset runTime = new( 2024, 3, 15, 9, 30, 0, TimeSpan.Zero );
        DateTimeOffset nextRun = new( 2024, 3, 15, 15, 30, 0, TimeSpan.Zero );
        DateTimeOffset errTime = new( 2024, 3, 14, 8, 0, 0, TimeSpan.Zero );
        DateTimeOffset genAt = new( 2024, 3, 15, 9, 0, 0, TimeSpan.Zero );

        string json = $$"""
            {
              "snapshot": {
                "totalRecords": 7,
                "albumCount": 3,
                "trackCount": 4,
                "providerCounts": { "Tidal": 5 },
                "recentEntries": [],
                "earliestLookup": "{{runTime.AddDays( -7 ):O}}",
                "latestLookup": "{{runTime:O}}",
                "generatedAt": "{{genAt:O}}",
                "cacheBootstrapStatus": null
              },
              "isRunning": true,
              "lastRunTime": "{{runTime:O}}",
              "nextScheduledRun": "{{nextRun:O}}",
              "lastError": "CAR parse failed",
              "lastErrorTime": "{{errTime:O}}"
            }
            """;

        // Act
        StatisticsStatus? result = JsonSerializer.Deserialize<StatisticsStatus>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( result );
        Assert.IsNotNull( result.Snapshot );
        Assert.AreEqual( 7, result.Snapshot.TotalRecords );
        Assert.AreEqual( 3, result.Snapshot.AlbumCount );
        Assert.AreEqual( 4, result.Snapshot.TrackCount );
        Assert.HasCount( 1, result.Snapshot.ProviderCounts );
        Assert.AreEqual( 5, result.Snapshot.ProviderCounts["Tidal"] );
        Assert.IsEmpty( result.Snapshot.RecentEntries );
        Assert.AreEqual( runTime.AddDays( -7 ), result.Snapshot.EarliestLookup );
        Assert.AreEqual( runTime, result.Snapshot.LatestLookup );
        Assert.AreEqual( genAt, result.Snapshot.GeneratedAt );
        Assert.IsTrue( result.IsRunning );
        Assert.AreEqual( runTime, result.LastRunTime );
        Assert.AreEqual( nextRun, result.NextScheduledRun );
        Assert.AreEqual( "CAR parse failed", result.LastError );
        Assert.AreEqual( errTime, result.LastErrorTime );
    }

    /// <summary>
    /// Verifies that a <see cref="StatisticsStatus"/> with <see langword="null"/>
    /// <see cref="StatisticsStatus.LastError"/> and <see cref="StatisticsStatus.LastErrorTime"/>
    /// (the healthy / no-error path) round-trips correctly.
    /// </summary>
    [TestMethod]
    public void StatisticsStatus_NullErrorPath_RoundTripsCorrectly( ) {
        // Arrange
        DateTimeOffset t = new( 2025, 1, 1, 0, 0, 0, TimeSpan.Zero );

        StatisticsStatus original = new( ) {
            Snapshot = null,
            IsRunning = false,
            LastRunTime = t,
            NextScheduledRun = null,
            LastError = null,
            LastErrorTime = null
        };

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        StatisticsStatus? result = JsonSerializer.Deserialize<StatisticsStatus>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( result );
        Assert.IsNull( result.Snapshot );
        Assert.IsFalse( result.IsRunning );
        Assert.AreEqual( t, result.LastRunTime );
        Assert.IsNull( result.NextScheduledRun );
        Assert.IsNull( result.LastError );
        Assert.IsNull( result.LastErrorTime );
    }

    /// <summary>
    /// Value pin: <see cref="RedisChannels.StatisticsRefreshRequested"/> must equal the literal
    /// agreed upon between the publisher (Web) and subscriber (Maintenance worker).
    /// </summary>
    [TestMethod]
    public void RedisChannels_StatisticsRefreshRequested_HasExpectedLiteralValue( ) {
        // Read via field-info so the compiler cannot fold this to a constant comparison.
        string? actual = typeof( RedisChannels )
            .GetField( nameof( RedisChannels.StatisticsRefreshRequested ) )
            ?.GetValue( null ) as string;
        Assert.AreEqual( "stats:refresh-requested", actual );
    }
}
