# Business Reconciliation Report

## Method and scope

Reconciliation SQL ran in `BEGIN TRANSACTION READ ONLY` with a 30-second statement timeout against the disposable PostgreSQL database after selected business tests. Aggregate values are test fixture output, not production/shop state. Direct SQL writes were separately probed in explicit transactions and rolled back. No fixture was crafted to cover every 12-domain golden trace; existing tests provide behavioral evidence, but the reports do not certify full cross-domain reconciliation.

## Results

- Sale header totals vs sum of net sale lines: zero mismatching rows in seeded fixture.
- Sale applied payment vs sale total: zero mismatching rows in seeded fixture.
- Sale return quantity vs original sale-item quantity: zero over-return rows in seeded fixture.
- Exact inventory identity required fields missing: zero rows in seeded fixture.
- Movement effects vs StockBalance: one discrepancy row; inspection showed a test setup inserted a StockBalance quantity directly as a negative/fraction rejection fixture without movement. This is fixture seeding, not a completed business event, so excluded as product defect.
- Lot bucket totals vs StockBalance: same single fixture-only stock-balance row; no independent clean baseline purchase was used for this query.
- Supplier ledger balances, cash sessions, cost states, unit statuses and outcome rows were enumerated, but the test database included independently seeded tests and cannot prove owner reconciliation across each flow.
- Succeeded outcomes with missing payload fingerprint were found among test fixtures. OperationOutcome allows nullable fingerprint by design; this routes to Phase12 and is not a direct contradiction unless the specific operation requires a fingerprint.

## Cross-domain economics inspected

The saved independent aggregate query records sales, return refunds, sale COGS and COGS reversals, expenses, recognized loss, Thaka issue charges/costs, reversals, payments, payment reversals and discounts. It was not treated as a single-period profit proof because tests use different dates and the existing report rules must honor sale/return timing and Thaka recognition policy. Canonical authority defines net sales and net COGS independently from supplier cash, purchase spend and Thaka collections.

## Required SELECT-only checks

`reconciliation.sql` includes movement-to-balance, lot-to-balance, sale/payment/return arithmetic, exact identity completeness, supplier signed ledger, cash opening plus movement arithmetic, inventory cost state, unit statuses, outcomes, warranty counts and Thaka project charge/paid comparisons. It also captures non-ANALYZE EXPLAIN plans. These queries are retained for repeatable review; only the disposable database was queried.

## Business facts / policy risks

- Sale, returns, inventory movement, lot/cost, supplier and cash truths are separately persisted with links and snapshots. Application handlers wrap high-value work in read-committed transactions and use row/advisory locks.
- InventoryUnit.status is integer without a database CHECK restricting it to known values. The domain throws for unknown status in application accounting, but direct SQL accepts 999.
- Frozen Pass5 resolution says purchase return and SupplierRefund are distinct facts but permits one atomic operation for actual cash receipt. Existing Pass5 governance resolution finds current behavior contradictory; routed to `PHASE7_PASS5`.
- Purchase void must not imply actual money returned; explicitly separate payment reversal/actual refund semantics. Existing governance resolution and tests protect this; no correction made.
- Pass5 governance decision package records exact missing physical `Missing` status for absent tracked units; no new enum/status is authorized during this audit.
- Thaka material revenue vs project receivable and settlement are not combined with shop POS cash/revenue. Warranty claim/custody identity has separate current and historical exact-unit links.

## Limits

Operational data unavailable by policy/evidence. No claims about production balances, financial mismatch counts, or historical orphan rows are made. The known test fixture movement/balance discrepancy is explicitly not a production finding.
