using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase89ShopTimeZoneAndLabelPrintIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "production");

            migrationBuilder.Sql("""
                ALTER TABLE system.shop_profile
                    ADD COLUMN IF NOT EXISTS time_zone_id character varying(100) NOT NULL DEFAULT '';
                """);

            migrationBuilder.CreateTable(
                name: "physical_label_print_intent",
                schema: "production",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    target_workstation_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_physical_label_print_intent", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_physical_label_print_intent_inventory_unit_id_kind",
                schema: "production",
                table: "physical_label_print_intent",
                columns: new[] { "inventory_unit_id", "kind" },
                unique: true,
                filter: "kind = 0");

            migrationBuilder.CreateIndex(
                name: "ix_physical_label_print_intent_source_client_operation_id",
                schema: "production",
                table: "physical_label_print_intent",
                column: "source_client_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_physical_label_print_intent_status",
                schema: "production",
                table: "physical_label_print_intent",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "physical_label_print_intent",
                schema: "production");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                schema: "system",
                table: "shop_profile");
        }
    }
}
