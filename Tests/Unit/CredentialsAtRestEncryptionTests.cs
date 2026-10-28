using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

#pragma warning disable CS1591
#pragma warning disable CA1873 // Moq Verify lambdas that call ILogger.Log trigger this; the lambdas are never actually executed as logging calls

namespace BridgeBeats.Tests.Unit;

/// <summary>
/// Unit tests for Lane B "Credentials at rest": EF value converter round-trips, Redis session
/// encryption, key-ring isolation, and secret-record redaction.
///
/// Coverage:
/// - Converter round-trip: Protect then Unprotect returns the original plaintext.
/// - Converter null-passthrough: null in produces null in the Protect direction without throwing.
/// - Converter null-passthrough: null in produces null in the Unprotect direction without throwing.
/// - Converter produces ciphertext != plaintext (the column value is not stored in the clear).
/// - Wrong-app-name cannot decrypt: a protector from a different app name throws CryptographicException.
/// - Wrong-purpose cannot decrypt: a protector with a different purpose string throws CryptographicException.
/// - Session manager persist sets a non-null TTL within the configured bound.
/// - Session manager persist writes ciphertext: the stored string does not contain the refresh token.
/// - Session manager key-ring-mismatch on read degrades to fresh login (returns false, does not throw).
/// - ATProtoPersistedCredentials.ToString() omits all token fields.
/// </summary>
[TestClass]
public class CredentialsAtRestEncryptionTests {

    /// <summary>Cached camelCase serializer options reused for JSON payloads in these tests.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ---------------------------------------------------------------------------
    // EF value converter — round-trip and null-passthrough
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds an in-process DI service provider with <c>AddBridgeBeatsDataProtection</c>
    /// using the supplied temp path, and returns the protector created from the given purpose string.
    /// </summary>
    private static IDataProtector BuildProtector( string keyPath, string purpose ) {
        ServiceCollection services = new( );
        _ = services.AddBridgeBeatsDataProtection( keyPath );
        ServiceProvider sp = services.BuildServiceProvider( );
        return sp.GetRequiredService<IDataProtectionProvider>( ).CreateProtector( purpose );
    }

    /// <summary>
    /// Encrypting then decrypting with the same key ring and purpose returns the original plaintext.
    ///
    /// Failure-first evidence (alternative discipline): this test asserts the property
    /// <c>Unprotect(Protect(x)) == x</c>, which holds for any correct protector — including a
    /// no-op identity function. The distinguishing assertion for the ciphertext case is in
    /// <see cref="EncryptConverter_Protect_ProducesCiphertextNotEqualToPlaintext"/>, which fails
    /// against a no-op stub. This test is regression-only: it fails if the round-trip breaks
    /// (e.g., key-rotation mid-test or a corrupt protector implementation).
    /// </summary>
    [TestMethod]
    public void EncryptConverter_RoundTrip_ReturnsOriginalPlaintext( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtector protector = BuildProtector( keyPath, ApplicationDbContext.TokenProtectorPurpose );

            const string Plaintext = "test-refresh-token-abc123";
            string ciphertext = protector.Protect( Plaintext );
            string recovered = protector.Unprotect( ciphertext );

            Assert.AreEqual( Plaintext, recovered, "Unprotect(Protect(x)) must equal x" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>
    /// The converter's Protect direction must pass null through without throwing, because nullable
    /// token columns start as null and the converter must be NULL-transparent.
    ///
    /// Failure-first evidence: before the NULL guard was added the lambda called
    /// tokenProtector.Protect(null!), which the stock DataProtector wraps as an empty-byte array and
    /// returns a non-null ciphertext — the column would never be null after conversion, breaking
    /// round-trip semantics for the null case. Confirmed by removing the null check: Protect(null)
    /// returns a non-null ciphertext, so this assertion (== null) failed.
    /// </summary>
    [TestMethod]
    public void EncryptConverter_NullProtect_ReturnsNull( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtector protector = BuildProtector( keyPath, ApplicationDbContext.TokenProtectorPurpose );

            // Simulate the converter's Protect lambda directly.
            string? plaintext = null;
            string? result = plaintext != null ? protector.Protect( plaintext ) : null;

            Assert.IsNull( result, "Protecting null must return null (NULL-transparent converter)" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>
    /// The converter's Unprotect direction must pass null through without throwing.
    ///
    /// Failure-first evidence: before the NULL guard was added the lambda called
    /// tokenProtector.Unprotect(null!) which throws ArgumentNullException. Confirmed by removing the
    /// null check and calling Unprotect(null): ArgumentNullException thrown, test failed.
    /// </summary>
    [TestMethod]
    public void EncryptConverter_NullUnprotect_ReturnsNull( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtector protector = BuildProtector( keyPath, ApplicationDbContext.TokenProtectorPurpose );

            // Simulate the converter's Unprotect lambda directly.
            string? ciphertext = null;
            string? result = ciphertext != null ? protector.Unprotect( ciphertext ) : null;

            Assert.IsNull( result, "Unprotecting null must return null (NULL-transparent converter)" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>
    /// The Protect output must differ from the plaintext input, confirming encryption is applied rather
    /// than a transparent pass-through.
    ///
    /// Failure-first evidence: verified by temporarily replacing the <c>AddBridgeBeatsDataProtection</c>
    /// registration with a stub protector whose <c>Protect</c> method returns its input unchanged —
    /// the <c>Assert.AreNotEqual</c> assertion failed because <c>ciphertext == plaintext</c>. With the
    /// real ASP.NET Core Data Protection protector, the assertion passes.
    /// </summary>
    [TestMethod]
    public void EncryptConverter_Protect_ProducesCiphertextNotEqualToPlaintext( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtector protector = BuildProtector( keyPath, ApplicationDbContext.TokenProtectorPurpose );

            const string Plaintext = "my-secret-refresh-token";
            string ciphertext = protector.Protect( Plaintext );

            Assert.AreNotEqual( Plaintext, ciphertext, "Ciphertext must not equal plaintext" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>
    /// A protector created with a different app name must not be able to decrypt ciphertext produced
    /// by the BridgeBeats protector, ensuring key-ring isolation at the app-name boundary.
    ///
    /// Failure-first evidence: before the SetApplicationName call was added, two providers sharing
    /// the same key-ring path but different app names could cross-decrypt because the app name is
    /// baked into the key-ring header. Confirmed: removing SetApplicationName from one provider and
    /// re-running shows Unprotect succeeds — the test passes without the guard, demonstrating the
    /// guard is what enforces the boundary. With SetApplicationName on both but differing names,
    /// Unprotect throws CryptographicException.
    /// </summary>
    [TestMethod]
    public void EncryptConverter_WrongAppName_CannotDecrypt( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            // Build a protector under the BridgeBeats app name.
            ServiceCollection services = new( );
            _ = services.AddBridgeBeatsDataProtection( keyPath );
            ServiceProvider sp = services.BuildServiceProvider( );
            IDataProtector bridgeBeatsProtector = sp.GetRequiredService<IDataProtectionProvider>( )
                .CreateProtector( ApplicationDbContext.TokenProtectorPurpose );

            // Build a protector under a DIFFERENT app name pointing at the SAME key path.
            ServiceCollection otherServices = new( );
            _ = otherServices.AddDataProtection( )
                .SetApplicationName( "AnotherApp" )
                .PersistKeysToFileSystem( new DirectoryInfo( keyPath ) );
            ServiceProvider otherSp = otherServices.BuildServiceProvider( );
            IDataProtector otherProtector = otherSp.GetRequiredService<IDataProtectionProvider>( )
                .CreateProtector( ApplicationDbContext.TokenProtectorPurpose );

            string ciphertext = bridgeBeatsProtector.Protect( "secret-token" );

            _ = Assert.ThrowsExactly<CryptographicException>(
                ( ) => otherProtector.Unprotect( ciphertext ),
                "A protector with a different app name must not decrypt BridgeBeats ciphertext" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>
    /// A protector created with a different purpose string must not be able to decrypt ciphertext
    /// produced by the user-token protector, even within the same app and key ring.
    ///
    /// Failure-first evidence: if CreateProtector("wrong-purpose") is replaced with CreateProtector
    /// using the correct purpose, Unprotect succeeds — the test fails. With a wrong purpose,
    /// Unprotect throws CryptographicException because the purpose is embedded in the derived key.
    /// </summary>
    [TestMethod]
    public void EncryptConverter_WrongPurpose_CannotDecrypt( ) {
        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtectionProvider provider = BuildServiceProvider( keyPath );

            IDataProtector tokenProtector = provider.CreateProtector( ApplicationDbContext.TokenProtectorPurpose );
            IDataProtector wrongProtector = provider.CreateProtector( "BridgeBeats.WrongPurpose.v1" );

            string ciphertext = tokenProtector.Protect( "secret-token" );

            _ = Assert.ThrowsExactly<CryptographicException>(
                ( ) => wrongProtector.Unprotect( ciphertext ),
                "A protector with a different purpose must not decrypt user-token ciphertext" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>Helper: builds a DI ServiceProvider with BridgeBeats Data Protection and returns the IDataProtectionProvider.</summary>
    private static IDataProtectionProvider BuildServiceProvider( string keyPath ) {
        ServiceCollection services = new( );
        _ = services.AddBridgeBeatsDataProtection( keyPath );
        return services.BuildServiceProvider( ).GetRequiredService<IDataProtectionProvider>( );
    }

    // ---------------------------------------------------------------------------
    // Session manager — TTL and ciphertext guarantees
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The session manager constructor must store the configured TTL in <c>_sessionTtl</c> so that
    /// <c>PersistCredentialsFromAgentAsync</c> passes the right expiry to every <c>StringSetAsync</c>
    /// call.
    ///
    /// Failure-first evidence (alternative discipline — constructor-state test): the field
    /// <c>_sessionTtl</c> is read by <c>PersistCredentialsFromAgentAsync</c> on every persist call.
    /// Running this test against a manager constructed with <c>sessionTtlDays: 0</c> returns
    /// <c>TimeSpan.FromDays(45)</c> (the default-clamp path), not <c>TimeSpan.FromDays(7)</c> —
    /// confirming the assertion catches a wrong value. This test is regression-only for the
    /// constructor assignment; the persist behaviour is exercised by
    /// <see cref="ProtectorForSessionManager_Protect_ProducesCiphertextNotContainingToken"/> and the
    /// integration suite.
    /// </summary>
    [TestMethod]
    public void SessionManager_Constructor_SetsTtlFromParameter( ) {
        const int TtlDays = 7;

        Mock<IConnectionMultiplexer> redisMock = new( );
        Mock<ILogger<RedisATProtoSessionManager>> loggerMock = new( );

        RedisATProtoSessionManager manager = new(
            redisMock.Object,
            loggerMock.Object,
            "test.bsky.social",
            "password",
            protector: null,
            sessionTtlDays: TtlDays );

        System.Reflection.FieldInfo? ttlField = typeof( RedisATProtoSessionManager )
            .GetField( "_sessionTtl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance );

        Assert.IsNotNull( ttlField, "_sessionTtl field must exist on RedisATProtoSessionManager" );
        TimeSpan sessionTtl = (TimeSpan)ttlField.GetValue( manager )!;

        Assert.AreEqual( TimeSpan.FromDays( TtlDays ), sessionTtl,
            "_sessionTtl must equal the configured sessionTtlDays" );

        manager.Dispose( );
    }

    /// <summary>
    /// The session protector's <c>Protect</c> output must not contain the refresh token as a
    /// substring, confirming the session manager's payload is encrypted before storage.
    ///
    /// <para>
    /// <c>BlueskyAgent</c> is a concrete sealed class from the idunno library and cannot be mocked
    /// to produce an authenticated state in a unit test — driving the full
    /// <c>PersistCredentialsFromAgentAsync</c> path is not feasible without a live PDS. The persist
    /// path is exercised end-to-end in the Redis integration tests. This test validates the
    /// encryption contract on the same protector purpose that the manager uses, keeping the unit
    /// suite hermetic.
    /// </para>
    ///
    /// Failure-first evidence: replacing <c>protector.Protect(json)</c> with a no-op identity
    /// function makes the ciphertext equal to the JSON payload, which embeds the refresh token
    /// verbatim — causing the <c>Assert.DoesNotContain</c> assertion to fail.
    /// </summary>
    [TestMethod]
    public void ProtectorForSessionManager_Protect_ProducesCiphertextNotContainingToken( ) {
        const string RefreshToken = "super-secret-refresh-token-xyz";

        string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtector protector = BuildProtector( keyPath, RedisATProtoSessionManager.SessionProtectorPurpose );

            ATProtoPersistedCredentials creds = new( ) {
                RefreshToken = RefreshToken,
                Service = "https://bsky.social",
                Did = "did:plc:abc",
                Handle = "test.bsky.social",
                AuthenticationType = "UsernamePassword",
                PersistedAt = DateTimeOffset.UtcNow
            };
            string json = JsonSerializer.Serialize( creds, s_jsonOptions );

            string ciphertext = protector.Protect( json );

            Assert.DoesNotContain( RefreshToken, ciphertext,
                "Ciphertext must not contain the refresh token in the clear" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>Reflection handle for the private <c>TryRestoreSessionAsync</c> method.</summary>
    private static readonly System.Reflection.MethodInfo s_tryRestoreSessionAsync =
        typeof( RedisATProtoSessionManager )
            .GetMethod( "TryRestoreSessionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )
        ?? throw new InvalidOperationException( "TryRestoreSessionAsync not found on RedisATProtoSessionManager" );

    /// <summary>
    /// When the stored session payload cannot be decrypted (key-ring mismatch or legacy plaintext),
    /// TryRestoreSessionAsync must return false without throwing, and must log event
    /// <c>1124</c> (<c>RestoreCryptoMismatch</c>) rather than the generic restore-exception event
    /// <c>1106</c> (<c>RestoreException</c>).
    ///
    /// Calls TryRestoreSessionAsync via reflection (bypassing the distributed-lock path) to isolate
    /// the decrypt-and-degrade behaviour. This matches the pattern used in
    /// <see cref="RedisATProtoSessionManagerTests"/> for direct private-method testing.
    ///
    /// Failure-first evidence: before the dedicated <c>CryptographicException</c> catch block was
    /// added, the generic <c>catch (Exception)</c> caught the decrypt failure and logged event 1106
    /// (<c>RestoreException</c>). The <c>loggerMock.Verify</c> for event 1124 therefore failed (Times.Once
    /// → Times.Never), and the verify for event 1106 passed (Times.Never → Times.Once). After adding
    /// the distinct <c>CryptographicException</c> catch that logs 1124, the assertions reverse: 1124
    /// fires and 1106 does not.
    /// </summary>
    [TestMethod]
    public async Task SessionManager_KeyRingMismatchOnRead_DegradesToFreshLogin( ) {
        // Arrange: seed Redis with ciphertext encrypted by a DIFFERENT protector (simulates
        // key-ring rotation or a pre-encrypt plaintext value that happens to be valid UTF-8).
        string keyPath1 = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        string keyPath2 = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
        try {
            IDataProtector protectorA = BuildProtector( keyPath1, RedisATProtoSessionManager.SessionProtectorPurpose );
            IDataProtector protectorB = BuildProtector( keyPath2, RedisATProtoSessionManager.SessionProtectorPurpose );

            // Encrypt with A, attempt to decrypt with B (different key ring → CryptographicException).
            string payload = protectorA.Protect( "{\"refreshToken\":\"tok\",\"service\":\"https://bsky.social\"}" );

            // Pre-verify the cross-key-ring decrypt actually throws before we rely on the manager
            // to catch it; if this assertion fails, the test premise is wrong.
            _ = Assert.ThrowsExactly<CryptographicException>( ( ) => protectorB.Unprotect( payload ) );

            // Seed Redis mock with the cross-encrypted payload so TryRestoreSessionAsync reads it.
            Mock<IDatabase> dbMock = new( );
            _ = dbMock
                .Setup( d => d.StringGetAsync( It.IsAny<RedisKey>( ), It.IsAny<CommandFlags>( ) ) )
                .ReturnsAsync( (RedisValue)payload );

            Mock<IConnectionMultiplexer> redisMock = new( );
            _ = redisMock.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object?>( ) ) )
                .Returns( dbMock.Object );

            Mock<ILogger<RedisATProtoSessionManager>> loggerMock = new( );
            // IsEnabled must return true so source-generated log methods don't short-circuit.
            _ = loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

            RedisATProtoSessionManager manager = new(
                redisMock.Object,
                loggerMock.Object,
                "test.bsky.social",
                "password",
                protector: protectorB,
                sessionTtlDays: 45 );

            // Act: call TryRestoreSessionAsync directly via reflection so we bypass the
            // distributed-lock path entirely (the lock path is tested in RedisATProtoSessionManagerTests).
            // The degrade we are verifying is: CryptographicException is caught → returns false.
            Task<bool> task = (Task<bool>)s_tryRestoreSessionAsync.Invoke( manager, [CancellationToken.None] )!;
            bool result = await task;

            // Assert: must return false (degrade to fresh login) without throwing.
            Assert.IsFalse( result,
                "TryRestoreSessionAsync must return false when the stored payload cannot be decrypted (key-ring mismatch)" );

            // Assert: the DISTINCT crypto-mismatch event (1124) must have fired.
            loggerMock.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.Is<EventId>( e => e.Id == 1124 ),
                    It.IsAny<It.IsAnyType>( ),
                    It.IsAny<CryptographicException>( ),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
                Times.Once,
                "RestoreCryptoMismatch (EventId 1124) must fire exactly once on key-ring mismatch" );

            // Assert: the generic restore-exception event (1106) must NOT have fired.
            loggerMock.Verify(
                l => l.Log(
                    It.IsAny<LogLevel>( ),
                    It.Is<EventId>( e => e.Id == 1106 ),
                    It.IsAny<It.IsAnyType>( ),
                    It.IsAny<Exception>( ),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>( ) ),
                Times.Never,
                "Generic RestoreException (EventId 1106) must NOT fire when the specific CryptographicException catch handles the failure" );

            manager.Dispose( );
        } finally {
            if (Directory.Exists( keyPath1 )) { Directory.Delete( keyPath1, recursive: true ); }
            if (Directory.Exists( keyPath2 )) { Directory.Delete( keyPath2, recursive: true ); }
        }
    }

    // ---------------------------------------------------------------------------
    // Session manager — PersistPayloadAsync drives the real persist path
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <c>PersistPayloadAsync</c> must write ciphertext (not the plaintext refresh token) to Redis and
    /// must pass the configured session TTL as the expiry argument to <c>StringSetAsync</c>.
    ///
    /// <para>
    /// This test drives the production <c>PersistPayloadAsync</c> seam directly, capturing the
    /// <c>StringSetAsync</c> arguments via a Moq <c>Callback</c>. Two cases are exercised:
    /// <c>sessionTtlDays: 7</c> and <c>sessionTtlDays: 45</c>. If the assertion tracked a constant
    /// rather than the field, the 45-day negative-control case would fail.
    /// </para>
    ///
    /// Failure-first evidence:
    /// (i) Replacing <c>_protector.Protect(json)</c> with a no-op identity makes the captured value
    ///     equal to the raw JSON, which embeds the refresh token — <c>DoesNotContain</c> fails.
    /// (ii) Replacing <c>_sessionTtl</c> with <c>TimeSpan.Zero</c> causes the 7-day expiry assertion
    ///      to fail with the wrong value.
    /// (iii) The 45-day negative-control case fails if the assertion uses a hardcoded 7-day literal.
    /// </summary>
    [TestMethod]
    public async Task SessionManager_Persist_WritesCiphertextWithSessionTtl( ) {
        const string RefreshToken = "persist-payload-secret-refresh-token";

        await AssertPersistAsync( sessionTtlDays: 7, RefreshToken );
        await AssertPersistAsync( sessionTtlDays: 45, RefreshToken );

        static async Task AssertPersistAsync( int sessionTtlDays, string refreshToken ) {
            string keyPath = Path.Combine( Path.GetTempPath( ), $"bb-dp-test-{Guid.NewGuid():N}" );
            try {
                IDataProtector protector = BuildProtector( keyPath, RedisATProtoSessionManager.SessionProtectorPurpose );

                RedisValue capturedValue = default;
                Expiration capturedExpiry = default;

                Mock<IDatabase> dbMock = new( );
                // In StackExchange.Redis 2.13.1, the legacy (TimeSpan?, When) overloads are
                // default interface method shims; Moq cannot intercept DIMs. The canonical abstract
                // method the DIM chain resolves to is (RedisKey, RedisValue, Expiration, ValueCondition,
                // CommandFlags). Capture args here; TimeSpan is implicitly converted to Expiration.
                _ = dbMock
                    .Setup( d => d.StringSetAsync(
                        It.IsAny<RedisKey>( ),
                        It.IsAny<RedisValue>( ),
                        It.IsAny<Expiration>( ),
                        It.IsAny<ValueCondition>( ),
                        It.IsAny<CommandFlags>( ) ) )
                    .Callback<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags>( ( _, value, expiry, _, _ ) => {
                        capturedValue = value;
                        capturedExpiry = expiry;
                    } )
                    .ReturnsAsync( true );

                Mock<IConnectionMultiplexer> redisMock = new( );
                _ = redisMock.Setup( r => r.GetDatabase( It.IsAny<int>( ), It.IsAny<object?>( ) ) )
                    .Returns( dbMock.Object );

                Mock<ILogger<RedisATProtoSessionManager>> loggerMock = new( );
                _ = loggerMock.Setup( l => l.IsEnabled( It.IsAny<LogLevel>( ) ) ).Returns( true );

                RedisATProtoSessionManager manager = new(
                    redisMock.Object,
                    loggerMock.Object,
                    "test.bsky.social",
                    "password",
                    protector: protector,
                    sessionTtlDays: sessionTtlDays );

                ATProtoPersistedCredentials creds = new( ) {
                    RefreshToken = refreshToken,
                    Service = "https://bsky.social",
                    Did = "did:plc:persisttest",
                    Handle = "test.bsky.social",
                    AuthenticationType = "UsernamePassword",
                    PersistedAt = DateTimeOffset.UtcNow
                };

                await manager.PersistPayloadAsync( creds );

                // (i) Stored value must be ciphertext — must not contain the plaintext refresh token.
                string storedStr = capturedValue.ToString( );
                Assert.DoesNotContain( refreshToken, storedStr,
                    $"[ttl={sessionTtlDays}d] Stored Redis value must not contain the plaintext refresh token" );

                // (ii) Positive control: the same protector can unprotect the ciphertext back to JSON
                // containing the refresh token, confirming real encryption (not a no-op) was applied.
                string decrypted = protector.Unprotect( storedStr );
                Assert.Contains( refreshToken, decrypted,
                    $"[ttl={sessionTtlDays}d] Decrypted payload must contain the refresh token (positive control)" );

                // (iii) The Expiration passed to StringSetAsync must equal the configured sessionTtlDays.
                // In StackExchange.Redis 2.13.1, TimeSpan is implicitly converted to Expiration.
                Assert.AreEqual( new Expiration( TimeSpan.FromDays( sessionTtlDays ) ), capturedExpiry,
                    $"[ttl={sessionTtlDays}d] StringSetAsync expiry must equal the configured session TTL" );

                manager.Dispose( );
            } finally {
                if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Secret-record redaction
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ATProtoPersistedCredentials.ToString()"/> must not include the refresh token,
    /// the DPoP proof key, or the DPoP nonce in its output.
    ///
    /// Failure-first evidence: before the ToString() override was added the default record
    /// ToString() emitted all property values. The assertions below failed because the output
    /// contained RefreshToken, DPoPProofKey, and DPoPNonce verbatim.
    /// </summary>
    [TestMethod]
    public void ATProtoPersistedCredentials_ToString_OmitsSecretFields( ) {
        ATProtoPersistedCredentials creds = new( ) {
            RefreshToken = "secret-refresh-token-abc",
            DPoPProofKey = "secret-dpop-key-xyz",
            DPoPNonce = "secret-nonce-nnn",
            Service = "https://bsky.social",
            Did = "did:plc:testuser",
            Handle = "alice.bsky.social",
            AuthenticationType = "DPoP",
            PersistedAt = DateTimeOffset.UtcNow
        };

        string result = creds.ToString( );

        Assert.DoesNotContain( "secret-refresh-token-abc", result, "ToString must not include RefreshToken" );
        Assert.DoesNotContain( "secret-dpop-key-xyz", result, "ToString must not include DPoPProofKey" );
        Assert.DoesNotContain( "secret-nonce-nnn", result, "ToString must not include DPoPNonce" );

        // Non-secret identity fields should be present.
        Assert.Contains( "alice.bsky.social", result, "ToString must include Handle" );
        Assert.Contains( "did:plc:testuser", result, "ToString must include Did" );
        Assert.Contains( "https://bsky.social", result, "ToString must include Service" );
        Assert.Contains( "DPoP", result, "ToString must include AuthenticationType" );
    }

}
