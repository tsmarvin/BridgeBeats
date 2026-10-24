using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Extensions;
using BridgeBeats.Core.Infrastructure.Identity;
using BridgeBeats.Core.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BridgeBeats.Tests.Integration;

/// <summary>
/// Integration coverage for encrypted database-backed application settings using real SQLite and
/// persistent ASP.NET Core Data Protection key rings.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory( "Integration" )]
public class ApplicationSettingsServiceIntegrationTests {
    private const string SpotifySecret = "spotify-secret-that-must-never-appear-in-the-database";
    private static readonly JsonSerializerOptions s_webJsonOptions = new( JsonSerializerDefaults.Web );

    /// <summary>Gets the MSTest context used for cooperative cancellation.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Verifies an options-only context factory cannot cause plaintext persistence and that raw-value
    /// inspection is a working positive control.
    /// </summary>
    [TestMethod]
    public async Task UpdateAndRead_StoresCiphertextAndReturnsRedactedStatus( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        DatabaseApplicationSettingsService service = store.CreateService( );

        ApplicationSettingsStatus status = await service.UpdateAsync(
            CreateInitialUpdate( ),
            TestContext.CancellationToken
        );

        string rawSecrets = await store.ReadRawSecretsAsync( TestContext.CancellationToken );
        string rawValues = await store.ReadRawValuesAsync( TestContext.CancellationToken );
        string protectionScheme = await store.ReadProtectionSchemeAsync( TestContext.CancellationToken );
        ApplicationSettingsSnapshot? runtime = await service.GetRuntimeSettingsAsync( TestContext.CancellationToken );
        string statusJson = JsonSerializer.Serialize( status );

        Assert.DoesNotContain( SpotifySecret, rawSecrets );
        Assert.DoesNotContain( SpotifySecret, rawValues );
        Assert.Contains( "spotify-client-id", rawValues );
        Assert.IsFalse( rawSecrets.TrimStart( ).StartsWith( '{' ), "Secret JSON must be protected before SQLite persistence." );
        Assert.AreEqual( ApplicationSettingsRecord.CurrentSecretsProtectionScheme, protectionScheme );
        Assert.IsNotNull( runtime );
        Assert.AreEqual( SpotifySecret, runtime.Secrets.SpotifyClientSecret.Reveal( ) );
        Assert.IsTrue( status.Secrets.SpotifyClientSecretConfigured );
        Assert.DoesNotContain( SpotifySecret, statusJson );
    }

    /// <summary>Verifies the hand-maintained migration artifacts exactly match the runtime EF model.</summary>
    [TestMethod]
    public async Task MigrationSnapshot_MatchesRuntimeModel( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );

        Assert.IsFalse( store.HasPendingModelChanges( ) );
    }

    /// <summary>Verifies independent service providers sharing a persisted key ring can decrypt settings.</summary>
    [TestMethod]
    public async Task PersistentKeyRing_NewProviderCanDecryptExistingSettings( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        _ = await store.CreateService( ).UpdateAsync( CreateInitialUpdate( ), TestContext.CancellationToken );

        DatabaseApplicationSettingsService restartedService = store.CreateServiceWithNewProvider( store.KeyPath );
        ApplicationSettingsSnapshot? runtime = await restartedService.GetRuntimeSettingsAsync(
            TestContext.CancellationToken
        );

        Assert.IsNotNull( runtime );
        Assert.AreEqual( SpotifySecret, runtime.Secrets.SpotifyClientSecret.Reveal( ) );
    }

    /// <summary>Verifies a different key ring fails safely with an actionable, redacted exception.</summary>
    [TestMethod]
    public async Task DifferentKeyRing_FailsWithUnreadableSettingsException( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        _ = await store.CreateService( ).UpdateAsync( CreateInitialUpdate( ), TestContext.CancellationToken );
        string wrongKeyPath = Path.Combine( Path.GetTempPath( ), $"bb-settings-wrong-keys-{Guid.NewGuid( ):N}" );

        try {
            DatabaseApplicationSettingsService wrongKeyService = store.CreateServiceWithNewProvider( wrongKeyPath );

            ApplicationSettingsDecryptionException exception = await Assert.ThrowsAsync<ApplicationSettingsDecryptionException>(
                ( ) => wrongKeyService.GetRuntimeSettingsAsync( TestContext.CancellationToken )
            );

            _ = Assert.IsInstanceOfType<CryptographicException>( exception.InnerException );
            Assert.Contains( "Do not overwrite", exception.Message );
            Assert.Contains( "original persistent key ring", exception.Message );
            Assert.DoesNotContain( SpotifySecret, exception.ToString( ) );
        } finally {
            if (Directory.Exists( wrongKeyPath )) {
                Directory.Delete( wrongKeyPath, recursive: true );
            }
        }
    }

    /// <summary>Verifies corrupted ciphertext fails safely and never falls back to plaintext.</summary>
    [TestMethod]
    public async Task CorruptedCiphertext_FailsWithUnreadableSettingsException( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        _ = await store.CreateService( ).UpdateAsync( CreateInitialUpdate( ), TestContext.CancellationToken );
        await store.ReplaceRawSecretsAsync( "corrupted-ciphertext", TestContext.CancellationToken );

        ApplicationSettingsDecryptionException exception = await Assert.ThrowsAsync<ApplicationSettingsDecryptionException>(
            ( ) => store.CreateService( ).GetRuntimeSettingsAsync( TestContext.CancellationToken )
        );

        Assert.Contains( "could not be decrypted", exception.Message );
        Assert.Contains( "Do not overwrite", exception.Message );
        Assert.DoesNotContain( SpotifySecret, exception.ToString( ) );
    }

    /// <summary>Verifies authenticated plaintext containing malformed JSON gets a stored-data diagnostic.</summary>
    [TestMethod]
    public async Task MalformedProtectedJson_FailsWithStoredDataException( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        _ = await store.CreateService( ).UpdateAsync( CreateInitialUpdate( ), TestContext.CancellationToken );
        await store.ReplaceRawSecretsAsync(
            store.ProtectSettingsPlaintext( "not-json" ),
            TestContext.CancellationToken
        );

        ApplicationSettingsDataException exception = await Assert.ThrowsAsync<ApplicationSettingsDataException>(
            ( ) => store.CreateService( ).GetRuntimeSettingsAsync( TestContext.CancellationToken )
        );

        _ = Assert.IsInstanceOfType<JsonException>( exception.InnerException );
        Assert.Contains( "malformed", exception.Message );
        Assert.DoesNotContain( "original persistent key ring", exception.Message );
    }

    /// <summary>Verifies settings ciphertext cannot be decrypted with the user-token purpose.</summary>
    [TestMethod]
    public async Task ProtectedSecrets_UsesPurposeIsolatedProtector( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        _ = await store.CreateService( ).UpdateAsync( CreateInitialUpdate( ), TestContext.CancellationToken );
        string ciphertext = await store.ReadRawSecretsAsync( TestContext.CancellationToken );

        _ = Assert.ThrowsExactly<CryptographicException>( ( ) => store.UnprotectWithTokenPurpose( ciphertext ) );
        string plaintext = store.UnprotectWithSettingsPurpose( ciphertext );

        Assert.Contains( SpotifySecret, plaintext );
    }

    /// <summary>Verifies an unknown protection marker blocks both reads and updates without dispatch.</summary>
    [TestMethod]
    public async Task UnsupportedProtectionScheme_BlocksReadAndUpdate( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        DatabaseApplicationSettingsService service = store.CreateService( );
        ApplicationSettingsStatus original = await service.UpdateAsync(
            CreateInitialUpdate( ),
            TestContext.CancellationToken
        );
        await store.ReplaceProtectionSchemeAsync( "plaintext:v1", TestContext.CancellationToken );

        _ = await Assert.ThrowsAsync<ApplicationSettingsDataException>(
            ( ) => service.GetRuntimeSettingsAsync( TestContext.CancellationToken )
        );
        _ = await Assert.ThrowsAsync<ApplicationSettingsDataException>(
            ( ) => service.UpdateAsync( new ApplicationSettingsUpdate {
                Values = CreateValues( ),
                ExpectedRevision = original.Revision
            }, TestContext.CancellationToken )
        );

        Assert.AreEqual( "plaintext:v1", await store.ReadProtectionSchemeAsync( TestContext.CancellationToken ) );
    }

    /// <summary>Verifies an unsupported schema blocks both reads and updates without rewriting the row.</summary>
    [TestMethod]
    public async Task UnsupportedSchema_BlocksReadAndUpdate( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        DatabaseApplicationSettingsService service = store.CreateService( );
        ApplicationSettingsStatus original = await service.UpdateAsync(
            CreateInitialUpdate( ),
            TestContext.CancellationToken
        );
        await store.ReplaceSchemaVersionAsync( 2, TestContext.CancellationToken );

        _ = await Assert.ThrowsAsync<ApplicationSettingsDataException>(
            ( ) => service.GetRuntimeSettingsAsync( TestContext.CancellationToken )
        );
        _ = await Assert.ThrowsAsync<ApplicationSettingsDataException>(
            ( ) => service.UpdateAsync( new ApplicationSettingsUpdate {
                Values = CreateValues( ),
                ExpectedRevision = original.Revision
            }, TestContext.CancellationToken )
        );

        Assert.AreEqual( 2, await store.ReadSchemaVersionAsync( TestContext.CancellationToken ) );
    }

    /// <summary>Verifies malformed deserialized update graphs fail validation before database access.</summary>
    [TestMethod]
    public async Task NullUpdateGraph_FailsWithValidationException( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        ApplicationSettingsUpdate? malformed = JsonSerializer.Deserialize<ApplicationSettingsUpdate>(
            """{"values":{"queue":null},"secrets":null}""",
            s_webJsonOptions
        );
        Assert.IsNotNull( malformed );

        ApplicationSettingsValidationException exception =
            await Assert.ThrowsAsync<ApplicationSettingsValidationException>(
                ( ) => store.CreateService( ).UpdateAsync( malformed, TestContext.CancellationToken )
            );

        Assert.Contains( "Queue cannot be null.", exception.Errors );
        Assert.Contains( "Secrets cannot be null.", exception.Errors );
        Assert.IsFalse( await store.SettingsRowExistsAsync( TestContext.CancellationToken ) );
    }

    /// <summary>Verifies stale revisions are rejected without overwriting a successful update.</summary>
    [TestMethod]
    public async Task StaleRevision_IsRejectedWithoutPartialUpdate( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        DatabaseApplicationSettingsService service = store.CreateService( );
        ApplicationSettingsStatus original = await service.UpdateAsync(
            CreateInitialUpdate( ),
            TestContext.CancellationToken
        );

        ApplicationSettingsUpdate firstUpdate = new( ) {
            Values = CreateValues( ) with { CacheDays = 14 },
            ExpectedRevision = original.Revision
        };
        ApplicationSettingsStatus firstStatus = await service.UpdateAsync( firstUpdate, TestContext.CancellationToken );
        ApplicationSettingsUpdate staleUpdate = new( ) {
            Values = CreateValues( ) with { CacheDays = 30 },
            ExpectedRevision = original.Revision
        };

        _ = await Assert.ThrowsAsync<ApplicationSettingsConcurrencyException>(
            ( ) => service.UpdateAsync( staleUpdate, TestContext.CancellationToken )
        );
        ApplicationSettingsSnapshot? runtime = await service.GetRuntimeSettingsAsync( TestContext.CancellationToken );

        Assert.IsNotNull( runtime );
        Assert.AreEqual( 14, runtime.Values.CacheDays );
        Assert.AreEqual( firstStatus.Revision, runtime.Revision );
        Assert.AreEqual( SpotifySecret, runtime.Secrets.SpotifyClientSecret.Reveal( ) );
    }

    /// <summary>Verifies a database-detected optimistic conflict is mapped to the typed contract.</summary>
    [TestMethod]
    public async Task SaveConcurrencyFailure_IsMappedWithoutOverwriting( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        ApplicationSettingsStatus original = await store.CreateService( ).UpdateAsync(
            CreateInitialUpdate( ),
            TestContext.CancellationToken
        );
        DatabaseApplicationSettingsService conflictingService = store.CreateServiceWithInterceptor(
            new ThrowingConcurrencyInterceptor( )
        );

        _ = await Assert.ThrowsAsync<ApplicationSettingsConcurrencyException>(
            ( ) => conflictingService.UpdateAsync( new ApplicationSettingsUpdate {
                Values = CreateValues( ) with { CacheDays = 99 },
                ExpectedRevision = original.Revision
            }, TestContext.CancellationToken )
        );
        ApplicationSettingsSnapshot? unchanged = await store.CreateService( ).GetRuntimeSettingsAsync(
            TestContext.CancellationToken
        );

        Assert.AreEqual( 7, unchanged?.Values.CacheDays );
        Assert.AreEqual( original.Revision, unchanged?.Revision );
    }

    /// <summary>Verifies keep and remove operations cannot accidentally disclose or overwrite secrets.</summary>
    [TestMethod]
    public async Task SecretUpdate_KeepThenRemove_HasExplicitSemantics( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        DatabaseApplicationSettingsService service = store.CreateService( );
        ApplicationSettingsStatus original = await service.UpdateAsync(
            CreateInitialUpdate( ),
            TestContext.CancellationToken
        );

        ApplicationSettingsStatus kept = await service.UpdateAsync( new ApplicationSettingsUpdate {
            Values = CreateValues( ) with { CacheDays = 8 },
            ExpectedRevision = original.Revision
        }, TestContext.CancellationToken );
        ApplicationSettingsSnapshot? afterKeep = await service.GetRuntimeSettingsAsync( TestContext.CancellationToken );
        Assert.AreEqual( SpotifySecret, afterKeep?.Secrets.SpotifyClientSecret.Reveal( ) );

        ApplicationSettingsStatus removed = await service.UpdateAsync( new ApplicationSettingsUpdate {
            Values = CreateValues( ) with { SpotifyClientId = string.Empty },
            Secrets = new ApplicationSettingsSecretUpdates {
                SpotifyClientSecret = ApplicationSecretUpdate.Remove( )
            },
            ExpectedRevision = kept.Revision
        }, TestContext.CancellationToken );
        ApplicationSettingsSnapshot? afterRemove = await service.GetRuntimeSettingsAsync( TestContext.CancellationToken );

        Assert.IsFalse( removed.Secrets.SpotifyClientSecretConfigured );
        Assert.IsNull( afterRemove?.Secrets.SpotifyClientSecret.Reveal( ) );
    }

    /// <summary>Verifies invalid updates do not create a partial settings row.</summary>
    [TestMethod]
    public async Task InvalidUpdate_DoesNotWriteSettingsRecord( ) {
        await using TestStore store = await TestStore.CreateAsync( TestContext.CancellationToken );
        DatabaseApplicationSettingsService service = store.CreateService( );
        ApplicationSettingsUpdate invalid = new( ) {
            Values = CreateValues( ) with { CardCacheMaxEntries = 0 },
            Secrets = new ApplicationSettingsSecretUpdates {
                SpotifyClientSecret = ApplicationSecretUpdate.Replace( SpotifySecret )
            }
        };

        ApplicationSettingsValidationException exception = await Assert.ThrowsAsync<ApplicationSettingsValidationException>(
            ( ) => service.UpdateAsync( invalid, TestContext.CancellationToken )
        );

        Assert.Contains(
            error => error.Contains( "CardCacheMaxEntries", StringComparison.Ordinal ),
            exception.Errors
        );
        Assert.IsNull( await service.GetStatusAsync( TestContext.CancellationToken ) );
    }

    private static ApplicationSettingsUpdate CreateInitialUpdate( ) => new( ) {
        Values = CreateValues( ),
        Secrets = new ApplicationSettingsSecretUpdates {
            SpotifyClientSecret = ApplicationSecretUpdate.Replace( SpotifySecret )
        }
    };

    private static ApplicationSettingsValues CreateValues( ) => new( ) {
        SpotifyClientId = "spotify-client-id"
    };

    private sealed class TestDbContextFactory( DbContextOptions<ApplicationDbContext> options )
        : IDbContextFactory<ApplicationDbContext> {
        public ApplicationDbContext CreateDbContext( ) => new( options );
    }

    private sealed class ThrowingConcurrencyInterceptor : SaveChangesInterceptor {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        ) => throw new DbUpdateConcurrencyException( "Injected settings concurrency conflict." );
    }

    private sealed class TestStore : IAsyncDisposable {
        private readonly string _directory;
        private readonly string _dbPath;
        private readonly DbContextOptions<ApplicationDbContext> _options;
        private readonly IDataProtectionProvider _provider;

        private TestStore(
            string directory,
            string dbPath,
            string keyPath,
            DbContextOptions<ApplicationDbContext> options,
            IDataProtectionProvider provider
        ) {
            _directory = directory;
            _dbPath = dbPath;
            KeyPath = keyPath;
            _options = options;
            _provider = provider;
        }

        public string KeyPath { get; }

        public static async Task<TestStore> CreateAsync( CancellationToken cancellationToken ) {
            string directory = Path.Combine( Path.GetTempPath( ), $"bb-settings-{Guid.NewGuid( ):N}" );
            string keyPath = Path.Combine( directory, "keys" );
            string dbPath = Path.Combine( directory, "settings.db" );
            _ = Directory.CreateDirectory( directory );

            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>( )
                .UseSqlite( $"Data Source={dbPath}" )
                .Options;
            IDataProtectionProvider provider = BuildProvider( keyPath );
            await using ApplicationDbContext context = new( options );
            await context.Database.MigrateAsync( cancellationToken );

            return new TestStore( directory, dbPath, keyPath, options, provider );
        }

        public DatabaseApplicationSettingsService CreateService( ) => CreateService( _provider );

        public DatabaseApplicationSettingsService CreateServiceWithNewProvider( string keyPath )
            => CreateService( BuildProvider( keyPath ) );

        public bool HasPendingModelChanges( ) {
            using ApplicationDbContext context = new( _options );
            return context.Database.HasPendingModelChanges( );
        }

        public DatabaseApplicationSettingsService CreateServiceWithInterceptor( SaveChangesInterceptor interceptor ) {
            DbContextOptions<ApplicationDbContext> interceptedOptions =
                new DbContextOptionsBuilder<ApplicationDbContext>( _options )
                    .AddInterceptors( interceptor )
                    .Options;
            return new DatabaseApplicationSettingsService(
                new TestDbContextFactory( interceptedOptions ),
                TimeProvider.System,
                _provider
            );
        }

        public async Task<string> ReadRawSecretsAsync( CancellationToken cancellationToken ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = "SELECT ProtectedSecrets FROM ApplicationSettings WHERE Id = 1";
            object? result = await command.ExecuteScalarAsync( cancellationToken );
            return Assert.IsInstanceOfType<string>( result );
        }

        public Task<string> ReadRawValuesAsync( CancellationToken cancellationToken )
            => ReadStringColumnAsync( "ValuesJson", cancellationToken );

        public Task<string> ReadProtectionSchemeAsync( CancellationToken cancellationToken )
            => ReadStringColumnAsync( "SecretsProtectionScheme", cancellationToken );

        public async Task<int> ReadSchemaVersionAsync( CancellationToken cancellationToken ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = "SELECT SchemaVersion FROM ApplicationSettings WHERE Id = 1";
            object? result = await command.ExecuteScalarAsync( cancellationToken );
            return Convert.ToInt32( result, System.Globalization.CultureInfo.InvariantCulture );
        }

        public async Task<bool> SettingsRowExistsAsync( CancellationToken cancellationToken ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = "SELECT COUNT(*) FROM ApplicationSettings";
            object? result = await command.ExecuteScalarAsync( cancellationToken );
            return Convert.ToInt64( result, System.Globalization.CultureInfo.InvariantCulture ) > 0;
        }

        public async Task ReplaceRawSecretsAsync( string value, CancellationToken cancellationToken ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = "UPDATE ApplicationSettings SET ProtectedSecrets = $value WHERE Id = 1";
            _ = command.Parameters.AddWithValue( "$value", value );
            Assert.AreEqual( 1, await command.ExecuteNonQueryAsync( cancellationToken ) );
        }

        public Task ReplaceProtectionSchemeAsync( string value, CancellationToken cancellationToken )
            => ReplaceStringColumnAsync( "SecretsProtectionScheme", value, cancellationToken );

        public async Task ReplaceSchemaVersionAsync( int value, CancellationToken cancellationToken ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = "UPDATE ApplicationSettings SET SchemaVersion = $value WHERE Id = 1";
            _ = command.Parameters.AddWithValue( "$value", value );
            Assert.AreEqual( 1, await command.ExecuteNonQueryAsync( cancellationToken ) );
        }

        public string ProtectSettingsPlaintext( string plaintext )
            => _provider.CreateProtector( DatabaseApplicationSettingsService.ProtectorPurpose ).Protect( plaintext );

        public string UnprotectWithSettingsPurpose( string ciphertext )
            => _provider.CreateProtector( DatabaseApplicationSettingsService.ProtectorPurpose ).Unprotect( ciphertext );

        public string UnprotectWithTokenPurpose( string ciphertext )
            => _provider.CreateProtector( ApplicationDbContext.TokenProtectorPurpose ).Unprotect( ciphertext );

        public ValueTask DisposeAsync( ) {
            if (Directory.Exists( _directory )) {
                Directory.Delete( _directory, recursive: true );
            }

            return ValueTask.CompletedTask;
        }

        private DatabaseApplicationSettingsService CreateService( IDataProtectionProvider provider ) {
            TestDbContextFactory factory = new( _options );
            return new DatabaseApplicationSettingsService( factory, TimeProvider.System, provider );
        }

        private async Task<string> ReadStringColumnAsync(
            string columnName,
            CancellationToken cancellationToken
        ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = $"SELECT {columnName} FROM ApplicationSettings WHERE Id = 1";
            object? result = await command.ExecuteScalarAsync( cancellationToken );
            return Assert.IsInstanceOfType<string>( result );
        }

        private async Task ReplaceStringColumnAsync(
            string columnName,
            string value,
            CancellationToken cancellationToken
        ) {
            await using SqliteConnection connection = new( $"Data Source={_dbPath}" );
            await connection.OpenAsync( cancellationToken );
            await using SqliteCommand command = connection.CreateCommand( );
            command.CommandText = $"UPDATE ApplicationSettings SET {columnName} = $value WHERE Id = 1";
            _ = command.Parameters.AddWithValue( "$value", value );
            Assert.AreEqual( 1, await command.ExecuteNonQueryAsync( cancellationToken ) );
        }

        private static IDataProtectionProvider BuildProvider( string keyPath ) {
            ServiceCollection services = new( );
            _ = services.AddBridgeBeatsDataProtection( keyPath );
            return services.BuildServiceProvider( ).GetRequiredService<IDataProtectionProvider>( );
        }
    }
}
