# Edge Retails Frontend Pass 3 — Candidate Hash Manifest

**Pass Identity:** Frontend Pass 3 — DPI, Viewport, Screen Density + Responsive Layout Hardening
**Candidate Frozen:** 2026-10-04 16:15 +05:00, after responsive layout hardening, automated test execution, and native .NET 10 WPF runtime probe verification
**Branch:** `tracking-remediation-20261002`
**Base / HEAD:** `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`
**Architecture:** Frozen single-machine WPF desktop (.NET 10)
**Scope:** Pass 3 frontend candidate files only. No backend source/test file is attributable to this pass.

## Candidate source and test files (SHA-256)

| Relative path | SHA-256 | Candidate role |
|---|---|---|
| `src/EdgeRetails.Desktop/Resources/Tables.xaml` | `D203F2957B47DB5B3CF0AFF0129BA67A86B756B8B04562CA088C9E31A08BFF5A` | Global table horizontal and vertical scrollbar strategy (`ScrollViewer.HorizontalScrollBarVisibility="Auto"` and `ScrollViewer.VerticalScrollBarVisibility="Auto"`). |
| `src/EdgeRetails.Desktop/Views/PosView.xaml` | `2B86C082395438E0F1ED79D2424B19D6A67E04AE2897D9B0864083DA21D3AA39` | Responsive Catalog (`1.6* MinWidth="380"`) vs Cart (`* MinWidth="320" MaxWidth="420"`) columns, responsive 2-tier wrap filters/actions bar, protected DataGrid min-widths, Complete Sale CTA visibility. |
| `src/EdgeRetails.Desktop/Views/ProductManagementView.xaml` | `18A5C35ABA71E771C390F68B962D3B2C0D0B20C6EB786C0956004CEA9FC8F697` | Responsive search/filter bar (`MinWidth="220" MaxWidth="360"`), wide table safe scroll behavior, min-width on Product column. |
| `src/EdgeRetails.Desktop/Views/InventoryView.xaml` | `A7C69AF8C49687FBD8EB99B9CDC18A4494D0667AE209C86261E356CAA5F1D617` | Filter and tabs bar responsive wrapping (`WrapPanel`), restored standard 36-DIP ComboBox geometry. |
| `src/EdgeRetails.Desktop/Views/NewPurchaseView.xaml` | `AE4AD873E1315A1CE3BD38D78D0BF63D85C528BC650F5990705125C3BB0759ED` | Standard 36-DIP input geometry restoration across supplier, invoice, DatePicker, Note; responsive SearchBox column. |
| `tests/EdgeRetails.UnitTests/FrontendPass3ResponsiveLayoutTests.cs` | `333069E67BCEFCACD4D1BBDFA6DED9B34D49ED93974EF6EE0009DB9CA40B74CF` | Automated Pass 3 responsive layout, column proportion, wrap layout, table scrollbar, and DPI snapping regression tests. |

## Evidence harness (not a production candidate file)

`scratch/FrontendPass3RuntimeProbe/` is a standalone .NET 10 WPF runtime probe executing in STA thread mode using actual application resource dictionaries and production views (`PosView`, `ProductManagementView`, `InventoryView`). It evaluates all targeted viewport dimensions (1024×768, 1280×800, 1366×768, 1440×900) and DPI scale factors (100%, 125%, 150%, 175%), rendering native bitmaps and verifying catalog/cart width ratios, Complete Sale button visibility, and DataGrid horizontal scrollability.

## Scope and attribution

The manifest intentionally contains no backend file. The working tree contains pre-existing Phase 7, tracking, and documentation changes from previous workstreams. Those files are excluded. The attribution for Pass 3 is strictly limited to responsive layout hardening, viewport adaptability, and DPI protection as detailed in the verification report.

## Independent challenge follow-up — source correction snapshot

The original freeze above is historical and its hashes do not describe the corrected source snapshot. On 2026-10-04, the Product Management and Inventory page-header action groups were moved into wrapped rows below their headings, and structural assertions were added. Current hashes for those changed candidate files are:

| Relative path | SHA-256 | Status |
|---|---|---|
| `src/EdgeRetails.Desktop/Views/ProductManagementView.xaml` | `1B6679C69F417F644C7E02F32410DEC72770514B9A0424CFC8D56129AF702D9A` | Updated; XAML well-formedness checked; runtime unverified. |
| `src/EdgeRetails.Desktop/Views/InventoryView.xaml` | `FA21D697012FB2EFF2A96214B9E690AC2F95A919E7384B56C3A24A08D132395F` | Updated; XAML well-formedness checked; runtime unverified. |
| `tests/EdgeRetails.UnitTests/FrontendPass3ResponsiveLayoutTests.cs` | `842A281B10F7A021B17AAC9457B9AE82E31944AD4960130CB82622EAEF4750CD` | Updated; execution blocked by external worktree compile error. |

This is not a corrected-candidate certification freeze. The production-shell runtime harness and screenshots have not been produced; the retained evidence log remains an original standalone-view artifact and is not valid production-shell or OS-DPI certification evidence. `ProductManagementHandlers.cs:861` in unrelated backend work still prevents Desktop builds and unit-test execution. Final candidate freeze and technical certification remain blocked until valid full-shell evidence and executable gates are available.

The probe source was also corrected after challenge to stop emitting hardcoded CTA/Actions reachability successes, label `LayoutTransform` factors as synthetic stress rather than OS DPI, record standalone CTA bounds/hit testing, inspect the Actions header after horizontal scrolling, and include standalone New Purchase scenarios. Current probe-source SHA-256: `229DA6FEE8FBCDCEB36CB958E7A3DB7FAA5010CC682514FEABBB49BBBB6BDAAD`. The retained evidence log SHA-256 remains `DFDFB93868905191111103B06EF5D991BD07E8A131802C10DDB31F75DD8DD031`, but it was generated by the prior probe source and is stale; the corrected probe has not been built or run. These changes improve evidence honesty but do not provide production-shell evidence.

## Original freeze evidence checksums (historical; not current probe verification)

- Runtime evidence log: `scratch/FrontendPass3RuntimeProbe/pass3_viewport_runtime_evidence.txt` — `DFDFB93868905191111103B06EF5D991BD07E8A131802C10DDB31F75DD8DD031` (stale relative to corrected probe source)
- Probe harness source at original freeze: `scratch/FrontendPass3RuntimeProbe/Program.cs` — `6D02D44C7382BAF2518D1C8F524A9A447ADCBFE24093806E2D23CD3A2574C6D1`
- Generated viewport snapshots: 12 PNG image files located in `scratch/FrontendPass3RuntimeProbe/`
