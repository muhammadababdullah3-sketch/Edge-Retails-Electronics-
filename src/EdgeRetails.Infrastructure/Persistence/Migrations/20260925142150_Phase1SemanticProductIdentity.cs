using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1SemanticProductIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "company_id",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_code",
                schema: "catalog",
                table: "products",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "identity_symbol",
                schema: "catalog",
                table: "categories",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "catalog",
                table: "categories",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "companies",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies", x => x.id);
                    table.CheckConstraint("ck_companies_code_uppercase", "code = UPPER(code)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_products_company_id",
                schema: "catalog",
                table: "products",
                column: "company_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_model_code_uppercase",
                schema: "catalog",
                table: "products",
                sql: "model_code IS NULL OR model_code = UPPER(model_code)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_sku_uppercase",
                schema: "catalog",
                table: "products",
                sql: "sku IS NULL OR sku = UPPER(sku)");

            migrationBuilder.CreateIndex(
                name: "ix_categories_identity_symbol",
                schema: "catalog",
                table: "categories",
                column: "identity_symbol",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_categories_symbol_uppercase",
                schema: "catalog",
                table: "categories",
                sql: "identity_symbol = UPPER(identity_symbol)");

            migrationBuilder.CreateIndex(
                name: "ix_companies_code",
                schema: "catalog",
                table: "companies",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_companies_name",
                schema: "catalog",
                table: "companies",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_products_companies_company_id",
                schema: "catalog",
                table: "products",
                column: "company_id",
                principalSchema: "catalog",
                principalTable: "companies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_products_companies_company_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropTable(
                name: "companies",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_products_company_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_model_code_uppercase",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_sku_uppercase",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_categories_identity_symbol",
                schema: "catalog",
                table: "categories");

            migrationBuilder.DropCheckConstraint(
                name: "ck_categories_symbol_uppercase",
                schema: "catalog",
                table: "categories");

            migrationBuilder.DropColumn(
                name: "company_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "model_code",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "identity_symbol",
                schema: "catalog",
                table: "categories");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "catalog",
                table: "categories");
        }
    }
}
