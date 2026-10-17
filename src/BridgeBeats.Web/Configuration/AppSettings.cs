using BridgeBeats.Core.Infrastructure.Extensions;

namespace BridgeBeats.Web.Configuration {
    /// <summary>
    /// Represents application settings for external service integrations.<para/>
    ///
    /// At least one music provider (Apple Music, Spotify, or Tidal) API credentials are required for the application to function properly.
    /// A Discord bot token is required for Discord integrations.<para/>
    ///
    /// Apple Music API credentials can be obtained from the Apple Developer portal.
    /// See <seealso href="https://developer.apple.com/help/account/configure-app-capabilities/create-a-media-identifier-and-private-key/">this link</seealso>
    /// for more details. <para/>
    ///
    /// Spotify API credentials can be obtained by creating an app in the Spotify Developer Dashboard.
    /// See <seealso href="https://developer.spotify.com/documentation/general/guides/app-settings/#register-your-app">this link</seealso>
    /// for more details. <para/>
    ///
    /// Tidal API credentials can be obtained by creating an app in the Tidal Developer Portal.
    /// See <seealso href="https://developer.tidal.com/">this link</seealso>
    /// for more details. <para/>
    ///
    /// A Discord bot token can be obtained by creating an app in the Discord Developer Portal.
    /// See <seealso href="https://discord.com/developers/docs/quick-start/getting-started">this link</seealso>
    /// for more details.
    /// </summary>
    internal class AppSettings {
        /// <summary>
        /// The Apple Developer team identifier used to sign Apple Music API requests.
        /// </summary>
        public string AppleTeamId { get; set; } = string.Empty;

        /// <summary>
        /// The Apple Music key identifier paired with the signing key.
        /// </summary>
        public string AppleKeyId { get; set; } = string.Empty;

        /// <summary>
        /// Filesystem path to the Apple Music private signing key (<c>.p8</c>) file.
        /// </summary>
        public string AppleKeyPath { get; set; } = string.Empty;

        /// <summary>
        /// The Spotify application client identifier.
        /// </summary>
        public string SpotifyClientId { get; set; } = string.Empty;

        /// <summary>
        /// The Spotify application client secret.
        /// </summary>
        public string SpotifyClientSecret { get; set; } = string.Empty;

        /// <summary>
        /// The Tidal application client identifier.
        /// </summary>
        public string TidalClientId { get; set; } = string.Empty;

        /// <summary>
        /// The Tidal application client secret.
        /// </summary>
        public string TidalClientSecret { get; set; } = string.Empty;

        /// <summary>
        /// The connection string for the Identity database. Defaults to a local SQLite file.
        /// </summary>
        public string IdentityConnectionString { get; set; } = "Data Source=bridgebeats.db";

        /// <summary>
        /// The salt used to hash API keys. Required at startup; an empty value causes configuration to fail.
        /// </summary>
        public string ApiKeySalt { get; set; } = string.Empty;

        /// <summary>
        /// The shared key that internal services present to authenticate to the web API. When empty, the
        /// internal-service scheme rejects all callers. Used by worker services (for example, the Discord
        /// worker) to authenticate without a user API key.
        /// </summary>
        public string InternalServiceKey { get; set; } = string.Empty;

        /// <summary>
        /// The maximum number of rate-limited lookup requests permitted per user per hour. Defaults to 20.
        /// </summary>
        public int RateLimitRequestsPerHour { get; set; } = 20;

        /// <summary>
        /// The ATProto identifier (handle) for the BridgeBeats service account.
        /// </summary>
        public string ATProtoIdentifier { get; set; } = string.Empty;

        /// <summary>
        /// The password for the BridgeBeats ATProto service account.
        /// </summary>
        public string ATProtoPassword { get; set; } = string.Empty;

        /// <summary>
        /// The DID of the ATProto user whose records are read and written. Validated at startup when ATProto is enabled.
        /// </summary>
        public string ATProtoUserDID { get; set; } = string.Empty;

        /// <summary>
        /// The base URI of the ATProto personal data server. Defaults to the BridgeBeats PDS.
        /// </summary>
        public string? ATProtoPdsUri { get; set; } = "https://pds.bridgebeats.link";

        /// <summary>
        /// The number of days a cached media-link result is considered fresh before it is treated as stale. Defaults to 7.
        /// </summary>
        public int CacheDays { get; set; } = 7;

        /// <summary>
        /// The public domain the application is served from, used for cookie scoping, ATProto client
        /// metadata, and generating Open Graph card URLs.
        /// </summary>
        public string Domain { get; set; } = string.Empty;

        /// <summary>
        /// Normalizes a domain value into a scheme-qualified URL by trimming a trailing slash and
        /// prefixing <c>https://</c> when the value does not already start with <c>https://</c>.
        /// Only the literal prefix <c>https://</c> is recognized; a value that starts with
        /// <c>http://</c> is not treated as scheme-present and will be double-prefixed
        /// (becoming <c>https://http://…</c>). In practice <see cref="Domain"/> holds a bare
        /// hostname, so the double-prefix case does not arise at runtime today.
        /// </summary>
        /// <param name="domain">The raw domain value to normalize.</param>
        /// <returns>The normalized <c>https://</c> URL, or <c>null</c> when the input is null or whitespace.</returns>
        public static string? NormalizeDomain( string? domain ) {
            if (string.IsNullOrWhiteSpace( domain )) {
                return null;
            }

            string trimmed = domain.TrimEnd( '/' );
            return trimmed.StartsWith( "https://", StringComparison.OrdinalIgnoreCase )
                ? trimmed
                : $"https://{trimmed}";
        }

        /// <summary>
        /// Filesystem path to the JWK or PEM file holding the ATProto OAuth client signing key. When the
        /// file is absent, empty, or <c>{}</c>, client-assertion signing is not configured and the ATProto
        /// OAuth service operates as a public client.
        /// </summary>
        public string ATProtoOAuthSigningKeyPath { get; set; } = string.Empty;

        /// <summary>Previous OAuth signing keys retained for refresh and revocation of existing sessions.</summary>
        public string[] ATProtoOAuthAdditionalSigningKeyPaths { get; set; } = [];


        /// <summary>
        /// Directory where application log files are written. Defaults to <c>./logs</c>.
        /// </summary>
        public string LogDirPath { get; set; } = "./logs";

        /// <summary>
        /// Directory where ASP.NET Data Protection keys are persisted. Defaults to <c>./keys</c>.
        /// Keys must survive container restarts to decrypt Identity personal data fields, so in Docker
        /// this should be a mounted volume (for example, <c>/app/keys</c>).
        /// </summary>
        public string DataProtectionKeyPath { get; set; } = DataProtectionExtensions.DefaultKeyPath;

        /// <summary>
        /// The number of days a service-account ATProto Redis session is retained before it expires and
        /// triggers a fresh login. Defaults to 45, matching a typical OAuth refresh-token lifetime. Every
        /// successful persist resets the TTL, so an active session never expires; only a dormant one ages out.
        /// </summary>
        public int ATProtoSessionTtlDays { get; set; } = 45;

        /// <summary>
        /// The number of hours a generated Open Graph card is cached before expiring. Defaults to 1.
        /// </summary>
        public int CardCacheExpirationHours { get; set; } = 1;

        /// <summary>
        /// The interval, expressed as a number of cached entries, between card-cache cleanup passes. Defaults to 500.
        /// </summary>
        public int CardCacheCleanupInterval { get; set; } = 500;

        /// <summary>
        /// The maximum number of cards retained in the in-memory store before nearest-expiry eviction begins. Defaults to 25000.
        /// </summary>
        public int CardCacheMaxEntries { get; set; } = 25000;

        /// <summary>
        /// HTTP resilience settings (retry and timeout limits) applied to outbound provider calls.
        /// </summary>
        public ResilienceSettings Resilience { get; set; } = new( );

        /// <summary>
        /// Worker enablement flags that select between in-process provider services and remote worker
        /// HTTP clients, and toggle individual providers.
        /// </summary>
        public WorkerSettings Workers { get; set; } = new( );

    }

    /// <summary>
    /// Flags that control how music providers are wired: whether the web host calls remote provider
    /// workers over HTTP rather than running provider lookups in-process, and which providers are enabled.
    /// </summary>
    public class WorkerSettings {
        /// <summary>
        /// When <c>true</c>, the web host calls remote provider workers over HTTP using the per-provider
        /// enablement flags below; when <c>false</c>, provider lookups run in-process from the configured credentials.
        /// </summary>
        public bool UseWorkerServices { get; set; }

        /// <summary>
        /// Whether the Spotify provider worker is enabled when <see cref="UseWorkerServices"/> is set.
        /// </summary>
        public bool SpotifyWorkerEnabled { get; set; }

        /// <summary>
        /// Whether the Apple Music provider worker is enabled when <see cref="UseWorkerServices"/> is set.
        /// </summary>
        public bool AppleMusicWorkerEnabled { get; set; }

        /// <summary>
        /// Whether the Tidal provider worker is enabled when <see cref="UseWorkerServices"/> is set.
        /// </summary>
        public bool TidalWorkerEnabled { get; set; }
    }

    /// <summary>
    /// HTTP resilience limits applied to outbound provider calls: the cap honored on a server-supplied
    /// retry-after, the retry count, and the overall and per-attempt timeouts.
    /// </summary>
    public class ResilienceSettings {
        /// <summary>
        /// The maximum number of seconds a server-supplied <c>Retry-After</c> value will be honored before
        /// the request is abandoned. Defaults to 120.
        /// </summary>
        public int MaxRetryAfterSeconds { get; set; } = 120;

        /// <summary>
        /// The maximum number of retry attempts for a failed request. Defaults to 5.
        /// </summary>
        public int MaxRetryAttempts { get; set; } = 5;

        /// <summary>
        /// The total time budget, in minutes, across all attempts for a single logical request. Defaults to 10.
        /// </summary>
        public int TotalTimeoutMinutes { get; set; } = 10;

        /// <summary>
        /// The timeout, in seconds, applied to each individual attempt. Defaults to 10.
        /// </summary>
        public int AttemptTimeoutSeconds { get; set; } = 10;
    }
}
