using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/action-preview")]
public class ActionPreviewController : ControllerBase
{
    private readonly Wave4DashboardAndPortalService _portalService;

    public ActionPreviewController(Wave4DashboardAndPortalService portalService)
    {
        _portalService = portalService ?? throw new ArgumentNullException(nameof(portalService));
    }

    [HttpPost]
    public async Task<IActionResult> PreviewAction(
        [FromBody] ActionPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        var preview = await _portalService.PreviewActionAsync(request, cancellationToken);
        return Ok(preview);
    }
}
