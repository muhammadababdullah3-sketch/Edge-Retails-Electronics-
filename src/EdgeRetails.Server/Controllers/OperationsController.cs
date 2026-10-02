using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class OperationsController : ControllerBase
{
    private readonly OperationStatusQueryHandler _operationStatusHandler;

    public OperationsController(OperationStatusQueryHandler operationStatusHandler)
    {
        _operationStatusHandler = operationStatusHandler;
    }

    [HttpGet("{clientOperationId:guid}")]
    public async Task<IActionResult> GetOperationStatus(
        [FromRoute] Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (clientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "validation.client_operation_id_required", message = "ClientOperationId cannot be empty." });
        }

        var terminal = HttpContext.Items["CurrentTerminal"] as Terminal;
        if (terminal is null)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                code = "auth.terminal_id_missing",
                message = "X-Terminal-Id header is required for authenticated terminal requests."
            });
        }

        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return StatusCode(StatusCodes.Status401Unauthorized, new
            {
                code = "auth.session_missing",
                message = "X-Session-Id header is required for authenticated requests."
            });
        }

        var query = new OperationStatusQuery(
            ClientOperationId: clientOperationId,
            ActorId: actor.UserId,
            TerminalId: terminal.Id,
            RequireIdentityScope: true,
            RequireCanonicalOutcome: true);

        var result = await _operationStatusHandler.HandleAsync(query, cancellationToken);

        if (result.IsSuccess)
        {
            return Ok(result.Value?.OperationType == EdgeRetails.Application.Features.Identity.CreateCashierHandler.OperationType
                ? result.Value with { PayloadFingerprint = null }
                : result.Value);
        }

        var statusCode = result.Error?.Code switch
        {
            var c when c != null && (c.StartsWith("auth.") || c.StartsWith("authorization.")) => StatusCodes.Status403Forbidden,
            var c when c != null && c.Contains("not_found") => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = result.Error?.Code, message = result.Error?.Message });
    }
}
