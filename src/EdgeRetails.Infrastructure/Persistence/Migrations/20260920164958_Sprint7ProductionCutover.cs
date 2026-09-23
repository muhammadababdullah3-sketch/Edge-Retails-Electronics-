using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Sprint7ProductionCutover : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "thaka");

        migrationBuilder.AddColumn<string>(
            name: "notes",
            schema: "parties",
            table: "suppliers",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "notes",
            schema: "parties",
            table: "customers",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "expense_categories",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                is_system = table.Column<bool>(type: "boolean", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_expense_categories", x => x.id);
            });

        migrationBuilder.InsertData(
            schema: "finance",
            table: "expense_categories",
            columns: new[] { "id", "name", "is_system", "is_active", "version" },
            values: new object[,]
            {
                    { new Guid("71000000-0000-7000-8000-000000000001"), "Staff Expense", true, true, 0L },
                    { new Guid("71000000-0000-7000-8000-000000000002"), "Utilities", true, true, 0L },
                    { new Guid("71000000-0000-7000-8000-000000000003"), "Rent", true, true, 0L },
                    { new Guid("71000000-0000-7000-8000-000000000004"), "Transport", true, true, 0L },
                    { new Guid("71000000-0000-7000-8000-000000000005"), "Maintenance", true, true, 0L },
                    { new Guid("71000000-0000-7000-8000-000000000006"), "Miscellaneous", true, true, 0L }
            });

        migrationBuilder.CreateTable(
            name: "projects",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                project_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                site_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                started_on = table.Column<DateOnly>(type: "date", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_projects", x => x.id);
                table.ForeignKey(
                    name: "fk_projects_customers_customer_id",
                    column: x => x.customer_id,
                    principalSchema: "parties",
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "expense_subcategories",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_expense_subcategories", x => x.id);
                table.ForeignKey(
                    name: "fk_expense_subcategories_expense_categories_category_id",
                    column: x => x.category_id,
                    principalSchema: "finance",
                    principalTable: "expense_categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "material_issues",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                challan_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                total_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                total_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                gross_profit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                issued_by = table.Column<Guid>(type: "uuid", nullable: false),
                issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_material_issues", x => x.id);
                table.CheckConstraint("ck_thaka_issue_values", "total_charge >= 0 AND total_cost >= 0 AND gross_profit = total_charge - total_cost");
                table.ForeignKey(
                    name: "fk_material_issues_projects_project_id",
                    column: x => x.project_id,
                    principalSchema: "thaka",
                    principalTable: "projects",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "payments",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                receipt_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                payment_method = table.Column<int>(type: "integer", nullable: false),
                cash_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payments", x => x.id);
                table.CheckConstraint("ck_thaka_payment_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_payments_cash_sessions_cash_session_id",
                    column: x => x.cash_session_id,
                    principalSchema: "finance",
                    principalTable: "cash_sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_payments_projects_project_id",
                    column: x => x.project_id,
                    principalSchema: "thaka",
                    principalTable: "projects",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "expenses",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                expense_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false),
                subcategory_id = table.Column<Guid>(type: "uuid", nullable: true),
                expense_date = table.Column<DateOnly>(type: "date", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                payment_method = table.Column<int>(type: "integer", nullable: false),
                cash_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_expenses", x => x.id);
                table.CheckConstraint("ck_expense_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_expenses_cash_sessions_cash_session_id",
                    column: x => x.cash_session_id,
                    principalSchema: "finance",
                    principalTable: "cash_sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_expenses_expense_categories_category_id",
                    column: x => x.category_id,
                    principalSchema: "finance",
                    principalTable: "expense_categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_expenses_expense_subcategories_subcategory_id",
                    column: x => x.subcategory_id,
                    principalSchema: "finance",
                    principalTable: "expense_subcategories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "material_issue_items",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                material_issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                sku_snapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                unit_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                line_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                total_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                gross_profit_snapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_material_issue_items", x => x.id);
                table.CheckConstraint("ck_thaka_issue_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND unit_charge >= 0 AND line_charge >= 0 AND unit_cost_snapshot >= 0 AND total_cost_snapshot >= 0");
                table.ForeignKey(
                    name: "fk_material_issue_items_material_issues_material_issue_id",
                    column: x => x.material_issue_id,
                    principalSchema: "thaka",
                    principalTable: "material_issues",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_material_issue_items_movements_inventory_movement_id",
                    column: x => x.inventory_movement_id,
                    principalSchema: "inventory",
                    principalTable: "movements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_material_issue_items_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_material_issue_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "material_reversals",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                material_issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                reversal_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                reversed_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                restored_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                reversed_by = table.Column<Guid>(type: "uuid", nullable: false),
                reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_material_reversals", x => x.id);
                table.CheckConstraint("ck_thaka_material_reversal_values", "reversed_charge >= 0 AND restored_cost >= 0");
                table.ForeignKey(
                    name: "fk_material_reversals_material_issues_material_issue_id",
                    column: x => x.material_issue_id,
                    principalSchema: "thaka",
                    principalTable: "material_issues",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_material_reversals_projects_project_id",
                    column: x => x.project_id,
                    principalSchema: "thaka",
                    principalTable: "projects",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "payment_reversals",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                reversal_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                reversed_by = table.Column<Guid>(type: "uuid", nullable: false),
                reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_payment_reversals", x => x.id);
                table.CheckConstraint("ck_thaka_payment_reversal_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_payment_reversals_payments_payment_id",
                    column: x => x.payment_id,
                    principalSchema: "thaka",
                    principalTable: "payments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_payment_reversals_projects_project_id",
                    column: x => x.project_id,
                    principalSchema: "thaka",
                    principalTable: "projects",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "settlements",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                settlement_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                gross_material_charges_snapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                payments_collected_snapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                settlement_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                final_payment_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                balance_before_settlement = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                final_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                settled_by = table.Column<Guid>(type: "uuid", nullable: false),
                settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_settlements", x => x.id);
                table.CheckConstraint("ck_thaka_settlement_values", "gross_material_charges_snapshot >= 0 AND payments_collected_snapshot >= 0 AND settlement_discount >= 0 AND final_payment_amount >= 0 AND balance_before_settlement >= 0");
                table.ForeignKey(
                    name: "fk_settlements_payments_final_payment_id",
                    column: x => x.final_payment_id,
                    principalSchema: "thaka",
                    principalTable: "payments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_settlements_projects_project_id",
                    column: x => x.project_id,
                    principalSchema: "thaka",
                    principalTable: "projects",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "material_issue_units",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                material_issue_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_material_issue_units", x => x.id);
                table.ForeignKey(
                    name: "fk_material_issue_units_material_issue_items_material_issue_it~",
                    column: x => x.material_issue_item_id,
                    principalSchema: "thaka",
                    principalTable: "material_issue_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_material_issue_units_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "reopenings",
            schema: "thaka",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                project_id = table.Column<Guid>(type: "uuid", nullable: false),
                settlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                reopened_by = table.Column<Guid>(type: "uuid", nullable: false),
                reopened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_reopenings", x => x.id);
                table.ForeignKey(
                    name: "fk_reopenings_projects_project_id",
                    column: x => x.project_id,
                    principalSchema: "thaka",
                    principalTable: "projects",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_reopenings_settlements_settlement_id",
                    column: x => x.settlement_id,
                    principalSchema: "thaka",
                    principalTable: "settlements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_expense_categories_name",
            schema: "finance",
            table: "expense_categories",
            column: "name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_expense_subcategories_category_id_name",
            schema: "finance",
            table: "expense_subcategories",
            columns: new[] { "category_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_expenses_cash_session_id",
            schema: "finance",
            table: "expenses",
            column: "cash_session_id");

        migrationBuilder.CreateIndex(
            name: "ix_expenses_category_id",
            schema: "finance",
            table: "expenses",
            column: "category_id");

        migrationBuilder.CreateIndex(
            name: "ix_expenses_client_operation_id",
            schema: "finance",
            table: "expenses",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_expenses_expense_date_status",
            schema: "finance",
            table: "expenses",
            columns: new[] { "expense_date", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_expenses_expense_number",
            schema: "finance",
            table: "expenses",
            column: "expense_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_expenses_subcategory_id",
            schema: "finance",
            table: "expenses",
            column: "subcategory_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_issue_items_inventory_movement_id",
            schema: "thaka",
            table: "material_issue_items",
            column: "inventory_movement_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_issue_items_material_issue_id",
            schema: "thaka",
            table: "material_issue_items",
            column: "material_issue_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_issue_items_product_id",
            schema: "thaka",
            table: "material_issue_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_issue_items_product_unit_id",
            schema: "thaka",
            table: "material_issue_items",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_issue_units_inventory_unit_id",
            schema: "thaka",
            table: "material_issue_units",
            column: "inventory_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_issue_units_material_issue_item_id_inventory_unit_~",
            schema: "thaka",
            table: "material_issue_units",
            columns: new[] { "material_issue_item_id", "inventory_unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_material_issues_challan_number",
            schema: "thaka",
            table: "material_issues",
            column: "challan_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_material_issues_client_operation_id",
            schema: "thaka",
            table: "material_issues",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_material_issues_project_id_issued_at",
            schema: "thaka",
            table: "material_issues",
            columns: new[] { "project_id", "issued_at" });

        migrationBuilder.CreateIndex(
            name: "ix_material_reversals_client_operation_id",
            schema: "thaka",
            table: "material_reversals",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_material_reversals_material_issue_id",
            schema: "thaka",
            table: "material_reversals",
            column: "material_issue_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_material_reversals_project_id",
            schema: "thaka",
            table: "material_reversals",
            column: "project_id");

        migrationBuilder.CreateIndex(
            name: "ix_material_reversals_reversal_number",
            schema: "thaka",
            table: "material_reversals",
            column: "reversal_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payment_reversals_client_operation_id",
            schema: "thaka",
            table: "payment_reversals",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payment_reversals_payment_id",
            schema: "thaka",
            table: "payment_reversals",
            column: "payment_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payment_reversals_project_id",
            schema: "thaka",
            table: "payment_reversals",
            column: "project_id");

        migrationBuilder.CreateIndex(
            name: "ix_payment_reversals_reversal_number",
            schema: "thaka",
            table: "payment_reversals",
            column: "reversal_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payments_cash_session_id",
            schema: "thaka",
            table: "payments",
            column: "cash_session_id");

        migrationBuilder.CreateIndex(
            name: "ix_payments_client_operation_id",
            schema: "thaka",
            table: "payments",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_payments_project_id_recorded_at",
            schema: "thaka",
            table: "payments",
            columns: new[] { "project_id", "recorded_at" });

        migrationBuilder.CreateIndex(
            name: "ix_payments_receipt_number",
            schema: "thaka",
            table: "payments",
            column: "receipt_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_projects_customer_id",
            schema: "thaka",
            table: "projects",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "ix_projects_project_number",
            schema: "thaka",
            table: "projects",
            column: "project_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_projects_status_started_on",
            schema: "thaka",
            table: "projects",
            columns: new[] { "status", "started_on" });

        migrationBuilder.CreateIndex(
            name: "ix_reopenings_project_id_reopened_at",
            schema: "thaka",
            table: "reopenings",
            columns: new[] { "project_id", "reopened_at" });

        migrationBuilder.CreateIndex(
            name: "ix_reopenings_settlement_id",
            schema: "thaka",
            table: "reopenings",
            column: "settlement_id");

        migrationBuilder.CreateIndex(
            name: "ix_settlements_client_operation_id",
            schema: "thaka",
            table: "settlements",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_settlements_final_payment_id",
            schema: "thaka",
            table: "settlements",
            column: "final_payment_id");

        migrationBuilder.CreateIndex(
            name: "ix_settlements_project_id_settled_at",
            schema: "thaka",
            table: "settlements",
            columns: new[] { "project_id", "settled_at" });

        migrationBuilder.CreateIndex(
            name: "ix_settlements_settlement_number",
            schema: "thaka",
            table: "settlements",
            column: "settlement_number",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "expenses",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "material_issue_units",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "material_reversals",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "payment_reversals",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "reopenings",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "expense_subcategories",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "material_issue_items",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "settlements",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "expense_categories",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "material_issues",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "payments",
            schema: "thaka");

        migrationBuilder.DropTable(
            name: "projects",
            schema: "thaka");

        migrationBuilder.DropColumn(
            name: "notes",
            schema: "parties",
            table: "suppliers");

        migrationBuilder.DropColumn(
            name: "notes",
            schema: "parties",
            table: "customers");
    }
}
