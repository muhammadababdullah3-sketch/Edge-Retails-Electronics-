using System.Net;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Recovery;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/recovery")]
public sealed class RecoveryController : ControllerBase
{
    private readonly IIdentityCredentialRecoveryRepository _recovery;
    private readonly RecoverOwnerPinHandler _handler;
    private readonly RuntimeLicenseService _runtimeLicense;
    private readonly IDeviceIdentityProvider _deviceIdentity;
    private readonly IRecoveryAuthorizationTrustProvider _trustProvider;

    public RecoveryController(
        IIdentityCredentialRecoveryRepository recovery,
        RecoverOwnerPinHandler handler,
        RuntimeLicenseService runtimeLicense,
        IDeviceIdentityProvider deviceIdentity,
        IRecoveryAuthorizationTrustProvider trustProvider)
    {
        _recovery = recovery;
        _handler = handler;
        _runtimeLicense = runtimeLicense;
        _deviceIdentity = deviceIdentity;
        _trustProvider = trustProvider;
    }

    [HttpGet("owner-pin")]
    public async Task<IActionResult> GetActiveOwners(CancellationToken cancellationToken)
    {
        if (!IsLoopbackRequest())
        {
            return NotFound();
        }

        var targets = await _recovery.GetActiveOwnerTargetsAsync(cancellationToken);
        return Ok(targets);
    }

    [HttpPost("owner-pin")]
    [RequestSizeLimit(48 * 1024)]
    public async Task<IActionResult> RecoverOwnerPin(
        [FromBody] RecoverOwnerPinRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsLoopbackRequest())
        {
            return NotFound();
        }

        var result = await _handler.HandleAsync(
            new RecoverOwnerPinCommand(request.SignedAuthorization, request.TargetUserId, request.NewPin ?? string.Empty),
            cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(new { success = true, targetUserId = result.Value!.TargetUserId });
        }

        return StatusCode(result.Error?.Code switch
        {
            "recovery.authorization_not_provisioned" => StatusCodes.Status503ServiceUnavailable,
            "recovery.authorization_invalid" or "recovery.authorization_expired" or "recovery.authorization_binding_mismatch" => StatusCodes.Status403Forbidden,
            "identity.recovery_replay" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        }, new { code = result.Error?.Code, message = result.Error?.Message });
    }

    [HttpGet("context")]
    public async Task<IActionResult> GetRecoveryContext(CancellationToken cancellationToken)
    {
        if (!IsLoopbackRequest())
        {
            return NotFound();
        }

        var license = await _runtimeLicense.ValidatePersistedAsync(cancellationToken);
        if (!license.IsValid || license.Payload is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { code = "recovery.license_unavailable", message = "Installed license binding could not be verified." });
        }

        string deviceId;
        try
        {
            deviceId = await _deviceIdentity.GetDeviceIdAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { code = "recovery.device_unavailable", message = "Device binding could not be verified." });
        }

        if (!string.Equals(license.Payload.DeviceId, deviceId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { code = "recovery.device_binding_invalid", message = "Installed license is not bound to this device." });
        }

        var trust = _trustProvider.GetTrust();
        return Ok(new RecoveryContextResponse(
            license.Payload.LicenseId,
            deviceId,
            string.IsNullOrWhiteSpace(trust.PublicKeyPem) ? null : trust.IssuerId));
    }

    private bool IsLoopbackRequest() =>
        HttpContext.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}

public sealed record RecoverOwnerPinRequest(
    string? SignedAuthorization,
    Guid TargetUserId,
    string? NewPin);

public sealed record RecoveryContextResponse(string LicenseId, string DeviceId, string? RecoveryIssuerId);
