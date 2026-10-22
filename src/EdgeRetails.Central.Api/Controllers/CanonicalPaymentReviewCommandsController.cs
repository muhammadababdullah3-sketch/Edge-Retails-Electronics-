using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/payments")]
public sealed class CanonicalPaymentReviewCommandsController(
    ICentralDbContext db,
    IOperationLedgerService ledger,
    ICentralAuditService audit,
    ICanonicalJsonSerializer serializer) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["orderId", "proofId", "decision", "reasonCode", "internalNote"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost("{paymentId:guid}/review")]
    public async Task<IActionResult> Review(
        [FromRoute] Guid paymentId,
        [FromBody] JsonElement envelope,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (paymentId == Guid.Empty || !ReadEnvelope(envelope, out var meta, out var payload) ||
            !TryReadPayload(payload, out var command) || command.ProofId != paymentId)
        {
            return BadRequest(new { code = "INVALID_PAYMENT_REVIEW", safeMessageKey = "payment.invalid_review" });
        }

        var evidence = await db.PaymentEvidences.SingleOrDefaultAsync(x => x.Id == paymentId, cancellationToken)
            ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("EVIDENCE_NOT_FOUND", "review.evidence_not_found", "The payment proof does not exist.");
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == command.OrderId, cancellationToken)
            ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("ORDER_NOT_FOUND", "review.order_not_found", "The order does not exist.");
        if (evidence.OrderId != order.Id || evidence.ShopId != order.ShopId)
        {
            throw new EdgeRetails.Central.Domain.Common.CentralDomainException("EVIDENCE_ORDER_MISMATCH", "review.evidence_order_mismatch", "The payment proof does not belong to the specified order and shop.");
        }

        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>(
            $"admin:{session.AdminUserId:D}", order.ShopId, meta!.ToLedgerMeta(), payload,
            async _ =>
            {
                if (meta.ExpectedVersion != evidence.RowVersion)
                {
                    throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The payment proof changed before review.");
                }

                var now = DateTimeOffset.UtcNow;
                var decision = command.Decision switch
                {
                    "APPROVE" => PaymentReviewRecord.DecisionApprove,
                    "REJECT" => PaymentReviewRecord.DecisionReject,
                    "CLARIFICATION" => PaymentReviewRecord.DecisionClarification,
                    _ => throw new EdgeRetails.Central.Domain.Common.CentralDomainException("INVALID_DECISION", "review.invalid_decision", "The payment decision is invalid.")
                };
                var review = PaymentReviewRecord.Create(evidence.Id, order.Id, session.AdminUserId,
                    decision, command.ReasonCode, command.InternalNote, now);
                switch (decision)
                {
                    case PaymentReviewRecord.DecisionApprove:
                        evidence.SetReviewDecision(PaymentEvidence.ReviewApproved);
                        order.ApprovePayment(evidence.Id, session.AdminUserId, now);
                        break;
                    case PaymentReviewRecord.DecisionReject:
                        evidence.SetReviewDecision(PaymentEvidence.ReviewRejected);
                        order.Reject(command.ReasonCode);
                        break;
                    default:
                        evidence.SetReviewDecision(PaymentEvidence.ReviewClarification);
                        break;
                }

                db.PaymentReviewRecords.Add(review);
                await db.SaveChangesAsync(cancellationToken);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId,
                    "commercial.payment_reviewed", nameof(PaymentReviewRecord), review.Id.ToString("D"),
                    serializer.SerializeCanonical(new { reviewId = review.Id, orderId = order.Id, proofId = evidence.Id, decision, reasonCode = command.ReasonCode, clientOperationId = meta.ClientOperationId }), now));
                await audit.RecordAuditAsync("COMMERCIAL_PAYMENT_REVIEWED", "ADMIN", session.AdminUserId,
                    session.SessionId, meta.Reason, meta.ClientOperationId, meta.ClientOperationId,
                    nameof(PaymentReviewRecord), review.Id.ToString("D"),
                    serializer.SerializeCanonical(new { orderId = order.Id, proofId = evidence.Id, decision, reasonCode = command.ReasonCode }),
                    true, meta.PayloadSha256, cancellationToken);
                return (review.Id, $"payment_reviews/{review.Id:D}", evidence.RowVersion, 1);
            },
            targetResource: $"payment_review/{paymentId:D}",
            cancellationToken: cancellationToken);

        return new JsonResult(outcome, WebJson);
    }

    private static bool ReadEnvelope(JsonElement envelope, out CanonicalDirectoryMeta? meta, out JsonElement payload)
    {
        meta = null;
        payload = default;
        if (!HasExactUniqueProperties(envelope, EnvelopeFields) ||
            !envelope.TryGetProperty("meta", out var metaElement) || metaElement.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("payload", out payload) || payload.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(metaElement, MetaFields))
        {
            return false;
        }
        try { meta = JsonSerializer.Deserialize<CanonicalDirectoryMeta>(metaElement.GetRawText(), WebJson); }
        catch (JsonException) { return false; }
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == "payment.review" &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 0 && meta.PolicyVersion >= 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason) && Regex.IsMatch(meta.PayloadSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z");
    }

    private static bool TryReadPayload(JsonElement payload, out CanonicalPaymentReviewPayload command)
    {
        command = default!;
        if (!HasOnlyUniqueProperties(payload, PayloadFields) ||
            !TryGuid(payload, "orderId", out var orderId) || !TryGuid(payload, "proofId", out var proofId) ||
            !TryString(payload, "decision", out var decision) || !TryString(payload, "reasonCode", out var reasonCode) ||
            decision is not ("APPROVE" or "REJECT" or "CLARIFICATION"))
        {
            return false;
        }

        string? internalNote = null;
        if (payload.TryGetProperty("internalNote", out var note))
        {
            if (note.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(note.GetString()))
            {
                return false;
            }
            internalNote = note.GetString();
        }
        command = new CanonicalPaymentReviewPayload(orderId, proofId, decision, reasonCode, internalNote);
        return true;
    }

    private static bool TryGuid(JsonElement value, string name, out Guid result)
    {
        result = Guid.Empty;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out result) && result != Guid.Empty;
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
    }

    private static bool HasExactUniqueProperties(JsonElement value, string[] fields) =>
        value.ValueKind == JsonValueKind.Object && HasOnlyUniqueProperties(value, fields) && value.EnumerateObject().Count() == fields.Length;

    private static bool HasOnlyUniqueProperties(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
            {
                return false;
            }
        }
        return true;
    }

    private sealed record CanonicalPaymentReviewPayload(Guid OrderId, Guid ProofId, string Decision, string ReasonCode, string? InternalNote);
}
