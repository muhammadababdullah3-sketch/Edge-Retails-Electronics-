using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11CatalogV3RelationalIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Taxonomy remains reference data outside the operational DbContext change tracker.
            // This forward migration owns the imported schema and relational constraints.
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS catalog_v3;");
            migrationBuilder.CreateTable(
                name: "divisions",
                schema: "catalog_v3",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table => table.PrimaryKey("pk_catalog_v3_divisions", x => x.code));
            migrationBuilder.CreateTable(
                name: "departments",
                schema: "catalog_v3",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    division_code = table.Column<string>(type: "text", nullable: false),
                    active_leaf_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    deprecated_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    navigation_only_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table => table.PrimaryKey("pk_catalog_v3_departments", x => x.code));
            migrationBuilder.CreateTable(
                name: "product_classes",
                schema: "catalog_v3",
                columns: table => new
                {
                    class_ref = table.Column<string>(type: "text", nullable: false),
                    class_name = table.Column<string>(type: "text", nullable: false),
                    division_code = table.Column<string>(type: "text", nullable: false),
                    department_code = table.Column<string>(type: "text", nullable: false),
                    class_type = table.Column<string>(type: "text", nullable: false),
                    class_status = table.Column<string>(type: "text", nullable: false),
                    catalog_role = table.Column<string>(type: "text", nullable: false),
                    canonical_class_ref = table.Column<string>(type: "text", nullable: true),
                    classification_rule = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table => table.PrimaryKey("pk_catalog_v3_product_classes", x => x.class_ref));
            migrationBuilder.CreateTable(
                name: "class_redirects",
                schema: "catalog_v3",
                columns: table => new
                {
                    legacy_class_ref = table.Column<string>(type: "text", nullable: false),
                    legacy_label = table.Column<string>(type: "text", nullable: false),
                    canonical_class_ref = table.Column<string>(type: "text", nullable: false),
                    canonical_label = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    migration_policy = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table => table.PrimaryKey("pk_catalog_v3_class_redirects", x => x.legacy_class_ref));

            migrationBuilder.AddForeignKey(
                name: "fk_catalog_v3_departments_divisions_division_code",
                schema: "catalog_v3",
                table: "departments",
                column: "division_code",
                principalSchema: "catalog_v3",
                principalTable: "divisions",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_catalog_v3_product_classes_divisions_division_code",
                schema: "catalog_v3",
                table: "product_classes",
                column: "division_code",
                principalSchema: "catalog_v3",
                principalTable: "divisions",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_catalog_v3_product_classes_departments_department_code",
                schema: "catalog_v3",
                table: "product_classes",
                column: "department_code",
                principalSchema: "catalog_v3",
                principalTable: "departments",
                principalColumn: "code",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_catalog_v3_class_redirects_product_classes_canonical_class_ref",
                schema: "catalog_v3",
                table: "class_redirects",
                column: "canonical_class_ref",
                principalSchema: "catalog_v3",
                principalTable: "product_classes",
                principalColumn: "class_ref",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "fk_products_class_ref_catalog_v3_product_classes_class_ref",
                schema: "catalog",
                table: "products",
                column: "class_ref",
                principalSchema: "catalog_v3",
                principalTable: "product_classes",
                principalColumn: "class_ref",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(name: "ix_catalog_v3_departments_division_code", schema: "catalog_v3", table: "departments", column: "division_code");
            migrationBuilder.CreateIndex(name: "ix_catalog_v3_product_classes_division_code", schema: "catalog_v3", table: "product_classes", column: "division_code");
            migrationBuilder.CreateIndex(name: "ix_catalog_v3_product_classes_department_code", schema: "catalog_v3", table: "product_classes", column: "department_code");
            migrationBuilder.CreateIndex(name: "ix_catalog_v3_class_redirects_canonical_class_ref", schema: "catalog_v3", table: "class_redirects", column: "canonical_class_ref");
            migrationBuilder.CreateIndex(name: "ix_products_class_ref", schema: "catalog", table: "products", column: "class_ref");

            // Separate prerequisite schema repair: ThakaCreditRefund is already in the runtime
            // model/config but the inherited migration chain lacks its table. This is explicitly
            // identified here, distinct from the CAT schema/FK operations above.
            migrationBuilder.CreateTable(
                name: "credit_refunds",
                schema: "thaka",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    refunded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    refunded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_refunds", x => x.id);
                    table.CheckConstraint("ck_thaka_credit_refund_amount_positive", "amount > 0");
                    table.ForeignKey(name: "fk_credit_refunds_projects_project_id", column: x => x.project_id, principalSchema: "thaka", principalTable: "projects", principalColumn: "id", onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(name: "ix_credit_refunds_client_operation_id", schema: "thaka", table: "credit_refunds", column: "client_operation_id", unique: true);
            migrationBuilder.CreateIndex(name: "ix_credit_refunds_project_id_refunded_at", schema: "thaka", table: "credit_refunds", columns: new[] { "project_id", "refunded_at" });
            migrationBuilder.CreateIndex(name: "ix_credit_refunds_refund_number", schema: "thaka", table: "credit_refunds", column: "refund_number", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reference data is retained. Rollback of the relationship is refused while products
            // still carry taxonomy references; imported classes are never silently deleted here.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM catalog.products WHERE class_ref IS NOT NULL) THEN
                        RAISE EXCEPTION 'CATALOG_V3_ROLLBACK_BLOCKED: product class references exist';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(name: "fk_products_class_ref_catalog_v3_product_classes_class_ref", schema: "catalog", table: "products");
            migrationBuilder.DropIndex(name: "ix_products_class_ref", schema: "catalog", table: "products");
            migrationBuilder.DropForeignKey(name: "fk_catalog_v3_class_redirects_product_classes_canonical_class_ref", schema: "catalog_v3", table: "class_redirects");
            migrationBuilder.DropForeignKey(name: "fk_catalog_v3_product_classes_departments_department_code", schema: "catalog_v3", table: "product_classes");
            migrationBuilder.DropForeignKey(name: "fk_catalog_v3_product_classes_divisions_division_code", schema: "catalog_v3", table: "product_classes");
            migrationBuilder.DropForeignKey(name: "fk_catalog_v3_departments_divisions_division_code", schema: "catalog_v3", table: "departments");
            migrationBuilder.DropIndex(name: "ix_catalog_v3_class_redirects_canonical_class_ref", schema: "catalog_v3", table: "class_redirects");
            migrationBuilder.DropIndex(name: "ix_catalog_v3_product_classes_department_code", schema: "catalog_v3", table: "product_classes");
            migrationBuilder.DropIndex(name: "ix_catalog_v3_product_classes_division_code", schema: "catalog_v3", table: "product_classes");
            migrationBuilder.DropIndex(name: "ix_catalog_v3_departments_division_code", schema: "catalog_v3", table: "departments");
            migrationBuilder.DropTable(name: "class_redirects", schema: "catalog_v3");
            migrationBuilder.DropTable(name: "product_classes", schema: "catalog_v3");
            migrationBuilder.DropTable(name: "departments", schema: "catalog_v3");
            migrationBuilder.DropTable(name: "divisions", schema: "catalog_v3");
            // The catalog_v3 schema itself is retained for any later explicitly managed objects.
        }
    }
}
