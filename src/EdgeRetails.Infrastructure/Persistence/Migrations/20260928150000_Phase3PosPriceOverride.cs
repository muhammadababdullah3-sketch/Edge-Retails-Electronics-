using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

[DbContext(typeof(EdgeRetailsDbContext))]
[Migration("20260928150000_Phase3PosPriceOverride")]
public sealed class Phase3PosPriceOverride : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "list_unit_price_snapshot",
            schema: "sales",
            table: "sale_items",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            defaultValue: 0m);

        migrationBuilder.AddColumn<string>(
            name: "price_override_reason",
            schema: "sales",
            table: "sale_items",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "price_override_by",
            schema: "sales",
            table: "sale_items",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql(
            "UPDATE sales.sale_items SET list_unit_price_snapshot = unit_price;");

        migrationBuilder.AlterColumn<decimal>(
            name: "list_unit_price_snapshot",
            schema: "sales",
            table: "sale_items",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(18,2)",
            oldPrecision: 18,
            oldScale: 2,
            oldDefaultValue: 0m);

        migrationBuilder.AddCheckConstraint(
            name: "ck_sale_item_price_override_audit",
            schema: "sales",
            table: "sale_items",
            sql: "(price_override_reason IS NULL AND price_override_by IS NULL) OR (price_override_reason IS NOT NULL AND btrim(price_override_reason) <> '' AND price_override_by IS NOT NULL)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_sale_item_price_override_audit",
            schema: "sales",
            table: "sale_items");

        migrationBuilder.DropColumn(
            name: "list_unit_price_snapshot",
            schema: "sales",
            table: "sale_items");

        migrationBuilder.DropColumn(
            name: "price_override_reason",
            schema: "sales",
            table: "sale_items");

        migrationBuilder.DropColumn(
            name: "price_override_by",
            schema: "sales",
            table: "sale_items");
    }
}
