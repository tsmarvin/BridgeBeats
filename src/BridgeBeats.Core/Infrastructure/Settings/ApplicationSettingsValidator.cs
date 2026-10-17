using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Storage;

namespace BridgeBeats.Core.Infrastructure.Settings;

/// <summary>Validates cross-field and numeric invariants before settings reach persistence.</summary>
internal static class ApplicationSettingsValidator {
    internal static IReadOnlyList<string> ValidateUpdateGraph( ApplicationSettingsUpdate update ) {
        List<string> errors = [];
        if (update.Values is null) {
            errors.Add( "Values is required." );
        } else {
            errors.AddRange( ValidateValuesGraph( update.Values ) );
        }

        if (update.Secrets is null) {
            errors.Add( "Secrets cannot be null." );
            return errors;
        }

        RequireSecretUpdate( update.Secrets.ApplePrivateKey, "Secrets.ApplePrivateKey", errors );
        RequireSecretUpdate( update.Secrets.SpotifyClientSecret, "Secrets.SpotifyClientSecret", errors );
        RequireSecretUpdate( update.Secrets.TidalClientSecret, "Secrets.TidalClientSecret", errors );
        RequireSecretUpdate( update.Secrets.DiscordToken, "Secrets.DiscordToken", errors );
        RequireSecretUpdate( update.Secrets.ATProtoPassword, "Secrets.ATProtoPassword", errors );
        RequireSecretUpdate( update.Secrets.ATProtoOAuthSigningKey, "Secrets.ATProtoOAuthSigningKey", errors );
        RequireSecretUpdate( update.Secrets.ApiKeySalt, "Secrets.ApiKeySalt", errors );
        RequireSecretUpdate( update.Secrets.InternalServiceKey, "Secrets.InternalServiceKey", errors );
        return errors;
    }

    internal static IReadOnlyList<string> ValidateValuesGraph( ApplicationSettingsValues values ) {
        List<string> errors = [];
        RequireString( values.AppleTeamId, nameof( values.AppleTeamId ), errors );
        RequireString( values.AppleKeyId, nameof( values.AppleKeyId ), errors );
        RequireString( values.SpotifyClientId, nameof( values.SpotifyClientId ), errors );
        RequireString( values.TidalClientId, nameof( values.TidalClientId ), errors );
        RequireString( values.ATProtoIdentifier, nameof( values.ATProtoIdentifier ), errors );
        RequireString( values.ATProtoUserDid, nameof( values.ATProtoUserDid ), errors );
        RequireString( values.ATProtoPdsUri, nameof( values.ATProtoPdsUri ), errors );
        RequireString( values.Domain, nameof( values.Domain ), errors );

        if (values.Workers is null) {
            errors.Add( "Workers cannot be null." );
        }

        if (values.Resilience is null) {
            errors.Add( "Resilience cannot be null." );
        }

        if (values.Queue is null) {
            errors.Add( "Queue cannot be null." );
        } else {
            if (values.Queue.Weights is null) {
                errors.Add( "Queue.Weights cannot be null." );
            }

            if (values.Queue.ProviderMinBulkThresholds is null) {
                errors.Add( "Queue.ProviderMinBulkThresholds cannot be null." );
            }

            if (values.Queue.ProviderConcurrency is null) {
                errors.Add( "Queue.ProviderConcurrency cannot be null." );
            }
        }

        if (values.Maintenance is null) {
            errors.Add( "Maintenance cannot be null." );
        }

        return errors;
    }

    internal static IReadOnlyList<string> Validate(
        ApplicationSettingsValues values,
        StoredApplicationSettingsSecrets secrets
    ) {
        List<string> errors = [.. ValidateValuesGraph( values )];
        if (errors.Count > 0) {
            return errors;
        }

        RequirePositive( values.RateLimitRequestsPerHour, nameof( values.RateLimitRequestsPerHour ), errors );
        RequirePositive( values.CacheDays, nameof( values.CacheDays ), errors );
        RequirePositive( values.ATProtoSessionTtlDays, nameof( values.ATProtoSessionTtlDays ), errors );
        RequirePositive( values.CardCacheExpirationHours, nameof( values.CardCacheExpirationHours ), errors );
        RequirePositive( values.CardCacheCleanupInterval, nameof( values.CardCacheCleanupInterval ), errors );
        RequirePositive( values.CardCacheMaxEntries, nameof( values.CardCacheMaxEntries ), errors );

        RequirePositive( values.Resilience.MaxRetryAfterSeconds, "Resilience.MaxRetryAfterSeconds", errors );
        RequireNonNegative( values.Resilience.MaxRetryAttempts, "Resilience.MaxRetryAttempts", errors );
        RequirePositive( values.Resilience.TotalTimeoutMinutes, "Resilience.TotalTimeoutMinutes", errors );
        RequirePositive( values.Resilience.AttemptTimeoutSeconds, "Resilience.AttemptTimeoutSeconds", errors );
        if (TimeSpan.FromMinutes( values.Resilience.TotalTimeoutMinutes ) <=
            TimeSpan.FromSeconds( values.Resilience.AttemptTimeoutSeconds )) {
            errors.Add( "Resilience.TotalTimeoutMinutes must exceed Resilience.AttemptTimeoutSeconds." );
        }

        RequirePositive( values.Queue.JobExpirationMinutes, "Queue.JobExpirationMinutes", errors );
        RequirePositive( values.Queue.InteractiveWaitSeconds, "Queue.InteractiveWaitSeconds", errors );
        RequirePositive( values.Queue.InteractiveAgingInterval, "Queue.InteractiveAgingInterval", errors );
        RequireNonNegative( values.Queue.DefaultMinBulkQueueThreshold, "Queue.DefaultMinBulkQueueThreshold", errors );
        if (values.Queue.DefaultProviderConcurrency is < 1 or > 32) {
            errors.Add( "Queue.DefaultProviderConcurrency must be between 1 and 32." );
        }
        if (values.Queue.RateLimitRetryThreshold <= TimeSpan.Zero) {
            errors.Add( "Queue.RateLimitRetryThreshold must be greater than zero." );
        }

        if (values.Queue.ProviderMinBulkThresholds.Any( pair => pair.Value < 0 )) {
            errors.Add( "Queue provider bulk thresholds cannot be negative." );
        }

        if (values.Queue.ProviderMinBulkThresholds.Keys.Any( provider => !Enum.IsDefined( provider ) ) ||
            values.Queue.ProviderConcurrency.Keys.Any( provider => !Enum.IsDefined( provider ) )) {
            errors.Add( "Queue provider overrides contain an unsupported provider." );
        }

        if (values.Queue.ProviderConcurrency.Any( pair => pair.Value is < 1 or > 32 )) {
            errors.Add( "Queue provider concurrency values must be between 1 and 32." );
        }

        RequirePositive( values.Maintenance.BootstrapIntervalHours, "Maintenance.BootstrapIntervalHours", errors );
        RequirePositive( values.Maintenance.RefreshIntervalHours, "Maintenance.RefreshIntervalHours", errors );
        RequirePositive( values.Maintenance.MaxRecordsPerRun, "Maintenance.MaxRecordsPerRun", errors );
        RequirePositive( values.Maintenance.RefreshRetryMinutes, "Maintenance.RefreshRetryMinutes", errors );

        ValidatePair(
            "Spotify",
            values.SpotifyClientId,
            secrets.SpotifyClientSecret,
            errors
        );
        ValidatePair(
            "Tidal",
            values.TidalClientId,
            secrets.TidalClientSecret,
            errors
        );
        ValidateATProtoServiceAccount( values, secrets, errors );

        bool anyAppleValue = HasValue( values.AppleTeamId )
            || HasValue( values.AppleKeyId )
            || HasValue( secrets.ApplePrivateKey );
        bool allAppleValues = HasValue( values.AppleTeamId )
            && HasValue( values.AppleKeyId )
            && HasValue( secrets.ApplePrivateKey );
        if (anyAppleValue && !allAppleValues) {
            errors.Add( "Apple Music requires a team ID, key ID, and private key together." );
        }

        if (!string.IsNullOrWhiteSpace( values.ATProtoPdsUri ) &&
            (!Uri.TryCreate( values.ATProtoPdsUri, UriKind.Absolute, out Uri? pdsUri ) ||
             pdsUri.Scheme != Uri.UriSchemeHttps)) {
            errors.Add( "ATProtoPdsUri must be an absolute HTTPS URI." );
        }

        if (HasValue( secrets.ApiKeySalt ) && secrets.ApiKeySalt!.Length < 32) {
            errors.Add( "ApiKeySalt must contain at least 32 characters." );
        }

        if (HasValue( secrets.InternalServiceKey ) && secrets.InternalServiceKey!.Length < 32) {
            errors.Add( "InternalServiceKey must contain at least 32 characters." );
        }

        return errors;
    }

    private static void ValidateATProtoServiceAccount(
        ApplicationSettingsValues values,
        StoredApplicationSettingsSecrets secrets,
        List<string> errors
    ) {
        bool anyConfigured = HasValue( values.ATProtoIdentifier )
            || HasValue( secrets.ATProtoPassword )
            || HasValue( values.ATProtoUserDid );
        bool allConfigured = HasValue( values.ATProtoIdentifier )
            && HasValue( secrets.ATProtoPassword )
            && HasValue( values.ATProtoUserDid );
        if (anyConfigured && !allConfigured) {
            errors.Add(
                "ATProto service-account identifier, app password, and repository DID must be configured or removed together."
            );
        }

        if (HasValue( values.ATProtoUserDid ) && !ATProtoUriHelper.IsValidDid( values.ATProtoUserDid )) {
            errors.Add( "ATProtoUserDid must start with 'did:plc:' or 'did:web:'." );
        }
    }

    private static void ValidatePair(
        string displayName,
        string identifier,
        string? secret,
        List<string> errors
    ) {
        if (HasValue( identifier ) != HasValue( secret )) {
            errors.Add( $"{displayName} identifier and secret must be configured or removed together." );
        }
    }

    private static bool HasValue( string? value ) => !string.IsNullOrWhiteSpace( value );

    private static void RequireString( string? value, string name, List<string> errors ) {
        if (value is null) {
            errors.Add( $"{name} cannot be null." );
        }
    }

    private static void RequireSecretUpdate(
        ApplicationSecretUpdate? update,
        string name,
        List<string> errors
    ) {
        if (update is null) {
            errors.Add( $"{name} cannot be null." );
        }
    }

    private static void RequirePositive( int value, string name, List<string> errors ) {
        if (value <= 0) {
            errors.Add( $"{name} must be greater than zero." );
        }
    }

    private static void RequireNonNegative( int value, string name, List<string> errors ) {
        if (value < 0) {
            errors.Add( $"{name} cannot be negative." );
        }
    }
}
