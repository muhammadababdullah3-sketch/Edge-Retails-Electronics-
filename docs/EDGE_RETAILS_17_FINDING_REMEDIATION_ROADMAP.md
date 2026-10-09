# Edge Retails — 17-finding remediation roadmap

Authority: user Master Forensic Remediation Prompt v2, 8 October 2026. Only Phase 1 is authorized. Production deployment, mutations, service operations and Git commit/push are prohibited.

## Preflight and ownership

Branch `tracking-remediation-20261002`; HEAD `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`. Pre-existing status, tracked diff and source hashes are retained in `scratch/frontend-phase1-20261008/`. No reset, stash or branch switch.

Material drift from the October 8 audit: source now contains additional `20261007063406_Phase7Pass5ExactWarrantyMultiSourceAuthority` and `20261007112131_Phase7ReceiptVoidIdentityOwnership` migrations. Pass 5 checkpoint describes unfinished D13 matrix and unfrozen source. Neither migration will be applied by this task. Installed evidence in the audit remains historical, not refreshed production verification.

Canonical architecture manifest identifies `docs/Edge_Retails_Final_Architecture_Report_v1.md`, particularly Sections 225 and 233.1: unresolved identity must survive, same-intent replay must preserve identity, frontend never becomes financial authority. Pass 5 frozen authority, whitelist, checkpoint and Tracking certification preserve their separate ownership.

Explicit protected overlap: `RemoteBackendBusinessOperationsService.cs` is owned by Pass 5 Lead for R06. All Application/Domain/Infrastructure/Server changes, migrations, existing Pass 5 tests and tracking authority remain outside this frontend implementation. Shared intent store remains unchanged pending reconstruction/lifecycle review.

Implementation owners: root exclusively owns Customer/Expense/StockAdjustment ViewModels and their dialog bindings, new corresponding frontend tests and these reports. Agent A exclusively owns SuppliersViewModel, SupplierEditDialog/SupplierDetailView and new supplier submission tests. Agent B investigates F04 read-only. Agent C independently reviews contracts/tests/candidate read-only. Three subordinate slots require investigations to finish before a fourth distinct reviewer can run; no concurrent overlapping edit.

## Finding / ownership matrix

| Finding | Evidence / root cause | Source owner | Proposed fix | Backend dependency | Test plan / phase |
|---|---|---|---|---|---|
| F01 | Installed/source delivery mismatch | Deployment | Coordinated identifiable release | Backend/tracking/deployment freeze | Phase 4 parked; installed acceptance |
| F02 | Historical khata mapping failure; correction undeployed | Backend/deployment | Preserve existing correction, approved delivery | Release/schema owner | Phase 4 parked; khata runtime |
| F03 | Installed setter reset + current source pending state | Frontend A; supplier contracts Pass 5 | Immutable submitted supplier intent, atomic gate; durable recovery dependency | Payment replay does not validate complete payload/owner; public outcome scope incomplete | Phase 1; commit/lost response/retry/restart, ledger assertions |
| F04 | Instance-only identity gaps | Frontend with protected adapter overlap | Reuse canonical store/approved replay, fail closed | Protected business adapter; missing reconstruction/outcome support, warranty owner | Phase 1; per-workflow restart/payload/owner matrix |
| F05 | Expense edit conflicts with immutable posting | Frontend / backend finance | Existing authorized void UI | Confirm void/correction contract | Phase 2 planned |
| F06 | N0 hides decimal money | Frontend | Authoritative display precision | Existing money precision only | Phase 2 planned |
| F07 | Async command reentry / mutable state | Frontend root/A | Atomic entry gate, snapshot, visible busy | F03/F04 recovery dependencies; broader purchase/product scope remains tracked | Phase 1; rapid/programmatic clicks, pending field edits |
| F08 | Warranty timeline late result | Frontend / warranty overlap | Selection generation | Protected warranty ownership | Phase 2 planned |
| F09 | Independent directory read generations | Frontend | Unified search/refresh generation | None identified | Phase 2 planned |
| F10 | Failed report retains zero/stale snapshot | Frontend | Persistent unavailable/stale state | Read contract unchanged | Phase 2 planned |
| F11 | Brand absent from POS DTO | Catalog/backend | Approved projection/filter | Catalog contract approval | Phase 3 planned |
| F12 | Local category filter capped at 200 | Catalog/backend | Approved paging/filter contract | Catalog contract approval | Phase 3 planned |
| F13 | Purchase readback requires InventoryManage | Backend permissions / frontend | Authorized detail composition | Permission/read projection owner | Phase 2 planned |
| F14 | Committed purchase readback failure says rejected | Frontend | Preserve confirmed result | Existing authoritative result | Phase 2 planned |
| F15 | Eager pages, serial detail reads | Frontend/backend reads | Bounded paging / batch enrichment | Approved read contract | Phase 3 planned |
| F16 | Diagnostics explicitly unavailable | Backend diagnostics/frontend | Supported protected diagnostics | Diagnostics owner | Phase 3 planned |
| F17 | Customer save error says load | Frontend | Operation-specific message | None identified | Phase 2 planned; not changed in Phase 1 |

## Current external dependency register

- F03 payment: `CreateSupplierPaymentHandler` compares replay supplier/amount only; method/purpose/reference/note/actor are not all compared. Owner must decide canonical fingerprint and actor/session scope. Do not alter protected handler here. Required tests: same-ID changed payload, changed owner, exact one ledger movement.
- F03 outcome: payment success record omits terminal scope required by public Operations GET; refund lacks the same authoritative outcome-ledger integration. Owner must approve scoped reconciliation through existing operation result semantics. No invented endpoint or client-only exactly-once claim.
- F04 store: existing file store persists ID/hash, not reconstructible payload/status. Full restart recovery must retain protected original request under a reviewed lifecycle. This pass cannot claim that in-memory snapshot fixes solve restart.
- F04 expense: remote business adapter is explicitly Pass 5-owned. No overlapping edit without owner handoff.
- F04 warranty: active warranty business authority and unfinished Pass 5 D13 work are protected. Any frontend mutation integration requires explicit contract/file ownership.

`DEPLOYMENT_HELD_BY_BACKEND_GOVERNANCE`. Phase 2/3/4 require separate user authorization. This file records recommendations, not completion or certification.
