using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Settings;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(
    GetSettingsConfigurationHandler getSettings,
    UpdateShopProfileHandler updateShop,
    UpdateReceiptTemplateHandler updateReceipt) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        var value = await getSettings.HandleAsync(cancellationToken);
        return value is null
            ? NotFound(new { code = "settings.not_configured", message = "Shop settings are not configured." })
            : Ok(value);
    }

    [HttpPut("shop")]
    public async Task<IActionResult> UpdateShop([FromBody] UpdateShopSettingsApiRequest request, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        if (request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "validation.correlation_required", message = "A correlation identifier is required." });
        }

        var result = await updateShop.HandleAsync(
            new UpdateShopProfileCommand(request.ShopName, request.Phone, request.Address, actor.UserId, request.CorrelationId),
            cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(new { success = true });
        }

        return Error(result.Error!.Code, result.Error.Message);
    }

    [HttpPut("receipt-template")]
    public async Task<IActionResult> UpdateReceipt([FromBody] UpdateReceiptTemplateApiRequest request, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        if (request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "validation.correlation_required", message = "A correlation identifier is required." });
        }

        var result = await updateReceipt.HandleAsync(
            new UpdateReceiptTemplateCommand(request.Header, request.Footer, request.ShowCustomer, request.ShowCashier,
                request.AutoPrintDefault, actor.UserId, request.CorrelationId), cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(new { success = true });
        }

        return Error(result.Error!.Code, result.Error.Message);
    }

    private IActionResult Error(string code, string message)
    {
        var status = code.StartsWith("authorization.", StringComparison.OrdinalIgnoreCase) ? StatusCodes.Status403Forbidden
            : code.Contains("not_found", StringComparison.OrdinalIgnoreCase) ? StatusCodes.Status404NotFound
            : code.Contains("conflict", StringComparison.OrdinalIgnoreCase) ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;
        return StatusCode(status, new { code, message });
    }
}

public sealed record UpdateShopSettingsApiRequest(string ShopName, string? Phone, string? Address, Guid CorrelationId);
public sealed record UpdateReceiptTemplateApiRequest(
    string? Header,
    string? Footer,
    bool ShowCustomer,
    bool ShowCashier,
    bool AutoPrintDefault,
    Guid CorrelationId);
