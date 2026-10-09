using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CustomersController : ControllerBase
{
    private readonly IPartyDirectoryReadService _partyReads;
    private readonly GetCustomersHandler _getCustomersHandler;
    private readonly SaveCustomerHandler _saveCustomerHandler;
    private readonly SetCustomerSuspensionHandler _suspensionHandler;

    public CustomersController(
        IPartyDirectoryReadService partyReads,
        GetCustomersHandler getCustomersHandler,
        SaveCustomerHandler saveCustomerHandler,
        SetCustomerSuspensionHandler suspensionHandler)
    {
        _partyReads = partyReads;
        _getCustomersHandler = getCustomersHandler;
        _saveCustomerHandler = saveCustomerHandler;
        _suspensionHandler = suspensionHandler;
    }

    [HttpPost("{id:guid}/suspension")]
    public async Task<IActionResult> SetSuspension(Guid id, SetCustomerSuspensionRequest request, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var terminal = HttpContext.Items["CurrentTerminal"] as Terminal;
        return ToActionResult(await _suspensionHandler.HandleAsync(new(request.ClientOperationId, id,
            request.IsSuspended, actor?.UserId ?? Guid.Empty, terminal?.Id, actor?.SessionId), cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> GetCustomers(
        [FromQuery] string? search,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? beforeName = null,
        [FromQuery] Guid? beforeCustomerId = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.CustomersManage);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(beforeName) != !beforeCustomerId.HasValue)
        {
            return BadRequest(new { code = "customers.cursor_invalid", message = "Both beforeName and beforeCustomerId are required for a customer cursor." });
        }

        var customers = await _partyReads.GetCustomersAsync(
            search,
            Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            cancellationToken,
            beforeName,
            beforeCustomerId,
            includeInactive);
        return Ok(customers);
    }

    [HttpGet("list")]
    public async Task<IActionResult> GetCustomerList(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.CustomersManage);
        if (denied is not null)
        {
            return denied;
        }

        var customers = await _getCustomersHandler.HandleAsync(includeInactive, cancellationToken);
        return Ok(customers);
    }

    [HttpPost]
    public async Task<IActionResult> CreateCustomer(
        [FromBody] SaveCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var command = new SaveCustomerCommand(
            CustomerId: null,
            Name: request.Name,
            Phone: request.Phone,
            Address: request.Address,
            IsActive: request.IsActive,
            ActorId: actorId,
            CorrelationId: correlationId,
            Notes: request.Notes,
            ClientOperationId: request.ClientOperationId,
            TerminalId: (HttpContext.Items["CurrentTerminal"] as Terminal)?.Id,
            SessionId: actor?.SessionId,
            PreserveActivityStatus: request.PreserveActivityStatus);

        var result = await _saveCustomerHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateCustomer(
        [FromRoute] Guid id,
        [FromBody] SaveCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var correlationId = request.CorrelationId ?? Guid.NewGuid();

        var command = new SaveCustomerCommand(
            CustomerId: id,
            Name: request.Name,
            Phone: request.Phone,
            Address: request.Address,
            IsActive: request.IsActive,
            ActorId: actorId,
            CorrelationId: correlationId,
            Notes: request.Notes,
            ClientOperationId: request.ClientOperationId,
            TerminalId: (HttpContext.Items["CurrentTerminal"] as Terminal)?.Id,
            SessionId: actor?.SessionId,
            PreserveActivityStatus: true);

        var result = await _saveCustomerHandler.HandleAsync(command, cancellationToken);
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

public sealed record SaveCustomerRequest(
    string Name,
    string? Phone = null,
    string? Address = null,
    bool IsActive = true,
    string? Notes = null,
    Guid? ActorId = null,
    Guid? CorrelationId = null,
    Guid? ClientOperationId = null,
    bool PreserveActivityStatus = false);

public sealed record SetCustomerSuspensionRequest(Guid ClientOperationId, bool IsSuspended);
