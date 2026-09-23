using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

public sealed class SerializedSalesPurchasingPostgresTests
{
    [Fact]
    public async Task Serialized_Unit_Preserves_Exact_Lot_Provenance_Through_Sale_And_Return()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await EnsureReceiptConfigurationAsync(db);
        var fixture = await SeedSerializedProductAsync(db);

        var serialA = "SN-" + Guid.NewGuid().ToString("N")[..12];
        var serialB = "SN-" + Guid.NewGuid().ToString("N")[..12];

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var purchase = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "SER-" + Guid.NewGuid().ToString("N")[..10],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        2m,
                        4000m,
                        5000m,
                        [
                            new SerializedIdentityInput(serialA),
                            new SerializedIdentityInput(serialB)
                        ])
                ]),
            CancellationToken.None);

        Assert.True(purchase.IsSuccess, purchase.Error?.Message);

        db.ChangeTracker.Clear();
        var receivedUnits = await db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.ProductId == fixture.ProductId)
            .OrderBy(x => x.SerialNumber)
            .ToListAsync();

        Assert.Equal(2, receivedUnits.Count);
        Assert.All(receivedUnits, x => Assert.Equal(InventoryUnitStatus.InStock, x.Status));
        Assert.All(receivedUnits, x => Assert.NotNull(x.InventoryLotId));
        Assert.All(receivedUnits, x => Assert.Equal(4000m, x.AcquisitionCost));

        var selected = receivedUnits.Single(x => x.SerialNumber == serialA.ToUpperInvariant());
        var selectedOriginalLot = selected.InventoryLotId!.Value;
        var other = receivedUnits.Single(x => x.Id != selected.Id);

        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var sale = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                null,
                0m,
                SalePaymentMethod.Bank,
                5000m,
                "SERIAL-BANK",
                null,
                [
                    new CompleteSaleLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        1m,
                        5000m,
                        [selected.Id])
                ]),
            CancellationToken.None);

        Assert.True(sale.IsSuccess, sale.Error?.Message);
        Assert.NotNull(sale.Value);

        db.ChangeTracker.Clear();
        var selectedAfterSale = await db.InventoryUnits
            .AsNoTracking()
            .SingleAsync(x => x.Id == selected.Id);
        var otherAfterSale = await db.InventoryUnits
            .AsNoTracking()
            .SingleAsync(x => x.Id == other.Id);

        Assert.Equal(InventoryUnitStatus.Sold, selectedAfterSale.Status);
        Assert.Equal(InventoryUnitStatus.InStock, otherAfterSale.Status);

        var saleItem = await db.SaleItems
            .AsNoTracking()
            .SingleAsync(x => x.SaleId == sale.Value!.SaleId);
        var saleItemUnit = await db.SaleItemUnits
            .AsNoTracking()
            .SingleAsync(x => x.SaleItemId == saleItem.Id);

        Assert.Equal(selected.Id, saleItemUnit.InventoryUnitId);
        Assert.Equal(4000m, saleItemUnit.UnitCostSnapshot);

        var consumption = await db.InventoryLotConsumptions
            .AsNoTracking()
            .SingleAsync(x => x.MovementId == saleItem.InventoryMovementId);
        Assert.Equal(selectedOriginalLot, consumption.LotId);
        Assert.Equal(1m, consumption.Quantity);
        Assert.Equal(4000m, consumption.TotalCostSnapshot);

        var returnHandler = ActivatorUtilities.CreateInstance<CreateSaleReturnHandler>(services);
        var saleReturn = await returnHandler.HandleAsync(
            new CreateSaleReturnCommand(
                sale.Value.SaleId,
                "CUSTOMER_CHANGED_MIND",
                null,
                RefundMethod.Bank,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new SaleReturnLineInput(
                        saleItem.Id,
                        1m,
                        SaleReturnDisposition.RestockSellable,
                        [selected.Id])
                ]),
            CancellationToken.None);

        Assert.True(saleReturn.IsSuccess, saleReturn.Error?.Message);

        db.ChangeTracker.Clear();
        var selectedAfterReturn = await db.InventoryUnits
            .AsNoTracking()
            .SingleAsync(x => x.Id == selected.Id);
        Assert.Equal(InventoryUnitStatus.InStock, selectedAfterReturn.Status);
        Assert.NotNull(selectedAfterReturn.InventoryLotId);
        Assert.NotEqual(selectedOriginalLot, selectedAfterReturn.InventoryLotId);

        var costState = await db.ProductCostStates
            .AsNoTracking()
            .SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(2m, costState.CostedQty);
        Assert.Equal(8000m, costState.TotalInventoryCost);

        var voidHandler = ActivatorUtilities.CreateInstance<VoidPurchaseHandler>(services);
        var voidResult = await voidHandler.HandleAsync(
            new VoidPurchaseCommand(
                purchase.Value!.PurchaseId,
                Guid.CreateVersion7(),
                fixture.ActorId,
                "cannot erase consumed history"),
            CancellationToken.None);

        Assert.False(voidResult.IsSuccess);
        Assert.Equal("purchasing.void_origin_consumed", voidResult.Error?.Code);
    }

    private static ServiceProvider BuildProvider()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        return services.BuildServiceProvider();
    }

    private static async Task EnsureReceiptConfigurationAsync(
        EdgeRetailsDbContext db)
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

    private static async Task<Fixture> SeedSerializedProductAsync(
        EdgeRetailsDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new Unit
        {
            Name = "Serialized Piece-" + suffix,
            Symbol = "sp-" + suffix[..8],
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Serialized Fan-" + suffix,
            Sku = "SF-" + suffix[..12],
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 5000m,
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

        return new Fixture(
            product.Id,
            productUnit.Id,
            supplier.Id,
            actorId);
    }

    private sealed record Fixture(
        Guid ProductId,
        Guid ProductUnitId,
        Guid SupplierId,
        Guid ActorId);
}

