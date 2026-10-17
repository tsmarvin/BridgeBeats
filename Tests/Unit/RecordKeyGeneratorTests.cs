using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Domain.Services;
using BridgeBeats.Core.Infrastructure.Storage;

namespace BridgeBeats.Tests.Unit {

    /// <summary>
    /// Unit tests for <see cref="RecordKeyGenerator"/>, the deterministic ATProto record-key (rkey)
    /// and card-id generator. Verify the external-id rkey forms (<c>track:{id}</c> / <c>album:{id}</c>),
    /// the metadata fallback (<c>metadata:{hash}</c>) when no external id exists, special-character
    /// sanitization, the direct-id overload and its empty-id guard, and the card-id derivation:
    /// determinism, distinctness across rkeys, URL-safe lowercase output bounded by the max length,
    /// metadata normalization (case/whitespace insensitive), and the empty-input/invalid-length guards.
    /// </summary>
    [TestClass]
    public class RecordKeyGeneratorTests {

        /// <summary>
        /// A result whose track carries an ISRC external id produces the rkey <c>track:{externalId}</c>.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithTrackISRC_ReturnsTrackRkey( ) {
            // Arrange
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "USRC12345678",
                IsAlbum = false,
                Artist = "Test Artist",
                Title = "Test Track",
                URL = "https://open.spotify.com/track/test"
            } );

            // Act
            string rkey = RecordKeyGenerator.GenerateRkey( result );

            // Assert
            Assert.IsNotNull( rkey );
            Assert.AreEqual( "track:USRC12345678", rkey );
        }

        /// <summary>
        /// A result whose album carries a UPC external id produces the rkey <c>album:{externalId}</c>.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithAlbumUPC_ReturnsAlbumRkey( ) {
            // Arrange
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.AppleMusic, new MusicLookupResult {
                ExternalId = "123456789012",
                IsAlbum = true,
                Artist = "Test Artist",
                Title = "Test Album",
                URL = "https://music.apple.com/album/test"
            } );

            // Act
            string rkey = RecordKeyGenerator.GenerateRkey( result );

            // Assert
            Assert.IsNotNull( rkey );
            Assert.AreEqual( "album:123456789012", rkey );
        }

        /// <summary>
        /// A result with no external id falls back to a metadata-hash rkey prefixed <c>metadata:</c>.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithNoExternalId_ReturnsMetadataBasedRkey( ) {
            // Arrange
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "",
                IsAlbum = false,
                Artist = "Test Artist",
                Title = "Test Track",
                URL = "https://open.spotify.com/track/test"
            } );

            // Act
            string rkey = RecordKeyGenerator.GenerateRkey( result );

            // Assert
            Assert.IsNotNull( rkey );
            Assert.StartsWith( "metadata:", rkey );
        }

        /// <summary>
        /// An external id with characters outside <c>[A-Za-z0-9-]</c> is sanitized, dropping the
        /// disallowed characters from the rkey.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithExternalIdContainingSpecialChars_SanitizesRkey( ) {
            // Arrange
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "US-RC1-23-45678!@#",
                IsAlbum = false,
                Artist = "Test Artist",
                Title = "Test Track",
                URL = "https://open.spotify.com/track/test"
            } );

            // Act
            string rkey = RecordKeyGenerator.GenerateRkey( result );

            // Assert
            Assert.IsNotNull( rkey );
            Assert.AreEqual( "track:US-RC1-23-45678", rkey );
        }

        /// <summary>Streaming and durable storage derive the same normalized external identity.</summary>
        [TestMethod]
        public void ExternalIdentity_WithSpecialCharacters_MatchesDurableRkey( ) {
            MusicLookupResult result = new( ) {
                ExternalId = " US-RC1-23-45678!@# ",
                IsAlbum = false
            };

            string? streamIdentity = MediaLookupResultIdentity.GetExternalKey( result );
            string durableIdentity = RecordKeyGenerator.GenerateRkey( result.ExternalId, false );

            Assert.AreEqual( durableIdentity, streamIdentity );
        }

        /// <summary>An unusable first external id does not hide a later valid provider identity.</summary>
        [TestMethod]
        public void GenerateRkey_WithFirstInvalidExternalId_UsesLaterValidId( ) {
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "@@@",
                IsAlbum = false
            } );
            result.Results.Add( SupportedProviders.AppleMusic, new MusicLookupResult {
                ExternalId = "USRC12345678",
                IsAlbum = false
            } );

            string rkey = RecordKeyGenerator.GenerateRkey( result );

            Assert.AreEqual( "track:USRC12345678", rkey );
        }

        /// <summary>A separator-only first id cannot hide a later durable provider identity.</summary>
        [TestMethod]
        public void GenerateRkey_WithHyphenOnlyFirstExternalId_UsesLaterValidId( ) {
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "---",
                IsAlbum = false
            } );
            result.Results.Add( SupportedProviders.AppleMusic, new MusicLookupResult {
                ExternalId = "USRC12345678",
                IsAlbum = false
            } );

            string rkey = RecordKeyGenerator.GenerateRkey( result );

            Assert.AreEqual( "track:USRC12345678", rkey );
        }

        /// <summary>A direct separator-only id is rejected instead of becoming a shared durable key.</summary>
        [TestMethod]
        public void GenerateRkey_WithHyphenOnlyExternalId_ThrowsArgumentException( ) {
            MusicLookupResult result = new( ) { ExternalId = "---", IsAlbum = false };

            Assert.IsFalse( MediaLookupResultIdentity.IsPersistable( result ) );
            Assert.IsNull( MediaLookupResultIdentity.GetExternalKey( result ) );
            _ = Assert.ThrowsExactly<ArgumentException>( ( ) =>
                RecordKeyGenerator.GenerateRkey( result.ExternalId, false ) );
        }

        /// <summary>
        /// The direct <c>GenerateRkey(externalId, isAlbum)</c> overload produces <c>track:{id}</c>
        /// when <c>isAlbum</c> is false.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_DirectMethod_ReturnsCorrectFormat( ) {
            // Act
            string rkey = RecordKeyGenerator.GenerateRkey( "USRC12345678", false );

            // Assert
            Assert.AreEqual( "track:USRC12345678", rkey );
        }

        /// <summary>
        /// The direct <c>GenerateRkey(externalId, isAlbum)</c> overload produces <c>album:{id}</c>
        /// when <c>isAlbum</c> is true.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_DirectMethod_WithAlbum_ReturnsAlbumPrefix( ) {
            // Act
            string rkey = RecordKeyGenerator.GenerateRkey( "123456789012", true );

            // Assert
            Assert.AreEqual( "album:123456789012", rkey );
        }

        /// <summary>
        /// The direct <c>GenerateRkey</c> overload throws <see cref="ArgumentException"/> for an empty id.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_DirectMethod_WithEmptyId_ThrowsException( ) {
            // Act & Assert
            _ = Assert.ThrowsExactly<ArgumentException>( ( ) => RecordKeyGenerator.GenerateRkey( "", false ) );
        }

        /// <summary>
        /// <c>GenerateCardId</c> is deterministic: the same rkey yields the same card id.
        /// </summary>
        [TestMethod]
        public void GenerateCardId_WithSameRkey_ReturnsSameId( ) {
            // Arrange
            string rkey = "track:USRC12345678";

            // Act
            string id1 = RecordKeyGenerator.GenerateCardId( rkey );
            string id2 = RecordKeyGenerator.GenerateCardId( rkey );

            // Assert
            Assert.AreEqual( id1, id2, "Card IDs should be deterministic" );
        }

        /// <summary>Different rkeys yield different card ids.</summary>
        [TestMethod]
        public void GenerateCardId_WithDifferentRkeys_ReturnsDifferentIds( ) {
            // Act
            string id1 = RecordKeyGenerator.GenerateCardId( "track:USRC12345678" );
            string id2 = RecordKeyGenerator.GenerateCardId( "track:USRC87654321" );

            // Assert
            Assert.AreNotEqual( id1, id2, "Different rkeys should generate different card IDs" );
        }

        /// <summary>
        /// A generated card id is URL-safe: at most 32 characters, lowercase, and limited to letters,
        /// digits, and hyphen.
        /// </summary>
        [TestMethod]
        public void GenerateCardId_ReturnsUrlSafeString( ) {
            // Arrange
            string rkey = "track:USRC12345678";

            // Act
            string cardId = RecordKeyGenerator.GenerateCardId( rkey );

            // Assert
            Assert.IsLessThanOrEqualTo( 32, cardId.Length, "Card ID should not exceed 32 characters" );
            Assert.IsTrue( cardId.All( c => char.IsLetterOrDigit( c ) || c == '-' ), "Card ID should be URL-safe" );
            Assert.IsTrue( cardId.All( c => !char.IsUpper( c ) ), "Card ID should be lowercase" );
        }

        /// <summary><c>GenerateCardId</c> throws <see cref="ArgumentException"/> for an empty rkey.</summary>
        [TestMethod]
        public void GenerateCardId_WithEmptyRkey_ThrowsException( ) {
            // Act & Assert
            _ = Assert.ThrowsExactly<ArgumentException>( ( ) => RecordKeyGenerator.GenerateCardId( "" ) );
        }

        /// <summary>
        /// <c>GenerateCardId</c> throws <see cref="ArgumentException"/> for an invalid (negative) max length.
        /// </summary>
        [TestMethod]
        public void GenerateCardId_WithInvalidMaxLength_ThrowsException( ) {
            // Act & Assert
            _ = Assert.ThrowsExactly<ArgumentException>( ( ) => RecordKeyGenerator.GenerateCardId( "track:test", -1 ) );
        }

        /// <summary>
        /// Two results with the same metadata (no external id) produce the same metadata-hash rkey,
        /// regardless of which provider supplied them.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithSameMetadata_ReturnsSameRkey( ) {
            // Arrange
            MediaLinkResult result1 = new( );
            result1.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "",
                IsAlbum = false,
                Artist = "Test Artist",
                Title = "Test Track",
                URL = "https://open.spotify.com/track/test1"
            } );

            MediaLinkResult result2 = new( );
            result2.Results.Add( SupportedProviders.AppleMusic, new MusicLookupResult {
                ExternalId = "",
                IsAlbum = false,
                Artist = "Test Artist",
                Title = "Test Track",
                URL = "https://music.apple.com/track/test2"
            } );

            // Act
            string rkey1 = RecordKeyGenerator.GenerateRkey( result1 );
            string rkey2 = RecordKeyGenerator.GenerateRkey( result2 );

            // Assert
            Assert.AreEqual( rkey1, rkey2, "Same metadata should generate same rkey" );
        }

        /// <summary>
        /// A result with no external id and both title and artist empty throws
        /// <see cref="ArgumentException"/> whose message names the empty title and artist.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithEmptyTitleAndArtist_ThrowsException( ) {
            // Arrange
            MediaLinkResult result = new( );
            result.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "",
                IsAlbum = false,
                Artist = "",
                Title = "",
                URL = "https://open.spotify.com/track/test"
            } );

            // Act & Assert
            ArgumentException ex = Assert.ThrowsExactly<ArgumentException>( ( ) => RecordKeyGenerator.GenerateRkey( result ) );
            Assert.Contains( "both Title and Artist are empty", ex.Message, "Exception message should contain expected text" );
        }

        /// <summary>
        /// Metadata normalization makes case and surrounding whitespace irrelevant: differently cased
        /// and padded title/artist values produce the same metadata-hash rkey.
        /// </summary>
        [TestMethod]
        public void GenerateRkey_WithNormalizedMetadata_ReturnsSameRkey( ) {
            // Arrange - Different case and whitespace but same content
            MediaLinkResult result1 = new( );
            result1.Results.Add( SupportedProviders.Spotify, new MusicLookupResult {
                ExternalId = "",
                IsAlbum = false,
                Artist = "Test Artist",
                Title = "Test Track",
                URL = "https://open.spotify.com/track/test1"
            } );

            MediaLinkResult result2 = new( );
            result2.Results.Add( SupportedProviders.AppleMusic, new MusicLookupResult {
                ExternalId = "",
                IsAlbum = false,
                Artist = "  TEST ARTIST  ",
                Title = "  test track  ",
                URL = "https://music.apple.com/track/test2"
            } );

            // Act
            string rkey1 = RecordKeyGenerator.GenerateRkey( result1 );
            string rkey2 = RecordKeyGenerator.GenerateRkey( result2 );

            // Assert
            Assert.AreEqual( rkey1, rkey2, "Metadata normalization should make case and whitespace irrelevant" );
        }

        /// <summary>
        /// <c>GenerateCardId</c> with a generous max length still returns a non-empty card id.
        /// </summary>
        [TestMethod]
        public void GenerateCardId_IncludesPadding( ) {
            // Arrange
            string rkey = "track:USRC12345678";

            // Act
            string cardId = RecordKeyGenerator.GenerateCardId( rkey, maxLength: 100 ); // Use longer length to see padding

            // Assert
            Assert.IsFalse( string.IsNullOrEmpty( cardId ) );
        }
    }
}
