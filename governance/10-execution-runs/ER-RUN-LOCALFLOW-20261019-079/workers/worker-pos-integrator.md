# Native Worker Log: POS Source Integrator

- **Worker ID**: `pos-source-integrator`
- **Runtime ID**: `actor-pos-integrator-079`
- **Role**: POS source code implementation for LF07 remediation
- **Assigned Tasks**: `OWNER019-LOCALPOS-LF07`

## Actions Performed
1. Diagnosed root causal defects for LF07:
   - In `FinanceController.cs`: `OpenCashSession` lacked route-level permission checking (`this.RequirePermission(PermissionKeys.SalesPosUse)`), while `CloseCashSession` enforced it, leaving route authorization asymmetric.
   - In `CashSessionHandlers.cs`: Both `OpenCashSessionHandler.HandleAsync` and `CloseCashSessionHandler.HandleAsync` checked permissions with `if (_authorizer is not null && command.ActorId != Guid.Empty)`. If `command.ActorId == Guid.Empty` was passed, the permission check was bypassed completely.
   - In `CashSessionHandlers.cs`: `CloseCashSessionHandler.HandleAsync` did not validate `command.SessionId == Guid.Empty`, passing it directly to repository query.
2. Executed causal repairs:
   - Added `this.RequirePermission(PermissionKeys.SalesPosUse)` to `OpenCashSession` in `FinanceController.cs` (lines 152-167).
   - Enforced non-empty `ActorId` check under authorizer in `OpenCashSessionHandler.HandleAsync` (lines 40-56), returning canonical code `"auth.actor_required"`.
   - Enforced non-empty `ActorId` check under authorizer in `CloseCashSessionHandler.HandleAsync` (lines 442-462), returning canonical code `"auth.actor_required"`.
   - Added `command.SessionId == Guid.Empty` check in `CloseCashSessionHandler.HandleAsync` (lines 464-469), returning canonical code `"cash.session_id_required"`.
3. Compiled solution across all 12 projects: 0 warnings, 0 errors (`build.log`).
