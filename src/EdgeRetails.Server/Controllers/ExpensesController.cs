using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ExpensesController : ControllerBase
{
    private readonly IExpenseReadService _expenseReads;
    private readonly PostExpenseHandler _postExpenseHandler;
    private readonly VoidExpenseHandler _voidExpenseHandler;

    public ExpensesController(
        IExpenseReadService expenseReads,
        PostExpenseHandler postExpenseHandler,
        VoidExpenseHandler voidExpenseHandler)
    {
        _expenseReads = expenseReads;
        _postExpenseHandler = postExpenseHandler;
        _voidExpenseHandler = voidExpenseHandler;
    }

    [HttpGet]
    public async Task<IActionResult> GetExpenses(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] Guid? categoryId,
        [FromQuery] int pageSize = 50,
        [FromQuery] DateOnly? beforeExpenseDate = null,
        [FromQuery] Guid? beforeExpenseId = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.ExpensesManage);
        if (denied is not null)
        {
            return denied;
        }

        if (beforeExpenseDate.HasValue != beforeExpenseId.HasValue)
        {
            return BadRequest(new { code = "expenses.cursor_invalid", message = "beforeExpenseDate and beforeExpenseId must be supplied together." });
        }

        var query = new GetExpensesPageQuery(
            FromDate: fromDate,
            ToDate: toDate,
            CategoryId: categoryId,
            PageSize: Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500),
            BeforeExpenseDate: beforeExpenseDate,
            BeforeExpenseId: beforeExpenseId);

        var expenses = await _expenseReads.GetExpensesPageAsync(query, cancellationToken);
        return Ok(expenses);
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.ExpensesManage);
        if (denied is not null)
        {
            return denied;
        }

        var categories = await _expenseReads.GetCategoriesAsync(cancellationToken);
        return Ok(categories);
    }

    [HttpGet("subcategories")]
    public async Task<IActionResult> GetSubcategories(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.ExpensesManage);
        if (denied is not null)
        {
            return denied;
        }

        var subcategories = await _expenseReads.GetSubcategoriesAsync(cancellationToken);
        return Ok(subcategories);
    }

    [HttpPost]
    public async Task<IActionResult> PostExpense(
        [FromBody] PostExpenseRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;

        var command = new PostExpenseCommand(
            ClientOperationId: request.ClientOperationId,
            CategoryId: request.CategoryId,
            SubcategoryId: request.SubcategoryId,
            ExpenseDate: request.ExpenseDate,
            Amount: request.Amount,
            PaymentMethod: request.PaymentMethod,
            Description: request.Description,
            Reference: request.Reference,
            ActorId: actorId);

        var result = await _postExpenseHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("{id:guid}/void")]
    public async Task<IActionResult> VoidExpense(
        [FromRoute] Guid id,
        [FromBody] VoidExpenseRequest request,
        CancellationToken cancellationToken)
    {
        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var clientOperationId = request.ClientOperationId ?? request.CorrelationId;
        if (clientOperationId is null || clientOperationId == Guid.Empty ||
            (request.ClientOperationId.HasValue && request.CorrelationId.HasValue &&
                request.ClientOperationId != request.CorrelationId))
        {
            return BadRequest(new { code = "expense.void_operation_id_required",
                message = "Supply one stable operation identity for the expense void." });
        }

        var command = new VoidExpenseCommand(
            ExpenseId: id,
            ActorId: actorId,
            ClientOperationId: clientOperationId.Value,
            Reason: request.Reason);

        var result = await _voidExpenseHandler.HandleAsync(command, cancellationToken);
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
            var c when c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok(new { success = true });
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record PostExpenseRequest(
    Guid ClientOperationId,
    Guid CategoryId,
    Guid? SubcategoryId,
    DateOnly ExpenseDate,
    decimal Amount,
    ExpensePaymentMethod PaymentMethod,
    string Description,
    string? Reference = null,
    Guid? ActorId = null);

public sealed record VoidExpenseRequest(
    string Reason,
    Guid? ActorId = null,
    Guid? CorrelationId = null,
    Guid? ClientOperationId = null);
