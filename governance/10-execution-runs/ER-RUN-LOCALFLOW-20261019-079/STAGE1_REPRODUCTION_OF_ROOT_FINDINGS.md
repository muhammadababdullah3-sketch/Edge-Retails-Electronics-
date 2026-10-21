# STAGE 1: Reproduction of Root Findings and LF07 Causal Remediation

**Run ID:** `ER-RUN-LOCALFLOW-20261019-079`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`
**Predecessor Review:** `governance/10-execution-runs/ER-RUN-LOCALFLOW-20261019-078/STAGE1_REPRODUCTION_OF_ROOT_FINDINGS.md`
**Timestamp UTC:** `2026-10-19T12:00:00Z`

---

## 1. Executive Summary & LF07 Remediation

In Run 078, canonical finding **LF07** (*Cash close lacks action permission*) was categorized as `CLOSED_IN_CANDIDATE` based on partial checks, but lacked full symmetric route-level guard validation, strict authorizer empty-actor prevention, and dedicated named behavioral unit test proof.

In Run 079, the native POS Integrator and Test Coordinator executed full causal remediation and direct behavioral verification:

### LF07 Root Causal Defects
1. **Asymmetric Route Permission Guard**:
   - Location: `src/EdgeRetails.Server/Controllers/FinanceController.cs:152-167`
   - Defect: `OpenCashSession` lacked route-level permission checking (`this.RequirePermission(PermissionKeys.SalesPosUse)`), whereas `CloseCashSession` enforced it, leaving route authorization asymmetric and allowing anonymous access to open session commands.
2. **Empty Actor Permission Bypass**:
   - Location: `src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs:40-56, 442-462`
   - Defect: Both `OpenCashSessionHandler` and `CloseCashSessionHandler` checked permissions using `if (_authorizer is not null && command.ActorId != Guid.Empty)`. If an anonymous or empty GUID (`command.ActorId == Guid.Empty`) was submitted, the authorizer check was completely bypassed.
3. **Missing Session ID Validation**:
   - Location: `src/EdgeRetails.Application/Features/Finance/CashSessionHandlers.cs:464-469`
   - Defect: `CloseCashSessionHandler.HandleAsync` did not validate `command.SessionId == Guid.Empty`, passing it directly to repository query.

### Causal Source Repairs
1. **OpenCashSession Route Permission Guard**:
   ```csharp
   [HttpPost("cash-session/open")]
   public async Task<IActionResult> OpenCashSession(
       [FromBody] OpenCashSessionCommand command,
       CancellationToken cancellationToken)
   {
       var denied = this.RequirePermission(PermissionKeys.SalesPosUse);
       if (denied is not null)
       {
           return denied;
       }

       var actor = HttpContext.GetActorContext();
       var sanitized = actor is not null ? command with {{ ActorId = actor.UserId }} : command;
       var result = await _openCashSessionHandler.HandleAsync(sanitized, cancellationToken);
       return ToActionResult(result);
   }
   ```
2. **OpenCashSessionHandler and CloseCashSessionHandler Authorizer & Actor Guards**:
   ```csharp
   if (_authorizer is not null)
   {
       if (command.ActorId == Guid.Empty)
       {
           return Result<Guid>.Failure(
               "auth.actor_required",
               "Actor id is required to open a cash session.");
       }

       var auth = await _authorizer.AuthorizeAsync(
           command.ActorId,
           PermissionKeys.SalesPosUse,
           ct);
       if (!auth.IsSuccess)
       {
           return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
       }
   }
   ```
3. **CloseCashSessionHandler Session ID Fast-Fail**:
   ```csharp
   if (command.SessionId == Guid.Empty)
   {
       return Result<CashClosingResult>.Failure(
           "cash.session_id_required",
           "Cash session id is required.");
   }
   ```

### Dedicated Named Behavior Test Proof
Added 5 dedicated named unit tests in `tests/EdgeRetails.UnitTests/Phase2StocktakeThakaCashSessionBehavioralTests.cs` (lines 370-450):
1. `LF07_OpenCashSession_WithoutPosPermission_RejectedWithUnauthorized` [PASSED]
2. `LF07_OpenCashSession_WithEmptyActor_WhenAuthorizerConfigured_RejectedWithActorRequired` [PASSED]
3. `LF07_CloseCashSession_WithoutPosPermission_RejectedWithUnauthorized` [PASSED]
4. `LF07_CloseCashSession_WithEmptyActor_WhenAuthorizerConfigured_RejectedWithActorRequired` [PASSED]
5. `LF07_CloseCashSession_WithEmptySessionId_RejectedWithSessionIdRequired` [PASSED]
