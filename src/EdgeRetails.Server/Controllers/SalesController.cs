using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Sales;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class SalesController : ControllerBase
{
    private readonly CompleteSaleHandler _completeSaleHandler;
    private readonly CompletePosDraftHandler _completePosDraftHandler;
    private readonly CreateSaleReturnHandler _saleReturnHandler;
    private readonly CommercialExchangeHandler _exchangeHandler;

    public SalesController(
        CompleteSaleHandler completeSaleHandler,
        CompletePosDraftHandler completePosDraftHandler,
        CreateSaleReturnHandler saleReturnHandler,
        CommercialExchangeHandler exchangeHandler)
    {
        _completeSaleHandler = completeSaleHandler;
        _completePosDraftHandler = completePosDraftHandler;
        _saleReturnHandler = saleReturnHandler;
        _exchangeHandler = exchangeHandler;
    }

    [HttpPost("complete")]
    public async Task<IActionResult> CompleteSale(
        [FromBody] CompleteSaleCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _completeSaleHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("drafts/complete")]
    public async Task<IActionResult> CompletePosDraft(
        [FromBody] CompletePosDraftCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _completePosDraftHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("return")]
    public async Task<IActionResult> CreateSaleReturn(
        [FromBody] CreateSaleReturnCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _saleReturnHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("exchange")]
    public async Task<IActionResult> ExecuteCommercialExchange(
        [FromBody] CommercialExchangeCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _exchangeHandler.HandleAsync(command, cancellationToken);
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
            var c when c.Contains("insufficient") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}
