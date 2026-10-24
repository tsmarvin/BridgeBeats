using BridgeBeats.Core.Infrastructure.Playlists;
using BridgeBeats.Core.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BridgeBeats.Core.Infrastructure.Identity;

/// <summary>
/// Entity Framework Core database context for the application, extending the ASP.NET Core
/// Identity context with playlist and ATProto OAuth-state entities.
/// </summary>
/// <param name="options">The options used to configure the context.</param>
/// <param name="tokenProtector">
/// An optional <see cref="IDataProtector"/> applied as an EF value converter to the four credential
/// columns (<c>AppleMusicUserToken</c>, <c>AtProtoAccessToken</c>, <c>AtProtoRefreshToken</c>,
/// <c>AtProtoDPoPKey</c>) so they are stored encrypted at rest. When <see langword="null"/> the
/// converter is not installed — this is the design-time factory path so <c>dotnet ef migrations</c>
/// tooling works without a key ring.
/// </param>
public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    IDataProtector? tokenProtector = null
) : IdentityDbContext<ApplicationUser>( options ) {

    /// <summary>
    /// Purpose string for the user-token column encryptor. Standalone and versioned, independent of
    /// the key-ring version and the service-account session purpose string.
    /// </summary>
    public const string TokenProtectorPurpose = "BridgeBeats.ApplicationUser.Tokens.v1";

    /// <summary>
    /// Indicates whether a token protector was supplied to this context instance. Used by
    /// <see cref="ApplicationDbContextModelCacheKeyFactory"/> to bucket EF Core's process-wide model
    /// cache so a null-protector model and a protector-bearing model never share a cache entry.
    /// </summary>
    internal bool TokenProtectorPresent { get; } = tokenProtector is not null;

    /// <summary>
    /// Gets or sets the set of stored playlist entries, created by users or anonymously.
    /// </summary>
    public DbSet<PlaylistEntry> Playlists { get; set; }

    /// <summary>
    /// Gets or sets the set of transient ATProto OAuth state rows held during the authentication flow.
    /// </summary>
    public DbSet<AtProtoOAuthState> AtProtoOAuthStates { get; set; }

    /// <summary>Gets the singleton database-backed application-settings record.</summary>
    internal DbSet<ApplicationSettingsRecord> ApplicationSettings { get; set; }

    /// <summary>
    /// Registers the <see cref="ApplicationDbContextModelCacheKeyFactory"/> so that every
    /// construction path — runtime factory, design-time factory — produces a model-cache key
    /// that reflects whether a token protector is present. This prevents a null-protector model
    /// from being cached and later reused for a protector-bearing context, which would silently
    /// drop the value converters.
    /// </summary>
    /// <param name="optionsBuilder">The builder used to configure the context options.</param>
    protected override void OnConfiguring( DbContextOptionsBuilder optionsBuilder ) {
        base.OnConfiguring( optionsBuilder );
        _ = optionsBuilder.ReplaceService<IModelCacheKeyFactory, ApplicationDbContextModelCacheKeyFactory>( );
    }

    /// <summary>
    /// Configures the entity model, including indexes and column constraints.
    /// </summary>
    /// <remarks>
    /// Notable index nuances:
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <see cref="ApplicationUser.ApiKeyHash"/> has a unique index that is not filtered for NULL,
    /// which differs from the DID index below.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <see cref="ApplicationUser.AtProtoDid"/> has a unique index filtered to non-null values,
    /// allowing many users without a DID but at most one per DID.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>NormalizedEmail</c> has a unique index named <c>EmailIndex</c>. This database constraint
    /// enforces email uniqueness even though Identity's own <c>RequireUniqueEmail</c> option is false.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    /// <param name="builder">The model builder used to construct the schema.</param>
    protected override void OnModelCreating( ModelBuilder builder ) {
        base.OnModelCreating( builder );

        // Lane B: encrypt the four credential columns at rest via a NULL-transparent value converter.
        // Applied only when a protector is supplied; the design-time factory passes null so EF tooling
        // works without a key ring. The converter is string→string (schema-transparent: column type
        // stays TEXT and the snapshot carries only an annotation, no DDL change). The indexed columns
        // AtProtoDid, AtProtoHandle, and ApiKeyHash are intentionally excluded so lookups remain exact.
        if (tokenProtector is not null) {
            ValueConverter<string?, string?> encryptConverter = new(
                plaintext => plaintext != null ? tokenProtector.Protect( plaintext ) : null,
                ciphertext => ciphertext != null ? tokenProtector.Unprotect( ciphertext ) : null
            );

            _ = builder.Entity<ApplicationUser>( )
                .Property( u => u.AppleMusicUserToken )
                .HasConversion( encryptConverter );

            _ = builder.Entity<ApplicationUser>( )
                .Property( u => u.AtProtoAccessToken )
                .HasConversion( encryptConverter );

            _ = builder.Entity<ApplicationUser>( )
                .Property( u => u.AtProtoRefreshToken )
                .HasConversion( encryptConverter );

            _ = builder.Entity<ApplicationUser>( )
                .Property( u => u.AtProtoDPoPKey )
                .HasConversion( encryptConverter );
        }

        _ = builder.Entity<ApplicationSettingsRecord>( entity => {
            _ = entity.ToTable(
                "ApplicationSettings",
                table => table.HasCheckConstraint( "CK_ApplicationSettings_Singleton", "\"Id\" = 1" )
            );
            _ = entity.HasKey( record => record.Id );
            _ = entity.Property( record => record.Id ).ValueGeneratedNever( );
            _ = entity.Property( record => record.SchemaVersion ).IsRequired( );
            _ = entity.Property( record => record.ValuesJson ).IsRequired( );
            _ = entity.Property( record => record.SecretsProtectionScheme )
                .IsRequired( )
                .HasMaxLength( 64 );
            _ = entity.Property( record => record.ProtectedSecrets ).IsRequired( );
            _ = entity.Property( record => record.Revision )
                .IsRequired( )
                .HasMaxLength( 32 )
                .IsConcurrencyToken( );
            _ = entity.Property( record => record.UpdatedAtUtc ).IsRequired( );
        } );

        // Add indexes for performance
        _ = builder.Entity<ApplicationUser>( )
            .HasIndex( u => u.ApiKeyHash )
            .IsUnique( );

        // Add unique index for ATProto DID
        _ = builder.Entity<ApplicationUser>( )
            .HasIndex( u => u.AtProtoDid )
            .IsUnique( )
            .HasFilter( "[AtProtoDid] IS NOT NULL" );

        // Upgrade Identity's default EmailIndex to unique: closes the Register TOCTOU race by
        // converting the window between FindByEmailAsync and CreateAsync into a DB constraint
        // violation caught in AccountController.Register. SQLite allows multiple NULLs in a
        // unique index, so email-less ATProto users are unaffected.
        _ = builder.Entity<ApplicationUser>( )
            .HasIndex( u => u.NormalizedEmail )
            .IsUnique( )
            .HasDatabaseName( "EmailIndex" );

        // Configure PlaylistEntry
        _ = builder.Entity<PlaylistEntry>( entity => {
            _ = entity.HasKey( e => e.PlaylistId );
            _ = entity.Property( e => e.PlaylistId )
                .IsRequired( )
                .HasMaxLength( 64 );
            _ = entity.Property( e => e.Title )
                .HasMaxLength( 200 );
            _ = entity.Property( e => e.Description )
                .HasMaxLength( 1000 );
            _ = entity.Property( e => e.CardIds )
                .IsRequired( )
                .HasMaxLength( 2000 ); // Support up to 20 card IDs
            _ = entity.Property( e => e.CardRkeys )
                .IsRequired( )
                .HasMaxLength( 2000 ); // Support up to 20 rkeys
            _ = entity.Property( e => e.UserId )
                .HasMaxLength( 450 ); // Standard ASP.NET Identity user ID length
            _ = entity.Property( e => e.CreatedAt )
                .IsRequired( );
            _ = entity.Property( e => e.ExpiresAt );

            _ = entity.HasIndex( e => e.UserId );
            _ = entity.HasIndex( e => e.CreatedAt );
            _ = entity.HasIndex( e => e.ExpiresAt );
        } );

        // Configure AtProtoOAuthState for temporary OAuth state storage
        _ = builder.Entity<AtProtoOAuthState>( entity => {
            _ = entity.HasKey( e => e.State );
            _ = entity.Property( e => e.State )
                .IsRequired( )
                .HasMaxLength( 128 );
            _ = entity.Property( e => e.CodeVerifier )
                .IsRequired( )
                .HasMaxLength( 512 );
            _ = entity.Property( e => e.Handle )
                .IsRequired( )
                .HasMaxLength( 256 );
            _ = entity.Property( e => e.Did )
                .HasMaxLength( 128 );
            _ = entity.Property( e => e.PdsUri )
                .HasMaxLength( 512 );
            _ = entity.Property( e => e.AuthorizationServerUri )
                .HasMaxLength( 512 );
            _ = entity.Property( e => e.DPoPKeyJwk )
                .IsRequired( );
            _ = entity.Property( e => e.CreatedAt )
                .IsRequired( );
            _ = entity.Property( e => e.ExpiresAt )
                .IsRequired( );

            _ = entity.HasIndex( e => e.ExpiresAt );
        } );
    }
}
