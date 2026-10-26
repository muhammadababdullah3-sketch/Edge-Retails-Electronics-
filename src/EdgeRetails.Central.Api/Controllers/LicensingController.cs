using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Operations;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/licensing/v1")]
public class LicensingController : ControllerBase
{
    private readonly IProtectedSigningEngine _signingEngine;
    private readonly IActivationService _activationService;
    private readonly ICentralDbContext _dbContext;

    public LicensingController(
        IProtectedSigningEngine signingEngine,
        IActivationService activationService,
        ICentralDbContext dbContext)
    {
        _signingEngine = signingEngine ?? throw new ArgumentNullException(nameof(signingEngine));
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    [HttpGet("trust-set")]
    public async Task<IActionResult> GetTrustSet(CancellationToken cancellationToken)
    {
        var trustSet = await _signingEngine.GetTrustSetAsync(cancellationToken);
        return Ok(trustSet);
    }

    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile(
        [FromBody] CanonicalDirectoryCommand envelope,
        CancellationToken cancellationToken)
    {
        if (envelope?.Meta is null || envelope.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            envelope.Meta.OperationType != "installation.reconcile" || envelope.Meta.ClientOperationId == Guid.Empty ||
            envelope.Meta.FingerprintVersion != 1 || envelope.Meta.ExpectedVersion < 0 ||
            envelope.Meta.PolicyVersion < 0 || string.IsNullOrWhiteSpace(envelope.Meta.Reason) ||
            string.IsNullOrWhiteSpace(envelope.Meta.PayloadSha256))
        {
            return BadRequest(new { code = "INVALID_RECONCILE_REQUEST", safeMessageKey = "reconcile.request_invalid" });
        }
        ReconcileCommandDto? command;
        try
        {
            command = System.Text.Json.JsonSerializer.Deserialize<ReconcileCommandDto>(envelope.Payload.GetRawText(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest(new { code = "INVALID_RECONCILE_REQUEST", safeMessageKey = "reconcile.request_invalid" });
        }
        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (command is null || !System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                command, new System.ComponentModel.DataAnnotations.ValidationContext(command), validationResults, validateAllProperties: true) ||
            command.InstallationId == Guid.Empty || command.CertificateId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.HardwareFingerprintHash) || string.IsNullOrWhiteSpace(command.ApplicationVersion))
        {
            return BadRequest(new { code = "INVALID_RECONCILE_REQUEST", safeMessageKey = "reconcile.request_invalid" });
        }
        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation authenticatedInstallation ||
            command.InstallationId != authenticatedInstallation.Id)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }
        var entitlement = await _activationService.ReconcileCanonicalInstallationAsync(envelope.Meta.ToLedgerMeta(), command, cancellationToken);
        return Ok(entitlement);
    }

    [HttpPost("bootstrap/challenge")]
    public async Task<IActionResult> GenerateChallenge(
        [FromBody] BootstrapChallengeRequestDto request,
        CancellationToken cancellationToken)
    {
        var challenge = await _activationService.GenerateBootstrapChallengeAsync(request, cancellationToken);
        return Ok(challenge);
    }

    [HttpPost("primary/register")]
    public async Task<IActionResult> RegisterPrimary(
        [FromBody] InstallRegistrationRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Meta is null || request.Payload is null)
        {
            return BadRequest(new { code = "INVALID_BOOTSTRAP_REQUEST", safeMessageKey = "bootstrap.request_invalid" });
        }
        var outcome = await _activationService.RegisterBootstrapInstallationAsync(request.Meta, request.Payload, cancellationToken);
        return Ok(outcome);
    }

    [HttpPost("activation/redeem")]
    public async Task<IActionResult> RedeemActivationToken(
        [FromBody] BootstrapActivationRedeemRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Meta is null || request.Payload is null)
        {
            return BadRequest(new { code = "INVALID_BOOTSTRAP_REQUEST", safeMessageKey = "bootstrap.request_invalid" });
        }
        var outcome = await _activationService.RedeemBootstrapActivationAsync(request.Meta, request.Payload, cancellationToken);
        return Ok(outcome);
    }

    [HttpGet("activation/{id:guid}/status")]
    public async Task<IActionResult> GetActivationStatus(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var redemption = await _dbContext.ActivationRedemptions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (redemption == null)
        {
            return NotFound(new { code = "ACTIVATION_NOT_FOUND", safeMessageKey = "activation.not_found" });
        }

        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation installation ||
            redemption.InstallationId != installation.Id)
        {
            return NotFound(new { code = "ACTIVATION_NOT_FOUND", safeMessageKey = "activation.not_found" });
        }

        var operation = await _dbContext.CentralOperationRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.ResultReference == $"redemptions/{redemption.Id:D}" &&
                x.ActorScope == $"installation:{installation.Id:D}" &&
                x.ShopId == installation.ShopId &&
                x.OperationType == "activation.redeem",
                cancellationToken);
        if (operation is null)
        {
            return NotFound(new { code = "ACTIVATION_OPERATION_NOT_FOUND", safeMessageKey = "activation.operation_not_found" });
        }

        var state = redemption.Status switch
        {
            ActivationRedemption.StatusApproved => "SUCCEEDED",
            ActivationRedemption.StatusRejected => "FAILED_FINAL",
            _ => "PENDING"
        };

        var outcome = new OutcomeDto
        {
            ClientOperationId = operation.ClientOperationId,
            OperationType = operation.OperationType,
            PayloadSha256 = operation.PayloadSha256,
            OutcomeId = operation.OutcomeId,
            ResultReference = operation.ResultReference,
            State = state,
            ResourceVersion = operation.ResourceVersion,
            AuthorityRevision = operation.AuthorityRevision
        };

        return Ok(outcome);
    }

    /// <summary>
    /// Client polls for admin-issued license material after activation approval.
    /// </summary>
    [HttpGet("installations/{installationId:guid}/delivery")]
    public async Task<IActionResult> GetInstallationDelivery(
        [FromRoute] Guid installationId,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation installation ||
            installation.Id != installationId)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }

        var redemption = await _dbContext.ActivationRedemptions.AsNoTracking()
            .Where(r => r.InstallationId == installationId)
            .OrderByDescending(r => r.RedeemedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (redemption is null)
        {
            return Ok(new { state = "NO_REQUEST", message = "No activation request exists for this installation." });
        }

        if (redemption.Status == ActivationRedemption.StatusPending)
        {
            return Ok(new { state = "PENDING_ADMIN_APPROVAL", redemptionId = redemption.Id });
        }

        if (redemption.Status == ActivationRedemption.StatusRejected)
        {
            return Ok(new { state = "REJECTED", redemptionId = redemption.Id });
        }

        if (installation.Status != Installation.StatusActive)
        {
            return Ok(new { state = "APPROVED_AWAITING_CERTIFICATE", redemptionId = redemption.Id });
        }

        var certificate = await _dbContext.LicenseCertificates.AsNoTracking()
            .Where(c => c.InstallationId == installationId && c.Status == LicenseCertificate.StatusActive)
            .OrderByDescending(c => c.IssuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (certificate is null)
        {
            return Ok(new { state = "APPROVED_AWAITING_CERTIFICATE", redemptionId = redemption.Id });
        }

        return Ok(new
        {
            state = "READY",
            redemptionId = redemption.Id,
            certificateId = certificate.Id,
            signedLicenseJson = certificate.SignedEnvelopeJson
        });
    }
}
