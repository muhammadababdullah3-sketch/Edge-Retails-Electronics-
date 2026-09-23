using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase1CanonicalSchemaDomainAlignmentTests
{
    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EdgeRetails.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("EdgeRetails.sln was not found.");
    }

    [Fact]
    public void Manifest_Matches_Canonical_Architecture_Sha256()
    {
        var root = SolutionRoot();
        var reportPath = Path.Combine(root, "docs", "Edge_Retails_Final_Architecture_Report_v1.md");
        var manifestPath = Path.Combine(root, "docs", "Architecture_Authority_Manifest.json");

        Assert.True(File.Exists(reportPath), $"Canonical report not found at: {reportPath}");
        Assert.True(File.Exists(manifestPath), $"Manifest not found at: {manifestPath}");

        var reportBytes = File.ReadAllBytes(reportPath);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(reportBytes));

        var manifestJson = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(manifestJson);
        var expectedSha256 = doc.RootElement.GetProperty("CanonicalSha256").GetString();

        Assert.Equal(expectedSha256, actualSha256);
    }

    [Fact]
    public void Root_Architecture_File_Is_Pointer_Only()
    {
        var root = SolutionRoot();
        var rootFile = Path.Combine(root, "EDGE_RETAILS_BACKEND_ARCHITECTURE_FINAL.md");
        Assert.True(File.Exists(rootFile), $"Root architecture file not found at: {rootFile}");

        var content = File.ReadAllText(rootFile);
        Assert.Contains("docs/Edge_Retails_Final_Architecture_Report_v1.md", content);
        Assert.Contains("docs/Architecture_Authority_Manifest.json", content);
        Assert.True(content.Length < 3000, "Root architecture file should remain a lightweight pointer.");
    }

    [Fact]
    public void TrackingCode_FormatsWithSixDigitDisplay_AndSupportsOver999999()
    {
        var code1 = TraceabilityCodeRules.BuildTrackingCode("SP1", "SKU100", 1);
        Assert.Equal("SP1-SKU100-000001", code1);

        var code999999 = TraceabilityCodeRules.BuildTrackingCode("SP1", "SKU100", 999999);
        Assert.Equal("SP1-SKU100-999999", code999999);

        var codeMillion = TraceabilityCodeRules.BuildTrackingCode("SP1", "SKU100", 1000000);
        Assert.Equal("SP1-SKU100-1000000", codeMillion);

        var ex = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.BuildTrackingCode("SP1", "SKU100", 0));
        Assert.Equal("inventory.item_sequence_invalid", ex.Code);
    }

    [Fact]
    public void DealerCode_DerivesPrefix_ForNamesWithTwoOrMoreLatinLetters()
    {
        Assert.Equal("DA", TraceabilityCodeRules.DeriveDealerPrefix("Dawn Electronics"));
        Assert.Equal("SO", TraceabilityCodeRules.DeriveDealerPrefix("Sony Pakistan"));
        Assert.Equal("AB", TraceabilityCodeRules.DeriveDealerPrefix("A & B"));
        Assert.Equal("AB", TraceabilityCodeRules.DeriveDealerPrefix("a-b"));
        Assert.Equal("XY", TraceabilityCodeRules.DeriveDealerPrefix("  x.y.z  "));

        var code = TraceabilityCodeRules.BuildDealerCode("DA", 42);
        Assert.Equal("DA42", code);

        var ex = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.BuildDealerCode("DA", 0));
        Assert.Equal("parties.dealer_sequence_invalid", ex.Code);
    }

    [Fact]
    public void DealerCode_RequiresExplicitPrefix_WhenSupplierNameHasLessThanTwoLatinLetters()
    {
        // Urdu supplier without explicit prefix must fail
        var exUrdu = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("الرحیم ٹریڈرز"));
        Assert.Equal("parties.dealer_prefix_required", exUrdu.Code);

        // Numeric supplier without explicit prefix must fail
        var exNum = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("12345"));
        Assert.Equal("parties.dealer_prefix_required", exNum.Code);

        // Single Latin letter without explicit prefix must fail
        var exSingle = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("A 123"));
        Assert.Equal("parties.dealer_prefix_required", exSingle.Code);

        // Empty or null supplier name without explicit prefix must fail
        var exEmpty = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix(""));
        Assert.Equal("parties.dealer_prefix_required", exEmpty.Code);

        var exNull = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix(null));
        Assert.Equal("parties.dealer_prefix_required", exNull.Code);

        // Providing valid explicit prefix succeeds
        Assert.Equal("AL", TraceabilityCodeRules.DeriveDealerPrefix("الرحیم ٹریڈرز", "AL"));
        Assert.Equal("DG", TraceabilityCodeRules.DeriveDealerPrefix("12345", "DG"));
        Assert.Equal("AX", TraceabilityCodeRules.DeriveDealerPrefix("A 123", "AX"));
        Assert.Equal("SP", TraceabilityCodeRules.DeriveDealerPrefix("", "SP"));
        Assert.Equal("SP", TraceabilityCodeRules.DeriveDealerPrefix(null, "SP"));

        // Invalid explicit prefix formats must fail
        var exInvLen = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("الرحیم ٹریڈرز", "A"));
        Assert.Equal("parties.dealer_prefix_invalid", exInvLen.Code);

        var exInvLen3 = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("الرحیم ٹریڈرز", "ABC"));
        Assert.Equal("parties.dealer_prefix_invalid", exInvLen3.Code);

        var exInvDigits = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("الرحیم ٹریڈرز", "12"));
        Assert.Equal("parties.dealer_prefix_invalid", exInvDigits.Code);

        var exInvMixed = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.DeriveDealerPrefix("الرحیم ٹریڈرز", "A1"));
        Assert.Equal("parties.dealer_prefix_invalid", exInvMixed.Code);
    }

    [Fact]
    public void IdentityNormalization_EnforcesSerialImeiAndBarcodeRules()
    {
        Assert.Equal("SN-12345", IdentityNormalizationRules.NormalizeSerialNumber("  sn-12345  "));
        var serialEx = Assert.Throws<BusinessRuleException>(() =>
            IdentityNormalizationRules.NormalizeSerialNumber("   "));
        Assert.Equal("identity.serial_required", serialEx.Code);

        // Valid 15-digit IMEI with valid Luhn checksum (356938035643809)
        var validImei15 = "356938035643809";
        Assert.Equal("356938035643809", IdentityNormalizationRules.NormalizeImei(validImei15));

        // Invalid Luhn checksum
        var invalidLuhnEx = Assert.Throws<BusinessRuleException>(() =>
            IdentityNormalizationRules.NormalizeImei("356938035643808"));
        Assert.Equal("identity.imei_checksum_invalid", invalidLuhnEx.Code);

        // 14-digit IMEI
        var imei14 = IdentityNormalizationRules.NormalizeImei("860-123-456-789-01");
        Assert.Equal("86012345678901", imei14);

        // Invalid length
        var lenEx = Assert.Throws<BusinessRuleException>(() =>
            IdentityNormalizationRules.NormalizeImei("12345"));
        Assert.Equal("identity.imei_invalid_length", lenEx.Code);

        // Barcode normalization
        Assert.Equal("BC-998877", IdentityNormalizationRules.NormalizeBarcode("  bc-998877  "));
        var barcodeEx = Assert.Throws<BusinessRuleException>(() =>
            IdentityNormalizationRules.NormalizeBarcode("  "));
        Assert.Equal("identity.barcode_required", barcodeEx.Code);
    }

    [Fact]
    public void SkuNormalization_EnforcesAllowedCharactersAndUppercase()
    {
        Assert.Equal("SKU-100.A_B", TraceabilityCodeRules.NormalizeSku("  sku-100.a_b  "));

        var exEmpty = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.NormalizeSku("   "));
        Assert.Equal("catalog.sku_required", exEmpty.Code);

        var exInvalid = Assert.Throws<BusinessRuleException>(() =>
            TraceabilityCodeRules.NormalizeSku("SKU@100#"));
        Assert.Equal("catalog.sku_invalid", exInvalid.Code);
    }

    [Fact]
    public void QuantityMath_IsWhole_ValidatesExactUnitPreRound()
    {
        Assert.True(QuantityMath.IsWhole(1.000000m));
        Assert.True(QuantityMath.IsWhole(5m));
        Assert.False(QuantityMath.IsWhole(1.5m));
        Assert.False(QuantityMath.IsWhole(0.0001m));

        var exactUnit = new ProductUnit
        {
            ProductId = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            FactorToBaseUnit = 1m
        };

        // Converting a fractional quantity on exact serialized unit throws
        var ex = Assert.Throws<BusinessRuleException>(() =>
            exactUnit.ToBaseQuantity(1.5m, TrackingMode.Serialized));
        Assert.Equal("catalog.serialized_whole_quantity", ex.Code);

        // Whole quantity succeeds
        var baseQty = exactUnit.ToBaseQuantity(2m, TrackingMode.Serialized);
        Assert.Equal(2m, baseQty);
    }

    [Fact]
    public void AttributesPolicy_ValidatesSchemaVersionAndValidJson()
    {
        // Valid JSON object
        AttributesPolicy.Validate("{\"brand\": \"Samsung\", \"color\": \"Black\"}", 1);
        AttributesPolicy.Validate(null, 1);
        AttributesPolicy.Validate("", 1);

        // Invalid schema version
        var exVer = Assert.Throws<BusinessRuleException>(() =>
            AttributesPolicy.Validate("{}", 0));
        Assert.Equal("catalog.attributes_schema_version_invalid", exVer.Code);

        // Non-object JSON
        var exArray = Assert.Throws<BusinessRuleException>(() =>
            AttributesPolicy.Validate("[1, 2, 3]", 1));
        Assert.Equal("catalog.attributes_json_invalid", exArray.Code);

        // Invalid JSON syntax
        var exSyntax = Assert.Throws<BusinessRuleException>(() =>
            AttributesPolicy.Validate("{invalid_json", 1));
        Assert.Equal("catalog.attributes_json_invalid", exSyntax.Code);
    }

    [Fact]
    public void InventoryUnitAccountingPolicy_CoversAllElevenStatuses_WithDeterministicRules()
    {
        InventoryUnitAccountingPolicy.ValidateAllStatusesCovered();

        var inStock = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.InStock);
        Assert.True(inStock.ContributesToStockBalance);
        Assert.Equal(InventoryBucket.Sellable, inStock.AuthoritativeBucket);
        Assert.True(inStock.ContributesToProductCostState);
        Assert.Equal(BusinessOwner.Shop, inStock.BusinessOwner);
        Assert.True(inStock.MayBeSold);
        Assert.False(inStock.IsTerminal);

        var sold = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Sold);
        Assert.False(sold.ContributesToStockBalance);
        Assert.Null(sold.AuthoritativeBucket);
        Assert.False(sold.ContributesToProductCostState);
        Assert.Equal(BusinessOwner.Customer, sold.BusinessOwner);
        Assert.False(sold.MayBeSold);
        Assert.True(sold.MayBeReturned);
        Assert.False(sold.IsTerminal);

        var issuedThaka = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.IssuedThaka);
        Assert.False(issuedThaka.ContributesToStockBalance);
        Assert.Null(issuedThaka.AuthoritativeBucket);
        Assert.True(issuedThaka.ContributesToProductCostState);
        Assert.Equal(BusinessOwner.Shop, issuedThaka.BusinessOwner);

        var withSupplier = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.WithSupplier);
        Assert.True(withSupplier.ContributesToStockBalance);
        Assert.Equal(InventoryBucket.WithSupplier, withSupplier.AuthoritativeBucket);
        Assert.True(withSupplier.ContributesToProductCostState);
        Assert.Equal(BusinessOwner.Shop, withSupplier.BusinessOwner);
        Assert.False(withSupplier.IsTerminal);

        var supplierReturned = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.SupplierReturned);
        Assert.False(supplierReturned.ContributesToStockBalance);
        Assert.Equal(BusinessOwner.ExternalOrHistorical, supplierReturned.BusinessOwner);
        Assert.True(supplierReturned.IsTerminal);

        var scrapped = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Scrapped);
        Assert.True(scrapped.ContributesToStockBalance);
        Assert.Equal(InventoryBucket.Scrap, scrapped.AuthoritativeBucket);
        Assert.True(scrapped.IsTerminal);

        var voided = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.ReceiptVoided);
        Assert.False(voided.ContributesToStockBalance);
        Assert.Equal(BusinessOwner.ExternalOrHistorical, voided.BusinessOwner);
        Assert.True(voided.IsTerminal);

        var warrantyCustomerHeld = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.WarrantyCustomerHeld);
        Assert.False(warrantyCustomerHeld.ContributesToStockBalance);
        Assert.Equal(BusinessOwner.Customer, warrantyCustomerHeld.BusinessOwner);

        var warrantyCustomerHandedOver = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.WarrantyCustomerHandedOver);
        Assert.False(warrantyCustomerHandedOver.ContributesToStockBalance);
        Assert.Equal(BusinessOwner.Customer, warrantyCustomerHandedOver.BusinessOwner);
    }

    [Fact]
    public void InventoryUnit_OriginProvenanceInvariants_EnforcesAuthoritativeSources()
    {
        var purchaseItemId = Guid.NewGuid();
        var warrantyClaimItemId = Guid.NewGuid();
        var warrantyCaseId = Guid.NewGuid();
        var stockAdjustmentItemId = Guid.NewGuid();

        // Valid Purchase origin
        var purchaseUnit = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItemId
        };
        purchaseUnit.ValidateOriginInvariants();

        // Invalid Purchase origin (missing source)
        var invalidPurchase1 = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.Purchase
        };
        var exP1 = Assert.Throws<BusinessRuleException>(() => invalidPurchase1.ValidateOriginInvariants());
        Assert.Equal("inventory.purchase_origin_missing_source", exP1.Code);

        // Invalid Purchase origin (mixed sources)
        var invalidPurchase2 = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = purchaseItemId,
            SourceStockAdjustmentItemId = stockAdjustmentItemId
        };
        var exP2 = Assert.Throws<BusinessRuleException>(() => invalidPurchase2.ValidateOriginInvariants());
        Assert.Equal("inventory.purchase_origin_invalid_sources", exP2.Code);

        // Valid WarrantyReplacement origin (Customer Claim)
        var warrantyClaimUnit = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.WarrantyReplacement,
            SourceWarrantyClaimItemId = warrantyClaimItemId
        };
        warrantyClaimUnit.ValidateOriginInvariants();

        // Valid WarrantyReplacement origin (Shop Stock Case)
        var warrantyCaseUnit = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.WarrantyReplacement,
            SourceWarrantyCaseId = warrantyCaseId
        };
        warrantyCaseUnit.ValidateOriginInvariants();

        // Invalid WarrantyReplacement origin (both claim and case set)
        var invalidWarrantyMixed = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.WarrantyReplacement,
            SourceWarrantyClaimItemId = warrantyClaimItemId,
            SourceWarrantyCaseId = warrantyCaseId
        };
        var exW = Assert.Throws<BusinessRuleException>(() => invalidWarrantyMixed.ValidateOriginInvariants());
        Assert.Equal("inventory.warranty_origin_missing_source", exW.Code);

        // Valid StockAdjustment origin
        var stockAdjUnit = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = stockAdjustmentItemId
        };
        stockAdjUnit.ValidateOriginInvariants();

        // Invalid StockAdjustment origin (missing source)
        var invalidAdj = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = InventoryUnitOriginType.StockAdjustment
        };
        var exAdj = Assert.Throws<BusinessRuleException>(() => invalidAdj.ValidateOriginInvariants());
        Assert.Equal("inventory.stock_adjustment_origin_missing_source", exAdj.Code);

        // Invalid origin type (0 is not valid)
        var invalidNone = new InventoryUnit
        {
            ProductId = Guid.NewGuid(),
            OriginType = (InventoryUnitOriginType)0
        };
        var exNone = Assert.Throws<BusinessRuleException>(() => invalidNone.ValidateOriginInvariants());
        Assert.Equal("inventory.origin_type_invalid", exNone.Code);
    }

    [Fact]
    public void StockAdjustment_Model_EnforcesCanonicalProperties()
    {
        var adjId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var adjustment = new StockAdjustment
        {
            Id = adjId,
            AdjustmentNumber = "ADJ-20260922-0001",
            Mode = StockAdjustmentMode.Delta,
            Reason = StockAdjustmentReason.OpeningStock,
            Status = StockAdjustmentStatus.Posted,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = correlationId,
            Note = "Initial count",
            CreatedAt = now,
            Version = 1
        };

        Assert.Equal(adjId, adjustment.Id);
        Assert.Equal("ADJ-20260922-0001", adjustment.AdjustmentNumber);
        Assert.Equal(StockAdjustmentMode.Delta, adjustment.Mode);
        Assert.Equal(StockAdjustmentReason.OpeningStock, adjustment.Reason);
        Assert.Equal(StockAdjustmentStatus.Posted, adjustment.Status);

        var itemId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        var item = new StockAdjustmentItem
        {
            Id = itemId,
            StockAdjustmentId = adjId,
            ProductId = productId,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = 5m,
            UnitCostSnapshot = 1200m,
            TotalCostSnapshot = 6000m,
            SupplierId = supplierId,
            ReasonDetails = "Warehouse count"
        };

        Assert.Equal(itemId, item.Id);
        Assert.Equal(adjId, item.StockAdjustmentId);
        Assert.Equal(StockAdjustmentDirection.Increase, item.Direction);
        Assert.Equal(5m, item.BaseQuantity);
        Assert.Equal(1200m, item.UnitCostSnapshot);
        Assert.Equal(6000m, item.TotalCostSnapshot);
    }

    [Fact]
    public void SupplierAccountDirectionMatrix_ValidatesAllEntryTypes()
    {
        var purchase = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 1000m
        };
        purchase.ValidateDirection();

        var payment = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.SupplierPayment,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 500m
        };
        payment.ValidateDirection();

        var paymentReversal = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.SupplierPaymentReversal,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 500m
        };
        paymentReversal.ValidateDirection();

        var purchaseReturn = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.PurchaseReturnCredit,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 200m
        };
        purchaseReturn.ValidateDirection();

        var purchaseVoid = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.PurchaseVoidReversal,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 1000m
        };
        purchaseVoid.ValidateDirection();

        var refund = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.SupplierRefundReceived,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 300m
        };
        refund.ValidateDirection();

        var refundReversal = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.SupplierRefundReversal,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 300m
        };
        refundReversal.ValidateDirection();

        var warrantyCredit = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.WarrantyCredit,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 400m
        };
        warrantyCredit.ValidateDirection();

        var adjIncrease = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.AdjustmentIncrease,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 100m
        };
        adjIncrease.ValidateDirection();

        var adjDecrease = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.AdjustmentDecrease,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 100m
        };
        adjDecrease.ValidateDirection();

        // Invalid direction throws
        var invalid = new SupplierAccountEntry
        {
            SupplierId = Guid.NewGuid(),
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 1000m
        };
        var ex = Assert.Throws<BusinessRuleException>(() => invalid.ValidateDirection());
        Assert.Equal("supplier.account_direction_invalid", ex.Code);
    }

    [Fact]
    public void WarrantyCustody_RestrictedToShopCustomerAndSupplierInV1()
    {
        var custodyNames = Enum.GetNames<WarrantyCustody>();
        Assert.Equal(3, custodyNames.Length);
        Assert.Contains(nameof(WarrantyCustody.WithCustomer), custodyNames);
        Assert.Contains(nameof(WarrantyCustody.WithShop), custodyNames);
        Assert.Contains(nameof(WarrantyCustody.WithSupplier), custodyNames);
        Assert.DoesNotContain("WithServiceCenter", custodyNames);
    }

    [Fact]
    public void PosDraft_IsDecoupledFromStockAndCashPosting()
    {
        var draft = new PosDraft
        {
            DraftNumber = "DFT-001",
            Status = PosDraftStatus.Open,
            CreatedBy = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(PosDraftStatus.Open, draft.Status);
        Assert.Equal(0, draft.Version);

        var item = new PosDraftItem
        {
            DraftId = draft.Id,
            ProductId = Guid.NewGuid(),
            ProductUnitId = Guid.NewGuid(),
            EnteredQuantity = 2m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 2m,
            DisplayedUnitPriceSnapshot = 1500m
        };

        Assert.Equal(2m, item.BaseQuantity);
    }

    [Fact]
    public void DbContext_Model_IncludesAllPhase1EntitiesAndConfigurations()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=test;Username=test")
            .Options;

        using var context = new EdgeRetailsDbContext(options);
        var designTimeModel = context.GetService<IDesignTimeModel>().Model;

        Assert.NotNull(designTimeModel.FindEntityType(typeof(SupplierProduct)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(SupplierCodeSequence)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(InventoryUnit)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(Product)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(StockBalance)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(ProductCostState)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(StockAdjustment)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(StockAdjustmentItem)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(SupplierAccountEntry)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(PosDraft)));
        Assert.NotNull(designTimeModel.FindEntityType(typeof(WarrantyClaim)));

        var productEntity = designTimeModel.FindEntityType(typeof(Product));
        Assert.NotNull(productEntity?.FindProperty("AttributesJson"));
        Assert.NotNull(productEntity?.FindProperty("AttributesSchemaVersion"));

        var unitEntity = designTimeModel.FindEntityType(typeof(InventoryUnit));
        Assert.NotNull(unitEntity?.FindProperty("SourceStockAdjustmentItemId"));

        // Check foreign key behavior for SourceStockAdjustmentItemId
        var foreignKey = unitEntity?.GetForeignKeys()
            .FirstOrDefault(fk => fk.Properties.Any(p => p.Name == "SourceStockAdjustmentItemId"));
        Assert.NotNull(foreignKey);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);

        // Check check constraints
        var unitCheckConstraints = unitEntity?.GetCheckConstraints();
        Assert.NotNull(unitCheckConstraints);
        Assert.Contains(unitCheckConstraints, c => c.Name == "ck_inventory_units_origin_provenance");

        var stockAdjItemEntity = designTimeModel.FindEntityType(typeof(StockAdjustmentItem));
        var stockAdjCheckConstraints = stockAdjItemEntity?.GetCheckConstraints();
        Assert.NotNull(stockAdjCheckConstraints);
        Assert.Contains(stockAdjCheckConstraints, c => c.Name == "ck_stock_adjustment_items_base_qty_positive");
    }
}

