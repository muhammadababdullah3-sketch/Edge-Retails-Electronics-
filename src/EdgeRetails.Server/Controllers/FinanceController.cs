using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class FinanceController : ControllerBase
{
    private readonly CreateSupplierPaymentHandler _paymentHandler;
    private readonly CreateSupplierRefundHandler _refundHandler;
    private readonly ReverseSupplierPaymentHandler _reversePaymentHandler;
    private readonly ReverseSupplierRefundHandler _reverseRefundHandler;
    private readonly OpenCashSessionHandler _openCashSessionHandler;
    private readonly ICashMovementService _cashMovementService;
    private readonly ISupplierAccountReadService _supplierAccountReads;

    public FinanceController(
        CreateSupplierPaymentHandler paymentHandler,
        CreateSupplierRefundHandler refundHandler,
        ReverseSupplierPaymentHandler reversePaymentHandler,
        ReverseSupplierRefundHandler reverseRefundHandler,
        OpenCashSessionHandler openCashSessionHandler,
        ICashMovementService cashMovementService,
        ISupplierAccountReadService supplierAccountReads)
    {
        _paymentHandler = paymentHandler;
        _refundHandler = refundHandler;
        _reversePaymentHandler = reversePaymentHandler;
        _reverseRefundHandler = reverseRefundHandler;
        _openCashSessionHandler = openCashSessionHandler;
        _cashMovementService = cashMovementService;
        _supplierAccountReads = supplierAccountReads;
    }

    [HttpGet("suppliers/{supplierId:guid}/workspace")]
    public async Task<IActionResult> GetSupplierWorkspace(
        [FromRoute] Guid supplierId,
        [FromQuery] int pageSize = 200,
        [FromQuery] DateTimeOffset? beforeOccurredAt = null,
        [FromQuery] DateTimeOffset? beforeCreatedAt = null,
        [FromQuery] Guid? beforeEntryId = null,
        [FromQuery] DateTimeOffset? beforePaymentPaidAt = null,
        [FromQuery] Guid? beforePaymentId = null,
        [FromQuery] DateTimeOffset? beforeRefundReceivedAt = null,
        [FromQuery] Guid? beforeRefundId = null,
        [FromQuery] string? beforeProductName = null,
        [FromQuery] Guid? beforeProductId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SupplierAccountView);
        if (denied is not null)
        {
            return denied;
        }

        if (HasPartialCursor(beforeOccurredAt.HasValue, beforeCreatedAt.HasValue, beforeEntryId.HasValue) ||
            HasPartialCursor(beforePaymentPaidAt.HasValue, beforePaymentId.HasValue) ||
            HasPartialCursor(beforeRefundReceivedAt.HasValue, beforeRefundId.HasValue) ||
            (string.IsNullOrWhiteSpace(beforeProductName) != !beforeProductId.HasValue))
        {
            return BadRequest(new { code = "finance.cursor_invalid", message = "Each workspace cursor must include all of its fields." });
        }

        var workspace = await _supplierAccountReads.GetWorkspaceAsync(
            supplierId,
            Math.Clamp(pageSize, 1, 500),
            beforeOccurredAt,
            beforeCreatedAt,
            beforeEntryId,
            cancellationToken,
            beforePaymentPaidAt,
            beforePaymentId,
            beforeRefundReceivedAt,
            beforeRefundId,
            beforeProductName,
            beforeProductId);
        return Ok(workspace);
    }

    private static bool HasPartialCursor(params bool[] fields) =>
        fields.Any(x => x) && fields.Any(x => !x);

    [HttpPost("supplier-payment")]
    public async Task<IActionResult> CreateSupplierPayment(
        [FromBody] CreateSupplierPaymentCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _paymentHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("supplier-refund")]
    public async Task<IActionResult> CreateSupplierRefund(
        [FromBody] CreateSupplierRefundCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _refundHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("supplier-payment/{id:guid}/reverse")]
    public async Task<IActionResult> ReverseSupplierPayment(
        [FromRoute] Guid id,
        [FromBody] ReverseSupplierSettlementRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = HttpContext.GetActorContext()?.UserId ?? request.ActorId ?? Guid.Empty;
        var result = await _reversePaymentHandler.HandleAsync(
            new ReverseSupplierPaymentCommand(id, request.Reason, actorId, request.ClientOperationId),
            cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("supplier-refund/{id:guid}/reverse")]
    public async Task<IActionResult> ReverseSupplierRefund(
        [FromRoute] Guid id,
        [FromBody] ReverseSupplierSettlementRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = HttpContext.GetActorContext()?.UserId ?? request.ActorId ?? Guid.Empty;
        var result = await _reverseRefundHandler.HandleAsync(
            new ReverseSupplierRefundCommand(id, request.Reason, actorId, request.ClientOperationId),
            cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("cash-session/open")]
    public async Task<IActionResult> OpenCashSession(
        [FromBody] OpenCashSessionCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _openCashSessionHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("cash-movement")]
    public async Task<IActionResult> RecordCashMovement(
        [FromBody] RecordCashMovementRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? request with { ActorId = actor.UserId } : request;
        var result = await _cashMovementService.RecordAsync(sanitized, cancellationToken);
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
            var c when c.Contains("exceeds_payable") => StatusCodes.Status409Conflict,
            var c when c.Contains("already_open") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok(true);
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") || c.Contains("locked") || c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record ReverseSupplierSettlementRequest(
    Guid ClientOperationId,
    string Reason,
    Guid? ActorId = null);
