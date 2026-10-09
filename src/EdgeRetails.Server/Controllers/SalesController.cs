using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SalesController : ControllerBase
{
    private readonly CompleteSaleHandler _completeSaleHandler;
    private readonly CompletePosDraftHandler _completePosDraftHandler;
    private readonly CreateSaleReturnHandler _saleReturnHandler;
    private readonly CommercialExchangeHandler _exchangeHandler;
    private readonly SavePosDraftHandler _saveDraftHandler;
    private readonly CancelPosDraftHandler _cancelDraftHandler;
    private readonly ISalesReadService _salesReads;
    private readonly IPhase4WorkflowReadService _workflowReads;

    public SalesController(
        CompleteSaleHandler completeSaleHandler,
        CompletePosDraftHandler completePosDraftHandler,
        CreateSaleReturnHandler saleReturnHandler,
        CommercialExchangeHandler exchangeHandler,
        SavePosDraftHandler saveDraftHandler,
        CancelPosDraftHandler cancelDraftHandler,
        ISalesReadService salesReads,
        IPhase4WorkflowReadService workflowReads)
    {
        _completeSaleHandler = completeSaleHandler;
        _completePosDraftHandler = completePosDraftHandler;
        _saleReturnHandler = saleReturnHandler;
        _exchangeHandler = exchangeHandler;
        _saveDraftHandler = saveDraftHandler;
        _cancelDraftHandler = cancelDraftHandler;
        _salesReads = salesReads;
        _workflowReads = workflowReads;
    }

    [HttpGet]
    public async Task<IActionResult> GetSalesHistory(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string? search,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? beforeCompletedAt = null,
        [FromQuery] Guid? beforeSaleId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesView);
        if (denied is not null)
        {
            return denied;
        }

        var query = new GetSalesHistoryQuery(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Search: search,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeCompletedAt: beforeCompletedAt,
            BeforeSaleId: beforeSaleId);

        var results = await _salesReads.GetHistoryAsync(query, cancellationToken);
        return Ok(results);
    }

    [HttpGet("returns")]
    public async Task<IActionResult> GetReturnHistory(
        [FromQuery] Guid? saleId,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? beforeCreatedAt = null,
        [FromQuery] Guid? beforeReturnId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesView);
        if (denied is not null)
        {
            return denied;
        }

        var query = new GetSaleReturnHistoryQuery(
            SaleId: saleId,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeCreatedAt: beforeCreatedAt,
            BeforeReturnId: beforeReturnId);

        var results = await _salesReads.GetReturnHistoryAsync(query, cancellationToken);
        return Ok(results);
    }

    [HttpGet("quotations")]
    public async Task<IActionResult> GetQuotations(
        [FromQuery] EdgeRetails.Domain.Sales.QuotationStatus? status,
        [FromQuery] string? search,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateOnly? beforeQuotationDate = null,
        [FromQuery] Guid? beforeQuotationId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesView);
        if (denied is not null)
        {
            return denied;
        }

        var query = new GetQuotationsQuery(
            Status: status,
            Search: search,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeQuotationDate: beforeQuotationDate,
            BeforeQuotationId: beforeQuotationId);

        var results = await _salesReads.GetQuotationsAsync(query, cancellationToken);
        return Ok(results);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetSaleDetail(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesView);
        if (denied is not null)
        {
            return denied;
        }

        var detail = await _salesReads.GetDetailAsync(new GetSaleDetailQuery(id), cancellationToken);
        if (detail is null)
        {
            return NotFound(new { code = "sales.sale_not_found", message = $"Sale '{id}' was not found." });
        }
        return Ok(detail);
    }

    [HttpGet("scan")]
    public async Task<IActionResult> Scan(
        [FromQuery] string code,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesPosUse);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest(new { code = "sales.scan_code_required", message = "Scan code is required." });
        }

        var matches = await _workflowReads.ResolveScannerAsync(code.Trim(), cancellationToken);
        return Ok(matches);
    }

    [HttpGet("exact-units")]
    public async Task<IActionResult> GetPosExactUnits(
        [FromQuery] Guid productId,
        [FromServices] IPhase4WorkflowReadService workflowReads,
        [FromQuery] InventoryUnitStatus? status,
        [FromQuery] Guid? sourcePurchaseItemId,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? beforeUnitId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesPosUse);
        if (denied is not null)
        {
            return denied;
        }

        if (productId == Guid.Empty)
        {
            return BadRequest(new
            {
                code = "inventory.product_id_required",
                message = "Product identifier is required."
            });
        }

        var units = await workflowReads.GetExactUnitsAsync(
            productId,
            status,
            sourcePurchaseItemId,
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200),
            beforeUnitId,
            cancellationToken);

        return Ok(units.Select(unit => new PosExactUnitDto(
            unit.InventoryUnitId,
            unit.ProductId,
            unit.ProductName,
            unit.Sku,
            unit.TrackingCode,
            unit.SerialNumber,
            unit.Imei1,
            unit.Imei2,
            unit.Status,
            unit.Version)));
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> GetPosCatalog(
        [FromServices] IPosCatalogReadService catalogReads,
        [FromQuery] string? search,
        [FromQuery] string? category = null,
        [FromQuery] string? brand = null,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? afterName = null,
        [FromQuery] Guid? afterId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(afterName) != !afterId.HasValue)
        {
            return BadRequest(new { code = "sales.catalog_cursor_invalid", message = "Both afterName and afterId are required for a catalog cursor." });
        }

        var denied = this.RequirePermission(PermissionKeys.SalesPosUse);
        if (denied is not null)
        {
            return denied;
        }

        if (search?.Length > 200)
        {
            return BadRequest(new { code = "sales.search_too_long", message = "Search text is too long." });
        }

        var products = await catalogReads.GetSellableCatalogAsync(
            search?.Trim(),
            category?.Trim(),
            brand?.Trim(),
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 200),
            afterName,
            afterId,
            cancellationToken);
        return Ok(products);
    }

    [HttpGet("drafts")]
    public async Task<IActionResult> GetOpenDrafts(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesDraftResume);
        if (denied is not null)
        {
            return denied;
        }

        var drafts = await _workflowReads.GetOpenDraftsAsync(cancellationToken);
        return Ok(drafts);
    }

    [HttpGet("drafts/{id:guid}")]
    public async Task<IActionResult> GetDraft(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesDraftResume);
        if (denied is not null)
        {
            return denied;
        }

        var draft = await _workflowReads.GetDraftAsync(id, cancellationToken);
        if (draft is null)
        {
            return NotFound(new { code = "sales.draft_not_found", message = $"Draft '{id}' was not found." });
        }
        return Ok(draft);
    }

    [HttpPost("drafts")]
    public async Task<IActionResult> SaveDraft(
        [FromBody] SavePosDraftCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new
            {
                code = "sales.draft_operation_id_required",
                message = "ClientOperationId is required to save a POS draft."
            });
        }

        var actor = HttpContext.GetActorContext();
        var terminal = HttpContext.Items["CurrentTerminal"] as Terminal;
        var sanitized = actor is not null
            ? command with
            {
                ActorId = actor.UserId,
                SessionId = actor.SessionId,
                RegisteredTerminalId = terminal?.Id,
                TerminalId = terminal?.TerminalCode
            }
            : command;
        var result = await _saveDraftHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("drafts/{id:guid}/cancel")]
    public async Task<IActionResult> CancelDraft(
        [FromRoute] Guid id,
        [FromBody] CancelPosDraftRequest? request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request?.ActorId ?? Guid.Empty;
        var command = new CancelPosDraftCommand(id, request?.ExpectedVersion, actorId);
        var result = await _cancelDraftHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("complete")]
    public async Task<IActionResult> CompleteSale(
        [FromBody] CompleteSaleCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null
            ? command with { CashierUserId = actor.UserId, SessionId = actor.SessionId }
            : command;
        var result = await _completeSaleHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("drafts/complete")]
    public async Task<IActionResult> CompletePosDraft(
        [FromBody] CompletePosDraftCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null
            ? command with { CashierUserId = actor.UserId, SessionId = actor.SessionId }
            : command;
        var result = await _completePosDraftHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("return")]
    public async Task<IActionResult> CreateSaleReturn(
        [FromBody] CreateSaleReturnCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { CreatedBy = actor.UserId } : command;
        var result = await _saleReturnHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("exchange")]
    public async Task<IActionResult> ExecuteCommercialExchange(
        [FromBody] CommercialExchangeCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null
            ? command with { CashierUserId = actor.UserId, SessionId = actor.SessionId }
            : command;
        var result = await _exchangeHandler.HandleAsync(sanitized, cancellationToken);
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
            var c when c.Contains("insufficient") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok(new { success = true });
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("insufficient") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record CancelPosDraftRequest(long? ExpectedVersion = null, Guid? ActorId = null);
