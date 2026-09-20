# Sprint 5 — Full Forensic Audit Report

**Date:** 2026-09-20
**Scope:** Sprint 5 Phases 1–3
**Screens:** Expenses, Customers, Suppliers, Reports, Settings
**Status:** ALL IN-SCOPE FORENSIC FINDINGS RESOLVED
**Manual visual QA:** deferred by product owner
**Backend executable attachment:** deferred by architecture
**Git:** untouched

## Audit method

The audit was not limited to successful compilation.

It covered:
- live Figma contracts;
- primary-navigation replacement;
- WPF/XAML structure;
- state/data-source boundaries;
- finance/reporting rules;
- stock/cost consistency;
- security-sensitive Settings UI;
- deferred backend honesty;
- reference integrity;
- event/lifecycle behavior already hardened through cached singleton-subscribing pages;
- TODO / NotImplemented search;
- Canvas / React / web residue search;
- direct Npgsql / EF / Dapper / connection-string leakage search;
- runtime state smokes;
- Debug and Release build/test gates.

## Baseline structural result

Sprint 5 primary routes are real WPF implementations:
- Expenses
- Customers
- Suppliers
- Reports
- Settings

Supporting workflows are dialogs/drawers. No extra primary screen was introduced.

Searches found:
- no TODO/FIXME/NotImplemented residue in production Desktop source;
- no Canvas/className/script residue in the audited production source;
- no direct Npgsql/DbContext/Dapper/connection-string coupling in the frontend implementation.

## Findings and resolutions

### F-01 — Deferred backend systems falsely appeared live
**Severity:** High
**Area:** Settings / Backup / License / Database

Initial frontend demo labels included Connected, Running, Active and Success while backend/worker attachment is explicitly deferred.

**Risk:** The UI could claim a real database connection, successful backup or active license verification that had never occurred.

**Resolution:**
- Database: Integration Pending.
- Connection: Not Connected · Frontend Shell.
- Worker: Integration Pending.
- License: Preview · Integration Pending.
- Backup status cards: Integration Pending / Preview Data.
- Backup history: Cloud Preview / Sample.
- explanatory text retained beside destructive-looking actions.

**Status:** RESOLVED.

### F-02 — PIN field exposed typed PIN
**Severity:** High
**Area:** Users & Access

The initial Add/Edit User dialog used a normal TextBox for New PIN.

**Risk:** PIN characters were visible.

**Resolution:**
- replaced with WPF PasswordBox;
- PasswordChanged forwards only transient input to the editor ViewModel;
- PIN is cleared on save/cancel;
- no plaintext PIN property exists on the persistent frontend user record.

The backend remains responsible for hashing and authentication.

**Status:** RESOLVED.

### F-03 — Category rename could orphan product references
**Severity:** High
**Area:** Categories & Units / Catalog bridge

Categories are currently referenced by string in the frontend demo product state. Renaming only the settings record would split category identity.

**Resolution:**
- rename captures the old category name;
- all products referencing it are migrated to the new name;
- retail state is notified;
- product counts refresh from the authoritative shared product collection.

**Status:** RESOLVED.

### F-04 — Unit rename missed legacy `Pcs` products
**Severity:** High
**Area:** Categories & Units / Catalog bridge

Figma uses Piece / pc while seeded products use Pcs.

A name-only unit rename therefore matched zero Piece products.

**Resolution:**
Unit matching now recognizes:
- unit name;
- unit symbol;
- plural symbol alias such as pc → pcs.

Runtime forensic smoke confirmed six seeded Pcs product references migrated correctly.

**Status:** RESOLVED.

### F-05 — License flow allowed an arbitrary typed path
**Severity:** Medium
**Area:** License

The first implementation exposed a text path field without a real file chooser.

**Resolution:**
- added Browse file picker;
- filter restricted to .lic and .key;
- path field is read-only;
- save validates File.Exists;
- extension is validated again;
- frontend still does not claim cryptographic activation.

**Status:** RESOLVED.

### F-06 — Invalid Auto-style Thickness layout hack
**Severity:** Medium
**Area:** Backup Settings XAML

A Restore button used `Margin="Auto,0,8,0"`. It compiled but is not valid responsive WPF layout semantics.

**Resolution:**
Replaced the row with explicit Grid columns and a right-aligned action StackPanel.

**Status:** RESOLVED.

### F-07 — Theme selector could become stale
**Severity:** Low
**Area:** Appearance

The theme-change handler raised PropertyChanged without synchronizing the selected field.

**Resolution:**
The handler now assigns `SelectedTheme = _themeService.CurrentTheme`.

**Status:** RESOLVED.

### F-08 — Sprint 5 master plan contradicted locked costing policy
**Severity:** Medium
**Area:** Documentation / Reports contract

The Business Boundaries section still said costing was not approved and Reports must not use weighted-average calculations, while the current backend architecture already locks Moving Weighted Average.

**Resolution:**
Documentation now states:
- MWA is locked;
- Inventory Value remains hidden in the V1 frontend;
- Reports use historical cost snapshots / MWA and must not invent alternate FIFO logic.

**Status:** RESOLVED.

## Reporting forensic result

Reports remain consistent with the current architecture:
- Net Local Sales subtract successful sale refunds in the return period.
- Gross Profit uses historical sale cost snapshots.
- RESTOCK_SELLABLE reverses original COGS.
- non-sellable return dispositions do not restore sellable COGS.
- Purchases are not Expenses.
- Thaka material remains separate from local sales.
- chart periods are zero-filled.
- Dashboard remains chart-free.
- purchase intake uses landed-cost allocation and Moving Weighted Average.
- Other Charges are not double-counted in Purchase Total.

## Settings runtime forensic smoke

PASS:
- last active Owner cannot be removed/demoted;
- duplicate user blocked;
- category rename migrates product references;
- unit alias/plural migration works for seeded Pcs products;
- deferred database status does not claim a live connection;
- deferred license status does not claim verified activation;
- backup history rows are marked Sample.

Observed:
- category references migrated: 1 seeded Lighting product.
- unit references migrated: 6 seeded Pcs products.

## Residual items intentionally outside Sprint 5 closure

These are not unresolved Sprint 5 defects:
- real database/worker health provider;
- real backup/restore execution;
- cryptographic license verification/activation;
- backend permission enforcement;
- full First Setup/License startup flow;
- global missing/empty/loading/error/permission states;
- 1366×768 / 1440×900 manual visual QA;
- full System-theme live OS-change tracking.

They belong to backend attachment or the remaining Sprint 6 frontend-hardening scope.

## Closure criterion

All discovered Sprint 5 in-scope forensic findings were resolved before Sprint 5 closure.

Final formatter / Debug / Release results are appended after the authoritative final gate.

## Authoritative Final Gate

- Formatter verify-no-changes: PASS.
- Debug build: PASS, 0 warnings, 0 errors.
- Debug unit tests: 88 / 88 PASS.
- Debug integration tests: 1 / 1 PASS.
- Release build: PASS, 0 warnings, 0 errors.
- Release unit tests: 88 / 88 PASS.
- Release integration tests: 1 / 1 PASS.
- Sprint 5 forensic runtime smoke: PASS.
- All discovered Sprint 5 in-scope findings: RESOLVED.
- Manual visual QA: deferred.
- Git: untouched.
