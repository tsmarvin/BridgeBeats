using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BridgeBeats.Core.Infrastructure.Extensions {

    /// <summary>
    /// Dependency-injection registration helpers for ASP.NET Core Identity and Data Protection
    /// as configured for BridgeBeats.
    /// </summary>
    public static class IdentityServiceExtensions {

        /// <summary>
        /// Registers ASP.NET Core Data Protection and Identity Core for BridgeBeats, wiring the
        /// application user store, roles, sign-in manager, token providers, and personal-data
        /// protection.
        /// </summary>
        /// <param name="services">The service collection to add the registrations to.</param>
        /// <param name="dataProtectionKeyPath">
        /// Filesystem directory where Data Protection persists its key ring. Defaults to
        /// <c>./keys</c>. In Docker, this should be a mounted volume (for example <c>/app/keys</c>) so
        /// keys survive container restarts and existing data stays decryptable.
        /// </param>
        /// <returns>The same <paramref name="services"/> instance, to allow call chaining.</returns>
        /// <remarks>
        /// Delegates the Data Protection registration to
        /// <see cref="DataProtectionExtensions.AddBridgeBeatsDataProtection"/> with application name
        /// <c>BridgeBeats</c> and the supplied key path. Identity Core is configured with a password
        /// policy requiring digits, lower- and upper-case letters, a non-alphanumeric character, and a
        /// minimum length of 14, and with unique-email enforcement disabled at the Identity validator
        /// level. It adds roles, the Entity Framework user store backed by the application database
        /// context, the sign-in manager, default token providers, and personal-data protection. The
        /// <see cref="DataProtectionLookupProtector"/> and <see cref="DataProtectionKeyRing"/> are
        /// registered for Identity personal-data support. The Apple Music token uses a separate
        /// <c>IDataProtector</c> wired in <c>ApplicationDbContext.OnModelCreating</c> via a value
        /// converter — not <c>[ProtectedPersonalData]</c>. A scoped application database context is
        /// also registered, created from the registered context factory.
        /// </remarks>
        public static IServiceCollection AddBridgeBeatsIdentity(
            this IServiceCollection services,
            string dataProtectionKeyPath = DataProtectionExtensions.DefaultKeyPath
        ) {
            // Delegate to the shared DP extension so the app-name and key-path convention is defined
            // in exactly one place across all three host processes.
            _ = services.AddBridgeBeatsDataProtection( dataProtectionKeyPath );

            _ = services.AddIdentityCore<ApplicationUser>( options => {
                // Password settings
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredLength = 14;

                // User settings
                // RequireUniqueEmail must be false because ATProto-only users have no email
                // (ATProto OAuth does not supply one, and null email cannot satisfy "unique non-empty").
                // Uniqueness for password-account emails is enforced explicitly in AccountController.Register.
                options.User.RequireUniqueEmail = false;
            } )
            .AddRoles<IdentityRole>( )
            .AddEntityFrameworkStores<ApplicationDbContext>( )
            .AddSignInManager( )
            .AddDefaultTokenProviders( )
            .AddPersonalDataProtection<DataProtectionLookupProtector, DataProtectionKeyRing>( );

            // Register scoped ApplicationDbContext for Identity framework using the factory
            _ = services.AddScoped( sp => {
                IDbContextFactory<ApplicationDbContext> factory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>( );
                return factory.CreateDbContext( );
            } );

            return services;
        }
    }
}
