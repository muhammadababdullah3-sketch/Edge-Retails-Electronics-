using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Purchasing;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class PurchasingController : ControllerBase
{
    private readonly CreatePurchaseHandler _createPurchaseHandler;
    private readonly CreatePurchaseReturnHandler _purchaseReturnHandler;
    private readonly VoidPurchaseHandler _voidPurchaseHandler;

    public PurchasingController(
        CreatePurchaseHandler createPurchaseHandler,
        CreatePurchaseReturnHandler purchaseReturnHandler,
        VoidPurchaseHandler voidPurchaseHandler)
    {
        _createPurchaseHandler = createPurchaseHandler;
        _purchaseReturnHandler = purchaseReturnHandler;
        _voidPurchaseHandler = voidPurchaseHandler;
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreatePurchase(
        [FromBody] CreatePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _createPurchaseHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("return")]
    public async Task<IActionResult> CreatePurchaseReturn(
        [FromBody] CreatePurchaseReturnCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _purchaseReturnHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("void")]
    public async Task<IActionResult> VoidPurchase(
        [FromBody] VoidPurchaseCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _voidPurchaseHandler.HandleAsync(command, cancellationToken);
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
            var c when c.Contains("invalid_status") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}
