# Edge Retails — Old frontend removal & new frontend attachment plan

**Date:** 2026-10-25  
**Audience:** mahi (implementation owner)  
**Authority baseline:** `ER-RUN-FE-FINAL-20261020-001` (FE-RD-01..42, DS-01..14), `EDGE_RETAILS_FRONTEND_UIUX_FINAL_CERTIFICATION.md`  
**Out of scope:** Phase 11 backend closure, full API/backups work (separate track)

---

## 1. What we mean by “new” vs “old”

| Term | Meaning in this repo |
|------|----------------------|
| **New frontend** | Oct 2026 redesign already in tree: `Resources/*` (themes, cards, buttons, …) + canonical `Views/*` bound to design tokens (`DynamicResource`, `Card.*`, `Text.*`, `Button.*`). Production path: **`DesktopApiClient` + `Remote*` services** → Server `/api/*`. |
| **Old frontend (to remove or gate)** | (A) **In-process EF desktop path** — `BackendRuntime.CreateDesktopServiceCollection` + `BackendPurchasingInventoryService`, `BackendThakaService`, `BackendDashboardService`, etc. (B) **Fallback placeholder navigation** — `PlaceholderPageView` for unknown targets. (C) **Straggler UI** — dialogs/views with inline `FontSize` / hex colours instead of typography tokens. (D) **Ambiguous “two apps”** — DEBUG preview feels like a second product (demo data + no server) vs Release remote UI. |
| **Keep (not “old UI”)** | `Demo*` services for **explicit** `--sprint1-preview` / design-time only; `Sprint1Verification` **DEBUG-only** gallery; `BackendTransactionService` (name is legacy but it is the **production** mutation façade over gateway). |

There is **no separate “new frontend” folder** to copy in — attachment means **one runtime, one design system, zero dead paths**.

---

## 2. Target end state

```text
[WPF Views — 19 screens + dialogs]
        ↓  only DynamicResource / shared styles
[ViewModels — PageViewModelFactory]
        ↓  Release: always Remote* + BackendTransactionService(scope+api)
        ↓  DEBUG: optional Demo* only via explicit flag
[DesktopApiClient + RemoteApplicationGateway]
        ↓
[EdgeRetails.Server /api/*]
```

**Definition of done**

1. Release build never registers `AddEdgeRetailsInfrastructure` in Desktop.  
2. `PageViewModelFactory` has **no** `backendScopeFactory` branch for feature services (only remote or explicit demo).  
3. Every navigable screen uses a real ViewModel/View (no `PlaceholderPage` in production nav).  
4. Dialog audit: 0 raw hex colours; close buttons use `Button.Close`; typography from `Typography.xaml`.  
5. `dotnet test` Desktop.PerformanceTests + FE forensic tests green; light/dark smoke on Sprint1 or automated view-instantiation tests.

---

## 3. Phased plan

### Phase 0 — Inventory & freeze (0.5 day)

| Task | Action |
|------|--------|
| 0.1 | Export checklist from `governance/.../ER-RUN-FE-FINAL-20261020-001/STAGE5_CANONICAL_FE_REDESIGN_CROSSWALK.md` — mark each FE-RD row **Attached / Partial / Missing**. |
| 0.2 | Grep audit: `FontSize="`, `FontWeight="`, `#` hex in `Views/` and `Controls/` → spreadsheet of files. |
| 0.3 | Confirm **your** colour/card tweaks live in `Resources/Themes/*.xaml` + `Cards.xaml` (single source of truth). |
| 0.4 | Screenshot baseline: Dashboard, POS, Login (Light + Dark) for before/after. |

**Deliverable:** `FE_ATTACHMENT_STATUS.csv` in `governance/10-execution-runs/ER-RUN-FE-ATTACH-20261025-001/` (create on Phase 1 start).

---

### Phase 1 — Single desktop runtime (1–2 days) **P0**

**Goal:** Remove the hidden “second frontend” (local EF adapters).

| Step | Work |
|------|------|
| 1.1 | Delete or `#if false` obsolete path: stop exposing `CreateDesktopServiceCollection` from public Desktop API; document “Desktop never hosts EF”. |
| 1.2 | Remove `BackendPurchasingInventoryService`, `BackendThakaService`, `BackendBusinessOperationsService`, `BackendOperationsService`, `BackendDashboardService`, `BackendSettingsService`, `BackendProductManagementService`, `BackendWorkflowReadService` **or** move to `tests/` harness only if still needed for unit-less integration. |
| 1.3 | Simplify `PageViewModelFactory`: constructor takes `DesktopApiClient?` + `IServiceScopeFactory?` **only** for `BackendTransactionService` scope (gateway), not for per-feature Backend* types. |
| 1.4 | `App.xaml.cs`: production path unchanged; DEBUG preview sets `apiClient = null` and wires **Demo*** only (no scope factory). |
| 1.5 | Fix `RemoteApplicationGateway` `receive-intake` → `intake` while touching gateway (avoid reviving Backend* intake). |

**Verification:** Release run against local Server; smoke all nav items; `EdgeRetails.Desktop.PerformanceTests` full suite.

**Risk:** Tests that constructed in-process Desktop scope — repoint to Server test host or keep minimal `TestDesktopHost` project.

---

### Phase 2 — Navigation & shell completeness (0.5 day) **P1**

| Step | Work |
|------|------|
| 2.1 | Audit `ShellViewModel` / sidebar: every item maps to `NavigationTarget` with a **real** page in `PageViewModelFactory` (not `_ => placeholder`). |
| 2.2 | Remove `PlaceholderPageViewModelFactory` from default switch; keep only for unknown enum guard → log + “unsupported” toast. |
| 2.3 | Ensure `AppShell` (`AppTopBar`, `AppSidebar`) uses only theme tokens (no stale branding assets). |

---

### Phase 3 — Design system attachment sweep (2–4 days) **P1**

**Goal:** Visual upgrade you did on tokens applies everywhere.

| Step | Work |
|------|------|
| 3.1 | **Dialogs first** (highest inconsistency): `PackPickerDialog`, `ConfirmationDialog`, `PhysicalIntakeDialog`, `ExpenseEditDialog`, … → `Text.SectionTitle`, `Button.Primary`, `Card.Default`. |
| 3.2 | **Login / FirstSetup** — replace remaining hex literals with `Brush.*` (grep shows concentrated in `LoginView.xaml`). |
| 3.3 | Screen pass in roadmap order: POS → Dashboard → Thaka → Purchases → Inventory → Settings (per FE-RD crosswalk). |
| 3.4 | `KpiCard` control — confirm uses `Card.Stat` + KPI brush keys from theme. |

**Verification:** Sprint1Verification page as living styleguide; manual theme toggle; optional re-run FE contrast scripts if present in `scripts/`.

---

### Phase 4 — Demo vs production clarity (0.5 day) **P2**

| Step | Work |
|------|------|
| 4.1 | Rename UX: DEBUG preview window title / banner **“Design preview — demo data”**. |
| 4.2 | Never use `DemoTransactionService` when `apiClient != null`. |
| 4.3 | Document launch modes in `src/EdgeRetails.Desktop/README.md` (Release = Server required; Debug flags table). |

---

### Phase 5 — Backend UI contract gaps (parallel, 1–2 days) **P2**

Frontend attachment fails if API missing:

| Gap | Frontend impact | Owner |
|-----|-----------------|--------|
| `/api/backups/*` missing on Server | Settings backup UI broken | Server: add controller or remove/disable UI until worker exposes API |
| Commercial exchange UI | Optional (Option A API-only) | Product decision |
| Quotation write APIs | If POS quotations UI exists | Server routes or hide UI |

---

### Phase 6 — Closure & regression (1 day)

| Step | Work |
|------|------|
| 6.1 | New run folder `ER-RUN-FE-ATTACH-20261025-001`: TRX logs, 10 PNG proofs (reuse FE-FINAL method). |
| 6.2 | Update `EDGE_RETAILS_FRONTEND_UIUX_FINAL_CERTIFICATION.md` addendum: “attachment complete, in-process path removed”. |
| 6.3 | Manifest: hash `Resources/`, `Views/`, slimmed `Services/Remote*`. |

---

## 4. Suggested execution order (one sprint)

```text
Week focus:
  Mon–Tue  Phase 0 + Phase 1 (runtime)
  Wed–Thu  Phase 3 dialogs + Login + POS
  Fri      Phase 2 + Phase 6 tests
  Parallel Phase 5 backups API when Server owner available
```

---

## 5. What we will **not** do in this plan

- Rewrite ViewModels business logic or Phase 11 handlers.  
- Merge two different design systems from outside the repo without copying into `Resources/`.  
- Remove `ThemeService` or Light/Dark themes.  
- Delete `Sprint1Verification` (valuable DEBUG gallery).

---

## 6. Immediate next step (when you say “implement”)

1. Create `ER-RUN-FE-ATTACH-20261025-001` evidence folder.  
2. Execute **Phase 1.3–1.4** (`PageViewModelFactory` + remove Backend* service classes) in a focused PR.  
3. Run full `EdgeRetails.Desktop.PerformanceTests` (486 tests) before any visual sweep.

---

## 7. Sign-off

| Milestone | Criteria |
|-----------|----------|
| **Runtime attached** | Phase 1 complete, no Desktop EF in Release |
| **Visually attached** | Phase 3 grep clean + owner visual OK |
| **Contract attached** | Phase 5 P0 gaps resolved or UI honestly disabled |
| **FE attachment closed** | Phase 6 evidence + certification addendum |
