using System.Text.Json;
using BridgeBeats.Contracts.DTOs;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Domain.Services.Queue;
using Microsoft.Extensions.Logging;
using Moq;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SagaResultCombiner"/>, which merges per-provider lookup results into a
/// single combined result. Covers both entry points: the saga-state overload that reads provider
/// states off a <c>LookupSagaState</c>, and the enumerable overload that takes provider/result
/// pairs directly. Encodes the combiner's invariants: a null result when no provider succeeded,
/// successful results only (failed, incomplete, null-, empty-, or invalid-JSON providers are
/// skipped), and exactly one result flagged as primary (the first successful provider).
/// </summary>
[TestClass]
public class SagaResultCombinerTests {
    /// <summary>Mock logger passed to the combiner; also used to confirm warnings on invalid JSON.</summary>
    private Mock<ILogger<SagaResultCombiner>> _loggerMock = null!;
    /// <summary>The combiner under test, reconstructed before each test.</summary>
    private SagaResultCombiner _combiner = null!;

    /// <summary>
    /// Serialization options (camel-case, non-indented) used by the helpers to build the
    /// <c>ResultJson</c> payloads the combiner deserializes, matching the production format.
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Creates a fresh logger mock and combiner before each test.
    /// </summary>
    [TestInitialize]
    public void Initialize( ) {
        _loggerMock = new Mock<ILogger<SagaResultCombiner>>( );
        _combiner = new SagaResultCombiner( _loggerMock.Object );
    }

    #region Constructor Tests

    /// <summary>
    /// Verifies that a non-null logger produces a usable combiner instance.
    /// </summary>
    [TestMethod]
    public void Constructor_WithValidLogger_ShouldCreateInstance( ) {
        // Act
        SagaResultCombiner combiner = new( _loggerMock.Object );

        // Assert
        Assert.IsNotNull( combiner );
    }

    /// <summary>
    /// Verifies that a null logger throws <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullLogger_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) => new SagaResultCombiner( null! ) );
    }

    #endregion

    #region CombineResults(LookupSagaState) Tests

    /// <summary>
    /// Verifies that the saga-state overload throws <see cref="ArgumentNullException"/> when passed
    /// a null saga.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithNullSaga_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>( ( ) => _combiner.CombineResults( null! ) );
    }

    /// <summary>
    /// Verifies that a saga with no provider states combines to null (nothing to merge).
    /// </summary>
    [TestMethod]
    public void CombineResults_WithNoProviderStates_ShouldReturnNull( ) {
        // Arrange
        LookupSagaState saga = CreateSaga( );

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNull( result );
    }

    /// <summary>
    /// Verifies that a saga whose providers all failed combines to null (no successful result to
    /// surface).
    /// </summary>
    [TestMethod]
    public void CombineResults_WithAllFailedProviders_ShouldReturnNull( ) {
        // Arrange
        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, isSuccess: false ),
                [SupportedProviders.AppleMusic] = CreateProviderState( SupportedProviders.AppleMusic, isSuccess: false )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNull( result );
    }

    /// <summary>
    /// Verifies that a saga with a single successful provider combines to a result containing that
    /// one provider, with the artist and title carried through from its <c>ResultJson</c>.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithSingleSuccessfulProvider_ShouldReturnResult( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-track-1", "Test Artist", "Test Song" );
        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, spotifyResult )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNotNull( result );
        Assert.HasCount( 1, result.Results );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.Spotify ) );
        Assert.AreEqual( "Test Artist", result.Results[SupportedProviders.Spotify].Artist );
        Assert.AreEqual( "Test Song", result.Results[SupportedProviders.Spotify].Title );
    }

    /// <summary>
    /// Verifies that a saga with three successful providers combines to a result containing all
    /// three keyed entries.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithMultipleSuccessfulProviders_ShouldReturnAllResults( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-track-1", "Artist", "Song" );
        MusicLookupResult appleResult = CreateLookupResult( "apple-track-1", "Artist", "Song" );
        MusicLookupResult tidalResult = CreateLookupResult( "tidal-track-1", "Artist", "Song" );

        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, spotifyResult ),
                [SupportedProviders.AppleMusic] = CreateProviderState( SupportedProviders.AppleMusic, appleResult ),
                [SupportedProviders.Tidal] = CreateProviderState( SupportedProviders.Tidal, tidalResult )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNotNull( result );
        Assert.HasCount( 3, result.Results );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.Spotify ) );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.AppleMusic ) );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.Tidal ) );
    }

    /// <summary>
    /// Verifies that exactly one provider in the combined result is flagged primary, enforcing the
    /// single-primary invariant across multiple successful providers.
    /// </summary>
    [TestMethod]
    public void CombineResults_FirstProviderResult_ShouldBeMarkedAsPrimary( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-track-1", "Artist", "Song" );
        MusicLookupResult appleResult = CreateLookupResult( "apple-track-1", "Artist", "Song" );

        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, spotifyResult ),
                [SupportedProviders.AppleMusic] = CreateProviderState( SupportedProviders.AppleMusic, appleResult )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNotNull( result );
        // First result should be primary, others should not
        int primaryCount = result.Results.Values.Count( r => r.IsPrimary );
        Assert.AreEqual( 1, primaryCount );
    }

    /// <summary>
    /// Verifies that when a saga mixes one successful and two failed providers, only the successful
    /// provider appears in the combined result.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithMixedSuccessAndFailure_ShouldReturnOnlySuccessfulResults( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-track-1", "Artist", "Song" );

        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, spotifyResult ),
                [SupportedProviders.AppleMusic] = CreateProviderState( SupportedProviders.AppleMusic, isSuccess: false ),
                [SupportedProviders.Tidal] = CreateProviderState( SupportedProviders.Tidal, isSuccess: false )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNotNull( result );
        Assert.HasCount( 1, result.Results );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.Spotify ) );
    }

    /// <summary>
    /// Verifies that an incomplete saga (one provider still in flight) still combines the providers
    /// that have finished, yielding the single available result rather than waiting or failing.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithIncompleteSaga_ShouldStillCombineAvailableResults( ) {
        // Arrange - Saga where not all providers have completed
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-track-1", "Artist", "Song" );

        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, spotifyResult ),
                [SupportedProviders.AppleMusic] = new ProviderLookupState(
                    SupportedProviders.AppleMusic,
                    IsComplete: false, // Not complete yet
                    IsSuccess: false,
                    ResultJson: null,
                    CompletedAt: null,
                    ErrorMessage: null
                )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert - Should still return available results and log warning
        Assert.IsNotNull( result );
        Assert.HasCount( 1, result.Results );
    }

    /// <summary>
    /// Verifies that a provider marked successful but carrying a null <c>ResultJson</c> is skipped;
    /// with no other providers, the combined result is null.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithNullResultJson_ShouldSkipProvider( ) {
        // Arrange
        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new ProviderLookupState(
                    SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true, // Success but null JSON (shouldn't happen normally)
                    ResultJson: null,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNull( result );
    }

    /// <summary>
    /// Verifies that a provider marked successful but carrying an empty <c>ResultJson</c> string is
    /// skipped; with no other providers, the combined result is null.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithEmptyResultJson_ShouldSkipProvider( ) {
        // Arrange
        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new ProviderLookupState(
                    SupportedProviders.Spotify,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: string.Empty,
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert
        Assert.IsNull( result );
    }

    /// <summary>A nominal success with no identifier or metadata cannot be persisted and is skipped.</summary>
    [TestMethod]
    public void CombineResults_WithEmptyProviderPayload_ShouldReturnNull( ) {
        _ = _loggerMock.Setup( logger => logger.IsEnabled( LogLevel.Warning ) ).Returns( true );
        MusicLookupResult emptyResult = new( ) {
            URL = "https://provider.example/item/empty"
        };
        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Tidal] = CreateProviderState( SupportedProviders.Tidal, emptyResult )
            }
        };

        MediaLinkResult? result = _combiner.CombineResults( saga );

        Assert.IsNull( result );
        _loggerMock.Verify(
            logger => logger.Log(
                LogLevel.Warning,
                It.Is<EventId>( eventId => eventId.Id == 3104 ),
                It.IsAny<It.IsAnyType>( ),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
            Times.Once );
    }

    /// <summary>An external id removed entirely by rkey sanitization is not persistable by itself.</summary>
    [TestMethod]
    public void CombineResults_WithUnsanitizableExternalIdOnly_ShouldReturnNull( ) {
        MusicLookupResult invalidResult = new( ) {
            ExternalId = "@@@",
            URL = "https://provider.example/item/invalid"
        };
        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Tidal] = CreateProviderState( SupportedProviders.Tidal, invalidResult )
            }
        };

        MediaLinkResult? result = _combiner.CombineResults( saga );

        Assert.IsNull( result );
    }

    /// <summary>
    /// Verifies that a provider whose <c>ResultJson</c> is malformed is skipped (and logged as a
    /// warning) while a sibling provider with valid JSON still combines: the result contains the
    /// valid provider and excludes the malformed one.
    /// </summary>
    [TestMethod]
    public void CombineResults_WithInvalidJson_ShouldSkipProviderAndLogWarning( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-track-1", "Artist", "Song" );

        LookupSagaState saga = CreateSaga( ) with {
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = CreateProviderState( SupportedProviders.Spotify, spotifyResult ),
                [SupportedProviders.AppleMusic] = new ProviderLookupState(
                    SupportedProviders.AppleMusic,
                    IsComplete: true,
                    IsSuccess: true,
                    ResultJson: "{ invalid json }}}",
                    CompletedAt: DateTimeOffset.UtcNow,
                    ErrorMessage: null
                )
            }
        };

        // Act
        MediaLinkResult? result = _combiner.CombineResults( saga );

        // Assert - Should return Spotify result and skip Apple due to invalid JSON
        Assert.IsNotNull( result );
        Assert.HasCount( 1, result.Results );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.Spotify ) );
        Assert.IsFalse( result.Results.ContainsKey( SupportedProviders.AppleMusic ) );
    }

    #endregion

    #region CombineResults(IEnumerable) Tests

    /// <summary>
    /// Verifies that the enumerable overload throws <see cref="ArgumentNullException"/> when the
    /// provider/result sequence is null.
    /// </summary>
    [TestMethod]
    public void CombineResults_EnumerableOverload_WithNullResults_ShouldThrowArgumentNullException( ) {
        // Act & Assert
        _ = Assert.ThrowsExactly<ArgumentNullException>(
            ( ) => _combiner.CombineResults( (IEnumerable<(SupportedProviders, MusicLookupResult)>)null!, "test" )
        );
    }

    /// <summary>
    /// Verifies that the enumerable overload returns null when given an empty sequence.
    /// </summary>
    [TestMethod]
    public void CombineResults_EnumerableOverload_WithEmptyResults_ShouldReturnNull( ) {
        // Arrange
        List<(SupportedProviders, MusicLookupResult)> results = [];

        // Act
        MediaLinkResult? result = _combiner.CombineResults( results, "test-lookup-value" );

        // Assert
        Assert.IsNull( result );
    }

    /// <summary>
    /// Verifies that the enumerable overload returns a result containing the single provider when
    /// given one provider/result pair.
    /// </summary>
    [TestMethod]
    public void CombineResults_EnumerableOverload_WithSingleResult_ShouldReturnResult( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-id", "Artist", "Song" );
        List<(SupportedProviders, MusicLookupResult)> results = [
            (SupportedProviders.Spotify, spotifyResult)
        ];

        // Act
        MediaLinkResult? result = _combiner.CombineResults( results, "isrc:US1234567890" );

        // Assert
        Assert.IsNotNull( result );
        Assert.HasCount( 1, result.Results );
        Assert.IsTrue( result.Results.ContainsKey( SupportedProviders.Spotify ) );
    }

    /// <summary>
    /// Verifies that the enumerable overload returns all provided results when given multiple
    /// provider/result pairs.
    /// </summary>
    [TestMethod]
    public void CombineResults_EnumerableOverload_WithMultipleResults_ShouldReturnAllResults( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-id", "Artist", "Song" );
        MusicLookupResult appleResult = CreateLookupResult( "apple-id", "Artist", "Song" );
        List<(SupportedProviders, MusicLookupResult)> results = [
            (SupportedProviders.Spotify, spotifyResult),
            (SupportedProviders.AppleMusic, appleResult)
        ];

        // Act
        MediaLinkResult? result = _combiner.CombineResults( results, "isrc:US1234567890" );

        // Assert
        Assert.IsNotNull( result );
        Assert.HasCount( 2, result.Results );
    }

    /// <summary>
    /// Verifies that the enumerable overload flags exactly one result as primary (the first in the
    /// sequence), matching the single-primary invariant of the saga-state overload.
    /// </summary>
    [TestMethod]
    public void CombineResults_EnumerableOverload_FirstResult_ShouldBeMarkedAsPrimary( ) {
        // Arrange
        MusicLookupResult spotifyResult = CreateLookupResult( "spotify-id", "Artist", "Song" );
        MusicLookupResult appleResult = CreateLookupResult( "apple-id", "Artist", "Song" );
        List<(SupportedProviders, MusicLookupResult)> results = [
            (SupportedProviders.Spotify, spotifyResult),
            (SupportedProviders.AppleMusic, appleResult)
        ];

        // Act
        MediaLinkResult? result = _combiner.CombineResults( results, "isrc:US1234567890" );

        // Assert
        Assert.IsNotNull( result );
        int primaryCount = result.Results.Values.Count( r => r.IsPrimary );
        Assert.AreEqual( 1, primaryCount );
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Builds a minimal ISRC-lookup <c>LookupSagaState</c> with no provider states, for tests that
    /// then attach specific provider states via a <c>with</c> expression.
    /// </summary>
    /// <param name="sagaId">Optional saga id; defaults to a fixed test id when null.</param>
    /// <returns>A bare saga state ready to receive provider states.</returns>
    private static LookupSagaState CreateSaga( string? sagaId = null ) => new( ) {
        SagaId = sagaId ?? "test-saga-123",
        LookupKey = "isrc:US1234567890",
        LookupType = LookupRequestType.IsrcLookup,
        LookupValue = "US1234567890",
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Builds a fully populated <c>MusicLookupResult</c> from the supplied identifiers, used as the
    /// per-provider payload that gets serialized into a provider state's <c>ResultJson</c>.
    /// </summary>
    /// <param name="externalId">The provider's external identifier for the track.</param>
    /// <param name="artist">The artist name.</param>
    /// <param name="title">The track title.</param>
    /// <returns>A lookup result with deterministic URL and art fields derived from the external id.</returns>
    private static MusicLookupResult CreateLookupResult( string externalId, string artist, string title ) => new( ) {
        ExternalId = externalId,
        Artist = artist,
        Title = title,
        URL = $"https://example.com/{externalId}",
        ArtUrl = "https://example.com/art.jpg",
        IsAlbum = false,
        MarketRegion = "us"
    };

    /// <summary>
    /// Builds a complete, successful provider state whose <c>ResultJson</c> is the serialized form
    /// of <paramref name="result"/>, for tests exercising the happy combine path.
    /// </summary>
    /// <param name="provider">The provider this state belongs to.</param>
    /// <param name="result">The lookup result to serialize into the state.</param>
    /// <returns>A complete, successful provider state carrying the serialized result.</returns>
    private static ProviderLookupState CreateProviderState( SupportedProviders provider, MusicLookupResult result ) =>
        new(
            provider,
            IsComplete: true,
            IsSuccess: true,
            ResultJson: JsonSerializer.Serialize( result, s_jsonOptions ),
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: null
        );

    /// <summary>
    /// Builds a complete provider state with the given success flag and no result JSON, for tests
    /// exercising failed or otherwise result-less providers.
    /// </summary>
    /// <param name="provider">The provider this state belongs to.</param>
    /// <param name="isSuccess">Whether the provider succeeded; false attaches a failure message.</param>
    /// <returns>A complete provider state with a null <c>ResultJson</c>.</returns>
    private static ProviderLookupState CreateProviderState( SupportedProviders provider, bool isSuccess ) =>
        new(
            provider,
            IsComplete: true,
            IsSuccess: isSuccess,
            ResultJson: null,
            CompletedAt: DateTimeOffset.UtcNow,
            ErrorMessage: isSuccess ? null : "Lookup failed"
        );

    #endregion
}
