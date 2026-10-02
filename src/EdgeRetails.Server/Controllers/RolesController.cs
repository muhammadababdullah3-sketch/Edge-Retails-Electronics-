using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RolesController : ControllerBase
{
    private readonly IIdentityReadRepository _identity;
    private readonly IApplicationPermissionAuthorizer _authorizer;

    public RolesController(
        IIdentityReadRepository identity,
        IApplicationPermissionAuthorizer authorizer)
    {
        _identity = identity;
        _authorizer = authorizer;
    }

    [HttpGet]
    public async Task<IActionResult> GetRoles(CancellationToken cancellationToken)
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

        var roles = await _identity.GetRolesAsync(cancellationToken);
        return Ok(roles.Select(r => new
        {
            id = r.Id,
            name = r.Name,
            isActive = r.IsActive
        }));
    }

    [HttpGet("permissions")]
    public async Task<IActionResult> GetPermissions(CancellationToken cancellationToken)
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

        var permissions = await _identity.GetPermissionsAsync(cancellationToken);
        return Ok(permissions.Select(p => new
        {
            id = p.Id,
            key = p.Key,
            description = p.Description
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetRole([FromRoute] Guid id, CancellationToken cancellationToken)
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

        var role = await _identity.GetRoleAsync(id, cancellationToken);
        if (role is null)
        {
            return NotFound(new { code = "identity.role_not_found", message = $"Role '{id}' was not found." });
        }

        return Ok(new
        {
            id = role.Id,
            name = role.Name,
            isActive = role.IsActive
        });
    }

    [HttpGet("{id:guid}/permissions")]
    public async Task<IActionResult> GetRolePermissions([FromRoute] Guid id, CancellationToken cancellationToken)
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

        var role = await _identity.GetRoleAsync(id, cancellationToken);
        if (role is null)
        {
            return NotFound(new { code = "identity.role_not_found", message = $"Role '{id}' was not found." });
        }

        var permissions = await _identity.GetRolePermissionKeysAsync(id, cancellationToken);
        return Ok(new
        {
            roleId = id,
            permissions = permissions
        });
    }
}
