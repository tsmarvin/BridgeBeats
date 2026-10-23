// Explicit type aliases to avoid shadowing by nested classes with same names
using ApiKeyProvider = BridgeBeats.Core.Infrastructure.Identity.ApiKeyProvider;
using AppleMusicLookupService = BridgeBeats.Core.Domain.Providers.AppleMusic.AppleMusicLookupService;
using AspireServiceExtensionsLog = BridgeBeats.Core.Domain.Extensions.AspireServiceExtensionsLog;
using ATProtoOAuthService = BridgeBeats.Core.Infrastructure.Identity.ATProtoOAuthService;
using ATProtoStorageService = BridgeBeats.Core.Infrastructure.Storage.ATProtoStorageService;
using CachingMediaLinkService = BridgeBeats.Core.Domain.Services.LinkResolver.CachingMediaLinkService;
using HttpMusicLookupService = BridgeBeats.Core.Domain.Providers.Common.HttpMusicLookupService;
using LookupOrchestrator = BridgeBeats.Services.LinkResolver.LookupOrchestrator;
using MediaLinkServiceBase = BridgeBeats.Services.LinkResolver.MediaLinkServiceBase;
using MusicLookupServiceBase = BridgeBeats.Core.Domain.Providers.Common.MusicLookupServiceBase;
using OAuthStateCleanupService = BridgeBeats.Core.Infrastructure.Identity.OAuthStateCleanupService;
using PlaylistCleanupService = BridgeBeats.Core.Domain.Services.Cards.PlaylistCleanupService;
using QueueProcessorBackgroundService = BridgeBeats.Core.Domain.Services.Queue.QueueProcessorBackgroundService;
using RedisATProtoSessionManager = BridgeBeats.Core.Infrastructure.Storage.RedisATProtoSessionManager;
using RedisGenreCache = BridgeBeats.Core.Infrastructure.Cache.RedisGenreCache;
using RedisMediaLinkCache = BridgeBeats.Core.Infrastructure.Cache.RedisMediaLinkCache;
using RedisRateLimitTracker = BridgeBeats.Core.Infrastructure.Queue.RedisRateLimitTracker;
using RedisRequestDeduplicator = BridgeBeats.Core.Infrastructure.Queue.RedisRequestDeduplicator;
using RedisRequestQueue = BridgeBeats.Core.Infrastructure.Queue.RedisRequestQueue<BridgeBeats.Contracts.Interfaces.IQueueableRequest>;
using RedisSagaStateManager = BridgeBeats.Core.Infrastructure.Queue.RedisSagaStateManager;
using RetryAfterLimitHandler = BridgeBeats.Core.Domain.Providers.Common.RetryAfterLimitHandler;
using SagaResultCombiner = BridgeBeats.Core.Domain.Services.Queue.SagaResultCombiner;
using SpotifyLookupService = BridgeBeats.Core.Domain.Providers.Spotify.SpotifyLookupService;
using SpotifyTokenHandler = BridgeBeats.Core.Domain.Providers.Spotify.SpotifyTokenHandler;
using TidalLookupService = BridgeBeats.Core.Domain.Providers.Tidal.TidalLookupService;
using TidalTokenHandler = BridgeBeats.Core.Domain.Providers.Tidal.TidalTokenHandler;
namespace BridgeBeats.Core.Infrastructure.Logging;

/// <summary>
/// Centralized EventId constants for high-performance logging with LoggerMessage source generator.
/// Organized by project and service area with 250-point ranges for future expansion.
/// </summary>
/// <remarks>
/// Range Allocation:
/// <list type="bullet">
///   <item>1000-1999: Core/Infrastructure (Storage, Queue, Identity, Cache)</item>
///   <item>2000-2999: Core/Providers (Spotify, Tidal, AppleMusic, Common)</item>
///   <item>3000-3999: Core/Services (Queue, LinkResolver, Cards, Other)</item>
///   <item>4000-4999: Web (Controllers, Middleware, Configuration)</item>
///   <item>5000-6999: Workers (SagaCoordinator, Spotify, Maintenance, JetStream, Discord, AppleMusic, Tidal)</item>
/// </list>
/// </remarks>
public static class LogEventIds {
    /// <summary>
    /// EventIds for Infrastructure layer components (1000-1999).
    /// </summary>
    public static class Infrastructure {
        /// <summary>
        /// EventIds for Storage services (1000-1249).
        /// </summary>
        public static class Storage {
            // RedisMediaLinkCache (1000-1049)

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.TryGetCachedResultAsync"/>.
            /// </summary>
            public const int RedisMediaLinkCacheGetByUrl = 1000;

            // ATProtoStorageService (1075-1099)

            /// <summary>
            /// EventId for <see cref="ATProtoStorageService.LogUpdatedRecord"/>.
            /// </summary>
            public const int ATProtoStorageServiceUpdatedRecord = 1075;

            /// <summary>
            /// EventId for <see cref="ATProtoStorageService.LogCreatedRecord"/>.
            /// </summary>
            public const int ATProtoStorageServiceCreatedRecord = 1076;

            /// <summary>
            /// EventId for <see cref="ATProtoStorageService.LogStoreError"/>.
            /// </summary>
            public const int ATProtoStorageServiceStoreError = 1077;

            /// <summary>
            /// EventId for <see cref="ATProtoStorageService.LogGetRecordFailed"/>.
            /// </summary>
            public const int ATProtoStorageServiceGetRecordFailed = 1078;

            /// <summary>
            /// EventId for <see cref="ATProtoStorageService.LogRetrieveError"/>.
            /// </summary>
            public const int ATProtoStorageServiceRetrieveError = 1079;

            /// <summary>
            /// EventId for ATProtoStorageService list-records error (retired; superseded by CAR-based logging).
            /// </summary>
            public const int ATProtoStorageServiceListRecordsError = 1080;

            // ATProtoStorageService CAR download log events (1081-1099)

            /// <summary>
            /// EventId for ATProtoStorageService CAR file downloaded successfully (bytes, blocks, elapsed).
            /// </summary>
            public const int ATProtoStorageServiceCarDownloaded = 1081;

            /// <summary>
            /// EventId for ATProtoStorageService CAR enumeration complete (record count).
            /// </summary>
            public const int ATProtoStorageServiceCarEnumerated = 1082;

            /// <summary>
            /// EventId for ATProtoStorageService CAR download or parse failure.
            /// </summary>
            public const int ATProtoStorageServiceCarDownloadFailed = 1083;

            /// <summary>
            /// EventId for ATProtoStorageService per-record skip due to parse or convert failure.
            /// </summary>
            public const int ATProtoStorageServiceCarRecordSkipped = 1084;

            /// <summary>
            /// EventId for ATProtoStorageService CAR commit version not equal to 3 (warn and proceed).
            /// </summary>
            public const int ATProtoStorageServiceCarCommitVersionUnexpected = 1085;

            /// <summary>
            /// EventId logged per re-download attempt before the retry wait, with the attempt number
            /// and the classified fault. Part of the CAR fetch+parse retry (1086-1087).
            /// </summary>
            public const int ATProtoStorageServiceCarRetryAttempt = 1086;

            /// <summary>
            /// EventId logged once when all 3 CAR fetch+parse attempts are exhausted, before the
            /// CarParseException propagates to the caller.
            /// </summary>
            public const int ATProtoStorageServiceCarRetryExhausted = 1087;

            /// <summary>
            /// EventId logged when a full-collection read is served from the fresh in-process CAR
            /// cache without a network download — logged at both the pre-lock fast read and the
            /// under-lock awaiter-reuse serve.
            /// </summary>
            public const int ATProtoStorageServiceCarCacheHit = 1088;

            /// <summary>
            /// EventId logged under the lock when a confirmed miss, expiry, or forced-refresh will
            /// trigger a fresh download. Carries a flag indicating whether the miss was forced.
            /// </summary>
            public const int ATProtoStorageServiceCarCacheMiss = 1089;

            /// <summary>
            /// EventId logged when the single-flight download begins under the lock — one per actual
            /// download, pairing with ATProtoStorageServiceCarCacheRefreshComplete to bound the
            /// populate duration in logs.
            /// </summary>
            public const int ATProtoStorageServiceCarCacheRefreshStart = 1090;

            /// <summary>
            /// EventId logged when an accepted transfer is committed to the cache after a successful
            /// download+parse. Carries the record count and the new cache timestamp.
            /// </summary>
            public const int ATProtoStorageServiceCarCacheRefreshComplete = 1091;

            /// <summary>
            /// EventId logged when the field overwrite replaces a previously-expired cache entry —
            /// distinguishes a true expiry-replace from a cold first populate. Not logged on the
            /// initial populate.
            /// </summary>
            public const int ATProtoStorageServiceCarCacheEvict = 1092;

            // RedisATProtoSessionManager (1100-1149)

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogForceReauthRequested"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerForceReauthRequested = 1100;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogNoStoredCredentials"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerNoStoredCredentials = 1101;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogDeserializeFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerDeserializeFailed = 1102;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogRestoringSession"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerRestoringSession = 1103;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogSessionRestored"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerSessionRestored = 1104;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogRestoreFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerRestoreFailed = 1105;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogRestoreException"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerRestoreException = 1106;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogWaitingForLock"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerWaitingForLock = 1107;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogLockWaitTimeout"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerLockWaitTimeout = 1108;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogFreshLogin"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerFreshLogin = 1109;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogAuthenticated"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerAuthenticated = 1110;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogAgentAuthenticated"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerAgentAuthenticated = 1111;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogPersistAfterAuthFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerPersistAfterAuthFailed = 1112;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogCredentialsUpdated"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerCredentialsUpdated = 1113;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogPersistUpdateFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerPersistUpdateFailed = 1114;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogTokenRefreshFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerTokenRefreshFailed = 1115;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogClearAfterRefreshFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerClearAfterRefreshFailed = 1116;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogUnauthenticated"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerUnauthenticated = 1117;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogClearAfterUnauthFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerClearAfterUnauthFailed = 1118;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogCannotPersist"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerCannotPersist = 1119;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogPersisted"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerPersisted = 1120;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogCleared"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerCleared = 1121;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogClearSuppressedByCooldown"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerClearSuppressedByCooldown = 1122;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogLockReleaseFailed"/>.
            /// </summary>
            public const int RedisATProtoSessionManagerLockReleaseFailed = 1123;

            /// <summary>
            /// EventId for <see cref="RedisATProtoSessionManager.LogRestoreCryptoMismatch"/>.
            /// Distinct from <see cref="RedisATProtoSessionManagerRestoreException"/> — tracks key-ring
            /// mismatch or legacy plaintext degradation, not a generic restore failure.
            /// </summary>
            public const int RedisATProtoSessionManagerRestoreCryptoMismatch = 1124;
        }

        /// <summary>
        /// EventIds for Queue infrastructure (1250-1499).
        /// </summary>
        public static class Queue {
            // RedisRequestQueue (1250-1299)

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogConsumerGroupCreated"/>.
            /// </summary>
            public const int RedisRequestQueueConsumerGroupCreated = 1250;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogConsumerGroupExists"/>.
            /// </summary>
            public const int RedisRequestQueueConsumerGroupExists = 1251;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogEnqueued"/>.
            /// </summary>
            public const int RedisRequestQueueEnqueued = 1252;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogSkippingBlockedMessage"/>.
            /// </summary>
            public const int RedisRequestQueueSkippingBlockedMessage = 1253;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogSkippingRateLimitedMessage"/>.
            /// </summary>
            public const int RedisRequestQueueSkippingRateLimitedMessage = 1254;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogAcknowledged"/>.
            /// </summary>
            public const int RedisRequestQueueAcknowledged = 1255;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogMessageNotFoundForRequeue"/>.
            /// </summary>
            public const int RedisRequestQueueMessageNotFoundForRequeue = 1256;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogRequeued"/>.
            /// </summary>
            public const int RedisRequestQueueRequeued = 1258;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogMovedFromDlq"/>.
            /// </summary>
            public const int RedisRequestQueueMovedFromDlq = 1259;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogMessageNotFoundForDlqMove"/>.
            /// </summary>
            public const int RedisRequestQueueMessageNotFoundForDlqMove = 1260;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogMovedToDlq"/>.
            /// </summary>
            public const int RedisRequestQueueMovedToDlq = 1261;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogDeletedFromDlq"/>.
            /// </summary>
            public const int RedisRequestQueueDeletedFromDlq = 1262;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogBulkGated"/>.
            /// </summary>
            public const int RedisRequestQueueBulkGated = 1263;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogMessageNoPayload"/>.
            /// </summary>
            public const int RedisRequestQueueMessageNoPayload = 1264;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogDeserializationFailed"/>.
            /// </summary>
            public const int RedisRequestQueueDeserializationFailed = 1265;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogDeserializationError"/>.
            /// </summary>
            public const int RedisRequestQueueDeserializationError = 1266;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogDequeueStarting"/>.
            /// </summary>
            public const int RedisRequestQueueDequeueStarting = 1267;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogDequeueNoEligibleMessages"/>.
            /// </summary>
            public const int RedisRequestQueueDequeueNoEligibleMessages = 1268;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogStreamScanSummary"/>.
            /// </summary>
            public const int RedisRequestQueueStreamScanSummary = 1269;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogClaimFailed"/>.
            /// </summary>
            public const int RedisRequestQueueClaimFailed = 1270;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogFoundEligibleMessage"/>.
            /// </summary>
            public const int RedisRequestQueueFoundEligibleMessage = 1271;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogNoNewMessagesInStream"/>.
            /// </summary>
            public const int RedisRequestQueueNoNewMessagesInStream = 1272;

            /// <summary>
            /// EventId for <see cref="RedisRequestQueue.LogAgingIntervalMisconfigured"/>.
            /// Emitted at startup when InteractiveAgingInterval is ≤ 1 (misconfiguration guard).
            /// </summary>
            public const int RedisRequestQueueAgingIntervalMisconfigured = 1273;

            // RedisSagaStateManager (1300-1349)

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogSagaResumed"/>.
            /// </summary>
            public const int RedisSagaStateManagerResumed = 1300;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogSagaCreated"/>.
            /// </summary>
            public const int RedisSagaStateManagerCreated = 1301;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogIncompleteCoreState"/>.
            /// </summary>
            public const int RedisSagaStateManagerIncompleteCoreState = 1302;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogInvalidLookupType"/>.
            /// </summary>
            public const int RedisSagaStateManagerInvalidLookupType = 1303;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogProviderStateUpdated"/>.
            /// </summary>
            public const int RedisSagaStateManagerProviderStateUpdated = 1304;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogPartialResultUriSet"/>.
            /// </summary>
            public const int RedisSagaStateManagerPartialResultUriSet = 1305;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogFinalResultUriSet"/>.
            /// </summary>
            public const int RedisSagaStateManagerFinalResultUriSet = 1306;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogSagaDeleted"/>.
            /// </summary>
            public const int RedisSagaStateManagerDeleted = 1307;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogIsPartialSet"/>.
            /// </summary>
            public const int RedisSagaStateManagerIsPartialSet = 1308;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogInitialProviderSet"/>.
            /// </summary>
            public const int RedisSagaStateManagerInitialProviderSet = 1309;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogRateLimitInfoSet"/>.
            /// </summary>
            public const int RedisSagaStateManagerRateLimitInfoSet = 1310;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogProviderStatesInitialized"/>.
            /// </summary>
            public const int RedisSagaStateManagerProviderStatesInitialized = 1311;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogUnfinalizedSagasFound"/>.
            /// </summary>
            public const int RedisSagaStateManagerUnfinalizedSagasFound = 1312;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogAddedToPendingIndex"/>.
            /// </summary>
            public const int RedisSagaStateManagerAddedToPendingIndex = 1313;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogRemovedFromPendingIndex"/>.
            /// </summary>
            public const int RedisSagaStateManagerRemovedFromPendingIndex = 1314;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogOriginPrioritySet"/>.
            /// </summary>
            public const int RedisSagaStateManagerOriginPrioritySet = 1315;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogSecondariesQueuedMarker"/>.
            /// </summary>
            public const int RedisSagaStateManagerSecondariesQueuedMarker = 1316;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogFinalizeClaimMarker"/>.
            /// </summary>
            public const int RedisSagaStateManagerFinalizeClaimMarker = 1317;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogFinalizeClaimReleased"/>.
            /// </summary>
            public const int RedisSagaStateManagerFinalizeClaimReleased = 1318;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogWriteGenerationAdvanced"/>.
            /// </summary>
            public const int RedisSagaStateManagerWriteGenerationAdvanced = 1319;

            /// <summary>
            /// EventId for <see cref="RedisSagaStateManager.LogWriteGenerationReset"/>.
            /// </summary>
            public const int RedisSagaStateManagerWriteGenerationReset = 1320;

            // RedisRequestDeduplicator (1350-1374)

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogLockAcquired"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorLockAcquired = 1350;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogAlreadyInFlight"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorAlreadyInFlight = 1351;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogReleaseDenied"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorReleaseDenied = 1352;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogLockReleased"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorLockReleased = 1353;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogCompletedBeforeSubscription"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorCompletedBeforeSubscription = 1354;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogWaitTimeout"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorWaitTimeout = 1355;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogFinalCompletedBeforeSubscription"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorFinalCompletedBeforeSubscription = 1356;

            /// <summary>
            /// EventId for <see cref="RedisRequestDeduplicator.LogFinalWaitTimeout"/>.
            /// </summary>
            public const int RedisRequestDeduplicatorFinalWaitTimeout = 1357;

            // RedisRateLimitTracker (1375-1399)

            /// <summary>
            /// EventId for <see cref="RedisRateLimitTracker.LogInvalidValueRemoved"/>.
            /// </summary>
            public const int RedisRateLimitTrackerInvalidValueRemoved = 1375;

            /// <summary>
            /// EventId for <see cref="RedisRateLimitTracker.LogExpiredNotSet"/>.
            /// </summary>
            public const int RedisRateLimitTrackerExpiredNotSet = 1376;

            /// <summary>
            /// EventId for <see cref="RedisRateLimitTracker.LogRateLimitSet"/>.
            /// </summary>
            public const int RedisRateLimitTrackerRateLimitSet = 1377;

            /// <summary>
            /// EventId for <see cref="RedisRateLimitTracker.LogRateLimitCleared"/>.
            /// </summary>
            public const int RedisRateLimitTrackerCleared = 1378;

            // QueueMetrics (1400-1424)
        }

        /// <summary>
        /// EventIds for Identity infrastructure (1500-1749).
        /// </summary>
        public static class Identity {
            // ATProtoOAuthService (1500-1549)

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogStartFlow"/>.
            /// </summary>
            public const int ATProtoOAuthServiceStartFlow = 1500;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogResolvedHandle"/>.
            /// </summary>
            public const int ATProtoOAuthServiceResolvedHandle = 1501;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogResolvedPds"/>.
            /// </summary>
            public const int ATProtoOAuthServiceResolvedPds = 1502;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogResolvedAuthServer"/>.
            /// </summary>
            public const int ATProtoOAuthServiceResolvedAuthServer = 1503;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogFetchedMetadata"/>.
            /// </summary>
            public const int ATProtoOAuthServiceFetchedMetadata = 1504;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogNoParSupport"/>.
            /// </summary>
            public const int ATProtoOAuthServiceNoParSupport = 1505;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogAuthStarted"/>.
            /// </summary>
            public const int ATProtoOAuthServiceAuthStarted = 1506;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogCompletingFlow"/>.
            /// </summary>
            public const int ATProtoOAuthServiceCompletingFlow = 1507;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogIssuerMismatch"/>.
            /// </summary>
            public const int ATProtoOAuthServiceIssuerMismatch = 1508;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogAuthCompleted"/>.
            /// </summary>
            public const int ATProtoOAuthServiceAuthCompleted = 1509;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshingTokens"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshingTokens = 1510;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshPdsFailed"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshPdsFailed = 1511;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshAuthServerFailed"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshAuthServerFailed = 1512;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshSuccess"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshSuccess = 1513;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshError"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshError = 1514;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogCleanedUpStates"/>.
            /// </summary>
            public const int ATProtoOAuthServiceCleanedUpStates = 1515;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogUsingCachedMetadata"/>.
            /// </summary>
            public const int ATProtoOAuthServiceUsingCachedMetadata = 1516;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogFetchingMetadata"/>.
            /// </summary>
            public const int ATProtoOAuthServiceFetchingMetadata = 1517;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogMetadataFetchFailed"/>.
            /// </summary>
            public const int ATProtoOAuthServiceMetadataFetchFailed = 1518;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogMetadataParseError"/>.
            /// </summary>
            public const int ATProtoOAuthServiceMetadataParseError = 1519;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogCachedMetadata"/>.
            /// </summary>
            public const int ATProtoOAuthServiceCachedMetadata = 1520;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogParSuccess"/>.
            /// </summary>
            public const int ATProtoOAuthServiceParSuccess = 1521;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogParParseError"/>.
            /// </summary>
            public const int ATProtoOAuthServiceParParseError = 1522;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogDPoPNonceRetry"/>.
            /// </summary>
            public const int ATProtoOAuthServiceDPoPNonceRetry = 1523;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogTokenRequestFailed"/>.
            /// </summary>
            public const int ATProtoOAuthServiceTokenRequestFailed = 1524;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogExchangingCode"/>.
            /// </summary>
            public const int ATProtoOAuthServiceExchangingCode = 1525;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogTokenParseError"/>.
            /// </summary>
            public const int ATProtoOAuthServiceTokenParseError = 1526;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogTokenMissingProperty"/>.
            /// </summary>
            public const int ATProtoOAuthServiceTokenMissingProperty = 1527;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogTokenMissingSub"/>.
            /// </summary>
            public const int ATProtoOAuthServiceTokenMissingSub = 1528;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogTokenDidMismatch"/>.
            /// </summary>
            public const int ATProtoOAuthServiceTokenDidMismatch = 1529;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogCodeExchanged"/>.
            /// </summary>
            public const int ATProtoOAuthServiceCodeExchanged = 1530;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogMissingTokenEndpoint"/>.
            /// </summary>
            public const int ATProtoOAuthServiceMissingTokenEndpoint = 1531;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshParseError"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshParseError = 1532;

            /// <summary>
            /// EventId for <see cref="ATProtoOAuthService.LogRefreshDidMismatch"/>.
            /// </summary>
            public const int ATProtoOAuthServiceRefreshDidMismatch = 1533;

            // OAuthStateCleanupService (1550-1574)

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogServiceStarted"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceStarted = 1550;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogRunningInitialCleanup"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceInitialCleanup = 1551;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogInitialCleanupCompleted"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceInitialCleanupCompleted = 1552;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogInitialCleanupError"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceInitialCleanupError = 1553;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogRunningCleanup"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceRunningCleanup = 1554;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogCleanupCompleted"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceCleanupCompleted = 1555;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogCleanupError"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceCleanupError = 1556;

            /// <summary>
            /// EventId for <see cref="OAuthStateCleanupService.LogServiceStopped"/>.
            /// </summary>
            public const int OAuthStateCleanupServiceStopped = 1557;

            // ApiKeyProvider (1575-1599)

            /// <summary>
            /// EventId for <see cref="ApiKeyProvider.LogDbError"/>.
            /// </summary>
            public const int ApiKeyProviderDbError = 1575;

            /// <summary>
            /// EventId for <see cref="ApiKeyProvider.LogInvalidOperation"/>.
            /// </summary>
            public const int ApiKeyProviderInvalidOperation = 1576;

            // DidPlcService (1600-1624)

            // DidWebService (1625-1649)
        }

        /// <summary>
        /// EventIds for Cache infrastructure (1750-1999).
        /// </summary>
        public static class Cache {
            // RedisMediaLinkCache (1750-1799)

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogCachedResult"/>.
            /// </summary>
            public const int RedisMediaLinkCacheCachedResult = 1750;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogCacheError"/>.
            /// </summary>
            public const int RedisMediaLinkCacheCacheError = 1751;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogCacheEntryExists"/>.
            /// </summary>
            public const int RedisMediaLinkCacheEntryExists = 1752;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogWriteVerificationFailed"/>.
            /// </summary>
            public const int RedisMediaLinkCacheWriteVerificationFailed = 1753;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogCreatedLookupEntries"/>.
            /// </summary>
            public const int RedisMediaLinkCacheCreatedLookupEntries = 1754;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogAddInputLinksError"/>.
            /// </summary>
            public const int RedisMediaLinkCacheAddInputLinksError = 1755;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogRemovedLookupKeys"/>.
            /// </summary>
            public const int RedisMediaLinkCacheRemovedLookupKeys = 1756;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogPdsLookupFailed"/>.
            /// </summary>
            public const int RedisMediaLinkCachePdsLookupFailed = 1757;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogCacheHit"/>.
            /// </summary>
            public const int RedisMediaLinkCacheCacheHit = 1758;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogRecordNotFound"/>.
            /// </summary>
            public const int RedisMediaLinkCacheRecordNotFound = 1759;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogHttpError"/>.
            /// </summary>
            public const int RedisMediaLinkCacheHttpError = 1760;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogPdsError"/>.
            /// </summary>
            public const int RedisMediaLinkCachePdsError = 1761;

            /// <summary>
            /// EventId for <see cref="RedisMediaLinkCache.LogExtractProviderIdFailed"/>.
            /// </summary>
            public const int RedisMediaLinkCacheExtractProviderIdFailed = 1762;

            // RedisGenreCache (1800-1849)

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogCachedTrackGenres"/>.
            /// </summary>
            public const int RedisGenreCacheCachedTrackGenres = 1800;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogCachedArtistGenres"/>.
            /// </summary>
            public const int RedisGenreCacheCachedArtistGenres = 1801;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogCachedArtistMappings"/>.
            /// </summary>
            public const int RedisGenreCacheCachedArtistMappings = 1802;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogEnqueuedArtists"/>.
            /// </summary>
            public const int RedisGenreCacheEnqueuedArtists = 1803;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogDequeuedArtists"/>.
            /// </summary>
            public const int RedisGenreCacheDequeuedArtists = 1804;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogNoArtistMapping"/>.
            /// </summary>
            public const int RedisGenreCacheNoArtistMapping = 1805;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogNoArtistGenres"/>.
            /// </summary>
            public const int RedisGenreCacheNoArtistGenres = 1806;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogResolvedGenres"/>.
            /// </summary>
            public const int RedisGenreCacheResolvedGenres = 1807;

            /// <summary>
            /// EventId for <see cref="RedisGenreCache.LogParseError"/>.
            /// </summary>
            public const int RedisGenreCacheParseError = 1808;
        }

        /// <summary>
        /// EventIds for other infrastructure components (1900-1999).
        /// </summary>
        public static class Other {
            // AspireServiceExtensions (1900-1924)

            /// <summary>
            /// EventId for <see cref="AspireServiceExtensionsLog.LogRetryWithRetryAfterHeader"/>.
            /// </summary>
            public const int AspireServiceExtensionsRetryWithHeader = 1900;

            /// <summary>
            /// EventId for <see cref="AspireServiceExtensionsLog.LogRetryWithExponentialBackoff"/>.
            /// </summary>
            public const int AspireServiceExtensionsRetryWithBackoff = 1901;
        }
    }

    /// <summary>
    /// EventIds for Provider layer components (2000-2999).
    /// </summary>
    public static class Providers {
        /// <summary>
        /// EventIds for Spotify provider (2000-2249).
        /// </summary>
        public static class Spotify {
            // SpotifyLookupService (2000-2099)

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogDeserializeAlbumFailed"/>.
            /// </summary>
            public const int DeserializeAlbumFailed = 2000;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogDeserializeTrackFailed"/>.
            /// </summary>
            public const int DeserializeTrackFailed = 2001;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogDeserializeBulkTracksFailed"/>.
            /// </summary>
            public const int DeserializeBulkTracksFailed = 2002;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogDeserializeBulkAlbumsFailed"/>.
            /// </summary>
            public const int DeserializeBulkAlbumsFailed = 2003;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogDeserializeBulkArtistsFailed"/>.
            /// </summary>
            public const int DeserializeBulkArtistsFailed = 2004;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogParseBulkTracksError"/>.
            /// </summary>
            public const int ParseBulkTracksError = 2005;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogParseBulkAlbumsError"/>.
            /// </summary>
            public const int ParseBulkAlbumsError = 2006;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogParseBulkArtistsError"/>.
            /// </summary>
            public const int ParseBulkArtistsError = 2007;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogBulkRequestFailed"/>.
            /// </summary>
            public const int BulkRequestFailed = 2009;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogParseResponseError"/>.
            /// </summary>
            public const int ParseResponseError = 2010;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogParseArtistListError"/>.
            /// </summary>
            public const int ParseArtistListError = 2011;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogCacheTrackMappingFailed"/>.
            /// </summary>
            public const int CacheTrackMappingFailed = 2012;

            /// <summary>
            /// EventId for <see cref="SpotifyLookupService.LogResponseBody"/>.
            /// </summary>
            public const int ResponseBodyTrace = 2013;

            // SpotifyTokenHandler (2100-2124)

            /// <summary>
            /// EventId for <see cref="SpotifyTokenHandler.LogTokenExpiredOrUnset"/>.
            /// </summary>
            public const int TokenExpiredOrUnset = 2100;
        }

        /// <summary>
        /// EventIds for Tidal provider (2250-2499).
        /// </summary>
        public static class Tidal {
            // TidalLookupService (2250-2349)

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogExtractSingleResourceError"/>.
            /// </summary>
            public const int ExtractSingleResourceError = 2250;

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogParseResponseError"/>.
            /// </summary>
            public const int ParseResponseError = 2251;

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogParseArtistListError"/>.
            /// </summary>
            public const int ParseArtistListError = 2252;

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogCacheGenresFailed"/>.
            /// </summary>
            public const int CacheGenresFailed = 2253;

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogResponseBody"/>.
            /// </summary>
            public const int ResponseBodyTrace = 2254;

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogDataTrace"/>.
            /// </summary>
            public const int DataTrace = 2255;

            /// <summary>
            /// EventId for <see cref="TidalLookupService.LogIncludedTrace"/>.
            /// </summary>
            public const int IncludedTrace = 2256;

            // TidalTokenHandler (2350-2374)

            /// <summary>
            /// EventId for <see cref="TidalTokenHandler.LogTokenExpiredOrUnset"/>.
            /// </summary>
            public const int TokenExpiredOrUnset = 2350;
        }

        /// <summary>
        /// EventIds for Apple Music provider (2500-2749).
        /// </summary>
        public static class AppleMusic {
            // AppleMusicLookupService (2500-2599)

            /// <summary>
            /// EventId for <see cref="AppleMusicLookupService.LogParseArtistListError"/>.
            /// </summary>
            public const int ParseArtistListError = 2500;

            /// <summary>
            /// EventId for <see cref="AppleMusicLookupService.LogParseResponseError"/>.
            /// </summary>
            public const int ParseResponseError = 2501;

            /// <summary>
            /// EventId for <see cref="AppleMusicLookupService.LogResponseBody"/>.
            /// </summary>
            public const int ResponseBodyTrace = 2502;

            /// <summary>
            /// EventId for <see cref="AppleMusicLookupService.LogCacheGenresFailed"/>.
            /// </summary>
            public const int CacheGenresFailed = 2503;

            // AppleJwtHandler (2600-2624)
        }

        /// <summary>
        /// EventIds for Common provider components (2750-2999).
        /// </summary>
        public static class Common {
            // HttpMusicLookupService (2750-2799)

            /// <summary>
            /// EventId for <see cref="HttpMusicLookupService.LogWorkerHttpError"/>.
            /// </summary>
            public const int WorkerHttpError = 2750;

            /// <summary>
            /// EventId for <see cref="HttpMusicLookupService.LogWorkerNullResponse"/>.
            /// </summary>
            public const int WorkerNullResponse = 2751;

            /// <summary>
            /// EventId for <see cref="HttpMusicLookupService.LogWorkerError"/>.
            /// </summary>
            public const int WorkerError = 2752;

            /// <summary>
            /// EventId for <see cref="HttpMusicLookupService.LogHttpRequestError"/>.
            /// </summary>
            public const int HttpRequestError = 2753;

            /// <summary>
            /// EventId for <see cref="HttpMusicLookupService.LogUnexpectedError"/>.
            /// </summary>
            public const int UnexpectedError = 2754;

            /// <summary>
            /// EventId for unexpected exceptions handled by worker HTTP lookup endpoints.
            /// </summary>
            public const int UnexpectedWorkerLookupFailure = 2755;

            // MusicLookupServiceBase (2800-2824)

            /// <summary>
            /// EventId for <see cref="MusicLookupServiceBase.LogApiRequestError"/>.
            /// </summary>
            public const int ApiRequestError = 2800;

            // RetryAfterLimitHandler (2850-2874)

            /// <summary>
            /// EventId for <see cref="RetryAfterLimitHandler.LogRateLimitExceeded"/>.
            /// </summary>
            public const int RateLimitExceeded = 2850;
        }
    }

    /// <summary>
    /// EventIds for Service layer components (3000-3999).
    /// </summary>
    public static class Services {
        /// <summary>
        /// EventIds for Queue services (3000-3249).
        /// </summary>
        public static class Queue {
            // QueueProcessorBackgroundService (3000-3099)

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogQueueProcessorStarting"/>.
            /// </summary>
            public const int QueueProcessorStarting = 3000;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogQueueProcessorStopping"/>.
            /// </summary>
            public const int QueueProcessorStopping = 3001;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogQueueProcessorLoopError"/>.
            /// </summary>
            public const int QueueProcessorLoopError = 3002;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogProcessingMessage"/>.
            /// </summary>
            public const int ProcessingMessage = 3003;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogEndpointRateLimited"/>.
            /// </summary>
            public const int EndpointRateLimited = 3004;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogMessageProcessed"/>.
            /// </summary>
            public const int MessageProcessed = 3005;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogRateLimitEncountered"/>.
            /// </summary>
            public const int RateLimitEncountered = 3006;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaMarkedPartial"/>.
            /// </summary>
            public const int SagaMarkedPartial = 3007;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogProcessingFailed"/>.
            /// </summary>
            public const int ProcessingFailed = 3008;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogMaxRetriesExceeded"/>.
            /// </summary>
            public const int MaxRetriesExceeded = 3009;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaNotFoundForCompletion"/>.
            /// </summary>
            public const int SagaNotFoundForCompletion = 3010;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaNotYetComplete"/>.
            /// </summary>
            public const int SagaNotYetComplete = 3011;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaAlreadyHasFinalResult"/>.
            /// </summary>
            public const int SagaAlreadyHasFinalResult = 3012;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaCompletePublishing"/>.
            /// </summary>
            public const int SagaCompletePublishing = 3013;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaCompletionCheckFailed"/>.
            /// </summary>
            public const int SagaCompletionCheckFailed = 3014;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogSagaNotFoundForLookupCompletion"/>.
            /// </summary>
            public const int SagaNotFoundForLookupCompletion = 3015;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogLookupCompletionPublished"/>.
            /// </summary>
            public const int LookupCompletionPublished = 3016;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogLookupCompletionPublishFailed"/>.
            /// </summary>
            public const int LookupCompletionPublishFailed = 3017;

            /// <summary>
            /// EventId for <see cref="QueueProcessorBackgroundService.LogInteractiveDeferredToBackground"/>.
            /// Emitted when a rate-limited request whose <c>OriginPriority</c> was Interactive is
            /// requeued at Background priority, deferring it to the bulk-stream retry lane.
            /// </summary>
            public const int InteractiveDeferredToBackground = 3018;

            /// <summary>EventId for a queue message whose saga identity does not match its payload.</summary>
            public const int SagaIdentityMismatch = 3019;
            /// <summary>EventId for a failed identity quarantine operation.</summary>
            public const int SagaIdentityQuarantineFailed = 3020;

            // SagaResultCombiner (3100-3149)

            /// <summary>
            /// EventId for <see cref="SagaResultCombiner.LogCombineResultsIncompleteSaga"/>.
            /// </summary>
            public const int CombineResultsIncompleteSaga = 3100;

            /// <summary>
            /// EventId for <see cref="SagaResultCombiner.LogDeserializeResultFailed"/>.
            /// </summary>
            public const int DeserializeResultFailed = 3101;

            /// <summary>
            /// EventId for <see cref="SagaResultCombiner.LogNoSuccessfulResults"/>.
            /// </summary>
            public const int NoSuccessfulResults = 3102;

            /// <summary>
            /// EventId for <see cref="SagaResultCombiner.LogCombinedResults"/>.
            /// </summary>
            public const int CombinedResults = 3103;
        }

        /// <summary>
        /// EventIds for LinkResolver services (3250-3499).
        /// </summary>
        public static class LinkResolver {
            // MediaLinkServiceBase (3250-3299)

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogUrlLookupProviderError"/>.
            /// </summary>
            public const int UrlLookupProviderError = 3250;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogUrlLookupProviderTrace"/>.
            /// </summary>
            public const int UrlLookupProviderTrace = 3251;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogUrlLookupError"/>.
            /// </summary>
            public const int UrlLookupError = 3252;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogUrlLookupTrace"/>.
            /// </summary>
            public const int UrlLookupTrace = 3253;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogArtistTitleLookupProviderError"/>.
            /// </summary>
            public const int ArtistTitleLookupProviderError = 3254;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogArtistTitleLookupProviderTrace"/>.
            /// </summary>
            public const int ArtistTitleLookupProviderTrace = 3255;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogArtistTitleLookupError"/>.
            /// </summary>
            public const int ArtistTitleLookupError = 3256;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogArtistTitleLookupTrace"/>.
            /// </summary>
            public const int ArtistTitleLookupTrace = 3257;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogExternalIdLookupProviderError"/>.
            /// </summary>
            public const int ExternalIdLookupProviderError = 3258;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogExternalIdLookupProviderTrace"/>.
            /// </summary>
            public const int ExternalIdLookupProviderTrace = 3259;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogExternalIdLookupError"/>.
            /// </summary>
            public const int ExternalIdLookupError = 3260;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogExternalIdLookupTrace"/>.
            /// </summary>
            public const int ExternalIdLookupTrace = 3261;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogProviderNotEnabled"/>.
            /// </summary>
            public const int ProviderNotEnabled = 3262;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogProviderIdLookupError"/>.
            /// </summary>
            public const int ProviderIdLookupError = 3263;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogProviderIdLookupTrace"/>.
            /// </summary>
            public const int ProviderIdLookupTrace = 3264;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogSecondaryLookupError"/>.
            /// </summary>
            public const int SecondaryLookupError = 3265;

            /// <summary>
            /// EventId for <see cref="MediaLinkServiceBase.LogSecondaryLookupTrace"/>.
            /// </summary>
            public const int SecondaryLookupTrace = 3266;

            // CachingMediaLinkService (3300-3324)

            /// <summary>
            /// EventId for <see cref="CachingMediaLinkService.LogPartialResultReturned"/>.
            /// </summary>
            public const int PartialResultReturned = 3300;

            /// <summary>
            /// EventId for <see cref="CachingMediaLinkService.LogPendingPartialReturned"/>.
            /// </summary>
            public const int PendingPartialResultReturned = 3301;

            // LookupOrchestrator (3350-3399)

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogCacheHit"/>.
            /// </summary>
            public const int OrchestratorCacheHit = 3350;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogRequestAlreadyInFlight"/>.
            /// </summary>
            public const int OrchestratorRequestAlreadyInFlight = 3351;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogLookupError"/>.
            /// </summary>
            public const int OrchestratorLookupError = 3352;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogSagaCreated"/>.
            /// </summary>
            public const int OrchestratorSagaCreated = 3353;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogSagaResumedWithResult"/>.
            /// </summary>
            public const int OrchestratorSagaResumedWithResult = 3354;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogWaitingForFinalResult"/>.
            /// </summary>
            public const int OrchestratorWaitingForFinalResult = 3355;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogReturningRateLimitedPartial"/>.
            /// </summary>
            public const int OrchestratorReturningRateLimitedPartial = 3356;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogWaitBudgetExhausted"/>.
            /// </summary>
            public const int OrchestratorWaitBudgetExhausted = 3357;

            /// <summary>
            /// EventId for <see cref="LookupOrchestrator.LogInFlightSagaHasStoredResult"/>.
            /// </summary>
            public const int OrchestratorInFlightSagaHasStoredResult = 3358;
        }

        /// <summary>
        /// EventIds for Cards services (3500-3749).
        /// </summary>
        public static class Cards {
            // PlaylistCleanupService (3500-3524)

            /// <summary>
            /// EventId for <see cref="PlaylistCleanupService.LogPlaylistCleanupStarted"/>.
            /// </summary>
            public const int PlaylistCleanupStarted = 3500;

            /// <summary>
            /// EventId for <see cref="PlaylistCleanupService.LogRunningCleanup"/>.
            /// </summary>
            public const int RunningCleanup = 3501;

            /// <summary>
            /// EventId for <see cref="PlaylistCleanupService.LogCleanupCompleted"/>.
            /// </summary>
            public const int CleanupCompleted = 3502;

            /// <summary>
            /// EventId for <see cref="PlaylistCleanupService.LogCleanupError"/>.
            /// </summary>
            public const int CleanupError = 3503;

            /// <summary>
            /// EventId for <see cref="PlaylistCleanupService.LogPlaylistCleanupStopped"/>.
            /// </summary>
            public const int PlaylistCleanupStopped = 3504;
        }

        /// <summary>
        /// EventIds for other services (3750-3999).
        /// </summary>
        public static class Other {
            // 3750-3753 retired (StatisticsService relocated to the worker stats fold)

            // AspireServiceExtensions (3800-3824)
        }
    }

    /// <summary>
    /// EventIds for Web layer components (4000-4999).
    /// Defined in BridgeBeats.Web.Logging.LogEventIds.
    /// </summary>
    /// <remarks>
    /// Range Allocation:
    /// <list type="bullet">
    ///   <item>4000-4499: Controllers</item>
    ///   <item>4500-4749: Middleware</item>
    ///   <item>4750-4899: Configuration</item>
    /// </list>
    /// </remarks>
    public static class Web {
        // EventIds defined in BridgeBeats.Web.Logging.LogEventIds
    }

    /// <summary>
    /// EventIds for BackgroundServices (4900-4999).
    /// </summary>
    public static class BackgroundServices {
        // 4900-4906 retired (StatisticsRefreshBackgroundService removed; refresh now cross-process Pub/Sub)
    }

    /// <summary>
    /// EventIds for Worker components (5000-6999).
    /// Defined in each worker project's Logging.LogEventIds class.
    /// </summary>
    /// <remarks>
    /// Range Allocation:
    /// <list type="bullet">
    ///   <item>5000-5249: SagaCoordinator (BridgeBeats.Worker.SagaCoordinator.Logging.LogEventIds)</item>
    ///   <item>5250-5499: Spotify (BridgeBeats.Worker.Spotify.Logging.LogEventIds)</item>
    ///   <item>5500-5749: Maintenance (BridgeBeats.Worker.Maintenance.Logging.LogEventIds)</item>
    ///   <item>5750-5999: JetStreamWatcher (BridgeBeats.Worker.JetStreamWatcher.Logging.LogEventIds)</item>
    ///   <item>6000-6249: Discord (BridgeBeats.Worker.Discord.Logging.LogEventIds)</item>
    ///   <item>6250-6499: AppleMusic (BridgeBeats.Worker.AppleMusic.Logging.LogEventIds)</item>
    ///   <item>6500-6749: Tidal (BridgeBeats.Worker.Tidal.Logging.LogEventIds)</item>
    /// </list>
    /// </remarks>
    public static class Workers {
        // EventIds defined in each worker project's Logging.LogEventIds class
    }
}
