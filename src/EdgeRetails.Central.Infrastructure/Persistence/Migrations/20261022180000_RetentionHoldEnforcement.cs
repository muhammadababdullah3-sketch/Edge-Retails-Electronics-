using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Central.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CentralDbContext))]
[Migration("20261022180000_RetentionHoldEnforcement")]
public sealed class RetentionHoldEnforcement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(name: "RetentionUntilUtc", schema: "central", table: "upload_sessions", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsUnderLegalHold", schema: "central", table: "upload_sessions", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "RetentionUntilUtc", schema: "central", table: "encrypted_objects", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsUnderLegalHold", schema: "central", table: "encrypted_objects", type: "boolean", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Object retention and legal-hold protections cannot be downgraded.");
}
