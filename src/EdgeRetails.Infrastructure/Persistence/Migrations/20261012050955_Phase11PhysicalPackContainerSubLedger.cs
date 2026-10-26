using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11PhysicalPackContainerSubLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $enum$ BEGIN
                  CREATE TYPE inventory.pack_state AS ENUM ('Sealed', 'Open', 'Consumed');
                EXCEPTION
                  WHEN duplicate_object THEN NULL;
                END $enum$;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS initial_base_quantity numeric(18,4) NULL;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS is_loose_stock boolean NOT NULL DEFAULT false;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS pack_state inventory.pack_state NULL;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS remaining_base_quantity numeric(18,4) NULL;
                """);

            migrationBuilder.CreateTable(
                name: "pack_allocations",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pack_unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sale_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sale_return_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    thaka_issue_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    allocated_base_quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    total_cost_snapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    remaining_quantity_after = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    pack_state_after = table.Column<int>(type: "inventory.pack_state", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pack_allocations", x => x.id);
                    table.CheckConstraint("chk_pack_alloc_positive_qty", "allocated_base_quantity > 0");
                    table.CheckConstraint("chk_pack_alloc_remaining_bounds", "remaining_quantity_after >= 0");
                    table.CheckConstraint("chk_pack_alloc_single_source", "(CASE WHEN sale_item_id IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN sale_return_item_id IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN thaka_issue_item_id IS NOT NULL THEN 1 ELSE 0 END) <= 1");
                    table.ForeignKey(
                        name: "fk_pack_allocations_lots_lot_id",
                        column: x => x.lot_id,
                        principalSchema: "inventory",
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_allocations_material_issue_items_thaka_issue_item_id",
                        column: x => x.thaka_issue_item_id,
                        principalSchema: "thaka",
                        principalTable: "material_issue_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_allocations_movements_movement_id",
                        column: x => x.movement_id,
                        principalSchema: "inventory",
                        principalTable: "movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_allocations_return_items_sale_return_item_id",
                        column: x => x.sale_return_item_id,
                        principalSchema: "sales",
                        principalTable: "return_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_allocations_sale_items_sale_item_id",
                        column: x => x.sale_item_id,
                        principalSchema: "sales",
                        principalTable: "sale_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pack_allocations_units_pack_unit_id",
                        column: x => x.pack_unit_id,
                        principalSchema: "inventory",
                        principalTable: "units",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_units_pack_quantity_bounds",
                schema: "inventory",
                table: "units",
                sql: "(pack_state IS NULL AND remaining_base_quantity IS NULL AND initial_base_quantity IS NULL) OR (pack_state IS NOT NULL AND initial_base_quantity IS NOT NULL AND remaining_base_quantity IS NOT NULL AND initial_base_quantity > 0 AND remaining_base_quantity >= 0 AND remaining_base_quantity <= initial_base_quantity AND ((pack_state = 'Sealed'::inventory.pack_state AND is_loose_stock IS FALSE AND remaining_base_quantity = initial_base_quantity) OR (pack_state = 'Open'::inventory.pack_state AND remaining_base_quantity > 0 AND ((is_loose_stock IS FALSE AND remaining_base_quantity < initial_base_quantity) OR (is_loose_stock IS TRUE AND remaining_base_quantity <= initial_base_quantity))) OR (pack_state = 'Consumed'::inventory.pack_state AND remaining_base_quantity = 0)))");

            migrationBuilder.CreateIndex(
                name: "ix_pack_allocations_lot_id",
                schema: "inventory",
                table: "pack_allocations",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_pack_allocations_movement_id",
                schema: "inventory",
                table: "pack_allocations",
                column: "movement_id");

            migrationBuilder.CreateIndex(
                name: "ix_pack_allocations_pack_unit_id_occurred_at",
                schema: "inventory",
                table: "pack_allocations",
                columns: new[] { "pack_unit_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pack_allocations_sale_item_id",
                schema: "inventory",
                table: "pack_allocations",
                column: "sale_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_pack_allocations_sale_return_item_id",
                schema: "inventory",
                table: "pack_allocations",
                column: "sale_return_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_pack_allocations_thaka_issue_item_id",
                schema: "inventory",
                table: "pack_allocations",
                column: "thaka_issue_item_id");

            migrationBuilder.CreateIndex(
                name: "uq_pack_alloc_return_idempotency",
                schema: "inventory",
                table: "pack_allocations",
                columns: new[] { "pack_unit_id", "sale_return_item_id" },
                unique: true,
                filter: "sale_return_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "uq_pack_alloc_sale_idempotency",
                schema: "inventory",
                table: "pack_allocations",
                columns: new[] { "pack_unit_id", "sale_item_id" },
                unique: true,
                filter: "sale_item_id IS NOT NULL");

            migrationBuilder.Sql(
                """
                -- Prevent mutations on append-only pack_allocations
                CREATE OR REPLACE FUNCTION inventory.fn_prevent_pack_allocation_mutation()
                RETURNS TRIGGER AS $$
                BEGIN
                    RAISE EXCEPTION 'Table inventory.pack_allocations is append-only. UPDATE, DELETE, and TRUNCATE are prohibited.'
                        USING ERRCODE = '55000';
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_pack_allocations_immutable ON inventory.pack_allocations;
                CREATE TRIGGER trg_pack_allocations_immutable
                BEFORE UPDATE OR DELETE ON inventory.pack_allocations
                FOR EACH ROW EXECUTE FUNCTION inventory.fn_prevent_pack_allocation_mutation();

                DROP TRIGGER IF EXISTS trg_pack_allocations_truncate_immutable ON inventory.pack_allocations;
                CREATE TRIGGER trg_pack_allocations_truncate_immutable
                BEFORE TRUNCATE ON inventory.pack_allocations
                FOR EACH STATEMENT EXECUTE FUNCTION inventory.fn_prevent_pack_allocation_mutation();

                -- Role separation grants for runtime
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'er_runtime') THEN
                        GRANT SELECT, INSERT ON inventory.pack_allocations TO er_runtime;
                    END IF;
                END $$;

                -- Legacy Data Backfill & Parity (ADR04 Section 6 / ER-G1R-002 Deterministic Lineage)
                -- 1. InStock container units with purchase item provenance
                UPDATE inventory.units u
                SET initial_base_quantity = pi.factor_to_base_snapshot,
                    remaining_base_quantity = pi.factor_to_base_snapshot,
                    pack_state = 'Sealed'::inventory.pack_state,
                    is_loose_stock = FALSE
                FROM purchasing.purchase_items pi
                JOIN catalog.products p ON p.id = pi.product_id
                WHERE u.source_purchase_item_id = pi.id
                  AND p.tracking_mode = 5
                  AND u.status = 1
                  AND u.pack_state IS NULL;

                -- 2. InStock container units fallback (deterministic non-base ProductUnit factor subquery)
                UPDATE inventory.units u
                SET initial_base_quantity = (
                        SELECT pu.factor_to_base_unit
                        FROM catalog.product_units pu
                        WHERE pu.product_id = u.product_id
                          AND (pu.unit_id <> p.base_unit_id OR pu.factor_to_base_unit > 1)
                        ORDER BY pu.factor_to_base_unit ASC
                        LIMIT 1
                    ),
                    remaining_base_quantity = (
                        SELECT pu.factor_to_base_unit
                        FROM catalog.product_units pu
                        WHERE pu.product_id = u.product_id
                          AND (pu.unit_id <> p.base_unit_id OR pu.factor_to_base_unit > 1)
                        ORDER BY pu.factor_to_base_unit ASC
                        LIMIT 1
                    ),
                    pack_state = 'Sealed'::inventory.pack_state,
                    is_loose_stock = FALSE
                FROM catalog.products p
                WHERE u.product_id = p.id
                  AND p.tracking_mode = 5
                  AND u.status = 1
                  AND u.pack_state IS NULL
                  AND EXISTS (
                      SELECT 1 FROM catalog.product_units pu
                      WHERE pu.product_id = p.id AND (pu.unit_id <> p.base_unit_id OR pu.factor_to_base_unit > 1)
                  );

                -- 3. Sold / Consumed container units (status = 2)
                UPDATE inventory.units u
                SET initial_base_quantity = COALESCE((SELECT pi.factor_to_base_snapshot FROM purchasing.purchase_items pi WHERE pi.id = u.source_purchase_item_id), 1),
                    remaining_base_quantity = 0,
                    pack_state = 'Consumed'::inventory.pack_state,
                    is_loose_stock = FALSE
                FROM catalog.products p
                WHERE u.product_id = p.id
                  AND p.tracking_mode = 5
                  AND u.status = 2
                  AND u.pack_state IS NULL;

                -- 4. Scrapped (8) / Missing (12) container units with known purchase item provenance
                UPDATE inventory.units u
                SET initial_base_quantity = COALESCE((SELECT pi.factor_to_base_snapshot FROM purchasing.purchase_items pi WHERE pi.id = u.source_purchase_item_id), 1),
                    remaining_base_quantity = 0,
                    pack_state = 'Consumed'::inventory.pack_state,
                    is_loose_stock = FALSE
                FROM catalog.products p
                WHERE u.product_id = p.id
                  AND p.tracking_mode = 5
                  AND u.status IN (8, 12)
                  AND u.pack_state IS NULL
                  AND u.source_purchase_item_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- ER-G1R-001 Fail-Closed Guard: Prevent destructive rollback if post-activation ledger data exists
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'inventory' AND table_name = 'pack_allocations'
                    ) THEN
                        IF EXISTS (SELECT 1 FROM inventory.pack_allocations LIMIT 1) THEN
                            RAISE EXCEPTION 'Destructive rollback prohibited: inventory.pack_allocations contains post-activation allocation ledger facts. Conservation requires ledger preservation.'
                                USING ERRCODE = '55000';
                        END IF;
                    END IF;
                END $$;

                DROP TRIGGER IF EXISTS trg_pack_allocations_truncate_immutable ON inventory.pack_allocations;
                DROP TRIGGER IF EXISTS trg_pack_allocations_immutable ON inventory.pack_allocations;
                DROP FUNCTION IF EXISTS inventory.fn_prevent_pack_allocation_mutation();
                """);

            migrationBuilder.DropTable(
                name: "pack_allocations",
                schema: "inventory");

            migrationBuilder.DropCheckConstraint(
                name: "chk_units_pack_quantity_bounds",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropColumn(
                name: "initial_base_quantity",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropColumn(
                name: "is_loose_stock",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropColumn(
                name: "pack_state",
                schema: "inventory",
                table: "units");

            migrationBuilder.DropColumn(
                name: "remaining_base_quantity",
                schema: "inventory",
                table: "units");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:Enum:inventory.pack_state", "Sealed,Open,Consumed");
        }
    }
}
