using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase1CanonicalSchemaAlignment : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "attributes_json",
            schema: "catalog",
            table: "products",
            type: "character varying(8000)",
            maxLength: 8000,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "attributes_schema_version",
            schema: "catalog",
            table: "products",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddCheckConstraint(
            name: "ck_products_attributes_schema_version_positive",
            schema: "catalog",
            table: "products",
            sql: "attributes_schema_version >= 1");

        migrationBuilder.CreateTable(
            name: "stock_adjustments",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                adjustment_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                mode = table.Column<int>(type: "integer", nullable: false),
                reason = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stock_adjustments", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "stock_adjustment_items",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                stock_adjustment_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                direction = table.Column<int>(type: "integer", nullable: false),
                target_bucket = table.Column<int>(type: "integer", nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                total_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                supplier_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                reason_details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stock_adjustment_items", x => x.id);
                table.CheckConstraint("ck_stock_adjustment_items_base_qty_positive", "base_quantity > 0");
                table.ForeignKey(
                    name: "fk_stock_adjustment_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_stock_adjustment_items_stock_adjustments_stock_adjustment_id",
                    column: x => x.stock_adjustment_id,
                    principalSchema: "inventory",
                    principalTable: "stock_adjustments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_stock_adjustment_items_supplier_products_supplier_product_id",
                    column: x => x.supplier_product_id,
                    principalSchema: "catalog",
                    principalTable: "supplier_products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_stock_adjustment_items_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "source_stock_adjustment_item_id",
            schema: "inventory",
            table: "units",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_units_origin_provenance",
            schema: "inventory",
            table: "units",
            sql: "(origin_type = 1 AND source_purchase_item_id IS NOT NULL AND source_warranty_claim_item_id IS NULL AND source_warranty_case_id IS NULL AND source_stock_adjustment_item_id IS NULL) OR (origin_type = 2 AND source_purchase_item_id IS NULL AND source_stock_adjustment_item_id IS NULL AND ((source_warranty_claim_item_id IS NOT NULL AND source_warranty_case_id IS NULL) OR (source_warranty_claim_item_id IS NULL AND source_warranty_case_id IS NOT NULL))) OR (origin_type = 3 AND source_stock_adjustment_item_id IS NOT NULL AND source_purchase_item_id IS NULL AND source_warranty_claim_item_id IS NULL AND source_warranty_case_id IS NULL)");

        migrationBuilder.CreateIndex(
            name: "ix_units_source_stock_adjustment_item_id",
            schema: "inventory",
            table: "units",
            column: "source_stock_adjustment_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustments_adjustment_number",
            schema: "inventory",
            table: "stock_adjustments",
            column: "adjustment_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustments_correlation_id",
            schema: "inventory",
            table: "stock_adjustments",
            column: "correlation_id");

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustments_occurred_at_status",
            schema: "inventory",
            table: "stock_adjustments",
            columns: new[] { "occurred_at", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustment_items_product_id",
            schema: "inventory",
            table: "stock_adjustment_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustment_items_stock_adjustment_id",
            schema: "inventory",
            table: "stock_adjustment_items",
            column: "stock_adjustment_id");

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustment_items_supplier_id",
            schema: "inventory",
            table: "stock_adjustment_items",
            column: "supplier_id");

        migrationBuilder.CreateIndex(
            name: "ix_stock_adjustment_items_supplier_product_id",
            schema: "inventory",
            table: "stock_adjustment_items",
            column: "supplier_product_id");

        migrationBuilder.AddForeignKey(
            name: "fk_units_stock_adjustment_items_source_stock_adjustment_item_id",
            schema: "inventory",
            table: "units",
            column: "source_stock_adjustment_item_id",
            principalSchema: "inventory",
            principalTable: "stock_adjustment_items",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_units_stock_adjustment_items_source_stock_adjustment_item_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropIndex(
            name: "ix_units_source_stock_adjustment_item_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropCheckConstraint(
            name: "ck_inventory_units_origin_provenance",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "source_stock_adjustment_item_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropTable(
            name: "stock_adjustment_items",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "stock_adjustments",
            schema: "inventory");

        migrationBuilder.DropCheckConstraint(
            name: "ck_products_attributes_schema_version_positive",
            schema: "catalog",
            table: "products");

        migrationBuilder.DropColumn(
            name: "attributes_schema_version",
            schema: "catalog",
            table: "products");

        migrationBuilder.DropColumn(
            name: "attributes_json",
            schema: "catalog",
            table: "products");
    }
}
