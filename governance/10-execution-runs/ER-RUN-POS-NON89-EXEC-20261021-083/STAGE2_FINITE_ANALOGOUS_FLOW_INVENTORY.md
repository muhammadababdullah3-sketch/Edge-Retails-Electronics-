# STAGE 2: Finite Analogous Flow Inventory
**Run ID:** `ER-RUN-POS-NON89-EXEC-20261021-083`

## Analogous Reporting & Activity Flows Inspected
1. **Purchase Returns Flow**: Verified that gross purchase totals and purchase returns calculate net purchases clamped to >= 0 (`Math.Max(0m, Math.Round(grossPurchases - purchaseReturns, 2))`).
2. **Sales Returns Flow**: Verified that gross sales and refunds calculate net sales with restocked return COGS reversals.
3. **Thaka Material Activity Flow**: Verified that issues and reversals calculate net material values per project with explicit presentation.
4. **Expense Breakdown Flow**: Categorized expenses sorted descending by amount.
