using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly Wave4DashboardAndPortalService _dashboardService;

    public DashboardController(Wave4DashboardAndPortalService dashboardService)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
    }

    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics(CancellationToken cancellationToken)
    {
        var metrics = await _dashboardService.GetDashboardMetricsAsync(cancellationToken);
        return Ok(metrics);
    }

    [HttpGet("customer360/{shopId:guid}")]
    public async Task<IActionResult> GetCustomer360([FromRoute] Guid shopId, CancellationToken cancellationToken)
    {
        var customer360 = await _dashboardService.GetCustomer360Async(shopId, cancellationToken);
        return Ok(customer360);
    }

    [HttpGet("queues/{queueType}")]
    public async Task<IActionResult> GetQueue(
        [FromRoute] string queueType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var items = await _dashboardService.GetDashboardQueueAsync(queueType, page, pageSize, cancellationToken);
        return Ok(items);
    }

    [HttpGet("~/api/admin/v1/queues/{queueType}")]
    public async Task<IActionResult> GetCanonicalQueue(
        [FromRoute] string queueType,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? filter = null,
        [FromQuery] long? asOfVersion = null,
        CancellationToken cancellationToken = default)
    {
        var offset = 0;
        if (pageSize is < 1 or > 100 || filter?.Length > 200 || asOfVersion < 0 ||
            cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100000) ||
            Request.Query.Keys.Any(key => key is not ("cursor" or "pageSize" or "filter" or "asOfVersion") || Request.Query[key].Count != 1))
        {
            return BadRequest(new { code = "INVALID_QUERY", safeMessageKey = "query.invalid" });
        }

        var page = await _dashboardService.GetDashboardQueuePageAsync(queueType, filter, pageSize, offset, asOfVersion, cancellationToken);
        if (asOfVersion is not null && asOfVersion != page.ProjectionVersion)
        {
            return Conflict(new { code = "PROJECTION_VERSION_CHANGED", safeMessageKey = "query.projection_changed" });
        }
        return Ok(new
        {
            items = page.Items,
            nextCursor = page.NextCursor,
            asOfVersion = page.ProjectionVersion,
            authorizedCount = page.AuthorizedCount,
            projectionRevision = page.ProjectionVersion
        });
    }
}
