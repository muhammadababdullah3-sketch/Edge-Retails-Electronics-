# Edge Retails — Phase 7 Pass 5
# Canonical Business Invariants Registry

**Document Status:** FROZEN CANONICAL AUTHORITY  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Governing Resolutions:** Resolution01, Resolution02, Resolution03, Resolution04, Resolution05  
**Accounting Provider:** Isolated PostgreSQL 18.0 / Npgsql  
**Scope:** Universal Business Rules & Invariant Constraints  

---

## 1. Stock / Quantity Conservation

1. **Total Stock Conservation Equation:**
   $$\text{TotalPhysicalUnits} = \sum_{\text{bucket} \in \text{Buckets}} \text{BucketQuantity}(\text{bucket})$$
   No physical unit or bulk quantity exists without belonging to an explicit canonical `InventoryBucket` (`Sellable`, `Damaged`, `Defective`, `WithSupplier`, `Scrap`, `Transit`).
2. **Book vs Owned Quantity Invariant:**
   - **Owned Quantity:** All stock economically owned by the business (`Sellable`, `Damaged`, `Defective`, `WithSupplier`, `Transit`).
   - **Non-Owned / Custody Only:** Customer warranty intake (`ClaimedCustomerStock`) is physical custody only, zero book stock, zero owned quantity, zero balance impact.
   - **Retained Scrap:** Retained operational units in `Scrap` bucket are non-sellable and have zero carrying value ($0.00).
3. **Exact vs Bulk Quantity Integrity:**
   - **Exact Units (`TraceabilityMode.Serialized` / `Container`):** BaseQuantity is fixed integer `1` (or fixed container content). Unit status strictly matches bucket (`Available` -> `Sellable`/`Damaged`/`Defective`; `WithSupplier` -> `WithSupplier`; `Missing` -> removed from active buckets; `Sold` -> exit; `Scrapped` -> `Scrap`).
   - **Bulk Units (`TraceabilityMode.Quantity` / `Length`):** Continuous non-negative decimal quantity. Length measured with 3-decimal precision (e.g., meters, yards).
4. **Conservation Across Movements:**
   - Every stock addition, decrement, or transfer MUST append an immutable `InventoryMovement` and `InventoryMovementEffect`.
   - $\sum(\text{Movement Deltas}) + \text{Opening} = \text{Current Balance}$ exact.

---

## 2. Monetary Conservation & Precision

1. **Rounding Policy Authority:**
   - Monetary values (Cash, Sales, Receivables, Payables, COGS, Expenses, Losses, Recoveries) are rounded to 2 decimal places using `MidpointRounding.AwayFromZero` via `MoneyRoundingPolicy`.
   - Continuous unit acquisition costs and MWA unit costs are preserved with high precision (4 to 6 decimal places).
2. **Proportional Document Allocation Invariant (C26):**
   - For all document-level adjustments (invoice discounts, shipping charges, taxes) split across lines:
     $$\sum_{i=1}^{n} \text{LineAllocation}_i = \text{DocumentTotal}$$
   - Any residual fractional cents ($0.01$) resulting from division are deterministically allocated to the largest line (or first line on tie) to ensure zero cent loss or inflation.
3. **Non-Negativity Invariant:**
   - Physical quantities, unit prices, acquisition costs, line totals, and recognized loss amounts are strictly $\ge 0.00$.
   - Negative values are represented solely via explicit financial directions (`DecreasePayable`, `CashMovementDirection.Out`, `SaleReturn`, etc.).

---

## 3. Cost Basis & Carrying Value Authority (Exact vs Bulk)

1. **Exact-Unit Authority (Resolution04 / Pass 4 H1/H2):**
   - Each exact unit carries an immutable `AcquisitionCost` established at certified physical intake.
   - Unit exits (Sale, Scrap, Lost, SupplierReturn) derecognize the unit's exact specific cost.
   - Return of an exact unit restores its original specific cost basis to the inventory pool.
2. **Bulk-Unit Authority (Resolution05 / D13-2):**
   - Carrying value is governed globally by `ProductCostState` using the product-wide **Moving Weighted Average (MWA)**:
     $$\text{MWA} = \frac{\text{TotalInventoryCost}}{\text{CostedQty}}$$
   - **Send to Shop Warranty (Custody-Only):**
     - Transfer to `WithSupplier` is custody-only.
     - `OwnedQty` is UNCHANGED. `TotalInventoryCost` is UNCHANGED.
     - MWA is UNCHANGED. Send-time snapshot is recorded for reference only.
   - **Terminal Economic Disposition (Resolution05 MWA55 Rule):**
     - When bulk warranty stock is terminally resolved as `Credited` or `Scrapped`:
       $$\text{CarryingValueRemoved} = \text{ResolvedQuantity} \times \text{CurrentMWA}(\text{ResolutionTime})$$
     - Derecognition occurs at the **current authoritative product-wide MWA**, NEVER at original purchase cost or send-time snapshot.
3. **Lot & Supplier Provenance (Immutable Facts):**
   - Provenance authority traces back: $\text{OriginalInventoryLot} \rightarrow \text{PurchaseItem} \rightarrow \text{Supplier}$.
   - Lineage is immutable even as carrying value follows MWA.

---

## 4. Revenue Recognition & COGS Authority

1. **Commercial Sales Equations:**
   $$\text{NetSales} = \text{GrossSales} - \text{SaleRefunds}$$
   $$\text{NetCOGS} = \text{GrossCOGS} - \text{ReversedCOGS}$$
   $$\text{GrossProfit} = \text{NetSales} - \text{NetCOGS}$$
2. **Exclusion of Non-Commercial Inflows:**
   - **Supplier Refunds:** Capital/AP recoveries, NEVER revenue.
   - **Warranty Recovery Gains:** Operating expense/recovery offsets, NEVER Sales revenue.
   - **Inventory Found Recoveries:** Loss recovery gains, NEVER Sales revenue.
   - **Cash Drawer Floats / In Movements:** Cash capital transfers, NEVER revenue.
3. **COGS Reversal Invariant:**
   - A sale return reverses COGS by the exact historical consumed cost of the returned item.
   - Reversal cannot exceed original recognized COGS.

---

## 5. Loss Recognition & Recovery Gains Authority (D17)

1. **Inventory Loss Authority:**
   - Inventory loss is recognized exclusively through `InventoryMovement.RecognizedLossAmount` ($\ge 0.00$) upon:
     - Stocktake shortage / Lost adjustment.
     - Condition transfer to Scrap.
     - Scrapped warranty case.
     - Shop warranty credit deficit: $\max(0, \text{ActualResolvedCarryingValue} - \text{SupplierCreditAmount})$.
   - Loss amounts are immutable facts. Reversals or subsequent recoveries NEVER mutate or delete historical loss.
2. **Operating Recovery Gains (Non-Sales / Non-COGS):**
   - **`InventoryLossRecoveryGain`:** Generated when a previously missing unit is recovered via `FoundInventoryUnitHandler`. Restores inventory value at historical removed value and recognizes separate operating gain equal to historical allocated loss.
   - **`WarrantyRecoveryGain`:** Generated when supplier credit exceeds resolved carrying value:
     $$\text{WarrantyRecoveryGain} = \max(0, \text{SupplierCreditAmount} - \text{ActualResolvedCarryingValue})$$
3. **Canonical Net Profit Equation:**
   $$\text{NetProfit} = \text{GrossProfit} - \text{Expenses} - \text{InventoryLoss} + \text{InventoryLossRecoveryGain} + \text{WarrantyRecoveryGain}$$

---

## 6. Cash & Payment Drawer Authority (B03)

1. **Session & Drawer Precondition:**
   - All drawer cash transactions require an active, open `CashSession`.
   - Cash drawer balance derives strictly from:
     $$\text{DrawerBalance} = \text{OpeningCash} + \sum \text{CashIn} - \sum \text{CashOut}$$
2. **Separation of Commercial vs Manual Cash:**
   - Commercial events (`SaleCashIn`, `SaleRefundCashOut`, `PurchaseCashOut`, `ExpenseCashOut`, etc.) are posted only by their respective authorized commercial handlers.
   - Generic manual cash endpoint (`POST /api/finance/cash-movement`) accepts ONLY `ManualCashIn` (with `Direction.In`) and `ManualCashOut` (with `Direction.Out`). All commercial event types and direction mismatches are strictly rejected (HTTP 400).
3. **Drawer Non-Cash Immunity:**
   - Bank, card, voucher, customer credit, and supplier AP offset transactions produce ZERO cash drawer movements.

---

## 7. Accounts Payable / Supplier Ledger Authority (C29)

1. **Running Balance Authority:**
   - Supplier ledger is an append-only sequence of `SupplierAccountEntry` records.
   - Running balance derives dynamically:
     $$\text{SupplierBalance} = \sum \text{IncreasePayable} - \sum \text{DecreasePayable}$$
2. **Entry Type Classification:**
   - `Purchase`: Increases payable.
   - `PurchaseReturn`: Decreases payable.
   - `SupplierPayment`: Decreases payable (settlement).
   - `SupplierRefund`: Increases payable (returns excess credit).
   - `WarrantyCredit`: Decreases payable.
   - `OpeningBalance` (C29): Explicit append-only opening liability adjustment; requires `PermissionKeys.SupplierAccountAdjust`. Cannot be represented as fake Purchase or fake payment.
3. **Immutability & Anti-Tampering:**
   - EF ChangeTracker enforces strict append-only rules: updates and deletions on `SupplierAccountEntry` throw `InvalidOperationException`.

---

## 8. Customer Khata & Suspension Authority

1. **Customer Khata Balance Invariant:**
   $$\text{CustomerBalance} = \sum \text{CreditSales} - \sum \text{Payments} - \sum \text{Refunds}$$
2. **Suspension Enforcement:**
   - Suspended customers are strictly blocked from:
     - New credit sales (Khata).
     - New quotations.
     - New Thaka issues.
   - Cash-only settlements and balance repayments remain permitted while suspended.
3. **Reactivation Authority:**
   - Only authorized managers can lift customer suspensions, recording explicit business audit attribution.

---

## 9. Identity, Session & Terminal Security Authority

1. **Terminal Authentication:**
   - Every API request requires valid `X-Terminal-Id` and `X-Terminal-Secret` matching an active registered terminal. Revoked or suspended terminals receive HTTP 401.
2. **User Session Authentication:**
   - Non-whitelisted endpoints require valid `X-Session-Id`.
   - Expired, revoked, or disabled user sessions are rejected (401/403).
3. **Actor Anti-Spoofing Invariant:**
   - Handlers derive `ActorId` strictly from the authenticated session context (`HttpContext.GetActorContext()`). Any caller-supplied `ActorId` in JSON payload is overridden.
4. **Role & Permission RBAC:**
   - Granular permissions (`PermissionKeys`) are checked before commercial mutations (`SalesCreate`, `PurchasingManage`, `SupplierAccountAdjust`, `ExpensesManage`, `ThakaManage`, `SettingsManage`).

---

## 10. Idempotency, Concurrency & Replay Authority

1. **Durable Operation Outcomes:**
   - Every mutating command carries an immutable `ClientOperationId`.
   - Handlers check `IOperationOutcomeLedger` before executing.
   - **Exact Replay:** Returns original outcome (`wasExisting: true`, original entity ID, original document number) with ZERO duplicate mutation.
   - **Payload Conflict:** If payload differs from original invocation for the same `ClientOperationId`, execution fails closed with `idempotency.payload_mismatch` (HTTP 409).
2. **Deterministic Pessimistic Locking:**
   - Resource locks (`IResourceLock`) acquire keys in strict alphabetical order:
     `product` $\rightarrow$ `inventory-unit` $\rightarrow$ `sale-item` $\rightarrow$ `warranty-case` $\rightarrow$ `customer` $\rightarrow$ `supplier`
   - Deadlock-free guarantee across concurrent sales, returns, exchanges, and warranty claims.
