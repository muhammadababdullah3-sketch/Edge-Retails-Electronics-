# Comprehensive Root Findings Reproduction & Resolution Report (C1–C7)

**Run ID:** `ER-RUN-LOCALFLOW-20261019-076`
**Baseline Review:** `run075-root-review/ROOT_REVIEW.md` (Disposition: `CHANGES_REQUIRED`)
**Authority:** `OWNER-UPDATE-20261018-019` & `OWNER-UPDATE-20261019-007`

---

## Summary of Findings & Run 076 Actions

| Finding | Topic | Root Review Diagnosis | Run 076 Action & Exact Resolution |
|---|---|---|---|
| **C1** | J1 Single Cash Effect Claim | Fake HttpMessageHandler records operationId, does not prove single cash effect on database ledger. | Renamed test oracle; qualified in report and code that server-side single cash effect is **UNPROVEN / OPEN** without raw ledger query proof. |
| **C2** | J2 UI Command Boundary & Refund Reversal | Reflected private methods, skipped `ICommand.Execute`, omitted refund reversal. | Added `ReverseRefundCommand` and `ReverseRefundAsync` deterministic rejection coverage with `"refund-reversal:<id>"` token cleanup. Added explicit `ICommand.Execute` invocations across all 5 RelayCommands verifying no escaping exceptions. |
| **C3** | J4 Production Warranty Identity | Used test-local `BuildPayload` duplicate helper rather than production ViewModel. | Refactored to instantiate production `WarrantyViewModel`, set UI text properties, and call production `CreateClaimAsync` and `CreateClaimCommand`. |
| **C4** | Historical TRX Provenance | Pack and PG read-contract TRX dated 18 Oct; fresh process check found port 5432 operational (PID 5364) and port 55437 inactive. | Fail-closed applied: zero DB tests run; port 5432 untouched; 18 Oct TRX deposited and labeled explicitly as `HISTORICAL_PRIOR_EVIDENCE_RECONCILED`. |
| **C5** | Missing Raw Build Output | Stage 6 claimed 12 projects built with 0 errors/warnings without raw log. | Executed `dotnet build` capturing raw output into `build.log` (7,330 bytes, 0 errors, 0 warnings across 12 projects). Deposited `build.log`. |
| **C6** | Timestamp Provenance Ambiguity | 12:00Z in receipts conflicted with ~17:55 PKT test timestamps. | Formally documented that `2026-10-19T12:00:00Z` is the fixed canonical timestamp for reproducible ZIP archives; contemporaneous events recorded with actual live UTC timestamps. |
| **C7** | Seam Attribution & Scope Retention | J3 (mock proxy) and J5 (fake HTTP pages) not explicitly qualified; 15 canonical LF findings not fully reconciled. | Qualified J3 and J5 exact seams; cataloged all 15 findings LF01–LF15 in crosswalk without substitute IDs, preserving unresolved items as OPEN. |
