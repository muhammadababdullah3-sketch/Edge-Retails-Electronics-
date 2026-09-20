PROJECT: EDGE RETAILS
TASK: FORENSIC UI/UX POLISH AND PRODUCTION-READY DESIGN REFINEMENT

IMPORTANT:
Do NOT redesign the product from scratch.
Do NOT change the existing information architecture.
Do NOT create new screens unless explicitly requested.
Do NOT remove existing business functionality.
Do NOT convert the UI into a mobile-first or web-dashboard style.

The existing design already represents the correct product skeleton and screen architecture.

Your job is to improve the existing design deeply and systematically so it becomes:

1. Premium
2. Professional
3. Modern
4. Operationally efficient
5. Desktop-first
6. WPF implementation friendly
7. Figma MCP friendly
8. Consistent across every screen
9. Visually refined without becoming decorative
10. Suitable for a real electronics retail POS used for long working hours

============================================================
A. IMPLEMENTATION CONTEXT
============================================================

This is NOT a web application.

Final production platform:

Windows Desktop
WPF
C#
XAML
.NET 10

The final design will later be read directly through Figma MCP by an AI development agent called Antigravity.

Antigravity will use the Figma design as the visual source of truth and reproduce the interface natively in WPF/XAML.

Therefore every design decision must satisfy BOTH requirements:

A. It looks premium and professional to the end user.
B. It can be understood and reproduced reliably from Figma MCP in WPF.

Do not use visual techniques that would require fragile browser-only CSS behavior.

Avoid:

- CSS-specific effects
- excessive blur
- glassmorphism
- huge gradient backgrounds
- complex masks
- experimental filters
- canvas-heavy visual effects
- highly fluid mobile layouts
- unrealistic browser interactions
- excessive animation
- overlapping decorative layers
- arbitrary absolute positioning
- giant rounded cards
- giant whitespace
- marketing landing-page styling
- neon trading-dashboard aesthetics

Prefer:

- Grid-based layout logic
- Auto Layout
- reusable components
- predictable dimensions
- clear component variants
- strong visual hierarchy
- clean typography
- subtle borders
- restrained shadows
- explicit states
- consistent spacing
- desktop density
- reusable patterns

============================================================
B. DO NOT CHANGE THE PRODUCT ARCHITECTURE
============================================================

There are exactly 17 primary full screens:

01 First Setup / License
02 Login / User Switch
03 Dashboard
04 POS / New Sale
05 Sales History
06 Sale Detail
07 Thaka / Projects
08 Thaka Workspace
09 New Purchase
10 Purchase History
11 Inventory
12 Product Detail
13 Expenses
14 Customers
15 Suppliers
16 Reports
17 Settings

DO NOT generate an 18th primary screen.

The following interactions must remain overlays, dialogs, drawers or contextual components:

Complete Sale
Sale Return
New Thaka
Add Thaka Material
Record Thaka Payment
Final Settlement
Add Product
Edit Product
Stock Adjustment
Add/Edit Expense
Add/Edit Customer
Add/Edit Supplier
Purchase Return
Purchase Detail
Customer Detail
Supplier Detail
Generic Confirmation
Permission Required
Toast Notifications
Loading States
Empty States
Error States

Do not turn these into separate pages.

============================================================
C. PRIMARY DESIGN GOAL
============================================================

The target visual personality is:

Premium enterprise desktop software
+
Stripe-inspired clarity
+
modern Windows business application
+
high operational density
+
electronics retail practicality

The application must NOT feel like:

- generic Bootstrap admin panel
- accounting software from 2012
- cryptocurrency dashboard
- marketing website
- mobile app stretched to desktop
- template marketplace dashboard
- overly playful SaaS product

It should feel precise, calm, expensive, fast and trustworthy.

The interface must remain comfortable for cashiers and shop owners who may use it continuously for many hours.

============================================================
D. FRAME AND DESKTOP TARGET
============================================================

Primary frame:

1440 × 900

Minimum supported visual target:

1366 × 768

Optimize all layouts for these desktop dimensions.

Important information must remain visible at 1366 × 768.

Avoid layouts that require unnecessary vertical scrolling.

POS totals and primary checkout action should remain visible while cart items scroll.

Tables should display useful row counts.

Do not wrap critical financial columns unless absolutely necessary.

============================================================
E. EXACT GLOBAL GEOMETRY
============================================================

Use a disciplined dimensional system.

Sidebar Expanded:
232 px

Sidebar Collapsed:
72 px

Top Bar:
64 px

Main page horizontal padding:
24 px

Main page vertical padding:
20 px

Major section gap:
24 px

Standard component gap:
16 px

Compact gap:
8 px

Standard input height:
40 px

Compact input:
36 px

Primary button:
40 px

Compact button:
36 px

Table header:
40 px

Standard table row:
44 px

Card padding:
16 px

Large card padding:
20 px

Right drawer:
480 px

Small modal:
440–480 px

Medium modal:
560–640 px

Large modal:
760–900 px

Small radius:
6 px

Medium radius:
8 px

Large radius:
10–12 px

Modal radius:
12 px

Do not randomly change control height or radius between screens.

============================================================
F. SPACING SYSTEM
============================================================

Use a strict 4px-based spacing scale:

4
8
12
16
20
24
32

Use these values consistently.

Spacing must communicate hierarchy.

Examples:

4–8 px:
micro relationships

12–16 px:
normal component spacing

20–24 px:
section separation

32 px:
major page-level separation

Avoid random values unless necessary.

============================================================
G. COLOR SYSTEM
============================================================

Use the existing Edge Retails color system.

BRAND

Primary:
#635BFF

Primary Hover:
#5851E5

Primary Tint:
#F0EDFF

Dark Mode Brand:
#7C74FF

LIGHT MODE

Application Background:
#F6F9FC

Surface:
#FFFFFF

Subtle Surface:
#F8FAFC

Border:
#E6EBF1

Primary Text:
#0A2540

Secondary Text:
#425466

Muted Text:
#6B7C93

DARK MODE

Background:
#0B1220

Surface:
#111827

Raised Surface:
#172033

Border:
#1F2937

Primary Text:
#F3F4F6

Secondary Text:
#D1D5DB

Muted Text:
#9CA3AF

Brand Accent:
#7C74FF

SEMANTIC

Success:
#0D9488

Warning:
#C27803

Danger:
#DF1B41

Info:
#3B82F6

Approximate color distribution:

80% neutrals
15% brand
5% semantic

Do NOT turn every KPI into a colored card.

Do NOT automatically make financial profit numbers green.

Use semantic colors intentionally for:

status
warning
success
error
badges
small indicators
state feedback

============================================================
H. TYPOGRAPHY
============================================================

Primary UI font:

Plus Jakarta Sans

Display / financial emphasis:

Space Grotesk

Fallback:

Segoe UI
sans-serif

Use Plus Jakarta Sans for:

navigation
tables
forms
buttons
labels
body text
dialogs
POS controls

Use Space Grotesk sparingly for:

major KPI numbers
large financial totals
high-value numeric information

Typography hierarchy:

Major KPI:
28–32 px

Page Title:
22–24 px

Section Heading:
16–18 px

Body:
14 px

Table:
14 px

Secondary:
13 px

Caption:
12 px

Do not randomly introduce new font sizes.

Financial numbers must be highly readable.

Use tabular alignment where visually appropriate.

============================================================
I. DESIGN TOKENS AND FIGMA VARIABLES
============================================================

Convert repeated design decisions into reusable Figma Variables or clearly defined styles.

Use naming similar to:

Color/Brand/Primary
Color/Brand/PrimaryHover
Color/Brand/PrimaryTint

Color/Surface/App
Color/Surface/Default
Color/Surface/Subtle

Color/Text/Primary
Color/Text/Secondary
Color/Text/Muted

Color/Border/Default

Color/Semantic/Success
Color/Semantic/Warning
Color/Semantic/Danger
Color/Semantic/Info

Spacing/04
Spacing/08
Spacing/12
Spacing/16
Spacing/20
Spacing/24
Spacing/32

Radius/06
Radius/08
Radius/12

Control/Height/36
Control/Height/40

Table/Header/40
Table/Row/44

Structure the design so Figma MCP can clearly understand these values.

============================================================
J. COMPONENT ARCHITECTURE
============================================================

Convert repeated visual elements into reusable components.

At minimum create/refine:

Sidebar Item
Sidebar Group
Page Header
Primary Button
Secondary Button
Danger Button
Ghost Button
Icon Button

Text Input
Number Input
Search Input
ComboBox / Dropdown
Date Picker
Checkbox
Radio
Toggle

Tabs
Status Badge
KPI Card
Information Card

Table
Table Header
Table Row
Pagination

Quantity Stepper

Modal
Drawer
Toast
Tooltip where useful

Loading Skeleton
Empty State
Inline Validation
Error Message
Confirmation Dialog
Permission Dialog

Do not duplicate visually identical components manually.

============================================================
K. COMPONENT STATES
============================================================

Every relevant interactive component must include clear variants for:

Default
Hover
Pressed
Focused
Disabled
Selected
Error
Loading

Where applicable also include:

Success
Warning
Read-only

Do not merely document these states.

Create actual visual variants/components so Figma MCP can read them.

Focus states are especially important because this is desktop software.

============================================================
L. LAYER AND COMPONENT NAMING
============================================================

Clean up meaningless names.

Avoid:

Frame 428
Rectangle 62
Group 31
Component 92

Use meaningful names such as:

Screen/03/Dashboard

Screen/04/POS

Component/Button/Primary
Component/Button/Secondary

Component/Input/Search

Component/Card/KPI

Component/Table/Header
Component/Table/Row

Component/Badge/Success

Dialog/CompleteSale

Dialog/SaleReturn

Drawer/PurchaseDetail

Icon/Search
Icon/Inventory
Icon/Settings

Asset/Logo/Primary

The design must be human-readable and machine-readable.

============================================================
M. AUTO LAYOUT AND STRUCTURAL CLEANUP
============================================================

Use Auto Layout intelligently.

Do not flatten everything into arbitrary groups.

Parent-child relationships should reflect the actual implementation structure.

Example:

POS Screen
  App Shell
    Sidebar
    Main
      Top Bar
      Content
        Product Panel
        Cart Panel

Dashboard
  KPI Section
  Thaka Section
  Activity Section

Use Hug, Fill and Fixed sizing intentionally.

Avoid unnecessary fixed positioning.

Do not break logical sections into dozens of disconnected layers.

============================================================
N. VISUAL DEPTH AND ELEVATION
============================================================

Use borders as the primary separation mechanism.

Primary border:

#E6EBF1

Use shadows minimally.

Cards should generally feel like:

white surface
clean border
10–12px radius
very subtle elevation if needed

Dialogs and drawers may use slightly stronger elevation than normal cards.

Do not create floating-card soup.

============================================================
O. SIDEBAR POLISH
============================================================

Sidebar should feel premium but quiet.

Light mode sidebar:

White surface

Active item:

Background:
#F0EDFF

Text/Icon:
#635BFF

Hover:

very subtle neutral background

Use consistent icon size and stroke weight.

Navigation hierarchy:

Dashboard

Sales
    New Sale
    Sales History

Thaka / Projects

Purchases

Inventory

Expenses

Customers

Suppliers

Reports

Settings

Sales may expand/collapse.

Do not make every section expandable.

Do not use oversized sidebar icons.

Do not use gradients in navigation.

============================================================
P. TOP BAR POLISH
============================================================

Top bar must remain compact.

Use:

Page title

Optional contextual action

Optional contextual search only where appropriate

Current user on right

Small system indicator only if useful

Do not fill top bar with unnecessary controls.

============================================================
Q. TABLE POLISH
============================================================

Tables are one of the most important visual systems in this application.

Treat them as first-class components.

Default appearance:

Header:
#F8FAFC

Rows:
#FFFFFF

Hover:
#F8FAFC

Selected:
#F0EDFF

Border:
#E6EBF1

Header text:
#425466

Primary row text:
#0A2540

Secondary:
#6B7C93

Table requirements:

clear numeric alignment
financial values easy to scan
adequate row density
subtle row hover
clear selected state
clean status badges
sort state
filter state
loading skeleton
empty state
pagination
contextual row interaction

Avoid zebra striping by default.

Avoid excessive visible grid lines.

Avoid putting 4 action icons on every row.

Prefer row click or contextual action.

============================================================
R. BUTTON POLISH
============================================================

Primary actions should visually dominate only when needed.

Use:

Primary button:
brand indigo

Secondary button:
neutral bordered or subtle surface

Danger:
danger color only for destructive action

Ghost/icon:
for tertiary controls

Do not use red Cancel buttons.

Do not use primary indigo for every single button.

Buttons must have consistent:

height
radius
padding
font weight
icon spacing
focus state
disabled state
loading state

============================================================
S. INPUT AND FORM POLISH
============================================================

Forms should feel compact and highly usable.

Use clear labels.

Use helper text only when needed.

Inline validation should appear directly near the field.

Do not use placeholder text as a replacement for labels in important fields.

Keep financial and numeric inputs aligned logically.

Input styles must support:

Default
Hover
Focus
Error
Disabled
Read-only

============================================================
T. FINANCIAL DISPLAY
============================================================

Use currency consistently:

Rs. 184,500

not random combinations such as:

PKR 184500
Rs184,500
184,500 Rs

Main totals should use strong primary text.

Only positive/negative semantic changes should use colors.

Do not color every profit value green.

============================================================
U. DASHBOARD POLISH
============================================================

Dashboard must communicate the current shop condition within approximately 5 seconds.

Keep current structure.

Primary KPI row:

Today Sales
Today Profit
Expenses
Low Stock

Second row:

Active Thakas
Current Thaka Value
Today Thaka Material

Third area:

Recent Activity
Low Stock Products

Do not add charts.

Do not add annual graphs.

Do not add customer statistics.

Do not add supplier statistics.

Do not add decorative analytics.

Improve:

spacing
hierarchy
number readability
card proportion
status treatment
activity row clarity

KPI cards should feel premium but calm.

============================================================
V. POS / NEW SALE POLISH
============================================================

This is the highest-frequency screen.

It must feel extremely fast and operational.

Maintain two-column structure:

LEFT:

Search / barcode scan
Category
Brand
Product table

RIGHT:

Current Sale
Cart lines
Customer
Subtotal
Invoice Discount
Total
Complete Sale

Important:

No HOLD feature.

No Thaka action inside POS.

Thaka is a separate workflow.

Out-of-stock products should be visually unavailable.

Low-stock products can remain selectable with a subtle warning.

Cart total and Complete Sale action must remain visible.

Quantity controls must be obvious but compact.

Product search must visually feel like the primary interaction.

Do not overdecorate the POS screen.

============================================================
W. SALES HISTORY POLISH
============================================================

Keep:

Search
Today
Yesterday
This Week
This Month
Date filter

Table:

Invoice
Time
Customer
Items
Total
Profit
Status

Use strong row readability.

Avoid action clutter.

Row click opens Sale Detail.

Bottom summary should remain easy to scan.

============================================================
X. SALE DETAIL POLISH
============================================================

This is transaction evidence.

It should feel calm, structured and read-only.

Show:

Invoice
Status
Date
Time
Cashier
Payment
Customer

Items:

Product
Qty
Price
Total

Summary:

Subtotal
Invoice Discount
Total

Actions:

Return Items
Print Receipt

Do not show line-level discount in V1.

============================================================
Y. THAKA / PROJECT DESIGN
============================================================

Thaka must remain visually distinct from normal counter sales while still using the same design system.

Do NOT invent a separate visual brand for Thaka.

Projects screen:

Search
Active / Settled tabs

Columns:

Project / Customer
Started
Material Value
Paid
Balance

Workspace:

Material Value
Paid
Balance

Primary actions:

Add Material
Record Payment
Final Settlement

Material History
Payment History

Settled projects should visually communicate read-only state.

Do not merge Thaka into normal sales metrics.

============================================================
Z. PURCHASES
============================================================

New Purchase must feel like an inventory intake workflow.

Show:

Supplier
Invoice number
Date
Note
Product search

Table:

Product
Qty
Cost
Sale Price
Total

Summary:

Subtotal
Other Charges
Total

Save Purchase

Keep the layout compact.

Purchase History must use the same table language as Sales History.

Purchase Detail remains a right-side drawer.

============================================================
AA. INVENTORY
============================================================

Inventory is a stock-control workspace.

Top KPI:

Total Products
Low Stock
Out of Stock

Do NOT show Inventory Value yet.

Costing methodology has not been finalized.

Tabs:

All Stock
Low Stock
Out of Stock
Stock Movements

Filters:

Category
Brand
Stock Status

Table:

Product
Brand
Stock
Minimum Stock
Cost
Sale Price

Current stock is not directly editable.

Stock Adjustment is a controlled dialog.

Stock Movement must clearly distinguish:

Purchase +
Sale -
Thaka -
Sale Return +
Purchase Return -
Damage -
Adjustment ±

Use small semantic indicators rather than large colored rows.

============================================================
AB. PRODUCT DETAIL
============================================================

Product detail should feel like a clean operational profile.

Show:

Brand
SKU
Category
Model
Unit

KPI:

Current Stock
Purchase Cost
Sale Price

Also:

Minimum Stock

Tabs:

Overview
Stock Movement
Purchases
Sales

Optional electronics information:

Warranty
Serial Tracking
IMEI Tracking
Color
Variant

Do not force IMEI or serial fields on every product.

============================================================
AC. EXPENSES
============================================================

Keep the screen simple and financial.

Top:

Today
This Month
Top Category

Filters:

Today
This Week
This Month
Category

Table:

Date
Category
Subcategory
Note
Amount

Categories may include:

Shop Rent
Staff Salary
Staff Expense
Electricity
Gas / Water
Internet
Phone / Communication
Transport
Maintenance
Stationery
Marketing
Bank / Transaction Charges
Other

Inventory Purchases are NOT operating Expenses.

============================================================
AD. CUSTOMERS
============================================================

Keep customer management operational, not CRM-like.

Search:

Name
Phone
Project

Columns:

Customer
Phone
Local Sales
Active Thaka
Last Sale

Use Customer Detail drawer.

Do not add:

campaigns
funnels
marketing scores
lead status
CRM analytics

============================================================
AE. SUPPLIERS
============================================================

Search:

Supplier
Phone
City

Table:

Supplier
Phone
Purchases
Last Purchase
City

Use Supplier Detail drawer.

Do not invent supplier payable accounting unless already specified elsewhere.

============================================================
AF. REPORTS
============================================================

One Reports screen.

Tabs:

Daily
Monthly
Yearly

DAILY:

Total Sales
Gross Profit
Expenses
Net Profit
Purchases
Thaka Material

MONTHLY:

Total Sales
Gross Profit
Net Profit
Expenses
Purchases
Thaka Material

Expense breakdown should remain compact.

YEARLY:

Only:

Total Sales
Total Profit

Do not introduce unnecessary charts.

Thaka must remain separate until accounting recognition rules are finalized.

============================================================
AG. SETTINGS
============================================================

Settings uses internal left navigation.

Sections:

Shop
Receipt
Users & Access
Categories & Units
Backup
License
Appearance
Database

Improve Settings to feel like a professional desktop settings console.

Do not make every section a separate full application screen.

SHOP:

Shop Name
Owner Name
Phone
Address
Logo

RECEIPT:

Printer
Paper Size
Header
Footer
Show Customer
Show Cashier
Auto Print

USERS & ACCESS:

User table
Role
Status
Action

Roles:

Owner
Manager
Cashier

Include a clean permissions UI.

CATEGORIES & UNITS:

Categories table
Units table

Allow:

Add
Edit
Deactivate

BACKUP:

DB health
Last Backup
Cloud Backup
Automatic Backup
Frequency
Backup Now
Recent Backup History

Restore should be secondary, not dominant.

LICENSE:

License ID
Store
Status
Module
Expiry
Terminals
Import New License

APPEARANCE:

Light
Dark
Optional System

DATABASE:

PostgreSQL Running
Connected
Database Size
Worker Service
Last Backup

Do not expose raw database credentials.

============================================================
AH. MODALS AND DRAWERS
============================================================

All dialogs should follow one visual language.

Standard structure:

Header
Body
Footer

Use consistent padding.

Use clear primary and secondary actions.

Do not stack modal on modal.

Do not use destructive red styling unless the action is actually destructive.

============================================================
AI. COMPLETE SALE DIALOG
============================================================

Show:

Total
Payment Method
Amount Received
Change
Customer
Print Receipt

Payment Methods:

Cash
Bank
Other

Bank / Other may expose optional reference.

Prevent duplicate submission.

Loading state:

Completing Sale...

Success:

close dialog
show toast

============================================================
AJ. SALE RETURN DIALOG
============================================================

This dialog requires special care.

Show original sale items.

Columns:

Product
Sold Qty
Return Qty
Refund

Fields:

Return Reason
Item Condition
Refund Method
Total Refund

Item Condition options:

Restock / Sellable
Damaged
Defective
Scrap / Non-sellable

Important:

Do not visually imply that every return automatically goes back into available sellable stock.

============================================================
AK. PURCHASE RETURN DIALOG
============================================================

Show:

Supplier
Purchase
Product
Purchased Qty
Consumed Qty
Already Returned
Eligible Return Qty
Requested Return Qty
Return Value
Reason

Visually emphasize:

Requested Return Qty must not exceed Eligible Return Qty.

============================================================
AL. STOCK ADJUSTMENT
============================================================

Show:

Product
Current Stock
Adjustment Type
Quantity
New Stock Preview
Reason
Note

Reasons:

Damaged
Lost
Physical Count Correction
Other

Current stock must never appear directly editable.

============================================================
AM. USER PERMISSIONS
============================================================

Create a clean role/permission matrix.

Typical permissions:

Create Sale
View Sales History
View Profit
Create Thaka
Add Thaka Material
Record Thaka Payment
Create Purchase
Manage Inventory
Stock Adjustment
Manage Expenses
View Reports
Manage Users
Manage Settings
Backup / Restore

Owner has full access.

Manager is configurable.

Cashier is restricted by default.

Do not create a different application shell for each role.

============================================================
AN. EMPTY, LOADING AND ERROR STATES
============================================================

Do not leave screens without states.

Create reusable patterns.

LOADING:

Skeletons for:

KPI
table rows
activity
low stock

Avoid full-screen spinner unless absolutely necessary.

EMPTY:

Compact.

Example:

No sales recorded today.

[ New Sale ]

ERROR:

Inline or section-level.

Never show raw stack traces.

============================================================
AO. TOASTS
============================================================

Use bottom-right.

Compact.

Examples:

Sale completed successfully

Receipt failed to print.
Sale was saved successfully.
Retry

Important:

A receipt printing failure must NOT make the user think the sale itself failed.

============================================================
AP. KEYBOARD AND DESKTOP BEHAVIOR
============================================================

This is a desktop POS.

Design visible support for:

Focus states
Tab navigation
Enter confirmation where safe
Escape close/back
Arrow-key navigation where useful
Barcode scanner focus

Potential shortcuts exist:

F2 Product Search
F4 Customer
F6 Discount
F8 Payment
F10 Complete Sale
Esc Back / Close

Do not clutter every screen with shortcut labels yet.

============================================================
AQ. MICRO-INTERACTION POLISH
============================================================

Use restrained motion.

Suggested:

Hover:
120–180 ms

Focus transitions:
fast and subtle

Drawer:
short controlled slide

Modal:
subtle opacity/scale

Toast:
short entrance/exit

Selection:
subtle background change

Do not use:

bounce
large scale
elastic effects
floating elements
decorative motion loops

This is enterprise desktop software.

============================================================
AR. EDGE RETAILS BRAND PERSONALITY
============================================================

After functional polish, introduce a restrained Edge Retails visual signature.

Possible places:

brand mark
sidebar brand treatment
login screen
setup wizard
important financial totals
selected state treatment
empty states
receipt preview
small section accents

Do not place branding decoration everywhere.

The application should be identifiable as Edge Retails without becoming visually loud.

============================================================
AS. FIGMA MCP READINESS
============================================================

This is CRITICAL.

The final Figma structure will be read programmatically using Figma MCP.

Therefore:

Use meaningful page names.

Use meaningful frame names.

Use meaningful component names.

Use components instead of copies.

Use component variants.

Use Auto Layout.

Use consistent variables/styles.

Keep the layer hierarchy logical.

Remove junk layers.

Remove unused hidden experiments.

Avoid unnecessary nested frames.

Do not flatten screens.

Do not convert complete UI sections into SVG or images.

Keep text as real text.

Keep icons as vectors/components.

Keep cards and controls as editable Figma elements.

Keep images as actual image assets.

============================================================
AT. ASSET RULES
============================================================

Whole screens must remain native editable Figma UI.

DO NOT flatten:

Dashboard
POS
Sidebar
Tables
Dialogs
Forms
Cards

into images.

Individual assets may remain:

Logo → vector
Icons → vector
Illustrations → vector if suitable
Photos → raster image

Name assets clearly.

============================================================
AU. IMPLEMENTATION ANNOTATIONS
============================================================

Where visual appearance alone cannot explain behavior, add concise Figma annotations.

Examples:

"Current Stock is read-only."

"Sale completion updates inventory."

"Printer failure must not rollback the completed sale."

"Thaka is separate from Local Sales."

"Drawer preserves current screen context."

"Table should use virtualization in production."

"Out-of-stock item cannot be added."

Keep annotations concise and implementation-relevant.

============================================================
AV. POLISH PROCESS
============================================================

Do NOT randomly polish screens independently.

Use this sequence:

PASS 1
Audit foundations

PASS 2
Normalize tokens

PASS 3
Normalize spacing and geometry

PASS 4
Improve typography hierarchy

PASS 5
Refine reusable components

PASS 6
Create interaction states

PASS 7
Refine sidebar and shell

PASS 8
Polish tables

PASS 9
Polish forms

PASS 10
Polish financial presentation

PASS 11
Polish Dashboard

PASS 12
Polish Sales / POS

PASS 13
Polish Thaka

PASS 14
Polish Purchasing

PASS 15
Polish Inventory

PASS 16
Polish Expenses / Contacts

PASS 17
Polish Reports / Settings

PASS 18
Polish system screens

PASS 19
Polish all dialogs and drawers

PASS 20
Cross-screen consistency audit

PASS 21
Figma MCP readiness cleanup

============================================================
AW. DO NOT INVENT BUSINESS LOGIC
============================================================

Two business decisions are intentionally unresolved.

1. Inventory costing method

Do not assume:

FIFO
Weighted Average
Last Cost

Therefore do not add Inventory Value analytics.

2. Thaka revenue recognition

Do not decide whether revenue is recognized at material issue or final settlement.

Therefore keep:

Local Sales
Thaka Material

visually separate.

Do not silently combine them.

============================================================
AX. FINAL QUALITY STANDARD
============================================================

The final design should feel approximately like:

a premium modern Windows enterprise application
with Stripe-level visual discipline
and the compact operational efficiency of professional POS software

but it must maintain its own Edge Retails identity.

The user should immediately feel:

"This software is serious, fast and professionally built."

The developer/AI implementation agent should immediately understand:

"These components, tokens, states and layouts can be translated cleanly into WPF/XAML."

============================================================
AY. FINAL ACCEPTANCE CHECK
============================================================

Before considering the redesign/polish complete, verify:

Exactly 17 primary screens remain.

No accidental extra pages were introduced.

Sidebar is consistent.

Top bar is consistent.

Spacing is consistent.

Component dimensions are consistent.

Typography is consistent.

Buttons are consistent.

Inputs are consistent.

Tables are consistent.

Cards are consistent.

Dialogs are consistent.

Drawers are consistent.

Light mode is complete.

Dark mode tokens are defined.

Focus states exist.

Hover states exist.

Disabled states exist.

Loading states exist.

Error states exist.

Empty states exist.

Permission states exist.

Sale Return has item disposition.

Purchase Return shows eligible quantity.

No direct stock editing exists.

No HOLD exists in POS.

No Thaka action exists in POS.

Thaka remains separate from local sales.

Invoice-level discount only is used in V1.

No inventory valuation is invented.

No FBR UI exists.

No mobile screens exist.

No unnecessary charts exist.

No whole screen has been flattened into an image.

Components are reusable.

Frames are clearly named.

Layers are clearly named.

Auto Layout is used logically.

Figma Variables/styles are used wherever practical.

The design is clean enough for Figma MCP to expose accurately.

The design is realistic for WPF/XAML implementation.

============================================================
FINAL INSTRUCTION
============================================================

Improve the CURRENT design.

Do not replace the application's architecture.

Do not redesign simply for visual novelty.

Treat the existing screens as the correct product structure and progressively refine them into a polished, premium, implementation-ready Windows desktop application.

Preserve functional clarity first.

Improve hierarchy second.

Improve visual quality third.

Add brand character fourth.

Always prioritize real desktop usability and clean future WPF implementation through Antigravity using Figma MCP.