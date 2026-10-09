# Edge Retails — Phase 7 Pass 5
# Business Event Effect Matrix

**Document Status:** FROZEN CANONICAL AUTHORITY  
**Workspace:** `C:\Users\muham\OneDrive\Desktop\Point of Sale`  
**Governing Authorities:** Resolution01, Resolution02, Resolution03, Resolution04, Resolution05, D13-2, D17, B03, C29, C26  
**Accounting Provider:** Isolated PostgreSQL 18.0 / Npgsql  

---

## 1. Matrix Notation & Definitions

- **0:** No effect / unchanged.
- **+ / −:** Concrete numerical increase / decrease.
- **Transfer:** Stock moves between internal buckets; total owned quantity and inventory value are preserved.
- **Exact:** Specific individual units updated with identity tracking (`InventoryUnitStatus`, `AcquisitionCost`).
- **Bulk:** Moving Weighted Average (`ProductCostState`) updated continuously.
- **Atomic Transaction:** All effects within a row execute within a single PostgreSQL transaction (`ITransactionRunner`).

---

## 2. Comprehensive Event Effect Matrix

| Business Event | Physical Units / Lots | Owned Qty / Book Qty | Inventory Buckets | ProductCostState (OwnedQty, TotalCost, MWA) | Cash | Customer AR / Khata | Supplier AP | Sales Revenue | COGS | Recognized Loss | Recovery Gains | Audit & Operation Ledger |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Intake / Receive** (`ReceiveProductIntakeHandler`) | New units allocated; new lot created | + BaseQuantity | + `Sellable` (or intake target) | OwnedQty +, TotalCost + ReceivedCost, MWA updated | 0 | 0 | 0 (AP established at Purchase) | 0 | 0 | 0 | 0 | `INTAKE_RECEIVED`, `ReceiveProductIntake` outcome recorded |
| **Commercial Sale** (`CompleteSaleHandler`) | Units set to `Sold`; bulk lots consumed | − BaseQuantity | − `Sellable` | OwnedQty −, TotalCost − SoldCost, MWA unchanged | + If cash | + If credit | 0 | + GrossSales | + ConsumedCost | 0 | 0 | `SALE_COMPLETED`, `CompleteSale` outcome recorded |
| **Sale Return (Restock)** (`CreateSaleReturnHandler`) | Returned units set to `Available`; restored to lot | + BaseQuantity | + `Sellable` (or target) | OwnedQty +, TotalCost + RestoredCost, MWA updated | − If cash | − If credit | 0 | − ReturnAmount (Refund) | − HistoricalConsumptCost | 0 | 0 | `SALE_RETURNED`, `CreateSaleReturn` outcome recorded |
| **Sale Return (Scrap)** (`CreateSaleReturnHandler`) | Returned units set to `Scrapped`; carrying zero | + Operational Qty (Book), Owned 0 | + `Scrap` | OwnedQty 0, TotalCost 0, MWA unchanged | − If cash | − If credit | 0 | − ReturnAmount (Refund) | 0 (COGS not reversed) | + ReturnCost | 0 | `SALE_RETURN_SCRAPPED`, `CreateSaleReturn` outcome recorded |
| **Commercial Exchange** (`CommercialExchangeHandler`) | Atomic return of old units + sale of new units | Net quantity delta | Destination bucket + / `Sellable` − | Net cost delta applied; MWA updated | Net cash difference | Net customer balance delta | 0 | Net sales delta | Net COGS delta | If return is scrap | 0 | `COMMERCIAL_EXCHANGE`, `CommercialExchange` outcome recorded |
| **Stocktake Shortage (Lost)** (`CreateStockAdjustmentHandler`) | Units marked `Missing`; bulk lot decremented | − BaseQuantity | − Source bucket | OwnedQty −, TotalCost − RemovedCost, MWA unchanged | 0 | 0 | 0 | 0 | 0 | + AllocatedLoss | 0 | `STOCK_ADJUSTMENT`, `StockAdjustment` outcome recorded |
| **Stock Condition Transfer** (`TransferInventoryConditionHandler`) | Same units; status updated to match destination | Total 0 (Neutral transfer) | Source −, Target + | OwnedQty 0, TotalCost 0, MWA unchanged | 0 | 0 | 0 | 0 | 0 | 0 | 0 | `CONDITION_TRANSFERRED`, `TransferCondition` outcome recorded |
| **Condition Transfer to Scrap** (`TransferInventoryConditionHandler`) | Units set to `Scrapped`; carrying zeroed | Owned −, Book retains operational | Source −, `Scrap` + | OwnedQty −, TotalCost − RemovedCost, MWA unchanged | 0 | 0 | 0 | 0 | 0 | + RemovedCost | 0 | `SCRAP_WRITTEN_OFF`, `TransferCondition` outcome recorded |
| **Found Unit Recovery** (`FoundInventoryUnitHandler`) | Same unit `Missing` $\rightarrow$ `Available`; lot linked | + BaseQuantity | + Target bucket (`Sellable`/`Damaged`) | OwnedQty +, TotalCost + RestoredValue, MWA updated | 0 | 0 | 0 | 0 | 0 | 0 (Historical loss preserved) | + `InventoryLossRecoveryGain` | `INVENTORY_UNIT_FOUND`, `FoundInventoryUnit` outcome recorded |
| **Shop Warranty Send** (`SendShopStockToSupplierWarrantyHandler`) | Same units/bulk; custody transferred | Total Owned 0 (Custody transfer) | Source −, `WithSupplier` + | OwnedQty 0, TotalCost 0, MWA unchanged (Custody only) | 0 | 0 | 0 | 0 | 0 | 0 | 0 | `SHOP_WARRANTY_SENT`, `SendShopStockToSupplierWarranty` outcome |
| **Shop Warranty: Repaired** (`ReceiveShopStockWarrantyHandler`) | Same units return to shop | Total Owned 0 (Custody return) | `WithSupplier` −, `Sellable`/`Damaged` + | OwnedQty 0, TotalCost 0, MWA unchanged | 0 | 0 | 0 | 0 | 0 | 0 | 0 | `WARRANTY_REPAIRED`, resolution allocation recorded |
| **Shop Warranty: Replaced** (`ReceiveShopStockWarrantyHandler`) | Lineage preserved; new unit/lot received | Quantity continuity | `WithSupplier` −, Destination + | Cost continuity; MWA unchanged | 0 | 0 | 0 | 0 | 0 | 0 | 0 | `WARRANTY_REPLACED`, resolution allocation recorded |
| **Shop Warranty: Rejected** (`ReceiveShopStockWarrantyHandler`) | Same units return un-repaired | Total Owned 0 (Custody return) | `WithSupplier` −, `Damaged`/`Defective` + | OwnedQty 0, TotalCost 0, MWA unchanged | 0 | 0 | 0 | 0 | 0 | 0 | 0 | `WARRANTY_REJECTED`, resolution allocation recorded |
| **Shop Warranty: Scrapped** (`ReceiveShopStockWarrantyHandler`) | Units set to `Scrapped` / bulk derecognized | Owned − | `WithSupplier` −, `Scrap` + | OwnedQty −, TotalCost − DerecognizedMwa, MWA updated | 0 | 0 | 0 | 0 | 0 | + DerecognizedMwa | 0 | `WARRANTY_SCRAPPED`, resolution allocation recorded |
| **Shop Warranty: Credited (Gain)** (`ReceiveShopStockWarrantyHandler`) | Units derecognized / bulk derecognized | Owned − | `WithSupplier` − | OwnedQty −, TotalCost − DerecognizedMwa, MWA updated | 0 | 0 | − SupplierCredit (AP reduced) | 0 (Zero fake sales) | 0 | 0 | + `WarrantyRecoveryGain` ($\text{Credit} - \text{Carrying}$) | `WARRANTY_CREDITED`, resolution allocation recorded |
| **Shop Warranty: Credited (Deficit)** (`ReceiveShopStockWarrantyHandler`) | Units derecognized / bulk derecognized | Owned − | `WithSupplier` − | OwnedQty −, TotalCost − DerecognizedMwa, MWA updated | 0 | 0 | − SupplierCredit (AP reduced) | 0 | 0 | + DeficitLoss ($\text{Carrying} - \text{Credit}$) | 0 | `WARRANTY_CREDITED`, resolution allocation recorded |
| **Supplier Payment** (`CreateSupplierPaymentHandler`) | 0 | 0 | 0 | 0 | − If cash | 0 | − Settlement | 0 | 0 | 0 | 0 | `SUPPLIER_PAYMENT_CREATED`, payment outcome recorded |
| **Supplier Refund** (`CreateSupplierRefundHandler`) | 0 | 0 | 0 | 0 | + If cash | 0 | + Liability adjustment | 0 | 0 | 0 | 0 | `SUPPLIER_REFUND_CREATED`, refund outcome recorded |
| **Supplier Payment Reversal** (`ReverseSupplierPaymentHandler`) | 0 | 0 | 0 | 0 | + If cash | 0 | + Liability compensation | 0 | 0 | 0 | 0 | `SUPPLIER_PAYMENT_REVERSED`, reversal outcome recorded |
| **Supplier Refund Reversal** (`ReverseSupplierRefundHandler`) | 0 | 0 | 0 | 0 | − If cash | 0 | − Liability compensation | 0 | 0 | 0 | 0 | `SUPPLIER_REFUND_REVERSED`, reversal outcome recorded |
| **Supplier Opening Balance** (`SupplierOpeningBalanceHandler`) | 0 | 0 | 0 | 0 | 0 | 0 | + IncreasePayable or − DecreasePayable | 0 | 0 | 0 | 0 | `SUPPLIER_OPENING_BALANCE`, `SupplierOpeningBalance` outcome |
| **Manual Cash In** (`RecordManualCashMovementHandler`) | 0 | 0 | 0 | 0 | + Amount | 0 | 0 | 0 | 0 | 0 | 0 | `MANUAL_CASH_IN`, cash movement appended |
| **Manual Cash Out** (`RecordManualCashMovementHandler`) | 0 | 0 | 0 | 0 | − Amount | 0 | 0 | 0 | 0 | 0 | 0 | `MANUAL_CASH_OUT`, cash movement appended |
| **Cash Session Open** (`OpenCashSessionHandler`) | 0 | 0 | 0 | 0 | Baseline drawer established | 0 | 0 | 0 | 0 | 0 | 0 | `CASH_SESSION_OPENED`, session entity created |
| **Cash Session Close** (`CloseCashSessionHandler`) | 0 | 0 | 0 | 0 | Reconciled against counted | 0 | 0 | 0 | 0 | 0 | 0 | `CASH_SESSION_CLOSED`, difference recorded |
| **Customer Khata Payment** (`RecordCustomerPaymentHandler`) | 0 | 0 | 0 | 0 | + If cash | − Customer balance | 0 | 0 | 0 | 0 | 0 | `CUSTOMER_PAYMENT_RECORDED`, payment outcome recorded |
| **Customer Khata Credit Adjust** (`AdjustCustomerCreditHandler`) | 0 | 0 | 0 | 0 | 0 | Explicit adjustment | 0 | 0 | 0 | 0 | 0 | `CUSTOMER_CREDIT_ADJUSTED`, audit recorded |
| **Customer Suspension** (`CustomerSuspensionHandler`) | 0 | 0 | 0 | 0 | 0 | Credit blocked | 0 | 0 | 0 | 0 | 0 | `CUSTOMER_SUSPENDED`, audit recorded |
| **Customer Reactivation** (`CustomerSuspensionHandler`) | 0 | 0 | 0 | 0 | 0 | Credit unblocked | 0 | 0 | 0 | 0 | 0 | `CUSTOMER_REACTIVATED`, audit recorded |
| **Thaka Material Issue** (`ThakaHandlers`) | Specific units issued to project | − Owned | − Source bucket | OwnedQty −, TotalCost − Cost, MWA unchanged | 0 | + ThakaReceivable | 0 | + ThakaRevenue | + ThakaCost | 0 | 0 | `THAKA_MATERIAL_ISSUED`, Thaka outcome recorded |
| **Thaka Material Return** (`ThakaHandlers`) | Same units returned | + Owned | + Target bucket | OwnedQty +, TotalCost + Cost, MWA updated | 0 | − ThakaReceivable | 0 | − ThakaRevenue | − ThakaCost | 0 | 0 | `THAKA_MATERIAL_RETURNED`, Thaka outcome recorded |
| **Thaka Payment** (`ThakaHandlers`) | 0 | 0 | 0 | 0 | + If cash | − ThakaReceivable | 0 | 0 (No duplicate revenue) | 0 | 0 | 0 | `THAKA_PAYMENT_RECORDED`, Thaka outcome recorded |
| **Thaka Settlement** (`ThakaHandlers`) | Project closed | 0 | 0 | 0 | If final cash | Project balance zeroed | 0 | 0 | 0 | 0 | 0 | `THAKA_SETTLED`, Thaka outcome recorded |

---

## 3. Financial Reconciliation Proof Equations

1. **Cash Drawer Equation:**
   $$\text{EndingCash} = \text{OpeningCash} + \sum \text{CashInMovements} - \sum \text{CashOutMovements}$$
2. **Supplier Accounts Payable Equation:**
   $$\text{SupplierAP}_{\text{Current}} = \text{AP}_{\text{Opening}} + \text{Purchases} - \text{PurchaseReturns} - \text{Payments} + \text{Refunds} - \text{WarrantyCredits}$$
3. **Net Profit Equation:**
   $$\text{NetProfit} = \text{NetSales} - \text{NetCOGS} - \text{OperatingExpenses} - \text{InventoryLoss} + \text{InventoryLossRecoveryGain} + \text{WarrantyRecoveryGain}$$
4. **Zero False Identity & Zero False Accounting Invariants:**
   - No fake Purchase entries created for Opening Balance, Warranty Replacements, or Stock Adjustments.
   - No fake Sales entries created for Warranty Credits, Found Recoveries, or Cash Floats.
   - Every economic fact is append-only, durable, and auditable.
