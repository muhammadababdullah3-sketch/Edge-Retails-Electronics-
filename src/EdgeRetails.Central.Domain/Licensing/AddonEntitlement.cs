namespace EdgeRetails.Central.Domain.Licensing;

public record AddonEntitlement
{
    public const string KindSupport = "SUPPORT";
    public const string KindPremiumUpgrade = "PREMIUM_UPGRADE";

    public const string StatusActive = "ACTIVE";
    public const string StatusExpired = "EXPIRED";
    public const string StatusNotPurchased = "NOT_PURCHASED";

    public Guid EntitlementId { get; init; }
    public string Kind { get; init; }
    public string Status { get; init; }
    public DateTimeOffset? ValidUntilUtc { get; init; }

    public AddonEntitlement(Guid entitlementId, string kind, string status, DateTimeOffset? validUntilUtc)
    {
        EntitlementId = entitlementId;
        Kind = kind;
        Status = status;
        ValidUntilUtc = validUntilUtc;
    }
}
