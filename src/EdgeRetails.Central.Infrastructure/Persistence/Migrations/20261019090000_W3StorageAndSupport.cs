using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261019090000_W3StorageAndSupport")]
public partial class W3StorageAndSupport : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "encrypted_objects",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ContentLength = table.Column<long>(type: "bigint", nullable: false),
                ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CiphertextSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                RetentionPolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                LocalManifestAuthenticationOpaque = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                IsTombstoned = table.Column<bool>(type: "boolean", nullable: false),
                TombstonedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RowVersion = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_encrypted_objects", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_encrypted_objects_ShopId",
            schema: "central",
            table: "encrypted_objects",
            column: "ShopId");

        migrationBuilder.CreateIndex(
            name: "IX_encrypted_objects_InstallationId",
            schema: "central",
            table: "encrypted_objects",
            column: "InstallationId");

        migrationBuilder.CreateIndex(
            name: "IX_encrypted_objects_RetentionPolicyId",
            schema: "central",
            table: "encrypted_objects",
            column: "RetentionPolicyId");

        migrationBuilder.CreateTable(
            name: "support_tickets",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ShopId = table.Column<Guid>(type: "uuid", nullable: false),
                RequesterPrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Summary = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RedactionProfileVersion = table.Column<int>(type: "integer", nullable: false),
                AttachmentObjectIdsJson = table.Column<string>(type: "text", nullable: false),
                RepliesJson = table.Column<string>(type: "text", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RowVersion = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_support_tickets", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_support_tickets_ShopId",
            schema: "central",
            table: "support_tickets",
            column: "ShopId");

        migrationBuilder.CreateIndex(
            name: "IX_support_tickets_RequesterPrincipalId",
            schema: "central",
            table: "support_tickets",
            column: "RequesterPrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_support_tickets_Status",
            schema: "central",
            table: "support_tickets",
            column: "Status");

        migrationBuilder.Sql(@"
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                    GRANT USAGE ON SCHEMA central TO central_runtime_user;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA central TO central_runtime_user;
                    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA central TO central_runtime_user;
                    REVOKE UPDATE, DELETE, TRUNCATE ON central.audit_records FROM central_runtime_user;
                END IF;
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central054_app') THEN
                    GRANT USAGE ON SCHEMA central TO central054_app;
                    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA central TO central054_app;
                    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA central TO central054_app;
                    REVOKE UPDATE, DELETE, TRUNCATE ON central.audit_records FROM central054_app;
                END IF;
            END
            $$;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "encrypted_objects",
            schema: "central");

        migrationBuilder.DropTable(
            name: "support_tickets",
            schema: "central");
    }
}
