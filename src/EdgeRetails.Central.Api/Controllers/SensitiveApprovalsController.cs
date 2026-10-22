using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/sensitive-approvals")]
public class SensitiveApprovalsController : ControllerBase
{
    private readonly Wave4DashboardAndPortalService _portalService;
    private readonly IAdminAuthService _authService;

    public SensitiveApprovalsController(
        Wave4DashboardAndPortalService portalService,
        IAdminAuthService authService)
    {
        _portalService = portalService ?? throw new ArgumentNullException(nameof(portalService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpPost("{approvalId:guid}/decision")]
    public async Task<IActionResult> MakeDecision(
        [FromRoute] Guid approvalId,
        [FromBody] SensitiveApprovalDecisionDto dto,
        CancellationToken cancellationToken)
    {
        var sessionId = ExtractSessionId();
        if (sessionId == Guid.Empty)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        var success = await _portalService.DecideSensitiveApprovalAsync(
            approvalId,
            session.AdminUserId,
            dto,
            cancellationToken);

        return Ok(new { success });
    }

    [HttpPost("unmask-pii")]
    public async Task<IActionResult> UnmaskPii(
        [FromBody] UnmaskPiiRequestDto request,
        CancellationToken cancellationToken)
    {
        var sessionId = ExtractSessionId();
        if (sessionId == Guid.Empty)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        var result = await _portalService.UnmaskPiiAsync(
            request,
            session.AdminUserId,
            "admin-unmasker",
            sessionId,
            cancellationToken);

        return Ok(result);
    }

    private Guid ExtractSessionId()
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid ? credentials.SessionId : Guid.Empty;
    }
}
