# Phase 11 Wave 4 Hand-Off Document

Artifact ID: `ER-P11-W4-HANDOFF-20261014-003`
Run ID: `ER-RUN-20261014-003`
Authority: `OWNER-UPDATE-20261013-007`, `OWNER-UPDATE-20261013-008/002`, `OWNER-UPDATE-20261014-001`, `OWNER-UPDATE-20261014-002`
Governance Event: `ER-CHANGE-20261014-017`
Governance Dispatch: `ER-DISPATCH-20261014-011`
Citing: `governance/15-reviews/2026-10-14-w3-run002/CONTINUATION.md` (SHA256: `181eb3ae339963e3ece8e39529572f5578c23237979db421dc519a39aa8c5f96`) and sibling `REVIEW.md`
Date: 2026-10-14
Status: `GATED_PENDING_W3_ACCEPTANCE`

---

## 1. Hand-Off Preconditions and Gate Status

| Gate Dimension | Value | Invariant Check |
|---|---|---|
| **Wave 3 Acceptance Status** | `SUBMITTED_FOR_GOVERNANCE_REVIEW` | All 8 tasks implemented, tested, and audited under ER-RUN-20261014-003. |
| **Wave 4 Certification Gate** | `CLOSED (LOCKED_PENDING_W3_ACCEPTANCE)` | Wave 4 activation forbidden until explicit owner authorization. |
| **PostgreSQL 18 Disposable Cluster** | Active on loopback port 55437 | 21/21 durable posting integration tests passed (including T21). |
| **Operational Port 5432 Isolation** | Verified dedicated target guard (port 55437, edgeretails_test_run20261012_001) | Zero operational DB authorization; connection string guards active. |
| **Solution Build Status** | 0 Warnings, 0 Errors | Release configuration build clean. |
| **Wave 3 Desktop Cutover Tests** | 26 passed, 0 failed | Phase11Wave3DesktopCutoverTests 100% green. |
| **Wave 3 Unit Cutover Tests** | 20 passed, 0 failed | Phase11Wave3ApiAndDesktopCutoverTests 100% green. |
| **Full Unit Regression Tests** | 1,301 passed, 0 failed | EdgeRetails.UnitTests 100% green. |
| **Full Desktop Suite Regression** | 294 passed, 0 failed (294 total) | All 5 legacy defects resolved at root cause (REG-W3-001). |
| **Isolated Clean Reconstruction** | VERIFIED PASSED | Base commit 3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2 + patch + untracked builds & passes tests (raw/reconstruction_verification.log). |

---

## 2. Invariants Preserved for Wave 4

1. **Strict Port Isolation**: Port 5432 remains untouched and unauthorized. Any Wave 4 certification runs or rehearsals must continue to target the dedicated disposable PostgreSQL 18 cluster on port 55437 with fail-closed configuration guards.
2. **Desktop POS HTTP Boundary**: Desktop POS components must interact with backend business logic solely through HTTP controller endpoints and validated DTO contracts. No direct DbContext or SQL injection allowed.
3. **Subledger Cost Basis Conservation**: All inventory allocations, transfers, and sales must adhere to fractional costing basis conservation (`RoundCost(unitCost * qty, 6)`).
4. **Cashier Cost Redaction**: All API responses must pass through recursive cost masking, ensuring wholesale costs and margins never reach cashier terminals.
5. **Supervisor Dual-Control**: Period locks take absolute precedence over supervisor overrides; backdating beyond 7 days requires formal governance exception.
6. **Masked Supervisor Credentials**: All supervisor credentials and PINs must be masked in UI and cryptographically zeroed on unload/cancel/disposal.
7. **Scanner Fast-Path & Container Tracking**: Tracking code resolution via `GetPackByTrackingCodeAsync`, stale capacity prompts, duplicate detection, and cable routing must be preserved.
8. **Catalog Identity & Attribute Validation**: Product updates preserve unit IDs, product IDs, and SKU identities while enforcing typed attribute validation.
9. **Stocktake Replay Identity & Loose Variance**: Stocktake separate loose stock counts produce authoritative variance effects, and changed measurement replaying preserves client intent idempotency or triggers conflict.
10. **Isolated Candidate Reconstruction**: Export bundles must remain fully reproducible from true base commit `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`.

---

## 3. Wave 4 Scope Overview (Group 11 & Group 12)

Upon formal governance acceptance of Wave 3, Wave 4 (*Hardening, Performance & Final Certification*) will encompass:
- Group 11: Production migration dry runs, large-volume load testing, and offline-online sync reconciliation.
- Group 12: Final certification gates, end-to-end POS hardware simulation, and release lockdown.

Wave 4 remains locked until authorized by governance event.
