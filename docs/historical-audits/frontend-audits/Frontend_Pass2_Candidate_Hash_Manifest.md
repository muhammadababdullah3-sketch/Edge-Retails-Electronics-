# Edge Retails Frontend Pass 2 — Corrected Candidate Hash Manifest

**Pass Identity:** Frontend Pass 2 — Keyboard focus visibility and interactive-state architecture, surgical correction and recertification
**Candidate Frozen:** 2026-10-04 15:03 +05:00, after scoped source edits and focused test stabilization
**Branch:** `tracking-remediation-20261002`
**Base / HEAD:** `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`
**Architecture:** Frozen single-machine WPF desktop (.NET 10)
**Scope:** Pass 2 frontend candidate files only. No backend source/test file is attributable to this correction.

## Candidate source and test files (SHA-256)

| Relative path | SHA-256 | Candidate role |
|---|---|---|
| `src/EdgeRetails.Desktop/Resources/Brushes.xaml` | `04CA4B2BAE233F57F98DA83B480A0BAA5CA8940C1FF99D2599E6EB69F76CA77D` | Shared focus-ring fallback token (original Pass 2 candidate). |
| `src/EdgeRetails.Desktop/Resources/Themes/Light.xaml` | `D306F1045232C5151853FA76E925A8531BDE01EEE8B7E1D282AC02A11D04B90C` | Light focus-ring theme token. |
| `src/EdgeRetails.Desktop/Resources/Themes/Dark.xaml` | `E4308702000573154E3F6E89811F9E757A69AB17C221A933E1A9FF767DC055E9` | Dark focus-ring theme token. |
| `src/EdgeRetails.Desktop/Resources/Buttons.xaml` | `CA9F6F081ED6E1BB2DEEA7C9C676FD41A59CA1BB1F5D0E9F3C23707B8ADE992B` | Shared button focus adorners and focused-state contracts (original Pass 2 candidate). |
| `src/EdgeRetails.Desktop/Resources/Inputs.xaml` | `872D874461826F797DFD4E423E822150A7C0E302DF991F60A57A801E79DC0AA0` | Checkbox/radio and input focus token contracts; preserve embedded geometry. |
| `src/EdgeRetails.Desktop/Resources/Tables.xaml` | `CAA2E78EDAC1ED5CB64F7507A7859B9AA9644C65D56A425BA240B31EB2D9D6F2` | DataGrid active-cell focus overlay with invariant cell border thickness. |
| `src/EdgeRetails.Desktop/Controls/SearchBox.xaml` | `2E4BEA99F8745050FD5408EC5AD4CBB305F9EA9D08E437B4DECC79E74CEAD1D5` | SearchBox focus-ring token consumer. |
| `src/EdgeRetails.Desktop/Views/ShellView.xaml` | `C1E59EE291A6DA44E9D834A0F00021BDE568525943C52C1341B5B22B516B6B68` | Non-modal page host uses `Continue` to permit shell traversal. |
| `src/EdgeRetails.Desktop/Views/PosView.xaml` | `17ED9C429024237B29E524C88F9A77253FD13741935F2B03D30E92C4CE1E9342` | POS discount host focus indication; POS embedded stepper/remove/complete-sale action keyboard visibility. |
| `src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml` | `19BDF77352212D4673AB1908BCA4B7DFF8D2E980B84AFF4348F4452A3A036403` | Amount host focus indication and visible keyboard focus for receipt, quick-cash, payment-method and primary actions. |
| `src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml` | `A4B8DCC130135DF77B3991565E5FBAFAD12D88DE0EE4BA7F7DAABC9FF6D96A83` | Quantity-host focus indication, stepper/card keyboard visuals, and explicit OneWay display bindings for read-only Brand/Sku. |
| `tests/EdgeRetails.UnitTests/FrontendPass2FocusAccessibilityTests.cs` | `85B0E83BFB4FDB8F13D202EE238814499A4BB33FA7037F42134D793C0059E473` | Focus contracts, embedded production consumers, DataGrid focus trigger and geometry, corrected shell navigation, and Pass 1 geometry contracts. |

## Evidence harness (not a production candidate file)

` scratch/FrontendPass2RuntimeProbe/ ` is a standalone WPF runtime probe using actual application resource dictionaries, the production ShellView/POS view, production Complete Sale and Sales Return dialogs, and a production-resource DataGrid equivalent. It uses synthetic keyboard input and demo/in-memory data only. The harness and runtime log are evidence artifacts; they are not part of the shipping candidate and make no backend or shop-database connection.

## Scope and attribution

The manifest intentionally contains no backend file. The working tree contains numerous unrelated pre-existing Phase 7, tracking, and documentation changes from the wider session. Those files are excluded. Some listed WPF views also contain pre-existing changes from that wider work; each row hashes the full current file snapshot, while the correction attribution is limited to the Pass 2 focus/navigation changes and the two read-only binding-mode fixes described in the verification report. The whole workspace is not represented as clean or isolated.

The verification report and this manifest are documentation artifacts and are not listed as source/test candidate hashes. Recompute the listed hashes before distributing or committing the candidate.

## Verified evidence checksums

- Runtime log: scratch/FrontendPass2RuntimeProbe/bin/Release/net10.0-windows/pass2_keyboard_runtime_evidence.txt — F17FA162613B622CA38AC6FD92EDFBD9BF3C2CA6609951EA92A1CB1493A74E4F.
- Probe source (evidence harness, not a production candidate file): scratch/FrontendPass2RuntimeProbe/Program.cs — 36039AE191E98E157D13C3B4CD4187AB833A343A624FE14C4C4B6A6CE4C2ACBA.
- Independent read-only candidate check: **ACCEPT**; 12 source/test hashes matched; no High or Medium blocker reported.
