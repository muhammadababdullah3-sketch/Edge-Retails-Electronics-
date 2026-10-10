# EDGE RETAILS — PHASE 11 W0 SINGLE MASTER PRE-EXECUTION REMEDIATION

**MODE: EVIDENCE/ARCHITECTURE PREPARATION ONLY, NO IMPLEMENTATION**  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Reported branch:** `tracking-remediation-20261002`  
**Historical protected R6:** `phase7-unified-final-r6`  
**Business:** Single city-central Electrical, Electronics & Home Appliances Superstore.

You are the coordinating independent Phase 11 W0 governance/remediation lead. Execute ONE bounded closure pass, **not another whole-project audit**. Read and follow the companion `EDGE_RETAILS_PHASE11_W0_PREEXECUTION_TASK_REGISTER_2026-10-10.md` in full. Complete **W0-01 through W0-15**; reuse source-grounded evidence and inspect only disputed or dependent paths. Relevant original sources:

- Actual current canonical architecture manifest and `docs/Edge_Retails_Final_Architecture_Report_v1.md`, including §§170–195, 233.1, Final21 gates.
- Original September 28 Phase 11–13 master authority; current integrated `EDGE_RETAILS_PHASE11_MASTER_ROADMAP_V2_WITH_SUPERSTORE_CATALOG_2026-10-10.md` and forensic adjudication report.
- Latest attached `Pasted text(20261010-110814).txt` (11-section audit; verdict `CONDITIONAL_READY_WITH_LISTED_GATES`).
- All **four** primary `artifacts/phase11-preimplementation/` forensic files.
- `EDGE_RETAILS_PHASE11_INTEGRATED_ROADMAP_AND_CATALOG_V3_2026-10-10.zip` and all V3 CSV datasets.
- Historical R6 freeze/source/harness/manifests and current live `git status`/ref/EF model/migrations/tests.

If any source cannot be found, mark `EVIDENCE_MISSING`, state what was checked, and keep related gate open. Do not substitute guessed facts.

## ABSOLUTE SAFETY

- Do not modify source, EF migrations/snapshot, tests, production DB, production services, existing artifacts or Git worktree/history; no commit/push/reset/clean/move/seed/deploy. **Do not execute** workspace-reorganization scripts. Respect any active writers and production governance hold.
- Permitted: source read, git diff/status/hash inspection, SQL read-only introspection, and new audit artifacts **outside repository** in owned staging. If writing outside is impossible, output complete report contents in chat and mark `ARTIFACT_WRITE_BLOCKED`.
- Existing baseline builds/tests may run **only** from an independently isolated source copy with proven disposable PG18 identity; otherwise `BLOCKED_ENVIRONMENT`. Never run destructive/rehearsal migrations against shop/operational database.
- This prompt does not authorize **W1–W4 implementation**, migration generation or execution, catalogue seed, credential rotation, release or Phase12/13 crossover.
- Delegate to at most four non-overlapping evidence specialists: Candidate/Governance; Pack/Coil/Cost; Time/Finance; Catalog/PG18/QA. ONE lead performs independent final falsification and crosswalk. No endless rescans.

## REQUIRED W0 REMEDIATIONS

1. **Candidate truth / B01:** Snapshot local branch/HEAD, dirty/untracked state, active writers. Independently compare R6 SHA (`97956831...`) with current reported HEAD (`261dd422...`), commits AND actual Git blob IDs and independently computed source/harness SHA256 manifest entries (all 1,116 paths if authentic manifest accessible). Investigate the report's **false documentation-only assertion**: current/ref comparison already surfaced changed blob SHAs in `CompleteSaleHandler.cs`, `BusinessOperationsReadServices.cs`, `CatalogModels.cs` and EF model snapshot. Identify exact code changes, protect R6, do not automatically declare an R7 freeze.
2. **Requirements/tasks/gates:** Recover authoritative original audit files. Reconcile original **32 claimed** requirements against **34 `REQ-11[A-I]` rows + 7 CAT** in the latest report; preserve provenance, not just totals. Restore original **14** Phase 11 tasks and exact seven `CAT-01`–`CAT-07` roles from integrated roadmap; map latest report's **27 enumerated tasks** using aliases, not silent renumbering. Quote exact ORIGINAL mandatory capability gates and map the report's 10 / claimed 16 to them; do not manufacture replacements.
3. **Tests / B05:** Reconcile 58 proposed tests = 22 pack + 14 PG concurrency + 12 shop time + 10 GT; identify actual named methods, not fabricated names or arithmetic quotas. Separate historical 1,519 proposed/claimed total, 23 CAT U01–U23 **not executed**, actual protected existing baseline and planned tests. Verify disposable PostgreSQL18 instance identity/isolation or document exact blocker. **Do not demand 58 not-yet-implemented tests PASS during W0**.
4. **ADR-04 / B02:** Analyze pack50->sell7->43, cross-pack A4+B6, 100m coil cuts, lot provenance, proportional cost, multi-cashier locking, positive/negative allocation facts, return/restock, Thaka, stocktake, warranty/custody and purchase returns. Challenge four-column-only plan and audit's proposed append-only `inventory.pack_allocations` SQL for mixed reference types, allocation/reversal lineage, price-vs-acquisition cost, carrying value, constraints, rounding, migration/legacy rows, lock ordering and unique constraints. Decide whether existing movement facts are sufficient; recommend minimal schema with stock/lot as sole quantity/cost authority. **Deliver ADR-04 as owner-approval-ready PROPOSED design, not ratified SQL.**
5. **ADR-05 / B04:** Trace host `TimeZoneInfo.Local` and `DateTime.Today` use through reports, trends, WPF, Server, finance, purchasing, expenses and Thaka. Propose one authoritative shop clock and half-open UTC bounds, distinguish event instant, posting date, supplier invoice date and legitimate backdate/effective date. Preserve F12 loss math and recognized gains. Define cross-platform timezone, DST and future-date tests. **Never auto-approve a 30-day supervisor backdate rule** or rewrite vendor dates; state owner policy options and impacts.
6. **Catalog V3 / B03:** Verify ZIP/CSV hashes, 9/65/732 refs (687 active,42 redirect,3 navigation), 51 units, 337 attribute profiles, 732 applicability, 68 facet rules, 300 aliases, 23 scenarios; validate real class ID/redirect joins. Map to existing CategorySymbol/ProductCode/SupplierProduct/ProductUnit/TrackingMode/permissions/DTOs/WPF. Detect overlaps, missing ENUM values/limits, aliases, taxonomies and unsupported physical dimensions. Preserve current COUNT/LENGTH where proven; keep MASS/VOLUME/AREA operationally disabled until proven and separately approved. **No automatic assignment to Phase 12**; Phase12 owns global retry/recovery. Produce safe, idempotent, metadata-only staged seed/upgrade plan, **never apply**.
7. **Implementation preparation:** Generate corrected dependency-ordered W1–W4 work order with original 14 + original 7 CAT task identities intact; show additions as separately identified delta tasks, disjoint agent/file owners, one EF migration/snapshot owner, preserved F01/F02/Sol-F03/F04/F10/F12 protections, rollback/production hold. No execution.
8. **Adversarial W0 closure:** Confirm every finding is `VERIFIED`, `OWNER_DECISION_PENDING`, `EVIDENCE_MISSING` or `BLOCKED_ENVIRONMENT`, with path/line/test/hash proof. Distinguish technical tasks agent can finish from explicit owner choices. No unjustified percentage score, no automatic GO.

## REQUIRED RESULTS

Write 10 supplemental W0 outputs **outside the repository** per the companion register: executive verdict, candidate hash diff, requirements/tasks crosswalk, original gates and test identity matrix, ADR-04, ADR-05, Catalog V3/Unit/seed compatibility, PG18 isolation readiness, corrected W1–W4 master work order, and owner decision/independent QA register. Produce SHA256 of created outputs when possible. Preserve all original 11-section report and Phase7 evidence without modifying or replacing them.

Final verdict EXACTLY ONE of:
- `W0_READY_FOR_OWNER_REVIEW` (technical evidence and decision proposals complete; **not** implementation authorization),
- `W0_CONDITIONAL_WITH_NAMED_GATES`, or
- `W0_BLOCKED`.

Return one concise final status with: completed W0 tasks, evidence-backed unresolved blockers, specific owner decisions D01–D05, artifact locations, proven test isolation, and the **single next owner approval action**. Stop. **DO NOT START PHASE 11 W1–W4**.
