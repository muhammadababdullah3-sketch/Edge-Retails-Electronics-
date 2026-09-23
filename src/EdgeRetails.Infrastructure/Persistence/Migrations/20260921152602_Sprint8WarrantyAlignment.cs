using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Sprint8WarrantyAlignment : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "inventory_carrying_cost_resolved",
            schema: "warranty",
            table: "shop_stock_cases",
            type: "numeric(18,6)",
            precision: 18,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "recovery_difference",
            schema: "warranty",
            table: "shop_stock_cases",
            type: "numeric(18,6)",
            precision: 18,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "resolution_client_operation_id",
            schema: "warranty",
            table: "shop_stock_cases",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "supplier_credit_amount",
            schema: "warranty",
            table: "shop_stock_cases",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: true);

        migrationBuilder.AddColumn<DateOnly>(
            name: "warranty_valid_until",
            schema: "sales",
            table: "sale_items",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "default_warranty_months",
            schema: "catalog",
            table: "products",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<Guid>(
            name: "active_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_shop_stock_cases_resolution_client_operation_id",
            schema: "warranty",
            table: "shop_stock_cases",
            column: "resolution_client_operation_id",
            unique: true,
            filter: "resolution_client_operation_id IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_products_warranty_months_nonnegative",
            schema: "catalog",
            table: "products",
            sql: "default_warranty_months >= 0");

        migrationBuilder.CreateIndex(
            name: "ix_claim_item_units_active_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units",
            column: "active_original_inventory_unit_id",
            unique: true,
            filter: "active_original_inventory_unit_id IS NOT NULL");

        migrationBuilder.AddForeignKey(
            name: "fk_claim_item_units_units_active_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units",
            column: "active_original_inventory_unit_id",
            principalSchema: "inventory",
            principalTable: "units",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_claim_item_units_units_active_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units");

        migrationBuilder.DropIndex(
            name: "ix_shop_stock_cases_resolution_client_operation_id",
            schema: "warranty",
            table: "shop_stock_cases");

        migrationBuilder.DropCheckConstraint(
            name: "ck_products_warranty_months_nonnegative",
            schema: "catalog",
            table: "products");

        migrationBuilder.DropIndex(
            name: "ix_claim_item_units_active_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units");

        migrationBuilder.DropColumn(
            name: "inventory_carrying_cost_resolved",
            schema: "warranty",
            table: "shop_stock_cases");

        migrationBuilder.DropColumn(
            name: "recovery_difference",
            schema: "warranty",
            table: "shop_stock_cases");

        migrationBuilder.DropColumn(
            name: "resolution_client_operation_id",
            schema: "warranty",
            table: "shop_stock_cases");

        migrationBuilder.DropColumn(
            name: "supplier_credit_amount",
            schema: "warranty",
            table: "shop_stock_cases");

        migrationBuilder.DropColumn(
            name: "warranty_valid_until",
            schema: "sales",
            table: "sale_items");

        migrationBuilder.DropColumn(
            name: "default_warranty_months",
            schema: "catalog",
            table: "products");

        migrationBuilder.DropColumn(
            name: "active_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units");
    }
}
