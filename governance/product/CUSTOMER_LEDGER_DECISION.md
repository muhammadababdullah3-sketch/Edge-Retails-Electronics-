# APP-01 Customer ledger decision (deferred)

**Status:** Pending product sign-off (2026-10-26)

## Context

Audit item APP-01 asked for materialized `customer_account_entries` versus query-based customer financial stats versus Thaka-only credit.

## Decision

No schema or read-path change is included in this remediation sprint. Customer financial posture continues to use existing query/Thaka paths in `BusinessOperationsReadServices`.

## Next step when approved

1. Choose ledger model (materialized entries vs on-demand aggregation).
2. Add migration + backfill plan and feature-flagged reads.
3. Extend integration coverage for customer account workspace APIs.
