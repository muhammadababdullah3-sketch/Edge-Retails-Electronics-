# Edge Retails — Pass5 architecture contradiction and controlled stop

## Verdict

**PASS5_BLOCKED_ARCHITECTURE_CONTRADICTION**

This is a read-only discovery report, not a Pass5 implementation, verification, certification or lock. Source, tests, harnesses, migrations, installed applications, services and operational databases were not changed. The frozen business authority was not amended.

The user's frozen master explicitly requires STOP and an architecture contradiction report when live evidence proves a contradiction (frozen authority lines 395–401). It also preserves certified Pass1/2 and Pass4 behavior. The following live behaviors are asserted by protected Pass1 tests and conflict with canonical architecture §§219.2–219.3. No implementation option has been selected.

## Proven protected baseline

- Branch: `tracking-remediation-20261002`.
- HEAD: `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`.
- Exact existing Pass4 source inventory: **893/893 matched**, failed0, skipped0, before audit and again at controlled stop.
- Pass4 manifest SHA256: `F16C47F01362F2000EEE89AF11219F8F4B11F7F20972639C816EB52D13878962`.
- Frozen Pass5 authority SHA256: `0D93BF325DC475A34366D1AC1568D3BC65232216AA354C1F1E13C5728605D306`, unchanged.
- Verification command evidence: `artifacts/phase7-pass5/stop-state-verification.json`, exit0, 893 matched, no active dotnet/testhost/vstest/MSBuild process at inspection.
- Earlier Pass4 certification results remain inherited evidence. No new Pass5 test, build, EF or PostgreSQL gate has executed. No fresh Pass5 independent final certifier has been launched.

## C-P5-ARCH-01 — Purchase return and cash settlement

### Authorities and live evidence

1. Canonical `docs/Edge_Retails_Final_Architecture_Report_v1.md:8282`, §219.2: Purchase Return creates supplier credit; cash settlement uses a separate SupplierRefund.
2. Frozen Pass5 authority `EDGE_RETAILS_PHASE7_PASS5_FROZEN_BUSINESS_AUTHORITY.md:1335`: Supplier Refund is settlement of supplier credit and is not Purchase Return.
3. Live `src/EdgeRetails.Application/Features/Purchasing/PurchaseReturnHandler.cs:427`: CashDrawer mode adds `PurchaseReturnCashIn`, then a `SupplierRefundReceived` ledger entry referencing `PurchaseReturnCashSettlement` and the PurchaseReturn ID. This path does not create a distinct SupplierRefund aggregate.
4. Protected `tests/EdgeRetails.IntegrationTests/Phase7Pass1ReturnPostgresTests.cs:22` (`D_RET_1_CashDrawerReturnAndRetry_PersistOneCashAndBalancedKhataEffect`), including assertions at line248 onward, requires the CashDrawer return, cash-in and balanced supplier ledger. Unit counterpart: `tests/EdgeRetails.UnitTests/Phase7Pass1IntegrityTests.cs:680`.

### Consequence and ownership

Authority IDs03/12/22/25/27/18/28/31 are affected by the distinction between a goods return credit and settlement evidence. Existing cash and ledger arithmetic can balance while event provenance differs from the canonical document. Simply removing the branch violates protected Pass1 behavior; simply accepting it leaves the canonical conflict unresolved.

### Governance options — none selected

| Option | Required decision | Consequence |
|---|---|---|
| Explicit inherited exception | Authorize the existing atomic CashDrawer return settlement as a documented exception to §219.2 and clarify its canonical provenance/read-model interpretation | Preserves current protected behavior, but changes governing interpretation; builder cannot silently make this exception |
| Atomic return plus canonical refund fact | Authorize a surgical change that keeps the user operation atomic while recording a distinct SupplierRefund through canonical authority | Requires exact protected-test review and upgrade/replay/provenance analysis; schema need must be inspected before any migration |
| Separate return and later refund | Authorize revising locked Pass1 expectations to prohibit cash settlement inside PurchaseReturn | Changes protected behavior and requires explicit replacement of the affected authority/tests; no assertion weakening by builder |

## C-P5-ARCH-02 — Purchase void and initial payment

### Authorities and live evidence

1. Canonical `docs/Edge_Retails_Final_Architecture_Report_v1.md:8294`, §219.3: Purchase Void reverses purchase liability; previously paid money remains, with later SupplierRefund settlement.
2. Live `src/EdgeRetails.Application/Features/Purchasing/VoidPurchaseHandler.cs:350`: a posted initial payment is marked Reversed; a SupplierPaymentReversal and positive supplier ledger entry are created. CashDrawer payment also produces `PurchaseVoidCashIn` and `CashDrawerReversalAmount`.
3. Protected `tests/EdgeRetails.IntegrationTests/Phase7Pass1PurchasingPostgresTests.cs:139`–154 requires the reversal fact, original payment status Reversed, +4000 supplier reversal and 4000 drawer reversal amount. Unit counterpart: `tests/EdgeRetails.UnitTests/Phase7Pass1IntegrityTests.cs:433` onward.

### Consequence and ownership

The current command combines cancellation of the payable with compensation of an initial payment. Canonical §219.3 specifies preservation of that payment and later refund. Both cannot govern the same command without an explicit exception or authority revision. Owner reconciliation and golden traceC must use the approved interpretation, not a balancing workaround.

### Governance options — none selected

| Option | Required decision | Consequence |
|---|---|---|
| Explicit mistaken-entry void exception | Approve this narrowly scoped compensation as inherited authority, defining eligibility, cash evidence and distinction from a goods return/refund | Preserves protected behavior but needs formal canonical exception |
| Liability-only void | Approve revising protected behavior so initial payment remains Posted and a separate SupplierRefund settles resulting supplier credit | Changes protected tests, read models and replay results; requires explicit authority before edits |
| Explicit payment correction | Define when an initial payment may be reversed versus when physical cash must be settled by SupplierRefund | Requires a precise business rule and protected-authority reconciliation; builder must not invent eligibility |

## Additional inventory decision candidate

The inventory specialist found a separate potential conflict: negative adjustment/stocktake shortage marks units Scrapped while removing source stock without adding retained Scrap stock. `InventoryUnitAccountingPolicy` treats Scrapped units as shop Scrap stock, yet protected shortage tests expect that status for missing units. Increasing Scrap quantity for physically missing units would create stock. This remains a specialist decision candidate requiring event-qualified terminal identity versus retained physical scrap authority; it is not an approved implementation correction. See auditA for final inspected evidence and scope limits.

## Work retained and unfinished gates

Retained: immutable complete32-area authority capture, exact baseline attestation, three independent bounded read-only specialist audits and this contradiction report. Their findings are source-inspected candidates, not new PostgreSQL proofs.

The full32-area audit was interrupted by the mandatory contradiction STOP. SpecialistD was not launched. Root-owned areas and remaining audit portions are not claimed complete. No edit whitelist exists. Implementation, focused tests, PostgreSQL golden traces, owner reconciliation, candidate freeze, all21 final gates, fresh Sol challenge and formal Pass5 lock remain unexecuted. This is an authorized controlled stop, not success or environment acceptance.

## Resume checkpoint

Resume only after governance resolves both contradictions and any confirmed inventory decision, stating the applicable authority and whether narrowly scoped protected Pass1 changes are authorized. Preserve all completed audit evidence. Continue unfinished read-only audit first, finish all32 rows, then resolve finding ownership and freeze the exact edit whitelist. Do not restart Pass4 or regenerate migrations to work around this stop.

No additional defect was proven within the inspected Pass5 authority scope beyond the specialist findings recorded with their evidence. This report makes no exhaustive correctness claim. No Phase7 final closure or Phase8/9/12 work is authorized.
