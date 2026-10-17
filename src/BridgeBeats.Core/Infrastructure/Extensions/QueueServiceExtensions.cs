using BridgeBeats.Contracts.Enums;
using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Contracts.Records;
using BridgeBeats.Core.Infrastructure.Queue;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BridgeBeats.Core.Infrastructure.Extensions;

/// <summary>
/// Dependency-injection registration helpers for the Redis-backed queue infrastructure:
/// request deduplication, rate-limit tracking, saga state management, per-provider request
/// queues, and queue settings.
/// </summary>
public static class QueueServiceExtensions {

    /// <summary>
    /// Registers the shared queue infrastructure singletons: the request deduplicator, the
    /// rate-limit tracker, the saga state manager, and (optionally) queue-depth metrics.
    /// </summary>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <param name="registerMetrics">
    /// When <see langword="true"/> (the default), registers a marker that wires up the queue-depth
    /// gauges against the shared Redis connection. Pass <see langword="false"/> to skip metric
    /// registration.
    /// </param>
    /// <param name="registerDispatchRelay">
    /// When <see langword="true"/>, registers the lookup-dispatch outbox relay. Enable this only in
    /// the host designated as the background relay owner; Saga Coordinator is the current owner.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
    /// <remarks>
    /// Registers <see cref="BridgeBeats.Contracts.Interfaces.IRequestDeduplicator"/>,
    /// <see cref="BridgeBeats.Contracts.Interfaces.IRateLimitTracker"/>,
    /// <see cref="BridgeBeats.Contracts.Interfaces.ILookupDispatchOutbox"/>, and
    /// <see cref="BridgeBeats.Contracts.Interfaces.ISagaStateManager"/>, each with singleton
    /// lifetime and each resolving the shared <c>IConnectionMultiplexer</c> and a typed logger at
    /// activation time. The saga state manager additionally consumes the bound queue settings.
    /// </remarks>
    public static IServiceCollection AddQueueInfrastructure(
        this IServiceCollection services,
        bool registerMetrics = true,
        bool registerDispatchRelay = false
    ) {
        // Register singleton services that are shared across all providers
        _ = services.AddSingleton<IRequestDeduplicator>( sp => new RedisRequestDeduplicator(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<ILogger<RedisRequestDeduplicator>>( )
        ) );

        _ = services.AddSingleton<IRateLimitTracker>( sp => new RedisRateLimitTracker(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( ),
            sp.GetRequiredService<ILogger<RedisRateLimitTracker>>( )
        ) );

        _ = services.AddSingleton<ILookupDispatchOutbox>( sp => new RedisLookupDispatchOutbox(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( ),
            sp.GetRequiredService<ILogger<RedisLookupDispatchOutbox>>( )
        ) );
        if (registerDispatchRelay) {
            _ = services.AddHostedService<LookupDispatchOutboxBackgroundService>( );
        }

        _ = services.AddSingleton<ISagaStateManager>( sp => new RedisSagaStateManager(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<ILogger<RedisSagaStateManager>>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( )
        ) );

        _ = services.AddSingleton<IRefreshReviewStore>( sp => new RedisRefreshReviewStore(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( ),
            sp.GetRequiredService<ILogger<RedisRefreshReviewStore>>( )
        ) );

        // Register queue depth metrics if requested. A hosted service is used so the gauges
        // are registered eagerly at host start after the DI container is built and Redis is
        // connected, rather than lazily on first resolution (which never happens for a marker).
        if (registerMetrics) {
            _ = services.AddHostedService<QueueMetricsRegistration>( );
        }

        return services;
    }

    /// <summary>
    /// Registers a single per-provider Redis request queue for the request type
    /// <typeparamref name="T"/>, wrapped in a registration marker keyed by provider. Call this once
    /// for each provider that needs queue support.
    /// </summary>
    /// <typeparam name="T">
    /// The queueable request type the queue carries. Must be a reference type implementing
    /// <see cref="BridgeBeats.Contracts.Interfaces.IQueueableRequest"/>.
    /// </typeparam>
    /// <param name="services">The service collection to add the registration to.</param>
    /// <param name="provider">The provider this queue serves.</param>
    /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
    /// <remarks>
    /// The queue is registered as a singleton <c>ProviderQueueRegistration&lt;T&gt;</c> so that
    /// multiple provider registrations can be resolved together. Each queue resolves the shared
    /// Redis connection, a typed logger, and the bound queue settings at activation time.
    /// </remarks>
    public static IServiceCollection AddProviderQueue<T>( this IServiceCollection services, SupportedProviders provider )
        where T : class, IQueueableRequest {
        // Use a keyed service pattern - register a factory that creates provider-specific queues
        _ = services.AddSingleton( sp => {
            RedisRequestQueue<T> queue = new(
                sp.GetRequiredService<IConnectionMultiplexer>( ),
                sp.GetRequiredService<ILogger<RedisRequestQueue<T>>>( ),
                sp.GetRequiredService<IOptions<QueueSettings>>( ),
                provider
            );
            return new ProviderQueueRegistration<T>( provider, queue );
        } );

        return services;
    }

    /// <summary>
    /// Registers a per-provider request queue for every supported provider and a
    /// <see cref="BridgeBeats.Contracts.Interfaces.IProviderQueueResolver{T}"/> that resolves
    /// among them.
    /// </summary>
    /// <typeparam name="T">
    /// The queueable request type the queues carry. Must be a reference type implementing
    /// <see cref="BridgeBeats.Contracts.Interfaces.IQueueableRequest"/>.
    /// </typeparam>
    /// <param name="services">The service collection to add the registrations to.</param>
    /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
    /// <remarks>
    /// Calls <see cref="AddProviderQueue{T}(Microsoft.Extensions.DependencyInjection.IServiceCollection,BridgeBeats.Contracts.Enums.SupportedProviders)"/>
    /// once per value of <see cref="BridgeBeats.Contracts.Enums.SupportedProviders"/>, then registers a
    /// singleton resolver built from all provider-queue registrations. When <typeparamref name="T"/> is
    /// <see cref="QueuedLookupRequest"/>, the Spotify queue is wrapped with
    /// <see cref="SpotifyBulkQueueDecorator"/> while building the resolver map, so
    /// <see cref="LookupRequestType.SongIdLookup"/> and <see cref="LookupRequestType.AlbumIdLookup"/>
    /// requests route to the type-specific bulk streams. No additional registration step is required
    /// in any host.
    /// </remarks>
    public static IServiceCollection AddAllProviderQueues<T>( this IServiceCollection services )
        where T : class, IQueueableRequest {
        foreach (SupportedProviders provider in Enum.GetValues<SupportedProviders>( )) {
            _ = services.AddProviderQueue<T>( provider );
        }

        // Register a resolver to get queues by provider. For QueuedLookupRequest, the Spotify
        // queue is wrapped with SpotifyBulkQueueDecorator via ApplySpotifyBulkDecorator so all
        // hosts that produce Spotify ID lookups automatically route to the type-specific bulk
        // streams without any per-host opt-in call.
        _ = services.AddSingleton<IProviderQueueResolver<T>>( sp => {
            IEnumerable<ProviderQueueRegistration<T>> registrations =
                sp.GetServices<ProviderQueueRegistration<T>>( );

            IConnectionMultiplexer redis = sp.GetRequiredService<IConnectionMultiplexer>( );
            ILogger<SpotifyBulkQueueDecorator> decoratorLogger =
                sp.GetRequiredService<ILogger<SpotifyBulkQueueDecorator>>( );

            Dictionary<SupportedProviders, IRequestQueue<T>> queues = registrations.ToDictionary(
                r => r.Provider,
                r => ApplySpotifyBulkDecorator( r.Queue, r.Provider, redis, decoratorLogger )
            );

            return new ProviderQueueResolver<T>( queues );
        } );

        return services;
    }

    /// <summary>
    /// Builds a Redis request queue for queued lookup requests for a single provider, applying the
    /// Spotify bulk decorator where applicable.
    /// </summary>
    /// <param name="sp">The service provider used to resolve the shared Redis connection, a typed logger, and queue settings.</param>
    /// <param name="provider">The provider the queue serves.</param>
    /// <returns>
    /// The provider's <see cref="BridgeBeats.Contracts.Interfaces.IRequestQueue{T}"/> of
    /// <c>QueuedLookupRequest</c>, decorated when the provider is Spotify.
    /// </returns>
    /// <remarks>
    /// Constructs a raw <see cref="RedisRequestQueue{T}"/> and passes it to
    /// <see cref="ApplySpotifyBulkDecorator"/>, which holds the single <c>provider == Spotify</c>
    /// conditional. Both <c>AddQueueProcessor</c> overloads in <c>ServiceExtensions</c> call this to
    /// obtain a correctly wrapped queue without duplicating the wrap decision.
    /// </remarks>
    internal static IRequestQueue<QueuedLookupRequest> CreateProviderQueue(
        IServiceProvider sp,
        SupportedProviders provider
    ) {
        RedisRequestQueue<QueuedLookupRequest> raw = new(
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<ILogger<RedisRequestQueue<QueuedLookupRequest>>>( ),
            sp.GetRequiredService<IOptions<QueueSettings>>( ),
            provider
        );
        return ApplySpotifyBulkDecorator(
            (IRequestQueue<QueuedLookupRequest>)raw,
            provider,
            sp.GetRequiredService<IConnectionMultiplexer>( ),
            sp.GetRequiredService<ILogger<SpotifyBulkQueueDecorator>>( )
        );
    }

    /// <summary>
    /// Wraps a raw provider queue with the Spotify bulk decorator when the provider is Spotify and
    /// the request type is the queued lookup request; otherwise returns the queue unchanged.
    /// </summary>
    /// <typeparam name="T">
    /// The queueable request type. Must be a reference type implementing
    /// <see cref="BridgeBeats.Contracts.Interfaces.IQueueableRequest"/>.
    /// </typeparam>
    /// <param name="raw">The undecorated provider queue.</param>
    /// <param name="provider">The provider the queue serves.</param>
    /// <param name="redis">The shared Redis connection passed to the decorator.</param>
    /// <param name="decoratorLogger">The logger used by the decorator.</param>
    /// <returns>
    /// The decorated queue when <paramref name="provider"/> is Spotify and <typeparamref name="T"/>
    /// is the queued lookup request type; otherwise <paramref name="raw"/>.
    /// </returns>
    /// <remarks>
    /// The double type-erasing cast is safe because the <c>typeof(T)</c> check is performed before
    /// casting. This is the only location in the codebase that performs this cast.
    /// </remarks>
    private static IRequestQueue<T> ApplySpotifyBulkDecorator<T>(
        IRequestQueue<T> raw,
        SupportedProviders provider,
        IConnectionMultiplexer redis,
        ILogger<SpotifyBulkQueueDecorator> decoratorLogger
    ) where T : class, IQueueableRequest {
        if (provider == SupportedProviders.Spotify && typeof( T ) == typeof( QueuedLookupRequest )) {
            // Safe: typeof(T) == typeof(QueuedLookupRequest) verified above.
            return (IRequestQueue<T>)(object)new SpotifyBulkQueueDecorator(
                (IRequestQueue<QueuedLookupRequest>)(object)raw, redis, decoratorLogger );
        }
        return raw;
    }

    /// <summary>
    /// Binds and configures the queue settings options using the supplied delegate.
    /// </summary>
    /// <param name="services">The service collection to add the options configuration to.</param>
    /// <param name="configureAction">A delegate that mutates the queue settings instance.</param>
    /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
    public static IServiceCollection ConfigureQueueSettings(
        this IServiceCollection services,
        Action<QueueSettings> configureAction
    ) {
        _ = services.Configure( configureAction );
        return services;
    }

}

/// <summary>
/// Registration marker that pairs a provider with its request queue so that all per-provider
/// queues can be resolved together when building a provider-queue resolver.
/// </summary>
/// <typeparam name="T">
/// The queueable request type the queue carries. Must be a reference type implementing
/// <see cref="BridgeBeats.Contracts.Interfaces.IQueueableRequest"/>.
/// </typeparam>
/// <param name="Provider">The provider the queue serves.</param>
/// <param name="Queue">The request queue registered for the provider.</param>
internal sealed record ProviderQueueRegistration<T>( SupportedProviders Provider, IRequestQueue<T> Queue )
    where T : class, IQueueableRequest;

/// <summary>
/// Default <see cref="BridgeBeats.Contracts.Interfaces.IProviderQueueResolver{T}"/>
/// implementation backed by a fixed map from provider to request queue.
/// </summary>
/// <typeparam name="T">
/// The queueable request type the queues carry. Must be a reference type implementing
/// <see cref="BridgeBeats.Contracts.Interfaces.IQueueableRequest"/>.
/// </typeparam>
/// <param name="queues">The provider-to-queue map this resolver wraps.</param>
internal sealed class ProviderQueueResolver<T>(
    Dictionary<SupportedProviders,
    IRequestQueue<T>> queues
) : IProviderQueueResolver<T> where T : class, IQueueableRequest {
    /// <summary>The read-only provider-to-queue map this resolver serves lookups from.</summary>
    private readonly IReadOnlyDictionary<SupportedProviders, IRequestQueue<T>> _queues = queues;

    /// <summary>Returns the request queue registered for the given provider.</summary>
    /// <param name="provider">The provider whose queue is requested.</param>
    /// <returns>The request queue registered for <paramref name="provider"/>.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when no queue is registered for <paramref name="provider"/>.
    /// </exception>
    public IRequestQueue<T> GetQueue( SupportedProviders provider ) {
        return !_queues.TryGetValue( provider, out IRequestQueue<T>? queue )
            ? throw new InvalidOperationException( $"No queue registered for provider {provider}" )
            : queue;
    }

    /// <summary>Returns every registered provider queue, keyed by provider.</summary>
    /// <returns>The read-only provider-to-queue map.</returns>
    public IReadOnlyDictionary<SupportedProviders, IRequestQueue<T>> GetAllQueues( ) => _queues;
}

/// <summary>
/// Hosted service that eagerly registers the queue-depth observable gauges at host start, after
/// the DI container is built and the Redis connection is available. Runs once per host startup. The
/// gauge instruments are created only once per process, but the Redis connection the gauge callbacks
/// read from tracks the most recent caller: when several hosts in the same process each register
/// their own connection, the last one to register wins, and an earlier host's connection is no
/// longer read from once superseded — see <see cref="QueueMetrics.RegisterQueueDepthGauges"/>.
/// </summary>
internal sealed class QueueMetricsRegistration( IConnectionMultiplexer redis ) : IHostedService {

    /// <inheritdoc/>
    public Task StartAsync( CancellationToken cancellationToken ) {
        QueueMetrics.RegisterQueueDepthGauges( redis );
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync( CancellationToken cancellationToken ) => Task.CompletedTask;
}
