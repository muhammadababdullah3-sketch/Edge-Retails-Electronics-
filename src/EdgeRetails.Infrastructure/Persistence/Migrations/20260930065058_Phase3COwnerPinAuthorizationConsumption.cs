using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3COwnerPinAuthorizationConsumption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_business_events_pin_recovery_consumed_nonce",
                schema: "audit",
                table: "business_events",
                columns: new[] { "correlation_id", "action" },
                unique: true,
                filter: "action = 'USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Recovery authorization nonce consumption is forward-only. Restore a verified backup with a compatible prior application instead of reversing production migration history.");
        }
    }
}
