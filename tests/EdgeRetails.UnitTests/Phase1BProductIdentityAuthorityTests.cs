using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase1BProductIdentityAuthorityTests
{
    [Fact]
    public async Task Semantic_Product_Code_Normalization()
    {
        var doubles = new Phase2TestDoubles();
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");

        var handler = new CreateProductHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var result = await handler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "   pkf-dlx56   "
            }),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var product = await doubles.Catalog.GetProductAsync(result.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);
        Assert.Equal("PKF-DLX56", product.Sku);

        // Invalid characters rejected
        var invalidResult = await handler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "PKF@DLX#56"
            }),
            CancellationToken.None);

        Assert.False(invalidResult.IsSuccess);
        Assert.Equal("catalog.sku_invalid", invalidResult.Error?.Code);
    }

    [Fact]
    public async Task Semantic_Product_Code_Duplicate_Protection()
    {
        var doubles = new Phase2TestDoubles();
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");

        var handler = new CreateProductHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var result1 = await handler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "PKF-DLX56"
            }),
            CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);

        // Case-insensitive duplicate attempt
        var result2 = await handler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "pkf-dlx56"
            }),
            CancellationToken.None);

        Assert.False(result2.IsSuccess);
        Assert.Equal("catalog.sku_duplicate", result2.Error?.Code);
    }

    [Fact]
    public async Task Semantic_Product_Code_Concurrency()
    {
        var doubles = new Phase2TestDoubles();
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");

        var handler = new CreateProductHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var tasks = Enumerable.Range(1, 20).Select(async _ =>
        {
            return await handler.HandleAsync(
                new CreateProductCommand(Guid.NewGuid(), ValidProductInput(unit.Id, category.Id) with
                {
                    Sku = "PKF-DLX56"
                }),
                CancellationToken.None);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var successes = results.Count(r => r.IsSuccess);
        var duplicates = results.Count(r => !r.IsSuccess && r.Error?.Code == "catalog.sku_duplicate");

        Assert.Equal(1, successes);
        Assert.Equal(19, duplicates);
    }

    [Fact]
    public async Task Product_Code_Immutability_After_History()
    {
        var doubles = new Phase2TestDoubles();
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");

        var createHandler = new CreateProductHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var created = await createHandler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "PKF-DLX56"
            }),
            CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var product = await doubles.Catalog.GetProductAsync(created.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);

        // Before history: operator can correct SKU
        var updateBeforeHistory = new UpdateProductHandler(
            doubles.Catalog, doubles.Authorization, new Phase3CatalogSafetyReadService(false), doubles.Transactions, doubles.UnitOfWork, new FakeBusinessAuditWriter());

        var corrected = await updateBeforeHistory.HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "PKF-DLX60"
            }),
            CancellationToken.None);
        Assert.True(corrected.IsSuccess, corrected.Error?.Message);

        product = await doubles.Catalog.GetProductAsync(product.Id, CancellationToken.None);
        Assert.Equal("PKF-DLX60", product!.Sku);

        // After history exists: SKU is strictly immutable
        var updateAfterHistory = new UpdateProductHandler(
            doubles.Catalog, doubles.Authorization, new Phase3CatalogSafetyReadService(true), doubles.Transactions, doubles.UnitOfWork, new FakeBusinessAuditWriter());

        var rejected = await updateAfterHistory.HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, ValidProductInput(unit.Id, category.Id) with
            {
                Sku = "PKF-MODIFIED"
            }),
            CancellationToken.None);

        Assert.False(rejected.IsSuccess);
        Assert.Equal("catalog.sku_immutable", rejected.Error?.Code);
        Assert.Equal("PKF-DLX60", product.Sku);
    }

    [Fact]
    public async Task DealerCode_AB1_AB2_Allocation()
    {
        var doubles = new Phase2TestDoubles();
        var handler = new SaveSupplierHandler(
            doubles.Parties,
            doubles.Traceability,
            doubles.ResourceLock,
            doubles.Audit,
            doubles.Clock,
            doubles.Transactions,
            doubles.Authorization,
            doubles.UnitOfWork);

        var actorId = Guid.NewGuid();

        // 1. Abdullah Electronics -> AB1
        var s1Result = await handler.HandleAsync(
            new SaveSupplierCommand(null, "Abdullah Electronics", "03001234567", "Lahore", "Hall Road", true, actorId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(s1Result.IsSuccess, s1Result.Error?.Message);

        var s1 = await doubles.Parties.GetSupplierAsync(s1Result.Value, CancellationToken.None);
        Assert.NotNull(s1);
        Assert.Equal("AB1", s1.DealerCode);

        // 2. Abdullah Traders -> AB2
        var s2Result = await handler.HandleAsync(
            new SaveSupplierCommand(null, "Abdullah Traders", "03007654321", "Lahore", "Mall Road", true, actorId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(s2Result.IsSuccess, s2Result.Error?.Message);

        var s2 = await doubles.Parties.GetSupplierAsync(s2Result.Value, CancellationToken.None);
        Assert.NotNull(s2);
        Assert.Equal("AB2", s2.DealerCode);

        // 3. Al-Rehman Sons -> AL1
        var s3Result = await handler.HandleAsync(
            new SaveSupplierCommand(null, "Al-Rehman Sons", "03009999999", "Karachi", "Electronics Market", true, actorId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(s3Result.IsSuccess, s3Result.Error?.Message);

        var s3 = await doubles.Parties.GetSupplierAsync(s3Result.Value, CancellationToken.None);
        Assert.NotNull(s3);
        Assert.Equal("AL1", s3.DealerCode);
    }

    [Fact]
    public async Task Dealer_Rename_Stability()
    {
        var doubles = new Phase2TestDoubles();
        var handler = new SaveSupplierHandler(
            doubles.Parties,
            doubles.Traceability,
            doubles.ResourceLock,
            doubles.Audit,
            doubles.Clock,
            doubles.Transactions,
            doubles.Authorization,
            doubles.UnitOfWork);

        var actorId = Guid.NewGuid();

        var createResult = await handler.HandleAsync(
            new SaveSupplierCommand(null, "Abdullah Electronics", "03001234567", "Lahore", "Hall Road", true, actorId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(createResult.IsSuccess, createResult.Error?.Message);

        var supplier = await doubles.Parties.GetSupplierAsync(createResult.Value, CancellationToken.None);
        Assert.Equal("AB1", supplier!.DealerCode);

        // Rename to Zafar Traders
        var renameResult = await handler.HandleAsync(
            new SaveSupplierCommand(supplier.Id, "Zafar Traders", "03001234567", "Lahore", "Hall Road", true, actorId, Guid.NewGuid()),
            CancellationToken.None);
        Assert.True(renameResult.IsSuccess, renameResult.Error?.Message);

        var updated = await doubles.Parties.GetSupplierAsync(supplier.Id, CancellationToken.None);
        Assert.Equal("Zafar Traders", updated!.Name);
        Assert.Equal("AB1", updated.DealerCode); // DealerCode remains permanently AB1
    }

    [Fact]
    public async Task SupplierProduct_Per_Pair_Sequence()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");
        var (product, pUnit) = CreateProduct(doubles, "Pak Fan Deluxe", "PKF-DLX56", unit.Id, category.Id, TrackingMode.IndividualPiece);

        var handler = CreatePurchaseHandler(doubles);

        // Purchase 1: 3 pieces
        var p1Result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 3m, 5000m),
            CancellationToken.None);
        Assert.True(p1Result.IsSuccess, p1Result.Error?.Message);

        var sp = await doubles.Traceability.GetSupplierProductAsync(supplier.Id, product.Id, CancellationToken.None);
        Assert.NotNull(sp);
        Assert.Equal(4, sp.NextItemSequence); // Sequences 1, 2, 3 issued; next is 4

        // Purchase 2: 2 pieces
        var p2Result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 2m, 5000m),
            CancellationToken.None);
        Assert.True(p2Result.IsSuccess, p2Result.Error?.Message);

        sp = await doubles.Traceability.GetSupplierProductAsync(supplier.Id, product.Id, CancellationToken.None);
        Assert.Equal(6, sp!.NextItemSequence); // Sequences 4, 5 issued; next is 6
    }

    [Fact]
    public async Task Individual_Piece_With_No_Serial_No_Imei()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");
        var (product, pUnit) = CreateProduct(doubles, "Pak Fan Deluxe 56", "PKF-DLX56", unit.Id, category.Id,
            TrackingMode.IndividualPiece, serialTracking: false, imeiTracking: false);

        var handler = CreatePurchaseHandler(doubles);

        var result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 2m, 6000m),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        var units = doubles.Inventory.Units.Where(u => u.ProductId == product.Id).OrderBy(u => u.ItemSequence).ToList();
        Assert.Equal(2, units.Count);

        Assert.Equal("AB1-PKF-DLX56-000001", units[0].TrackingCode);
        Assert.Equal(1, units[0].ItemSequence);
        Assert.Null(units[0].SerialNumber);
        Assert.Null(units[0].Imei1);
        Assert.Null(units[0].Imei2);

        Assert.Equal("AB1-PKF-DLX56-000002", units[1].TrackingCode);
        Assert.Equal(2, units[1].ItemSequence);
        Assert.Null(units[1].SerialNumber);
        Assert.Null(units[1].Imei1);
        Assert.Null(units[1].Imei2);
    }

    [Fact]
    public async Task Individual_Piece_With_Serial()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Appliances");
        var (product, pUnit) = CreateProduct(doubles, "Washing Machine", "WM-AUT10", unit.Id, category.Id,
            TrackingMode.IndividualPiece, serialTracking: true, imeiTracking: false);

        var handler = CreatePurchaseHandler(doubles);

        var identities = new[]
        {
            new SerializedIdentityInput("WM-SER-001", null, null),
            new SerializedIdentityInput("WM-SER-002", null, null)
        };

        var result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 2m, 45000m, identities),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        var units = doubles.Inventory.Units.Where(u => u.ProductId == product.Id).OrderBy(u => u.ItemSequence).ToList();
        Assert.Equal(2, units.Count);

        Assert.Equal("AB1-WM-AUT10-000001", units[0].TrackingCode);
        Assert.Equal("WM-SER-001", units[0].SerialNumber);
        Assert.Null(units[0].Imei1);

        Assert.Equal("AB1-WM-AUT10-000002", units[1].TrackingCode);
        Assert.Equal("WM-SER-002", units[1].SerialNumber);
        Assert.Null(units[1].Imei1);
    }

    [Fact]
    public async Task Individual_Piece_With_Imei()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Smartphones");
        var (product, pUnit) = CreateProduct(doubles, "Smartphone X", "SMP-X100", unit.Id, category.Id,
            TrackingMode.IndividualPiece, serialTracking: false, imeiTracking: true);

        var handler = CreatePurchaseHandler(doubles);

        var identities = new[]
        {
            new SerializedIdentityInput(null, "356789012345678", "356789012345679")
        };

        var result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 1m, 80000m, identities),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        var unitRecord = Assert.Single(doubles.Inventory.Units, u => u.ProductId == product.Id);
        Assert.Equal("AB1-SMP-X100-000001", unitRecord.TrackingCode);
        Assert.Null(unitRecord.SerialNumber);
        Assert.Equal("356789012345678", unitRecord.Imei1);
        Assert.Equal("356789012345679", unitRecord.Imei2);
    }

    [Fact]
    public async Task Length_Product_Has_No_Per_Meter_InventoryUnits()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Meter", "m");
        var category = CreateCategory(doubles, "Cables");
        var (product, pUnit) = CreateProduct(doubles, "Copper Cable 2.5mm", "CBL-25MM", unit.Id, category.Id,
            TrackingMode.Length, serialTracking: false, imeiTracking: false);

        var handler = CreatePurchaseHandler(doubles);

        var result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 100m, 150m),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Zero inventory units created for continuous length
        Assert.DoesNotContain(doubles.Inventory.Units, u => u.ProductId == product.Id);

        // Movement and stock balance properly updated to 100m
        var balance = doubles.Inventory.Balances[product.Id];
        Assert.NotNull(balance);
        Assert.Equal(100m, balance.SellableQty);
    }

    [Fact]
    public async Task Container_Pack_Policy()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var baseUnit = CreateUnit(doubles, "Piece", "Pcs");
        var boxUnit = CreateUnit(doubles, "Box", "Box");
        var category = CreateCategory(doubles, "Hardware");
        var (product, _) = CreateProduct(doubles, "Box of Screws 500pk", "SCR-BX500", baseUnit.Id, category.Id,
            TrackingMode.Container, serialTracking: false, imeiTracking: false);

        // Box unit has factor 500 (1 box = 500 pieces)
        var productBoxUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = boxUnit.Id,
            FactorToBaseUnit = 500m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = false,
            CanPurchase = true,
            CanSell = true,
            IsActive = true
        };
        doubles.Catalog.AddProductUnit(productBoxUnit);

        var handler = CreatePurchaseHandler(doubles);

        // Purchasing 2 boxes (1000 base pieces)
        var result = await handler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, productBoxUnit.Id, 2m, 2500m),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Exactly 2 InventoryUnits created (one per box), NOT 1000!
        var units = doubles.Inventory.Units.Where(u => u.ProductId == product.Id).OrderBy(u => u.ItemSequence).ToList();
        Assert.Equal(2, units.Count);
        Assert.Equal("AB1-SCR-BX500-000001", units[0].TrackingCode);
        Assert.Equal("AB1-SCR-BX500-000002", units[1].TrackingCode);

        // Stock balance in base unit is 1000 pieces
        var balance = doubles.Inventory.Balances[product.Id];
        Assert.NotNull(balance);
        Assert.Equal(1000m, balance.SellableQty);
    }

    [Fact]
    public void Physical_Sku_Format_AB1_PKF_DLX56_000001()
    {
        var code1 = TraceabilityCodeRules.BuildTrackingCode("AB1", "PKF-DLX56", 1);
        Assert.Equal("AB1-PKF-DLX56-000001", code1);

        var code27 = TraceabilityCodeRules.BuildTrackingCode("AB1", "PKF-DLX56", 27);
        Assert.Equal("AB1-PKF-DLX56-000027", code27);

        // Sequences beyond 6 digits are not truncated
        var codeLarge = TraceabilityCodeRules.BuildTrackingCode("AB1", "PKF-DLX56", 1000000);
        Assert.Equal("AB1-PKF-DLX56-1000000", codeLarge);
    }

    [Fact]
    public async Task Two_Products_From_Same_Dealer_Have_Independent_Sequences()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");

        var (fan, fanUnit) = CreateProduct(doubles, "Pak Fan Deluxe", "PKF-DLX56", unit.Id, category.Id, TrackingMode.IndividualPiece);
        var (royal, royalUnit) = CreateProduct(doubles, "Royal Fan", "RYF-234", unit.Id, category.Id, TrackingMode.IndividualPiece);

        var handler = CreatePurchaseHandler(doubles);

        // Buy 2 Pak Fans
        var r1 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, fan.Id, fanUnit.Id, 2m, 5000m), CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.Error?.Message);
        // Buy 2 Royal Fans
        var r2 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, royal.Id, royalUnit.Id, 2m, 4500m), CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.Error?.Message);

        var fanUnits = doubles.Inventory.Units.Where(u => u.ProductId == fan.Id).OrderBy(u => u.ItemSequence).ToList();
        var royalUnits = doubles.Inventory.Units.Where(u => u.ProductId == royal.Id).OrderBy(u => u.ItemSequence).ToList();

        Assert.Equal("AB1-PKF-DLX56-000001", fanUnits[0].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000002", fanUnits[1].TrackingCode);

        // Royal Fan sequence starts independently at 000001!
        Assert.Equal("AB1-RYF-234-000001", royalUnits[0].TrackingCode);
        Assert.Equal("AB1-RYF-234-000002", royalUnits[1].TrackingCode);
    }

    [Fact]
    public async Task Same_Product_From_Two_Dealers_Have_Independent_Sequences()
    {
        var doubles = new Phase2TestDoubles();
        var s1 = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var s2 = CreateSupplier(doubles, "Abdullah Traders", "AB2");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");
        var (product, pUnit) = CreateProduct(doubles, "Pak Fan Deluxe", "PKF-DLX56", unit.Id, category.Id, TrackingMode.IndividualPiece);

        var handler = CreatePurchaseHandler(doubles);

        // Buy 2 from Dealer AB1
        var r1 = await handler.HandleAsync(BuildPurchaseCommand(s1.Id, product.Id, pUnit.Id, 2m, 5000m), CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.Error?.Message);
        // Buy 2 from Dealer AB2
        var r2 = await handler.HandleAsync(BuildPurchaseCommand(s2.Id, product.Id, pUnit.Id, 2m, 5200m), CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.Error?.Message);

        var ab1Units = doubles.Inventory.Units.Where(u => u.SupplierCodeSnapshot == "AB1").OrderBy(u => u.ItemSequence).ToList();
        var ab2Units = doubles.Inventory.Units.Where(u => u.SupplierCodeSnapshot == "AB2").OrderBy(u => u.ItemSequence).ToList();

        Assert.Equal("AB1-PKF-DLX56-000001", ab1Units[0].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000002", ab1Units[1].TrackingCode);

        // Dealer AB2 sequence starts independently at 000001!
        Assert.Equal("AB2-PKF-DLX56-000001", ab2Units[0].TrackingCode);
        Assert.Equal("AB2-PKF-DLX56-000002", ab2Units[1].TrackingCode);
    }

    [Fact]
    public async Task Sequence_Continues_Across_Purchases()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");
        var (product, pUnit) = CreateProduct(doubles, "Pak Fan Deluxe", "PKF-DLX56", unit.Id, category.Id, TrackingMode.IndividualPiece);

        var handler = CreatePurchaseHandler(doubles);

        // Purchase 1
        var r1 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 2m, 5000m), CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.Error?.Message);
        // Purchase 2
        var r2 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 2m, 5100m), CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.Error?.Message);

        var units = doubles.Inventory.Units.Where(u => u.ProductId == product.Id).OrderBy(u => u.ItemSequence).ToList();
        Assert.Equal(4, units.Count);

        Assert.Equal("AB1-PKF-DLX56-000001", units[0].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000002", units[1].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000003", units[2].TrackingCode);
        Assert.Equal("AB1-PKF-DLX56-000004", units[3].TrackingCode);
    }

    [Fact]
    public async Task Void_Does_Not_Reuse_Issued_Identity()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");
        var (product, pUnit) = CreateProduct(doubles, "Pak Fan Deluxe", "PKF-DLX56", unit.Id, category.Id, TrackingMode.IndividualPiece);

        var handler = CreatePurchaseHandler(doubles);

        // Purchase 1 issues 000001 and 000002
        var p1 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 2m, 5000m), CancellationToken.None);
        Assert.True(p1.IsSuccess, p1.Error?.Message);

        // Voiding purchase does NOT decrement NextItemSequence
        var sp = await doubles.Traceability.GetSupplierProductAsync(supplier.Id, product.Id, CancellationToken.None);
        Assert.Equal(3, sp!.NextItemSequence);

        // Subsequent purchase issues 000003, never reusing 000001 or 000002
        var p2 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 1m, 5000m), CancellationToken.None);
        Assert.True(p2.IsSuccess, p2.Error?.Message);

        var latestUnit = doubles.Inventory.Units.OrderByDescending(u => u.ItemSequence).First();
        Assert.Equal("AB1-PKF-DLX56-000003", latestUnit.TrackingCode);
        Assert.Equal(3, latestUnit.ItemSequence);
    }

    [Fact]
    public async Task Restore_High_Water_Prevents_Reuse()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var unit = CreateUnit(doubles, "Piece", "Pcs");
        var category = CreateCategory(doubles, "Fans");
        var (product, pUnit) = CreateProduct(doubles, "Pak Fan Deluxe", "PKF-DLX56", unit.Id, category.Id, TrackingMode.IndividualPiece);

        var highWater = new MemorySequenceHighWaterService();
        var handler = CreatePurchaseHandler(doubles, highWater);

        // Issue up to sequence 10
        var r1 = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 10m, 5000m), CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.Error?.Message);

        var sp = await doubles.Traceability.GetSupplierProductAsync(supplier.Id, product.Id, CancellationToken.None);
        Assert.NotNull(sp);
        Assert.Equal(11, sp.NextItemSequence);
        Assert.Equal(11, highWater.GetSupplierProductHighWater(supplier.Id, product.Id));

        // Simulate database restore rewinding sequence back to 3!
        sp.NextItemSequence = 3;

        // Next purchase must consult high-water mark, jump to MAX(3, 11) = 11, and issue sequence 11
        var pNext = await handler.HandleAsync(BuildPurchaseCommand(supplier.Id, product.Id, pUnit.Id, 1m, 5000m), CancellationToken.None);
        Assert.True(pNext.IsSuccess, pNext.Error?.Message);

        var latestUnit = doubles.Inventory.Units.OrderByDescending(u => u.ItemSequence).First();
        Assert.Equal(11, latestUnit.ItemSequence);
        Assert.Equal("AB1-PKF-DLX56-000011", latestUnit.TrackingCode);
    }

    [Fact]
    public void TrackingCode_Uniqueness_Validation()
    {
        var code1 = TraceabilityCodeRules.BuildTrackingCode("AB1", "PKF-DLX56", 1);
        var code2 = TraceabilityCodeRules.BuildTrackingCode("AB1", "PKF-DLX56", 1);
        var code3 = TraceabilityCodeRules.BuildTrackingCode("AB1", "PKF-DLX56", 2);

        Assert.Equal(code1, code2);
        Assert.NotEqual(code1, code3);
    }

    [Fact]
    public async Task CompanyCode_Collision_Resolution_PakFan_PakElectronics_PakHome_RoyalFan_SuperAsia()
    {
        var doubles = new Phase2TestDoubles();
        var handler = new SaveCompanyHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var actorId = Guid.NewGuid();

        // 1. Pak Fan -> PK
        var r1 = await handler.HandleAsync(
            new SaveCompanyCommand(actorId, null, "Pak Fan"),
            CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.Error?.Message);
        var c1 = await doubles.Catalog.GetCompanyAsync(r1.Value, CancellationToken.None);
        Assert.NotNull(c1);
        Assert.Equal("PK", c1.Code);

        // 2. Pak Electronics -> PE (since PK is taken)
        var r2 = await handler.HandleAsync(
            new SaveCompanyCommand(actorId, null, "Pak Electronics"),
            CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.Error?.Message);
        var c2 = await doubles.Catalog.GetCompanyAsync(r2.Value, CancellationToken.None);
        Assert.NotNull(c2);
        Assert.Equal("PE", c2.Code);

        // 3. Pak Home -> PH (since PK and PE are taken)
        var r3 = await handler.HandleAsync(
            new SaveCompanyCommand(actorId, null, "Pak Home"),
            CancellationToken.None);
        Assert.True(r3.IsSuccess, r3.Error?.Message);
        var c3 = await doubles.Catalog.GetCompanyAsync(r3.Value, CancellationToken.None);
        Assert.NotNull(c3);
        Assert.Equal("PH", c3.Code);

        // 4. Royal Fan -> RO
        var r4 = await handler.HandleAsync(
            new SaveCompanyCommand(actorId, null, "Royal Fan"),
            CancellationToken.None);
        Assert.True(r4.IsSuccess, r4.Error?.Message);
        var c4 = await doubles.Catalog.GetCompanyAsync(r4.Value, CancellationToken.None);
        Assert.NotNull(c4);
        Assert.Equal("RO", c4.Code);

        // 5. Super Asia -> SU
        var r5 = await handler.HandleAsync(
            new SaveCompanyCommand(actorId, null, "Super Asia"),
            CancellationToken.None);
        Assert.True(r5.IsSuccess, r5.Error?.Message);
        var c5 = await doubles.Catalog.GetCompanyAsync(r5.Value, CancellationToken.None);
        Assert.NotNull(c5);
        Assert.Equal("SU", c5.Code);
    }

    [Fact]
    public async Task CategorySymbol_Duplicate_Rejection_And_Auto_Suggestion()
    {
        var doubles = new Phase2TestDoubles();
        var handler = new SaveCategoryHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var actorId = Guid.NewGuid();

        // 1. Fan -> F
        var r1 = await handler.HandleAsync(
            new SaveCategoryCommand(actorId, null, "Fan"),
            CancellationToken.None);
        Assert.True(r1.IsSuccess, r1.Error?.Message);
        var cat1 = await doubles.Catalog.GetCategoryAsync(r1.Value, CancellationToken.None);
        Assert.NotNull(cat1);
        Assert.Equal("F", cat1.IdentitySymbol);

        // 2. Bulb -> B
        var r2 = await handler.HandleAsync(
            new SaveCategoryCommand(actorId, null, "Bulb"),
            CancellationToken.None);
        Assert.True(r2.IsSuccess, r2.Error?.Message);
        var cat2 = await doubles.Catalog.GetCategoryAsync(r2.Value, CancellationToken.None);
        Assert.NotNull(cat2);
        Assert.Equal("B", cat2.IdentitySymbol);

        // 3. Iron -> I
        var r3 = await handler.HandleAsync(
            new SaveCategoryCommand(actorId, null, "Iron"),
            CancellationToken.None);
        Assert.True(r3.IsSuccess, r3.Error?.Message);
        var cat3 = await doubles.Catalog.GetCategoryAsync(r3.Value, CancellationToken.None);
        Assert.NotNull(cat3);
        Assert.Equal("I", cat3.IdentitySymbol);

        // 4. Duplicate symbol explicit attempt -> rejected
        var dupResult = await handler.HandleAsync(
            new SaveCategoryCommand(actorId, null, "Ceiling Fan", "F"),
            CancellationToken.None);
        Assert.False(dupResult.IsSuccess);
        Assert.Equal("catalog.category_symbol_duplicate", dupResult.Error?.Code);
    }

    [Fact]
    public async Task Canonical_Authority_Identity_Assertion_AB1_PKF_DLX56_000027()
    {
        var doubles = new Phase2TestDoubles();
        var supplier = CreateSupplier(doubles, "Abdullah Electronics", "AB1");
        var company = CreateCompany(doubles, "Pak Fan", "PK");
        var category = CreateCategory(doubles, "Fan", "F");
        var unit = CreateUnit(doubles, "Piece", "Pcs");

        // Product creation deriving PKF-DLX56 from (PK, F, DLX56)
        var createProductHandler = new CreateProductHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var productResult = await createProductHandler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), new ProductCatalogInput(
                Name: "Pak Fan Deluxe 56",
                Sku: null, // Let system derive PKF-DLX56
                Brand: "Pak Fan",
                Model: "Deluxe 56",
                CategoryId: category.Id,
                BaseUnitId: unit.Id,
                TrackingMode: TrackingMode.IndividualPiece,
                SerialTrackingEnabled: false,
                ImeiTrackingEnabled: false,
                ReferencePurchaseCost: 5500m,
                DefaultSalePrice: 7200m,
                MinimumStockLevel: 5m,
                DefaultWarrantyMonths: 24,
                AttributesJson: null,
                AttributesSchemaVersion: 1,
                CompanyId: company.Id,
                ModelCode: "DLX56")),
            CancellationToken.None);

        Assert.True(productResult.IsSuccess, productResult.Error?.Message);
        var product = await doubles.Catalog.GetProductAsync(productResult.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);
        Assert.Equal("PKF-DLX56", product.Sku);
        Assert.Equal("DLX56", product.ModelCode);
        Assert.Equal(company.Id, product.CompanyId);
        Assert.Equal(category.Id, product.CategoryId);

        var baseProductUnit = doubles.Catalog.ProductUnits.Values.First(pu => pu.ProductId == product.Id);

        // Execute purchases totaling 27 physical units
        var purchaseHandler = CreatePurchaseHandler(doubles);

        // Purchase 1: 20 units
        var p1Result = await purchaseHandler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, baseProductUnit.Id, 20m, 5500m),
            CancellationToken.None);
        Assert.True(p1Result.IsSuccess, p1Result.Error?.Message);

        // Purchase 2: 7 units -> 27th unit is the canonical assertion
        var p2Result = await purchaseHandler.HandleAsync(
            BuildPurchaseCommand(supplier.Id, product.Id, baseProductUnit.Id, 7m, 5500m),
            CancellationToken.None);
        Assert.True(p2Result.IsSuccess, p2Result.Error?.Message);

        var units = doubles.Inventory.Units
            .Where(u => u.ProductId == product.Id)
            .OrderBy(u => u.ItemSequence)
            .ToList();

        Assert.Equal(27, units.Count);

        // CANONICAL ASSERTION:
        // Supplier = AB1
        // ProductCode = PKF-DLX56
        // Sequence = 000027
        // TrackingCode = AB1-PKF-DLX56-000027
        var unit27 = units[26];
        Assert.Equal(27, unit27.ItemSequence);
        Assert.Equal("AB1", unit27.SupplierCodeSnapshot);
        Assert.Equal("AB1-PKF-DLX56-000027", unit27.TrackingCode);
    }

    [Fact]
    public async Task ModelCode_And_Company_And_Category_Immutability_Once_History_Exists()
    {
        var doubles = new Phase2TestDoubles();
        var company1 = CreateCompany(doubles, "Pak Fan", "PK");
        var company2 = CreateCompany(doubles, "Royal Fan", "RO");
        var category1 = CreateCategory(doubles, "Fan", "F");
        var category2 = CreateCategory(doubles, "Bulb", "B");
        var unit = CreateUnit(doubles, "Piece", "Pcs");

        var createHandler = new CreateProductHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var created = await createHandler.HandleAsync(
            new CreateProductCommand(Guid.NewGuid(), new ProductCatalogInput(
                Name: "Pak Fan Deluxe",
                Sku: "PKF-DLX56",
                Brand: "Pak Fan",
                Model: "Deluxe 56",
                CategoryId: category1.Id,
                BaseUnitId: unit.Id,
                TrackingMode: TrackingMode.IndividualPiece,
                SerialTrackingEnabled: false,
                ImeiTrackingEnabled: false,
                ReferencePurchaseCost: 5000m,
                DefaultSalePrice: 6500m,
                MinimumStockLevel: 5m,
                DefaultWarrantyMonths: 12,
                AttributesJson: null,
                AttributesSchemaVersion: 1,
                CompanyId: company1.Id,
                ModelCode: "DLX56")),
            CancellationToken.None);

        Assert.True(created.IsSuccess, created.Error?.Message);
        var product = await doubles.Catalog.GetProductAsync(created.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);

        // Before history: update allowed
        var updateBeforeHistory = new UpdateProductHandler(
            doubles.Catalog, doubles.Authorization, new Phase3CatalogSafetyReadService(false), doubles.Transactions, doubles.UnitOfWork, new FakeBusinessAuditWriter());

        var beforeResult = await updateBeforeHistory.HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, new ProductCatalogInput(
                Name: "Pak Fan Deluxe Revised",
                Sku: "PKF-DLX56",
                Brand: "Pak Fan",
                Model: "Deluxe 56",
                CategoryId: category1.Id,
                BaseUnitId: unit.Id,
                TrackingMode: TrackingMode.IndividualPiece,
                SerialTrackingEnabled: false,
                ImeiTrackingEnabled: false,
                ReferencePurchaseCost: 5000m,
                DefaultSalePrice: 6500m,
                MinimumStockLevel: 5m,
                DefaultWarrantyMonths: 12,
                AttributesJson: null,
                AttributesSchemaVersion: 1,
                CompanyId: company1.Id,
                ModelCode: "DLX56")),
            CancellationToken.None);
        Assert.True(beforeResult.IsSuccess, beforeResult.Error?.Message);

        // After history exists: ModelCode, CompanyId, CategoryId are strictly immutable
        var updateAfterHistory = new UpdateProductHandler(
            doubles.Catalog, doubles.Authorization, new Phase3CatalogSafetyReadService(true), doubles.Transactions, doubles.UnitOfWork, new FakeBusinessAuditWriter());

        // 1. Attempt to change ModelCode -> rejected
        var mcResult = await updateAfterHistory.HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, new ProductCatalogInput(
                Name: "Pak Fan Deluxe Revised",
                Sku: "PKF-DLX56",
                Brand: "Pak Fan",
                Model: "Deluxe 56",
                CategoryId: category1.Id,
                BaseUnitId: unit.Id,
                TrackingMode: TrackingMode.IndividualPiece,
                SerialTrackingEnabled: false,
                ImeiTrackingEnabled: false,
                ReferencePurchaseCost: 5000m,
                DefaultSalePrice: 6500m,
                MinimumStockLevel: 5m,
                DefaultWarrantyMonths: 12,
                AttributesJson: null,
                AttributesSchemaVersion: 1,
                CompanyId: company1.Id,
                ModelCode: "DLX60")),
            CancellationToken.None);
        Assert.False(mcResult.IsSuccess);
        Assert.Equal("catalog.model_code_immutable", mcResult.Error?.Code);

        // 2. Attempt to change CompanyId -> rejected
        var compResult = await updateAfterHistory.HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, new ProductCatalogInput(
                Name: "Pak Fan Deluxe Revised",
                Sku: "PKF-DLX56",
                Brand: "Pak Fan",
                Model: "Deluxe 56",
                CategoryId: category1.Id,
                BaseUnitId: unit.Id,
                TrackingMode: TrackingMode.IndividualPiece,
                SerialTrackingEnabled: false,
                ImeiTrackingEnabled: false,
                ReferencePurchaseCost: 5000m,
                DefaultSalePrice: 6500m,
                MinimumStockLevel: 5m,
                DefaultWarrantyMonths: 12,
                AttributesJson: null,
                AttributesSchemaVersion: 1,
                CompanyId: company2.Id,
                ModelCode: "DLX56")),
            CancellationToken.None);
        Assert.False(compResult.IsSuccess);
        Assert.Equal("catalog.company_immutable", compResult.Error?.Code);

        // 3. Attempt to change CategoryId -> rejected
        var catResult = await updateAfterHistory.HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), product.Id, product.Version, new ProductCatalogInput(
                Name: "Pak Fan Deluxe Revised",
                Sku: "PKF-DLX56",
                Brand: "Pak Fan",
                Model: "Deluxe 56",
                CategoryId: category2.Id,
                BaseUnitId: unit.Id,
                TrackingMode: TrackingMode.IndividualPiece,
                SerialTrackingEnabled: false,
                ImeiTrackingEnabled: false,
                ReferencePurchaseCost: 5000m,
                DefaultSalePrice: 6500m,
                MinimumStockLevel: 5m,
                DefaultWarrantyMonths: 12,
                AttributesJson: null,
                AttributesSchemaVersion: 1,
                CompanyId: company1.Id,
                ModelCode: "DLX56")),
            CancellationToken.None);
        Assert.False(catResult.IsSuccess);
        Assert.Equal("catalog.category_immutable", catResult.Error?.Code);
    }

    [Fact]
    public async Task Company_And_Category_Rename_Stability()
    {
        var doubles = new Phase2TestDoubles();
        var companyHandler = new SaveCompanyHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);
        var categoryHandler = new SaveCategoryHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var actorId = Guid.NewGuid();

        // Create Company and Category
        var compRes = await companyHandler.HandleAsync(new SaveCompanyCommand(actorId, null, "Pak Fan"), CancellationToken.None);
        Assert.True(compRes.IsSuccess);
        var catRes = await categoryHandler.HandleAsync(new SaveCategoryCommand(actorId, null, "Fan"), CancellationToken.None);
        Assert.True(catRes.IsSuccess);

        var company = await doubles.Catalog.GetCompanyAsync(compRes.Value, CancellationToken.None);
        var category = await doubles.Catalog.GetCategoryAsync(catRes.Value, CancellationToken.None);
        Assert.Equal("PK", company!.Code);
        Assert.Equal("F", category!.IdentitySymbol);

        // Rename Company
        var renameCompRes = await companyHandler.HandleAsync(
            new SaveCompanyCommand(actorId, company.Id, "Pak Fan Industries (Pvt) Ltd"),
            CancellationToken.None);
        Assert.True(renameCompRes.IsSuccess);

        var updatedCompany = await doubles.Catalog.GetCompanyAsync(company.Id, CancellationToken.None);
        Assert.Equal("Pak Fan Industries (Pvt) Ltd", updatedCompany!.Name);
        Assert.Equal("PK", updatedCompany.Code); // Code remains PK permanently

        // Rename Category
        var renameCatRes = await categoryHandler.HandleAsync(
            new SaveCategoryCommand(actorId, category.Id, "Electric Ceiling Fans"),
            CancellationToken.None);
        Assert.True(renameCatRes.IsSuccess);

        var updatedCategory = await doubles.Catalog.GetCategoryAsync(category.Id, CancellationToken.None);
        Assert.Equal("Electric Ceiling Fans", updatedCategory!.Name);
        Assert.Equal("F", updatedCategory.IdentitySymbol); // Symbol remains F permanently
    }

    [Fact]
    public async Task Concurrency_Company_And_Category_Allocation()
    {
        var doubles = new Phase2TestDoubles();
        var companyHandler = new SaveCompanyHandler(
            doubles.Catalog, doubles.Authorization, doubles.Transactions, doubles.UnitOfWork);

        var actorId = Guid.NewGuid();

        // 10 concurrent requests creating "Pak Fan" with explicit code "PK"
        var tasks = Enumerable.Range(1, 10).Select(async _ =>
        {
            return await companyHandler.HandleAsync(
                new SaveCompanyCommand(actorId, null, "Pak Fan", "PK"),
                CancellationToken.None);
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        var successes = results.Count(r => r.IsSuccess);
        var duplicates = results.Count(r => !r.IsSuccess && (r.Error?.Code == "catalog.company_code_duplicate" || r.Error?.Code == "catalog.company_duplicate"));

        Assert.Equal(1, successes);
        Assert.Equal(9, duplicates);
    }

    #region Helpers

    private static Unit CreateUnit(Phase2TestDoubles d, string name, string symbol)
    {
        var u = new Unit { Name = name, Symbol = symbol, IsActive = true };
        d.Catalog.AddUnit(u);
        return u;
    }

    private static Company CreateCompany(Phase2TestDoubles d, string name, string? code = null)
    {
        var comp = new Company
        {
            Name = name,
            Code = code ?? TraceabilityCodeRules.SuggestCompanyCode(name, c => d.Catalog.Companies.Values.Any(x => x.Code == c)),
            IsActive = true
        };
        d.Catalog.AddCompany(comp);
        return comp;
    }

    private static Category CreateCategory(Phase2TestDoubles d, string name, string? symbol = null)
    {
        var c = new Category
        {
            Name = name,
            IdentitySymbol = symbol ?? TraceabilityCodeRules.SuggestCategorySymbol(name, s => d.Catalog.Categories.Values.Any(x => x.IdentitySymbol == s)),
            IsActive = true
        };
        d.Catalog.AddCategory(c);
        return c;
    }

    private static Supplier CreateSupplier(Phase2TestDoubles d, string name, string dealerCode)
    {
        var s = new Supplier
        {
            Name = name,
            DealerCode = dealerCode,
            IsActive = true
        };
        d.Parties.AddSupplier(s);
        return s;
    }

    private static (Product Product, ProductUnit Unit) CreateProduct(
        Phase2TestDoubles d,
        string name,
        string sku,
        Guid baseUnitId,
        Guid categoryId,
        TrackingMode trackingMode,
        bool serialTracking = false,
        bool imeiTracking = false,
        Guid? companyId = null,
        string? modelCode = null)
    {
        var p = new Product
        {
            Name = name,
            Sku = sku,
            BaseUnitId = baseUnitId,
            CategoryId = categoryId,
            CompanyId = companyId,
            ModelCode = modelCode,
            TrackingMode = trackingMode,
            SerialTrackingEnabled = serialTracking,
            ImeiTrackingEnabled = imeiTracking,
            DefaultSalePrice = 1000m,
            IsActive = true,
            Version = 1
        };
        d.Catalog.AddProduct(p);

        var baseUnit = new ProductUnit
        {
            ProductId = p.Id,
            UnitId = baseUnitId,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true,
            IsActive = true
        };
        d.Catalog.AddProductUnit(baseUnit);
        return (p, baseUnit);
    }

    private static ProductCatalogInput ValidProductInput(
        Guid unitId,
        Guid categoryId,
        Guid? companyId = null,
        string? modelCode = null) =>
        new(
            Name: "Deluxe Fan",
            Sku: null,
            Brand: "Pak Fan",
            Model: "56",
            CategoryId: categoryId,
            BaseUnitId: unitId,
            TrackingMode: TrackingMode.IndividualPiece,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 5000m,
            DefaultSalePrice: 6500m,
            MinimumStockLevel: 5m,
            DefaultWarrantyMonths: 12,
            AttributesJson: null,
            AttributesSchemaVersion: 1,
            CompanyId: companyId,
            ModelCode: modelCode);

    private static CreatePurchaseHandler CreatePurchaseHandler(Phase2TestDoubles d, ISequenceHighWaterService? highWater = null) =>
        new(
            d.Purchasing,
            d.Parties,
            d.Catalog,
            d.Inventory,
            d.CostAllocator,
            d.Cash,
            d.Traceability,
            d.SupplierAccounts,
            d.OperationLock,
            d.ResourceLock,
            d.Audit,
            d.Numbers,
            d.Clock,
            d.Transactions,
            d.Authorization,
            d.UnitOfWork,
            highWater,
            d.OutcomeLedger,
            new FakePhysicalUnitCreationAuthority(d.Catalog, d.Parties, d.Traceability, d.Inventory, d.Clock, highWater));

    private static CreatePurchaseCommand BuildPurchaseCommand(
        Guid supplierId,
        Guid productId,
        Guid productUnitId,
        decimal quantity,
        decimal unitCost,
        IReadOnlyList<SerializedIdentityInput>? identities = null)
    {
        return new CreatePurchaseCommand(
            SupplierId: supplierId,
            SupplierInvoiceNumber: $"INV-{Guid.NewGuid():N}"[..12],
            PurchaseDate: new DateOnly(2026, 9, 25),
            Note: "Test Purchase",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: Guid.NewGuid(),
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new CreatePurchaseLineInput(
                    ProductId: productId,
                    ProductUnitId: productUnitId,
                    EnteredQuantity: quantity,
                    EnteredUnitCost: unitCost,
                    BaseUnitSalePrice: unitCost * 1.3m,
                    SerializedUnits: identities ?? Array.Empty<SerializedIdentityInput>())
            });
    }

    private sealed class MemorySequenceHighWaterService : ISequenceHighWaterService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _highWater = new();

        public long GetSupplierProductHighWater(Guid supplierId, Guid productId)
        {
            var key = $"sp:{supplierId:D}:{productId:D}";
            return _highWater.TryGetValue(key, out var val) ? val : 0;
        }

        public void RecordSupplierProductHighWater(Guid supplierId, Guid productId, long sequenceValue)
        {
            var key = $"sp:{supplierId:D}:{productId:D}";
            _highWater.AddOrUpdate(key, sequenceValue, (_, existing) => Math.Max(existing, sequenceValue));
        }

        public long GetDealerPrefixHighWater(string prefix)
        {
            var key = $"dealer:{prefix.ToUpperInvariant()}";
            return _highWater.TryGetValue(key, out var val) ? val : 0;
        }

        public void RecordDealerPrefixHighWater(string prefix, long sequenceValue)
        {
            var key = $"dealer:{prefix.ToUpperInvariant()}";
            _highWater.AddOrUpdate(key, sequenceValue, (_, existing) => Math.Max(existing, sequenceValue));
        }
    }

    #endregion
}
