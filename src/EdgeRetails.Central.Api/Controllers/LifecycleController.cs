using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/lifecycle/v1")]
public class LifecycleController : ControllerBase
{
    private readonly ISensitiveLifecycleService _lifecycleService;
    private readonly IAdminAuthService _authService;

    public LifecycleController(
        ISensitiveLifecycleService lifecycleService,
        IAdminAuthService authService)
    {
        _lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpPost("sensitive-approvals")]
    public async Task<IActionResult> RequestApproval(
        [FromBody] RequestSensitiveApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var (adminId, sessionId) = await GetAdminIdentityAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var approval = await _lifecycleService.RequestApprovalAsync(
            request.OperationType,
            request.TargetEntityId,
            request.TargetPayloadSha256,
            request.ActionScope,
            adminId,
            sessionId,
            request.StepUpGrantId,
            cancellationToken,
            request.Reason);

        return Ok(approval);
    }

    [HttpPost("sensitive-approvals/{requestId:guid}/decide")]
    public async Task<IActionResult> DecideApproval(
        [FromRoute] Guid requestId,
        [FromBody] DecideSensitiveApprovalCommand command,
        CancellationToken cancellationToken)
    {
        var approverAdminId = await GetAdminUserIdAsync(cancellationToken);
        if (approverAdminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _lifecycleService.DecideApprovalAsync(requestId, command, approverAdminId, cancellationToken);
        return Ok(new { success = true, decision = command.Decision });
    }

    [HttpPost("licenses/{licenseId:guid}/pause")]
    public async Task<IActionResult> PauseLicense(
        [FromRoute] Guid licenseId,
        [FromBody] LicenseLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _lifecycleService.PauseLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(new { success = true, status = "PAUSED" });
    }

    [HttpPost("licenses/{licenseId:guid}/resume")]
    public async Task<IActionResult> ResumeLicense(
        [FromRoute] Guid licenseId,
        [FromBody] LicenseLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _lifecycleService.ResumeLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(new { success = true, status = "ACTIVE" });
    }

    [HttpPost("licenses/{licenseId:guid}/suspend")]
    public async Task<IActionResult> SuspendLicense(
        [FromRoute] Guid licenseId,
        [FromBody] LicenseLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _lifecycleService.SuspendLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(new { success = true, status = "SUSPENDED" });
    }

    [HttpPost("licenses/{licenseId:guid}/reinstate")]
    public async Task<IActionResult> ReinstateLicense(
        [FromRoute] Guid licenseId,
        [FromBody] LicenseLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _lifecycleService.ReinstateLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(new { success = true, status = "ACTIVE" });
    }

    [HttpPost("licenses/{licenseId:guid}/revoke")]
    public async Task<IActionResult> RevokeLicense(
        [FromRoute] Guid licenseId,
        [FromBody] LicenseLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _lifecycleService.RevokeLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(new { success = true, status = "REVOKED" });
    }

    [HttpPost("policies")]
    public async Task<IActionResult> ConfigurePolicy(
        [FromBody] ConfigurePolicyCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var policy = await _lifecycleService.ConfigurePolicyAsync(command, adminId, cancellationToken);
        return Ok(policy);
    }

    private async Task<Guid> GetAdminUserIdAsync(CancellationToken cancellationToken)
    {
        var (adminId, _) = await GetAdminIdentityAsync(cancellationToken);
        return adminId;
    }

    private async Task<(Guid AdminUserId, Guid SessionId)> GetAdminIdentityAsync(CancellationToken cancellationToken)
    {
        var sessionId = ExtractSessionId();
        if (sessionId != Guid.Empty)
        {
            var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
            if (session != null)
            {
                return (session.AdminUserId, sessionId);
            }
        }

        return (Guid.Empty, Guid.Empty);
    }

    private Guid ExtractSessionId()
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid ? credentials.SessionId : Guid.Empty;
    }
}

public class RequestSensitiveApprovalRequest
{
    public string OperationType { get; set; } = string.Empty;
    public Guid TargetEntityId { get; set; }
    public string TargetPayloadSha256 { get; set; } = string.Empty;
    public string ActionScope { get; set; } = string.Empty;
    public Guid StepUpGrantId { get; set; }
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(500, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}
