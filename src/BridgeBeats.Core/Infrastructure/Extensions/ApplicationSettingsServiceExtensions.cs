using BridgeBeats.Contracts.Interfaces;
using BridgeBeats.Core.Infrastructure.Settings;

namespace BridgeBeats.Core.Infrastructure.Extensions;

/// <summary>Dependency-injection registration for database-backed application settings.</summary>
public static class ApplicationSettingsServiceExtensions {
    /// <summary>Adds the typed database-backed application-settings service.</summary>
    /// <param name="services">The service collection to populate.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddDatabaseApplicationSettings( this IServiceCollection services ) {
        _ = services.AddSingleton<DatabaseApplicationSettingsService>( );
        _ = services.AddSingleton<IApplicationSettingsService>( serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseApplicationSettingsService>( )
        );
        _ = services.AddSingleton<IApplicationSettingsRuntimeReader>( serviceProvider =>
            serviceProvider.GetRequiredService<DatabaseApplicationSettingsService>( )
        );
        return services;
    }
}
