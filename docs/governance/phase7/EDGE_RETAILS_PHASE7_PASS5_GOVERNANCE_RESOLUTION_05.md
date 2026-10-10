# Phase7 Pass5 — Governance Resolution05

Recorded2026-10-06 from user authority saved verbatim in `artifacts/phase7-pass5/governance-resolution05-master-20261006.md`. Supersedes Resolution04 only where bulk financial snapshot semantics conflicted with protected product-wide moving weighted average. Resolves the carrying-value decision; no certification or lock is implied.

## Bulk provenance and valuation

Quantity/Length original lot→purchase item→supplier is provenance authority. Single ProductCostState/product-wide MWA remains accounting carrying-value authority. Send is custody-only: no owned quantity/value exit, COGS, loss, revenue, gain or supplier credit. Source cost, send-time MWA and send-time carrying value are immutable forensic snapshots, never a reserved future withdrawal amount. No second pool or custody-based costing engine.

Economic disposition uses the central allocator's protected current MWA under canonical transaction/cost locks, persisting actual resolution-time MWA/removal once. Custody-only repaired/rejected returns remove/re-add no cost. Like-for-like bulk replacement preserves owned quantity/value; changed physical/source provenance is recorded without automatic repricing. Actual credit/debit or quantity/value delta is an explicit separate fact.

SupplierCredit minus actual resolved carrying value yields WarrantyRecoveryGain when positive, neutral when zero, recognized warranty/inventory loss when negative. Recovery never enters NetSales. Exact Serialized/IndividualPiece/Container/Pack value and identity, H1/H2 and Missing/Found authority are unchanged. Ordinary Purchase/Sale/Thaka/Stocktake/Adjustment/Return continue; no business blocking or historical COGS rewrite.

## Approved 100→55 acceptance alignment

A1@100 sent; snapshot100 and owned value100 unchanged. B1@10 received; pool110/qty2/MWA55. Ordinary sale COGS55 leaves qty1/value55; original A lot provenance and send snapshot100 remain. Credit100 removes55 and creates warranty recovery45, reconciling receipts110=COGS55+disposition55. Scrap removes55/loss55; repair preserves55; bulk replacement preserves continuity.

Prior Expected100 remaining pool is superseded: AUTHORIZED_ASSERTION_ALIGNMENT to55 with original RED history retained. This change is explicitly governed, not a test weakening to match a defect. Independent review remains mandatory.

## Continuing schema and gates

Resolution04 minimal append-only sends/resolutions, partial remaining quantity and narrow claim/return sold-source allocations remain authorized. Snapshot field names must reflect this forensic role. Remaining unresolved quantity derives sent minus committed resolution quantities; no future frozen financial remaining balance. Forward migration only, no released migration edits or speculative backfill. Ambiguous legacy provenance fails closed.

Mandatory owned PostgreSQL18/Npgsql zero/upgrade/constraints/old-writer refusal/backuprestore/model alignment/cleanup precedes acceptance; then full D13/D13-2 source/capacity/concurrency/economics/rollback/replay/exact protections and bounded read-only Resolution05 review. D17/remainder/golden/owner/regressions/freeze/final21/NEW independent Sol/high/post-certifier rehash precede lock. SourceNOTFROZEN; operational DB/data/services/Desktop/deployment and Git history untouched.
