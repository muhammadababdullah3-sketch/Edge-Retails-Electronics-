namespace EdgeRetails.Central.Application.DTOs;

public class PortalSessionContextDto
{
    public Guid AdminUserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = new();
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int ActiveSessionsCount { get; set; }
}

public class DashboardMetricsDto
{
    public int ActiveShopsCount { get; set; }
    public int LifetimeBaseCount { get; set; }
    public int FixedTermBaseCount { get; set; }
    public int ExpiringAddonsCount { get; set; }
    public int ExpiringFixedTermBasesCount { get; set; }
    public int PendingOrdersCount { get; set; }
    public int PendingEvidenceReviewsCount { get; set; }
    public int PendingHardwareReviewsCount { get; set; }
    public int PendingApprovalsCount { get; set; }
    public int OpenSupportTicketsCount { get; set; }
}

public class Customer360Dto
{
    public Guid ShopId { get; set; }
    public string ShopCode { get; set; } = string.Empty;
    public string ShopName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public CommercialLicenseSummaryDto? CommercialLicense { get; set; }
    public List<CommercialEntitlementSummaryDto> Addons { get; set; } = new();
    public List<InstallationSummaryDto> Installations { get; set; } = new();
    public List<OrderSummaryDto> RecentOrders { get; set; } = new();
    public List<SupportTicketSummaryDto> RecentTickets { get; set; } = new();
    public PolicySummaryDto? EnforcementPolicy { get; set; }
}

public class CommercialLicenseSummaryDto
{
    public Guid Id { get; set; }
    public string TermKind { get; set; } = string.Empty; // "LIFETIME" or "FIXED_TERM"
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? ValidUntilUtc { get; set; }
    public int AuthorityRevision { get; set; }
    public bool IsLifetimeBase => string.Equals(TermKind, "LIFETIME", StringComparison.OrdinalIgnoreCase);
}

public class CommercialEntitlementSummaryDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? ValidUntilUtc { get; set; }
    public bool IsActive => string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    public bool IsExpiringSoon { get; set; }
}

public class InstallationSummaryDto
{
    public Guid Id { get; set; }
    public string MachineFingerprint { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string OsPlatform { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; set; }
}

public class OrderSummaryDto
{
    public Guid Id { get; set; }
    public string PurchaseTarget { get; set; } = string.Empty;
    public string TermKind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class SupportTicketSummaryDto
{
    public Guid Id { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public class PolicySummaryDto
{
    public string EnforcementMode { get; set; } = string.Empty; // "FULL_STOP" or "CONFIGURABLE_EXTENSION"
    public int PolicyVersion { get; set; }
    public DateTimeOffset? ExtensionEndUtc { get; set; }
}

public class QueueItemDto
{
    public Guid EntityId { get; set; }
    public string QueueType { get; set; } = string.Empty;
    public Guid ShopId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? ReasonOrSummary { get; set; }
    public bool RequiresDualApproval { get; set; }
}

public class ActionPreviewRequestDto
{
    public string ActionType { get; set; } = string.Empty; // "REVOKE_LICENSE", "REPLACE_HARDWARE", "TRANSFER_PRIMARY", "RENEW_ADDON", "WATERMARK_POLICY"
    public Guid TargetEntityId { get; set; }
    public Guid ShopId { get; set; }
    public string? ProposedParametersJson { get; set; }
}

public class ActionPreviewResponseDto
{
    public string ActionType { get; set; } = string.Empty;
    public bool IsDestructive { get; set; }
    public int AffectedInstallationsCount { get; set; }
    public bool RequiresStepUp { get; set; }
    public bool RequiresDualApproval { get; set; }
    public string ConsequenceSummary { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

public class UnmaskPiiRequestDto
{
    public Guid AuditRecordId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class UnmaskPiiResponseDto
{
    public Guid AuditRecordId { get; set; }
    public string UnmaskedData { get; set; } = string.Empty;
    public DateTimeOffset AccessLoggedAtUtc { get; set; }
}

public class SensitiveApprovalDecisionDto
{
    public string Decision { get; set; } = "APPROVE"; // "APPROVE" or "REJECT"
    public string TargetPayloadSha256 { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
