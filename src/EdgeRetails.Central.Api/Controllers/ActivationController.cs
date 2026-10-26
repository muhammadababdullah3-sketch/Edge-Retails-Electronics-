using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/activation/v1")]
public class ActivationController : ControllerBase
{
    private readonly IActivationService _activationService;
    private readonly IAdminAuthService _authService;
    private readonly ICentralDbContext _dbContext;
    private readonly IStepUpService _stepUpService;
    private readonly ICanonicalJsonSerializer _serializer;

    public ActivationController(
        IActivationService activationService,
        IAdminAuthService authService,
        ICentralDbContext dbContext,
        IStepUpService stepUpService,
        ICanonicalJsonSerializer serializer)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _stepUpService = stepUpService ?? throw new ArgumentNullException(nameof(stepUpService));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
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
            request.InstallationId,
            request.ReportedFingerprint,
            request.Reason,
            cancellationToken);

        return Ok(review);
    }

    [HttpPost("hardware-reviews/{reviewId:guid}/decide")]
    public Task<IActionResult> DecideHardwareReview(
        [FromRoute] Guid reviewId,
        [FromBody] DecideHardwareReviewCommand command,
        CancellationToken cancellationToken)
    {
        // This compatibility route predates the contract decision payload and cannot
        // bind a same-shop evidence attachment or a typed hardware decision. Keep it
        // explicit for old clients, but never let it mutate a review.
        return Task.FromResult<IActionResult>(Conflict(new
        {
            code = "CANONICAL_HARDWARE_REVIEW_REQUIRED",
            safeMessageKey = "hardware.canonical_review_required",
            route = "/api/admin/v1/hardware-reviews/{id}/decide"
        }));
    }

    [HttpPost("primary-transfers")]
    public async Task<IActionResult> InitiatePrimaryTransfer(
        [FromBody] InitiatePrimaryTransferCommand command,
        CancellationToken cancellationToken)
    {
        // The legacy DTO cannot bind same-shop verification evidence or the contract REQUEST decision.
        // Route all new requests through the canonical contract endpoint.
        return Conflict(new
        {
            code = "CANONICAL_PRIMARY_TRANSFER_REQUIRED",
            safeMessageKey = "transfer.canonical_request_required",
            route = "/api/admin/v1/transfers/{id}/decide"
        });
    }

    [HttpPost("primary-transfers/{transferId:guid}/approve")]
    public async Task<IActionResult> ApprovePrimaryTransfer(
        [FromRoute] Guid transferId,
        [FromBody] ApprovePrimaryTransferCommand command,
        CancellationToken cancellationToken)
    {
        // The legacy DTO cannot bind installation identities and verified evidence.
        return Conflict(new
        {
            code = "CANONICAL_PRIMARY_TRANSFER_REQUIRED",
            safeMessageKey = "transfer.canonical_decision_required",
            route = "/api/admin/v1/transfers/{id}/decide"
        });
    }

    [HttpPost("primary-transfers/{transferId:guid}/complete")]
    public async Task<IActionResult> CompletePrimaryTransfer(
        [FromRoute] Guid transferId,
        [FromBody] CompletePrimaryTransferCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        if (command.CommandMeta is null || command.CommandMeta.ClientOperationId == Guid.Empty ||
            command.CommandMeta.OperationType != "primary.transfer" || command.CommandMeta.FingerprintVersion != 1 ||
            command.CommandMeta.ExpectedVersion < 1 || command.CommandMeta.PolicyVersion < 0 ||
            string.IsNullOrWhiteSpace(command.CommandMeta.PayloadSha256) || string.IsNullOrWhiteSpace(command.CommandMeta.Reason))
        {
            return BadRequest(new { code = "INVALID_PRIMARY_TRANSFER_COMMAND", safeMessageKey = "transfer.command_invalid" });
        }

        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session || session.AdminUserId != adminId)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        // Completion is a separate state transition after the contract REQUEST/APPROVE
        // decisions. Consume fresh execution-session step-up atomically with its effects.
        var stepUpPayloadSha256 = _serializer.SerializeAndHash(new
        {
            transferId,
            commandPayloadSha256 = command.CommandMeta.PayloadSha256
        }).Sha256Hex;
        await using var transaction = await SecurityTransaction.BeginAsync(
            _dbContext, transferId.ToString(), cancellationToken);
        if (!await _stepUpService.ValidateAndConsumeStepUpGrantAsync(
                session.SessionId, "primary.transfer", stepUpPayloadSha256, cancellationToken: cancellationToken))
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException(
                "STEP_UP_REQUIRED", "auth.step_up_required", "A fresh execution-session step-up grant for this exact completion is required.");
        }

        await _activationService.CompletePrimaryTransferAsync(transferId, adminId, command, cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
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
    public string Reason { get; set; } = string.Empty;
}
