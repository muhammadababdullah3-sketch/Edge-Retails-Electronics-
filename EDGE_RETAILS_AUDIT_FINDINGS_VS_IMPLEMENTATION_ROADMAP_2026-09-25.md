# Edge Retails audit findings mapped to the implementation roadmap

**Date:** 2026-09-25  
**Compared documents:** [Backend forensic audit](EDGE_RETAILS_BACKEND_FORENSIC_AUDIT_2026-09-25.md) and `C:\Users\muham\OneDrive\Desktop\Edge_Retails_Consolidated_Final_Implementation_Roadmap_2026-09-25.md`  
**Purpose:** Separate defects the roadmap gives a concrete route to address from defects it does not currently close.

## How to read this comparison

“Covered” means the roadmap names a relevant implementation requirement or certification gate. It does **not** mean the defect is fixed today. The roadmap status snapshot says multiple relevant phases are pending or planned, so covered findings remain open until implementation and their exit tests pass.

“Partial” means the roadmap touches the surrounding area but does not state the specific invariant, failure behavior, or regression test needed to close the audit finding. For planning, treat partial items as **not guaranteed to be solved** and add the acceptance criteria below.

The roadmap is a plan, not a direction to modify source during this comparison. The user asked to classify the audit findings; no application code was changed.

## Findings the roadmap directly covers

| Audit finding | Roadmap coverage | What must pass before calling it resolved |
|---|---|---|
| **C-01** — User/session authority can be forged through caller-supplied actor IDs | Phase 1 runtime/security, §§10.5–10.12: LocalClient authentication, loopback server, ExecutionPrincipal, session security, fail-closed authorization, PIN protection; Phase 6 §15.4 explicitly drills actor spoof and SessionId-as-token | Server derives actor/session from authenticated request context; reject forged actor IDs, stale or mismatched sessions, and non-loopback clients; hostile security drills pass |
| **H-04** — Sale return can conflict with active or terminal warranty custody | Phase 1D §§9.7–9.12 covers returns and warranty in one Golden Trace; Phase 6 §15.6 explicitly requires sale/return and warranty/replacement race drills | Return and warranty transitions serialize on the same unit/sale item; active custody and terminal warranty outcomes reject incompatible returns; race tests prove one valid outcome |
| **H-05** — Stock adjustment and manual cash mutations lack safe replay | Phase 2 §§11.2–11.4 provides trusted request context and a central outcome ledger for retry-sensitive mutations | Include both commands in the ledger; same ID and same payload returns the original result; changed payload or command type is rejected; response-loss replay is tested |
| **H-06** — Idempotency is inconsistent across mutation types | Phase 2 §11.4 explicitly centralizes retry-sensitive outcomes; §11.2 carries ClientOperationId and trusted principal context | Every retry-sensitive mutation has a typed outcome, actor/client binding, canonical payload fingerprint, retention policy, and recoverable original result |
| **H-07** — Converted POS draft cannot recover a sale after response loss | Phase 2 §11.1 includes Draft/Hold/Resume/Cancel and sale completion APIs; §11.4 supplies central replay authority | Retry completion after draft conversion returns the committed sale result; no duplicate sale; same-ID/different-payload rejection tested |
| **H-17** — Outbox rows have no durable worker claim/lease | Phase 2 §11.5 explicitly requires durable claim semantics and Worker crash recovery | Concurrent workers cannot execute one effect twice; lease expiry/reclaim and crash recovery are integration-tested |
| **H-19** — Scheduled backup job does not create backups | Phase 3 §12.9 requires Worker to create and verify real backup artifacts; §12.8 and Phase 6 §15.5 cover restore | A scheduled run creates a verifiable artifact, reports failure visibly, and a restore drill recovers it |
| **M-01** — Permission resolution ignores role deactivation | Phase 1 §10.10 requires fail-closed authorization; Phase 6 §15.4 explicitly includes permission downgrade; Phase 2 §11.1 exposes users, roles, and permissions | Deactivating a role immediately removes its effective permissions from existing sessions/requests; downgrade/revocation test passes |
| **M-02** — Reachable mutation handlers omit permission checks | Phase 1 §10.10 requires fail-closed production authorization; Phase 2 §11.1 brings all listed business areas behind APIs and §12.2 maps permissions/state/errors | Enforce authorization at the server/application command boundary for each reachable mutation, including stocktake, cash, product units, and inventory condition; command-level denial tests pass |
| **M-03** — Default production authorization is a no-op | Phase 1 §10.10 explicitly requires production authorization to fail closed | Missing authorization provider/configuration blocks startup or denies sensitive operations; no production composition can resolve the no-op implementation |
| **M-04** — Legacy terminal registration/quota behavior conflicts with target authority | Roadmap §§2.1, 10.1, and 13.2 prohibit terminal quotas and require removal of active V1 terminal/quota authority | Retire the obsolete terminal registration/quota authority as part of single-machine cutover; verify no runtime path still treats it as licensing or access authority |
| **M-05** — PIN authentication lacks throttling, lockout, and failed-attempt audit | Phase 1 §10.11 explicitly requires persistent failed attempts, delay, lock, throttle, and audit; Phase 6 §15.4 drills PIN brute force | Enforce per-account/client abuse controls and durable audit; brute-force drill proves bounded attempts and recovery behavior |
| **M-10** — Authorization denials map to HTTP 400 instead of 403 | Phase 2 §11.3 explicitly requires stable machine-readable authorization error categories | Denials return the documented authorization category and correct HTTP status consistently across controllers; contract tests cover all endpoints |
| **M-12** — Serialized products cannot omit manufacturer serial/IMEI | Phase 1B §7.7 separates physical identity from optional manufacturer identity; §7.12 and the exit gate explicitly require IndividualPiece without Serial/IMEI | Create, receive, scan, sell, return, and restore a serialized physical unit with TrackingCode and null Serial/IMEI; tests pass end to end |

## Findings only partially covered: add explicit criteria to the roadmap

These are not safe to count as resolved by the current roadmap wording alone.

| Audit finding | Related roadmap material | Missing specific requirement to add |
|---|---|---|
| **C-02** — Simulated print engine reports success without printing | Phase 3 §12.7 moves physical printing to Desktop; §15.7 requires real hardware drills | Prohibit simulated engine in production composition; absent/unavailable printer must return failure or pending status, never “printed”; test production DI selection and device failure |
| **H-08** — Thaka charge ignores selected ProductUnit conversion factor | Phase 2 §11.1 includes Thaka APIs; §2.3 says preserve Thaka | Define price basis for entered unit vs base unit, calculate charge using the same conversion snapshot as stock consumption, and test a non-base pack factor |
| **H-09** — Same serialized unit can appear in separate Thaka issue lines | Phase 2 §11.1 includes Thaka; §11.8 preserves exact-unit authority | Reject duplicate InventoryUnitIds across the whole command before any consumption; test same unit on two different lines and verify stock/cost changes once |
| **H-10** — Warranty eligibility incorrectly requires purchase provenance | Phase 1D §§9.10–9.12 covers warranty and Golden Trace | State eligibility for opening stock and shop-owned replacement units with no SourcePurchaseItemId; test claim creation and full resolution for both origins |
| **H-11** — Shop-stock warranty resolution does not bind units to the current case | Phase 1D warranty/replacement trace and Phase 6 warranty/replacement race drill | Require case-specific outgoing/incoming unit links for every credit/replacement transition; test cross-case unit substitution |
| **H-12** — Purchase and warranty replacement identity validation is inconsistent | Phase 1B §7.7 and §7.12 cover Serial/IMEI separation and identity tests; Phase 1D scanner precedence covers IMEI | Require canonical IMEI length/checksum validation and command-wide collision detection across all serial/IMEI fields; test same-batch cross-column duplicates |
| **H-13** — Warranty operation fingerprints are ambiguous and do not consistently bind actor/context | Phase 2 §§11.2–11.4 centralize request context and outcomes | Specify unambiguous canonical payload encoding, include command type/actor/client context, and lock shared sale-item/unit state across warranty and return operations; add delimiter-collision and concurrent lifecycle tests |
| **H-16** — Initial receipt/label requests have no transactional outbox producer | Phase 1C §8.5 says identities become printable after commit; §8.8 defines DB commit then label job; Phase 2 §11.5 adds worker claims | Persist the print intent atomically with the business commit (or a durable post-commit handoff); prove crash-after-commit recovery for receipts and labels |
| **H-18** — Uncertain printer submission can create duplicate physical prints | Phase 1C §8.9–8.10 covers label reprint identity; Phase 3 §12.7 moves physical output to Desktop | Persist stable print-job identity and distinguish submitted/unknown/confirmed states; uncertain submission must not silently create a new job; test timeout/cancellation after physical submission |
| **H-20** — Backup encryption has no key ID/version for rotation recovery | Phase 3 §12.8 specifies AES-GCM manifests/checksums/history; Phase 6 §15.5 drills restore | Put key ID and envelope version in the backup format; retain old decrypt keys under a rotation policy; test restore of pre-rotation backups |
| **H-21** — Operation-status query is anonymous, incomplete, and ambiguous | Phase 2 §§11.1–11.4 includes operation status and a central outcome ledger; §11.2 adds trusted principal context | Authenticate/authorize outcome lookup, scope it to client/principal, use typed operation IDs, and cover every retry-sensitive mutation; test guessed IDs and cross-command ID reuse |
| **M-08** — Purchase/warranty IMEIs bypass checksum/length normalization | Same identity sections as H-12 | Apply one canonical IMEI validator at every write boundary, not only catalog/scanner paths; invalid and checksum-failing values must be rejected |
| **M-09** — Search and overview reads have gaps in bounds and point-in-time consistency | Phase 2 backend/API parity and Phase 3 screen-by-screen cutover | Define paging/cursors and maximum result bounds for each read; choose snapshot consistency for multi-query financial dashboards; test omitted records beyond page limits and large-history performance |
| **M-13** — Transaction runner lacks bounded lock-wait retry behavior | Phase 2 §11.8 preserves PostgreSQL locks; Phase 6 requires integration certification | Define scoped lock timeout, retryable SQLSTATE set, bounded whole-command retry with the same operation ID, and telemetry; test deadlock, timeout, and uncertain commit recovery |
| **L-02** — Entered quantity and base quantity can round inconsistently | Phase 1C §§8.2–8.3 and Phase 1D §9.4 define unit conversion | Define maximum input precision and a single canonical rounding point; assert persisted entered × factor reconciles to persisted base quantity |
| **L-03** — Health endpoint reports healthy without readiness checks | Phase 3 §12.10 mentions installer/services/health checks; Phase 2 adds WorkerRuntimeGuard | Define separate liveness/readiness probes and required DB/schema/worker/backup status; unhealthy dependencies must affect readiness |

## Findings not currently covered by the roadmap

These need new explicit implementation work or acceptance criteria. General references to “preserve stable domain,” “backend hardening,” “accounting integration,” or “hostile certification” do not specify how these defects are corrected.

| Audit finding | Why the roadmap does not currently close it |
|---|---|
| **H-01** — StockAdjustmentMode is stored but ignored | No section defines absolute physical-count semantics, target-minus-current behavior, or zero-count support |
| **H-02** — Serialized condition transfers can move the wrong lot cost | No requirement binds each selected serialized unit’s transfer to its InventoryLotId and exact cost layer |
| **H-03** — Scrap cost may be multiplied by quantity twice | No scrap carrying-value conservation rule or serialized scrap valuation test |
| **H-14** — Net profit omits recognized inventory losses | Reporting/accounting APIs are named, but the required net-profit equation and loss aggregation are not specified |
| **H-15** — Reporting uses host local time instead of configured shop time | No shop business-date/time-zone authority or cross-zone reporting test is specified |
| **H-22** — Purchase, expense, and Thaka dates are caller-controlled | No rule requires the server to derive posted business dates from the configured shop clock |
| **M-06** — Stock-adjustment loss/provenance and lot allocation can be inconsistent | Stocktake and accounting are preserved, but no negative-adjustment allocation, loss recognition, missing-lot failure, or lock-order contract is defined |
| **M-07** — Warranty/reversal paths can bypass stocktake mutation barriers | Stocktake, Warranty and Thaka are each in scope, but no shared mutation barrier across those workflows is required or tested |
| **L-01** — Product attributes accept arbitrary schema versions and fields | Product identity grammar is detailed, but no versioned product-attributes schema/key/type/range registry is defined |
| **L-04** — Shop time-zone fallback silently switches to UTC | The roadmap does not define required shop time-zone configuration, startup validation, or a fail-closed fallback policy |

## Other architecture conflicts from the audit

- **Price overrides (audit architecture-conflict bullet):** the roadmap identifies price authority and API coverage but does not define the permissioned override, reason, below-cost permission, or audit snapshot required by frozen architecture Section 213. Add it to Phase 1D/Phase 2 and the Golden Trace.
- **Production startup gates:** Phase 1 centralizes runtime states and Phase 3 adds services/health checks, but the roadmap does not name the existing `ProductionStartupCoordinator` or require license/schema/disk/recovery gates on both Desktop and Server startup. Add explicit startup wiring and failure drills.
- **Backup verification and reconciliation authority:** Phase 3 includes local backup/restore, reconciliation, and restore drills, so basic local recovery is covered. Remote verification and an authoritative reconciliation-failure state are not defined; keep those as explicit unsupported states or add concrete mechanisms and tests.
- **Audit integrity key configuration/rotation:** Phase 2 separates business audit from machine operations journals, but does not require HMAC to be enabled, key ownership, key rotation, or verification after rotation. Add these if tamper-evident local audit is a production requirement.
- **Operation status as a second recovery authority:** Phase 2’s central `OperationOutcomeLedger` is the right consolidation direction; require legacy business-table operation lookups to delegate to it or remove duplicate recovery authority after migration.

## Planning takeaway

The roadmap directly covers 14 audit findings, partially covers 16, and leaves 10 without a concrete closure path. Treat the 26 partial/uncovered items as still open for implementation planning. In particular, do not mark a finding fixed because its broad feature area appears in a roadmap; close it only when the stated invariant is implemented and its focused test passes.
