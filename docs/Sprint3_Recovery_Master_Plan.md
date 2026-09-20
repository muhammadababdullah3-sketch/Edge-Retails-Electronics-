# Sprint 3 Recovery Master Plan

Snapshot basis: forensic audit on 2026-09-19 after Antigravity limit was reached.

## Goal
Recover Sprint 3 from the current partially-implemented state, remove compile/runtime blockers, unify transaction behavior, wire all Sprint 3 screens, verify against the live Figma source, and only then produce a final implementation report. No Sprint 3 freeze/commit is allowed without explicit human approval.

## Phase 0 - Safety and baseline
- Preserve a full pre-recovery backup outside the repo.
- Record git branch/status/diff and current build failure.
- Do not reset, clean, mass-format, or discard uncommitted Sprint 1/2/3 work.
- Keep all demo-only behavior explicitly isolated from persistence/backend behavior.

Exit: recoverable baseline exists and no work is lost.

## Phase 1 - Restore buildability
- Complete missing Thaka action ViewModels: Add Material, Record Payment, Final Settlement.
- Complete their WPF dialogs and the Thaka Workspace view.
- Add the required WPF DataTemplates.
- Rebuild immediately before doing wider refactors.

Exit: Debug build succeeds with zero compile errors.

## Phase 2 - Integrate Sprint 3 navigation
- Page factory must create real Sales History and Thaka Projects ViewModels.
- Thaka project selection must open a real Thaka Workspace.
- Workspace Back must return to Thaka Projects.
- Only production screens 01-17 remain primary screens. Workspace/dialog states are subflows, not new primary navigation items.

Exit: all Sprint 3 entry points are reachable from the shell.

## Phase 3 - Normal Sale completion chain
- Replace Sprint 2 direct CompleteSale toast/clear behavior with CompleteSale dialog flow.
- Pass full cart context: subtotal, invoice discount, total, customer, line items, cashier.
- Record the completed transaction through one transaction service.
- Preserve IsProcessing/double-submit protection.
- Save success remains success even if later printing fails; print retry is a separate concern.

Exit: New Sale -> Complete Sale -> recorded transaction works as one flow.

## Phase 4 - One transaction truth
- Sales History must read from the same transaction service used by Complete Sale.
- Remove independent duplicate hardcoded Sales History transaction universe.
- Correct gross sales / returns / net sales semantics.
- Replace hardcoded 18 Sep "Today" with current/demo clock abstraction.
- Split payment state from return state so PARTIAL is not ambiguous.

Exit: a sale completed in POS appears in Sales History and detail without re-creating data.

## Phase 5 - Financial correctness
- Remove double-discount behavior.
- Preserve subtotal, line discount, invoice discount, total, received, change.
- Return eligibility remains decimal.
- Return refund pricing policy must be isolated; final allocation of invoice-level discount remains a named business decision, not silently invented.
- Return processing becomes a separate return record/projection instead of rewriting the original sale facts.

Exit: arithmetic is internally consistent and audited by tests.

## Phase 6 - Stock and quantity correctness
- Product available stock becomes decimal, matching the business requirement.
- Cart increment/add validates against available stock.
- Out-of-stock and over-stock actions are blocked with user feedback.
- Do not post inventory movements yet unless Sprint 4/backend ownership is explicitly enabled.

Exit: UI cannot sell/issue more than demo available stock.

## Phase 7 - Thaka transaction subflows
- Add Material updates workspace material ledger/value.
- Record Payment validates amount and updates payment ledger/paid/balance.
- Final Settlement validates remaining balance and locks the project as SETTLED.
- Settled workspace is read-only for financial actions.
- Overpayment/reopening settled Thaka remain explicit unresolved business rules; recovery code must not invent them.

Exit: active Thaka flow works and settled state locks actions.

## Phase 8 - UI/theme/design cleanup
- Remove hardcoded New Thaka error colors and use shared theme resources.
- Use one return drawer mechanism only.
- Verify 1440x900 and 1366x768 without accidental horizontal scroll.
- Verify Light/Dark mode and existing shell/sidebar/modal/drawer/toast behavior.
- Compare each implemented Sprint 3 screen/state to the verified live Figma nodes by actual content, not frame name alone.

Exit: no resource/theme violations and layouts are usable at both target resolutions.

## Phase 9 - Automated forensic tests
Add Sprint3ForensicAuditTests covering:
- required DataTemplates and page-factory mappings;
- no Canvas;
- resource resolution and theme parity;
- decimal stock/quantity contract;
- stock cap logic;
- single transaction source wiring;
- no hardcoded historical "Today";
- complete-sale dialog wiring;
- Thaka workspace/action file existence and integration;
- settled action lock;
- no hardcoded Sprint 3 theme colors;
- no duplicate local/global return drawer ownership.

Exit: all unit/integration tests pass.

## Phase 10 - Final verification
Run, in order:
1. dotnet restore
2. Debug build
3. all tests
4. dotnet format --verify-no-changes
5. Release build
6. git diff --check
7. runtime smoke verification for Login, Dashboard, New Sale, Complete Sale, Sales History, Sale Detail, Return, Thaka Projects, New Thaka, Workspace, Add Material, Record Payment, Final Settlement
8. both 1440x900 and 1366x768
9. Light/Dark
10. Windows Event Log check after the final runtime pass

Exit: fresh final binaries and final files are the exact artifacts audited.

## Phase 11 - Report and freeze gate
Create docs/Sprint3_Implementation_Report.md with:
- VERIFIED COMPLETE;
- IMPLEMENTED BUT NOT VERIFIED;
- DEFERRED TO LATER SPRINT;
- NEEDS BUSINESS DECISION;
- exact command results;
- exact runtime results;
- known limitations.

Sprint 3 is not "locked" or "frozen" until the user visually accepts it and explicitly approves the final commit/freeze.
