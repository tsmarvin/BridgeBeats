using BridgeBeats.Contracts.Enums;
using BridgeBeats.Core.Domain.Utilities;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ProviderUrlParser"/>, the provider-agnostic URL-to-id extractor.
/// Cover the per-provider extractors (<c>ExtractAppleMusicId</c>, <c>ExtractSpotifyId</c>,
/// <c>ExtractTidalId</c>) and the <c>ExtractId</c> switch that dispatches on
/// <see cref="SupportedProviders"/>. Exercise the supported URL shapes per provider, the
/// <c>?i=</c> Apple Music song-within-album selector, case-insensitive hosts, and the
/// null/empty/invalid/unsupported-provider paths that return <c>null</c>.
/// </summary>
[TestClass]
public class ProviderUrlParserTests {

    #region Apple Music Tests

    /// <summary>An Apple Music album URL yields the trailing numeric album id.</summary>
    [TestMethod]
    public void ExtractAppleMusicId_ExtractsId_FromAlbumUrl( ) {
        // Arrange
        string url = "https://music.apple.com/us/album/album-name/1234567890";

        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( url );

        // Assert
        Assert.AreEqual( "1234567890", id );
    }

    /// <summary>An Apple Music song URL yields the trailing numeric song id.</summary>
    [TestMethod]
    public void ExtractAppleMusicId_ExtractsId_FromSongUrl( ) {
        // Arrange
        string url = "https://music.apple.com/us/song/song-name/1234567890";

        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( url );

        // Assert
        Assert.AreEqual( "1234567890", id );
    }

    /// <summary>
    /// For an album URL carrying the <c>?i=</c> selector, the song id from the <c>i</c> query
    /// parameter takes precedence over the album id in the path.
    /// </summary>
    [TestMethod]
    public void ExtractAppleMusicId_ExtractsSongId_FromAlbumUrlWithQueryParameter( ) {
        // Arrange
        string url = "https://music.apple.com/us/album/album-name/9876543210?i=1234567890";

        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( url );

        // Assert
        Assert.AreEqual( "1234567890", id, "Should extract song ID from ?i= query parameter" );
    }

    /// <summary>
    /// An album URL with unrelated query parameters (no <c>i</c> selector) still yields the
    /// album id from the path.
    /// </summary>
    [TestMethod]
    public void ExtractAppleMusicId_ExtractsId_FromUrlWithQueryParameters( ) {
        // Arrange
        string url = "https://music.apple.com/us/album/album-name/1234567890?app=music&ign-mpt=uo%3D4";

        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( url );

        // Assert
        Assert.AreEqual( "1234567890", id );
    }

    /// <summary>Host matching is case-insensitive, so a mixed-case domain still yields the id.</summary>
    [TestMethod]
    public void ExtractAppleMusicId_HandlesCase_MixedCaseDomain( ) {
        // Arrange
        string url = "https://Music.Apple.Com/us/album/album-name/1234567890";

        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( url );

        // Assert
        Assert.AreEqual( "1234567890", id );
    }

    /// <summary>A non-Apple-Music URL yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractAppleMusicId_ReturnsNull_ForInvalidUrl( ) {
        // Arrange
        string url = "https://example.com/not-apple-music";

        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( url );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>A <c>null</c> input yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractAppleMusicId_ReturnsNull_ForNullInput( ) {
        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( null! );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>An empty-string input yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractAppleMusicId_ReturnsNull_ForEmptyInput( ) {
        // Act
        string? id = ProviderUrlParser.ExtractAppleMusicId( "" );

        // Assert
        Assert.IsNull( id );
    }

    #endregion

    #region Spotify Tests

    /// <summary>A Spotify track URL yields the base-62 track id.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ExtractsId_FromTrackUrl( ) {
        // Arrange
        string url = "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp";

        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( url );

        // Assert
        Assert.AreEqual( "3n3Ppam7vgaVa1iaRUc9Lp", id );
    }

    /// <summary>A Spotify album URL yields the base-62 album id.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ExtractsId_FromAlbumUrl( ) {
        // Arrange
        string url = "https://open.spotify.com/album/6WdSsBrH5QtofaTTqgwxOV";

        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( url );

        // Assert
        Assert.AreEqual( "6WdSsBrH5QtofaTTqgwxOV", id );
    }

    /// <summary>A Spotify prerelease URL yields the prerelease id.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ExtractsId_FromPrereleaseUrl( ) {
        // Arrange
        string url = "https://open.spotify.com/prerelease/1ABC2DEF3GHI4JKL5MNO6P";

        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( url );

        // Assert
        Assert.AreEqual( "1ABC2DEF3GHI4JKL5MNO6P", id );
    }

    /// <summary>A track URL with a <c>?si=</c> share token still yields the track id.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ExtractsId_FromUrlWithQueryParameters( ) {
        // Arrange
        string url = "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp?si=abc123";

        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( url );

        // Assert
        Assert.AreEqual( "3n3Ppam7vgaVa1iaRUc9Lp", id );
    }

    /// <summary>Host and path matching is case-insensitive, so an upper-case URL still yields the id.</summary>
    [TestMethod]
    public void ExtractSpotifyId_HandlesCase_CaseInsensitive( ) {
        // Arrange
        string url = "https://OPEN.SPOTIFY.COM/TRACK/3n3Ppam7vgaVa1iaRUc9Lp";

        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( url );

        // Assert
        Assert.AreEqual( "3n3Ppam7vgaVa1iaRUc9Lp", id );
    }

    /// <summary>A non-Spotify URL yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ReturnsNull_ForInvalidUrl( ) {
        // Arrange
        string url = "https://example.com/not-spotify";

        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( url );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>A <c>null</c> input yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ReturnsNull_ForNullInput( ) {
        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( null! );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>An empty-string input yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractSpotifyId_ReturnsNull_ForEmptyInput( ) {
        // Act
        string? id = ProviderUrlParser.ExtractSpotifyId( "" );

        // Assert
        Assert.IsNull( id );
    }

    #endregion

    #region Tidal Tests

    /// <summary>A Tidal <c>/browse/track/</c> URL yields the numeric track id.</summary>
    [TestMethod]
    public void ExtractTidalId_ExtractsId_FromTrackUrl( ) {
        // Arrange
        string url = "https://tidal.com/browse/track/123456789";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.AreEqual( "123456789", id );
    }

    /// <summary>A Tidal <c>/browse/album/</c> URL yields the numeric album id.</summary>
    [TestMethod]
    public void ExtractTidalId_ExtractsId_FromAlbumUrl( ) {
        // Arrange
        string url = "https://tidal.com/browse/album/987654321";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.AreEqual( "987654321", id );
    }

    /// <summary>A <c>listen.tidal.com</c> track URL yields the numeric track id.</summary>
    [TestMethod]
    public void ExtractTidalId_ExtractsId_FromListenSubdomain( ) {
        // Arrange
        string url = "https://listen.tidal.com/track/123456789";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.AreEqual( "123456789", id );
    }

    /// <summary>A Tidal track URL without the <c>/browse</c> segment still yields the track id.</summary>
    [TestMethod]
    public void ExtractTidalId_ExtractsId_FromUrlWithoutBrowse( ) {
        // Arrange
        string url = "https://tidal.com/track/123456789";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.AreEqual( "123456789", id );
    }

    /// <summary>
    /// An artist URL yields <c>null</c>: only track and album entities carry an extractable id.
    /// </summary>
    [TestMethod]
    public void ExtractTidalId_ReturnsNull_ForArtistUrl( ) {
        // Arrange
        string url = "https://tidal.com/browse/artist/123456";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.IsNull( id, "Artist URLs should not return an ID" );
    }

    /// <summary>Host and path matching is case-insensitive, so an upper-case URL still yields the id.</summary>
    [TestMethod]
    public void ExtractTidalId_HandlesCase_CaseInsensitive( ) {
        // Arrange
        string url = "https://TIDAL.COM/BROWSE/TRACK/123456789";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.AreEqual( "123456789", id );
    }

    /// <summary>A non-Tidal URL yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractTidalId_ReturnsNull_ForInvalidUrl( ) {
        // Arrange
        string url = "https://example.com/not-tidal";

        // Act
        string? id = ProviderUrlParser.ExtractTidalId( url );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>A <c>null</c> input yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractTidalId_ReturnsNull_ForNullInput( ) {
        // Act
        string? id = ProviderUrlParser.ExtractTidalId( null! );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>An empty-string input yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractTidalId_ReturnsNull_ForEmptyInput( ) {
        // Act
        string? id = ProviderUrlParser.ExtractTidalId( "" );

        // Assert
        Assert.IsNull( id );
    }

    #endregion

    #region ExtractId (Provider Switch) Tests

    /// <summary>
    /// <c>ExtractId</c> with <see cref="SupportedProviders.AppleMusic"/> routes to the Apple Music
    /// extractor and returns its id.
    /// </summary>
    [TestMethod]
    public void ExtractId_RoutesToAppleMusicParser_ForAppleMusicProvider( ) {
        // Arrange
        string url = "https://music.apple.com/us/album/album-name/1234567890";

        // Act
        string? id = ProviderUrlParser.ExtractId( SupportedProviders.AppleMusic, url );

        // Assert
        Assert.AreEqual( "1234567890", id );
    }

    /// <summary>
    /// <c>ExtractId</c> with <see cref="SupportedProviders.Spotify"/> routes to the Spotify
    /// extractor and returns its id.
    /// </summary>
    [TestMethod]
    public void ExtractId_RoutesToSpotifyParser_ForSpotifyProvider( ) {
        // Arrange
        string url = "https://open.spotify.com/track/3n3Ppam7vgaVa1iaRUc9Lp";

        // Act
        string? id = ProviderUrlParser.ExtractId( SupportedProviders.Spotify, url );

        // Assert
        Assert.AreEqual( "3n3Ppam7vgaVa1iaRUc9Lp", id );
    }

    /// <summary>
    /// <c>ExtractId</c> with <see cref="SupportedProviders.Tidal"/> routes to the Tidal extractor
    /// and returns its id.
    /// </summary>
    [TestMethod]
    public void ExtractId_RoutesToTidalParser_ForTidalProvider( ) {
        // Arrange
        string url = "https://tidal.com/browse/track/123456789";

        // Act
        string? id = ProviderUrlParser.ExtractId( SupportedProviders.Tidal, url );

        // Assert
        Assert.AreEqual( "123456789", id );
    }

    /// <summary>
    /// <c>ExtractId</c> with an out-of-range provider value yields <c>null</c> (no extractor matches).
    /// </summary>
    [TestMethod]
    public void ExtractId_ReturnsNull_ForUnsupportedProvider( ) {
        // Arrange
        string url = "https://example.com/track/123";

        // Act
        string? id = ProviderUrlParser.ExtractId( (SupportedProviders)999, url );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary><c>ExtractId</c> with a <c>null</c> URL yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractId_ReturnsNull_ForNullUrl( ) {
        // Act
        string? id = ProviderUrlParser.ExtractId( SupportedProviders.Spotify, null! );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary><c>ExtractId</c> with an empty URL yields <c>null</c>.</summary>
    [TestMethod]
    public void ExtractId_ReturnsNull_ForEmptyUrl( ) {
        // Act
        string? id = ProviderUrlParser.ExtractId( SupportedProviders.Spotify, "" );

        // Assert
        Assert.IsNull( id );
    }

    /// <summary>Storefront extraction accepts only Apple Music country path segments.</summary>
    [TestMethod]
    [DataRow( SupportedProviders.AppleMusic, "https://music.apple.com/JP/album/example/123", "jp" )]
    [DataRow( SupportedProviders.AppleMusic, "https://music.apple.com/us/song/example/123", "us" )]
    [DataRow( SupportedProviders.AppleMusic, "https://example.com/us/album/example/123", null )]
    [DataRow( SupportedProviders.Spotify, "https://music.apple.com/us/album/example/123", null )]
    [DataRow( SupportedProviders.Tidal, "https://tidal.com/browse/track/123", null )]
    [DataRow( SupportedProviders.AppleMusic, "not-a-url", null )]
    public void ExtractStorefront_ProviderAndUrlShape_ReturnsExpected(
        SupportedProviders provider,
        string url,
        string? expected
    ) {
        string? storefront = ProviderUrlParser.ExtractStorefront( provider, url );

        Assert.AreEqual( expected, storefront );
    }

    #endregion
}
