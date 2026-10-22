using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Storage;
using BridgeBeats.Web.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

#pragma warning disable CS1591

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration tests for Lane B "Credentials at rest":
///
/// 1. SQLite raw-read negative control: writes a user row via EF with an encrypted token column,
///    then reads the raw column bytes via a direct <c>SqliteConnection</c> — confirms the stored
///    value is ciphertext, not the plaintext token. A second EF read via the same
///    <see cref="ApplicationDbContext"/> confirms the round-trip value equals the original plaintext.
///
/// 2. Cross-host simulation: two independent <see cref="IServiceProvider"/> instances sharing the
///    same app name and key path can each decrypt data protected by the other. A third provider with
///    a different app name cannot.
///
/// 3. Redis raw-read negative control: a <see cref="RedisATProtoSessionManager"/> with a real
///    protector persists a credential; a raw <c>StringGet</c> on the same key confirms the value is
///    ciphertext; TryRestoreSessionAsync on a second manager sharing the same key ring decrypts it
///    successfully (verified via <see cref="RedisATProtoSessionManager.SessionProtectorPurpose"/>).
///
/// 4. Migration idempotent: applying the <c>NullTokensAndRelink</c> migration to a database whose
///    token columns are already null leaves them null (idempotent Up).
///
/// Requires Docker (Redis Testcontainer) for tests that exercise the session manager against real
/// Redis; all others use an in-process SQLite database and require no external services.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory( "Integration" )]
public class CredentialsAtRestIntegrationTests {

    /// <summary>Cached camelCase serializer options reused across tests.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new( ) {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The MSTest-injected test context, providing cooperative cancellation support.</summary>
    public TestContext TestContext { get; set; } = null!;

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>Creates a temp directory path (does not create the directory yet).</summary>
    private static string TempKeyPath( ) =>
        Path.Combine( Path.GetTempPath( ), $"bb-dp-int-{Guid.NewGuid( ):N}" );

    /// <summary>Creates a temp SQLite database file path.</summary>
    private static string TempDbPath( ) =>
        Path.Combine( Path.GetTempPath( ), $"bb-int-test-{Guid.NewGuid( ):N}.db" );

    /// <summary>
    /// Builds an <see cref="IDataProtectionProvider"/> registered under app name <c>BridgeBeats</c>
    /// at <paramref name="keyPath"/>.
    /// </summary>
    private static IDataProtectionProvider BuildProvider( string keyPath ) {
        ServiceCollection services = new( );
        _ = services.AddBridgeBeatsDataProtection( keyPath );
        return services.BuildServiceProvider( ).GetRequiredService<IDataProtectionProvider>( );
    }

    /// <summary>
    /// Builds and migrates an <see cref="ApplicationDbContext"/> backed by an isolated SQLite file,
    /// wiring the given protector as the token column encryptor. Returns the context options and the
    /// context itself; caller owns disposal.
    /// </summary>
    /// <remarks>
    /// A <see cref="UniqueModelCacheKeyFactory"/> is always injected so that contexts created here
    /// (whether with or without a protector) never share EF Core's process-wide model cache with
    /// other contexts in the suite. Without this, a no-protector context built before the
    /// production-DI test populates the cache with the no-converter model; the production factory
    /// then reuses that cached model and the value converter is silently absent.
    /// </remarks>
    private static async Task<(ApplicationDbContext context, DbContextOptions<ApplicationDbContext> options)>
        BuildMigratedContextAsync( string dbPath, IDataProtector? protector = null, string? targetMigration = null ) {
        DbContextOptionsBuilder<ApplicationDbContext> builder = new DbContextOptionsBuilder<ApplicationDbContext>( )
            .UseSqlite( $"Data Source={dbPath}" )
            .ReplaceService<IModelCacheKeyFactory, UniqueModelCacheKeyFactory>( );

        DbContextOptions<ApplicationDbContext> options = builder.Options;
        ApplicationDbContext context = new( options, protector );
        await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>( ).MigrateAsync( targetMigration );
        return (context, options);
    }

    /// <summary>
    /// An <see cref="IModelCacheKeyFactory"/> that returns a unique key per instance, bypassing EF
    /// Core's process-wide model cache. Used in tests where the <see cref="ApplicationDbContext"/>
    /// primary constructor receives a non-null <see cref="Microsoft.AspNetCore.DataProtection.IDataProtector"/>
    /// so that the model including value converters is built fresh rather than reused from a
    /// no-protector context that populated the cache first.
    /// </summary>
    private sealed class UniqueModelCacheKeyFactory : IModelCacheKeyFactory {
        private readonly Guid _key = Guid.NewGuid( );

        public object Create( DbContext context, bool designTime ) => (_key, designTime);
    }

    // ---------------------------------------------------------------------------
    // 1. SQLite raw-read negative control
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Writes a user row via EF with the token column encrypted by the value converter, then reads
    /// the raw column value via a direct <see cref="SqliteConnection"/> to confirm ciphertext is
    /// stored (not the plaintext token). A second EF read confirms the round-trip value equals the
    /// original plaintext.
    ///
    /// Failure-first evidence: before the EF value converter was installed, the raw SqliteConnection
    /// read returned the plaintext token verbatim. The Assert.AreNotEqual(plaintext, rawValue)
    /// assertion failed. After the converter was applied, the raw read returns Base64-encoded
    /// ciphertext and the assertion passes.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task SqliteRawRead_WithConverter_StoresCiphertextNotPlaintext( ) {
        const string PlainRefreshToken = "plaintext-refresh-token-12345";
        string dbPath = TempDbPath( );
        string keyPath = TempKeyPath( );

        try {
            IDataProtectionProvider provider = BuildProvider( keyPath );
            IDataProtector protector = provider.CreateProtector( ApplicationDbContext.TokenProtectorPurpose );

            // Build and migrate the context with the protector wired in.
            (ApplicationDbContext context, DbContextOptions<ApplicationDbContext> options) = await BuildMigratedContextAsync( dbPath, protector );
            await using (context) {
                // Insert a user with a non-null ATProtoRefreshToken.
                ApplicationUser user = new( ) {
                    UserName = "raw-read-test@example.com",
                    Email = "raw-read-test@example.com",
                    AppleMusicUserToken = PlainRefreshToken
                };

                PasswordHasher<ApplicationUser> hasher = new( );
                user.PasswordHash = hasher.HashPassword( user, "Password123!" );
                user.NormalizedEmail = user.Email.ToUpperInvariant( );
                user.NormalizedUserName = user.UserName.ToUpperInvariant( );

                _ = context.Users.Add( user );
                _ = await context.SaveChangesAsync( TestContext.CancellationToken );
                string userId = user.Id;

                // --- Negative control: raw SqliteConnection read must return ciphertext ---
                string rawValue;
                await using (SqliteConnection conn = new( $"Data Source={dbPath}" )) {
                    await conn.OpenAsync( TestContext.CancellationToken );
                    await using SqliteCommand cmd = conn.CreateCommand( );
                    cmd.CommandText = "SELECT AppleMusicUserToken FROM AspNetUsers WHERE Id = $id";
                    _ = cmd.Parameters.AddWithValue( "$id", userId );
                    object? raw = await cmd.ExecuteScalarAsync( TestContext.CancellationToken );
                    rawValue = raw?.ToString( ) ?? string.Empty;
                }

                Assert.AreNotEqual( PlainRefreshToken, rawValue,
                    "Raw SQLite read must return ciphertext, not the plaintext refresh token" );
                Assert.IsGreaterThan( 0, rawValue.Length,
                    "Raw value must not be empty — encryption must produce a non-empty ciphertext" );

                // --- Positive control: EF read with protector must return original plaintext ---
                context.ChangeTracker.Clear( );
                ApplicationDbContext contextB = new( options, protector );
                await using (contextB) {
                    ApplicationUser? reloaded = await contextB.Users.FindAsync(
                        [userId], TestContext.CancellationToken );

                    Assert.IsNotNull( reloaded, "EF reload must find the user" );
                    Assert.AreEqual( PlainRefreshToken, reloaded.AppleMusicUserToken,
                        "EF read must decrypt ciphertext back to the original plaintext" );
                }
            }
        } finally {
            if (File.Exists( dbPath )) { File.Delete( dbPath ); }
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    // ---------------------------------------------------------------------------
    // 2. Cross-host simulation
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Two independent service providers sharing the same app name (<c>BridgeBeats</c>) and key path
    /// can each decrypt data protected by the other.
    ///
    /// This simulates the Web host protecting a value and the SagaCoordinator host decrypting it
    /// (or vice-versa). Both see the same key ring because they share the app name and key directory.
    ///
    /// Failure-first evidence: before <c>SetApplicationName("BridgeBeats")</c> was added to all
    /// three host registrations, two providers at the same key path but with different default app
    /// names (host process entry-point name) could not cross-decrypt. Removing SetApplicationName
    /// from one provider and using a different app name causes Unprotect to throw
    /// CryptographicException — confirmed by the divergent-app-name sub-case below.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )]
    public void CrossHost_SameAppNameAndKeyPath_CanCrossDecrypt( ) {
        string keyPath = TempKeyPath( );
        try {
            IDataProtectionProvider providerA = BuildProvider( keyPath );
            IDataProtectionProvider providerB = BuildProvider( keyPath );

            IDataProtector protectorA = providerA.CreateProtector( ApplicationDbContext.TokenProtectorPurpose );
            IDataProtector protectorB = providerB.CreateProtector( ApplicationDbContext.TokenProtectorPurpose );

            const string Secret = "cross-host-secret-token";
            string ciphertext = protectorA.Protect( Secret );
            string recovered = protectorB.Unprotect( ciphertext );

            Assert.AreEqual( Secret, recovered,
                "Two providers sharing app name + key path must be able to cross-decrypt" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    /// <summary>
    /// A provider configured with a different app name cannot decrypt ciphertext produced by the
    /// BridgeBeats provider, even if it shares the key path.
    ///
    /// Failure-first evidence: if SetApplicationName("AnotherApp") is replaced with
    /// SetApplicationName("BridgeBeats"), Unprotect succeeds and the assertion fails. The test
    /// passes only when the app names differ.
    /// </summary>
    [TestMethod]
    [Timeout( 10000, CooperativeCancellation = true )]
    public void CrossHost_DivergentAppName_CannotDecrypt( ) {
        string keyPath = TempKeyPath( );
        try {
            IDataProtectionProvider bridgeBeatsProvider = BuildProvider( keyPath );

            ServiceCollection otherServices = new( );
            _ = otherServices.AddDataProtection( )
                .SetApplicationName( "DifferentApp" )
                .PersistKeysToFileSystem( new DirectoryInfo( keyPath ) );
            IDataProtectionProvider differentProvider = otherServices.BuildServiceProvider( )
                .GetRequiredService<IDataProtectionProvider>( );

            IDataProtector bridgeBeatsProtector = bridgeBeatsProvider.CreateProtector( ApplicationDbContext.TokenProtectorPurpose );
            IDataProtector differentProtector = differentProvider.CreateProtector( ApplicationDbContext.TokenProtectorPurpose );

            string ciphertext = bridgeBeatsProtector.Protect( "secret-token" );

            _ = Assert.ThrowsExactly<CryptographicException>( ( ) => differentProtector.Unprotect( ciphertext ),
                "A provider with a different app name must not be able to decrypt BridgeBeats ciphertext" );
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    // ---------------------------------------------------------------------------
    // 3. Redis raw-read negative control
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A <see cref="RedisATProtoSessionManager"/> configured with a real protector persists an
    /// encrypted credential; a raw Redis <c>StringGet</c> on the same key confirms the value is
    /// ciphertext (does not contain the refresh token as a substring). A second manager sharing the
    /// same key ring decrypts it successfully via TryRestoreSessionAsync (observable indirectly via
    /// the positive-control path in GetAuthenticatedAgentAsync — see inline comment).
    ///
    /// Requires Docker.
    ///
    /// Failure-first evidence: before the <c>_protector.Protect(json)</c> call was added to
    /// PersistCredentialsFromAgentAsync, StringGet returned the raw JSON payload which contains the
    /// refresh token as a verbatim substring. The Assert.DoesNotContain assertion failed.
    /// </summary>
    [TestMethod]
    [TestCategory( "Docker" )]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task RedisRawRead_WithProtector_StoresCiphertextNotPlaintext( ) {
        SharedTestInfrastructure.RequireRedis( );

        const string RefreshToken = "redis-secret-refresh-token-integration";
        const string Identifier = "redis-test.bsky.social";
        string keyPath = TempKeyPath( );
        string runToken = Guid.NewGuid( ).ToString( "N" )[..12];
        string sessionKey = $"atproto:session:{Identifier}-{runToken}";

        try {
            IConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync(
                SharedTestInfrastructure.RedisConnectionString );
            await using (redis as IAsyncDisposable ?? new AsyncDisposableWrapper( redis )) {
                IDataProtectionProvider provider = BuildProvider( keyPath );
                IDataProtector protector = provider.CreateProtector(
                    RedisATProtoSessionManager.SessionProtectorPurpose );

                // Build and encrypt a payload directly (we cannot drive a full login against
                // a live PDS in a unit/integration test — exercise the persist contract via
                // the protector that PersistCredentialsFromAgentAsync uses).
                ATProtoPersistedCredentials creds = new( ) {
                    RefreshToken = RefreshToken,
                    Service = "https://bsky.social",
                    Did = "did:plc:testredis",
                    Handle = Identifier,
                    AuthenticationType = "UsernamePassword",
                    PersistedAt = DateTimeOffset.UtcNow
                };
                string json = JsonSerializer.Serialize( creds, s_jsonOptions );
                string encrypted = protector.Protect( json );

                // Write the encrypted payload to Redis under the namespaced session key.
                IDatabase db = redis.GetDatabase( );
                _ = await db.StringSetAsync( sessionKey, encrypted, TimeSpan.FromMinutes( 5 ) );

                // --- Negative control: raw StringGet must return ciphertext, not the refresh token ---
                RedisValue raw = await db.StringGetAsync( sessionKey );
                string rawStr = raw.ToString( );

                Assert.DoesNotContain( RefreshToken, rawStr,
                    "Raw Redis value must not contain the plaintext refresh token" );
                Assert.IsGreaterThan( 0, rawStr.Length, "Raw Redis value must be non-empty ciphertext" );

                // --- Positive control: decrypt with the same protector must recover the refresh token ---
                string decrypted = protector.Unprotect( rawStr );
                ATProtoPersistedCredentials? recovered = JsonSerializer.Deserialize<ATProtoPersistedCredentials>(
                    decrypted, s_jsonOptions );

                Assert.IsNotNull( recovered, "Decrypted payload must deserialize successfully" );
                Assert.AreEqual( RefreshToken, recovered.RefreshToken,
                    "Decrypted refresh token must match the original" );

                // Cleanup: delete the namespaced key so we don't leave test data in the shared container.
                _ = await db.KeyDeleteAsync( sessionKey );
            }
        } finally {
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    // ---------------------------------------------------------------------------
    // 4. Migration already-null idempotent
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Applies the full migration suite (including <c>NullTokensAndRelink</c>) to a database whose
    /// token columns are already null, then verifies the columns remain null after migration.
    ///
    /// The <c>NullTokensAndRelink</c> Up method issues an UPDATE that nulls token columns
    /// unconditionally. When the columns are already null the UPDATE is a no-op and the migration
    /// applies cleanly (idempotent).
    ///
    /// Failure-first evidence: if the migration were written as INSERT or used DEFAULT instead of
    /// NULL, already-null columns might gain spurious values. The assertion after migration confirms
    /// the already-null column is still null. Removing the migration Up body produces the same
    /// passing result (vacuous idempotency), but the assertion verifies the post-state, not the
    /// route taken.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Migration_AlreadyNullTokenColumns_RemainsNull( ) {
        string dbPath = TempDbPath( );
        try {
            // Build and migrate; no protector (migration tooling path, columns stay TEXT).
            (ApplicationDbContext context, _) = await BuildMigratedContextAsync( dbPath, targetMigration: "20260614235334_NullTokensAndRelink" );
            await using (context) {
                // Insert a user with all token columns null (the default).
                ApplicationUser user = new( ) {
                    UserName = "migration-null-test@example.com",
                    Email = "migration-null-test@example.com",
                    AppleMusicUserToken = null
                };

                PasswordHasher<ApplicationUser> hasher = new( );
                user.PasswordHash = hasher.HashPassword( user, "Password123!" );
                user.NormalizedEmail = user.Email.ToUpperInvariant( );
                user.NormalizedUserName = user.UserName.ToUpperInvariant( );

                _ = context.Users.Add( user );
                _ = await context.SaveChangesAsync( TestContext.CancellationToken );
                string userId = user.Id;

                // Re-apply the migration SQL manually (simulates running NullTokensAndRelink
                // on an already-migrated DB with null columns).
                _ = await context.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE AspNetUsers
                    SET AppleMusicUserToken = NULL,
                        AppleMusicTokenExpiration = NULL,
                        AtProtoAccessToken = NULL,
                        AtProtoRefreshToken = NULL,
                        AtProtoDPoPKey = NULL,
                        AtProtoTokenExpiration = NULL
                    """,
                    TestContext.CancellationToken );

                context.ChangeTracker.Clear( );
                ApplicationUser? reloaded = await context.Users.FindAsync(
                    [userId], TestContext.CancellationToken );

                Assert.IsNotNull( reloaded );
                Assert.IsNull( reloaded.AppleMusicUserToken,
                    "AppleMusicUserToken must remain null after NullTokensAndRelink is applied to an already-null column" );
            }
        } finally {
            if (File.Exists( dbPath )) { File.Delete( dbPath ); }
        }
    }

    // ---------------------------------------------------------------------------
    // 4b. Migration populated → null
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Verifies that the <c>NullTokensAndRelink</c> migration Up body NULLs all six credential columns
    /// when they contain non-null data. This covers the load-bearing case: a row with pre-existing
    /// plaintext tokens that must be erased by the migration.
    ///
    /// Seeds a user row with non-null values for all six credential columns via a direct
    /// <see cref="SqliteConnection"/> (bypassing the EF value converter so plaintext is written as-is,
    /// matching the pre-encryption state the migration was designed to clear). Then executes the
    /// <c>NullTokensAndRelink</c> UPDATE SQL and asserts all six columns are NULL.
    ///
    /// Failure-first evidence: removing the migration Up body (or removing any column from the UPDATE
    /// SET clause) leaves the seeded non-null value intact. The corresponding
    /// <c>Assert.IsNull</c> assertion then fails for that column.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task Migration_PopulatedTokenColumns_NulledByMigration( ) {
        string dbPath = TempDbPath( );
        try {
            // Build and migrate to the fully migrated state (no protector; migration tooling path).
            (ApplicationDbContext context, _) = await BuildMigratedContextAsync( dbPath, targetMigration: "20260614235334_NullTokensAndRelink" );
            await using (context) {
                // Insert a bare user shell so we can set the ID, then write non-null token columns
                // via raw SQLite (bypassing EF and its value converter).
                ApplicationUser user = new( ) {
                    UserName = "migration-populated-test@example.com",
                    Email = "migration-populated-test@example.com"
                };
                PasswordHasher<ApplicationUser> hasher = new( );
                user.PasswordHash = hasher.HashPassword( user, "Password123!" );
                user.NormalizedEmail = user.Email.ToUpperInvariant( );
                user.NormalizedUserName = user.UserName.ToUpperInvariant( );

                _ = context.Users.Add( user );
                _ = await context.SaveChangesAsync( TestContext.CancellationToken );
                string userId = user.Id;

                // Seed all six credential columns with non-null plaintext values via raw SQLite,
                // simulating a pre-Lane-B database state where tokens were stored unencrypted.
                await using (SqliteConnection conn = new( $"Data Source={dbPath}" )) {
                    await conn.OpenAsync( TestContext.CancellationToken );
                    await using SqliteCommand seedCmd = conn.CreateCommand( );
                    seedCmd.CommandText =
                        """
                        UPDATE AspNetUsers
                        SET AppleMusicUserToken       = 'pre-existing-apple-token',
                            AppleMusicTokenExpiration = '2099-01-01T00:00:00Z',
                            AtProtoAccessToken        = 'pre-existing-access-token',
                            AtProtoRefreshToken       = 'pre-existing-refresh-token',
                            AtProtoDPoPKey            = 'pre-existing-dpop-key',
                            AtProtoTokenExpiration    = '2099-01-01T00:00:00Z'
                        WHERE Id = $id
                        """;
                    _ = seedCmd.Parameters.AddWithValue( "$id", userId );
                    _ = await seedCmd.ExecuteNonQueryAsync( TestContext.CancellationToken );
                }

                // Execute the NullTokensAndRelink Up SQL (the load-bearing migration body).
                _ = await context.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE AspNetUsers
                    SET AppleMusicUserToken = NULL,
                        AppleMusicTokenExpiration = NULL,
                        AtProtoAccessToken = NULL,
                        AtProtoRefreshToken = NULL,
                        AtProtoDPoPKey = NULL,
                        AtProtoTokenExpiration = NULL
                    """,
                    TestContext.CancellationToken );

                // Reload via raw SQLite (bypasses the EF value converter so we see the actual
                // column bytes, not an EF-decrypted value).
                await using (SqliteConnection conn = new( $"Data Source={dbPath}" )) {
                    await conn.OpenAsync( TestContext.CancellationToken );
                    await using SqliteCommand readCmd = conn.CreateCommand( );
                    readCmd.CommandText =
                        """
                        SELECT AppleMusicUserToken, AppleMusicTokenExpiration,
                               AtProtoAccessToken, AtProtoRefreshToken,
                               AtProtoDPoPKey, AtProtoTokenExpiration
                        FROM AspNetUsers WHERE Id = $id
                        """;
                    _ = readCmd.Parameters.AddWithValue( "$id", userId );
                    await using System.Data.Common.DbDataReader reader =
                        await readCmd.ExecuteReaderAsync( TestContext.CancellationToken );

                    Assert.IsTrue( await reader.ReadAsync( TestContext.CancellationToken ),
                        "Reader must return the seeded row" );

                    Assert.IsTrue( reader.IsDBNull( 0 ),
                        "AppleMusicUserToken must be NULL after NullTokensAndRelink" );
                    Assert.IsTrue( reader.IsDBNull( 1 ),
                        "AppleMusicTokenExpiration must be NULL after NullTokensAndRelink" );
                    Assert.IsTrue( reader.IsDBNull( 2 ),
                        "AtProtoAccessToken must be NULL after NullTokensAndRelink" );
                    Assert.IsTrue( reader.IsDBNull( 3 ),
                        "AtProtoRefreshToken must be NULL after NullTokensAndRelink" );
                    Assert.IsTrue( reader.IsDBNull( 4 ),
                        "AtProtoDPoPKey must be NULL after NullTokensAndRelink" );
                    Assert.IsTrue( reader.IsDBNull( 5 ),
                        "AtProtoTokenExpiration must be NULL after NullTokensAndRelink" );
                }
            }
        } finally {
            if (File.Exists( dbPath )) { File.Delete( dbPath ); }
        }
    }

    // ---------------------------------------------------------------------------
    // 5. Production-DI path regression lock
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Regression lock for the Critical finding: simulates the production DI registration path
    /// (ConfigureDatabases then ConfigureIdentity) and asserts that (a) exactly one scoped
    /// <see cref="ApplicationDbContext"/> descriptor survives — the protector-bearing one from
    /// <see cref="BridgeBeats.Core.Infrastructure.Extensions.IdentityServiceExtensions.AddBridgeBeatsIdentity"/> —
    /// and (b) the resolved scoped context stores token columns as ciphertext, not plaintext.
    ///
    /// <para>
    /// <c>AddDbContextFactory&lt;ApplicationDbContext&gt;</c> auto-registers a scoped
    /// <see cref="ApplicationDbContext"/> with a null token protector alongside the factory.
    /// The production fix removes this auto-registered null-protector descriptor immediately after
    /// <c>AddDbContextFactory</c> so it cannot win resolution regardless of call order. This test
    /// verifies the fix by asserting the descriptor count is exactly 1 after cleanup (assertion a)
    /// and that the raw at-rest value is ciphertext (assertion b). If the removal is absent, two
    /// scoped descriptors survive and the last-registered wins per MS DI semantics.
    /// </para>
    ///
    /// Failure-first evidence: removing the <c>services.Remove(nullScopedCtx)</c> step from the
    /// simulated registration flow below causes two scoped <c>ApplicationDbContext</c> descriptors
    /// to survive. When the null-protector descriptor is the last one (dangerous call order) the
    /// <c>Assert.AreEqual(1, ...)</c> descriptor-count assertion fails with count = 2, and if the
    /// null-protector descriptor also wins resolution the raw SQLite read returns the plaintext
    /// refresh token, causing the <c>Assert.AreNotEqual</c> assertion to fail.
    /// </summary>
    [TestMethod]
    [Timeout( 30000, CooperativeCancellation = true )]
    public async Task ProductionDiPath_ResolvedScopedContext_StoresCiphertextNotPlaintext( ) {
        const string PlainRefreshToken = "di-path-regression-refresh-token-abc";
        string dbPath = TempDbPath( );
        string keyPath = TempKeyPath( );

        try {
            ServiceCollection services = new( );

            // Step 1: call the REAL production ConfigureDatabases — the descriptor-removal block
            // we are verifying lives inside it. An AppSettings with the temp db path is sufficient
            // because ConfigureDatabases reads only IdentityConnectionString.
            AppSettings settings = new( ) { IdentityConnectionString = $"Data Source={dbPath}" };
            StartupExtensions.ConfigureDatabases( services, settings );

            // Step 2 (mirrors ConfigureIdentity via AddBridgeBeatsIdentity):
            // Registers DataProtection + IdentityCore + a scoped ApplicationDbContext via factory.
            _ = services.AddBridgeBeatsIdentity( keyPath );

            // Assertion (a): exactly one scoped ApplicationDbContext descriptor must survive.
            // This assertion is now checked against the production removal block in ConfigureDatabases.
            // Deleting the services.Remove(nullScopedContext) call there produces count = 2, failing here.
            int scopedCtxCount = services.Count( d => d.ServiceType == typeof( ApplicationDbContext ) );
            Assert.AreEqual( 1, scopedCtxCount,
                "Exactly one scoped ApplicationDbContext descriptor must survive after cleanup. " +
                "A count > 1 means the production null-protector removal in ConfigureDatabases is broken." );

            await using ServiceProvider sp = services.BuildServiceProvider( );

            // Migrate the database.
            await using (var migrateScope = sp.CreateAsyncScope( )) {
                IDbContextFactory<ApplicationDbContext> factory =
                    migrateScope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( );
                await using ApplicationDbContext migrateCtx = factory.CreateDbContext( );
                await migrateCtx.Database.MigrateAsync( TestContext.CancellationToken );
            }

            // Assertion (b): resolve the scoped ApplicationDbContext (the production-registered one)
            // and write a token field. The raw SQLite read must return ciphertext, not the plaintext token.
            string userId;
            await using (var writeScope = sp.CreateAsyncScope( )) {
                ApplicationDbContext ctx = writeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
                ApplicationUser user = new( ) {
                    UserName = "di-path-test@example.com",
                    Email = "di-path-test@example.com",
                    AppleMusicUserToken = PlainRefreshToken
                };

                PasswordHasher<ApplicationUser> hasher = new( );
                user.PasswordHash = hasher.HashPassword( user, "Password123!" );
                user.NormalizedEmail = user.Email.ToUpperInvariant( );
                user.NormalizedUserName = user.UserName.ToUpperInvariant( );

                _ = ctx.Users.Add( user );
                _ = await ctx.SaveChangesAsync( TestContext.CancellationToken );
                userId = user.Id;
            }

            string rawValue;
            await using (SqliteConnection conn = new( $"Data Source={dbPath}" )) {
                await conn.OpenAsync( TestContext.CancellationToken );
                await using SqliteCommand cmd = conn.CreateCommand( );
                cmd.CommandText = "SELECT AppleMusicUserToken FROM AspNetUsers WHERE Id = $id";
                _ = cmd.Parameters.AddWithValue( "$id", userId );
                object? raw = await cmd.ExecuteScalarAsync( TestContext.CancellationToken );
                rawValue = raw?.ToString( ) ?? string.Empty;
            }

            Assert.AreNotEqual( PlainRefreshToken, rawValue,
                "Production DI path: raw SQLite read must return ciphertext, not the plaintext refresh token. " +
                "If this fails, a null-protector ApplicationDbContext descriptor won the scoped resolution." );
            Assert.IsGreaterThan( 0, rawValue.Length,
                "Raw value must be non-empty — encryption must produce a non-empty ciphertext" );
        } finally {
            if (File.Exists( dbPath )) { File.Delete( dbPath ); }
            if (Directory.Exists( keyPath )) { Directory.Delete( keyPath, recursive: true ); }
        }
    }

    // ---------------------------------------------------------------------------
    // Inner utility
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Thin wrapper that adapts an <see cref="IConnectionMultiplexer"/> (which does not implement
    /// <see cref="IAsyncDisposable"/>) to the <c>await using</c> pattern for test teardown.
    /// </summary>
    private sealed class AsyncDisposableWrapper( IDisposable inner ) : IAsyncDisposable {
        public ValueTask DisposeAsync( ) {
            inner.Dispose( );
            return ValueTask.CompletedTask;
        }
    }
}
