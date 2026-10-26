using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("internal/v1")]
public class ProtectedSigningController : ControllerBase
{


    private readonly IProtectedSigningEngine _signingEngine;
    private readonly ICentralAuditService _auditService;
    private readonly IAdminAuthService _authService;
    private readonly IStepUpService _stepUpService;

    public ProtectedSigningController(
        IProtectedSigningEngine signingEngine,
        ICentralAuditService auditService,
        IAdminAuthService authService,
        IStepUpService stepUpService)
    {
        _signingEngine = signingEngine ?? throw new ArgumentNullException(nameof(signingEngine));
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _stepUpService = stepUpService;
    }

    [HttpPost("sign")]
    public async Task<IActionResult> Sign([FromBody] SigningRequestDto request, CancellationToken cancellationToken)
    {
        // 1. Prohibit cross-origin browser access
        if (Request.Headers.ContainsKey("Origin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "CROSS_ORIGIN_PROHIBITED",
                safeMessageKey = "signer.cross_origin_prohibited",
                message = "Cross-origin requests to internal signing endpoints are strictly prohibited."
            });
        }

        // 2. Caller authentication (shared secret / mutual auth token)
        if (!IsAuthorizedCaller())
        {
            await _auditService.RecordAuditAsync(
                eventType: "signer.sign.rejected",
                actorScope: "unauthorized_caller",
                adminUserId: null,
                sessionId: null,
                reason: "Unauthorized caller attempted to invoke internal signing service.",
                correlationId: request.SigningRequestId,
                clientOperationId: request.SigningRequestId,
                entityName: "CentralSigningRequest",
                entityId: request.SigningRequestId.ToString(),
                detailsJson: JsonSerializer.Serialize(new { request.KeyId, request.KeyVersion, request.Purpose }),
                cancellationToken: cancellationToken);

            return Unauthorized(new
            {
                code = "CALLER_UNAUTHORIZED",
                safeMessageKey = "signer.caller_unauthorized",
                message = "Caller is not authorized to invoke internal signing endpoint."
            });
        }

        // 3. Execute signing with comprehensive audit logging
        try
        {
            var response = await _signingEngine.SignAsync(request, cancellationToken);

            await _auditService.RecordAuditAsync(
                eventType: "signer.sign.succeeded",
                actorScope: "system:protected-signer",
                adminUserId: null,
                sessionId: null,
                reason: $"Successfully signed payload for key '{request.KeyId}', version {request.KeyVersion}, purpose '{request.Purpose}'",
                correlationId: request.SigningRequestId,
                clientOperationId: request.SigningRequestId,
                entityName: "CentralSigningRequest",
                entityId: request.SigningRequestId.ToString(),
                detailsJson: JsonSerializer.Serialize(new { request.KeyId, request.KeyVersion, request.Purpose, request.PayloadSha256 }),
                cancellationToken: cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            await _auditService.RecordAuditAsync(
                eventType: "signer.sign.failed",
                actorScope: "system:protected-signer",
                adminUserId: null,
                sessionId: null,
                reason: $"Signing failed for request '{request.SigningRequestId}': {ex.Message}",
                correlationId: request.SigningRequestId,
                clientOperationId: request.SigningRequestId,
                entityName: "CentralSigningRequest",
                entityId: request.SigningRequestId.ToString(),
                detailsJson: JsonSerializer.Serialize(new { request.KeyId, request.KeyVersion, request.Purpose, error = ex.Message }),
                cancellationToken: cancellationToken);

            throw;
        }
    }

    [HttpPost("keys/{keyId}/rotate")]
    public async Task<IActionResult> RotateKey([FromRoute] string keyId, CancellationToken cancellationToken)
    {
        // 1. Prohibit cross-origin
        if (Request.Headers.ContainsKey("Origin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "CROSS_ORIGIN_PROHIBITED", safeMessageKey = "signer.cross_origin_prohibited" });
        }

        // 2. Authenticate Admin session and verify the exact action capability or its approved broad administrator grants.
        var session = await AuthenticateAdminSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        if (!session.Capabilities.Contains(AdminCapabilities.SignerRotate) &&
            !session.Capabilities.Contains(AdminCapabilities.SecurityManage) &&
            !session.Capabilities.Contains(AdminCapabilities.AdminManage))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN", safeMessageKey = "auth.unauthorized_capability" });
        }

        await RequireKeyStepUpAsync(session, keyId, "rotate", cancellationToken);
        // 3. Rotate key and record audit
        await _signingEngine.RotateKeyAsync(keyId, cancellationToken);

        await _auditService.RecordAuditAsync(
            eventType: "signer.key.rotated",
            actorScope: $"admin:{session.SessionId}",
            adminUserId: session.AdminUserId,
            sessionId: session.SessionId,
            reason: $"Admin rotated signing key '{keyId}' to verify-only.",
            correlationId: Guid.NewGuid(),
            clientOperationId: Guid.NewGuid(),
            entityName: "CentralSigningKeyRecord",
            entityId: keyId,
            detailsJson: JsonSerializer.Serialize(new { keyId, action = "rotate" }),
            cancellationToken: cancellationToken);

        return Ok(new { success = true, keyId, action = "rotate" });
    }

    [HttpPost("keys/{keyId}/revoke")]
    public async Task<IActionResult> RevokeKey([FromRoute] string keyId, CancellationToken cancellationToken)
    {
        // 1. Prohibit cross-origin
        if (Request.Headers.ContainsKey("Origin"))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "CROSS_ORIGIN_PROHIBITED", safeMessageKey = "signer.cross_origin_prohibited" });
        }

        // 2. Authenticate Admin session and verify the exact action capability or its approved broad administrator grants.
        var session = await AuthenticateAdminSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_INVALID", safeMessageKey = "auth.session_invalid" });
        }

        if (!session.Capabilities.Contains(AdminCapabilities.SignerRevoke) &&
            !session.Capabilities.Contains(AdminCapabilities.SecurityManage) &&
            !session.Capabilities.Contains(AdminCapabilities.AdminManage))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN", safeMessageKey = "auth.unauthorized_capability" });
        }

        await RequireKeyStepUpAsync(session, keyId, "revoke", cancellationToken);
        // 3. Revoke key and record audit
        await _signingEngine.RevokeKeyAsync(keyId, cancellationToken);

        await _auditService.RecordAuditAsync(
            eventType: "signer.key.revoked",
            actorScope: $"admin:{session.SessionId}",
            adminUserId: session.AdminUserId,
            sessionId: session.SessionId,
            reason: $"Admin revoked signing key '{keyId}'. Emergency rollover revision incremented.",
            correlationId: Guid.NewGuid(),
            clientOperationId: Guid.NewGuid(),
            entityName: "CentralSigningKeyRecord",
            entityId: keyId,
            detailsJson: JsonSerializer.Serialize(new { keyId, action = "revoke" }),
            cancellationToken: cancellationToken);

        return Ok(new { success = true, keyId, action = "revoke" });
    }

    private async Task RequireKeyStepUpAsync(AdminSessionDto session, string keyId, string action, CancellationToken ct)
    {
        var required = (Environment.GetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS") ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SECURITY_POLICY_REQUIRED", "security.policy_required", "Explicit independent approval matrix required; NONE is an isolated configured policy, not a default.")).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (required.Contains($"signer.key.{action}", StringComparer.OrdinalIgnoreCase) || Request.Headers.ContainsKey("X-Sensitive-Approval-Id"))
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("INDEPENDENT_APPROVAL_UNAVAILABLE", "admin.independent_approval_unavailable", "Configured independent approval is unavailable in W1.");
        }

        var serializer = new EdgeRetails.Central.Application.Common.CanonicalJsonSerializer();
        var (_, hash) = serializer.SerializeAndHash(new { keyId, action });
        if (!Guid.TryParse(Request.Headers["X-Step-Up-Grant-Id"], out var grantId) ||
            !await _stepUpService.ValidateAndConsumeStepUpGrantAsync(session.SessionId, $"signer.key.{action}", hash, grantId, ct))
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("INVALID_STEPUP_GRANT", "auth.invalid_stepup_grant", "Fresh action-bound step-up required.");
        }
    }

    private static bool TokenEquals(string supplied, string expected) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(supplied)),
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected)));

    private bool IsAuthorizedCaller()
    {
        var expectedToken = Environment.GetEnvironmentVariable("CENTRAL_INTERNAL_SIGNER_TOKEN");

        if (string.IsNullOrWhiteSpace(expectedToken) || expectedToken.Length < 32)
        {
            return false;
        }
        if (Request.Headers.TryGetValue("X-Internal-Signer-Token", out var headerToken) &&
            TokenEquals(headerToken.ToString(), expectedToken))
        {
            return true;
        }

        if (Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var tokenStr = authHeader.ToString()["Bearer ".Length..].Trim();
            if (TokenEquals(tokenStr, expectedToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<AdminSessionDto?> AuthenticateAdminSessionAsync(CancellationToken cancellationToken)
    {
        Guid sessionId = Guid.Empty;
        if (Request.Cookies.TryGetValue("central_session", out var cookieVal) && Guid.TryParse(cookieVal, out var parsedCookie))
        {
            sessionId = parsedCookie;
        }
        else if (Request.Headers.TryGetValue("Authorization", out var authHeader) &&
                 authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var tokenStr = authHeader.ToString()["Bearer ".Length..].Trim();
            _ = Guid.TryParse(tokenStr, out sessionId);
        }

        if (sessionId == Guid.Empty)
        {
            return null;
        }

        return await _authService.ValidateSessionAsync(sessionId, cancellationToken);
    }
}
