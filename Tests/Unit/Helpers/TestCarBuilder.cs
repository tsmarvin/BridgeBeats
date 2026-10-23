using System.Formats.Cbor;
using System.Security.Cryptography;
using BridgeBeats.Contracts.Records;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Unit.Helpers;

/// <summary>
/// Builds in-memory CARv1 / DAG-CBOR / MST fixtures for the ATProto CAR-parser tests
/// (<c>CarV1ReaderTests</c>, <c>CarRepoReaderTests</c>, <c>MstWalkerTests</c>,
/// <c>DagCborConverterTests</c>, and related tests).
/// </summary>
/// <remarks>
/// Each fixture is a byte array shaped to exercise one parser path: a well-formed repo export,
/// or a deliberately malformed/oversized/cyclic input that the parser's defensive caps must reject.
/// CIDs are atproto-style CIDv1 (dag-cbor 0x71, sha2-256 0x12, 32-byte digest); DAG-CBOR links use
/// CBOR tag 42 with a leading <c>0x00</c> multibase byte; blocks are content-addressed by the SHA-256
/// of their bytes so the reader's per-block digest verification passes. The shapes mirror the format
/// consumed by <see cref="BridgeBeats.Contracts.Records.MediaLinkResultRecord"/> records stored under
/// the <c>link.bridgebeats.lookup</c> collection.
/// </remarks>
internal static class TestCarBuilder {

    /// <summary>
    /// The fixed atproto CIDv1 prefix: version <c>0x01</c>, dag-cbor codec <c>0x71</c>,
    /// sha2-256 hash function <c>0x12</c>, 32-byte digest length <c>0x20</c>.
    /// </summary>
    private static readonly byte[] s_cidPrefix = [0x01, 0x71, 0x12, 0x20];

    // ─── Public record type for MST node entries ─────────────────────────────

    /// <summary>
    /// One entry in a built MST node, expressed in the on-wire prefix-compressed form the walker reads.
    /// </summary>
    /// <param name="PrefixLen">Shared-prefix length with the previous key (the <c>p</c> field); the first entry must use 0.</param>
    /// <param name="KeySuffix">The key bytes after the shared prefix (the <c>k</c> field), encoded as UTF-8.</param>
    /// <param name="ValueCidBytes">The raw CID bytes of the value block this entry points at (the <c>v</c> link).</param>
    /// <param name="RightChildCidBytes">Optional CID bytes of the right subtree node (the <c>t</c> link); null writes a CBOR null.</param>
    internal sealed record MstEntry(
        int PrefixLen,
        string KeySuffix,
        byte[] ValueCidBytes,
        byte[]? RightChildCidBytes = null
    );

    // ─── CID helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the 36-byte atproto CIDv1 (4-byte prefix + SHA-256 digest) for a block's bytes,
    /// matching the content-addressing the reader verifies each block against.
    /// </summary>
    /// <param name="blockBytes">The block bytes to hash.</param>
    /// <returns>The 36-byte CID (prefix followed by the SHA-256 digest).</returns>
    internal static byte[] ComputeCidBytes( ReadOnlySpan<byte> blockBytes ) {
        byte[] digest = SHA256.HashData( blockBytes );
        byte[] cidBytes = new byte[4 + 32];
        s_cidPrefix.CopyTo( cidBytes, 0 );
        digest.CopyTo( cidBytes, 4 );
        return cidBytes;
    }

    /// <summary>
    /// Wraps raw CID bytes in the DAG-CBOR link form by prepending the <c>0x00</c> multibase prefix,
    /// the layout a CBOR tag-42 link byte string must carry.
    /// </summary>
    /// <param name="cidBytes">The raw 36-byte CID to wrap.</param>
    /// <returns>The CID bytes prefixed with <c>0x00</c>, ready to write as a tag-42 byte string.</returns>
    internal static byte[] MakeLinkBytes( byte[] cidBytes ) {
        byte[] linkBytes = new byte[1 + cidBytes.Length];
        linkBytes[0] = 0x00; // multibase prefix
        cidBytes.CopyTo( linkBytes, 1 );
        return linkBytes;
    }

    // ─── Varint encoding ─────────────────────────────────────────────────────

    /// <summary>
    /// Writes an unsigned LEB128 varint (little-endian, 7 bits per byte, high bit as continuation)
    /// to the stream, matching the CAR header- and section-length encoding the reader decodes.
    /// </summary>
    /// <param name="stream">The destination stream.</param>
    /// <param name="value">The value to encode.</param>
    internal static void WriteVarint( MemoryStream stream, ulong value ) {
        while (value >= 0x80) {
            stream.WriteByte( (byte)((value & 0x7F) | 0x80) );
            value >>= 7;
        }
        stream.WriteByte( (byte)value );
    }

    // ─── Block builders ──────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a <see cref="MediaLinkResultRecord"/> as a DAG-CBOR record block (the value block an MST
    /// entry points at), writing <c>results</c> and round-trip-formatted <c>lookedUpAt</c>.
    /// </summary>
    /// <param name="record">The record to encode.</param>
    /// <returns>The CBOR-encoded record block bytes.</returns>
    internal static byte[] BuildRecordBlock( MediaLinkResultRecord record ) {
        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        // DAG-CBOR canonical key order: length-first, then lexicographic.
        // Key lengths: results(7) < lookedUpAt(10)
        // Write results first, then lookedUpAt.

        WriteCborTextString( writer, "results" );
        writer.WriteStartArray( null );
        foreach (ProviderResultRecord r in record.Results) {
            BuildProviderResultBlock( writer, r );
        }
        writer.WriteEndArray( );

        WriteCborTextString( writer, "lookedUpAt" );
        WriteCborTextString( writer, record.LookedUpAt.ToString( "O" ) );

        writer.WriteEndMap( );
        return writer.Encode( );
    }

    /// <summary>
    /// Writes one <see cref="ProviderResultRecord"/> as a CBOR map into the record block's <c>results</c>
    /// array, emitting the required fields and omitting optional <c>artUrl</c>/<c>isAlbum</c>/<c>externalId</c>
    /// when absent.
    /// </summary>
    /// <param name="writer">The open CBOR writer positioned inside the results array.</param>
    /// <param name="r">The provider result to encode.</param>
    private static void BuildProviderResultBlock( CborWriter writer, ProviderResultRecord r ) {
        writer.WriteStartMap( null );

        // Write fields in DAG-CBOR key order: length-first, then lexicographic within the same length.
        // Key lengths: url(3), title(5), artUrl(6), artist(6), isAlbum(7), provider(8), externalId(10), marketRegion(12)
        // Sorted order: url(3) < title(5) < artUrl(6)=artist(6)[lex] < isAlbum(7) < provider(8) < externalId(10) < marketRegion(12)

        WriteCborTextString( writer, "url" );
        WriteCborTextString( writer, r.Url );

        WriteCborTextString( writer, "title" );
        WriteCborTextString( writer, r.Title );

        if (r.ArtUrl is not null) {
            // artUrl (6) before artist (6) — lexicographic tie-break: 'artUrl' < 'artist'
            WriteCborTextString( writer, "artUrl" );
            WriteCborTextString( writer, r.ArtUrl );
        }

        WriteCborTextString( writer, "artist" );
        WriteCborTextString( writer, r.Artist );

        if (r.IsAlbum.HasValue) {
            WriteCborTextString( writer, "isAlbum" );
            writer.WriteBoolean( r.IsAlbum.Value );
        }

        WriteCborTextString( writer, "provider" );
        WriteCborTextString( writer, r.Provider );

        if (r.ExternalId is not null) {
            WriteCborTextString( writer, "externalId" );
            WriteCborTextString( writer, r.ExternalId );
        }

        WriteCborTextString( writer, "marketRegion" );
        WriteCborTextString( writer, r.MarketRegion );

        writer.WriteEndMap( );
    }

    /// <summary>
    /// Builds a DAG-CBOR MST node with an optional left subtree link (<c>l</c>) and an ordered entry
    /// array (<c>e</c>), each entry carrying its prefix length, key suffix, optional right subtree link,
    /// and value link in the shape the MST walker traverses.
    /// </summary>
    /// <param name="leftCidBytes">CID bytes of the left subtree node, or null to write a CBOR null.</param>
    /// <param name="entries">The node's entries in key order.</param>
    /// <returns>The CBOR-encoded MST node block bytes.</returns>
    internal static byte[] BuildMstNode( byte[]? leftCidBytes, IReadOnlyList<MstEntry> entries ) {
        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        // e field (entries array) — key "e" length 1
        WriteCborTextString( writer, "e" );
        writer.WriteStartArray( null );
        foreach (MstEntry entry in entries) {
            writer.WriteStartMap( null );
            // k (1), p (1), t (1), v (1) — all length 1, alphabetic: k < p < t < v
            WriteCborTextString( writer, "k" );
            writer.WriteByteString( System.Text.Encoding.UTF8.GetBytes( entry.KeySuffix ) );

            WriteCborTextString( writer, "p" );
            writer.WriteInt32( entry.PrefixLen );

            WriteCborTextString( writer, "t" );
            if (entry.RightChildCidBytes is not null) {
                writer.WriteTag( (CborTag)42 );
                writer.WriteByteString( MakeLinkBytes( entry.RightChildCidBytes ) );
            } else {
                writer.WriteNull( );
            }

            WriteCborTextString( writer, "v" );
            writer.WriteTag( (CborTag)42 );
            writer.WriteByteString( MakeLinkBytes( entry.ValueCidBytes ) );

            writer.WriteEndMap( );
        }
        writer.WriteEndArray( );

        // l field — key "l" length 1 (comes after "e" alphabetically)
        WriteCborTextString( writer, "l" );
        if (leftCidBytes is not null) {
            writer.WriteTag( (CborTag)42 );
            writer.WriteByteString( MakeLinkBytes( leftCidBytes ) );
        } else {
            writer.WriteNull( );
        }

        writer.WriteEndMap( );
        return writer.Encode( );
    }

    /// <summary>
    /// Builds the repo commit block: the root block whose <c>data</c> link points at the MST root node.
    /// Carries <c>did</c>, <c>rev</c>, a zero-filled <c>sig</c>, the MST-root <c>data</c> link, a null
    /// <c>prev</c>, and the commit <c>version</c> (default 3, the value the reader expects).
    /// </summary>
    /// <param name="did">The repository DID to embed.</param>
    /// <param name="mstRootCidBytes">CID bytes of the MST root node the commit's <c>data</c> link points at.</param>
    /// <param name="version">The commit version to write; defaults to 3.</param>
    /// <returns>The CBOR-encoded commit block bytes.</returns>
    internal static byte[] BuildCommit( string did, byte[] mstRootCidBytes, int version = 3 ) {
        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        // Keys by length: rev(3), did(3), sig(3), data(4), prev(4), version(7)
        // rev (3), did (3), sig (3) — alphabetical: did < rev < sig
        // data (4), prev (4) — alphabetical: data < prev
        // version (7)

        WriteCborTextString( writer, "did" );
        WriteCborTextString( writer, did );

        WriteCborTextString( writer, "rev" );
        WriteCborTextString( writer, "3" );

        WriteCborTextString( writer, "sig" );
        writer.WriteByteString( new byte[64] ); // placeholder signature

        WriteCborTextString( writer, "data" );
        writer.WriteTag( (CborTag)42 );
        writer.WriteByteString( MakeLinkBytes( mstRootCidBytes ) );

        WriteCborTextString( writer, "prev" );
        writer.WriteNull( );

        WriteCborTextString( writer, "version" );
        writer.WriteInt32( version );

        writer.WriteEndMap( );
        return writer.Encode( );
    }

    // ─── Security test helpers ────────────────────────────────────────────────

    /// <summary>
    /// Builds a CAR whose MST has node B reference node A both as its left subtree (<c>l</c>) and as an
    /// entry's right subtree (<c>t</c>), so the same node is reachable by two paths. Exercises the walker
    /// on a DAG that is not a strict tree (a shared node reached more than once) without forming a cycle.
    /// </summary>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildDoublingDagCar( ) {
        byte[] recBytes = BuildRecordBlock( new BridgeBeats.Contracts.Records.MediaLinkResultRecord(
            results: [new BridgeBeats.Contracts.Records.ProviderResultRecord(
                "spotify", "Artist", "Track",
                "https://open.spotify.com/track/x", "US" )],
            lookedUpAt: new DateTimeOffset( 2024, 1, 1, 0, 0, 0, TimeSpan.Zero )
        ) );
        byte[] recCid = ComputeCidBytes( recBytes );

        // nodeA: standalone leaf — will be referenced twice by nodeB
        byte[] nodeABytes = BuildMstNode( null,
            [new MstEntry( 0, "link.bridgebeats.lookup/a", recCid )] );
        byte[] nodeACid = ComputeCidBytes( nodeABytes );

        // nodeB: l=nodeA, entry[0].t=nodeA  ← nodeA referenced TWICE
        byte[] nodeBBytes = BuildMstNode( nodeACid,
            [new MstEntry( 0, "link.bridgebeats.lookup/b", recCid, nodeACid )] );
        byte[] nodeBCid = ComputeCidBytes( nodeBBytes );

        byte[] commitBytes = BuildCommit( "did:plc:test", nodeBCid );
        byte[] commitCid = ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayComparer.Instance ) {
            [commitCid] = commitBytes,
            [nodeBCid] = nodeBBytes,
            [nodeACid] = nodeABytes,
            [recCid] = recBytes,
        };
        return BuildCar( commitCid, blocks );
    }

    /// <summary>
    /// Builds a raw DAG-CBOR byte sequence of <paramref name="nestingDepth"/> nested single-element arrays
    /// (<c>0x81</c> repeated) wrapping an empty array (<c>0x80</c>). Used to drive the converter's maximum
    /// nesting-depth guard (depth cap 32) past its limit.
    /// </summary>
    /// <param name="nestingDepth">The number of nested arrays to emit.</param>
    /// <returns>The raw CBOR bytes (not a full CAR).</returns>
    internal static byte[] BuildDeeplyNestedCborBytes( int nestingDepth ) {
        using MemoryStream ms = new( );
        // Write nestingDepth levels of 0x81 (array of 1 item), then 0x80 (empty array)
        for (int i = 0; i < nestingDepth; i++) {
            ms.WriteByte( 0x81 ); // array(1)
        }
        ms.WriteByte( 0x80 ); // array(0) — innermost
        return ms.ToArray( );
    }

    /// <summary>
    /// Builds a CAR whose second MST entry declares an invalid prefix length (negative by default), so the
    /// walker's prefix-length validation rejects the node. The first entry is well-formed (<c>p=0</c>); the
    /// second supplies the bad <c>p</c> value.
    /// </summary>
    /// <param name="negativePrefixLen">The invalid prefix length to write on the second entry; defaults to -1.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildCarWithNegativePrefixLen( int negativePrefixLen = -1 ) {
        byte[] recBytes = BuildRecordBlock( new BridgeBeats.Contracts.Records.MediaLinkResultRecord(
            results: [new BridgeBeats.Contracts.Records.ProviderResultRecord(
                "spotify", "Artist", "Track",
                "https://open.spotify.com/track/x", "US" )],
            lookedUpAt: new DateTimeOffset( 2024, 1, 1, 0, 0, 0, TimeSpan.Zero )
        ) );
        byte[] recCid = ComputeCidBytes( recBytes );

        // Build the node bytes manually so we can write a negative p value.
        // First entry: p=0, k="link.bridgebeats.lookup/rkey1", valid
        // Second entry: p=negativePrefixLen (e.g. -1) — structurally invalid
        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        WriteCborTextString( writer, "e" );
        writer.WriteStartArray( null );

        // Entry 0: valid
        writer.WriteStartMap( null );
        WriteCborTextString( writer, "k" );
        writer.WriteByteString( System.Text.Encoding.UTF8.GetBytes( "link.bridgebeats.lookup/rkey1" ) );
        WriteCborTextString( writer, "p" );
        writer.WriteInt32( 0 );
        WriteCborTextString( writer, "t" );
        writer.WriteNull( );
        WriteCborTextString( writer, "v" );
        writer.WriteTag( (CborTag)42 );
        writer.WriteByteString( MakeLinkBytes( recCid ) );
        writer.WriteEndMap( );

        // Entry 1: negative p
        writer.WriteStartMap( null );
        WriteCborTextString( writer, "k" );
        writer.WriteByteString( System.Text.Encoding.UTF8.GetBytes( "suffix" ) );
        WriteCborTextString( writer, "p" );
        writer.WriteInt32( negativePrefixLen );
        WriteCborTextString( writer, "t" );
        writer.WriteNull( );
        WriteCborTextString( writer, "v" );
        writer.WriteTag( (CborTag)42 );
        writer.WriteByteString( MakeLinkBytes( recCid ) );
        writer.WriteEndMap( );

        writer.WriteEndArray( );

        WriteCborTextString( writer, "l" );
        writer.WriteNull( );

        writer.WriteEndMap( );

        byte[] mstNodeBytes = writer.Encode( );
        byte[] mstCid = ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = BuildCommit( "did:plc:test", mstCid );
        byte[] commitCid = ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayComparer.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            [recCid] = recBytes,
        };
        return BuildCar( commitCid, blocks );
    }

    /// <summary>
    /// Builds a CAR padded with <paramref name="blockCount"/> tiny unreferenced blocks plus an empty MST
    /// and commit. Used to push the reader/walker past block- or node-count caps that bound how many
    /// blocks a single repo export may contain.
    /// </summary>
    /// <param name="blockCount">The number of filler blocks to add.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildCarWithManyTinyBlocks( int blockCount ) {
        Dictionary<byte[], byte[]> blocks = new( ByteArrayComparer.Instance );

        for (int i = 0; i < blockCount; i++) {
            byte[] blockBytes = [(byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i];
            byte[] cid = ComputeCidBytes( blockBytes );
            blocks[cid] = blockBytes;
        }

        byte[] mstNodeBytes = BuildMstNode( null, [] );
        byte[] mstCid = ComputeCidBytes( mstNodeBytes );
        blocks[mstCid] = mstNodeBytes;

        byte[] commitBytes = BuildCommit( "did:plc:test", mstCid );
        byte[] commitCid = ComputeCidBytes( commitBytes );
        blocks[commitCid] = commitBytes;

        return BuildCar( commitCid, blocks );
    }

    /// <summary>
    /// Builds a deliberately truncated CAR whose section length header declares an oversized block
    /// (CID + 3 MB, above the per-block 2 MB cap) but supplies no block body. Drives the reader's
    /// per-block size cap and bounds checking.
    /// </summary>
    /// <returns>The serialized (truncated) CAR bytes.</returns>
    internal static byte[] BuildCarWithOversizedBlockSection( ) {
        using MemoryStream ms = new( );

        // Build a minimal valid header pointing to a fake commit CID
        byte[] fakeCid = ComputeCidBytes( new byte[1] { 0xFF } );
        byte[] header = BuildCarHeaderPublic( fakeCid );
        WriteVarint( ms, (ulong)header.Length );
        ms.Write( header );

        // Write one section whose declared length is 3 MB (> MaxBlockSize 2 MB)
        // section_len = CID_bytes(36) + block_bytes
        ulong oversizedLen = (ulong)(36 + 3 * 1024 * 1024);
        WriteVarint( ms, oversizedLen );
        // Write the CID bytes (real CID for the over-limit block)
        ms.Write( fakeCid );
        // Don't write the block bytes — the parser should throw before reading them
        // because blockLen > MaxBlockSize is checked before reading block content.

        return ms.ToArray( );
    }

    /// <summary>
    /// Builds a CAR that writes the same CID/block section twice, then the MST and commit. Exercises the
    /// reader's handling of a duplicated CID in the block stream.
    /// </summary>
    /// <param name="duplicatedCidHex">Receives the lowercase hex digest (the block-map key) of the duplicated CID.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildCarWithDuplicateCid( out string duplicatedCidHex ) {
        byte[] blockBytes = System.Text.Encoding.UTF8.GetBytes( "duplicate-block-content" );
        byte[] cid = ComputeCidBytes( blockBytes );
        duplicatedCidHex = Convert.ToHexStringLower( cid.AsSpan( 4 ) ); // KeyHex uses digest bytes only

        byte[] mstNodeBytes = BuildMstNode( null, [] );
        byte[] mstCid = ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = BuildCommit( "did:plc:test", mstCid );
        byte[] commitCid = ComputeCidBytes( commitBytes );

        // We build the CAR manually to emit the duplicate-content block twice
        using MemoryStream ms = new( );
        byte[] header = BuildCarHeaderPublic( commitCid );
        WriteVarint( ms, (ulong)header.Length );
        ms.Write( header );

        // Emit blockBytes block TWICE with the same CID
        foreach (byte[] _ in new[] { blockBytes, blockBytes }) {
            ulong sectionLen = (ulong)(cid.Length + blockBytes.Length);
            WriteVarint( ms, sectionLen );
            ms.Write( cid );
            ms.Write( blockBytes );
        }

        // Emit commit and MST node normally
        void WriteBlock( byte[] c, byte[] b ) {
            ulong sl = (ulong)(c.Length + b.Length);
            WriteVarint( ms, sl );
            ms.Write( c );
            ms.Write( b );
        }

        WriteBlock( mstCid, mstNodeBytes );
        WriteBlock( commitCid, commitBytes );

        return ms.ToArray( );
    }

    /// <summary>
    /// Builds a single-record CAR whose MST key is <c>{collection}/{rawRkey}</c> using the supplied raw
    /// rkey verbatim. Lets a test feed an rkey that fails the storage layer's rkey validation so the
    /// reader/consumer's skip-invalid-rkey behavior can be checked.
    /// </summary>
    /// <param name="collection">The collection NSID prefixing the MST key.</param>
    /// <param name="rawRkey">The raw record key (possibly invalid) to embed without sanitization.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildCarWithRawRkey( string collection, string rawRkey ) {
        byte[] recBytes = BuildRecordBlock( new BridgeBeats.Contracts.Records.MediaLinkResultRecord(
            results: [new BridgeBeats.Contracts.Records.ProviderResultRecord(
                "spotify", "Artist", "Track",
                "https://open.spotify.com/track/x", "US" )],
            lookedUpAt: new DateTimeOffset( 2024, 1, 1, 0, 0, 0, TimeSpan.Zero )
        ) );
        byte[] recCid = ComputeCidBytes( recBytes );

        string fullKey = $"{collection}/{rawRkey}";
        byte[] mstNodeBytes = BuildMstNode( null,
            [new MstEntry( 0, fullKey, recCid )] );
        byte[] mstCid = ComputeCidBytes( mstNodeBytes );
        byte[] commitBytes = BuildCommit( "did:plc:test", mstCid );
        byte[] commitCid = ComputeCidBytes( commitBytes );

        Dictionary<byte[], byte[]> blocks = new( ByteArrayComparer.Instance ) {
            [commitCid] = commitBytes,
            [mstCid] = mstNodeBytes,
            [recCid] = recBytes,
        };
        return BuildCar( commitCid, blocks );
    }

    /// <summary>
    /// Test-visible wrapper over the private CAR header builder, letting fixtures that hand-assemble a CAR
    /// body emit a valid varint-prefixed header for a given root CID.
    /// </summary>
    /// <param name="rootCidBytes">The root CID the header's <c>roots</c> array points at.</param>
    /// <returns>The CBOR-encoded CAR header bytes (without the leading varint length).</returns>
    internal static byte[] BuildCarHeaderPublic( byte[] rootCidBytes ) =>
        BuildCarHeader( rootCidBytes );

    /// <summary>
    /// Assembles a complete CARv1 byte stream: a varint-length-prefixed header carrying the root CID,
    /// followed by each block written as a varint section length, its 36-byte CID, then its bytes.
    /// </summary>
    /// <param name="rootCidBytes">The root (commit) CID the header points at.</param>
    /// <param name="blocks">The CID-to-block map to serialize, keyed by raw CID bytes.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildCar( byte[] rootCidBytes, Dictionary<byte[], byte[]> blocks ) {
        using MemoryStream stream = new( );

        // Build DAG-CBOR header: {version: 1, roots: [rootCid]}
        byte[] header = BuildCarHeader( rootCidBytes );

        // Write varint(header_len) | header
        WriteVarint( stream, (ulong)header.Length );
        stream.Write( header );

        // Write sections: varint(cid_len + block_len) | cid_bytes | block_bytes
        foreach (KeyValuePair<byte[], byte[]> kvp in blocks) {
            byte[] cidBytes = kvp.Key;
            byte[] blockBytes = kvp.Value;
            ulong sectionLen = (ulong)(cidBytes.Length + blockBytes.Length);
            WriteVarint( stream, sectionLen );
            stream.Write( cidBytes );
            stream.Write( blockBytes );
        }

        return stream.ToArray( );
    }

    /// <summary>
    /// Builds the DAG-CBOR CAR header map: a <c>roots</c> array holding the root CID as a tag-42 link and a
    /// <c>version</c> of 1 (CARv1).
    /// </summary>
    /// <param name="rootCidBytes">The root CID to embed in the <c>roots</c> array.</param>
    /// <returns>The CBOR-encoded header bytes.</returns>
    private static byte[] BuildCarHeader( byte[] rootCidBytes ) {
        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        // roots (5) before version (7)
        WriteCborTextString( writer, "roots" );
        writer.WriteStartArray( null );
        writer.WriteTag( (CborTag)42 );
        writer.WriteByteString( MakeLinkBytes( rootCidBytes ) );
        writer.WriteEndArray( );

        WriteCborTextString( writer, "version" );
        writer.WriteInt32( 1 );

        writer.WriteEndMap( );
        return writer.Encode( );
    }

    /// <summary>
    /// Writes a CBOR text string, centralizing the map-key and string-value encoding used throughout the builder.
    /// </summary>
    /// <param name="writer">The CBOR writer to write to.</param>
    /// <param name="value">The string to encode.</param>
    private static void WriteCborTextString( CborWriter writer, string value ) {
        writer.WriteTextString( value );
    }

    // ─── High-level factory: single-collection repo ───────────────────────────

    /// <summary>
    /// Builds a well-formed repo CAR containing the supplied records: encodes each record block, sorts the
    /// MST keys (<c>{collection}/{rkey}</c>) ordinally, applies prefix compression across them, and wraps the
    /// resulting MST node in a commit. The canonical "happy path" fixture for repo enumeration.
    /// </summary>
    /// <param name="did">The repository DID embedded in the commit.</param>
    /// <param name="collection">The collection NSID prefixing every MST key.</param>
    /// <param name="records">The (rkey, record) pairs to include.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildRepoCarWithRecords(
        string did,
        string collection,
        IReadOnlyList<(string Rkey, MediaLinkResultRecord Record)> records
    ) {
        Dictionary<byte[], byte[]> blocks = new( ByteArrayComparer.Instance );

        // Build a record block for each entry
        List<(string Key, byte[] CidBytes)> mstEntries = [];
        foreach ((string rkey, MediaLinkResultRecord record) in records) {
            byte[] recordBytes = BuildRecordBlock( record );
            byte[] cidBytes = ComputeCidBytes( recordBytes );
            blocks[cidBytes] = recordBytes;
            mstEntries.Add( ($"{collection}/{rkey}", cidBytes) );
        }

        // Sort entries by key (MST is lexicographically ordered)
        mstEntries.Sort( static ( a, b ) => string.Compare( a.Key, b.Key, StringComparison.Ordinal ) );

        // Build MST entries with prefix compression
        List<MstEntry> entries = [];
        string prevKey = "";
        foreach ((string key, byte[] cidBytes) in mstEntries) {
            int prefixLen = CommonPrefixLength( prevKey, key );
            string suffix = key[prefixLen..];
            entries.Add( new MstEntry( prefixLen, suffix, cidBytes ) );
            prevKey = key;
        }

        // Build single MST node
        byte[] mstNodeBytes = BuildMstNode( null, entries );
        byte[] mstCidBytes = ComputeCidBytes( mstNodeBytes );
        blocks[mstCidBytes] = mstNodeBytes;

        // Build commit
        byte[] commitBytes = BuildCommit( did, mstCidBytes );
        byte[] commitCidBytes = ComputeCidBytes( commitBytes );
        blocks[commitCidBytes] = commitBytes;

        return BuildCar( commitCidBytes, blocks );
    }

    /// <summary>
    /// Same as <see cref="BuildRepoCarWithRecords"/> but writes an explicit commit <paramref name="commitVersion"/>,
    /// so a test can supply a non-3 version and assert the reader's commit-version handling (it warns on
    /// versions other than 3).
    /// </summary>
    /// <param name="did">The repository DID embedded in the commit.</param>
    /// <param name="collection">The collection NSID prefixing every MST key.</param>
    /// <param name="records">The (rkey, record) pairs to include.</param>
    /// <param name="commitVersion">The commit version to write.</param>
    /// <returns>The serialized CAR bytes.</returns>
    internal static byte[] BuildRepoCarWithRecordsAndVersion(
        string did,
        string collection,
        IReadOnlyList<(string Rkey, MediaLinkResultRecord Record)> records,
        int commitVersion
    ) {
        Dictionary<byte[], byte[]> blocks = new( ByteArrayComparer.Instance );

        List<(string Key, byte[] CidBytes)> mstEntries = [];
        foreach ((string rkey, MediaLinkResultRecord record) in records) {
            byte[] recordBytes = BuildRecordBlock( record );
            byte[] cidBytes = ComputeCidBytes( recordBytes );
            blocks[cidBytes] = recordBytes;
            mstEntries.Add( ($"{collection}/{rkey}", cidBytes) );
        }

        mstEntries.Sort( static ( a, b ) => string.Compare( a.Key, b.Key, StringComparison.Ordinal ) );

        List<MstEntry> entries = [];
        string prevKey = "";
        foreach ((string key, byte[] cidBytes) in mstEntries) {
            int prefixLen = CommonPrefixLength( prevKey, key );
            string suffix = key[prefixLen..];
            entries.Add( new MstEntry( prefixLen, suffix, cidBytes ) );
            prevKey = key;
        }

        byte[] mstNodeBytes = BuildMstNode( null, entries );
        byte[] mstCidBytes = ComputeCidBytes( mstNodeBytes );
        blocks[mstCidBytes] = mstNodeBytes;

        byte[] commitBytes = BuildCommit( did, mstCidBytes, commitVersion );
        byte[] commitCidBytes = ComputeCidBytes( commitBytes );
        blocks[commitCidBytes] = commitBytes;

        return BuildCar( commitCidBytes, blocks );
    }

    /// <summary>
    /// Returns the length of the shared leading prefix of two strings, used to compute the per-entry
    /// prefix-compression length (<c>p</c>) when building MST nodes in key order.
    /// </summary>
    /// <param name="a">The previous key.</param>
    /// <param name="b">The current key.</param>
    /// <returns>The number of leading characters the two keys share.</returns>
    private static int CommonPrefixLength( string a, string b ) {
        int len = Math.Min( a.Length, b.Length );
        int i = 0;
        while (i < len && a[i] == b[i]) {
            i++;
        }
        return i;
    }

    // ─── Malformed-input helpers (hardening tests) ────────────────────────────

    /// <summary>
    /// Builds a CAR header whose root link byte string is one byte too long (a valid CID plus a trailing
    /// <c>0xFF</c>), so the reader's root-CID length/prefix validation rejects it.
    /// </summary>
    /// <returns>The serialized (header-only) CAR bytes.</returns>
    internal static byte[] BuildCarWithOversizedCidInHeader( ) {
        byte[] normalCid = ComputeCidBytes( new byte[] { 0xAA } );
        // Produce a 37-byte link: 0x00 multibase prefix + 36-byte CID + 1 extra trailing byte
        byte[] oversizedLinkBytes = new byte[1 + normalCid.Length + 1];
        oversizedLinkBytes[0] = 0x00; // multibase prefix
        normalCid.CopyTo( oversizedLinkBytes, 1 );
        oversizedLinkBytes[^1] = 0xFF; // extra trailing byte

        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        WriteCborTextString( writer, "roots" );
        writer.WriteStartArray( null );
        writer.WriteTag( (CborTag)42 );
        writer.WriteByteString( oversizedLinkBytes );
        writer.WriteEndArray( );

        WriteCborTextString( writer, "version" );
        writer.WriteInt32( 1 );

        writer.WriteEndMap( );
        byte[] header = writer.Encode( );

        using MemoryStream ms = new( );
        WriteVarint( ms, (ulong)header.Length );
        ms.Write( header );
        return ms.ToArray( );
    }

    /// <summary>
    /// Builds a CAR header that omits the <c>version</c> field entirely, so the reader's header parsing must
    /// reject a header that carries <c>roots</c> but no version.
    /// </summary>
    /// <returns>The serialized (header-only) CAR bytes.</returns>
    internal static byte[] BuildCarWithHeaderMissingVersion( ) {
        byte[] fakeCid = ComputeCidBytes( new byte[] { 0xBB } );

        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        WriteCborTextString( writer, "roots" );
        writer.WriteStartArray( null );
        writer.WriteTag( (CborTag)42 );
        writer.WriteByteString( MakeLinkBytes( fakeCid ) );
        writer.WriteEndArray( );

        // "version" key intentionally omitted

        writer.WriteEndMap( );
        byte[] header = writer.Encode( );

        using MemoryStream ms = new( );
        WriteVarint( ms, (ulong)header.Length );
        ms.Write( header );
        return ms.ToArray( );
    }

    /// <summary>
    /// Builds a commit block whose <c>data</c> link is a CBOR null instead of an MST-root link, so the
    /// walker must handle a commit that points at no MST root.
    /// </summary>
    /// <param name="did">The repository DID embedded in the commit.</param>
    /// <param name="version">The commit version to write; defaults to 3.</param>
    /// <returns>The CBOR-encoded commit block bytes.</returns>
    internal static byte[] BuildCommitWithNullData( string did, int version = 3 ) {
        CborWriter writer = new( CborConformanceMode.Lax );
        writer.WriteStartMap( null );

        // Same key ordering as BuildCommit: did(3) < rev(3) < sig(3) < data(4) < prev(4) < version(7)
        WriteCborTextString( writer, "did" );
        WriteCborTextString( writer, did );

        WriteCborTextString( writer, "rev" );
        WriteCborTextString( writer, "3" );

        WriteCborTextString( writer, "sig" );
        writer.WriteByteString( new byte[64] );

        WriteCborTextString( writer, "data" );
        writer.WriteNull( ); // explicitly null — no data link

        WriteCborTextString( writer, "prev" );
        writer.WriteNull( );

        WriteCborTextString( writer, "version" );
        writer.WriteInt32( version );

        writer.WriteEndMap( );
        return writer.Encode( );
    }

    /// <summary>
    /// Structural equality comparer for <c>byte[]</c> keys, so the block dictionaries are keyed by CID
    /// content rather than array reference.
    /// </summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]> {
        /// <summary>Shared singleton instance.</summary>
        internal static readonly ByteArrayComparer Instance = new( );
        /// <summary>Returns true when both arrays are non-null and have identical contents.</summary>
        /// <param name="x">The first array.</param>
        /// <param name="y">The second array.</param>
        /// <returns><see langword="true"/> if the arrays are element-wise equal; otherwise <see langword="false"/>.</returns>
        public bool Equals( byte[]? x, byte[]? y ) => x is not null && y is not null && x.AsSpan( ).SequenceEqual( y );
        /// <summary>Computes a content-based hash code over the array's bytes.</summary>
        /// <param name="obj">The array to hash.</param>
        /// <returns>A hash code derived from the array contents.</returns>
        public int GetHashCode( byte[] obj ) {
            HashCode hc = new( );
            hc.AddBytes( obj );
            return hc.ToHashCode( );
        }
    }
}

#pragma warning restore CS1591
