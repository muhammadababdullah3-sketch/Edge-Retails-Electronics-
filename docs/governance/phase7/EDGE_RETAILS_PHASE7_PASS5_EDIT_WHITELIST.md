# Pass5 exact edit whitelist — implementation wave01

Frozen at2026-10-05 after the retained32-area audit, finding ownership and bounded Resolution03 representation inspection. **RECOVERY_SCHEMA_EXISTING_MODEL_SUFFICIENT**: prospective exact Missing uses one real per-unit source removal movement, durable actual value6 consumption and allocated loss2; Found references the source, reuses its identity and derives frozen gain. Ambiguous legacy episodes fail closed. No migration/new column/table is approved or needed by this shape. No code/test edit preceded this whitelist.

Authority: original frozen master + immutable Governance Resolutions01/02/03. C-P5-ARCH-01/02 and Missing12 approved; R02-RECOVERY-POLICY GOVERNANCE_RESOLVED, classification InventoryLossRecoveryGain. Source audits preserved; no restarts.

## Exact production paths and owners

All paths relative to current live workspace. The lead currently executes sequentially because specialist planning turns reported account usage limits. If specialist execution becomes available, only transfer exclusive listed ownership after a terminal handoff; no concurrent shared-file edits. No nested agents.

|Path|Finding / bounded scope|Owner|
|---|---|---|
|src/EdgeRetails.Domain/Common/MoneyRoundingPolicy.cs (new)|C26 stable nonnegative proportional shares, canonical money rounding; reusable exact-source loss allocation|Lead/C|
|src/EdgeRetails.Domain/Purchasing/PurchasingModels.cs|C26 charge allocation entrypoint only|Lead/C|
|src/EdgeRetails.Domain/Sales/SalesModels.cs|C26 discount allocation entrypoint only|Lead/C|
|src/EdgeRetails.Application/Features/Finance/ExpenseHandlers.cs|C26 expense midpoint convention only|Lead/C|
|src/EdgeRetails.Application/Features/Sales/CommercialExchangeHandler.cs|B01 exact original residual; B02 Other mapping|Lead/B|
|src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs|B03 canonical manual validation/save/audit; preserve low-level noncommitting domain service|Lead/B|
|src/EdgeRetails.Server/Controllers/FinanceController.cs|B03 manual route/reject forged commercial event vocabulary; C29 opening route|Lead|
|src/EdgeRetails.Application/Features/Finance/SupplierAccountHandlers.cs|C01 shared existing canonical refund posting, no second engine/nested commit|Lead/C|
|src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs|C01 atomic explicit immediate refund with durable refund fact and correct balance including pending return credit|Lead/C|
|src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs|C02 default void leaves actual payment intact; no automatic financial reversal/cash assumption|Lead/C|
|src/EdgeRetails.Application/Features/Finance/SupplierOpeningBalanceHandler.cs (new)|C29 explicit append-only opening type/metadata/direction, established permission|Lead/C|
|src/EdgeRetails.Domain/Inventory/InventoryModels.cs|Missing12 + zero-contribution ordinary-terminal rule, values1–11 unchanged|Lead/A|
|src/EdgeRetails.Application/Features/Inventory/StockAdjustmentHandlers.cs|A01–04 plus explicitLost Missing prospective per-unit real source writes/loss allocation; guard ambiguous Other rather than inventMissing|Lead/A|
|src/EdgeRetails.Application/Features/Inventory/StocktakeHandlers.cs|approvedPost-only Missing prospective per-unit source writes/loss allocation; preserve observations|Lead/A|
|src/EdgeRetails.Application/Features/Inventory/FoundInventoryUnitHandler.cs (new)|R02REC01 source-episode locked same-ID restoration/gain authority, no new identity|Lead/A|
|src/EdgeRetails.Application/Features/Inventory/HistoricalMissingClassifier.cs (new)|Resolution02 pure conclusive/ambiguous/notmissing classifier; no operational repair|Lead/A|
|src/EdgeRetails.Application/Abstractions/RepositoryAbstractions.cs|narrow episode/claim/source lot reads needed by Found and D13; protect existing contracts via compatible additions|Lead|
|src/EdgeRetails.Infrastructure/Repositories/EfRepositories.cs|narrow source episode/lot reads only; preserve P4H1/H2 methods/tolerances|Lead|
|src/EdgeRetails.Application/Abstractions/InventoryAbstractions.cs|D13 scoped source/case lot allocation contract only|Lead/D|
|src/EdgeRetails.Infrastructure/Services/InventoryCostAllocator.cs|D13 source/case-constrained transfer/consumption, no cost-pool tolerance widening|Lead/D|
|src/EdgeRetails.Application/Features/Warranty/WarrantyHandlers.cs|D13 remaining supplier-specific sold claims/returns and bulk source/case lot ownership|Lead/D|
|src/EdgeRetails.Infrastructure/Services/BusinessOperationsReadServices.cs|D17 WarrantyRecoveryGain and approved FoundGain projection; R06 derived project count/balance|Lead/D|
|src/EdgeRetails.Application/Features/Reporting/ReportingQueries.cs|separate explicit recovery breakdown additive contract|Lead/D|
|src/EdgeRetails.Application/Features/Parties/PartyDirectoryQueries.cs|R06 additive derived Thaka metrics, no CustomerAR columns|Lead/D|
|src/EdgeRetails.Application/Features/Catalog/ProductManagementHandlers.cs|R19 transactional SKU/pair significant-action audit only; preserve sequences/identity and G01|Lead|
|src/EdgeRetails.Infrastructure/InfrastructureServiceCollectionExtensions.cs|new Found/opening/classifier registrations and existing dependency integration|Lead|
|src/EdgeRetails.Server/Controllers/InventoryController.cs|explicit Found authority route with authenticated actor|Lead|
|src/EdgeRetails.Application/Gateways/IApplicationGateway.cs|opening/Found canonical command contract only if needed for existing API-only client authority|Lead|
|src/EdgeRetails.Application/Gateways/LocalApplicationGateway.cs|canonical new handler forwarding only|Lead|
|src/EdgeRetails.Application/Gateways/RemoteApplicationGateway.cs|canonical new route forwarding only|Lead|
|src/EdgeRetails.Desktop/Services/BackendBusinessOperationsService.cs|R06/report additive projections, new exposed approved commands if existing flow required|Lead|
|src/EdgeRetails.Desktop/Services/RemoteBackendBusinessOperationsService.cs|same API-only projections, no directDBfallback|Lead|
|src/EdgeRetails.Desktop/Services/DemoBusinessDirectoryService.cs|additive derived display metrics only, not PG proof|Lead|

No existing migration/config/model snapshot source change is whitelisted. If a genuine schema dependency appears, STOP before any new migration and document it. No Tracking physical creation/claims/normalization/highwater/custody redesign, installer, operational data or Phase8/9/12 edits.

## Exact test paths / classifications

New coverage files (all NEW_COVERAGE):

- tests/EdgeRetails.UnitTests/Phase7Pass5CommercialTests.cs
- tests/EdgeRetails.UnitTests/Phase7Pass5FinanceTests.cs
- tests/EdgeRetails.UnitTests/Phase7Pass5InventoryRecoveryTests.cs
- tests/EdgeRetails.UnitTests/Phase7Pass5WarrantyReportingTests.cs
- tests/EdgeRetails.UnitTests/Phase7Pass5CatalogCustomerTests.cs
- tests/EdgeRetails.IntegrationTests/Phase7Pass5CommercialPostgresTests.cs
- tests/EdgeRetails.IntegrationTests/Phase7Pass5FinancePostgresTests.cs
- tests/EdgeRetails.IntegrationTests/Phase7Pass5InventoryRecoveryPostgresTests.cs
- tests/EdgeRetails.IntegrationTests/Phase7Pass5WarrantyReportingPostgresTests.cs
- tests/EdgeRetails.IntegrationTests/Phase7Pass5GoldenOwnerPostgresTests.cs
- tests/EdgeRetails.Desktop.PerformanceTests/Phase7Pass5BusinessContractTests.cs

Existing exact alignment paths:

- tests/EdgeRetails.UnitTests/Phase2StocktakeThakaCashSessionBehavioralTests.cs — HARNESS_CORRECTION mandatory audit constructor dependency; assertions untouched.
- tests/EdgeRetails.UnitTests/Phase2TestDoubles.cs — HARNESS_CORRECTION narrow new repository/allocator contracts if needed; no fakePGacceptance.
- tests/EdgeRetails.UnitTests/Phase7Pass1IntegrityTests.cs — AUTHORIZED_ASSERTION_ALIGNMENT only directlysuperseded C01 provenance/paidcredit fixture/C02 unconditional payment reversal; accounting/replay guarantees preserved.
- tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs — same C01 alignment, genuine credit/canonical refund fact, unpaid atomic refusal newcoverage.
- tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs — same C02 alignment, historical posted payment/default noCashIn, explicit separate reversal newcoverage.
- tests/EdgeRetails.UnitTests/StocktakeReconciliationIdempotencyTests.cs — directly superseded shortage destination/count only, identity/quantity/value/loss/replay protected.
- tests/EdgeRetails.IntegrationTests/TrackingGoldenTracePostgresTests.cs — directly superseded shortage destination only; Tracking lifecycleprotected.
- tests/EdgeRetails.IntegrationTests/Phase7Pass2HostileNumericPostgresTests.cs — directlysuperseded shortage destinations only; positive exactSetPhysicalCount failclosed and retainedScrap untouched.
- tests/EdgeRetails.UnitTests/StockAdjustmentHandlerBehavioralTests.cs — directDamaged semantics alignment only with explicit frozenneutralcondition evidence; oldlost tests strengthened, no unrelated weakassertion.

Any additional path requires a recorded exact whitelist addendum before editing; this is a scope gate, not permission for blanket test weakening. No skips, removedraces or harnessprovider substitution.

## Evidence/documentation paths

Mutable execution evidence: artifacts/phase7-pass5/execution-ledger.md, implementation-change-ledger.md, focused-gate records/logs/TRX, prospective final runner/manifest evidence. Add documentation requiredby master: EDGE_RETAILS_BUSINESS_INVARIANTS_REGISTRY.md; EDGE_RETAILS_PHASE7_PASS5_BUSINESS_EVENT_EFFECT_MATRIX.md; EDGE_RETAILS_PHASE7_PASS5_IMPLEMENTATION_CHECKPOINT.md; compatibility evidence. Prior immutable authority/audit/contradiction/stop snapshots and all Pass4 artifacts remain unchanged.

## Execution status and gates

Whitelist freezes scopes, not a verified candidate. Next stage sequential minimum B/C corrections and NEW_COVERAGE; then A/D/source integration. Focused PASS does not complete task. Real PostgreSQL18/Npgsql owned runner, goldenA–D/owner, fullprotected regressions, builds/model/migration inventory, candidate freeze/onefinalwave and NEW independentSol6.1/high remain mandatory. No final challenge until every command terminal and ownedclustercleanup complete. Resourcecheckpoint45–60min stops NEWwork with exact unfinished gates; no selfcertification or prematurelock.
