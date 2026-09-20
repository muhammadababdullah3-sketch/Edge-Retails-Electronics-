# Edge Retails — Frontend Master UI/UX Specification
## For Figma Make / Google Stitch

**Product:** Edge Retails  
**Module Focus:** Electronics Retail only  
**Platform:** Windows Desktop POS  
**Frontend Implementation Target:** WPF + C# + XAML + .NET 10  
**Design Target:** Figma Make or Google Stitch  
**Design Style:** Stripe-inspired modern minimalist desktop business UI  
**Status:** Final Wireframe + Visual System Specification  
**Scope:** V1 Electronics POS only  

---

# 1. Product Context

Edge Retails is a local-first Windows desktop Point of Sale system designed for electronics/electrical retail shops.

This V1 frontend must focus only on the **Electronics Retail** workflow. Do not generalize the UI for pharmacy, clothing, grocery, or other future modules.

The product must support:

- Local counter sales
- Sales history and invoice details
- Thaka / Project-based customer accounts
- Wholesaler / supplier purchases
- Inventory management
- Stock movement audit
- Product management
- Shop expenses
- Customers
- Suppliers / wholesalers
- Daily reporting
- Monthly reporting
- Minimal yearly reporting
- Local signed license
- Local PostgreSQL database status
- Cloud backup status
- User login / switching
- Owner / cashier access patterns

Do **not** include:

- FBR integration
- Digital tax authority invoicing
- Online marketplace features
- CRM campaign features
- Payroll management
- Staff attendance
- Complex accounting ledgers
- Multi-branch UI in V1
- Mobile app layouts
- Web app navigation patterns

---

# 2. Core UX Philosophy

Edge Retails is an **operational desktop application**, not a marketing website.

The UI must feel:

- Professional
- Premium
- Minimal
- Dense but breathable
- Fast
- Keyboard-friendly
- Table-first where needed
- Easy for a shop owner or cashier
- Clear at 1366×768 and above
- Visually modern without looking decorative

Avoid:

- Huge empty whitespace
- Oversized hero sections
- Marketing-style cards
- Excessive charts
- Glassmorphism
- Excessive gradients
- Heavy shadows
- Giant rounded pill UI
- Neon trading-dashboard styling
- Overuse of purple
- Too many colored KPI cards
- Mobile-first stacked layouts
- Over-animated transitions

---

# 3. Desktop Frame & Responsive Rules

## Primary design frame
`1440 × 900`

## Minimum supported visual target
`1366 × 768`

## Layout behavior

- Fixed left sidebar
- Flexible main content area
- Full-height desktop layout
- Data tables should show many rows
- Avoid oversized top bars
- Avoid excessive page margins
- Use responsive columns
- Important actions should remain visible without scrolling where practical
- Primary POS totals should remain visible while cart content scrolls
- Tables should prefer horizontal flexibility over wrapping critical numeric columns

---

# 4. Visual Direction

Use a **Stripe-inspired modern minimalist enterprise UI**, but do not copy Stripe screens literally.

The design should combine:

- Cool neutral surfaces
- Electric indigo brand accent
- Deep navy text
- Crisp borders
- Compact typography
- Clean tables
- Subtle interaction states
- Strong financial-number hierarchy
- Minimal but intentional use of semantic colors

Recommended visual balance:

- 80% neutral colors
- 15% brand indigo
- 5% semantic colors

---

# 5. Final Color System

## 5.1 Brand

| Token | Color | Use |
|---|---|---|
| Primary | `#635BFF` | Primary buttons, active nav, active tabs, links, selected states |
| Primary Hover | `#5851E5` | Hover state |
| Primary Tint | `#F0EDFF` | Selected row, active sidebar background, subtle brand surfaces |
| Dark Mode Brand | `#7C74FF` | Better contrast on dark surfaces |

## 5.2 Light Mode

| Token | Color |
|---|---|
| App Background | `#F6F9FC` |
| Surface / Card / Modal | `#FFFFFF` |
| Subtle Surface | `#F8FAFC` |
| Border | `#E6EBF1` |
| Main Text | `#0A2540` |
| Secondary Text | `#425466` |
| Muted Text | `#6B7C93` |

## 5.3 Dark Mode

| Token | Color |
|---|---|
| App Background | `#0B1220` |
| Surface | `#111827` |
| Raised Surface | `#172033` |
| Border | `#1F2937` |
| Main Text | `#F3F4F6` |
| Secondary Text | `#D1D5DB` |
| Muted Text | `#9CA3AF` |
| Brand Accent | `#7C74FF` |

Light mode is the **default**.

Dark mode exists under:

`Settings → Appearance`

## 5.4 Semantic Colors

| Meaning | Color |
|---|---|
| Success / Paid / In Stock | `#0D9488` |
| Warning / Low Stock / Pending | `#C27803` |
| Danger / Error / Out of Stock | `#DF1B41` |
| Info | `#3B82F6` |

Semantic colors should be used mainly for:

- Small badges
- Status icons
- Alerts
- Inline feedback

Do not fill large KPI cards with semantic colors.

---

# 6. Typography

## Primary UI Font

**Plus Jakarta Sans**

Use for:

- Sidebar
- Buttons
- Forms
- Tables
- POS terminal
- Labels
- Body text
- Navigation
- Dialogs

Weights:

- 400 Regular
- 500 Medium
- 600 SemiBold
- 700 Bold
- 800 ExtraBold only when truly needed

## Display Font

**Space Grotesk**

Use sparingly for:

- Dashboard KPI numbers
- Large report totals
- Major financial figures

Do not use it for ordinary tables/forms/navigation.

## Fallback

- Segoe UI
- sans-serif

Actual desktop application must bundle fonts locally or use system fallback. The final product must not depend on internet access for fonts.

## Type Scale

| Role | Size |
|---|---|
| KPI / Large Financial Number | 28–32px |
| Page Title | 22–24px |
| Section Heading | 16–18px |
| Body | 14px |
| Table | 14px |
| Secondary | 13px |
| Caption | 12px |

Desktop base size: `14px`

---

# 7. Spacing & Shape System

Use a compact 4px-based spacing system.

Recommended spacing tokens:

- 4
- 8
- 12
- 16
- 20
- 24
- 32

Recommended radius:

- Small: 6px
- Medium: 8px
- Large: 10–12px
- Modal: 12px

Avoid extreme 20–30px rounding.

Use borders more than shadows.

Recommended card treatment:

- White surface
- `#E6EBF1` border
- 10–12px radius
- Very subtle shadow only when hierarchy requires it

---

# 8. Global Application Shell

## 8.1 Main structure

```text
┌───────────────┬──────────────────────────────────────────────────────────┐
│               │ Top Bar                                                  │
│   Sidebar     ├──────────────────────────────────────────────────────────┤
│               │                                                          │
│               │ Main Page Content                                        │
│               │                                                          │
│               │                                                          │
└───────────────┴──────────────────────────────────────────────────────────┘
```

## 8.2 Sidebar navigation

Use one persistent left sidebar.

Navigation:

```text
Edge Retails

Dashboard

Sales
  ├─ New Sale
  └─ Sales History

Thaka / Projects

Purchases

Inventory

Expenses

Customers

Suppliers

Reports

Settings
```

Rules:

- Dashboard is standalone
- Do not create Dashboard subcategories
- Sales may expand/collapse
- Use simple line icons
- Active item:
  - background `#F0EDFF`
  - text/icon `#635BFF`
- Hover:
  - subtle neutral background
- Sidebar should remain visually light and quiet

## 8.3 Top Bar

Use:

- Current page title
- Optional page-specific action
- Optional search only where useful
- Right side:
  - current user
  - small system/notification area only if needed

Do not overload the top bar.

---

# 9. Global Component System

Build reusable components before individual screens.

Required components:

- Sidebar item
- Sidebar group
- Page header
- Primary button
- Secondary button
- Danger button
- Icon button
- Text input
- Search input
- Number input
- Dropdown
- Date picker
- Checkbox
- Radio group
- Tabs
- Status badge
- KPI card
- Data table
- Table row
- Pagination
- Quantity stepper
- Modal
- Right drawer
- Toast
- Empty state
- Loading skeleton
- Inline error
- Confirmation dialog
- Permission dialog

Every interactive component needs:

- Default
- Hover
- Pressed
- Focused
- Disabled
- Error where relevant
- Selected where relevant

---

# 10. Table Design Rules

Tables are a first-class component in Edge Retails.

Use:

- Header background: `#F8FAFC`
- Header text: `#425466`
- Row background: `#FFFFFF`
- Hover: `#F8FAFC`
- Selected: `#F0EDFF`
- Borders: `#E6EBF1`
- Primary text: `#0A2540`
- Secondary text: `#6B7C93`

Avoid default zebra striping.

Support:

- Search
- Sort
- Filters
- Status
- Row selection where useful
- Pagination / virtualization
- Empty state
- Loading state

Numeric columns should align consistently.

---

# 11. Financial Display Rules

Financial values should be easy to scan.

Use:

- `Rs.` prefix
- Consistent alignment
- Consistent thousand separators
- Main totals in strong navy text
- Do not automatically color profit green
- Green/teal is reserved for semantic success or positive delta

Examples:

`Rs. 184,500`

`Rs. 31,420`

---

# 12. Canonical Full Screen List

There are exactly **17 full screens**.

```text
01. First Setup / License
02. Login / User Switch
03. Dashboard

04. POS / New Sale
05. Sales History
06. Sale Detail

07. Thaka / Projects
08. Thaka Workspace

09. New Purchase
10. Purchase History

11. Inventory
12. Product Detail

13. Expenses

14. Customers
15. Suppliers

16. Reports
17. Settings
```

Do not create additional full screens for:

- Add Product
- Edit Product
- Add Expense
- Stock Adjustment
- Return
- Customer Detail
- Supplier Detail
- Purchase Detail
- Payment
- Final Settlement

Those are overlays.

---

# 13. Screen 01 — First Setup / License

This screen appears only on first installation.

Use a 3-step setup wizard:

```text
1. License
2. Shop Setup
3. Ready
```

## Step 1 — License

Center the content.

Show:

- Edge Retails branding
- Import License File area
- Browse File button
- License Status
- Continue button after valid license

After validation show:

- Customer
- Store
- Module: Electronics
- License type/status

Do not show technical cryptography details.

## Step 2 — Shop Setup

Fields:

- Shop Name
- Owner Name
- Phone
- Address
- Owner PIN

Do not ask for PostgreSQL host, port, password, or connection string.

## Step 3 — Ready

Show:

- License: Active
- Database: Connected
- Module: Electronics
- Shop Name
- Start Edge Retails button

---

# 14. Screen 02 — Login / User Switch

Purpose: fast staff access.

Layout:

- Centered brand
- User cards
- Selected user
- 4-digit PIN field
- Sign In button

Example users:

- Abdullah — Owner
- Ali — Cashier

Support user switching later from top-right user menu.

Do not use a large username/password enterprise login form.

---

# 15. Screen 03 — Dashboard

Purpose: understand current shop condition within 5 seconds.

No categories. No dashboard sub-navigation.

## Row 1 — Primary KPIs

- Today Sales
- Today Profit
- Expenses
- Low Stock

## Row 2 — Thaka KPIs

- Active Thakas
- Current Thaka Value
- Today Thaka Material

## Row 3

Left:

**Recent Activity**

Show 6–8 items maximum:

- Sale
- Thaka material
- Expense
- Purchase

Right:

**Low Stock**

Show 5–6 urgent products maximum.

No huge charts.

No annual chart.

No customer/supplier statistics.

No Add Product button.

Cards may be clickable navigation shortcuts.

---

# 16. Screen 04 — POS / New Sale

This is the highest-frequency screen.

Use a dedicated 2-column layout.

## Left

- Search / scan product
- Category filter
- Brand filter
- Product list/table
- Product
- Stock
- Price

## Right — Current Sale

Each line:

- Product name
- Brand
- Unit price
- Quantity stepper
- Line total
- Remove

Bottom area:

- Customer: default Walk-in
- Discount
- Subtotal
- Total
- Complete Sale

Do **not** include HOLD in V1.

Do **not** add Thaka button to this screen.

Thaka is a separate workflow.

Out-of-stock products should be disabled or clearly unavailable.

Low stock can still be sold with a subtle warning.

---

# 17. Screen 05 — Sales History

Show:

- Search invoice/customer
- Today
- Yesterday
- This Week
- This Month
- Date picker

Table:

- Invoice
- Time
- Customer
- Items
- Total
- Profit
- Status

Bottom summary:

- Sales count
- Total sales
- Profit

Avoid action-button clutter on every row.

Click row → Sale Detail.

---

# 18. Screen 06 — Sale Detail

Read-only transaction detail.

Top:

- Invoice number
- Status
- Back to Sales History

Information blocks:

- Date
- Time
- Cashier
- Payment method
- Customer

Items table:

- Product
- Qty
- Price
- Discount
- Total

Summary:

- Subtotal
- Discount
- Total

Actions:

- Return Items
- Print Receipt

If returns exist, show Return History.

Never overwrite the original invoice after return.

---

# 19. Screen 07 — Thaka / Projects

Purpose: manage running house/project customer accounts.

Header:

- Page title
- New Thaka

Search:

- Customer
- Project
- Phone

Tabs:

- Active
- Settled

Table:

- Project / Customer
- Started
- Material Value
- Paid
- Balance

Bottom summary:

- Active project count
- Material value
- Balance

Click row → Thaka Workspace.

---

# 20. Screen 08 — Thaka Workspace

This is the main project account screen.

Top:

- Back to Thaka
- Project name
- Active / Settled badge
- Customer
- Phone
- Address
- Start date

KPI cards:

- Material Value
- Paid
- Balance

Primary actions:

- Add Material
- Record Payment
- Final Settlement

## Material History

Columns:

- Date
- Product
- Qty
- Rate
- Total
- Added By

## Payment History

Columns:

- Date
- Amount
- Method
- Note

Settled project should become read-only for normal users.

---

# 21. Screen 09 — New Purchase

Purpose: receive inventory from wholesaler.

Top fields:

- Supplier
- Invoice number
- Purchase date
- Note

Product entry:

- Search / scan product

Purchase table:

- Product
- Qty
- Cost
- Sale Price
- Total

Summary:

- Subtotal
- Other Charges
- Total

Primary action:

- Save Purchase

Allow quick Add New Product dialog without leaving the purchase screen.

Saving purchase:

- Creates purchase
- Increases stock
- Creates stock movement
- Updates purchase history

---

# 22. Screen 10 — Purchase History

Show:

- Search invoice/supplier
- Today
- This Week
- This Month
- Supplier filter

Table:

- Purchase
- Date
- Supplier
- Items
- Total

Bottom:

- Purchase count
- Total purchase amount

Click row → Purchase Detail drawer.

---

# 23. Screen 11 — Inventory

This is the main stock-control center.

Top KPI cards:

- Total Products
- Low Stock
- Out of Stock

Do not show inventory value until valuation method is formally decided.

Search:

- Product
- Model
- SKU
- Barcode

Tabs:

- All Stock
- Low Stock
- Out of Stock
- Stock Movements

Filters:

- Category
- Brand
- Stock status

Main table:

- Product
- Brand
- Stock
- Minimum Stock
- Cost
- Sale Price

Actions:

- Add Product
- Edit
- Adjust Stock
- Product Detail

## Stock Movements tab

Columns:

- Time
- Product
- Type
- Qty
- Before
- After

Movement types:

- Purchase
- Sale
- Thaka
- Sale Return
- Purchase Return
- Damage
- Adjustment

---

# 24. Screen 12 — Product Detail

Top:

- Back to Inventory
- Product Name
- Edit Product

Product metadata:

- Brand
- SKU
- Category
- Model
- Unit

KPI cards:

- Current Stock
- Purchase Cost
- Sale Price

Show Minimum Stock.

Tabs:

- Overview
- Stock Movement
- Purchases
- Sales

Stock Movement table:

- Date
- Type
- Reference
- Qty
- Before
- After

Optional electronics attributes:

- Warranty
- Serial Tracking
- IMEI Tracking
- Color
- Variant

Important:

Current stock must never be directly editable.

---

# 25. Screen 13 — Expenses

Top KPI cards:

- Today
- This Month
- Top Category

Filters:

- Today
- This Week
- This Month
- Category

Table:

- Date
- Category
- Subcategory
- Note
- Amount

Expense categories:

- Shop Rent
- Staff Salary
- Staff Expense
- Electricity
- Gas / Water
- Internet
- Transport
- Maintenance
- Stationery
- Marketing
- Other

Important:

Inventory Purchase is **not** an Expense.

---

# 26. Screen 14 — Customers

Search:

- Name
- Phone
- Project

Table:

- Customer
- Phone
- Local Sales
- Active Thaka
- Last Sale

Action:

- Add Customer

Click row → Customer Detail drawer.

Customer fields remain simple:

- Name
- Phone
- Address
- Notes

Do not turn this into a CRM.

---

# 27. Screen 15 — Suppliers / Wholesalers

Search:

- Supplier
- Phone
- City

Table:

- Supplier
- Phone
- Purchases
- Last Purchase
- City

Action:

- Add Supplier

Click row → Supplier Detail drawer.

Supplier fields:

- Supplier Name
- Phone
- City
- Address
- Notes

---

# 28. Screen 16 — Reports

Use one screen with:

`Daily | Monthly | Yearly`

## Daily

Show:

- Total Sales
- Gross Profit
- Expenses
- Net Profit
- Purchases
- Thaka Material

Breakdown:

- Local Sales
- Gross Profit
- Shop Expenses
- Net Profit
- Purchases
- Thaka Material Issued
- Sale Returns

## Monthly

Show:

- Total Sales
- Gross Profit
- Net Profit
- Expenses
- Purchases
- Thaka Material

Expense breakdown:

- Salaries
- Rent
- Electricity
- Staff Expenses
- Other

Avoid unnecessary charts in V1.

## Yearly

Keep intentionally minimal.

Show only:

- Total Sales
- Total Profit

Use a year selector.

**For the visual design, treat Yearly Total Profit as Net Profit.**

---

# 29. Screen 17 — Settings

Use internal left navigation inside Settings.

Sections:

- Shop
- Receipt
- Users & Access
- Categories & Units
- Backup
- License
- Appearance
- Database

## Shop

- Shop Name
- Owner Name
- Phone
- Address
- Logo

## Receipt

- Printer
- Paper size
- Header
- Footer
- Show customer name
- Show cashier
- Auto print after sale

## Users & Access

Table:

- Name
- Role
- Status
- Action

Roles:

- Owner
- Manager
- Cashier

## Backup

Show:

- Local DB health
- Last backup
- Cloud backup status
- Automatic backup toggle
- Frequency
- Backup Now
- Recent backup history

Restore should be less prominent.

## License

Show:

- License ID
- Store
- Status
- Module: Electronics
- Expiry
- Terminals
- Import New License

## Appearance

- Light
- Dark
- System optional

Light is default.

## Database

Show diagnostics only:

- PostgreSQL Running
- Connection Connected
- Database Size
- Worker Service Running
- Last Backup

Do not expose raw DB credentials to normal users.

---

# 30. Overlay Architecture

The design must use overlays instead of creating unnecessary full pages.

Use:

- Modal for focused actions
- Drawer for contextual details
- Toast for short feedback
- Inline validation for field errors

Never stack modal-on-modal.

---

# 31. Modal Size Rules

## Small

Approx. 440–480px

Use for:

- Confirmation
- Payment
- Simple expense

## Medium

Approx. 560–640px

Use for:

- Customer
- Supplier
- Stock adjustment
- Thaka setup

## Large

Approx. 760–900px

Use for:

- Product editor
- Sale Return
- Purchase Return
- Complex material selection

## Right Drawer

Approx. 420–520px

Use for:

- Purchase detail
- Customer detail
- Supplier detail

---

# 32. Overlay 01 — Complete Sale

Fields:

- Total
- Payment Method:
  - Cash
  - Bank
  - Other
- Amount Received
- Change
- Customer
- Print Receipt checkbox

Buttons:

- Cancel
- Complete Sale

For bank/other payment:

- Reference optional

Prevent duplicate submission.

During save:

`Completing Sale...`

Success:

Toast and close modal.

---

# 33. Overlay 02 — Sale Return

Show original sale products.

Columns:

- Product
- Sold Qty
- Return Qty
- Refund

Fields:

- Return reason
- Refund method
- Total refund

Return reasons may include:

- Customer Return
- Defective
- Wrong Item
- Other

Result:

- Return record created
- Inventory increases
- Refund recorded
- Original invoice preserved

---

# 34. Overlay 03 — New Thaka

Fields:

- Customer search/select
- Project / House Name
- Phone
- Address
- Start Date
- Notes

Existing customer selection should prefill contact details.

After create:

Open Thaka Workspace.

---

# 35. Overlay 04 — Add Thaka Material

Fields:

- Search / Scan Product
- Selected product
- Available stock
- Quantity
- Rate
- Total
- Note

If requested quantity is greater than stock:

- Show inline warning
- Disable Add Material

Result:

- Thaka total increases
- Inventory decreases
- Stock movement created

---

# 36. Overlay 05 — Record Thaka Payment

Show:

- Current Balance

Fields:

- Amount
- Method
- Date
- Note

Buttons:

- Cancel
- Save Payment

---

# 37. Overlay 06 — Final Settlement

Show:

- Material Total
- Previous Payments
- Final Discount
- Remaining Before Settlement
- Received Now
- Payment Method
- Final Balance

Warning:

`Project will be marked as Settled.`

Buttons:

- Cancel
- Settle Project

After success:

- Active → Settled
- Normal editing disabled

---

# 38. Overlay 07 — Add / Edit Product

Fields:

- Product Name
- Category
- Brand
- Model
- SKU / Barcode
- Unit
- Purchase Cost
- Selling Price
- Minimum Stock

Electronics options:

- Serial Tracking
- IMEI Tracking
- Warranty

Important:

Do **not** include Initial Stock.

Stock must come from:

- Purchase
- Stock Adjustment

---

# 39. Overlay 08 — Stock Adjustment

Show:

- Product
- Current Stock

Fields:

- Adjustment Type
- Quantity
- New Stock preview
- Reason
- Note

Reasons:

- Damaged
- Lost
- Physical Count Correction
- Other

Never silently overwrite stock.

---

# 40. Overlay 09 — Add / Edit Expense

Fields:

- Category
- Subcategory
- Amount
- Date
- Payment Method
- Staff Member when applicable
- Note

If category is Staff Salary:

Staff Member becomes required.

---

# 41. Overlay 10 — Add / Edit Customer

Fields:

- Name
- Phone
- Address
- Notes

Keep it simple.

---

# 42. Overlay 11 — Add / Edit Supplier

Fields:

- Supplier Name
- Phone
- City
- Address
- Notes

---

# 43. Overlay 12 — Purchase Return

Open from Purchase Detail drawer.

Show:

- Supplier
- Purchase ID
- Product
- Purchased Qty
- Return Qty
- Return Value
- Reason

Result:

- Inventory decreases
- Purchase Return record created
- Original purchase remains intact

---

# 44. Drawer 01 — Purchase Detail

Right-side drawer.

Show:

- Purchase ID
- Supplier
- Date
- Supplier invoice/reference
- Products
- Quantities
- Costs
- Total

Actions:

- Print
- Return to Supplier

---

# 45. Drawer 02 — Customer Detail

Show:

- Name
- Phone
- Address
- Local Sales value
- Active Thaka count
- Current Thaka Balance

Tabs:

- Sales
- Thaka
- Details

Recent activity.

Action:

- Edit Customer

---

# 46. Drawer 03 — Supplier Detail

Show:

- Supplier
- Phone
- City
- Total Purchases
- Last Purchase

Tabs:

- Purchases
- Products
- Details

Action:

- Edit Supplier

---

# 47. Reusable Confirmation Dialog

Use one generic confirmation component.

Examples:

- Remove expense
- Disable user
- Restore backup
- Cancel important operation
- Reopen settled Thaka

Structure:

- Clear title
- One-sentence consequence
- Cancel
- Destructive / Confirm action

Do not use red for normal Cancel.

---

# 48. Permission Dialog

Use when a restricted user accesses a protected function.

Example message:

`You do not have permission to view profit information.`

Actions:

- Close

Optional future manager override is out of V1 unless explicitly requested later.

---

# 49. Toast System

Use bottom-right toasts.

Success example:

`✓ Sale completed successfully`

Error example:

`⚠ Receipt failed to print. Sale was saved successfully. [Retry]`

Important:

Printer failure must not visually imply that the sale itself failed.

Toast types:

- Success
- Info
- Warning
- Error

Keep them compact.

---

# 50. Loading, Empty & Error States

## Loading

Use skeletons for:

- KPI cards
- Table rows
- Recent activity
- Low-stock list

Avoid blocking the whole page with a single spinner.

## Empty State

Use a compact state with one obvious next action.

Example:

`No sales recorded today.`  
`[ New Sale ]`

## Error

Use inline or section-level error where possible.

Do not show raw stack traces.

---

# 51. Keyboard & Desktop Interaction

This is a desktop POS, so keyboard use matters.

The visual design must support:

- Clear focus rings
- Predictable tab order
- Enter to confirm where safe
- Escape to close non-critical overlays
- Arrow navigation where useful
- Barcode scanner input in search

Potential shortcuts may be added later:

- F2 Search Product
- F4 Customer
- F6 Discount
- F8 Payment
- F10 Complete Sale
- Esc Close / Back

Do not permanently display shortcut clutter everywhere yet.

---

# 52. Electronics-Specific Product Behavior

Product design must accommodate different electronics inventory styles.

Examples:

## Quantity-based

- LED bulb
- Switch
- Breaker
- Socket

## Roll / length-based

- Wire
- Cable

## Unit-identity-based

- Mobile
- Device
- Appliance
- Serialized electronics

Optional product attributes:

- Brand
- Model
- Warranty
- Color
- Variant
- Serial tracking
- IMEI tracking

Do not force IMEI/serial UI on every product.

---

# 53. Inventory Truth Model — Visual Implication

The UI must communicate that stock comes from movements.

Concept:

```text
Opening Stock
+ Purchases
+ Sale Returns
- Local Sales
- Thaka Material
- Purchase Returns
- Damage
± Adjustments
= Current Stock
```

Therefore:

- No direct editable Current Stock field
- Product detail must show movement history
- Adjustment must require reason
- Purchase and Thaka actions must visually feel connected to stock

---

# 54. Thaka Reporting Rule for V1 Visuals

A backend accounting rule for Thaka revenue recognition is intentionally not finalized.

Therefore in the UI:

- Keep Local Sales separate from Thaka Material
- Do not silently merge Thaka Material into Today Sales
- Do not merge Thaka into Local Sale profit cards
- Dashboard and Reports should show Thaka as a separate metric

This avoids making an accounting assumption in the visual design.

---

# 55. Navigation & Interaction Rules

- Clicking Dashboard KPI may open the relevant filtered screen
- Clicking table row opens detail screen/drawer
- Avoid action clutter in every table row
- Use row click + contextual actions
- Use dialogs for short actions
- Use full screens only for high-focus workflows
- Preserve context when using drawers
- Return/back navigation should be obvious
- Avoid hidden hamburger navigation on desktop

---

# 56. Role-Aware UI

The visual system should allow role-based visibility.

Owner:

- Full access

Cashier:

- New Sale
- Sales History
- Other modules depending on permission
- No profit/settings by default

Do not hardcode a completely different shell per role.

Use the same architecture with restricted/hidden actions.

---

# 57. WPF-Friendly Design Constraints

Although design is created in Figma Make or Stitch, the final implementation target is WPF/XAML.

Design with implementation realism.

Prefer:

- Grid-based layouts
- Reusable components
- Consistent dimensions
- Standard forms
- Standard tabs
- Standard tables
- Resource-token-friendly colors
- Predictable states
- Simple transitions
- Clean drawers/modals

Avoid:

- Browser-only interactions
- Complex CSS-only effects
- Excessive blur
- Deep nested floating layers
- Highly fluid mobile layouts
- Experimental visual effects that are difficult in WPF
- Unnecessary custom canvas drawing

---

# 58. Figma / Stitch Generation Instructions

When generating the visual design:

1. Build the **global shell and design system first**.
2. Maintain the same component language across all 17 screens.
3. Do not redesign the sidebar differently per page.
4. Do not invent new colors.
5. Do not invent new product modules.
6. Do not add charts unless specified.
7. Do not add FBR or tax-authority UI.
8. Do not merge Thaka into normal sales.
9. Do not add mobile responsive screens.
10. Do not add unnecessary analytics.
11. Keep table density appropriate for desktop POS.
12. Use realistic electronics product examples.
13. Use `Rs.` for currency.
14. Keep light mode as the default.
15. Make all main components reusable.
16. Create hover/focus/disabled/selected states.
17. Create overlays as reusable modal/drawer components.
18. Preserve the 17-screen canonical architecture.
19. Do not generate extra full pages for overlay actions.
20. Prioritize operational clarity over visual decoration.

---

# 59. Recommended Figma File Structure

```text
00 — Cover

01 — Foundations
02 — Colors
03 — Typography
04 — Components
05 — Patterns
06 — Application Shell

07 — Dashboard
08 — Sales
09 — Thaka
10 — Purchases
11 — Inventory
12 — Expenses
13 — Customers
14 — Suppliers
15 — Reports
16 — Settings

17 — System Screens
18 — Dialogs & Drawers
19 — States
20 — Prototype
```

---

# 60. Recommended Generation Order

Do not generate all screens in one uncontrolled pass.

## Batch 1 — Foundation

- Design System
- Application Shell
- Dashboard

## Batch 2 — Sales

- POS / New Sale
- Sales History
- Sale Detail
- Complete Sale modal
- Sale Return modal

## Batch 3 — Thaka

- Thaka / Projects
- Thaka Workspace
- New Thaka
- Add Material
- Record Payment
- Final Settlement

## Batch 4 — Purchasing & Inventory

- New Purchase
- Purchase History
- Purchase Detail drawer
- Purchase Return
- Inventory
- Product Detail
- Add/Edit Product
- Stock Adjustment

## Batch 5 — Finance & Contacts

- Expenses
- Add/Edit Expense
- Customers
- Customer drawer
- Suppliers
- Supplier drawer

## Batch 6 — Final System

- Reports
- Settings
- Login
- First Setup / License
- Generic confirmation
- Permission dialog
- Toast states
- Empty/loading/error states

After each batch, preserve existing components instead of regenerating them.

---

# 61. Final Visual Quality Checklist

Before considering the design complete, verify:

- [ ] Exactly 17 full screens
- [ ] Consistent sidebar
- [ ] Consistent top bar
- [ ] Consistent button system
- [ ] Consistent table system
- [ ] Consistent modal system
- [ ] Consistent drawer system
- [ ] Light theme complete
- [ ] Dark theme tokens defined
- [ ] All text readable at desktop density
- [ ] Dashboard not overloaded
- [ ] POS cart remains prominent
- [ ] Thaka clearly separate from local sales
- [ ] Inventory movement visible
- [ ] Expenses separate from purchases
- [ ] Yearly report contains only Total Sales + Total Profit
- [ ] No FBR UI
- [ ] No mobile UI
- [ ] No unnecessary charts
- [ ] No direct editable stock field
- [ ] No runtime web-font dependency assumed in final desktop app
- [ ] Dialogs have validation/error/loading states
- [ ] Destructive actions use explicit confirmation
- [ ] WPF implementation remains realistic

---

# 62. Final Design Statement

Edge Retails should visually feel like a premium, modern Windows business application built for real shop operations.

The final experience should combine:

**Stripe-inspired clarity + compact desktop density + electronics-retail practicality + strong inventory transparency + fast POS operation.**

The design must remain visually refined without sacrificing the speed and information density required by a real retail counter.

---

## FINAL LOCKED DESIGN FOUNDATION

```text
Windows Desktop
1440×900 Primary Frame
1366×768 Minimum Target

Stripe-Inspired Minimal UI
Electric Indigo Accent
Cool Neutral Surfaces
Plus Jakarta Sans
Space Grotesk for KPI/Display

17 Full Screens
Reusable Modals
Reusable Drawers
Reusable Tables
Keyboard-Friendly POS
Electronics-Only V1
Thaka Separate from Local Sales
PostgreSQL-Backed Inventory Logic
Local Signed License
No FBR
No Mobile Layouts
No Unnecessary Charts
WPF-Friendly Components
```

**Document Status: READY FOR FIGMA MAKE / GOOGLE STITCH**
