# Sprint 5 Phase 2 — Reports Implementation Report

**Date:** 2026-09-20
**Status:** COMPLETE
**Primary Screen:** 16 — Reports
**Backend executable attachment:** intentionally deferred
**Manual visual QA:** deferred
**Git:** untouched

## 1. Live design authority

Reports implementation was derived from the live Figma source:
- Reports primary frame: `14:10022`
- Reports content frame: `14:10128`

The Figma contract establishes one Reports primary screen, Daily / Monthly / Yearly modes, six core financial cards and Monthly Expense Breakdown. The implementation preserves this visual hierarchy while applying the newer locked backend reporting requirements for analytical graphics and averages.

## 2. Real Reports route

`NavigationTarget.Reports` now resolves to a real cached `ReportsViewModel` instead of the placeholder page.

Application data templates map:
- `ReportsViewModel` → `ReportsView`

The Reports page ViewModel is cached in `PageViewModelFactory` because it subscribes to shared singleton state. Repeated navigation therefore does not accumulate duplicate event subscriptions.

## 3. Reporting data contract

`DemoReportingService` is the temporary frontend reporting read model. It consumes the existing frontend operational state:
- `DemoTransactionService` for local sales and sale returns.
- `DemoPurchaseInventoryService` for purchases.
- `DemoBusinessDirectoryService` for operating expenses.
- `DemoRetailState` for Thaka material ledgers.

It contains no direct PostgreSQL, Npgsql, Dapper, HTTP or API dependency. When the separately designed backend is attached, the WPF Reports surface can be fed by backend report DTO/query services without redesigning the screen.

## 4. Financial rules implemented

### Net Local Sales

`Net Local Sales = Completed Local Sales - Successful Sale Refunds`

Returns affect the period in which the return occurs.

### Gross Profit

`Gross Profit = Net Local Sales - Net Local COGS`

Sale items capture `UnitCostSnapshot` at sale time so historical profit is not recalculated from a future product cost.

### Net Profit

`Net Profit = Gross Profit - Operating Expenses`

Purchases are never treated as operating expenses.

### Return cost treatment

For `RESTOCK_SELLABLE`, the original sale item's cost snapshot is reversed from COGS.

For non-sellable dispositions such as defective or damaged returns, revenue is refunded but sellable COGS is not reversed.

### Thaka

Thaka material value is read from material-issue ledgers and remains a separate reporting dimension from Local Sales. Payment collection is not treated as new revenue.

## 5. Costing bridge hardened

The frontend purchase demo state was aligned to the backend's locked Moving Weighted Average rule.

Purchase intake now:
1. Calculates each line base value.
2. Allocates Other Charges proportionally as landed cost.
3. Keeps Base Cost separate from Effective/Landed Unit Cost.
4. Updates product cost using Moving Weighted Average.
5. Keeps Purchase Subtotal based on supplier invoice base cost.
6. Adds Other Charges only once to Purchase Total.

This prevents a subtle double-counting error while making future sale cost snapshots meaningful.

## 6. Daily Reports

Daily mode includes:
- Net Local Sales
- Gross Profit
- Expenses
- Net Profit
- Purchases
- Thaka Material
- Average Invoice Value
- Average Gross Profit per Invoice
- Sales Count
- Average Items per Invoice

Primary graph:
- Hourly Sales
- Hourly Gross Profit
- 24 hour buckets, including zero-activity hours

## 7. Monthly Reports

Monthly mode includes the six core financial KPIs plus:
- Average Daily Sales
- Average Daily Net Profit
- Average Daily Expenses
- Average Invoice Value

Primary graph:
- Daily Sales
- Daily Net Profit
- Daily Expenses
- every calendar day exists in the series, including zero-activity days

Additional sections:
- Expense Category Breakdown
- separate Thaka Activity by project

## 8. Yearly Reports

Yearly summary includes:
- Total Net Sales
- Net Profit
- Expenses
- Thaka Material

Average metrics:
- Average Monthly Sales
- Average Monthly Net Profit
- Average Monthly Expenses
- Sales Count

Primary graph:
- Jan through Dec Sales
- Net Profit
- Expenses
- missing months zero-filled

For the current year, average monthly metrics divide by elapsed months rather than blindly by 12.

## 9. Graph implementation

A custom WPF `FinancialTrendChart` was introduced.

Properties:
- report trend collection
- optional expense series
- dynamic profit-series label

Rendering includes:
- analytical Y-axis grid
- currency-scale labels
- zero baseline when negative values exist
- X-axis period labels
- Sales line
- Profit line
- optional Expenses line
- compact legend
- empty-period state

It does not use XAML Canvas and adds no external chart dependency.

Dashboard remains chart-free as required.

## 10. Automated verification

Final gates:
- Formatter verify-no-changes: PASS
- Debug build: PASS, 0 warnings / 0 errors
- Debug unit tests: 72 / 72 PASS
- Debug integration tests: 1 / 1 PASS
- Release build: PASS, 0 warnings / 0 errors
- Release unit tests: 72 / 72 PASS
- Release integration tests: 1 / 1 PASS

Phase 2 added forensic coverage for:
- real/cached Reports navigation
- Reports DataTemplate
- operational ledger sourcing
- historical sale cost snapshots
- return-period revenue and COGS behavior
- zero-filled charts
- calendar-day/month average rules
- expense breakdown
- Thaka separation
- chart/no-Canvas rule
- Dashboard chart-free rule
- MWA landed-cost bridge
- no direct backend transport/database coupling

## 11. Runtime smoke

Runtime report service execution passed.

Observed demo-state values:
- Daily Net Sales: Rs. 37,050
- Daily Gross Profit: Rs. 8,670
- Daily Expenses: Rs. 11,700
- Daily Net Profit: Rs. -3,030
- Monthly Purchases: Rs. 198,500
- Monthly Thaka Material: Rs. 932,400

Trend cardinality:
- Daily: 24 points
- Monthly: all days in selected month
- Yearly: 12 points

Moving Weighted Average smoke:
- starting product cost: Rs. 350
- new landed purchase: 10 units × Rs. 400 + Rs. 100 Other Charges
- resulting MWA cost: Rs. 356.52
- Purchase Subtotal: Rs. 4,000
- Purchase Total: Rs. 4,100
- Other Charges double-count check: PASS

## 12. Deferred

- Manual visual comparison across 1440×900 and 1366×768 remains deferred.
- Backend Dapper/report-query attachment remains deferred until frontend completion.
- Git cleanup/commit/freeze remains deferred until explicitly requested.

## 13. Next

Sprint 5 Phase 3: Settings.
