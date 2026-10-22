using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class PaymentReviewRecord : Entity
{
    public const string DecisionApprove = "APPROVE";
    public const string DecisionReject = "REJECT";
    public const string DecisionClarification = "REQUEST_CLARIFICATION";

    public Guid EvidenceId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ReviewerAdminId { get; private set; }
    public string Decision { get; private set; } = DecisionApprove;
    public string? ReasonCode { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset DecidedAtUtc { get; private set; }

    private PaymentReviewRecord() { }

    public static PaymentReviewRecord Create(
        Guid evidenceId,
        Guid orderId,
        Guid reviewerAdminId,
        string decision,
        string? notes,
        DateTimeOffset decidedAtUtc)
        => Create(evidenceId, orderId, reviewerAdminId, decision, null, notes, decidedAtUtc);

    public static PaymentReviewRecord Create(
        Guid evidenceId,
        Guid orderId,
        Guid reviewerAdminId,
        string decision,
        string? reasonCode,
        string? notes,
        DateTimeOffset decidedAtUtc)
    {
        if (evidenceId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_EVIDENCE_ID", "review.invalid_evidence_id", "Evidence ID is required.");
        }
        if (orderId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ORDER_ID", "review.invalid_order_id", "Order ID is required.");
        }
        if (reviewerAdminId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_REVIEWER_ID", "review.invalid_reviewer_id", "Reviewer Admin ID is required.");
        }
        if (decision != DecisionApprove && decision != DecisionReject && decision != DecisionClarification)
        {
            throw new CentralDomainException("INVALID_DECISION", "review.invalid_decision", $"Invalid review decision: {decision}");
        }

        return new PaymentReviewRecord
        {
            Id = Guid.NewGuid(),
            EvidenceId = evidenceId,
            OrderId = orderId,
            ReviewerAdminId = reviewerAdminId,
            Decision = decision,
            ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode.Trim(),
            Notes = notes,
            DecidedAtUtc = decidedAtUtc
        };
    }
}
