using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

internal static class Phase2PostgresTestHarness
{
    public static ServiceProvider BuildProvider()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        return services.BuildServiceProvider();
    }

    public static async Task EnsureReceiptConfigurationAsync(EdgeRetailsDbContext db)
    {
        if (!await db.ShopProfiles.AnyAsync(x => x.ProfileKey == "PRIMARY"))
        {
            db.ShopProfiles.Add(new ShopProfile
            {
                ProfileKey = "PRIMARY",
                ShopName = "Edge Retails Integration Shop",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!await db.ReceiptTemplateSettings.AnyAsync(x => x.TemplateKey == "PRIMARY"))
        {
            db.ReceiptTemplateSettings.Add(new ReceiptTemplateSettings
            {
                TemplateKey = "PRIMARY",
                LogoBehavior = "NONE",
                TemplateVersion = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    public static async Task<QuantityProductFixture> SeedQuantityProductAsync(
        EdgeRetailsDbContext db,
        decimal defaultSalePrice = 150m)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "pc-" + suffix[..8],
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Product-" + suffix,
            Sku = "SKU-" + suffix[..12],
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = defaultSalePrice,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        var supplier = new Supplier
        {
            Name = "Supplier-" + suffix,
            DealerCode = TraceabilityCodeRules.BuildDealerCode("SU", Random.Shared.Next(1000, 999999)),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        db.ChangeTracker.Clear();

        return new QuantityProductFixture(
            product.Id,
            productUnit.Id,
            supplier.Id,
            actorId,
            unit.Id);
    }

    public static async Task<SerializedProductFixture> SeedSerializedProductAsync(
        EdgeRetailsDbContext db,
        decimal defaultSalePrice = 5000m,
        int defaultWarrantyMonths = 12)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new Unit
        {
            Name = "Serialized Unit-" + suffix,
            Symbol = "su-" + suffix[..8],
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Serialized Product-" + suffix,
            Sku = "SP-" + suffix[..12],
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = defaultSalePrice,
            DefaultWarrantyMonths = defaultWarrantyMonths,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        var supplier = new Supplier
        {
            Name = "Serialized Supplier-" + suffix,
            DealerCode = TraceabilityCodeRules.BuildDealerCode("SS", Random.Shared.Next(1000, 999999)),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        db.ChangeTracker.Clear();

        return new SerializedProductFixture(
            product.Id,
            productUnit.Id,
            supplier.Id,
            actorId,
            unit.Id);
    }

    public static async Task<Customer> SeedCustomerAsync(
        EdgeRetailsDbContext db,
        string name = "Test Customer")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new Customer
        {
            Name = $"{name} {suffix}",
            Phone = "0300" + Random.Shared.Next(1000000, 9999999),
            Address = "Test City",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    public static async Task<Supplier> SeedSupplierAsync(
        EdgeRetailsDbContext db,
        string name = "Test Supplier",
        string prefix = "TS")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplier = new Supplier
        {
            Name = $"{name} {suffix}",
            DealerCode = TraceabilityCodeRules.BuildDealerCode(prefix, Random.Shared.Next(1000, 999999)),
            Phone = "0300" + Random.Shared.Next(1000000, 9999999),
            Address = "Market Area",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    public static async Task<CashSession> SeedOpenCashSessionAsync(
        EdgeRetailsDbContext db,
        Guid actorId,
        decimal openingCash = 10000m)
    {
        var session = new CashSession
        {
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = actorId,
            OpenedAt = DateTimeOffset.UtcNow,
            OpeningCash = openingCash,
            Status = CashSessionStatus.Open,
            Version = 1
        };
        db.CashSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }
}

internal sealed record QuantityProductFixture(
    Guid ProductId,
    Guid ProductUnitId,
    Guid SupplierId,
    Guid ActorId,
    Guid UnitId);

internal sealed record SerializedProductFixture(
    Guid ProductId,
    Guid ProductUnitId,
    Guid SupplierId,
    Guid ActorId,
    Guid UnitId);
