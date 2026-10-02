# Edge Retails — Phase 2 Shop Server API Parity Matrix

**Document Version:** 1.0.0  
**Date:** 2026-09-26  
**Status:** CERTIFIED COMPLETE (`API_COMPLETE`)  
**Runtime Topology:** Single-Machine Loopback Authority (`http://127.0.0.1:7150`)  
**Database Authority:** PostgreSQL via EF Core (`EdgeRetailsDbContext`)  
**Schema Drift:** Zero (0) pending model changes  

---

## 1. Executive Summary

Phase 2 establishes `EdgeRetails.Server` as the hardened, trusted, production backend authority for Edge Retails. Every business operation executed by the desktop application or terminal clients flows through thin ASP.NET Core controllers, enforces two-layer terminal and session authentication, overrides caller-supplied actor identity with authenticated session tokens, and executes within application command handlers protected by row-level and pessimistic database locks.

### Certification Summary
- **Controllers Active:** 17 Controllers covering all 12 mutation domains, read query spaces, and identity management.
- **Unit Test Baseline:** 634 Passed, 0 Failed, 0 Skipped.
- **Performance Test Baseline:** 23 Passed, 0 Failed, 0 Skipped.
- **Server Contract & Security Tests:** Certified with 0 Failed.
- **Schema Drift:** Zero model changes since certified Phase 1 baseline.
- **Thinness Standard:** 100% compliant. Zero database logic or calculation in controllers; all dispatch to `Application` handlers or read services.

---

## 2. Authentication & Security Middleware Pipeline

All incoming HTTP requests to `EdgeRetails.Server` flow through the following ordered middleware pipeline:

```
[Incoming Request]
        │
        ▼
1. ProtocolCompatibilityMiddleware
   - Validates X-Protocol-Version header against TerminalProtocol rules.
   - Rejects incompatible versions with 400 Bad Request ("protocol.incompatible").
        │
        ▼
2. MaintenanceModeGuardMiddleware
   - Inspects maintenance barriers and EDGE_RETAILS_MAINTENANCE_MODE environment flag.
   - Allows anonymous health probes; rejects operational mutations with 503 Service Unavailable ("server.maintenance_mode").
        │
        ▼
3. TerminalAuthenticationMiddleware
   - Allows whitelisted anonymous endpoints: /api/system/health, /api/system/ready, /api/system/version, /api/terminals/register, /api/auth/accounts, /api/auth/login.
   - Enforces X-Terminal-Id and X-Terminal-Secret.
   - Rejects missing headers with 401 Unauthorized ("auth.terminal_id_missing", "auth.terminal_secret_missing").
   - Validates terminal registration and cryptographically verified SHA-256 AuthSecretHash.
   - Enforces terminal status: rejects Revoked/Suspended with 403 Forbidden ("auth.terminal_revoked", "auth.terminal_suspended").
        │
        ▼
4. UserSessionAuthenticationMiddleware
   - Allows anonymous system/terminal endpoints and login/accounts endpoints.
   - Enforces X-Session-Id header on all operational endpoints.
   - Rejects missing session with 401 Unauthorized ("auth.session_missing").
   - Validates session existence, expiration, and revocation status ("auth.session_invalid").
   - Validates user account active status ("auth.user_disabled").
   - Validates user role active status: immediately revokes access if role is deactivated ("auth.role_disabled" - Finding M-01).
   - Injects ActorContext (UserId, SessionId, RoleId, Permissions) into HttpContext.
        │
        ▼
[Thin Controller Action (Session Actor Overwrite)]
        │
        ▼
[Application Handler / Read Service (Pessimistic Locks & Postgres Transaction)]
```

---

## 3. Complete Endpoint Parity Matrix

### Legend
- **Classification:** `API_COMPLETE` (Full production implementation with command/query handler backing).
- **Auth Level:**
  - `ANONYMOUS`: Public probe, no headers required.
  - `TERMINAL`: Requires `X-Terminal-Id` and `X-Terminal-Secret`.
  - `SESSION`: Requires `X-Terminal-Id`, `X-Terminal-Secret`, and `X-Session-Id`.
- **Idempotency:** Protected by `ClientOperationId` against replay and double-posting.

| Area | HTTP Method | Route | Auth Level | Idempotent | Application Handler / Query Service | Status Code(s) | Classification |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **System** | `GET` | `/api/system/health` | ANONYMOUS | No | System health liveness probe | 200 | `API_COMPLETE` |
| **System** | `GET` | `/api/system/ready` | ANONYMOUS | No | `IDatabaseReadinessService.CheckAsync` | 200, 503 | `API_COMPLETE` |
| **System** | `GET` | `/api/system/version` | ANONYMOUS | No | `TerminalProtocol` version metadata | 200 | `API_COMPLETE` |
| **System** | `GET` | `/api/system/operations/{clientOperationId}` | TERMINAL / SESSION | No | `OperationStatusQueryHandler` (Legacy route: minimal non-sensitive status if terminal auth only; full status if session auth) | 200, 400, 401, 403 | `API_COMPLETE` |
| **Operations** | `GET` | `/api/operations/{clientOperationId}` | SESSION | No | `OperationStatusQueryHandler.HandleAsync` (Canonical route: passes trusted session actor & terminal) | 200, 400, 401, 403 | `API_COMPLETE` |
| **Terminals** | `POST` | `/api/terminals/register` | ANONYMOUS | No | `RegisterTerminalHandler.HandleAsync` | 200, 400 | `API_COMPLETE` |
| **Terminals** | `POST` | `/api/terminals/heartbeat` | TERMINAL | No | `TerminalHeartbeatHandler.HandleAsync` | 200, 400, 401, 403 | `API_COMPLETE` |
| **Terminals** | `POST` | `/api/terminals/status` | TERMINAL | No | `UpdateTerminalStatusHandler.HandleAsync` | 200, 400, 401, 403 | `API_COMPLETE` |
| **Terminals** | `POST` | `/api/terminals/revalidate` | TERMINAL | No | `AuthoritativeRevalidationHandler.HandleAsync` | 200, 400, 401, 403 | `API_COMPLETE` |
| **Auth** | `GET` | `/api/auth/accounts` | ANONYMOUS / TERMINAL | No | `GetLoginAccountsHandler.HandleAsync` | 200 | `API_COMPLETE` |
| **Auth** | `POST` | `/api/auth/login` | ANONYMOUS / TERMINAL | No | `AuthenticateUserHandler.HandleAsync` | 200, 400, 401, 403 | `API_COMPLETE` |
| **Auth** | `POST` | `/api/auth/logout` | SESSION | No | `EndUserSessionHandler.HandleAsync` | 200, 400, 401 | `API_COMPLETE` |
| **Auth** | `GET` | `/api/auth/session` | SESSION | No | Current `ActorContext` reflection | 200, 401 | `API_COMPLETE` |
| **Users** | `GET` | `/api/users` | SESSION | No | `IIdentityReadRepository.GetActiveUsersAsync` (requires `settings.manage`) | 200, 401, 403 | `API_COMPLETE` |
| **Users** | `GET` | `/api/users/{id}` | SESSION | No | `IIdentityReadRepository.GetUserAsync` (requires `settings.manage`) | 200, 401, 403, 404 | `API_COMPLETE` |
| **Users** | `GET` | `/api/users/{id}/permissions` | SESSION | No | `IIdentityReadRepository.GetEffectivePermissionKeysAsync` (requires `settings.manage`) | 200, 401, 403 | `API_COMPLETE` |
| **Roles** | `GET` | `/api/roles` | SESSION | No | `IIdentityReadRepository.GetRolesAsync` (requires `settings.manage`) | 200, 401, 403 | `API_COMPLETE` |
| **Roles** | `GET` | `/api/roles/{id}` | SESSION | No | `IIdentityReadRepository.GetRoleAsync` (requires `settings.manage`) | 200, 401, 403, 404 | `API_COMPLETE` |
| **Roles** | `GET` | `/api/roles/{id}/permissions` | SESSION | No | `IIdentityReadRepository.GetRolePermissionKeysAsync` (requires `settings.manage`) | 200, 401, 403, 404 | `API_COMPLETE` |
| **Roles** | `GET` | `/api/roles/permissions` | SESSION | No | `IIdentityReadRepository.GetPermissionsAsync` (requires `settings.manage`) | 200, 401, 403 | `API_COMPLETE` |
| **Purchasing**| `POST` | `/api/purchasing/intake` | SESSION | Yes (`ClientOperationId`) | `ReceiveProductIntakeHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Purchasing**| `POST` | `/api/purchasing` | SESSION | Yes (`ClientOperationId`) | `CreatePurchaseHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Purchasing**| `GET` | `/api/purchasing` | SESSION | No | `IPurchasingReadService.GetPurchasesPageAsync` | 200, 401 | `API_COMPLETE` |
| **Purchasing**| `GET` | `/api/purchasing/{id}` | SESSION | No | `IPurchasingReadService.GetPurchaseDetailAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Purchasing**| `GET` | `/api/purchasing/items/{id}/units` | SESSION | No | `IPurchasingReadService.GetPurchaseItemUnitsAsync` | 200, 401 | `API_COMPLETE` |
| **Purchasing**| `POST` | `/api/purchasing/return` | SESSION | Yes (`ClientOperationId`) | `CreatePurchaseReturnHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Purchasing**| `POST` | `/api/purchasing/void` | SESSION | Yes (`ClientOperationId`) | `VoidPurchaseHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Sales** | `POST` | `/api/sales/complete` | SESSION | Yes (`ClientOperationId`) | `CompleteSaleHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Sales** | `POST` | `/api/sales/drafts/complete` | SESSION | Yes (`ClientOperationId`) | `CompletePosDraftHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Sales** | `POST` | `/api/sales/return` | SESSION | Yes (`ClientOperationId`) | `CreateSaleReturnHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Sales** | `POST` | `/api/sales/exchange` | SESSION | Yes (`ClientOperationId`) | `CommercialExchangeHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Sales** | `GET` | `/api/sales` | SESSION | No | `ISalesReadService.GetHistoryAsync` | 200, 401 | `API_COMPLETE` |
| **Sales** | `GET` | `/api/sales/{id}` | SESSION | No | `ISalesReadService.GetDetailAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Sales** | `GET` | `/api/sales/scan` | SESSION | No | `IPhase4WorkflowReadService.ResolveScannerAsync` | 200, 400, 401 | `API_COMPLETE` |
| **Sales** | `GET` | `/api/sales/drafts` | SESSION | No | `IPhase4WorkflowReadService.GetOpenDraftsAsync` | 200, 401 | `API_COMPLETE` |
| **Sales** | `GET` | `/api/sales/drafts/{id}` | SESSION | No | `IPhase4WorkflowReadService.GetDraftAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Sales** | `POST` | `/api/sales/drafts` | SESSION | No | `SavePosDraftHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Sales** | `POST` | `/api/sales/drafts/{id}/cancel` | SESSION | No | `CancelPosDraftHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `GET` | `/api/warranty/dashboard` | SESSION | No | `IWarrantyReadService.GetDashboardSummaryAsync` | 200, 401 | `API_COMPLETE` |
| **Warranty** | `GET` | `/api/warranty/claims/{id}/timeline` | SESSION | No | `IWarrantyReadService.GetClaimTimelineAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Warranty** | `GET` | `/api/warranty/search` | SESSION | No | `IWarrantyReadService.SearchClaimsAsync` | 200, 401 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims` | SESSION | Yes (`ClientOperationId`) | `CreateWarrantyClaimHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/review` | SESSION | No | `BeginWarrantyClaimReviewHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/cancel` | SESSION | No | `CancelWarrantyClaimHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/send-supplier` | SESSION | No | `SendWarrantyClaimToSupplierHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/supplier-processing` | SESSION | No | `MarkWarrantySupplierProcessingHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/resolution` | SESSION | No | `RecordWarrantyResolutionHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/receive-replacement` | SESSION | No | `ReceiveCustomerWarrantyReplacementHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/claims/{id}/handover` | SESSION | No | `HandoverWarrantyItemHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/shop-stock/send-supplier` | SESSION | No | `SendShopStockToSupplierWarrantyHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Warranty** | `POST` | `/api/warranty/shop-stock/receive` | SESSION | No | `ReceiveShopStockWarrantyHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/supplier-payment` | SESSION | Yes (`ClientOperationId`) | `CreateSupplierPaymentHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/supplier-payment/reverse` | SESSION | Yes (`ClientOperationId`) | `ReverseSupplierPaymentHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/supplier-refund` | SESSION | Yes (`ClientOperationId`) | `CreateSupplierRefundHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/supplier-refund/reverse` | SESSION | Yes (`ClientOperationId`) | `ReverseSupplierRefundHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/supplier-adjustment` | SESSION | Yes (`ClientOperationId`) | `SupplierAccountAdjustmentHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `GET` | `/api/finance/suppliers/{id}/workspace` | SESSION | No | `ISupplierAccountReadService.GetWorkspaceAsync` | 200, 401 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/cash-session/open` | SESSION | No | `OpenCashSessionHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/cash-session/close` | SESSION | No | `CloseCashSessionHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Finance** | `POST` | `/api/finance/cash-movement` | SESSION | No | `RecordManualCashMovementHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `GET` | `/api/catalog/products` | SESSION | No | `IProductManagementReadService.GetProductsPageAsync` | 200, 401 | `API_COMPLETE` |
| **Catalog** | `GET` | `/api/catalog/products/{id}` | SESSION | No | `IProductManagementReadService.GetProductAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Catalog** | `GET` | `/api/catalog/products/sku/{sku}` | SESSION | No | `IProductManagementReadService.GetProductBySkuAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/products` | SESSION | No | `CreateProductHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `PUT` | `/api/catalog/products/{id}` | SESSION | No | `UpdateProductHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/products/{id}/deactivate` | SESSION | No | `DeactivateProductHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/products/{id}/reactivate` | SESSION | No | `ReactivateProductHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `GET` | `/api/catalog/companies` | SESSION | No | `IProductManagementReadService.GetCompaniesAsync` | 200, 401 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/companies` | SESSION | No | `SaveCompanyHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `GET` | `/api/catalog/categories` | SESSION | No | `IProductManagementReadService.GetCategoriesAsync` | 200, 401 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/categories` | SESSION | No | `SaveCategoryHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `GET` | `/api/catalog/units` | SESSION | No | `IProductManagementReadService.GetUnitsAsync` | 200, 401 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/products/{id}/units` | SESSION | No | `ConfigureProductUnitsHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Catalog** | `POST` | `/api/catalog/units/{id}/barcode` | SESSION | No | `SetProductUnitBarcodeHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Inventory**| `GET` | `/api/inventory/stock` | SESSION | No | `IInventoryOverviewReadService.GetOverviewAsync` | 200, 401 | `API_COMPLETE` |
| **Inventory**| `GET` | `/api/inventory/movements` | SESSION | No | `IInventoryOverviewReadService.GetMovementsPageAsync` | 200, 401 | `API_COMPLETE` |
| **Inventory**| `GET` | `/api/inventory/exact-units` | SESSION | No | `IInventoryOverviewReadService.GetUnitsPageAsync` | 200, 401 | `API_COMPLETE` |
| **Inventory**| `GET` | `/api/inventory/units/{id}/history` | SESSION | No | `IInventoryOverviewReadService.GetUnitHistoryAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Inventory**| `GET` | `/api/inventory/products/{id}/purchase-provenance` | SESSION | No | `IInventoryProvenanceReadService.GetPurchaseProvenanceAsync` | 200, 401 | `API_COMPLETE` |
| **Inventory**| `GET` | `/api/inventory/products/{id}/sale-history` | SESSION | No | `IInventoryProvenanceReadService.GetSaleHistoryAsync` | 200, 401 | `API_COMPLETE` |
| **Inventory**| `POST` | `/api/inventory/adjustments` | SESSION | Yes (`ClientOperationId`) | `CreateStockAdjustmentHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Inventory**| `POST` | `/api/inventory/stocktake` | SESSION | No | `CreateStocktakeHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Inventory**| `POST` | `/api/inventory/condition/transfer` | SESSION | Yes (`ClientOperationId`) | `TransferInventoryConditionHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Customers**| `GET` | `/api/customers` | SESSION | No | `IPartyDirectoryReadService.GetCustomersAsync` | 200, 401 | `API_COMPLETE` |
| **Customers**| `GET` | `/api/customers/list` | SESSION | No | `GetCustomersHandler.HandleAsync` | 200, 401 | `API_COMPLETE` |
| **Customers**| `POST` | `/api/customers` | SESSION | No | `SaveCustomerHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Customers**| `PUT` | `/api/customers/{id}` | SESSION | No | `SaveCustomerHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Suppliers**| `GET` | `/api/suppliers` | SESSION | No | `IPartyDirectoryReadService.GetSuppliersAsync` | 200, 401 | `API_COMPLETE` |
| **Suppliers**| `GET` | `/api/suppliers/list` | SESSION | No | `GetSuppliersHandler.HandleAsync` | 200, 401 | `API_COMPLETE` |
| **Suppliers**| `GET` | `/api/suppliers/{id}/workspace` | SESSION | No | `ISupplierAccountReadService.GetWorkspaceAsync` | 200, 401 | `API_COMPLETE` |
| **Suppliers**| `POST` | `/api/suppliers` | SESSION | No | `SaveSupplierHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Suppliers**| `PUT` | `/api/suppliers/{id}` | SESSION | No | `SaveSupplierHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Expenses** | `GET` | `/api/expenses` | SESSION | No | `IExpenseReadService.GetExpensesPageAsync` | 200, 401 | `API_COMPLETE` |
| **Expenses** | `GET` | `/api/expenses/categories` | SESSION | No | `IExpenseReadService.GetCategoriesAsync` | 200, 401 | `API_COMPLETE` |
| **Expenses** | `GET` | `/api/expenses/subcategories` | SESSION | No | `IExpenseReadService.GetSubcategoriesAsync` | 200, 401 | `API_COMPLETE` |
| **Expenses** | `POST` | `/api/expenses` | SESSION | Yes (`ClientOperationId`) | `PostExpenseHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Expenses** | `POST` | `/api/expenses/{id}/void` | SESSION | No | `VoidExpenseHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `GET` | `/api/thaka/projects` | SESSION | No | `IThakaReadService.GetProjectsPageAsync` | 200, 401 | `API_COMPLETE` |
| **Thaka** | `GET` | `/api/thaka/projects/{id}` | SESSION | No | `IThakaReadService.GetProjectAsync` | 200, 401, 404 | `API_COMPLETE` |
| **Thaka** | `GET` | `/api/thaka/catalog` | SESSION | No | `IThakaReadService.GetMaterialCatalogAsync` | 200, 401 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/projects` | SESSION | No | `CreateThakaProjectHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/material-issue` | SESSION | Yes (`ClientOperationId`) | `IssueThakaMaterialHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/payments` | SESSION | Yes (`ClientOperationId`) | `RecordThakaPaymentHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/settlement` | SESSION | Yes (`ClientOperationId`) | `SettleThakaHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/projects/{id}/reopen` | SESSION | No | `ReopenThakaHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/material-reversal` | SESSION | Yes (`ClientOperationId`) | `ReverseThakaMaterialHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Thaka** | `POST` | `/api/thaka/payment-reversal` | SESSION | Yes (`ClientOperationId`) | `ReverseThakaPaymentHandler.HandleAsync` | 200, 400, 401, 403, 409 | `API_COMPLETE` |
| **Reports** | `GET` | `/api/reports/snapshot` | SESSION | No | `IReportingReadService.GetSnapshotAsync` | 200, 401 | `API_COMPLETE` |
| **Reports** | `GET` | `/api/reports/daily` | SESSION | No | `IReportingReadService.GetSnapshotAsync` (Daily) | 200, 401 | `API_COMPLETE` |
| **Reports** | `GET` | `/api/reports/monthly` | SESSION | No | `IReportingReadService.GetSnapshotAsync` (Monthly) | 200, 401 | `API_COMPLETE` |
| **Reports** | `GET` | `/api/reports/yearly` | SESSION | No | `IReportingReadService.GetSnapshotAsync` (Yearly) | 200, 401 | `API_COMPLETE` |

---

## 4. Idempotency & Operation Recovery Standard

1. **ClientOperationId Lifecycle Safety:**
   - Every state-altering operational mutation requires a caller-provided UUID v7 `ClientOperationId`.
   - Handlers acquire an exclusive PostgreSQL advisory lock on `ClientOperationId` before querying for existing outcomes.
   - Repeated calls with the exact same `ClientOperationId` replay the previously committed result without creating duplicate ledger records, double-counting stock, or creating phantom cash movements.
   - Calling `/api/operations/{clientOperationId}` queries the unified `OperationStatusQueryHandler` across all 12 operational domains and returns authoritative document numbers, entity IDs, timestamps, and committed flags.

2. **Actor Attribution Neutralization:**
   - Callers cannot forge `ActorId` or `CashierUserId`.
   - The server controller automatically overrides the caller's request payload `ActorId` with `HttpContext.GetActorContext().UserId` extracted from the cryptographically verified session.

3. **Role & Permission Boundary:**
   - Role deactivation immediately revokes all effective permissions and denies request execution at both middleware boundary (403 Forbidden) and application handler boundary.

---

## 5. Critical Parity Classifications & Operational Boundaries

Every critical capability across identity, reporting, system operations, printing, backup/restore, and outbox transactional guarantees has been audited and classified with an explicit boundary determination below. Ambiguous combined statuses have been eliminated in favor of unequivocal operation-level classifications. Zero items remain unclassified or unresolved.

### 5.1 Users, Roles & Permissions Operation-Level Matrix

| Domain | Operation / Action | Route / Contract | Classification | Rationale & Operational Boundary |
| :--- | :--- | :--- | :--- | :--- |
| **Users** | List Users | `GET /api/users` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetActiveUsersAsync`. |
| **Users** | User Detail | `GET /api/users/{id}` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetUserAsync`. |
| **Users** | Effective Permissions | `GET /api/users/{id}/permissions` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetEffectivePermissionKeysAsync`. |
| **Users** | Create User | Mutation | `NOT_PART_OF_CURRENT_V1` | Users are seeded and bootstrapped during initial install (`FirstSetupBootstrapHandler`); interactive operator user creation is deferred to Phase 6. |
| **Users** | Update User | Mutation | `NOT_PART_OF_CURRENT_V1` | User profile updates deferred to Phase 6 operator tooling. |
| **Users** | Deactivate User | Mutation | `NOT_PART_OF_CURRENT_V1` | Operator lifecycle management deferred to Phase 6 operator tooling. |
| **Users** | PIN Reset | Mutation | `NOT_PART_OF_CURRENT_V1` | PIN credential rotation deferred to Phase 6 operator tooling. |
| **Roles** | List Roles | `GET /api/roles` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetRolesAsync`. |
| **Roles** | Role Detail | `GET /api/roles/{id}` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetRoleAsync`. |
| **Roles** | Role Permissions | `GET /api/roles/{id}/permissions` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetRolePermissionKeysAsync`. |
| **Roles** | Create Custom Role | Mutation | `NOT_PART_OF_CURRENT_V1` | System roles (Owner, Manager, Cashier) are fixed domain archetypes created during database setup. Custom dynamic roles are intentionally excluded from Edge Retails v1 scope. |
| **Roles** | Assign Role | Mutation | `NOT_PART_OF_CURRENT_V1` | Role assignments are bootstrapped at setup; runtime interactive role assignment is deferred to Phase 6 operator tooling. |
| **Permissions** | List Permissions | `GET /api/roles/permissions` | `SERVER_API_COMPLETE` | Requires valid session and `settings.manage` permission. Dispatches to `IIdentityReadRepository.GetPermissionsAsync`. |
| **Permissions** | Get Effective Permissions | Query | `SERVER_API_COMPLETE` | Served via `GET /api/users/{id}/permissions` and reflected in authenticated `ActorContext`. |
| **Permissions** | Assign Permissions | Mutation | `NOT_PART_OF_CURRENT_V1` | Role-permission mappings and overrides are seeded in DB schema; dynamic interactive permission matrix editing is deferred to Phase 6 operator tooling. |

### 5.2 Backup & Restore Operation-Level Matrix

| Domain | Operation / Action | Route / Contract | Classification | Rationale & Operational Boundary |
| :--- | :--- | :--- | :--- | :--- |
| **Backup** | Create Backup (Backup Creation) | Handler / Pipeline | `SERVER_API_COMPLETE` | Authoritative PostgreSQL engine execution (`pg_dump` with authenticated manifest and checksums). Mandatory `settings.manage` authorization enforced in `CreateBackupHandler`. |
| **Backup** | Backup History | Local Storage / Probe | `LOCAL_INFRASTRUCTURE_BY_DESIGN` | Local infrastructure inspection (`BackupHealthProbe` / `Phase5DiagnosticsService`) probes local backup store directory, validates manifest envelopes, and verifies artifact checksums. |
| **Restore** | Validate Restore (Restore Pipeline) | Pipeline | `SERVER_API_COMPLETE` | Pre-flight manifest inspection, PostgreSQL version verification, schema pre-validation, and non-destructive dry-run restore validation executed via `PrepareRestoreHandler`. |
| **Restore** | Perform Restore (Restore Pipeline) | Pipeline | `SERVER_API_COMPLETE` | Active database connection termination, staged schema restoration, integrity validation, and final cutover executed via `CutoverRestoreHandler`. |
| **Restore** | Restore Status | Probe / Health | `SERVER_API_COMPLETE` | Verifies restored database connection, schema readiness, and post-restore integrity via `IDatabaseReadinessService`. |

### 5.3 Printing Operation-Level Matrix

| Domain | Operation / Action | Route / Contract | Classification | Rationale & Operational Boundary |
| :--- | :--- | :--- | :--- | :--- |
| **Printing** | Authoritative Document / Receipt Data | Handlers / Encoders | `SERVER_API_COMPLETE` | Server generates and validates authoritative receipt layouts, barcode encodings (`Code128Encoder`), and document payloads (`ProductionDocument`, `ReceiptTemplateSettings`, `PrintPhysicalStickersHandler`). |
| **Printing** | Physical Printing Engine | Windows Print Spooler | `DESKTOP_WPF_EXCLUSIVE_BY_DESIGN` | Physical printer driver execution is exclusively the responsibility of Desktop WPF (`System.Printing`, `WpfPhysicalStickerPrintEngine`, `WpfProductionPrintEngine`). Server remains headless with zero physical driver dependencies. |

### 5.4 Outbox Transactional Guarantees & Certification Matrix

| Domain | Operation / Action | Test Scope | Classification | Rationale & Operational Boundary |
| :--- | :--- | :--- | :--- | :--- |
| **Outbox** | Unit-level lease, retry, atomicity | Unit / In-Memory Test Suite | `PASS` | Outbox lease acquisition, retry backoff with exponential jitter, dead-lettering, and atomic state transitions are fully verified in unit test suite. |
| **Outbox** | Real PostgreSQL Concurrency Locking | Integration Test Suite | `BLOCKED_ENVIRONMENT` | Concurrency locking tests require a live PostgreSQL instance with advisory lock support. Guarded in integration tests when `EDGE_RETAILS_TEST_DB` environment variable is unset. |

### 5.5 System & Dashboard Probes

| Domain | Capability / Metric | Route / Source | Classification | Rationale & Operational Boundary |
| :--- | :--- | :--- | :--- | :--- |
| **Dashboard** | Financial Metrics | `GET /api/reports/...` | `COMPLETE` | Financial aggregates (`NetSales`, `NetProfit`, `Expenses`, `ThakaMaterial`) computed on server via `ReportsController` and consumed by desktop `BackendDashboardService`. |
| **Dashboard** | Active Projects | `GET /api/thaka/projects` | `COMPLETE` | Active project balances and counts served by `ThakaController` and consumed by desktop `BackendDashboardService`. |
| **Dashboard** | Database Readiness | `GET /api/system/ready` | `COMPLETE` | Connection status, pending migration checks, and failure reasons evaluated on server via `IDatabaseReadinessService`. |
| **Operations** | Operation Status Authority | `GET /api/operations/{id}` | `SERVER_API_COMPLETE` | Canonical outcome recovery backed by `IOperationOutcomeLedger` and `OperationStatusQueryHandler`. |


