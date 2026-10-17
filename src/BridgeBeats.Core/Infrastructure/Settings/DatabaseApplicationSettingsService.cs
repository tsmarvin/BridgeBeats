using System.Security.Cryptography;
using System.Text.Json;
using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Exceptions;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BridgeBeats.Core.Infrastructure.Settings;

/// <summary>
/// Stores one versioned application-settings aggregate in SQLite. The service explicitly protects
/// the secret document before persistence, independently of the EF model used by its context factory.
/// </summary>
internal sealed class DatabaseApplicationSettingsService
    : IApplicationSettingsService, IApplicationSettingsRuntimeReader {
    internal const string ProtectorPurpose = "BridgeBeats.ApplicationSettings.Secrets.v1";

    private static readonly JsonSerializerOptions s_jsonOptions = ApplicationSettingsJson.CreateOptions( );
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IDataProtector _settingsProtector;

    /// <summary>Initializes the settings service with mandatory Data Protection.</summary>
    public DatabaseApplicationSettingsService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        TimeProvider timeProvider,
        IDataProtectionProvider dataProtectionProvider
    ) {
        ArgumentNullException.ThrowIfNull( dbContextFactory );
        ArgumentNullException.ThrowIfNull( timeProvider );
        ArgumentNullException.ThrowIfNull( dataProtectionProvider );

        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
        _settingsProtector = dataProtectionProvider.CreateProtector( ProtectorPurpose );
    }

    /// <inheritdoc />
    public async Task<ApplicationSettingsSnapshot?> GetRuntimeSettingsAsync(
        CancellationToken cancellationToken = default
    ) {
        await using ApplicationDbContext context = await _dbContextFactory.CreateDbContextAsync( cancellationToken );
        ApplicationSettingsRecord? record = await ReadRecordAsync( context, tracking: false, cancellationToken );
        return record is null ? null : ToSnapshot( record );
    }

    /// <inheritdoc />
    public async Task<ApplicationSettingsStatus?> GetStatusAsync(
        CancellationToken cancellationToken = default
    ) {
        ApplicationSettingsSnapshot? snapshot = await GetRuntimeSettingsAsync( cancellationToken );
        return snapshot is null ? null : ToStatus( snapshot );
    }

    /// <inheritdoc />
    public async Task<ApplicationSettingsStatus> UpdateAsync(
        ApplicationSettingsUpdate update,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull( update );
        IReadOnlyList<string> graphErrors = ApplicationSettingsValidator.ValidateUpdateGraph( update );
        if (graphErrors.Count > 0) {
            throw new ApplicationSettingsValidationException( graphErrors );
        }

        ApplicationSettingsValues values = update.Values!;
        ApplicationSettingsSecretUpdates secretUpdates = update.Secrets!;

        await using ApplicationDbContext context = await _dbContextFactory.CreateDbContextAsync( cancellationToken );
        ApplicationSettingsRecord? record = await ReadRecordAsync( context, tracking: true, cancellationToken );
        bool isNew = record is null;

        if (!isNew) {
            EnsureSupportedEnvelope( record! );
        }

        if (isNew && update.ExpectedRevision is not null) {
            throw new ApplicationSettingsConcurrencyException( );
        }

        if (!isNew && !string.Equals( update.ExpectedRevision, record!.Revision, StringComparison.Ordinal )) {
            throw new ApplicationSettingsConcurrencyException( );
        }

        StoredApplicationSettingsSecrets currentSecrets = isNew
            ? new( )
            : DeserializeSecrets( record! );
        StoredApplicationSettingsSecrets updatedSecrets = ApplySecretUpdates( currentSecrets, secretUpdates );

        IReadOnlyList<string> validationErrors = ApplicationSettingsValidator.Validate( values, updatedSecrets );
        if (validationErrors.Count > 0) {
            throw new ApplicationSettingsValidationException( validationErrors );
        }

        string revision = Guid.NewGuid( ).ToString( "N" );
        DateTimeOffset updatedAtUtc = _timeProvider.GetUtcNow( );
        string protectedSecrets = ProtectSecrets( updatedSecrets );
        if (isNew) {
            record = new ApplicationSettingsRecord( );
            _ = context.ApplicationSettings.Add( record );
        }

        record!.SchemaVersion = ApplicationSettingsRecord.CurrentSchemaVersion;
        record.ValuesJson = JsonSerializer.Serialize( values, s_jsonOptions );
        record.SecretsProtectionScheme = ApplicationSettingsRecord.CurrentSecretsProtectionScheme;
        record.ProtectedSecrets = protectedSecrets;
        record.Revision = revision;
        record.UpdatedAtUtc = updatedAtUtc;

        try {
            _ = await context.SaveChangesAsync( cancellationToken );
        } catch (DbUpdateConcurrencyException ex) {
            throw new ApplicationSettingsConcurrencyException( ex );
        } catch (DbUpdateException ex) when (
            isNew && ex.InnerException is SqliteException { SqliteErrorCode: 19 }
        ) {
            throw new ApplicationSettingsConcurrencyException( ex );
        }

        return ToStatus( new ApplicationSettingsSnapshot(
            values,
            ToRuntimeSecrets( updatedSecrets ),
            revision,
            updatedAtUtc
        ) );
    }

    private static async Task<ApplicationSettingsRecord?> ReadRecordAsync(
        ApplicationDbContext context,
        bool tracking,
        CancellationToken cancellationToken
    ) {
        IQueryable<ApplicationSettingsRecord> query = context.ApplicationSettings;
        if (!tracking) {
            query = query.AsNoTracking( );
        }

        return await query.SingleOrDefaultAsync(
            record => record.Id == ApplicationSettingsRecord.SingletonId,
            cancellationToken
        );
    }

    private ApplicationSettingsSnapshot ToSnapshot( ApplicationSettingsRecord record ) {
        EnsureSupportedEnvelope( record );

        ApplicationSettingsValues values = DeserializeDocument<ApplicationSettingsValues>(
            record.ValuesJson,
            "values"
        );
        StoredApplicationSettingsSecrets secrets = DeserializeSecrets( record );
        IReadOnlyList<string> storedErrors = ApplicationSettingsValidator.Validate( values, secrets );
        if (storedErrors.Count > 0) {
            throw new ApplicationSettingsDataException(
                new InvalidDataException( $"The stored settings graph is invalid: {string.Join( ' ', storedErrors )}" )
            );
        }

        return new ApplicationSettingsSnapshot(
            values,
            ToRuntimeSecrets( secrets ),
            record.Revision,
            record.UpdatedAtUtc
        );
    }

    private static void EnsureSupportedEnvelope( ApplicationSettingsRecord record ) {
        if (record.SchemaVersion != ApplicationSettingsRecord.CurrentSchemaVersion) {
            throw new ApplicationSettingsDataException(
                new NotSupportedException( $"Unsupported application-settings schema version {record.SchemaVersion}." )
            );
        }

        if (!string.Equals(
                record.SecretsProtectionScheme,
                ApplicationSettingsRecord.CurrentSecretsProtectionScheme,
                StringComparison.Ordinal
            )) {
            throw new ApplicationSettingsDataException(
                new NotSupportedException( "The application-settings protection scheme is unsupported." )
            );
        }
    }

    private string ProtectSecrets( StoredApplicationSettingsSecrets secrets ) {
        string plaintext = JsonSerializer.Serialize( secrets, s_jsonOptions );
        try {
            return _settingsProtector.Protect( plaintext );
        } catch (CryptographicException ex) {
            throw new ApplicationSettingsProtectionException( ex );
        }
    }

    private StoredApplicationSettingsSecrets DeserializeSecrets( ApplicationSettingsRecord record ) {
        string plaintext;
        try {
            plaintext = _settingsProtector.Unprotect( record.ProtectedSecrets );
        } catch (CryptographicException ex) {
            throw new ApplicationSettingsDecryptionException( ex );
        }

        return DeserializeDocument<StoredApplicationSettingsSecrets>( plaintext, "secrets" );
    }

    private static T DeserializeDocument<T>( string json, string documentName ) where T : class {
        try {
            return JsonSerializer.Deserialize<T>( json, s_jsonOptions )
                ?? throw new JsonException( $"The settings {documentName} document was null." );
        } catch (JsonException ex) {
            throw new ApplicationSettingsDataException( ex );
        } catch (NotSupportedException ex) {
            throw new ApplicationSettingsDataException( ex );
        }
    }

    private static StoredApplicationSettingsSecrets ApplySecretUpdates(
        StoredApplicationSettingsSecrets current,
        ApplicationSettingsSecretUpdates updates
    ) => new( ) {
        ApplePrivateKey = ApplySecretUpdate( current.ApplePrivateKey, updates.ApplePrivateKey ),
        SpotifyClientSecret = ApplySecretUpdate( current.SpotifyClientSecret, updates.SpotifyClientSecret ),
        TidalClientSecret = ApplySecretUpdate( current.TidalClientSecret, updates.TidalClientSecret ),
        DiscordToken = ApplySecretUpdate( current.DiscordToken, updates.DiscordToken ),
        ATProtoPassword = ApplySecretUpdate( current.ATProtoPassword, updates.ATProtoPassword ),
        ATProtoOAuthSigningKey = ApplySecretUpdate(
            current.ATProtoOAuthSigningKey,
            updates.ATProtoOAuthSigningKey
        ),
        ApiKeySalt = ApplySecretUpdate( current.ApiKeySalt, updates.ApiKeySalt ),
        InternalServiceKey = ApplySecretUpdate( current.InternalServiceKey, updates.InternalServiceKey )
    };

    private static string? ApplySecretUpdate( string? current, ApplicationSecretUpdate update )
        => update.Mode switch {
            SecretUpdateMode.Keep => current,
            SecretUpdateMode.Replace => update.RevealReplacement( ),
            SecretUpdateMode.Remove => null,
            _ => throw new ApplicationSettingsValidationException(
                [$"Unsupported secret update mode: {update.Mode}."]
            )
        };

    private static ApplicationSettingsSecrets ToRuntimeSecrets( StoredApplicationSettingsSecrets secrets ) => new( ) {
        ApplePrivateKey = ApplicationSecretValue.FromPlaintext( secrets.ApplePrivateKey ),
        SpotifyClientSecret = ApplicationSecretValue.FromPlaintext( secrets.SpotifyClientSecret ),
        TidalClientSecret = ApplicationSecretValue.FromPlaintext( secrets.TidalClientSecret ),
        DiscordToken = ApplicationSecretValue.FromPlaintext( secrets.DiscordToken ),
        ATProtoPassword = ApplicationSecretValue.FromPlaintext( secrets.ATProtoPassword ),
        ATProtoOAuthSigningKey = ApplicationSecretValue.FromPlaintext( secrets.ATProtoOAuthSigningKey ),
        ApiKeySalt = ApplicationSecretValue.FromPlaintext( secrets.ApiKeySalt ),
        InternalServiceKey = ApplicationSecretValue.FromPlaintext( secrets.InternalServiceKey )
    };

    private static ApplicationSettingsStatus ToStatus( ApplicationSettingsSnapshot snapshot ) => new(
        snapshot.Values,
        new ApplicationSettingsSecretStatus {
            ApplePrivateKeyConfigured = snapshot.Secrets.ApplePrivateKey.IsConfigured,
            SpotifyClientSecretConfigured = snapshot.Secrets.SpotifyClientSecret.IsConfigured,
            TidalClientSecretConfigured = snapshot.Secrets.TidalClientSecret.IsConfigured,
            DiscordTokenConfigured = snapshot.Secrets.DiscordToken.IsConfigured,
            ATProtoPasswordConfigured = snapshot.Secrets.ATProtoPassword.IsConfigured,
            ATProtoOAuthSigningKeyConfigured = snapshot.Secrets.ATProtoOAuthSigningKey.IsConfigured,
            ApiKeySaltConfigured = snapshot.Secrets.ApiKeySalt.IsConfigured,
            InternalServiceKeyConfigured = snapshot.Secrets.InternalServiceKey.IsConfigured
        },
        snapshot.Revision,
        snapshot.UpdatedAtUtc
    );
}
