using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class CommercialEntitlement : Entity
{
    public const string KindSupport = "SUPPORT";
    public const string KindPremiumUpgrade = "PREMIUM_UPGRADE";

    public const string StatusNotPurchased = "NOT_PURCHASED";
    public const string StatusActive = "ACTIVE";
    public const string StatusExpired = "EXPIRED";

    public Guid LicenseId { get; private set; }
    public string Kind { get; private set; } = KindSupport;
    public string Status { get; private set; } = StatusNotPurchased;
    public DateTimeOffset? ValidUntilUtc { get; private set; }
    public Guid LastOrderId { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private CommercialEntitlement() { }

    public static CommercialEntitlement Create(
        Guid licenseId,
        string kind,
        DateTimeOffset? validUntilUtc,
        Guid orderId)
    {
        if (licenseId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_LICENSE_ID", "entitlement.invalid_license_id", "License ID is required.");
        }
        if (kind != KindSupport && kind != KindPremiumUpgrade)
        {
            throw new CentralDomainException("INVALID_KIND", "entitlement.invalid_kind", $"Invalid entitlement kind: {kind}");
        }

        return new CommercialEntitlement
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Kind = kind,
            Status = validUntilUtc.HasValue ? StatusActive : StatusNotPurchased,
            ValidUntilUtc = validUntilUtc,
            LastOrderId = orderId,
            RowVersion = 1
        };
    }

    public void RenewAddon(DateTimeOffset newValidUntilUtc, Guid orderId)
    {
        if (orderId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ORDER_ID", "entitlement.invalid_order_id", "Order ID is required.");
        }
        if (ValidUntilUtc.HasValue && newValidUntilUtc <= ValidUntilUtc.Value)
        {
            throw new CentralDomainException("INVALID_RENEWAL_DATE", "entitlement.invalid_renewal_date", "New expiration date must be after current expiration.");
        }

        ValidUntilUtc = newValidUntilUtc;
        Status = StatusActive;
        LastOrderId = orderId;
        RowVersion++;
    }

    public void CheckExpiration(DateTimeOffset trustedTimeUtc)
    {
        if (ValidUntilUtc.HasValue && trustedTimeUtc > ValidUntilUtc.Value && Status == StatusActive)
        {
            Status = StatusExpired;
            RowVersion++;
        }
    }
}
