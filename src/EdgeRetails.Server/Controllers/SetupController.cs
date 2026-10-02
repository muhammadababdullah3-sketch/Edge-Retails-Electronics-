using System.Net;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Setup;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

public sealed record BootstrapSetupRequest(
    string ShopName,
    string OwnerName,
    string? Phone,
    string? Address,
    string OwnerPin,
    string? SelectedModule,
    string SignedLicenseContent,
    Guid ClientOperationId);

[ApiController]
[Route("api/setup")]
public sealed class SetupController : ControllerBase
{
    private readonly IInstallationStateReadService _installation;
    private readonly ILicenseValidator _licenseValidator;
    private readonly ILicenseStore _licenseStore;
    private readonly FirstSetupBootstrapHandler _bootstrap;
    private readonly IHostEnvironment _environment;

    public SetupController(
        IInstallationStateReadService installation,
        ILicenseValidator licenseValidator,
        ILicenseStore licenseStore,
        FirstSetupBootstrapHandler bootstrap,
        IHostEnvironment environment)
    {
        _installation = installation;
        _licenseValidator = licenseValidator;
        _licenseStore = licenseStore;
        _bootstrap = bootstrap;
        _environment = environment;
    }

    [HttpGet("state")]
    public async Task<IActionResult> State(CancellationToken cancellationToken)
    {
        if (!IsLocalRequest())
        {
            return ForbiddenRemote();
        }

        var state = await _installation.GetAsync(cancellationToken);
        return Ok(new { isSetupRequired = state?.SetupStatus != SetupStatus.Complete });
    }

    [HttpPost("bootstrap")]
    public async Task<IActionResult> Bootstrap(
        [FromBody] BootstrapSetupRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsLocalRequest())
        {
            return ForbiddenRemote();
        }

        var state = await _installation.GetAsync(cancellationToken);
        if (state?.SetupStatus == SetupStatus.Complete)
        {
            return Conflict(new { code = "setup.already_complete", message = "First Setup has already been completed." });
        }

        if (request.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "setup.correlation_required", message = "A client operation identifier is required." });
        }

        if (string.IsNullOrWhiteSpace(request.SignedLicenseContent))
        {
            return BadRequest(new { code = "license.missing", message = "A signed license is required for First Setup." });
        }

        var validation = await _licenseValidator.ValidateAsync(request.SignedLicenseContent, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new
            {
                code = validation.ErrorCode ?? "license.invalid",
                message = validation.Message ?? "Signed license verification failed."
            });
        }

        var persisted = await _licenseStore.TryPersistInitialRawAsync(
            request.SignedLicenseContent, cancellationToken);
        if (!persisted)
        {
            var installed = await _licenseStore.ReadRawAsync(cancellationToken);
            if (!string.Equals(installed, request.SignedLicenseContent, StringComparison.Ordinal))
            {
                return Conflict(new
                {
                    code = "setup.license_already_installed",
                    message = "A different signed license is already installed."
                });
            }
        }

        var result = await _bootstrap.HandleAsync(
            new FirstSetupBootstrapCommand(
                request.ShopName,
                request.Phone,
                request.Address,
                request.OwnerName,
                request.OwnerPin,
                request.SelectedModule,
                request.ClientOperationId),
            cancellationToken);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = result.Error!;
        var statusCode = error.Code is "setup.already_complete" or "setup.identity_exists"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;
        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private bool IsLocalRequest()
    {
        var address = HttpContext.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address) ||
               address is null && _environment.IsEnvironment("Testing");
    }

    private ObjectResult ForbiddenRemote() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = "setup.local_only",
            message = "First Setup is available only on the local machine."
        });
}
