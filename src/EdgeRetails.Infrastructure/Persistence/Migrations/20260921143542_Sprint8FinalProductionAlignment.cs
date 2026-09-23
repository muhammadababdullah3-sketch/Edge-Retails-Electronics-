using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class Sprint8FinalProductionAlignment : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "item_sequence",
            schema: "inventory",
            table: "units",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "origin_type",
            schema: "inventory",
            table: "units",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "product_sku_snapshot",
            schema: "inventory",
            table: "units",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "source_warranty_claim_item_id",
            schema: "inventory",
            table: "units",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "supplier_code_snapshot",
            schema: "inventory",
            table: "units",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "supplier_product_id",
            schema: "inventory",
            table: "units",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "tracking_code",
            schema: "inventory",
            table: "units",
            type: "character varying(320)",
            maxLength: 320,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "dealer_code",
            schema: "parties",
            table: "suppliers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "pos_drafts",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                draft_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                terminal_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_pos_drafts", x => x.id);
                table.ForeignKey(
                    name: "fk_pos_drafts_customers_customer_id",
                    column: x => x.customer_id,
                    principalSchema: "parties",
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "supplier_account_entries",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                entry_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                entry_type = table.Column<int>(type: "integer", nullable: false),
                direction = table.Column<int>(type: "integer", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                reference_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_account_entries", x => x.id);
                table.CheckConstraint("ck_supplier_account_entry_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_supplier_account_entries_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "supplier_code_sequences",
            schema: "system",
            columns: table => new
            {
                prefix = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                next_value = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_code_sequences", x => x.prefix);
                table.CheckConstraint("ck_supplier_code_sequences_next_positive", "next_value >= 1");
            });

        migrationBuilder.CreateTable(
            name: "supplier_payments",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                payment_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                purpose = table.Column<int>(type: "integer", nullable: false),
                method = table.Column<int>(type: "integer", nullable: false),
                cash_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                external_reference = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_payments", x => x.id);
                table.CheckConstraint("ck_supplier_payment_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_supplier_payments_cash_sessions_cash_session_id",
                    column: x => x.cash_session_id,
                    principalSchema: "finance",
                    principalTable: "cash_sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_supplier_payments_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "supplier_products",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                next_item_sequence = table.Column<long>(type: "bigint", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_products", x => x.id);
                table.CheckConstraint("ck_supplier_products_next_sequence_positive", "next_item_sequence >= 1");
                table.ForeignKey(
                    name: "fk_supplier_products_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_supplier_products_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "supplier_refunds",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                refund_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                method = table.Column<int>(type: "integer", nullable: false),
                cash_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                external_reference = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                reference_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_refunds", x => x.id);
                table.CheckConstraint("ck_supplier_refund_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_supplier_refunds_cash_sessions_cash_session_id",
                    column: x => x.cash_session_id,
                    principalSchema: "finance",
                    principalTable: "cash_sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_supplier_refunds_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "pos_draft_items",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                draft_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                displayed_unit_price_snapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                selected_inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_pos_draft_items", x => x.id);
                table.CheckConstraint("ck_pos_draft_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND displayed_unit_price_snapshot >= 0");
                table.ForeignKey(
                    name: "fk_pos_draft_items_pos_drafts_draft_id",
                    column: x => x.draft_id,
                    principalSchema: "sales",
                    principalTable: "pos_drafts",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_pos_draft_items_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_pos_draft_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_pos_draft_items_units_selected_inventory_unit_id",
                    column: x => x.selected_inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "supplier_payment_reversals",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                supplier_payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                reversed_by = table.Column<Guid>(type: "uuid", nullable: false),
                reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_payment_reversals", x => x.id);
                table.ForeignKey(
                    name: "fk_supplier_payment_reversals_supplier_payments_supplier_payme~",
                    column: x => x.supplier_payment_id,
                    principalSchema: "finance",
                    principalTable: "supplier_payments",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "supplier_refund_reversals",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                supplier_refund_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                reversed_by = table.Column<Guid>(type: "uuid", nullable: false),
                reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_supplier_refund_reversals", x => x.id);
                table.ForeignKey(
                    name: "fk_supplier_refund_reversals_supplier_refunds_supplier_refund_~",
                    column: x => x.supplier_refund_id,
                    principalSchema: "finance",
                    principalTable: "supplier_refunds",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_units_source_warranty_claim_item_id",
            schema: "inventory",
            table: "units",
            column: "source_warranty_claim_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_units_supplier_product_id",
            schema: "inventory",
            table: "units",
            column: "supplier_product_id");

        migrationBuilder.CreateIndex(
            name: "ix_units_supplier_product_id_item_sequence",
            schema: "inventory",
            table: "units",
            columns: new[] { "supplier_product_id", "item_sequence" },
            unique: true,
            filter: "supplier_product_id IS NOT NULL AND item_sequence IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_units_tracking_code",
            schema: "inventory",
            table: "units",
            column: "tracking_code",
            unique: true,
            filter: "tracking_code IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_inventory_unit_sequence_positive",
            schema: "inventory",
            table: "units",
            sql: "item_sequence IS NULL OR item_sequence >= 1");

        migrationBuilder.CreateIndex(
            name: "ix_suppliers_dealer_code",
            schema: "parties",
            table: "suppliers",
            column: "dealer_code",
            unique: true,
            filter: "dealer_code IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_pos_draft_items_draft_id",
            schema: "sales",
            table: "pos_draft_items",
            column: "draft_id");

        migrationBuilder.CreateIndex(
            name: "ix_pos_draft_items_product_id",
            schema: "sales",
            table: "pos_draft_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_pos_draft_items_product_unit_id",
            schema: "sales",
            table: "pos_draft_items",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_pos_draft_items_selected_inventory_unit_id",
            schema: "sales",
            table: "pos_draft_items",
            column: "selected_inventory_unit_id",
            filter: "selected_inventory_unit_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_pos_drafts_created_by_status_updated_at",
            schema: "sales",
            table: "pos_drafts",
            columns: new[] { "created_by", "status", "updated_at" });

        migrationBuilder.CreateIndex(
            name: "ix_pos_drafts_customer_id",
            schema: "sales",
            table: "pos_drafts",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "ix_pos_drafts_draft_number",
            schema: "sales",
            table: "pos_drafts",
            column: "draft_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_pos_drafts_status_updated_at_id",
            schema: "sales",
            table: "pos_drafts",
            columns: new[] { "status", "updated_at", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_supplier_account_entries_client_operation_id",
            schema: "finance",
            table: "supplier_account_entries",
            column: "client_operation_id");

        migrationBuilder.CreateIndex(
            name: "ix_supplier_account_entries_entry_number",
            schema: "finance",
            table: "supplier_account_entries",
            column: "entry_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_account_entries_entry_type_reference_type_referenc~",
            schema: "finance",
            table: "supplier_account_entries",
            columns: new[] { "entry_type", "reference_type", "reference_id" },
            unique: true,
            filter: "reference_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_supplier_account_entries_supplier_id_occurred_at_id",
            schema: "finance",
            table: "supplier_account_entries",
            columns: new[] { "supplier_id", "occurred_at", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_supplier_payment_reversals_client_operation_id",
            schema: "finance",
            table: "supplier_payment_reversals",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_payment_reversals_supplier_payment_id",
            schema: "finance",
            table: "supplier_payment_reversals",
            column: "supplier_payment_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_payments_cash_session_id",
            schema: "finance",
            table: "supplier_payments",
            column: "cash_session_id");

        migrationBuilder.CreateIndex(
            name: "ix_supplier_payments_client_operation_id",
            schema: "finance",
            table: "supplier_payments",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_payments_payment_number",
            schema: "finance",
            table: "supplier_payments",
            column: "payment_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_payments_supplier_id_paid_at_id",
            schema: "finance",
            table: "supplier_payments",
            columns: new[] { "supplier_id", "paid_at", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_supplier_products_product_id_is_active",
            schema: "catalog",
            table: "supplier_products",
            columns: new[] { "product_id", "is_active" });

        migrationBuilder.CreateIndex(
            name: "ix_supplier_products_supplier_id_is_active",
            schema: "catalog",
            table: "supplier_products",
            columns: new[] { "supplier_id", "is_active" });

        migrationBuilder.CreateIndex(
            name: "ix_supplier_products_supplier_id_product_id",
            schema: "catalog",
            table: "supplier_products",
            columns: new[] { "supplier_id", "product_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_refund_reversals_client_operation_id",
            schema: "finance",
            table: "supplier_refund_reversals",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_refund_reversals_supplier_refund_id",
            schema: "finance",
            table: "supplier_refund_reversals",
            column: "supplier_refund_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_refunds_cash_session_id",
            schema: "finance",
            table: "supplier_refunds",
            column: "cash_session_id");

        migrationBuilder.CreateIndex(
            name: "ix_supplier_refunds_client_operation_id",
            schema: "finance",
            table: "supplier_refunds",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_refunds_refund_number",
            schema: "finance",
            table: "supplier_refunds",
            column: "refund_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_supplier_refunds_supplier_id_received_at_id",
            schema: "finance",
            table: "supplier_refunds",
            columns: new[] { "supplier_id", "received_at", "id" });

        migrationBuilder.AddForeignKey(
            name: "fk_units_claim_items_source_warranty_claim_item_id",
            schema: "inventory",
            table: "units",
            column: "source_warranty_claim_item_id",
            principalSchema: "warranty",
            principalTable: "claim_items",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "fk_units_supplier_products_supplier_product_id",
            schema: "inventory",
            table: "units",
            column: "supplier_product_id",
            principalSchema: "catalog",
            principalTable: "supplier_products",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "fk_units_claim_items_source_warranty_claim_item_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropForeignKey(
            name: "fk_units_supplier_products_supplier_product_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropTable(
            name: "pos_draft_items",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "supplier_account_entries",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "supplier_code_sequences",
            schema: "system");

        migrationBuilder.DropTable(
            name: "supplier_payment_reversals",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "supplier_products",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "supplier_refund_reversals",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "pos_drafts",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "supplier_payments",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "supplier_refunds",
            schema: "finance");

        migrationBuilder.DropIndex(
            name: "ix_units_source_warranty_claim_item_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropIndex(
            name: "ix_units_supplier_product_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropIndex(
            name: "ix_units_supplier_product_id_item_sequence",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropIndex(
            name: "ix_units_tracking_code",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropCheckConstraint(
            name: "ck_inventory_unit_sequence_positive",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropIndex(
            name: "ix_suppliers_dealer_code",
            schema: "parties",
            table: "suppliers");

        migrationBuilder.DropColumn(
            name: "item_sequence",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "origin_type",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "product_sku_snapshot",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "source_warranty_claim_item_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "supplier_code_snapshot",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "supplier_product_id",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "tracking_code",
            schema: "inventory",
            table: "units");

        migrationBuilder.DropColumn(
            name: "dealer_code",
            schema: "parties",
            table: "suppliers");
    }
}
