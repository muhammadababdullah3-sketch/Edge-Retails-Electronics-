using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly AuthenticateUserHandler _authenticateUserHandler;
    private readonly EndUserSessionHandler _endUserSessionHandler;
    private readonly GetLoginAccountsHandler _getLoginAccountsHandler;

    public AuthController(
        AuthenticateUserHandler authenticateUserHandler,
        EndUserSessionHandler endUserSessionHandler,
        GetLoginAccountsHandler getLoginAccountsHandler)
    {
        _authenticateUserHandler = authenticateUserHandler;
        _endUserSessionHandler = endUserSessionHandler;
        _getLoginAccountsHandler = getLoginAccountsHandler;
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts(CancellationToken cancellationToken)
    {
        var accounts = await _getLoginAccountsHandler.HandleAsync(cancellationToken);
        return Ok(accounts);
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] AuthenticateUserCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _authenticateUserHandler.HandleAsync(command, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var statusCode = result.Error?.Code switch
        {
            "identity.role_inactive" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status401Unauthorized
        };

        return StatusCode(statusCode, new { code = result.Error?.Code, message = result.Error?.Message });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromBody] EndUserSessionCommand command,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var sanitizedCommand = command;
        if (actor is not null)
        {
            sanitizedCommand = command with { UserId = actor.UserId, SessionId = actor.SessionId };
        }

        var result = await _endUserSessionHandler.HandleAsync(sanitizedCommand, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(new { success = true });
        }

        return BadRequest(new { code = result.Error?.Code, message = result.Error?.Message });
    }

    [HttpGet("session")]
    public IActionResult GetCurrentSession()
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        return Ok(new
        {
            userId = actor.UserId,
            sessionId = actor.SessionId,
            displayName = actor.DisplayName,
            roleId = actor.RoleId,
            roleName = actor.RoleName,
            permissions = actor.Permissions
        });
    }
}
