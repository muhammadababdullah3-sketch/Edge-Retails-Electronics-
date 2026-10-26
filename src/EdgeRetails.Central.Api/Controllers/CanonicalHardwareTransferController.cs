using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
public sealed class CanonicalHardwareTransferController(
    ICentralDbContext db,
    IActivationService activation,
    IOperationLedgerService ledger,
    ICentralAuditService audit,
    ICanonicalJsonSerializer serializer,
    IStepUpService stepUp) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] Envelope = ["meta", "payload"];
    private static readonly string[] Meta = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];

    [HttpPost("/api/admin/v1/hardware-reviews/{id:guid}/decide")]
    public async Task<IActionResult> DecideHardware(Guid id, [FromBody] JsonElement envelope, CancellationToken ct)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (id == Guid.Empty || !Read(envelope, "hardware.review", out var meta, out var payload) ||
            !Exact(payload, "caseId", "installationId", "previousFingerprintId", "currentFingerprintId", "decision", "verificationEvidenceId") ||
            !GuidValue(payload, "caseId", out var caseId) || !GuidValue(payload, "installationId", out var installationId) ||
            !GuidValue(payload, "previousFingerprintId", out var previousId) || !GuidValue(payload, "currentFingerprintId", out var currentId) ||
            !GuidValue(payload, "verificationEvidenceId", out var evidenceId) || !StringValue(payload, "decision", out var decision) ||
            caseId != id || meta!.ExpectedVersion < 1 || decision is not ("APPROVE_REBIND" or "RECOMMEND_TRANSFER" or "REQUEST_INFORMATION" or "REJECT") ||
            serializer.SerializeAndHash(payload).Sha256Hex != meta!.PayloadSha256)
        {
            return BadRequest(new { code = "INVALID_HARDWARE_REVIEW", safeMessageKey = "hardware.review_invalid" });
        }

        var review = await db.HardwareReviews.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("HARDWARE_REVIEW_NOT_FOUND", "hw.not_found", "Hardware review was not found.");
        var evidence = await ValidEvidence(evidenceId, review.ShopId, ct);
        SensitiveApprovalRequest? approval = null;
        if (decision == "APPROVE_REBIND")
        {
            if (meta.SensitiveApprovalId is not Guid approvalId || approvalId == Guid.Empty)
            {
                throw new EdgeRetails.Central.Domain.Common.CentralDomainException("APPROVAL_REQUIRED", "approval.required", "Hardware rebind requires an independent approved sensitive-action request.");
            }
            approval = await db.SensitiveApprovalRequests.SingleOrDefaultAsync(x => x.Id == approvalId, ct)
                ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("APPROVAL_NOT_FOUND", "approval.not_found", "The hardware rebind approval was not found.");
            if (!string.Equals(approval.OperationType, "HARDWARE_REVIEW", StringComparison.OrdinalIgnoreCase) ||
                approval.TargetEntityId != review.Id || approval.RequesterAdminId == session.AdminUserId ||
                approval.RequesterAdminId == approval.ApproverAdminId ||
                !string.Equals(approval.TargetPayloadSha256, meta.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new EdgeRetails.Central.Domain.Common.CentralDomainException("APPROVAL_TARGET_MISMATCH", "approval.target_mismatch", "The approval must bind this exact hardware review payload and a distinct administrator.");
            }
        }
        else if (meta.SensitiveApprovalId is not null)
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("UNEXPECTED_APPROVAL", "approval.unexpected", "A sensitive approval may only accompany an approved hardware rebind.");
        }
        if (review.InstallationId != installationId || review.StoredMachineFingerprintId != previousId || review.ReportedMachineFingerprintId != currentId)
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("HARDWARE_TARGET_MISMATCH", "hw.target_mismatch", "The submitted fingerprint identities do not match this persisted review.");
        }

        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>($"admin:{session.AdminUserId:D}", review.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                if (db is not DbContext ef)
                {
                    throw new InvalidOperationException("Canonical hardware decisions require EF locked-row refresh.");
                }
                await ef.Entry(review).ReloadAsync(ct);
                await ef.Entry(evidence).ReloadAsync(ct);
                if (approval is not null)
                {
                    await ef.Entry(approval).ReloadAsync(ct);
                }
                if (review.RowVersion != meta.ExpectedVersion || review.Status != HardwareReview.StatusPending || review.InstallationId != installationId ||
                    review.StoredMachineFingerprintId != previousId || review.ReportedMachineFingerprintId != currentId || !EvidenceMatches(evidence, review.ShopId))
                {
                    throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "Review, evidence or target changed before the decision.");
                }
                var now = DateTimeOffset.UtcNow;
                if (approval is not null)
                {
                    if (approval.Status != SensitiveApprovalRequest.StatusApproved)
                    {
                        throw new EdgeRetails.Central.Domain.Common.CentralDomainException("APPROVAL_NOT_APPROVED", "approval.not_approved", "Hardware rebind approval must be approved before execution.");
                    }
                    if (approval.ExpiresAtUtc <= now)
                    {
                        throw new EdgeRetails.Central.Domain.Common.CentralDomainException("APPROVAL_EXPIRED", "approval.expired", "Hardware rebind approval has expired.");
                    }
                    await RequireExecutionStepUp(session, "hardware.review", meta.PayloadSha256, ct);
                    approval.Consume(now);
                }
                review.Decide(decision, evidenceId, session.AdminUserId, now);
                await db.SaveChangesAsync(ct);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId, "activation.hardware_review_decided", nameof(HardwareReview), id.ToString("D"),
                    serializer.SerializeCanonical(new { id, decision, installationId, evidenceId, clientOperationId = meta.ClientOperationId }), now));
                await audit.RecordAuditAsync("HARDWARE_REVIEW_DECIDED", "ADMIN", session.AdminUserId, session.SessionId, meta.Reason, meta.ClientOperationId,
                    meta.ClientOperationId, nameof(HardwareReview), id.ToString("D"), serializer.SerializeCanonical(new { decision, installationId, evidenceId }), true, meta.PayloadSha256, ct);
                return (id, $"hardware_reviews/{id:D}", review.RowVersion, 1);
            }, targetResource: $"hardware_review/{id:D}", cancellationToken: ct);
        return new JsonResult(outcome, Json);
    }

    [HttpPost("/api/admin/v1/transfers/{id:guid}/decide")]
    public async Task<IActionResult> DecideTransfer(Guid id, [FromBody] JsonElement envelope, CancellationToken ct)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (id == Guid.Empty || !Read(envelope, "primary.transfer", out var meta, out var payload) ||
            !Exact(payload, "transferId", "oldInstallationId", "newInstallationId", "recoveryType", "verificationEvidenceId", "decision") ||
            !GuidValue(payload, "transferId", out var transferId) || !GuidValue(payload, "oldInstallationId", out var oldId) ||
            !GuidValue(payload, "newInstallationId", out var newId) || !GuidValue(payload, "verificationEvidenceId", out var evidenceId) ||
            !StringValue(payload, "recoveryType", out var recovery) || !StringValue(payload, "decision", out var decision) || transferId != id ||
            recovery is not ("STANDARD_TRANSFER" or "LOST_HARDWARE_RECOVERY") || decision is not ("REQUEST" or "APPROVE" or "REJECT" or "CANCEL") ||
            serializer.SerializeAndHash(payload).Sha256Hex != meta!.PayloadSha256)
        {
            return BadRequest(new { code = "INVALID_PRIMARY_TRANSFER", safeMessageKey = "transfer.command_invalid" });
        }
        if (decision != "APPROVE" && meta!.SensitiveApprovalId is not null)
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("UNEXPECTED_APPROVAL", "approval.unexpected", "A sensitive approval may only accompany an approved primary transfer.");
        }

        var source = await db.Installations.SingleOrDefaultAsync(x => x.Id == oldId, ct);
        var target = await db.Installations.SingleOrDefaultAsync(x => x.Id == newId, ct);
        if (source is null || target is null || source.ShopId != target.ShopId || source.LicenseId != target.LicenseId)
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("TRANSFER_SCOPE_MISMATCH", "transfer.scope_mismatch", "Both installations must belong to the same persisted shop and license.");
        }
        var transfer = await db.PrimaryTransfers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (transfer is null && decision != "REQUEST")
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("TRANSFER_NOT_FOUND", "transfer.not_found", "Primary transfer was not found.");
        }
        var shopId = source.ShopId;
        if (transfer is not null && (transfer.ShopId != shopId || transfer.SourceInstallationId != oldId || transfer.TargetInstallationId != newId))
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("TRANSFER_TARGET_MISMATCH", "transfer.target_mismatch", "Persisted transfer scope does not match its installations.");
        }
        var evidence = await ValidEvidence(evidenceId, shopId, ct);

        if (transfer is null)
        {
            var createdOutcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>($"admin:{session.AdminUserId:D}", shopId, meta!.ToLedgerMeta(), payload,
                async _ =>
                {
                    if (db is not DbContext ef)
                    {
                        throw new InvalidOperationException("Canonical transfer requests require EF locked-row refresh.");
                    }
                    await ef.Entry(source).ReloadAsync(ct);
                    await ef.Entry(target).ReloadAsync(ct);
                    await ef.Entry(evidence).ReloadAsync(ct);
                    var existing = await db.PrimaryTransfers.AnyAsync(x => x.Id == id, ct);
                    if (meta.ExpectedVersion != 0 || existing || source.ShopId != target.ShopId || source.ShopId != shopId || source.LicenseId != target.LicenseId || !EvidenceMatches(evidence, shopId))
                    {
                        throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "Transfer identity, installation scope or evidence changed before request creation.");
                    }
                    var now = DateTimeOffset.UtcNow;
                    var requested = PrimaryTransfer.Initiate(id, shopId, oldId, newId, meta.Reason, session.AdminUserId, now);
                    requested.ApplyContractDecision("REQUEST", recovery, evidenceId, session.AdminUserId);
                    db.PrimaryTransfers.Add(requested);
                    await db.SaveChangesAsync(ct);
                    db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId, "activation.primary_transfer_initiated", nameof(PrimaryTransfer), id.ToString("D"),
                        serializer.SerializeCanonical(new { id, shopId, oldInstallationId = oldId, newInstallationId = newId, recovery, evidenceId, clientOperationId = meta.ClientOperationId }), now));
                    await audit.RecordAuditAsync("PRIMARY_TRANSFER_REQUESTED", "ADMIN", session.AdminUserId, session.SessionId, meta.Reason, meta.ClientOperationId,
                        meta.ClientOperationId, nameof(PrimaryTransfer), id.ToString("D"), serializer.SerializeCanonical(new { oldInstallationId = oldId, newInstallationId = newId, recovery, evidenceId }), true, meta.PayloadSha256, ct);
                    return (id, $"transfers/{id:D}", requested.RowVersion, 1);
                }, targetResource: $"primary_transfer/{id:D}", cancellationToken: ct);
            return new JsonResult(createdOutcome, Json);
        }

        if (decision == "APPROVE")
        {
            if (meta!.SensitiveApprovalId is not Guid approvalId || approvalId == Guid.Empty)
            {
                throw new EdgeRetails.Central.Domain.Common.CentralDomainException("APPROVAL_REQUIRED", "transfer.approval_required", "Approval requires the approved dual-control request.");
            }
            // Keep grant consumption and the existing transfer ledger mutation in
            // one transaction. A matching committed replay needs no new grant.
            await using var transaction = await SecurityTransaction.BeginAsync(db, id.ToString(), ct);
            if (await ledger.GetOutcomeAsync(meta.ClientOperationId, $"admin:{session.AdminUserId:D}", ct) is null)
            {
                await RequireExecutionStepUp(session, "primary.transfer", meta.PayloadSha256, ct);
            }
            await activation.ApprovePrimaryTransferAsync(id, session.AdminUserId, approvalId,
                new ApprovePrimaryTransferCommand { CommandMeta = meta.ToLedgerMeta(), SensitiveApprovalId = approvalId, ContractRecoveryType = recovery, ContractEvidenceId = evidenceId, CanonicalPayload = payload }, ct);
            var approvalOutcome = await ledger.GetOutcomeAsync(meta!.ClientOperationId, $"admin:{session.AdminUserId:D}", ct);
            if (approvalOutcome is not null && transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
            return approvalOutcome is null ? StatusCode(StatusCodes.Status500InternalServerError) : new JsonResult(approvalOutcome, Json);
        }
        else
        {
            var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>($"admin:{session.AdminUserId:D}", transfer.ShopId, meta!.ToLedgerMeta(), payload,
                async _ =>
                {
                    if (db is not DbContext ef)
                    {
                        throw new InvalidOperationException("Canonical transfer decisions require EF locked-row refresh.");
                    }
                    await ef.Entry(transfer).ReloadAsync(ct);
                    await ef.Entry(evidence).ReloadAsync(ct);
                    if (transfer.RowVersion != meta.ExpectedVersion || transfer.SourceInstallationId != oldId || transfer.TargetInstallationId != newId || !EvidenceMatches(evidence, transfer.ShopId))
                    {
                        throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "Transfer, evidence or target changed before the decision.");
                    }
                    var now = DateTimeOffset.UtcNow;
                    transfer.ApplyContractDecision(decision, recovery, evidenceId, session.AdminUserId);
                    await db.SaveChangesAsync(ct);
                    db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId, "activation.primary_transfer_decided", nameof(PrimaryTransfer), id.ToString("D"),
                        serializer.SerializeCanonical(new { id, decision, recovery, evidenceId, clientOperationId = meta.ClientOperationId }), now));
                    await audit.RecordAuditAsync("PRIMARY_TRANSFER_DECIDED", "ADMIN", session.AdminUserId, session.SessionId, meta.Reason, meta.ClientOperationId,
                        meta.ClientOperationId, nameof(PrimaryTransfer), id.ToString("D"), serializer.SerializeCanonical(new { decision, recovery, evidenceId }), true, meta.PayloadSha256, ct);
                    return (id, $"transfers/{id:D}", transfer.RowVersion, 1);
                }, targetResource: $"primary_transfer/{id:D}", cancellationToken: ct);
            return new JsonResult(outcome, Json);
        }
    }

    private async Task<EncryptedObjectRecord> ValidEvidence(Guid id, Guid shopId, CancellationToken ct)
    {
        var evidence = await db.EncryptedObjects.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("EVIDENCE_NOT_FOUND", "support.evidence_not_found", "Verification evidence was not found.");
        if (!EvidenceMatches(evidence, shopId))
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("EVIDENCE_SCOPE_INVALID", "support.evidence_scope_invalid", "Evidence must be an active support attachment belonging to the same shop.");
        }
        return evidence;
    }
    private async Task RequireExecutionStepUp(AdminSessionDto session, string action, string hash, CancellationToken ct)
    {
        if (!await stepUp.ValidateAndConsumeStepUpGrantAsync(session.SessionId, action, hash, cancellationToken: ct))
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("STEP_UP_REQUIRED", "auth.step_up_required", "A fresh execution-session step-up grant for this exact decision is required.");
        }
    }
    private static bool EvidenceMatches(EncryptedObjectRecord e, Guid shop) => e.ShopId == shop && e.Purpose == EncryptedObjectRecord.PurposeSupportAttachment && !e.IsTombstoned;
    private static bool Read(JsonElement e, string type, out CanonicalDirectoryMeta? meta, out JsonElement payload)
    {
        meta = null; payload = default;
        if (!Exact(e, Envelope) || !e.TryGetProperty("meta", out var m) || m.ValueKind != JsonValueKind.Object || !Only(m, Meta) || !e.TryGetProperty("payload", out payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        try
        {
            meta = JsonSerializer.Deserialize<CanonicalDirectoryMeta>(m.GetRawText(), Json);
        }
        catch (JsonException)
        {
            return false;
        }
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == type && meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 0 && meta.PolicyVersion >= 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason) && Regex.IsMatch(meta.PayloadSha256 ?? "", "\\A[a-f0-9]{64}\\z");
    }
    private static bool Exact(JsonElement e, params string[] names) => e.ValueKind == JsonValueKind.Object && Only(e, names) && e.EnumerateObject().Count() == names.Length;
    private static bool Only(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            if (!names.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name))
            {
                return false;
            }
        }
        return true;
    }
    private static bool GuidValue(JsonElement e, string name, out Guid v)
    {
        v = Guid.Empty;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out v) && v != Guid.Empty;
    }
    private static bool StringValue(JsonElement e, string name, out string v)
    {
        v = "";
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        v = p.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(v);
    }
}
