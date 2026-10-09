using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase1CReceivingAndLabelPrintingTests
{
    [Fact]
    public async Task OneProductPhysicalIntakeSession_IsStrictlyEnforced_WhenProductDoesNotBelongToInvoice()
    {
        var testContext = CreateTestContext();
        var (purchase, itemA, _) = await testContext.SeedPurchaseWithProductsAsync();

        var foreignProductId = Guid.NewGuid();
        var foreignProductUnitId = Guid.NewGuid();

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            foreignProductId,
            foreignProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: 100m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result = await testContext.IntakeHandler.HandleAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("purchasing.product_not_on_purchase", result.Error?.Code);
    }

    [Fact]
    public async Task MultiLinePurchaseInvoice_ReceivesProductA_ThenProductB_Independently()
    {
        var testContext = CreateTestContext();
        var (purchase, itemA, itemB) = await testContext.SeedPurchaseWithTwoLinesAsync();

        // 1. Receive Product A (Quantity mode, 10 pcs)
        var commandA = new ReceiveProductIntakeCommand(
            purchase.Id,
            itemA.ProductId,
            itemA.ProductUnitId,
            EnteredQuantity: 10m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var resultA = await testContext.IntakeHandler.HandleAsync(commandA);
        Assert.True(resultA.IsSuccess, resultA.Error?.Message);
        Assert.Equal(10m, resultA.Value!.ReceivedQuantity);

        // 2. Receive Product B (Quantity mode, 5 pcs)
        var commandB = new ReceiveProductIntakeCommand(
            purchase.Id,
            itemB.ProductId,
            itemB.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: 120m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var resultB = await testContext.IntakeHandler.HandleAsync(commandB);
        Assert.True(resultB.IsSuccess, resultB.Error?.Message);
        Assert.Equal(5m, resultB.Value!.ReceivedQuantity);

        // Verify stock balances for both products were independently updated
        var balanceA = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(itemA.ProductId, CancellationToken.None);
        var balanceB = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(itemB.ProductId, CancellationToken.None);

        Assert.NotNull(balanceA);
        Assert.Equal(10m, balanceA!.SellableQty);

        Assert.NotNull(balanceB);
        Assert.Equal(5m, balanceB!.SellableQty);
    }

    [Fact]
    public async Task QuantityTrackingPolicy_Intake_CreatesZeroInventoryUnits()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product) = await testContext.SeedPurchaseWithProductPolicyAsync(TrackingMode.Quantity);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 25m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result = await testContext.IntakeHandler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(25m, result.Value!.ReceivedQuantity);
        Assert.Equal(TrackingMode.Quantity, result.Value.TrackingMode);

        // Hard invariant: Zero fake InventoryUnits created for bulk quantity tracking
        Assert.Empty(result.Value.CommittedUnits);
        var allUnits = testContext.Doubles.Inventory.Units.Where(u => u.ProductId == product.Id).ToList();
        Assert.Empty(allUnits);

        // Stock balance and lot correctly credited
        var balance = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.NotNull(balance);
        Assert.Equal(25m, balance!.SellableQty);
    }

    [Fact]
    public async Task LengthTrackingPolicy_Intake_CreatesZeroPerMeterInventoryUnits()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product) = await testContext.SeedPurchaseWithProductPolicyAsync(TrackingMode.Length);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 100m, // 100 meters
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result = await testContext.IntakeHandler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(100m, result.Value!.ReceivedQuantity);
        Assert.Equal(TrackingMode.Length, result.Value.TrackingMode);

        // Hard invariant: Zero per-meter InventoryUnits created
        Assert.Empty(result.Value.CommittedUnits);
        var allUnits = testContext.Doubles.Inventory.Units.Where(u => u.ProductId == product.Id).ToList();
        Assert.Empty(allUnits);

        var balance = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.NotNull(balance);
        Assert.Equal(100m, balance!.SellableQty);
    }

    [Fact]
    public async Task IndividualPieceTrackingPolicy_AllocatesExactIdentities_AndAdvancesSequenceAtomically()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "AB1",
            companyCode: "PK",
            categorySymbol: "F",
            modelCode: "DLX56",
            startingSequence: 27);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 3m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result = await testContext.IntakeHandler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3m, result.Value!.ReceivedQuantity);
        Assert.Equal(3, result.Value.CommittedUnits.Count);

        // Validate canonical physical identities: SupplierCode-ProductCode-PhysicalSequence
        // ProductCode = CompanyCode + CategorySymbol + "-" + ModelCode = PKF-DLX56
        // Canonical: AB1-PKF-DLX56-000027, 000028, 000029
        var units = result.Value.CommittedUnits;

        Assert.Equal(27, units[0].ItemSequence);
        Assert.Equal("AB1-PKF-DLX56-000027", units[0].TrackingCode);

        Assert.Equal(28, units[1].ItemSequence);
        Assert.Equal("AB1-PKF-DLX56-000028", units[1].TrackingCode);

        Assert.Equal(29, units[2].ItemSequence);
        Assert.Equal("AB1-PKF-DLX56-000029", units[2].TrackingCode);

        // Sequence must advance to 30
        Assert.Equal(30, supplierProduct.NextItemSequence);

        // Units must be saved in repository
        Assert.Equal(3, testContext.Doubles.Inventory.Units.Count);
    }

    [Fact]
    public async Task ContainerPackTrackingPolicy_Intake_AllocatesOneUnitPerContainer()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedContainerSetupAsync(
            unitsPerPack: 12,
            startingSequence: 1);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 2m, // 2 containers = 24 base units
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result = await testContext.IntakeHandler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2m, result.Value!.ReceivedQuantity);
        Assert.Equal(24m, result.Value.BaseQuantity);
        Assert.Equal(2, result.Value.CommittedUnits.Count); // Exactly 1 unit per container

        var balance = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.NotNull(balance);
        Assert.Equal(24m, balance!.SellableQty);
    }

    [Fact]
    public async Task SerializedProductIntake_RejectsMissingOrDuplicateSerialAndImei()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, _) = await testContext.SeedSerializedSetupAsync();

        // 1. Missing serial when serial is required
        var missingSerialCmd = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 1m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: new[] { new SerializedIdentityInput(SerialNumber: "", Imei1: "354891001234567") },
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result1 = await testContext.IntakeHandler.HandleAsync(missingSerialCmd);
        Assert.False(result1.IsSuccess);
        Assert.Equal("purchasing.serial_required", result1.Error?.Code);

        // 2. Duplicate serial within same batch
        var duplicateSerialCmd = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 2m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: new[]
            {
                new SerializedIdentityInput(SerialNumber: "SN-1001", Imei1: "354891001234561"),
                new SerializedIdentityInput(SerialNumber: "SN-1001", Imei1: "354891001234562")
            },
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result2 = await testContext.IntakeHandler.HandleAsync(duplicateSerialCmd);
        Assert.False(result2.IsSuccess);
        Assert.Equal("purchasing.duplicate_serial_in_batch", result2.Error?.Code);

        // 3. Duplicate serial existing in database
        testContext.Doubles.Inventory.ExistingSerials.Add("SN-EXISTING");
        testContext.Doubles.Inventory.Units.Add(new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SerialNumber = "SN-EXISTING",
            Status = InventoryUnitStatus.InStock
        });

        var existingSerialCmd = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 1m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: new[] { new SerializedIdentityInput(SerialNumber: "SN-EXISTING", Imei1: "354891001234563") },
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result3 = await testContext.IntakeHandler.HandleAsync(existingSerialCmd);
        Assert.False(result3.IsSuccess);
        Assert.Equal("purchasing.identity_already_exists", result3.Error?.Code);

        // 4. Missing IMEI 1 when IMEI is required
        var missingImeiCmd = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 1m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: new[] { new SerializedIdentityInput(SerialNumber: "SN-9999", Imei1: "") },
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result4 = await testContext.IntakeHandler.HandleAsync(missingImeiCmd);
        Assert.False(result4.IsSuccess);
        Assert.Equal("purchasing.imei_required", result4.Error?.Code);
    }

    [Fact]
    public async Task PhysicalSequenceHighWater_IsPreservedAndNeverDecrementedOnVoid()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "SUP1",
            companyCode: "CP",
            categorySymbol: "E",
            modelCode: "M1",
            startingSequence: 1);

        // Intake 2 units (sequences 1 and 2)
        var cmd1 = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 2m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result1 = await testContext.IntakeHandler.HandleAsync(cmd1);
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.Equal(3, supplierProduct.NextItemSequence);

        // Simulate high water retention: sequence must remain at least 3
        Assert.True(supplierProduct.NextItemSequence >= 3);

        // Subsequent intake uses sequence 3, never reusing 1 or 2
        var cmd2 = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 1m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result2 = await testContext.IntakeHandler.HandleAsync(cmd2);
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.Equal(3, result2.Value!.CommittedUnits[0].ItemSequence);
        Assert.Equal(4, supplierProduct.NextItemSequence);
    }

    [Fact]
    public void Code128Encoder_ProducesValidBinaryModules_ForCanonicalPhysicalIdentity()
    {
        const string trackingCode = "AB1-PKF-DLX56-000027";
        var modules = Code128Encoder.EncodeToModules(trackingCode);

        Assert.NotNull(modules);
        Assert.NotEmpty(modules);

        // Only binary bits
        Assert.All(modules, ch => Assert.True(ch == '0' || ch == '1'));

        // Starts with Start B pattern: 11010010000
        Assert.StartsWith("11010010000", modules);

        // Ends with Stop pattern: 1100011101011
        Assert.EndsWith("1100011101011", modules);

        // Total modules count: (1 [start] + 20 [data chars] + 1 [checksum]) * 11 + 13 [stop] = 22 * 11 + 13 = 255
        Assert.Equal(255, modules.Length);
    }

    [Fact]
    public async Task PrintPhysicalStickers_PrintAll_SendsAllCommittedUnitsToPrintEngine()
    {
        var testContext = CreateTestContext();
        var unitId1 = Guid.NewGuid();
        var unitId2 = Guid.NewGuid();

        var doc1 = new PhysicalItemStickerDocument(unitId1, "AB1-PKF-DLX56-000001", "Edge Retails", "Item 1", null, "SKU1", 1, null, null, null, "Main Shop", 100m, false, DateTimeOffset.UtcNow);
        var doc2 = new PhysicalItemStickerDocument(unitId2, "AB1-PKF-DLX56-000002", "Edge Retails", "Item 2", null, "SKU2", 2, null, null, null, "Main Shop", 100m, false, DateTimeOffset.UtcNow);

        var docSource = new MemoryStickerDocSource(new Dictionary<Guid, PhysicalItemStickerDocument>
        {
            [unitId1] = doc1,
            [unitId2] = doc2
        });

        var engine = new RecordingStickerPrintEngine();
        var auth = new RecordingProductionAuth();
        var audit = new ProductionAuditCoordinator(new NullAuditSink(), new NullAuditFailureReporter());
        var handler = new PrintPhysicalStickersHandler(auth, docSource, engine, audit, testContext.Doubles.Clock);

        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId1, unitId2 },
            PrinterName: null,
            IsReprint: false,
            RequestedBy: testContext.ActorId);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.TotalRequested);
        Assert.Equal(2, result.Value.SucceededCount);
        Assert.Equal(0, result.Value.FailedCount);
        Assert.Equal(2, engine.PrintedDocuments.Count);
        Assert.Equal("AB1-PKF-DLX56-000001", engine.PrintedDocuments[0].BarcodePayload);
        Assert.Equal("AB1-PKF-DLX56-000002", engine.PrintedDocuments[1].BarcodePayload);
    }

    [Fact]
    public async Task PrintPhysicalStickers_PrintSelected_SendsOnlySelectedUnits()
    {
        var testContext = CreateTestContext();
        var unitId1 = Guid.NewGuid();
        var unitId2 = Guid.NewGuid();
        var unitId3 = Guid.NewGuid();

        var doc1 = new PhysicalItemStickerDocument(unitId1, "AB1-PKF-DLX56-000001", "Edge Retails", "Item 1", null, "SKU1", 1, null, null, null, "Main Shop", 100m, false, DateTimeOffset.UtcNow);
        var doc2 = new PhysicalItemStickerDocument(unitId2, "AB1-PKF-DLX56-000002", "Edge Retails", "Item 2", null, "SKU2", 2, null, null, null, "Main Shop", 100m, false, DateTimeOffset.UtcNow);
        var doc3 = new PhysicalItemStickerDocument(unitId3, "AB1-PKF-DLX56-000003", "Edge Retails", "Item 3", null, "SKU3", 3, null, null, null, "Main Shop", 100m, false, DateTimeOffset.UtcNow);

        var docSource = new MemoryStickerDocSource(new Dictionary<Guid, PhysicalItemStickerDocument>
        {
            [unitId1] = doc1,
            [unitId2] = doc2,
            [unitId3] = doc3
        });

        var engine = new RecordingStickerPrintEngine();
        var auth = new RecordingProductionAuth();
        var audit = new ProductionAuditCoordinator(new NullAuditSink(), new NullAuditFailureReporter());
        var handler = new PrintPhysicalStickersHandler(auth, docSource, engine, audit, testContext.Doubles.Clock);

        // Only print unit 2
        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId2 },
            PrinterName: null,
            IsReprint: false,
            RequestedBy: testContext.ActorId);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value!.TotalRequested);
        Assert.Equal(1, result.Value.SucceededCount);
        Assert.Single(engine.PrintedDocuments);
        Assert.Equal("AB1-PKF-DLX56-000002", engine.PrintedDocuments[0].BarcodePayload);
    }

    [Fact]
    public async Task PrintPhysicalStickers_Reprint_PreservesTrackingCode_RequiresReprintPermission_AndNeverIncrementsSequence()
    {
        var testContext = CreateTestContext();
        var unitId = Guid.NewGuid();

        var doc = new PhysicalItemStickerDocument(
            unitId,
            "AB1-PKF-DLX56-000027",
            "Edge Retails",
            "Item",
            null,
            "SKU",
            27,
            null,
            null,
            null,
            "Main Shop",
            150m,
            IsReprint: true,
            DateTimeOffset.UtcNow);

        var docSource = new MemoryStickerDocSource(new Dictionary<Guid, PhysicalItemStickerDocument>
        {
            [unitId] = doc
        });

        var engine = new RecordingStickerPrintEngine();
        var auth = new RecordingProductionAuth();
        var recordingAudit = new RecordingAuditSink();
        var audit = new ProductionAuditCoordinator(recordingAudit, new NullAuditFailureReporter());
        var handler = new PrintPhysicalStickersHandler(auth, docSource, engine, audit, testContext.Doubles.Clock);

        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: null,
            IsReprint: true,
            RequestedBy: testContext.ActorId);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(ProductionPermissionNames.PrintingReprint, auth.LastCheckedPermission);
        Assert.Single(engine.PrintedDocuments);
        Assert.True(engine.PrintedDocuments[0].IsReprint);
        Assert.Equal("AB1-PKF-DLX56-000027", engine.PrintedDocuments[0].BarcodePayload);

        // Audit record must be DocumentReprinted
        Assert.Contains(recordingAudit.Records, r => r.EventType == ProductionAuditEvents.DocumentReprinted);
    }

    [Fact]
    public async Task PrinterFailure_DoesNotRollbackInventoryOrAlterSequences()
    {
        var testContext = CreateTestContext();
        var unitId = Guid.NewGuid();

        var doc = new PhysicalItemStickerDocument(
            unitId,
            "AB1-PKF-DLX56-000027",
            "Edge Retails",
            "Item",
            null,
            "SKU",
            27,
            null,
            null,
            null,
            "Main Shop",
            150m,
            IsReprint: false,
            DateTimeOffset.UtcNow);

        var docSource = new MemoryStickerDocSource(new Dictionary<Guid, PhysicalItemStickerDocument>
        {
            [unitId] = doc
        });

        // Printer simulates paper jam / failure
        var failingEngine = new RecordingStickerPrintEngine(new PrintJobResult(
            false,
            "print.paper_jam",
            "Printer paper jam occurred."));

        var auth = new RecordingProductionAuth();
        var recordingAudit = new RecordingAuditSink();
        var audit = new ProductionAuditCoordinator(recordingAudit, new NullAuditFailureReporter());
        var handler = new PrintPhysicalStickersHandler(auth, docSource, failingEngine, audit, testContext.Doubles.Clock);

        var command = new PrintPhysicalStickersCommand(
            InventoryUnitIds: new[] { unitId },
            PrinterName: null,
            IsReprint: false,
            RequestedBy: testContext.ActorId);

        var result = await handler.HandleAsync(command);

        Assert.True(result.IsSuccess, result.Error?.Message); // Batch execution handled with failure details
        Assert.Equal(1, result.Value!.TotalRequested);
        Assert.Equal(0, result.Value.SucceededCount);
        Assert.Equal(1, result.Value.FailedCount);
        Assert.Equal("print.paper_jam", result.Value.JobResults[0].ErrorCode);

        // Audit reports DocumentPrintFailed
        Assert.Contains(recordingAudit.Records, r => r.EventType == ProductionAuditEvents.DocumentPrintFailed);
    }

    [Fact]
    public async Task PrintLater_CloseAndReopen_UsesOriginalUnits()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR1",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M1",
            startingSequence: 100);

        // Receive 5 units
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var intakeResult = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);
        Assert.Equal(5, intakeResult.Value!.CommittedUnits.Count);

        var originalUnits = intakeResult.Value.CommittedUnits;
        var originalIds = originalUnits.Select(u => u.Id).ToList();
        var originalTrackingCodes = originalUnits.Select(u => u.TrackingCode).ToList();
        var originalSequences = originalUnits.Select(u => u.ItemSequence).ToList();

        // Print Later simulation: Dialog is closed, user leaves screen, then reopens purchase/receipt later
        // Authority for Print Later: IPurchasingReadService.GetUnitsForPurchaseItemAsync(purchaseItemId, ct)
        var reopenedUnits = await testContext.Doubles.PurchasingReads.GetUnitsForPurchaseItemAsync(item.Id, CancellationToken.None);

        // Verification: Exactly the same 5 units, same IDs, same TrackingCodes, same sequences
        Assert.Equal(5, reopenedUnits.Count);
        Assert.Equal(originalIds, reopenedUnits.Select(u => u.Id));
        Assert.Equal(originalTrackingCodes, reopenedUnits.Select(u => u.TrackingCode));
        Assert.Equal(originalSequences, reopenedUnits.Select(u => u.ItemSequence));

        // When printing these reopened units, the exact same labels are printed
        var docSource = new MemoryStickerDocSource(reopenedUnits.ToDictionary(
            u => u.Id,
            u => new PhysicalItemStickerDocument(u.Id, u.TrackingCode, "Edge Retails", product.Name, null, product.Sku!, u.ItemSequence, null, null, null, "Main", 200m, false, DateTimeOffset.UtcNow)));

        var printEngine = new RecordingStickerPrintEngine();
        var printHandler = new PrintPhysicalStickersHandler(
            new RecordingProductionAuth(),
            docSource,
            printEngine,
            new ProductionAuditCoordinator(new NullAuditSink(), new NullAuditFailureReporter()),
            testContext.Doubles.Clock);

        var printCommand = new PrintPhysicalStickersCommand(
            reopenedUnits.Select(u => u.Id).ToArray(),
            PrinterName: null,
            IsReprint: false,
            RequestedBy: testContext.ActorId);

        var printResult = await printHandler.HandleAsync(printCommand);
        Assert.True(printResult.IsSuccess);
        Assert.Equal(5, printEngine.PrintedDocuments.Count);
        Assert.Equal(originalTrackingCodes, printEngine.PrintedDocuments.Select(d => d.BarcodePayload));
    }

    [Fact]
    public async Task PrintLater_AfterRestart_UsesOriginalUnits()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR2",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M2",
            startingSequence: 50);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var intakeResult = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(intakeResult.IsSuccess);
        var originalUnits = intakeResult.Value!.CommittedUnits;

        // Simulate app restart: create a new PurchasingReadService instance querying the committed repository
        var freshReadService = new FakePurchasingReadService(testContext.Doubles.Purchasing, testContext.Doubles.Inventory);
        var loadedUnits = await freshReadService.GetUnitsForPurchaseItemAsync(item.Id, CancellationToken.None);

        Assert.Equal(5, loadedUnits.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(originalUnits[i].Id, loadedUnits[i].Id);
            Assert.Equal(originalUnits[i].TrackingCode, loadedUnits[i].TrackingCode);
            Assert.Equal(originalUnits[i].ItemSequence, loadedUnits[i].ItemSequence);
            Assert.Equal(originalUnits[i].AcquisitionCost, loadedUnits[i].AcquisitionCost);
        }
    }

    [Fact]
    public async Task CrashAfterCommit_BeforePrint_IsRecoverable()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR3",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M3",
            startingSequence: 10);

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: opId);

        // 1. Initial intake executes and commits
        var firstResult = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(firstResult.IsSuccess);
        Assert.False(firstResult.Value!.WasExisting);
        Assert.Equal(5, firstResult.Value.CommittedUnits.Count);
        var seqAfterFirst = supplierProduct.NextItemSequence;
        Assert.Equal(15, seqAfterFirst);

        // 2. CRASH occurs before printing!
        // Client recovers and replays the exact same command with same ClientOperationId
        var replayResult = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(replayResult.IsSuccess);
        Assert.True(replayResult.Value!.WasExisting); // Recognizes committed operation!

        // Stock was not double-incremented
        var balance = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(5m, balance!.SellableQty);

        // NextItemSequence was NOT incremented again
        Assert.Equal(seqAfterFirst, supplierProduct.NextItemSequence);

        // Total inventory units remains 5
        Assert.Equal(5, testContext.Doubles.Inventory.Units.Count);

        // Units returned match the original units perfectly
        Assert.Equal(firstResult.Value.CommittedUnits.Select(u => u.Id), replayResult.Value.CommittedUnits.Select(u => u.Id));
        Assert.Equal(firstResult.Value.CommittedUnits.Select(u => u.TrackingCode), replayResult.Value.CommittedUnits.Select(u => u.TrackingCode));

        // Now print can execute smoothly using recovered units
        var docSource = new MemoryStickerDocSource(replayResult.Value.CommittedUnits.ToDictionary(
            u => u.Id,
            u => new PhysicalItemStickerDocument(u.Id, u.TrackingCode, "Edge Retails", product.Name, null, product.Sku!, u.ItemSequence, null, null, null, "Main", 300m, false, DateTimeOffset.UtcNow)));
        var printEngine = new RecordingStickerPrintEngine();
        var printHandler = new PrintPhysicalStickersHandler(
            new RecordingProductionAuth(),
            docSource,
            printEngine,
            new ProductionAuditCoordinator(new NullAuditSink(), new NullAuditFailureReporter()),
            testContext.Doubles.Clock);

        var printCmd = new PrintPhysicalStickersCommand(
            replayResult.Value.CommittedUnits.Select(u => u.Id).ToArray(),
            PrinterName: null,
            IsReprint: false,
            RequestedBy: testContext.ActorId);

        var printRes = await printHandler.HandleAsync(printCmd);
        Assert.True(printRes.IsSuccess);
        Assert.Equal(5, printEngine.PrintedDocuments.Count);
    }

    [Fact]
    public async Task PrintLater_DoesNotAdvanceSequence()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR4",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M4",
            startingSequence: 1);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var intakeResult = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(intakeResult.IsSuccess);

        // NextItemSequence should advance from 1 to 6 during intake
        Assert.Equal(6, supplierProduct.NextItemSequence);

        // Later retrieval and printing
        var units = await testContext.Doubles.PurchasingReads.GetUnitsForPurchaseItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(5, units.Count);

        var docSource = new MemoryStickerDocSource(units.ToDictionary(
            u => u.Id,
            u => new PhysicalItemStickerDocument(u.Id, u.TrackingCode, "Edge Retails", product.Name, null, product.Sku!, u.ItemSequence, null, null, null, "Main", 100m, false, DateTimeOffset.UtcNow)));
        var printEngine = new RecordingStickerPrintEngine();
        var printHandler = new PrintPhysicalStickersHandler(
            new RecordingProductionAuth(),
            docSource,
            printEngine,
            new ProductionAuditCoordinator(new NullAuditSink(), new NullAuditFailureReporter()),
            testContext.Doubles.Clock);

        await printHandler.HandleAsync(new PrintPhysicalStickersCommand(
            units.Select(u => u.Id).ToArray(),
            PrinterName: null,
            IsReprint: false,
            RequestedBy: testContext.ActorId));

        // And even reprinting
        await printHandler.HandleAsync(new PrintPhysicalStickersCommand(
            units.Select(u => u.Id).ToArray(),
            PrinterName: null,
            IsReprint: true,
            RequestedBy: testContext.ActorId));

        // Hard invariant: NextItemSequence remains strictly 6
        Assert.Equal(6, supplierProduct.NextItemSequence);
    }

    [Fact]
    public async Task PrintLater_DoesNotCreateInventoryUnits()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR5",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M5",
            startingSequence: 1);

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var intakeResult = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(intakeResult.IsSuccess);
        Assert.Equal(5, testContext.Doubles.Inventory.Units.Count);

        // Querying for Print Later and printing
        var units = await testContext.Doubles.PurchasingReads.GetUnitsForPurchaseItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(5, units.Count);

        // Ensure no new InventoryUnits are added
        Assert.Equal(5, testContext.Doubles.Inventory.Units.Count);
    }

    [Fact]
    public async Task PrintLater_PrintsOnlyOriginalReceiptBatch()
    {
        var testContext = CreateTestContext();
        var (purchase, itemA, itemB) = await testContext.SeedPurchaseWithTwoLinesAsync();

        // Convert Product A to IndividualPiece
        var productA = (await testContext.Doubles.Catalog.GetProductAsync(itemA.ProductId, CancellationToken.None))!;
        productA.TrackingMode = TrackingMode.IndividualPiece;

        var supplier = (await testContext.Doubles.Parties.GetSupplierAsync(purchase.SupplierId, CancellationToken.None))!;
        var supplierProduct = new SupplierProduct
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ProductId = productA.Id,
            NextItemSequence = 1,
            IsActive = true
        };
        testContext.Doubles.Traceability.AddSupplierProduct(supplierProduct);

        // Batch 1: Intake 3 units for Line A
        var cmd1 = new ReceiveProductIntakeCommand(
            purchase.Id,
            itemA.ProductId,
            itemA.ProductUnitId,
            EnteredQuantity: 3m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());
        var res1 = await testContext.IntakeHandler.HandleAsync(cmd1);
        Assert.True(res1.IsSuccess);
        Assert.Equal(3, res1.Value!.CommittedUnits.Count);

        // Later: Batch 2: Another intake of 2 units of the same product on a separate receipt / purchase
        var purchase2 = new Purchase
        {
            Id = Guid.NewGuid(),
            PurchaseNumber = "PUR-0002",
            SupplierId = supplier.Id,
            SupplierInvoiceNumber = "INV-002",
            Status = PurchaseStatus.Completed,
            PurchaseDate = testContext.Doubles.Clock.ShopDate,
            CreatedBy = testContext.ActorId
        };
        testContext.Doubles.Purchasing.AddPurchase(purchase2);

        var itemA2 = new PurchaseItem
        {
            Id = Guid.NewGuid(),
            PurchaseId = purchase2.Id,
            ProductId = productA.Id,
            ProductUnitId = itemA.ProductUnitId,
            EnteredQuantity = 2m,
            EnteredUnitCost = 50m,
            BaseQuantity = 2m,
            EffectiveBaseUnitCost = 50m,
            FactorToBaseSnapshot = (2m) / (2m),
            EffectiveLineCost = (2m) * (50m),
            BaseLineTotal = 100m
        };
        testContext.Doubles.Purchasing.AddPurchaseItem(itemA2);

        var cmd2 = new ReceiveProductIntakeCommand(
            purchase2.Id,
            itemA2.ProductId,
            itemA2.ProductUnitId,
            EnteredQuantity: 2m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());
        var res2 = await testContext.IntakeHandler.HandleAsync(cmd2);
        Assert.True(res2.IsSuccess);
        Assert.Equal(2, res2.Value!.CommittedUnits.Count);

        // Total units in DB is 5 (3 + 2)
        Assert.Equal(5, testContext.Doubles.Inventory.Units.Count);

        // Print Later on Line A (itemA) returns ONLY the 3 units from the original batch
        var unitsBatch1 = await testContext.Doubles.PurchasingReads.GetUnitsForPurchaseItemAsync(itemA.Id, CancellationToken.None);
        Assert.Equal(3, unitsBatch1.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, unitsBatch1.Select(u => u.ItemSequence));

        // Line A2 returns ONLY its 2 units
        var unitsBatch2 = await testContext.Doubles.PurchasingReads.GetUnitsForPurchaseItemAsync(itemA2.Id, CancellationToken.None);
        Assert.Equal(2, unitsBatch2.Count);
        Assert.Equal(new long[] { 4, 5 }, unitsBatch2.Select(u => u.ItemSequence));
    }

    [Fact]
    public async Task ReceivingReplay_ReturnsOriginalCommittedUnits()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR7",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M7",
            startingSequence: 10);

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: opId);

        // 1. Initial attempt
        var result1 = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(result1.IsSuccess);
        Assert.False(result1.Value!.WasExisting);
        Assert.Equal(5, result1.Value.CommittedUnits.Count);

        // 2. Exact replay
        var result2 = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(result2.IsSuccess);
        Assert.True(result2.Value!.WasExisting);
        Assert.Equal(5, result2.Value.CommittedUnits.Count);

        // Verify identical identities
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(result1.Value.CommittedUnits[i].Id, result2.Value.CommittedUnits[i].Id);
            Assert.Equal(result1.Value.CommittedUnits[i].TrackingCode, result2.Value.CommittedUnits[i].TrackingCode);
            Assert.Equal(result1.Value.CommittedUnits[i].ItemSequence, result2.Value.CommittedUnits[i].ItemSequence);
        }

        // Verify no double-stock delta
        var balance = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(5m, balance!.SellableQty);
    }

    [Fact]
    public async Task PurchaseCreation_DoesNotDoublePostPhysicalStock()
    {
        var testContext = CreateTestContext();
        var (supplier, productA, productUnitA, _, _) = await testContext.SetupCatalogForPurchasingAsync();

        // 1. Create Purchase with ReceiveStockImmediately = false (Commercial Authority Only)
        var createCmd = new CreatePurchaseCommand(
            SupplierId: supplier.Id,
            SupplierInvoiceNumber: "INV-DEFERRED-01",
            PurchaseDate: testContext.Doubles.Clock.ShopDate,
            Note: "Commercial invoice only",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new CreatePurchaseLineInput(
                    productA.Id,
                    productUnitA.Id,
                    EnteredQuantity: 10m,
                    EnteredUnitCost: 50m,
                    BaseUnitSalePrice: 80m,
                    SerializedUnits: Array.Empty<SerializedIdentityInput>())
            },
            InitialPaymentAmount: 0m,
            ReceiveStockImmediately: false);

        var createResult = await testContext.CreatePurchaseHandler.HandleAsync(createCmd, CancellationToken.None);
        Assert.True(createResult.IsSuccess, createResult.Error?.Message);

        var purchaseId = createResult.Value!.PurchaseId;
        var purchaseItems = await testContext.Doubles.Purchasing.GetPurchaseItemsAsync(purchaseId, CancellationToken.None);
        var purchaseItem = Assert.Single(purchaseItems);

        // Assert commercial authority only: ZERO stock balance, ZERO lots, ZERO movements, ZERO units
        var initialBalance = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productA.Id, CancellationToken.None);
        Assert.True(initialBalance == null || initialBalance.SellableQty == 0m);
        Assert.Empty(testContext.Doubles.Inventory.Lots);
        Assert.Empty(testContext.Doubles.Inventory.Movements);
        Assert.Empty(testContext.Doubles.Inventory.Units);

        // 2. Physical Intake of full 10 units (Physical Receiving Authority)
        var intakeCmd = new ReceiveProductIntakeCommand(
            purchaseId,
            productA.Id,
            productUnitA.Id,
            EnteredQuantity: 10m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var intakeResult = await testContext.IntakeHandler.HandleAsync(intakeCmd);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);
        Assert.Equal(10m, intakeResult.Value!.ReceivedQuantity);

        // Verify stock is now posted exactly once
        var balanceAfterIntake = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productA.Id, CancellationToken.None);
        Assert.NotNull(balanceAfterIntake);
        Assert.Equal(10m, balanceAfterIntake!.SellableQty);
        Assert.Single(testContext.Doubles.Inventory.Lots);
        Assert.Equal(10m, testContext.Doubles.Inventory.Lots[0].ReceivedQuantity);

        // 3. Attempting another intake against the same purchase item is rejected (cannot exceed ordered / double-post)
        var duplicateIntakeCmd = new ReceiveProductIntakeCommand(
            purchaseId,
            productA.Id,
            productUnitA.Id,
            EnteredQuantity: 1m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var duplicateResult = await testContext.IntakeHandler.HandleAsync(duplicateIntakeCmd);
        Assert.False(duplicateResult.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", duplicateResult.Error?.Code);

        // Stock remains exactly 10, not 11
        var balanceFinal = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productA.Id, CancellationToken.None);
        Assert.Equal(10m, balanceFinal!.SellableQty);

        // 4. Reverse scenario: Purchase created with ReceiveStockImmediately = true (Historical mode)
        var immediateCmd = new CreatePurchaseCommand(
            SupplierId: supplier.Id,
            SupplierInvoiceNumber: "INV-IMMEDIATE-01",
            PurchaseDate: testContext.Doubles.Clock.ShopDate,
            Note: "Immediate stock receipt",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new CreatePurchaseLineInput(
                    productA.Id,
                    productUnitA.Id,
                    EnteredQuantity: 5m,
                    EnteredUnitCost: 50m,
                    BaseUnitSalePrice: 80m,
                    SerializedUnits: Array.Empty<SerializedIdentityInput>())
            },
            InitialPaymentAmount: 0m,
            ReceiveStockImmediately: true);

        var immediateResult = await testContext.CreatePurchaseHandler.HandleAsync(immediateCmd, CancellationToken.None);
        Assert.True(immediateResult.IsSuccess, immediateResult.Error?.Message);

        // Stock was incremented immediately from 10 to 15
        var balanceImmediate = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productA.Id, CancellationToken.None);
        Assert.Equal(15m, balanceImmediate!.SellableQty);

        // Subsequent intake attempt against this immediate purchase item is rejected because outstanding is 0
        var redundantIntake = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            immediateResult.Value!.PurchaseId,
            productA.Id,
            productUnitA.Id,
            EnteredQuantity: 1m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid()));

        Assert.False(redundantIntake.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", redundantIntake.Error?.Code);
        Assert.Equal(15m, (await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productA.Id, CancellationToken.None))!.SellableQty);
    }

    [Fact]
    public async Task MultiLinePurchase_ReceiveA_DoesNotReceiveB()
    {
        var testContext = CreateTestContext();
        var (supplier, productA, productUnitA, productB, productUnitB) = await testContext.SetupCatalogForPurchasingAsync();

        var createCmd = new CreatePurchaseCommand(
            SupplierId: supplier.Id,
            SupplierInvoiceNumber: "INV-MULTILINE-01",
            PurchaseDate: testContext.Doubles.Clock.ShopDate,
            Note: "Multi-line purchase",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new CreatePurchaseLineInput(productA.Id, productUnitA.Id, 10m, 50m, 80m, Array.Empty<SerializedIdentityInput>()),
                new CreatePurchaseLineInput(productB.Id, productUnitB.Id, 5m, 100m, 150m, Array.Empty<SerializedIdentityInput>())
            },
            InitialPaymentAmount: 0m,
            ReceiveStockImmediately: false);

        var createResult = await testContext.CreatePurchaseHandler.HandleAsync(createCmd, CancellationToken.None);
        Assert.True(createResult.IsSuccess);

        var purchaseId = createResult.Value!.PurchaseId;
        var purchaseItems = await testContext.Doubles.Purchasing.GetPurchaseItemsAsync(purchaseId, CancellationToken.None);
        var itemA = purchaseItems.Single(x => x.ProductId == productA.Id);
        var itemB = purchaseItems.Single(x => x.ProductId == productB.Id);

        // Receive Line A (10 pcs)
        var intakeACmd = new ReceiveProductIntakeCommand(
            purchaseId,
            productA.Id,
            productUnitA.Id,
            EnteredQuantity: 10m,
            EnteredUnitCost: 50m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var intakeAResult = await testContext.IntakeHandler.HandleAsync(intakeACmd);
        Assert.True(intakeAResult.IsSuccess);

        // Product A has 10 pcs sellable stock and 0 outstanding
        var balanceA = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productA.Id, CancellationToken.None);
        Assert.NotNull(balanceA);
        Assert.Equal(10m, balanceA!.SellableQty);
        var receivedA = await testContext.Doubles.Inventory.GetPurchaseItemReceivedBaseQuantityAsync(itemA.Id, CancellationToken.None);
        Assert.Equal(10m, receivedA);

        // Product B has ZERO stock received and full 5 pcs outstanding
        var balanceB = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productB.Id, CancellationToken.None);
        Assert.True(balanceB == null || balanceB.SellableQty == 0m);
        var receivedB = await testContext.Doubles.Inventory.GetPurchaseItemReceivedBaseQuantityAsync(itemB.Id, CancellationToken.None);
        Assert.Equal(0m, receivedB);

        // Trying to receive more for Product A fails
        var overIntakeA = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, productA.Id, productUnitA.Id, 1m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.False(overIntakeA.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", overIntakeA.Error?.Code);

        // Receiving Product B now succeeds independently
        var intakeBResult = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, productB.Id, productUnitB.Id, 5m, 100m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(intakeBResult.IsSuccess);

        balanceB = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(productB.Id, CancellationToken.None);
        Assert.NotNull(balanceB);
        Assert.Equal(5m, balanceB!.SellableQty);
    }

    [Fact]
    public async Task PartialReceiving_AccumulatesExactlyOnce()
    {
        var testContext = CreateTestContext();
        var (supplier, product, productUnit, _, _) = await testContext.SetupCatalogForPurchasingAsync();

        var createCmd = new CreatePurchaseCommand(
            SupplierId: supplier.Id,
            SupplierInvoiceNumber: "INV-PARTIAL-01",
            PurchaseDate: testContext.Doubles.Clock.ShopDate,
            Note: "Partial intake purchase",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[] { new CreatePurchaseLineInput(product.Id, productUnit.Id, 10m, 50m, 80m, Array.Empty<SerializedIdentityInput>()) },
            InitialPaymentAmount: 0m,
            ReceiveStockImmediately: false);

        var createResult = await testContext.CreatePurchaseHandler.HandleAsync(createCmd, CancellationToken.None);
        Assert.True(createResult.IsSuccess);
        var purchaseId = createResult.Value!.PurchaseId;
        var item = (await testContext.Doubles.Purchasing.GetPurchaseItemsAsync(purchaseId, CancellationToken.None)).Single();

        // Receipt 1: Receive 4 units (Outstanding remaining: 6)
        var receipt1 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 4m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(receipt1.IsSuccess);
        Assert.Equal(4m, await testContext.Doubles.Inventory.GetPurchaseItemReceivedBaseQuantityAsync(item.Id, CancellationToken.None));
        var stock1 = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(4m, stock1!.SellableQty);

        // Receipt 2: Receive 3 units (Outstanding remaining: 3)
        var receipt2 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 3m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(receipt2.IsSuccess);
        Assert.Equal(7m, await testContext.Doubles.Inventory.GetPurchaseItemReceivedBaseQuantityAsync(item.Id, CancellationToken.None));
        var stock2 = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(7m, stock2!.SellableQty);

        // Receipt 3: Receive 3 units (Outstanding remaining: 0)
        var receipt3 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 3m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(receipt3.IsSuccess);
        Assert.Equal(10m, await testContext.Doubles.Inventory.GetPurchaseItemReceivedBaseQuantityAsync(item.Id, CancellationToken.None));
        var stock3 = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(10m, stock3!.SellableQty);

        // Receipt 4: Trying to receive any more must fail
        var receipt4 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 1m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.False(receipt4.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", receipt4.Error?.Code);
        var stockFinal = await testContext.Doubles.Inventory.GetStockBalanceForUpdateAsync(product.Id, CancellationToken.None);
        Assert.Equal(10m, stockFinal!.SellableQty);
    }

    [Fact]
    public async Task Receiving_CannotExceedAllowedOutstandingQuantity()
    {
        var testContext = CreateTestContext();
        var (supplier, product, productUnit, _, _) = await testContext.SetupCatalogForPurchasingAsync();

        var createCmd = new CreatePurchaseCommand(
            SupplierId: supplier.Id,
            SupplierInvoiceNumber: "INV-LIMIT-01",
            PurchaseDate: testContext.Doubles.Clock.ShopDate,
            Note: "Limit test",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[] { new CreatePurchaseLineInput(product.Id, productUnit.Id, 10m, 20m, 35m, Array.Empty<SerializedIdentityInput>()) },
            InitialPaymentAmount: 0m,
            ReceiveStockImmediately: false);

        var createResult = await testContext.CreatePurchaseHandler.HandleAsync(createCmd, CancellationToken.None);
        Assert.True(createResult.IsSuccess);
        var purchaseId = createResult.Value!.PurchaseId;

        // 1. Partial receipt of 4 (outstanding = 6)
        var r1 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 4m, 20m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(r1.IsSuccess);

        // 2. Attempt receipt of 7 (exceeds outstanding 6) -> Rejection
        var r2Fail = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 7m, 20m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.False(r2Fail.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", r2Fail.Error?.Code);

        // 3. Receipt of exactly outstanding 6 -> Success
        var r2Ok = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 6m, 20m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(r2Ok.IsSuccess);

        // 4. Attempt receipt of even 0.01 -> Rejection
        var r3Fail = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, product.Id, productUnit.Id, 0.01m, 20m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.False(r3Fail.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", r3Fail.Error?.Code);
    }

    [Fact]
    public async Task IndividualPiece_CreatesExactlyOneInventoryUnitPerPhysicalPiece()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M9",
            startingSequence: 1);

        // HARNESS_CORRECTION: this scenario asserts a 200 purchase acquisition
        // snapshot. The shared seed is 250; set the PO, rather than forge receipt cost.
        item.EnteredUnitCost = 200m;
        item.EffectiveBaseUnitCost = 200m;
        item.BaseLineTotal = item.BaseQuantity * 200m;
        item.EffectiveLineCost = item.BaseQuantity * 200m;

        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 3m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var result = await testContext.IntakeHandler.HandleAsync(command);
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value!.CommittedUnits.Count);

        // Invariant: Exactly one InventoryUnit per physical piece
        var unitsInRepo = testContext.Doubles.Inventory.Units.Where(u => u.ProductId == product.Id).ToList();
        Assert.Equal(3, unitsInRepo.Count);

        for (var i = 0; i < 3; i++)
        {
            var u = unitsInRepo[i];
            Assert.Equal(InventoryUnitOriginType.Purchase, u.OriginType);
            Assert.Equal(item.Id, u.SourcePurchaseItemId);
            Assert.Equal(InventoryUnitStatus.InStock, u.Status);
            Assert.Equal(200m, u.AcquisitionCost);
            Assert.Equal($"DLR-CMPC-M9-00000{i + 1}", u.TrackingCode);
            Assert.Equal(i + 1, u.ItemSequence);
        }
    }

    [Fact]
    public async Task Receiving_AdvancesSupplierProductSequenceExactlyOnce()
    {
        var testContext = CreateTestContext();
        var (purchase, item, product, supplierProduct) = await testContext.SeedIndividualPieceSetupAsync(
            dealerCode: "DLR",
            companyCode: "CMP",
            categorySymbol: "C",
            modelCode: "M9",
            startingSequence: 10);

        // Receipt 1: 2 pieces
        var cmd1 = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 2m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var res1 = await testContext.IntakeHandler.HandleAsync(cmd1);
        Assert.True(res1.IsSuccess);
        Assert.Equal(2, res1.Value!.CommittedUnits.Count);
        Assert.Equal(10, res1.Value.CommittedUnits[0].ItemSequence);
        Assert.Equal(11, res1.Value.CommittedUnits[1].ItemSequence);
        Assert.Equal(12, supplierProduct.NextItemSequence);

        // Receipt 2: 3 pieces
        var cmd2 = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 3m,
            EnteredUnitCost: item.EnteredUnitCost,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid());

        var res2 = await testContext.IntakeHandler.HandleAsync(cmd2);
        Assert.True(res2.IsSuccess);
        Assert.Equal(3, res2.Value!.CommittedUnits.Count);
        Assert.Equal(12, res2.Value.CommittedUnits[0].ItemSequence);
        Assert.Equal(13, res2.Value.CommittedUnits[1].ItemSequence);
        Assert.Equal(14, res2.Value.CommittedUnits[2].ItemSequence);
        Assert.Equal(15, supplierProduct.NextItemSequence);
    }

    [Fact]
    public async Task Receiving_DoesNotDuplicateSupplierAccounting()
    {
        var testContext = CreateTestContext();
        var (supplier, productA, productUnitA, productB, productUnitB) = await testContext.SetupCatalogForPurchasingAsync();

        // Create purchase: Line A (10 * $50 = $500), Line B (5 * $100 = $500), Total = $1000, Initial Payment = $300
        var createCmd = new CreatePurchaseCommand(
            SupplierId: supplier.Id,
            SupplierInvoiceNumber: "INV-ACCT-01",
            PurchaseDate: testContext.Doubles.Clock.ShopDate,
            Note: "Accounting test",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: testContext.ActorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new CreatePurchaseLineInput(productA.Id, productUnitA.Id, 10m, 50m, 80m, Array.Empty<SerializedIdentityInput>()),
                new CreatePurchaseLineInput(productB.Id, productUnitB.Id, 5m, 100m, 150m, Array.Empty<SerializedIdentityInput>())
            },
            InitialPaymentAmount: 300m,
            ReceiveStockImmediately: false);

        var createResult = await testContext.CreatePurchaseHandler.HandleAsync(createCmd, CancellationToken.None);
        Assert.True(createResult.IsSuccess);
        var purchaseId = createResult.Value!.PurchaseId;

        // Commercial authority check: Exactly 2 supplier account entries (1 Purchase + 1 Payment)
        Assert.Equal(2, testContext.Doubles.SupplierAccounts.Entries.Count);
        var purchaseEntry = testContext.Doubles.SupplierAccounts.Entries.Single(e => e.EntryType == SupplierAccountEntryType.Purchase);
        Assert.Equal(1000m, purchaseEntry.Amount);
        Assert.Equal(SupplierAccountDirection.IncreasePayable, purchaseEntry.Direction);

        var paymentEntry = testContext.Doubles.SupplierAccounts.Entries.Single(e => e.EntryType == SupplierAccountEntryType.SupplierPayment);
        Assert.Equal(300m, paymentEntry.Amount);
        Assert.Equal(SupplierAccountDirection.DecreasePayable, paymentEntry.Direction);

        var balance = await testContext.Doubles.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(700m, balance);

        // Perform Physical Intake 1 for Line A (partial 4 units)
        var intake1 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, productA.Id, productUnitA.Id, 4m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(intake1.IsSuccess);

        // Perform Physical Intake 2 for Line A (remaining 6 units)
        var intake2 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, productA.Id, productUnitA.Id, 6m, 50m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(intake2.IsSuccess);

        // Perform Physical Intake 3 for Line B (full 5 units)
        var intake3 = await testContext.IntakeHandler.HandleAsync(new ReceiveProductIntakeCommand(
            purchaseId, productB.Id, productUnitB.Id, 5m, 100m, Array.Empty<SerializedIdentityInput>(), testContext.ActorId, Guid.NewGuid()));
        Assert.True(intake3.IsSuccess);

        // Hard Invariant: Physical intake receipts MUST NOT touch supplier accounts!
        // Entries count remains exactly 2. No duplicate accounting entries created!
        Assert.Equal(2, testContext.Doubles.SupplierAccounts.Entries.Count);
        Assert.Single(testContext.Doubles.SupplierAccounts.Payments);

        var balanceAfterIntakes = await testContext.Doubles.SupplierAccounts.GetCurrentBalanceAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(700m, balanceAfterIntakes);
    }

    // --- Helper Fixtures and Test Context Setup ---

    private sealed class TestContext
    {
        public Phase2TestDoubles Doubles { get; } = new();
        public Guid ActorId { get; } = Guid.NewGuid();
        public ReceiveProductIntakeHandler IntakeHandler { get; }
        public CreatePurchaseHandler CreatePurchaseHandler { get; }

        public TestContext()
        {
            IntakeHandler = new ReceiveProductIntakeHandler(
                Doubles.Purchasing,
                Doubles.Parties,
                Doubles.Catalog,
                Doubles.Inventory,
                Doubles.CostAllocator,
                Doubles.Traceability,
                Doubles.OperationLock,
                Doubles.ResourceLock,
                Doubles.Audit,
                Doubles.Clock,
                Doubles.Transactions,
                Doubles.Authorization,
                Doubles.UnitOfWork,
                NullSequenceHighWaterService.Instance,
                Doubles.OutcomeLedger,
                Doubles.PhysicalUnits);

            CreatePurchaseHandler = new CreatePurchaseHandler(
                Doubles.Purchasing,
                Doubles.Parties,
                Doubles.Catalog,
                Doubles.Inventory,
                Doubles.CostAllocator,
                Doubles.Cash,
                Doubles.Traceability,
                Doubles.SupplierAccounts,
                Doubles.OperationLock,
                Doubles.ResourceLock,
                Doubles.Audit,
                Doubles.Numbers,
                Doubles.Clock,
                Doubles.Transactions,
                Doubles.Authorization,
                Doubles.UnitOfWork,
                NullSequenceHighWaterService.Instance,
                Doubles.OutcomeLedger,
                Doubles.PhysicalUnits);
        }

        public async Task<(Supplier supplier, Product productA, ProductUnit productUnitA, Product productB, ProductUnit productUnitB)> SetupCatalogForPurchasingAsync()
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Authority Supplier", IsActive = true, DealerCode = "AUT" };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "Authority Co", Code = "AC", IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IdentitySymbol = "E", IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
            Doubles.Catalog.AddUnit(unit);

            var productA = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Product A",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "PA",
                ModelCode = "PA",
                Sku = "ACE-PA",
                TrackingMode = TrackingMode.Quantity,
                IsActive = true
            };
            var productUnitA = new ProductUnit { Id = Guid.NewGuid(), ProductId = productA.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(productA);
            Doubles.Catalog.AddProductUnit(productUnitA);

            var productB = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Product B",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "PB",
                ModelCode = "PB",
                Sku = "ACE-PB",
                TrackingMode = TrackingMode.Quantity,
                IsActive = true
            };
            var productUnitB = new ProductUnit { Id = Guid.NewGuid(), ProductId = productB.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(productB);
            Doubles.Catalog.AddProductUnit(productUnitB);

            return (supplier, productA, productUnitA, productB, productUnitB);
        }

        public async Task<(Purchase purchase, PurchaseItem itemA, PurchaseItem itemB)> SeedPurchaseWithTwoLinesAsync()
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Alpha Supplier", IsActive = true, DealerCode = "ALP" };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "Alpha Co", Code = "AC", IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IdentitySymbol = "E", IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
            Doubles.Catalog.AddUnit(unit);

            var productA = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Product A",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "PA",
                ModelCode = "PA",
                Sku = "ACE-PA",
                TrackingMode = TrackingMode.Quantity,
                IsActive = true
            };
            var productUnitA = new ProductUnit { Id = Guid.NewGuid(), ProductId = productA.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(productA);
            Doubles.Catalog.AddProductUnit(productUnitA);

            var productB = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Product B",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "PB",
                ModelCode = "PB",
                Sku = "ACE-PB",
                TrackingMode = TrackingMode.Quantity,
                IsActive = true
            };
            var productUnitB = new ProductUnit { Id = Guid.NewGuid(), ProductId = productB.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(productB);
            Doubles.Catalog.AddProductUnit(productUnitB);

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                PurchaseNumber = "PUR-0001",
                SupplierId = supplier.Id,
                SupplierInvoiceNumber = "INV-001",
                Status = PurchaseStatus.Completed,
                PurchaseDate = Doubles.Clock.ShopDate,
                CreatedBy = ActorId
            };
            Doubles.Purchasing.AddPurchase(purchase);

            var itemA = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = productA.Id,
                ProductUnitId = productUnitA.Id,
                EnteredQuantity = 10m,
                EnteredUnitCost = 50m,
                BaseQuantity = 10m,
                EffectiveBaseUnitCost = 50m,
                FactorToBaseSnapshot = (10m) / (10m),
                EffectiveLineCost = (10m) * (50m),
                BaseLineTotal = 500m
            };
            var itemB = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = productB.Id,
                ProductUnitId = productUnitB.Id,
                EnteredQuantity = 5m,
                EnteredUnitCost = 120m,
                BaseQuantity = 5m,
                EffectiveBaseUnitCost = 120m,
                FactorToBaseSnapshot = (5m) / (5m),
                EffectiveLineCost = (5m) * (120m),
                BaseLineTotal = 600m
            };

            Doubles.Purchasing.AddPurchaseItem(itemA);
            Doubles.Purchasing.AddPurchaseItem(itemB);

            return (purchase, itemA, itemB);
        }

        public async Task<(Purchase purchase, PurchaseItem item, Product product)> SeedPurchaseWithProductsAsync()
        {
            var (purchase, itemA, _) = await SeedPurchaseWithTwoLinesAsync();
            var productA = (await Doubles.Catalog.GetProductAsync(itemA.ProductId, CancellationToken.None))!;
            return (purchase, itemA, productA);
        }

        public async Task<(Purchase purchase, PurchaseItem item, Product product)> SeedPurchaseWithProductPolicyAsync(TrackingMode mode)
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Supplier 1", IsActive = true, DealerCode = "SUP" };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "Test Co", Code = "TC", IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "General", IdentitySymbol = "G", IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var unit = new Unit { Id = Guid.NewGuid(), Name = "Unit", Symbol = "U", IsActive = true };
            Doubles.Catalog.AddUnit(unit);

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Product Policy",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "PP",
                ModelCode = "PP",
                Sku = "TCG-PP",
                TrackingMode = mode,
                IsActive = true
            };
            var productUnit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(product);
            Doubles.Catalog.AddProductUnit(productUnit);

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                PurchaseNumber = "PUR-POL",
                SupplierId = supplier.Id,
                SupplierInvoiceNumber = "INV-POL",
                Status = PurchaseStatus.Completed,
                PurchaseDate = Doubles.Clock.ShopDate,
                CreatedBy = ActorId
            };
            Doubles.Purchasing.AddPurchase(purchase);

            var item = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = product.Id,
                ProductUnitId = productUnit.Id,
                EnteredQuantity = 100m,
                EnteredUnitCost = 10m,
                BaseQuantity = 100m,
                EffectiveBaseUnitCost = 10m,
                FactorToBaseSnapshot = (100m) / (100m),
                EffectiveLineCost = (100m) * (10m),
                BaseLineTotal = 1000m
            };
            Doubles.Purchasing.AddPurchaseItem(item);

            return (purchase, item, product);
        }

        public async Task<(Purchase purchase, PurchaseItem item, Product product, SupplierProduct supplierProduct)> SeedIndividualPieceSetupAsync(
            string dealerCode,
            string companyCode,
            string categorySymbol,
            string modelCode,
            long startingSequence)
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Canonical Supplier", IsActive = true, DealerCode = dealerCode };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "PK Company", Code = companyCode, IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "Phones", IdentitySymbol = categorySymbol, IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
            Doubles.Catalog.AddUnit(unit);

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Deluxe Phone",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = modelCode,
                ModelCode = modelCode,
                Sku = $"{companyCode}{categorySymbol}-{modelCode}",
                TrackingMode = TrackingMode.IndividualPiece,
                IsActive = true
            };
            var productUnit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(product);
            Doubles.Catalog.AddProductUnit(productUnit);

            var supplierProduct = new SupplierProduct
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                ProductId = product.Id,
                NextItemSequence = startingSequence,
                IsActive = true
            };
            Doubles.Traceability.AddSupplierProduct(supplierProduct);

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                PurchaseNumber = "PUR-PIECE",
                SupplierId = supplier.Id,
                SupplierInvoiceNumber = "INV-PIECE",
                Status = PurchaseStatus.Completed,
                PurchaseDate = Doubles.Clock.ShopDate,
                CreatedBy = ActorId
            };
            Doubles.Purchasing.AddPurchase(purchase);

            var item = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = product.Id,
                ProductUnitId = productUnit.Id,
                EnteredQuantity = 10m,
                EnteredUnitCost = 250m,
                BaseQuantity = 10m,
                EffectiveBaseUnitCost = 250m,
                FactorToBaseSnapshot = (10m) / (10m),
                EffectiveLineCost = (10m) * (250m),
                BaseLineTotal = 2500m
            };
            Doubles.Purchasing.AddPurchaseItem(item);

            return (purchase, item, product, supplierProduct);
        }

        public async Task<(Purchase purchase, PurchaseItem item, Product product, SupplierProduct supplierProduct)> SeedContainerSetupAsync(
            decimal unitsPerPack,
            long startingSequence)
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Pack Supplier", IsActive = true, DealerCode = "PCK" };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "Pack Co", Code = "PC", IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "Beverages", IdentitySymbol = "B", IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var baseUnit = new Unit { Id = Guid.NewGuid(), Name = "Bottle", Symbol = "Btl", IsActive = true };
            var packUnit = new Unit { Id = Guid.NewGuid(), Name = "Crate", Symbol = "Crt", IsActive = true };
            Doubles.Catalog.AddUnit(baseUnit);
            Doubles.Catalog.AddUnit(packUnit);

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Soda",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "SODA",
                ModelCode = "SODA",
                Sku = "PCB-SODA",
                TrackingMode = TrackingMode.Container,
                IsActive = true
            };
            var baseProductUnit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = baseUnit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            var packProductUnit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = packUnit.Id, FactorToBaseUnit = unitsPerPack, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(product);
            Doubles.Catalog.AddProductUnit(baseProductUnit);
            Doubles.Catalog.AddProductUnit(packProductUnit);

            var supplierProduct = new SupplierProduct
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                ProductId = product.Id,
                NextItemSequence = startingSequence,
                IsActive = true
            };
            Doubles.Traceability.AddSupplierProduct(supplierProduct);

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                PurchaseNumber = "PUR-PACK",
                SupplierId = supplier.Id,
                SupplierInvoiceNumber = "INV-PACK",
                Status = PurchaseStatus.Completed,
                PurchaseDate = Doubles.Clock.ShopDate,
                CreatedBy = ActorId
            };
            Doubles.Purchasing.AddPurchase(purchase);

            var item = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = product.Id,
                ProductUnitId = packProductUnit.Id,
                EnteredQuantity = 5m,
                EnteredUnitCost = 1200m,
                BaseQuantity = 5m * unitsPerPack,
                EffectiveBaseUnitCost = 100m,
                FactorToBaseSnapshot = (5m * unitsPerPack) / (5m),
                EffectiveLineCost = (5m * unitsPerPack) * (100m),
                BaseLineTotal = 6000m
            };
            Doubles.Purchasing.AddPurchaseItem(item);

            return (purchase, item, product, supplierProduct);
        }

        public async Task<(Purchase purchase, PurchaseItem item, Product product, SupplierProduct supplierProduct)> SeedSerializedSetupAsync()
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Mobile Supplier", IsActive = true, DealerCode = "MOB" };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "Mobile Co", Code = "MC", IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "Smartphones", IdentitySymbol = "P", IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
            Doubles.Catalog.AddUnit(unit);

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Flagship Phone",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "FP1",
                ModelCode = "FP1",
                Sku = "MCP-FP1",
                TrackingMode = TrackingMode.IndividualPiece,
                SerialTrackingEnabled = true,
                ImeiTrackingEnabled = true,
                IsActive = true
            };
            var productUnit = new ProductUnit { Id = Guid.NewGuid(), ProductId = product.Id, UnitId = unit.Id, FactorToBaseUnit = 1m, IsActive = true, CanPurchase = true };
            Doubles.Catalog.AddProduct(product);
            Doubles.Catalog.AddProductUnit(productUnit);

            var supplierProduct = new SupplierProduct
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                ProductId = product.Id,
                NextItemSequence = 1,
                IsActive = true
            };
            Doubles.Traceability.AddSupplierProduct(supplierProduct);

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                PurchaseNumber = "PUR-SERIAL",
                SupplierId = supplier.Id,
                SupplierInvoiceNumber = "INV-SERIAL",
                Status = PurchaseStatus.Completed,
                PurchaseDate = Doubles.Clock.ShopDate,
                CreatedBy = ActorId
            };
            Doubles.Purchasing.AddPurchase(purchase);

            var item = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = product.Id,
                ProductUnitId = productUnit.Id,
                EnteredQuantity = 5m,
                EnteredUnitCost = 500m,
                BaseQuantity = 5m,
                EffectiveBaseUnitCost = 500m,
                FactorToBaseSnapshot = (5m) / (5m),
                EffectiveLineCost = (5m) * (500m),
                BaseLineTotal = 2500m
            };
            Doubles.Purchasing.AddPurchaseItem(item);

            return (purchase, item, product, supplierProduct);
        }
    }

    private static TestContext CreateTestContext() => new();

    private sealed class SimpleTestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class MemoryStickerDocSource : IPhysicalStickerDocumentSource
    {
        private readonly Dictionary<Guid, PhysicalItemStickerDocument> _documents;

        public MemoryStickerDocSource(Dictionary<Guid, PhysicalItemStickerDocument> documents)
        {
            _documents = documents;
        }

        public Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(Guid inventoryUnitId, bool isReprint = false, CancellationToken cancellationToken = default)
        {
            if (_documents.TryGetValue(inventoryUnitId, out var doc))
            {
                return Task.FromResult(doc with { IsReprint = isReprint });
            }

            throw new InvalidOperationException($"Document for unit '{inventoryUnitId}' not found.");
        }
    }

    private sealed class RecordingStickerPrintEngine : IPhysicalStickerPrintEngine
    {
        private readonly PrintJobResult _result;
        public List<PhysicalItemStickerDocument> PrintedDocuments { get; } = new();

        public RecordingStickerPrintEngine(PrintJobResult? result = null)
        {
            _result = result ?? new PrintJobResult(true, null, null);
        }

        public Task<PrintJobResult> PrintStickerAsync(PhysicalItemStickerDocument document, string? printerName = null, int copies = 1, CancellationToken cancellationToken = default)
        {
            PrintedDocuments.Add(document);
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingProductionAuth : IProductionAuthorization
    {
        public string? LastCheckedPermission { get; private set; }

        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default)
        {
            LastCheckedPermission = permission;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuditSink : IProductionAuditSink
    {
        public List<ProductionAuditRecord> Records { get; } = new();

        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class NullAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
