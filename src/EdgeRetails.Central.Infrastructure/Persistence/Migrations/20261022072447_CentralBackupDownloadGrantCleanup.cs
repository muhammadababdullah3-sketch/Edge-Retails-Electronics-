using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

    /// <inheritdoc />
    public partial class CentralBackupDownloadGrantCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_object_download_grants_ExpiresAtUtc",
                schema: "central",
                table: "object_download_grants",
                column: "ExpiresAtUtc");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                        GRANT DELETE ON central.object_download_grants TO central_runtime_user;
                    END IF;
                    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central054_app') THEN
                        GRANT DELETE ON central.object_download_grants TO central054_app;
                    END IF;
                END
                $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_object_download_grants_ExpiresAtUtc",
                schema: "central",
                table: "object_download_grants");
        }
    }
