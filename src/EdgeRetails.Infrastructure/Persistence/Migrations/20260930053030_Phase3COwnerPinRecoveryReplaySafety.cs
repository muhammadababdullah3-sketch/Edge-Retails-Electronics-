using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

public partial class Phase3COwnerPinRecoveryReplaySafety : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateIndex(
            name: "ux_business_events_pin_recovery_success_operation",
            schema: "audit",
            table: "business_events",
            columns: new[] { "correlation_id", "action" },
            unique: true,
            filter: "action = 'USER_PIN_RECOVERY_SUCCEEDED'");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "PIN recovery replay protection is forward-only. Restore a verified backup with a compatible prior application instead of reversing production migration history.");
}
