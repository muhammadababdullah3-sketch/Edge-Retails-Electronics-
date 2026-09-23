using EdgeRetails.Application.Features.Terminals;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SystemController : ControllerBase
{
    private readonly OperationStatusQueryHandler _operationStatusHandler;

    public SystemController(OperationStatusQueryHandler operationStatusHandler)
    {
        _operationStatusHandler = operationStatusHandler;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "Healthy",
            timestamp = DateTimeOffset.UtcNow,
            protocolVersion = TerminalProtocol.CurrentProtocolVersion
        });
    }

    [HttpGet("version")]
    public IActionResult Version()
    {
        return Ok(new
        {
            server = "EdgeRetails.Server",
            version = "1.0.0",
            protocolVersion = TerminalProtocol.CurrentProtocolVersion,
            minSupportedProtocolVersion = TerminalProtocol.MinimumSupportedProtocolVersion
        });
    }

    [HttpGet("operations/{clientOperationId:guid}")]
    public async Task<IActionResult> GetOperationStatus(
        [FromRoute] Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        var result = await _operationStatusHandler.HandleAsync(
            new OperationStatusQuery(clientOperationId), cancellationToken);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return BadRequest(new { code = result.Error?.Code, message = result.Error?.Message });
    }
}
