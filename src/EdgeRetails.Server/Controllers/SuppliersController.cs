using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SuppliersController : ControllerBase
{
    private readonly IPartyDirectoryReadService _partyReads;
    private readonly GetSuppliersHandler _getSuppliersHandler;
    private readonly SaveSupplierHandler _saveSupplierHandler;
    private readonly ISupplierAccountReadService _supplierAccountReads;

    public SuppliersController(
        IPartyDirectoryReadService partyReads,
        GetSuppliersHandler getSuppliersHandler,
        SaveSupplierHandler saveSupplierHandler,
        ISupplierAccountReadService supplierAccountReads)
    {
        _partyReads = partyReads;
        _getSuppliersHandler = getSuppliersHandler;
        _saveSupplierHandler = saveSupplierHandler;
        _supplierAccountReads = supplierAccountReads;
    }

    [HttpGet]
    public async Task<IActionResult> GetSuppliers(
        [FromQuery] string? search,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? beforeName = null,
        [FromQuery] Guid? beforeSupplierId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SuppliersManage);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(beforeName) != !beforeSupplierId.HasValue)
        {
            return BadRequest(new { code = "suppliers.cursor_invalid", message = "Both beforeName and beforeSupplierId are required for a supplier cursor." });
        }

        var suppliers = await _partyReads.GetSuppliersAsync(
            search,
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            cancellationToken,
            beforeName,
            beforeSupplierId);
        return Ok(suppliers);
    }

    [HttpGet("list")]
    public async Task<IActionResult> GetSupplierList(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SuppliersManage);
        if (denied is not null)
        {
            return denied;
        }

        var suppliers = await _getSuppliersHandler.HandleAsync(includeInactive, cancellationToken);
        return Ok(suppliers);
    }

    [HttpGet("{id:guid}/workspace")]
    public async Task<IActionResult> GetSupplierWorkspace(
        [FromRoute] Guid id,
        [FromQuery] int pageSize = 50,
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
            return BadRequest(new { code = "suppliers.cursor_invalid", message = "Each workspace cursor must include all of its fields." });
        }

        var workspace = await _supplierAccountReads.GetWorkspaceAsync(
            id,
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
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

    [HttpPost]
    public async Task<IActionResult> CreateSupplier(
        [FromBody] SaveSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var command = new SaveSupplierCommand(
            SupplierId: null,
            Name: request.Name,
            Phone: request.Phone,
            City: request.City,
            Address: request.Address,
            IsActive: request.IsActive,
            ActorId: actorId,
            CorrelationId: correlationId,
            Notes: request.Notes,
            ExplicitDealerPrefix: request.ExplicitDealerPrefix,
            ClientOperationId: request.ClientOperationId,
            TerminalId: (HttpContext.Items["CurrentTerminal"] as Terminal)?.Id,
            SessionId: actor?.SessionId);

        var result = await _saveSupplierHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateSupplier(
        [FromRoute] Guid id,
        [FromBody] SaveSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var command = new SaveSupplierCommand(
            SupplierId: id,
            Name: request.Name,
            Phone: request.Phone,
            City: request.City,
            Address: request.Address,
            IsActive: request.IsActive,
            ActorId: actorId,
            CorrelationId: correlationId,
            Notes: request.Notes,
            ExplicitDealerPrefix: request.ExplicitDealerPrefix,
            ClientOperationId: request.ClientOperationId,
            TerminalId: (HttpContext.Items["CurrentTerminal"] as Terminal)?.Id,
            SessionId: actor?.SessionId);

        var result = await _saveSupplierHandler.HandleAsync(command, cancellationToken);
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
            var c when c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record SaveSupplierRequest(
    string Name,
    string? Phone = null,
    string? City = null,
    string? Address = null,
    bool IsActive = true,
    string? Notes = null,
    string? ExplicitDealerPrefix = null,
    Guid? ActorId = null,
    Guid? CorrelationId = null,
    Guid? ClientOperationId = null);
