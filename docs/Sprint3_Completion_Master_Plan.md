# Sprint 3 Completion Master Plan

Date: 2026-09-19
Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`

## Completion rule
Sprint 3 is complete only when the final source is integrated, Debug and Release builds pass, dedicated Sprint 3 tests pass, format and git-diff gates pass, the complete runtime journey is exercised, Light/Dark and 1366x768/1440x900 are checked, Windows Event Log shows no new crash, and the final report is written. No freeze or commit without explicit approval.

## Phase 0 - Safety
- Preserve current workspace with a full pre-completion snapshot and git patch.
- Keep Sprint 1/2 functionality intact and avoid unrelated rewrites.

## Phase 1 - Shared demo state
- Replace disconnected POS/Thaka project lists with one shared Thaka project store.
- Replace disconnected POS/Add Material product catalogs with one shared product catalog.
- Keep quantities decimal and stock mutable in one place.
- Store each Thaka project's material and payment ledgers in shared state.

## Phase 2 - Real Sprint 3 integration
- Wire POS Thaka Material Issue into the selected project's ledger and shared stock.
- Ensure New Thaka immediately appears in POS Thaka selection.
- Ensure Add Material and POS material issue both reduce the same stock.
- Ensure Record Payment and Final Settlement update the project list KPIs and status.
- Ensure settled projects become read-only and disappear from active POS selection.
## Phase 3 - Sales and returns integrity
- Preserve the working Complete Sale -> transaction service -> Sales History pipeline.
- Keep payment status and return status separate.
- Keep original completed sales immutable and record returns separately.
- Keep invoice-level discount returns blocked until the allocation rule is approved.
- Verify totals, returns, net sales, stock caps, and decimal quantities.

## Phase 4 - UI and code hygiene
- Fix Thaka workspace status badge tone.
- Remove mojibake/encoding damage.
- Normalize CRLF/UTF-8 according to repository rules.
- Remove trailing whitespace and make `dotnet format --verify-no-changes` and `git diff --check` pass.
- Preserve existing Figma-aligned layout and theme resources.

## Phase 5 - Forensic verification
- Add `Sprint3ForensicAuditTests.cs` for shared state, navigation, sale flow, return safety, stock caps, Thaka updates, settled locks, and resource invariants.
- Run Debug build, Release build, all tests, format verification, and diff check.
- Run the application through Login -> Dashboard -> New Sale -> Complete Sale -> Sales History -> Detail -> Return -> Thaka Projects -> New Thaka -> Workspace -> Add Material -> Record Payment -> Final Settlement.
- Verify Light/Dark, 1366x768, 1440x900, no accidental horizontal scrolling, and no new Windows crash event.
- Compare final Sprint 3 screens to the verified live Figma nodes.

## Phase 6 - Report and freeze gate
- Write `docs/Sprint3_Implementation_Report.md` with verified complete, deferred, and business-decision items.
- Do not claim Sprint 3 locked or frozen until explicit approval and an actual freeze/commit.
