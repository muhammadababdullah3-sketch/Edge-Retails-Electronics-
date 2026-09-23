# Agent A — Recovery / State / Migration Audit Report (Phase 5)

## 1. Files Inspected
- `src/EdgeRetails.Infrastructure/Persistence/Migrations/20260923095632_Phase5WarrantyClaimClientOperationId.cs` & `.Designer.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Migrations/20260923110943_Phase5WarrantyLifecycleIdempotency.cs` & `.Designer.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Migrations/20260923111027_Phase5MovementHistoryOrderingIndex.cs` & `.Designer.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Migrations/20260923125420_Phase5PurchaseHistoryOrderingIndex.cs` & `.Designer.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Migrations/EdgeRetailsDbContextModelSnapshot.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Configurations/WarrantyConfigurations.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Configurations/InventoryConfigurations.cs`
- `src/EdgeRetails.Infrastructure/Persistence/Configurations/PurchasingConfigurations.cs`
- `src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs`
- `src/EdgeRetails.Desktop/ViewModels/PurchaseDetailViewModel.cs`
- `src/EdgeRetails.Desktop/ViewModels/PurchaseHistoryViewModel.cs`
- `src/EdgeRetails.Desktop/ViewModels/ThakaProjectsViewModel.cs`
- `src/EdgeRetails.Desktop/ViewModels/ProductDetailViewModel.cs`
- `src/EdgeRetails.Desktop/Services/BackendPurchasingInventoryService.cs`
- `src/EdgeRetails.Desktop/Services/BackendThakaService.cs`
- `src/EdgeRetails.Desktop/Services/BackendDashboardService.cs`
- `src/EdgeRetails.Infrastructure/Services/DapperReadServices.cs` (`PurchasingReadService`)
- `src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs`
- `src/EdgeRetails.Infrastructure/Services/Phase5OperationsReadServices.cs`
- `tests/EdgeRetails.UnitTests/Sprint9Phase2DemoAuthorityTests.cs`
- `tests/EdgeRetails.UnitTests/Sprint4ForensicAuditTests.cs`
- `tests/EdgeRetails.UnitTests/Phase2PurchasingAndKhataBehavioralTests.cs`
- `tests/EdgeRetails.UnitTests/Phase2StocktakeThakaCashSessionBehavioralTests.cs`
- `tests/EdgeRetails.IntegrationTests/SalesPurchasingTransactionalPostgresTests.cs`
- `tests/EdgeRetails.PerformanceTests/Program.cs`
- `scripts/Invoke-Phase5ScalePerformanceRehearsal.ps1`
- `scripts/Verify-EfModelSync.ps1`
- `docs/Phase5_Performance_Evidence.json`

---

## 2. Migrations & Model Snapshot Verification

### Verified Phase 5 Migrations
1. **`20260923095632_Phase5WarrantyClaimClientOperationId`**:
   - Adds `client_operation_id` (uuid, non-null) to `warranty.claims` with backfill via `gen_random_uuid()` followed by dropping the SQL default.
   - Adds unique index `ix_claims_client_operation_id` on `warranty.claims(client_operation_id)`.
   - Verified present in `WarrantyClaimConfiguration.cs` and `EdgeRetailsDbContextModelSnapshot.cs` (lines 4589-4592, 4645-4648).

2. **`20260923110943_Phase5WarrantyLifecycleIdempotency`**:
   - Creates append-only operations log table `warranty.operations` (id, client_operation_id, target_type, target_id, operation_type, actor_id, payload_hash, result_id, occurred_at).
   - Creates unique index `ix_operations_client_operation_id`.
   - Creates composite index `ix_operations_target_type_target_id_operation_type_occurred_at`.
   - Verified present in `WarrantyOperationConfiguration.cs` and `EdgeRetailsDbContextModelSnapshot.cs` (lines 4827-4880).

3. **`20260923111027_Phase5MovementHistoryOrderingIndex`**:
   - Creates composite index `ix_movements_occurred_at_id` on `inventory.movements(occurred_at DESC, id DESC)`.
   - Verified present in `InventoryMovementConfiguration.cs` and `EdgeRetailsDbContextModelSnapshot.cs` (line 1539).

4. **`20260923125420_Phase5PurchaseHistoryOrderingIndex`**:
   - Creates composite index `ix_purchases_purchase_date_id` on `purchasing.purchases(purchase_date DESC, id DESC)`.
   - Verified present in `PurchaseConfiguration.cs` and `EdgeRetailsDbContextModelSnapshot.cs` (line 2407).

### Migration Drift Status: **0 MIGRATION DRIFT (100% SYNC)**
- **Forward Invariants:** All 4 migrations are strictly append-only in `Up()` methods (zero `DropTable` or `DropColumn` operations).
- **Snapshot Parity:** `EdgeRetailsDbContextModelSnapshot.cs` contains identical entity configurations to `Phase5PurchaseHistoryOrderingIndex.Designer.cs`.
- **Table Census:** Exactly 64 tables across 8 core schemas (+1 from Phase 4's 63 tables due to `warranty.operations`). 80 tables total across all 11 schemas.

---

## 3. Bounded Queries and Keyset Pagination Implementation Audit

### `PurchaseHistoryViewModel.cs` & `PurchaseDetailViewModel.cs`
- **Pattern:** Bounded 2-tier query pattern.
- **List Level (`PurchaseHistoryViewModel.cs`):**
  - Page size capped at `BackendPageSize = 200`.
  - Date filtering bounded via `ResolvePeriod` (defaults to current month: `(FromDate, ToDate)`).
  - List projection (`IPurchasingReadService.GetHistoryAsync`) queries `purchasing.purchases` with `ORDER BY p.purchase_date DESC, p.id DESC LIMIT @PageSize`, supported directly by index `ix_purchases_purchase_date_id`.
  - In `BackendPurchasingInventoryService.cs` (lines 165-179), history rows are mapped to `PurchaseRecord` with summary counts and `Items = []` (no child lines loaded).
- **Detail Level (`PurchaseDetailViewModel.cs`):**
  - When a user opens a purchase drawer, `PurchaseDetailViewModel` triggers `LoadBackendDetailAsync()`.
  - Fetches the full single-purchase aggregate on demand via `_backendService.GetPurchaseAsync(purchaseId)`.
  - Avoids N+1 queries and eliminates huge memory allocations for history lists.

### `ThakaProjectsViewModel.cs`
- **Pattern:** Keyset / Cursor pagination.
- **Page Size:** `ProjectPageSize = 200`.
- **Cursor State:** Tracks `_nextStartedOn`, `_nextProjectId`, and `_hasMore`.
- **Paging Mechanism:** Keyset predicate:
  `p.started_on < @BeforeStartedOn OR (p.started_on = @BeforeStartedOn AND p.id < @BeforeProjectId) ORDER BY p.started_on DESC, p.id DESC LIMIT @TakePlusOne`.
- **Query Aggregation Optimization:** `ThakaReadService.cs` limits expensive child table aggregations (`material_issues`, `payments`, `settlements`) to `WHERE project_id IN (SELECT project_id FROM paged_projects)`, eliminating full-table scans.
- **UI Incremental Loading:** `LoadMoreCommand` / `LoadMoreAsync()` appends newly fetched projects to `AllProjects` and `FilteredProjects`.

---

## 4. Test Suite Audit & Stale Assumptions

### Test Findings:
1. **Sprint 9 Authority Tests:** `Sprint9Phase2DemoAuthorityTests.cs` (lines 109-110) has ALREADY been updated to assert `_backendService.GetProjectsPageAsync(` and `ProjectPageSize` on `ThakaProjectsViewModel.cs`.
2. **Phase 2 Behavioral Tests:** `Phase2PurchasingAndKhataBehavioralTests.cs` and `Phase2StocktakeThakaCashSessionBehavioralTests.cs` test command handlers directly using domain fakes in `Phase2TestDoubles.cs`. No stale unbounded query assumptions exist in Phase 2 tests.
3. **Integration Tests:** `SalesPurchasingTransactionalPostgresTests.cs` (line 355) already uses bounded queries (`GetPurchaseHistoryQuery(SupplierId: ..., PageSize: 10)`).
4. **All 450 Unit Tests Pass:** Release build and test run produce 0 failures, 0 warnings.

### CRITICAL DEFECT DISCOVERED ⚠️
- **File:** `src/EdgeRetails.Desktop/ViewModels/ProductDetailViewModel.cs` (lines 331, 346-350)
- **Problem:**
  ```csharp
  var purchases = await _backendService.GetPurchasesAsync();
  ...
  Purchases.Clear();
  foreach (var purchase in purchases
      .Where(p => p.Items.Any(item => item.Product.BackendProductId == productId))
      .OrderByDescending(p => p.Date))
  {
      Purchases.Add(purchase);
  }
  ```
  In Phase 5 bounded query optimization, `BackendPurchasingInventoryService.GetPurchasesAsync` stripped line item details (`Items = []`) for performance. As a direct consequence:
  - `p.Items.Any(...)` evaluates to `false` for every purchase.
  - The `Purchases` collection in `ProductDetailViewModel` is **permanently and silently empty**!
  - Furthermore, `GetPurchasesAsync()` was called unbounded without date or product filters, pulling the first 200 arbitrary shop purchases.
- **Recommendation:** Add a dedicated product-filtered purchase history read query (`GetPurchasesForProductAsync(productId, pageSize)`) to `IBackendPurchasingInventoryService` and `IPurchasingReadService`.

### SECONDARY UNBOUNDED QUERY DEFECT DISCOVERED ⚠️
- **File:** `src/EdgeRetails.Desktop/Services/BackendDashboardService.cs` (line 85)
- **Problem:**
  ```csharp
  var projects = await _thaka.GetProjectsAsync(cancellationToken);
  activeProjects = projects.Where(project => project.IsActive)...
  activeThakaCount = activeProjects.Count;
  activeThakaOutstanding = activeProjects.Sum(project => project.Balance);
  ```
  The dashboard calls the deprecated unbounded `GetProjectsAsync()` to load all Thaka projects into memory simply to compute KPI counts and sums, despite `IThakaReadService.GetProjectsPageAsync` already producing authoritative `total_active_count` and `total_active_balance` directly in SQL.

---

## 5. Recovered State vs Pending Items

| Item | State | Notes |
|---|---|---|
| **Phase 5 Migrations (4 total)** | **RECOVERED & VERIFIED** | `Phase5WarrantyClaimClientOperationId`, `Phase5WarrantyLifecycleIdempotency`, `Phase5MovementHistoryOrderingIndex`, `Phase5PurchaseHistoryOrderingIndex` verified append-only and in 100% snapshot sync. |
| **EF Model Snapshot** | **RECOVERED & VERIFIED** | Exactly 64 core tables, 80 total tables. 0 model drift. |
| **Performance Benchmark Evidence** | **RECOVERED & VERIFIED** | `docs/Phase5_Performance_Evidence.json` captured on PostgreSQL 18.6 with 120 samples across all 15 P0/P1/P2 benchmarks meeting all required volume thresholds. |
| **ProductDetailViewModel Purchase History** | **PENDING DEFECT FIX** | Bounded query refactor stripped `Items = []`, breaking product-level purchase history display. |
| **BackendDashboardService Unbounded Read** | **PENDING REMEDIATION** | Dashboard calls `GetProjectsAsync()` instead of aggregate/paged query. |
| **ViewModel Gateway Bypass** | **PENDING REMEDIATION** | ViewModels bypass `IApplicationGateway` and call `Backend*Service` directly. |
| **Diagnostic Contracts Nomenclature** | **PENDING CLEANUP** | `Phase5DiagnosticsContracts.cs` contains roadmap nomenclature (`Phase5HealthClassification`, etc.). |
| **Server/Worker Dev Connection Strings** | **PENDING CLEANUP** | `Program.cs` in Server/Worker has fallback credentials. |
| **Loose Project Folder** | **PENDING CLEANUP** | `tests/EdgeRetails.DesktopPerformanceTests` is not in `.sln`, whereas `tests/EdgeRetails.Desktop.PerformanceTests` is. |

---

## 6. Recommendations for Main Agent & Next Steps
1. **Apply Defect Fix for `ProductDetailViewModel.cs`:** Expose `GetProductPurchasesAsync(Guid productId, int limit = 50)` on `IPurchasingReadService` and `IBackendPurchasingInventoryService` and replace line 331 in `ProductDetailViewModel.cs`.
2. **Remediate `BackendDashboardService.cs`:** Replace `_thaka.GetProjectsAsync` call with `_thaka.GetProjectsPageAsync(null, "ACTIVE", pageSize: 10)` to leverage server-side KPI totals and retrieve only top 10 active projects.
3. **Persist Handoff Artifact:** Write this content to `docs/Phase5_Agent_Handoffs/AgentA_Recovery_Handoff.md`.
