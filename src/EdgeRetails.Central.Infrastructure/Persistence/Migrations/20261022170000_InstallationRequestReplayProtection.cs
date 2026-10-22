using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022170000_InstallationRequestReplayProtection")]
public sealed class InstallationRequestReplayProtection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "installation_request_nonces",
            schema: "central",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                Nonce = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_installation_request_nonces", x => x.Id));

        migrationBuilder.CreateIndex("IX_installation_request_nonces_InstallationId_Nonce", "installation_request_nonces", new[] { "InstallationId", "Nonce" }, schema: "central", unique: true);
        migrationBuilder.CreateIndex("IX_installation_request_nonces_TimestampUtc", "installation_request_nonces", "TimestampUtc", schema: "central");
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                    GRANT SELECT, INSERT, DELETE ON central.installation_request_nonces TO central_runtime_user;
                END IF;
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central054_app') THEN
                    GRANT SELECT, INSERT, DELETE ON central.installation_request_nonces TO central054_app;
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Installation replay protection is a security boundary and cannot be downgraded.");
}
