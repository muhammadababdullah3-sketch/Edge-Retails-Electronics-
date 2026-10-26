using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class PlanCatalogItem : Entity
{
    public const string TargetBaseLicense = "BASE_LICENSE";
    public const string TargetSupport = "SUPPORT";
    public const string TargetPremiumUpgrade = "PREMIUM_UPGRADE";

    public const string TermFixed = "FIXED_TERM";
    public const string TermLifetime = "LIFETIME";

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string PurchaseTarget { get; private set; } = TargetBaseLicense;
    public string TermKind { get; private set; } = TermFixed;
    public string? BillingPeriodId { get; private set; }
    public decimal UnitPrice { get; private set; }
    public string Currency { get; private set; } = "USD";
    public bool IsActive { get; private set; } = true;
    public int RowVersion { get; private set; } = 1;

    private PlanCatalogItem() { }

    public static PlanCatalogItem Create(
        string code,
        string name,
        string purchaseTarget,
        string termKind,
        string? billingPeriodId,
        decimal unitPrice,
        string currency)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new CentralDomainException("INVALID_CODE", "catalog.invalid_code", "Catalog item code is required.");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CentralDomainException("INVALID_NAME", "catalog.invalid_name", "Catalog item name is required.");
        }
        if (purchaseTarget != TargetBaseLicense && purchaseTarget != TargetSupport && purchaseTarget != TargetPremiumUpgrade)
        {
            throw new CentralDomainException("INVALID_TARGET", "catalog.invalid_target", $"Invalid purchase target: {purchaseTarget}");
        }
        if (termKind != TermFixed && termKind != TermLifetime)
        {
            throw new CentralDomainException("INVALID_TERM", "catalog.invalid_term", $"Invalid term kind: {termKind}");
        }
        if (termKind == TermLifetime && purchaseTarget != TargetBaseLicense)
        {
            throw new CentralDomainException("LIFETIME_ADDON_PROHIBITED", "catalog.lifetime_addon_prohibited", "Lifetime term is only valid for BASE_LICENSE.");
        }
        if (termKind == TermLifetime && billingPeriodId != null)
        {
            throw new CentralDomainException("LIFETIME_NO_BILLING_PERIOD", "catalog.lifetime_no_billing_period", "Lifetime items must have null billingPeriodId.");
        }
        if (unitPrice < 0)
        {
            throw new CentralDomainException("INVALID_PRICE", "catalog.invalid_price", "Unit price cannot be negative.");
        }

        return new PlanCatalogItem
        {
            Id = Guid.NewGuid(),
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            PurchaseTarget = purchaseTarget,
            TermKind = termKind,
            BillingPeriodId = billingPeriodId?.Trim().ToUpperInvariant(),
            UnitPrice = unitPrice,
            Currency = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant(),
            IsActive = true,
            RowVersion = 1
        };
    }
}
