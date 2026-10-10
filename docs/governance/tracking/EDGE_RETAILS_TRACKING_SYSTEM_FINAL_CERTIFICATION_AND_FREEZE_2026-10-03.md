# EDGE RETAILS TRACKING FINAL HARDENING / CERTIFICATION REPORT

Date: 2026-10-03, Asia/Karachi. Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`.

**FINAL STATUS: TRACKING_CERTIFIED_FROZEN_LOCK_CANDIDATE**

This certifies the current uncommitted Tracking candidate and its tested consuming workflows. It does not certify the entire backend, approve production deployment/cutover, commit repository history, or reopen Program Phases 1–3. The latest user-invoked final-hardening attachment authorizes surgical corrections to proven Tracking defects; it supersedes the earlier audit-only scope for this work.

The user explicitly prohibited operational shop database/backup/export access. Legacy Unicode inventory is **NOT_RUN_ENVIRONMENT / REQUIRES_APPROVED_DATA_SOURCE**. That assessment does not block other isolated certification or freeze-candidate verification; its unknown counts are never represented as zero.

## Required result

| Field | Final evidence/result |
|---|---|
| Branch | `tracking-remediation-20261002` |
| HEAD | `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`, unchanged |
| Isolated PostgreSQL environment | AVAILABLE; owned temporary cluster only |
| PostgreSQL version | 18.6, `server_version_num=180006`; Npgsql |
| Existing 14 Tracking DB tests | 14 executed / 14 passed / 0 failed / 0 skipped / 0 NOT_RUN |
| Additional Tracking DB cases | 69 executed / 69 passed / 0 failed / 0 skipped |
| Protected sequence/custody DB cases | 12 executed / 12 passed / 0 failed / 0 skipped |
| Additional DB cases overall | 81 executed / 81 passed / 0 failed; includes hostile races, lifecycle/read contracts and migration rehearsals |
| Cross-slot IMEI race | PASS, actual PostgreSQL global unique-constraint loser |
| Normalized Serial race | PASS, actual PostgreSQL global unique-constraint loser |
| StockAdjustment concurrent replay | PASS, IndividualPiece and Container; one persisted effect/range |
| Warranty canonical replay | PASS, customer/shop × Serialized/IndividualPiece/Container, including factor-2 Container |
| First-use/SKU race | PASS, creation-first and mutation-first |
| First-use/Company.Code race | PASS, real observed DB lock wait; edit cannot commit after physical history |
| First-use/Category.IdentitySymbol race | PASS, real observed DB lock wait; edit cannot commit after physical history |
| First-use/TrackingMode race | PASS, both mutation/receipt orders; Quantity ↔ IndividualPiece; purchase and adjustment |
| Cross-workflow identity races | PASS, eight cases; exactly one owner and no loser stock/lot/claim/history leakage |
| From-zero migration rehearsal | PASS, all 21 migrations plus model-derived FK/index/check verification |
| Actual legacy upgrade rehearsal | PASS, eight pre-Tracking checkpoint scenarios, including deliberate fail-closed rejection |
| Legacy Unicode inventory | NOT_RUN_ENVIRONMENT / REQUIRES_APPROVED_DATA_SOURCE |
| Historical Warranty replay | CERTIFIED exact-old/new-canonical behavior; ambiguous old hash RECONCILIATION_REQUIRED |
| Legacy StockAdjustment replay | CERTIFIED fail-closed compatibility; missing canonical outcome RECONCILIATION_REQUIRED |
| Tracking Golden Trace | PASS, Serialized, IndividualPiece, Container factors 1 and 2 |
| Debug build | PASS, 0 warnings / 0 errors |
| Release build | PASS, 0 warnings / 0 errors |
| Full unit tests | 881 total / 881 passed / 0 failed / 0 skipped |
| Focused Tracking unit tests | 106 total / 106 passed / 0 failed / 0 skipped |
| Tracking integration tests | 83 total / 83 passed / 0 failed / 0 skipped / 0 NOT_RUN |
| Combined final PostgreSQL run | 95 total / 95 passed / 0 failed / 0 skipped |
| Actual desktop component checks | 16 vectors PASS, purchase/return counts and identity-less purchase admission; no App or DB started |
| EF pending model changes | NONE |
| git diff --check | PASS, exit 0; Git LF/CRLF conversion notices are not compiler warnings |
| Architecture drift checks | PASS, compiled assembly/CIL authority checks and executed scanner behavior |
| Independent final certifier | TRACKING_CERTIFIED |
| Remaining Critical / High / Medium / Low | 0 / 0 / 0 / 0 in the reviewed Tracking scope; unassessed shop data excluded |

The final focused suite is a subset of the full unit suite. The 12 protected sequence cases are included in the 95 DB total, not the 83 Tracking total. The original two Warranty cases were expanded into mode/factor theories; their original Serialized/factor-1 scenarios remain executed. Counts do not sum repeated historical runs or desktop component vectors into xUnit totals.

## Evidence custody and Git state

The certification baseline was the previous continuation's 871 full units, 96 focused units and 14 unexecuted Tracking PostgreSQL cases. Its report remains intact: [previous continuation report](<C:/Users/muham/OneDrive/Desktop/Point of Sale/EDGE_RETAILS_TRACKING_REMEDIATION_CONTINUATION_REPORT_2026-10-03.md>).

At task takeover, 23 tracked modifications and 6 untracked files were inherited. Before this final report, the current tree contained 52 modified tracked files, 13 untracked files, 0 staged files and 0 conflicts. Adding this authoritative report produces 14 untracked files. This includes inherited work; the tracked diff does not count new test/helper/report contents. No source was discarded or historical evidence deleted. No commit, push, stash, reset, clean, restore, merge/rebase or other-worktree edit occurred.

The candidate is bound to the branch/HEAD above **plus the working-tree source hashes**, rather than to HEAD alone. [Source manifest](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/source-manifest.csv>) records 703 source/test/project/script files, including new untracked tests/helper, excluding generated bin/obj files. The independent certifier verified all 703 hashes; the final post-review comparison also reports 703 checked, 0 mismatches.

Authoritative evidence:

- [Final PostgreSQL terminal result](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/final-pg/terminal-result.json>), [command log](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/final-pg/commands.log>), [each of the 95 test results](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/final-pg/individual-results.csv>) and [migration/checkpoint output](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/final-pg/migration-test-output.txt>).
- [Final verification commands](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/commands.json>), [Debug build](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/debug-build.log>), [Release build](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/release-build.log>), [full unit TRX](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/full-unit.trx>), [focused TRX](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/focused-tracking.trx>) and [diff check](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/verification-certified/git-diff-check.log>).
- [Desktop component execution](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/desktop-contract-check/results-final.log>). This exercises actual compiled NewPurchaseLineViewModel/PurchaseReturnLineViewModel calculations, not copied formula assertions. It does not claim interactive WPF UI certification.
- [Original 14 individual results](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/baseline-pg-repaired/individual-results.csv>), [first independent failed verdict](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/independent-certifier-first-pass.md>) and [final independent verdict](<C:/Users/muham/OneDrive/Desktop/Point of Sale/artifacts/tracking-final-20261003/independent-certifier-final.md>).

## Isolated environment and cleanup

The unchanged [owned rehearsal runner](<C:/Users/muham/OneDrive/Desktop/Point of Sale/scripts/Invoke-MasterRemediationPostgresRehearsal.ps1:1>) created its own PostgreSQL data directory below `%TEMP%\EdgeRetailsMasterPg_<GUID>`, bound to `127.0.0.1:55640`, with owned credentials and the `edge_retails_` database namespace. The harness attested server version, cluster data-directory ownership and loopback/high-port connection. ACLs restricted the temporary root to its owner and SYSTEM; secrets were not printed in evidence.

Process-only DB/design-time/high-water/custody/keyring settings selected the owned environment and were restored in `finally`. The operational ProgramData connection fallback was explicitly excluded by attestation. All schemas, legacy fixtures, sequences and corruption simulations were owned test data.

The final cluster directory was `%TEMP%\EdgeRetailsMasterPg_48c1a3484abc4be68b35d6b038933731`. The command log records that generated path. Successful cleanup requires `pg_ctl` shutdown, subsequent status exit 3/no running server, and deletion only after verifying the absolute owned temp boundary. Final `CleanupPass=true`; no operational database connection or data inventory was performed.

The final command was:

```powershell
& ./scripts/Invoke-MasterRemediationPostgresRehearsal.ps1 `
  -TestFilter 'FullyQualifiedName~EdgeRetails.IntegrationTests.Tracking|FullyQualifiedName~MasterSupplierProductSequencePostgresTests' `
  -EvidenceDirectory 'artifacts/tracking-final-20261003/final-pg'
```

## Proven gaps closed, with exact authority references

The following are observed/reproduced Tracking defects or explicit compatibility gaps. Severity describes their pre-fix consequence. Fixture/setup/compiler failures are recorded separately and are not misrepresented as product defects. No Critical defect was established in this pass.

| ID / severity | Trigger and pre-fix consequence | Surgical correction, class/function reference and regression evidence |
|---|---|---|
| T01 — Medium | Concurrent purchase/intake supplied one canonical manufacturer identity; DB rejected the loser, but a stale precheck surfaced an unhandled 23505 rather than the expected business failure. | [PhysicalUnitCreationAuthority.CreateAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:38>) normalizes the entire batch, acquires sorted canonical `inventory-identity` locks before persisted ownership checks/reservation, retaining PostgreSQL uniqueness as the ultimate authority. Eight cross-workflow races assert exactly one winner and no loser effects. |
| T02 — Medium | Deferred physical purchase with no identities was rejected before subsequent physical intake could supply them. | [CreatePurchaseHandler.PrepareLinesAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:624>) distinguishes a deferred order from a physical receipt. Actual receipt still enforces identity policy; order creation allocates no units. Purchase-versus-intake races and protected partial-receipt tests execute this path. |
| T03 — High | CompleteSale/Warranty/Thaka branches recognized Serialized only: other physical modes were rejected or quantity effects occurred without matching exact-unit custody. | [CompleteSaleHandler.PrepareLinesAsync/ConsumeSerializedAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Sales/CompleteSaleHandler.cs:562>), [Warranty physical workflows](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs:124>) and [IssueThakaMaterialHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Thaka/ThakaHandlers.cs:195>) require/transition all physical modes. Golden traces, eight canonical replacement cases and three exact Thaka issues verify DB state and identity. |
| T04 — High | A factor-2 Container could store per-base cost as whole-unit acquisition cost or remove/restore only one base unit. This understated exact cost and desynchronized stock, lots and the cost pool. | [Purchase receipt](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/CreatePurchaseHandler.cs:431>) and [physical intake](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/ReceiveProductIntakeHandler.cs:563>) pass whole physical acquisition cost. Exact sale/issue/credit/removal use immutable base quantity and acquisitionCost/baseQuantity as the allocator's per-base argument. Factor-2 cost/quantity traces, shop credit and legacy-cost refusal execute on PG. |
| T05 — Medium | Sale/purchase return required one ID per base quantity and/or restored 1m per Container, rejecting valid packs or corrupting custody. | [CreateSaleReturnHandler.PrepareSerializedUnitsAsync/RestoreSerializedUnitsAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Sales/SaleReturnHandler.cs:503>) uses immutable sold-item/link snapshots. [CreatePurchaseReturnHandler.ProcessSerializedAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs:492>) uses each selected unit's committed received quantity, not order quantity divided by all received IDs. Four sale-return and four purchase-return mode/factor traces; partial intake unit regression remains green. |
| T06 — High | Exact condition transfer changed the selected second-lot unit's status but moved FIFO quantity from a different first lot. Later exact lifecycle operations lost matching lot custody. | [ExactUnitLotTransfer.TransferAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/ExactUnitLotTransfer.cs:9>) transfers selected units' actual lots/quantities under existing locks; consumed by [InventoryConditionService](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/InventoryConditionHandlers.cs:28>) and shop Warranty replacement. Two-lot PG regression proves the unselected unit/lot remains untouched. No unit/sequence allocator was added. |
| T07 — Medium | Missing/counting Container stocktake compared physical count to base quantity; a factor-2 missing pack could not post consistently. | [RecordSerializedStocktakeHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs:403>) sums received quantities; [PostStocktakeHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs:685>) scraps the same missing identities and removes exact quantities/costs. Three missing-unit post/replay cases and four found-unit golden scans execute. |
| T08 — High | Negative Container adjustment accepted a selected physical unit with incompatible pack quantity or missing/insufficient exact lot, allowing aggregate effects without recoverable lot removal. | [CreateStockAdjustmentHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs:87>) validates immutable quantity and requires lot provenance/balance before removal. Two hostile PG fixtures fail without adjustment items, stock/cost changes or unit transition; transaction rollback verified. |
| T09 — Medium | Historical one-way Trim-based Warranty hashes cannot safely be transformed into new canonical fingerprints. Canonically equivalent spelling could yield an ambiguous replay. | [ReceiveCustomerWarrantyReplacementHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs:1435>) and [ReceiveShopStockWarrantyHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs:2197>) retain exact old-hash replay, guarantee new canonical replay, and return `warranty.replay_reconciliation_required` for unmatched hashes on the same prior target. Different operation type/target remains mismatch. Four historical regression vectors; no hash rewrite or extra unit. |
| T10 — Medium | A legacy committed StockAdjustment without an authoritative payload outcome cannot be identified safely from a new payload. | [CreateStockAdjustmentHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs:87>) reads replay/outcomes before master validation and refuses legacy movement-only replay. [TrackingAdjustmentReplayTests](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/TrackingAdjustmentReplayTests.cs:11>) proves no new unit/movement and no guessed outcome. |
| T11 — Medium | `SaveChanges(bool)`/`SaveChangesAsync(bool, ct)` bypassed the assigned DealerCode immutability guard enforced by the simpler overloads. | [EdgeRetailsDbContext.SaveChanges/SaveChangesAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:129>) routes every ORM save overload through existing audit/permanent-code guards. Both boolean-overload regressions fail forbidden mutation; assigned code cannot be cleared/reassigned. |
| T12 — High, independent challenge | Issue factor-2 Container then reverse: stock restored +2 but original reversal restored lot/cost quantity 1 at whole-pack cost, breaking subsequent immutable quantity/cost validation. | [ReverseThakaMaterialHandler.RestoreSerializedAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Thaka/ThakaReversalHandlers.cs:278>) derives per-unit base quantity from immutable issue-item quantity/links and per-base cost from the whole snapshot. Three PG reverse/replay cases assert stock, new lot, carrying value, original ID/TrackingCode and one reversal. |
| T13 — High, independent challenge | Commercial exchange accepted IndividualPiece without IDs and sold quantity while the exact unit stayed InStock; other physical exact inputs/Container returns were rejected. | [CommercialExchangeHandler.PrepareSaleLinesAsync/ConsumeSerializedSaleAsync/PrepareSerializedReturnUnitsAsync/RestoreSerializedUnitsAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs:724>) now uses all physical modes, selected pack snapshots, immutable sold quantity and exact per-base costing on both legs. Five PG cases cover Serialized, IndividualPiece, factor-2 Container and no-ID rejection/rollback; retry adds no second effect. |
| T14 — High, independent challenge | Master mutation first: old policy preparation could create physical history after mode changed to Quantity, or skip physical authority after Quantity changed to IndividualPiece. SKU-only locking proof had missed this direction. | [CatalogRepository.GetProductForUpdateAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs:29>) reloads tracked masters after FOR UPDATE. Purchase, intake and adjustment acquire/refresh this boundary before policy preparation, including nonphysical receipt. Authority additionally refuses nonphysical mode. Four mutation-first and two receipt-first PG mode races observe actual lock waits and verify final policy/unit/stock/cost state. Existing sorted resource-lock regression remains unchanged and passes. |
| T15 — Medium, independent challenge and follow-through | POS/SKU/barcode/stocktake/inventory/purchase/Thaka projections marked only Serialized exact; desktop could skip selection or require one selected ID per base unit for a Container pack. | [PosCatalogReadService](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PosCatalogReadService.cs:18>), [Phase4WorkflowReadService](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/Phase4WorkflowReadService.cs:68>), [PurchaseCatalogReadService](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PurchaseCatalogReadService.cs:18>), [InventoryOverviewReadService](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/InventoryOverviewReadService.cs:23>), Dapper purchase-detail and [ThakaReadService.GetMaterialCatalogAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/ThakaReadService.cs:410>) now classify every physical mode. Additive TrackingMode/factor metadata reaches local/remote desktop adapters; purchase/return/Thaka count Containers by entered packs. SavePosDraft requires one selected physical unit while allowing its Container base quantity >1. Four PG DTO/scanner/draft/stocktake traces plus 16 actual desktop vectors pass. |
| T16 — Medium, independent challenge | Remove a free exact unit from a free+100-cost pair: passing null exact cost removed moving average 50 from the paid unit's carrying value. | [CreateStockAdjustmentHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs:87>) passes explicit zero per-base exact cost. PG regression verifies paid unit remains InStock with cost100, then removes it successfully to zero. |

[InventoryRepository.GetPhysicalUnitBaseQuantitySnapshotAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs:391>) is a quantity/provenance reader, not a second physical identity authority. For Container it reads the current lot's immutable source movement positive effects and distinct exact-unit links, then checks whole acquisition cost against per-base lot cost × received physical quantity. It never infers pack size from mutable balances, averages ordered quantity over partial receipts, allocates a sequence or rewrites identity/cost history. Missing quantity/cost provenance fails with `inventory.physical_quantity_reconciliation_required` / `inventory.physical_cost_reconciliation_required`.

## Failure evidence and repair discipline

Intermediate failing logs/TRX remain under the evidence root. Representative reproductions:

- `hostile-pg-before-fix`: 35/39 PASS; real sale-mode and deferred-order gaps.
- `hostile-pg-next`: 47/51 PASS; Warranty mode gaps and cross-workflow uniqueness error handling.
- `lifecycle-expanded-pg`: 61/65 PASS; exact Thaka status and Container return problems.
- `pack-snapshots-compiled-pg`: 68/69 PASS; selected-unit lot transfer moved another FIFO lot.
- `exact-lot-missing-compiled-pg`: 71/72 PASS; Container missing-stocktake quantity.
- `credit-legacy-cost-before-fix`: 72/74 PASS; whole-pack credit and legacy cost reconciliation.
- `negative-pack-focused-before-fix`: 0/2 PASS; negative adjustment quantity/lot gaps.
- `independent-challenge-executed-before-fix`: 3/16 PASS; four real master-mode race failures and exchange/read classification failures, alongside fixture issues subsequently corrected.
- `reversal-free-confirm-before-fix`: 2/4 PASS; factor-2 reversal lot quantity and free-unit moving-average loss reproduced with corrected fixtures.
- `remaining-read-contracts-before-fix`: 1/3 PASS; remaining physical catalog classification mismatch.

Compiler-only failures (missing braces, wrong fixture property names, analyzer rules), nonunique test unit symbols and incorrect test expectations have distinct logs and are not counted as executed product regressions. Baseline Warranty setup was corrected to move stock to Damaged before a Damaged warranty send. The lock-order regression caused by duplicate acquisition during the final fix was repaired by removing duplicate calls, preserving its assertions. A legacy SQL text assertion was updated to require the exact all-three-modes projection; real PostgreSQL document reads independently verify it. No database constraint or substantive persisted-state assertion was weakened to obtain green.

## Final PostgreSQL inventory and hostile results

| Test class | Executed/passed | What persisted state establishes |
|---|---:|---|
| TrackingManufacturerIdentityPostgresTests | 2/2 | Direct Npgsql global IMEI cross-slot and normalized Serial races: one logical claim/owner and expected unique-constraint loser. |
| TrackingCutoverPostgresTests | 7/7 | Admitted ASCII Serial backfill matches runtime and preserves raw columns; collision/control/Format/Unicode-review/cross-slot/same-unit cases fail closed. |
| TrackingWorkflowPostgresTests | 13/13 | Claim-backed scanner with raw Serial column set NULL in owned fixture; purchase/adjustment simultaneous pair sequence allocation; eight Warranty replay cases; two concurrent identity-less adjustments; creation-first SKU freeze. |
| TrackingMasterRacePostgresTests | 9/9 | Mutation-first SKU refresh; Company/Category history lock contention; four mutation-first TrackingMode cases; two receipt-first policy freeze cases. Actual pg_stat_activity lock waits observed. |
| TrackingMigrationRehearsalPostgresTests | 9/9 | From-zero complete schema/model contract plus eight real checkpoint upgrade scenarios. |
| TrackingCrossWorkflowPostgresTests | 8/8 | Purchase↔Warranty, Purchase↔Adjustment, Warranty↔Adjustment, Purchase↔Intake × canonical Serial/cross-slot IMEI. Separate product/supplier fixtures prevent product locks masking the identity race. |
| TrackingGoldenTracePostgresTests | 22/22 | Full physical traces, pack sale/return cost, Thaka issue, purchase return, second-lot transfer, missing stocktake post/replay, credit/legacy cost refusal, negative adjustment guards, and Quantity/Length bypass of unit creation. |
| TrackingFinalChallengePostgresTests | 13/13 | Three Thaka reverse/replay cases, five exchange/reject cases, free-unit carrying-value conservation and four catalog/scanner/draft/stocktake read traces including factor-2 Container. |
| MasterSupplierProductSequencePostgresTests | 12/12 | Partial/later receipts; separate supplier pair partitioning; simultaneous ranges; response-loss replay across disposed scopes; same-operation replay; rollback without reuse; void/next receipt; foreign product-unit refusal; actual lower database restore and corrupt custody fail-closed. |
| **Total** | **95/95** | **0 failed, 0 skipped/NOT_RUN** |

The direct races establish DB uniqueness, while cross-workflow tests establish the handlers' error/rollback behavior. Concurrent replay tests assert stored unit/movement/outcome/range counts, not only returned success. The protected sequence tests inject an owned commit failure after reservation and restore a lower database while retaining the machine high-water fixture; the retry advances rather than reuses the reserved sequence, or fails closed on corrupted custody. Sequence gaps remain permitted.

## Migration history and legacy cutover

Full from-zero PostgreSQL execution applied, in order:

```text
20260920094824_InitialProductionBaseline
20260920111318_Sprint7Phase1SetupIdentity
20260920164958_Sprint7ProductionCutover
20260921101001_Sprint8CanonicalReportingSchema
20260921143542_Sprint8FinalProductionAlignment
20260921152602_Sprint8WarrantyAlignment
20260922120000_Phase1CanonicalSchemaAlignment
20260922135055_Phase3ProductionSafetyOutbox
20260923071510_Phase4MultiTerminalSchema
20260923095632_Phase5WarrantyClaimClientOperationId
20260923110943_Phase5WarrantyLifecycleIdempotency
20260923111027_Phase5MovementHistoryOrderingIndex
20260923125420_Phase5PurchaseHistoryOrderingIndex
20260925142150_Phase1SemanticProductIdentity
20260927062206_Phase2DurableOperationOutcome
20260927121724_Phase2OutboxLeaseFencing
20260928150000_Phase3PosPriceOverride
20260929100000_Phase3LegacyCategoryUpgradeRecovery
20260930053030_Phase3COwnerPinRecoveryReplaySafety
20260930065058_Phase3COwnerPinAuthorizationConsumption
20261002101709_TrackingManufacturerIdentityAuthorityV1
```

[TrackingMigrationRehearsalPostgresTests](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/TrackingMigrationRehearsalPostgresTests.cs:19>) compares actual ordered migration history and model-derived relational FKs, indexes and check constraints, including identity claim global/slot uniqueness. Eight extra owned databases were migrated to `20260930065058_Phase3COwnerPinAuthorizationConsumption`, seeded at that real checkpoint and upgraded through the Tracking migration: `valid`, `serial-collision`, `serial-control`, `serial-format`, `serial-unicode`, `imei-invalid`, `cross-slot`, `same-unit`.

The valid fixture includes blank/ASCII identities and both IMEI slots; raw values/legacy columns are retained, claims target the original units and version1 is correct. Rejected fixtures leave Tracking unapplied, no partially committed claim table and unchanged original raw unit identities. No collision winner is selected or silently discarded. This proves actual migration rollback, beyond temporary-table SQL tests.

The existing migration ID, Designer and model snapshot were preserved. EF `has-pending-model-changes` in the attested environment reported: `No changes have been made to the model since the last migration.` Synthetic non-ASCII rejection tests establish fail-closed behavior, not absence of such data in a shop.

## Legacy data inventory: deliberately not run

| Requested operational/legacy inventory metric | Result |
|---|---|
| Legacy Serial total | UNKNOWN — NOT_RUN_ENVIRONMENT |
| ASCII-only valid Serial | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Non-ASCII Serial | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Control characters | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Format/invisible characters | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Serial length >160 | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Canonical Serial collisions | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Invalid IMEI | UNKNOWN — NOT_RUN_ENVIRONMENT |
| Cross-slot IMEI collisions | UNKNOWN — NOT_RUN_ENVIRONMENT |

No InventoryUnitId/raw-value/canonical-candidate/manual-disposition table can be produced without an approved data source. None was authorized; none was inspected. Future production cutover must obtain an approved read-only export/backup/source and resolve any rejected identities or cost provenance through the existing approved reconciliation process. Runtime Unicode admission is broader than the deliberate legacy SQL admission gate; accepted runtime Unicode values are not assumed safe for historical SQL cutover. No historical normalization/hash/identity/cost rewrite is authorized by this report.

## Golden trace and lifecycle authority

The executed full trace is: create permanent supplier → bind product/product unit → receive via canonical authority → allocate pair-scoped ItemSequence/TrackingCode → persist manufacturer claim → resolve TrackingCode first and canonical Serial through claims → sell selected exact unit → return the same unit to Damaged → send it to supplier Warranty → repair and restore the original unit → exact stocktake scan verifies original TrackingCode/custody and the original received base quantity. It executes for Serialized, IndividualPiece, Container factor1 and Container factor2.

The trace asserts ID, TrackingCode, ItemSequence, SupplierProductId, DealerCode/SKU snapshots, Purchase source, unit status, sequence cursor and aggregate stock. Separate tests certify customer/shop replacement as new identities, original history retention, shop original SupplierReturned; exact purchase return as SupplierReturned; Thaka issue/reversal as IssuedThaka/InStock; missing stocktake and negative adjustment as Scrapped. Factor-2 tests assert exact lot quantity and whole carrying value. Repair/return/issue/reverse/count do not regenerate physical identity. Quantity/Length receiving creates no InventoryUnit.

## Protected architecture and independent result

The contract remains:

1. One production physical-unit constructor and TrackingCode allocator: `PhysicalUnitCreationAuthority.CreateAsync`; no caller-owned exact-unit construction.
2. One runtime manufacturer normalization implementation: `IdentityNormalizationRules`, V1 Serial Trim→NFKC→invariant uppercase/bounds/Control+Format rejection; IMEI Unicode decimal digits→ASCII, whitespace/hyphen handling, 14/15 identity digits and checksum semantics kept separate.
3. One global logical Serial/IMEI claim namespace, unique `(IdentifierType, NormalizedValue)` and unique `(InventoryUnitId, IdentifierSlot)`; scanner uses claims and TrackingCode-first resolution.
4. SupplierProduct-scoped sequence partitioning, immutable committed snapshots/provenance, unchanged machine high-water/custody architecture. Gaps are allowed; committed/reserved identity reuse is forbidden.
5. Returns/Thaka/stocktake/repair preserve identity; Warranty replacement creates new units under that same authority. One replayed intent cannot allocate a second physical effect.
6. Identity-defining master/policy values lock at authoritative first use, with narrow product/resource/row domains. Ambiguous cutover/history fails closed.

[TrackingArchitectureDriftTests](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/TrackingArchitectureDriftTests.cs:12>) inspect compiled production assemblies/CIL, including WPF without loading it, for unit construction, TrackingCode allocation, snapshot writes and sequence-write domains. Normalizer-wrapper checks cover current Serial/IMEI normalization wrappers; they are not a formal proof against every conceivable future differently named normalizer. The PG scanner test independently disables the raw Serial column in its owned fixture and still resolves the exact unit from the claim.

The first independent read-only pass returned FAILED with 3 High/2 Medium, despite 76 then-green PG tests. Those findings were reproduced and surgically corrected; the failed verdict remains evidence. A fresh re-review followed final green verification and challenged the revised source, sequence/custody, migration, replay and lifecycle/read contracts. The certifier did not edit files, execute builds/tests/migrations, connect to any database or inspect shop data.

**Final independent verdict: TRACKING_CERTIFIED.** All five first-pass defects are closed. Source hashes and saved real PostgreSQL/build/unit evidence match. The final verdict explicitly excludes full UI acceptance and production cutover.

## Limits and prioritized remaining actions

1. **P1 — Before any production migration/cutover:** obtain an approved read-only legacy source and complete the Unicode/collision/invalid-data inventory. Assess any historical Container per-base acquisition costs or missing received-lot provenance; the candidate deliberately refuses ambiguous transitions. Counts and affected shop records are currently unknown.
2. **P1 — When an old operation needs retry:** exact old Warranty hashes remain supported; differently represented ambiguous old retries require reconciliation. Legacy adjustment movement without a canonical outcome also requires reconciliation. Never manufacture matching hashes/outcomes or allocate another physical effect by guessing.
3. **P2 — Deployment acceptance:** validate the actual target installation's native custody/ACL/service account, client/server version alignment and recovery permissions. Owned sequence restore/corruption tests passed; this is not certification of an uninspected shop machine or its backups. No deployment occurred.
4. **P2 — Scale acceptance:** measure production-like large Container batches/stocktakes and scanner latency using owned synthetic workloads. The immutable quantity reader currently performs several DB queries per selected Container; no latency/load/SLA claim is made. Current tests demonstrate integrity and locking, not exhaustive stress behavior or every possible race schedule.
5. **P2 — UI acceptance:** actual compiled purchase/return count components and PG read contracts passed, with desktop local/remote mappings inspected/compiled. Complete an interactive WPF workflow acceptance pass before rollout. Generic physical StockAdjustment UI still retains its existing exact-selection guard; this report does not redesign that screen or claim new interactive adjustment capabilities.

**Separate known consumer limitation — POS multi-piece sale UOM (Medium, outside the bounded authority freeze):** [PosCartItemViewModel constructor](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PosCartItemViewModel.cs:19>) forces each exact line to entered quantity1 and holds one InventoryUnitId. [PosViewModel.OpenExactUnitPicker](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs:742>) selects one exact unit. With a default Serialized/IndividualPiece sale UOM factor2, CompleteSale correctly requires two physical IDs for entered quantity1, so that desktop selection fails closed. This existing Serialized consumer behavior is not a corruption/bypass and was not redesigned by this Tracking task. The sixteen component vectors certify purchase/return counts, not this POS workflow. Resolve/test the consumer UOM interaction before deploying such configurations; authority invariants must remain intact. The 0/0/0/0 row above is scoped to the independent Tracking authority certificate and does not mean this separate UI backlog or the unassessed entire backend has no defects.

These are explicit data/deployment/scale/UI acceptance limits, not a claim that unassessed environments have zero defects. The original entire-backend forensic mission, other business modules and unrelated Program Phases1–3 remain outside this Tracking certificate.

## Freeze candidate and future no-reopen contract

Tracking is CERTIFIED / FROZEN / LOCK_CANDIDATE for this documented code/test state and bounded authority scope. The authorized legacy-data assessment exception remains NOT_RUN_ENVIRONMENT; shop-data clearance and production cutover are not approved. The separate POS consumer limitation remains on the UI acceptance backlog.

Future phases may consume Tracking authority and must not redesign/bypass it. Reopening is allowed for a proven production regression, proven security defect, proven data-integrity defect or explicitly approved architecture version change. Every correction must be surgical, regression-tested, PostgreSQL-certified where relevant and followed by protected Tracking regression. This is not an absolute ban on corrections.

No repository commit/push or production data change is implied. The certificate applies to the documented working-tree source manifest; modifying relevant code invalidates its final-state binding until appropriate re-verification and independent review.

## Appendix A — all original 14 cases individually

| Test | Result |
|---|---|
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationBackfill_MatchesRuntimeForAdmittedSerialsAndPreservesRawIdentity | Passed |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "imei-cross-slot") | Passed |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "imei-same-unit") | Passed |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-collision") | Passed |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-control") | Passed |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-format") | Passed |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-unicode-review") | Passed |
| EdgeRetails.IntegrationTests.TrackingManufacturerIdentityPostgresTests.CrossSlotImeiConcurrency_AllowsExactlyOneGlobalIdentityClaim | Passed |
| EdgeRetails.IntegrationTests.TrackingManufacturerIdentityPostgresTests.NormalizedSerialConcurrency_AllowsExactlyOneLogicalSerialClaim | Passed |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.ConcurrentIdentitylessAdjustmentReplay_CommitsOneUnitMovementAndSequence(mode: Container) | Passed |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.ConcurrentIdentitylessAdjustmentReplay_CommitsOneUnitMovementAndSequence(mode: IndividualPiece) | Passed |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.FirstPhysicalCreation_BlocksIdentityMasterMutationUntilHistoryIsCommitted | Passed |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: False) | Passed |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: True) | Passed |

## Appendix B — all final 95 cases individually

| Test | Result | Duration |
|---|---|---|
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.ActualLowerDatabaseRestorePreservesHighWaterOrFailsClosedOnCorruption(corruptAuthority: False) | Passed | 00:00:03.2096356 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.ActualLowerDatabaseRestorePreservesHighWaterOrFailsClosedOnCorruption(corruptAuthority: True) | Passed | 00:00:04.3810114 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.ConcurrentDistinctOperationsPreserveSameOrDifferentSupplierPairs(differentSupplier: False) | Passed | 00:00:00.2163415 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.ConcurrentDistinctOperationsPreserveSameOrDifferentSupplierPairs(differentSupplier: True) | Passed | 00:00:04.8230886 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.FailedCommitRollsBackStockAndUnitsWithoutReusingReservedSequence | Passed | 00:00:00.2170150 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.ForeignProductUnitCannotChangeTheOrderedProduct | Passed | 00:00:00.0865382 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.LaterPurchaseForSameSupplierProductContinuesOnePairSequence | Passed | 00:00:00.4925739 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.PartialNextReceiptContinuesSamePairAndAuthoritativeReceivedQuantity | Passed | 00:00:00.2507437 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.ResponseLossReplayAcrossDisposedScopesReturnsIdenticalCommittedUnits | Passed | 00:00:00.2302320 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.SameOperationIdConcurrentReplayAllocatesOnlyOneRange | Passed | 00:00:00.1164765 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.SameProductSecondSupplierHasSeparatePairPrefixAndSequence | Passed | 00:00:00.2418288 |
| EdgeRetails.IntegrationTests.MasterSupplierProductSequencePostgresTests.VoidReceiptPreservesSequenceAndNextPurchaseContinues | Passed | 00:00:00.4589268 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "purchase", b: "adjustment", crossSlotImei: False) | Passed | 00:00:00.0683238 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "purchase", b: "adjustment", crossSlotImei: True) | Passed | 00:00:00.1425112 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "purchase", b: "intake", crossSlotImei: False) | Passed | 00:00:00.0988006 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "purchase", b: "intake", crossSlotImei: True) | Passed | 00:00:00.0825269 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "purchase", b: "warranty", crossSlotImei: False) | Passed | 00:00:00.1475293 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "purchase", b: "warranty", crossSlotImei: True) | Passed | 00:00:00.1251674 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "warranty", b: "adjustment", crossSlotImei: False) | Passed | 00:00:00.1384096 |
| EdgeRetails.IntegrationTests.TrackingCrossWorkflowPostgresTests.CrossWorkflowIdentityRace_OneOwnerAndLoserHasNoPartialBusinessEffect(a: "warranty", b: "adjustment", crossSlotImei: True) | Passed | 00:00:00.1302987 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationBackfill_MatchesRuntimeForAdmittedSerialsAndPreservesRawIdentity | Passed | 00:00:00.0330439 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "imei-cross-slot") | Passed | 00:00:00.0300988 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "imei-same-unit") | Passed | 00:00:00.0284831 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-collision") | Passed | 00:00:00.0381099 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-control") | Passed | 00:00:00.0312090 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-format") | Passed | 00:00:00.0369042 |
| EdgeRetails.IntegrationTests.TrackingCutoverPostgresTests.MigrationPreflight_InvalidOrAmbiguousLegacyDataFailsClosed(scenario: "serial-unicode-review") | Passed | 00:00:00.0339677 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.CatalogScannerDraftAndStocktake_ReadContractsClassifyPhysicalModes(mode: Container, factor: 1) | Passed | 00:00:00.1509942 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.CatalogScannerDraftAndStocktake_ReadContractsClassifyPhysicalModes(mode: Container, factor: 2) | Passed | 00:00:00.1329792 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.CatalogScannerDraftAndStocktake_ReadContractsClassifyPhysicalModes(mode: IndividualPiece, factor: 1) | Passed | 00:00:00.1426118 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.CatalogScannerDraftAndStocktake_ReadContractsClassifyPhysicalModes(mode: Serialized, factor: 1) | Passed | 00:00:00.4318482 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.Exchange_RequiresExactUnitsAndPreservesBothLegs(mode: Container, factor: 2, missingIds: False) | Passed | 00:00:00.2100605 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.Exchange_RequiresExactUnitsAndPreservesBothLegs(mode: Container, factor: 2, missingIds: True) | Passed | 00:00:00.1330617 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.Exchange_RequiresExactUnitsAndPreservesBothLegs(mode: IndividualPiece, factor: 1, missingIds: False) | Passed | 00:00:00.1454681 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.Exchange_RequiresExactUnitsAndPreservesBothLegs(mode: IndividualPiece, factor: 1, missingIds: True) | Passed | 00:00:00.1379899 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.Exchange_RequiresExactUnitsAndPreservesBothLegs(mode: Serialized, factor: 1, missingIds: False) | Passed | 00:00:00.1415674 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.FreeExactAdjustment_DoesNotRemovePaidUnitCarryingValue | Passed | 00:00:00.1264847 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.ThakaReversal_RestoresExactQuantityCostAndIdentityOnce(mode: Container, factor: 2) | Passed | 00:00:00.2125472 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.ThakaReversal_RestoresExactQuantityCostAndIdentityOnce(mode: IndividualPiece, factor: 1) | Passed | 00:00:00.0889141 |
| EdgeRetails.IntegrationTests.TrackingFinalChallengePostgresTests.ThakaReversal_RestoresExactQuantityCostAndIdentityOnce(mode: Serialized, factor: 1) | Passed | 00:00:00.1167509 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ContainerCreditAndLegacyCost_ConserveWholePackOrRequireReconciliation(legacyCost: False) | Passed | 00:00:00.1092959 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ContainerCreditAndLegacyCost_ConserveWholePackOrRequireReconciliation(legacyCost: True) | Passed | 00:00:00.0755372 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ContainerPack_SaleAndReturnPreserveBaseQuantityCostAndExactIdentity | Passed | 00:00:00.1196186 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ExactConditionTransfer_MovesSelectedUnitLotRatherThanAnotherFifoLot | Passed | 00:00:00.1382960 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.GoldenTrace_ReceiveScanSellReturnRepairAndStocktakePreservePhysicalIdentity(mode: Container, factor: 1) | Passed | 00:00:00.2511526 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.GoldenTrace_ReceiveScanSellReturnRepairAndStocktakePreservePhysicalIdentity(mode: Container, factor: 2) | Passed | 00:00:00.6417840 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.GoldenTrace_ReceiveScanSellReturnRepairAndStocktakePreservePhysicalIdentity(mode: IndividualPiece, factor: 1) | Passed | 00:00:00.2070901 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.GoldenTrace_ReceiveScanSellReturnRepairAndStocktakePreservePhysicalIdentity(mode: Serialized, factor: 1) | Passed | 00:00:00.2184548 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.NegativeContainerAdjustment_WrongQuantityOrMissingLotFailsWithoutEffects(missingLot: False) | Passed | 00:00:00.0816697 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.NegativeContainerAdjustment_WrongQuantityOrMissingLotFailsWithoutEffects(missingLot: True) | Passed | 00:00:00.0804581 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.NonPhysicalModes_ReceivingDoesNotAllocateAnInventoryUnit(mode: Length) | Passed | 00:00:00.0339559 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.NonPhysicalModes_ReceivingDoesNotAllocateAnInventoryUnit(mode: Quantity) | Passed | 00:00:00.0313262 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.PurchaseReturn_PreservesExactIdentityAndOriginalBaseQuantity(mode: Container, factor: 1) | Passed | 00:00:00.0919945 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.PurchaseReturn_PreservesExactIdentityAndOriginalBaseQuantity(mode: Container, factor: 2) | Passed | 00:00:00.1971976 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.PurchaseReturn_PreservesExactIdentityAndOriginalBaseQuantity(mode: IndividualPiece, factor: 1) | Passed | 00:00:00.0812688 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.PurchaseReturn_PreservesExactIdentityAndOriginalBaseQuantity(mode: Serialized, factor: 1) | Passed | 00:00:00.0727841 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.StocktakeMissing_PostScrapsExistingExactUnitAndItsWholeQuantity(mode: Container, factor: 2) | Passed | 00:00:00.1302522 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.StocktakeMissing_PostScrapsExistingExactUnitAndItsWholeQuantity(mode: IndividualPiece, factor: 1) | Passed | 00:00:00.1090221 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.StocktakeMissing_PostScrapsExistingExactUnitAndItsWholeQuantity(mode: Serialized, factor: 1) | Passed | 00:00:00.1084638 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ThakaIssue_PreservesOriginalIdentityAndChangesExactUnitCustody(mode: Container) | Passed | 00:00:00.2539796 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ThakaIssue_PreservesOriginalIdentityAndChangesExactUnitCustody(mode: IndividualPiece) | Passed | 00:00:00.0866164 |
| EdgeRetails.IntegrationTests.TrackingGoldenTracePostgresTests.ThakaIssue_PreservesOriginalIdentityAndChangesExactUnitCustody(mode: Serialized) | Passed | 00:00:00.0954743 |
| EdgeRetails.IntegrationTests.TrackingManufacturerIdentityPostgresTests.CrossSlotImeiConcurrency_AllowsExactlyOneGlobalIdentityClaim | Passed | 00:00:00.1163136 |
| EdgeRetails.IntegrationTests.TrackingManufacturerIdentityPostgresTests.NormalizedSerialConcurrency_AllowsExactlyOneLogicalSerialClaim | Passed | 00:00:00.1645238 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.FirstPhysicalCreation_IdentityCompanyOrCategoryEditWaitsAndCannotCommit(companyEdit: False) | Passed | 00:00:00.1196085 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.FirstPhysicalCreation_IdentityCompanyOrCategoryEditWaitsAndCannotCommit(companyEdit: True) | Passed | 00:00:00.2197151 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.ReceiptWinsFirst_TrackingPolicyMutationWaitsAndFailsAfterHistory(mode: IndividualPiece) | Passed | 00:00:00.0655220 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.ReceiptWinsFirst_TrackingPolicyMutationWaitsAndFailsAfterHistory(mode: Quantity) | Passed | 00:00:00.0982127 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.SkuMutationWinsFirst_PhysicalCreationRefreshesMasterAndSnapshotsCommittedSku | Passed | 00:00:00.1586899 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.TrackingModeMutationWinsFirst_ReceiptUsesLockedCurrentPolicy(purchase: False, becomesPhysical: False) | Passed | 00:00:00.1171156 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.TrackingModeMutationWinsFirst_ReceiptUsesLockedCurrentPolicy(purchase: False, becomesPhysical: True) | Passed | 00:00:00.3152932 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.TrackingModeMutationWinsFirst_ReceiptUsesLockedCurrentPolicy(purchase: True, becomesPhysical: False) | Passed | 00:00:00.1454606 |
| EdgeRetails.IntegrationTests.TrackingMasterRacePostgresTests.TrackingModeMutationWinsFirst_ReceiptUsesLockedCurrentPolicy(purchase: True, becomesPhysical: True) | Passed | 00:00:00.1509141 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.FromZero_AllMigrationsAndTrackingDatabaseConstraintsMatchModel | Passed | 00:00:02.0324954 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "imei-cross-slot") | Passed | 00:00:03.4910072 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "imei-invalid") | Passed | 00:00:02.8357484 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "imei-same-unit") | Passed | 00:00:02.0556874 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "serial-collision") | Passed | 00:00:03.3440396 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "serial-control") | Passed | 00:00:02.1767328 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "serial-format") | Passed | 00:00:02.2976162 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "serial-unicode") | Passed | 00:00:05.6245912 |
| EdgeRetails.IntegrationTests.TrackingMigrationRehearsalPostgresTests.LegacyUpgrade_ActualPreTrackingSchemaBackfillsOrRollsBackFailClosed(scenario: "valid") | Passed | 00:00:02.1591576 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.ConcurrentIdentitylessAdjustmentReplay_CommitsOneUnitMovementAndSequence(mode: Container) | Passed | 00:00:00.2513482 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.ConcurrentIdentitylessAdjustmentReplay_CommitsOneUnitMovementAndSequence(mode: IndividualPiece) | Passed | 00:00:00.0814165 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.ConcurrentPurchaseAndAdjustment_SameSupplierProductKeepDistinctSequences | Passed | 00:00:00.1423929 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.FirstPhysicalCreation_BlocksIdentityMasterMutationUntilHistoryIsCommitted | Passed | 00:00:00.1518992 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.Scanner_TrackingCodeWinsAndManufacturerLookupUsesClaimsWithoutRawColumns | Passed | 00:00:00.1987279 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: False, mode: Container, factor: 1) | Passed | 00:00:00.1499140 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: False, mode: Container, factor: 2) | Passed | 00:00:00.1830775 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: False, mode: IndividualPiece, factor: 1) | Passed | 00:00:00.2192921 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: False, mode: Serialized, factor: 1) | Passed | 00:00:00.9347217 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: True, mode: Container, factor: 1) | Passed | 00:00:00.7672804 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: True, mode: Container, factor: 2) | Passed | 00:00:00.1641799 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: True, mode: IndividualPiece, factor: 1) | Passed | 00:00:00.2564592 |
| EdgeRetails.IntegrationTests.TrackingWorkflowPostgresTests.WarrantyReplacement_CanonicalEquivalentRetryReturnsPersistedOperation(shopStock: True, mode: Serialized, factor: 1) | Passed | 00:00:00.1819122 |
