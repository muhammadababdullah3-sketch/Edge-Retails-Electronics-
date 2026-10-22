using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Domain.Signing;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class SensitiveLifecycleService : ISensitiveLifecycleService
{
    private readonly ICentralDbContext _dbContext;
    private readonly IProtectedSignerClient _signingEngine;
    private readonly IStepUpService _stepUpService;
    private readonly ICentralAuditService _auditService;
    private readonly ICanonicalJsonSerializer _serializer;
    private readonly IOperationLedgerService _operationLedger;

    public SensitiveLifecycleService(
        ICentralDbContext dbContext,
        IProtectedSignerClient signingEngine,
        IStepUpService stepUpService,
        ICentralAuditService auditService,
        IOperationLedgerService? operationLedger = null,
        ICanonicalJsonSerializer? serializer = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _signingEngine = signingEngine ?? throw new ArgumentNullException(nameof(signingEngine));
        _stepUpService = stepUpService ?? throw new ArgumentNullException(nameof(stepUpService));
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _serializer = serializer ?? new CanonicalJsonSerializer();
        _operationLedger = operationLedger ?? new OperationLedgerService(dbContext, _serializer);
    }

    public async Task<SensitiveApprovalRequest> RequestApprovalAsync(
        string operationType,
        Guid targetEntityId,
        string targetPayloadSha256,
        string actionScope,
        Guid requesterAdminId,
        Guid stepUpSessionId,
        TimeSpan validity,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, targetEntityId.ToString(), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(validity > TimeSpan.Zero ? validity : TimeSpan.FromHours(2));

        var request = SensitiveApprovalRequest.Request(
            operationType,
            targetEntityId,
            targetPayloadSha256,
            requesterAdminId,
            actionScope,
            stepUpSessionId,
            now,
            expiresAt);

        _dbContext.SensitiveApprovalRequests.Add(request);
        _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
            request.Id, "approval.requested", nameof(SensitiveApprovalRequest), request.Id.ToString("D"),
            _serializer.SerializeCanonical(new { request.Id, request.TargetEntityId, request.OperationType }), now));
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "SENSITIVE_APPROVAL_REQUESTED",
            "ADMIN",
            requesterAdminId,
            stepUpSessionId,
            $"Approval requested for {operationType}",
            null,
            null,
            nameof(SensitiveApprovalRequest),
            request.Id.ToString(),
            $"{{\"operationType\":\"{operationType}\",\"targetId\":\"{targetEntityId}\"}}",
            true,
            targetPayloadSha256,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return request;
    }

    public async Task DecideApprovalAsync(
        Guid requestId,
        DecideSensitiveApprovalCommand command,
        Guid approverAdminId,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, requestId.ToString(), cancellationToken);

        var request = await _dbContext.SensitiveApprovalRequests.FindAsync(new object[] { requestId }, cancellationToken)
            ?? throw new CentralDomainException("APPROVAL_REQUEST_NOT_FOUND", "approval.not_found", $"Request {requestId} does not exist.");

        var now = DateTimeOffset.UtcNow;

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{approverAdminId}";
            var targetResource = $"approvals/{requestId}";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    if (string.Equals(cmd.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Approve(approverAdminId, cmd.TargetPayloadSha256, now);
                    }
                    else
                    {
                        request.Reject(approverAdminId, now);
                    }

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "approval.decided",
                        nameof(SensitiveApprovalRequest),
                        request.Id.ToString(),
                        _serializer.SerializeCanonical(new { request.Id, decision = cmd.Decision, approverAdminId, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "SENSITIVE_APPROVAL_DECIDED",
                        "ADMIN",
                        approverAdminId,
                        null,
                        cmd.CommandMeta?.Reason ?? $"Approval decision: {cmd.Decision}",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(SensitiveApprovalRequest),
                        request.Id.ToString(),
                        $"{{\"decision\":\"{cmd.Decision}\",\"approverId\":\"{approverAdminId}\"}}",
                        true,
                        cmd.TargetPayloadSha256,
                        cancellationToken);

                    return (true, $"approvals/{request.Id}", 1, 1);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        if (string.Equals(command.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            request.Approve(approverAdminId, command.TargetPayloadSha256, now);
        }
        else
        {
            request.Reject(approverAdminId, now);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "SENSITIVE_APPROVAL_DECIDED",
            "ADMIN",
            approverAdminId,
            null,
            command.CommandMeta?.Reason ?? $"Approval decision: {command.Decision}",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(SensitiveApprovalRequest),
            request.Id.ToString(),
            $"{{\"decision\":\"{command.Decision}\",\"approverId\":\"{approverAdminId}\"}}",
            true,
            command.TargetPayloadSha256,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    private async Task ValidateAndConsumeApprovalAsync(
        string operationType,
        Guid licenseId,
        LicenseLifecycleCommand command,
        Guid adminId,
        CancellationToken cancellationToken,
        string? expectedPayloadSha256 = null)
    {
        SensitiveApprovalRequest? approval = null;
        if (command.SensitiveApprovalId.HasValue && command.SensitiveApprovalId.Value != Guid.Empty)
        {
            approval = await _dbContext.SensitiveApprovalRequests.FindAsync(new object[] { command.SensitiveApprovalId.Value }, cancellationToken)
                ?? throw new CentralDomainException("APPROVAL_NOT_FOUND", "approval.not_found", $"Approval request {command.SensitiveApprovalId.Value} does not exist.");
        }
        else
        {
            approval = await _dbContext.SensitiveApprovalRequests
                .Where(r => r.TargetEntityId == licenseId && (r.OperationType == operationType || r.OperationType == operationType.ToLowerInvariant()) && r.Status == SensitiveApprovalRequest.StatusApproved)
                .OrderByDescending(r => r.RequestedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new CentralDomainException("APPROVAL_REQUIRED", "approval.required", $"Operation {operationType} requires an approved SensitiveApprovalRequest.");
        }

        if (approval.Status != SensitiveApprovalRequest.StatusApproved)
        {
            throw new CentralDomainException("APPROVAL_NOT_APPROVED", "approval.not_approved", $"Approval request is in status {approval.Status}; must be APPROVED.");
        }

        if (!string.Equals(approval.OperationType, operationType, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("OPERATION_TYPE_MISMATCH", "approval.operation_type_mismatch", $"Approval request operation {approval.OperationType} does not match required {operationType}.");
        }

        if (approval.TargetEntityId != licenseId)
        {
            throw new CentralDomainException("TARGET_MISMATCH", "approval.target_mismatch", "Approval target does not match target license.");
        }

        if (approval.RequesterAdminId == approval.ApproverAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "approval.self_approval_prohibited", "Self-approved request cannot be executed.");
        }

        if (expectedPayloadSha256 is not null &&
            !string.Equals(approval.TargetPayloadSha256, expectedPayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("APPROVAL_PAYLOAD_MISMATCH", "approval.payload_mismatch", "The approved payload digest does not match this canonical lifecycle command.");
        }

        var now = DateTimeOffset.UtcNow;
        if (approval.ExpiresAtUtc <= now)
        {
            throw new CentralDomainException("APPROVAL_EXPIRED", "approval.expired", "Approval request has expired.");
        }

        approval.Consume(now);
    }

    public async Task<OutcomeDto> ExecuteCanonicalLicenseLifecycleAsync(
        string operationType,
        Guid licenseId,
        Guid capabilityPolicyId,
        DateTimeOffset effectiveAtUtc,
        JsonElement payload,
        CanonicalDirectoryMeta meta,
        Guid adminId,
        Guid? adminSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (licenseId == Guid.Empty || capabilityPolicyId == Guid.Empty || adminId == Guid.Empty ||
            payload.ValueKind != JsonValueKind.Object || effectiveAtUtc == default)
        {
            throw new CentralDomainException("INVALID_LICENSE_COMMAND", "license.invalid_command", "A valid license, capability policy, effective time and canonical payload are required.");
        }

        var action = operationType switch
        {
            "license.pause" => (Approval: "LICENSE_PAUSE", Audit: "COMMERCIAL_LICENSE_PAUSED", Event: "license.paused", Apply: (Action<CommercialLicense>)(x => x.Pause())),
            "license.resume" => (Approval: "LICENSE_RESUME", Audit: "COMMERCIAL_LICENSE_RESUMED", Event: "license.resumed", Apply: (Action<CommercialLicense>)(x => x.Resume())),
            "license.suspend" => (Approval: "LICENSE_SUSPEND", Audit: "COMMERCIAL_LICENSE_SUSPENDED", Event: "license.suspended", Apply: (Action<CommercialLicense>)(x => x.Suspend())),
            "license.reinstate" => (Approval: "LICENSE_REINSTATE", Audit: "COMMERCIAL_LICENSE_REINSTATED", Event: "license.reinstated", Apply: (Action<CommercialLicense>)(x => x.Reinstate())),
            "license.revoke" => (Approval: "LICENSE_REVOKE", Audit: "COMMERCIAL_LICENSE_REVOKED", Event: "license.revoked", Apply: (Action<CommercialLicense>)(x => x.Revoke())),
            _ => throw new CentralDomainException("INVALID_LICENSE_COMMAND", "license.invalid_command", "The license lifecycle operation is not supported.")
        };

        var initial = await _dbContext.CommercialLicenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == licenseId, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        return await _operationLedger.ExecuteOrReplayAsync<JsonElement, bool>(
            $"admin:{adminId:D}", initial.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var license = await _dbContext.CommercialLicenses
                    .SingleOrDefaultAsync(x => x.Id == licenseId && x.ShopId == initial.ShopId, cancellationToken)
                    ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", "The license no longer belongs to the persisted shop scope.");
                if (license.RowVersion != meta.ExpectedVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The license changed before this lifecycle command was applied.");
                }

                var currentPolicy = await _dbContext.ShopEnforcementPolicies.AsNoTracking()
                    .Where(x => x.ShopId == license.ShopId && x.LicenseId == license.Id)
                    .OrderByDescending(x => x.PolicyVersion)
                    .ThenByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken)
                    ?? throw new CentralDomainException("LICENSE_POLICY_NOT_FOUND", "license.policy_not_found", "A persisted capability policy is required for this lifecycle command.");
                if (currentPolicy.CapabilityPolicyId != capabilityPolicyId || currentPolicy.PolicyVersion != meta.PolicyVersion)
                {
                    throw new CentralDomainException("POLICY_VERSION_CONFLICT", "operation.policy_conflict", "The supplied capability policy does not match the current persisted policy.");
                }

                var now = DateTimeOffset.UtcNow;
                if (effectiveAtUtc > now)
                {
                    throw new CentralDomainException("EFFECTIVE_TIME_IN_FUTURE", "license.effective_time_in_future", "Future-dated lifecycle commands are not applied before their effective time.");
                }

                var lifecycleCommand = new LicenseLifecycleCommand
                {
                    CommandMeta = meta.ToLedgerMeta(),
                    SensitiveApprovalId = meta.SensitiveApprovalId
                };
                await ValidateAndConsumeApprovalAsync(action.Approval, licenseId, lifecycleCommand, adminId,
                    cancellationToken, meta.PayloadSha256);

                action.Apply(license);
                await _dbContext.SaveChangesAsync(cancellationToken);
                var details = _serializer.SerializeCanonical(new
                {
                    licenseId = license.Id,
                    license.ShopId,
                    status = license.Status,
                    license.AuthorityRevision,
                    effectiveAtUtc,
                    capabilityPolicyId,
                    clientOperationId = meta.ClientOperationId
                });
                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId,
                    action.Event, nameof(CommercialLicense), license.Id.ToString("D"), details, now));
                await _auditService.RecordAuditAsync(action.Audit, "ADMIN", adminId, adminSessionId,
                    meta.Reason, meta.ClientOperationId, meta.ClientOperationId, nameof(CommercialLicense),
                    license.Id.ToString("D"), details, true, meta.PayloadSha256, cancellationToken);
                return (true, $"licenses/{license.Id:D}", license.RowVersion, license.AuthorityRevision);
            },
            targetResource: $"license/{licenseId:D}/{operationType}",
            cancellationToken: cancellationToken);
    }

    public async Task PauseLicenseAsync(
        Guid licenseId,
        LicenseLifecycleCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"licenses/{licenseId}/pause";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: license.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    await ValidateAndConsumeApprovalAsync("LICENSE_PAUSE", licenseId, cmd, adminId, cancellationToken);
                    license.Pause();
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "license.paused",
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        _serializer.SerializeCanonical(new { license.Id, license.Status, license.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_PAUSED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license paused",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (true, $"licenses/{license.Id}", 1, license.AuthorityRevision);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        await ValidateAndConsumeApprovalAsync("LICENSE_PAUSE", licenseId, command, adminId, cancellationToken);

        license.Pause();
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_PAUSED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license paused",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            license.Id.ToString(),
            $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task ResumeLicenseAsync(
        Guid licenseId,
        LicenseLifecycleCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"licenses/{licenseId}/resume";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: license.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    await ValidateAndConsumeApprovalAsync("LICENSE_RESUME", licenseId, cmd, adminId, cancellationToken);
                    license.Resume();
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "license.resumed",
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        _serializer.SerializeCanonical(new { license.Id, license.Status, license.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_RESUMED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license resumed",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (true, $"licenses/{license.Id}", 1, license.AuthorityRevision);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        await ValidateAndConsumeApprovalAsync("LICENSE_RESUME", licenseId, command, adminId, cancellationToken);

        license.Resume();
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_RESUMED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license resumed",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            license.Id.ToString(),
            $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task SuspendLicenseAsync(
        Guid licenseId,
        LicenseLifecycleCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"licenses/{licenseId}/suspend";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: license.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    await ValidateAndConsumeApprovalAsync("LICENSE_SUSPEND", licenseId, cmd, adminId, cancellationToken);
                    license.Suspend();
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "license.suspended",
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        _serializer.SerializeCanonical(new { license.Id, license.Status, license.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_SUSPENDED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license suspended",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (true, $"licenses/{license.Id}", 1, license.AuthorityRevision);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        await ValidateAndConsumeApprovalAsync("LICENSE_SUSPEND", licenseId, command, adminId, cancellationToken);

        license.Suspend();
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_SUSPENDED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license suspended",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            license.Id.ToString(),
            $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task ReinstateLicenseAsync(
        Guid licenseId,
        LicenseLifecycleCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"licenses/{licenseId}/reinstate";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: license.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    await ValidateAndConsumeApprovalAsync("LICENSE_REINSTATE", licenseId, cmd, adminId, cancellationToken);
                    license.Reinstate();
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "license.reinstated",
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        _serializer.SerializeCanonical(new { license.Id, license.Status, license.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_REINSTATED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license reinstated",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (true, $"licenses/{license.Id}", 1, license.AuthorityRevision);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        await ValidateAndConsumeApprovalAsync("LICENSE_REINSTATE", licenseId, command, adminId, cancellationToken);

        license.Reinstate();
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_REINSTATED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license reinstated",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            license.Id.ToString(),
            $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RevokeLicenseAsync(
        Guid licenseId,
        LicenseLifecycleCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"licenses/{licenseId}/revoke";

            await _operationLedger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: license.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    await ValidateAndConsumeApprovalAsync("LICENSE_REVOKE", licenseId, cmd, adminId, cancellationToken);
                    license.Revoke();
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "license.revoked",
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        _serializer.SerializeCanonical(new { license.Id, license.Status, license.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_REVOKED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license revoked",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        license.Id.ToString(),
                        $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (true, $"licenses/{license.Id}", 1, license.AuthorityRevision);
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return;
        }

        await ValidateAndConsumeApprovalAsync("LICENSE_REVOKE", licenseId, command, adminId, cancellationToken);

        license.Revoke();
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_REVOKED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license revoked",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            license.Id.ToString(),
            $"{{\"status\":\"{license.Status}\",\"revision\":{license.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<OutcomeDto> ConfigureCanonicalPolicyAsync(
        Guid routeLicenseId,
        PolicyDto policy,
        JsonElement payload,
        CanonicalDirectoryMeta meta,
        Guid adminId,
        Guid? adminSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(meta);
        if (routeLicenseId == Guid.Empty || adminId == Guid.Empty || payload.ValueKind != JsonValueKind.Object ||
            policy.LicenseId != routeLicenseId || policy.ShopId == Guid.Empty || policy.PolicyVersion < 1 ||
            policy.ApprovedByAdminId != adminId || policy.AfterExtensionPolicyId == Guid.Empty ||
            policy.CapabilityPolicyId == Guid.Empty || policy.LeaseDurationSeconds < 1 ||
            policy.OfflineGraceSeconds < 0 || policy.RollbackToleranceSeconds < 0 || policy.ApprovedAtUtc == default)
        {
            throw new CentralDomainException("INVALID_POLICY_COMMAND", "policy.invalid_command", "The policy command fields or server-derived approver do not match the authenticated request.");
        }

        var initialLicense = await _dbContext.CommercialLicenses.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == routeLicenseId, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "policy.license_not_found", $"License {routeLicenseId} does not exist.");
        if (policy.ShopId != initialLicense.ShopId)
        {
            throw new CentralDomainException("SHOP_SCOPE_MISMATCH", "policy.shop_scope_mismatch", "The policy shop must match the persisted license shop.");
        }
        if (meta.OperationType != "license.policy" || meta.PolicyVersion != policy.PolicyVersion)
        {
            throw new CentralDomainException("POLICY_VERSION_CONFLICT", "operation.policy_conflict", "The operation and policy version identities must match.");
        }

        return await _operationLedger.ExecuteOrReplayAsync<JsonElement, Guid>(
            $"admin:{adminId:D}", initialLicense.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var license = await _dbContext.CommercialLicenses
                    .SingleOrDefaultAsync(x => x.Id == routeLicenseId && x.ShopId == initialLicense.ShopId, cancellationToken)
                    ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "policy.license_not_found", "The license no longer matches the persisted shop scope.");
                var current = await _dbContext.ShopEnforcementPolicies
                    .Where(x => x.ShopId == license.ShopId && x.LicenseId == license.Id)
                    .OrderByDescending(x => x.PolicyVersion)
                    .ThenByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                var currentVersion = current?.PolicyVersion ?? 0;
                if (meta.ExpectedVersion != currentVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The persisted license policy changed before this command.");
                }
                if (policy.PolicyVersion <= currentVersion)
                {
                    throw new CentralDomainException("NON_MONOTONIC_VERSION", "policy.non_monotonic_version", $"Policy version {policy.PolicyVersion} must be greater than the current version {currentVersion}.");
                }
                if (policy.OfflineGraceSeconds < 86400)
                {
                    throw new CentralDomainException("MINIMUM_GRACE_REQUIRED", "policy.minimum_grace", "Minimum offline grace period is 24 hours (86400 seconds).");
                }

                var now = DateTimeOffset.UtcNow;
                var activeKey = await _signingEngine.EnsureActiveKeyAsync(SigningPurpose.License, cancellationToken);
                var policyEnvelope = new
                {
                    shopId = license.ShopId,
                    licenseId = license.Id,
                    policyVersion = policy.PolicyVersion,
                    enforcementMode = policy.EnforcementMode,
                    extensionEndUtc = policy.ExtensionEndUtc,
                    afterExtensionPolicyId = policy.AfterExtensionPolicyId,
                    leaseDurationSeconds = policy.LeaseDurationSeconds,
                    offlineGraceSeconds = policy.OfflineGraceSeconds,
                    rollbackToleranceSeconds = policy.RollbackToleranceSeconds,
                    capabilityPolicyId = policy.CapabilityPolicyId
                };
                var (canonicalJson, digestSha256) = _serializer.SerializeAndHash(policyEnvelope);
                var signingRequestId = Guid.NewGuid();
                _dbContext.CentralSigningRequests.Add(CentralSigningRequest.Create(
                    signingRequestId, license.ShopId, activeKey.Purpose, activeKey.KeyId, activeKey.KeyVersion,
                    policy.PolicyVersion, digestSha256, canonicalJson, now));
                await _dbContext.SaveChangesAsync(cancellationToken);
                var signingResponse = await _signingEngine.SignAsync(new SigningRequestDto
                {
                    SigningRequestId = signingRequestId,
                    IssuanceRequestId = license.ShopId,
                    PayloadSha256 = digestSha256,
                    AuthorityRevision = policy.PolicyVersion,
                    KeyId = activeKey.KeyId,
                    KeyVersion = activeKey.KeyVersion,
                    Purpose = activeKey.Purpose,
                    CanonicalPayload = canonicalJson
                }, cancellationToken);

                var record = ShopEnforcementPolicy.Create(license.ShopId, license.Id, policy.PolicyVersion,
                    policy.EnforcementMode, policy.ExtensionEndUtc, policy.AfterExtensionPolicyId,
                    policy.LeaseDurationSeconds, policy.OfflineGraceSeconds, policy.RollbackToleranceSeconds,
                    policy.CapabilityPolicyId, canonicalJson, signingResponse.Signature,
                    signingResponse.KeyId, adminId, now);
                _dbContext.ShopEnforcementPolicies.Add(record);
                await _dbContext.SaveChangesAsync(cancellationToken);

                _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId,
                    "policy.configured", nameof(ShopEnforcementPolicy), record.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { record.Id, record.ShopId, record.LicenseId, record.PolicyVersion, clientOperationId = meta.ClientOperationId }), now));
                await _auditService.RecordAuditAsync("ENFORCEMENT_POLICY_CONFIGURED", "ADMIN", adminId,
                    adminSessionId, meta.Reason, meta.ClientOperationId, meta.ClientOperationId,
                    nameof(ShopEnforcementPolicy), record.Id.ToString("D"),
                    _serializer.SerializeCanonical(new { record.ShopId, record.LicenseId, record.PolicyVersion, record.CapabilityPolicyId, record.ApprovedByAdminId, approvedAtUtc = record.CreatedAtUtc }),
                    true, meta.PayloadSha256, cancellationToken);
                return (record.Id, $"policies/{record.Id:D}", record.RowVersion, record.PolicyVersion);
            },
            targetResource: $"license/{routeLicenseId:D}/policy",
            cancellationToken: cancellationToken);
    }

    public async Task<ShopEnforcementPolicy> ConfigurePolicyAsync(
        ConfigurePolicyCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        var p = command.Policy;

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, p.ShopId.ToString(), cancellationToken);

        var license = await _dbContext.CommercialLicenses.FindAsync(new object[] { p.LicenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "policy.license_not_found", $"License {p.LicenseId} does not exist.");

        if (p.OfflineGraceSeconds < 86400)
        {
            throw new CentralDomainException("MINIMUM_GRACE_REQUIRED", "policy.minimum_grace", "Minimum offline grace period is 24 hours (86400 seconds).");
        }

        var latestPolicy = await _dbContext.ShopEnforcementPolicies
            .Where(ep => ep.ShopId == p.ShopId)
            .OrderByDescending(ep => ep.PolicyVersion)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestPolicy != null && p.PolicyVersion <= latestPolicy.PolicyVersion)
        {
            throw new CentralDomainException("NON_MONOTONIC_VERSION", "policy.non_monotonic_version", $"Policy version {p.PolicyVersion} must be strictly greater than current version {latestPolicy.PolicyVersion}.");
        }
        if (latestPolicy == null && p.PolicyVersion < 1)
        {
            throw new CentralDomainException("INVALID_POLICY_VERSION", "policy.invalid_version", "Policy version must be positive.");
        }

        var activeKey = await _signingEngine.EnsureActiveKeyAsync(SigningPurpose.License, cancellationToken);

        string keyId = activeKey.KeyId;
        int keyVersion = activeKey.KeyVersion;
        string purpose = activeKey.Purpose;

        var policyEnvelope = new
        {
            shopId = p.ShopId,
            licenseId = p.LicenseId,
            policyVersion = p.PolicyVersion,
            enforcementMode = p.EnforcementMode,
            extensionEndUtc = p.ExtensionEndUtc,
            afterExtensionPolicyId = p.AfterExtensionPolicyId,
            leaseDurationSeconds = p.LeaseDurationSeconds,
            offlineGraceSeconds = p.OfflineGraceSeconds,
            rollbackToleranceSeconds = p.RollbackToleranceSeconds,
            capabilityPolicyId = p.CapabilityPolicyId
        };

        var (canonicalJson, digestSha256) = _serializer.SerializeAndHash(policyEnvelope);
        var signingRequestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var signingRequest = CentralSigningRequest.Create(
            signingRequestId,
            p.ShopId,
            purpose,
            keyId,
            keyVersion,
            p.PolicyVersion,
            digestSha256,
            canonicalJson,
            now);

        _dbContext.CentralSigningRequests.Add(signingRequest);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var signingResponse = await _signingEngine.SignAsync(new SigningRequestDto
        {
            SigningRequestId = signingRequestId,
            IssuanceRequestId = p.ShopId,
            PayloadSha256 = digestSha256,
            AuthorityRevision = p.PolicyVersion,
            KeyId = keyId,
            KeyVersion = keyVersion,
            Purpose = purpose,
            CanonicalPayload = canonicalJson
        }, cancellationToken);

        if (command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"shops/{p.ShopId}/policies";

            var (_, policyRecord) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: p.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var pol = ShopEnforcementPolicy.Create(
                        p.ShopId,
                        p.LicenseId,
                        p.PolicyVersion,
                        p.EnforcementMode,
                        p.ExtensionEndUtc,
                        p.AfterExtensionPolicyId,
                        p.LeaseDurationSeconds,
                        p.OfflineGraceSeconds,
                        p.RollbackToleranceSeconds,
                        p.CapabilityPolicyId,
                        canonicalJson,
                        signingResponse.Signature,
                        signingResponse.KeyId,
                        adminId,
                        now);

                    _dbContext.ShopEnforcementPolicies.Add(pol);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "policy.configured",
                        nameof(ShopEnforcementPolicy),
                        pol.Id.ToString(),
                        _serializer.SerializeCanonical(new { pol.Id, pol.ShopId, pol.PolicyVersion, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "ENFORCEMENT_POLICY_CONFIGURED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? $"Enforcement policy v{pol.PolicyVersion} configured",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(ShopEnforcementPolicy),
                        pol.Id.ToString(),
                        $"{{\"enforcementMode\":\"{pol.EnforcementMode}\",\"offlineGraceSeconds\":{pol.OfflineGraceSeconds}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (pol, $"policies/{pol.Id}", 1, pol.PolicyVersion);
                },
                recoverResultAsync: async record =>
                {
                    var polId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.ShopEnforcementPolicies.FindAsync(new object[] { polId }, cancellationToken)
                        ?? throw new CentralDomainException("POLICY_NOT_FOUND", "policy.not_found", $"Policy {polId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            if (securityTransaction is not null)
            {
                await securityTransaction.CommitAsync(cancellationToken);
            }

            return policyRecord;
        }

        var policy = ShopEnforcementPolicy.Create(
            p.ShopId,
            p.LicenseId,
            p.PolicyVersion,
            p.EnforcementMode,
            p.ExtensionEndUtc,
            p.AfterExtensionPolicyId,
            p.LeaseDurationSeconds,
            p.OfflineGraceSeconds,
            p.RollbackToleranceSeconds,
            p.CapabilityPolicyId,
            canonicalJson,
            signingResponse.Signature,
            signingResponse.KeyId,
            adminId,
            now);

        _dbContext.ShopEnforcementPolicies.Add(policy);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "ENFORCEMENT_POLICY_CONFIGURED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? $"Enforcement policy v{policy.PolicyVersion} configured",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(ShopEnforcementPolicy),
            policy.Id.ToString(),
            $"{{\"enforcementMode\":\"{policy.EnforcementMode}\",\"offlineGraceSeconds\":{policy.OfflineGraceSeconds}}}",
            true,
            digestSha256,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return policy;
    }
}
