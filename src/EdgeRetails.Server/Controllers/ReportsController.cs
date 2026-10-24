using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportingReadService _reportingReads;

    public ReportsController(IReportingReadService reportingReads)
    {
        _reportingReads = reportingReads;
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(
        [FromQuery] ReportingPeriodKind mode = ReportingPeriodKind.Daily,
        [FromQuery] DateOnly? date = null,
        [FromQuery] int? month = null,
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.ReportsView);
        if (denied is not null)
        {
            return denied;
        }

        var targetDate = date ?? DateOnly.FromDateTime(DateTime.Today);
        var targetMonth = month ?? targetDate.Month;
        var targetYear = year ?? targetDate.Year;

        var snapshot = await _reportingReads.GetSnapshotAsync(
            mode,
            targetDate,
            targetMonth,
            targetYear,
            cancellationToken);

        return Ok(snapshot);
    }

    [HttpGet("financial-valuation")]
    public async Task<IActionResult> GetFinancialValuation(
        [FromServices] EdgeRetailsDbContext db,
        CancellationToken cancellationToken = default)
    {
        var actor = HttpContext.GetActorContext();
        if (actor is null)
        {
            return Unauthorized(new { code = "auth.session_missing", message = "No authenticated user session." });
        }

        // Cashiers are strictly forbidden from viewing financial cost valuations
        if (string.Equals(actor.RoleName, "Cashier", StringComparison.OrdinalIgnoreCase) ||
            (!actor.Permissions.Contains(PermissionKeys.ReportsView) &&
             !string.Equals(actor.RoleName, "Supervisor", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(actor.RoleName, "Manager", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(actor.RoleName, "Owner", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "reports.financial_valuation_forbidden",
                message = "Access to financial valuation reports requires Supervisor or Manager authority."
            });
        }

        var products = await db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);

        var costStates = await db.ProductCostStates.AsNoTracking()
            .ToDictionaryAsync(c => c.ProductId, cancellationToken);

        var stockBalances = await db.StockBalances.AsNoTracking()
            .ToDictionaryAsync(s => s.ProductId, cancellationToken);

        var lines = products.Select(p =>
        {
            var stock = stockBalances.TryGetValue(p.Id, out var sb) ? sb.SellableQty : 0m;
            var cost = (costStates.TryGetValue(p.Id, out var cs) && cs.MovingAverageCost > 0)
                ? cs.MovingAverageCost
                : (p.ReferencePurchaseCost ?? 0m);
            var retail = p.DefaultSalePrice;
            var totalCost = stock * cost;
            var totalRetail = stock * retail;
            var marginPct = totalRetail > 0 ? ((totalRetail - totalCost) / totalRetail) * 100m : 0m;

            return new FinancialValuationLineDto(
                ProductId: p.Id,
                Sku: p.Sku ?? string.Empty,
                ProductName: p.Name,
                InStockQuantity: stock,
                UnitCost: cost,
                RetailPrice: retail,
                TotalCost: totalCost,
                TotalRetailValue: totalRetail,
                MarginPercentage: decimal.Round(marginPct, 2, MidpointRounding.AwayFromZero));
        }).ToList();

        var report = new FinancialValuationReportDto(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            TotalProductsCount: lines.Count,
            TotalUnitsInStock: (int)lines.Sum(l => l.InStockQuantity),
            TotalRetailValuation: lines.Sum(l => l.TotalRetailValue),
            TotalCostValuation: lines.Sum(l => l.TotalCost),
            TotalEstimatedMargin: lines.Sum(l => l.TotalRetailValue) - lines.Sum(l => l.TotalCost),
            ValuationLines: lines);

        return Ok(report);
    }

    [HttpGet("daily")]
    public Task<IActionResult> GetDailyReport(
        [FromQuery] DateOnly? date = null,
        CancellationToken cancellationToken = default) =>
        GetSnapshot(ReportingPeriodKind.Daily, date, null, null, cancellationToken);

    [HttpGet("monthly")]
    public Task<IActionResult> GetMonthlyReport(
        [FromQuery] int? month = null,
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default) =>
        GetSnapshot(ReportingPeriodKind.Monthly, null, month, year, cancellationToken);

    [HttpGet("yearly")]
    public Task<IActionResult> GetYearlyReport(
        [FromQuery] int? year = null,
        CancellationToken cancellationToken = default) =>
        GetSnapshot(ReportingPeriodKind.Yearly, null, null, year, cancellationToken);
}
