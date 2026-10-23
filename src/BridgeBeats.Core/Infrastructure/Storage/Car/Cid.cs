using System.Security.Cryptography;

namespace BridgeBeats.Core.Infrastructure.Storage.Car;

/// <summary>
/// A content identifier (CID) for an atproto repository block, restricted to the single
/// shape atproto uses: CIDv1 with the dag-cbor codec and a sha2-256 digest.
/// </summary>
/// <remarks>
/// Every accepted CID is exactly 36 bytes: a four-byte prefix
/// (<c>0x01</c> version, <c>0x71</c> dag-cbor codec, <c>0x12</c> sha2-256 hash function,
/// <c>0x20</c> 32-byte digest length) followed by the 32-byte digest. CIDv0 and any other
/// codec, hash function, or length are rejected. The 32-byte digest is the SHA-256 of the
/// block bytes, so a CID both names a block and lets the reader verify the block's integrity
/// (content addressing). The struct stores only the digest; the fixed prefix is re-applied
/// when re-encoding.
/// </remarks>
internal readonly struct Cid {

    // CIDv1 codec prefix: version=0x01, codec=dag-cbor=0x71, hash-fn=sha2-256=0x12, hash-len=0x20
    /// <summary>Multicodec version byte for CIDv1 (the only version atproto uses).</summary>
    private const byte CidV1Version = 0x01;

    /// <summary>Multicodec content-type byte for the dag-cbor codec (<c>0x71</c>).</summary>
    private const byte DagCborCodec = 0x71;

    /// <summary>Multihash function byte for sha2-256 (<c>0x12</c>).</summary>
    private const byte Sha256HashFn = 0x12;

    /// <summary>Multihash digest-length byte for a 32-byte (256-bit) digest (<c>0x20</c>).</summary>
    private const byte Sha256HashLen = 0x20;

    /// <summary>Total encoded length of an accepted CID: a 4-byte prefix plus the 32-byte digest.</summary>
    private const int ExpectedCidBytes = 4 + 32; // 4-byte prefix + 32-byte digest

    /// <summary>
    /// First byte of a CIDv0 (<c>0x12</c>, a raw sha2-256 multihash). Used to detect and explicitly
    /// reject CIDv0, which atproto never produces.
    /// </summary>
    private const byte CidV0FirstByte = 0x12;

    /// <summary>The 32-byte sha2-256 digest that uniquely identifies the block.</summary>
    private readonly byte[] _digest; // 32-byte sha256 digest

    /// <summary>
    /// Initializes a CID from an already-validated 32-byte digest.
    /// </summary>
    /// <param name="digest">The 32-byte sha2-256 digest. Callers must validate length before invoking.</param>
    private Cid( byte[] digest ) {
        _digest = digest;
    }

    /// <summary>
    /// Gets the lowercase hexadecimal encoding of the digest. This is the key used to index
    /// blocks in the in-memory block map (<see cref="CarFile.Blocks"/>), so the same block is
    /// always found under a stable, case-normalized key.
    /// </summary>
    internal string KeyHex => Convert.ToHexStringLower( _digest );

    /// <summary>Rehydrates a CID from the lowercase digest key used by the CAR block map.</summary>
    internal static Cid FromKeyHex( string keyHex ) {
        ArgumentException.ThrowIfNullOrWhiteSpace( keyHex );
        byte[] digest = Convert.FromHexString( keyHex );
        return digest.Length == 32
            ? new Cid( digest )
            : throw new CarParseException( $"CID digest must be 32 bytes, got {digest.Length}." );
    }

    /// <summary>
    /// Parses a CID from its exact 36-byte binary encoding, validating that it is the
    /// CIDv1 dag-cbor sha2-256 shape atproto requires.
    /// </summary>
    /// <param name="bytes">The CID bytes, which must be exactly 36 bytes (4-byte prefix plus 32-byte digest).</param>
    /// <returns>A <see cref="Cid"/> carrying the extracted 32-byte digest.</returns>
    /// <exception cref="CarParseException">
    /// Thrown when the input is a CIDv0, is not exactly 36 bytes, or does not begin with the
    /// expected <c>0x01 0x71 0x12 0x20</c> prefix.
    /// </exception>
    internal static Cid ParseFromBytes( ReadOnlySpan<byte> bytes ) {
        if (bytes.Length > 0 && bytes[0] == CidV0FirstByte) {
            throw new CarParseException( "CIDv0 is not supported; atproto repos use CIDv1 only." );
        }

        if (bytes.Length != ExpectedCidBytes) {
            throw new CarParseException(
                bytes.Length < ExpectedCidBytes
                    ? $"CID bytes too short: expected exactly {ExpectedCidBytes}, got {bytes.Length}."
                    : $"CID bytes too long: expected exactly {ExpectedCidBytes}, got {bytes.Length}." );
        }

        if (bytes[0] != CidV1Version || bytes[1] != DagCborCodec ||
            bytes[2] != Sha256HashFn || bytes[3] != Sha256HashLen) {
            throw new CarParseException(
                $"Unexpected CID prefix 0x{bytes[0]:x2} 0x{bytes[1]:x2} 0x{bytes[2]:x2} 0x{bytes[3]:x2}; " +
                "expected CIDv1 dag-cbor sha2-256 (0x01 0x71 0x12 0x20)." );
        }

        byte[] digest = bytes.Slice( 4, 32 ).ToArray( );
        return new Cid( digest );
    }

    /// <summary>
    /// Parses a CID from the byte string carried by a DAG-CBOR tag-42 CID link, which prepends a
    /// single <c>0x00</c> multibase-identity prefix before the raw CID bytes.
    /// </summary>
    /// <param name="bytes">The tag-42 link bytes: a leading <c>0x00</c> followed by the 36-byte CID encoding.</param>
    /// <returns>The parsed <see cref="Cid"/>.</returns>
    /// <exception cref="CarParseException">
    /// Thrown when the leading <c>0x00</c> multibase prefix is missing, or when the remaining
    /// bytes are not a valid CID per <see cref="ParseFromBytes"/>.
    /// </exception>
    internal static Cid FromDagCborLinkBytes( ReadOnlySpan<byte> bytes ) {
        if (bytes.Length < 1 || bytes[0] != 0x00) {
            throw new CarParseException(
                $"DAG-CBOR tag-42 link missing 0x00 multibase prefix (first byte: 0x{(bytes.Length > 0 ? bytes[0] : 0xFF):x2})." );
        }

        return ParseFromBytes( bytes.Slice( 1 ) );
    }

    /// <summary>
    /// Verifies that a block's bytes hash to this CID's digest, the content-addressing integrity
    /// check that proves a block has not been altered or substituted.
    /// </summary>
    /// <param name="blockBytes">The raw block bytes to hash and compare.</param>
    /// <returns>
    /// <see langword="true"/> if the SHA-256 of <paramref name="blockBytes"/> equals this CID's
    /// 32-byte digest; otherwise <see langword="false"/>.
    /// </returns>
    internal bool VerifyBlock( ReadOnlySpan<byte> blockBytes ) {
        Span<byte> hash = stackalloc byte[32];
        int written = SHA256.HashData( blockBytes, hash );
        return written == 32 && hash.SequenceEqual( _digest );
    }

    /// <summary>
    /// Re-encodes the CID to its canonical string form: the four-byte prefix and 32-byte digest
    /// encoded as RFC 4648 lowercase base32 (no padding) with the multibase <c>b</c> prefix that
    /// marks base32. This is the textual CID used in the dag-json <c>$link</c> sentinel.
    /// </summary>
    /// <returns>The multibase base32 string form of the CID, for example <c>bafyrei...</c>.</returns>
    public override string ToString( ) {
        // Build: 0x01 0x71 0x12 0x20 + digest, then base32lower with 'b' prefix
        byte[] cidBytes = new byte[ExpectedCidBytes];
        cidBytes[0] = CidV1Version;
        cidBytes[1] = DagCborCodec;
        cidBytes[2] = Sha256HashFn;
        cidBytes[3] = Sha256HashLen;
        _digest.CopyTo( cidBytes, 4 );
        return "b" + Base32Encode( cidBytes );
    }

    /// <summary>
    /// Encodes bytes as RFC 4648 base32 using the lowercase alphabet and no padding, the encoding
    /// atproto uses for textual CIDs.
    /// </summary>
    /// <param name="data">The bytes to encode.</param>
    /// <returns>The lowercase, unpadded base32 string.</returns>
    private static string Base32Encode( byte[] data ) {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        System.Text.StringBuilder sb = new( );
        int bits = 0;
        int accumulator = 0;

        foreach (byte b in data) {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5) {
                bits -= 5;
                _ = sb.Append( Alphabet[(accumulator >> bits) & 0x1F] );
            }
        }

        if (bits > 0) {
            _ = sb.Append( Alphabet[(accumulator << (5 - bits)) & 0x1F] );
        }

        return sb.ToString( );
    }
}
