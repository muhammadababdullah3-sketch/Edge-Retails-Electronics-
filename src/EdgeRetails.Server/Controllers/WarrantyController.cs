using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Warranty;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class WarrantyController : ControllerBase
{
    private readonly CreateWarrantyClaimHandler _createClaimHandler;

    public WarrantyController(CreateWarrantyClaimHandler createClaimHandler)
    {
        _createClaimHandler = createClaimHandler;
    }

    [HttpPost("claims")]
    public async Task<IActionResult> CreateClaim(
        [FromBody] CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _createClaimHandler.HandleAsync(command, cancellationToken);
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("active_claim") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}
