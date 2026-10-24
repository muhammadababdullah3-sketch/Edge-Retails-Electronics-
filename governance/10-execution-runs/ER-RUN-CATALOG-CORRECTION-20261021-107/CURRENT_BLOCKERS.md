# Current CAT-107 blockers

Updated 2026-10-22 after the route-matrix and LOGIN-SCROLL correction wave.

**CAT-107 status: OPEN.** It is not accepted, not closed, and carries no phase credit.

## Closed / resolved

1. **Disposable PostgreSQL 18 rehearsal environment** — resolved and attested. Owned cluster on
   loopback `55437` under `%TEMP%\EdgeRetailsCatalogPg_*`, PostgreSQL 18 version asserted, separate
   admin/test roles, schema privileges and table/sequence/schema ownership transferred to
   `er_provisioner`, clean shutdown and full cleanup. Protected ports `5432`/`7150` never contacted.
   Runs on a **default** engine configuration; no `--no-data-checksums`, `wal_log_hints=off` or
   `autovacuum=off` crutches.
2. **Migration stack and model sync** — resolved. `20261021160049_Phase11ThakaCreditRefundTable` and
   `20261021160100_Phase11CatalogV3RelationalIntegrity` are applied; `has-pending-model-changes`
   returns exit 0.
3. **Dry-run no-write proof** — resolved *methodologically*. The previous in-method `CHECKPOINT` /
   catalog pre-warming was removed (it was a persistent write by the preview API, which the master
   prompt forbids even when the later LSN stays stable). WAL/LSN equality is no longer asserted.
   Replaced by a persistent-state fingerprint plus observed `transaction_read_only`, with a
   dedicated non-vacuity negative-control test.
4. **Destructive rollback classification** — resolved. Both `23503` and `23001` are treated as
   rejection.
5. **Route matrix** — resolved for all fifteen mutating `CatalogController` routes. Every route now
   has a controller-level `RequirePermission(InventoryManage)` guard *and* four independent
   assertions (401 anonymous, 403 without permission, not-denied with permission, and never-2xx for
   an unprivileged caller with a malformed body). Coverage is reflection-derived, so a new mutating
   action cannot be silently omitted. See `ROUTE_AUTHORIZATION_EVIDENCE.md`.
6. **Fail-open unit-handler authorization** — resolved for the two catalog sites. Both
   `ConfigureProductUnitsHandler` and `SetProductUnitBarcodeHandler` now deny when the authorizer is
   absent instead of proceeding. Pinned by six regression tests that assert *no write occurred*.
   The seven remaining sites outside catalog are recorded, not fixed — see
   `FAIL_OPEN_AUTHORIZER_AUDIT.md`.
7. **LOGIN-SCROLL** — resolved and now proven by assertions rather than printed diagnostics. Only
   the account list scrolls; the login shell, PIN keypad and sign-in button stay on screen at every
   supported window size (>= 640px). Includes a non-vacuity guard proving the harness can actually
   detect outer scrolling. Tested across window heights 640, 700, 768, 900, 1080px with 1, 12, 40 accounts.
8. **Concurrency and failure-mode proof** — resolved for the seeder/importer. Pinned by
   `CatalogV3ImportAtomicityPostgresTests` on PostgreSQL 18:
   - Mid-transaction simulated interruption rolls back every inserted row (divisions, departments, product classes).
   - Pre-commit simulated failure rolls back entire transaction.
   - Second apply preserves exact persistent-state fingerprint and zero net mutations (0 deletes, 0 inserts, 0 updates, 848 unchanged).
   - Competing concurrent applies serialize under transaction-scoped advisory locks / serializable isolation without partial commits or orphaned records.

## Still open

1. **Deterministic manifest defects (narrowed).** Documented in `MANIFEST_FINDINGS.md`:
   - *Correction*: The claim that aliases/ambiguous terms are omitted was stale and is withdrawn; lines 1163–1173 hash 304 search aliases and 16 ambiguous terms across 10 entity groups.
   - *Open defect A*: `AppliedManifestSha256` ignores `ExtraClasses`/`ExtraRedirects` applied via `TaxonomySeedOptions` because it hashes only static `TaxonomyV3Store.*`.
   - *Open defect B*: Preview and apply target fingerprints use structurally different dictionary keys and cannot be directly compared.
   - *Open defect C*: Apply does not verify the target is unchanged since preview (relies on SERIALIZABLE isolation instead).
2. **Distinct review outstanding.** Independent root/D05 disposition of a frozen candidate is
   required before phase credit.
3. **Authorization ordering (follow-up).** Authorization runs inside the action body, after model
   binding. An authenticated caller without `InventoryManage` who sends a malformed body gets `400`
   rather than `403`. Nothing is mutated and no handler is reached, so no security property is
   violated, but the response code discloses route shape and the ordering is fragile. Both paths are
   now tested; hoisting the check ahead of binding is a separate cross-cutting change. See
   `ROUTE_AUTHORIZATION_EVIDENCE.md`.
4. **Fail-open authorizers outside catalog (follow-up).** 7 call sites outside catalog (Finance cash
   sessions, Sales quotations, Inventory stocktake) remain fail-open if `_authorizer` is null or
   `command.ActorId == Guid.Empty`. Latent but recorded in `FAIL_OPEN_AUTHORIZER_AUDIT.md`.

## Verified results for this wave

| Check | Result | Evidence |
| :--- | :--- | :--- |
| PostgreSQL 18 integration suite (disposable port 55437) | **75 / 75 passed** | `test-results/pg18-20261022-113154-201/`, TRX `75 outcome="Passed"`, `0 Failed` |
| Rehearsal lifecycle steps (initdb → grant → test → stop → verify stopped) | **10 / 10 PASS** | `terminal-result.json`, `status: PASS`, `cleanup_pass: true` |
| Full unit suite | **1419 / 1419 passed** | `EdgeRetails.UnitTests` |
| Login scroll / catalog desktop suite | **62 / 62 passed** | `EdgeRetails.Desktop.PerformanceTests` |
| `dotnet ef migrations has-pending-model-changes` | clean (exit 0) | `verify-ef-model-sync` step |
| Authorization matrix coverage | 15 routes × 4 theories + 1 coverage guard = **61 tests** | TRX breakdown |
| Atomicity, isolation & idempotency | 4 tests = **4 tests** | TRX breakdown (`CatalogV3ImportAtomicityPostgresTests`) |

## Status of the earlier conflicting report

The prior claim in this folder of a clean 9/9 pass with "operational ports untouched" is retained as
a **failed/partial attempt**, not as acceptance evidence. What actually happened, what was wrong with
it, and what was reverted is recorded in `CORRECTION_NOTICE.md`. The `VERIFICATION_OUTCOME.md` in this
folder now records only default-configuration results and states explicitly what remains open.

## Standing statement

CAT-107 stays **OPEN** until the deterministic manifest is complete, concurrency and failure modes
have direct proof, and a distinct root/D05 review of a frozen candidate is recorded. The green
integration run above is necessary evidence, not sufficient acceptance.
