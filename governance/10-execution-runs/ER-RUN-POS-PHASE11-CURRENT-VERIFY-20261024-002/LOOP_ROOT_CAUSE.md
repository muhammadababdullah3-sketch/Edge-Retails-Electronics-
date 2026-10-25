# Why Phase 11 kept looping

This is a source/evidence workflow diagnosis, based on the live repository, its test oracles, and the raw current-candidate runs.

## Concrete causes observed

1. **Review evidence was attached to a moving target.** The previous worker receipt records D05 review against parent `ea305c76…`; the live candidate had advanced to `6b5bbd04…` and changed 56 source/test files. Re-running a suite or repeating a review against the parent could not close the current candidate. Every new correction creates another after-byte candidate, so the loop continues unless a final exact candidate is frozen and reviewed once.
2. **Test setup failures were mixed with product failures.** The disposable PostgreSQL runner accepted ports that the integration guard rejects; a broad test filter also pulled in unrelated fixtures. Desktop tests wrote under protected ProgramData, and tests used process-global provider construction patterns. Those failures did not consistently indicate Phase 11 product defects, but they repeatedly generated new reruns and revised reports.
3. **Some test oracles were wrong or timing-sensitive.** The Thaka race assertion subtracted a refund again after the read model had already subtracted it. The catalog UI test used a fixed 350 ms wait. The aggregate PG class also made EF warning behavior order-dependent. Each could create a different result on rerun without a changed product contract.
4. **The current code contained a real retry defect.** Stocktake treated broad 4xx responses as definitive and could clear an operation intent after 408/409/429. This was a product issue and needed a code fix plus same-operation-ID retry tests.
5. **A row-level proof was initially missing.** A reversal-only LF14 activity row is now verified against PostgreSQL 18, and the latest candidate-bound test also asserts the selected Option B headline behavior (the activity row keeps signed net; the summary headline stays clamped at zero). This closes that focused test gap without changing policy.
6. **The final exit gate remained separate from implementation progress.** A worker report, passing selected tests, and focused code reviews do not themselves produce a full original-criteria reconciliation or distinct candidate-bound D05 disposition. Treating them as equivalent yields a loop of “almost done” reports without an actual closure decision.

## Result

The runner/oracle issues and the stocktake retry defect have been corrected and the focused current-candidate selections pass. The earlier stale workflow lock is not the present blocker. The latest PG18 run verifies the selected LF14 Option B row and headline behavior. Remaining formal-closure work is the complete 125-task/16-gate/Final21 crosswalk against exact current bytes and a distinct candidate-bound D05 review, plus the still-uncovered Final21 build/migration-inventory criteria. Do not reopen already-passing slices unless their source bytes change or the independent reviewer identifies a concrete defect.
