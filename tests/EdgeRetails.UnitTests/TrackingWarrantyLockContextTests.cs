using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.UnitTests;

public sealed class TrackingWarrantyLockContextTests
{
    [Fact]
    public async Task Shop_changed_supplier_after_discovery_fails_before_allocating_or_posting()
    {
        var f = new Phase2TestDoubles();
        var product = new Product { Name = "Context test", Sku = "CTX", TrackingMode = TrackingMode.Serialized, SerialTrackingEnabled = true };
        f.Catalog.Products[product.Id] = product;
        var warrantyCase = new ShopStockWarrantyCase { ProductId = product.Id, SupplierId = Guid.NewGuid(), Status = ShopWarrantyCaseStatus.WithSupplier };
        f.Warranty.AddShopStockCase(warrantyCase);
        var locks = new AtIdentityLock(() => warrantyCase.SupplierId = Guid.NewGuid());
        var conditions = new InventoryConditionService(f.Catalog, f.Inventory, f.CostAllocator, f.Clock);
        var handler = new ReceiveShopStockWarrantyHandler(f.Catalog, f.Inventory, conditions, f.CostAllocator,
            f.Warranty, f.Traceability, f.Parties, f.SupplierAccounts, f.OperationLock, locks, f.Authorization,
            f.Numbers, f.Audit, f.Clock, f.Transactions, f.UnitOfWork, f.PhysicalUnits);
        var result = await handler.HandleAsync(new(warrantyCase.Id, WarrantyResolutionType.Replaced,
            Guid.NewGuid(), [Guid.NewGuid()], [new("CTX-REPLACEMENT", null, null)], null, Guid.NewGuid()), default);
        Assert.Equal("warranty.shop_context_changed_retry", result.Error?.Code);
        AssertNoPosting(f);
    }

    [Fact]
    public async Task Customer_changed_product_link_after_discovery_fails_before_allocating_or_posting()
    {
        var f = new Phase2TestDoubles();
        var supplier = new Supplier { Name = "Context dealer", DealerCode = "CTX", IsActive = true };
        f.Parties.Suppliers[supplier.Id] = supplier;
        var claim = new WarrantyClaim { SupplierId = supplier.Id, Status = WarrantyClaimStatus.SentToSupplier };
        f.Warranty.AddClaim(claim);
        var item = new WarrantyClaimItem { ClaimId = claim.Id, ProductId = Guid.NewGuid() };
        f.Warranty.AddClaimItem(item);
        var link = new WarrantyClaimItemUnit { ClaimItemId = item.Id, OriginalInventoryUnitId = Guid.NewGuid() };
        f.Warranty.AddClaimItemUnit(link);
        var locks = new AtIdentityLock(() => item.ProductId = Guid.NewGuid());
        var handler = new ReceiveCustomerWarrantyReplacementHandler(f.Warranty, f.Catalog, f.Inventory,
            f.Traceability, f.Parties, f.OperationLock, locks, f.Authorization, f.Clock, f.Transactions,
            f.UnitOfWork, f.PhysicalUnits);
        var result = await handler.HandleAsync(new(claim.Id, Guid.NewGuid(), Guid.NewGuid(),
            [new(link.Id, "CTX-REPLACEMENT", null, null)], null), default);
        Assert.Equal("warranty.replacement_context_changed_retry", result.Error?.Code);
        AssertNoPosting(f);
    }

    private static void AssertNoPosting(Phase2TestDoubles f)
    {
        Assert.Empty(f.Inventory.Units);
        Assert.Empty(f.Inventory.Movements);
        Assert.Empty(f.Warranty.Operations);
        Assert.Empty(f.Warranty.ClaimEvents);
        Assert.Empty(f.Warranty.ResolutionAllocations);
        Assert.Empty(f.Audit.Records);
    }

    private sealed class AtIdentityLock(Action mutate) : IResourceLock
    {
        public Task AcquireAsync(string resourceType, string key, CancellationToken cancellationToken)
        {
            if (resourceType == "inventory-identity")
            {
                mutate();
            }
            return Task.CompletedTask;
        }
        public Task AcquireAsync(string resourceType, Guid id, CancellationToken cancellationToken) =>
            AcquireAsync(resourceType, id.ToString("D"), cancellationToken);
    }
}
