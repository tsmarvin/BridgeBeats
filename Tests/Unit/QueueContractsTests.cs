using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Records;

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for the queue-and-rate-limit contract surface in <c>BridgeBeats.Contracts</c>:
/// the <see cref="QueuePriority"/> enum and the records <see cref="QueuedLookupRequest"/>,
/// <see cref="QueuedMessage{T}"/>, <see cref="QueueDepth"/>, <see cref="DeduplicationResult"/>,
/// <see cref="RateLimitState"/>, <see cref="RateLimitedEndpoint"/>, <see cref="LookupSagaState"/>,
/// <see cref="ProviderLookupState"/>, <see cref="QueueSettings"/>, and <see cref="PriorityWeights"/>.
/// Cover record value-equality, camelCase JSON round-tripping with null-omission for optional
/// fields, default values, and the computed <see cref="LookupSagaState.IsComplete"/> invariant.
/// </summary>
[TestClass]
public class QueueContractsTests {
    /// <summary>Shared camelCase, non-indented serializer options matching the wire contract.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region QueuePriority Enum Tests

    /// <summary>
    /// <see cref="QueuePriority"/> has the expected ordinal values: Interactive=0, Background=1,
    /// Bulk=2 (the lower ordinal is the higher-priority lane).
    /// </summary>
    [TestMethod]
    public void QueuePriority_HasExpectedValues( ) {
        // Assert all expected values exist with correct numeric values
        int interactive = ( int )QueuePriority.Interactive;
        int background = ( int )QueuePriority.Background;
        int bulk = ( int )QueuePriority.Bulk;

        Assert.AreEqual( 0, interactive );
        Assert.AreEqual( 1, background );
        Assert.AreEqual( 2, bulk );
    }

    /// <summary><see cref="QueuePriority"/> defines exactly three lanes.</summary>
    [TestMethod]
    public void QueuePriority_HasExactlyThreeValues( ) {
        // Arrange
        QueuePriority[] values = Enum.GetValues<QueuePriority>( );

        // Assert
        Assert.HasCount( 3, values );
    }

    #endregion

    #region QueuedLookupRequest Tests

    /// <summary>
    /// Two <see cref="QueuedLookupRequest"/> instances with identical field values are equal and
    /// share a hash code (record value-equality).
    /// </summary>
    [TestMethod]
    public void QueuedLookupRequest_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        QueuedLookupRequest request1 = new( ) {
            RequestId = "req-123",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US1234567890",
            SagaId = "saga-456",
            CreatedAt = createdAt
        };
        QueuedLookupRequest request2 = new( ) {
            RequestId = "req-123",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US1234567890",
            SagaId = "saga-456",
            CreatedAt = createdAt
        };

        // Assert
        Assert.AreEqual( request1, request2 );
        Assert.AreEqual( request1.GetHashCode( ), request2.GetHashCode( ) );
    }

    /// <summary>
    /// Two <see cref="QueuedLookupRequest"/> instances differing only by <c>RequestId</c> are not equal.
    /// </summary>
    [TestMethod]
    public void QueuedLookupRequest_Equality_ReturnsFalseForDifferentRecords( ) {
        // Arrange
        QueuedLookupRequest request1 = new( ) {
            RequestId = "req-123",
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US1234567890",
            SagaId = "saga-456"
        };
        QueuedLookupRequest request2 = new( ) {
            RequestId = "req-789",  // Different ID
            Provider = SupportedProviders.Spotify,
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US1234567890",
            SagaId = "saga-456"
        };

        // Assert
        Assert.AreNotEqual( request1, request2 );
    }

    /// <summary>
    /// A fully-populated <see cref="QueuedLookupRequest"/> (including optional fields) survives a
    /// JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void QueuedLookupRequest_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        DateTimeOffset createdAt = DateTimeOffset.Parse( "2026-01-20T12:00:00Z" );
        QueuedLookupRequest original = new( ) {
            RequestId = "req-123",
            Provider = SupportedProviders.AppleMusic,
            LookupType = LookupRequestType.UpcLookup,
            LookupValue = "0123456789012",
            Artist = "Test Artist",
            Title = "Test Title",
            IsAlbum = true,
            SagaId = "saga-789",
            CreatedAt = createdAt,
            AttemptCount = 2,
            RateLimitedEndpoint = "/v1/catalog/us/albums",
            Storefront = "jp",
            FallbackLookupType = LookupRequestType.UpcLookup,
            FallbackLookupValue = "0123456789012"
        };

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        QueuedLookupRequest? deserialized = JsonSerializer.Deserialize<QueuedLookupRequest>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    /// <summary>
    /// Serializing a <see cref="QueuedLookupRequest"/> with unset optional fields omits
    /// <c>artist</c>, <c>title</c>, and <c>rateLimitedEndpoint</c> from the JSON.
    /// </summary>
    [TestMethod]
    public void QueuedLookupRequest_Serialization_OmitsNullOptionalFields( ) {
        // Arrange
        QueuedLookupRequest request = new( ) {
            RequestId = "req-123",
            Provider = SupportedProviders.Tidal,
            LookupType = LookupRequestType.SongIdLookup,
            LookupValue = "12345678",
            SagaId = "saga-456"
        };

        // Act
        string json = JsonSerializer.Serialize( request, s_jsonOptions );

        // Assert - null optional fields should be omitted
        Assert.DoesNotContain( "\"artist\"", json, "JSON should not contain 'artist' field" );
        Assert.DoesNotContain( "\"title\"", json, "JSON should not contain 'title' field" );
        Assert.DoesNotContain( "\"rateLimitedEndpoint\"", json, "JSON should not contain 'rateLimitedEndpoint' field" );
        Assert.DoesNotContain( "\"storefront\"", json, "JSON should not contain 'storefront' field" );
        Assert.DoesNotContain( "\"fallbackLookupType\"", json, "JSON should not contain 'fallbackLookupType' field" );
        Assert.DoesNotContain( "\"fallbackLookupValue\"", json, "JSON should not contain 'fallbackLookupValue' field" );
    }

    /// <summary>A payload written before storefront fallback fields existed remains readable.</summary>
    [TestMethod]
    public void QueuedLookupRequest_Deserialization_LegacyPayloadDefaultsNewFields( ) {
        const string Json = """
            {
              "requestId":"req-legacy",
              "provider":2,
              "lookupType":1,
              "lookupValue":"USRC12345678",
              "sagaId":"saga-legacy"
            }
            """;

        QueuedLookupRequest? request = JsonSerializer.Deserialize<QueuedLookupRequest>( Json, s_jsonOptions );

        Assert.IsNotNull( request );
        Assert.IsNull( request.Storefront );
        Assert.IsNull( request.FallbackLookupType );
        Assert.IsNull( request.FallbackLookupValue );
    }

    #endregion

    #region QueuedMessage<T> Tests

    /// <summary>
    /// Two <see cref="QueuedMessage{T}"/> envelopes with identical message id, payload, and
    /// enqueue time are equal and share a hash code.
    /// </summary>
    [TestMethod]
    public void QueuedMessage_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        DateTimeOffset enqueuedAt = DateTimeOffset.UtcNow;
        QueuedMessage<string> message1 = new( "msg-123", "payload", enqueuedAt );
        QueuedMessage<string> message2 = new( "msg-123", "payload", enqueuedAt );

        // Assert
        Assert.AreEqual( message1, message2 );
        Assert.AreEqual( message1.GetHashCode( ), message2.GetHashCode( ) );
    }

    /// <summary>
    /// A <see cref="QueuedMessage{T}"/> survives a JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void QueuedMessage_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        DateTimeOffset enqueuedAt = DateTimeOffset.Parse( "2026-01-20T15:30:00Z" );
        QueuedMessage<string> original = new( "msg-456", "test-payload", enqueuedAt );

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        QueuedMessage<string>? deserialized = JsonSerializer.Deserialize<QueuedMessage<string>>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    /// <summary>Delivery-lane priority is derived at dequeue time and is not persisted.</summary>
    [TestMethod]
    public void QueuedMessage_Serialization_OmitsDerivedPriority( ) {
        QueuedMessage<string> message = new( "msg-789", "payload", DateTimeOffset.UtcNow ) {
            Priority = QueuePriority.Interactive
        };

        string json = JsonSerializer.Serialize( message, s_jsonOptions );

        Assert.DoesNotContain( "\"priority\"", json );
    }

    #endregion

    #region QueueDepth Tests

    /// <summary>
    /// Two <see cref="QueueDepth"/> snapshots with identical per-lane counts and total are equal
    /// and share a hash code.
    /// </summary>
    [TestMethod]
    public void QueueDepth_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        QueueDepth depth1 = new( 10, 5, 2, 17 );
        QueueDepth depth2 = new( 10, 5, 2, 17 );

        // Assert
        Assert.AreEqual( depth1, depth2 );
        Assert.AreEqual( depth1.GetHashCode( ), depth2.GetHashCode( ) );
    }

    /// <summary>
    /// A <see cref="QueueDepth"/> survives a JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void QueueDepth_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        QueueDepth original = new( 100, 50, 25, 175 );

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        QueueDepth? deserialized = JsonSerializer.Deserialize<QueueDepth>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    #endregion

    #region DeduplicationResult Tests

    /// <summary>
    /// Two <see cref="DeduplicationResult"/> instances with identical acquired/in-flight flags and
    /// request key are equal and share a hash code.
    /// </summary>
    [TestMethod]
    public void DeduplicationResult_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        DeduplicationResult result1 = new( true, false, "isrc:US1234567890" );
        DeduplicationResult result2 = new( true, false, "isrc:US1234567890" );

        // Assert
        Assert.AreEqual( result1, result2 );
        Assert.AreEqual( result1.GetHashCode( ), result2.GetHashCode( ) );
    }

    /// <summary>
    /// A <see cref="DeduplicationResult"/> survives a JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void DeduplicationResult_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        DeduplicationResult original = new( false, true, "url:spotify:track:abc123" );

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        DeduplicationResult? deserialized = JsonSerializer.Deserialize<DeduplicationResult>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    #endregion

    #region RateLimitState Tests

    /// <summary>
    /// Two <see cref="RateLimitState"/> instances with identical limited flag, retry-after instant,
    /// and time-remaining are equal and share a hash code.
    /// </summary>
    [TestMethod]
    public void RateLimitState_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 5 );
        TimeSpan remaining = TimeSpan.FromMinutes( 5 );
        RateLimitState state1 = new( true, retryAfter, remaining );
        RateLimitState state2 = new( true, retryAfter, remaining );

        // Assert
        Assert.AreEqual( state1, state2 );
        Assert.AreEqual( state1.GetHashCode( ), state2.GetHashCode( ) );
    }

    /// <summary>
    /// A rate-limited <see cref="RateLimitState"/> survives a JSON serialize/deserialize round trip
    /// unchanged.
    /// </summary>
    [TestMethod]
    public void RateLimitState_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        RateLimitState original = new( true, DateTimeOffset.Parse( "2026-01-20T16:00:00Z" ), TimeSpan.FromMinutes( 3 ) );

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        RateLimitState? deserialized = JsonSerializer.Deserialize<RateLimitState>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    /// <summary>
    /// Serializing a not-rate-limited <see cref="RateLimitState"/> omits the null <c>retryAfter</c>
    /// and <c>timeRemaining</c> fields from the JSON.
    /// </summary>
    [TestMethod]
    public void RateLimitState_Serialization_OmitsNullOptionalFields( ) {
        // Arrange
        RateLimitState notLimited = new( false, null, null );

        // Act
        string json = JsonSerializer.Serialize( notLimited, s_jsonOptions );

        // Assert
        Assert.DoesNotContain( "\"retryAfter\"", json, "JSON should not contain 'retryAfter' field" );
        Assert.DoesNotContain( "\"timeRemaining\"", json, "JSON should not contain 'timeRemaining' field" );
    }

    #endregion

    #region RateLimitedEndpoint Tests

    /// <summary>
    /// Two <see cref="RateLimitedEndpoint"/> instances with identical endpoint and retry-after
    /// instant are equal and share a hash code.
    /// </summary>
    [TestMethod]
    public void RateLimitedEndpoint_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        DateTimeOffset retryAfter = DateTimeOffset.UtcNow.AddMinutes( 10 );
        RateLimitedEndpoint endpoint1 = new( "/v1/search", retryAfter );
        RateLimitedEndpoint endpoint2 = new( "/v1/search", retryAfter );

        // Assert
        Assert.AreEqual( endpoint1, endpoint2 );
        Assert.AreEqual( endpoint1.GetHashCode( ), endpoint2.GetHashCode( ) );
    }

    /// <summary>
    /// A <see cref="RateLimitedEndpoint"/> survives a JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void RateLimitedEndpoint_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        RateLimitedEndpoint original = new( "/v1/catalog/us/songs", DateTimeOffset.Parse( "2026-01-20T17:00:00Z" ) );

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        RateLimitedEndpoint? deserialized = JsonSerializer.Deserialize<RateLimitedEndpoint>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    #endregion

    #region LookupSagaState Tests

    /// <summary>
    /// A <see cref="LookupSagaState"/> and its <c>with</c>-expression clone are equal, share a hash
    /// code, and carry the same key value properties.
    /// </summary>
    [TestMethod]
    public void LookupSagaState_Equality_ComparesValueProperties( ) {
        // Arrange - Note: LookupSagaState contains a Dictionary which uses reference equality,
        // so two instances with the same values in different Dictionary instances are not equal.
        // This test verifies the value properties are correctly compared.
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        LookupSagaState saga1 = new( ) {
            SagaId = "saga-123",
            LookupKey = "isrc:US1234567890",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "US1234567890",
            CreatedAt = createdAt,
            PartialResultUri = "at://test",
            FinalResultUri = "at://final"
        };
        LookupSagaState saga2 = saga1 with { }; // Create a copy using with expression

        // Assert - with expression creates a shallow copy, so ProviderStates reference is shared
        Assert.AreEqual( saga1, saga2 );
        Assert.AreEqual( saga1.GetHashCode( ), saga2.GetHashCode( ) );

        // Verify value properties
        Assert.AreEqual( saga1.SagaId, saga2.SagaId );
        Assert.AreEqual( saga1.LookupKey, saga2.LookupKey );
        Assert.AreEqual( saga1.LookupType, saga2.LookupType );
        Assert.AreEqual( saga1.LookupValue, saga2.LookupValue );
    }

    /// <summary>
    /// A <see cref="LookupSagaState"/> with per-provider <see cref="ProviderLookupState"/> entries
    /// survives a JSON serialize/deserialize round trip, preserving its key fields and provider map.
    /// </summary>
    [TestMethod]
    public void LookupSagaState_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        DateTimeOffset createdAt = DateTimeOffset.Parse( "2026-01-20T12:00:00Z" );
        LookupSagaState original = new( ) {
            SagaId = "saga-456",
            LookupKey = "upc:0123456789012",
            LookupType = LookupRequestType.UpcLookup,
            LookupValue = "0123456789012",
            CreatedAt = createdAt,
            PartialResultUri = "at://did:plc:abc/app.bridgebeats.lookup/xyz",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null ),
                [SupportedProviders.AppleMusic] = new( SupportedProviders.AppleMusic, false, false, null, null, null )
            }
        };

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        LookupSagaState? deserialized = JsonSerializer.Deserialize<LookupSagaState>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original.SagaId, deserialized.SagaId );
        Assert.AreEqual( original.LookupKey, deserialized.LookupKey );
        Assert.AreEqual( original.LookupType, deserialized.LookupType );
        Assert.AreEqual( original.LookupValue, deserialized.LookupValue );
        Assert.AreEqual( original.PartialResultUri, deserialized.PartialResultUri );
        Assert.HasCount( 2, deserialized.ProviderStates );
    }

    /// <summary>
    /// <see cref="LookupSagaState.IsComplete"/> is <c>false</c> when no provider states are
    /// registered (the empty-dictionary edge is deliberately incomplete).
    /// </summary>
    [TestMethod]
    public void LookupSagaState_IsComplete_ReturnsFalseWhenEmpty( ) {
        // Arrange
        LookupSagaState saga = new( ) {
            SagaId = "saga-123",
            LookupKey = "isrc:test",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "test"
        };

        // Assert
        Assert.IsFalse( saga.IsComplete );
    }

    /// <summary>
    /// <see cref="LookupSagaState.IsComplete"/> is <c>false</c> while any registered provider state
    /// is still incomplete.
    /// </summary>
    [TestMethod]
    public void LookupSagaState_IsComplete_ReturnsFalseWhenAnyProviderIncomplete( ) {
        // Arrange
        LookupSagaState saga = new( ) {
            SagaId = "saga-123",
            LookupKey = "isrc:test",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "test",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null ),
                [SupportedProviders.AppleMusic] = new( SupportedProviders.AppleMusic, false, false, null, null, null )
            }
        };

        // Assert
        Assert.IsFalse( saga.IsComplete );
    }

    /// <summary>
    /// <see cref="LookupSagaState.IsComplete"/> is <c>true</c> once every registered provider state
    /// reports complete.
    /// </summary>
    [TestMethod]
    public void LookupSagaState_IsComplete_ReturnsTrueWhenAllProvidersComplete( ) {
        // Arrange
        LookupSagaState saga = new( ) {
            SagaId = "saga-123",
            LookupKey = "isrc:test",
            LookupType = LookupRequestType.IsrcLookup,
            LookupValue = "test",
            ProviderStates = new Dictionary<SupportedProviders, ProviderLookupState> {
                [SupportedProviders.Spotify] = new( SupportedProviders.Spotify, true, true, "{}", DateTimeOffset.UtcNow, null ),
                [SupportedProviders.AppleMusic] = new( SupportedProviders.AppleMusic, true, true, "{}", DateTimeOffset.UtcNow, null )
            }
        };

        // Assert
        Assert.IsTrue( saga.IsComplete );
    }

    #endregion

    #region ProviderLookupState Tests

    /// <summary>
    /// Two <see cref="ProviderLookupState"/> instances with identical field values are equal and
    /// share a hash code.
    /// </summary>
    [TestMethod]
    public void ProviderLookupState_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        ProviderLookupState state1 = new( SupportedProviders.Tidal, true, true, "{\"id\": 123}", completedAt, null );
        ProviderLookupState state2 = new( SupportedProviders.Tidal, true, true, "{\"id\": 123}", completedAt, null );

        // Assert
        Assert.AreEqual( state1, state2 );
        Assert.AreEqual( state1.GetHashCode( ), state2.GetHashCode( ) );
    }

    /// <summary>
    /// A complete-but-failed <see cref="ProviderLookupState"/> (no result, with an error message)
    /// survives a JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void ProviderLookupState_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        ProviderLookupState original = new(
            SupportedProviders.Spotify,
            true,
            false,
            null,
            DateTimeOffset.Parse( "2026-01-20T14:00:00Z" ),
            "Rate limited"
        );

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        ProviderLookupState? deserialized = JsonSerializer.Deserialize<ProviderLookupState>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    #endregion

    #region QueueSettings Tests

    /// <summary>
    /// A default <see cref="QueueSettings"/> carries the expected defaults: a 2-minute
    /// rate-limit retry threshold, a 2880-minute (48-hour) job expiration, and a non-null
    /// <see cref="PriorityWeights"/>.
    /// </summary>
    [TestMethod]
    public void QueueSettings_HasExpectedDefaults( ) {
        // Arrange
        QueueSettings settings = new( );

        // Assert
        Assert.AreEqual( TimeSpan.FromMinutes( 2 ), settings.RateLimitRetryThreshold );
        Assert.AreEqual( 2880, settings.JobExpirationMinutes );
        Assert.IsNotNull( settings.Weights );
    }

    /// <summary>
    /// A customized <see cref="QueueSettings"/> (including its nested <see cref="PriorityWeights"/>)
    /// survives a JSON serialize/deserialize round trip, preserving threshold, expiration, and weights.
    /// </summary>
    [TestMethod]
    public void QueueSettings_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        QueueSettings original = new( ) {
            RateLimitRetryThreshold = TimeSpan.FromMinutes( 5 ),
            JobExpirationMinutes = 120,
            Weights = new PriorityWeights {
                Interactive = 10,
                Background = 5,
                Bulk = 1
            }
        };

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        QueueSettings? deserialized = JsonSerializer.Deserialize<QueueSettings>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original.RateLimitRetryThreshold, deserialized.RateLimitRetryThreshold );
        Assert.AreEqual( original.JobExpirationMinutes, deserialized.JobExpirationMinutes );
        Assert.AreEqual( original.Weights.Interactive, deserialized.Weights.Interactive );
        Assert.AreEqual( original.Weights.Background, deserialized.Weights.Background );
        Assert.AreEqual( original.Weights.Bulk, deserialized.Weights.Bulk );
    }

    #endregion

    #region PriorityWeights Tests

    /// <summary>
    /// A default <see cref="PriorityWeights"/> carries the expected default values:
    /// Interactive=5, Background=2, Bulk=1. (These are legacy weights retained for configuration
    /// backwards compatibility; they no longer drive queue ordering.)
    /// </summary>
    [TestMethod]
    public void PriorityWeights_HasExpectedDefaults( ) {
        // Arrange
        PriorityWeights weights = new( );

        // Assert
        Assert.AreEqual( 5, weights.Interactive );
        Assert.AreEqual( 2, weights.Background );
        Assert.AreEqual( 1, weights.Bulk );
    }

    /// <summary>
    /// Two <see cref="PriorityWeights"/> with identical weights are equal and share a hash code.
    /// </summary>
    [TestMethod]
    public void PriorityWeights_Equality_ReturnsTrueForIdenticalRecords( ) {
        // Arrange
        PriorityWeights weights1 = new( ) { Interactive = 10, Background = 5, Bulk = 2 };
        PriorityWeights weights2 = new( ) { Interactive = 10, Background = 5, Bulk = 2 };

        // Assert
        Assert.AreEqual( weights1, weights2 );
        Assert.AreEqual( weights1.GetHashCode( ), weights2.GetHashCode( ) );
    }

    /// <summary>
    /// A <see cref="PriorityWeights"/> survives a JSON serialize/deserialize round trip unchanged.
    /// </summary>
    [TestMethod]
    public void PriorityWeights_Serialization_RoundTripsCorrectly( ) {
        // Arrange
        PriorityWeights original = new( ) { Interactive = 8, Background = 4, Bulk = 1 };

        // Act
        string json = JsonSerializer.Serialize( original, s_jsonOptions );
        PriorityWeights? deserialized = JsonSerializer.Deserialize<PriorityWeights>( json, s_jsonOptions );

        // Assert
        Assert.IsNotNull( deserialized );
        Assert.AreEqual( original, deserialized );
    }

    #endregion
}
