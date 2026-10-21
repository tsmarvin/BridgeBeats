using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BridgeBeats.Core.Infrastructure.Migrations {
    /// <inheritdoc />
    public partial class UseBlueskyAuthenticationStores : Migration {
        /// <inheritdoc />
        protected override void Up( MigrationBuilder migrationBuilder ) {
            // SDK v8.0.0 SQLite schema. The SDK owns these tables; EF owns their deployment lifecycle.
            _ = migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "idunno_bluesky_identities"
                (
                    "Did" TEXT NOT NULL COLLATE BINARY,
                    "Identity" BLOB NOT NULL,
                    "ExpiresAtUtcTicks" INTEGER NOT NULL,
                    PRIMARY KEY ("Did")
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS "IX_idunno_bluesky_identities_ExpiresAtUtcTicks"
                    ON "idunno_bluesky_identities" ("ExpiresAtUtcTicks");

                CREATE TABLE IF NOT EXISTS "idunno_bluesky_refresh_locks"
                (
                    "Did" TEXT NOT NULL COLLATE BINARY,
                    "LockToken" TEXT NOT NULL COLLATE BINARY,
                    "ExpiresAtUtcTicks" INTEGER NOT NULL,
                    PRIMARY KEY ("Did")
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS "IX_idunno_bluesky_refresh_locks_ExpiresAtUtcTicks"
                    ON "idunno_bluesky_refresh_locks" ("ExpiresAtUtcTicks");

                CREATE TABLE IF NOT EXISTS "idunno_bluesky_correlation_states"
                (
                    "CorrelationId" BLOB NOT NULL,
                    "State" TEXT NOT NULL,
                    "ExpiresAtUtcTicks" INTEGER NOT NULL,
                    PRIMARY KEY ("CorrelationId")
                ) WITHOUT ROWID;

                CREATE INDEX IF NOT EXISTS "IX_idunno_bluesky_correlation_states_ExpiresAtUtcTicks"
                    ON "idunno_bluesky_correlation_states" ("ExpiresAtUtcTicks");

                """ );

            _ = migrationBuilder.DropTable(
                name: "AtProtoOAuthStates" );

            // Native SQLite DROP COLUMN keeps this migration transactional and avoids rebuilding Identity tables.
            _ = migrationBuilder.Sql(
                """
                ALTER TABLE AspNetUsers DROP COLUMN AtProtoAccessToken;
                ALTER TABLE AspNetUsers DROP COLUMN AtProtoDPoPKey;
                ALTER TABLE AspNetUsers DROP COLUMN AtProtoRefreshToken;
                ALTER TABLE AspNetUsers DROP COLUMN AtProtoTokenExpiration;
                """ );
        }

        /// <inheritdoc />
        protected override void Down( MigrationBuilder migrationBuilder ) {
            _ = migrationBuilder.Sql( "DROP TABLE idunno_bluesky_correlation_states; DROP TABLE idunno_bluesky_refresh_locks; DROP TABLE idunno_bluesky_identities;" );

            _ = migrationBuilder.AddColumn<string>(
                name: "AtProtoAccessToken",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true );

            _ = migrationBuilder.AddColumn<string>(
                name: "AtProtoDPoPKey",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true );

            _ = migrationBuilder.AddColumn<string>(
                name: "AtProtoRefreshToken",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true );

            _ = migrationBuilder.AddColumn<DateTime>(
                name: "AtProtoTokenExpiration",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true );

            _ = migrationBuilder.CreateTable(
                name: "AtProtoOAuthStates",
                columns: table => new {
                    State = table.Column<string>( type: "TEXT", maxLength: 128, nullable: false ),
                    AuthorizationServerUri = table.Column<string>( type: "TEXT", maxLength: 512, nullable: true ),
                    CodeVerifier = table.Column<string>( type: "TEXT", maxLength: 512, nullable: false ),
                    CreatedAt = table.Column<DateTime>( type: "TEXT", nullable: false ),
                    DPoPKeyJwk = table.Column<string>( type: "TEXT", nullable: false ),
                    Did = table.Column<string>( type: "TEXT", maxLength: 128, nullable: true ),
                    ExpiresAt = table.Column<DateTime>( type: "TEXT", nullable: false ),
                    Handle = table.Column<string>( type: "TEXT", maxLength: 256, nullable: false ),
                    PdsUri = table.Column<string>( type: "TEXT", maxLength: 512, nullable: true )
                },
                constraints: table => {
                    _ = table.PrimaryKey( "PK_AtProtoOAuthStates", x => x.State );
                } );

            _ = migrationBuilder.CreateIndex(
                name: "IX_AtProtoOAuthStates_ExpiresAt",
                table: "AtProtoOAuthStates",
                column: "ExpiresAt" );
        }
    }
}
