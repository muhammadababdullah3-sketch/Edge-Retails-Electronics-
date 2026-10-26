using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/admin-users")]
public class AdminUsersController : ControllerBase
{
    private readonly ICentralDbContext _dbContext;
    private readonly IOperationLedgerService _operationLedger;
    private readonly IAdminAuthService _authService;
    private readonly IStepUpService _stepUpService;
    private readonly ICentralAuditService _auditService;
    private readonly ICanonicalJsonSerializer _serializer;

    public AdminUsersController(
        ICentralDbContext dbContext,
        IOperationLedgerService operationLedger,
        IAdminAuthService authService,
        IStepUpService stepUpService,
        ICentralAuditService auditService,
        ICanonicalJsonSerializer serializer)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _operationLedger = operationLedger ?? throw new ArgumentNullException(nameof(operationLedger));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _stepUpService = stepUpService ?? throw new ArgumentNullException(nameof(stepUpService));
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    [HttpPost("{id:guid}/commands")]
    public async Task<IActionResult> ExecuteCommand(
        [FromRoute] Guid id,
        [FromBody] JsonElement body,
        [FromHeader(Name = "X-Client-Operation-Id")] Guid? legacyClientOperationId,
        [FromHeader(Name = "X-Sensitive-Approval-Id")] Guid? sensitiveApprovalId,
        [FromHeader(Name = "X-Step-Up-Grant-Id")] Guid? stepUpGrantId,
        CancellationToken cancellationToken)
    {
        // 1. Authenticate session & check capabilities
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

        if (!session.Capabilities.Contains(AdminCapabilities.AdminManage))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "FORBIDDEN", safeMessageKey = "auth.unauthorized_capability" });
        }

        AdminActionCommandDto command;
        Guid clientOperationId;
        int expectedVersion;
        int policyVersion;
        string? reason;
        string? operationType;
        string? payloadSha256;
        var isCanonical = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("meta", out _);
        if (isCanonical)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<CanonicalDirectoryCommand>(body.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (envelope is null || envelope.Meta.OperationType != "admin.manage" || envelope.Meta.FingerprintVersion != 1)
                {
                    return BadRequest(new { code = "INVALID_COMMAND_ENVELOPE", safeMessageKey = "command.invalid_metadata" });
                }
                command = JsonSerializer.Deserialize<AdminActionCommandDto>(envelope.Payload.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new JsonException("Admin action payload is required.");
                clientOperationId = envelope.Meta.ClientOperationId;
                expectedVersion = envelope.Meta.ExpectedVersion;
                policyVersion = envelope.Meta.PolicyVersion;
                reason = envelope.Meta.Reason;
                operationType = envelope.Meta.OperationType;
                payloadSha256 = envelope.Meta.PayloadSha256;
                if (sensitiveApprovalId.HasValue && envelope.Meta.SensitiveApprovalId.HasValue &&
                    sensitiveApprovalId.Value != envelope.Meta.SensitiveApprovalId.Value)
                {
                    return BadRequest(new { code = "APPROVAL_METADATA_CONFLICT", safeMessageKey = "command.invalid_metadata" });
                }
                sensitiveApprovalId ??= envelope.Meta.SensitiveApprovalId;
                var actualPayloadHash = _serializer.SerializeAndHash(command).Sha256Hex;
                if (!string.Equals(actualPayloadHash, payloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { code = "PAYLOAD_DIGEST_MISMATCH", safeMessageKey = "command.payload_digest_mismatch" });
                }
            }
            catch (JsonException)
            {
                return BadRequest(new { code = "INVALID_COMMAND_ENVELOPE", safeMessageKey = "command.invalid_metadata" });
            }
        }
        else
        {
            command = body.Deserialize<AdminActionCommandDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new CentralDomainException("INVALID_COMMAND", "command.invalid", "Admin action payload is required.");
            clientOperationId = legacyClientOperationId ?? Guid.Empty;
            expectedVersion = int.TryParse(Request.Headers["X-Expected-Version"].FirstOrDefault(), out var parsedExpected) ? parsedExpected : 0;
            policyVersion = int.TryParse(Request.Headers["X-Policy-Version"].FirstOrDefault(), out var parsedPolicy) ? parsedPolicy : 0;
            reason = Request.Headers["X-Reason"].FirstOrDefault();
            operationType = Request.Headers["X-Operation-Type"].FirstOrDefault();
            payloadSha256 = Request.Headers["X-Payload-Sha256"].FirstOrDefault();
        }

        // 2. Self-escalation / self-modification defense: Admin cannot modify own role or status
        if (session.AdminUserId == id)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = "SELF_ESCALATION_PROHIBITED",
                safeMessageKey = "admin.self_escalation_prohibited",
                message = "Admin users cannot modify their own roles or active status."
            });
        }

        // 3. Validate metadata
        if (clientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "MISSING_CLIENT_OPERATION_ID", safeMessageKey = "command.missing_operation_id" });
        }

        if (id == Guid.Empty || command.AdminUserId != id || string.IsNullOrWhiteSpace(command.Action) || expectedVersion <= 0 || policyVersion <= 0 || string.IsNullOrWhiteSpace(reason))
        {
            throw new CentralDomainException("INVALID_COMMAND_METADATA", "command.invalid_metadata", "Route target, positive versions and reason are required.");
        }

        var action = command.Action.ToUpperInvariant();
        if (action is not ("INVITE" or "DISABLE" or "REVOKE_SESSION" or "REVOKE_ALL" or "ASSIGN_ROLE" or "REVOKE_ROLE"))
        {
            throw new CentralDomainException("UNSUPPORTED_ACTION", "admin.unsupported_action", $"Action '{command.Action}' is not defined by the admin.manage contract.");
        }

        if (operationType is not null && operationType != "admin.manage")
        {
            throw new CentralDomainException("OPERATION_TYPE_CONFLICT", "command.operation_type_conflict", "This endpoint only accepts admin.manage.");
        }
        var opType = "admin.manage";
        var opReason = reason ?? "Admin command execution";

        // If client didn't supply hash in header, compute it or require it
        var (_, computedSha256) = _serializer.SerializeAndHash(command);
        if (payloadSha256 is not null && !string.Equals(payloadSha256, computedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { code = "PAYLOAD_DIGEST_MISMATCH", safeMessageKey = "command.payload_digest_mismatch" });
        }
        var effectiveSha256 = payloadSha256 ?? computedSha256;

        var effectiveApprovalId = stepUpGrantId;
        var actionScope = $"admin.user.{command.Action.ToLowerInvariant()}";

        var meta = new CommandMetaDto
        {
            ClientOperationId = clientOperationId,
            OperationType = opType,
            FingerprintVersion = 1,
            PayloadSha256 = effectiveSha256,
            ExpectedVersion = expectedVersion,
            PolicyVersion = policyVersion,
            Reason = opReason,
            SensitiveApprovalId = sensitiveApprovalId
        };

        var actorScope = $"admin:{session.SessionId}";

        // 4. Execute through Operation Ledger
        var outcome = await _operationLedger.ExecuteOrReplayAsync(
            actorScope: actorScope,
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: async cmd =>
            {
                // Independent approval is a separate authority; a grant is never an approval.
                var required = (Environment.GetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS") ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SECURITY_POLICY_REQUIRED", "security.policy_required", "Explicit independent approval matrix required; NONE is an isolated configured policy, not a default.")).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (required.Contains(cmd.Action, StringComparer.OrdinalIgnoreCase) || sensitiveApprovalId.HasValue)
                {
                    throw new CentralDomainException("INDEPENDENT_APPROVAL_UNAVAILABLE", "admin.independent_approval_unavailable", "Configured independent approval cannot be bypassed by a step-up grant.");
                }

                // Revalidate live authority within the serialized command transaction.
                if (await _authService.ValidateSessionAsync(session.SessionId, cancellationToken) is null)
                {
                    throw new CentralDomainException("SESSION_INVALID", "auth.session_invalid", "Session authority changed before execution.");
                }
                // Sensitive action step-up grant enforcement and consumption
                if (!effectiveApprovalId.HasValue)
                {
                    throw new CentralDomainException(
                        "APPROVAL_REQUIRED",
                        "admin.approval_required",
                        "Every administrative authority change requires a fresh bound step-up grant.");
                }

                if (effectiveApprovalId.HasValue)
                {
                    var grantConsumed = await _stepUpService.ValidateAndConsumeStepUpGrantAsync(
                        session.SessionId,
                        actionScope,
                        effectiveSha256,
                        effectiveApprovalId.Value,
                        cancellationToken);

                    if (!grantConsumed)
                    {
                        throw new CentralDomainException(
                            "INVALID_STEPUP_GRANT",
                            "auth.invalid_stepup_grant",
                            "Step-up grant is invalid, expired, or already consumed.");
                    }
                }

                var targetUser = await _dbContext.AdminUsers
                    .Include(u => u.UserRoles)
                    .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

                if (targetUser is null)
                {
                    throw new CentralDomainException("USER_NOT_FOUND", "admin.user_not_found", $"Admin user '{id}' was not found.");
                }

                if (targetUser.RowVersion != expectedVersion)
                {
                    throw new CentralDomainException(
                        "CONCURRENCY_CONFLICT",
                        "operation.concurrency_conflict",
                        $"Expected version {expectedVersion} does not match current user version {targetUser.RowVersion}.");
                }

                switch (cmd.Action.ToUpperInvariant())
                {
                    case "ASSIGN_ROLE":
                        if (!cmd.RoleId.HasValue)
                        {
                            throw new CentralDomainException("ROLE_ID_REQUIRED", "admin.role_id_required", "RoleId is required for ASSIGN_ROLE.");
                        }

                        var role = await _dbContext.AdminRoles.FirstOrDefaultAsync(r => r.Id == cmd.RoleId.Value, cancellationToken);
                        if (role is null)
                        {
                            throw new CentralDomainException("ROLE_NOT_FOUND", "admin.role_not_found", $"Role '{cmd.RoleId.Value}' not found.");
                        }

                        if (role.GetCapabilities().Except(session.Capabilities, StringComparer.Ordinal).Any())
                        {
                            throw new CentralDomainException("DELEGATION_FORBIDDEN", "admin.delegation_forbidden", "Cannot delegate capabilities not held by the caller.");
                        }
                        if (!targetUser.UserRoles.Any(ur => ur.RoleId == role.Id))
                        {
                            var userRole = new AdminUserRole(targetUser.Id, role.Id, DateTimeOffset.UtcNow);
                            _dbContext.AdminUserRoles.Add(userRole);
                            targetUser.BumpSecurityRevision();
                        }
                        break;

                    case "REVOKE_ROLE":
                        if (!cmd.RoleId.HasValue)
                        {
                            throw new CentralDomainException("ROLE_ID_REQUIRED", "admin.role_id_required", "RoleId is required for REVOKE_ROLE.");
                        }

                        var existingRole = targetUser.UserRoles.FirstOrDefault(ur => ur.RoleId == cmd.RoleId.Value);
                        if (existingRole is not null)
                        {
                            _dbContext.AdminUserRoles.Remove(existingRole);
                            targetUser.BumpSecurityRevision();
                        }
                        break;

                    case "DISABLE":
                        targetUser.SetActive(false);
                        break;

                    case "REVOKE_SESSION":
                        if (!cmd.SessionId.HasValue || cmd.SessionId.Value == Guid.Empty)
                        {
                            throw new CentralDomainException("SESSION_ID_REQUIRED", "admin.session_id_required", "SessionId is required for REVOKE_SESSION.");
                        }
                        var targetSession = await _dbContext.AdminSessions.FirstOrDefaultAsync(s => s.Id == cmd.SessionId.Value, cancellationToken);
                        if (targetSession is null || targetSession.AdminUserId != targetUser.Id)
                        {
                            throw new CentralDomainException("SESSION_NOT_FOUND", "admin.session_not_found", "The session does not belong to the target admin user.");
                        }
                        if (!targetSession.IsRevoked)
                        {
                            targetSession.Revoke("Revoked by authorized admin.manage command", DateTimeOffset.UtcNow);
                        }
                        break;

                    case "REVOKE_ALL":
                        var targetSessions = await _dbContext.AdminSessions
                            .Where(s => s.AdminUserId == targetUser.Id && !s.IsRevoked)
                            .ToListAsync(cancellationToken);
                        foreach (var adminSession in targetSessions)
                        {
                            adminSession.Revoke("All sessions revoked by authorized admin.manage command", DateTimeOffset.UtcNow);
                        }
                        break;

                    case "INVITE":
                        throw new CentralDomainException("ADMIN_INVITE_CONTRACT_INCOMPLETE", "admin.invite_contract_incomplete", "The admin.manage contract does not provide the identity and invitation delivery fields required to create an admin invitation.");

                    default:
                        throw new CentralDomainException("UNSUPPORTED_ACTION", "admin.unsupported_action", $"Action '{cmd.Action}' is unsupported.");
                }

                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(clientOperationId,
                    $"admin.{cmd.Action.ToLowerInvariant()}", "AdminUser", targetUser.Id.ToString(),
                    _serializer.SerializeCanonical(new { targetUser.Id, cmd.Action, clientOperationId }), DateTimeOffset.UtcNow));
                // Record audit entry with acting admin user ID attribution
                await _auditService.RecordAuditAsync(
                    eventType: $"admin.{cmd.Action.ToLowerInvariant()}",
                    actorScope: actorScope,
                    adminUserId: session.AdminUserId,
                    sessionId: session.SessionId,
                    reason: opReason,
                    correlationId: clientOperationId,
                    clientOperationId: clientOperationId,
                    entityName: "AdminUser",
                    entityId: targetUser.Id.ToString(),
                    detailsJson: _serializer.SerializeCanonical(cmd),
                    payloadSha256: computedSha256,
                    cancellationToken: cancellationToken);

                return (
                    Result: true,
                    ResultReference: $"admin_users/{targetUser.Id}",
                    ResourceVersion: targetUser.RowVersion,
                    AuthorityRevision: targetUser.SecurityRevision
                );
            },
            targetResource: $"admin-users/{id}",
            cancellationToken: cancellationToken);

        return Ok(outcome);
    }

    private Guid ExtractSessionId()
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid ? credentials.SessionId : Guid.Empty;
    }
}
