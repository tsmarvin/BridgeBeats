using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Storage.Car;
using BridgeBeats.Tests.Unit.Helpers;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Tests <see cref="CarRepoReader.EnumerateCollection"/>, which parses an ATProto repository CAR,
/// walks its Merkle Search Tree, and yields the records belonging to a requested collection.
/// </summary>
/// <remarks>
/// CAR fixtures are assembled with the test helper <c>TestCarBuilder</c> (commit block, MST node with
/// prefix-compressed entries, and per-record blocks). The tests verify that enumeration filters to the
/// requested collection's key prefix, handles an empty repository, and returns the correct record keys.
/// </remarks>
[TestClass]
public class CarRepoReaderTests {

    /// <summary>MSTest-injected context, used to flow the test's cancellation token into the reader.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Builds a minimal persisted lookup record with a single Spotify provider result, used as block
    /// content in the CAR fixtures.
    /// </summary>
    /// <param name="title">The track title to embed in the record.</param>
    /// <returns>A populated <see cref="MediaLinkResultRecord"/>.</returns>
    private static MediaLinkResultRecord MakeRecord( string title ) =>
        new(
            results: [
                new ProviderResultRecord(
                    provider: "spotify",
                    artist: "Artist",
                    title: title,
                    url: "https://open.spotify.com/track/x",
                    marketRegion: "US"
                )
            ],
            lookedUpAt: new DateTimeOffset( 2024, 6, 1, 0, 0, 0, TimeSpan.Zero )
        );

    /// <summary>
    /// Verifies that enumerating a repository containing both lookup and playlist records returns only
    /// the records under the requested <c>link.bridgebeats.lookup</c> collection, excluding the playlist
    /// entry. The fixture sorts entries by key and applies MST prefix compression to mimic a real tree.
    /// </summary>
    [TestMethod]
    public void EnumerateCollection_MixedCollections_FiltersToRequestedCollection( ) {
        // Build a repo with both lookup records and playlist records
        // They share the same CAR but are in different collections
        byte[] lookupRecord1Bytes = TestCarBuilder.BuildRecordBlock( MakeRecord( "Lookup Track 1" ) );
        byte[] lookupRecord2Bytes = TestCarBuilder.BuildRecordBlock( MakeRecord( "Lookup Track 2" ) );
        byte[] playlistRecordBytes = TestCarBuilder.BuildRecordBlock( MakeRecord( "Playlist" ) );

        byte[] lookupCid1 = TestCarBuilder.ComputeCidBytes( lookupRecord1Bytes );
        byte[] lookupCid2 = TestCarBuilder.ComputeCidBytes( lookupRecord2Bytes );
        byte[] playlistCid = TestCarBuilder.ComputeCidBytes( playlistRecordBytes );

        // MST entries for two collections
        List<TestCarBuilder.MstEntry> entries = [
            new TestCarBuilder.MstEntry( 0, "link.bridgebeats.lookup/key1", lookupCid1 ),
            new TestCarBuilder.MstEntry( 0, "link.bridgebeats.playlist/pl1", playlistCid ),
            new TestCarBuilder.MstEntry( 0, "link.bridgebeats.lookup/key2", lookupCid2 )
        ];

        // Sort entries by key (required for MST)
        entries.Sort( ( a, b ) => string.Compare( a.KeySuffix, b.KeySuffix, StringComparison.Ordinal ) );

        // Apply prefix compression
        List<TestCarBuilder.MstEntry> compressed = [];
        string prevKey = "";
        foreach (TestCarBuilder.MstEntry e in entries) {
            int prefixLen = CommonPrefixLength( prevKey, e.KeySuffix );
            compressed.Add( new TestCarBuilder.MstEntry( prefixLen, e.KeySuffix[prefixLen..], e.ValueCidBytes ) );
            prevKey = e.KeySuffix;
        }

        byte[] mstNodeBytes = TestCarBuilder.BuildMstNode( null, compressed );
        byte[] mstCid = TestCarBuilder.ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = TestCarBuilder.BuildCommit( "did:plc:test", mstCid );
        byte[] commitCid = TestCarBuilder.ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayKeyComparer.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            [lookupCid1] = lookupRecord1Bytes,
            [lookupCid2] = lookupRecord2Bytes,
            [playlistCid] = playlistRecordBytes
        };

        byte[] carBytes = TestCarBuilder.BuildCar( commitCid, blocks );
        ReadOnlyMemory<byte> carMemory = carBytes;

        // Act: enumerate only the lookup collection
        List<(string Rkey, string Cid, System.Text.Json.Nodes.JsonNode Record)> lookupResults = [
            .. CarRepoReader.EnumerateCollection( carMemory, "link.bridgebeats.lookup", TestContext.CancellationToken ).Records
        ];

        // Assert: only 2 lookup records returned, playlist filtered out
        Assert.HasCount( 2, lookupResults );
        // Both should have rkeys from the lookup collection
        Assert.IsTrue( lookupResults.All( r => !r.Rkey.Contains( "pl1" ) ) );
    }

    /// <summary>
    /// Verifies that enumerating a repository with no records in the collection yields nothing.
    /// </summary>
    [TestMethod]
    public void EnumerateCollection_EmptyRepo_YieldsNothing( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            "did:plc:test",
            "link.bridgebeats.lookup",
            []
        );

        List<(string Rkey, string Cid, System.Text.Json.Nodes.JsonNode Record)> results = [
            .. CarRepoReader.EnumerateCollection( carBytes, "link.bridgebeats.lookup", TestContext.CancellationToken ).Records
        ];

        Assert.IsEmpty( results );
    }

    /// <summary>
    /// Verifies that enumerating a repository of lookup records yields each record's rkey, confirming
    /// the collection prefix is stripped to recover the bare record key.
    /// </summary>
    [TestMethod]
    public void EnumerateCollection_LookupRecords_YieldsCorrectRkeys( ) {
        byte[] carBytes = TestCarBuilder.BuildRepoCarWithRecords(
            "did:plc:test",
            "link.bridgebeats.lookup",
            [
                ("rkey_alpha", MakeRecord( "Alpha" )),
                ("rkey_beta", MakeRecord( "Beta" ))
            ]
        );

        List<(string Rkey, string Cid, System.Text.Json.Nodes.JsonNode Record)> results = [
            .. CarRepoReader.EnumerateCollection( carBytes, "link.bridgebeats.lookup", TestContext.CancellationToken ).Records
        ];

        Assert.HasCount( 2, results );
        List<string> rkeys = [.. results.Select( r => r.Rkey ).OrderBy( s => s )];
        Assert.AreEqual( "rkey_alpha", rkeys[0] );
        Assert.AreEqual( "rkey_beta", rkeys[1] );
    }

    /// <summary>
    /// Computes the length of the shared leading prefix of two strings, used to build the MST
    /// prefix-compressed entries in the mixed-collection fixture.
    /// </summary>
    /// <param name="a">The first string.</param>
    /// <param name="b">The second string.</param>
    /// <returns>The number of leading characters the two strings share.</returns>
    private static int CommonPrefixLength( string a, string b ) {
        int len = Math.Min( a.Length, b.Length );
        int i = 0;
        while (i < len && a[i] == b[i]) { i++; }
        return i;
    }

    /// <summary>
    /// Equality comparer that treats byte arrays as keys by their contents, so a CID's raw bytes can key
    /// the in-memory block dictionary used to assemble a CAR fixture.
    /// </summary>
    private sealed class ByteArrayKeyComparer : System.Collections.Generic.IEqualityComparer<byte[]> {
        /// <summary>Shared singleton instance.</summary>
        internal static readonly ByteArrayKeyComparer Instance = new( );

        /// <summary>Compares two byte arrays for content equality.</summary>
        /// <param name="x">The first array.</param>
        /// <param name="y">The second array.</param>
        /// <returns><see langword="true"/> when both are non-null and have identical contents.</returns>
        public bool Equals( byte[]? x, byte[]? y ) => x is not null && y is not null && x.AsSpan( ).SequenceEqual( y );

        /// <summary>Computes a content-based hash code for a byte array.</summary>
        /// <param name="obj">The array to hash.</param>
        /// <returns>A hash code derived from the array contents.</returns>
        public int GetHashCode( byte[] obj ) {
            System.HashCode hc = new( );
            hc.AddBytes( obj );
            return hc.ToHashCode( );
        }
    }
}

#pragma warning restore CS1591
