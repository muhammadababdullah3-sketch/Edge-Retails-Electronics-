# Edge Retails — Phase 11 W0 Pre-Execution Remediation Task Register

**Date:** 2026-10-10  
**Status:** Proposed W0 work order, not Phase 11 implementation authorization  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Branch reported by most recent audit:** `tracking-remediation-20261002`  
**Historic protected candidate:** `phase7-unified-final-r6`  
**Business:** One city-central Electrical, Electronics & Home Appliances Superstore  
**Authority rule:** Canonical architecture + exact September 28 Phase 11–13 authority + actual repository and certification evidence override retrospective summaries. Latest integrated Phase 11 V2 roadmap adds Catalog V3 without replacing original Phase 11 workstreams.

## Objective and strict boundary

Close *pre-execution* evidence and design gaps from `Pasted text(20261010-110814).txt` in ONE bounded W0 pass, preparing a safe owner decision for Phase 11 W1–W4. This is **not** authority to change live source, EF migrations, operational PostgreSQL, deployed services, Git, approved identifiers, existing certifications, or perform catalog seeding. New W0 artifacts may be written only to an independently owned output folder **outside** the repository; never alter/rewrite the original audits. Optional builds/tests only from an isolated source copy with a proven disposable test database, never on operational DB. No automation of owner approval.

## Principal evidence from the supplied audit and follow-up checks

- Uploaded audit's own overall verdict: `CONDITIONAL_READY_WITH_LISTED_GATES` with B01–B05, not production-ready.
- B01: R6 commit `97956831...` vs reported branch HEAD `261dd422...` (two-commit difference). **Contradiction**: audit calls both commits documentation-only, but independent GitHub ref comparison returned different blob SHAs for `CompleteSaleHandler.cs`, `BusinessOperationsReadServices.cs`, EF model snapshot and `CatalogModels.cs`. Perform complete, local authoritative evidence reconciliation; do not assert equal bytes from commit count.
- B02: `CompleteSaleHandler` requires full container quantity; `ConsumeSerializedAsync` marks selected unit Sold and removes acquisition cost. Original four-column-only proposal lacks transaction/quantity allocation lineage. ADR-04 new fact-table design remains **proposed**, not approved, not executable SQL.
- B03: integrated Catalog V3 ZIP was stated uninspected by the audit. Earlier independent package QA confirms *reference* dataset structure: 9 divisions, 65 departments, 732 class refs (687 active + 42 redirects + 3 navigation), 51 units, 337 attribute profiles, 732 applicability mappings, 68 facet rules, 300 aliases, 23 proposed acceptance scenarios. **Live app compatibility, attributes, operational dimensions and seeding remain unverified**.
- B04: source uses host-local time (`TimeZoneInfo.Local` and WPF/server `DateTime.Today`) in relevant reporting flows. Distinguish UTC event instants, shop posting date, supplier document date and authorized effective/backdate. Neither a new time service nor a 30-day backdate threshold is yet approved.
- B05: disposable PostgreSQL 18 identity and execution evidence absent; 58 new tests are **proposed and not yet implemented**. Do not require their passing in W0.
- Audit internally reports `14 original + 7 catalog = 21` tasks yet enumerates 27 differently identified W0–W4 tasks. Prior integrated V2 roadmap owns the exact `CAT-01`..`CAT-07` mapping. Uploaded requirement CSV has 34 `REQ-11[A-I]` rows plus 7 catalog rows (41 total), while historical headline says 32. Reconcile exact sources rather than silently changing counts.
- Original authoritative Phase 11 gates must be quoted from original authority and independently mapped. Uploaded audit's substitute '16 gates' may be a proposal, not the exact original text.

## W0 task list: 15 bounded remediations

| ID | Priority | Task and required W0 output | Can agent close without owner business sign-off? | Evidence / stop condition |
|---|---|---|---|---|
| **W0-01** | P0 | Record actual local branch/HEAD, clean/dirty/untracked status, ongoing writers, canonical docs and permitted output dir. Produce non-mutating baseline manifest. | Yes, evidence only | If current writer activity makes snapshot inconsistent, mark `FREEZE_UNPROVEN`; no reset/cleanup |
| **W0-02** | P0 | Reconcile R6 vs live source/harness: both commit trees, changed-file lists and actual full candidate-file SHA256 comparisons; classify code, migration, harness, docs. | Evidence yes; **new freeze approval no** | No automatic R7 designation or claim that code did not change |
| **W0-03** | P0 | Retrieve and parse all four original pre-implementation audit artifacts and original Sept 28 Phase 11 authority; source-map missing files. | Yes, where accessible | Absent sources => `EVIDENCE_MISSING`, never invent |
| **W0-04** | P0 | Count/crosswalk 32 reported vs 34 currently enumerated Phase 11 requirements plus 7 CAT. Trace stable IDs back to original owners. | Yes | Explicit additions/renames/deletions + provenance |
| **W0-05** | P0 | Reconcile original 14 implementation tasks + original CAT-01..07 to report's 27 enumerated tasks, avoiding collisions and lost coverage. | Yes | Keep original IDs; present alias/extension registry and diff |
| **W0-06** | P0 | Recover exact original capability exit gate names from authority, map audit's claimed 10/16, plus CAT gates. | Yes | Never replace original gates by inferred list |
| **W0-07** | P0 | Test baseline/count reconciliation: 58 proposed = 22 pack + 14 concurrency + 12 timezone + 10 golden; actual unique named test methods, historic `1519` claimed passes, GT01–GT10 and CAT U01–U23. | Yes for matrix; execution only if isolated | Distinguish planned / written / executed / passed / blocked; no fabricated test method IDs |
| **W0-08** | P0 | Rework ADR-04 into a complete, adversarial pack/coil allocation design: quantity and lot authority, immutable source cost, proportional COGS, returns, stocktake, Thaka, purchases, purchase returns, warranty, locks, roundoff and legacy rows. | **Owner approval needed** | SQL shown as proposal only; no schema or migration write |
| **W0-09** | P0 | 11C cross-layer trace ownership: identify exact Desktop, API, Application, Infrastructure, EF paths and branch impacts, and protected regression cases. | Yes | No duplicate stock authority; no whole-unit mutation on partial draw |
| **W0-10** | P0 | Rework ADR-05 shop time design with reporting boundaries, POS/client defaults, dates/permissions/audit, DST/cross-OS zone mapping and preservation of F12 financial math. | **Owner approval needed for policy** | No default 30-day rule; vendor dates must not be conflated with posting dates |
| **W0-11** | P1 | Verify actual Catalog V3 archive content and CSV joins, stable class IDs/redirects, aliases, 51 units, 337 profiles, 68 facets; maintain defects register incl. missing enum ranges/values. | Yes for QA; activation not authorized | Never equate ZIP integrity with catalog certification |
| **W0-12** | P0 | Compare Catalog V3 with live CategorySymbol/ProductCode/Company/SupplierProduct/ProductUnit/TrackingMode and WPF/API capabilities; draft non-destructive metadata/seed rollout options. | **Owner approval for collision/activation choices** | MASS/VOLUME/AREA remain disabled unless independently proven; no auto-assign to Phase12 |
| **W0-13** | P1 | Verify disposable PG18 harness identity, credentials separation, DB name/port, backup and no production alias; optionally run existing non-destructive baseline in isolated copy. | Yes if sandbox available; infra grant may be needed | Do not provision or use operational PostgreSQL; 58 unimplemented tests remain planned |
| **W0-14** | P1 | Build corrected W1–W4 implementation dependency DAG, no overlapping owners for migration/snapshot, cutover hold, open decisions, Phase12/13 boundary. | Yes, proposed work order | Do not start implementation, seed, deploy, commit or push |
| **W0-15** | P0 | Independent adversarial W0 acceptance: every crosswalk traceable, every claim has proof, owner decisions isolated, open blockers explicit; produce one truthful readiness verdict. | Yes for evidence verdict; **owner chooses GO** | W0 `READY_FOR_OWNER_REVIEW`, `CONDITIONAL`, or `BLOCKED` only; not production-certified |

## Owner decisions needed after technical preparation

| Decision | What team recommends | What owner must explicitly decide |
|---|---|---|
| **D01: candidate lineage** | Preserve R6 as immutable historical proof; identify current true source/harness. | Approve a new freeze/candidate name **only if** differences and evidence justify it. |
| **D02: ADR-04** | Adopt one source-of-truth stock/lot authority and append-only, allocation-level pack provenance if current facts are inadequate. | Approve exact pack/coil lifecycle, return-to-pack vs loose/restock rules, fractional length/cost behavior, schema option after proofs. |
| **D03: ADR-05** | Central shop timezone + half-open UTC intervals; separate posting date, vendor invoice date, backdate. | Select legitimate backdate/future-date thresholds, supervisors/permissions and audit requirements. Do **not** default to 30 days. |
| **D04: Catalog V3** | Metadata-first staged activation, preserving historical identities and disabling unproven stock dimensions. | Approve compatibility mappings, conflict handling and final activation policy. |
| **D05: test isolation/cutover** | Existing tests on isolated copy; new tests in W1–W4 then W4 certification. | Grant isolated resources and later separate deployment/production release authorization. |

## W0 completion and explicit halt

W0 can close its **technical evidence package** when all 15 tasks are `EVIDENCE_VERIFIED`, `OWNER_DECISION_PENDING` or `BLOCKED_WITH_CAUSE`, with no unknown silent omissions. **W1 implementation GO requires** candidate identity resolved, ADR-04/05 approved, catalog safety mapping approved, real disposable PostgreSQL test path proven, and a distinct owner implementation authorization. A new freeze candidate, a 30-day backdate policy, approval of SQL snippets, or execution of 58 future tests cannot be manufactured by the agent.

## Expected W0 artifacts (outside repo, or output inline if blocked)

1. `00_W0_EXECUTIVE_GATE_VERDICT.md`
2. `01_SOURCE_CANDIDATE_DIFF_AND_HASH_MANIFEST.md`
3. `02_REQUIREMENTS_32_34_AND_TASK_14_7_CROSSWALK.csv`
4. `03_ORIGINAL_EXIT_GATES_AND_TEST_IDENTITY_MATRIX.md`
5. `04_ADR04_PACK_COIL_AND_COST_DESIGN_REVIEW.md`
6. `05_ADR05_SHOP_TIME_AND_DATE_POLICY_REVIEW.md`
7. `06_V3_CATALOG_UNIT_ATTRIBUTE_SEED_COMPATIBILITY.md`
8. `07_DISPOSABLE_PG18_AND_PROTECTED_BASELINE_READINESS.md`
9. `08_W1_W4_SINGLE_IMPLEMENTATION_WORK_ORDER.md`
10. `09_OWNER_DECISION_REGISTER_AND_INDEPENDENT_W0_CERTIFICATION.md`

All outputs must include source refs, exact candidate identity, status, limitation, and SHA256 manifest where permitted. These are new, supplemental artifacts and do not replace original architecture documentation.
