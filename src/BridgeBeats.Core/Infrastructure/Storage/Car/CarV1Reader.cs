using System.Formats.Cbor;

namespace BridgeBeats.Core.Infrastructure.Storage.Car;

/// <summary>
/// Reads a CARv1 (Content-Addressable aRchive, version 1) byte buffer into an in-memory map of
/// content-addressed blocks, the container format an atproto PDS returns from
/// <c>com.atproto.sync.getRepo</c>.
/// </summary>
/// <remarks>
/// The layout is: a varint length followed by the CBOR header (carrying <c>roots</c> and
/// <c>version</c>), then a sequence of sections, each being a varint length, a 36-byte CID, and
/// the block bytes (the section length covers both the CID and the block). The reader is
/// deliberately strict and bounded against hostile input: only version 1 is accepted (CARv2 is
/// rejected), every block's bytes are verified against its CID digest, individual blocks are
/// capped at 2&#160;MB, and every slice is bounds-checked before it is taken. A zero-length section
/// terminates the stream. All block buffers are slices over the single download buffer, with no
/// per-block copies.
/// </remarks>
internal static class CarV1Reader {

    /// <summary>Maximum size of a single decoded block (2&#160;MB). Larger blocks are rejected as malformed or hostile.</summary>
    private const int MaxBlockSize = 2 * 1024 * 1024; // 2 MB per block

    /// <summary>Fixed encoded length of an accepted CID: a 4-byte prefix plus a 32-byte digest.</summary>
    private const int ExpectedCidBytes = 4 + 32;       // CIDv1 dag-cbor sha2-256

    /// <summary>The CAR <c>version</c> value (<c>2</c>) that identifies CARv2, which this reader explicitly rejects.</summary>
    private const int CarV2Magic = 2;                  // CAR version 2 — rejected

    /// <summary>
    /// Parses a CARv1 buffer into a <see cref="CarFile"/> of verified blocks keyed by their
    /// lowercase-hex CID digest.
    /// </summary>
    /// <param name="buffer">The complete CARv1 byte content.</param>
    /// <returns>
    /// A <see cref="CarFile"/> exposing the root CID and the map of block digest (hex) to block bytes.
    /// </returns>
    /// <exception cref="CarParseException">
    /// Thrown when a length varint cannot be read, a declared length runs past the buffer, the header
    /// is not a supported CARv1 header, a CID is malformed, a block exceeds the 2&#160;MB cap, or a
    /// block's bytes do not hash to its CID digest.
    /// </exception>
    /// <remarks>
    /// The download is currently unbounded at the byte-count level; no upstream cap is enforced.
    /// A per-block count cap is intentionally omitted because it would false-positive against
    /// legitimate repo growth (the production repo grows ~1,810 records per 6-hour cycle; every
    /// record is its own IPLD block). The per-block 2 MB size cap and per-block sha256 digest
    /// verification remain active.
    /// </remarks>
    internal static CarFile Read( ReadOnlyMemory<byte> buffer ) {
        int offset = 0;
        ReadOnlySpan<byte> span = buffer.Span;

        // Parse header length varint
        if (!Varint.TryRead( span.Slice( offset ), out ulong headerLen, out int headerLenBytes )) {
            throw new CarParseException( "Failed to read CAR header length varint." );
        }
        offset += headerLenBytes;

        if (headerLen == 0 || (ulong)span.Length < (ulong)offset + headerLen) {
            throw new CarParseException( $"CAR header length {headerLen} exceeds buffer." );
        }

        // Parse header DAG-CBOR
        ReadOnlyMemory<byte> headerBytes = buffer.Slice( offset, (int)headerLen );
        Cid rootCid = ParseCarHeader( headerBytes );
        offset += (int)headerLen;

        // Parse block sections
        Dictionary<string, ReadOnlyMemory<byte>> blocks = [];

        while (offset < span.Length) {
            // Section length varint
            if (!Varint.TryRead( span.Slice( offset ), out ulong sectionLen, out int sectionLenBytes )) {
                throw new CarParseException( $"Failed to read section length varint at offset {offset}." );
            }
            offset += sectionLenBytes;

            if (sectionLen == 0) {
                // Zero-length section = stream terminator (valid in some CAR dialects)
                break;
            }

            if ((ulong)(span.Length - offset) < sectionLen) {
                throw new CarParseException( $"Section length {sectionLen} at offset {offset} exceeds remaining buffer." );
            }

            int sectionStart = offset;

            // CID bytes (fixed 36 bytes for CIDv1 dag-cbor sha256)
            if (span.Length - offset < ExpectedCidBytes) {
                throw new CarParseException( $"Section at offset {offset} too short for CID." );
            }
            Cid blockCid = Cid.ParseFromBytes( span.Slice( offset, ExpectedCidBytes ) );
            offset += ExpectedCidBytes;

            // Block bytes
            int blockLen = (int)sectionLen - ExpectedCidBytes;
            if (blockLen < 0) {
                throw new CarParseException( $"Section length {sectionLen} smaller than CID size at offset {sectionStart}." );
            }
            if (blockLen > MaxBlockSize) {
                throw new CarParseException( $"Block at offset {offset} exceeds 2 MB cap ({blockLen} bytes)." );
            }

            // Verify sha256 digest
            ReadOnlySpan<byte> blockSpan = span.Slice( offset, blockLen );
            if (!blockCid.VerifyBlock( blockSpan )) {
                throw new CarParseException( $"Block digest mismatch at offset {offset} (CID: {blockCid})." );
            }

            // Store as a slice over the original buffer (no copy)
            blocks[blockCid.KeyHex] = buffer.Slice( offset, blockLen );
            offset += blockLen;
        }

        return new CarFile( rootCid, blocks );
    }

    /// <summary>
    /// Parses the CAR header bytes and returns the single root CID.
    /// </summary>
    /// <param name="headerBytes">The raw CBOR header bytes (the region after the header-length varint).</param>
    /// <returns>The root CID declared in the header's <c>roots</c> array.</returns>
    /// <exception cref="CarParseException">Thrown when the header is malformed or declares an unsupported version.</exception>
    private static Cid ParseCarHeader( ReadOnlyMemory<byte> headerBytes ) =>
        ParseRootCidFromRawHeader( headerBytes );

    /// <summary>
    /// Decodes the CBOR header map, extracting the root CID from the <c>roots</c> array (a tag-42
    /// CID link) and validating the <c>version</c> field. Uses
    /// <see cref="System.Formats.Cbor.CborConformanceMode.Lax"/>; canonical DAG-CBOR key ordering
    /// (length-first then lexicographic) is not enforced by this decoder.
    /// </summary>
    /// <param name="headerBytes">The raw CBOR header bytes.</param>
    /// <returns>The root CID.</returns>
    /// <exception cref="CarParseException">
    /// Thrown when a root entry is not a tag-42 CID link, when <c>version</c> is 2 (CARv2) or any
    /// value other than 1, when <c>version</c> or <c>roots</c> is missing, or when the underlying
    /// CBOR is invalid.
    /// </exception>
    private static Cid ParseRootCidFromRawHeader( ReadOnlyMemory<byte> headerBytes ) {
        try {
            CborReader reader = new( headerBytes, CborConformanceMode.Lax );
            _ = reader.ReadStartMap( );

            Cid? rootCid = null;
            int? version = null;

            while (reader.PeekState( ) != CborReaderState.EndMap) {
                string key = reader.ReadTextString( );
                if (key == "roots") {
                    _ = reader.ReadStartArray( );
                    while (reader.PeekState( ) != CborReaderState.EndArray) {
                        CborTag tag = reader.ReadTag( );
                        if (tag != (CborTag)42) {
                            throw new CarParseException( $"CAR header root CID has unexpected tag {(ulong)tag}." );
                        }
                        byte[] linkBytes = reader.ReadByteString( );
                        rootCid = Cid.FromDagCborLinkBytes( linkBytes );
                    }
                    reader.ReadEndArray( );
                } else if (key == "version") {
                    version = reader.ReadInt32( );
                    if (version == CarV2Magic) {
                        throw new CarParseException( "CAR v2 is not supported; only v1 is accepted." );
                    }
                    if (version != 1) {
                        throw new CarParseException( $"Unsupported CAR version {version}; expected 1." );
                    }
                } else {
                    reader.SkipValue( );
                }
            }

            reader.ReadEndMap( );

            if (version is null) {
                throw new CarParseException( "CAR header missing required 'version' field." );
            }

            if (rootCid is null) {
                throw new CarParseException( "CAR header missing roots." );
            }

            return rootCid.Value;
        } catch (CarParseException) {
            throw;
        } catch (Exception ex) when (ex is CborContentException or OverflowException or InvalidOperationException) {
            throw new CarParseException( "Failed to parse CAR header CBOR.", ex );
        }
    }
}

/// <summary>
/// The decoded contents of a CARv1 buffer: the repository root CID and the set of verified blocks
/// keyed by their digest, ready for the MST walk and per-record CBOR decoding.
/// </summary>
internal sealed class CarFile {

    /// <summary>
    /// Initializes a new instance with the root CID and the block map.
    /// </summary>
    /// <param name="root">The repository root CID, which names the commit block.</param>
    /// <param name="blocks">The map of block digest (lowercase hex) to block bytes.</param>
    internal CarFile( Cid root, Dictionary<string, ReadOnlyMemory<byte>> blocks ) {
        Root = root;
        Blocks = blocks;
    }

    /// <summary>Gets the repository root CID. The block it points to is the commit block.</summary>
    internal Cid Root { get; }

    /// <summary>
    /// Gets the verified blocks, keyed by their lowercase-hex sha2-256 digest
    /// (<see cref="Cid.KeyHex"/>). Lookups during the MST walk and value resolution use this map.
    /// </summary>
    internal Dictionary<string, ReadOnlyMemory<byte>> Blocks { get; }
}
