using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022160000_BootstrapChallengeRuntimePermissions")]
public sealed class BootstrapChallengeRuntimePermissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central_runtime_user') THEN
                    GRANT SELECT, INSERT, UPDATE ON central.bootstrap_challenges TO central_runtime_user;
                END IF;
                IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'central054_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON central.bootstrap_challenges TO central054_app;
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Bootstrap replay protection is a security boundary and cannot be downgraded.");
}
