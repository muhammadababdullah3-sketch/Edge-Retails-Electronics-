using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7Pass5WarrantySourceAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "claim_source_allocations",
                schema: "warranty",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_consumption_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_claim_source_allocations", x => x.id);
                    table.CheckConstraint("ck_claim_source_allocation_operation_required", "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_claim_source_allocation_quantity_positive", "base_quantity > 0");
                    table.ForeignKey(
                        name: "fk_claim_source_allocation_actor",
                        column: x => x.actor_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claim_source_allocation_consumption",
                        column: x => x.sale_consumption_id,
                        principalSchema: "inventory",
                        principalTable: "lot_consumptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_claim_source_allocation_item",
                        column: x => x.claim_item_id,
                        principalSchema: "warranty",
                        principalTable: "claim_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_return_source_allocations",
                schema: "warranty",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_return_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_consumption_id = table.Column<Guid>(type: "uuid", nullable: false),
                    return_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    restored_inventory_lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_return_source_allocations", x => x.id);
                    table.CheckConstraint("ck_sale_return_source_allocation_operation_required", "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_sale_return_source_allocation_quantity_positive", "base_quantity > 0");
                    table.ForeignKey(
                        name: "fk_sale_return_source_allocation_actor",
                        column: x => x.actor_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_return_source_allocation_consumption",
                        column: x => x.sale_consumption_id,
                        principalSchema: "inventory",
                        principalTable: "lot_consumptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_return_source_allocation_item",
                        column: x => x.sale_return_item_id,
                        principalSchema: "sales",
                        principalTable: "return_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_return_source_allocation_movement",
                        column: x => x.return_movement_id,
                        principalSchema: "inventory",
                        principalTable: "movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_return_source_allocation_restored_lot",
                        column: x => x.restored_inventory_lot_id,
                        principalSchema: "inventory",
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shop_send_allocations",
                schema: "warranty",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_inventory_lot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    send_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    source_unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    send_time_mwa_unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    send_time_carrying_value_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shop_send_allocations", x => x.id);
                    table.CheckConstraint("ck_shop_send_allocation_operation_required", "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_shop_send_allocation_quantity_positive", "base_quantity > 0");
                    table.CheckConstraint("ck_shop_send_allocation_values_nonnegative", "source_unit_cost_snapshot >= 0 AND send_time_mwa_unit_cost_snapshot >= 0 AND send_time_carrying_value_snapshot >= 0");
                    table.ForeignKey(
                        name: "fk_shop_send_allocation_actor",
                        column: x => x.actor_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shop_send_allocation_case",
                        column: x => x.case_id,
                        principalSchema: "warranty",
                        principalTable: "shop_stock_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shop_send_allocation_movement",
                        column: x => x.send_movement_id,
                        principalSchema: "inventory",
                        principalTable: "movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shop_send_allocation_original_lot",
                        column: x => x.original_inventory_lot_id,
                        principalSchema: "inventory",
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shop_resolution_allocations",
                schema: "warranty",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    send_allocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution_movement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolved_base_quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    resolution_outcome = table.Column<int>(type: "integer", nullable: false),
                    resolution_time_mwa_unit_cost_snapshot = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    actual_resolved_carrying_value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    supplier_credit_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    replacement_inventory_lot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shop_resolution_allocations", x => x.id);
                    table.CheckConstraint("ck_shop_resolution_allocation_credit", "(resolution_outcome = 4 AND supplier_credit_amount IS NOT NULL) OR (resolution_outcome <> 4 AND supplier_credit_amount IS NULL)");
                    table.CheckConstraint("ck_shop_resolution_allocation_operation_required", "client_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_shop_resolution_allocation_outcome", "resolution_outcome IN (1, 2, 3, 4, 7)");
                    table.CheckConstraint("ck_shop_resolution_allocation_quantity_positive", "resolved_base_quantity > 0");
                    table.CheckConstraint("ck_shop_resolution_allocation_replacement", "replacement_inventory_lot_id IS NULL OR resolution_outcome = 2");
                    table.CheckConstraint("ck_shop_resolution_allocation_valuation", "(resolution_outcome IN (1, 2, 3) AND actual_resolved_carrying_value = 0 AND resolution_time_mwa_unit_cost_snapshot IS NULL) OR (resolution_outcome IN (4, 7) AND resolution_time_mwa_unit_cost_snapshot IS NOT NULL)");
                    table.CheckConstraint("ck_shop_resolution_allocation_values_nonnegative", "actual_resolved_carrying_value >= 0 AND (resolution_time_mwa_unit_cost_snapshot IS NULL OR resolution_time_mwa_unit_cost_snapshot >= 0) AND (supplier_credit_amount IS NULL OR supplier_credit_amount >= 0)");
                    table.ForeignKey(
                        name: "fk_shop_resolution_allocation_actor",
                        column: x => x.actor_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shop_resolution_allocation_movement",
                        column: x => x.resolution_movement_id,
                        principalSchema: "inventory",
                        principalTable: "movements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shop_resolution_allocation_replacement_lot",
                        column: x => x.replacement_inventory_lot_id,
                        principalSchema: "inventory",
                        principalTable: "lots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shop_resolution_allocation_send",
                        column: x => x.send_allocation_id,
                        principalSchema: "warranty",
                        principalTable: "shop_send_allocations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_claim_source_allocation_operation",
                schema: "warranty",
                table: "claim_source_allocations",
                column: "client_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_source_allocations_actor_id",
                schema: "warranty",
                table: "claim_source_allocations",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_claim_source_allocations_sale_consumption_id",
                schema: "warranty",
                table: "claim_source_allocations",
                column: "sale_consumption_id");

            migrationBuilder.CreateIndex(
                name: "ux_claim_source_allocation_consumption",
                schema: "warranty",
                table: "claim_source_allocations",
                columns: new[] { "claim_item_id", "sale_consumption_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_return_source_allocation_operation",
                schema: "warranty",
                table: "sale_return_source_allocations",
                column: "client_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_return_source_allocations_actor_id",
                schema: "warranty",
                table: "sale_return_source_allocations",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_return_source_allocations_return_movement_id",
                schema: "warranty",
                table: "sale_return_source_allocations",
                column: "return_movement_id");

            migrationBuilder.CreateIndex(
                name: "ix_sale_return_source_allocations_sale_consumption_id",
                schema: "warranty",
                table: "sale_return_source_allocations",
                column: "sale_consumption_id");

            migrationBuilder.CreateIndex(
                name: "ux_sale_return_source_allocation_consumption",
                schema: "warranty",
                table: "sale_return_source_allocations",
                columns: new[] { "sale_return_item_id", "sale_consumption_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sale_return_source_allocation_restored_lot",
                schema: "warranty",
                table: "sale_return_source_allocations",
                column: "restored_inventory_lot_id",
                unique: true,
                filter: "restored_inventory_lot_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_shop_resolution_allocation_operation",
                schema: "warranty",
                table: "shop_resolution_allocations",
                column: "client_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_resolution_allocations_actor_id",
                schema: "warranty",
                table: "shop_resolution_allocations",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_resolution_allocations_replacement_inventory_lot_id",
                schema: "warranty",
                table: "shop_resolution_allocations",
                column: "replacement_inventory_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_resolution_allocations_resolution_movement_id",
                schema: "warranty",
                table: "shop_resolution_allocations",
                column: "resolution_movement_id");

            migrationBuilder.CreateIndex(
                name: "ux_shop_resolution_allocation_operation",
                schema: "warranty",
                table: "shop_resolution_allocations",
                columns: new[] { "send_allocation_id", "client_operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shop_send_allocation_operation",
                schema: "warranty",
                table: "shop_send_allocations",
                column: "client_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_send_allocations_actor_id",
                schema: "warranty",
                table: "shop_send_allocations",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_send_allocations_original_inventory_lot_id",
                schema: "warranty",
                table: "shop_send_allocations",
                column: "original_inventory_lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_shop_send_allocations_send_movement_id",
                schema: "warranty",
                table: "shop_send_allocations",
                column: "send_movement_id");

            migrationBuilder.CreateIndex(
                name: "ux_shop_send_allocation_source",
                schema: "warranty",
                table: "shop_send_allocations",
                columns: new[] { "case_id", "original_inventory_lot_id", "send_movement_id" },
                unique: true);
            migrationBuilder.Sql("""
                -- Resolution04/05: append-only provenance, not a financial reserve or inferred backfill.
                CREATE FUNCTION warranty.enforce_provenance_append_only() RETURNS trigger LANGUAGE plpgsql AS $pass5$
                BEGIN
                  RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_APPEND_ONLY: allocation facts cannot be updated or deleted';
                END;
                $pass5$;
                
                -- Physical exemption quantity comes from the received event and its full unit set.
                -- It does not consult current tracking mode, current bucket balance or order pack size.
                CREATE FUNCTION warranty.physical_source_quantity(p_unit_id uuid) RETURNS numeric LANGUAGE sql AS $pass5$
                  SELECT (SELECT sum(e.quantity_delta) FROM inventory.movement_effects e
                          WHERE e.movement_id=l.source_movement_id AND e.quantity_delta>0)
                    / nullif((SELECT count(DISTINCT mu.inventory_unit_id) FROM inventory.movement_units mu
                              WHERE mu.movement_id=l.source_movement_id),0)
                  FROM inventory.units u JOIN inventory.lots l ON l.id=u.inventory_lot_id
                  WHERE u.id=p_unit_id AND l.product_id=u.product_id
                    AND (EXISTS (SELECT 1 FROM inventory.movement_units mu WHERE mu.movement_id=l.source_movement_id AND mu.inventory_unit_id=u.id)
                      OR (u.origin_type=2 AND u.source_warranty_case_id IS NOT NULL AND EXISTS (
                        SELECT 1 FROM inventory.movements replacement
                        JOIN inventory.movement_units new_link ON new_link.movement_id=replacement.id AND new_link.inventory_unit_id=u.id
                        JOIN inventory.movement_units old_link ON old_link.movement_id=replacement.id AND old_link.to_status=7 AND old_link.from_status=6
                        JOIN inventory.units original ON original.id=old_link.inventory_unit_id
                        WHERE replacement.movement_type=14 AND replacement.reference_type='SHOP_WARRANTY'
                          AND replacement.reference_id=u.source_warranty_case_id AND replacement.product_id=u.product_id
                          AND new_link.from_status IS NULL AND new_link.to_status=1 AND original.product_id=u.product_id
                          AND original.inventory_lot_id=l.id AND original.acquisition_cost=u.acquisition_cost
                          AND EXISTS (SELECT 1 FROM inventory.movements send JOIN inventory.movement_units sent ON sent.movement_id=send.id
                            WHERE send.movement_type=12 AND send.reference_type='SHOP_WARRANTY' AND send.reference_id=u.source_warranty_case_id
                              AND sent.inventory_unit_id=original.id AND sent.to_status=6))))
                $pass5$;
                
                CREATE FUNCTION warranty.assert_shop_case_source(p_case_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
                DECLARE c warranty.shop_stock_cases%ROWTYPE; sent numeric; resolved numeric;
                BEGIN
                  SELECT * INTO c FROM warranty.shop_stock_cases WHERE id=p_case_id FOR NO KEY UPDATE;
                  IF NOT FOUND THEN RETURN; END IF;
                  SELECT coalesce(sum(a.base_quantity),0) INTO sent FROM warranty.shop_send_allocations a WHERE a.case_id=c.id;
                  -- Serialize the cross-case capacity check on shared original source rows.
                  PERFORM l.id FROM inventory.lots l WHERE l.id IN
                    (SELECT a.original_inventory_lot_id FROM warranty.shop_send_allocations a WHERE a.case_id=c.id)
                    ORDER BY l.id FOR NO KEY UPDATE;
                  -- A real physical send is independently evidenced by immutable movement-unit links/effects.
                  IF sent=0 AND EXISTS (
                    SELECT 1 FROM inventory.movements m
                    WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.product_id=c.product_id AND m.movement_type=12
                    AND EXISTS (SELECT 1 FROM inventory.movement_units mu JOIN inventory.units u ON u.id=mu.inventory_unit_id
                                WHERE mu.movement_id=m.id AND u.product_id=c.product_id AND mu.to_status=6)
                    AND NOT EXISTS (SELECT 1 FROM inventory.movement_units mu JOIN inventory.units u ON u.id=mu.inventory_unit_id
                                    LEFT JOIN catalog.supplier_products sp ON sp.id=u.supplier_product_id
                                    WHERE mu.movement_id=m.id AND (u.product_id<>c.product_id OR mu.to_status<>6
                                      OR sp.supplier_id IS DISTINCT FROM c.supplier_id OR warranty.physical_source_quantity(u.id) IS NULL))
                    AND (SELECT sum(warranty.physical_source_quantity(mu.inventory_unit_id)) FROM inventory.movement_units mu
                         WHERE mu.movement_id=m.id)=c.base_quantity
                    AND (SELECT coalesce(sum(e.quantity_delta),0) FROM inventory.movement_effects e
                         WHERE e.movement_id=m.id AND e.stock_bucket=4)=c.base_quantity
                  ) THEN RETURN; END IF;
                  IF c.status=1 AND sent=0 AND NOT EXISTS (SELECT 1 FROM inventory.movements WHERE reference_type='SHOP_WARRANTY' AND reference_id=c.id) THEN RETURN; END IF;
                  IF sent<>c.base_quantity THEN
                    RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: bulk case send allocations must cover its quantity';
                  END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.shop_send_allocations a
                    JOIN inventory.lots l ON l.id=a.original_inventory_lot_id
                    JOIN inventory.movements m ON m.id=a.send_movement_id
                    LEFT JOIN purchasing.purchase_items pi ON pi.id=l.purchase_item_id
                    LEFT JOIN purchasing.purchases p ON p.id=pi.purchase_id
                    WHERE a.case_id=c.id AND (l.product_id<>c.product_id OR c.source_purchase_item_id IS NULL
                      OR l.purchase_item_id IS DISTINCT FROM c.source_purchase_item_id OR p.supplier_id IS DISTINCT FROM c.supplier_id
                      OR a.actor_id<>m.actor_id OR a.client_operation_id<>m.correlation_id
                      OR m.product_id<>c.product_id OR m.reference_type<>'SHOP_WARRANTY' OR m.reference_id IS DISTINCT FROM c.id OR m.movement_type<>12)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: case original lot/purchase/supplier/send lineage mismatch'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.shop_send_allocations a WHERE a.case_id=c.id
                    GROUP BY a.send_movement_id HAVING sum(a.base_quantity)<>(SELECT coalesce(sum(e.quantity_delta),0)
                      FROM inventory.movement_effects e WHERE e.movement_id=a.send_movement_id AND e.stock_bucket=4)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: send movement quantity mismatch'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.shop_resolution_allocations r
                    JOIN warranty.shop_send_allocations a ON a.id=r.send_allocation_id
                    JOIN inventory.movements m ON m.id=r.resolution_movement_id
                    WHERE a.case_id=c.id AND (r.actor_id<>m.actor_id OR r.client_operation_id<>m.correlation_id
                      OR m.product_id<>c.product_id OR m.reference_type<>'SHOP_WARRANTY'
                      OR m.reference_id IS DISTINCT FROM c.id OR m.movement_type<>CASE r.resolution_outcome WHEN 1 THEN 13 WHEN 2 THEN 14 WHEN 3 THEN 15 WHEN 4 THEN 19 WHEN 7 THEN 16 END)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: resolution event does not belong to case/outcome'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.shop_resolution_allocations r JOIN warranty.shop_send_allocations a ON a.id=r.send_allocation_id
                    WHERE a.case_id=c.id GROUP BY r.resolution_movement_id
                    HAVING sum(r.resolved_base_quantity)<>-(SELECT coalesce(sum(e.quantity_delta),0)
                      FROM inventory.movement_effects e WHERE e.movement_id=r.resolution_movement_id AND e.stock_bucket=4)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: resolution movement quantity mismatch'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.shop_send_allocations a WHERE a.case_id=c.id
                    AND coalesce((SELECT sum(r.resolved_base_quantity) FROM warranty.shop_resolution_allocations r WHERE r.send_allocation_id=a.id),0)>a.base_quantity
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_CAPACITY: case allocation over-resolved'; END IF;
                  SELECT coalesce(sum(r.resolved_base_quantity),0) INTO resolved FROM warranty.shop_resolution_allocations r
                    JOIN warranty.shop_send_allocations a ON a.id=r.send_allocation_id WHERE a.case_id=c.id;
                  IF (c.status IN (3,4,5) AND resolved<>sent) OR (c.status=2 AND resolved>=sent) THEN
                    RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: case status does not match remaining quantity';
                  END IF;
                  -- At COMMIT, all case-owned unresolved quantities must still exist in their original lot custody.
                  IF EXISTS (
                    SELECT 1 FROM warranty.shop_send_allocations a WHERE a.case_id=c.id AND
                      (SELECT coalesce(sum(s.base_quantity-coalesce((SELECT sum(r.resolved_base_quantity)
                         FROM warranty.shop_resolution_allocations r WHERE r.send_allocation_id=s.id),0)),0)
                       FROM warranty.shop_send_allocations s WHERE s.original_inventory_lot_id=a.original_inventory_lot_id)
                      >(SELECT coalesce(sum(b.quantity),0) FROM inventory.lot_bucket_balances b
                        WHERE b.lot_id=a.original_inventory_lot_id AND b.stock_bucket=4)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_CAPACITY: original lot custody cannot fund case allocations'; END IF;
                END;
                $pass5$;
                
                CREATE FUNCTION warranty.validate_shop_source_authority() RETURNS trigger LANGUAGE plpgsql AS $pass5$
                DECLARE target_case uuid;
                BEGIN
                  IF TG_TABLE_NAME='shop_stock_cases' THEN target_case:=NEW.id;
                  ELSIF TG_TABLE_NAME='shop_send_allocations' THEN target_case:=NEW.case_id;
                  ELSE SELECT case_id INTO target_case FROM warranty.shop_send_allocations WHERE id=NEW.send_allocation_id;
                  END IF;
                  PERFORM warranty.assert_shop_case_source(target_case);
                  RETURN NULL;
                END;
                $pass5$;
                
                CREATE FUNCTION warranty.assert_sold_item_source(p_sale_item_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
                DECLARE s sales.sale_items%ROWTYPE;
                BEGIN
                  SELECT * INTO s FROM sales.sale_items WHERE id=p_sale_item_id FOR NO KEY UPDATE;
                  IF NOT FOUND THEN
                    RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: original sale item was not found';
                  END IF;
                  PERFORM m.id FROM inventory.movements m WHERE m.id=s.inventory_movement_id FOR NO KEY UPDATE;
                  IF NOT EXISTS (SELECT 1 FROM inventory.movements m WHERE m.id=s.inventory_movement_id
                    AND m.product_id=s.product_id AND m.movement_type=3 AND m.reference_type='SALE' AND m.reference_id=s.sale_id)
                    OR EXISTS (SELECT 1 FROM sales.sale_items other WHERE other.inventory_movement_id=s.inventory_movement_id AND other.id<>s.id)
                    OR s.base_quantity<>(SELECT coalesce(sum(lc.quantity),0) FROM inventory.lot_consumptions lc WHERE lc.movement_id=s.inventory_movement_id)
                  THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: original sold event ownership or quantity mismatch'; END IF;
                  IF EXISTS (SELECT 1 FROM sales.sale_item_units su JOIN inventory.units u ON u.id=su.inventory_unit_id
                             JOIN inventory.movement_units mu ON mu.inventory_unit_id=u.id AND mu.movement_id=s.inventory_movement_id
                             WHERE su.sale_item_id=s.id AND u.product_id=s.product_id AND mu.to_status=2)
                    AND NOT EXISTS (SELECT 1 FROM sales.sale_item_units su JOIN inventory.units u ON u.id=su.inventory_unit_id
                                    WHERE su.sale_item_id=s.id AND (u.product_id<>s.product_id OR NOT EXISTS
                                      (SELECT 1 FROM inventory.movement_units mu WHERE mu.inventory_unit_id=u.id AND mu.movement_id=s.inventory_movement_id AND mu.to_status=2)))
                    AND (SELECT count(*) FROM sales.sale_item_units su WHERE su.sale_item_id=s.id)=
                        (SELECT count(*) FROM inventory.movement_units mu WHERE mu.movement_id=s.inventory_movement_id)
                    AND NOT EXISTS (SELECT 1 FROM sales.sale_item_units su WHERE su.sale_item_id=s.id AND warranty.physical_source_quantity(su.inventory_unit_id) IS NULL)
                    AND (SELECT sum(warranty.physical_source_quantity(su.inventory_unit_id)) FROM sales.sale_item_units su WHERE su.sale_item_id=s.id)=s.base_quantity
                  THEN
                    IF EXISTS (
                      SELECT 1 FROM warranty.claim_items ci WHERE ci.original_sale_item_id=s.id AND
                        (ci.product_id<>s.product_id OR ci.quantity IS DISTINCT FROM
                          (SELECT sum(warranty.physical_source_quantity(cu.original_inventory_unit_id))
                           FROM warranty.claim_item_units cu WHERE cu.claim_item_id=ci.id)
                         OR EXISTS (SELECT 1 FROM warranty.claim_item_units cu WHERE cu.claim_item_id=ci.id AND
                           (cu.original_inventory_unit_id IS NULL OR NOT EXISTS (SELECT 1 FROM sales.sale_item_units su
                             WHERE su.sale_item_id=s.id AND su.inventory_unit_id=cu.original_inventory_unit_id))))
                    ) OR EXISTS (
                      SELECT 1 FROM sales.return_items ri WHERE ri.sale_item_id=s.id AND
                        (ri.product_id<>s.product_id OR ri.base_quantity IS DISTINCT FROM
                          (SELECT sum(warranty.physical_source_quantity(ru.inventory_unit_id))
                           FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id)
                         OR EXISTS (SELECT 1 FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id AND
                           NOT EXISTS (SELECT 1 FROM sales.sale_item_units su WHERE su.sale_item_id=s.id AND su.inventory_unit_id=ru.inventory_unit_id)))
                    ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: exact claim/return owner physical graph mismatch'; END IF;
                    RETURN;
                  END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.claim_items ci JOIN warranty.claims c ON c.id=ci.claim_id
                    WHERE ci.original_sale_item_id=s.id AND (c.status NOT IN (6,7) OR (c.status=6 AND ci.resolution_type IN (2,5)))
                    AND ci.quantity<>(SELECT coalesce(sum(a.base_quantity),0) FROM warranty.claim_source_allocations a WHERE a.claim_item_id=ci.id)
                  ) OR EXISTS (
                    SELECT 1 FROM sales.return_items ri WHERE ri.sale_item_id=s.id AND
                      ri.base_quantity<>(SELECT coalesce(sum(a.base_quantity),0) FROM warranty.sale_return_source_allocations a WHERE a.sale_return_item_id=ri.id)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: ambiguous or missing bulk sold-source allocation'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.claim_source_allocations a JOIN warranty.claim_items ci ON ci.id=a.claim_item_id
                    JOIN warranty.claims c ON c.id=ci.claim_id JOIN sales.sales sale ON sale.id=s.sale_id
                    JOIN inventory.lot_consumptions lc ON lc.id=a.sale_consumption_id
                    JOIN inventory.lots l ON l.id=lc.lot_id LEFT JOIN purchasing.purchase_items pi ON pi.id=l.purchase_item_id
                    LEFT JOIN purchasing.purchases p ON p.id=pi.purchase_id
                    WHERE ci.original_sale_item_id=s.id AND (lc.movement_id<>s.inventory_movement_id OR l.product_id<>s.product_id
                      OR ci.product_id<>s.product_id OR p.supplier_id IS DISTINCT FROM c.supplier_id OR c.supplier_id IS NULL
                      OR c.original_sale_id IS DISTINCT FROM s.sale_id OR c.customer_id IS DISTINCT FROM sale.customer_id
                      OR a.client_operation_id<>c.client_operation_id OR a.actor_id<>c.created_by)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: claim sold consumption/product/supplier mismatch'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.sale_return_source_allocations a JOIN sales.return_items ri ON ri.id=a.sale_return_item_id
                    JOIN inventory.lot_consumptions lc ON lc.id=a.sale_consumption_id JOIN inventory.lots l ON l.id=lc.lot_id
                    JOIN sales.returns sr ON sr.id=ri.sale_return_id
                    JOIN inventory.movements m ON m.id=a.return_movement_id LEFT JOIN inventory.lots restored ON restored.id=a.restored_inventory_lot_id
                    WHERE ri.sale_item_id=s.id AND (lc.movement_id<>s.inventory_movement_id OR l.product_id<>s.product_id
                      OR ri.product_id<>s.product_id OR m.product_id<>s.product_id OR m.movement_type<>4
                      OR sr.sale_id<>s.sale_id OR a.client_operation_id<>sr.client_operation_id OR a.actor_id<>sr.created_by
                      OR m.actor_id<>a.actor_id OR m.correlation_id<>a.client_operation_id
                      OR m.reference_type<>'SALE_RETURN' OR m.reference_id IS DISTINCT FROM ri.sale_return_id OR restored.id IS NULL
                      OR (restored.id IS NOT NULL AND (restored.product_id<>s.product_id OR restored.source_movement_id<>m.id
                        OR restored.purchase_item_id IS DISTINCT FROM l.purchase_item_id OR restored.received_quantity<>a.base_quantity)))
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: return sold-source/item/movement/restoration mismatch'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM warranty.sale_return_source_allocations a JOIN sales.return_items ri ON ri.id=a.sale_return_item_id
                    WHERE ri.sale_item_id=s.id GROUP BY a.return_movement_id
                    HAVING sum(a.base_quantity)<>(SELECT coalesce(sum(e.quantity_delta),0) FROM inventory.movement_effects e
                      WHERE e.movement_id=a.return_movement_id AND e.quantity_delta>0)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: return allocation/event quantity mismatch'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM inventory.lot_consumptions lc WHERE lc.movement_id=s.inventory_movement_id AND lc.quantity<
                    coalesce((SELECT sum(a.base_quantity) FROM warranty.claim_source_allocations a JOIN warranty.claim_items ci ON ci.id=a.claim_item_id
                      JOIN warranty.claims c ON c.id=ci.claim_id WHERE a.sale_consumption_id=lc.id
                      AND (c.status NOT IN (6,7) OR (c.status=6 AND ci.resolution_type IN (2,5)))),0)
                    +coalesce((SELECT sum(a.base_quantity) FROM warranty.sale_return_source_allocations a WHERE a.sale_consumption_id=lc.id),0)
                  ) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_CAPACITY: original sold consumption reused'; END IF;
                END;
                $pass5$;
                
                CREATE FUNCTION warranty.validate_sold_source_authority() RETURNS trigger LANGUAGE plpgsql AS $pass5$
                DECLARE target_item uuid;
                BEGIN
                  IF TG_TABLE_NAME='claim_items' THEN
                    IF TG_OP='UPDATE' AND OLD.original_sale_item_id IS NOT NULL AND OLD.original_sale_item_id IS DISTINCT FROM NEW.original_sale_item_id THEN
                      PERFORM warranty.assert_sold_item_source(OLD.original_sale_item_id);
                    END IF;
                    target_item:=NEW.original_sale_item_id;
                  ELSIF TG_TABLE_NAME='return_items' THEN target_item:=NEW.sale_item_id;
                  ELSIF TG_TABLE_NAME='claim_source_allocations' THEN SELECT original_sale_item_id INTO target_item FROM warranty.claim_items WHERE id=NEW.claim_item_id;
                  ELSE SELECT sale_item_id INTO target_item FROM sales.return_items WHERE id=NEW.sale_return_item_id;
                  END IF;
                  IF target_item IS NULL THEN
                    IF TG_TABLE_NAME='claim_items' THEN
                      PERFORM warranty.assert_external_claim_source(NEW.id);
                      RETURN NULL;
                    END IF;
                    RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: original sale item is required';
                  END IF;
                  PERFORM warranty.assert_sold_item_source(target_item);
                  RETURN NULL;
                END;
                $pass5$;
                
                CREATE FUNCTION warranty.assert_external_claim_source(p_claim_item_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
                DECLARE ci warranty.claim_items%ROWTYPE;
                BEGIN
                  SELECT * INTO ci FROM warranty.claim_items WHERE id=p_claim_item_id;
                  IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM warranty.claim_item_units cu WHERE cu.claim_item_id=ci.id)
                    OR EXISTS (SELECT 1 FROM warranty.claim_item_units cu LEFT JOIN inventory.units u ON u.id=cu.original_inventory_unit_id
                      WHERE cu.claim_item_id=ci.id AND (u.id IS NULL OR u.product_id<>ci.product_id OR warranty.physical_source_quantity(u.id) IS NULL))
                    OR ci.quantity IS DISTINCT FROM (SELECT sum(warranty.physical_source_quantity(cu.original_inventory_unit_id))
                      FROM warranty.claim_item_units cu WHERE cu.claim_item_id=ci.id)
                  THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: external claim requires a complete physical source graph'; END IF;
                END;
                $pass5$;
                
                CREATE FUNCTION warranty.validate_claim_lifecycle_source() RETURNS trigger LANGUAGE plpgsql AS $pass5$
                DECLARE ci warranty.claim_items%ROWTYPE;
                BEGIN
                  FOR ci IN SELECT * FROM warranty.claim_items WHERE claim_id=NEW.id ORDER BY original_sale_item_id, id LOOP
                    IF ci.original_sale_item_id IS NULL THEN PERFORM warranty.assert_external_claim_source(ci.id);
                    ELSE PERFORM warranty.assert_sold_item_source(ci.original_sale_item_id); END IF;
                  END LOOP;
                  RETURN NULL;
                END;
                $pass5$;
                
                CREATE FUNCTION warranty.validate_shop_lot_custody() RETURNS trigger LANGUAGE plpgsql AS $pass5$
                DECLARE target_lot uuid; target_lots uuid[];
                BEGIN
                  IF TG_OP='DELETE' THEN target_lots:=ARRAY[OLD.lot_id];
                  ELSIF TG_OP='UPDATE' THEN target_lots:=ARRAY[OLD.lot_id,NEW.lot_id];
                  ELSE target_lots:=ARRAY[NEW.lot_id]; END IF;
                  FOR target_lot IN SELECT DISTINCT unnest(target_lots) ORDER BY 1 LOOP
                  IF NOT EXISTS (SELECT 1 FROM warranty.shop_send_allocations a WHERE a.original_inventory_lot_id=target_lot) THEN CONTINUE; END IF;
                  PERFORM l.id FROM inventory.lots l WHERE l.id=target_lot FOR NO KEY UPDATE;
                  IF (SELECT coalesce(sum(a.base_quantity-coalesce((SELECT sum(r.resolved_base_quantity)
                        FROM warranty.shop_resolution_allocations r WHERE r.send_allocation_id=a.id),0)),0)
                      FROM warranty.shop_send_allocations a WHERE a.original_inventory_lot_id=target_lot)
                    >(SELECT coalesce(sum(b.quantity),0) FROM inventory.lot_bucket_balances b WHERE b.lot_id=target_lot AND b.stock_bucket=4)
                  THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_CAPACITY: original lot custody cannot fund case allocations'; END IF;
                  END LOOP;
                  RETURN NULL;
                END;
                $pass5$;
                
                CREATE TRIGGER tr_shop_send_allocations_append_only BEFORE UPDATE OR DELETE ON warranty.shop_send_allocations FOR EACH ROW EXECUTE FUNCTION warranty.enforce_provenance_append_only();
                CREATE TRIGGER tr_shop_resolution_allocations_append_only BEFORE UPDATE OR DELETE ON warranty.shop_resolution_allocations FOR EACH ROW EXECUTE FUNCTION warranty.enforce_provenance_append_only();
                CREATE TRIGGER tr_claim_source_allocations_append_only BEFORE UPDATE OR DELETE ON warranty.claim_source_allocations FOR EACH ROW EXECUTE FUNCTION warranty.enforce_provenance_append_only();
                CREATE TRIGGER tr_sale_return_source_allocations_append_only BEFORE UPDATE OR DELETE ON warranty.sale_return_source_allocations FOR EACH ROW EXECUTE FUNCTION warranty.enforce_provenance_append_only();
                CREATE CONSTRAINT TRIGGER ct_shop_stock_cases_source_authority AFTER INSERT OR UPDATE ON warranty.shop_stock_cases DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_shop_source_authority();
                CREATE CONSTRAINT TRIGGER ct_shop_send_allocations_source_authority AFTER INSERT ON warranty.shop_send_allocations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_shop_source_authority();
                CREATE CONSTRAINT TRIGGER ct_shop_resolution_allocations_source_authority AFTER INSERT ON warranty.shop_resolution_allocations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_shop_source_authority();
                CREATE CONSTRAINT TRIGGER ct_claim_items_source_authority AFTER INSERT OR UPDATE ON warranty.claim_items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_sold_source_authority();
                CREATE CONSTRAINT TRIGGER ct_claims_source_authority AFTER UPDATE ON warranty.claims DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_claim_lifecycle_source();
                CREATE CONSTRAINT TRIGGER ct_return_items_source_authority AFTER INSERT OR UPDATE ON sales.return_items DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_sold_source_authority();
                CREATE CONSTRAINT TRIGGER ct_shop_lot_custody_authority AFTER INSERT OR UPDATE OR DELETE ON inventory.lot_bucket_balances DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_shop_lot_custody();
                CREATE CONSTRAINT TRIGGER ct_claim_source_allocations_source_authority AFTER INSERT ON warranty.claim_source_allocations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_sold_source_authority();
                CREATE CONSTRAINT TRIGGER ct_sale_return_source_allocations_source_authority AFTER INSERT ON warranty.sale_return_source_allocations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.validate_sold_source_authority();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Source allocations are immutable business history. Recovery uses verified backup,
            // never a downgrade that discards these facts or re-enables an older bulk writer.
            throw new InvalidOperationException(
                "Phase7Pass5WarrantySourceAuthority is forward-only; use verified backup recovery.");
        }
    }
}
