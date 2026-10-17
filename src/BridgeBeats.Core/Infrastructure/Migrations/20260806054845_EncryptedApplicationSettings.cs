using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BridgeBeats.Core.Infrastructure.Migrations {
    /// <inheritdoc />
    public partial class EncryptedApplicationSettings : Migration {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder ) {
            _ = migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                columns: table => new {
                    Id = table.Column<int>( type: "INTEGER", nullable: false ),
                    SchemaVersion = table.Column<int>( type: "INTEGER", nullable: false ),
                    ValuesJson = table.Column<string>( type: "TEXT", nullable: false ),
                    SecretsProtectionScheme = table.Column<string>( type: "TEXT", maxLength: 64, nullable: false ),
                    ProtectedSecrets = table.Column<string>( type: "TEXT", nullable: false ),
                    Revision = table.Column<string>( type: "TEXT", maxLength: 32, nullable: false ),
                    UpdatedAtUtc = table.Column<DateTimeOffset>( type: "TEXT", nullable: false )
                },
                constraints: table => {
                    _ = table.PrimaryKey( "PK_ApplicationSettings", x => x.Id );
                    _ = table.CheckConstraint( "CK_ApplicationSettings_Singleton", "\"Id\" = 1" );
                } );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder ) {
            _ = migrationBuilder.DropTable(
                name: "ApplicationSettings" );
        }
    }
}
