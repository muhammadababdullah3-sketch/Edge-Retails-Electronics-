using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/sensitive-approvals")]
public class SensitiveApprovalsController : ControllerBase
{
    private readonly Wave4DashboardAndPortalService _portalService;
    private readonly IAdminAuthService _authService;
    private readonly ISensitiveLifecycleService _lifecycleService;

    public SensitiveApprovalsController(
        Wave4DashboardAndPortalService portalService,
        IAdminAuthService authService,
        ISensitiveLifecycleService lifecycleService)
    {
        _portalService = portalService ?? throw new ArgumentNullException(nameof(portalService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
    }

    [HttpPost("{approvalId:guid}/decide")]
    public async Task<IActionResult> DecideCanonicalApproval(
        [FromRoute] Guid approvalId,
        [FromBody] JsonElement body,
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
        if (!session.Capabilities.Contains("sensitive.approve"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN", safeMessageKey = "auth.unauthorized_capability" });
        }

        CanonicalSensitiveApprovalCommand? command;
        try
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                return BadRequest(new { code = "INVALID_COMMAND_ENVELOPE", safeMessageKey = "command.invalid_metadata" });
            }
            command = JsonSerializer.Deserialize<CanonicalSensitiveApprovalCommand>(body.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });
        }
        catch (JsonException)
        {
            return BadRequest(new { code = "INVALID_COMMAND_ENVELOPE", safeMessageKey = "command.invalid_metadata" });
        }

        if (command is null)
        {
            return BadRequest(new { code = "INVALID_COMMAND_ENVELOPE", safeMessageKey = "command.invalid_metadata" });
        }

        var outcome = await _lifecycleService.DecideApprovalCanonicalAsync(
            approvalId,
            command.Payload,
            command.Meta.ToCanonicalMeta(),
            session.AdminUserId,
            session.SessionId,
            cancellationToken);
        return Ok(outcome);
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
