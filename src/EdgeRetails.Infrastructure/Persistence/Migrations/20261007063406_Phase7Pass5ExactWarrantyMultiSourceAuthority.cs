using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    /// <summary>Forward-only correction of exact shop warranty physical source authority.</summary>
    public partial class Phase7Pass5ExactWarrantyMultiSourceAuthority : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION warranty.assert_shop_case_source(p_case_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
                DECLARE c warranty.shop_stock_cases%ROWTYPE; sent numeric; resolved numeric; exact_graph boolean;
                BEGIN
                  SELECT * INTO c FROM warranty.shop_stock_cases WHERE id=p_case_id FOR NO KEY UPDATE;
                  IF NOT FOUND THEN RETURN; END IF;
                  SELECT coalesce(sum(a.base_quantity),0) INTO sent FROM warranty.shop_send_allocations a WHERE a.case_id=c.id;
                  -- Serialize the cross-case capacity check on shared original source rows.
                  PERFORM l.id FROM inventory.lots l WHERE l.id IN
                    (SELECT a.original_inventory_lot_id FROM warranty.shop_send_allocations a WHERE a.case_id=c.id)
                    ORDER BY l.id FOR NO KEY UPDATE;
                  -- Classification is an observed immutable physical graph, never caller/product mode.
                  SELECT EXISTS (SELECT 1 FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                    WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type=12) INTO exact_graph;
                  IF exact_graph THEN
                    -- Every send event must be a complete, purchase-backed physical event.
                    IF EXISTS (
                      SELECT 1 FROM inventory.movements m
                      WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type=12 AND
                      (m.product_id IS DISTINCT FROM c.product_id
                       OR NOT EXISTS (SELECT 1 FROM inventory.movement_units mu WHERE mu.movement_id=m.id)
                       OR EXISTS (SELECT 1 FROM inventory.movement_units mu
                         LEFT JOIN inventory.units u ON u.id=mu.inventory_unit_id
                         LEFT JOIN inventory.lots l ON l.id=u.inventory_lot_id
                         LEFT JOIN purchasing.purchase_items pi ON pi.id=l.purchase_item_id
                         LEFT JOIN purchasing.purchases p ON p.id=pi.purchase_id
                         LEFT JOIN catalog.supplier_products sp ON sp.id=u.supplier_product_id
                         WHERE mu.movement_id=m.id AND (u.id IS NULL OR l.id IS NULL OR (sent>0 AND (pi.id IS NULL OR p.id IS NULL))
                           OR u.product_id IS DISTINCT FROM c.product_id OR l.product_id IS DISTINCT FROM c.product_id
                           OR (sent>0 AND (pi.product_id IS DISTINCT FROM c.product_id OR p.supplier_id IS DISTINCT FROM c.supplier_id))
                           OR sp.supplier_id IS DISTINCT FROM c.supplier_id OR sp.product_id IS DISTINCT FROM c.product_id
                           OR (sent>0 AND u.origin_type=1 AND u.source_purchase_item_id IS DISTINCT FROM l.purchase_item_id)
                           OR mu.from_status NOT IN (4,5)
                           OR mu.from_status IS NULL OR mu.to_status IS DISTINCT FROM 6
                           OR warranty.physical_source_quantity(u.id) IS NULL OR warranty.physical_source_quantity(u.id)<=0))
                       OR (SELECT sum(warranty.physical_source_quantity(mu.inventory_unit_id))
                           FROM inventory.movement_units mu WHERE mu.movement_id=m.id) IS DISTINCT FROM
                          (SELECT coalesce(sum(e.quantity_delta),0) FROM inventory.movement_effects e WHERE e.movement_id=m.id AND e.stock_bucket=4)))
                      OR EXISTS (SELECT 1 FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                         WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type=12
                         GROUP BY mu.inventory_unit_id HAVING count(*)<>1)
                      OR c.base_quantity IS DISTINCT FROM (SELECT sum(warranty.physical_source_quantity(mu.inventory_unit_id))
                         FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                         WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type=12)
                    THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: exact send physical graph mismatch'; END IF;
                    -- All old resolution links must identify a sent original; one resolution per original.
                    IF EXISTS (SELECT 1 FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                      WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type IN (13,14,15,16,19)
                      AND mu.from_status=6 AND NOT EXISTS (SELECT 1 FROM inventory.movements sm
                        JOIN inventory.movement_units su ON su.movement_id=sm.id
                        WHERE sm.reference_type='SHOP_WARRANTY' AND sm.reference_id=c.id AND sm.movement_type=12
                          AND su.inventory_unit_id=mu.inventory_unit_id AND su.to_status=6))
                      OR EXISTS (SELECT 1 FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                        WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type IN (13,14,15,16,19)
                          AND mu.from_status=6 GROUP BY mu.inventory_unit_id HAVING count(*)<>1)
                      OR EXISTS (SELECT 1 FROM inventory.movements m WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id
                        AND m.movement_type IN (13,14,15,16,19) AND
                        ((SELECT sum(warranty.physical_source_quantity(mu.inventory_unit_id)) FROM inventory.movement_units mu
                            WHERE mu.movement_id=m.id AND mu.from_status=6) IS DISTINCT FROM
                         -(SELECT coalesce(sum(e.quantity_delta),0) FROM inventory.movement_effects e WHERE e.movement_id=m.id AND e.stock_bucket=4)
                         OR EXISTS (SELECT 1 FROM inventory.movement_units mu WHERE mu.movement_id=m.id AND
                           ((mu.from_status IS DISTINCT FROM 6 AND NOT (m.movement_type=14 AND mu.from_status IS NULL AND mu.to_status=1))
                            OR (mu.from_status=6 AND mu.to_status IS DISTINCT FROM
                              CASE m.movement_type WHEN 13 THEN 1 WHEN 14 THEN 7 WHEN 15 THEN 5 WHEN 16 THEN 8 WHEN 19 THEN 7 END)))))
                    THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: exact resolved identity mismatch or reuse'; END IF;
                    IF sent=0 THEN
                      -- Established legacy graph-only cases remain supported only with a complete unambiguous graph.
                      SELECT coalesce(sum(warranty.physical_source_quantity(mu.inventory_unit_id)),0) INTO resolved
                        FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                        WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id
                          AND m.movement_type IN (13,14,15,16,19) AND mu.from_status=6;
                      IF resolved>c.base_quantity OR (c.status IN (3,4,5) AND resolved<>c.base_quantity)
                        OR (c.status=2 AND resolved>=c.base_quantity) THEN
                        RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: legacy exact case remaining quantity mismatch';
                      END IF;
                      RETURN;
                    END IF;
                    -- Each typed send row owns exactly the identities of its movement and original lot.
                    IF EXISTS (SELECT 1 FROM warranty.shop_send_allocations a WHERE a.case_id=c.id AND a.base_quantity IS DISTINCT FROM
                      (SELECT sum(warranty.physical_source_quantity(u.id)) FROM inventory.movement_units mu
                         JOIN inventory.units u ON u.id=mu.inventory_unit_id
                         WHERE mu.movement_id=a.send_movement_id AND u.inventory_lot_id=a.original_inventory_lot_id))
                      OR EXISTS (SELECT 1 FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                        JOIN inventory.units u ON u.id=mu.inventory_unit_id
                        WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type=12 AND
                          (SELECT count(*) FROM warranty.shop_send_allocations a WHERE a.case_id=c.id
                            AND a.send_movement_id=m.id AND a.original_inventory_lot_id=u.inventory_lot_id)<>1)
                    THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: exact allocation physical source mismatch'; END IF;
                    -- A selected original maps to its unique immutable send, including same-lot legacy ambiguity rejection.
                    IF EXISTS (SELECT 1 FROM warranty.shop_resolution_allocations r JOIN warranty.shop_send_allocations a ON a.id=r.send_allocation_id
                      WHERE a.case_id=c.id AND r.resolved_base_quantity IS DISTINCT FROM
                       (SELECT sum(warranty.physical_source_quantity(u.id)) FROM inventory.movement_units mu
                        JOIN inventory.units u ON u.id=mu.inventory_unit_id
                        WHERE mu.movement_id=r.resolution_movement_id AND mu.from_status=6 AND u.inventory_lot_id=a.original_inventory_lot_id
                          AND EXISTS (SELECT 1 FROM inventory.movement_units su WHERE su.movement_id=a.send_movement_id
                            AND su.inventory_unit_id=u.id AND su.to_status=6)))
                      OR EXISTS (SELECT 1 FROM inventory.movements m JOIN inventory.movement_units mu ON mu.movement_id=m.id
                        JOIN inventory.units u ON u.id=mu.inventory_unit_id
                        WHERE m.reference_type='SHOP_WARRANTY' AND m.reference_id=c.id AND m.movement_type IN (13,14,15,16,19)
                          AND mu.from_status=6 AND (SELECT count(*) FROM warranty.shop_resolution_allocations r
                            JOIN warranty.shop_send_allocations a ON a.id=r.send_allocation_id
                            JOIN inventory.movement_units su ON su.movement_id=a.send_movement_id AND su.inventory_unit_id=u.id
                            WHERE a.case_id=c.id AND a.original_inventory_lot_id=u.inventory_lot_id
                              AND r.resolution_movement_id=m.id AND su.to_status=6)<>1)
                    THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: exact resolution allocation identity mismatch'; END IF;
                  END IF;
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
                    WHERE a.case_id=c.id AND (l.product_id<>c.product_id OR (NOT exact_graph AND c.source_purchase_item_id IS NULL)
                      OR (NOT exact_graph AND l.purchase_item_id IS DISTINCT FROM c.source_purchase_item_id) OR p.supplier_id IS DISTINCT FROM c.supplier_id
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
                      OR m.reference_id IS DISTINCT FROM c.id OR m.movement_type IS DISTINCT FROM CASE r.resolution_outcome WHEN 1 THEN 13 WHEN 2 THEN 14 WHEN 3 THEN 15 WHEN 4 THEN 19 WHEN 7 THEN 16 END)
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
                -- H02-only graph scope: an identity is protected once it belongs to a real shop send.
                CREATE FUNCTION warranty.h02_exact_cases_for_unit(p_unit uuid) RETURNS SETOF uuid LANGUAGE sql AS $h02$
                  SELECT DISTINCT m.reference_id FROM inventory.movements m
                  JOIN inventory.movement_units mu ON mu.movement_id=m.id
                  WHERE mu.inventory_unit_id=p_unit AND m.reference_type='SHOP_WARRANTY'
                    AND m.movement_type=12 AND mu.to_status=6
                $h02$;
                
                CREATE FUNCTION warranty.h02_exact_cases_for_movement(p_movement uuid) RETURNS SETOF uuid LANGUAGE sql AS $h02$
                  SELECT DISTINCT m.reference_id FROM inventory.movements m
                  WHERE m.id=p_movement AND m.reference_type='SHOP_WARRANTY'
                    AND EXISTS (SELECT 1 FROM inventory.movements sm JOIN inventory.movement_units su ON su.movement_id=sm.id
                      WHERE sm.reference_type='SHOP_WARRANTY' AND sm.reference_id=m.reference_id AND sm.movement_type=12)
                  UNION
                  SELECT DISTINCT c.case_id FROM inventory.lots l JOIN inventory.units u ON u.inventory_lot_id=l.id
                    CROSS JOIN LATERAL warranty.h02_exact_cases_for_unit(u.id) c(case_id)
                  WHERE l.source_movement_id=p_movement
                $h02$;
                
                CREATE FUNCTION warranty.h02_guard_exact_graph_mutation() RETURNS trigger LANGUAGE plpgsql AS $h02$
                DECLARE old_row jsonb:=to_jsonb(OLD); new_row jsonb; protected boolean:=false; fields text[];
                BEGIN
                  IF TG_OP='UPDATE' THEN new_row:=to_jsonb(NEW); END IF;
                  IF TG_TABLE_NAME='movements' THEN
                    SELECT EXISTS (SELECT 1 FROM warranty.h02_exact_cases_for_movement(OLD.id)) INTO protected;
                    -- All event identity/economic quantity facts participating in this graph are append-only.
                  ELSIF TG_TABLE_NAME IN ('movement_units','movement_effects') THEN
                    SELECT EXISTS (SELECT 1 FROM warranty.h02_exact_cases_for_movement(OLD.movement_id)) INTO protected;
                  ELSIF TG_TABLE_NAME='units' THEN
                    SELECT EXISTS (SELECT 1 FROM warranty.h02_exact_cases_for_unit(OLD.id)) INTO protected;
                    fields:=ARRAY['id','product_id','inventory_lot_id','supplier_product_id','origin_type',
                      'acquisition_cost','source_purchase_item_id','source_warranty_case_id','source_warranty_claim_item_id','source_stock_adjustment_item_id'];
                  ELSIF TG_TABLE_NAME='lots' THEN
                    SELECT EXISTS (SELECT 1 FROM inventory.units u CROSS JOIN LATERAL warranty.h02_exact_cases_for_unit(u.id) c
                      WHERE u.inventory_lot_id=OLD.id) INTO protected;
                    fields:=ARRAY['id','product_id','purchase_item_id','source_movement_id'];
                  ELSIF TG_TABLE_NAME='purchase_items' THEN
                    SELECT EXISTS (SELECT 1 FROM inventory.lots l JOIN inventory.units u ON u.inventory_lot_id=l.id
                      CROSS JOIN LATERAL warranty.h02_exact_cases_for_unit(u.id) c WHERE l.purchase_item_id=OLD.id) INTO protected;
                    fields:=ARRAY['id','purchase_id','product_id'];
                  ELSIF TG_TABLE_NAME='purchases' THEN
                    SELECT EXISTS (SELECT 1 FROM purchasing.purchase_items pi JOIN inventory.lots l ON l.purchase_item_id=pi.id
                      JOIN inventory.units u ON u.inventory_lot_id=l.id CROSS JOIN LATERAL warranty.h02_exact_cases_for_unit(u.id) c
                      WHERE pi.purchase_id=OLD.id) INTO protected;
                    fields:=ARRAY['id','supplier_id'];
                  ELSIF TG_TABLE_NAME='supplier_products' THEN
                    SELECT EXISTS (SELECT 1 FROM inventory.units u CROSS JOIN LATERAL warranty.h02_exact_cases_for_unit(u.id) c
                      WHERE u.supplier_product_id=OLD.id) INTO protected;
                    fields:=ARRAY['id','supplier_id','product_id'];
                  END IF;
                  IF protected AND (TG_OP='DELETE' OR fields IS NULL OR EXISTS
                    (SELECT 1 FROM unnest(fields) f WHERE old_row->f IS DISTINCT FROM new_row->f)) THEN
                    RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_APPEND_ONLY: exact shop warranty graph provenance cannot be changed';
                  END IF;
                  IF TG_OP='DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END;
                $h02$;
                
                -- INSERT never gets an immediate incomplete-transaction verdict. Complete graph is checked at COMMIT.
                CREATE FUNCTION warranty.h02_validate_exact_graph_event() RETURNS trigger LANGUAGE plpgsql AS $h02$
                DECLARE target_movement uuid; target_case uuid;
                BEGIN
                  IF TG_TABLE_NAME='movements' THEN target_movement:=NEW.id; ELSE target_movement:=NEW.movement_id; END IF;
                  FOR target_case IN SELECT affected.case_id FROM warranty.h02_exact_cases_for_movement(target_movement) AS affected(case_id) ORDER BY affected.case_id LOOP
                    PERFORM warranty.assert_shop_case_source(target_case);
                  END LOOP;
                  RETURN NULL;
                END;
                $h02$;
                
                CREATE TRIGGER tr_h02_movements_exact_graph BEFORE UPDATE OR DELETE ON inventory.movements
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_movement_units_exact_graph BEFORE UPDATE OR DELETE ON inventory.movement_units
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_movement_effects_exact_graph BEFORE UPDATE OR DELETE ON inventory.movement_effects
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_units_exact_source BEFORE UPDATE OR DELETE ON inventory.units
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_lots_exact_source BEFORE UPDATE OR DELETE ON inventory.lots
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_purchase_items_exact_source BEFORE UPDATE OR DELETE ON purchasing.purchase_items
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_purchases_exact_source BEFORE UPDATE OR DELETE ON purchasing.purchases
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE TRIGGER tr_h02_supplier_products_exact_source BEFORE UPDATE OR DELETE ON catalog.supplier_products
                  FOR EACH ROW EXECUTE FUNCTION warranty.h02_guard_exact_graph_mutation();
                CREATE CONSTRAINT TRIGGER ct_h02_movements_exact_graph AFTER INSERT ON inventory.movements
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.h02_validate_exact_graph_event();
                CREATE CONSTRAINT TRIGGER ct_h02_movement_units_exact_graph AFTER INSERT ON inventory.movement_units
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.h02_validate_exact_graph_event();
                CREATE CONSTRAINT TRIGGER ct_h02_movement_effects_exact_graph AFTER INSERT ON inventory.movement_effects
                  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION warranty.h02_validate_exact_graph_event();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException("Exact warranty source authority is forward-only; restore a verified backup to revert the database.");
        }
    }
}