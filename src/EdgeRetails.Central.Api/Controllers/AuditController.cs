using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/audit")]
public class AuditController : ControllerBase
{
    private readonly ICentralAuditService _auditService;
    private readonly IAdminAuthService _authService;

    public AuditController(
        ICentralAuditService auditService,
        IAdminAuthService authService)
    {
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpGet]
    public async Task<IActionResult> QueryAuditRecords(
        [FromQuery] AuditQueryDto query,
        CancellationToken cancellationToken)
    {
        // 1. Authenticate session & verify audit-view capability
        var sessionId = ExtractSessionId();
        if (sessionId == Guid.Empty)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        if (!session.Capabilities.Contains(AdminCapabilities.AuditView))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN", safeMessageKey = "auth.unauthorized_capability" });
        }

        var offset = 0;
        if (query.PageSize is < 1 or > 100 || query.Filter?.Length > 200 || query.AsOfVersion < 0 ||
            query.Cursor is not null && (!int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100000) ||
            Request.Query.Keys.Any(key => key is not ("cursor" or "pageSize" or "filter" or "asOfVersion")))
        {
            return BadRequest(new { code = "INVALID_QUERY", safeMessageKey = "query.invalid" });
        }

        // 2. Query bounded audit records
        var page = await _auditService.QueryAuditRecordsAsync(query, cancellationToken);
        if (query.AsOfVersion is not null && query.AsOfVersion != page.AsOfVersion)
        {
            return Conflict(new { code = "PROJECTION_VERSION_CHANGED", safeMessageKey = "query.projection_changed" });
        }
        return Ok(page);
    }

    private Guid ExtractSessionId()
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid ? credentials.SessionId : Guid.Empty;
    }
}
