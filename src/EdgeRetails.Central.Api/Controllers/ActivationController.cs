using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/activation/v1")]
public class ActivationController : ControllerBase
{
    private readonly IActivationService _activationService;
    private readonly IAdminAuthService _authService;

    public ActivationController(
        IActivationService activationService,
        IAdminAuthService authService)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpPost("installations")]
    public async Task<IActionResult> RegisterInstallation(
        [FromBody] RegisterInstallationCommand command,
        CancellationToken cancellationToken)
    {
        var installation = await _activationService.RegisterInstallationAsync(command, cancellationToken);
        return Ok(installation);
    }

    [HttpPost("tokens")]
    public async Task<IActionResult> IssueActivationToken(
        [FromBody] IssueActivationTokenCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var tokenDto = await _activationService.IssueActivationTokenAsync(command, adminId, cancellationToken);
        return Ok(tokenDto);
    }

    [HttpPost("challenge")]
    public async Task<IActionResult> GenerateChallenge(
        [FromBody] BootstrapChallengeRequestDto request,
        CancellationToken cancellationToken)
    {
        var challenge = await _activationService.GenerateBootstrapChallengeAsync(request, cancellationToken);
        return Ok(challenge);
    }

    [HttpPost("redeem")]
    public async Task<IActionResult> RedeemActivationToken(
        [FromBody] RedeemActivationTokenCommand command,
        CancellationToken cancellationToken)
    {
        var redemption = await _activationService.RedeemActivationTokenAsync(command, cancellationToken);
        return Ok(redemption);
    }

    [HttpPost("redemptions/{redemptionId:guid}/review")]
    public async Task<IActionResult> ReviewActivation(
        [FromRoute] Guid redemptionId,
        [FromBody] ReviewActivationCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _activationService.ReviewActivationAsync(redemptionId, command, adminId, cancellationToken);
        return Ok(new { success = true, decision = command.Decision });
    }

    [HttpPost("certificates/issue")]
    public async Task<IActionResult> IssueCertificate(
        [FromBody] IssueCertificateRequest request,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var cert = await _activationService.IssueCertificateAsync(request.InstallationId, adminId, cancellationToken);
        return Ok(cert);
    }

    [HttpPost("hardware-reviews")]
    public async Task<IActionResult> ReportHardwareDiscrepancy(
        [FromBody] ReportHardwareDiscrepancyRequest request,
        CancellationToken cancellationToken)
    {
        var review = await _activationService.ReportHardwareDiscrepancyAsync(
            request.ShopId,
            request.InstallationId,
            request.ReportedFingerprint,
            request.StoredFingerprint,
            request.Reason,
            cancellationToken);

        return Ok(review);
    }

    [HttpPost("hardware-reviews/{reviewId:guid}/decide")]
    public async Task<IActionResult> DecideHardwareReview(
        [FromRoute] Guid reviewId,
        [FromBody] DecideHardwareReviewCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _activationService.DecideHardwareReviewAsync(reviewId, command, adminId, cancellationToken);
        return Ok(new { success = true, approve = command.Approve });
    }

    [HttpPost("primary-transfers")]
    public async Task<IActionResult> InitiatePrimaryTransfer(
        [FromBody] InitiatePrimaryTransferCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var transfer = await _activationService.InitiatePrimaryTransferAsync(command, adminId, cancellationToken);
        return Ok(transfer);
    }

    [HttpPost("primary-transfers/{transferId:guid}/approve")]
    public async Task<IActionResult> ApprovePrimaryTransfer(
        [FromRoute] Guid transferId,
        [FromBody] ApprovePrimaryTransferCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _activationService.ApprovePrimaryTransferAsync(transferId, adminId, command.SensitiveApprovalId, command, cancellationToken);
        return Ok(new { success = true, status = "APPROVED" });
    }

    [HttpPost("primary-transfers/{transferId:guid}/complete")]
    public async Task<IActionResult> CompletePrimaryTransfer(
        [FromRoute] Guid transferId,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        await _activationService.CompletePrimaryTransferAsync(transferId, adminId, cancellationToken);
        return Ok(new { success = true, status = "COMPLETED" });
    }

    private async Task<Guid> GetAdminUserIdAsync(CancellationToken cancellationToken)
    {
        var credential = CentralSessionCredentials.Parse(Request);
        var sessionId = credential.IsValid ? credential.SessionId : Guid.Empty;
        if (sessionId != Guid.Empty)
        {
            var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
            if (session != null)
            {
                return session.AdminUserId;
            }
        }

        return Guid.Empty;
    }

}

public class IssueCertificateRequest
{
    public Guid InstallationId { get; set; }
}

public class ReportHardwareDiscrepancyRequest
{
    public Guid ShopId { get; set; }
    public Guid InstallationId { get; set; }
    public string ReportedFingerprint { get; set; } = string.Empty;
    public string StoredFingerprint { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
