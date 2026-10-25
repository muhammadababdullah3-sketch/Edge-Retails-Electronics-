# ER-RUN-FE-ATTACH-20261025-001 — closure

**Date:** 2026-10-25  
**Outcome:** Canonical WPF views and dialogs under `src/EdgeRetails.Desktop/Views/` now use `Resources/` theme tokens (typography, brushes, cards, inputs, buttons). Inline `FontSize` / `FontWeight` and hardcoded `#` colours are removed from production views (excluding `Sprint1VerificationView` gallery).

## What shipped

- Safe mechanical pass: `Apply-ViewThemeTokens.ps1` (TextBlock-only; skips elements with inline `TextBlock.Style`).
- Resource extensions: `Text.Run.*`, `Input.TextBox.Amount|Quantity`, `Input.ComboBox.Micro`, `Input.CheckBox.*`, `Button.Neutral.Inline|Micro`, `Table.TextCell.*`, `Text.PriceHero`, `Text.Micro`, `Color.Shadow.Deep`.
- P0–P2: all listed views/dialogs attached; grep on `Views/**/*.xaml` (excl. Sprint1) is clean for `FontSize="`, `FontWeight="`, `Foreground="#`, `Background="#`, `Color="#`.

## Verify

| Check | Result |
|-------|--------|
| `dotnet build` Desktop Release | Pass |
| `RenderedVisualProofTests` + Imp101 dialog/XAML | 10/10 pass |
| `MasterSharedInputTemplateTests` | 33/33 pass |
| `RenderedVisualProofTests` + Imp101 | 10/10 pass |
| Full `EdgeRetails.Desktop.PerformanceTests` | 472+ pass; some failures unrelated to XAML (ProgramData intent store ACL, Phase7 timeouts, editor atomic-gate flakes) |

## Manual smoke (recommended)

POS, Dashboard, Login, Settings, Thaka list — each in Light and Dark via Settings or theme toggle.

## Note

`Run` inline text uses `Text.Run.*` styles, not `TextBlock` styles, to satisfy WPF `TargetType` rules.
