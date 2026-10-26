using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
public class OperationsController : ControllerBase
{
    private readonly IOperationLedgerService _operationLedger;
    private readonly IAdminAuthService _authService;

    public OperationsController(
        IOperationLedgerService operationLedger,
        IAdminAuthService authService)
    {
        _operationLedger = operationLedger ?? throw new ArgumentNullException(nameof(operationLedger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpGet("api/admin/v1/operations/{id:guid}")]
    public async Task<IActionResult> GetAdminOperationStatus([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var credResult = CentralSessionCredentials.Parse(Request);
        if (credResult.ErrorCode == "SESSION_CREDENTIAL_CONFLICT")
        {
            return BadRequest(new { code = "SESSION_CREDENTIAL_CONFLICT", safeMessageKey = "security.session_credential_conflict" });
        }

        if (!credResult.IsPresent || credResult.SessionId == Guid.Empty)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        if (credResult.ErrorCode == "SESSION_CREDENTIAL_INVALID")
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        var session = await _authService.ValidateSessionAsync(credResult.SessionId, cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        // Admin lookup is strictly scoped to the authenticated admin actor
        var outcome = await _operationLedger.GetOutcomeAsync(id, $"admin:{session.AdminUserId:D}", cancellationToken)
            ?? await _operationLedger.GetOutcomeAsync(id, $"admin:{session.AdminUserId:N}", cancellationToken)
            ?? await _operationLedger.GetOutcomeAsync(id, $"admin:{session.SessionId:D}", cancellationToken)
            ?? await _operationLedger.GetOutcomeAsync(id, $"admin:{session.SessionId:N}", cancellationToken);

        if (outcome is null)
        {
            return NotFound(new { code = "OPERATION_NOT_FOUND", safeMessageKey = "operation.not_found" });
        }

        return Ok(outcome);
    }

    [HttpGet("api/licensing/v1/operations/{id:guid}")]
    public async Task<IActionResult> GetLicensingOperationStatus([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation installation || installation.Status != Installation.StatusActive)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }

        // Current upload/redemption producers use installation:<id>. Legacy
        // redemption uses machine:<id>; both bind to the same validated persisted
        // installation. Bootstrap and shop-wide scopes are deliberately excluded.
        var outcome = await _operationLedger.GetOutcomeAsync(id, $"installation:{installation.Id:D}", cancellationToken)
            ?? await _operationLedger.GetOutcomeAsync(id, $"installation:{installation.Id:N}", cancellationToken)
            ?? await _operationLedger.GetOutcomeAsync(id, $"machine:{installation.Id:D}", cancellationToken)
            ?? await _operationLedger.GetOutcomeAsync(id, $"machine:{installation.Id:N}", cancellationToken);
        if (outcome is null)
        {
            return NotFound(new { code = "OPERATION_NOT_FOUND", safeMessageKey = "operation.not_found" });
        }
        return Ok(outcome);
    }
}
