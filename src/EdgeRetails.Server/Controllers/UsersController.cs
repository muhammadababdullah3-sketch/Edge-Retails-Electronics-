using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class UsersController : ControllerBase
{
    private readonly IIdentityReadRepository _identity;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly CreateCashierHandler _createCashier;

    public UsersController(
        IIdentityReadRepository identity,
        IApplicationPermissionAuthorizer authorizer,
        CreateCashierHandler createCashier)
    {
        _identity = identity;
        _authorizer = authorizer;
        _createCashier = createCashier;
    }

    [HttpPost("cashiers")]
    public async Task<IActionResult> CreateCashier([FromBody] CreateCashierRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }
        if (HttpContext.Items["CurrentTerminal"] is not Terminal terminal)
        {
            return Unauthorized(new { code = "auth.terminal_id_missing", message = "An authenticated terminal is required." });
        }
        var result = await _createCashier.HandleAsync(new(request.DisplayName, request.Pin, request.ClientOperationId,
            actor.UserId, actor.SessionId, terminal.Id, request.CorrelationId), cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }
        var error = result.Error!;
        var status = error.Code.StartsWith("auth.", StringComparison.Ordinal) ||
            error.Code.StartsWith("authorization.", StringComparison.Ordinal) ? 403 :
            error.Code is "idempotency.payload_mismatch" or "identity.display_name_duplicate" or "operation.outcome_unknown" ? 409 : 400;
        return StatusCode(status, new { code = error.Code, message = error.Message });
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers(CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        var authResult = await _authorizer.AuthorizeAsync(actor.UserId, PermissionKeys.SettingsManage, cancellationToken);
        if (!authResult.IsSuccess)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = authResult.Error?.Code, message = authResult.Error?.Message });
        }

        var users = await _identity.GetActiveUsersAsync(cancellationToken);
        return Ok(users.Select(u => new
        {
            id = u.Id,
            displayName = u.DisplayName,
            roleId = u.RoleId,
            status = u.Status
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetUser([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        var authResult = await _authorizer.AuthorizeAsync(actor.UserId, PermissionKeys.SettingsManage, cancellationToken);
        if (!authResult.IsSuccess)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = authResult.Error?.Code, message = authResult.Error?.Message });
        }

        var user = await _identity.GetUserAsync(id, cancellationToken);
        if (user is null)
        {
            return NotFound(new { code = "identity.user_not_found", message = $"User '{id}' was not found." });
        }

        return Ok(new
        {
            id = user.Id,
            displayName = user.DisplayName,
            roleId = user.RoleId,
            status = user.Status
        });
    }

    [HttpGet("{id:guid}/permissions")]
    public async Task<IActionResult> GetUserPermissions([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        var authResult = await _authorizer.AuthorizeAsync(actor.UserId, PermissionKeys.SettingsManage, cancellationToken);
        if (!authResult.IsSuccess)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = authResult.Error?.Code, message = authResult.Error?.Message });
        }

        var permissions = await _identity.GetEffectivePermissionKeysAsync(id, cancellationToken);
        return Ok(new
        {
            userId = id,
            permissions = permissions
        });
    }
}

public sealed record CreateCashierRequest(string DisplayName, string Pin, Guid ClientOperationId, Guid CorrelationId)
{
    public override string ToString() => $"CreateCashierRequest {{ ClientOperationId = {ClientOperationId} }}";
}
