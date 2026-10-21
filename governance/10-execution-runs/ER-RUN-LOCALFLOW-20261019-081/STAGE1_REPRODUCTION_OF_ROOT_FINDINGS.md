# STAGE 1: Reproduction of Root Findings and LF15 Causal Remediation

**Run ID:** `ER-RUN-LOCALFLOW-20261019-081`
**Authority:** `OWNER-UPDATE-20261018-019` / `OWNER-UPDATE-20261019-001` / `OWNER-UPDATE-20261019-007`
**Predecessor Review:** `governance/10-execution-runs/ER-RUN-LOCALFLOW-20261019-080/STAGE1_REPRODUCTION_OF_ROOT_FINDINGS.md`
**Timestamp UTC:** `2026-10-19T14:00:00Z`

---

## 1. Executive Summary & LF15 Remediation

In Run 080, canonical finding **LF15** (*Supervisor posting preflight parity with application authorization*) was categorized as `REMAINS_OPEN`. While `PostingController.RequestSupervisorApproval` provided basic validation, it suffered from:
1. Discrepant input validation error codes between the HTTP API and the application service.
2. Inverted evaluation order in `ShopPostingValidationService.cs` where the generic supervisor permission check preceded the anti-spoofing check, leaving `authorization.spoofed_supervisor_rejected` unreachable.
3. Lack of caller identity anti-spoofing enforcement in `PostingController.cs` via `HttpContext.GetActorContext()`.

In Run 081, the native POS Integrator and Test Coordinator executed full causal remediation and direct behavioral verification:

### LF15 Root Causal Defects
1. **Unreachable Anti-Spoofing Code in Domain Service**:
   - Location: `src/EdgeRetails.Application/Features/Posting/ShopPostingValidationService.cs:141-160`
   - Defect: Generic supervisor permission check evaluated first, returning `authorization.supervisor_permission_denied` before anti-spoofing could return `authorization.spoofed_supervisor_rejected`.
2. **Error Code Discrepancies**:
   - Location: `src/EdgeRetails.Server/Controllers/PostingController.cs`
   - Defect: Controller returned ad-hoc codes (e.g. `authorization.supervisor_permission_required`) instead of canonical application domain error codes (`authorization.supervisor_permission_denied`).
3. **Missing Preflight Caller Anti-Spoofing Check**:
   - Location: `src/EdgeRetails.Server/Controllers/PostingController.cs`
   - Defect: HTTP endpoint verified supervisor PIN and permissions but did not verify whether authenticated caller supplied their own user ID as supervisor without being a supervisor.

### Causal Source Repairs
1. **ShopPostingValidationService.cs**:
   - Reordered anti-spoofing evaluation before generic supervisor permission checks.
2. **PostingController.cs**:
   - Aligned input validation codes (`authorization.supervisor_id_required`, `authorization.invalid_supervisor_pin`, `authorization.reason_required`, `authorization.supervisor_permission_denied`).
   - Added caller context anti-spoofing inspection via `HttpContext.GetActorContext()`, returning 403 Forbidden with `authorization.spoofed_supervisor_rejected`.
3. **Behavioral Test Verification**:
   - Updated `Phase11ShopTimeAndPostingTests.cs`: verified `T11_SupervisorAuth_SpoofedActor_Rejected` and added `T11b_SupervisorAuth_UnprivilegedSupervisor_PermissionDenied`.
   - Added 5 dedicated named tests in `Phase11Wave3ApiAndDesktopCutoverTests.cs` proving full preflight parity.
