# Universal frontend surface and text correction — 2026-10-06

The user's rule applies across the frontend: complete rounded tab/button geometry, readable values, and rounded standalone boxes. The Supplier drawer is the motivating example, not the scope boundary.

Implemented:
- Resources/Cards.xaml: application-wide implicit Border style inherits Surface.Box (Radius.Control = 8). Existing card/control/circle radii remain explicit. Removed SupplierDetailView's private rounding override.
- Resources/Tabs.xaml: shared wrapping header panel preserves whole header allocation; selected white foreground is confined to header. Content retains theme primary foreground. Rounded keyboard focus restored.
- Every Views UserControl root and shared Controls root uses theme primary foreground, with MainWindow and print preview aligned. Explicit caption/semantic/button styles keep their intended colors.
- Fixed brand foregrounds across 15 XAML files to use theme-aware Brush.Badge.Brand.Text, including Settings selected navigation and text actions. Physical intake warning/error and setup error foregrounds are theme-aware.
- Resources/Tables.xaml + Controls/RoundedViewport.cs: rounded outer table clip updates on load/resize without replacing the DataGrid template, scrolling or virtualization. DrawerHost and the sales-return drawer similarly contain child backgrounds within rounded corners.
- Divider lines, contiguous header/footer separator edges, and full-window dim overlays explicitly opt out of box rounding. They are structural geometry, not standalone cards or controls.

Validation of final functional source:
- Release Desktop/probe build: zero warnings/errors.
- 24 existing geometry/focus/user-reported UI regression tests: all passed.
- Production ResourceOnlyApp fixture: all 52 parameterless production view/dialog types instantiated in Light and Dark; 104 renders; 750 runtime assertions passed, zero failed.
- Owned table fixture: 1,000 rows, eight wide columns, two widths in both themes; rounded clip resizes correctly, horizontal extent remains scrollable, row virtualization remains active.
- Drawer content clipping verified in both themes.
- Supplier fixture: all seven tab borders/allocated widths plus amount foreground; 30 assertions passed, zero failed.
- Debug sample-data visual pass: 14 navigation destinations, Light/Dark, widths 1,100/1,440; 56 renders. Spot visual inspection included Supplier, Warranty, POS and Settings.

Evidence:
- scratch/UserReportedUiDefectPack/2026-10-04/universal-ui/runtime.txt and corresponding PNGs
- scratch/UserReportedUiDefectPack/2026-10-04/supplier-tabs/runtime.txt and corresponding PNGs
- scratch/UserReportedUiDefectPack/2026-10-04/visual-review/runtime.txt and corresponding PNGs
- scratch/UserReportedUiDefectPack/Probe/Program.cs

Limits: empty-context view construction verifies resources/layout inheritance, not every data-dependent state or business workflow. Populated navigation and supplier/table/drawer fixtures supplement it. This is not a live-shop visual certification or an exhaustive WCAG contrast audit. No operational database read or write was performed.

Delivery: final Release DLL staged with hash matching the build at artifacts/production/pending-supplier-ui. The existing desktop shortcut continues targeting artifacts/production/desktop/EdgeRetails.Desktop.exe, preserving the dated brand icon. An owned hidden updater waits for the running production desktop to close naturally, then installs the staged files; deployment-status.txt records WAITING/APPLIED/FAILED. The loaded app remains on its existing assembly until close/reopen. No app process was forcibly closed and no database or connection setting was changed.
