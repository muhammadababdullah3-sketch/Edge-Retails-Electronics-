# Live Server endpoint register — 2026-09-27

Source inventory from controller attributes and action bodies. Permission means direct controller check; handlers can add checks.

| Controller | HTTP | Route | Action | Auth | Controller permission | Actor context | ClientOperationId |
|---|---|---|---|---|---|---|---|
| Auth | GET | /api/auth/accounts | GetAccounts | Anonymous | — | No | No |
| Auth | POST | /api/auth/login | Login | Anonymous | — | No | No |
| Auth | POST | /api/auth/logout | Logout | Terminal + session | — | Yes | No |
| Auth | GET | /api/auth/session | GetCurrentSession | Terminal + session | — | Yes | No |
| Catalog | GET | /api/catalog/products | GetProducts | Terminal + session | InventoryManage | No | No |
| Catalog | GET | /api/catalog/products/{id:guid} | GetProduct | Terminal + session | InventoryManage | No | No |
| Catalog | GET | /api/catalog/products/sku/{sku} | GetProductBySku | Terminal + session | InventoryManage | No | No |
| Catalog | POST | /api/catalog/products | CreateProduct | Terminal + session | — | Yes | No |
| Catalog | PUT | /api/catalog/products/{id:guid} | UpdateProduct | Terminal + session | — | Yes | No |
| Catalog | POST | /api/catalog/products/{id:guid}/deactivate | DeactivateProduct | Terminal + session | — | Yes | No |
| Catalog | POST | /api/catalog/products/{id:guid}/reactivate | ReactivateProduct | Terminal + session | — | Yes | No |
| Catalog | GET | /api/catalog/companies | GetCompanies | Terminal + session | InventoryManage | No | No |
| Catalog | POST | /api/catalog/companies | CreateCompany | Terminal + session | — | Yes | No |
| Catalog | GET | /api/catalog/categories | GetCategories | Terminal + session | InventoryManage | No | No |
| Catalog | POST | /api/catalog/categories | CreateCategory | Terminal + session | — | Yes | No |
| Catalog | GET | /api/catalog/units | GetUnits | Terminal + session | InventoryManage | No | No |
| Catalog | POST | /api/catalog/products/{id:guid}/units | ConfigureUnits | Terminal + session | — | Yes | No |
| Catalog | POST | /api/catalog/units/{unitId:guid}/barcode | SetBarcode | Terminal + session | — | Yes | No |
| Customers | GET | /api/customers | GetCustomers | Terminal + session | CustomersManage | No | No |
| Customers | GET | /api/customers/list | GetCustomerList | Terminal + session | CustomersManage | No | No |
| Customers | POST | /api/customers | CreateCustomer | Terminal + session | — | Yes | No |
| Customers | PUT | /api/customers/{id:guid} | UpdateCustomer | Terminal + session | — | Yes | No |
| Expenses | GET | /api/expenses | GetExpenses | Terminal + session | ExpensesManage | No | No |
| Expenses | GET | /api/expenses/categories | GetCategories | Terminal + session | ExpensesManage | No | No |
| Expenses | GET | /api/expenses/subcategories | GetSubcategories | Terminal + session | ExpensesManage | No | No |
| Expenses | POST | /api/expenses | PostExpense | Terminal + session | — | Yes | Yes |
| Expenses | POST | /api/expenses/{id:guid}/void | VoidExpense | Terminal + session | — | Yes | Yes |
| Finance | GET | /api/finance/suppliers/{supplierId:guid}/workspace | GetSupplierWorkspace | Terminal + session | SupplierAccountView | No | No |
| Finance | POST | /api/finance/supplier-payment | CreateSupplierPayment | Terminal + session | — | Yes | No |
| Finance | POST | /api/finance/supplier-refund | CreateSupplierRefund | Terminal + session | — | Yes | No |
| Finance | POST | /api/finance/cash-session/open | OpenCashSession | Terminal + session | — | Yes | No |
| Finance | POST | /api/finance/cash-movement | RecordCashMovement | Terminal + session | — | Yes | No |
| Inventory | GET | /api/inventory/stock | GetStock | Terminal + session | InventoryManage | No | No |
| Inventory | GET | /api/inventory/movements | GetMovements | Terminal + session | InventoryManage | No | No |
| Inventory | GET | /api/inventory/exact-units | GetExactUnits | Terminal + session | InventoryManage | No | No |
| Inventory | GET | /api/inventory/units/{inventoryUnitId:guid}/history | GetUnitHistory | Terminal + session | InventoryManage | No | No |
| Inventory | GET | /api/inventory/products/{productId:guid}/purchase-provenance | GetProductPurchaseProvenance | Terminal + session | InventoryManage | No | No |
| Inventory | GET | /api/inventory/products/{productId:guid}/sale-history | GetProductSaleHistory | Terminal + session | InventoryManage | No | No |
| Inventory | POST | /api/inventory/adjustments | CreateAdjustment | Terminal + session | — | Yes | No |
| Inventory | POST | /api/inventory/stocktake | CreateStocktake | Terminal + session | — | Yes | No |
| Inventory | POST | /api/inventory/stocktake/{id:guid}/post | PostStocktake | Terminal + session | — | Yes | Yes |
| Operations | GET | /api/operations/{clientOperationId:guid} | GetOperationStatus | Terminal + session | — | Yes | Yes |
| Purchasing | GET | /api/purchasing | GetHistory | Terminal + session | PurchasingManage | No | No |
| Purchasing | GET | /api/purchasing/returns | GetReturnHistory | Terminal + session | PurchasingManage | No | No |
| Purchasing | GET | /api/purchasing/{id:guid} | GetDocument | Terminal + session | PurchasingManage | No | No |
| Purchasing | GET | /api/purchasing/items/{purchaseItemId:guid}/units | GetUnitsForPurchaseItem | Terminal + session | PurchasingManage | No | No |
| Purchasing | POST | /api/purchasing/create | CreatePurchase | Terminal + session | — | Yes | No |
| Purchasing | POST | /api/purchasing/intake | ReceiveProductIntake | Terminal + session | — | Yes | No |
| Purchasing | POST | /api/purchasing/return | CreatePurchaseReturn | Terminal + session | — | Yes | No |
| Purchasing | POST | /api/purchasing/void | VoidPurchase | Terminal + session | — | Yes | No |
| Reports | GET | /api/reports/snapshot | GetSnapshot | Terminal + session | ReportsView | No | No |
| Reports | GET | /api/reports/daily | GetDailyReport | Terminal + session | — | No | No |
| Reports | GET | /api/reports/monthly | GetMonthlyReport | Terminal + session | — | No | No |
| Reports | GET | /api/reports/yearly | GetYearlyReport | Terminal + session | — | No | No |
| Roles | GET | /api/roles | GetRoles | Terminal + session | SettingsManage | Yes | No |
| Roles | GET | /api/roles/permissions | GetPermissions | Terminal + session | SettingsManage | Yes | No |
| Roles | GET | /api/roles/{id:guid} | GetRole | Terminal + session | SettingsManage | Yes | No |
| Roles | GET | /api/roles/{id:guid}/permissions | GetRolePermissions | Terminal + session | SettingsManage | Yes | No |
| Sales | GET | /api/sales | GetSalesHistory | Terminal + session | SalesView | No | No |
| Sales | GET | /api/sales/returns | GetReturnHistory | Terminal + session | SalesView | No | No |
| Sales | GET | /api/sales/quotations | GetQuotations | Terminal + session | SalesView | No | No |
| Sales | GET | /api/sales/{id:guid} | GetSaleDetail | Terminal + session | SalesView | No | No |
| Sales | GET | /api/sales/scan | Scan | Terminal + session | SalesPosUse | No | No |
| Sales | GET | /api/sales/exact-units | GetPosExactUnits | Terminal + session | SalesPosUse | No | No |
| Sales | GET | /api/sales/catalog | GetPosCatalog | Terminal + session | SalesPosUse | No | No |
| Sales | GET | /api/sales/drafts | GetOpenDrafts | Terminal + session | SalesDraftResume | No | No |
| Sales | GET | /api/sales/drafts/{id:guid} | GetDraft | Terminal + session | SalesDraftResume | No | No |
| Sales | POST | /api/sales/drafts | SaveDraft | Terminal + session | — | Yes | No |
| Sales | POST | /api/sales/drafts/{id:guid}/cancel | CancelDraft | Terminal + session | — | Yes | No |
| Sales | POST | /api/sales/complete | CompleteSale | Terminal + session | — | Yes | No |
| Sales | POST | /api/sales/drafts/complete | CompletePosDraft | Terminal + session | — | Yes | No |
| Sales | POST | /api/sales/return | CreateSaleReturn | Terminal + session | — | Yes | No |
| Sales | POST | /api/sales/exchange | ExecuteCommercialExchange | Terminal + session | — | Yes | No |
| Setup | GET | /api/setup/state | State | Anonymous | — | No | No |
| Setup | POST | /api/setup/bootstrap | Bootstrap | Anonymous | — | No | Yes |
| Suppliers | GET | /api/suppliers | GetSuppliers | Terminal + session | SuppliersManage | No | No |
| Suppliers | GET | /api/suppliers/list | GetSupplierList | Terminal + session | SuppliersManage | No | No |
| Suppliers | GET | /api/suppliers/{id:guid}/workspace | GetSupplierWorkspace | Terminal + session | SupplierAccountView | No | No |
| Suppliers | POST | /api/suppliers | CreateSupplier | Terminal + session | — | Yes | No |
| Suppliers | PUT | /api/suppliers/{id:guid} | UpdateSupplier | Terminal + session | — | Yes | No |
| System | GET | /api/system/health | Health | Anonymous | — | No | No |
| System | GET | /api/system/ready | Ready | Anonymous | — | No | No |
| System | GET | /api/system/version | Version | Anonymous | — | No | No |
| System | GET | /api/system/operations/{clientOperationId:guid} | GetOperationStatus | Terminal; session optional | — | Yes | Yes |
| Terminals | POST | /api/terminals/register | Register | Anonymous | — | No | No |
| Terminals | POST | /api/terminals/heartbeat | Heartbeat | Terminal | — | No | No |
| Terminals | POST | /api/terminals/status | UpdateStatus | Terminal | — | No | No |
| Terminals | POST | /api/terminals/revalidate | Revalidate | Terminal | — | No | No |
| Thaka | GET | /api/thaka/projects | GetProjects | Terminal + session | ThakaManage | No | No |
| Thaka | GET | /api/thaka/projects/{id:guid} | GetProjectDetail | Terminal + session | ThakaManage | No | No |
| Thaka | GET | /api/thaka/catalog | GetMaterialCatalog | Terminal + session | ThakaManage | No | No |
| Thaka | POST | /api/thaka/projects | CreateProject | Terminal + session | — | Yes | No |
| Thaka | POST | /api/thaka/material-issue | IssueMaterial | Terminal + session | — | Yes | Yes |
| Thaka | POST | /api/thaka/payments | RecordPayment | Terminal + session | — | Yes | Yes |
| Thaka | POST | /api/thaka/settlement | SettleProject | Terminal + session | — | Yes | Yes |
| Thaka | POST | /api/thaka/projects/{id:guid}/reopen | ReopenProject | Terminal + session | — | Yes | No |
| Thaka | POST | /api/thaka/material-reversal | ReverseMaterial | Terminal + session | — | Yes | Yes |
| Thaka | POST | /api/thaka/payment-reversal | ReversePayment | Terminal + session | — | Yes | Yes |
| Users | GET | /api/users | GetUsers | Terminal + session | SettingsManage | Yes | No |
| Users | GET | /api/users/{id:guid} | GetUser | Terminal + session | SettingsManage | Yes | No |
| Users | GET | /api/users/{id:guid}/permissions | GetUserPermissions | Terminal + session | SettingsManage | Yes | No |
| Warranty | GET | /api/warranty/dashboard | GetDashboard | Terminal + session | WarrantyView | No | No |
| Warranty | GET | /api/warranty/claims/{id:guid}/timeline | GetClaimTimeline | Terminal + session | WarrantyView | No | No |
| Warranty | GET | /api/warranty/search | SearchIntake | Terminal + session | WarrantyView | No | No |
| Warranty | POST | /api/warranty/claims | CreateClaim | Terminal + session | — | Yes | No |
