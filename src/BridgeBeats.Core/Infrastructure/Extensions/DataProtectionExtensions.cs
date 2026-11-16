using Microsoft.AspNetCore.DataProtection;

namespace BridgeBeats.Core.Infrastructure.Extensions {

    /// <summary>
    /// Dependency-injection registration helper for the BridgeBeats shared Data Protection
    /// foundation, used by all three host processes (Web, SagaCoordinator, Maintenance) to
    /// ensure values encrypted in one process can be decrypted in another.
    /// </summary>
    public static class DataProtectionExtensions {

        /// <summary>
        /// The default filesystem directory for the Data Protection key ring when no explicit path is
        /// configured. Defined once here so that the Web host (<c>AppSettings.DataProtectionKeyPath</c>),
        /// the SagaCoordinator, and the Maintenance worker all fall back to the same value and a
        /// future edit cannot desync the cross-process key path.
        /// </summary>
        public const string DefaultKeyPath = "./keys";

        /// <summary>
        /// Adds ASP.NET Core Data Protection configured for BridgeBeats: application name
        /// <c>BridgeBeats</c> with keys persisted to <paramref name="keyPath"/>. All three host
        /// processes must call this method with the same key path so the shared key ring is
        /// accessible everywhere.
        /// </summary>
        /// <param name="services">The service collection to add the registration to.</param>
        /// <param name="keyPath">
        /// Filesystem directory where Data Protection persists its key ring. In Docker this must be
        /// a mounted volume (for example <c>/app/keys</c>) so keys survive container restarts and
        /// encrypted data stays decryptable across process boundaries.
        /// </param>
        /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
        public static IServiceCollection AddBridgeBeatsDataProtection(
            this IServiceCollection services,
            string keyPath
        ) {
            _ = services.AddDataProtection( )
                .SetApplicationName( "BridgeBeats" )
                .PersistKeysToFileSystem( new DirectoryInfo( keyPath ) );

            return services;
        }
    }
}
