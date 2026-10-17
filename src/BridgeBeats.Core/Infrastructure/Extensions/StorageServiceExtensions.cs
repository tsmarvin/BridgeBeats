using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Extensions {

    /// <summary>
    /// Dependency-injection registration helpers for the ATProto (Bluesky PDS) session manager
    /// and storage service in the Infrastructure layer.
    /// </summary>
    public static class StorageServiceExtensions {

        /// <summary>
        /// Registers the Redis-backed ATProto session manager as a singleton implementation of
        /// <see cref="BridgeBeats.Contracts.Interfaces.IATProtoSessionManager"/>, using the supplied
        /// service-account credentials. Must be called before <see cref="AddATProtoStorage"/> and
        /// requires a Redis connection.
        /// </summary>
        /// <param name="services">The service collection to add the registration to.</param>
        /// <param name="atProtoIdentifier">The ATProto service-account identifier (handle or DID).</param>
        /// <param name="atProtoPassword">The ATProto service-account password (app password).</param>
        /// <param name="sessionTtlDays">
        /// Number of days before a dormant Redis session key expires. Defaults to 45. Every successful
        /// persist resets the TTL, so an active session never expires.
        /// </param>
        /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
        /// <remarks>
        /// No registration is performed when <paramref name="atProtoIdentifier"/> or
        /// <paramref name="atProtoPassword"/> is null, empty, or whitespace; the method returns the
        /// service collection unchanged. The implementation resolves the shared
        /// <c>IConnectionMultiplexer</c>, a typed logger, and an <c>IDataProtector</c> created from the
        /// singleton <c>IDataProtectionProvider</c> with the canonical purpose
        /// <c>BridgeBeats.ServiceAccount.ATProtoSession.v1</c>. Registered with singleton lifetime.
        /// </remarks>
        public static IServiceCollection AddATProtoSessionManager(
            this IServiceCollection services,
            string? atProtoIdentifier,
            string? atProtoPassword,
            int sessionTtlDays = 45
        ) {
            if (string.IsNullOrWhiteSpace( atProtoIdentifier ) ||
                string.IsNullOrWhiteSpace( atProtoPassword )) {
                return services;
            }

            _ = services.AddSingleton<IATProtoSessionManager>( sp => {
                IDataProtector protector = sp.GetRequiredService<IDataProtectionProvider>( )
                    .CreateProtector( RedisATProtoSessionManager.SessionProtectorPurpose );
                return new RedisATProtoSessionManager(
                    sp.GetRequiredService<IConnectionMultiplexer>( ),
                    sp.GetRequiredService<ILogger<RedisATProtoSessionManager>>( ),
                    atProtoIdentifier,
                    atProtoPassword,
                    protector,
                    sessionTtlDays
                );
            } );

            return services;
        }

        /// <summary>
        /// Registers the ATProto storage service as a singleton implementation of
        /// <see cref="BridgeBeats.Contracts.Interfaces.IATProtoStorageService"/>, together with the
        /// named HttpClient it uses for repository sync operations. Requires
        /// <see cref="BridgeBeats.Contracts.Interfaces.IATProtoSessionManager"/> to be registered
        /// first via <see cref="AddATProtoSessionManager"/>.
        /// </summary>
        /// <param name="services">The service collection to add the registrations to.</param>
        /// <param name="carCacheTtl">
        /// Time-to-live for the in-process CAR result cache. The cache holds the already-materialized
        /// record list so multiple consumers within one TTL window share a single download. Defaults to
        /// 5 minutes, which covers the startup overlap between the cache-bootstrap and stale-cache
        /// refresh workers while remaining well under the ~1-hour refresh cadence.
        /// </param>
        /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
        /// <remarks>
        /// Configures a named HttpClient (named by <c>ATProtoStorageService.ATProtoSyncHttpClientName</c>)
        /// with a 130-second request timeout for potentially large repository (CAR) downloads. The storage
        /// service resolves the ATProto session manager, a typed logger, and the HttpClient factory at
        /// activation time. Registered with singleton lifetime.
        /// </remarks>
        public static IServiceCollection AddATProtoStorage(
            this IServiceCollection services,
            TimeSpan? carCacheTtl = null
        ) {
            // CAR downloads (com.atproto.sync.getRepo) join the global resilience pipeline.
            // HttpClient.Timeout is a transport backstop above the global 120s AttemptTimeout.
            _ = services
                .AddHttpClient( ATProtoStorageService.ATProtoSyncHttpClientName )
                .ConfigureHttpClient( c => c.Timeout = TimeSpan.FromSeconds( 130 ) );

            TimeSpan ttl = carCacheTtl ?? TimeSpan.FromMinutes( 5 );

            _ = services.AddSingleton<ATProtoStorageService>( sp =>
                new ATProtoStorageService(
                    sp.GetRequiredService<IATProtoSessionManager>( ),
                    sp.GetRequiredService<ILogger<ATProtoStorageService>>( ),
                    sp.GetRequiredService<IHttpClientFactory>( ),
                    ttl
                )
            );
            _ = services.AddSingleton<IATProtoStorageService>( sp =>
                sp.GetRequiredService<ATProtoStorageService>( ) );
            _ = services.AddSingleton<ITargetedATProtoStorageService>( sp =>
                sp.GetRequiredService<ATProtoStorageService>( ) );

            return services;
        }
    }
}
