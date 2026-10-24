using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11CatalogCanonicalIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "class_ref",
                schema: "catalog",
                table: "products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dimension",
                schema: "catalog",
                table: "units",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Count");

            migrationBuilder.AddColumn<decimal>(
                name: "factor_to_reference",
                schema: "catalog",
                table: "units",
                type: "numeric(18,9)",
                precision: 18,
                scale: 9,
                nullable: false,
                defaultValue: 1.0m);

            migrationBuilder.AddColumn<string>(
                name: "conversion_kind",
                schema: "catalog",
                table: "units",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "FIXED");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "class_ref",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "dimension",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropColumn(
                name: "factor_to_reference",
                schema: "catalog",
                table: "units");

            migrationBuilder.DropColumn(
                name: "conversion_kind",
                schema: "catalog",
                table: "units");
        }
    }
}
