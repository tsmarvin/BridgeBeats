using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Providers.AppleMusic;
using BridgeBeats.Core.Domain.Providers.Common;
using BridgeBeats.Core.Domain.Providers.Tidal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Verifies that provider-native track genres returned by Apple Music and Tidal are
/// written to the shared genre cache as part of a successful lookup.
/// </summary>
[TestClass]
public sealed class ProviderTrackGenreCachingTests {
    /// <summary>Apple Music song genre names are cached under the song's catalog id.</summary>
    [TestMethod]
    public async Task AppleMusicTrackLookup_CachesGenreNames( ) {
        const string Response = """
            {
              "data": [{
                "id": "123456",
                "type": "songs",
                "attributes": {
                  "artistName": "Artist",
                  "name": "Track",
                  "isrc": "USAAA0000001",
                  "url": "https://music.apple.com/us/song/123456",
                  "genreNames": ["Alternative", "Music"]
                }
              }]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );
        _ = genreCache
            .Setup( cache => cache.SetGenresAsync(
                SupportedProviders.AppleMusic,
                "123456",
                It.Is<IEnumerable<string>>( genres => genres.SequenceEqual( new[] { "Alternative", "Music" } ) ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        using ECDsa key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
        AppleJwtHandler jwtHandler = new( "team", "key", key.ExportPkcs8PrivateKeyPem( ) );
        Mock<IHttpClientFactory> factory = CreateFactory( "musickit-api", Response, "https://api.music.apple.com/v1/catalog/" );
        AppleMusicLookupService service = new(
            jwtHandler,
            factory.Object,
            NullLogger<AppleMusicLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        _ = await service.GetInfoByIDAsync( "123456", false );

        genreCache.VerifyAll( );
    }

    /// <summary>Tidal genre resources use genreName and are cached under the track id.</summary>
    [TestMethod]
    public async Task TidalTrackLookup_CachesIncludedGenreNames( ) {
        const string TokenResponse = """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""";
        const string TrackResponse = """
            {
              "data": {
                "id": "12345",
                "type": "tracks",
                "attributes": {
                  "title": "Track",
                  "isrc": "USAAA0000001",
                  "externalLinks": [{"href":"https://tidal.com/browse/track/12345"}]
                },
                "relationships": {
                  "artists": {"data":[{"id":"artist-1","type":"artists"}]},
                  "genres": {"data":[{"id":"genre-1","type":"genres"},{"id":"genre-2","type":"genres"}]}
                }
              },
              "included": [
                {"id":"artist-1","type":"artists","attributes":{"name":"Artist"}},
                {"id":"genre-1","type":"genres","attributes":{"genreName":"Electronic"}},
                {"id":"genre-2","type":"genres","attributes":{"genreName":"Dance"}}
              ]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );
        _ = genreCache
            .Setup( cache => cache.SetGenresAsync(
                SupportedProviders.Tidal,
                "12345",
                It.Is<IEnumerable<string>>( genres => genres.SequenceEqual( new[] { "Electronic", "Dance" } ) ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( item => item.CreateClient( "tidal-auth" ) )
            .Returns( CreateClient( TokenResponse, "https://auth.tidal.com/" ) );
        _ = factory.Setup( item => item.CreateClient( "tidal-api" ) )
            .Returns( CreateClient( TrackResponse, "https://openapi.tidal.com/v2/" ) );

        TidalTokenHandler tokenHandler = new(
            new TidalCredentials( "client", "secret" ),
            factory.Object,
            NullLogger<TidalTokenHandler>.Instance );
        TidalLookupService service = new(
            tokenHandler,
            factory.Object,
            NullLogger<TidalLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        _ = await service.GetInfoByIDAsync( "12345", false );

        genreCache.VerifyAll( );
    }

    /// <summary>
    /// An album lookup never writes to the genre cache: <c>TidalLookupService.ParseTidalResponse</c>
    /// gates genre caching to tracks only, even when the album response carries a fully resolvable
    /// genres relationship. Verified with an explicit <c>Times.Never()</c> assertion rather than
    /// relying on a strict-mock throw, because <c>CacheGenresFromIncludedAsync</c> catches and
    /// swallows any exception raised while calling <see cref="IGenreCacheService.SetGenresAsync"/>
    /// (including a strict-mock verification exception), which would otherwise mask an unauthorized
    /// call instead of failing the test.
    /// </summary>
    [TestMethod]
    public async Task TidalAlbumLookup_NeverCachesGenres( ) {
        const string TokenResponse = """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""";
        const string AlbumResponse = """
            {
              "data": {
                "id": "999999",
                "type": "albums",
                "attributes": {
                  "title": "Album",
                  "barcodeId": "000000000000",
                  "externalLinks": [{"href":"https://tidal.com/browse/album/999999"}]
                },
                "relationships": {
                  "artists": {"data":[{"id":"artist-1","type":"artists"}]},
                  "genres": {"data":[{"id":"genre-1","type":"genres"}]}
                }
              },
              "included": [
                {"id":"artist-1","type":"artists","attributes":{"name":"Artist"}},
                {"id":"genre-1","type":"genres","attributes":{"genreName":"Electronic"}}
              ]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );

        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( item => item.CreateClient( "tidal-auth" ) )
            .Returns( CreateClient( TokenResponse, "https://auth.tidal.com/" ) );
        _ = factory.Setup( item => item.CreateClient( "tidal-api" ) )
            .Returns( CreateClient( AlbumResponse, "https://openapi.tidal.com/v2/" ) );

        TidalTokenHandler tokenHandler = new(
            new TidalCredentials( "client", "secret" ),
            factory.Object,
            NullLogger<TidalTokenHandler>.Instance );
        TidalLookupService service = new(
            tokenHandler,
            factory.Object,
            NullLogger<TidalLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        MusicLookupResult? result = await service.GetInfoByIDAsync( "999999", true );

        Assert.IsNotNull( result );
        genreCache.Verify(
            cache => cache.SetGenresAsync(
                It.IsAny<SupportedProviders>( ),
                It.IsAny<string>( ),
                It.IsAny<IEnumerable<string>>( ),
                It.IsAny<CancellationToken>( ) ),
            Times.Never( ) );
    }

    /// <summary>
    /// When a track's genres relationship references genre ids that do not resolve to any
    /// <c>included</c> resource with a <c>genreName</c>, <see cref="IGenreCacheService.SetGenresAsync"/>
    /// is still called, with an empty collection. Per the cache contract, an empty collection means
    /// the provider confirmed no genres are available (as opposed to a cache miss), so writing it here
    /// pins that Tidal reports a definitive "no genres resolved" rather than leaving the cache unset.
    /// </summary>
    [TestMethod]
    public async Task TidalTrackLookup_UnresolvedGenreRelationship_CachesEmptyGenreList( ) {
        const string TokenResponse = """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""";
        const string TrackResponse = """
            {
              "data": {
                "id": "12345",
                "type": "tracks",
                "attributes": {
                  "title": "Track",
                  "isrc": "USAAA0000001",
                  "externalLinks": [{"href":"https://tidal.com/browse/track/12345"}]
                },
                "relationships": {
                  "artists": {"data":[{"id":"artist-1","type":"artists"}]},
                  "genres": {"data":[{"id":"genre-1","type":"genres"}]}
                }
              },
              "included": [
                {"id":"artist-1","type":"artists","attributes":{"name":"Artist"}}
              ]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );
        _ = genreCache
            .Setup( cache => cache.SetGenresAsync(
                SupportedProviders.Tidal,
                "12345",
                It.Is<IEnumerable<string>>( genres => !genres.Any( ) ),
                It.IsAny<CancellationToken>( ) ) )
            .Returns( Task.CompletedTask );

        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( item => item.CreateClient( "tidal-auth" ) )
            .Returns( CreateClient( TokenResponse, "https://auth.tidal.com/" ) );
        _ = factory.Setup( item => item.CreateClient( "tidal-api" ) )
            .Returns( CreateClient( TrackResponse, "https://openapi.tidal.com/v2/" ) );

        TidalTokenHandler tokenHandler = new(
            new TidalCredentials( "client", "secret" ),
            factory.Object,
            NullLogger<TidalTokenHandler>.Instance );
        TidalLookupService service = new(
            tokenHandler,
            factory.Object,
            NullLogger<TidalLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        MusicLookupResult? result = await service.GetInfoByIDAsync( "12345", false );

        Assert.IsNotNull( result );
        genreCache.VerifyAll( );
    }

    /// <summary>
    /// A genre-cache write failure never fails the Tidal lookup: <c>CacheGenresFromIncludedAsync</c>
    /// catches and logs the exception rather than letting it propagate, so the mapped result is
    /// still returned.
    /// </summary>
    [TestMethod]
    public async Task TidalTrackLookup_GenreCacheThrows_StillReturnsResult( ) {
        const string TokenResponse = """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""";
        const string TrackResponse = """
            {
              "data": {
                "id": "12345",
                "type": "tracks",
                "attributes": {
                  "title": "Track",
                  "isrc": "USAAA0000001",
                  "externalLinks": [{"href":"https://tidal.com/browse/track/12345"}]
                },
                "relationships": {
                  "artists": {"data":[{"id":"artist-1","type":"artists"}]},
                  "genres": {"data":[{"id":"genre-1","type":"genres"}]}
                }
              },
              "included": [
                {"id":"artist-1","type":"artists","attributes":{"name":"Artist"}},
                {"id":"genre-1","type":"genres","attributes":{"genreName":"Electronic"}}
              ]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );
        _ = genreCache
            .Setup( cache => cache.SetGenresAsync(
                SupportedProviders.Tidal,
                "12345",
                It.IsAny<IEnumerable<string>>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "cache unavailable" ) );

        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( item => item.CreateClient( "tidal-auth" ) )
            .Returns( CreateClient( TokenResponse, "https://auth.tidal.com/" ) );
        _ = factory.Setup( item => item.CreateClient( "tidal-api" ) )
            .Returns( CreateClient( TrackResponse, "https://openapi.tidal.com/v2/" ) );

        TidalTokenHandler tokenHandler = new(
            new TidalCredentials( "client", "secret" ),
            factory.Object,
            NullLogger<TidalTokenHandler>.Instance );
        TidalLookupService service = new(
            tokenHandler,
            factory.Object,
            NullLogger<TidalLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        MusicLookupResult? result = await service.GetInfoByIDAsync( "12345", false );

        Assert.IsNotNull( result );
        genreCache.VerifyAll( );
    }

    /// <summary>
    /// A rate limit from Tidal's secondary album lookup escapes both parsing layers so the queue
    /// retries the provider leg instead of persisting a false no-match.
    /// </summary>
    [TestMethod]
    public async Task TidalTrackLookup_AlbumArtRateLimit_PropagatesTypedException( ) {
        const string TokenResponse = """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""";
        const string TrackResponse = """
            {
              "data": {
                "id": "12345",
                "type": "tracks",
                "attributes": {
                  "title": "Track",
                  "isrc": "USAAA0000001",
                  "externalLinks": [{"href":"https://tidal.com/browse/track/12345"}]
                },
                "relationships": {
                  "artists": {"data":[{"id":"artist-1","type":"artists"}]},
                  "albums": {"data":[{"id":"67890","type":"albums"}]}
                }
              },
              "included": [
                {"id":"artist-1","type":"artists","attributes":{"name":"Artist"}},
                {"id":"67890","type":"albums","attributes":{"title":"Album"}}
              ]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );
        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( item => item.CreateClient( "tidal-auth" ) )
            .Returns( CreateClient( TokenResponse, "https://auth.tidal.com/" ) );

        SequencedResponseHandler apiHandler = new(
            new HttpResponseMessage( HttpStatusCode.OK ) {
                Content = new StringContent( TrackResponse, Encoding.UTF8, "application/json" )
            },
            CreateRateLimitedResponse( TimeSpan.FromSeconds( 17 ) )
        );
        _ = factory.Setup( item => item.CreateClient( "tidal-api" ) )
            .Returns( ( ) => {
                RetryAfterLimitHandler rateLimitHandler = new(
                    120,
                    new QueueSettings( ),
                    NullLogger<RetryAfterLimitHandler>.Instance ) {
                    InnerHandler = apiHandler
                };
                return new HttpClient( rateLimitHandler, disposeHandler: false ) {
                    BaseAddress = new Uri( "https://openapi.tidal.com/v2/" )
                };
            } );

        TidalTokenHandler tokenHandler = new(
            new TidalCredentials( "client", "secret" ),
            factory.Object,
            NullLogger<TidalTokenHandler>.Instance );
        TidalLookupService service = new(
            tokenHandler,
            factory.Object,
            NullLogger<TidalLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        ProviderRateLimitException exception = await Assert.ThrowsExactlyAsync<ProviderRateLimitException>(
            ( ) => service.GetInfoByIDAsync( "12345", false )
        );

        Assert.AreEqual( 2, apiHandler.CallCount, "Expected a primary lookup followed by an album-art lookup." );
        Assert.AreEqual( TimeSpan.FromSeconds( 17 ), exception.RetryAfterValue );
        Assert.AreEqual( SupportedProviders.Tidal, exception.Provider );
    }

    /// <summary>
    /// A genre-cache write failure never fails the Apple Music lookup:
    /// <c>ParseAppleMusicSongResponse</c> catches and logs the exception rather than letting it
    /// propagate, so the mapped result is still returned.
    /// </summary>
    [TestMethod]
    public async Task AppleMusicTrackLookup_GenreCacheThrows_StillReturnsResult( ) {
        const string Response = """
            {
              "data": [{
                "id": "123456",
                "type": "songs",
                "attributes": {
                  "artistName": "Artist",
                  "name": "Track",
                  "isrc": "USAAA0000001",
                  "url": "https://music.apple.com/us/song/123456",
                  "genreNames": ["Alternative", "Music"]
                }
              }]
            }
            """;

        Mock<IGenreCacheService> genreCache = new( MockBehavior.Strict );
        _ = genreCache
            .Setup( cache => cache.SetGenresAsync(
                SupportedProviders.AppleMusic,
                "123456",
                It.IsAny<IEnumerable<string>>( ),
                It.IsAny<CancellationToken>( ) ) )
            .ThrowsAsync( new InvalidOperationException( "cache unavailable" ) );

        using ECDsa key = ECDsa.Create( ECCurve.NamedCurves.nistP256 );
        AppleJwtHandler jwtHandler = new( "team", "key", key.ExportPkcs8PrivateKeyPem( ) );
        Mock<IHttpClientFactory> factory = CreateFactory( "musickit-api", Response, "https://api.music.apple.com/v1/catalog/" );
        AppleMusicLookupService service = new(
            jwtHandler,
            factory.Object,
            NullLogger<AppleMusicLookupService>.Instance,
            new JsonSerializerOptions( ),
            genreCache.Object );

        MusicLookupResult? result = await service.GetInfoByIDAsync( "123456", false );

        Assert.IsNotNull( result );
        genreCache.VerifyAll( );
    }

    private static Mock<IHttpClientFactory> CreateFactory( string name, string body, string baseAddress ) {
        Mock<IHttpClientFactory> factory = new( );
        _ = factory.Setup( item => item.CreateClient( name ) )
            .Returns( CreateClient( body, baseAddress ) );
        return factory;
    }

    private static HttpClient CreateClient( string body, string baseAddress )
        => new( new FixedResponseHandler( body ) ) { BaseAddress = new Uri( baseAddress ) };

    private static HttpResponseMessage CreateRateLimitedResponse( TimeSpan retryAfter ) {
        HttpResponseMessage response = new( HttpStatusCode.TooManyRequests );
        response.Headers.RetryAfter = new RetryConditionHeaderValue( retryAfter );
        return response;
    }

    private sealed class FixedResponseHandler( string body ) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult( new HttpResponseMessage( HttpStatusCode.OK ) {
            Content = new StringContent( body, Encoding.UTF8, "application/json" ),
            RequestMessage = request
        } );
    }

    private sealed class SequencedResponseHandler( params HttpResponseMessage[] responses ) : HttpMessageHandler {
        private int _nextResponse;

        public int CallCount => _nextResponse;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) {
            int index = Interlocked.Increment( ref _nextResponse ) - 1;
            HttpResponseMessage response = responses[index];
            response.RequestMessage = request;
            return Task.FromResult( response );
        }
    }
}
