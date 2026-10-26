using System.Text.Json;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Support;

public class SupportTicket : Entity
{
    public const string StatusOpen = "OPEN";
    public const string StatusInProgress = "IN_PROGRESS";
    public const string StatusWaiting = "WAITING";
    public const string StatusClosed = "CLOSED";

    public Guid ShopId { get; private set; }
    public Guid RequesterPrincipalId { get; private set; }
    public Guid? AssignedAdminUserId { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusOpen;
    public int RedactionProfileVersion { get; private set; } = 1;
    public string AttachmentObjectIdsJson { get; private set; } = "[]";
    public string RepliesJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private SupportTicket() { }

    public static SupportTicket Create(
        Guid ticketId,
        Guid shopId,
        Guid requesterPrincipalId,
        string subject,
        string summary,
        List<Guid> attachmentObjectIds,
        int redactionProfileVersion,
        DateTimeOffset createdAtUtc)
    {
        if (ticketId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_TICKET_ID", "support.invalid_ticket_id", "Ticket ID is required.");
        }
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "support.invalid_shop_id", "Shop ID is required.");
        }
        if (requesterPrincipalId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_REQUESTER_ID", "support.invalid_requester_id", "Requester principal ID is required.");
        }
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new CentralDomainException("INVALID_SUBJECT", "support.invalid_subject", "Ticket subject is required.");
        }
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new CentralDomainException("INVALID_SUMMARY", "support.invalid_summary", "Ticket summary is required.");
        }
        if (redactionProfileVersion < 1)
        {
            throw new CentralDomainException("INVALID_REDACTION_PROFILE", "support.invalid_redaction_profile", "Redaction profile version must be at least 1.");
        }

        // PII and Secret Sanitization check: Ensure subject/summary does not contain raw private keys or passwords
        if (ContainsUnredactedSecret(subject) || ContainsUnredactedSecret(summary))
        {
            throw new CentralDomainException("SENSITIVE_SECRET_LEAKAGE", "support.secret_leakage", "Ticket content contains unredacted credentials or private keys.");
        }

        var attachments = attachmentObjectIds ?? new List<Guid>();

        return new SupportTicket
        {
            Id = ticketId,
            ShopId = shopId,
            RequesterPrincipalId = requesterPrincipalId,
            Subject = subject.Trim(),
            Summary = summary.Trim(),
            Status = StatusOpen,
            RedactionProfileVersion = redactionProfileVersion,
            AttachmentObjectIdsJson = JsonSerializer.Serialize(attachments),
            RepliesJson = "[]",
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void AddReply(string author, string replyText, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(replyText))
        {
            throw new CentralDomainException("INVALID_REPLY", "support.invalid_reply", "Reply text cannot be empty.");
        }
        if (ContainsUnredactedSecret(replyText))
        {
            throw new CentralDomainException("SENSITIVE_SECRET_LEAKAGE", "support.secret_leakage", "Reply content contains unredacted credentials or private keys.");
        }

        var existingReplies = string.IsNullOrWhiteSpace(RepliesJson)
            ? new List<SupportReplyDto>()
            : JsonSerializer.Deserialize<List<SupportReplyDto>>(RepliesJson) ?? new List<SupportReplyDto>();

        existingReplies.Add(new SupportReplyDto
        {
            ReplyId = Guid.NewGuid(),
            Kind = SupportReplyDto.KindMessage,
            Author = author,
            Text = replyText.Trim(),
            TimestampUtc = timestampUtc
        });

        RepliesJson = JsonSerializer.Serialize(existingReplies);
        UpdatedAtUtc = timestampUtc;
        RowVersion++;
    }

    public void SetStatus(string newStatus, string changedBy, DateTimeOffset timestampUtc)
    {
        if (newStatus != StatusOpen && newStatus != StatusInProgress && newStatus != StatusWaiting && newStatus != StatusClosed)
        {
            throw new CentralDomainException("INVALID_STATUS", "support.invalid_status", $"Invalid support ticket status: '{newStatus}'.");
        }

        if (string.IsNullOrWhiteSpace(changedBy))
        {
            throw new CentralDomainException("SUPPORT_ACTOR_REQUIRED", "support.actor_required", "A validated staff identity is required for status history.");
        }

        var previousStatus = Status;
        Status = newStatus;
        var events = string.IsNullOrWhiteSpace(RepliesJson)
            ? new List<SupportReplyDto>()
            : JsonSerializer.Deserialize<List<SupportReplyDto>>(RepliesJson) ?? new List<SupportReplyDto>();
        events.Add(new SupportReplyDto
        {
            ReplyId = Guid.NewGuid(),
            Kind = SupportReplyDto.KindStatusChanged,
            Author = changedBy,
            Text = $"{previousStatus} -> {newStatus}",
            TimestampUtc = timestampUtc
        });
        RepliesJson = JsonSerializer.Serialize(events);
        UpdatedAtUtc = timestampUtc;
        RowVersion++;
    }

    public void AssignTo(Guid adminUserId, string changedBy, DateTimeOffset timestampUtc)
    {
        if (adminUserId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SUPPORT_ASSIGNEE", "support.invalid_assignee", "A persisted staff assignee is required.");
        }
        if (string.IsNullOrWhiteSpace(changedBy))
        {
            throw new CentralDomainException("SUPPORT_ACTOR_REQUIRED", "support.actor_required", "A validated staff identity is required for assignment history.");
        }

        var previous = AssignedAdminUserId;
        AssignedAdminUserId = adminUserId;
        var events = string.IsNullOrWhiteSpace(RepliesJson)
            ? new List<SupportReplyDto>()
            : JsonSerializer.Deserialize<List<SupportReplyDto>>(RepliesJson) ?? new List<SupportReplyDto>();
        events.Add(new SupportReplyDto
        {
            ReplyId = Guid.NewGuid(),
            Kind = SupportReplyDto.KindAssignmentChanged,
            Author = changedBy,
            Text = previous.HasValue ? $"{previous:D} -> {adminUserId:D}" : $"Assigned to {adminUserId:D}",
            AssigneeAdminUserId = adminUserId,
            TimestampUtc = timestampUtc
        });
        RepliesJson = JsonSerializer.Serialize(events);
        UpdatedAtUtc = timestampUtc;
        RowVersion++;
    }

    private static bool ContainsUnredactedSecret(string value) =>
        value.Contains("BEGIN RSA PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret=", StringComparison.OrdinalIgnoreCase);
}

public class SupportReplyDto
{
    public const string KindMessage = "MESSAGE";
    public const string KindStatusChanged = "STATUS_CHANGED";
    public const string KindAssignmentChanged = "ASSIGNMENT_CHANGED";

    public Guid ReplyId { get; set; }
    public string Kind { get; set; } = KindMessage;
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Guid? AssigneeAdminUserId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
}
