using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace EdgeRetails.Infrastructure.Persistence.Migrations
{
    public partial class Phase11LooseReturnSourceGraph : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION warranty.assert_container_loose_return_source_graph(p_sale_item_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
                DECLARE s sales.sale_items%ROWTYPE;
                BEGIN
                  SELECT * INTO s FROM sales.sale_items WHERE id=p_sale_item_id;
                  IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: original sale item was not found'; END IF;
                  IF EXISTS (
                    SELECT 1 FROM sales.return_items ri
                    WHERE ri.sale_item_id=s.id AND EXISTS (SELECT 1 FROM sales.return_item_units ru JOIN inventory.units u ON u.id=ru.inventory_unit_id WHERE ru.sale_return_item_id=ri.id AND u.is_loose_stock IS TRUE)
                    AND (ri.product_id<>s.product_id
                      OR ri.base_quantity IS DISTINCT FROM (SELECT coalesce(sum(a.base_quantity),0) FROM warranty.sale_return_source_allocations a WHERE a.sale_return_item_id=ri.id)
                      OR (SELECT count(*) FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id)<>(SELECT count(*) FROM warranty.sale_return_source_allocations a WHERE a.sale_return_item_id=ri.id)
                      OR EXISTS (SELECT 1 FROM (
                        SELECT count(DISTINCT a.id) AS matched_allocation_count,
                               count(DISTINCT ru.inventory_unit_id) AS matched_unit_count
                        FROM sales.return_item_units ru
                        JOIN inventory.units u ON u.id=ru.inventory_unit_id
                        JOIN warranty.sale_return_source_allocations a
                          ON a.sale_return_item_id=ri.id
                         AND a.restored_inventory_lot_id=u.inventory_lot_id
                         AND a.base_quantity=u.initial_base_quantity
                        JOIN inventory.movement_units mu
                          ON mu.movement_id=a.return_movement_id AND mu.inventory_unit_id=u.id
                        JOIN inventory.lot_consumptions consumed ON consumed.id=a.sale_consumption_id
                        JOIN inventory.lots original ON original.id=consumed.lot_id
                        JOIN inventory.lots restored ON restored.id=a.restored_inventory_lot_id
                        WHERE ru.sale_return_item_id=ri.id
                          AND consumed.movement_id=s.inventory_movement_id
                          AND original.product_id=s.product_id
                          AND u.is_loose_stock IS TRUE
                          AND u.product_id=s.product_id
                          AND u.inventory_lot_id IS NOT NULL
                          AND u.source_purchase_item_id IS NOT NULL
                          AND u.initial_base_quantity IS NOT NULL
                          AND restored.product_id=u.product_id
                          AND restored.purchase_item_id IS NOT DISTINCT FROM original.purchase_item_id
                          AND u.source_purchase_item_id IS NOT DISTINCT FROM original.purchase_item_id
                      ) matched
                      WHERE matched.matched_allocation_count <>
                        (SELECT count(*) FROM warranty.sale_return_source_allocations a WHERE a.sale_return_item_id=ri.id)
                        OR matched.matched_unit_count <>
                        (SELECT count(*) FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id))
                  )) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: loose return unit/source graph mismatch'; END IF;
                  IF EXISTS (SELECT 1 FROM warranty.sale_return_source_allocations a JOIN sales.return_items ri ON ri.id=a.sale_return_item_id JOIN inventory.lot_consumptions lc ON lc.id=a.sale_consumption_id JOIN inventory.lots l ON l.id=lc.lot_id JOIN sales.returns sr ON sr.id=ri.sale_return_id JOIN inventory.movements m ON m.id=a.return_movement_id LEFT JOIN inventory.lots restored ON restored.id=a.restored_inventory_lot_id
                    WHERE ri.sale_item_id=s.id AND (lc.movement_id<>s.inventory_movement_id OR l.product_id<>s.product_id OR ri.product_id<>s.product_id OR m.product_id<>s.product_id OR m.movement_type<>4 OR sr.sale_id<>s.sale_id OR a.client_operation_id<>sr.client_operation_id OR a.actor_id<>sr.created_by OR m.actor_id<>a.actor_id OR m.correlation_id<>a.client_operation_id OR m.reference_type<>'SALE_RETURN' OR m.reference_id IS DISTINCT FROM ri.sale_return_id OR restored.id IS NULL OR restored.product_id<>s.product_id OR restored.source_movement_id<>m.id OR restored.purchase_item_id IS DISTINCT FROM l.purchase_item_id OR restored.received_quantity<>a.base_quantity))
                  THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: return sold-source/item/movement/restoration mismatch'; END IF;
                  IF EXISTS (SELECT 1 FROM inventory.lot_consumptions lc WHERE lc.movement_id=s.inventory_movement_id AND lc.quantity<coalesce((SELECT sum(a.base_quantity) FROM warranty.claim_source_allocations a JOIN warranty.claim_items ci ON ci.id=a.claim_item_id JOIN warranty.claims c ON c.id=ci.claim_id WHERE a.sale_consumption_id=lc.id AND (c.status NOT IN (6,7) OR (c.status=6 AND ci.resolution_type IN (2,5)))),0)+coalesce((SELECT sum(a.base_quantity) FROM warranty.sale_return_source_allocations a JOIN sales.return_items ri ON ri.id=a.sale_return_item_id WHERE ri.sale_item_id=s.id AND a.sale_consumption_id=lc.id),0))
                  THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_CAPACITY: original sold consumption reused'; END IF;
                END;
                $pass5$;
                CREATE OR REPLACE FUNCTION warranty.assert_sold_item_source(p_sale_item_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
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
                        (ri.product_id<>s.product_id OR NOT (
                          (ri.base_quantity IS NOT DISTINCT FROM (SELECT sum(warranty.physical_source_quantity(ru.inventory_unit_id)) FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id)
                           AND NOT EXISTS (SELECT 1 FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id AND NOT EXISTS (SELECT 1 FROM sales.sale_item_units su WHERE su.sale_item_id=s.id AND su.inventory_unit_id=ru.inventory_unit_id)))
                          OR (ri.base_quantity IS NOT DISTINCT FROM (SELECT sum(u.initial_base_quantity) FROM sales.return_item_units ru JOIN inventory.units u ON u.id=ru.inventory_unit_id WHERE ru.sale_return_item_id=ri.id AND u.is_loose_stock IS TRUE)
                           AND EXISTS (SELECT 1 FROM sales.return_item_units ru JOIN inventory.units u ON u.id=ru.inventory_unit_id WHERE ru.sale_return_item_id=ri.id AND u.is_loose_stock IS TRUE)
                           AND (SELECT count(*) FROM sales.return_item_units ru WHERE ru.sale_return_item_id=ri.id)=(SELECT count(*) FROM warranty.sale_return_source_allocations a WHERE a.sale_return_item_id=ri.id)
                           AND NOT EXISTS (SELECT 1 FROM sales.return_item_units ru JOIN inventory.units u ON u.id=ru.inventory_unit_id WHERE ru.sale_return_item_id=ri.id AND (u.is_loose_stock IS DISTINCT FROM TRUE OR u.inventory_lot_id IS NULL OR NOT EXISTS (SELECT 1 FROM warranty.sale_return_source_allocations a JOIN inventory.movement_units mu ON mu.movement_id=a.return_movement_id AND mu.inventory_unit_id=u.id WHERE a.sale_return_item_id=ri.id AND a.restored_inventory_lot_id=u.inventory_lot_id AND a.base_quantity=u.initial_base_quantity)))
                        ))
                    )) THEN RAISE EXCEPTION USING ERRCODE='23514', MESSAGE='PASS5_PROVENANCE_REQUIRED: exact claim/return owner physical graph mismatch'; END IF;
                    IF EXISTS (SELECT 1 FROM sales.return_items ri JOIN sales.return_item_units ru ON ru.sale_return_item_id=ri.id JOIN inventory.units u ON u.id=ru.inventory_unit_id WHERE ri.sale_item_id=s.id AND u.is_loose_stock IS TRUE) THEN
                      PERFORM warranty.assert_container_loose_return_source_graph(s.id);
                    END IF;
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
                    SELECT 1 FROM (
                      SELECT a.return_movement_id, sum(a.base_quantity) AS allocated_quantity
                      FROM warranty.sale_return_source_allocations a
                      JOIN sales.return_items ri ON ri.id=a.sale_return_item_id
                      WHERE ri.sale_item_id=s.id
                      GROUP BY a.return_movement_id
                    ) allocated
                    WHERE allocated.allocated_quantity IS DISTINCT FROM (
                      SELECT coalesce(sum(e.quantity_delta),0)
                      FROM inventory.movement_effects e
                      WHERE e.movement_id=allocated.return_movement_id AND e.quantity_delta>0)
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
                """);
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION warranty.assert_sold_item_source(p_sale_item_id uuid) RETURNS void LANGUAGE plpgsql AS $pass5$
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
                """);
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS warranty.assert_container_loose_return_source_graph(uuid);");
        }
    }
}
