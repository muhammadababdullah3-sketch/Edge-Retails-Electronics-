# Candidate Drift and Provenance Audit: Run 081 vs Run 080

- **Run ID**: `ER-RUN-LOCALFLOW-20261019-081`
- **Predecessor Baseline**: `ER-RUN-LOCALFLOW-20261019-080`
- **Base Commit**: `3cd3b4b8f903adef6f6d7cf3c922c2ce184c2fc2`
- **Candidate Files**: `363` (`194` tracked, `169` untracked)
- **Identical Files**: `327`
- **Modified Files**: `12`
- **New Files**: `24`

## Intentional Modifications Summary

1. `src/EdgeRetails.Application/Features/Posting/ShopPostingValidationService.cs`:
   - **LF15 Anti-Spoofing Precedence**: Reordered anti-spoofing check before generic supervisor permission evaluation, guaranteeing `authorization.spoofed_supervisor_rejected` is returned when a cashier attempts self-authorization without supervisor permissions.

2. `src/EdgeRetails.Server/Controllers/PostingController.cs`:
   - **LF15 Preflight Parity with Application Authorization**: Synchronized input error codes (`authorization.supervisor_id_required`, `authorization.invalid_supervisor_pin`, `authorization.reason_required`), added authenticated caller anti-spoofing inspection returning `authorization.spoofed_supervisor_rejected`, and aligned permission denial to `authorization.supervisor_permission_denied`.

3. `tests/EdgeRetails.UnitTests/Phase11ShopTimeAndPostingTests.cs`:
   - **LF15 Application Service Behavioral Proof**: Updated `T11_SupervisorAuth_SpoofedActor_Rejected` to verify `authorization.spoofed_supervisor_rejected` and added `T11b_SupervisorAuth_UnprivilegedSupervisor_PermissionDenied` to verify `authorization.supervisor_permission_denied`.

4. `tests/EdgeRetails.UnitTests/Phase11Wave3ApiAndDesktopCutoverTests.cs`:
   - **LF15 Server Controller Behavioral Proof**: Added 5 dedicated named tests:
     - `LF15_PostingController_InputValidation_ReturnsExpectedDomainErrorCodes`
     - `LF15_PostingController_PreflightValidation_ParityWithApplicationAuthorization`
     - `LF15_PostingController_AntiSpoofing_RejectsSelfAuthorizingCashierWith403`
     - `LF15_PostingController_UnprivilegedSupervisor_Returns403PermissionDenied`
     - `LF15_PostingController_AuthorizedSupervisor_Returns200WithCryptographicToken`

## Preserved Source Modules
All other components from Run 080 (including LF02, LF03, LF04, LF05, LF06, LF07, LF08, LF09, LF10, LF11, LF12) are 100% preserved bit-for-bit without change.
