using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022123000_AuditImmutabilityErrorContract")]
public sealed class AuditImmutabilityErrorContract : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION central.reject_audit_mutation()
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'AUDIT_IMMUTABILITY_VIOLATION: audit_records are append-only'
                    USING ERRCODE = '42501';
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Audit immutability is a security boundary and cannot be downgraded.");
}
