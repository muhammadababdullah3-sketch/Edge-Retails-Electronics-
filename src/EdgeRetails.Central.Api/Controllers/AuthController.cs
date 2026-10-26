using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

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

    [HttpGet("local-sign-in-hint")]
    public IActionResult LocalSignInHint([FromServices] IWebHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || !IsLocalPortalRequest(Request))
        {
            return NotFound();
        }

        var record = CentralLocalPortalCredentialStore.TryRead(environment.ContentRootPath);
        if (record is null || string.IsNullOrWhiteSpace(record.Username))
        {
            return NotFound(new
            {
                code = "LOCAL_SIGN_IN_RECORD_MISSING",
                message = "Run scripts/central/Initialize-CentralLocalDisposable.ps1 or DevTools sync-portal-admin."
            });
        }

        return Ok(new
        {
            username = record.Username,
            password = record.Password,
            mfaSecret = record.MfaSecret,
            localPasswordBypassAllowed = true,
            kind = "LOCAL_DISPOSABLE_DEV_ONLY"
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequestDto request,
        [FromServices] IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        // Drop stale browser cookies before password verification so a revoked or disposable-DB
        // session id cannot confuse operators or interact with CSRF rules on later requests.
        Response.Cookies.Delete(CentralSessionCredentials.CookieName, new CookieOptions { Path = "/" });

        if (request is null || string.IsNullOrWhiteSpace(request.Username))
        {
            return BadRequest(new { code = "LOGIN_PAYLOAD_INVALID", safeMessageKey = "auth.login_payload_invalid" });
        }

        var localBypass = environment.IsDevelopment()
            && IsLocalPortalRequest(Request)
            && request.LocalPasswordBypass;

        if (!localBypass && string.IsNullOrWhiteSpace(request.PasswordOrProviderAssertion))
        {
            return BadRequest(new { code = "LOGIN_PAYLOAD_INVALID", safeMessageKey = "auth.login_payload_invalid" });
        }

        if (request.LocalPasswordBypass && !localBypass)
        {
            request.LocalPasswordBypass = false;
        }

        var challenge = await _authService.StartLoginAsync(request, localBypass, cancellationToken);
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
        if (!credential.IsValid || request.SessionId != Guid.Empty && credential.SessionId != request.SessionId)
        {
            return Unauthorized(new { code = "SESSION_INVALID" });
        }

        var session = await _authService.ValidateSessionAsync(credential.SessionId, cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID" });
        }

        // The HttpOnly cookie is the authority. The body field remains supported for
        // contract-compatible callers, but browser clients that do not retain session
        // identifiers may send Guid.Empty and the validated server session is bound here.
        request.SessionId = session.SessionId;
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

    private static bool IsLocalPortalRequest(HttpRequest request)
    {
        var host = request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }
}
