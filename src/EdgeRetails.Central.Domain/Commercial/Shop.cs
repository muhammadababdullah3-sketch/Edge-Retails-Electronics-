using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class Shop : Entity
{
    public const string StatusActive = "ACTIVE";
    public const string StatusSuspended = "SUSPENDED";
    public const string StatusClosed = "CLOSED";

    public Guid CustomerId { get; private set; }
    public string ShopCode { get; private set; } = string.Empty;
    public string ShopName { get; private set; } = string.Empty;
    public string? ContactReference { get; private set; }
    public string Status { get; private set; } = StatusActive;
    public int PrimaryCapacity { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private Shop() { }

    public static Shop RegisterFromContract(Guid customerId, string shopName, string? contactReference, DateTimeOffset now)
    {
        var shop = Create(customerId, Guid.NewGuid().ToString("N"), shopName, now);
        shop.ContactReference = contactReference;
        return shop;
    }

    public static Shop Create(Guid customerId, string shopCode, string shopName, DateTimeOffset createdAtUtc)
    {
        if (customerId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_CUSTOMER_ID", "shop.invalid_customer_id", "Customer ID is required.");
        }
        if (string.IsNullOrWhiteSpace(shopCode))
        {
            throw new CentralDomainException("INVALID_SHOP_CODE", "shop.invalid_shop_code", "Shop code is required.");
        }
        if (string.IsNullOrWhiteSpace(shopName))
        {
            throw new CentralDomainException("INVALID_SHOP_NAME", "shop.invalid_shop_name", "Shop name is required.");
        }

        return new Shop
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ShopCode = shopCode.Trim().ToUpperInvariant(),
            ShopName = shopName.Trim(),
            Status = StatusActive,
            PrimaryCapacity = 1, // Single-primary V1 invariant
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void SetStatus(string status)
    {
        if (status != StatusActive && status != StatusSuspended && status != StatusClosed)
        {
            throw new CentralDomainException("INVALID_STATUS", "shop.invalid_status", $"Invalid shop status: {status}");
        }
        Status = status;
        RowVersion++;
    }
}
