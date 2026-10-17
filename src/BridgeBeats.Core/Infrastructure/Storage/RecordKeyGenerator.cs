using System.Security.Cryptography;
using System.Text;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Core.Domain.Services;

namespace BridgeBeats.Core.Infrastructure.Storage;

/// <summary>
/// Generates deterministic record keys (rkeys) and card identifiers for media-link records, to
/// ensure consistent mapping and prevent duplication.
/// </summary>
/// <remarks>
/// Determinism is the point: the same input always produces the same rkey, so writes are idempotent
/// and the same logical record naturally deduplicates. An rkey is derived from an external id when
/// one is present (<c>album:</c> or <c>track:</c> plus the sanitized id); otherwise it is derived
/// from a SHA-256 hash of the title, artist, and album/track kind, encoded as RFC 4648 base32.
/// </remarks>
public static class RecordKeyGenerator {

    /// <summary>
    /// Generates the rkey for a media-link result, preferring an external-id-based key and falling
    /// back to a metadata-based key.
    /// </summary>
    /// <param name="result">The media-link result; must contain at least one provider result.</param>
    /// <returns>
    /// An external-id-based rkey (<c>album:{id}</c> or <c>track:{id}</c>) when any result carries an
    /// external id (ISRC/UPC) that survives sanitization; otherwise a metadata-based rkey.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="result"/> has no results, or when no usable external id or metadata is available.</exception>
    public static string GenerateRkey( MediaLinkResult result ) {
        if (result?.Results == null || result.Results.Count == 0) {
            throw new ArgumentException( "MediaLinkResult must contain at least one result", nameof( result ) );
        }

        // Find the first result with an external id that survives rkey sanitization.
        MusicLookupResult? firstResultWithId = result.Results.Values
            .FirstOrDefault( MediaLookupResultIdentity.HasUsableExternalId );

        if (firstResultWithId != null) {
            string? rkey = GenerateRkey( firstResultWithId.ExternalId, firstResultWithId.IsAlbum ?? false );
            if (rkey is not null) { return rkey; }
        }
        // Fallback: Generate rkey from metadata hash
        return GenerateMetadataBasedRkey( result );
    }

    /// <summary>
    /// Generates a metadata-based rkey of the form <c>metadata:{hash}</c> from the first result's
    /// title, artist, and album/track kind, used as a fallback when no external id is available.
    /// </summary>
    /// <param name="result">The media-link result; its first provider result supplies the metadata.</param>
    /// <returns>
    /// An rkey <c>metadata:{hash}</c>, where the hash is the first 16 characters of the lowercase
    /// base32 encoding of the SHA-256 of the normalized <c>title|artist|kind</c> string.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when both title and artist of the first result are empty.</exception>
    private static string GenerateMetadataBasedRkey( MediaLinkResult result ) {
        // Get first result to extract metadata
        MusicLookupResult firstResult = result.Results.Values.First( );

        // Validate that at least one of Title or Artist is non-empty
        if (string.IsNullOrWhiteSpace( firstResult.Title ) && string.IsNullOrWhiteSpace( firstResult.Artist )) {
            throw new ArgumentException( "Cannot generate metadata-based rkey: both Title and Artist are empty" );
        }

        // Create a stable string from normalized metadata (lowercase, trimmed)
        string metadataString = $"{firstResult.Title?.Trim().ToLowerInvariant() ?? ""}|{firstResult.Artist?.Trim().ToLowerInvariant() ?? ""}|{(firstResult.IsAlbum == true ? "album" : "track")}";

        // Compute SHA-256 hash
        byte[] hashBytes = SHA256.HashData( Encoding.UTF8.GetBytes( metadataString ) );

        // Convert to base32 and truncate to reasonable length (16 chars for metadata-based keys)
        string base32Hash = ToBase32( hashBytes )[..16].ToLowerInvariant( );

        return $"metadata:{base32Hash}";
    }

    /// <summary>
    /// Generates an external-id-based rkey of the form <c>album:{id}</c> or <c>track:{id}</c>.
    /// </summary>
    /// <param name="externalId">The provider external id (a UPC or ISRC). Sanitized to letters, digits, and hyphen before use.</param>
    /// <param name="isAlbum">When <see langword="true"/>, the key uses the <c>album</c> prefix (UPC); otherwise <c>track</c> (ISRC).</param>
    /// <returns>The rkey <c>{album|track}:{sanitizedId}</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="externalId"/> is null, empty, or contains no valid characters after sanitization.</exception>
    public static string GenerateRkey( string externalId, bool isAlbum ) {
        if (string.IsNullOrWhiteSpace( externalId )) {
            throw new ArgumentException( "ExternalId cannot be null or empty", nameof( externalId ) );
        }

        string prefix = isAlbum ? "album" : "track";
        string sanitizedId = MediaLookupResultIdentity.NormalizeExternalId( externalId );

        return !sanitizedId.Any( char.IsLetterOrDigit )
            ? throw new ArgumentException( "ExternalId must contain valid characters after sanitization", nameof( externalId ) )
            : $"{prefix}:{sanitizedId}";
    }

    /// <summary>
    /// Generates a deterministic, URL-safe card identifier from an rkey by hashing it and encoding the
    /// hash as base32.
    /// </summary>
    /// <param name="rkey">The record key to derive the card id from; must be non-empty.</param>
    /// <param name="maxLength">The maximum length of the returned id (default 32). Must be positive.</param>
    /// <returns>
    /// The lowercase base32 encoding of <c>SHA-256(rkey)</c>, truncated to at most
    /// <paramref name="maxLength"/> characters. Base32 keeps the id URL-safe and case-insensitive.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rkey"/> is null, empty, or whitespace, or when <paramref name="maxLength"/> is not positive.</exception>
    public static string GenerateCardId( string rkey, int maxLength = 32 ) {
        if (string.IsNullOrWhiteSpace( rkey )) {
            throw new ArgumentException( "Rkey cannot be null or empty", nameof( rkey ) );
        }

        if (maxLength <= 0) {
            throw new ArgumentException( "Max length must be positive", nameof( maxLength ) );
        }

        // Compute SHA-256 hash of the rkey
        byte[] hashBytes = SHA256.HashData( Encoding.UTF8.GetBytes( rkey ) );

        // Convert to base32 (using a standard base32 alphabet)
        string base32Hash = ToBase32( hashBytes );

        // Truncate to maxLength and return lowercase for consistency
        return base32Hash[..Math.Min( maxLength, base32Hash.Length )].ToLowerInvariant( );
    }

    /// <summary>
    /// Encodes bytes as RFC 4648 base32 using the uppercase alphabet, then trims the <c>=</c> padding
    /// for URL safety.
    /// </summary>
    /// <param name="input">The bytes to encode.</param>
    /// <returns>The unpadded base32 string, or an empty string when <paramref name="input"/> is null or empty.</returns>
    private static string ToBase32( byte[] input ) {
        if (input == null || input.Length == 0) {
            return string.Empty;
        }

        // Base32 alphabet (RFC 4648)
        const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        StringBuilder result = new( );
        int bits = 0;
        int value = 0;

        foreach (byte b in input) {
            value = (value << 8) | b;
            bits += 8;

            while (bits >= 5) {
                bits -= 5;
                int index = (value >> bits) & 0x1F;
                _ = result.Append( Base32Alphabet[index] );
            }
        }

        if (bits > 0) {
            int index = (value << (5 - bits)) & 0x1F;
            _ = result.Append( Base32Alphabet[index] );
        }

        // RFC 4648: Pad output to a multiple of 8 characters with '='
        while (result.Length % 8 != 0) {
            _ = result.Append( '=' );
        }

        // Remove padding ('=') for URL safety
        return result.ToString( ).TrimEnd( '=' );
    }
}
