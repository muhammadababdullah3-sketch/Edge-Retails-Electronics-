using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class PurchasingController : ControllerBase
{
    private readonly CreatePurchaseHandler _createPurchaseHandler;
    private readonly CreatePurchaseReturnHandler _purchaseReturnHandler;
    private readonly VoidPurchaseHandler _voidPurchaseHandler;
    private readonly ReceiveProductIntakeHandler _receiveIntakeHandler;
    private readonly IPurchasingReadService _purchasingReads;

    public PurchasingController(
        CreatePurchaseHandler createPurchaseHandler,
        CreatePurchaseReturnHandler purchaseReturnHandler,
        VoidPurchaseHandler voidPurchaseHandler,
        ReceiveProductIntakeHandler receiveIntakeHandler,
        IPurchasingReadService purchasingReads)
    {
        _createPurchaseHandler = createPurchaseHandler;
        _purchaseReturnHandler = purchaseReturnHandler;
        _voidPurchaseHandler = voidPurchaseHandler;
        _receiveIntakeHandler = receiveIntakeHandler;
        _purchasingReads = purchasingReads;
    }

    [HttpGet]
    public async Task<IActionResult> GetHistory(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] Guid? supplierId,
        [FromQuery] string? search,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateOnly? beforePurchaseDate = null,
        [FromQuery] Guid? beforePurchaseId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.PurchasingManage);
        if (denied is not null)
        {
            return denied;
        }

        var query = new GetPurchaseHistoryQuery(
            FromDate: fromDate,
            ToDate: toDate,
            SupplierId: supplierId,
            Search: search,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforePurchaseDate: beforePurchaseDate,
            BeforePurchaseId: beforePurchaseId);

        var result = await _purchasingReads.GetHistoryAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("returns")]
    public async Task<IActionResult> GetReturnHistory(
        [FromQuery] Guid? purchaseId,
        [FromQuery] Guid? supplierId,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? beforeCreatedAt = null,
        [FromQuery] Guid? beforeReturnId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.PurchasingManage);
        if (denied is not null)
        {
            return denied;
        }

        var query = new GetPurchaseReturnHistoryQuery(
            PurchaseId: purchaseId,
            SupplierId: supplierId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeCreatedAt: beforeCreatedAt,
            BeforeReturnId: beforeReturnId);

        var result = await _purchasingReads.GetReturnHistoryAsync(query, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDocument(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.PurchasingManage);
        if (denied is not null)
        {
            return denied;
        }

        var doc = await _purchasingReads.GetDocumentAsync(new GetPurchaseDocumentQuery(id), cancellationToken);
        if (doc is null)
        {
            return NotFound(new { code = "purchasing.purchase_not_found", message = $"Purchase '{id}' was not found." });
        }
        return Ok(doc);
    }

    [HttpGet("items/{purchaseItemId:guid}/units")]
    public async Task<IActionResult> GetUnitsForPurchaseItem(
        [FromRoute] Guid purchaseItemId,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.PurchasingManage);
        if (denied is not null)
        {
            return denied;
        }

        var units = await _purchasingReads.GetUnitsForPurchaseItemAsync(purchaseItemId, cancellationToken);
        return Ok(units);
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreatePurchase(
        [FromBody] CreatePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { CreatedBy = actor.UserId } : command;
        var result = await _createPurchaseHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("intake")]
    public async Task<IActionResult> ReceiveProductIntake(
        [FromBody] ReceiveProductIntakeCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { CreatedBy = actor.UserId } : command;
        var result = await _receiveIntakeHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("return")]
    public async Task<IActionResult> CreatePurchaseReturn(
        [FromBody] CreatePurchaseReturnCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { CreatedBy = actor.UserId } : command;
        var result = await _purchaseReturnHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("void")]
    public async Task<IActionResult> VoidPurchase(
        [FromBody] VoidPurchaseCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.PurchasingManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { VoidedBy = actor.UserId } : command;
        var result = await _voidPurchaseHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("invalid_status") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}
