using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

    /// <inheritdoc />
    public partial class CentralBackupDownloadGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "object_download_grants",
                schema: "central",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_object_download_grants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_object_download_grants_InstallationId_ObjectId_ExpiresAtUtc",
                schema: "central",
                table: "object_download_grants",
                columns: new[] { "InstallationId", "ObjectId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_object_download_grants_TokenSha256",
                schema: "central",
                table: "object_download_grants",
                column: "TokenSha256",
                unique: true);

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                        GRANT SELECT, INSERT ON central.object_download_grants TO central_runtime_user;
                    END IF;
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central054_app') THEN
                        GRANT SELECT, INSERT ON central.object_download_grants TO central054_app;
                    END IF;
                END
                $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "object_download_grants",
                schema: "central");
        }
    }
