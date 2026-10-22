using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAdminAuthService _authService;
    private readonly IStepUpService _stepUpService;

    public AuthController(
        IAdminAuthService authService,
        IStepUpService stepUpService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _stepUpService = stepUpService ?? throw new ArgumentNullException(nameof(stepUpService));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request, CancellationToken cancellationToken)
    {
        var challenge = await _authService.StartLoginAsync(request, cancellationToken);
        return Ok(challenge);
    }

    [HttpPost("mfa")]
    public async Task<IActionResult> CompleteMfa([FromBody] MfaRequestDto request, CancellationToken cancellationToken)
    {
        var session = await _authService.CompleteMfaAsync(request, cancellationToken);

        // Session identifiers are delivered only through the protected browser cookie.
        Response.Cookies.Append("central_session", session.SessionId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = session.ExpiresAtUtc,
            IsEssential = true,
            Path = "/"
        });

        return Ok(new
        {
            kind = session.Kind,
            expiresAtUtc = session.ExpiresAtUtc,
            absoluteExpiresAtUtc = session.AbsoluteExpiresAtUtc,
            mfaSatisfied = session.MfaSatisfied,
            adminSecurityRevision = session.AdminSecurityRevision,
            capabilities = session.Capabilities
        });
    }

    [HttpPost("step-up")]
    public async Task<IActionResult> StepUp([FromBody] StepUpRequestDto request, CancellationToken cancellationToken)
    {
        var credential = CentralSessionCredentials.Parse(Request);
        if (!credential.IsValid || credential.SessionId != request.SessionId ||
            await _authService.ValidateSessionAsync(credential.SessionId, cancellationToken) is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID" });
        }
        var grant = await _stepUpService.VerifyAndGrantStepUpAsync(request, cancellationToken);
        return Ok(grant);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var credential = CentralSessionCredentials.Parse(Request);
        var sessionId = credential.IsValid ? credential.SessionId : Guid.Empty;

        if (sessionId != Guid.Empty)
        {
            await _authService.LogoutAsync(sessionId, "User logout request", cancellationToken);
            Response.Cookies.Delete("central_session");
        }

        return Ok(new { success = true });
    }
}
