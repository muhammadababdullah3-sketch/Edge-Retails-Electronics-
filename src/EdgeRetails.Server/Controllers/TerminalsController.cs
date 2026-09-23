using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Terminals;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TerminalsController : ControllerBase
{
    private readonly RegisterTerminalHandler _registerHandler;
    private readonly TerminalHeartbeatHandler _heartbeatHandler;
    private readonly UpdateTerminalStatusHandler _updateStatusHandler;
    private readonly AuthoritativeRevalidationHandler _revalidationHandler;

    public TerminalsController(
        RegisterTerminalHandler registerHandler,
        TerminalHeartbeatHandler heartbeatHandler,
        UpdateTerminalStatusHandler updateStatusHandler,
        AuthoritativeRevalidationHandler revalidationHandler)
    {
        _registerHandler = registerHandler;
        _heartbeatHandler = heartbeatHandler;
        _updateStatusHandler = updateStatusHandler;
        _revalidationHandler = revalidationHandler;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterTerminalCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _registerHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(
        [FromBody] TerminalHeartbeatCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _heartbeatHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("status")]
    public async Task<IActionResult> UpdateStatus(
        [FromBody] UpdateTerminalStatusCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _updateStatusHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("revalidate")]
    public async Task<IActionResult> Revalidate(
        [FromBody] AuthoritativeRevalidationQuery query,
        CancellationToken cancellationToken)
    {
        var result = await _revalidationHandler.HandleAsync(query, cancellationToken);
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
            var c when c.Contains("quota") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}
