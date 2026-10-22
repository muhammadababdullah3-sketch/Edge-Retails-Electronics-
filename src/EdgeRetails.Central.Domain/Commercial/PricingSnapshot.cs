using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class PricingSnapshot : Entity
{
    public Guid ShopId { get; private set; }
    public Guid CatalogItemId { get; private set; }
    public string PurchaseTarget { get; private set; } = string.Empty;
    public string TermKind { get; private set; } = string.Empty;
    public string? BillingPeriodId { get; private set; }
    public decimal UnitPrice { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string ServerGeneratedDigest { get; private set; } = string.Empty;
    public int AuthorityVersion { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private PricingSnapshot() { }

    public static PricingSnapshot Create(
        Guid shopId,
        PlanCatalogItem item,
        int authorityVersion,
        DateTimeOffset createdAtUtc)
    {
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "pricing.invalid_shop_id", "Shop ID is required.");
        }
        if (item == null)
        {
            throw new CentralDomainException("INVALID_CATALOG_ITEM", "pricing.null_item", "Catalog item is required.");
        }

        string rawPayload = $"{shopId:N}:{item.Id:N}:{item.PurchaseTarget}:{item.TermKind}:{item.BillingPeriodId ?? "NONE"}:{item.UnitPrice:F2}:{item.Currency}:{authorityVersion}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        string digest = Convert.ToHexString(hash).ToLowerInvariant();

        return new PricingSnapshot
        {
            Id = Guid.NewGuid(),
            ShopId = shopId,
            CatalogItemId = item.Id,
            PurchaseTarget = item.PurchaseTarget,
            TermKind = item.TermKind,
            BillingPeriodId = item.BillingPeriodId,
            UnitPrice = item.UnitPrice,
            Currency = item.Currency,
            ServerGeneratedDigest = digest,
            AuthorityVersion = authorityVersion,
            CreatedAtUtc = createdAtUtc
        };
    }

    public bool MatchesSnapshot(decimal clientAmount, string clientCurrency)
    {
        return UnitPrice == clientAmount && string.Equals(Currency, clientCurrency, StringComparison.OrdinalIgnoreCase);
    }
}
