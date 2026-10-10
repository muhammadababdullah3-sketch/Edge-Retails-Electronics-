# Edge Retails Desktop Frontend — Pass 1 Verification Report
## Design-System Geometry + Global Input Baseline Repair

**Original Certification Verdict:** `FRONTEND_PASS1_CERTIFIED` (subsequently challenged; original evidence is retained below for audit trail)  
**Original Execution Timestamp:** `2026-10-04T12:18:30+05:00`  
**Base Commit / HEAD:** `9795683` (`feat(tracking): implement tracking manufacturer identity authority V1`)  
**Original Reported Branch:** `main` (documentation drift; live branch is recorded in the correction section)  
**Scope Boundary:** Strict Frontend Pass 1 Only (`src/EdgeRetails.Desktop`, `tests/EdgeRetails.UnitTests`)  
**Backend Changes Attributable to Pass 1:** Zero (`0`); unrelated pre-existing backend changes exist in the live working tree.  

---

## 1. Authority & Scope Boundaries

This remediation pass operates under the authority of the canonical frontend remediation roadmap:
`EDGE_RETAILS_FRONTEND_5_PASS_REMEDIATION_MASTER_ROADMAP.md` (Pass 1).

### In-Scope:
- Shared WPF design-system geometry and tokens in `Resources/Spacing.xaml`.
- Shared input styling, implicit templates, and visual family parity in `Resources/Inputs.xaml` (`TextBox`, `PasswordBox`, `ComboBox`, `DatePicker`).
- Composite SearchBox input transitions and placeholder baseline alignment in `Controls/SearchBox.xaml`.
- POS currency input baseline alignment with `Rs.` prefix in `Views/Dialogs/CompleteSaleDialog.xaml` and `Views/PosView.xaml`.
- Embedded stepper and input constraint fixes in `Views/Dialogs/SalesReturnDialog.xaml`.
- Automated frontend forensic contract tests in `tests/EdgeRetails.UnitTests/FrontendPass1GeometryBaselineTests.cs`.
- Coordinated Debug/Release builds, git isolation verification, and 10-scenario runtime layout execution.

### Explicitly Out-of-Scope:
- Pass 2 (Keyboard navigation, tab order, focus trapping, shortcuts).
- Pass 3 (DPI scaling, viewport layouts, column budgets).
- Pass 4 (Dialog layouts, modals).
- Pass 5 (Recovery tool UI parity).
- Backend, PostgreSQL, EF Core models, migrations, domain logic, mutation handlers, or API endpoints.

---

## 2. Baseline & Git Isolation

- **Original recorded Git state:** The prior report said `main`; that branch claim was inaccurate for the live checkout. The original candidate was based on commit `9795683`.
- **Current surgical correction state:** Branch `tracking-remediation-20261002`, HEAD `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`. The tree contains unrelated pre-existing work, including backend source changes; these were not included in or changed by this frontend correction.
- **Isolation Protocol:** All pre-existing modified backend files were strictly protected and left untouched.
- **Changed Files List (Pass 1):**
  1. `src/EdgeRetails.Desktop/Resources/Spacing.xaml`
  2. `src/EdgeRetails.Desktop/Resources/Inputs.xaml`
  3. `src/EdgeRetails.Desktop/Controls/SearchBox.xaml`
  4. `src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml`
  5. `src/EdgeRetails.Desktop/Views/PosView.xaml`
  6. `src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml`
  7. `tests/EdgeRetails.UnitTests/FrontendPass1GeometryBaselineTests.cs`
  8. `docs/Frontend_Pass1_Candidate_Hash_Manifest.md`
  9. `docs/EDGE_RETAILS_FRONTEND_PASS1_VERIFICATION.md`
- **Backend Modifications Attributable to Pass 1:** Exactly `0` backend files modified. This does not claim that the entire live working tree is backend-clean.

---

## 3. Root Causes Identified & Repaired

| Finding ID | Symptom | Owning Shared Authority | Root Cause Mechanism | Repaired Architecture |
| :--- | :--- | :--- | :--- | :--- |
| **RC-GEO-01** | **SearchBox Baseline Jump** | `Controls/SearchBox.xaml` & `Resources/Inputs.xaml` | Embedded `TextBox` lacked explicit style, inheriting `Input.TextBox` implicit style which enforced `Height="36"` and `Padding="12,0"`. Meanwhile, placeholder `TextBlock` had `Margin="0"`, `Padding="0"`. When typing, text jumped 12 DIPs horizontally and several DIPs vertically due to conflicting container heights and padding. | Created `Input.TextBox.Embedded` with `Height="Auto"`, `Padding="0"`, `VerticalAlignment="Center"`. Bound both `TextBox` and `TextBlock` in SearchBox to Column 2, X=0, baseline-centered at 14pt font with identical zero-padding. |
| **RC-GEO-02** | **Compact Height Discrepancy & Clipping** | `Resources/Spacing.xaml` & `Resources/Inputs.xaml` | `Dimension.Control.Compact` was set to `28` DIPs while `Dimension.Button.CompactAction` and `Input.ComboBox.Compact` were set to `32` DIPs. Side-by-side compact controls had mismatched heights and font sizes (`13.5` vs `13.0`), and 28 DIP inputs clipped text descenders. | Harmonized `Dimension.Control.Compact` to `32` DIPs to match `Dimension.Button.CompactAction`. Tokenized `Padding.Input.Compact` as `8,0`. Normalized `Input.TextBox.Compact`, `Input.ComboBox.Compact`, `Input.PasswordBox.Compact`, and `Input.DatePicker.Compact` to 32 DIPs, 13pt font, and `8,0` padding. |
| **RC-GEO-03** | **PasswordBox Compact Omission & Bullet Alignment** | `Resources/Inputs.xaml` | `Input.PasswordBox` had no compact style variant and lacked `SnapsToDevicePixels="True"` on its internal border and content host, causing subpixel jitter and uneven bullet rendering. | Added `Input.PasswordBox.Compact` (32 DIPs, 13pt, `8,0` padding). Added `SnapsToDevicePixels="True"` on all PasswordBox templates. |
| **RC-GEO-04** | **Currency Prefix "Rs." Vertical Misalignment** | `CompleteSaleDialog.xaml` & `PosView.xaml` | The container was 46 DIPs (or 28 DIPs in POS discount), but the inner `TextBox` lacked an embedded style and inherited `Height="36"` from `Input.TextBox`. Inside the 46 DIP container, the 36 DIP TextBox sat at a different vertical offset than the 16pt "Rs." label. In POS discount, the 36 DIP TextBox overflowed the 28 DIP container. | In `CompleteSaleDialog`, applied `Input.TextBox.Embedded` to `AmountReceivedTextBox` and harmonized both `"Rs."` and the amount to 18pt Bold `Font.Numeric` with `VerticalAlignment="Center"` inside the 46 DIP border. In `PosView`, applied `Input.TextBox.Embedded` to the discount amount input. |
| **RC-GEO-05** | **DatePicker Input Family Drift** | `Resources/Inputs.xaml` | `Input.DatePicker` had hardcoded `FontSize="13.5"`, missing compact variant, and no disabled opacity trigger, drifting from the standard input family contract. | Normalized `Input.DatePicker` to `14pt` font, added `SnapsToDevicePixels="True"`, added disabled state trigger, and introduced `Input.DatePicker.Compact` (32 DIPs, 13pt, `Padding.Input.Compact`). |

---

## 4. Implemented Geometry Contract

```text
Standard Input Family (Height: 36 DIPs | Font: 14pt | Padding: 12,0):
├── Input.TextBox          (Height: 36, Padding: 12,0, Font: 14pt, VCenter, SnapsToDevicePixels)
├── Input.ComboBox         (Height: 36, Padding: 12,0, Font: 14pt, VCenter, SnapsToDevicePixels)
├── Input.PasswordBox      (Height: 36, Padding: 12,0, Font: 14pt, VCenter, SnapsToDevicePixels)
└── Input.DatePicker       (Height: 36, Padding: 12,0, Font: 14pt, VCenter, SnapsToDevicePixels)

Compact Input Family (Height: 32 DIPs | Font: 13pt | Padding: 8,0):
├── Input.TextBox.Compact     (Height: 32, Padding: 8,0, Font: 13pt, VCenter, SnapsToDevicePixels)
├── Input.ComboBox.Compact    (Height: 32, Padding: 8,0, Font: 13pt, VCenter, SnapsToDevicePixels)
├── Input.PasswordBox.Compact (Height: 32, Padding: 8,0, Font: 13pt, VCenter, SnapsToDevicePixels)
└── Input.DatePicker.Compact  (Height: 32, Padding: 8,0, Font: 13pt, VCenter, SnapsToDevicePixels)

Embedded Composite Family (Height: Auto | MinHeight: 0 | Padding: 0 | Border: 0):
└── Input.TextBox.Embedded    (Height: Auto, Padding: 0, Border: 0, VCenter, SnapsToDevicePixels)
    ├── SearchBox (Column 2, X=0, baseline match between placeholder & typed text)
    ├── CompleteSale Amount Box (Height: 46 DIPs, "Rs." 18pt Bold == Amount 18pt Bold)
    ├── POS Discount Box (Height: 28 DIPs, "Rs." 12pt == Discount 13pt, no height overflow)
    └── Stepper Quantity Box (Height: 28 DIPs, centered numeric, no clipping)
```

---

## 5. Verification Gate Results

### Gate A: Desktop Debug Build
- **Command:** `dotnet build src/EdgeRetails.Desktop/EdgeRetails.Desktop.csproj -c Debug`
- **Result:** `Build succeeded. 0 Warning(s), 0 Error(s).`
- **Status:** `PASSED`

### Gate B: Desktop Release Build
- **Command:** `dotnet build src/EdgeRetails.Desktop/EdgeRetails.Desktop.csproj -c Release`
- **Result:** `Build succeeded. 0 Warning(s), 0 Error(s).`
- **Status:** `PASSED`

### Gate C: Original Focused Pass 1 Forensic Contract Tests (superseded)
- **Command:** `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --filter "FullyQualifiedName~FrontendPass1GeometryBaselineTests"`
- **Original Result:** `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 46 ms`. The corrected candidate result is recorded below.
- **Tests Verified:**
  1. `SpacingTokens_DefineHarmonizedGeometryBaseline`
  2. `Inputs_DefineStandardAndCompactVisualFamilyParity`
  3. `Inputs_ImplicitStyles_AreProperlyDeclared`
  4. `SearchBox_EliminatesBaselineAndHorizontalJump`
  5. `CompleteSaleDialog_CurrencyPrefixAndAmount_AreHarmonized`
  6. `PosView_DiscountInput_UsesEmbeddedStyleWithoutClipping`
  7. `SalesReturnDialog_StepperInput_UsesEmbeddedStyleWithoutClipping`
- **Status:** `PASSED`

### Gate D: Broader Frontend Regression Suite
- **Command:** `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --filter "FullyQualifiedName~Sprint2ForensicAuditTests|FullyQualifiedName~Sprint6ForensicAuditTests|FullyQualifiedName~RecoveryUiContractTests"`
- **Result:** `Passed! - Failed: 0, Passed: 26, Skipped: 0, Total: 26, Duration: 1 s`
- **Status:** `PASSED`

### Gate E: Original Runtime Visual Verification Matrix (10 Scenarios) — Evidence Limitation
- **Original claim:** Standalone STA WPF host rendering with direct measurement, arrangement, layout update, and `RenderTargetBitmap` snapshot capture. The harness, snapshots, and raw measurements were not retained in the workspace; these claims are not independently reproducible and are not relied on as corrected-candidate evidence.
- **Original reported scenarios (historical claims, not independently verified):**
  1. **Login / PIN Inputs:** Standard TextBox (`Height=36 DIPs`), PasswordBox (`Height=36 DIPs`). Optical centering confirmed; zero clipping.
  2. **SearchBox Placeholder vs Typed State:** Placeholder TextBlock and typed TextBox both render at Column 2, X=28.5 DIPs inside 44 DIP SearchBox. Transition has 0 DIP horizontal offset, 0 DIP vertical offset, identical font baseline.
  3. **POS Discount Input:** Container `Height=28 DIPs`, embedded TextBox `Height=17.29 DIPs` (no 36 DIP overflow or clipping). Text vertically centered and baseline-aligned with `Rs.`.
  4. **Complete Sale Amount Input:** Container `Height=46 DIPs`, "Rs." prefix 18pt Bold vertically aligned with Amount 18pt Bold.
  5. **Settings Forms:** Standard TextBox `Height=36 DIPs`, `Padding=12,0`, `FontSize=14pt`.
  6. **Warranty Search/Filter:** Filter input `Height=36 DIPs`, consistent padding and focus visual.
  7. **New Purchase Side-by-Side:** The prior report claimed 36 DIPs. Live `NewPurchaseView.xaml` declares 35-DIP local heights for its supplier ComboBox, invoice TextBox, and DatePicker; geometry was not changed by this correction.
  8. **Reports Selectors (Compact):** The prior report claimed 32 DIPs and 13pt typography. That was not supported by the original view: controls had local 32-DIP heights but standard styles. The corrected runtime result is recorded below.
  9. **Supplier Detail Forms (Read-Only):** Read-only TextBox `Height=36 DIPs`, subtle surface background trigger verified.
  10. **Standard Dialog Input Rows:** Modal dialog inputs render at canonical `36 DIPs` height with clean borders.
- **Original status claim:** `PASSED` (runtime evidence limitation documented above).

### Gate F: Original Source Candidate Freeze & Hash Manifest (superseded)
- **Manifest:** `docs/Frontend_Pass1_Candidate_Hash_Manifest.md`
- **Original status claim:** `FROZEN & VERIFIED`; corrected hashes are recorded in the updated manifest.

---

## 6. Original Certification Claims and Subsequent Challenge

The independent forensic review challenged the candidate for one blocking Pass 1 defect: Reports compact selectors used local 32-DIP heights without canonical compact styles. It also noted that the old runtime harness evidence was unavailable, that the New Purchase geometry statement conflicted with the source, and that the branch name in this report was inaccurate. Those findings and the surgical correction are documented below.

---

## 7. Corrected Candidate Status

The corrected candidate and independent validator disposition are recorded in the following audit section. The original certification verdict above is historical and does not certify the corrected candidate.

## Independent Forensic Challenge and Surgical Correction

### Challenge

- The independent review issued `PASS1_CERTIFICATION_CHALLENGED` with one blocking finding: Reports DatePicker and ComboBoxes had local 32-DIP heights but remained on standard typography/padding.
- No broader Pass 1 rewrite was authorized or performed.

### Root Cause

Local `Height="32"` overrides only the height property. The implicit standard DatePicker and ComboBox styles still supplied 14pt font and standard `12,0` padding, so the production selectors did not consume the canonical compact-family contract.

### Correction

- `Views/ReportsView.xaml`: applied `Input.DatePicker.Compact` to the report DatePicker and `Input.ComboBox.Compact` to both month/year selectors; removed the redundant local 32-DIP heights so the shared styles own geometry.
- `FrontendPass1GeometryBaselineTests.cs`: added `ReportsView_UsesCanonicalCompactStylesForProductionSelectors`, which checks all three production consumers use the intended style and carry no local height override. Existing assertions remain intact; no tests were removed or weakened.

### Focused Validation

- Pass 1 focused tests: `dotnet test tests/EdgeRetails.UnitTests/EdgeRetails.UnitTests.csproj --filter "FullyQualifiedName~FrontendPass1GeometryBaselineTests"` — **8 passed, 0 failed, 0 skipped**.
- Reports runtime probe: an STA WPF probe instantiated `App` and the actual `ReportsView`, showed it in a hidden off-screen window, and resolved controls through the application resources. DatePicker and both ComboBoxes resolved to the exact canonical compact `Style` objects. Each reported `Height=32`, `ActualHeight=32`, `DesiredSize.Height=32`, `FontSize=13`, `Padding=8,0,8,0`. Visual descendants measured: DatePicker 34, each ComboBox 10; **zero descendants exceeded 32 DIPs**. Result: **PASS** with no oversized layout parts. Probe source is retained at `C:\Users\muham\AppData\Local\Temp\EdgeRetailsReportsRuntimeProbe-51ce1e66fec746538b00671f7c59de39\Program.cs` for this review session.
- Desktop Debug build: **succeeded, 0 warnings, 0 errors**.
- Desktop Release build: **succeeded, 0 warnings, 0 errors**.
- Protected frontend regression subset (`Sprint2ForensicAuditTests|Sprint6ForensicAuditTests|RecoveryUiContractTests`): **26 passed, 0 failed, 0 skipped**.
- Desktop builds loaded the production XAML successfully; the runtime probe also instantiated the view with the application resource dictionaries and reported no resource-resolution failure.
- Bounded sibling search for 32-DIP DatePicker/ComboBox consumers lacking canonical compact styles: **no remaining matches**.

### Documentation Corrections

- Original baseline commit remains `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`; current live branch is `tracking-remediation-20261002`. The former report's `main` branch label was documentation drift.
- The current working tree contains unrelated pre-existing backend and other changes. Zero backend modifications refers to changes attributable to Pass 1 and to this surgical correction; no backend file was changed for this correction.
- `NewPurchaseView.xaml` currently declares 35-DIP local heights for the side-by-side supplier ComboBox, invoice TextBox, and DatePicker. This correction did not change that geometry; the previous 36-DIP runtime statement is withdrawn as unverified.
- The original ten-scenario STA harness has no retained source, snapshots, or raw measurements. Its results remain historical claims only. The focused Reports runtime probe above is reproducible from its retained temporary source; the full real-application visual sweep remains for Pass 5.

### Remaining Nonblocking Notes

- **NONBLOCKING — DEFERRED INTERACTIVE STATE HARDENING:** `Input.TextBox.Embedded` does not preserve the standard disabled/read-only visual triggers. No current SearchBox consumer binds `IsReadOnly`; no Pass 1 change was made for this deferred state concern.
- Pass 5 remains responsible for the complete hostile real-application visual sweep.

### Corrected Candidate Freeze and Independent Review

- **Candidate branch / base:** `tracking-remediation-20261002` / `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`.
- **Pass 1 candidate files:** the six original Desktop XAML files, the added `ReportsView.xaml`, and `FrontendPass1GeometryBaselineTests.cs`; only `ReportsView.xaml` and the test file changed in this correction. The refreshed manifest lists SHA-256 hashes for all eight candidate source/test files.
- **Working-tree isolation:** unrelated dirty files were preserved. No backend file is attributable to the correction; no Pass 2/3/4 work was added.
- **Independent validator verdict:** `ACCEPT` — read-only review confirmed the three production style consumers, compact style setters, production-view test, all eight manifest hashes, report accuracy, and frontend-only attribution for this correction; no blocker found.
- **Corrected certification verdict:** `FRONTEND_PASS1_CERTIFIED`.

## 8. Corrected Final Verdict

```text
FRONTEND_PASS1_CERTIFIED
```

The Reports compact-selector blocker is resolved. The corrected source/test candidate hashes match, focused tests and builds pass, the actual ReportsView runtime probe resolves the expected 32-DIP/13pt/8-DIP compact contract with no oversized visual descendants, the bounded sibling scan found no equivalent remaining defect, and the independent validator accepted the evidence. No Pass 2 work was started.
