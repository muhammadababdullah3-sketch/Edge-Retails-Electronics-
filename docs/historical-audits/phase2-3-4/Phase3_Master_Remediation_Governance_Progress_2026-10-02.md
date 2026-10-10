# Phase 3 master remediation — governance progress

Date: 2026-10-02. This is an interim implementation report, **not final certification or deployment approval**.

## Scope and checkpoint

Continue the existing 1.0.11 candidate22 installation and preserved source checkpoint. Phase 1 and Phase 2 remain protected. No Phase 4 work or full Rs. 5,000,000 inventory population is authorized in this continuation.

The user's latest scope correction defers server-side backup implementation to later phases. Prior uninstalled backup source/evidence is preserved, its new key-ring implementation is not composed into production DI, and its remaining defects are not claimed as certified. Existing backup protection requirements for any operational pilot must be resolved explicitly; deferral is not evidence of a fresh verified backup.

Read-only runtime inspection at 2026-10-02 05:58 UTC confirmed:

| Item | Observed result |
|---|---|
| Installed Desktop | `C:\Program Files\Edge Retails\EdgeRetails.Desktop.exe`, ProductVersion 1.0.11 |
| Server | Running / Auto, PID 3748 |
| Worker | Running / Auto, PID 3764 |
| PostgreSQL 18 | Running / Auto, PID 4032 |
| `/api/system/ready` | HTTP 200; Ready; canConnect=true; hasPendingMigrations=false; maintenanceState=Normal |
| Desktop authentication | Installed normal client opened; Amir/Owner sign-in screen observed; human PIN entry requested |

No source corrections described below have been installed. The operational pilot and actual committed-data label pack are not complete.

## Architecture retained

Business ProductCode maps to `Product.Sku`. Product remains supplier independent. SupplierCode maps to `DealerCode`. Each `(SupplierId, ProductId)` SupplierProduct retains its own next-unused sequence. Exact labels encode the existing `InventoryUnit.TrackingCode`; quantity/length labels use existing ProductUnit/barcode authority and create no exact identities. Serial and IMEI remain separate manufacturer identities.

No migration was generated or edited. No operational SQL business insertion, sequence manipulation, PIN collection, service/configuration/registry/ACL mutation, Git commit/push/reset/clean, or reinstallation occurred in this continuation.

## Preserved implementation and proved defects

| Area | Correction or completed source work | Evidence and limit |
|---|---|---|
| Multi-supplier intake | Authoritative supplier/purchase context, remaining base quantity and tracking flags; one-product committed intake; explicit existing IDs | Focused Desktop and real PostgreSQL coverage; installed operator pilot pending |
| Concurrent receiving | Existing canonical product resource lock before stock/cost reads | Real PostgreSQL RED reproduced stock/cost creation race; subsequent focused GREEN |
| Purchasing projections | Native UTC DateTime Dapper rows mapped to DateTimeOffset DTOs | Real PostgreSQL header/return projection failures reproduced and corrected |
| Sequence persistence | Strict envelope validation, propagating failures, atomic flushed writes, cross-process lease, refreshing existing instances | Earlier focused GREEN; independent review subsequently found additional gaps listed below |
| Labels | Exact/product document sources, vector PDF Code128 output, normal Desktop print/export/reprint commands, explicit audit warnings | Synthetic layout/decode and fixture-source proof; actual pilot PDFs and installed print path pending |
| Shared input UI | Consistent input/search/focus templates | 31 focused Desktop tests PASS; installed visual checks pending |
| Cashier creation | Owner-authorized normal Settings adapter, existing Cashier role/PBKDF2 authority, atomic user/audit/outcome, scoped replay, transient human PIN entry | 20 Unit/controller/status + 7 Desktop PASS; actual PostgreSQL concurrency/rollback and installed account creation pending |

## Terminal evidence

Every required final command will additionally be recorded with exact command, exit, passed/failed/skipped, database provider and completion status. Historical failures remain retained.

| Evidence directory / result | Exit | Passed | Failed | Skipped | Provider | Meaning |
|---|---:|---:|---:|---:|---|---|
| `postgres-behavior-red` | 1 | 7 | 6 | 0 | PostgreSQL 18 / Npgsql | Actual defect proof; migrations/model/cleanup PASS |
| `postgres-focused-green-final` | 0 | 22 | 0 | 0 | PostgreSQL 18 / Npgsql | 13 actual DB cases + 9 supporting controller cases; migrations/model/cleanup PASS |
| `postgres-protected-regression` | 0 | 107 | 0 | 0 | PostgreSQL 18 / Npgsql | Protected/relevant regression, migrations/model/cleanup PASS; not full final certification |
| `tracking-missing-green` | 0 | 107 | 0 | 0 | None | Supporting Unit high-water/PDF/protected receiving/DI checks |
| `labels/workflow-green-corrected` | 0 | 37 | 0 | 0 | None | Supporting Desktop label/intake workflow |
| `labels/qa/independent_barcode_decode.json` | 0 | 14 decoded | 0 | 0 | None | Synthetic renderer fixture at 300 DPI, no mismatches; not actual pilot labels |
| `tracking-authority-review-red/authority-review-red-corrected.trx` | 1 | 8 | 5 | 0 | None | New independent-review defects reproduced |
| `pilot-cashier-tests/cashier-unit-green-attempt2.trx` | 0 | 20 | 0 | 0 | EF InMemory / supporting only | Unit/controller/status GREEN |
| `pilot-cashier-tests/cashier-desktop-green-attempt1.trx` | 0 | 7 | 0 | 0 | None | Desktop/remote supporting GREEN |

Paths above are relative to `artifacts/master-remediation-20261002`, except `pilot-cashier-tests`, which is directly under `artifacts`. Source changes since a terminal result require the affected gates to run again before final freeze.

## Independent review findings being corrected

Three High tracking findings remain open until implementation and regression:

1. A fresh process silently treats loss of both authority files as a new zero authority.
2. Replaying a valid older manifest lowers known maxima, including across restart.
3. Leaf-only checks do not enforce trusted owner/effective ACL/ancestor custody or reject ancestor reparse paths. Public machine-name HMAC does not establish protection against an unauthorized writer.

The reviewed correction requires explicit initialization, strict established artifacts, componentwise monotonic checkpoint maxima, serialized reads/writes and fail-closed crash divergence. Trusted custody is enforced before publication. A checkpoint does not claim to detect coordinated rollback by an administrator controlling all protected custody. The installed legacy user-owned manifest must not be silently promoted; an approved operational custody/history migration remains a deployment gate.

Medium label findings are also being corrected: atomic complete pack publication, truthful generation versus workstation publication audit, audit-failure visibility, attempt identity before print submission, restart-safe handling of unknown print outcomes and selection of only definitely failed units for ordinary retries. Printer submission is not proof of physical paper output.

## Test-change classification

New behavioral cases are **NEW_COVERAGE**. Fixture-only corrections are **HARNESS_CORRECTION**: PowerShell native child-handle capture/exit handling, terminal TRX capture, rollback injection after actual sequence reservation, realistic label unit display length, immutable TrackingMode fixture construction, missing braces, and testing the canonical ObjectResult HTTP 401 shape.

Existing no-reuse, identity/provenance, concurrency, permission and barcode assertions remain. No skip change or reduced concurrency barrier is authorized. Added exact-exception assertions strengthen causal proof. All corrections must be independently reviewed before final certification.

## Required remaining gates

- High-water and label corrections: RED → minimum source fix → focused GREEN → protected PostgreSQL regression.
- Cashier adapter real PostgreSQL atomicity/concurrency/rollback/replay and fresh independent security review.
- Complete Unit/API/Integration/Desktop/performance gates, Debug and Release builds, zero-to-latest migrations and EF model alignment, with terminal counts and no required skipped gates.
- Concrete deployment/custody migration approval and approved Release installation only after relevant regressions; no production `dotnet run` workaround.
- Authenticated installed five-product pilot, exact/product PDF packs and manifest from actual committed data, independent decode of all actual codes, physical/scan checks and business reconciliation.
- Workspace freeze after all implementation and regression tasks terminate, then a **fresh independent read-only final certifier**. Implementation agents and reviews are not final certification.

## Current decision

**Phase 3 remains open. Full inventory population is NOT APPROVED.** No final success claim is supported. Continue the unfinished source corrections and certification gates; retain deferred server-side backup work in the later-phase handoff.

The detailed live command and gate ledger is `docs/Master_Remediation_Tracking_Labels_Ledger_2026-10-01.md`.
