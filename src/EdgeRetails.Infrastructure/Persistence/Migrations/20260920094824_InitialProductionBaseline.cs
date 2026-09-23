using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialProductionBaseline : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "audit");

        migrationBuilder.EnsureSchema(
            name: "finance");

        migrationBuilder.EnsureSchema(
            name: "catalog");

        migrationBuilder.EnsureSchema(
            name: "warranty");

        migrationBuilder.EnsureSchema(
            name: "inventory");

        migrationBuilder.EnsureSchema(
            name: "parties");

        migrationBuilder.EnsureSchema(
            name: "system");

        migrationBuilder.EnsureSchema(
            name: "purchasing");

        migrationBuilder.EnsureSchema(
            name: "sales");

        migrationBuilder.CreateTable(
            name: "business_events",
            schema: "audit",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                entity_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_business_events", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "cash_sessions",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_date = table.Column<DateOnly>(type: "date", nullable: false),
                opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                opening_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                expected_closing_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                counted_closing_cash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                difference = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cash_sessions", x => x.id);
                table.CheckConstraint("ck_cash_session_amounts", "opening_cash >= 0 AND (counted_closing_cash IS NULL OR counted_closing_cash >= 0)");
            });

        migrationBuilder.CreateTable(
            name: "categories",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_categories", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "customers",
            schema: "parties",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                is_walk_in = table.Column<bool>(type: "boolean", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_customers", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "document_sequences",
            schema: "system",
            columns: table => new
            {
                series = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                last_value = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_document_sequences", x => x.series);
                table.CheckConstraint("ck_document_sequence_nonnegative", "last_value >= 0");
            });

        migrationBuilder.CreateTable(
            name: "receipt_template_settings",
            schema: "system",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                template_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                header = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                footer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                show_customer = table.Column<bool>(type: "boolean", nullable: false),
                show_cashier = table.Column<bool>(type: "boolean", nullable: false),
                logo_behavior = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                template_version = table.Column<int>(type: "integer", nullable: false),
                auto_print_default = table.Column<bool>(type: "boolean", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_receipt_template_settings", x => x.id);
                table.CheckConstraint("ck_receipt_template_primary_key", "template_key = 'PRIMARY' AND template_version > 0");
            });

        migrationBuilder.CreateTable(
            name: "shop_profile",
            schema: "system",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                profile_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                shop_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                phone = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_shop_profile", x => x.id);
                table.CheckConstraint("ck_shop_profile_primary_key", "profile_key = 'PRIMARY'");
            });

        migrationBuilder.CreateTable(
            name: "suppliers",
            schema: "parties",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_suppliers", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "units",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                symbol = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                display_decimal_places = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_units", x => x.id);
                table.CheckConstraint("ck_units_display_decimal_places", "display_decimal_places BETWEEN 0 AND 6");
            });

        migrationBuilder.CreateTable(
            name: "cash_movements",
            schema: "finance",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                cash_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                movement_type = table.Column<int>(type: "integer", nullable: false),
                direction = table.Column<int>(type: "integer", nullable: false),
                amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                source_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                source_id = table.Column<Guid>(type: "uuid", nullable: true),
                reason = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cash_movements", x => x.id);
                table.CheckConstraint("ck_cash_movement_amount_positive", "amount > 0");
                table.ForeignKey(
                    name: "fk_cash_movements_cash_sessions_cash_session_id",
                    column: x => x.cash_session_id,
                    principalSchema: "finance",
                    principalTable: "cash_sessions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stocktakes",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                scope = table.Column<int>(type: "integer", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                review_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stocktakes", x => x.id);
                table.ForeignKey(
                    name: "fk_stocktakes_categories_category_id",
                    column: x => x.category_id,
                    principalSchema: "catalog",
                    principalTable: "categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "quotations",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                quotation_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                customer_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                quotation_date = table.Column<DateOnly>(type: "date", nullable: false),
                valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                converted_sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_quotations", x => x.id);
                table.CheckConstraint("ck_quotation_totals", "subtotal >= 0 AND discount >= 0 AND discount <= subtotal AND grand_total >= 0");
                table.ForeignKey(
                    name: "fk_quotations_customers_customer_id",
                    column: x => x.customer_id,
                    principalSchema: "parties",
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sales",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                invoice_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                cashier_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                session_id = table.Column<Guid>(type: "uuid", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                invoice_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                payment_status = table.Column<int>(type: "integer", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                receipt_template_snapshot = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sales", x => x.id);
                table.CheckConstraint("ck_sales_totals", "subtotal >= 0 AND invoice_discount >= 0 AND grand_total >= 0 AND invoice_discount <= subtotal");
                table.ForeignKey(
                    name: "fk_sales_customers_customer_id",
                    column: x => x.customer_id,
                    principalSchema: "parties",
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "claims",
            schema: "warranty",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                original_sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                current_custody = table.Column<int>(type: "integer", nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_claims", x => x.id);
                table.ForeignKey(
                    name: "fk_claims_customers_customer_id",
                    column: x => x.customer_id,
                    principalSchema: "parties",
                    principalTable: "customers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_claims_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "purchases",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                supplier_invoice_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                normalized_supplier_invoice_number = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                purchase_date = table.Column<DateOnly>(type: "date", nullable: false),
                note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                subtotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                other_charges = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                grand_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                settlement_mode = table.Column<int>(type: "integer", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_purchases", x => x.id);
                table.CheckConstraint("ck_purchase_totals", "subtotal >= 0 AND other_charges >= 0 AND grand_total >= 0");
                table.ForeignKey(
                    name: "fk_purchases_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "products",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                base_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: true),
                tracking_mode = table.Column<int>(type: "integer", nullable: false),
                serial_tracking_enabled = table.Column<bool>(type: "boolean", nullable: false),
                imei_tracking_enabled = table.Column<bool>(type: "boolean", nullable: false),
                reference_purchase_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                default_sale_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_products", x => x.id);
                table.CheckConstraint("ck_products_nonnegative_prices", "default_sale_price >= 0 AND (reference_purchase_cost IS NULL OR reference_purchase_cost >= 0)");
                table.ForeignKey(
                    name: "fk_products_categories_category_id",
                    column: x => x.category_id,
                    principalSchema: "catalog",
                    principalTable: "categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_products_units_base_unit_id",
                    column: x => x.base_unit_id,
                    principalSchema: "catalog",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "quotation_operations",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                operation_type = table.Column<int>(type: "integer", nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_quotation_operations", x => x.id);
                table.ForeignKey(
                    name: "fk_quotation_operations_quotations_quotation_id",
                    column: x => x.quotation_id,
                    principalSchema: "sales",
                    principalTable: "quotations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "returns",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                return_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                reason_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                refund_method = table.Column<int>(type: "integer", nullable: false),
                refund_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_returns", x => x.id);
                table.CheckConstraint("ck_sale_return_refund_nonnegative", "refund_amount >= 0");
                table.ForeignKey(
                    name: "fk_returns_sales_sale_id",
                    column: x => x.sale_id,
                    principalSchema: "sales",
                    principalTable: "sales",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sale_payments",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                method = table.Column<int>(type: "integer", nullable: false),
                amount_tendered = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                applied_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                change_given = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                reference = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sale_payments", x => x.id);
                table.CheckConstraint("ck_sale_payment_values", "amount_tendered >= 0 AND applied_amount >= 0 AND change_given >= 0");
                table.ForeignKey(
                    name: "fk_sale_payments_sales_sale_id",
                    column: x => x.sale_id,
                    principalSchema: "sales",
                    principalTable: "sales",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "claim_events",
            schema: "warranty",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                custody = table.Column<int>(type: "integer", nullable: false),
                event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_claim_events", x => x.id);
                table.ForeignKey(
                    name: "fk_claim_events_claims_claim_id",
                    column: x => x.claim_id,
                    principalSchema: "warranty",
                    principalTable: "claims",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "purchase_voids",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                voided_by = table.Column<Guid>(type: "uuid", nullable: false),
                voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                cash_drawer_reversal_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_purchase_voids", x => x.id);
                table.CheckConstraint("ck_purchase_void_cash_reversal_nonnegative", "cash_drawer_reversal_amount IS NULL OR cash_drawer_reversal_amount >= 0");
                table.ForeignKey(
                    name: "fk_purchase_voids_purchases_purchase_id",
                    column: x => x.purchase_id,
                    principalSchema: "purchasing",
                    principalTable: "purchases",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "returns",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                return_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                settlement_mode = table.Column<int>(type: "integer", nullable: false),
                supplier_return_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                inventory_cost_removed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                client_operation_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_returns", x => x.id);
                table.CheckConstraint("ck_purchase_return_values", "supplier_return_value >= 0 AND inventory_cost_removed >= 0");
                table.ForeignKey(
                    name: "fk_returns_purchases_purchase_id",
                    column: x => x.purchase_id,
                    principalSchema: "purchasing",
                    principalTable: "purchases",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "claim_items",
            schema: "warranty",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                original_sale_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                fault_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                warranty_valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                resolution_type = table.Column<int>(type: "integer", nullable: true),
                resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                replacement_product_id = table.Column<Guid>(type: "uuid", nullable: true),
                replacement_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_claim_items", x => x.id);
                table.CheckConstraint("ck_warranty_claim_item_quantity_positive", "quantity > 0");
                table.ForeignKey(
                    name: "fk_claim_items_claims_claim_id",
                    column: x => x.claim_id,
                    principalSchema: "warranty",
                    principalTable: "claims",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_claim_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_claim_items_products_replacement_product_id",
                    column: x => x.replacement_product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "cost_states",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                costed_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                total_inventory_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                moving_average_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                last_purchase_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                last_purchase_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cost_states", x => x.id);
                table.CheckConstraint("ck_cost_states_nonnegative", "costed_qty >= 0 AND total_inventory_cost >= 0 AND moving_average_cost >= 0 AND (last_purchase_cost IS NULL OR last_purchase_cost >= 0)");
                table.ForeignKey(
                    name: "fk_cost_states_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "movements",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                movement_type = table.Column<int>(type: "integer", nullable: false),
                reference_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                recognized_loss_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                reason = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_movements", x => x.id);
                table.CheckConstraint("ck_inventory_movement_loss_nonnegative", "recognized_loss_amount >= 0");
                table.ForeignKey(
                    name: "fk_movements_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "product_units",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                factor_to_base_unit = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                can_purchase = table.Column<bool>(type: "boolean", nullable: false),
                can_sell = table.Column<bool>(type: "boolean", nullable: false),
                can_use_in_thaka = table.Column<bool>(type: "boolean", nullable: false),
                is_default_purchase_unit = table.Column<bool>(type: "boolean", nullable: false),
                is_default_sale_unit = table.Column<bool>(type: "boolean", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_product_units", x => x.id);
                table.CheckConstraint("ck_product_units_factor_positive", "factor_to_base_unit > 0");
                table.ForeignKey(
                    name: "fk_product_units_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_product_units_units_unit_id",
                    column: x => x.unit_id,
                    principalSchema: "catalog",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "quotation_items",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                selected_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                quoted_unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_quotation_items", x => x.id);
                table.CheckConstraint("ck_quotation_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND quoted_unit_price >= 0 AND line_total >= 0");
                table.ForeignKey(
                    name: "fk_quotation_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_quotation_items_quotations_quotation_id",
                    column: x => x.quotation_id,
                    principalSchema: "sales",
                    principalTable: "quotations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_quotation_items_units_selected_unit_id",
                    column: x => x.selected_unit_id,
                    principalSchema: "catalog",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "shop_stock_cases",
            schema: "warranty",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                case_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                supplier_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_purchase_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                fault_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                supplier_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                resolution_type = table.Column<int>(type: "integer", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_shop_stock_cases", x => x.id);
                table.CheckConstraint("ck_shop_warranty_quantity_positive", "base_quantity > 0");
                table.ForeignKey(
                    name: "fk_shop_stock_cases_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_shop_stock_cases_suppliers_supplier_id",
                    column: x => x.supplier_id,
                    principalSchema: "parties",
                    principalTable: "suppliers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stock_balances",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                sellable_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                damaged_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                defective_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                with_supplier_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                scrap_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stock_balances", x => x.id);
                table.CheckConstraint("ck_stock_balances_nonnegative", "sellable_qty >= 0 AND damaged_qty >= 0 AND defective_qty >= 0 AND with_supplier_qty >= 0 AND scrap_qty >= 0");
                table.ForeignKey(
                    name: "fk_stock_balances_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stocktake_items",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                stocktake_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                expected_sellable_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                counted_sellable_qty = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                counted_by = table.Column<Guid>(type: "uuid", nullable: true),
                counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stocktake_items", x => x.id);
                table.CheckConstraint("ck_stocktake_items_nonnegative", "expected_sellable_qty >= 0 AND (counted_sellable_qty IS NULL OR counted_sellable_qty >= 0)");
                table.ForeignKey(
                    name: "fk_stocktake_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_stocktake_items_stocktakes_stocktake_id",
                    column: x => x.stocktake_id,
                    principalSchema: "inventory",
                    principalTable: "stocktakes",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "movement_effects",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                stock_bucket = table.Column<int>(type: "integer", nullable: false),
                quantity_delta = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                quantity_before = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                quantity_after = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_movement_effects", x => x.id);
                table.CheckConstraint("ck_inventory_movement_effect_snapshots_nonnegative", "quantity_before >= 0 AND quantity_after >= 0");
                table.ForeignKey(
                    name: "fk_movement_effects_movements_movement_id",
                    column: x => x.movement_id,
                    principalSchema: "inventory",
                    principalTable: "movements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "product_unit_barcodes",
            schema: "catalog",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                barcode = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_product_unit_barcodes", x => x.id);
                table.ForeignKey(
                    name: "fk_product_unit_barcodes_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "purchase_items",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_name_snapshot = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                sku_snapshot = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                entered_unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                base_line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                allocated_other_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                effective_base_unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                effective_line_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                sale_price_at_purchase = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_purchase_items", x => x.id);
                table.CheckConstraint("ck_purchase_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND entered_unit_cost >= 0 AND base_line_total >= 0 AND allocated_other_cost >= 0 AND effective_base_unit_cost >= 0 AND effective_line_cost >= 0 AND sale_price_at_purchase >= 0");
                table.ForeignKey(
                    name: "fk_purchase_items_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_purchase_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_purchase_items_purchases_purchase_id",
                    column: x => x.purchase_id,
                    principalSchema: "purchasing",
                    principalTable: "purchases",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sale_items",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_name_snapshot = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                sku_snapshot = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                gross_line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                allocated_invoice_discount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                net_line_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                total_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                gross_profit_snapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sale_items", x => x.id);
                table.CheckConstraint("ck_sale_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND unit_price >= 0 AND gross_line_total >= 0 AND allocated_invoice_discount >= 0 AND net_line_total >= 0 AND unit_cost_snapshot >= 0 AND total_cost_snapshot >= 0");
                table.ForeignKey(
                    name: "fk_sale_items_movements_inventory_movement_id",
                    column: x => x.inventory_movement_id,
                    principalSchema: "inventory",
                    principalTable: "movements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_sale_items_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_sale_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_sale_items_sales_sale_id",
                    column: x => x.sale_id,
                    principalSchema: "sales",
                    principalTable: "sales",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "lots",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                received_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                original_unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                effective_unit_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_lots", x => x.id);
                table.CheckConstraint("ck_inventory_lots_values", "received_quantity > 0 AND original_unit_cost >= 0 AND effective_unit_cost >= 0");
                table.ForeignKey(
                    name: "fk_lots_movements_source_movement_id",
                    column: x => x.source_movement_id,
                    principalSchema: "inventory",
                    principalTable: "movements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_lots_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_lots_purchase_items_purchase_item_id",
                    column: x => x.purchase_item_id,
                    principalSchema: "purchasing",
                    principalTable: "purchase_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_items",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                supplier_unit_return_value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                supplier_return_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                inventory_unit_cost_removed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                inventory_cost_removed = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_return_items", x => x.id);
                table.CheckConstraint("ck_purchase_return_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND supplier_unit_return_value >= 0 AND supplier_return_value >= 0 AND inventory_unit_cost_removed >= 0 AND inventory_cost_removed >= 0");
                table.ForeignKey(
                    name: "fk_return_items_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_items_purchase_items_purchase_item_id",
                    column: x => x.purchase_item_id,
                    principalSchema: "purchasing",
                    principalTable: "purchase_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_items_returns_purchase_return_id",
                    column: x => x.purchase_return_id,
                    principalSchema: "purchasing",
                    principalTable: "returns",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_items",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                sale_return_id = table.Column<Guid>(type: "uuid", nullable: false),
                sale_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                entered_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                product_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                factor_to_base_snapshot = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                disposition = table.Column<int>(type: "integer", nullable: false),
                refund_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                original_cost_amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                cost_reversal_amount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_return_items", x => x.id);
                table.CheckConstraint("ck_sale_return_item_values", "entered_quantity > 0 AND factor_to_base_snapshot > 0 AND base_quantity > 0 AND refund_amount >= 0 AND original_cost_amount >= 0 AND cost_reversal_amount >= 0");
                table.ForeignKey(
                    name: "fk_return_items_product_units_product_unit_id",
                    column: x => x.product_unit_id,
                    principalSchema: "catalog",
                    principalTable: "product_units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_items_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_items_returns_sale_return_id",
                    column: x => x.sale_return_id,
                    principalSchema: "sales",
                    principalTable: "returns",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_items_sale_items_sale_item_id",
                    column: x => x.sale_item_id,
                    principalSchema: "sales",
                    principalTable: "sale_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "lot_bucket_balances",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                stock_bucket = table.Column<int>(type: "integer", nullable: false),
                quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_lot_bucket_balances", x => x.id);
                table.CheckConstraint("ck_inventory_lot_bucket_quantity_nonnegative", "quantity >= 0");
                table.ForeignKey(
                    name: "fk_lot_bucket_balances_lots_lot_id",
                    column: x => x.lot_id,
                    principalSchema: "inventory",
                    principalTable: "lots",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "lot_consumptions",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                total_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_lot_consumptions", x => x.id);
                table.CheckConstraint("ck_inventory_lot_consumptions_values", "quantity > 0 AND unit_cost_snapshot >= 0 AND total_cost_snapshot >= 0");
                table.ForeignKey(
                    name: "fk_lot_consumptions_lots_lot_id",
                    column: x => x.lot_id,
                    principalSchema: "inventory",
                    principalTable: "lots",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_lot_consumptions_movements_movement_id",
                    column: x => x.movement_id,
                    principalSchema: "inventory",
                    principalTable: "movements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "units",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                product_id = table.Column<Guid>(type: "uuid", nullable: false),
                serial_number = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                imei1 = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                imei2 = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                acquisition_cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                inventory_lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_purchase_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_warranty_case_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_units", x => x.id);
                table.CheckConstraint("ck_inventory_unit_cost_nonnegative", "acquisition_cost >= 0");
                table.ForeignKey(
                    name: "fk_units_lots_inventory_lot_id",
                    column: x => x.inventory_lot_id,
                    principalSchema: "inventory",
                    principalTable: "lots",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_units_products_product_id",
                    column: x => x.product_id,
                    principalSchema: "catalog",
                    principalTable: "products",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_units_purchase_items_source_purchase_item_id",
                    column: x => x.source_purchase_item_id,
                    principalSchema: "purchasing",
                    principalTable: "purchase_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_units_shop_stock_cases_source_warranty_case_id",
                    column: x => x.source_warranty_case_id,
                    principalSchema: "warranty",
                    principalTable: "shop_stock_cases",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "claim_item_units",
            schema: "warranty",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                claim_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                original_inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                original_identity_snapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                replacement_inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                replacement_identity_snapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_claim_item_units", x => x.id);
                table.ForeignKey(
                    name: "fk_claim_item_units_claim_items_claim_item_id",
                    column: x => x.claim_item_id,
                    principalSchema: "warranty",
                    principalTable: "claim_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_claim_item_units_units_original_inventory_unit_id",
                    column: x => x.original_inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_claim_item_units_units_replacement_inventory_unit_id",
                    column: x => x.replacement_inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "movement_units",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                from_status = table.Column<int>(type: "integer", nullable: true),
                to_status = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_movement_units", x => x.id);
                table.ForeignKey(
                    name: "fk_movement_units_movements_movement_id",
                    column: x => x.movement_id,
                    principalSchema: "inventory",
                    principalTable: "movements",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_movement_units_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "purchase_item_units",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_purchase_item_units", x => x.id);
                table.ForeignKey(
                    name: "fk_purchase_item_units_purchase_items_purchase_item_id",
                    column: x => x.purchase_item_id,
                    principalSchema: "purchasing",
                    principalTable: "purchase_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_purchase_item_units_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_item_units",
            schema: "purchasing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                purchase_return_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_return_item_units", x => x.id);
                table.ForeignKey(
                    name: "fk_return_item_units_return_items_purchase_return_item_id",
                    column: x => x.purchase_return_item_id,
                    principalSchema: "purchasing",
                    principalTable: "return_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_item_units_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "return_item_units",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                sale_return_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_return_item_units", x => x.id);
                table.ForeignKey(
                    name: "fk_return_item_units_return_items_sale_return_item_id",
                    column: x => x.sale_return_item_id,
                    principalSchema: "sales",
                    principalTable: "return_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_return_item_units_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "sale_item_units",
            schema: "sales",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                sale_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                warranty_valid_until = table.Column<DateOnly>(type: "date", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sale_item_units", x => x.id);
                table.ForeignKey(
                    name: "fk_sale_item_units_sale_items_sale_item_id",
                    column: x => x.sale_item_id,
                    principalSchema: "sales",
                    principalTable: "sale_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_sale_item_units_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stocktake_unit_checks",
            schema: "inventory",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                stocktake_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                inventory_unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                identity_snapshot = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                result = table.Column<int>(type: "integer", nullable: false),
                note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stocktake_unit_checks", x => x.id);
                table.ForeignKey(
                    name: "fk_stocktake_unit_checks_stocktake_items_stocktake_item_id",
                    column: x => x.stocktake_item_id,
                    principalSchema: "inventory",
                    principalTable: "stocktake_items",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_stocktake_unit_checks_units_inventory_unit_id",
                    column: x => x.inventory_unit_id,
                    principalSchema: "inventory",
                    principalTable: "units",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_business_events_actor_id",
            schema: "audit",
            table: "business_events",
            column: "actor_id");

        migrationBuilder.CreateIndex(
            name: "ix_business_events_correlation_id",
            schema: "audit",
            table: "business_events",
            column: "correlation_id");

        migrationBuilder.CreateIndex(
            name: "ix_business_events_entity_type_entity_id_occurred_at",
            schema: "audit",
            table: "business_events",
            columns: new[] { "entity_type", "entity_id", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_business_events_occurred_at",
            schema: "audit",
            table: "business_events",
            column: "occurred_at");

        migrationBuilder.CreateIndex(
            name: "ix_cash_movements_cash_session_id_occurred_at",
            schema: "finance",
            table: "cash_movements",
            columns: new[] { "cash_session_id", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_cash_movements_source_type_source_id",
            schema: "finance",
            table: "cash_movements",
            columns: new[] { "source_type", "source_id" });

        migrationBuilder.CreateIndex(
            name: "ix_cash_sessions_business_date",
            schema: "finance",
            table: "cash_sessions",
            column: "business_date");

        migrationBuilder.CreateIndex(
            name: "ix_cash_sessions_status",
            schema: "finance",
            table: "cash_sessions",
            column: "status",
            unique: true,
            filter: "status = 1");

        migrationBuilder.CreateIndex(
            name: "ix_categories_name",
            schema: "catalog",
            table: "categories",
            column: "name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_claim_events_claim_id_occurred_at",
            schema: "warranty",
            table: "claim_events",
            columns: new[] { "claim_id", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_claim_item_units_claim_item_id",
            schema: "warranty",
            table: "claim_item_units",
            column: "claim_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_claim_item_units_original_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units",
            column: "original_inventory_unit_id",
            filter: "original_inventory_unit_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_claim_item_units_replacement_inventory_unit_id",
            schema: "warranty",
            table: "claim_item_units",
            column: "replacement_inventory_unit_id",
            filter: "replacement_inventory_unit_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_claim_items_claim_id",
            schema: "warranty",
            table: "claim_items",
            column: "claim_id");

        migrationBuilder.CreateIndex(
            name: "ix_claim_items_product_id",
            schema: "warranty",
            table: "claim_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_claim_items_replacement_product_id",
            schema: "warranty",
            table: "claim_items",
            column: "replacement_product_id");

        migrationBuilder.CreateIndex(
            name: "ix_claims_claim_number",
            schema: "warranty",
            table: "claims",
            column: "claim_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_claims_customer_id_status_received_at",
            schema: "warranty",
            table: "claims",
            columns: new[] { "customer_id", "status", "received_at" });

        migrationBuilder.CreateIndex(
            name: "ix_claims_supplier_id",
            schema: "warranty",
            table: "claims",
            column: "supplier_id");

        migrationBuilder.CreateIndex(
            name: "ix_cost_states_product_id",
            schema: "inventory",
            table: "cost_states",
            column: "product_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_customers_is_active_name",
            schema: "parties",
            table: "customers",
            columns: new[] { "is_active", "name" });

        migrationBuilder.CreateIndex(
            name: "ix_customers_is_walk_in",
            schema: "parties",
            table: "customers",
            column: "is_walk_in",
            unique: true,
            filter: "is_walk_in = TRUE");

        migrationBuilder.CreateIndex(
            name: "ix_customers_phone",
            schema: "parties",
            table: "customers",
            column: "phone");

        migrationBuilder.CreateIndex(
            name: "ix_lot_bucket_balances_lot_id_stock_bucket",
            schema: "inventory",
            table: "lot_bucket_balances",
            columns: new[] { "lot_id", "stock_bucket" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_lot_consumptions_lot_id",
            schema: "inventory",
            table: "lot_consumptions",
            column: "lot_id");

        migrationBuilder.CreateIndex(
            name: "ix_lot_consumptions_lot_id_movement_id",
            schema: "inventory",
            table: "lot_consumptions",
            columns: new[] { "lot_id", "movement_id" });

        migrationBuilder.CreateIndex(
            name: "ix_lot_consumptions_movement_id",
            schema: "inventory",
            table: "lot_consumptions",
            column: "movement_id");

        migrationBuilder.CreateIndex(
            name: "ix_lots_product_id_created_at",
            schema: "inventory",
            table: "lots",
            columns: new[] { "product_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_lots_purchase_item_id",
            schema: "inventory",
            table: "lots",
            column: "purchase_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_lots_source_movement_id",
            schema: "inventory",
            table: "lots",
            column: "source_movement_id");

        migrationBuilder.CreateIndex(
            name: "ix_movement_effects_movement_id",
            schema: "inventory",
            table: "movement_effects",
            column: "movement_id");

        migrationBuilder.CreateIndex(
            name: "ix_movement_units_inventory_unit_id",
            schema: "inventory",
            table: "movement_units",
            column: "inventory_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_movement_units_movement_id_inventory_unit_id",
            schema: "inventory",
            table: "movement_units",
            columns: new[] { "movement_id", "inventory_unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_movements_correlation_id",
            schema: "inventory",
            table: "movements",
            column: "correlation_id");

        migrationBuilder.CreateIndex(
            name: "ix_movements_product_id_occurred_at",
            schema: "inventory",
            table: "movements",
            columns: new[] { "product_id", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_movements_reference_type_reference_id",
            schema: "inventory",
            table: "movements",
            columns: new[] { "reference_type", "reference_id" });

        migrationBuilder.CreateIndex(
            name: "ix_product_unit_barcodes_barcode",
            schema: "catalog",
            table: "product_unit_barcodes",
            column: "barcode",
            unique: true,
            filter: "is_active = TRUE");

        migrationBuilder.CreateIndex(
            name: "ix_product_unit_barcodes_product_unit_id",
            schema: "catalog",
            table: "product_unit_barcodes",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_product_units_product_id_unit_id",
            schema: "catalog",
            table: "product_units",
            columns: new[] { "product_id", "unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_product_units_unit_id",
            schema: "catalog",
            table: "product_units",
            column: "unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_products_base_unit_id",
            schema: "catalog",
            table: "products",
            column: "base_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_products_category_id_is_active",
            schema: "catalog",
            table: "products",
            columns: new[] { "category_id", "is_active" });

        migrationBuilder.CreateIndex(
            name: "ix_products_name",
            schema: "catalog",
            table: "products",
            column: "name");

        migrationBuilder.CreateIndex(
            name: "ix_products_sku",
            schema: "catalog",
            table: "products",
            column: "sku",
            unique: true,
            filter: "sku IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_purchase_item_units_inventory_unit_id",
            schema: "purchasing",
            table: "purchase_item_units",
            column: "inventory_unit_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchase_item_units_purchase_item_id_inventory_unit_id",
            schema: "purchasing",
            table: "purchase_item_units",
            columns: new[] { "purchase_item_id", "inventory_unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchase_items_product_id",
            schema: "purchasing",
            table: "purchase_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_purchase_items_product_unit_id",
            schema: "purchasing",
            table: "purchase_items",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_purchase_items_purchase_id",
            schema: "purchasing",
            table: "purchase_items",
            column: "purchase_id");

        migrationBuilder.CreateIndex(
            name: "ix_purchase_voids_client_operation_id",
            schema: "purchasing",
            table: "purchase_voids",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchase_voids_purchase_id",
            schema: "purchasing",
            table: "purchase_voids",
            column: "purchase_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchases_client_operation_id",
            schema: "purchasing",
            table: "purchases",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchases_purchase_number",
            schema: "purchasing",
            table: "purchases",
            column: "purchase_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchases_supplier_id_normalized_supplier_invoice_number",
            schema: "purchasing",
            table: "purchases",
            columns: new[] { "supplier_id", "normalized_supplier_invoice_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_purchases_supplier_id_purchase_date",
            schema: "purchasing",
            table: "purchases",
            columns: new[] { "supplier_id", "purchase_date" });

        migrationBuilder.CreateIndex(
            name: "ix_quotation_items_product_id",
            schema: "sales",
            table: "quotation_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_quotation_items_quotation_id",
            schema: "sales",
            table: "quotation_items",
            column: "quotation_id");

        migrationBuilder.CreateIndex(
            name: "ix_quotation_items_selected_unit_id",
            schema: "sales",
            table: "quotation_items",
            column: "selected_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_quotation_operations_client_operation_id",
            schema: "sales",
            table: "quotation_operations",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_quotation_operations_quotation_id_occurred_at",
            schema: "sales",
            table: "quotation_operations",
            columns: new[] { "quotation_id", "occurred_at" });

        migrationBuilder.CreateIndex(
            name: "ix_quotations_customer_id_quotation_date",
            schema: "sales",
            table: "quotations",
            columns: new[] { "customer_id", "quotation_date" });

        migrationBuilder.CreateIndex(
            name: "ix_quotations_quotation_number",
            schema: "sales",
            table: "quotations",
            column: "quotation_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_quotations_status_quotation_date",
            schema: "sales",
            table: "quotations",
            columns: new[] { "status", "quotation_date" });

        migrationBuilder.CreateIndex(
            name: "ix_receipt_template_settings_template_key",
            schema: "system",
            table: "receipt_template_settings",
            column: "template_key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_return_item_units_inventory_unit_id",
            schema: "purchasing",
            table: "return_item_units",
            column: "inventory_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_item_units_purchase_return_item_id_inventory_unit_id",
            schema: "purchasing",
            table: "return_item_units",
            columns: new[] { "purchase_return_item_id", "inventory_unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_return_item_units_inventory_unit_id",
            schema: "sales",
            table: "return_item_units",
            column: "inventory_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_item_units_sale_return_item_id_inventory_unit_id",
            schema: "sales",
            table: "return_item_units",
            columns: new[] { "sale_return_item_id", "inventory_unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_return_items_product_id",
            schema: "purchasing",
            table: "return_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_product_unit_id",
            schema: "purchasing",
            table: "return_items",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_purchase_item_id",
            schema: "purchasing",
            table: "return_items",
            column: "purchase_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_purchase_return_id",
            schema: "purchasing",
            table: "return_items",
            column: "purchase_return_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_product_id",
            schema: "sales",
            table: "return_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_product_unit_id",
            schema: "sales",
            table: "return_items",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_sale_item_id",
            schema: "sales",
            table: "return_items",
            column: "sale_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_return_items_sale_return_id",
            schema: "sales",
            table: "return_items",
            column: "sale_return_id");

        migrationBuilder.CreateIndex(
            name: "ix_returns_client_operation_id",
            schema: "purchasing",
            table: "returns",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_returns_purchase_id_created_at",
            schema: "purchasing",
            table: "returns",
            columns: new[] { "purchase_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_returns_return_number",
            schema: "purchasing",
            table: "returns",
            column: "return_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_returns_client_operation_id",
            schema: "sales",
            table: "returns",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_returns_return_number",
            schema: "sales",
            table: "returns",
            column: "return_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_returns_sale_id_created_at",
            schema: "sales",
            table: "returns",
            columns: new[] { "sale_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_sale_item_units_inventory_unit_id",
            schema: "sales",
            table: "sale_item_units",
            column: "inventory_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_sale_item_units_sale_item_id_inventory_unit_id",
            schema: "sales",
            table: "sale_item_units",
            columns: new[] { "sale_item_id", "inventory_unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_sale_items_inventory_movement_id",
            schema: "sales",
            table: "sale_items",
            column: "inventory_movement_id");

        migrationBuilder.CreateIndex(
            name: "ix_sale_items_product_id",
            schema: "sales",
            table: "sale_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_sale_items_product_unit_id",
            schema: "sales",
            table: "sale_items",
            column: "product_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_sale_items_sale_id",
            schema: "sales",
            table: "sale_items",
            column: "sale_id");

        migrationBuilder.CreateIndex(
            name: "ix_sale_payments_sale_id",
            schema: "sales",
            table: "sale_payments",
            column: "sale_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_sales_client_operation_id",
            schema: "sales",
            table: "sales",
            column: "client_operation_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_sales_completed_at",
            schema: "sales",
            table: "sales",
            column: "completed_at");

        migrationBuilder.CreateIndex(
            name: "ix_sales_customer_id",
            schema: "sales",
            table: "sales",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "ix_sales_invoice_number",
            schema: "sales",
            table: "sales",
            column: "invoice_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_shop_profile_profile_key",
            schema: "system",
            table: "shop_profile",
            column: "profile_key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_shop_stock_cases_case_number",
            schema: "warranty",
            table: "shop_stock_cases",
            column: "case_number",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_shop_stock_cases_product_id",
            schema: "warranty",
            table: "shop_stock_cases",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_shop_stock_cases_supplier_id_status_created_at",
            schema: "warranty",
            table: "shop_stock_cases",
            columns: new[] { "supplier_id", "status", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_stock_balances_product_id",
            schema: "inventory",
            table: "stock_balances",
            column: "product_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_stocktake_items_product_id",
            schema: "inventory",
            table: "stocktake_items",
            column: "product_id");

        migrationBuilder.CreateIndex(
            name: "ix_stocktake_items_stocktake_id_product_id",
            schema: "inventory",
            table: "stocktake_items",
            columns: new[] { "stocktake_id", "product_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_stocktake_unit_checks_inventory_unit_id",
            schema: "inventory",
            table: "stocktake_unit_checks",
            column: "inventory_unit_id");

        migrationBuilder.CreateIndex(
            name: "ix_stocktake_unit_checks_stocktake_item_id_inventory_unit_id",
            schema: "inventory",
            table: "stocktake_unit_checks",
            columns: new[] { "stocktake_item_id", "inventory_unit_id" },
            unique: true,
            filter: "inventory_unit_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_stocktakes_category_id",
            schema: "inventory",
            table: "stocktakes",
            column: "category_id");

        migrationBuilder.CreateIndex(
            name: "ix_stocktakes_status_created_at",
            schema: "inventory",
            table: "stocktakes",
            columns: new[] { "status", "created_at" });

        migrationBuilder.Sql(
            """
            CREATE UNIQUE INDEX ux_product_units_default_purchase
            ON catalog.product_units (product_id)
            WHERE is_active = TRUE AND is_default_purchase_unit = TRUE;

            CREATE UNIQUE INDEX ux_product_units_default_sale
            ON catalog.product_units (product_id)
            WHERE is_active = TRUE AND is_default_sale_unit = TRUE;

            CREATE UNIQUE INDEX ux_stocktakes_single_open
            ON inventory.stocktakes ((1))
            WHERE status IN (1, 2, 3);
            """);

        migrationBuilder.CreateIndex(
            name: "ix_suppliers_is_active_name",
            schema: "parties",
            table: "suppliers",
            columns: new[] { "is_active", "name" });

        migrationBuilder.CreateIndex(
            name: "ix_suppliers_phone",
            schema: "parties",
            table: "suppliers",
            column: "phone");

        migrationBuilder.CreateIndex(
            name: "ix_units_name",
            schema: "catalog",
            table: "units",
            column: "name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_units_symbol",
            schema: "catalog",
            table: "units",
            column: "symbol",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_units_imei1",
            schema: "inventory",
            table: "units",
            column: "imei1",
            unique: true,
            filter: "imei1 IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_units_imei2",
            schema: "inventory",
            table: "units",
            column: "imei2",
            unique: true,
            filter: "imei2 IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_units_inventory_lot_id",
            schema: "inventory",
            table: "units",
            column: "inventory_lot_id");

        migrationBuilder.CreateIndex(
            name: "ix_units_product_id_status",
            schema: "inventory",
            table: "units",
            columns: new[] { "product_id", "status" });

        migrationBuilder.CreateIndex(
            name: "ix_units_serial_number",
            schema: "inventory",
            table: "units",
            column: "serial_number",
            unique: true,
            filter: "serial_number IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_units_source_purchase_item_id",
            schema: "inventory",
            table: "units",
            column: "source_purchase_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_units_source_warranty_case_id",
            schema: "inventory",
            table: "units",
            column: "source_warranty_case_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "business_events",
            schema: "audit");

        migrationBuilder.DropTable(
            name: "cash_movements",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "claim_events",
            schema: "warranty");

        migrationBuilder.DropTable(
            name: "claim_item_units",
            schema: "warranty");

        migrationBuilder.DropTable(
            name: "cost_states",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "document_sequences",
            schema: "system");

        migrationBuilder.DropTable(
            name: "lot_bucket_balances",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "lot_consumptions",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "movement_effects",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "movement_units",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "product_unit_barcodes",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "purchase_item_units",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "purchase_voids",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "quotation_items",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "quotation_operations",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "receipt_template_settings",
            schema: "system");

        migrationBuilder.DropTable(
            name: "return_item_units",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "return_item_units",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "sale_item_units",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "sale_payments",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "shop_profile",
            schema: "system");

        migrationBuilder.DropTable(
            name: "stock_balances",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "stocktake_unit_checks",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "cash_sessions",
            schema: "finance");

        migrationBuilder.DropTable(
            name: "claim_items",
            schema: "warranty");

        migrationBuilder.DropTable(
            name: "quotations",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "return_items",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "return_items",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "stocktake_items",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "units",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "claims",
            schema: "warranty");

        migrationBuilder.DropTable(
            name: "returns",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "returns",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "sale_items",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "stocktakes",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "lots",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "shop_stock_cases",
            schema: "warranty");

        migrationBuilder.DropTable(
            name: "sales",
            schema: "sales");

        migrationBuilder.DropTable(
            name: "movements",
            schema: "inventory");

        migrationBuilder.DropTable(
            name: "purchase_items",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "customers",
            schema: "parties");

        migrationBuilder.DropTable(
            name: "product_units",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "purchases",
            schema: "purchasing");

        migrationBuilder.DropTable(
            name: "products",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "suppliers",
            schema: "parties");

        migrationBuilder.DropTable(
            name: "categories",
            schema: "catalog");

        migrationBuilder.DropTable(
            name: "units",
            schema: "catalog");
    }
}
