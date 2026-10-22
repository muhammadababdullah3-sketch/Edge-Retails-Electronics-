using EdgeRetails.Central.Api.Services;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/notifications")]
public sealed class NotificationsController(ICentralDbContext db, CentralOutboxWorkerState worker) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status()
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED" });
        }
        if (!session.Capabilities.Contains("notifications.view", StringComparer.OrdinalIgnoreCase))
        {
            return Forbid();
        }
        return Ok(worker.Snapshot());
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED" });
        }

        if (page < 1 || page > 100000 || pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new { code = "INVALID_PAGINATION" });
        }

        var capabilities = session.Capabilities.ToArray();
        var query = db.AdminNotifications.AsNoTracking().Where(n => capabilities.Contains(n.RequiredCapability));
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(n => n.OccurredAtUtc).ThenBy(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new { n.Id, n.Title, n.Category, n.TargetType, n.TargetId, n.ShopId, n.OccurredAtUtc })
            .ToListAsync(cancellationToken);
        return Ok(new { page, pageSize, totalCount = count, items });
    }
}
