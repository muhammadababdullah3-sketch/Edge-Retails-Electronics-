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
            // Idempotent: some rehearsal databases created class_ref before this migration was recorded.
            migrationBuilder.Sql("""
                ALTER TABLE catalog.products
                    ADD COLUMN IF NOT EXISTS class_ref character varying(20);
                ALTER TABLE catalog.units
                    ADD COLUMN IF NOT EXISTS dimension character varying(20) NOT NULL DEFAULT 'Count';
                ALTER TABLE catalog.units
                    ADD COLUMN IF NOT EXISTS factor_to_reference numeric(18,9) NOT NULL DEFAULT 1.0;
                ALTER TABLE catalog.units
                    ADD COLUMN IF NOT EXISTS conversion_kind character varying(20) NOT NULL DEFAULT 'FIXED';
                """);
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
