using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Middleware;

internal static class ControllerPermissionExtensions
{
    public static IActionResult? RequirePermission(
        this ControllerBase controller,
        string permissionKey)
    {
        var actor = controller.HttpContext.GetActorContext();
        if (actor is null)
        {
            return controller.Unauthorized(new
            {
                code = "auth.session_missing",
                message = "No authenticated user session."
            });
        }

        return actor.Permissions.Contains(permissionKey)
            ? null
            : controller.StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "authorization.denied",
                message = $"Permission '{permissionKey}' is required."
            });
    }
}
