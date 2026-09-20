# Edge Retails Sprint 1 — Button, Colour, Texture & Layer Audit

Status: **Focused forensic pass complete (static/offline)**

## Figma button population

The current Edge Retails Figma page contains **429 button frames**.

Observed visual construction:
- 71 gradient-filled buttons
- 109 solid-filled buttons
- 249 transparent/text/icon buttons
- 272 buttons with a visible stroke
- 89 buttons with a drop shadow
- 0 button image fills

This confirms that the design does **not** use bitmap/noise textures for buttons. Its “texture” comes from controlled gradients, borders, subtle elevation, semantic tint layers, opacity and nested icon/text layers.

## Most important radius families

- Radius 8: dominant filled/outlined action button
- Radius 6: tabs and compact controls
- Radius 9: high-emphasis transaction CTA
- Radius 5: small icon actions
- Radius 0: text-only links/settings rows

## Important button height families

- ~35 px: standard CTA / neutral action
- ~31.5 px: compact workflow actions
- 46 px: Complete Sale primary transaction CTA
- ~48 px: Save Purchase primary transaction CTA
- 28 px: compact tab/control family
- 20–24 px: text/icon actions
- 42 px: login keypad family

## Exact semantic button treatments preserved in WPF

### Brand primary
Six-stop diagonal ramp:
`#635BFF → #665DFF → #6960FF → #6C62FF → #6F64FF → #7266FF`

Standard geometry:
- ~35 px height
- 14 px horizontal padding
- radius 8
- shadow: x0 / y3 / blur 8 / `#3730A3` at 18%

### Neutral
- white surface
- `#E6EBF1` border
- radius 8
- shadow: x0 / y1 / blur 3 / `#0F172A` at 6%
- text `#425466`

### Add Material / blue workflow action
- `#2563EB → #3B82F6`
- ~31.5 px high
- radius 8
- 14 px horizontal padding
- shadow x0 / y2 / blur 7 / `#2563EB` at 28%

### Record Payment
- `#0891B2 → #14B8A6`
- ~31.5 px high
- radius 8
- shadow x0 / y2 / blur 7 / `#0891B2` at 28%

### Final Settlement
- `#D97706 → #F59E0B`
- ~31.5 px high
- radius 8
- shadow x0 / y2 / blur 7 / `#D97706` at 28%

### Expense
The current Figma design uses a smooth ten-stop ramp rather than a simple two-stop orange:
`#EA580C → #EC5B0D → #ED5E0E → #EF6110 → #F16411 → #F26712 → #F46A13 → #F66D14 → #F77015 → #F97316`

- ~35 px high
- radius 8
- shadow x0 / y3 / blur 8 / `#EA580C` at 18%

### Complete Sale
- `#0D9488 → #10B981`
- 46 px high
- radius 9
- shadow x0 / y2 / blur 10 / `#0D9488` at 35%
- composed of a text layer plus a keyboard-hint layer in Figma

### Save Purchase
- `#2563EB → #3B82F6`
- ~48 px high
- radius 9
- shadow x0 / y3 / blur 10 / `#2563EB` at 30%

## Surface/layer audit

Across 170 card/panel/header/sidebar/modal-like Figma frames:
- image fills: 0
- gradient fills: 54
- solid fills: 106
- with strokes: 158
- with shadows: 144
- blur effects: 0

The visual language is therefore **layered but crisp**. It is not glassmorphism and not texture-image based.

Recurring constructions include:

### Neutral elevated card
- soft white gradient:
  `#FFFFFF → #FEFEFF → #FCFDFF → #FBFCFF → #FAFBFF`
- border `#E6EBF1`
- radius 10
- shadow x0 / y1 / blur 3 / `#0F172A` at 4%

### Brand KPI card
- soft indigo gradient:
  `#EEF2FF → #F0F4FF → #F2F5FF → #F4F7FF → #F7F9FF → #F9FAFF → #FBFCFF`
- border `#4F46E5`
- radius 10
- subtle 4% elevation

### Success / warning / Thaka / info KPI cards
Each uses its own very light semantic gradient plus a semantic border and the same restrained elevation model.

### Modal layer
- white surface
- border `#E6EBF1`
- shadow x0 / y25 / blur 50 / black at 25%
- separate scrim underneath

### Shell layers
Sidebar and top bar use distinct directional shadows, not the same generic shadow:
- sidebar: x1 / y0 / blur 6 / `#0F172A` at 5%
- top bar: x0 / y1 / blur 4 / `#0F172A` at 5%

## WPF changes made after this focused audit

- Added exact six-stop brand CTA ramp.
- Replaced simplified expense gradient with the exact ten-stop ramp.
- Added separate primary, neutral, blue, payment, warning, expense, strong-success and strong-blue shadow resources.
- Added exact compact-action, standard-action and primary-transaction button size tiers.
- Added dedicated compact semantic button styles.
- Added large Complete Sale and Save Purchase button styles.
- Added text-only action styles for Settings/Inventory link actions.
- Reworked KPI cards to use full semantic gradients, uniform 1 px semantic borders and subtle layered elevation.
- Added a recurring neutral textured-card style.
- Split sidebar/topbar shadow resources to match their different directional elevation.
- Updated modal shadow to the current Figma 25% / blur-50 treatment.

## Post-change static verification

- XAML files: 30
- C# files: 43
- resource keys: 213
- resource references checked: 363
- missing resources: 0
- invalid WPF patterns: 0
- theme parity errors: 0
- duplicate resource keys: 0
- StaticResource load-order issues: 0

Runtime rendering is still pending the Windows device reconnect.
