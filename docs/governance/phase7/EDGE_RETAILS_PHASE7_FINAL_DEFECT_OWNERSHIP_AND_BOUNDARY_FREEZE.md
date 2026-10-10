# EDGE RETAILS — PHASE 7 STEP 2
# FINAL DEFECT OWNERSHIP, TRACEABILITY RECONCILIATION & IMPLEMENTATION BOUNDARY FREEZE

**Target Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Execution Mode:** STRICT READ-ONLY / AUDIT & OWNERSHIP FREEZE  
**Status:** COMPLETE / FROZEN  

---

## 1. CANONICAL PROGRAM AUTHORITY & REMAINING EXECUTION ORDER

The program roadmap is authoritative and permanently locked against drift or unauthorized expansion.

### Authoritative Phase Baseline
- **Phase 1 (Authority, Runtime Rebase, Core Security + Product/Physical Identity):** COMPLETE / CLOSED / LOCKED
- **Phase 2 (Shop Server API Parity & Backend Hardening):** COMPLETE / CLOSED / LOCKED
- **Phase 3 (Desktop Cutover, POS UX & Local Production Operations):** COMPLETE / CERTIFIED / CLOSED / LOCKED
- **Phase 4 (Licensing V2, Vendor Backend & Protected Signer):** FUTURE
- **Phase 5 (Admin Portal & Commercial Operations Cutover):** FUTURE
- **Phase 6 (Hostile Production Certification & Release Closure):** FUTURE
- **Phase 7 (Business Mutation, Stock & Accounting Integrity):** CURRENT ACTIVE TARGET
- **Phase 8 (Lifecycle, Concurrency, Time & Identity Hardening):** NEXT TECHNICAL PHASE
- **Phase 9 (Printing, Backup & Operational Reliability):** FUTURE
- **Phase 10 (Extended Forensic Certification & Final Authority Closure):** FUTURE
- **Phase 11 (Business Integrity + Inventory Provenance + Final Tracking / Container-Pack Architecture):** COMPLETED EARLY (Tracking Remediation & Freeze Certified)
- **Phase 12 (Operation Identity + Replay / Recovery Authority):** SINGLE-INSTALLATION REPLAY RECOVERY
- **Phase 13 (Ultimate Whole-System Hostile Certification + Final V1 Authority Closure):** FINAL GATEWAY

### Authoritative Practical Execution Order
Because Phase 11 (Tracking & Container-Pack Architecture) was completed, independently challenged, and certified early under `TRACKING_CERTIFIED_FROZEN_LOCK_CANDIDATE`, it is not repeated. The remaining program sequence is strictly:

$$\text{Phase 7} \longrightarrow \text{Phase 12} \longrightarrow \text{Phase 8} \longrightarrow \text{Phase 9} \longrightarrow \text{POS Acceptance} \longrightarrow \text{Phase 4} \longrightarrow \text{Phase 5} \longrightarrow \text{Phase 6} \longrightarrow \text{Phase 10} \longrightarrow \text{Phase 13}$$

---

## 2. V1 ARCHITECTURE BOUNDARY (LOCKED)

Edge Retails V1 is strictly:
- **SINGLE SHOP**
- **SINGLE PRIMARY INSTALLATION**
- **SINGLE MACHINE COMMERCIAL POS**

The following concepts are permanently classified as **STALE / UNAUTHORIZED / OUTSIDE V1** and are barred from all current and future phase backlogs:
1. Multi-branch architecture
2. Multi-outlet replication
3. Central branch replication
4. Offline mesh synchronization
5. Peer-to-peer register synchronization
6. LAN multi-workstation authority
7. Seat/terminal quota architecture (`MaxTerminals`)
8. Multi-party distributed transaction coordinators
9. Customer Khata (Accounts Receivable Credit Ledger) is categorized as **OUTSIDE V1 / COMMERCIAL EXPANSION** (V1 commercial POS requires immediate settlement via Cash, Bank, or Card).

---

## 3. TRACKING AUTHORITY (FROZEN DEPENDENCY)

The Tracking System remediation is 100% complete, certified under PostgreSQL integration rehearsals, and passed adversarial lock challenges.
Phase 7 operates under the strict **TRACKING CONSUMER ONLY** contract.

Phase 7 consumes, and does not alter:
- `PhysicalUnitCreationAuthority` (PUCA)
- `TrackingCode` canonical format and generation
- `ItemSequence` canonical allocation
- `InventoryUnitIdentityClaim`
- `IdentityNormalizationRules`
- `MachineSequenceHighWaterService`
- `SequenceAuthorityCustody`
- Exact-unit lot provenance & snapshots (`DealerCode`, `Sku`, `ProductUnitId`, `AcquisitionCost`)
- Container physical quantity provenance

**Tracking Changes Required for Phase 7:** **NONE (0).**

---

## 4. DEFECT RECONCILIATION & CRITICAL TRACEABILITY AUDIT

In accordance with the **Critical Traceability Rule**, no original defect ID has been overwritten, mutated, or repurposed. Where new, distinct issues were uncovered during Step 1 analysis, they have been assigned permanent new identifiers (`P7-N01`, `P7-N02`, `P7-N03`), while original IDs (`F01`–`F14`, `D-VOID-1`, `D-VOID-2`, `D-EXP-1`, `D-RET-1`, `D-ADJ-1`) are restored to their true forensic definitions.

### Reconciled Comparison Matrix

| Defect ID | Original Definition (First Forensic Audit) | Step 1 Mutated Definition | Live Code Inspection Truth | Resolution / Assigned ID |
|---|---|---|---|---|
| **D-VOID-1** | Unreceived purchase orders must be voidable without lot checks (`VoidPurchaseHandler.cs:165`). | Purchase Order Voiding bypasses inventory check when `alreadyReceived > 0`, enabling phantom stock. | When `ReceiveStockImmediately == false`, `VoidPurchaseHandler` fails closed (`void_stock_consumed`) because 0 lots exist. Unreceived POs cannot be voided. | **D-VOID-1 Restored to Original Meaning.** |
| **D-VOID-2** | Voiding purchase with initial cash payment must restore cash drawer (`VoidPurchaseHandler.cs:190-210`). | PO Voiding leaves supplier payments orphaned in `SupplierPayments` without refunding drawer or reversing Khata. | Both aspects are the same bug: `VoidPurchaseHandler` sets `CashDrawerReversalAmount = null` and does not reverse `SupplierPayment` or refund cash. | **D-VOID-2 Confirmed & Unified.** |
| **D-EXP-1** | Voiding expense must return cash to register drawer (`ExpenseHandlers.cs:220-245`). | Non-operating expenses debit the cash drawer without a compensating `CashMovement` entry. | `PostExpenseHandler` correctly calls `_cashMovements.RecordAsync`! But `VoidExpenseHandler` does NOT inject cash services and never restores cash. | **D-EXP-1 Restored to Original Meaning (Void Path). Posting claim is False Positive.** |
| **D-RET-1** | `CashDrawer` settlement mode must record cash drawer intake (`PurchaseReturnHandler.cs:520-545`). | Purchase Returns from partially received POs calculate invalid returned quantities. | Two distinct bugs exist: (1) `CashDrawer` settlement never creates `CashMovementType.CashIn`. (2) Return quantity limit is checked against ordered `item.BaseQuantity` instead of received quantity. | **D-RET-1 Restored to Cash Drawer Intake.** New ID **`P7-N01`** assigned to partial-receipt quantity limit defect. |
| **D-ADJ-1** | Container adjustment requires valid `ProductUnit`; missing check defaults factor to 1.0m. | Container adjustment `ProductUnit` requirement. | Line 225 of `StockAdjustmentHandlers.cs` sets `var factor = 1m;` when `item.ProductUnitId` is null, miscalculating container packs as raw pieces. | **D-ADJ-1 Confirmed.** |
| **F01** | `SetPhysicalCount` delta semantics: $\Delta = \text{Target} - \text{Current}$. | Ignored container pack factor $K$ when `ProductUnitId` is null (merged with D-ADJ-1). | `CreateStockAdjustmentHandler` treats all inputs as blind deltas. It never computes $\text{Target} - \text{Current}$. `SetPhysicalCount` is completely broken. | **F01 Restored to SetPhysicalCount Semantics.** |
| **F02** | Condition transfer scrap carrying-value arithmetic (`InventoryConditionHandlers.cs:136-150`). | Thaka line charge UOM conversion (Step 1 overwrote F02 with F04). | `InventoryConditionHandlers.cs:141` divides `AcquisitionCost` by quantity to pass unit cost, but unit cost allocator multiplies by quantity again. | **F02 Restored to Condition Scrap Arithmetic.** |
| **F03** | Physical/serialized condition-transfer quantity integrity (`InventoryConditionHandlers.cs:52-88`). | Thaka duplicate `InventoryUnitId` references (Step 1 overwrote F03). | Serialized condition transfers must enforce integer units and exact unit count. | **F03 Restored to Condition Serialized Quantity.** New ID **`P7-N02`** assigned to Thaka duplicate unit defect. |
| **F04** | Thaka line item unit factor `ProductUnit.FactorToBaseUnit` omitted in line charges (`ThakaHandlers.cs:380-410`). | Inventory accounting policy divergence (`Scrapped` and `IssuedThaka` cost state). | `ThakaHandlers.cs:414` calculates `Money(authoritativeCharge * input.EnteredQuantity)` instead of multiplying by `quantity.BaseQuantity`. | **F04 Restored to Thaka UOM Line Charge Multiplier.** New ID **`P7-N03`** assigned to policy reconciliation. |
| **F05** | Purchase Void replay/idempotency via `IOperationOutcomeLedger` (`VoidPurchaseHandler.cs`). | POS checkout lacks idempotent request tokens (Step 1 overwrote F05). | `VoidPurchaseHandler` does not integrate `IOperationOutcomeLedger`. CompleteSale is already replay-safe! | **F05 Restored to Purchase Void Replay.** POS checkout claim is False Positive. |
| **F06** | Commercial Exchange replay/idempotency via `IOperationOutcomeLedger` (`CommercialExchangeHandler.cs`). | Warranty Replacement allows same-unit / non-sellable unit reuse (Step 1 overwrote F06). | `CommercialExchangeHandler` does not integrate `IOperationOutcomeLedger`. Warranty replacement mints new units via PUCA! | **F06 Restored to Commercial Exchange Replay.** Warranty claim is False Positive. |
| **F07** | POS Draft & Quote idempotency / replay (`QuotationHandlers.cs:502`, `PosDraftHandlers.cs:120`). | Cash Drawer float reconciliation on shift close (Step 1 overwrote F07). | Quotation cancellation and draft conversion replay gaps exist. Cash session float handling is standard. | **F07 Restored to POS Draft & Quote Replay.** Float claim is False Positive / Phase 8. |
| **F10** | Commercial Exchange return vs active Warranty custody (`CommercialExchangeHandler.cs:750-780`). | Sale Void allows partial voiding without recomputing tax/discounts (Step 1 overwrote F10). | Active warranty unit check is missing in Exchange. Sale Void DOES NOT EXIST in the codebase! | **F10 Restored to Commercial Exchange Warranty Guard.** Sale Void claim is False Positive / Removed. |
| **F12** | Net Profit omits recognized inventory scrap losses in `BusinessOperationsReadServices.cs`. | Net Profit omits `RecognizedLossAmount`. | `ReportingReadService.cs:390` calculates `netProfit = grossProfit - expenseTotal;`, omitting `InventoryMovement.RecognizedLossAmount`. | **F12 Confirmed.** |
| **F13** | `BusinessDate` / shop timezone consistency across mutation handlers. | Shop timezone / BusinessDate handover to Phase 8. | Accounting subledgers record UTC. Timezone day-boundary presentation belongs to Phase 8. | **F13 Handed Over to Phase 8.** |
| **F14** | Append-only audit check for `SupplierAccountEntry` and `CashMovement` in `EdgeRetailsDbContext`. | Subledger append-only ChangeTracker guard. | `EdgeRetailsDbContext.EnforceAppendOnlyAudit()` only checks `BusinessAuditEvent`. Subledgers can be modified by ORM. | **F14 Confirmed (ORM ChangeTracker Guard).** |

---

## 5. SPECIAL TOPIC FORENSIC RESOLUTIONS

### 5.1 Purchase Void Semantics (Cases A–F)
The domain rule is absolute: **COMMITTED RECEIPT HISTORY MUST NEVER DISAPPEAR.**

- **Case A (Zero Quantity Received):**  
  Purchase created with `ReceiveStockImmediately = false`, 0 items received.  
  - Inventory effect: None (bypass lot check; 0 lots exist).  
  - Financial effect: Decrease payable by `purchase.GrandTotal` (`PurchaseVoidReversal`). If initial payment was made (Case D), refund cash drawer (`CashMovementType.CashIn`).  
  - Terminal status: `PurchaseStatus.Voided`.
- **Case B (Partially Received PO, e.g. Ordered 10, Received 4, Outstanding 6):**  
  - The 4 received units are live physical inventory with lots and costs. They **cannot be cancelled via Purchase Void**. (To return them to the supplier, the operator must execute a `PurchaseReturnHandler` transaction).  
  - The unreceived 6 units are cancelled/closed against further intake.  
  - Supplier payable is decreased to reflect only the 4 received units.  
  - Purchase remains `PurchaseStatus.Completed` (or closed against further intakes).
- **Case C (Fully Received PO):**  
  Permitted ONLY if 100% of received stock is intact in `InventoryBucket.Sellable` (`availableLotQuantity == item.BaseQuantity`), zero downstream consumption exists, and zero supplier returns exist.  
  - Units transition to terminal `InventoryUnitStatus.ReceiptVoided`.  
  - Lots and stock balances are decremented. Carrying value is removed via `_costs.RemoveCarryingValueAsync`.  
  - Supplier payable decreased by `GrandTotal`.  
  - If ANY unit is consumed, sold, or returned: **Void fails closed.**
- **Case D (Initial Payment Exists):**  
  Cash was withdrawn from drawer (`CashMovementType.SupplierPaymentCashOut`). Voiding requires:  
  1. Active cash session must be open.  
  2. Compensating `CashMovementType.CashIn` recorded to restore physical drawer cash.  
  3. `SupplierPayment` marked refunded/reversed, and compensating `SupplierAccountEntry` (`IncreasePayable`) created to balance the `PurchaseVoidReversal`.
- **Case E (Subsequent Supplier Payments Exist):**  
  Void blocked (`purchasing.void_blocked_subsequent_payments_exist`) until operator explicitly reverses or reallocates subsequent supplier payments.
- **Case F (Purchase Return Exists):**  
  Void permanently forbidden (`purchasing.void_has_returns`).

### 5.2 Expense & Cash Drawer Reconciliation
- `EXPENSE_POSTING_DEFECT`: **NO (False Positive).** Live code inspection proves `PostExpenseHandler` lines 130–148 properly calls `_cashMovements.RecordAsync(...)` when `PaymentMethod == Cash`.
- `EXPENSE_VOID_DEFECT`: **YES (Confirmed Defect D-EXP-1).** `VoidExpenseHandler` does not inject cash services, does not inspect payment method, and never refunds cash to the drawer when a cash expense is voided.

### 5.3 Purchase Return Reconciliation
- `D-RET-1 (Cash Drawer Settlement Missing CashMovement)`: **CONFIRMED.** When `SettlementMode == CashDrawer`, `PurchaseReturnHandler` updates supplier ledger but records no `CashMovementType.CashIn`.
- `P7-N01 (Partial-Receipt Quantity Limit Defect)`: **CONFIRMED.** Line 265 compares return quantity against ordered `item.BaseQuantity` instead of received base quantity. Assigned new ID `P7-N01`.

### 5.4 Stock Adjustment Semantics (`SetPhysicalCount`)
- **Non-Physical (`Quantity`, `Length`):** Target physical quantity $Q_{\text{target}} \ge 0$. Current balance $Q_{\text{current}} = \text{stock.SellableQty}$. Delta $\Delta = Q_{\text{target}} - Q_{\text{current}}$. Increase adds FIFO lot; decrease consumes FIFO lot. No `InventoryUnitIds`.
- **Physical (`Serialized`, `IndividualPiece`):** Cannot be a blind delta. If $\Delta > 0$, operator must provide $\Delta$ new identities; handler calls `PUCA.CreateAsync`. If $\Delta < 0$, operator must select $|\Delta|$ specific `InventoryUnitIds` to remove.
- **Container (`TrackingMode.Container`):** Target is container pack count $C_{\text{target}}$. Pack factor $K$ must come from `ProductUnit.FactorToBaseUnit`. Fallback to $1.0\text{m}$ when `ProductUnitId` is null is eliminated (**D-ADJ-1**). Base delta $\Delta_{\text{base}} = (C_{\text{target}} - C_{\text{current}}) \times K$.

### 5.5 Thaka UOM & Identity Reconciliation
- **UOM Line Charge Multiplier (F04):** `authoritativeCharge` is per base unit. Line 414 must compute `Money(authoritativeCharge * quantity.BaseQuantity)` instead of `EnteredQuantity`.
- **Cross-Line Duplicate Unit Protection (P7-N02):** `IssueThakaMaterialHandler` checks uniqueness within a single line, but lacks a cross-line deduplication check `command.Lines.SelectMany(x => x.InventoryUnitIds).Distinct()`. Assigned new ID `P7-N02`.

### 5.6 Inventory Accounting Policy Reconciliation (P7-N03)
- `InventoryUnitStatus.Scrapped`: Carrying value is derecognized from the inventory cost pool by `_costAllocator.RemoveCarryingValueAsync` and recorded as `RecognizedLossAmount`. It is NOT an owned inventory asset. In `InventoryModels.cs:619`, `ContributesToProductCostState` must be updated to `false`.
- `InventoryUnitStatus.IssuedThaka`: Carrying value transfers out of merchandise inventory assets into project WIP/job cost (`issue.TotalCost`). In `InventoryModels.cs:569`, `ContributesToProductCostState` must be updated to `false`.

### 5.7 Net Profit Calculation (F12)
- Query path: `ReportingReadService.cs:388-390`.
- Inspection: `cogs` only queries `SaleItems`; `expenseTotal` only queries `Expenses`. Neither includes scrap losses.
- Formula change: $\text{NetProfit} = \text{GrossProfit} - \text{Expenses} - \text{RecognizedLossAmount}$.
- Result: **`F12_CONFIRMED`**. Zero risk of double counting.

### 5.8 Append-Only Subledger Enforcement (F14)
- Scope: **ORM-Level Immutability via DbContext ChangeTracker.**
- `EdgeRetailsDbContext.EnforceAppendOnlyAudit()` will be extended to check `SupplierAccountEntry` and `CashMovement`. If state is `Modified` or `Deleted`, throw `InvalidOperationException`.
- Database-level/direct-SQL tamper resistance (DDL triggers, grant restrictions, HMAC hashing) is classified as **LATER SECURITY HARDENING (Phase 11 / Phase 10)** and is **NOT REQUIRED FOR V1**.

### 5.9 Sale Void Non-Existence
- Live code inspection verifies that no `VoidSaleHandler`, `CancelSaleHandler`, or `SaleStatus.Voided` exists.
- Commercial cancellations are handled exclusively via `SaleReturnHandler.cs` (returns) or `CommercialExchangeHandler.cs` (exchanges).
- Step 1's claim of "Sale Void partial tax/discount recalculation" was a **FALSE POSITIVE / NON-EXISTENT WORKFLOW**. Removed.

### 5.10 POS Sale Idempotency
- Live code inspection of `CompleteSaleHandler.cs` lines 143–176 confirms:
  1. `_operationLock.AcquireAsync(command.ClientOperationId)` is acquired.
  2. `GetSaleByClientOperationIdAsync` checks for previous submission.
  3. `MatchesExistingSalePayloadAsync` verifies exact payload fingerprint.
  4. Outcome is returned idempotently without duplicate inventory or financial effects.
- Live code inspection of `CompleteSaleViewModel.cs` confirms `_clientOperationId` is allocated once and reused, with `IsProcessing = true` button-guarding.
- Verdict: **`BACKEND_REPLAY_SAFE`**. The Step 1 claim is a **FALSE POSITIVE**.

### 5.11 Warranty Replacement Non-Regression
- Live code inspection of `ReceiveCustomerWarrantyReplacementHandler` and `ReceiveShopStockWarrantyHandler` confirms that replacement units are minted as brand new physical units via `_physicalUnits.CreateAsync(...)` with new `TrackingCode`s and verified global identity uniqueness.
- Claim C10 remains 100% true.
- Verdict: **`FALSE_POSITIVE`** (no tracking regression, no workflow guard gap).

---

## 6. PHASE 12 & PHASE 8 HANDOVERS (PRESERVED EXACTLY)

### Phase 12 Handover (Locked & Preserved)
- **`P12-H01 (Replay-before-current-validation ordering)`:**  
  *Observed Area:* `ReceiveProductIntakeHandler`, `SendShopStockToSupplierWarrantyHandler`.  
  *Issue:* A retried operation may fail current-state master validation instead of returning the durable original outcome.  
  *Primary Owner:* **PHASE 12**. *Tracking Reopening:* **NO**.
- **`P12-H02 (Canonical payload fingerprint uniformity)`:**  
  *Observed Area:* `CreatePurchaseHandler`, `SaleReturnHandler`.  
  *Issue:* Immutable canonical payload fingerprinting is not uniform across all replay-sensitive operations.  
  *Primary Owner:* **PHASE 12**. *Tracking Reopening:* **NO**.

### Phase 8 Handover (Locked & Preserved)
- **`F13 (Shop Timezone / BusinessDate Authority)`:**  
  All mutations record exact UTC timestamps (`CreatedAt`, `OccurredAt`). Converting UTC to operational shop date based on `ShopProfile.TimezoneId` across day boundaries belongs to Phase 8 time hardening.  
  *Primary Owner:* **PHASE 8**.

---

## 7. AUTHORITATIVE PHASE 7 DEFECT REGISTER

| Defect ID | Original Source | Defect Description | Verified? | Severity | Primary Owner | Phase 7 Blocker? | Tracking Impact | Migration Impact | Affected Handler | Business Consequence | Recommended Direction | Required Regression Proof |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **D-VOID-1** | Forensic Audit | Unreceived PO cannot be voided; lot check fails closed when 0 received. | YES | **FATAL** | PHASE 7 | YES | NONE | NONE | `VoidPurchaseHandler.cs` | Operator cannot cancel pending/unreceived supplier purchase orders. | Bypass lot position check when purchase is unreceived (`alreadyReceived == 0`). | Unit/integration test voiding PO with `ReceiveStockImmediately = false`. |
| **D-VOID-2** | Forensic Audit | Purchase void does not refund cash drawer or balance supplier ledger for initial payments. | YES | **FATAL** | PHASE 7 | YES | NONE | NONE | `VoidPurchaseHandler.cs` | Cash drawer permanently short; supplier ledger balance corrupted. | Inject `ICashMovementService`, record `CashMovementType.CashIn`, reverse `SupplierPayment`. | Test verifying drawer cash restored and supplier ledger net balance = 0. |
| **D-EXP-1** | Forensic Audit | Voiding expense does not restore cash to register drawer. | YES | **FATAL** | PHASE 7 | YES | NONE | NONE | `ExpenseHandlers.cs` | Cash drawer permanently short by voided expense amount. | Inject `ICashMovementService`, record `CashMovementType.CashIn` on cash expense void. | Test voiding cash expense and asserting drawer cash balance increases. |
| **D-RET-1** | Forensic Audit | Purchase return `CashDrawer` settlement mode does not record cash drawer intake. | YES | **FATAL** | PHASE 7 | YES | NONE | NONE | `PurchaseReturnHandler.cs` | Physical cash returned by supplier is missing from register ledger. | Inject `ICashMovementService`, record `CashMovementType.CashIn` on return. | Test return with `SettlementMode = CashDrawer` asserting `CashMovement` created. |
| **D-ADJ-1** | Forensic Audit | Container stock adjustment defaults factor to 1.0m if `ProductUnitId` is null. | YES | **CRITICAL** | PHASE 7 | YES | NONE | NONE | `StockAdjustmentHandlers.cs` | Multi-unit container packs treated as single base pieces. | Require valid `ProductUnitId` for Container tracking mode adjustments. | Test asserting validation failure when adjusting Container without `ProductUnitId`. |
| **F01** | Forensic Audit | `SetPhysicalCount` mode does not calculate $\Delta = \text{Target} - \text{Current}$. | YES | **CRITICAL** | PHASE 7 | YES | NONE | NONE | `StockAdjustmentHandlers.cs` | Inventory balance corrupted; target physical count cannot be set or zeroed. | Compute delta $\Delta = \text{Target} - \text{Current}$ and apply signed delta. | Test setting physical count from 10 to 4 and 10 to 0. |
| **F02** | Forensic Audit | Condition transfer scrap carrying-value arithmetic divides by quantity before allocator. | YES | **CRITICAL** | PHASE 7 | YES | NONE | NONE | `InventoryConditionHandlers.cs` | General ledger carrying value and recognized scrap loss distorted. | Pass total exact unit cost directly to `RemoveCarryingValueAsync`. | Test verifying exact recognized loss matches unit acquisition cost. |
| **F03** | Forensic Audit | Condition transfer serialized quantity integrity on container/piece modes. | YES | **HIGH** | PHASE 7 | NO | NONE | NONE | `InventoryConditionHandlers.cs` | Fractional pack quantities could bypass validation. | Enforce whole pack quantity validation for container condition transfers. | Test rejecting fractional container transfers. |
| **F04** | Forensic Audit | Thaka line charge calculation omits `ProductUnit.FactorToBaseUnit`. | YES | **HIGH** | PHASE 7 | YES | NONE | NONE | `ThakaHandlers.cs` | Projects under-billed when issuing materials in multi-base UOMs. | Multiply line charge by `quantity.BaseQuantity` instead of `EnteredQuantity`. | Test issuing 2 boxes of 10 and asserting charge reflects 20 base units. |
| **F05** | Forensic Audit | Purchase void missing `IOperationOutcomeLedger` integration. | YES | **HIGH** | PHASE 7 | NO | NONE | NONE | `VoidPurchaseHandler.cs` | Replayed void could return inconsistent error on retry. | Integrate `IOperationOutcomeLedger` caching. | Replay test with same `ClientOperationId` asserting cached outcome. |
| **F06** | Forensic Audit | Commercial exchange missing `IOperationOutcomeLedger` integration. | YES | **HIGH** | PHASE 7 | NO | NONE | NONE | `CommercialExchangeHandler.cs` | Retried exchange could fail with "units already returned". | Integrate `IOperationOutcomeLedger` caching. | Replay test with same `ClientOperationId` asserting cached outcome. |
| **F07** | Forensic Audit | Quotation cancellation and draft conversion replay gaps. | YES | **HIGH** | PHASE 7 | NO | NONE | NONE | `QuotationHandlers.cs`, `PosDraftHandlers.cs` | Retried draft conversion or quote cancellation fails. | Add idempotent replay guards to quotation and draft handlers. | Replay tests for draft conversion and quote cancellation. |
| **F10** | Forensic Audit | Commercial exchange does not verify returned units are not in warranty custody. | YES | **MEDIUM** | PHASE 7 | NO | NONE | NONE | `CommercialExchangeHandler.cs` | Units under active warranty claim could be returned for store credit. | Add check: unit must not have active warranty claim. | Test attempting to exchange unit in active warranty custody fails. |
| **F12** | Forensic Audit | Net Profit query omits `RecognizedLossAmount` (scrap losses). | YES | **HIGH** | PHASE 7 | NO | NONE | NONE | `BusinessOperationsReadServices.cs` | Management reports overstate net profitability. | Subtract `RecognizedLossAmount` from Net Profit formula. | Test with sales, expenses, and scrap loss asserting exact net profit. |
| **F14** | Forensic Audit | Subledger entries (`SupplierAccountEntry`, `CashMovement`) lack append-only guard. | YES | **MEDIUM** | PHASE 7 | NO | NONE | NONE | `EdgeRetailsDbContext.cs` | Buggy handlers could modify or delete ledger history via ORM. | Add `SupplierAccountEntry` & `CashMovement` to `EnforceAppendOnlyAudit()`. | Test asserting `InvalidOperationException` on subledger entity update/delete. |
| **P7-N01** | Step 1 Discovery | Purchase return checks quantity against ordered instead of received on partial POs. | YES | **HIGH** | PHASE 7 | YES | NONE | NONE | `PurchaseReturnHandler.cs` | Allows returning more non-serialized goods than physically received. | Check return quantity against `received - alreadyReturned`. | Test returning 6 units on a PO where 10 were ordered but only 4 received fails. |
| **P7-N02** | Step 1 Discovery | Thaka issue permits duplicate `InventoryUnitId` across multiple lines. | YES | **HIGH** | PHASE 7 | YES | NONE | NONE | `ThakaHandlers.cs` | Double-issuance race condition or unit status conflict. | Add cross-line uniqueness check across all issue lines. | Test submitting command with duplicate unit IDs across lines fails. |
| **P7-N03** | Step 1 Discovery | Policy mismatch: `Scrapped` and `IssuedThaka` set `ContributesToProductCostState = true`. | YES | **MEDIUM** | PHASE 7 | NO | NONE | NONE | `InventoryModels.cs` | Domain metadata contradicts handler derecognition logic. | Update enum metadata: `ContributesToProductCostState = false`. | Domain model alignment unit tests. |

---

## 8. FINAL PHASE 7 BUCKET CLASSIFICATION

### A. MUST FIX IN PHASE 7 (Fatal / Blockers)
1. **`D-VOID-1`:** Purchase Void unreceived order bypass.
2. **`D-VOID-2`:** Purchase Void cash drawer refund & supplier payment reversal.
3. **`D-EXP-1`:** Expense Void cash drawer refund (`CashMovementType.CashIn`).
4. **`D-RET-1`:** Purchase Return cash drawer settlement intake (`CashMovementType.CashIn`).
5. **`D-ADJ-1`:** Container adjustment mandatory `ProductUnitId` check (eliminate 1.0m fallback).
6. **`F01`:** `StockAdjustment` `SetPhysicalCount` delta calculation ($\Delta = \text{Target} - \text{Current}$).
7. **`F02`:** Condition transfer scrap carrying-value arithmetic.
8. **`F04`:** Thaka line charge UOM conversion (`BaseQuantity` multiplier).
9. **`P7-N01`:** Purchase return partial-receipt quantity limit check.
10. **`P7-N02`:** Thaka cross-line duplicate `InventoryUnitId` rejection.

### B. SHOULD FIX IN PHASE 7 (Quality & Non-Blockers)
1. **`F03`:** Condition transfer container whole pack validation.
2. **`F05`:** Purchase Void `IOperationOutcomeLedger` integration.
3. **`F06`:** Commercial Exchange `IOperationOutcomeLedger` integration.
4. **`F07`:** POS Draft & Quotation replay guards.
5. **`F10`:** Commercial Exchange active warranty custody guard.
6. **`F12`:** Net Profit formula deduction of `RecognizedLossAmount`.
7. **`F14`:** ORM ChangeTracker append-only guard for subledgers in `EdgeRetailsDbContext`.
8. **`P7-N03`:** Domain policy harmonization for `Scrapped` and `IssuedThaka` in `InventoryModels.cs`.

### C. PHASE 8 HANDOVER
1. **`F13`:** `BusinessDate` & shop timezone authority (`ShopProfile.TimezoneId`).
2. **Register/Session Lifecycle:** Shift handover float carryover semantics and optimistic locking retry loops.

### D. PHASE 12 HANDOVER
1. **`P12-H01`:** Replay-before-current-validation ordering (`ReceiveProductIntakeHandler`, `SendShopStockToSupplierWarrantyHandler`).
2. **`P12-H02`:** Canonical payload fingerprint uniformity (`CreatePurchaseHandler`, `SaleReturnHandler`).

### E. POS / DESKTOP ACCEPTANCE BACKLOG
1. Desktop button double-click debouncing / visual busy spinners.
2. Shift close float carryover UI dialog workflow.

### F. OUTSIDE V1 / UNAUTHORIZED (EXPUNGED)
1. Multi-branch / multi-outlet replication.
2. Peer-to-peer / LAN sync protocols.
3. Seat and terminal quotas (`MaxTerminals`).
4. Customer Khata / Accounts Receivable credit ledger.

### G. FALSE POSITIVES / REMOVED
1. **Expense Posting Drawer Issue:** False positive; `PostExpenseHandler` already records cash movements.
2. **Sale Void Tax/Discount Issue:** False positive; Sale Void does not exist in the codebase.
3. **POS Sale Checkout Idempotency Gap:** False positive; backend `CompleteSaleHandler` is fully replay-safe.
4. **Warranty Replacement Identity Reuse:** False positive; PUCA and Warranty handlers enforce brand new physical unit minting.

### H. REQUIRES BUSINESS DECISION
**NONE.** All domain semantics (Purchase Void cases, Stock Adjustment modes, Thaka charges, and inventory accounting) have been resolved using authoritative business conservation laws.

---

## 9. MIGRATION REQUIREMENT DETERMINATION

```text
═══════════════════════════════════════════════════════════════════════════════
                      NO MIGRATION CURRENTLY IDENTIFIED
═══════════════════════════════════════════════════════════════════════════════
```
All 18 accepted remediations are purely C# algorithmic, handler logic, validation rule, ChangeTracker, and LINQ query corrections within existing tables and columns. Zero migrations, schema modifications, or EF Core model snapshot regenerations are required.

---

## 10. IMPLEMENTATION BLOCKER DECISION & PASS 1 SCOPE

```text
═══════════════════════════════════════════════════════════════════════════════
                        PHASE7_BOUNDARY_FROZEN_READY
═══════════════════════════════════════════════════════════════════════════════
```

### Exact Scope for Phase 7 Pass 1 (Fatal Purchasing, Expense & Drawer Cash)
When authorized by the user, Phase 7 implementation will begin strictly with **Pass 1**, addressing:
1. **`D-VOID-1`:** Unreceived Purchase Order Voiding in `VoidPurchaseHandler.cs`.
2. **`D-VOID-2`:** Purchase Void Cash Drawer Compensation & Supplier Payment Reversal in `VoidPurchaseHandler.cs`.
3. **`D-EXP-1`:** Expense Void Cash Drawer Compensation in `ExpenseHandlers.cs`.
4. **`D-RET-1`:** Purchase Return `CashDrawer` Settlement Cash Inflow in `PurchaseReturnHandler.cs`.
5. **`P7-N01`:** Purchase Return Partial-Receipt Quantity & Outstanding Balance Limit in `PurchaseReturnHandler.cs`.

---

## 11. FINAL SAFETY CERTIFICATION

- **Source Code Modified:** NO (0 lines)
- **Tests Modified:** NO (0 lines)
- **Migrations Created:** NO (0 migrations)
- **Tracking System Integrity:** 100% UNTOUCHED & FROZEN
- **Git Working Tree:** Strictly preserved
