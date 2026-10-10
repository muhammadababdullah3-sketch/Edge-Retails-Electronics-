# Constraint and Relationship Matrix

This matrix is generated from the clean PostgreSQL 18.6 catalog, not inferred from C# navigation properties. Delete/update action codes use PostgreSQL: `a` NO ACTION, `r` RESTRICT, `c` CASCADE, `n` SET NULL, `d` SET DEFAULT. All 124 FK rows are listed.

## Foreign keys

| Constraint | Child columns | Parent | Delete | Update | Validated |
|---|---|---|---|---|---|
| `catalog.product_unit_barcodes.fk_product_unit_barcodes_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `catalog.product_units.fk_product_units_products_product_id` | product_id | `catalog.products` | r | a | True |
| `catalog.product_units.fk_product_units_units_unit_id` | unit_id | `catalog.units` | r | a | True |
| `catalog.products.fk_products_categories_category_id` | category_id | `catalog.categories` | r | a | True |
| `catalog.products.fk_products_companies_company_id` | company_id | `catalog.companies` | r | a | True |
| `catalog.products.fk_products_units_base_unit_id` | base_unit_id | `catalog.units` | r | a | True |
| `catalog.supplier_products.fk_supplier_products_products_product_id` | product_id | `catalog.products` | r | a | True |
| `catalog.supplier_products.fk_supplier_products_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `finance.cash_movements.fk_cash_movements_cash_sessions_cash_session_id` | cash_session_id | `finance.cash_sessions` | r | a | True |
| `finance.expense_subcategories.fk_expense_subcategories_expense_categories_category_id` | category_id | `finance.expense_categories` | r | a | True |
| `finance.expenses.fk_expenses_cash_sessions_cash_session_id` | cash_session_id | `finance.cash_sessions` | r | a | True |
| `finance.expenses.fk_expenses_expense_categories_category_id` | category_id | `finance.expense_categories` | r | a | True |
| `finance.expenses.fk_expenses_expense_subcategories_subcategory_id` | subcategory_id | `finance.expense_subcategories` | r | a | True |
| `finance.supplier_account_entries.fk_supplier_account_entries_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `finance.supplier_payment_reversals.fk_supplier_payment_reversals_supplier_payments_supplier_payme~` | supplier_payment_id | `finance.supplier_payments` | r | a | True |
| `finance.supplier_payments.fk_supplier_payments_cash_sessions_cash_session_id` | cash_session_id | `finance.cash_sessions` | r | a | True |
| `finance.supplier_payments.fk_supplier_payments_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `finance.supplier_refund_reversals.fk_supplier_refund_reversals_supplier_refunds_supplier_refund_~` | supplier_refund_id | `finance.supplier_refunds` | r | a | True |
| `finance.supplier_refunds.fk_supplier_refunds_cash_sessions_cash_session_id` | cash_session_id | `finance.cash_sessions` | r | a | True |
| `finance.supplier_refunds.fk_supplier_refunds_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `identity.role_permissions.fk_role_permissions_permissions_permission_id` | permission_id | `identity.permissions` | c | a | True |
| `identity.role_permissions.fk_role_permissions_roles_role_id` | role_id | `identity.roles` | c | a | True |
| `identity.user_permission_overrides.fk_user_permission_overrides_permissions_permission_id` | permission_id | `identity.permissions` | c | a | True |
| `identity.user_permission_overrides.fk_user_permission_overrides_users_user_id` | user_id | `identity.users` | c | a | True |
| `identity.user_sessions.fk_user_sessions_users_user_id` | user_id | `identity.users` | r | a | True |
| `identity.users.fk_users_roles_role_id` | role_id | `identity.roles` | r | a | True |
| `inventory.cost_states.fk_cost_states_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.lot_bucket_balances.fk_lot_bucket_balances_lots_lot_id` | lot_id | `inventory.lots` | r | a | True |
| `inventory.lot_consumptions.fk_lot_consumptions_lots_lot_id` | lot_id | `inventory.lots` | r | a | True |
| `inventory.lot_consumptions.fk_lot_consumptions_movements_movement_id` | movement_id | `inventory.movements` | r | a | True |
| `inventory.lots.fk_lots_movements_source_movement_id` | source_movement_id | `inventory.movements` | r | a | True |
| `inventory.lots.fk_lots_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.lots.fk_lots_purchase_items_purchase_item_id` | purchase_item_id | `purchasing.purchase_items` | r | a | True |
| `inventory.movement_effects.fk_movement_effects_movements_movement_id` | movement_id | `inventory.movements` | r | a | True |
| `inventory.movement_units.fk_movement_units_movements_movement_id` | movement_id | `inventory.movements` | r | a | True |
| `inventory.movement_units.fk_movement_units_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `inventory.movements.fk_movements_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.stock_adjustment_items.fk_stock_adjustment_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.stock_adjustment_items.fk_stock_adjustment_items_stock_adjustments_stock_adjustment_id` | stock_adjustment_id | `inventory.stock_adjustments` | r | a | True |
| `inventory.stock_adjustment_items.fk_stock_adjustment_items_supplier_products_supplier_product_id` | supplier_product_id | `catalog.supplier_products` | r | a | True |
| `inventory.stock_adjustment_items.fk_stock_adjustment_items_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `inventory.stock_balances.fk_stock_balances_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.stocktake_items.fk_stocktake_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.stocktake_items.fk_stocktake_items_stocktakes_stocktake_id` | stocktake_id | `inventory.stocktakes` | r | a | True |
| `inventory.stocktake_unit_checks.fk_stocktake_unit_checks_stocktake_items_stocktake_item_id` | stocktake_item_id | `inventory.stocktake_items` | r | a | True |
| `inventory.stocktake_unit_checks.fk_stocktake_unit_checks_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `inventory.stocktakes.fk_stocktakes_categories_category_id` | category_id | `catalog.categories` | r | a | True |
| `inventory.unit_identity_claims.fk_unit_identity_claims_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `inventory.units.fk_units_claim_items_source_warranty_claim_item_id` | source_warranty_claim_item_id | `warranty.claim_items` | r | a | True |
| `inventory.units.fk_units_lots_inventory_lot_id` | inventory_lot_id | `inventory.lots` | r | a | True |
| `inventory.units.fk_units_products_product_id` | product_id | `catalog.products` | r | a | True |
| `inventory.units.fk_units_purchase_items_source_purchase_item_id` | source_purchase_item_id | `purchasing.purchase_items` | r | a | True |
| `inventory.units.fk_units_shop_stock_cases_source_warranty_case_id` | source_warranty_case_id | `warranty.shop_stock_cases` | r | a | True |
| `inventory.units.fk_units_stock_adjustment_items_source_stock_adjustment_item_id` | source_stock_adjustment_item_id | `inventory.stock_adjustment_items` | r | a | True |
| `inventory.units.fk_units_supplier_products_supplier_product_id` | supplier_product_id | `catalog.supplier_products` | r | a | True |
| `purchasing.purchase_item_units.fk_purchase_item_units_purchase_items_purchase_item_id` | purchase_item_id | `purchasing.purchase_items` | r | a | True |
| `purchasing.purchase_item_units.fk_purchase_item_units_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `purchasing.purchase_items.fk_purchase_items_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `purchasing.purchase_items.fk_purchase_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `purchasing.purchase_items.fk_purchase_items_purchases_purchase_id` | purchase_id | `purchasing.purchases` | r | a | True |
| `purchasing.purchase_voids.fk_purchase_voids_purchases_purchase_id` | purchase_id | `purchasing.purchases` | r | a | True |
| `purchasing.purchases.fk_purchases_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `purchasing.return_item_units.fk_return_item_units_return_items_purchase_return_item_id` | purchase_return_item_id | `purchasing.return_items` | r | a | True |
| `purchasing.return_item_units.fk_return_item_units_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `purchasing.return_items.fk_return_items_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `purchasing.return_items.fk_return_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `purchasing.return_items.fk_return_items_purchase_items_purchase_item_id` | purchase_item_id | `purchasing.purchase_items` | r | a | True |
| `purchasing.return_items.fk_return_items_returns_purchase_return_id` | purchase_return_id | `purchasing.returns` | r | a | True |
| `purchasing.returns.fk_returns_purchases_purchase_id` | purchase_id | `purchasing.purchases` | r | a | True |
| `sales.pos_draft_items.fk_pos_draft_items_pos_drafts_draft_id` | draft_id | `sales.pos_drafts` | c | a | True |
| `sales.pos_draft_items.fk_pos_draft_items_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `sales.pos_draft_items.fk_pos_draft_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `sales.pos_draft_items.fk_pos_draft_items_units_selected_inventory_unit_id` | selected_inventory_unit_id | `inventory.units` | r | a | True |
| `sales.pos_drafts.fk_pos_drafts_customers_customer_id` | customer_id | `parties.customers` | r | a | True |
| `sales.quotation_items.fk_quotation_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `sales.quotation_items.fk_quotation_items_quotations_quotation_id` | quotation_id | `sales.quotations` | r | a | True |
| `sales.quotation_items.fk_quotation_items_units_selected_unit_id` | selected_unit_id | `catalog.units` | r | a | True |
| `sales.quotation_operations.fk_quotation_operations_quotations_quotation_id` | quotation_id | `sales.quotations` | r | a | True |
| `sales.quotations.fk_quotations_customers_customer_id` | customer_id | `parties.customers` | r | a | True |
| `sales.return_item_units.fk_return_item_units_return_items_sale_return_item_id` | sale_return_item_id | `sales.return_items` | r | a | True |
| `sales.return_item_units.fk_return_item_units_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `sales.return_items.fk_return_items_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `sales.return_items.fk_return_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `sales.return_items.fk_return_items_returns_sale_return_id` | sale_return_id | `sales.returns` | r | a | True |
| `sales.return_items.fk_return_items_sale_items_sale_item_id` | sale_item_id | `sales.sale_items` | r | a | True |
| `sales.returns.fk_returns_sales_sale_id` | sale_id | `sales.sales` | r | a | True |
| `sales.sale_item_units.fk_sale_item_units_sale_items_sale_item_id` | sale_item_id | `sales.sale_items` | r | a | True |
| `sales.sale_item_units.fk_sale_item_units_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `sales.sale_items.fk_sale_items_movements_inventory_movement_id` | inventory_movement_id | `inventory.movements` | r | a | True |
| `sales.sale_items.fk_sale_items_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `sales.sale_items.fk_sale_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `sales.sale_items.fk_sale_items_sales_sale_id` | sale_id | `sales.sales` | r | a | True |
| `sales.sale_payments.fk_sale_payments_sales_sale_id` | sale_id | `sales.sales` | r | a | True |
| `sales.sales.fk_sales_customers_customer_id` | customer_id | `parties.customers` | r | a | True |
| `thaka.material_issue_items.fk_material_issue_items_material_issues_material_issue_id` | material_issue_id | `thaka.material_issues` | r | a | True |
| `thaka.material_issue_items.fk_material_issue_items_movements_inventory_movement_id` | inventory_movement_id | `inventory.movements` | r | a | True |
| `thaka.material_issue_items.fk_material_issue_items_product_units_product_unit_id` | product_unit_id | `catalog.product_units` | r | a | True |
| `thaka.material_issue_items.fk_material_issue_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `thaka.material_issue_units.fk_material_issue_units_material_issue_items_material_issue_it~` | material_issue_item_id | `thaka.material_issue_items` | r | a | True |
| `thaka.material_issue_units.fk_material_issue_units_units_inventory_unit_id` | inventory_unit_id | `inventory.units` | r | a | True |
| `thaka.material_issues.fk_material_issues_projects_project_id` | project_id | `thaka.projects` | r | a | True |
| `thaka.material_reversals.fk_material_reversals_material_issues_material_issue_id` | material_issue_id | `thaka.material_issues` | r | a | True |
| `thaka.material_reversals.fk_material_reversals_projects_project_id` | project_id | `thaka.projects` | r | a | True |
| `thaka.payment_reversals.fk_payment_reversals_payments_payment_id` | payment_id | `thaka.payments` | r | a | True |
| `thaka.payment_reversals.fk_payment_reversals_projects_project_id` | project_id | `thaka.projects` | r | a | True |
| `thaka.payments.fk_payments_cash_sessions_cash_session_id` | cash_session_id | `finance.cash_sessions` | r | a | True |
| `thaka.payments.fk_payments_projects_project_id` | project_id | `thaka.projects` | r | a | True |
| `thaka.projects.fk_projects_customers_customer_id` | customer_id | `parties.customers` | r | a | True |
| `thaka.reopenings.fk_reopenings_projects_project_id` | project_id | `thaka.projects` | r | a | True |
| `thaka.reopenings.fk_reopenings_settlements_settlement_id` | settlement_id | `thaka.settlements` | r | a | True |
| `thaka.settlements.fk_settlements_payments_final_payment_id` | final_payment_id | `thaka.payments` | r | a | True |
| `thaka.settlements.fk_settlements_projects_project_id` | project_id | `thaka.projects` | r | a | True |
| `warranty.claim_events.fk_claim_events_claims_claim_id` | claim_id | `warranty.claims` | r | a | True |
| `warranty.claim_item_units.fk_claim_item_units_claim_items_claim_item_id` | claim_item_id | `warranty.claim_items` | r | a | True |
| `warranty.claim_item_units.fk_claim_item_units_units_active_original_inventory_unit_id` | active_original_inventory_unit_id | `inventory.units` | r | a | True |
| `warranty.claim_item_units.fk_claim_item_units_units_original_inventory_unit_id` | original_inventory_unit_id | `inventory.units` | r | a | True |
| `warranty.claim_item_units.fk_claim_item_units_units_replacement_inventory_unit_id` | replacement_inventory_unit_id | `inventory.units` | r | a | True |
| `warranty.claim_items.fk_claim_items_claims_claim_id` | claim_id | `warranty.claims` | r | a | True |
| `warranty.claim_items.fk_claim_items_products_product_id` | product_id | `catalog.products` | r | a | True |
| `warranty.claim_items.fk_claim_items_products_replacement_product_id` | replacement_product_id | `catalog.products` | r | a | True |
| `warranty.claims.fk_claims_customers_customer_id` | customer_id | `parties.customers` | r | a | True |
| `warranty.claims.fk_claims_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |
| `warranty.shop_stock_cases.fk_shop_stock_cases_products_product_id` | product_id | `catalog.products` | r | a | True |
| `warranty.shop_stock_cases.fk_shop_stock_cases_suppliers_supplier_id` | supplier_id | `parties.suppliers` | r | a | True |

There are 119 restrictive FKs and five cascade FKs, confined to role/permission join/override rows and POS draft items. No financial or posted inventory FK uses cascade.

## Index support for child-side FK lookups

These FKs have no unconditional ordinary index whose leading column matches the FK; filtered indexes do not cover null rows for general FK checks. This matters most for cleanup/update checks and parent delete/update operations.

- `inventory.stocktake_unit_checks` (stocktake_item_id) -> `inventory.stocktake_items`
- `sales.pos_draft_items` (selected_inventory_unit_id) -> `inventory.units`
- `warranty.claim_item_units` (active_original_inventory_unit_id) -> `inventory.units`
- `warranty.claim_item_units` (original_inventory_unit_id) -> `inventory.units`
- `warranty.claim_item_units` (replacement_inventory_unit_id) -> `inventory.units`

## Check constraints

All 59 physical CHECK constraints follow.

- `catalog.categories.ck_categories_symbol_uppercase`: `CHECK (((identity_symbol)::text = upper((identity_symbol)::text)))`
- `catalog.companies.ck_companies_code_uppercase`: `CHECK (((code)::text = upper((code)::text)))`
- `catalog.product_units.ck_product_units_factor_positive`: `CHECK ((factor_to_base_unit > (0)::numeric))`
- `catalog.products.ck_products_attributes_schema_version_positive`: `CHECK ((attributes_schema_version >= 1))`
- `catalog.products.ck_products_model_code_uppercase`: `CHECK (((model_code IS NULL) OR ((model_code)::text = upper((model_code)::text))))`
- `catalog.products.ck_products_nonnegative_prices`: `CHECK (((default_sale_price >= (0)::numeric) AND (minimum_stock_level >= (0)::numeric) AND ((reference_purchase_cost IS NULL) OR (reference_purchase_cost >= (0)::numeric))))`
- `catalog.products.ck_products_sku_uppercase`: `CHECK (((sku IS NULL) OR ((sku)::text = upper((sku)::text))))`
- `catalog.products.ck_products_warranty_months_nonnegative`: `CHECK ((default_warranty_months >= 0))`
- `catalog.supplier_products.ck_supplier_products_next_sequence_positive`: `CHECK ((next_item_sequence >= 1))`
- `catalog.units.ck_units_display_decimal_places`: `CHECK (((display_decimal_places >= 0) AND (display_decimal_places <= 6)))`
- `finance.cash_movements.ck_cash_movement_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `finance.cash_sessions.ck_cash_session_amounts`: `CHECK (((opening_cash >= (0)::numeric) AND ((counted_closing_cash IS NULL) OR (counted_closing_cash >= (0)::numeric))))`
- `finance.expenses.ck_expense_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `finance.supplier_account_entries.ck_supplier_account_entry_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `finance.supplier_payments.ck_supplier_payment_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `finance.supplier_refunds.ck_supplier_refund_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `identity.users.ck_user_pin_iterations_positive`: `CHECK ((pin_iterations > 0))`
- `inventory.cost_states.ck_cost_states_nonnegative`: `CHECK (((costed_qty >= (0)::numeric) AND (total_inventory_cost >= (0)::numeric) AND (moving_average_cost >= (0)::numeric) AND ((last_purchase_cost IS NULL) OR (last_purchase_cost >= (0)::numeric))))`
- `inventory.lot_bucket_balances.ck_inventory_lot_bucket_quantity_nonnegative`: `CHECK ((quantity >= (0)::numeric))`
- `inventory.lot_consumptions.ck_inventory_lot_consumptions_values`: `CHECK (((quantity > (0)::numeric) AND (unit_cost_snapshot >= (0)::numeric) AND (total_cost_snapshot >= (0)::numeric)))`
- `inventory.lots.ck_inventory_lots_values`: `CHECK (((received_quantity > (0)::numeric) AND (original_unit_cost >= (0)::numeric) AND (effective_unit_cost >= (0)::numeric)))`
- `inventory.movement_effects.ck_inventory_movement_effect_snapshots_nonnegative`: `CHECK (((quantity_before >= (0)::numeric) AND (quantity_after >= (0)::numeric)))`
- `inventory.movements.ck_inventory_movement_loss_nonnegative`: `CHECK ((recognized_loss_amount >= (0)::numeric))`
- `inventory.stock_adjustment_items.ck_stock_adjustment_items_base_qty_positive`: `CHECK ((base_quantity > (0)::numeric))`
- `inventory.stock_balances.ck_stock_balances_nonnegative`: `CHECK (((sellable_qty >= (0)::numeric) AND (damaged_qty >= (0)::numeric) AND (defective_qty >= (0)::numeric) AND (with_supplier_qty >= (0)::numeric) AND (scrap_qty >= (0)::numeric)))`
- `inventory.stocktake_items.ck_stocktake_items_nonnegative`: `CHECK (((expected_sellable_qty >= (0)::numeric) AND ((counted_sellable_qty IS NULL) OR (counted_sellable_qty >= (0)::numeric))))`
- `inventory.unit_identity_claims.ck_inventory_unit_identity_claim_normalization_version`: `CHECK ((normalization_version >= 1))`
- `inventory.unit_identity_claims.ck_inventory_unit_identity_claim_type_slot`: `CHECK ((((identifier_type = 1) AND (identifier_slot = 1)) OR ((identifier_type = 2) AND (identifier_slot = ANY (ARRAY[2, 3])))))`
- `inventory.unit_identity_claims.ck_inventory_unit_identity_claim_value_nonempty`: `CHECK ((length(btrim((normalized_value)::text)) > 0))`
- `inventory.units.ck_inventory_unit_cost_nonnegative`: `CHECK ((acquisition_cost >= (0)::numeric))`
- `inventory.units.ck_inventory_unit_sequence_positive`: `CHECK (((item_sequence IS NULL) OR (item_sequence >= 1)))`
- `inventory.units.ck_inventory_units_origin_provenance`: `CHECK ((((origin_type = 1) AND (source_purchase_item_id IS NOT NULL) AND (source_warranty_claim_item_id IS NULL) AND (source_warranty_case_id IS NULL) AND (source_stock_adjustment_item_id IS NULL)) OR ((origin_type = 2) AND (source_purchase_item_id IS NULL) AND (source_stock_adjustment_item_id IS NULL) AND (((source_warranty_claim_item_id IS NOT NULL) AND (source_warranty_case_id IS NULL)) OR ((source_warranty_claim_item_id IS NULL) AND (source_warranty_case_id IS NOT NULL)))) OR ((origin_type = 3) AND (source_stock_adjustment_item_id IS NOT NULL) AND (source_purchase_item_id IS NULL) AND (source_warranty_claim_item_id IS NULL) AND (source_warranty_case_id IS NULL))))`
- `purchasing.purchase_items.ck_purchase_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (entered_unit_cost >= (0)::numeric) AND (base_line_total >= (0)::numeric) AND (allocated_other_cost >= (0)::numeric) AND (effective_base_unit_cost >= (0)::numeric) AND (effective_line_cost >= (0)::numeric) AND (sale_price_at_purchase >= (0)::numeric)))`
- `purchasing.purchase_voids.ck_purchase_void_cash_reversal_nonnegative`: `CHECK (((cash_drawer_reversal_amount IS NULL) OR (cash_drawer_reversal_amount >= (0)::numeric)))`
- `purchasing.purchases.ck_purchase_totals`: `CHECK (((subtotal >= (0)::numeric) AND (other_charges >= (0)::numeric) AND (grand_total >= (0)::numeric)))`
- `purchasing.return_items.ck_purchase_return_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (supplier_unit_return_value >= (0)::numeric) AND (supplier_return_value >= (0)::numeric) AND (inventory_unit_cost_removed >= (0)::numeric) AND (inventory_cost_removed >= (0)::numeric)))`
- `purchasing.returns.ck_purchase_return_values`: `CHECK (((supplier_return_value >= (0)::numeric) AND (inventory_cost_removed >= (0)::numeric)))`
- `sales.pos_draft_items.ck_pos_draft_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (displayed_unit_price_snapshot >= (0)::numeric)))`
- `sales.quotation_items.ck_quotation_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (quoted_unit_price >= (0)::numeric) AND (line_total >= (0)::numeric)))`
- `sales.quotations.ck_quotation_totals`: `CHECK (((subtotal >= (0)::numeric) AND (discount >= (0)::numeric) AND (discount <= subtotal) AND (grand_total >= (0)::numeric)))`
- `sales.return_items.ck_sale_return_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (refund_amount >= (0)::numeric) AND (original_cost_amount >= (0)::numeric) AND (cost_reversal_amount >= (0)::numeric)))`
- `sales.returns.ck_sale_return_refund_nonnegative`: `CHECK ((refund_amount >= (0)::numeric))`
- `sales.sale_items.ck_sale_item_price_override_audit`: `CHECK ((((price_override_reason IS NULL) AND (price_override_by IS NULL)) OR ((price_override_reason IS NOT NULL) AND (btrim((price_override_reason)::text) <> ''::text) AND (price_override_by IS NOT NULL))))`
- `sales.sale_items.ck_sale_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (unit_price >= (0)::numeric) AND (gross_line_total >= (0)::numeric) AND (allocated_invoice_discount >= (0)::numeric) AND (net_line_total >= (0)::numeric) AND (unit_cost_snapshot >= (0)::numeric) AND (total_cost_snapshot >= (0)::numeric)))`
- `sales.sale_payments.ck_sale_payment_values`: `CHECK (((amount_tendered >= (0)::numeric) AND (applied_amount >= (0)::numeric) AND (change_given >= (0)::numeric)))`
- `sales.sales.ck_sales_totals`: `CHECK (((subtotal >= (0)::numeric) AND (invoice_discount >= (0)::numeric) AND (grand_total >= (0)::numeric) AND (invoice_discount <= subtotal)))`
- `system.document_sequences.ck_document_sequence_nonnegative`: `CHECK ((last_value >= 0))`
- `system.installation_state.ck_installation_state_singleton`: `CHECK (((singleton_key)::text = 'PRIMARY'::text))`
- `system.receipt_template_settings.ck_receipt_template_primary_key`: `CHECK ((((template_key)::text = 'PRIMARY'::text) AND (template_version > 0)))`
- `system.shop_profile.ck_shop_profile_primary_key`: `CHECK (((profile_key)::text = 'PRIMARY'::text))`
- `system.supplier_code_sequences.ck_supplier_code_sequences_next_positive`: `CHECK ((next_value >= 1))`
- `thaka.material_issue_items.ck_thaka_issue_item_values`: `CHECK (((entered_quantity > (0)::numeric) AND (factor_to_base_snapshot > (0)::numeric) AND (base_quantity > (0)::numeric) AND (unit_charge >= (0)::numeric) AND (line_charge >= (0)::numeric) AND (unit_cost_snapshot >= (0)::numeric) AND (total_cost_snapshot >= (0)::numeric)))`
- `thaka.material_issues.ck_thaka_issue_values`: `CHECK (((total_charge >= (0)::numeric) AND (total_cost >= (0)::numeric) AND (gross_profit = (total_charge - total_cost))))`
- `thaka.material_reversals.ck_thaka_material_reversal_values`: `CHECK (((reversed_charge >= (0)::numeric) AND (restored_cost >= (0)::numeric)))`
- `thaka.payment_reversals.ck_thaka_payment_reversal_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `thaka.payments.ck_thaka_payment_amount_positive`: `CHECK ((amount > (0)::numeric))`
- `thaka.settlements.ck_thaka_settlement_values`: `CHECK (((gross_material_charges_snapshot >= (0)::numeric) AND (payments_collected_snapshot >= (0)::numeric) AND (settlement_discount >= (0)::numeric) AND (final_payment_amount >= (0)::numeric) AND (balance_before_settlement >= (0)::numeric)))`
- `warranty.claim_items.ck_warranty_claim_item_quantity_positive`: `CHECK ((quantity > (0)::numeric))`
- `warranty.shop_stock_cases.ck_shop_warranty_quantity_positive`: `CHECK ((base_quantity > (0)::numeric))`

## Primary/unique identity highlights

- Primary keys: 84, all present in the clean migrated schema.
- Unique indexes: 265. These enforce normalized product SKU, unit barcode, TrackingCode, normalized manufacturer identity claim, SupplierProduct pair, supplier DealerCode, supplier item sequence, document numbers, operation identity (where configured), and one active warranty unit claim.
- Application-only aggregate invariants such as sale header equals sale item sum, payment application equals sale total, stock bucket balance equals movement effects, and ledgers remain immutable are not expressed as PostgreSQL constraints.
- Direct-SQL matrix probes showed financial/audit history updates/deletes can execute for seeded rows, identity columns can be cleared, and unrecognized enum integers can be written. Database role privilege limitation may reduce exposure but remains unproven. See finding register.

## Conditional uniqueness / model drift

The open-stocktake index is physically `UNIQUE ((1)) WHERE status IN (1,2,3)`, allowing at most one row across all three active states. EF model/snapshot represents `UNIQUE(status) WHERE status IN (1,2,3)`, which would permit one row per active status. `has-pending-model-changes` reports none because the custom migration index remains outside the EF model comparison. The physical rule is stricter than the modeled rule; this is a drift finding, not evidence that concurrent active stocktakes were observed. Two default ProductUnit partial unique indexes are PostgreSQL-only and enforce one active purchase default and one active sale default.
