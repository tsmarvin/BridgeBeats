using BridgeBeats.Contracts.Records;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BridgeBeats.Core.Domain.Extensions;

/// <summary>Registers queue settings with the invariants required by durable retry processing.</summary>
public static class QueueSettingsServiceCollectionExtensions {
    /// <summary>Binds and validates the shared queue configuration at host startup.</summary>
    public static IServiceCollection AddValidatedQueueSettings(
        this IServiceCollection services,
        IConfiguration configuration
    ) {
        _ = services.AddOptions<QueueSettings>( )
            .Bind( configuration.GetSection( "BridgeBeats:Queue" ) )
            .Validate( settings => settings.JobExpirationMinutes > 0,
                "BridgeBeats:Queue:JobExpirationMinutes must be greater than zero." )
            .Validate( settings => settings.RateLimitMinimumRetryAfter > TimeSpan.Zero,
                "BridgeBeats:Queue:RateLimitMinimumRetryAfter must be greater than zero." )
            .Validate( settings => settings.RateLimitDefaultRetryAfter >= settings.RateLimitMinimumRetryAfter,
                "BridgeBeats:Queue:RateLimitDefaultRetryAfter must be at least RateLimitMinimumRetryAfter." )
            .Validate( settings => settings.RateLimitMaximumRetryAfter >= settings.RateLimitDefaultRetryAfter,
                "BridgeBeats:Queue:RateLimitMaximumRetryAfter must be at least RateLimitDefaultRetryAfter." )
            .Validate( settings => settings.RateLimitMaximumRetryAfter <= TimeSpan.FromMinutes( settings.JobExpirationMinutes ),
                "BridgeBeats:Queue:RateLimitMaximumRetryAfter must not exceed JobExpirationMinutes." )
            .ValidateOnStart( );

        return services;
    }
}
