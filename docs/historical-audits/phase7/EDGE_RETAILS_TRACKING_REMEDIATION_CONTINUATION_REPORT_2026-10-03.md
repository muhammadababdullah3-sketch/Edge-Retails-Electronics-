# EDGE RETAILS TRACKING REMEDIATION CONTINUATION REPORT

Date: 2026-10-03 (Asia/Karachi).

Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`.

This report covers the Tracking remediation continuation requested in the latest attachment. It does not certify the entire backend forensic audit or reopen Program Phases 1–3. All references below concern the current, uncommitted working tree, including the authoritative takeover changes.

## Required result

| Field | Result |
|---|---|
| Branch | `tracking-remediation-20261002` |
| HEAD | `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b` — unchanged |
| Phase 3 merge-base | `4b36f38f6729975391a29175d6b149aa622a8ce3` — verified as ancestor/merge-base |
| Initial build | FAILED: CS1513, TrackingManufacturerIdentityPostgresTests.cs:66; 0 warnings, 1 error |
| Final Debug build | PASS: 0 warnings, 0 errors |
| Final Release build | PASS: 0 warnings, 0 errors |
| Compile blocker repaired | YES |
| Warranty fixture failures repaired | YES |
| StockAdjustment replay | COMPLETE in implementation and sequential unit evidence; PostgreSQL concurrent execution NOT_RUN |
| Warranty canonical replay fingerprint | COMPLETE for newly recorded operations; historical hash compatibility limit below |
| Migration Serial parity | PARTIAL: fail-closed SQL implemented; ASCII admission parity designed; PostgreSQL execution and Unicode-data cutover review outstanding |
| Master first-use concurrency | PARTIAL: narrow shared row-lock boundary implemented; real PostgreSQL test compiled/discovered but NOT_RUN |
| PostgreSQL environment | BLOCKED_ENVIRONMENT: `EDGE_RETAILS_TEST_DB` not configured |
| PostgreSQL race tests | NOT_RUN |
| Migration cutover tests | NOT_RUN |
| Unit tests | 871 total / 871 passed / 0 failed / 0 skipped |
| Focused Tracking tests | 96 total / 96 passed / 0 failed / 0 skipped |
| Integration tests | 14 Tracking cases discovered; 0 executed / 0 passed / 0 failed / 0 framework-skipped; 14 NOT_RUN due to environment |
| EF pending model changes | NONE |
| Migration/model consistency | Model check PASS; Designer/snapshot unchanged; database migration execution NOT_RUN |
| git diff --check | PASS, exit 0; Git emits LF/CRLF conversion notices |
| Protected architecture regressions | NONE identified by source inspection and executed unit suite; PostgreSQL/custody deployment behavior not newly certified |
| FINAL TRACKING STATUS | **BLOCKED_ENVIRONMENT** |

`COMPLETE` implementation entries do not mean PostgreSQL certification. Discovery, compilation, unit tests, and EF model inspection cannot prove database races or executable migration SQL.

## Original gap matrix

| Gap | Status | Evidence and limit |
|---|---|---|
| 1 — Manufacturer Identity Authority | COMPLETE | Existing claims authority and both unique model indexes preserved. `TrackingManufacturerIdentityTests.Identity_claim_model_has_global_type_normalized_uniqueness_and_unit_slot_uniqueness` passes. Two direct PostgreSQL uniqueness races await execution; production database enforcement is not newly certified. |
| 2 — Canonical Manufacturer Identity Normalization | COMPLETE | Existing V1 rules retained; Rune-based validation closes supplementary Format-character escape and handles supplementary decimal digits. Canonical Serial/IMEI and Warranty/adjustment replay unit cases pass. |
| 3 — Single Physical-Unit Creation Authority | COMPLETE | Source search finds one production `new InventoryUnit` and one production `BuildTrackingCode` call, both inside `PhysicalUnitCreationAuthority.CreateAsync`. Callers and repaired fixtures use the authority. |
| 4 — Snapshot + Master Mutation Governance | BLOCKED_ENVIRONMENT | Product row locking plus Company/Category/Supplier shared locks implemented. Effective-value mutation guards and DealerCode mutation/removal unit cases pass. Actual first-use lock test NOT_RUN. |
| 5 — High-Water / Never-Reuse | COMPLETE | Machine high-water and SequenceAuthorityCustody source unchanged. Existing machine/sequence regression unit suites pass in the 871-test run. Sequence gaps remain allowed and reuse forbidden; no new Windows deployment rehearsal claimed. |
| 6 — Cross-Workflow Concurrency + Replay | BLOCKED_ENVIRONMENT | Sequential adjustment and both Warranty replay regression cases pass; canonical operation lock/outcome retained. Direct identity races and concurrent identity-less adjustment tests NOT_RUN. Old Warranty hashes have the explicit compatibility limit below. |
| 7 — Lifecycle Provenance | COMPLETE | Exact-unit lifecycle, return, Warranty, stocktake, Thaka and adjustment unit suites pass. Container decrease scraps the same unit and retains its identity. Real database lifecycle transaction certification remains outside this green unit result. |
| 8 — Scanner + Identity Visibility | COMPLETE | TrackingCode-first and claim-backed Serial/IMEI source preserved; scanner/exact-unit/stocktake unit suites pass. No raw-column scanner authority restored. |
| 9 — Migration / Existing-Data Cutover | BLOCKED_ENVIRONMENT | Surgical SQL preflight and backfill changes, existing uniqueness/FK design preserved, model unchanged. Seven real SQL cutover cases NOT_RUN. Non-ASCII legacy Serial data requires explicit review. |
| 10 — Final Certification Contract | BLOCKED_ENVIRONMENT | Debug/Release, focused/full unit, EF consistency and diff checks pass. All 14 Tracking PostgreSQL cases are discoverable but not executed. |

## Changes completed and evidence

### Compile blocker and fixture composition

Completed the interrupted [TrackingManufacturerIdentityPostgresTests.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/TrackingManufacturerIdentityPostgresTests.cs:16>), retaining real purchase setup and two separate Npgsql claim inserts. A synchronized start tests global cross-slot IMEI and normalized Serial uniqueness. The loser must hit the specific global identity unique constraint; the test asserts one stored logical claim. These are actual database tests, not precheck-only substitutes.

Connections/scopes are disposed. Persisted fixture rows belong to the existing attested disposable PostgreSQL rehearsal environment, whose runner owns cleanup. No operational database was contacted. The harness rejects missing connection configuration and requires the owned temporary run root, loopback/high-port connection and matching PostgreSQL data directory.

Customer/shop Warranty, purchase and intake unit-test composition now injects the existing fake canonical physical authority. Production fail-closed checks remain. Older fixtures were corrected where they supplied a Serial while declaring Serial policy disabled, or reused a globally unique Serial when trying to test a different supplier.

After compile/fixture repair, the requested original focused baseline was **75/75 passed**, 0 failed/skipped, before additional regressions were added. Evidence: [tracking-baseline.trx](<C:/Users/muham/OneDrive/Desktop/Point of Sale/.tracking-results-2026-10-03/tracking-baseline.trx>).

### StockAdjustment operation replay and physical identity

[CreateStockAdjustmentHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs:87>) now requires the existing `IOperationLock` and `IOperationOutcomeLedger`. An empty operation ID or missing replay authority fails closed.

The fingerprint includes actor, operation mode/reason, note, ordered item intents, exact canonical decimal values, supplier/product/unit identifiers, canonical manufacturer identities, sorted selected unit IDs, and reason details. Replay acquires the canonical operation lock and reads its outcome **before current master/item validation or effects**. A committed same-payload retry returns the stored adjustment ID. Different actor/type/payload returns `idempotency.payload_mismatch`; unresolved outcomes return a failure requiring reconciliation. A legacy movement lacking an authoritative payload outcome fails closed rather than allocating again. Payload-mismatch attempts do not replace the original outcome.

Positive IndividualPiece and Container adjustments now use the same authority as positive Serialized adjustments. Identity-less items still receive a new shop TrackingCode and sequence. Container count derives from base quantity / selected unit factor; acquisition cost and negative lot/cost quantities use the same physical-to-base relationship. A negative adjustment changes the selected unit to Scrapped, preserving ID/TrackingCode/sequence.

[TrackingAdjustmentReplayTests](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/TrackingAdjustmentReplayTests.cs:8>) verifies:

- Serialized, IndividualPiece and Container retries retain one adjustment, movement, unit and sequence allocation, return the original result, and reject changed quantity.
- Canonically equivalent Serial retries return the original operation.
- A factor-2 Container creates one physical unit at the matching acquisition cost, replays even after product-unit deactivation, then decreases to zero stock/lot/cost and scraps that same identity.

Database concurrent replay evidence remains blocked. The existing outcome ledger implementation, operation lock domain and machine high-water architecture were reused.

### Canonical Warranty fingerprints and runtime normalization

[WarrantyOperationIdentity.ManufacturerIdentity](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs:28>) calls the existing `IdentityNormalizationRules` for Serial/Imei1/Imei2. Structured JSON represents the identity fields rather than ambiguous manufacturer-field delimiter concatenation.

[ReceiveCustomerWarrantyReplacementHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs:1430>) and [ReceiveShopStockWarrantyHandler](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs:2178>) now record canonical replacement fingerprints. [CustomerReplacement_CanonicalEquivalentReplayDoesNotAllocateAgain](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/Phase1DWarrantyReplacementLifecycleTests.cs:629>) and [ShopReplacement_CanonicalEquivalentReplayDoesNotAllocateAgain](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/Phase1DWarrantyReplacementLifecycleTests.cs:660>) cover case, compatibility-width Serial and visual IMEI formatting retries with no new unit/sequence and retained historical original identity.

**Historical hash compatibility:** an exact retry can still match the prior Trim-based hash. A differently spelled canonical-equivalent retry against a pre-change operation may still return `payload_mismatch`: its one-way legacy hash cannot generally be transformed into the new canonical hash. No historical hash was rewritten, and no mismatched intent was silently accepted. New canonical operations have the tested equivalence guarantee; historical operations need explicit reconciliation if such retries occur.

[IdentityNormalizationRules](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Domain/Catalog/TraceabilityModels.cs:587>) preserves Trim → NFKC → invariant uppercase, optional blank handling, 160-character bound and normalization version 1. Rune iteration rejects Control/Format code points including supplementary U+E0001. IMEI Rune iteration maps decimal digits including supplementary Osmanya digits to ASCII while retaining whitespace/hyphen handling, 14/15-digit identity and separate checksum semantics. Malformed normalization input produces a stable business-rule failure.

### Migration cutover

[TrackingManufacturerIdentityAuthorityV1.Up](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/Migrations/20261002101709_TrackingManufacturerIdentityAuthorityV1.cs:49>) uses a temporary Serial normalization function with the explicit .NET Trim whitespace set. It rejects retained ASCII controls/DEL, lengths over 160 and all remaining non-ASCII characters before collision/backfill. Admitted ASCII casing uses translation independent of database locale. Backfill preserves raw values and writes normalized V1 claims.

This is a **conservative fail-closed cutover gate**, not a full PostgreSQL Unicode implementation of .NET normalization. A valid runtime Unicode Serial can deliberately block migration. That prevents guessing across Unicode casing/NFKC implementations, choosing collision winners, or automatically rewriting valid existing data. Non-ASCII existing identities require a reviewed runtime-normalized cutover procedure before deployment. Unicode IMEI inputs not supported by the existing SQL gate likewise remain blocked; they are not silently stripped into identities.

[TrackingCutoverPostgresTests](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/TrackingCutoverPostgresTests.cs:8>) executes the checked-in custom migration SQL with only table targets redirected to connection-owned temporary tables. Six invalid/ambiguous scenarios assert failure before claims; the admitted ASCII/blank backfill case compares runtime normalization and preserved raw text. No migration Designer/model snapshot regeneration occurred. These SQL cases have **not executed** in this environment; a successful C# build does not validate PL/pgSQL execution.

### Master mutation and snapshots

[PhysicalUnitCreationAuthority.CreateAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/PhysicalUnitCreationAuthority.cs:38>) retains existing product and supplier-product advisory lock domains and now obtains the Product `FOR UPDATE` row boundary used by catalog mutation. Supplier and identity-defining Company/Category rows receive shared locks. Tracked Product/Supplier entities reload after the row lock, avoiding snapshots from pre-lock validation state. No broad global lock was introduced.

[UpdateProductHandler.HandleAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs:337>) guards the **effective values applied** after normalization. History blocks Company/Category removal, ModelCode removal/null-to-assignment and an implicitly regenerated SKU, as well as ordinary mutations. Existing BaseUnit/tracking policy rules remain. Takeover Company/Category guards still include inactive products with history.

[EdgeRetailsDbContext.EnforcePermanentDealerCode](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Persistence/EdgeRetailsDbContext.cs:147>) rejects tracked changes or removal of an already assigned DealerCode on the production SaveChanges paths, even without current stock. Initial assignment from blank remains possible. This is an application persistence guard, not a new database trigger or protection against privileged direct SQL.

[TrackingMasterIdentityGovernanceTests](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.UnitTests/TrackingMasterIdentityGovernanceTests.cs:10>) has nine passing cases: five effective product identity mutations, DealerCode reassignment/removal, and inactive-product Company/Category history. [FirstPhysicalCreation_BlocksIdentityMasterMutationUntilHistoryIsCommitted](<C:/Users/muham/OneDrive/Desktop/Point of Sale/tests/EdgeRetails.IntegrationTests/TrackingWorkflowPostgresTests.cs:143>) observes a real PostgreSQL lock wait, then requires the edit to fail once first physical history commits. It is compiled/discovered but NOT_RUN. The current test covers first creation winning against SKU edit; reverse-order and separate Company/Category races remain useful additional database coverage.

## Protected architecture review

Source search in production `src` finds:

| Evidence | Location |
|---|---|
| Only `new InventoryUnit` | PhysicalUnitCreationAuthority.cs:204 |
| Only invocation of `BuildTrackingCode` | PhysicalUnitCreationAuthority.cs:211 |
| Formula declaration retained | Domain/Catalog/TraceabilityModels.cs:94 |
| Global claim uniqueness | Infrastructure/Persistence/Configurations/InventoryConfigurations.cs:246 |
| Unit-slot uniqueness | Infrastructure/Persistence/Configurations/InventoryConfigurations.cs:248 |
| TrackingCode-first, Serial claims, IMEI claims, barcode, SKU, broad search | [Phase4WorkflowReadService.ResolveScannerAsync](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Infrastructure/Services/Phase4WorkflowReadService.cs:68>) |
| Stocktake visible identity fallback | [StocktakeHandlers.cs](<C:/Users/muham/OneDrive/Desktop/Point of Sale/src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs:559>) |
| Machine high-water, custody, Designer and snapshot | No changes in Git diff |

Full passing unit execution includes `MasterMachineHighWaterTests`, `SequenceAuthorityRegressionTests`, `Phase1DExactUnitLifecycleTests`, `Phase1DExactUnitReturnBehavioralTests`, `Phase1DPosResolutionAndExactUnitSaleTests`, `Phase2StocktakeThakaCashSessionBehavioralTests`, Warranty lifecycle and StockAdjustment tests. It gives preservation evidence without claiming exhaustive production failure certification.

## Validation and runnable continuation

Executed against the final source:

```powershell
dotnet build EdgeRetails.sln --configuration Debug --no-restore
dotnet build EdgeRetails.sln --configuration Release --no-restore
dotnet test tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj --configuration Debug --no-build --no-restore --logger 'trx;LogFileName=tracking-full-unit-final.trx' --results-directory .tracking-results-2026-10-03
dotnet test tests\EdgeRetails.UnitTests\EdgeRetails.UnitTests.csproj --configuration Debug --no-build --no-restore --filter 'FullyQualifiedName~EdgeRetails.UnitTests.Tracking|FullyQualifiedName~EdgeRetails.UnitTests.PhysicalReceivingForensicTests|FullyQualifiedName~EdgeRetails.UnitTests.StockAdjustmentHandlerBehavioralTests|FullyQualifiedName~EdgeRetails.UnitTests.Phase1CReceivingAndLabelPrintingTests|FullyQualifiedName~EdgeRetails.UnitTests.Phase1DWarrantyReplacementLifecycleTests' --logger 'trx;LogFileName=tracking-focused-final.trx' --results-directory .tracking-results-2026-10-03
dotnet test tests\EdgeRetails.IntegrationTests\EdgeRetails.IntegrationTests.csproj --configuration Debug --no-build --no-restore --list-tests --filter 'FullyQualifiedName~EdgeRetails.IntegrationTests.Tracking'
dotnet ef migrations has-pending-model-changes --project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --startup-project src\EdgeRetails.Infrastructure\EdgeRetails.Infrastructure.csproj --configuration Debug --no-build
git diff --check
```

EF inspection used a temporary loopback port-1 model-only connection setting and restored the prior environment value. No database connection/migration was attempted. EF returned: “No changes have been made to the model since the last migration.”

Final evidence: [full unit TRX](<C:/Users/muham/OneDrive/Desktop/Point of Sale/.tracking-results-2026-10-03/tracking-full-unit-final.trx>) and [focused TRX](<C:/Users/muham/OneDrive/Desktop/Point of Sale/.tracking-results-2026-10-03/tracking-focused-final.trx>). Intermediate failures were corrected; the final runs above are the authoritative results.

Tracking database inventory: two direct uniqueness races, seven cutover/backfill cases, two concurrent identity-less adjustment cases, two Warranty canonical retry cases and one first-use/master mutation race = **14 NOT_RUN cases**. Their not-run state is not a framework skip or PASS.

Once an owned isolated PostgreSQL environment is available, the existing [Invoke-MasterRemediationPostgresRehearsal.ps1](<C:/Users/muham/OneDrive/Desktop/Point of Sale/scripts/Invoke-MasterRemediationPostgresRehearsal.ps1>) exposes `-TestFilter 'FullyQualifiedName~EdgeRetails.IntegrationTests.Tracking'`. Its temporary owned cluster/harness attestation is required; merely setting an arbitrary database connection is insufficient. The runner was not invoked in this continuation because the user explicitly requires BLOCKED_ENVIRONMENT when the test DB is unavailable.

## Prioritized remaining work

1. **P1 — PostgreSQL certification blocker:** execute the 14 cases in the attested owned isolated environment, inspect actual unique-constraint losers/lock waits, cutover failures and committed counts. Run a full from-zero migration rehearsal, and a representative legacy upgrade rehearsal, because temporary-table SQL tests alone do not certify the complete migration/FK/index chain. Resolve failures before changing Gap 4/6/9/10 to COMPLETE.
2. **P1 if affected data exists — Unicode cutover gate:** read-only inventory of legacy Serial/IMEI data, produce runtime-normalized candidate claims and collision/invalid-data evidence, and review a cutover procedure preserving existing valid identities. Current non-ASCII migration gate intentionally blocks rather than autocorrecting. No operational data was assessed here.
3. **P2 — Historical replay compatibility:** reconcile any pre-change Trim-hash Warranty operation whose retry changes only canonical representation. Exact old retries remain supported; equivalent legacy hashes are not universally upgraded by this patch. Legacy StockAdjustments without payload outcomes likewise require explicit reconciliation.
4. **P2 — Wider real-database race coverage:** add reverse-order master-edit/first-use tests and separate Company.Code/Category.IdentitySymbol contention, plus end-to-end cross-workflow identity conflicts between purchase/intake/Warranty/adjustment. The direct claim races prove global uniqueness only after execution; they do not alone prove all handler error/rollback behavior.

These remaining items are not marked complete based on documentation or unit-only evidence. The immediate final status is BLOCKED_ENVIRONMENT, with the conditional legacy-data/compatibility limits explicitly retained.

## Git preservation

Initial authoritative takeover: 12 modified tracked files, 2 untracked tests, 0 staged, 0 conflicts. The requested branch/HEAD were verified before edits and retained.

Final working tree including this report: 23 modified tracked files, 6 untracked files (five test files and this report), 0 staged, 0 conflicts. Tracked diff: 23 files, 881 insertions, 598 deletions; this excludes untracked test/report content and includes inherited takeover changes. Some StockAdjustment churn is indentation after moving new-intent validation inside the existing operation transaction.

No commit/push, stash/reset/clean/restore/merge/rebase, other-worktree edits or workspace cleanup occurred. Phases 1–3 and high-water/custody architecture were not reopened. HEAD remains `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`.

**FINAL TRACKING STATUS: BLOCKED_ENVIRONMENT.**
