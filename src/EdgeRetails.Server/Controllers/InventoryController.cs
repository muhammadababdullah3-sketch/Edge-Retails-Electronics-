using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class InventoryController : ControllerBase
{
    private readonly IInventoryOverviewReadService _overviewReads;
    private readonly IInventoryProvenanceReadService _provenanceReads;
    private readonly IPhase4WorkflowReadService _workflowReads;
    private readonly CreateStockAdjustmentHandler _stockAdjustmentHandler;
    private readonly CreateStocktakeHandler _stocktakeHandler;
    private readonly StartStocktakeHandler _startStocktakeHandler;
    private readonly RecordStocktakeCountHandler _recordStocktakeCountHandler;
    private readonly RecordSerializedStocktakeHandler _recordSerializedStocktakeHandler;
    private readonly ReviewStocktakeHandler _reviewStocktakeHandler;
    private readonly CancelStocktakeHandler _cancelStocktakeHandler;
    private readonly PostStocktakeHandler? _postStocktakeHandler;

    public InventoryController(
        IInventoryOverviewReadService overviewReads,
        IInventoryProvenanceReadService provenanceReads,
        IPhase4WorkflowReadService workflowReads,
        CreateStockAdjustmentHandler stockAdjustmentHandler,
        CreateStocktakeHandler stocktakeHandler,
        StartStocktakeHandler startStocktakeHandler,
        RecordStocktakeCountHandler recordStocktakeCountHandler,
        RecordSerializedStocktakeHandler recordSerializedStocktakeHandler,
        ReviewStocktakeHandler reviewStocktakeHandler,
        CancelStocktakeHandler cancelStocktakeHandler,
        PostStocktakeHandler? postStocktakeHandler = null)
    {
        _overviewReads = overviewReads;
        _provenanceReads = provenanceReads;
        _workflowReads = workflowReads;
        _stockAdjustmentHandler = stockAdjustmentHandler;
        _stocktakeHandler = stocktakeHandler;
        _startStocktakeHandler = startStocktakeHandler;
        _recordStocktakeCountHandler = recordStocktakeCountHandler;
        _recordSerializedStocktakeHandler = recordSerializedStocktakeHandler;
        _reviewStocktakeHandler = reviewStocktakeHandler;
        _cancelStocktakeHandler = cancelStocktakeHandler;
        _postStocktakeHandler = postStocktakeHandler;
    }

    [HttpGet("stock")]
    public async Task<IActionResult> GetStock(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery] string? brand,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? beforeName = null,
        [FromQuery] Guid? beforeProductId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var query = new InventoryStockPageQuery(
            Search: search,
            Category: category,
            Brand: brand,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeName: beforeName,
            BeforeProductId: beforeProductId);

        var stock = await _overviewReads.GetStockPageAsync(query, cancellationToken);
        return Ok(stock);
    }

    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements(
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? beforeOccurredAt = null,
        [FromQuery] Guid? beforeMovementId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var movements = await _overviewReads.GetMovementsAsync(
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            cancellationToken,
            beforeOccurredAt,
            beforeMovementId);
        return Ok(movements);
    }

    [HttpGet("exact-units")]
    public async Task<IActionResult> GetExactUnits(
        [FromQuery] Guid productId,
        [FromQuery] InventoryUnitStatus? status,
        [FromQuery] Guid? sourcePurchaseItemId,
        [FromQuery] int pageSize = 50,
        [FromQuery] Guid? beforeUnitId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var units = await _workflowReads.GetExactUnitsAsync(
            productId,
            status,
            sourcePurchaseItemId,
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            beforeUnitId,
            cancellationToken);
        return Ok(units);
    }

    [HttpGet("units/{inventoryUnitId:guid}/history")]
    public async Task<IActionResult> GetUnitHistory(
        [FromRoute] Guid inventoryUnitId,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var history = await _provenanceReads.GetSerializedUnitHistoryAsync(
            new GetSerializedUnitHistoryQuery(inventoryUnitId),
            cancellationToken);

        if (history is null)
        {
            return NotFound(new { code = "inventory.unit_not_found", message = $"Inventory unit '{inventoryUnitId}' was not found." });
        }

        return Ok(history);
    }

    [HttpGet("products/{productId:guid}/purchase-provenance")]
    public async Task<IActionResult> GetProductPurchaseProvenance(
        [FromRoute] Guid productId,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var provenance = await _provenanceReads.GetProductPurchaseProvenanceAsync(
            new GetProductPurchaseProvenanceQuery(productId, Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500)),
            cancellationToken);
        return Ok(provenance);
    }

    [HttpGet("products/{productId:guid}/sale-history")]
    public async Task<IActionResult> GetProductSaleHistory(
        [FromRoute] Guid productId,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var history = await _provenanceReads.GetProductSaleHistoryAsync(
            new GetProductSaleHistoryQuery(productId, Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500)),
            cancellationToken);
        return Ok(history);
    }

    [HttpPost("adjustments")]
    public async Task<IActionResult> CreateAdjustment(
        [FromBody] CreateStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _stockAdjustmentHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("stocktake")]
    public async Task<IActionResult> CreateStocktake(
        [FromBody] CreateStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "inventory.stocktake_operation_id_required", message = "A stocktake operation id is required." });
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _stocktakeHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("stocktake/open")]
    public async Task<IActionResult> GetOpenStocktake(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        return Ok(new OpenStocktakeApiResponse(
            await _workflowReads.GetOpenStocktakeAsync(cancellationToken)));
    }

    [HttpPost("stocktake/{id:guid}/start")]
    public async Task<IActionResult> StartStocktake(
        [FromRoute] Guid id,
        [FromBody] StocktakeActionApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (request.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "inventory.stocktake_operation_id_required", message = "A stocktake operation id is required." });
        }

        var result = await _startStocktakeHandler.HandleAsync(
            new StartStocktakeCommand(id, HttpContext.GetActorContext()?.UserId ?? Guid.Empty, request.ClientOperationId), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("stocktake/{id:guid}/counts")]
    public async Task<IActionResult> RecordStocktakeCount(
        [FromRoute] Guid id,
        [FromBody] RecordStocktakeCountApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (request.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "inventory.stocktake_operation_id_required", message = "A stocktake operation id is required." });
        }

        var actor = HttpContext.GetActorContext();
        var result = await _recordStocktakeCountHandler.HandleAsync(
            new RecordStocktakeCountCommand(
                id,
                request.ProductId,
                request.CountedSellableQty,
                actor?.UserId ?? Guid.Empty,
                request.ReviewNote,
                request.ClientOperationId),
            cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("stocktake/{id:guid}/serialized-counts")]
    public async Task<IActionResult> RecordSerializedStocktake(
        [FromRoute] Guid id,
        [FromBody] RecordSerializedStocktakeApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (request.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "inventory.stocktake_operation_id_required", message = "A stocktake operation id is required." });
        }

        var actor = HttpContext.GetActorContext();
        var result = await _recordSerializedStocktakeHandler.HandleAsync(
            new RecordSerializedStocktakeCommand(
                id,
                request.ProductId,
                request.FoundInventoryUnitIds,
                request.UnexpectedIdentitySnapshots,
                actor?.UserId ?? Guid.Empty,
                request.ReviewNote,
                request.ClientOperationId),
            cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("stocktake/{id:guid}/review")]
    public async Task<IActionResult> ReviewStocktake(
        [FromRoute] Guid id,
        [FromBody] StocktakeActionApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (request.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "inventory.stocktake_operation_id_required", message = "A stocktake operation id is required." });
        }

        var result = await _reviewStocktakeHandler.HandleAsync(
            new ReviewStocktakeCommand(id, HttpContext.GetActorContext()?.UserId ?? Guid.Empty, request.ClientOperationId), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("stocktake/{id:guid}/cancel")]
    public async Task<IActionResult> CancelStocktake(
        [FromRoute] Guid id,
        [FromBody] StocktakeActionApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }
        if (request.ClientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "inventory.stocktake_operation_id_required", message = "A stocktake operation id is required." });
        }

        var result = await _cancelStocktakeHandler.HandleAsync(
            new CancelStocktakeCommand(id, HttpContext.GetActorContext()?.UserId ?? Guid.Empty, request.ClientOperationId), cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("stocktake/{id:guid}/post")]
    public async Task<IActionResult> PostStocktake(
        [FromRoute] Guid id,
        [FromBody] PostStocktakeApiRequest? request,
        [FromHeader(Name = "X-Client-Operation-Id")] Guid? headerOperationId,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        if (_postStocktakeHandler is null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { code = "service_unavailable", message = "PostStocktakeHandler is unavailable." });
        }

        var actor = HttpContext.GetActorContext();
        var clientOpId = request?.ClientOperationId is { } op && op != Guid.Empty
            ? op
            : headerOperationId ?? Guid.Empty;

        var command = new PostStocktakeCommand(
            StocktakeId: id,
            ActorId: actor?.UserId ?? Guid.Empty,
            PositiveVarianceUnitCosts: request?.PositiveVarianceUnitCosts,
            ClientOperationId: clientOpId);

        var result = await _postStocktakeHandler.HandleAsync(command, cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok();
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("conflict") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("already_open") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
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
            var c when c.Contains("already_open") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record PostStocktakeApiRequest(
    Guid? ClientOperationId,
    IReadOnlyDictionary<Guid, decimal>? PositiveVarianceUnitCosts);

public sealed record RecordStocktakeCountApiRequest(
    Guid ProductId,
    decimal CountedSellableQty,
    string? ReviewNote = null,
    Guid ClientOperationId = default);

public sealed record RecordSerializedStocktakeApiRequest(
    Guid ProductId,
    IReadOnlyCollection<Guid> FoundInventoryUnitIds,
    IReadOnlyCollection<string> UnexpectedIdentitySnapshots,
    string? ReviewNote = null,
    Guid ClientOperationId = default);

public sealed record StocktakeActionApiRequest(Guid ClientOperationId);

public sealed record OpenStocktakeApiResponse(StocktakeSnapshotDto? Stocktake);
