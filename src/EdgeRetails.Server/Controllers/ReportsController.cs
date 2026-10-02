using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

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
