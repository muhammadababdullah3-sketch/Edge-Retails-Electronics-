using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class FinanceController : ControllerBase
{
    private readonly CreateSupplierPaymentHandler _paymentHandler;
    private readonly CreateSupplierRefundHandler _refundHandler;

    public FinanceController(
        CreateSupplierPaymentHandler paymentHandler,
        CreateSupplierRefundHandler refundHandler)
    {
        _paymentHandler = paymentHandler;
        _refundHandler = refundHandler;
    }

    [HttpPost("supplier-payment")]
    public async Task<IActionResult> CreateSupplierPayment(
        [FromBody] CreateSupplierPaymentCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _paymentHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("supplier-refund")]
    public async Task<IActionResult> CreateSupplierRefund(
        [FromBody] CreateSupplierRefundCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _refundHandler.HandleAsync(command, cancellationToken);
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
            var c when c.Contains("exceeds_payable") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}
