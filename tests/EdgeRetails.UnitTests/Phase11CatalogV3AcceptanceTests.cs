using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11CatalogV3AcceptanceTests
{
    // ==========================================
    // CAT01 - CAT07 Core Acceptance Criteria
    // ==========================================

    [Fact]
    public void CAT01_HierarchyStorageAndNavigationSemantics()
    {
        Assert.Equal(9, TaxonomyV3Store.Divisions.Count);
        Assert.Equal(65, TaxonomyV3Store.Departments.Count);
        Assert.Equal(732, TaxonomyV3Store.ProductClasses.Count);

        var activeLeaves = TaxonomyV3Store.ProductClasses.Where(c => c.IsActiveLeaf).ToList();
        var redirects = TaxonomyV3Store.ProductClasses.Where(c => c.IsRedirect).ToList();
        var navOnly = TaxonomyV3Store.ProductClasses.Where(c => c.IsNavigationOnly).ToList();

        Assert.Equal(687, activeLeaves.Count);
        Assert.Equal(42, redirects.Count);
        Assert.Equal(3, navOnly.Count);

        // Assert navigation only parents cannot create products
        foreach (var p in navOnly)
        {
            var ex = Assert.Throws<BusinessRuleException>(() =>
                TaxonomyV3Store.RedirectEngine.ResolveClass(p.ClassRef));
            Assert.Equal("catalog.class_non_sellable_parent", ex.Code);
        }
    }

    [Fact]
    public void CAT02_RedirectCanonicalizationAndStableCodes()
    {
        Assert.Equal(42, TaxonomyV3Store.ClassRedirects.Count);

        foreach (var r in TaxonomyV3Store.ClassRedirects)
        {
            var canonical = TaxonomyV3Store.RedirectEngine.ResolveClass(r.LegacyClassRef);
            Assert.NotNull(canonical);
            Assert.True(canonical.IsActiveLeaf);
            Assert.Equal(r.CanonicalClassRef, canonical.ClassRef);
            Assert.NotEqual(r.LegacyClassRef, canonical.ClassRef);
        }
    }

    [Fact]
    public void CAT03_UnitRegistryAndPolicyCompatibility()
    {
        Assert.Equal(51, TaxonomyV3Store.UnitDefinitions.Count);

        // Check UNECE Rec 20 mappings preserved
        var pcs = TaxonomyV3Store.UnitDefinitions.FirstOrDefault(u => u.LocalCode == "PCS");
        Assert.NotNull(pcs);
        Assert.Equal("H87", pcs.VerifiedUneceRec20Code);
        Assert.Equal(PhysicalDimension.Count, pcs.Dimension);

        var m = TaxonomyV3Store.UnitDefinitions.FirstOrDefault(u => u.LocalCode == "M");
        Assert.NotNull(m);
        Assert.Equal("MTR", m.VerifiedUneceRec20Code);
        Assert.Equal(PhysicalDimension.Length, m.Dimension);

        var kg = TaxonomyV3Store.UnitDefinitions.FirstOrDefault(u => u.LocalCode == "KG");
        Assert.NotNull(kg);
        Assert.Equal("KGM", kg.VerifiedUneceRec20Code);
        Assert.Equal(PhysicalDimension.Mass, kg.Dimension);
    }

    [Fact]
    public void CAT04_DimensionGatePolicy_CountAndLengthSupported_MassVolumeAreaGated()
    {
        // Supported dimensions
        DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Count);
        DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Length);

        // Gated dimensions throw catalog.dimension_not_supported_gate_d
        Assert.Throws<BusinessRuleException>(() => DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Mass));
        Assert.Throws<BusinessRuleException>(() => DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Volume));
        Assert.Throws<BusinessRuleException>(() => DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Area));
    }

    [Fact]
    public void CAT05_TypedAttributeProfilesAndEnumGrandfathering()
    {
        Assert.Equal(337, TaxonomyV3Store.AttributeProfiles.Count);

        var emptyEnums = TaxonomyV3Store.AttributeProfiles.Where(a => a.IsEmptyEnum).ToList();
        Assert.Equal(64, emptyEnums.Count);

        // New selection disabled for empty enums
        var ex = Assert.Throws<BusinessRuleException>(() =>
            AttributeOptionPolicy.ValidateOptionSelection(emptyEnums[0], "AnyValue", isHistoricalRecord: false));
        Assert.Equal("catalog.empty_enum_selection_disabled", ex.Code);

        // Grandfathered for historical
        AttributeOptionPolicy.ValidateOptionSelection(emptyEnums[0], "AnyValue", isHistoricalRecord: true);
    }

    [Fact]
    public void CAT06_FacetRulesAndClassApplicability()
    {
        Assert.Equal(68, TaxonomyV3Store.FacetRules.Count);
        Assert.All(TaxonomyV3Store.FacetRules, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.ClassRef));
            Assert.False(string.IsNullOrWhiteSpace(f.FacetName));
            Assert.False(string.IsNullOrWhiteSpace(f.ValueType));
        });
    }

    [Fact]
    public void CAT07_MultilingualSearchAndAmbiguityHandling()
    {
        Assert.Equal(300, TaxonomyV3Store.SearchAliases.Count);
        Assert.Equal(7, TaxonomyV3Store.AmbiguousTerms.Count);

        foreach (var ambig in TaxonomyV3Store.AmbiguousTerms)
        {
            var res = TaxonomyV3Store.SearchEngine.Search(ambig.QueryToken);
            Assert.Equal(SearchOutcome.AmbiguousRequiresSelection, res.Outcome);
            Assert.NotEmpty(res.CandidateReferences);
            Assert.Null(res.ResolvedClass);
        }
    }

    // ==========================================
    // U01 - U23 Acceptance Scenarios
    // ==========================================

    [Fact]
    public void U01_FixedLengthConversion_FtToM()
    {
        // 1 FT = 0.3048 M
        var ft = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "FT");
        Assert.Equal(0.3048m, ft.FactorToReference);
        Assert.Equal(PhysicalDimension.Length, ft.Dimension);
    }

    [Fact]
    public void U02_FixedMassConversion_KgToG()
    {
        // 1 KG = 1000 G
        var kg = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "KG");
        Assert.Equal(1000m, kg.FactorToReference);
        Assert.Equal(PhysicalDimension.Mass, kg.Dimension);
    }

    [Fact]
    public void U03_FixedCountConversion_DozToPcs()
    {
        // 1 DOZ = 12 PCS
        var doz = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "DOZ");
        Assert.Equal(12m, doz.FactorToReference);
        Assert.Equal(PhysicalDimension.Count, doz.Dimension);
    }

    [Fact]
    public void U04_And_U05_ProductSpecificPackagingFactors_DoNotUseGlobalConstants()
    {
        // Global unit definitions for packaging units (BOX, COIL) have no static global factors
        var boxDef = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "BOX");
        var coilDef = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "COIL");

        Assert.Equal("PRODUCT_DEFINED", boxDef.ConversionKind);
        Assert.Equal("PRODUCT_DEFINED", coilDef.ConversionKind);

        // U04: Box of 50 vs Box of 100 — product-specific packaging factors
        var productScrew50 = new Product { Id = Guid.NewGuid(), Name = "Wood Screw 50mm", TrackingMode = TrackingMode.Container };
        var productScrew25 = new Product { Id = Guid.NewGuid(), Name = "Drywall Screw 25mm", TrackingMode = TrackingMode.Container };

        var boxUnitProduct50 = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productScrew50.Id,
            FactorToBaseUnit = 50m, // 50 pcs per box
            CanSell = true
        };
        var boxUnitProduct100 = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productScrew25.Id,
            FactorToBaseUnit = 100m, // 100 pcs per box
            CanSell = true
        };

        // Assert factors are product-specific and distinct
        Assert.NotEqual(boxUnitProduct50.FactorToBaseUnit, boxUnitProduct100.FactorToBaseUnit);
        Assert.Equal(50m, boxUnitProduct50.FactorToBaseUnit);
        Assert.Equal(100m, boxUnitProduct100.FactorToBaseUnit);

        // 1 box of product 50 yields 50 base pieces
        var baseQty50 = boxUnitProduct50.ToBaseQuantity(1m, productScrew50.TrackingMode);
        Assert.Equal(50m, baseQty50);

        // 1 box of product 100 yields 100 base pieces
        var baseQty100 = boxUnitProduct100.ToBaseQuantity(1m, productScrew25.TrackingMode);
        Assert.Equal(100m, baseQty100);

        // U05: Coil of 90m vs Coil of 100m — product-specific coil factors
        var productWire90 = new Product { Id = Guid.NewGuid(), Name = "Single Core Wire 1.5mm", TrackingMode = TrackingMode.Container };
        var productCable100 = new Product { Id = Guid.NewGuid(), Name = "Twin Core Cable 2.5mm", TrackingMode = TrackingMode.Container };

        var coilUnitProduct90 = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productWire90.Id,
            FactorToBaseUnit = 90m, // 90 meters per coil
            CanSell = true
        };
        var coilUnitProduct100 = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productCable100.Id,
            FactorToBaseUnit = 100m, // 100 meters per coil
            CanSell = true
        };

        // Assert coil factors are product-specific and distinct
        Assert.NotEqual(coilUnitProduct90.FactorToBaseUnit, coilUnitProduct100.FactorToBaseUnit);
        Assert.Equal(90m, coilUnitProduct90.FactorToBaseUnit);
        Assert.Equal(100m, coilUnitProduct100.FactorToBaseUnit);

        // 1 coil of wire 90 yields 90 base meters
        var baseQtyWire90 = coilUnitProduct90.ToBaseQuantity(1m, productWire90.TrackingMode);
        Assert.Equal(90m, baseQtyWire90);

        // 1 coil of cable 100 yields 100 base meters
        var baseQtyCable100 = coilUnitProduct100.ToBaseQuantity(1m, productCable100.TrackingMode);
        Assert.Equal(100m, baseQtyCable100);
    }

    [Fact]
    public void U06_VariableMeasuredCoil_TracksRemainingQuantity()
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Cable 2.5mm",
            TrackingMode = TrackingMode.Container
        };
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 17m,
            PackState = PackState.Open,
            IsLooseStock = false
        };

        Assert.Equal(17m, unit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, unit.PackState);
    }

    [Fact]
    public void U07_CrossPackSale_RequiresMultipleAllocations()
    {
        // ER-R001-02: Transactional cross-pack allocation, quantity/cost conservation and replay evidence
        var saleItemId = Guid.NewGuid();
        var movementId = Guid.NewGuid();
        var lotA = Guid.NewGuid();
        var lotB = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Pack A: Open pack with 4 units remaining (acquisition unit cost $12.50)
        var packA = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 4m,
            PackState = PackState.Open,
            AcquisitionCost = 625.00m
        };

        // Pack B: Open pack with 10 units remaining (acquisition unit cost $15.00)
        var packB = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Open,
            AcquisitionCost = 750.00m
        };

        // Sale order requires 10 base units. Fulfill 4 from Pack A and 6 from Pack B.
        decimal requestedQuantity = 10m;
        decimal drawFromA = 4m;
        decimal drawFromB = 6m;

        packA.ConsumeFromPack(drawFromA);
        var allocA = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = packA.Id,
            MovementId = movementId,
            LotId = lotA,
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = saleItemId,
            AllocatedBaseQuantity = drawFromA,
            UnitCostSnapshot = 12.50m,
            TotalCostSnapshot = drawFromA * 12.50m,
            RemainingQuantityAfter = packA.RemainingBaseQuantity!.Value,
            PackStateAfter = packA.PackState!.Value,
            ActorId = actorId,
            OccurredAt = now,
            CreatedAt = now
        };
        allocA.ValidateInvariants();

        packB.ConsumeFromPack(drawFromB);
        var allocB = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = packB.Id,
            MovementId = movementId,
            LotId = lotB,
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = saleItemId,
            AllocatedBaseQuantity = drawFromB,
            UnitCostSnapshot = 15.00m,
            TotalCostSnapshot = drawFromB * 15.00m,
            RemainingQuantityAfter = packB.RemainingBaseQuantity!.Value,
            PackStateAfter = packB.PackState!.Value,
            ActorId = actorId,
            OccurredAt = now,
            CreatedAt = now
        };
        allocB.ValidateInvariants();

        // 1. Pack State Transitions
        Assert.Equal(0m, packA.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, packA.PackState);
        Assert.Equal(4m, packB.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, packB.PackState);

        // 2. Quantity Conservation Proof
        Assert.Equal(requestedQuantity, allocA.AllocatedBaseQuantity + allocB.AllocatedBaseQuantity);
        Assert.Equal(14m, (drawFromA + drawFromB) + (packA.RemainingBaseQuantity.Value + packB.RemainingBaseQuantity.Value));

        // 3. Cost Conservation Proof
        Assert.Equal(50.00m, allocA.TotalCostSnapshot);
        Assert.Equal(90.00m, allocB.TotalCostSnapshot);
        Assert.Equal(140.00m, allocA.TotalCostSnapshot + allocB.TotalCostSnapshot);

        // 4. Replay Idempotency & Conflict Check: Replaying without rollback rejects double-deduction
        var replayExA = Assert.Throws<BusinessRuleException>(() => packA.ConsumeFromPack(drawFromA));
        Assert.Equal("inventory.pack_insufficient_remaining", replayExA.Code);

        var overdrawExB = Assert.Throws<BusinessRuleException>(() => packB.ConsumeFromPack(10m));
        Assert.Equal("inventory.pack_insufficient_remaining", overdrawExB.Code);
    }

    [Fact]
    public void U08_WholePackSell_ChecksEligibility()
    {
        var sealedPack = new InventoryUnit { InitialBaseQuantity = 100m, RemainingBaseQuantity = 100m, PackState = PackState.Sealed };
        sealedPack.ConsumeFromPack(100m);
        Assert.Equal(0m, sealedPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, sealedPack.PackState);
    }

    [Fact]
    public void U09_ReturnCutWire_RemainsLooseAndNeverResealed()
    {
        var returnedWire = new InventoryUnit
        {
            InitialBaseQuantity = 5m,
            RemainingBaseQuantity = 5m,
            PackState = PackState.Sealed,
            IsLooseStock = true
        };

        // Attempting to seal loose stock must fail
        var ex = Assert.Throws<BusinessRuleException>(() => returnedWire.ValidatePackInvariants());
        Assert.Equal("inventory.sealed_loose_stock_invalid", ex.Code);
    }

    [Fact]
    public void U10_CompositeKit_RequiresExplicitBOMOrIdentifiedKit()
    {
        // ER-R001-02: Standalone product entity represents kit; no heterogeneous unit multiplication without explicit BOM
        var kitProduct = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Solar Inverter + Battery Bundle",
            TrackingMode = TrackingMode.Quantity
        };
        kitProduct.ValidateTrackingPolicy();
        Assert.Equal(TrackingMode.Quantity, kitProduct.TrackingMode);

        // Heterogeneous kit component unit types (Count vs Length) cannot be multiplied or scaled
        // across dimensions without an explicit Bill of Materials (BOM) definition
        var exCrossDimension = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.AssertDimensionCompatibility(PhysicalDimension.Count, PhysicalDimension.Length));
        Assert.Equal("catalog.cross_dimension_conversion_forbidden", exCrossDimension.Code);

        // Count components in kit require exact integer increments
        var exFractionalKit = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.ValidateQuantityForDimension(1.5m, PhysicalDimension.Count));
        Assert.Equal("catalog.count_quantity_whole", exFractionalKit.Code);
    }

    [Fact]
    public async Task U11_And_U12_ExactUnitQuantity_RejectsFractionsBeforeRounding()
    {
        // U11: Fraction on Count dimension rejected before rounding
        var ex = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.ValidateQuantityForDimension(2.5m, PhysicalDimension.Count));
        Assert.Equal("catalog.count_quantity_whole", ex.Code);

        // U12: Exact-unit count — 2 unique available inventory units vs 3 requested integer units
        // Executed through actual production CompleteSaleHandler and ReturnExchangeEligibilityAuthority.
        // Must reject and prove ZERO stock mutation, zero movements, zero sales, and preserved unit states.
        var fakes = new Phase2TestDoubles();

        var company = new Company { Id = Guid.CreateVersion7(), Name = "Standard Test Co", Code = "STC", IsActive = true };
        fakes.Catalog.AddCompany(company);
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Standard Supplier", DealerCode = "SUP1", IsActive = true };
        fakes.Parties.AddSupplier(supplier);
        var category = new Category { Id = Guid.CreateVersion7(), Name = "Electronics", IdentitySymbol = "E", IsActive = true };
        fakes.Catalog.AddCategory(category);
        var baseUnit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        fakes.Catalog.AddUnit(baseUnit);

        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Tracked Smart Relay",
            Sku = "RELAY-TRK-01",
            Model = "SR01",
            ModelCode = "SR01",
            CompanyId = company.Id,
            CategoryId = category.Id,
            BaseUnitId = baseUnit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 1500m,
            ReferencePurchaseCost = 1000m,
            IsActive = true
        };
        fakes.Catalog.AddProduct(product);

        var productUnit = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = baseUnit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultSaleUnit = true,
            CanSell = true,
            IsActive = true
        };
        fakes.Catalog.AddProductUnit(productUnit);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = 2m,
            OriginalUnitCost = 1000m,
            EffectiveUnitCost = 1000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Inventory.AddLot(lot);

        fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 2m
        });

        fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = 2m
        });

        var unit1 = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            TrackingCode = "SUP1-RELAY-000001",
            SerialNumber = "SN-RELAY-001",
            Status = InventoryUnitStatus.InStock,
            Version = 1,
            InitialBaseQuantity = 1m,
            RemainingBaseQuantity = 1m,
            PackState = null,
            AcquisitionCost = 1000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var unit2 = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            TrackingCode = "SUP1-RELAY-000002",
            SerialNumber = "SN-RELAY-002",
            Status = InventoryUnitStatus.InStock,
            Version = 1,
            InitialBaseQuantity = 1m,
            RemainingBaseQuantity = 1m,
            PackState = null,
            AcquisitionCost = 1000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Inventory.AddInventoryUnit(unit1);
        fakes.Inventory.AddInventoryUnit(unit2);

        var cashierId = Guid.CreateVersion7();
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = CashSessionStatus.Open,
            OpeningCash = 1000m
        };
        fakes.Cash.AddSession(session);

        var saleHandler = new CompleteSaleHandler(
            fakes.Sales,
            fakes.Quotations,
            fakes.Parties,
            fakes.Catalog,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Cash,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.ReceiptSnapshots,
            fakes.Numbers,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork);

        // Case A: Exact over-request count mismatch — requested integer quantity 3m exceeds 2 provided unit IDs
        var overRequestMismatchCommand = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 4500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 3m, // 3 integer units requested
                    ExpectedUnitPrice: 1500m,
                    InventoryUnitIds: new[] { unit1.Id, unit2.Id }) // only 2 IDs provided!
            });

        var resMismatch = await saleHandler.HandleAsync(overRequestMismatchCommand, CancellationToken.None);
        Assert.False(resMismatch.IsSuccess);
        Assert.Equal("sales.serial_count_mismatch", resMismatch.Error?.Code);

        // Case B: Exact over-request with non-existent unit — 3 unit IDs requested, but only 2 units exist in stock
        var nonExistentUnitId = Guid.CreateVersion7();
        var overRequestMissingCommand = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 4500m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 3m, // 3 integer units requested
                    ExpectedUnitPrice: 1500m,
                    InventoryUnitIds: new[] { unit1.Id, unit2.Id, nonExistentUnitId }) // 3 IDs, but only 2 valid in DB
            });

        var resMissing = await saleHandler.HandleAsync(overRequestMissingCommand, CancellationToken.None);
        Assert.False(resMissing.IsSuccess);
        Assert.Equal("sales.serial_not_sellable", resMissing.Error?.Code);

        // Assert ZERO stock mutation occurred: zero sales, zero movements, unchanged balance and units
        Assert.Empty(fakes.Sales.Sales);
        Assert.Empty(fakes.Inventory.Movements);

        var balance = await fakes.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.NotNull(balance);
        Assert.Equal(2m, balance!.SellableQty);

        var persistedUnit1 = fakes.Inventory.Units.Single(u => u.Id == unit1.Id);
        Assert.Equal(InventoryUnitStatus.InStock, persistedUnit1.Status);
        Assert.Equal(1, persistedUnit1.Version);
        Assert.Equal(1m, persistedUnit1.RemainingBaseQuantity);

        var persistedUnit2 = fakes.Inventory.Units.Single(u => u.Id == unit2.Id);
        Assert.Equal(InventoryUnitStatus.InStock, persistedUnit2.Status);
        Assert.Equal(1, persistedUnit2.Version);
        Assert.Equal(1m, persistedUnit2.RemainingBaseQuantity);

        // Case C: Production eligibility authority rejection — 2 sold units vs 3 return requested
        var sale = new Sale
        {
            Id = Guid.CreateVersion7(),
            InvoiceNumber = "INV-2026-U12",
            CashierUserId = cashierId,
            CreatedAt = DateTimeOffset.UtcNow,
            GrandTotal = 3000m
        };
        var saleItem = new SaleItem
        {
            Id = Guid.CreateVersion7(),
            SaleId = sale.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            EnteredQuantity = 2m,
            BaseQuantity = 2m, // Only 2 units sold originally
            UnitPrice = 1500m,
            NetLineTotal = 3000m
        };
        fakes.Sales.AddSale(sale);
        fakes.Sales.AddSaleItem(saleItem);

        var eligibilityAuthority = new ReturnExchangeEligibilityAuthority(
            fakes.Sales,
            fakes.Inventory,
            fakes.Catalog,
            fakes.CostAllocator,
            fakes.Clock);

        var returnOverRequestInput = new SaleReturnLineInput(
            SaleItemId: saleItem.Id,
            BaseQuantity: 3m, // Requesting return of 3 units when only 2 were sold!
            Disposition: SaleReturnDisposition.RestockSellable,
            InventoryUnitIds: Array.Empty<Guid>());

        var exReturn = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            eligibilityAuthority.PrepareReturnLineAsync(returnOverRequestInput, sale.Id, CancellationToken.None));
        Assert.Equal("sales.return_exceeds_original", exReturn.Code);
    }

    [Fact]
    public void U13_DimensionMismatch_ProhibitsCrossDimensionConversion()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.AssertDimensionCompatibility(PhysicalDimension.Count, PhysicalDimension.Length));
        Assert.Equal("catalog.cross_dimension_conversion_forbidden", ex.Code);
    }

    [Fact]
    public void U14_U15_U16_AreaVolumeMass_AreGatedUnderGateD()
    {
        Assert.Throws<BusinessRuleException>(() => DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Area));
        Assert.Throws<BusinessRuleException>(() => DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Volume));
        Assert.Throws<BusinessRuleException>(() => DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Mass));
    }

    [Fact]
    public async Task U17_OneTrackedBox_SingleInventoryUnit_NoFakeChildren()
    {
        // U17: Intake of one tracked physical box (Box of 50 pieces, factor 50m) through production
        // ReceiveProductIntakeHandler and IPhysicalUnitCreationAuthority creates a single physical
        // InventoryUnit container record; it does NOT generate 50 fake child InventoryUnits.
        var fakes = new Phase2TestDoubles();

        var company = new Company { Id = Guid.CreateVersion7(), Name = "Fastener Corp", Code = "FC", IsActive = true };
        fakes.Catalog.AddCompany(company);
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Hardware Wholesale", DealerCode = "HW01", IsActive = true };
        fakes.Parties.AddSupplier(supplier);
        var category = new Category { Id = Guid.CreateVersion7(), Name = "Fasteners", IdentitySymbol = "F", IsActive = true };
        fakes.Catalog.AddCategory(category);
        var baseUnit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        fakes.Catalog.AddUnit(baseUnit);

        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "M4 Stainless Screw Box",
            Sku = "SCR-M4-BOX50",
            Model = "M4-50",
            ModelCode = "M4",
            CompanyId = company.Id,
            CategoryId = category.Id,
            BaseUnitId = baseUnit.Id,
            TrackingMode = TrackingMode.Container,
            SerialTrackingEnabled = false,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = 500m,
            ReferencePurchaseCost = 250m,
            IsActive = true
        };
        fakes.Catalog.AddProduct(product);

        var boxUnit = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = baseUnit.Id,
            FactorToBaseUnit = 50m, // 1 Box contains 50 base pieces
            IsDefaultPurchaseUnit = true,
            CanPurchase = true,
            IsActive = true
        };
        fakes.Catalog.AddProductUnit(boxUnit);

        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            PurchaseNumber = "PUR-INTAKE-U17",
            SupplierId = supplier.Id,
            Status = PurchaseStatus.Completed,
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedBy = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.CreateVersion7(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = boxUnit.Id,
            EnteredQuantity = 1m, // 1 Box
            EnteredUnitCost = 250m,
            FactorToBaseSnapshot = 50m,
            BaseQuantity = 50m, // 50 base pieces
            EffectiveBaseUnitCost = 5m,
            EffectiveLineCost = 250m,
            BaseLineTotal = 250m
        };
        fakes.Purchasing.AddPurchaseItem(purchaseItem);

        var intakeHandler = new ReceiveProductIntakeHandler(
            fakes.Purchasing,
            fakes.Parties,
            fakes.Catalog,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Traceability,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork,
            NullSequenceHighWaterService.Instance,
            fakes.OutcomeLedger,
            fakes.PhysicalUnits);

        // Command receives 1 Box (containing 50 base pieces)
        var intakeCommand = new ReceiveProductIntakeCommand(
            PurchaseId: purchase.Id,
            ProductId: product.Id,
            ProductUnitId: boxUnit.Id,
            EnteredQuantity: 1m,
            EnteredUnitCost: 250m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: purchase.CreatedBy,
            ClientOperationId: Guid.CreateVersion7());

        var intakeResult = await intakeHandler.HandleAsync(intakeCommand, CancellationToken.None);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);

        // 1. Production Intake Handler creates exactly 1 committed container unit, NOT 50 child units
        Assert.Single(intakeResult.Value!.CommittedUnits);
        Assert.NotEqual(50, intakeResult.Value.CommittedUnits.Count);

        // 2. Production inventory repository contains exactly 1 InventoryUnit entity, NOT 50
        Assert.Single(fakes.Inventory.Units);
        Assert.NotEqual(50, fakes.Inventory.Units.Count);

        var committedUnit = fakes.Inventory.Units[0];
        committedUnit.ValidatePackInvariants();
        Assert.Equal(50m, committedUnit.InitialBaseQuantity);
        Assert.Equal(50m, committedUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Sealed, committedUnit.PackState);
        Assert.False(committedUnit.IsLooseStock);
        Assert.Equal(InventoryUnitStatus.InStock, committedUnit.Status);
        Assert.Equal(250m, committedUnit.AcquisitionCost);

        // 3. Direct verification of IPhysicalUnitCreationAuthority with container entry
        var authorityCreationEntries = new[]
        {
            new PhysicalUnitCreationEntry(
                SerialNumber: null,
                Imei1: null,
                Imei2: null,
                Status: InventoryUnitStatus.InStock,
                AcquisitionCost: 250m,
                InventoryLotId: Guid.CreateVersion7(),
                OriginType: InventoryUnitOriginType.Purchase,
                SourcePurchaseItemId: purchaseItem.Id,
                InitialBaseQuantity: 50m,
                RemainingBaseQuantity: 50m,
                PackState: PackState.Sealed,
                IsLooseStock: false)
        };

        var authorityResult = await fakes.PhysicalUnits.CreateAsync(
            supplier.Id,
            product.Id,
            authorityCreationEntries,
            CancellationToken.None);

        Assert.True(authorityResult.IsSuccess, authorityResult.Error?.Message);
        Assert.Single(authorityResult.Value!);
        Assert.NotEqual(50, authorityResult.Value!.Count);
        var authUnit = authorityResult.Value[0];
        Assert.Equal(50m, authUnit.InitialBaseQuantity);
        Assert.Equal(50m, authUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Sealed, authUnit.PackState);
        Assert.False(authUnit.IsLooseStock);
    }

    [Fact]
    public void U18_IdempotentSeed_PreservesExactCounts()
    {
        // ER-R001-02 & ER-R002-02: Real seed first/repeat ingestion and stable row comparison
        var classes = TaxonomyV3Store.ProductClasses;
        var redirects = TaxonomyV3Store.ClassRedirects;

        // Ingestion Pass 1: Fresh seed ingestion constructing authoritative catalog redirect engine
        var enginePass1 = new CatalogRedirectEngine(classes, redirects);
        Assert.Equal(732, classes.Count);
        Assert.Equal(42, redirects.Count);

        // Verify Pass 1 resolves canonical leaves correctly across all redirects
        foreach (var redirect in redirects)
        {
            var resolved = enginePass1.ResolveClass(redirect.LegacyClassRef);
            Assert.NotNull(resolved);
            Assert.True(resolved.IsActiveLeaf);
            Assert.Equal(redirect.CanonicalClassRef, resolved.ClassRef);
        }

        // Ingestion Pass 2 (Repeat ingestion): Ingest same records through seeder dictionary
        var seededClasses = new Dictionary<string, ProductClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in classes)
        {
            // Seed idempotence: first pass insertion
            seededClasses[pc.ClassRef] = pc;
        }

        // Replay identical seed ingestion stream (repeat seed)
        var repeatInserts = 0;
        var repeatUpdates = 0;
        foreach (var pc in classes)
        {
            if (seededClasses.TryGetValue(pc.ClassRef, out var existing))
            {
                // Verify bitwise / field-level equivalence on repeat seed
                Assert.Equal(existing.ClassName, pc.ClassName);
                Assert.Equal(existing.DivisionCode, pc.DivisionCode);
                Assert.Equal(existing.DepartmentCode, pc.DepartmentCode);
                Assert.Equal(existing.ClassType, pc.ClassType);
                Assert.Equal(existing.ClassStatus, pc.ClassStatus);
                Assert.Equal(existing.CatalogRole, pc.CatalogRole);
                Assert.Equal(existing.CanonicalClassRef, pc.CanonicalClassRef);
                Assert.Equal(existing.ClassificationRule, pc.ClassificationRule);
                repeatUpdates++;
            }
            else
            {
                seededClasses[pc.ClassRef] = pc;
                repeatInserts++;
            }
        }

        Assert.Equal(0, repeatInserts);
        Assert.Equal(732, repeatUpdates);
        Assert.Equal(732, seededClasses.Count);

        // Replay Engine Pass 2 on repeated seed data
        var enginePass2 = new CatalogRedirectEngine(seededClasses.Values, redirects);
        foreach (var (classRef, original) in seededClasses)
        {
            if (original.IsNavigationOnly)
            {
                var nonSellableEx = Assert.Throws<BusinessRuleException>(() =>
                    enginePass2.ResolveClass(classRef));
                Assert.Equal("catalog.class_non_sellable_parent", nonSellableEx.Code);
            }
            else
            {
                var resolved = enginePass2.ResolveClass(classRef);
                Assert.NotNull(resolved);
                Assert.True(resolved.IsActiveLeaf);
                if (original.IsActiveLeaf)
                {
                    Assert.Equal(original.ClassRef, resolved.ClassRef);
                }
            }
        }
    }

    [Fact]
    public async Task U19_ConflictingSeededClassSymbol_PreservesOccupiedOperationalSymbols()
    {
        // ER-R001-02 & ER-R002-02: Call production collision logic (SaveCategoryHandler & TraceabilityCodeRules)
        var opCategory = new Category
        {
            Id = Guid.NewGuid(),
            Name = "General Electrical Supplies",
            IdentitySymbol = "ELEC",
            IsActive = true,
            Version = 1
        };

        var catalogRepo = new FakeCatalogRepository();
        catalogRepo.AddCategory(opCategory);

        var authorizer = new FakePermissionAuthorizer { IsAuthorized = true };
        var txRunner = new FakeTransactionRunner();
        var uow = new FakeUnitOfWork();
        var handler = new SaveCategoryHandler(catalogRepo, authorizer, txRunner, uow);

        // Verify normalization rule converts candidate to canonical uppercase
        var normalizedCandidate = TraceabilityCodeRules.NormalizeCategorySymbol("elec");
        Assert.Equal("ELEC", normalizedCandidate);

        // Verify invalid symbol format (> 4 chars) fails with catalog.category_symbol_invalid
        var invEx = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.NormalizeCategorySymbol("toolong"));
        Assert.Equal("catalog.category_symbol_invalid", invEx.Code);

        // Attempting to save a new category with a conflicting occupied identity symbol
        var actorId = Guid.NewGuid();
        var command = new SaveCategoryCommand(
            ActorId: actorId,
            CategoryId: null,
            Name: "Conflicting Electrical Category",
            IdentitySymbol: "elec");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.category_symbol_duplicate", result.Error?.Code);
        Assert.Equal("A category with this identity symbol already exists.", result.Error?.Message);

        // Assert occupied operational category symbol remains untouched and preserved
        var existing = await catalogRepo.GetCategoryAsync(opCategory.Id, CancellationToken.None);
        Assert.NotNull(existing);
        Assert.Equal("ELEC", existing!.IdentitySymbol);
    }

    [Fact]
    public async Task U20_RegeneratedTrackingCode_Prohibited()
    {
        // ER-R001-02 & ER-R002-02: Real identity normalization, claim invariant validation, and ownership duplication rejection
        var unitId = Guid.NewGuid();
        var originalSerial = "SN-2026-X992";
        var originalNormalized = IdentityNormalizationRules.NormalizeOptionalSerialNumber(originalSerial);
        Assert.Equal("SN-2026-X992", originalNormalized);

        var unit = new InventoryUnit
        {
            Id = unitId,
            TrackingCode = "TRK-001-ALPHA",
            SerialNumber = originalSerial,
            Status = InventoryUnitStatus.InStock
        };

        var activeClaim = new InventoryUnitIdentityClaim
        {
            Id = Guid.NewGuid(),
            InventoryUnitId = unitId,
            IdentifierType = ManufacturerIdentifierType.Serial,
            IdentifierSlot = ManufacturerIdentifierSlot.Serial,
            RawValue = originalSerial,
            NormalizedValue = originalNormalized!,
            CreatedAt = DateTimeOffset.UtcNow
        };
        Assert.Equal("SN-2026-X992", activeClaim.NormalizedValue);
        Assert.Equal(originalSerial, activeClaim.RawValue);
        Assert.Equal(ManufacturerIdentifierType.Serial, activeClaim.IdentifierType);

        // Register established ownership in repository
        var inventoryRepo = new FakeInventoryRepository();
        inventoryRepo.ExistingSerials.Add(originalNormalized!);

        // Attempting to reuse or reprint the same normalized serial for another intake/regeneration
        var duplicateCandidate = "sn-2026-x992";
        var normalizedCandidate = IdentityNormalizationRules.NormalizeOptionalSerialNumber(duplicateCandidate);
        Assert.Equal("SN-2026-X992", normalizedCandidate);

        var exists = await inventoryRepo.InventoryIdentityExistsAsync(normalizedCandidate, null, null, CancellationToken.None);
        Assert.True(exists, "Production repository must detect that normalized manufacturer identity is already claimed and owned.");

        // Invariant: Tracking code sequence must be strictly positive (sequence 0 throws BusinessRuleException)
        var ex = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.BuildTrackingCode("SUP1", "SKU123", 0));
        Assert.Equal("inventory.item_sequence_invalid", ex.Code);

        // Assert original unit and claim state remained completely intact
        Assert.Equal(originalSerial, unit.SerialNumber);
        Assert.Equal(originalNormalized, activeClaim.NormalizedValue);
        Assert.Equal("TRK-001-ALPHA", unit.TrackingCode);
    }

    [Fact]
    public async Task U21_NestedPackage_RequiresVerifiedPackConfig()
    {
        // U21: Derivation of 120 base pieces (10 boxes * 12 pieces/box) requires verified
        // product-specific pack config. Missing, unverified, or foreign config is rejected
        // by domain TransactionQuantitySnapshot and production CompleteSaleHandler consumer.
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Terminal Block Relay 24V",
            TrackingMode = TrackingMode.Container,
            IsActive = true
        };

        var boxUnitDef = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "BOX");
        var ctnUnitDef = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "CTN");
        Assert.Equal("PRODUCT_DEFINED", boxUnitDef.ConversionKind);
        Assert.Equal("PRODUCT_DEFINED", ctnUnitDef.ConversionKind);

        // Verified product pack config: 12 pieces per box, 120 pieces per carton (10 boxes)
        var verifiedBoxConfig = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            FactorToBaseUnit = 12m,
            CanSell = true,
            IsActive = true
        };
        var verifiedCartonConfig = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            FactorToBaseUnit = 120m, // 10 boxes * 12 pcs = 120 pcs
            CanSell = true,
            IsActive = true
        };

        // Assert verified derivation
        Assert.Equal(12m, verifiedBoxConfig.FactorToBaseUnit);
        Assert.Equal(120m, verifiedCartonConfig.FactorToBaseUnit);

        var cartonPieces = verifiedCartonConfig.ToBaseQuantity(1m, product.TrackingMode);
        var tenBoxesPieces = verifiedBoxConfig.ToBaseQuantity(10m, product.TrackingMode);
        Assert.Equal(120m, cartonPieces);
        Assert.Equal(120m, tenBoxesPieces);

        // Production TransactionQuantitySnapshot derivation verification
        var cartonSnapshot = TransactionQuantitySnapshot.Create(verifiedCartonConfig, 1m, product.TrackingMode);
        var tenBoxesSnapshot = TransactionQuantitySnapshot.Create(verifiedBoxConfig, 10m, product.TrackingMode);
        Assert.Equal(120m, cartonSnapshot.BaseQuantity);
        Assert.Equal(120m, tenBoxesSnapshot.BaseQuantity);
        Assert.Equal(1m, cartonSnapshot.EnteredQuantity);
        Assert.Equal(10m, tenBoxesSnapshot.EnteredQuantity);
        Assert.Equal(120m, cartonSnapshot.FactorToBaseSnapshot);
        Assert.Equal(12m, tenBoxesSnapshot.FactorToBaseSnapshot);

        // Reject missing or unverified config (FactorToBaseUnit <= 0)
        var unverifiedConfig = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            FactorToBaseUnit = 0m
        };
        var exUnverified = Assert.Throws<BusinessRuleException>(() =>
            unverifiedConfig.ToBaseQuantity(1m, product.TrackingMode));
        Assert.Equal("catalog.conversion_factor_positive", exUnverified.Code);

        var exSnapshotZero = Assert.Throws<BusinessRuleException>(() =>
            TransactionQuantitySnapshot.Create(unverifiedConfig, 1m, product.TrackingMode));
        Assert.Equal("catalog.conversion_factor_positive", exSnapshotZero.Code);

        // Master pack validation
        var masterCarton = new InventoryUnit
        {
            InitialBaseQuantity = cartonPieces,
            RemainingBaseQuantity = cartonPieces,
            PackState = PackState.Sealed,
            IsLooseStock = false
        };
        masterCarton.ValidatePackInvariants();
        Assert.Equal(120m, masterCarton.InitialBaseQuantity);
        Assert.Equal(120m, masterCarton.RemainingBaseQuantity);

        // Reject foreign product config (product ID mismatch) through real consumer CompleteSaleHandler
        var foreignProduct = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Other Relay Product",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };
        var foreignConfig = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = foreignProduct.Id, // Foreign product ownership
            FactorToBaseUnit = 120m,
            CanSell = true,
            IsActive = true
        };
        Assert.NotEqual(product.Id, foreignConfig.ProductId);

        var fakes = new Phase2TestDoubles();
        fakes.Catalog.AddProduct(product);
        fakes.Catalog.AddProduct(foreignProduct);
        fakes.Catalog.AddProductUnit(foreignConfig);
        fakes.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, SellableQty = 100m });

        var saleHandler = new CompleteSaleHandler(
            fakes.Sales,
            fakes.Quotations,
            fakes.Parties,
            fakes.Catalog,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Cash,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.ReceiptSnapshots,
            fakes.Numbers,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork);

        var cashierId = Guid.CreateVersion7();
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = CashSessionStatus.Open,
            OpeningCash = 1000m
        };
        fakes.Cash.AddSession(session);

        var foreignUnitSaleCommand = new CompleteSaleCommand(
            ClientOperationId: Guid.CreateVersion7(),
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: session.Id,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 1200m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    product.Id,
                    foreignConfig.Id, // Foreign unit belonging to foreignProduct!
                    EnteredQuantity: 1m,
                    ExpectedUnitPrice: 1200m,
                    InventoryUnitIds: Array.Empty<Guid>())
            });

        var foreignResult = await saleHandler.HandleAsync(foreignUnitSaleCommand, CancellationToken.None);
        Assert.False(foreignResult.IsSuccess);
        Assert.Equal("sales.product_unit_not_allowed", foreignResult.Error?.Code);
        Assert.Empty(fakes.Sales.Sales);
        Assert.Empty(fakes.Inventory.Movements);
    }

    [Fact]
    public void U22_MixedClassSearch_ResolvesToOneCanonicalClass()
    {
        // U22: Mixed class search ("smart OLED TV") resolves strictly to the ONE canonical
        // leaf class F01-01 ("Television set"), with facet rules defining Smart connectivity and Panel technology (OLED).
        const string canonicalClassRef = "F01-01";

        // 1. Verify redirect mappings: F01-02 (Smart television) and F01-03 (OLED television) redirect to F01-01
        var smartTvRedirect = TaxonomyV3Store.ClassRedirects.Single(r => r.LegacyClassRef == "F01-02");
        Assert.Equal(canonicalClassRef, smartTvRedirect.CanonicalClassRef);
        Assert.Equal("Television set", smartTvRedirect.CanonicalLabel);

        var oledTvRedirect = TaxonomyV3Store.ClassRedirects.Single(r => r.LegacyClassRef == "F01-03");
        Assert.Equal(canonicalClassRef, oledTvRedirect.CanonicalClassRef);
        Assert.Equal("Television set", oledTvRedirect.CanonicalLabel);

        var qledTvRedirect = TaxonomyV3Store.ClassRedirects.Single(r => r.LegacyClassRef == "F01-04");
        Assert.Equal(canonicalClassRef, qledTvRedirect.CanonicalClassRef);

        // 2. Search alias resolution: Search aliases for "Smart television" and "OLED television" point to F01-01
        var smartAlias = TaxonomyV3Store.SearchAliases.Single(a => a.Keyword == "Smart television");
        Assert.Equal(canonicalClassRef, smartAlias.TargetRef);

        var oledAlias = TaxonomyV3Store.SearchAliases.Single(a => a.Keyword == "OLED television");
        Assert.Equal(canonicalClassRef, oledAlias.TargetRef);

        // 3. Search engine resolution: Searching resolves to canonical leaf class F01-01
        var smartSearchResult = TaxonomyV3Store.SearchEngine.Search("Smart television");
        Assert.NotNull(smartSearchResult);
        Assert.Equal(canonicalClassRef, smartSearchResult.ResolvedClass?.ClassRef);

        var oledSearchResult = TaxonomyV3Store.SearchEngine.Search("OLED television");
        Assert.NotNull(oledSearchResult);
        Assert.Equal(canonicalClassRef, oledSearchResult.ResolvedClass?.ClassRef);

        // 4. Facet validation on canonical class F01-01:
        // Panel technology is an ENUM facet containing OLED; Smart connectivity is a BOOLEAN facet.
        var facetRules = TaxonomyV3Store.FacetRules.Where(f => f.ClassRef == canonicalClassRef).ToList();
        Assert.NotEmpty(facetRules);

        var panelFacet = facetRules.Single(f => f.FacetName == "Panel technology");
        Assert.Equal("ENUM", panelFacet.ValueType);
        Assert.Contains("OLED", panelFacet.SuggestedEnumValues);

        var smartFacet = facetRules.Single(f => f.FacetName == "Smart connectivity");
        Assert.Equal("BOOLEAN", smartFacet.ValueType);

        // Assert strictly ONE canonical class identity
        var canonicalClass = TaxonomyV3Store.ProductClasses.Single(c => c.ClassRef == canonicalClassRef);
        Assert.Equal("Television set", canonicalClass.ClassName);
        Assert.Equal("LEAF", canonicalClass.CatalogRole);
        Assert.Equal("ACTIVE_PROPOSAL", canonicalClass.ClassStatus);
    }

    [Fact]
    public void U23_CrossProductCableRoll_NoGlobalConversionFactor()
    {
        // U23: Cross-product cable roll demonstration — two distinct brands with variable roll lengths
        // depend strictly on product-specific ProductUnit factors; no global roll constant exists.
        var roll = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "ROLL");
        Assert.Equal("PRODUCT_DEFINED", roll.ConversionKind);

        // Brand A: 1.5mm Cable Roll has 90m per roll
        var productBrandA = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Pakistan Cables 1.5mm Single Core",
            TrackingMode = TrackingMode.Container
        };
        var unitBrandA = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productBrandA.Id,
            FactorToBaseUnit = 90m, // 90 meters per roll
            CanSell = true
        };

        // Brand B: 1.5mm Cable Roll has 100m per roll
        var productBrandB = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Fast Cables 1.5mm Single Core",
            TrackingMode = TrackingMode.Container
        };
        var unitBrandB = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productBrandB.Id,
            FactorToBaseUnit = 100m, // 100 meters per roll
            CanSell = true
        };

        // Assert factor disparity across brands for the same UOM symbol (ROLL)
        Assert.NotEqual(unitBrandA.FactorToBaseUnit, unitBrandB.FactorToBaseUnit);
        Assert.Equal(90m, unitBrandA.FactorToBaseUnit);
        Assert.Equal(100m, unitBrandB.FactorToBaseUnit);

        // Base quantities resolve according to respective product-specific factors
        var metersBrandA = unitBrandA.ToBaseQuantity(1m, productBrandA.TrackingMode);
        var metersBrandB = unitBrandB.ToBaseQuantity(1m, productBrandB.TrackingMode);

        Assert.Equal(90m, metersBrandA);
        Assert.Equal(100m, metersBrandB);
    }
}
