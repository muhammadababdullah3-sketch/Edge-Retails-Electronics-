using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3ProductionSafetyOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_products_nonnegative_prices",
                schema: "catalog",
                table: "products");

            migrationBuilder.AddColumn<string>(
                name: "brand",
                schema: "catalog",
                table: "products",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "minimum_stock_level",
                schema: "catalog",
                table: "products",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "model",
                schema: "catalog",
                table: "products",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "system",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    effect_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    source_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_nonnegative_prices",
                schema: "catalog",
                table: "products",
                sql: "default_sale_price >= 0 AND minimum_stock_level >= 0 AND (reference_purchase_cost IS NULL OR reference_purchase_cost >= 0)");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_idempotency_key",
                schema: "system",
                table: "outbox_messages",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_status_next_attempt",
                schema: "system",
                table: "outbox_messages",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "system");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_nonnegative_prices",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "brand",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "minimum_stock_level",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "model",
                schema: "catalog",
                table: "products");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_nonnegative_prices",
                schema: "catalog",
                table: "products",
                sql: "default_sale_price >= 0 AND (reference_purchase_cost IS NULL OR reference_purchase_cost >= 0)");
        }
    }
}
