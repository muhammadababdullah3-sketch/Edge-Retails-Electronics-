# Edge Retails Frontend Pass 1 — Candidate Source Hash Manifest

**Pass Identity:** Frontend Pass 1 — Design-System Geometry + Global Input Baseline Repair  
**Base Commit / HEAD:** `9795683` (`feat(tracking): implement tracking manufacturer identity authority V1`)  
**Candidate Timestamp:** `2026-10-04T12:52:22+05:00`  
**Target Architecture:** Frozen Single-Machine Desktop WPF (.NET 10)  
**Scope Boundary:** Strict Frontend-Only (`src/EdgeRetails.Desktop` and `tests/EdgeRetails.UnitTests`)
**Live Branch / Base Commit:** `tracking-remediation-20261002` / `97956831e01d7ed6b4e2f1051b15e8ec272e8e4b`
**Backend Changes Attributable to Pass 1:** Zero (`0`); unrelated pre-existing backend changes remain in the live working tree.

---

## Candidate Source File Manifest (SHA-256)

| Relative File Path | SHA-256 Checksum | Role / Reason Changed |
| :--- | :--- | :--- |
| `src/EdgeRetails.Desktop/Resources/Spacing.xaml` | `FEEDDF689F6ABDA0C2216DF9EE39565A465316F41E362721B6F4ACFCAC99BA28` | Shared spacing authority: harmonized `Dimension.Control.Compact` to `32` DIPs to match `Dimension.Button.CompactAction`; added token `Padding.Input.Compact` (`8,0`). |
| `src/EdgeRetails.Desktop/Resources/Inputs.xaml` | `8D4CA81377EEE1332289511A88FA4C9326A3179B783BC54F9CEEF1FF242749DE` | Shared input styling authority: added `Input.TextBox.Embedded`; normalized compact TextBox, ComboBox, PasswordBox to 32 DIPs with 13pt font and `Padding.Input.Compact`; added `Input.PasswordBox.Compact` and `Input.DatePicker.Compact`; normalized `Input.DatePicker` font to 14pt; added `SnapsToDevicePixels="True"` to prevent subpixel jitter. |
| `src/EdgeRetails.Desktop/Controls/SearchBox.xaml` | `9DD9AE3A70FD9D0FC982FBD3CA963C5312E559F4D5ED8FD100719A3B81B84828` | SearchBox composite control: adopted `Input.TextBox.Embedded` to eliminate 12 DIP left-padding offset and hardcoded 36 DIP height; aligned placeholder and typed text at Column 2, X=0, baseline-centered with zero vertical/horizontal jump. |
| `src/EdgeRetails.Desktop/Views/Dialogs/CompleteSaleDialog.xaml` | `E32BC887194A80E8D65C649EA0ED2AE8FC367ACF050282B126C663E6157D523E` | Complete Sale currency input: adopted `Input.TextBox.Embedded` on `AmountReceivedTextBox` inside 46 DIP container; harmonized "Rs." prefix and amount text to 18pt Bold `Font.Numeric`, centered vertically with exact baseline parity. |
| `src/EdgeRetails.Desktop/Views/PosView.xaml` | `F7A17AD1FEE56213DB79AB4002F3A802D6EDFCE470D1186032C51595B4386A12` | POS discount input: adopted `Input.TextBox.Embedded` on discount amount TextBox inside 28 DIP container, eliminating 36 DIP style overflow and aligning baseline with "Rs." prefix. |
| `src/EdgeRetails.Desktop/Views/Dialogs/SalesReturnDialog.xaml` | `53E9BD3CA4BE0CB8262CD4942F8E2DD27AB0D830A5E158949E3CA2B95C07ABB0` | Sales Return quantity stepper: adopted `Input.TextBox.Embedded` on `ReturnQuantityText` inside 28 DIP stepper border, eliminating 36 DIP vertical overflow/clipping. |
| `src/EdgeRetails.Desktop/Views/ReportsView.xaml` | `CB513F5E1BA7CA6C13D519864DD9552FE368E51EE5D9010A1C539E2DCDECC28C` | Surgical correction: Reports DatePicker and month/year ComboBoxes now consume canonical compact styles; style owns 32-DIP geometry. |
| `tests/EdgeRetails.UnitTests/FrontendPass1GeometryBaselineTests.cs` | `36EBDA668B3DEAA0BA89C7E5A8CA64BE7A5DCC6EFCABA5327B79109CE7F177A9` | Forensic contract test suite: 8 automated tests covering spacing, standard/compact input styles, implicit styles, SearchBox, ReportsView compact consumers, CompleteSale, POS discount, and SalesReturn. |

---

## Working-Tree State Verification

- **Total Pass 1 Desktop XAML Files:** 7 (the six original files plus `Views/ReportsView.xaml`)
- **Total Pass 1 Test Files:** 1 C# test file (now with 8 focused tests)
- **Backend Files Modified by Pass 1:** Exactly 0 attributable to Pass 1; unrelated pre-existing backend changes are outside this candidate.
- **Pre-existing Working Tree Changes:** Preserved intact without modification or regression
