using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class WarrantyController : ControllerBase
{
    private readonly CreateWarrantyClaimHandler _createClaimHandler;
    private readonly BeginWarrantyClaimReviewHandler _beginReviewHandler;
    private readonly SendWarrantyClaimToSupplierHandler _sendToSupplierHandler;
    private readonly MarkWarrantySupplierProcessingHandler _supplierProcessingHandler;
    private readonly RecordWarrantyResolutionHandler _recordResolutionHandler;
    private readonly ReceiveCustomerWarrantyReplacementHandler _receiveReplacementHandler;
    private readonly HandoverWarrantyItemHandler _handoverHandler;
    private readonly CancelWarrantyClaimHandler _cancelHandler;
    private readonly SendShopStockToSupplierWarrantyHandler _sendShopStockHandler;
    private readonly ReceiveShopStockWarrantyHandler _receiveShopStockHandler;
    private readonly IWarrantyReadService _warrantyReads;

    public WarrantyController(
        CreateWarrantyClaimHandler createClaimHandler,
        BeginWarrantyClaimReviewHandler beginReviewHandler,
        SendWarrantyClaimToSupplierHandler sendToSupplierHandler,
        MarkWarrantySupplierProcessingHandler supplierProcessingHandler,
        RecordWarrantyResolutionHandler recordResolutionHandler,
        ReceiveCustomerWarrantyReplacementHandler receiveReplacementHandler,
        HandoverWarrantyItemHandler handoverHandler,
        CancelWarrantyClaimHandler cancelHandler,
        SendShopStockToSupplierWarrantyHandler sendShopStockHandler,
        ReceiveShopStockWarrantyHandler receiveShopStockHandler,
        IWarrantyReadService warrantyReads)
    {
        _createClaimHandler = createClaimHandler;
        _beginReviewHandler = beginReviewHandler;
        _sendToSupplierHandler = sendToSupplierHandler;
        _supplierProcessingHandler = supplierProcessingHandler;
        _recordResolutionHandler = recordResolutionHandler;
        _receiveReplacementHandler = receiveReplacementHandler;
        _handoverHandler = handoverHandler;
        _cancelHandler = cancelHandler;
        _sendShopStockHandler = sendShopStockHandler;
        _receiveShopStockHandler = receiveShopStockHandler;
        _warrantyReads = warrantyReads;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] string? search,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateTimeOffset? beforeCreatedAt = null,
        [FromQuery] Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default,
        [FromQuery] WarrantyWorkKind? beforeWorkKind = null)
    {
        var denied = this.RequirePermission(PermissionKeys.WarrantyView);
        if (denied is not null)
        {
            return denied;
        }

        if ((beforeCreatedAt.HasValue || beforeWorkId.HasValue || beforeWorkKind.HasValue) &&
            (!beforeCreatedAt.HasValue || !beforeWorkId.HasValue || !beforeWorkKind.HasValue))
        {
            return BadRequest(new { code = "warranty.cursor_invalid", message = "beforeCreatedAt, beforeWorkKind, and beforeWorkId must be supplied together." });
        }

        var dashboard = await _warrantyReads.GetDashboardAsync(
            search,
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            beforeCreatedAt,
            beforeWorkId,
            cancellationToken,
            beforeWorkKind);
        return Ok(dashboard);
    }

    [HttpGet("claims/{id:guid}/timeline")]
    public async Task<IActionResult> GetClaimTimeline(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.WarrantyView);
        if (denied is not null)
        {
            return denied;
        }

        var timeline = await _warrantyReads.GetClaimTimelineAsync(id, cancellationToken);
        return Ok(timeline);
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchIntake(
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.WarrantyView);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { code = "warranty.query_required", message = "Search query is required." });
        }

        var results = await _warrantyReads.SearchClaimIntakeAsync(query.Trim(), cancellationToken);
        return Ok(results);
    }

    [HttpPost("claims")]
    public async Task<IActionResult> CreateClaim(
        [FromBody] CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _createClaimHandler.HandleAsync(sanitized, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return ToActionResult(result);
    }

    [HttpPost("claims/{id:guid}/review")]
    public async Task<IActionResult> BeginReview(
        [FromRoute] Guid id,
        [FromBody] WarrantyClaimActionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new BeginWarrantyClaimReviewCommand(id, RequireActorId(request.ActorId), request.ClientOperationId, request.Note);
        return ToActionResult(await _beginReviewHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("claims/{id:guid}/send-to-supplier")]
    public async Task<IActionResult> SendToSupplier(
        [FromRoute] Guid id,
        [FromBody] WarrantyClaimActionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendWarrantyClaimToSupplierCommand(id, RequireActorId(request.ActorId), request.ClientOperationId, request.Note);
        return ToActionResult(await _sendToSupplierHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("claims/{id:guid}/supplier-processing")]
    public async Task<IActionResult> MarkSupplierProcessing(
        [FromRoute] Guid id,
        [FromBody] WarrantyClaimActionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new MarkWarrantySupplierProcessingCommand(id, RequireActorId(request.ActorId), request.ClientOperationId, request.Note);
        return ToActionResult(await _supplierProcessingHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("claims/{id:guid}/resolution")]
    public async Task<IActionResult> RecordResolution(
        [FromRoute] Guid id,
        [FromBody] WarrantyResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RecordWarrantyResolutionCommand(id, request.Resolution, RequireActorId(request.ActorId), request.ClientOperationId, request.Note);
        return ToActionResult(await _recordResolutionHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("claims/{id:guid}/replacement-receipt")]
    public async Task<IActionResult> ReceiveReplacement(
        [FromRoute] Guid id,
        [FromBody] CustomerWarrantyReplacementRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ReceiveCustomerWarrantyReplacementCommand(id, RequireActorId(request.ActorId), request.ClientOperationId, request.Units, request.Note);
        return ToActionResult(await _receiveReplacementHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("claims/{id:guid}/handover")]
    public async Task<IActionResult> Handover(
        [FromRoute] Guid id,
        [FromBody] WarrantyClaimActionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new HandoverWarrantyItemCommand(id, RequireActorId(request.ActorId), request.ClientOperationId, request.Note);
        return ToActionResult(await _handoverHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("claims/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        [FromRoute] Guid id,
        [FromBody] WarrantyClaimActionRequest request,
        CancellationToken cancellationToken)
    {
        var command = new CancelWarrantyClaimCommand(id, RequireActorId(request.ActorId), request.ClientOperationId, request.Note);
        return ToActionResult(await _cancelHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("shop-stock/send")]
    public async Task<IActionResult> SendShopStockToSupplier(
        [FromBody] SendShopStockWarrantyRequest request,
        CancellationToken cancellationToken)
    {
        var command = new SendShopStockToSupplierWarrantyCommand(
            request.ProductId, request.SourceBucket, request.BaseQuantity, request.SupplierId,
            request.SourcePurchaseItemId, request.FaultDescription, RequireActorId(request.ActorId),
            request.ClientOperationId, request.InventoryUnitIds);
        return ToActionResult(await _sendShopStockHandler.HandleAsync(command, cancellationToken));
    }

    [HttpPost("shop-stock/{id:guid}/receive")]
    public async Task<IActionResult> ReceiveShopStock(
        [FromRoute] Guid id,
        [FromBody] ReceiveShopStockWarrantyRequest request,
        CancellationToken cancellationToken)
    {
        var command = new ReceiveShopStockWarrantyCommand(
            id, request.Resolution, RequireActorId(request.ActorId), request.OriginalInventoryUnitIds,
            request.ReplacementUnits, request.Note, request.ClientOperationId,
            request.SupplierCreditAmount, request.SupplierReference, request.ResolvedQuantity);
        return ToActionResult(await _receiveShopStockHandler.HandleAsync(command, cancellationToken));
    }

    private Guid RequireActorId(Guid? fallback) =>
        HttpContext.GetActorContext()?.UserId ?? fallback ?? Guid.Empty;

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok(true);
        }
        var error = result.Error!;
        var statusCode = ErrorStatus(error.Code);
        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        var error = result.Error!;
        var statusCode = ErrorStatus(error.Code);
        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private static int ErrorStatus(string code) => code switch
    {
        var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
        var c when c.Contains("active_claim") || c.Contains("locked") || c.Contains("mismatch") || c.Contains("duplicate") => StatusCodes.Status409Conflict,
        var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest
    };

}

public sealed record WarrantyClaimActionRequest(Guid ClientOperationId, string? Note = null, Guid? ActorId = null);
public sealed record WarrantyResolutionRequest(Guid ClientOperationId, EdgeRetails.Domain.Warranty.WarrantyResolutionType Resolution, string? Note = null, Guid? ActorId = null);
public sealed record CustomerWarrantyReplacementRequest(Guid ClientOperationId, IReadOnlyList<CustomerWarrantyReplacementUnitInput> Units, string? Note = null, Guid? ActorId = null);
public sealed record SendShopStockWarrantyRequest(Guid ProductId, EdgeRetails.Domain.Inventory.InventoryBucket SourceBucket, decimal BaseQuantity, Guid SupplierId, Guid? SourcePurchaseItemId, string FaultDescription, Guid ClientOperationId, IReadOnlyCollection<Guid>? InventoryUnitIds = null, Guid? ActorId = null);
public sealed record ReceiveShopStockWarrantyRequest(EdgeRetails.Domain.Warranty.WarrantyResolutionType Resolution, Guid ClientOperationId, IReadOnlyCollection<Guid>? OriginalInventoryUnitIds = null, IReadOnlyList<ReplacementSerializedUnitInput>? ReplacementUnits = null, string? Note = null, decimal? SupplierCreditAmount = null, string? SupplierReference = null, Guid? ActorId = null, decimal? ResolvedQuantity = null);
