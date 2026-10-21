# Stage 1: Reproduction of Root Findings & Forensic Discrepancy Baseline

- **Run ID**: `ER-RUN-FE-FINAL-20261020-001`
- **Stage**: 1 — Reproduction and Forensic Inspection
- **Baseline Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Date**: 2026-10-20

## 1. Executive Summary

Stage 1 established the objective forensic baseline for the EdgeRetails POS Desktop frontend redesign. Rather than relying on historical summaries, the codebase was inspected at the file and line level. 12 distinct forensic issues (`FE-IND-001` through `FE-IND-012`) were cataloged and cross-referenced against the 42 source-defined redesign rows (`FE-RD-01..42`) and the 10 board decision packages (`FE-AP-01..10`).

## 2. Catalog of Re-produced Findings

1. **FE-IND-001: Approval Contradiction Between Governance Artifacts**
   - File `board/current/FRONTEND_REDESIGN_APPROVAL_STATUS.json` recorded status as `APPROVED`, whereas historical run delivery notes still marked it as `PENDING`.
   - *Reconciliation*: Resolved under explicit owner instruction to execute to 100% completion while preserving transparency.
2. **FE-IND-002: Missing Historical Source Receipt**
   - Historical claim `OWNER-UPDATE-20261020-008` had no physical JSON receipt on disk.
   - *Disclosure*: Documented as an evidence defect; zero fabricated receipts generated.
3. **FE-IND-003: Large Mixed Working Tree Isolation**
   - The working tree contained ~170 modified files belonging to Phase 11 / MON-W4 / local-flow tasks.
   - *Remediation*: Isolated Desktop presentation layer modifications strictly to `src/EdgeRetails.Desktop/` and `tests/EdgeRetails.Desktop.PerformanceTests/`.
4. **FE-IND-004: Adherence to Honesty Rules**
   - Prior iterations claimed completion without compiling or running real WPF views.
   - *Remediation*: Full C# .NET 10 compilation and test suite execution enforced.
5. **FE-IND-005: Absence of Rendered Visual Proof**
   - Prior runs contained no visual image bitmaps demonstrating real WPF rendering.
   - *Remediation*: Implemented `RenderedVisualProofTests.cs` using `RenderTargetBitmap` in an STA thread to produce 10 high-resolution PNGs across Light/Dark themes and DPI settings.
6. **FE-IND-006: Cashier Touch Ergonomics Mismatch**
   - Cart steppers and remove button were 28x28 DIP; Complete Sale CTA was 46px.
   - *Remediation*: Cart steppers enlarged to 36x36 DIP; remove button enlarged to 36x36 DIP hit bounds; Complete Sale CTA height set to 52 DIP in `PosView.xaml`.
7. **FE-IND-007: Unicode Raw Text Close Buttons**
   - 27 dialogs and views used raw unicode text `Content="✕"`, causing inconsistent font-dependent rendering.
   - *Remediation*: Added vector `Icon.Close` `PathGeometry` in `NavigationIcons.xaml` and `Button.Close` style in `Buttons.xaml`; converted all 27 files to vector styling.
8. **FE-IND-008: Identity Persistence Across Session Types**
   - Verified that Store Owner identity (`"Store Owner"`, initials `"SO"`, role `"Owner"`) properly flows through `DemoIdentityService`, `DesignPreviewSessionContext`, and backend PostgreSQL `identity.users` into `AppTopBar` and `AppSidebar`.
9. **FE-IND-009: Mathematical WCAG 2.2 Gradient Stop Contrast**
   - Gradient stops evaluated along the linear gradient vector ($	heta pprox 40.36^\circ$). Start stop luminance adjusted so that 100% of gradient stops exceed 4.5:1 AA / 7.0:1 AAA.
10. **FE-IND-010: SC 1.4.11 Non-Text Focus Indicator Contrast**
    - Light focus ring `#4F46E5` achieves 5.86:1; Dark focus ring `#8C7BFF` achieves 5.59:1 against canvas surfaces (exceeding the 3.0:1 requirement).
11. **FE-IND-011: Automated Test Evidence & Compiler Diagnostics**
    - Verified 1,811 automated tests pass with 0 failures; build succeeds with 0 warnings and 0 errors.
12. **FE-IND-012: Authoritative Deposition & Independent Audit**
    - Package deposited into `Edge Retails Goverence Body\governance-execution-runs\ER-RUN-FE-FINAL-20261020-001` with automated readback verification.
