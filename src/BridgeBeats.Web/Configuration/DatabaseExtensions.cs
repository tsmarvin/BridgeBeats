using BridgeBeats.Contracts.Constants;
using BridgeBeats.Core.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BridgeBeats.Web.Configuration;

/// <summary>
/// Startup helpers that apply database migrations, reset stale rate-limit state, and seed required roles.
/// </summary>
public static class DatabaseExtensions {
    /// <summary>
    /// Applies any pending Identity database migrations, clears rate-limit counters whose window has
    /// elapsed, and seeds the application's required roles.
    /// </summary>
    /// <param name="app">The web application whose service provider supplies the database context and role manager.</param>
    /// <returns>The same <paramref name="app"/> instance, to allow call chaining.</returns>
    public static async Task<WebApplication> InitializeDatabaseAsync( this WebApplication app ) {
        using IServiceScope scope = app.Services.CreateScope( );
        ApplicationDbContext context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>( );
        RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>( );

        // Apply pending migrations and create the database if it doesn't exist
        await context.Database.MigrateAsync( );

        // Recovery pass: clear any rate-limit windows that have already expired so users
        // pinned by the pre-fix non-persisted-reset bug are released on startup. Idempotent —
        // a no-op once windows are fresh, safe to run on every boot.
        DateTime windowFloor = DateTime.UtcNow.AddHours( -1 );
        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE AspNetUsers
               SET RequestCount = 0,
                   RateLimitWindowStart = NULL
               WHERE RateLimitWindowStart IS NOT NULL
                 AND RateLimitWindowStart <= {windowFloor}" );

        // Seed the AspireDashboardAccess role if it doesn't exist
        await SeedRolesAsync( roleManager );

        return app;
    }

    /// <summary>
    /// Creates the roles the application depends on if they do not already exist.
    /// </summary>
    /// <param name="roleManager">The Identity role manager used to check for and create roles.</param>
    /// <returns>A task that completes when the required roles are present.</returns>
    private static async Task SeedRolesAsync( RoleManager<IdentityRole> roleManager ) {
        // Create AspireDashboardAccess role if it doesn't exist
        if (!await roleManager.RoleExistsAsync( Roles.AspireDashboardAccess )) {
            _ = await roleManager.CreateAsync( new IdentityRole( Roles.AspireDashboardAccess ) );
        }
        if (!await roleManager.RoleExistsAsync( Roles.PdsRecordAdministrator )) {
            _ = await roleManager.CreateAsync( new IdentityRole( Roles.PdsRecordAdministrator ) );
        }
    }
}
