using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class PaymentEvidence : Entity
{
    public const string ReviewPending = "PENDING";
    public const string ReviewApproved = "APPROVED";
    public const string ReviewRejected = "REJECTED";
    public const string ReviewClarification = "CLARIFICATION_REQUESTED";

    public Guid UploadSessionId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ShopId { get; private set; }
    public string StoragePath { get; private set; } = string.Empty;
    public string Sha256Digest { get; private set; } = string.Empty;
    public long ByteLength { get; private set; }
    public string MimeType { get; private set; } = string.Empty;
    public string ReviewStatus { get; private set; } = ReviewPending;
    public DateTimeOffset UploadedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private PaymentEvidence() { }

    public static PaymentEvidence CreateFromSession(
        UploadSession session,
        Guid orderId,
        string mimeType,
        DateTimeOffset uploadedAtUtc)
    {
        if (session == null || session.Status != UploadSession.StatusFinalized)
        {
            throw new CentralDomainException("UPLOAD_NOT_FINALIZED", "evidence.not_finalized", "Upload session must be finalized before evidence creation.");
        }
        if (orderId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ORDER_ID", "evidence.invalid_order_id", "Order ID is required.");
        }

        return new PaymentEvidence
        {
            Id = Guid.NewGuid(),
            UploadSessionId = session.Id,
            OrderId = orderId,
            ShopId = session.ShopId,
            StoragePath = session.StoragePath,
            Sha256Digest = session.ActualSha256,
            ByteLength = session.UploadedBytes,
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType.Trim(),
            ReviewStatus = ReviewPending,
            UploadedAtUtc = uploadedAtUtc,
            RowVersion = 1
        };
    }

    public void SetReviewDecision(string decision)
    {
        if (decision != ReviewApproved && decision != ReviewRejected && decision != ReviewClarification)
        {
            throw new CentralDomainException("INVALID_REVIEW_DECISION", "evidence.invalid_decision", $"Invalid review decision: {decision}");
        }
        ReviewStatus = decision;
        RowVersion++;
    }
}
