using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/portal")]
public class PortalController : ControllerBase
{
    private readonly Wave4DashboardAndPortalService _portalService;
    private readonly IAdminAuthService _authService;

    public PortalController(
        Wave4DashboardAndPortalService portalService,
        IAdminAuthService authService)
    {
        _portalService = portalService ?? throw new ArgumentNullException(nameof(portalService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    [HttpGet("~/api/admin/v1/sessions")]
    public async Task<IActionResult> ListSessions(
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? filter = null,
        [FromQuery] long? asOfVersion = null,
        CancellationToken cancellationToken = default)
    {
        var sessionId = ExtractSessionId();
        var offset = 0;
        if (sessionId == Guid.Empty || pageSize is < 1 or > 100 || filter?.Length > 200 || asOfVersion < 0 ||
            cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100000) ||
            Request.Query.Keys.Any(key => key is not ("cursor" or "pageSize" or "filter" or "asOfVersion")))
        {
            return BadRequest(new { code = "INVALID_QUERY", safeMessageKey = "query.invalid" });
        }

        var page = await _portalService.GetAdminSessionsPageAsync(sessionId, filter, pageSize, offset, asOfVersion, cancellationToken);
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

    [HttpGet("session")]
    public async Task<IActionResult> GetSessionContext(CancellationToken cancellationToken)
    {
        SetNoStoreHeaders();
        var sessionId = ExtractSessionId();
        if (sessionId == Guid.Empty)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        var context = await _portalService.GetPortalSessionContextAsync(sessionId, cancellationToken);
        return Ok(context);
    }

    [HttpPost("sessions/revoke")]
    public async Task<IActionResult> RevokeSession([FromQuery] Guid? targetSessionId, CancellationToken cancellationToken)
    {
        var sessionId = ExtractSessionId();
        if (sessionId == Guid.Empty)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        var success = await _portalService.RevokeSessionAsync(sessionId, targetSessionId ?? Guid.Empty, cancellationToken);
        return Ok(new { success });
    }

    private void SetNoStoreHeaders()
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";
    }

    private Guid ExtractSessionId()
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid ? credentials.SessionId : Guid.Empty;
    }
}
