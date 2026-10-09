# UI stroke corrections — 2026-10-05

## Scope and outcome

The user authorized correcting the screenshot's Change F4 outline and inspecting and resolving additional small input/stroke issues. No operational database was connected to or modified. Inventory/data-source differences remain unresolved.

Corrected findings:

- POS Change F4 inherited both a focused button border and a rounded focus adorner. `Resources/Buttons.xaml` now supplies `Button.Text.Borderless` with zero border thickness and a keyboard underline adorner. `Views/PosView.xaml` uses it without changing its command.
- Other direct text-action consumers had the same outline behavior. Edit/View actions in Expenses, Customers, Suppliers, Inventory, PurchaseHistory, ProductManagement and Settings, ProductDetail navigation/adjustment actions, CatalogReferenceManager actions and CompleteSale Set Exact now use the same variant. Existing contained/primary button styles and their focus indicators are preserved.
- Standard text/password inputs lacked hover border feedback. `Resources/Inputs.xaml` now adds hover before keyboard-focus triggers, retaining the single template-owned stroke and focus precedence.
- POS discount, CompleteSale received amount and SalesReturn return quantity composite inputs lacked the same hover feedback. Their enclosing border styles now provide it before their existing focus triggers; embedded editors remain borderless.
- DatePicker retained Windows default internal chrome despite the shared outer style. Runtime inspection found multiple internal borders, including default cyan focus chrome. `Resources/Inputs.xaml` now gives it a rounded single-outline template, a borderless date editor and named framework calendar parts. The editor retains the framework watermark, calendar behavior and theme brushes.

## Verification

- Isolated application/probe build initially passed with zero warnings/errors. Final Desktop and probe builds passed with zero warnings/errors using `BuildProjectReferences=false` against the existing compiled dependency assemblies. No backend changes were made for this UI task.
- The existing unit-test binary was executed with `--no-build --no-restore`: 34 passed, zero failed, covering geometry, focus accessibility, reported UI regressions and exact-unit workflows. Source-inspection tests read the current XAML. This is not a newly compiled full-backend test certification.
- Owned fixture/resource-only runtime probe passed: Change Customer remains keyboard-focusable with zero template stroke and the underline focus style; DatePicker has exactly one nonzero border owner; typed-date commit, calendar-button popup, calendar selection and date clearing all work; reusable search geometry remains aligned.
- Rendered light/dark focus and dark disabled states were visually inspected. Evidence and runtime log: `scratch/UserReportedUiDefectPack/2026-10-04/borderless-action/`. The directory date belongs to the earlier harness; this run occurred on October 5.
- Current 100% DPI was measured. Other OS DPI settings and every live-data screen/state are not certified by this run. Rendered focus evidence is programmatic; physical pointer/tab interaction across every screen is not claimed.

## Build/shortcut delivery and limits

The normal output directory was locked by the user's running `EdgeRetails.Desktop` process. That process was not terminated. The verified application output was copied, excluding the probe executable/files, to `artifacts/DesktopUiStrokeFix_2026-10-05`. The existing desktop shortcut `Edge Retails - Latest Build.lnk` now targets that folder's actual `EdgeRetails.Desktop.exe`, with empty arguments and that folder as its working directory. No preview/fixture startup arguments were added. The normal operational startup was not launched for testing.

A fresh whole-project test build encountered unrelated current backend source compile errors in `Application/Features/Inventory/StockAdjustmentHandlers.cs`: unresolved `ExactMissingSourcePosting` at line 622 and nullable decimal conversion at line 632. Those errors were not changed. The delivered UI candidate uses existing compiled dependency assemblies, not a certification of all current backend source.

The earlier UI defect-pack candidate manifest describes its historical candidate and must not be treated as a hash manifest for this changed build. This pass resolves the listed defects; it does not establish that every remaining UI defect or the inventory mismatch is closed.
