# Edge Retails — user-reported UI defect pack

Date: 2026-10-05, Asia/Karachi. Verdict: **UI_DEFECT_PACK_PARTIALLY_RESOLVED**.

The corrections below are accepted at the inspected runtime geometry. This is not a full frontend, backend, release, or shop-data certification. Missing acceptance evidence is listed explicitly. No commit, push, or new frontend pass was performed.

## Candidate

Workspace: `C:\Users\muham\OneDrive\Desktop\Point of Sale`.

Files changed for this bounded correction (paths relative to the workspace):

| File | Correction |
|---|---|
| `src/EdgeRetails.Desktop/Controls/SearchBox.xaml` | Watermark uses the same embedded TextBox renderer; hover border. |
| `src/EdgeRetails.Desktop/Resources/Inputs.xaml` | Noninteractive watermark style; custom ComboBoxItem/arrow templates; compact item typography; popup/focus stroke. |
| `src/EdgeRetails.Desktop/Resources/Themes/Light.xaml` | Shared hover-border brush. |
| `src/EdgeRetails.Desktop/Resources/Themes/Dark.xaml` | Shared hover-border brush. |
| `src/EdgeRetails.Desktop/Views/PosView.xaml` | Embedded search renderer, matching watermark, effective host focus/hover; remove duplicate inner focus adorner. |
| `src/EdgeRetails.Desktop/ViewModels/PosViewModel.cs` | Price Check opens a visible query dialog, including empty input, and delegates to the existing read authority. |
| `src/EdgeRetails.Desktop/ViewModels/PriceCheckViewModel.cs` | Guarded async query, result/error state, distinct identity handling, read-only result presentation. |
| `src/EdgeRetails.Desktop/Views/Dialogs/PriceCheckDialog.xaml` | Query field, Check price action, Enter binding, visible feedback, bounded close area. |
| `src/EdgeRetails.Desktop/Views/Dialogs/SaleCustomerPickerDialog.xaml` | Content-driven two-line rows, minimum height64, wrapping, reserved close column. |
| `src/EdgeRetails.Desktop/Controls/ModalHost.xaml` | Presenter cannot become an unintended keyboard focus target. |
| `src/EdgeRetails.Desktop/Controls/ModalHost.xaml.cs` | Focus a real enabled content control on open/focus recovery. |
| `src/EdgeRetails.Desktop/Views/SalesHistoryView.xaml` | Selected/hover/pressed/focus authority in one style; Boolean selection conversion; selected hover preserves contrast. |
| `src/EdgeRetails.Desktop/Resources/Brand.xaml` | New vector mark. |
| `src/EdgeRetails.Desktop/App.xaml` | Load vector branding resource. |
| `src/EdgeRetails.Desktop/Controls/AppSidebar.xaml` | Vector icon and readable theme-aware wordmark. |
| `src/EdgeRetails.Desktop/MainWindow.xaml` | Vector window icon. |
| `src/EdgeRetails.Desktop/Views/LoginView.xaml` | New icon/wordmark. |
| `src/EdgeRetails.Desktop/Views/FirstSetupView.xaml` | New icon/wordmark. |
| `src/EdgeRetails.Desktop/Resources/Buttons.xaml` | Line-ending normalization during resource editing; no new button geometry contract. |
| `tests/EdgeRetails.UnitTests/FrontendPass1GeometryBaselineTests.cs` | Strengthen renderer parity protection while retaining geometry assertions. |
| `tests/EdgeRetails.UnitTests/UserReportedUiDefectRegressionTests.cs` | Original POS padding/focus, modal clipping and pill precedence regressions. |
| `scratch/UserReportedUiDefectPack/Probe/UiDefectProbe.csproj` | Owned WPF runtime evidence project referencing the actual Desktop project. |
| `scratch/UserReportedUiDefectPack/Probe/Program.cs` | Actual MainWindow/Shell/navigation/views, controlled read-only query fixture, measured geometry and image capture. |

The worktree contains substantial pre-existing/concurrent changes. The whole Git diff is not attributed to this UI task. The accompanying hash manifest identifies this candidate's source, executable, log and selected evidence snapshots.

Desktop shortcut created and read back successfully:

`C:\Users\muham\OneDrive\Desktop\Edge Retails - Latest Build.lnk`

Target: `C:\Users\muham\OneDrive\Desktop\Point of Sale\src\EdgeRetails.Desktop\bin\Debug\net10.0-windows\EdgeRetails.Desktop.exe`. Working directory is the executable directory; arguments are empty. This opens normal application startup. It is a source Debug build, not a newly published release or the evidence harness. The shortcut was verified without launching operational startup.

## SearchBox

Root causes: the reusable watermark used TextBlock while input used TextBox; the POS field inherited standard input height/padding instead of embedded geometry. The POS host also set a local BorderBrush, which defeated its style focus trigger. An explicit button focus adorner drew a second inner outline on F2 focus.

References: `Controls/SearchBox.xaml` Input/PlaceholderInput; `Resources/Inputs.xaml:91` Input.TextBox.Placeholder; `Views/PosView.xaml:173` SearchInput and `:188` SearchPlaceholder.

Correction: both states now use the embedded TextBox renderer, same column/font and zero embedded padding. The normal host stroke is a style setter so hover/focus can override it. The input inherits the embedded focus contract; the enclosing border provides the single visible focus stroke.

Measured POS first-character rectangles, placeholder and typed: **2,0.19,0,18.62 DIP**, equal. The actual reusable SearchBox in Sales History also passed runtime character-rectangle equality. Native focused-empty/typed images show unchanged text placement, fixed search icon alignment and a single host outline. Evidence covers measured100% DPI; other display scaling is not certified.

## Stroke System

Input normal/open/focus strokes retain thickness1. Shared light/dark hover brush keys provide stronger neutral hover boundaries. Popup and parent use the same focus authority while open. Default WPF arrow and item templates were replaced rather than concealed with margins.

Modal focus was a separate defect: the presenter could take focus and draw a dotted rectangle around the whole dialog. `ModalHost.FocusFirstContentControl()` at `Controls/ModalHost.xaml.cs:82` now focuses an enabled, visible, tabbable content control. Native F4 evidence shows focus on the close button, a clean rounded modal border, and Escape returns to POS.

Verified visually: POS search, Category/Brand closed/open, popup keyboard item, customer modal/rows, period pills and visible toolbar. A complete normal/hover/focus/pressed/disabled matrix for every affected family was not captured; no system-wide stroke certification is claimed.

## Text Alignment

Corrected controls: reusable/actual POS search, compact dropdown items, customer two-line rows, modal header/close, Price Check query/result controls. Compact ComboBox display/item font sizes now agree; item padding comes from the shared input token and vertical content alignment is centered.

Price Check/Save Draft/Hold/Drafts toolbar, customer Use/Select labels, period labels, product/cart empty-state icons and text were inspected in real production views. Standard36/compact32 geometry remains protected by Pass1 tests. No arbitrary local baseline offset was introduced.

## ComboBox Popup

`Resources/Inputs.xaml` Input.ComboBoxItem, Input.ComboBoxItem.Compact, Input.ComboBox template and PopupBorder are the shared authorities. Whole-field click opens the popup; the arrow has a transparent custom hit surface. Category/Brand collapsed fields are120×32 DIP in this POS. Popup items are36 DIP tall with compact13-DIP typography. Category popup120×218; Brand popup120×320 with scrolling.

Native images demonstrate selected All, custom popup strokes and text alignment. Down-arrow moves the keyboard highlight to Accessories while All remains selected; Escape dismisses without committing that highlight. Brand's open list is also retained. Full popup hover/disabled permutations remain unverified.

## Price Check

Original failure: empty input returned after setting ScannerStatusMessage, which was not presented on the POS screen. Missing service wiring only produced an unavailable notification. The old grouping selected the first match per product, potentially hiding distinct units/identities.

Production chain: POS button/F8 → `PosViewModel.PriceCheckCommand` → `PriceCheckAsync()` at `ViewModels/PosViewModel.cs:910` → visible PriceCheckViewModel → existing `ResolveScannerAsync` → dialog result. `Navigation/PageViewModelFactory.cs` selects RemotePosWorkflowService for the production API client. This composition was inspected in source; it was not connected to shop data.

`PriceCheckViewModel.CheckAsync()` at `ViewModels/PriceCheckViewModel.cs:40` rejects empty/no-match/ambiguous results, prevents overlapping queries with IsBusy, clears stale result state before query execution, and presents safe failure feedback. Distinct product-unit/physical identities are preserved. No cart, sale or inventory mutation dependency was added.

Native OS click opened the dialog; Enter on PH-LED-12W displayed the owned resolver fixture's **Rs.250**,25pcs. These are fixture values, not claims about shop inventory or the demo grid's price. Probe assertions passed success, empty, missing, ambiguous and offline outcomes. Cart count and the full preview stock snapshot remained unchanged; service spy recorded only ResolveScannerAsync. This proves the actual production view/command and read-only query behavior with controlled data. Actual authoritative service/database result certification remains **NOT_RUN_ENVIRONMENT**.

## Logo

Concept: compact angular E with a small scan-edge accent, white on purple. `Resources/Brand.xaml` uses WPF DrawingImage/GeometryDrawing; the wordmark uses theme-aware text. Expanded sidebar, collapsed mark, login, setup and window icon consume the same identity.

Inspected runtime sidebar images show crisp expanded light/dark and collapsed dark branding. Login/setup were updated but were not separately certified by native screenshots. No new bitmap logo was generated.

## Hover

The targeted Today/Yesterday/This Week/This Month pills are in SalesHistoryView. The template's hover setter formerly overrode derived selected backgrounds, leaving white text on a pale surface. `Button.FilterPill` at `Views/SalesHistoryView.xaml:18` now owns all interaction setters. Data-trigger conversion handles Boolean Tag selection; selected is applied before pressed/focus, and hover is gated to unselected pills.

Native proof: normal Today-selected; Yesterday-selected while pointer remains over it; keyboard focus on Today; keyboard Space selecting Today while pointer remains over unselected Yesterday. Selection stays strongly purple, hover remains a light neutral treatment with a distinct border, and focus is independently visible. Pressed/down capture was not retained. A complete state matrix for sidebar/buttons/modal rows/disabled controls is not certified.

## Change Customer Modal

Root cause: both Walk-in and saved-customer rows inherited36-DIP single-line Button.Neutral height; saved rows additionally consumed vertical padding. Rows now use Height Auto, MinHeight64 and Padding12,10, with bounded name text and wrapping detail. Header reserves a close column. Actual row heights measured64,64,64,64,64 DIP. Names/details/Use/Select are readable and unclipped in final native evidence.

Shared focus correction removes the dotted full-dialog outline and prevents initial focus from staying on the POS field. F4 opens the dialog with close-button focus; Escape closes it. Long-name/large-directory/low-height modal stress variants remain unverified.

## Build / Tests

Final normal command: `dotnet build scratch/UserReportedUiDefectPack/Probe/UiDefectProbe.csproj -c Debug --no-restore`.

Actual result: **exit0,0 warnings,0 errors,13.15 seconds**. This built Domain/Application/Infrastructure/Desktop and the owned probe with normal analyzers. Earlier backend IDE0011 blockers no longer blocked this final snapshot; no unrelated backend fix was made by this task. Earlier analyzer-disabled builds were diagnostic only.

Final command: `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~FrontendPass1GeometryBaselineTests|FullyQualifiedName~FrontendPass2FocusAccessibilityTests|FullyQualifiedName~UserReportedUiDefectRegressionTests|FullyQualifiedName~Sprint9Phase4ExactUnitWorkflowTests'`.

Actual result: **34 passed,0 failed,0 skipped**. Original renderer, local-brush precedence, inherited row height and pill trigger ordering/presence are protected; assertions were strengthened, not removed.

Earlier broader targeted run including FrontendPass3ResponsiveLayoutTests: **39 passed,1 failed,total40**. `ProductManagementView_HeaderActions_WrapBelowHeading()` at `tests/EdgeRetails.UnitTests/FrontendPass3ResponsiveLayoutTests.cs:186` expected5 buttons, found6. This separately changed Product Management contract is outside this bounded correction; it was not weakened or declared green.

Latest owned runtime probe exited0. Runtime log records both renderer checks, popup dimensions, customer heights and read-only query assertions. No full-suite, Release publish, complete OS scaling matrix or backend certification is claimed.

## Screenshots

Evidence root: `C:\Users\muham\OneDrive\Desktop\Point of Sale\scratch\UserReportedUiDefectPack\2026-10-04` (original folder date retained; final native checks continued on2026-10-05).

Selected final evidence:

| State | File under evidence root |
|---|---|
| POS placeholder/logo/toolbar | `native-after/pos_placeholder_logo_toolbar.png` |
| Focused empty | `native-after/pos_focused_empty.png` |
| Typed/no products/cart empty | `native-after/pos_typed_empty_products.png` |
| Category selected/open | `native-after/category_open_selected.png`, `_1.png` popup |
| Category keyboard item | `native-after/category_keyboard_item.png`, `_1.png` popup |
| Brand selected/open | `native-after/brand_open_selected.png`, `_1.png` popup |
| Customer modal final | `native-after/change_customer.png` |
| Customer Escape outcome | `native-after/customer_escape_restores_pos.png` |
| Normal/selected pills | `native-after/period_pills_normal_today_selected.png` |
| Selected hover | `native-after/period_pills_yesterday_selected_hover.png` |
| Keyboard focus | `native-after/period_pills_today_keyboard_focus.png` |
| Unselected hover vs selected | `native-after/period_pills_today_selected_yesterday_hover.png` |
| Native Price Check result | `native-after/price_check_result_keyboard.png` |
| Latest price/result render | `after/price_check_result.png` |
| Dark expanded/collapsed logo | `after/pos_dark_logo.png`, `after/pos_dark_collapsed_logo.png` |
| Geometry/query assertions | `after/runtime.txt` |

Native captures use computer-use against the owned test window. `after/` renders use actual MainWindow/Shell/production views with measured DPI; they are RenderTargetBitmap captures, not native screen captures. Older native images not listed above are intermediate evidence. The native price screenshot predates the final ModalHost focus-only change; latest price render includes that change.

Before references are retained in `before/`: original POS renderer, original36-DIP customer rows, raster branding, original pills and empty Price Check. Shared Inputs had already been corrected when some before renders were generated, so those dropdown images are not a clean original-template baseline. Original user screenshot attachments were not present in the supplied defect-pack attachment directory. Missing before-hover/pressed references are not fabricated.

Isolation correction: an early probe instantiated App, whose queued OnStartup could run startup/health requests despite Dispatcher.Run. Those early renders are baseline references, not isolated certification. The final probe instead uses ResourceOnlyApp, blocks OnStartup and loads the unchanged production resource layers in dependency order. It cannot create a BackendRuntime. No operational inventory/data was used as evidence; no legacy Unicode inventory assessment was performed.

## Independent Validator

Read-only validator `/root/ui_defect_validator` inspected final source, native images and production-view renders. It accepted corrected search/focus, popup/text alignment, pill contrast/selection/focus, modal rows/rounded boundary/Escape, vector branding and fixture-backed Price Check/read-only assertions.

Final validator verdict: **UI_DEFECT_PACK_PARTIALLY_RESOLVED**. It found no remaining obvious clipping, dead fixture-backed Price Check, visible search jump, broken dropdown or unchanged logo in the reviewed final images. It rejected full certification because the acceptance evidence below is incomplete. Validator edited no files and inspected no operational database.

## Remaining Problems

| Priority | Remaining acceptance gap | Required follow-up |
|---|---|---|
| High | Actual authoritative Price Check service result was not exercised from the UI. | Use an owned isolated server/database and authorized test session; demonstrate the real API result and read-only persistence snapshots. Operational shop access remains unauthorized. |
| Medium | Pressed/down evidence and complete control-family state matrix are missing. | Capture actual pressed/hover/focus/disabled interactions across targeted outlined actions, sidebar, modal rows/fields and pills; preserve before references where available. |
| Medium | Runtime evidence is measured100% DPI only. | Recheck relevant native controls at actual additional supported display scales and constrained heights. |
| Medium | Broader Pass3 Product Management button-count contract fails. | Reconcile that separate screen's intended action list and meaningful contract in its authorized task; do not weaken assertions merely to pass. |
| Low | Login/setup branding and modal long-content stress were not separately runtime-certified. | Inspect those production states with retained evidence. |

Legacy Unicode assessment remains **NOT_RUN_ENVIRONMENT / REQUIRES_APPROVED_DATA_SOURCE**, as instructed; it does not block these owned UI tests. This report does not open another pass or authorize deployment. Stop after this bounded verdict.
