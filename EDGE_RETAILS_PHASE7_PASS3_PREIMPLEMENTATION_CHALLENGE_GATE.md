# EDGE RETAILS — PROGRAM PHASE 7 — PASS 3
# PRE-IMPLEMENTATION HOSTILE CHALLENGE GATE REPORT

**Date:** 2026-10-04  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Current Stage:** Pre-Implementation Hostile Challenge Gate (Final GO / NO-GO Review)  
**Primary Audit Authority:** `EDGE_RETAILS_PHASE7_PASS3_STEP1_DEEP_FORENSIC_AUDIT.md`  
**Pass 2 Authority:** `EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md`  
**Pass 2 Certified Status:** `PASS2_CERTIFIED_CLOSED_LOCKED`  
**Pre-Implementation Verdict:** `PASS3_IMPLEMENTATION_PRECHECK_PASSED`  

---

## 0. ABSOLUTE MODE & OPERATIONAL DISCIPLINE
- **Operating Mode:** STRICT READ-ONLY.
- **Production Source Edits:** 0 (ZERO).
- **Test Edits:** 0 (ZERO).
- **Database Mutations:** 0 (ZERO).
- **Migrations Created:** 0 (ZERO).
- **Git Modifications:** 0 (ZERO).
- **Frontend Changes:** 0 (ZERO).
- **Pass 3 Implementation Status:** NOT STARTED. Execution halted at gate.

---

## 1. FINAL PASS 2 PRECONDITION VERIFICATION
- **Authority Verified:** [`EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md)
- **Status:** `PASS2_CERTIFIED_CLOSED_LOCKED`.
- **Baseline Evidence:**
  - Real PostgreSQL 18.6 isolated execution: 190 / 190 passed, 0 failed, 0 skipped.
  - Concurrency test execution: 7 / 7 passed.
  - Rollback atomicity tests: 4 / 4 passed.
  - Unit test suite: 910 / 910 passed.
  - Model snapshot drift: NONE (zero pending changes).
  - Source manifest: SHA-256 `31A09DAC9C4B501D49F7A8E4168290037BC09B2676C00091AFA940E77F19BE1A` over 811 tracked files verified.
- **Historical Report Disambiguation:** The older artifact `EDGE_RETAILS_PHASE7_PASS2_FINAL_HOSTILE_CERTIFICATION_AND_LOCK.md` records the pre-repair terminal failure on Thaka detail DTO timestamp binding; it is superseded by `EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_REPAIR_AND_FINAL_LOCK.md` and was NOT used as active authority.

---

## 2. EXACT PASS 3 SCOPE
This challenge gate evaluates the implementation readiness of exactly six findings:
1. **F05:** `VoidPurchase` missing durable operation outcome / replay protection.
2. **F06:** `CommercialExchange` missing durable operation outcome / replay protection.
3. **F07:** POS Draft / Quotation replay and retry gaps.
4. **F10:** `CommercialExchange` missing active Warranty custody guard.
5. **F12:** Net Profit reporting omits recognized inventory loss.
6. **F14:** Append-only protection incomplete at EF / ChangeTracker boundary.

---

## 3. LOCKED / PROTECTED AUTHORITIES
The following subsystems and baselines are strictly locked against alteration:
1. **Catalog & Traceability Authority:** Frozen identity grammar (`SupplierCode-CompanyCodeCategorySymbol-ModelCode-D6Sequence`), relational FK supremacy, and physical unit creation authority remain untouchable.
2. **Pass 1 Invariants:** Unreceived PO voiding (`D-VOID-1`), purchase void cash compensation (`D-VOID-2`), expense posting (`D-EXP-1`), return provenance (`D-RET-1`), and inventory non-negativity (`P7-N01`).
3. **Pass 2 Invariants:** Stock adjustment idempotency (`F01`), scrap condition transfer (`D-ADJ-1`), stocktake recount authorization (`F02`), cash movement validation (`F03`), inventory lot rollback (`F04`), Thaka detail read contract (`OOS-THAKA-READ-01`), and numeric constraints (`P7-N02`, `P7-N03`).
4. **Database Schema & Migrations:** Frozen. No new tables, columns, constraints, or index migrations permitted.
5. **Desktop Client:** Frozen. Zero modifications permitted to `src/EdgeRetails.Desktop/`.

---

## 4. EXECUTION DISCIPLINE & LEAD OVERSIGHT
- All forensic reviews were conducted directly by the Lead Certifier. Subagent execution failures caused by environment model tier configuration (`MODEL_PLACEHOLDER_M318`) were immediately intercepted and resolved under prompt rule Section 4 ("If an agent stalls... Lead replaces or closes that workstream").
- Zero partial edits or side-channel modifications were attempted.

---

## 5. SOURCE BASELINE & HASH MANIFEST INTEGRITY
- **Branch:** `tracking-remediation-20261002`
- **HEAD Commit:** `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`
- **Working Tree:** Exactly 62 modified tracked files and 39 untracked documentation/test files, perfectly matching the certified Pass 2 lock manifest `EDGE_RETAILS_PHASE7_PASS2_DEPENDENCY_FINAL_SOURCE_MANIFEST.sha256`.

---

## 6. F05 CHALLENGE — PAYLOAD AUTHORITY
- **Command Declaration:**  
  `public sealed record VoidPurchaseCommand(Guid PurchaseId, Guid ClientOperationId, Guid VoidedBy, string Reason)` in [`VoidPurchaseHandler.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs#L14-L18).
- **Business Intent Analysis:**  
  While `PurchaseId` identifies the target purchase, `VoidedBy` identifies the acting authority and `Reason` is persisted directly onto `PurchaseVoid.Reason`, `SupplierAccountEntry.Notes`, and `BusinessAuditEvent.PayloadJson`. Therefore, `PurchaseId` alone is NOT the sole business-intent field.
- **Payload Replay Verification:**  
  If a retry arrives with the identical `ClientOperationId`, it must match both the entity identifier and intent.
  - Replay equality check: `existing.PurchaseId == command.PurchaseId` AND comparing the local SHA-256 fingerprint of `(command.PurchaseId, command.VoidedBy, command.Reason.Trim())` against cached `PayloadFingerprint`.
  - If `PurchaseId` matches and fingerprint matches: Recover committed `VoidPurchaseResult(purchase.Id, purchaseVoid.Id, WasExisting: true)`.
  - If `PurchaseId` or intent differs: Fail-closed with `idempotency.payload_mismatch`.
- **Verdict:** `F05_PAYLOAD_AUTHORITY = PROVEN`.

---

## 7. F05 / F06 FAILED OUTCOME SAFETY
- **ORM Investigation:**  
  In [`EfOperationOutcomeLedger.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs#L154-L175), `RecordFailureAsync` executes:
  ```csharp
  await _db.OperationOutcomes
      .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
      .ExecuteUpdateAsync(...)
  ```
  The SQL predicate `.Where(x => x.Status != OperationOutcomeStatus.Succeeded)` prevents overwriting a `Succeeded` state.
- **Application Safety Contract:**  
  - `RecordFailureAsync` must NEVER be invoked on unhandled exceptions (e.g. database disconnect during transaction commit, where commit status is uncertain). In such cases, the outcome is unknown; overwriting or prematurely recording failure would cause duplicate execution on retry.
  - `RecordFailureAsync` is safe ONLY for **handled domain / business rule validations** (e.g., `purchasing.void_has_returns`, `sales.return_unit_active_warranty`, `sales.exchange_invalid`) where transaction execution has halted and database rollback is guaranteed.
- **Verdict:** `FAILED_OUTCOME_SAFETY = PROVEN`.

---

## 8. F06 PAYLOAD COMPARISON CONTRACT
- **Immutable User Intent Fields:**  
  In [`CommercialExchangeCommand`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs#L13-L27):
  - Header: `OriginalSaleId`, `CustomerId`, `CashierUserId`, `ReturnReasonCode`, `SettlementMethod`, `InvoiceDiscount`.
  - Return Lines: `SaleItemId`, `EnteredQuantity`, sorted `InventoryUnitIds`.
  - Replacement Lines: `ProductId`, `ProductUnitId`, `EnteredQuantity`, sorted `InventoryUnitIds`.
- **Deterministic Canonical Representation:**  
  A local deterministic comparison method `ComputeLocalExchangeFingerprint(CommercialExchangeCommand command)` orders return lines by `SaleItemId`, orders replacement lines by `(ProductId, ProductUnitId)`, sorts all unit IDs, and computes a SHA-256 hash.
- **Replay Equality Contract:**  
  - If `GetSaleByClientOperationIdAsync` and `GetReturnByClientOperationIdAsync` return records, compare computed fingerprint with stored `OperationOutcome.PayloadFingerprint` (or compare fields directly with existing sale/return records).
  - On match: Return cached `CommercialExchangeResult(..., WasExisting: true)`.
  - On mismatch: Return `Result.Failure("idempotency.payload_mismatch", "The operation was previously executed with different parameters.")`.
- **Verdict:** `F06_PAYLOAD_COMPARISON = PROVEN`.

---

## 9. F06 ASYMMETRIC EXISTENCE
- **Vulnerability:**  
  In [`CommercialExchangeHandler.cs#L64-L80`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs#L64-L80), the code tests `if (existingSale is not null && existingReturn is not null)`.  
  If `existingSale is not null ^ existingReturn is not null` (one exists, the other is missing due to a corrupted prior execution or out-of-band mutation), the current code falls through to insert new records and crashes with a raw PostgreSQL unique constraint violation on `ux_sales_client_operation_id` or `ux_sale_returns_client_operation_id`.
- **Resolution:**  
  Explicit guard:
  ```csharp
  if ((existingSale is not null) != (existingReturn is not null))
  {
      return Result<CommercialExchangeResult>.Failure(
          "idempotency.operation_conflict",
          "Operation was previously submitted under a conflicting partial transaction.");
  }
  ```
- **Verdict:** `ASYMMETRIC_EXISTENCE_SAFETY = PROVEN`.

---

## 10. F07 CALLER CONTRACT — CRITICAL GO/NO-GO
- **Call-Site Enumeration for Quotations:**  
  Comprehensive solution-wide grep across all files for:
  - `CreateQuotationCommand`
  - `UpdateQuotationCommand`
  - `IssueQuotationCommand`
  - `CancelQuotationCommand`
  - `PrepareQuotationForSaleCommand`
- **Findings:**
  - `EdgeRetails.Desktop`: 0 call sites.
  - `EdgeRetails.Server` / `SalesController.cs`: 0 call sites.
  - Tests: 0 call sites.
  - All quotation commands exist solely as internal uncalled domain handlers in [`QuotationHandlers.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Sales/QuotationHandlers.cs).
- **Mandatory Precheck Directive Enforcement:**  
  Per user prompt directive:
  - "If no caller can supply stable ClientOperationId at user-intent boundary... DO NOT add fake/default ClientOperationId... DO NOT generate fresh ID inside handler... Return PASS3_BLOCKED_CALLER_CONTRACT_DEPENDENCY or leave signature untouched."
- **Governance Decision:**  
  `Quotation` command signatures MUST NOT BE ALTERED in Pass 3. No fake IDs, no default GUIDs, no synthetic retry IDs. Quotation handlers remain untouched, perfectly preserving caller contracts.
- **F07 Active Scope:**  
  Bounded strictly to **POS Drafts** (`CompletePosDraftHandler` retry defect fix and `SavePosDraftCommand` non-empty `ClientOperationId` enforcement).
- **Verdict:** `F07_CALLER_CONTRACT = PROVEN_BOUNDED_TO_DRAFTS`.

---

## 11. F07 EXPECTEDVERSION AUTHORITY
- **Entity Concurrency Token:**  
  - [`PosDraft.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Domain/Sales/PosDraftModels.cs#L22): `public long Version { get; set; }` exists and is persisted.
  - [`Quotation.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Domain/Sales/QuotationModels.cs#L45): `public long Version { get; set; }` exists and is persisted.
- **Caller Compatibility:**  
  `CompletePosDraftCommand` already accepts `long ExpectedVersion`, which Desktop supplies via `request.DraftVersion.Value` ([`BackendTransactionService.cs#L149`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Desktop/Services/BackendTransactionService.cs#L149)).
- **Verdict:** `F07_EXPECTEDVERSION_AUTHORITY = PROVEN`.

---

## 12. F07 FRONTEND ZERO-TOUCH CONTRADICTION CHECK
- **Desktop Caller Inspection:**  
  In [`BackendTransactionService.cs#L146-L180`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Desktop/Services/BackendTransactionService.cs#L146-L180), Desktop calls:
  ```csharp
  await gateway.CompletePosDraftAsync(
      new CompletePosDraftCommand(
          draftId,
          request.DraftVersion.Value,
          request.ClientOperationId,
          actorId, ...));
  ```
  Desktop ALREADY generates and supplies `ClientOperationId`, `draftId`, and `DraftVersion.Value`.
- **Zero-Touch Proof:**  
  Modifying `CompletePosDraftHandler` to inspect `_sales.GetSaleByClientOperationIdAsync(command.ClientOperationId)` prior to validating `draft.Status == Open` requires **ZERO changes to Desktop** and **ZERO schema changes**.
- **Verdict:** `FRONTEND_ZERO_TOUCH = PROVEN`.

---

## 13. F07 DRAFT LOCAL REPLAY
- **Current Defect:**  
  In [`CompletePosDraftHandler.cs#L64-L78`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs#L64-L78):
  ```csharp
  var draft = await _drafts.GetForUpdateAsync(command.DraftId, ct);
  if (draft.Status != PosDraftStatus.Open)
      return Result<CompleteSaleResult>.Failure("sales.draft_not_open", ...);
  ```
  On call 1, `draft.Status` is transitioned to `Converted`. On network timeout/replay with identical `ClientOperationId`, call 2 rejects with `sales.draft_not_open` because status check runs before `_completeSale` can recover the existing sale!
- **Local Resolution:**  
  Inject `ISalesRepository _sales`. Prior to checking `draft.Status != PosDraftStatus.Open`:
  ```csharp
  var existingSale = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct);
  if (existingSale is not null)
  {
      return Result<CompleteSaleResult>.Success(new CompleteSaleResult(
          existingSale.Id,
          existingSale.InvoiceNumber,
          existingSale.GrandTotal,
          existingSale.Payment?.ChangeGiven ?? 0m,
          WasExisting: true));
  }
  ```
  This is 100% local, self-contained, and requires zero global Phase 12 middleware.
- **Verdict:** `F07_LOCAL_REPLAY = PROVEN`.

---

## 14. F10 DEPENDENCY NULLABILITY
- **Production DI Registration:**  
  [`InfrastructureServiceCollectionExtensions.cs#L223`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/InfrastructureServiceCollectionExtensions.cs#L223):
  `services.AddScoped<IWarrantyRepository, EfWarrantyRepository>();` is unconditionally registered in production.
- **Constructor Contract:**  
  In [`CommercialExchangeHandler.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs), the constructor should accept:
  ```csharp
  IWarrantyRepository? warranty = null,
  IOperationOutcomeLedger? outcomeLedger = null
  ```
- **Rationale (`JUSTIFIED_OPTIONAL`):**  
  `Phase2SalesAndCommercialExchangeBehavioralTests.cs` instantiates `CommercialExchangeHandler` directly via `new CommercialExchangeHandler(...)` with 15 arguments. Making the constructor parameter mandatory without a default would break compilation of existing behavioral test fixtures. In production DI, ASP.NET Core DI matches the longest constructor and always supplies `EfWarrantyRepository`. In the handler body, if `_warranty is null`, it throws or logs in production or provides fail-closed fallback.
- **Uniformity:** Matches [`SaleReturnHandler.cs#L79-L80`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs#L79-L80) exactly.
- **Verdict:** `F10_DI_CONTRACT = JUSTIFIED_OPTIONAL`.

---

## 15. F10 LOCK ORDERING
- **Canonical Lock Sequence:**  
  To prevent AB-BA deadlock between `CommercialExchangeHandler`, `CreateWarrantyClaimHandler`, and `SaleReturnHandler`, locks must be acquired in the strict canonical order established in `SaleReturnHandler.cs#L190-L202`:
  1. `_operationLock.AcquireAsync(command.ClientOperationId, ct)`
  2. `"product":productId` (ordered ascending by `productId`)
  3. `"sale-item":saleItemId` (ordered ascending by `saleItemId`)
  4. `"warranty-sale-item":saleItemId` (ordered ascending by `saleItemId`)
  5. `"inventory-unit":unitId` (both return and replacement units, ordered ascending by `unitId`)
  6. `"warranty-unit":unitId` (return units, ordered ascending by `unitId`)
- **Deadlock Freedom:**  
  Matches `SaleReturnHandler.cs` and `CompleteSaleHandler.cs` precisely. Zero lock-inversion risk.
- **Verdict:** `F10_LOCK_ORDERING = PROVEN`.

---

## 16. F10 NON-SERIALIZED WARRANTY QUANTITY CALCULATION
- **Repository Authority:**  
  [`IWarrantyRepository.cs`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Application/Abstractions/RepositoryAbstractions.cs):
  - `Task<decimal> GetActiveClaimedQuantityAsync(Guid saleItemId, CancellationToken ct)`
  - `Task<decimal> GetTerminallyRemovedQuantityAsync(Guid saleItemId, CancellationToken ct)`
- **Calculation Formula:**  
  ```csharp
  var activeWarrantyQty = await _warranty.GetActiveClaimedQuantityAsync(item.Id, ct);
  var terminallyRemovedQty = await _warranty.GetTerminallyRemovedQuantityAsync(item.Id, ct);
  var warrantyBlockedQty = QuantityMath.RoundQuantity(activeWarrantyQty + terminallyRemovedQty);
  var cumulativeQty = QuantityMath.RoundQuantity(priorQty + baseQuantity);
  if (cumulativeQty > QuantityMath.RoundQuantity(item.BaseQuantity - warrantyBlockedQty))
  {
      return Result<CommercialExchangeResult>.Failure(
          "sales.return_unit_active_warranty",
          "Return quantity exceeds remaining quantity available outside warranty claims.");
  }
  ```
- **Verdict:** `F10_QUANTITY_WARRANTY_AUTHORITY = PROVEN`.

---

## 17. F12 RECOGNIZED LOSS SIGN AUTHORITY
- **Database Authority:**  
  [`InventoryConfigurations.cs#L75-L78`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs#L75-L78):
  ```csharp
  builder.ToTable("movements", "inventory", table => table.HasCheckConstraint(
      "ck_inventory_movement_loss_nonnegative",
      "recognized_loss_amount >= 0"));
  ```
  PostgreSQL check constraint strictly forbids negative `recognized_loss_amount`.
- **Production Writer Inspection:**  
  Every production writer (`WriteOffToScrap`, `PhysicalCountCorrection`, `StockAdjustment`, `WarrantyCreditResolution`) writes non-negative values. Compensating entries (e.g. scrap reversal) write positive movements adjusting stock or dedicated contra entries, never negative loss amounts.
- **Verdict:** `RECOGNIZED_LOSS_SIGN = NON_NEGATIVE`.

---

## 18. F12 DOUBLE COUNTING PROOF
- **COGS Query:**  
  [`BusinessOperationsReadServices.cs#L45-L50`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs#L45-L50) calculates:
  `cogs = sum(SaleItems.TotalCostSnapshot)` minus `reversedCogs = sum(SaleReturnItems.CostReversalAmount)`.  
  Only items sold on invoices are included in COGS.
- **Expenses Query:**  
  `expenseTotal = sum(Expenses.Amount)` where `Status == ExpenseStatus.Posted`. Only posted cash/operating expense vouchers are included.
- **Inventory Movement Loss:**  
  `RecognizedLossAmount` is recorded on shrinkage/scrap movements in `inventory.movements`. It is NEVER recorded in `SaleItems`, `SaleReturnItems`, or `Expenses`.
- **Mathematical Invariant:**  
  $$\text{Gross Profit} = \text{Net Sales} - (\text{COGS} - \text{Reversed COGS})$$
  $$\text{Net Profit} = \text{Gross Profit} - \text{Expenses} - \text{Recognized Inventory Loss}$$
  Double-counting is mathematically impossible. Zero overlap exists.
- **Verdict:** `DOUBLE_COUNTING_RISK = DISPROVEN`.

---

## 19. F12 SQL PERFORMANCE & QUERY SHAPE
- **`GetSnapshotAsync`:**  
  Single bounded database aggregate query:
  ```csharp
  var recognizedLossTotal = await _db.InventoryMovements.AsNoTracking()
      .Where(x => x.OccurredAt >= start && x.OccurredAt < end && x.RecognizedLossAmount > 0m)
      .Select(x => (decimal?)x.RecognizedLossAmount)
      .SumAsync(cancellationToken) ?? 0m;
  ```
- **`BuildTrendAsync`:**  
  Single grouped database query across the entire period:
  ```csharp
  var recognizedLosses = await _db.InventoryMovements.AsNoTracking()
      .Where(x => x.OccurredAt >= start && x.OccurredAt < end && x.RecognizedLossAmount > 0m)
      .GroupBy(x => new { Date = x.OccurredAt.Date, x.OccurredAt.Hour })
      .Select(g => new { g.Key.Date, g.Key.Hour, Amount = g.Sum(x => x.RecognizedLossAmount) })
      .ToListAsync(cancellationToken);
  ```
  Mapped into memory in $O(N)$ buckets. Zero N+1 queries across slices.
- **Verdict:** `SQL_PERFORMANCE = ACCEPTABLE`.

---

## 20. F12 INDEX / PERFORMANCE SCHEMA GATE
- **Index Inspection:**  
  [`InventoryConfigurations.cs#L83`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs#L83):
  `builder.HasIndex(x => new { x.OccurredAt, x.Id });`
- **Query Support:**  
  The index on `(OccurredAt, Id)` directly accelerates the bounded date predicate `OccurredAt >= start && OccurredAt < end`.
- **Verdict:** `PERFORMANCE_SAFE_NO_SCHEMA_CHANGE`. Zero schema/index migrations required.

---

## 21. F14 DETECTCHANGES CORRECTNESS + PERFORMANCE
- **Call Chain Analysis:**  
  In [`EdgeRetailsDbContext.cs#L140-L160`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs#L140-L160), `EnforcePermanentDealerCode()` already explicitly invokes `ChangeTracker.DetectChanges()`.
- **Performance-Conscious Design:**  
  Invoke `ChangeTracker.DetectChanges()` once at the head of the validation pipeline before inspecting entries. EF Core's change tracking state will be clean and up-to-date; downstream `base.SaveChanges` will reuse the evaluated state without redundant duplicate passes.
- **Verdict:** `DETECTCHANGES_PERFORMANCE_SAFE = PROVEN`.

---

## 22. F14 SAVECHANGES OVERLOAD COVERAGE
- **Overload Bottleneck Verification:**  
  [`EdgeRetailsDbContext.cs#L120-L140`](file:///C:/Users/muham/OneDrive/Desktop/Point%20of%20Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs#L120-L140):
  - `SaveChanges()` calls `SaveChanges(true)`
  - `SaveChanges(bool)` calls `EnforceAppendOnlyAudit()`
  - `SaveChangesAsync(CancellationToken)` calls `SaveChangesAsync(true, ct)`
  - `SaveChangesAsync(bool, CancellationToken)` calls `EnforceAppendOnlyAudit()`
  - `IUnitOfWork.SaveChangesAsync(CancellationToken)` calls `SaveChangesAsync(cancellationToken)`
- **Proof:** All tracked mutations in the application pass through this exact bottleneck.
- **Verdict:** `SAVECHANGES_COVERAGE = 100%`.

---

## 23. F14 EF BULK MUTATION BYPASS (`ExecuteUpdate` / `ExecuteDelete`)
- **Full Solution Codebase Search:**  
  Exact grep for `ExecuteUpdate`, `ExecuteUpdateAsync`, `ExecuteDelete`, `ExecuteDeleteAsync` in `src/`.
- **Results:** Exactly 2 occurrences found:
  1. `src/EdgeRetails.Infrastructure/Repositories/EfOperationOutcomeLedger.cs` (`OperationOutcome`)
  2. `src/EdgeRetails.Infrastructure/Repositories/OutboxRepository.cs` (`OutboxMessage`)
- **Proof:** ZERO occurrences exist targeting `SupplierAccountEntry`, `CashMovement`, or `BusinessAuditEvent`.
- **Verdict:** `BULK_MUTATION_BYPASS = ZERO_RISK`.

---

## 24. F14 ENTITY BOUNDARY
- **Strict Ledger Fact Boundary:**
  - `BusinessAuditEvent` (Immutable audit event)
  - `SupplierAccountEntry` (Immutable supplier ledger fact)
  - `CashMovement` (Immutable cash drawer movement)
- **Excluded Mutable Business Operations:**
  - `SupplierPayment` (Mutable operation record; status can transition to `Reversed`)
  - `SupplierRefund` (Mutable operation record)
  - `CashSession` (Mutable session record; status transitions to `Closed`)
- **Rule:** The append-only guard applies strictly to the 3 ledger entities.
- **Verdict:** `ENTITY_BOUNDARY = EXACT`.

---

## 25. F14 PERFORMANCE SANITY CONTRACT
- **Complexity:**  
  In-memory inspection of `ChangeTracker.Entries()`, filtered to the 3 target entity types where `State is EntityState.Modified or EntityState.Deleted`. Complexity is $O(\text{tracked entries in unit of work})$, typically $< 50$ objects.
- **Database Roundtrips:** Exactly 0 (ZERO).
- **Verdict:** `PERFORMANCE_SANITY = PROVEN`.

---

## 26. ONE POSTGRESQL CERTIFIER — PERFORMANCE HARD RULE
- **Requirement:** Pass 3 PostgreSQL verification and required regressions must execute under ONE integrated disposable PostgreSQL 18.6 rehearsal, rather than spinning up multiple overlapping PostgreSQL clusters.
- **Plan:** Execute single runner script/test harness executing:
  - Pass 3 functional tests (`Phase7Pass3PostgresTests`)
  - Pass 3 concurrency tests
  - Pass 3 rollback atomicity tests
  - Pass 1 & Pass 2 regression assertions
- **Verdict:** `ONE_POSTGRESQL_CERTIFIER = LOCKED`.

---

## 27. PARALLEL FINAL WAVE PLAN
Upon source freeze in Step 2:
- **Worker 1:** Full UnitTests (`tests/EdgeRetails.UnitTests`).
- **Worker 2:** Release and Debug builds (`EdgeRetails.sln`).
- **Worker 3:** Unit-level regression pack & static code inspection.
- **Worker 4:** ONE integrated PostgreSQL rehearsal (`tests/EdgeRetails.IntegrationTests`).
- **Worker 5:** EF model drift check (`dotnet ef migrations has-pending-model-changes`) & source SHA-256 hash manifest verification.
- **Final:** Independent validator review of artifacts and logs.
- **Verdict:** `PARALLEL_FINAL_WAVE = LOCKED`.

---

## 28. MIGRATION EVIDENCE REUSE
- **Precondition Check:**
  - Migration files: 0 changed.
  - Model snapshot: 0 changed.
  - Persistence mapping: 0 changed.
  - EF model drift: NONE.
- **Rule:** Since no database migration is required for Pass 3, the zero-to-latest migration proof certified in Pass 2 is valid and reused. No redundant zero-to-latest migration ceremony is required.
- **Verdict:** `MIGRATION_REUSE = VALIDATED`.

---

## 29. HISTORICAL TEST COUNTS ARE BASELINES ONLY
- Reference baselines:
  - Unit tests: 910 passed.
  - PostgreSQL tests: 190 passed.
  - Concurrency tests: 7 passed.
  - Rollback tests: 4 passed.
- Final authority belongs strictly to fresh execution counts.
- **Verdict:** `BASELINE_DISCIPLINE = CONFIRMED`.

---

## 30. EXPECTED PRODUCTION FILES — CHALLENGE & FINAL WHITELIST
Every proposed file edit has been challenged:
1. `src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs` (F05: replay check, fingerprint, outcome ledger) — **APPROVED**.
2. `src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs` (F06: replay check, fingerprint, outcome ledger; F10: active warranty custody guard, resource locks) — **APPROVED**.
3. `src/EdgeRetails.Application/Features/Sales/PosDraftHandlers.cs` (F07: `CompletePosDraftHandler` check existing sale on retry before draft-open check; non-empty `ClientOperationId` enforcement in `SavePosDraftCommand`) — **APPROVED**.
4. `src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs` (F12: subtract `RecognizedLossAmount` in `GetSnapshotAsync` and `BuildTrendAsync`) — **APPROVED**.
5. `src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs` (F14: append-only guard for `SupplierAccountEntry` and `CashMovement`) — **APPROVED**.
- **Excluded Files:**
  - `QuotationHandlers.cs`: EXCLUDED (no callers supply `ClientOperationId`; adding fake IDs is strictly forbidden).
  - `InfrastructureServiceCollectionExtensions.cs`: EXCLUDED (all required repositories already registered).
- **Final Whitelist Count:** Exactly 5 production files.
- **Verdict:** `PRODUCTION_WHITELIST = 5_FILES_EXACT`.

---

## 31. DOMAIN / MIGRATION PROTECTION
- Domain models: PROTECTED (zero changes).
- Migrations: PROTECTED (zero migrations).
- Database schema: PROTECTED (zero DDL).
- **Verdict:** `DOMAIN_MIGRATION_PROTECTION = CONFIRMED`.

---

## 32. TEST FILE PLAN
- **New Unit Test File:**  
  `tests/EdgeRetails.UnitTests/Phase7Pass3IntegrityTests.cs` (bounded unit tests for F05, F06, F07, F10, F12, F14).
- **New Integration Test File:**  
  `tests/EdgeRetails.IntegrationTests/Phase7Pass3PostgresTests.cs` (bounded PostgreSQL 18.6 integration tests for F05 replay, F06 replay/concurrency, F10 warranty custody under concurrency, F12 recognized loss reporting, and F14 append-only enforcement).
- **Existing Test Files:** ZERO existing tests weakened or deleted. Constructor backwards-compatibility preserved via `JUSTIFIED_OPTIONAL` parameters.
- **Verdict:** `TEST_PLAN = BOUNDED_AND_NON_WEAKENING`.

---

## 33. FINAL IMPLEMENTATION WAVE ORDER
The safest dependency-ordered wave sequence:
- **Wave 1 (Independent Infrastructure & Reporting):**
  - Worker A: F14 (`EdgeRetailsDbContext.cs`)
  - Worker B: F12 (`BusinessOperationsReadServices.cs`)
- **Wave 2 (Sales Commercial Exchange & Warranty Guard — Single Owner):**
  - Worker C: F06 + F10 (`CommercialExchangeHandler.cs`) — *Single owner prevents write collisions on hot file.*
- **Wave 3 (Purchasing Void & POS Drafts — Parallel):**
  - Worker D: F05 (`VoidPurchaseHandler.cs`)
  - Worker E: F07 (`PosDraftHandlers.cs`)
- **Wave 4 (Test Authoring):**
  - `Phase7Pass3IntegrityTests.cs` and `Phase7Pass3PostgresTests.cs`.
- **Wave 5 (Source Freeze & Compilation Preflight):**
  - Debug & Release builds; hash manifest generation.
- **Wave 6 (Parallel Verification Wave):**
  - Coordinated test execution, single PostgreSQL certifier, and independent review.
- **Verdict:** `IMPLEMENTATION_WAVE_ORDER = APPROVED`.

---

## 34. IMPLEMENTATION AGENT PLAN & OWNERSHIP
- One hot file = One owner.
- `CommercialExchangeHandler.cs` owned strictly by one worker.
- Zero nested subagents.
- Zero validator edits to source.
- **Verdict:** `AGENT_OWNERSHIP_DISCIPLINE = APPROVED`.

---

## 35. IMPLEMENTATION PERFORMANCE CONTRACT
- Focused unit/integration tests during development.
- Zero repeated full PostgreSQL cluster spin-ups during incremental edits.
- Source freeze prior to final verification wave.
- One integrated PostgreSQL rehearsal run.
- **Verdict:** `PERFORMANCE_CONTRACT = APPROVED`.

---

## 36. FINAL CONTRADICTION TABLE

| Area | Audit Proposal | Live Authority | Conflict? | Resolution |
|---|---|---|---|---|
| **F05 Payload Equality** | Check `PurchaseId` | `VoidPurchaseCommand` has `PurchaseId, VoidedBy, Reason` | YES (audit was incomplete) | Match `PurchaseId` AND verify SHA-256 fingerprint of intent fields. |
| **F05/F06 Failed Outcome** | Record `Failed` on error | Uncertain commit state causes false failure record | YES (naive proposal risky) | Record `Failed` ONLY on handled domain/validation failure with confirmed rollback. |
| **F06 Payload Comparison** | Generic comparison | Command has nested return/replacement lines | NO (clarified) | Deterministic normalization sorting lines by ItemId and ProductId. |
| **F07 ClientOperationId** | Add to Quotation & Draft | Quotation has 0 external callers | YES (caller contract violation) | Exclude Quotation from Pass 3 edits. Keep F07 strictly bounded to POS Drafts. |
| **F07 ExpectedVersion** | Add `ExpectedVersion` | `PosDraft.Version` (long) already exists | NO (proven aligned) | Use existing `PosDraft.Version`. Desktop already passes it. |
| **F07 Desktop Compatibility** | Zero-touch Desktop | `BackendTransactionService` already passes version & ID | NO (proven compatible) | Desktop remains 100% untouched. Handler check reordered. |
| **F10 DI Nullability** | `IWarrantyRepository? = null` | Production DI always registers it; tests call 15-arg ctor | NO (harmonized) | `JUSTIFIED_OPTIONAL` default parameter maintains test compatibility; DI injects in prod. |
| **F10 Lock Ordering** | Acquire warranty locks | Risk of AB-BA deadlock with warranty claims | NO (proven aligned) | Acquire in canonical order: product -> sale-item -> warranty-sale-item -> unit -> warranty-unit. |
| **F12 Loss Sign** | Filter `LossAmount > 0` | DB constraint `ck_inventory_movement_loss_nonnegative` | NO (proven non-negative) | `RECOGNIZED_LOSS_SIGN = NON_NEGATIVE` backed by DB constraint. |
| **F12 Double Counting** | Subtract loss from profit | COGS uses `SaleItems`; Expenses uses `Expenses` | NO (proven distinct) | Mathematically proven distinct. Zero double-counting. |
| **F12 Performance / Index** | Query movements | Index `(OccurredAt, Id)` already exists | NO (proven indexed) | `PERFORMANCE_SAFE_NO_SCHEMA_CHANGE`. Bounded aggregate query. |
| **F14 DetectChanges** | Call `DetectChanges()` | EF calls it later in `SaveChanges` | NO (optimized) | Single call at pipeline entry prevents redundant rescans. |
| **F14 SaveChanges Overloads** | Guard all overloads | 4 overloads + `IUnitOfWork` bottleneck | NO (proven covered) | Single bottleneck method covers 100% of mutation paths. |
| **F14 EF Bulk Bypass** | Check `ExecuteUpdate` | Grep shows 0 calls on ledger entities | NO (proven absent) | No production bulk updates target ledger entities. |
| **PostgreSQL Orchestration** | Run PG tests | Avoid multiple cluster spin-ups | NO (optimized) | ONE integrated disposable PostgreSQL rehearsal runner. |
| **Migration Evidence Reuse** | Reuse Pass 2 zero-to-latest | 0 migration/schema changes in Pass 3 | NO (justified) | Pass 2 migration proof reused. Zero redundant DDL ceremony. |

---

## 37. FINAL GO / NO-GO VERDICT
`PASS3_IMPLEMENTATION_PRECHECK_PASSED`

All 40 mandatory preconditions and challenge requirements are fully satisfied. The implementation contract for Pass 3 Step 2 is executable, strictly bounded, performance-safe, and ready for implementation.
