# Edge Retails - Frontend Master UI/UX Specification
## Version 1.1 - Corrected, Forensic-Reviewed, Generation-Ready

**Product:** Edge Retails  
**Module Focus:** Electronics Retail only  
**Platform:** Windows Desktop POS  
**Frontend Implementation Target:** WPF + C# + XAML + .NET 10  
**Design Target:** Figma Make or Google Stitch  
**Primary Design Frame:** 1440 x 900  
**Minimum Supported Visual Target:** 1366 x 768  
**Status:** Final corrected frontend architecture and visual specification  
**Scope:** V1 Electronics POS only  

---

# 1. Product Definition

Edge Retails is a local-first Windows desktop Point of Sale application for electronics and electrical retail shops.

The V1 frontend must support:

- Local counter sales
- Sales history
- Invoice details
- Sale returns
- Thaka / Project accounts
- Thaka material issue
- Thaka payments
- Final Thaka settlement
- Wholesaler purchases
- Purchase history
- Purchase returns
- Inventory management
- Stock movement audit
- Product management
- Shop expenses
- Customers
- Suppliers / wholesalers
- Daily reports
- Monthly reports
- Minimal yearly reports
- User login / switching
- Owner / manager / cashier access
- Local signed licensing
- PostgreSQL diagnostics
- Cloud backup status

Do not include:

- FBR integration
- Tax authority integration
- Mobile layouts
- Web-first navigation
- CRM campaigns
- Payroll
- Staff attendance
- Complex accounting ledgers
- Multi-branch UI in V1
- Online marketplace features

---

# 2. Canonical Full Screen Count

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

These are not full screens:

```text
Complete Sale
Sale Return
New Thaka
Add Thaka Material
Record Payment
Final Settlement
Add/Edit Product
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
Toast
Validation States
```

These must be dialogs, drawers, overlays, or toasts.

---

# 3. UX Philosophy

Edge Retails is an operational desktop tool, not a marketing website.

The UI must feel:

- Professional
- Premium
- Minimal
- Fast
- Compact
- Information-dense
- Easy to scan
- Keyboard-friendly
- Table-friendly
- Modern without decorative excess

Avoid:

- Giant empty whitespace
- Large marketing cards
- Glassmorphism
- Heavy gradients
- Excessive animations
- Giant pills
- Heavy shadows
- Overuse of indigo
- Too many KPI cards
- Unnecessary charts
- Mobile-first layouts
- Web dashboard gimmicks

---

# 4. Visual Direction

Use a Stripe-inspired minimalist enterprise style.

Visual personality:

```text
Clean
Precise
Cool-neutral
Indigo-accented
Desktop-operational
Financially readable
Modern but restrained
```

Recommended color usage:

```text
80% Neutral
15% Brand Indigo
5% Semantic
```

---

# 5. Final Color System

## 5.1 Brand

| Token | Value | Use |
|---|---|---|
| Primary | `#635BFF` | Primary actions, active nav, selected states |
| Primary Hover | `#5851E5` | Hover |
| Primary Tint | `#F0EDFF` | Active nav background, selected rows |
| Dark Brand | `#7C74FF` | Dark mode accent |

## 5.2 Light Theme

| Token | Value |
|---|---|
| App Background | `#F6F9FC` |
| Surface | `#FFFFFF` |
| Surface Subtle | `#F8FAFC` |
| Border | `#E6EBF1` |
| Text Primary | `#0A2540` |
| Text Secondary | `#425466` |
| Text Muted | `#6B7C93` |

## 5.3 Dark Theme

| Token | Value |
|---|---|
| App Background | `#0B1220` |
| Surface | `#111827` |
| Surface Raised | `#172033` |
| Border | `#1F2937` |
| Text Primary | `#F3F4F6` |
| Text Secondary | `#D1D5DB` |
| Text Muted | `#9CA3AF` |
| Brand | `#7C74FF` |

Light mode is default.

## 5.4 Semantic

| Meaning | Value |
|---|---|
| Success | `#0D9488` |
| Warning | `#C27803` |
| Danger | `#DF1B41` |
| Info | `#3B82F6` |

Use semantic colors for:

- Status badges
- Small alerts
- Icons
- Inline messages
- Error/success feedback

Do not fill large cards with semantic colors.

---

# 6. Typography

## Primary

**Plus Jakarta Sans**

Use for:

- Body
- Forms
- Buttons
- Sidebar
- Tables
- POS
- Dialogs
- Navigation

Weights:

```text
400 Regular
500 Medium
600 SemiBold
700 Bold
800 Rare
```

## Display

**Space Grotesk**

Use only for:

- KPI figures
- Large report totals
- Major financial numbers

## Fallback

```text
Segoe UI
sans-serif
```

Final WPF application must bundle fonts locally or safely fall back to system fonts.

## Type Scale

| Role | Size |
|---|---|
| KPI / Major Number | 28-32px |
| Page Title | 22-24px |
| Section Heading | 16-18px |
| Body | 14px |
| Table | 14px |
| Secondary | 13px |
| Caption | 12px |

---

# 7. Exact Geometry Tokens

These dimensions must remain consistent across generated screens.

```text
Sidebar Expanded Width      232px
Sidebar Collapsed Width      72px
Top Bar Height               64px
Page Horizontal Padding      24px
Page Vertical Padding        20px
Major Section Gap            24px
Standard Component Gap       16px
Compact Gap                   8px

Standard Input Height        40px
Compact Input Height         36px
Primary Button Height        40px
Compact Button Height        36px

Table Header Height          40px
Table Row Height             44px

Card Padding                 16px
Large Card Padding           20px

Modal Header Height          56px
Modal Footer Height          64px

Right Drawer Width          480px

Small Modal Width        440-480px
Medium Modal Width       560-640px
Large Modal Width        760-900px

Radius Small                  6px
Radius Medium                 8px
Radius Large              10-12px
Modal Radius                 12px
```

Do not randomly change density between screens.

---

# 8. Global Application Shell

```text
┌──────────────────────┬───────────────────────────────────────────────────────────────┐
│                      │ Top Bar                                                       │
│                      ├───────────────────────────────────────────────────────────────┤
│      Sidebar         │                                                               │
│                      │                    Main Content                               │
│                      │                                                               │
│                      │                                                               │
└──────────────────────┴───────────────────────────────────────────────────────────────┘
```

## Sidebar

```text
Edge Retails

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
```

Rules:

- Dashboard remains standalone
- Sales can expand/collapse
- Sidebar should remain visually quiet
- Active item uses `#F0EDFF` background and `#635BFF` text/icon
- Hover uses subtle neutral fill
- Use simple line icons
- No hamburger-first navigation on desktop

## Top Bar

Use:

- Current page title
- Optional page-specific action
- Optional contextual search
- Right side user area
- Optional small system-status indicator

Do not overload it.

---

# 9. Global Component System

Required reusable components:

```text
Sidebar Item
Sidebar Group
Page Header
Primary Button
Secondary Button
Danger Button
Icon Button
Text Input
Search Input
Number Input
Dropdown
Date Picker
Checkbox
Radio
Tabs
Status Badge
KPI Card
Data Table
Table Row
Pagination
Quantity Stepper
Modal
Drawer
Toast
Empty State
Loading Skeleton
Inline Error
Confirmation Dialog
Permission Dialog
```

Every interactive component should support relevant states:

```text
Default
Hover
Pressed
Focused
Disabled
Selected
Error
Loading
```

---

# 10. Table System

Default table styling:

```text
Header Background   #F8FAFC
Header Text         #425466
Row Background      #FFFFFF
Hover               #F8FAFC
Selected            #F0EDFF
Border              #E6EBF1
Primary Text        #0A2540
Secondary Text      #6B7C93
```

Rules:

- No default zebra striping
- Numeric columns align consistently
- Row click may open detail screen/drawer
- Avoid 3-5 action buttons per row
- Support search/filter/sort where specified
- Show empty/loading states
- Use virtualization/pagination where needed

---

# 11. Financial Display Rules

Use:

```text
Rs. 184,500
Rs. 31,420
```

Rules:

- Use `Rs.` consistently
- Main financial numbers use dark primary text
- Profit is not automatically green
- Green is semantic, not decorative
- Thousands separators are mandatory
- Decimal values should be avoided unless product/unit logic requires them

---

# 12. Business Rules Pending

Two business rules are intentionally unresolved and must not be invented by Figma Make or Stitch.

## 12.1 Product Costing Method

Pending:

```text
Moving Weighted Average
FIFO
Last Purchase Cost
```

Until resolved:

- Do not show Inventory Value
- Do not add costing-specific labels
- Do not invent valuation charts

## 12.2 Thaka Revenue Recognition

Pending:

```text
Recognize when material is issued
OR
Recognize when project is settled
```

Until resolved:

- Keep Local Sales separate
- Keep Thaka Material separate
- Do not merge Thaka into Today Sales
- Do not merge Thaka into Local Sale Profit
- Dashboard and Reports show Thaka separately

---

# 13. Discount Rule - Final V1 Decision

V1 uses **invoice-level discount only**.

Do not implement per-item discount in V1.

POS:

```text
Subtotal
Invoice Discount
Total
```

Sale Detail:

```text
Product
Qty
Price
Total
```

Do not show line-level discount column.

---

# 14. Screen 01 - First Setup / License

Purpose: first-run setup only.

## Wireframe

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ Edge Retails                                                                │
│                                                                              │
│         1 License          2 Shop Setup          3 Ready                    │
│                                                                              │
│                     Import License File                                      │
│                                                                              │
│                 [       Browse License       ]                               │
│                                                                              │
│                 Status: Not Activated                                        │
│                                                                              │
│                                              [ Continue ]                    │
└──────────────────────────────────────────────────────────────────────────────┘
```

Step 2 fields:

```text
Shop Name
Owner Name
Phone
Address
Owner PIN
```

Step 3:

```text
License        Active
Database       Connected
Module         Electronics
Shop           <Shop Name>

[ Start Edge Retails ]
```

Do not expose PostgreSQL technical credentials.

---

# 15. Screen 02 - Login / User Switch

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│                                                                              │
│                              EDGE RETAILS                                    │
│                                                                              │
│                         Select your account                                  │
│                                                                              │
│                      [ Abdullah - Owner ]                                    │
│                      [ Ali - Cashier   ]                                     │
│                                                                              │
│                              PIN                                             │
│                          [ • • • • ]                                         │
│                                                                              │
│                         [ SIGN IN ]                                          │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
```

Use fast PIN-based access.

---

# 16. Screen 03 - Dashboard

Purpose: understand current shop condition within 5 seconds.

## Wireframe

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Dashboard                                                                  17 Sep 2026    │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                            │
│  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐  ┌────────────────┐          │
│  │ TODAY SALES    │  │ TODAY PROFIT   │  │ EXPENSES       │  │ LOW STOCK      │          │
│  │ Rs. 84,500     │  │ Rs. 13,400     │  │ Rs. 3,200      │  │ 12 Items       │          │
│  └────────────────┘  └────────────────┘  └────────────────┘  └────────────────┘          │
│                                                                                            │
│  ┌──────────────────────┐  ┌──────────────────────┐  ┌──────────────────────┐             │
│  │ ACTIVE THAKAS        │  │ THAKA VALUE          │  │ TODAY THAKA MATERIAL │             │
│  │ 14                   │  │ Rs. 482,000          │  │ Rs. 28,400           │             │
│  └──────────────────────┘  └──────────────────────┘  └──────────────────────┘             │
│                                                                                            │
│  ┌──────────────────────────────────────────┐  ┌────────────────────────────────────────┐ │
│  │ RECENT ACTIVITY                          │  │ LOW STOCK                              │ │
│  │                                          │  │                                        │ │
│  │ Sale #1288                 Rs. 4,200     │  │ LED Bulb 12W              8 left      │ │
│  │ Thaka #45                  Rs. 8,700     │  │ Wire 2.5mm                2 rolls     │ │
│  │ Expense                    Rs.   850     │  │ Breaker 32A               4 left      │ │
│  │ Purchase #252              Rs.72,000     │  │ Switch 16A                7 left      │ │
│  └──────────────────────────────────────────┘  └────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

No charts.

No category tabs.

No Add Product button.

---

# 17. Screen 04 - POS / New Sale

## Wireframe

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ New Sale                                                    Invoice #1292                 │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                            │
│  ┌─────────────────────────────────────────────────┐  ┌─────────────────────────────────┐  │
│  │ Search / Scan product                           │  │ CURRENT SALE                    │  │
│  └─────────────────────────────────────────────────┘  ├─────────────────────────────────┤  │
│                                                      │ LED Bulb 12W                    │  │
│  Category [All]    Brand [All]                       │ Rs. 500 x 2      Rs. 1,000      │  │
│                                                      │ [ - ] 2 [ + ]                  │  │
│  ┌─────────────────────────────────────────────────┐ │                                 │  │
│  │ PRODUCT              STOCK         PRICE        │ │ Switch 16A                      │  │
│  ├─────────────────────────────────────────────────┤ │ Rs. 300 x 4      Rs. 1,200      │  │
│  │ LED Bulb 12W          82           Rs. 500      │ │ [ - ] 4 [ + ]                  │  │
│  │ Switch 16A           124           Rs. 300      │ │                                 │  │
│  │ Breaker 32A            4           Rs.1,450     │ │ Wire 2.5mm                      │  │
│  │ Wire 2.5mm            12           Rs.6,500     │ │ Rs.6,500 x 1    Rs. 6,500      │  │
│  └─────────────────────────────────────────────────┘ ├─────────────────────────────────┤  │
│                                                      │ Customer      Walk-in            │  │
│                                                      │ Subtotal      Rs. 8,700          │  │
│                                                      │ Discount      Rs.   200          │  │
│                                                      │ TOTAL         Rs. 8,500          │  │
│                                                      │                                 │  │
│                                                      │ [ COMPLETE SALE ]               │  │
│                                                      └─────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Rules:

- No HOLD in V1
- No Thaka action here
- Out-of-stock rows disabled
- Low-stock warning allowed
- Cart total area remains visible

---

# 18. Screen 05 - Sales History

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Sales History                                                        [ + New Sale ]       │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search invoice/customer                                                                      │
│                                                                                            │
│ [Today] [Yesterday] [This Week] [This Month]                    Date [17 Sep 2026]         │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ INVOICE   TIME      CUSTOMER      ITEMS       TOTAL         PROFIT        STATUS        │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ #1292     04:20 PM  Walk-in        3         Rs.8,500      Rs.1,620       Paid          │ │
│ │ #1291     03:45 PM  Ahmed          5         Rs.4,500      Rs.  840       Paid          │ │
│ │ #1290     02:10 PM  Walk-in        2         Rs.2,100      Rs.  390       Paid          │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                            │
│ 58 Sales Today                     Total Sales Rs.184,500      Profit Rs.31,420            │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 19. Screen 06 - Sale Detail

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ < Sales History       Invoice #1292                                      PAID             │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                            │
│  Sale Information                             Customer                                    │
│  Date       17 Sep 2026                       Walk-in Customer                             │
│  Time       04:20 PM                                                                       │
│  Cashier    Ali                                                                            │
│  Payment    Cash                                                                           │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ PRODUCT                     QTY           PRICE                     TOTAL               │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ LED Bulb 12W                2            Rs. 500                   Rs.1,000             │ │
│ │ Switch 16A                  4            Rs. 300                   Rs.1,200             │ │
│ │ Wire 2.5mm                  1            Rs.6,500                  Rs.6,500             │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                            │
│                                                     Subtotal        Rs.8,700               │
│                                                     Discount        Rs.  200               │
│                                                     TOTAL           Rs.8,500               │
│                                                                                            │
│ [ Return Items ]                                                  [ Print Receipt ]        │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 20. Screen 07 - Thaka / Projects

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Thaka / Projects                                                   [ + New Thaka ]        │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search customer/project/phone                                                               │
│                                                                                            │
│ [ Active 14 ]   [ Settled 38 ]                                      Start Date [All]      │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ PROJECT / CUSTOMER       STARTED      MATERIAL       PAID          BALANCE             │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ Ahmed House              05 Sep       Rs.185,000     Rs.      0    Rs.185,000          │ │
│ │ Ali Plaza                18 Aug       Rs.420,000     Rs. 50,000    Rs.370,000          │ │
│ │ Usman House              11 Sep       Rs. 82,400     Rs.      0    Rs. 82,400          │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                            │
│ Active 14          Material Rs.1,482,000          Balance Rs.1,112,000                    │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 21. Screen 08 - Thaka Workspace

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ < Thaka / Projects      Ahmed House                                      ACTIVE           │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Customer Ahmed       Phone 0300-xxxxxxx      Started 05 Sep 2026                           │
│ Address Model Town, Lahore                                                                  │
│                                                                                            │
│ ┌──────────────────────┐ ┌──────────────────────┐ ┌──────────────────────┐                │
│ │ MATERIAL VALUE       │ │ PAID                 │ │ BALANCE              │                │
│ │ Rs.185,000           │ │ Rs.50,000            │ │ Rs.135,000           │                │
│ └──────────────────────┘ └──────────────────────┘ └──────────────────────┘                │
│                                                                                            │
│ [ + ADD MATERIAL ]   [ RECORD PAYMENT ]                         [ FINAL SETTLEMENT ]      │
│                                                                                            │
│ MATERIAL HISTORY                                                                           │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ DATE      PRODUCT        QTY       RATE        TOTAL         ADDED BY                  │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ 12 Sep    Wire 2.5mm      4        9,000       36,000        Ali                       │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                            │
│ PAYMENT HISTORY                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ DATE      AMOUNT          METHOD          NOTE                                         │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ 10 Sep    Rs.20,000       Cash            Advance                                      │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 22. Screen 09 - New Purchase

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ New Purchase                                                                               │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Supplier [ABC Electrical]    Invoice [INV-4589]    Date [17 Sep 2026]    Note [optional]  │
│                                                                                            │
│ Search / Scan product                                                                       │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ PRODUCT             QTY          COST          SALE PRICE          TOTAL                │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ LED Bulb 12W        100          Rs.350        Rs.500              Rs.35,000            │ │
│ │ Switch 16A          200          Rs.120        Rs.300              Rs.24,000            │ │
│ │ Wire 2.5mm           20          Rs.5,000      Rs.6,500            Rs.100,000           │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                            │
│                                            Subtotal       Rs.159,000                       │
│                                            Other Charges  Rs.      0                       │
│                                            TOTAL          Rs.159,000                       │
│                                                                                            │
│                                                     [ SAVE PURCHASE ]                      │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 23. Screen 10 - Purchase History

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Purchase History                                                  [ + New Purchase ]      │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search invoice/supplier                                                                       │
│                                                                                            │
│ [Today] [This Week] [This Month]                         Supplier [All]                    │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ PURCHASE    DATE       SUPPLIER               ITEMS             TOTAL                  │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ P-0252      17 Sep     ABC Electrical         3                 Rs.159,000              │ │
│ │ P-0251      16 Sep     XYZ Traders            8                 Rs. 82,000              │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                            │
│ Purchases This Month 28                                  Total Rs.1,840,000               │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 24. Screen 11 - Inventory

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Inventory                                                        [ + Add Product ]        │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────┐ ┌──────────────────────┐ ┌──────────────────────┐                │
│ │ TOTAL PRODUCTS       │ │ LOW STOCK            │ │ OUT OF STOCK         │                │
│ │ 1,248                │ │ 42                   │ │ 8                    │                │
│ └──────────────────────┘ └──────────────────────┘ └──────────────────────┘                │
│                                                                                            │
│ Search product/model/SKU/barcode                                                           │
│                                                                                            │
│ [ All Stock ] [ Low Stock ] [ Out of Stock ] [ Stock Movements ]                         │
│                                                                                            │
│ Category [All]      Brand [All]      Stock [All]                                         │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ PRODUCT         BRAND            STOCK     MIN STOCK      COST        SALE PRICE       │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ LED Bulb 12W    Philips           82       20             Rs.350      Rs.500           │ │
│ │ Breaker 32A     Schneider          4       10             Rs.1,100    Rs.1,450         │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Stock Movements tab:

```text
TIME     PRODUCT          TYPE              QTY       BEFORE      AFTER
04:20    LED Bulb 12W     Sale              -2        84          82
03:10    Switch 16A       Thaka             -4       128         124
01:30    Wire 2.5mm       Purchase         +20         0          20
```

---

# 25. Screen 12 - Product Detail

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ < Inventory       LED Bulb 12W                                      [ Edit Product ]      │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Philips      SKU LED-12W-001      Category Lighting      Unit Piece                       │
│                                                                                            │
│ ┌──────────────────────┐ ┌──────────────────────┐ ┌──────────────────────┐                │
│ │ CURRENT STOCK        │ │ PURCHASE COST        │ │ SALE PRICE           │                │
│ │ 82 Pieces            │ │ Rs.350               │ │ Rs.500               │                │
│ └──────────────────────┘ └──────────────────────┘ └──────────────────────┘                │
│                                                                                            │
│ Minimum Stock 20                                                                            │
│                                                                                            │
│ [ Overview ] [ Stock Movement ] [ Purchases ] [ Sales ]                                  │
│                                                                                            │
│ Stock Movement Table                                                                        │
│ DATE      TYPE        REFERENCE       QTY       BEFORE       AFTER                         │
│ 17 Sep    Sale        #1292           -2        84           82                            │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 26. Screen 13 - Expenses

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Expenses                                                        [ + Add Expense ]        │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────┐ ┌──────────────────────┐ ┌──────────────────────┐                │
│ │ TODAY                │ │ THIS MONTH           │ │ TOP CATEGORY         │                │
│ │ Rs.4,200             │ │ Rs.148,000           │ │ Staff Salaries       │                │
│ └──────────────────────┘ └──────────────────────┘ └──────────────────────┘                │
│                                                                                            │
│ [Today] [This Week] [This Month]                         Category [All]                   │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ DATE      CATEGORY         SUBCATEGORY       NOTE                    AMOUNT             │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ 17 Sep    Staff Expense    Lunch             Staff Lunch             Rs.2,500           │ │
│ │ 17 Sep    Staff Expense    Tea               Evening Tea             Rs.  700           │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 27. Screen 14 - Customers

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Customers                                                       [ + Add Customer ]       │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search name/phone/project                                                                     │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ CUSTOMER        PHONE           LOCAL SALES        ACTIVE THAKA        LAST SALE       │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ Ahmed           0300...         Rs.82,000          Ahmed House         Today           │ │
│ │ Usman           0312...         Rs.45,500          Usman House         16 Sep          │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 28. Screen 15 - Suppliers

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Suppliers / Wholesalers                                          [ + Add Supplier ]      │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ Search supplier/phone/city                                                                     │
│                                                                                            │
│ ┌────────────────────────────────────────────────────────────────────────────────────────┐ │
│ │ SUPPLIER            PHONE          PURCHASES         LAST PURCHASE       CITY          │ │
│ ├────────────────────────────────────────────────────────────────────────────────────────┤ │
│ │ ABC Electrical      0300...        Rs.1,200,000      17 Sep              Lahore        │ │
│ │ XYZ Traders         0312...        Rs.  850,000      16 Sep              Multan        │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 29. Screen 16 - Reports

One screen only.

Tabs:

```text
Daily | Monthly | Yearly
```

## Daily Wireframe

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Reports                                                                                    │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ [ Daily ] [ Monthly ] [ Yearly ]                              Date [17 Sep 2026]          │
│                                                                                            │
│ ┌────────────────────┐ ┌────────────────────┐ ┌────────────────────┐                     │
│ │ TOTAL SALES        │ │ GROSS PROFIT       │ │ EXPENSES           │                     │
│ │ Rs.184,500         │ │ Rs.31,420          │ │ Rs.4,200           │                     │
│ └────────────────────┘ └────────────────────┘ └────────────────────┘                     │
│                                                                                            │
│ ┌────────────────────┐ ┌────────────────────┐ ┌────────────────────┐                     │
│ │ NET PROFIT         │ │ PURCHASES          │ │ THAKA MATERIAL     │                     │
│ │ Rs.27,220          │ │ Rs.159,000         │ │ Rs.28,400          │                     │
│ └────────────────────┘ └────────────────────┘ └────────────────────┘                     │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Monthly uses same component pattern plus expense breakdown.

Yearly shows only:

```text
TOTAL SALES
TOTAL PROFIT
```

For visual design, Yearly Total Profit should be labeled as **Net Profit** unless product wording is intentionally simplified later.

---

# 30. Screen 17 - Settings

## Wireframe

```text
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ Settings                                                                                   │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ ┌────────────────────────┐  ┌────────────────────────────────────────────────────────────┐ │
│ │ Shop                   │  │ SHOP SETTINGS                                              │ │
│ │ Receipt                │  │                                                            │ │
│ │ Users & Access         │  │ Shop Name      [ Edge Electronics ]                       │ │
│ │ Categories & Units     │  │ Owner Name     [ Abdullah ]                              │ │
│ │ Backup                 │  │ Phone          [ 03xx... ]                                │ │
│ │ License                │  │ Address        [ __________________________ ]             │ │
│ │ Appearance             │  │ Logo           [ Upload Logo ]                           │ │
│ │ Database               │  │                                                            │ │
│ │                        │  │                                      [ Save Changes ]     │ │
│ └────────────────────────┘  └────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

# 31. Settings - Categories & Units

This section is mandatory.

## Categories

```text
Categories

Lighting
Switches
Breakers
Cables
Accessories

[ + Add Category ]
```

Compact table:

```text
NAME            PRODUCTS        STATUS        ACTION
Lighting        124             Active        Edit
Cables           42             Active        Edit
```

## Units

```text
Piece
Box
Roll
Meter
Foot
Set
```

Compact table:

```text
UNIT            SYMBOL          STATUS        ACTION
Piece           pc              Active        Edit
Roll            roll            Active        Edit
Meter           m               Active        Edit
```

Actions:

```text
Add
Edit
Deactivate
```

Do not permanently delete units/categories that are already referenced.

---

# 32. Settings - Users & Access

User table:

```text
NAME         ROLE        STATUS      ACTION
Abdullah     Owner       Active      Edit
Ali          Cashier     Active      Edit
```

Add/Edit User dialog:

```text
Name
Role
PIN
Status
```

Permission matrix:

```text
PERMISSION                      OWNER     MANAGER     CASHIER

Create Sale                     Yes       Yes         Yes
View Sales History              Yes       Yes         Yes
View Profit                     Yes       Optional    No
Create Thaka                    Yes       Optional    Optional
Add Thaka Material              Yes       Optional    Optional
Record Thaka Payment            Yes       Optional    Optional
Create Purchase                 Yes       Yes         No
Manage Inventory                Yes       Yes         No
Stock Adjustment                Yes       Optional    No
Manage Expenses                 Yes       Optional    No
View Reports                    Yes       Optional    No
Manage Users                    Yes       No          No
Manage Settings                 Yes       No          No
Backup / Restore                Yes       No          No
```

Figma can represent this as a clean permission matrix or grouped checkbox dialog.

---

# 33. Overlay Rules

Never stack modal-on-modal.

Use:

```text
Modal       Focused action
Drawer      Contextual detail
Toast       Short feedback
Inline      Validation/error
```

Modal sizes:

```text
Small     440-480px
Medium    560-640px
Large     760-900px
Drawer    480px
```

---

# 34. Overlay - Complete Sale

```text
┌──────────────────────────────────────────────────────┐
│ Complete Sale                                        │
├──────────────────────────────────────────────────────┤
│ Total                                     Rs.8,500   │
│                                                      │
│ Payment Method                                       │
│ [ Cash ] [ Bank ] [ Other ]                          │
│                                                      │
│ Amount Received                                      │
│ [ Rs.10,000 ]                                        │
│                                                      │
│ Change                                    Rs.1,500   │
│                                                      │
│ Customer                                  Walk-in    │
│                                                      │
│ [ ] Print Receipt                                    │
│                                                      │
│ [ Cancel ]                         [ Complete Sale ]  │
└──────────────────────────────────────────────────────┘
```

Prevent duplicate submission.

---

# 35. Overlay - Sale Return

Critical correction: a returned item does not always return to sellable inventory.

```text
┌────────────────────────────────────────────────────────────────────┐
│ Return Items                                                       │
├────────────────────────────────────────────────────────────────────┤
│ Product             Sold Qty      Return Qty      Refund            │
│ LED Bulb 12W        2             [1]             Rs.500            │
│                                                                    │
│ Return Reason                                                      │
│ [ Defective ▼ ]                                                    │
│                                                                    │
│ Item Condition                                                     │
│ [ Restock / Sellable ▼ ]                                           │
│                                                                    │
│ Refund Method                                                      │
│ [ Cash ▼ ]                                                         │
│                                                                    │
│ Total Refund                                      Rs.500           │
│                                                                    │
│ [ Cancel ]                              [ Confirm Return ]          │
└────────────────────────────────────────────────────────────────────┘
```

Condition options:

```text
Restock / Sellable
Damaged
Defective
Scrap / Non-sellable
```

Result:

```text
Sellable
→ Available Stock + Qty

Damaged / Defective / Scrap
→ Available Stock unchanged
→ Non-sellable stock bucket / movement created
```

Original invoice remains unchanged.

---

# 36. Overlay - New Thaka

Fields:

```text
Customer
Project / House Name
Phone
Address
Start Date
Notes
```

Existing customer selection prefills phone/address.

---

# 37. Overlay - Add Thaka Material

```text
Search / Scan Product
Available Stock
Quantity
Rate
Total
Note
```

Validation:

```text
Requested Qty <= Available Sellable Stock
```

If not:

```text
Show warning
Disable Add Material
```

---

# 38. Overlay - Record Thaka Payment

Fields:

```text
Current Balance
Amount
Payment Method
Date
Note
```

---

# 39. Overlay - Final Settlement

Show:

```text
Material Total
Previous Payments
Final Discount
Remaining Before Settlement
Received Now
Payment Method
Final Balance
```

Warning:

```text
Project will be marked as Settled.
```

After success:

```text
Active → Settled
Normal editing disabled
```

---

# 40. Overlay - Add / Edit Product

Fields:

```text
Product Name
Category
Brand
Model
SKU / Barcode
Unit
Purchase Cost
Selling Price
Minimum Stock
```

Electronics options:

```text
Serial Tracking
IMEI Tracking
Warranty
Color
Variant
```

Do not include Initial Stock.

Stock comes only from:

```text
Purchase
Stock Adjustment
Return
```

---

# 41. Overlay - Stock Adjustment

Fields:

```text
Product
Current Stock
Adjustment Type
Quantity
New Stock Preview
Reason
Note
```

Reasons:

```text
Damaged
Lost
Physical Count Correction
Other
```

No silent stock overwrite.

---

# 42. Overlay - Add / Edit Expense

Fields:

```text
Category
Subcategory
Amount
Date
Payment Method
Staff Member if applicable
Note
```

If category is Staff Salary:

```text
Staff Member required
```

---

# 43. Overlay - Add / Edit Customer

Fields:

```text
Name
Phone
Address
Notes
```

---

# 44. Overlay - Add / Edit Supplier

Fields:

```text
Supplier Name
Phone
City
Address
Notes
```

---

# 45. Overlay - Purchase Return

Critical validation:

```text
Purchased Qty
Already Sold / Used Qty
Already Returned Qty
Current Eligible Return Qty
Requested Return Qty
```

Wireframe:

```text
┌──────────────────────────────────────────────────────────────┐
│ Return to Supplier                                           │
├──────────────────────────────────────────────────────────────┤
│ Supplier      ABC Electrical                                 │
│ Purchase      P-0252                                         │
│                                                              │
│ Product       LED Bulb 12W                                   │
│ Purchased     100                                            │
│ Consumed       90                                            │
│ Returned        0                                            │
│ Eligible       10                                            │
│                                                              │
│ Return Qty    [ 10 ]                                         │
│ Return Value  Rs.3,500                                       │
│ Reason        [ Damaged / Wrong Goods ▼ ]                    │
│                                                              │
│ [ Cancel ]                     [ Confirm Supplier Return ]   │
└──────────────────────────────────────────────────────────────┘
```

Rule:

```text
Return Qty <= Eligible Return Qty
```

Result:

```text
Inventory decreases
Purchase Return record created
Original Purchase preserved
```

---

# 46. Drawer - Purchase Detail

Show:

```text
Purchase ID
Supplier
Date
Supplier Invoice
Products
Qty
Cost
Total
```

Actions:

```text
Print
Return to Supplier
```

---

# 47. Drawer - Customer Detail

Show:

```text
Name
Phone
Address
Local Sales
Active Thaka Count
Current Thaka Balance
```

Tabs:

```text
Sales
Thaka
Details
```

---

# 48. Drawer - Supplier Detail

Show:

```text
Supplier
Phone
City
Total Purchases
Last Purchase
```

Tabs:

```text
Purchases
Products
Details
```

---

# 49. Reusable Confirmation Dialog

Use for:

```text
Remove Expense
Disable User
Restore Backup
Cancel Important Operation
Reopen Settled Thaka
```

Structure:

```text
Title
Consequence
Cancel
Confirm / Destructive Action
```

Normal Cancel is not red.

---

# 50. Permission Dialog

Example:

```text
You do not have permission to view profit information.

[ Close ]
```

No manager override in V1.

---

# 51. Toast System

Bottom-right.

Examples:

```text
✓ Sale completed successfully

⚠ Receipt failed to print.
  Sale was saved successfully. [Retry]
```

Printer failure must not visually imply sale failure.

---

# 52. Loading / Empty / Error States

Loading:

```text
KPI Skeleton
Table Row Skeleton
Recent Activity Skeleton
Low Stock Skeleton
```

Do not block the entire screen with one spinner.

Empty:

```text
No sales recorded today.

[ New Sale ]
```

Errors:

```text
Inline validation
Section-level message
No raw stack trace
```

---

# 53. Keyboard and Desktop Interaction

Design must support:

```text
Visible focus ring
Predictable Tab order
Enter to confirm where safe
Escape to close non-critical dialogs
Arrow navigation where useful
Barcode scanner into search
```

Potential shortcuts:

```text
F2   Product Search
F4   Customer
F6   Discount
F8   Payment
F10  Complete Sale
Esc  Close / Back
```

Do not permanently clutter UI with shortcut labels yet.

---

# 54. Electronics Product Behavior

Quantity-based:

```text
Bulb
Switch
Breaker
Socket
```

Roll/length-based:

```text
Wire
Cable
```

Unit-identity-based:

```text
Mobile
Device
Appliance
Serialized Electronics
```

Optional attributes:

```text
Brand
Model
Warranty
Color
Variant
Serial Tracking
IMEI Tracking
```

Do not force serial/IMEI fields on every product.

---

# 55. Inventory Truth Model

```text
Opening Stock
+ Purchases
+ Sale Returns to Sellable
- Local Sales
- Thaka Material
- Purchase Returns
- Damage
± Adjustments
= Current Sellable Stock
```

Therefore:

- No direct editable Current Stock field
- Adjustment requires reason
- Returns need disposition
- Product Detail shows movement history
- Purchases and Thaka visibly affect inventory

---

# 56. Role-Aware UI

Owner:

```text
Full Access
```

Manager:

```text
Configurable Operational Access
```

Cashier:

```text
New Sale
Sales History
Limited Other Modules
No Profit by default
No Settings by default
```

Do not create a separate shell per role.

Hide or disable restricted actions consistently.

---

# 57. WPF-Friendly Design Constraints

Prefer:

```text
Grid layouts
Reusable controls
Standard forms
Standard tables
Resource-friendly color tokens
Simple animations
Simple dialogs
Simple drawers
Predictable component states
```

Avoid:

```text
CSS-only effects
Excessive blur
Browser-only interactions
Deep floating layers
Experimental visual effects
Custom canvas-heavy layouts
Highly fluid mobile behavior
```

---

# 58. Figma Make / Stitch Generation Rules

The generator must:

1. Build the design system first.
2. Build the application shell once.
3. Reuse components.
4. Preserve exactly 17 full screens.
5. Keep Dashboard standalone.
6. Keep Thaka separate from Local Sales.
7. Never invent FBR UI.
8. Never invent mobile layouts.
9. Never invent new colors.
10. Never invent extra analytics.
11. Never add charts unless specified.
12. Use realistic electronics examples.
13. Use `Rs.` currency.
14. Keep light mode default.
15. Preserve exact density and geometry tokens.
16. Use drawers for contextual details.
17. Use modals for focused actions.
18. Use invoice-level discount only.
19. Respect return disposition logic.
20. Respect purchase-return eligibility.
21. Do not show inventory value before costing rule is finalized.
22. Do not merge Thaka into normal sales totals.
23. Use WPF-realistic components.
24. Preserve existing components across batches.
25. Do not regenerate the entire system after each batch.

---

# 59. Recommended Figma File Structure

```text
00 Cover

01 Foundations
02 Colors
03 Typography
04 Components
05 Patterns
06 Application Shell

07 Dashboard
08 Sales
09 Thaka
10 Purchases
11 Inventory
12 Expenses
13 Customers
14 Suppliers
15 Reports
16 Settings

17 System Screens
18 Dialogs & Drawers
19 States
20 Prototype
```

---

# 60. Generation Plan

Do not generate all screens in one uncontrolled pass.

## Batch 1 - Foundation

Generate:

```text
Design System
Application Shell
Dashboard
```

Instruction:

```text
Create the master visual language for Edge Retails.
Use the exact color, typography, geometry, spacing, table, and card rules from this specification.
Build the shell and Dashboard only.
Do not create additional screens.
All future batches must reuse these components.
```

## Batch 2 - Sales

Generate:

```text
POS / New Sale
Sales History
Sale Detail
Complete Sale
Sale Return
```

Instruction:

```text
Reuse the existing shell and design system.
Create the Sales flow only.
Use invoice-level discount only.
Do not add HOLD.
Do not merge Thaka into POS.
Implement Sale Return with item condition/disposition.
```

## Batch 3 - Thaka

Generate:

```text
Thaka / Projects
Thaka Workspace
New Thaka
Add Material
Record Payment
Final Settlement
```

Instruction:

```text
Reuse all existing components.
Keep Thaka visually distinct from local counter sales.
Do not count Thaka as normal sales in the UI.
Use clear material, paid, and balance hierarchy.
```

## Batch 4 - Purchases & Inventory

Generate:

```text
New Purchase
Purchase History
Purchase Detail Drawer
Purchase Return
Inventory
Product Detail
Add/Edit Product
Stock Adjustment
```

Instruction:

```text
Reuse existing shell and components.
Emphasize stock traceability.
Do not show Inventory Value.
Purchase Return must show Eligible Return Qty.
Current Stock must not be directly editable.
```

## Batch 5 - Finance & Contacts

Generate:

```text
Expenses
Add/Edit Expense
Customers
Customer Drawer
Suppliers
Supplier Drawer
```

Instruction:

```text
Reuse existing components.
Keep Expenses visually separate from Inventory Purchases.
Keep Customers simple and avoid CRM-like features.
```

## Batch 6 - Reports & System

Generate:

```text
Reports
Settings
Categories & Units
Users & Access
Login
First Setup / License
Confirmations
Permissions
Toasts
Empty/Loading/Error States
```

Instruction:

```text
Reuse all existing components.
Yearly report must show only Total Sales and Total Profit/Net Profit.
Include Categories & Units and permission matrix inside Settings.
Do not add extra system screens.
```

---

# 61. Final QA Checklist

Before accepting the visual system:

- [ ] Exactly 17 full screens
- [ ] No duplicate full screens
- [ ] Consistent sidebar
- [ ] Consistent top bar
- [ ] Consistent page padding
- [ ] Consistent component heights
- [ ] Consistent table density
- [ ] Consistent button styles
- [ ] Consistent modal styles
- [ ] Consistent drawer styles
- [ ] Light theme complete
- [ ] Dark theme tokens defined
- [ ] Dashboard clean
- [ ] POS cart prominent
- [ ] No HOLD
- [ ] No Thaka inside POS
- [ ] Thaka separate from Local Sales
- [ ] Invoice-level discount only
- [ ] Sale Return includes item condition
- [ ] Purchase Return includes eligibility validation
- [ ] No direct stock editing
- [ ] Stock movement visible
- [ ] Categories & Units present
- [ ] Permission matrix present
- [ ] Expenses separate from Purchases
- [ ] Yearly report minimal
- [ ] No FBR
- [ ] No mobile UI
- [ ] No invented charts
- [ ] No inventory valuation before costing decision
- [ ] WPF-realistic layouts
- [ ] All major wireframes included
- [ ] Overlay behavior consistent
- [ ] Loading/empty/error states included

---

# 62. Final Locked Visual Foundation

```text
Windows Desktop
1440 x 900 Primary
1366 x 768 Minimum

WPF-Ready
C# / XAML Implementation Target

Stripe-Inspired Minimal UI
Electric Indigo Accent
Cool Neutral Surfaces

Plus Jakarta Sans
Space Grotesk for Display Figures

17 Full Screens
Reusable Tables
Reusable Modals
Reusable Drawers
Reusable Toasts

Electronics-Only V1
Thaka Separate from Local Sales
Invoice-Level Discount Only
Inventory Movement Based
Return Disposition Aware
Purchase Return Eligibility Aware

Local Signed License
PostgreSQL Diagnostics
Cloud Backup Status

No FBR
No Mobile Layouts
No Unnecessary Charts
No Direct Stock Editing
No Premature Inventory Valuation
```

**Document Status: CORRECTED AND READY FOR CONTROLLED FIGMA MAKE / STITCH GENERATION**
