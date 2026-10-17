using DashboardController = BridgeBeats.Web.Controllers.DashboardController;
using HealthEndpointAuthorizationMiddleware = BridgeBeats.Web.Middleware.HealthEndpointAuthorizationMiddleware;
using RateLimitingMiddleware = BridgeBeats.Web.Middleware.RateLimitingMiddleware;

namespace BridgeBeats.Web.Logging;

/// <summary>
/// Centralized logging event identifiers for Web layer components (4000-4999), grouped by the area that
/// emits them. Each constant is a stable numeric event id used by the source-generated
/// <see cref="LoggerMessage"/> definitions across the web application. Extends
/// <see cref="Core.Infrastructure.Logging.LogEventIds"/> with Web-specific event ids.
/// </summary>
/// <remarks>
/// Range Allocation:
/// <list type="bullet">
///   <item>4000-4499: Controllers</item>
///   <item>4500-4749: Middleware</item>
///   <item>4750-4899: Configuration</item>
///   <item>4900-4999: BackgroundServices</item>
/// </list>
/// </remarks>
public static class LogEventIds {
    /// <summary>
    /// Event identifiers emitted by the web controllers (4000-4499), allocated in per-controller numeric blocks.
    /// </summary>
    public static class Controllers {
        // AccountController (4000-4049)

        /// <summary>A new account was registered.</summary>
        public const int AccountControllerRegistered = 4000;

        /// <summary>A user logged in.</summary>
        public const int AccountControllerLoggedIn = 4001;

        /// <summary>A user's API key was regenerated.</summary>
        public const int AccountControllerApiKeyRegenerated = 4002;

        /// <summary>A user downloaded their personal data.</summary>
        public const int AccountControllerDataDownloaded = 4003;

        /// <summary>An account deletion attempt failed.</summary>
        public const int AccountControllerDeleteFailed = 4004;

        /// <summary>An account was deleted.</summary>
        public const int AccountControllerDeleted = 4005;

        /// <summary>An ATProto OAuth authorization flow was started.</summary>
        public const int AccountControllerAtProtoOAuthStarted = 4006;

        /// <summary>Starting an ATProto OAuth authorization flow failed.</summary>
        public const int AccountControllerAtProtoOAuthStartFailed = 4007;

        /// <summary>An ATProto OAuth flow returned an error.</summary>
        public const int AccountControllerAtProtoOAuthError = 4008;

        /// <summary>Creating a user from an ATProto identity failed.</summary>
        public const int AccountControllerAtProtoUserCreateFailed = 4009;

        /// <summary>A user was created from an ATProto identity.</summary>
        public const int AccountControllerAtProtoUserCreated = 4010;

        /// <summary>A user's ATProto tokens were updated.</summary>
        public const int AccountControllerAtProtoTokensUpdated = 4011;

        /// <summary>A user logged in via ATProto.</summary>
        public const int AccountControllerAtProtoLoggedIn = 4012;

        /// <summary>Handling the ATProto OAuth callback failed.</summary>
        public const int AccountControllerAtProtoCallbackFailed = 4013;

        // HomeController (4050-4074)

        /// <summary>A cache operation raised an invalid-operation error.</summary>
        public const int HomeControllerCacheInvalidOp = 4050;

        /// <summary>A cache operation raised an argument error.</summary>
        public const int HomeControllerCacheArgError = 4051;

        /// <summary>A cache operation raised an unexpected error.</summary>
        public const int HomeControllerCacheError = 4052;

        /// <summary>Streaming a single lookup result failed.</summary>
        public const int HomeControllerStreamResultError = 4053;

        /// <summary>Streaming lookup results failed.</summary>
        public const int HomeControllerStreamError = 4054;

        /// <summary>A client disconnected while lookup results were being streamed.</summary>
        public const int HomeControllerStreamClientDisconnected = 4055;

        // DashboardController (4075-4099)

        /// <summary>
        /// Authorization for the Aspire dashboard failed because the user could not be found.
        /// </summary>
        public const int DashboardControllerAuthorizationFailedUserNotFound = 4075;

        /// <summary>
        /// Authorization for the Aspire dashboard was denied because the user lacks the required role.
        /// </summary>
        public const int DashboardControllerAuthorizationDeniedMissingRole = 4076;

        /// <summary>
        /// Authorization for the Aspire dashboard was granted.
        /// </summary>
        public const int DashboardControllerAuthorizationGranted = 4077;

        // MusicLookupController (4100-4149)

        // OpenGraphCardController (4150-4174)

        /// <summary>An Open Graph card cache operation raised an invalid-operation error.</summary>
        public const int OpenGraphCardControllerCacheInvalidOp = 4150;

        /// <summary>An Open Graph card cache operation raised an argument error.</summary>
        public const int OpenGraphCardControllerCacheArgError = 4151;

        /// <summary>An Open Graph card cache operation raised an unexpected error.</summary>
        public const int OpenGraphCardControllerCacheError = 4152;

        // PlaylistController (4200-4249)

        /// <summary>Creating a playlist failed.</summary>
        public const int PlaylistControllerCreateError = 4200;

        /// <summary>A playlist create request supplied mismatched card identifiers and record keys.</summary>
        public const int PlaylistControllerMismatchedCards = 4201;

        /// <summary>A playlist card was regenerated.</summary>
        public const int PlaylistControllerCardRegenerated = 4202;

        /// <summary>Loading a playlist card failed.</summary>
        public const int PlaylistControllerCardLoadFailed = 4203;

        /// <summary>A playlist embed card was regenerated.</summary>
        public const int PlaylistControllerEmbedCardRegenerated = 4204;

        /// <summary>Loading a playlist embed card failed.</summary>
        public const int PlaylistControllerEmbedCardLoadFailed = 4205;

        // PlaylistsController (4275-4299)

        /// <summary>Loading the user's playlists failed.</summary>
        public const int PlaylistsControllerLoadError = 4275;

        /// <summary>Deleting a playlist failed.</summary>
        public const int PlaylistsControllerDeleteError = 4276;

        // StatisticsController (4300-4324)

        /// <summary>The statistics service was unavailable.</summary>
        public const int StatisticsControllerNotAvailable = 4300;

        /// <summary>Retrieving statistics failed.</summary>
        public const int StatisticsControllerRetrieveError = 4301;

        /// <summary>Refreshing statistics failed.</summary>
        public const int StatisticsControllerRefreshError = 4302;

        /// <summary>A manual refresh request was suppressed by the per-admin throttle.</summary>
        public const int StatisticsControllerRefreshThrottled = 4303;

        // RefreshReview disposition (4325-4349)

        /// <summary>An operator completed a refresh-review disposition.</summary>
        public const int RefreshReviewDisposition = 4325;

        /// <summary>An operator's refresh-review disposition failed.</summary>
        public const int RefreshReviewDispositionFailed = 4326;

        /// <summary>A refresh-review entry no longer matched the expected saga/CID (already removed or replaced), so its cleanup was skipped.</summary>
        public const int RefreshReviewDispositionCleanupSkipped = 4327;

        // AppleMusicController (4250-4274)

        /// <summary>Storing an Apple Music user token failed.</summary>
        public const int AppleMusicControllerStoreTokenFailed = 4250;

        /// <summary>An Apple Music user token was stored.</summary>
        public const int AppleMusicControllerTokenStored = 4251;

        /// <summary>Fetching Apple Music playlists failed.</summary>
        public const int AppleMusicControllerGetPlaylistsFailed = 4252;

        /// <summary>Fetching Apple Music playlists raised an error.</summary>
        public const int AppleMusicControllerGetPlaylistsError = 4253;

        /// <summary>An Apple Music cache operation raised an invalid-operation error.</summary>
        public const int AppleMusicControllerCacheInvalidOp = 4254;

        /// <summary>An Apple Music cache operation raised an argument error.</summary>
        public const int AppleMusicControllerCacheArgError = 4255;

        /// <summary>An Apple Music cache operation raised an unexpected error.</summary>
        public const int AppleMusicControllerCacheError = 4256;

        /// <summary>Fetching Apple Music playlist tracks failed.</summary>
        public const int AppleMusicControllerGetTracksFailed = 4257;

        /// <summary>An Apple Music playlist exceeded the supported size.</summary>
        public const int AppleMusicControllerPlaylistTooLarge = 4258;

        /// <summary>Processing an Apple Music playlist failed.</summary>
        public const int AppleMusicControllerProcessPlaylistError = 4259;

        /// <summary>Processing an Apple Music song failed.</summary>
        public const int AppleMusicControllerProcessSongError = 4260;

        /// <summary>Unexpected failure while writing the playlist result stream.</summary>
        public const int AppleMusicControllerPlaylistResultsStreamError = 4261;
    }

    /// <summary>
    /// Event identifiers emitted by Web services (4400-4499).
    /// </summary>
    public static class Services {
        // RedisStatisticsReader (4400-4424)

        /// <summary>Reading the <c>status:statistics</c> Redis key failed.</summary>
        public const int RedisStatisticsReaderStatusReadError = 4400;

        /// <summary>Reading the <c>status:cache-bootstrap</c> Redis key failed.</summary>
        public const int RedisStatisticsReaderBootstrapStatusReadError = 4401;

        /// <summary>Publishing to the statistics-refresh Pub/Sub channel failed.</summary>
        public const int RedisStatisticsReaderPublishError = 4402;
    }

    /// <summary>
    /// Event identifiers emitted by the request-pipeline middleware (4500-4749).
    /// </summary>
    public static class Middleware {
        // RateLimitingMiddleware (4500-4524)

        /// <summary>
        /// A user exceeded the hourly request quota in <see cref="RateLimitingMiddleware"/>.
        /// </summary>
        public const int RateLimitingMiddlewareRateLimitExceeded = 4500;

        // 4501 retired (concurrent-check path removed)

        // HealthEndpointAuthorizationMiddleware (4525-4549)

        /// <summary>
        /// An external caller was blocked from the liveness endpoint by
        /// <see cref="HealthEndpointAuthorizationMiddleware"/>.
        /// </summary>
        public const int HealthEndpointAuthorizationMiddlewareBlockedExternalAccess = 4525;

        // ApiKeyAuthenticationMiddleware (4550-4574)

        // ExceptionHandlingMiddleware (4600-4624)
    }

    /// <summary>
    /// Event identifiers emitted during application startup and configuration (4750-4899).
    /// </summary>
    public static class Configuration {
        // StartupExtensions (4750-4774)

        /// <summary>Redis was not configured, so caching is unavailable.</summary>
        public const int StartupExtensionsRedisNotConfigured = 4750;

        /// <summary>The Redis cache connection was established.</summary>
        public const int StartupExtensionsRedisConnected = 4751;

        /// <summary>Initializing the Redis cache connection failed.</summary>
        public const int StartupExtensionsRedisFailed = 4752;
    }
}
