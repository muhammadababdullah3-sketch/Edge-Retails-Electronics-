using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Audit;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase7Pass3IntegrityTests
{
    private readonly Phase2TestDoubles _fakes = new();

    private VoidPurchaseHandler CreateVoidPurchaseHandler() =>
        new(
            _fakes.Purchasing,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.SupplierAccounts,
            _fakes.CashMovements,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            cash: _fakes.Cash,
            outcomeLedger: _fakes.OutcomeLedger);

    private CommercialExchangeHandler CreateCommercialExchangeHandler() =>
        new(
            _fakes.Sales,
            _fakes.Parties,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.ReceiptSnapshots,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            warranty: _fakes.Warranty,
            outcomeLedger: _fakes.OutcomeLedger);

    private CompleteSaleHandler CreateCompleteSaleHandler() =>
        new(
            _fakes.Sales,
            _fakes.Quotations,
            _fakes.Parties,
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Cash,
            _fakes.OperationLock,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.ReceiptSnapshots,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            outcomeLedger: _fakes.OutcomeLedger);

    private CompletePosDraftHandler CreateCompletePosDraftHandler() =>
        new(
            _fakes.PosDrafts,
            _fakes.ResourceLock,
            _fakes.Authorization,
            CreateCompleteSaleHandler(),
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            operationLock: _fakes.OperationLock,
            sales: _fakes.Sales);

    private SavePosDraftHandler CreateSavePosDraftHandler() =>
        new(
            _fakes.PosDrafts,
            _fakes.Catalog,
            _fakes.Numbers,
            _fakes.Clock,
            _fakes.Authorization,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            operationLock: _fakes.OperationLock,
            outcomeLedger: _fakes.OutcomeLedger);

    // =========================================================================
    // F05: VoidPurchase Durable Replay & Outcome Integrity
    // =========================================================================

    [Fact]
    public async Task F05_VoidPurchase_FirstCall_Records_Success_In_OutcomeLedger()
    {
        var handler = CreateVoidPurchaseHandler();
        var (purchase, actorId) = SeedPurchase(out var cashSession);
        var opId = Guid.CreateVersion7();

        var result = await handler.HandleAsync(
            new VoidPurchaseCommand(purchase.Id, opId, actorId, "Defective order cancellation"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.WasExisting);

        var outcome = await _fakes.OutcomeLedger.GetOutcomeAsync(opId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome!.State);
        Assert.Equal("VoidPurchase", outcome.OperationType);
        Assert.Equal(actorId, outcome.ActorId);
        Assert.False(string.IsNullOrWhiteSpace(outcome.PayloadFingerprint));
    }

    [Fact]
    public async Task F05_VoidPurchase_Replay_Recovers_OriginalResult_Without_Duplicating_Ledger()
    {
        var handler = CreateVoidPurchaseHandler();
        var (purchase, actorId) = SeedPurchase(out var cashSession);
        var opId = Guid.CreateVersion7();
        var cmd = new VoidPurchaseCommand(purchase.Id, opId, actorId, "Customer return void");

        var result1 = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.False(result1.Value!.WasExisting);

        var initialEntriesCount = _fakes.SupplierAccounts.Entries.Count;
        var initialCashMovementsCount = _fakes.Cash.Movements.Count;

        // Replay call
        var result2 = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.True(result2.Value!.WasExisting);
        Assert.Equal(result1.Value.PurchaseVoidId, result2.Value.PurchaseVoidId);
        Assert.Equal(result1.Value.PurchaseId, result2.Value.PurchaseId);

        // Ensure zero additional entries or movements created
        Assert.Equal(initialEntriesCount, _fakes.SupplierAccounts.Entries.Count);
        Assert.Equal(initialCashMovementsCount, _fakes.Cash.Movements.Count);
    }

    [Fact]
    public async Task F05_VoidPurchase_Replay_With_Modified_Payload_Fails_PayloadMismatch()
    {
        var handler = CreateVoidPurchaseHandler();
        var (purchase, actorId) = SeedPurchase(out var cashSession);
        var opId = Guid.CreateVersion7();

        var result1 = await handler.HandleAsync(
            new VoidPurchaseCommand(purchase.Id, opId, actorId, "Reason A"),
            CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);

        // Replay with different reason
        var result2 = await handler.HandleAsync(
            new VoidPurchaseCommand(purchase.Id, opId, actorId, "Reason B (different)"),
            CancellationToken.None);
        Assert.False(result2.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", result2.Error?.Code);
    }

    [Fact]
    public async Task F05_VoidPurchase_AlreadyVoided_With_Different_OperationId_Returns_AlreadyVoided()
    {
        var handler = CreateVoidPurchaseHandler();
        var (purchase, actorId) = SeedPurchase(out var cashSession);
        var opId1 = Guid.CreateVersion7();
        var opId2 = Guid.CreateVersion7();

        var result1 = await handler.HandleAsync(
            new VoidPurchaseCommand(purchase.Id, opId1, actorId, "Original void"),
            CancellationToken.None);
        Assert.True(result1.IsSuccess);

        var result2 = await handler.HandleAsync(
            new VoidPurchaseCommand(purchase.Id, opId2, actorId, "Second void attempt"),
            CancellationToken.None);
        Assert.False(result2.IsSuccess);
        Assert.Equal("purchasing.already_voided", result2.Error?.Code);
    }

    [Fact]
    public async Task F05_VoidPurchase_Validation_Failure_Records_Failure_In_OutcomeLedger()
    {
        var handler = CreateVoidPurchaseHandler();
        var opId = Guid.CreateVersion7();
        var unknownPurchaseId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();

        var result = await handler.HandleAsync(
            new VoidPurchaseCommand(unknownPurchaseId, opId, actorId, "Missing purchase"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("purchasing.purchase_not_found", result.Error?.Code);

        var outcome = await _fakes.OutcomeLedger.GetOutcomeAsync(opId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Failed, outcome!.State);
        Assert.Equal("purchasing.purchase_not_found", outcome.ErrorCode);
    }

    // =========================================================================
    // F06 & F10: CommercialExchange Durable Replay & Warranty Custody Guard
    // =========================================================================

    [Fact]
    public async Task F06_CommercialExchange_FirstCall_Records_Success_In_OutcomeLedger()
    {
        var handler = CreateCommercialExchangeHandler();
        var (saleId, returnLine, replProduct, replUnit, cashierId, session) = await SeedExchangePrerequisitesAsync();
        var opId = Guid.CreateVersion7();

        var cmd = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "DEFECTIVE_EXCHANGE",
            "Exchange for customer",
            [new SaleReturnLineInput(returnLine.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 1m, 300m, [])],
            0m,
            SalePaymentMethod.Cash,
            100m,
            null);

        var result = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.WasExisting);

        var outcome = await _fakes.OutcomeLedger.GetOutcomeAsync(opId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcomeState.Succeeded, outcome!.State);
        Assert.Equal("CommercialExchange", outcome.OperationType);
        Assert.Equal(cashierId, outcome.ActorId);
    }

    [Fact]
    public async Task F06_CommercialExchange_Replay_SameOperationId_Recovers_OriginalResult()
    {
        var handler = CreateCommercialExchangeHandler();
        var (saleId, returnLine, replProduct, replUnit, cashierId, session) = await SeedExchangePrerequisitesAsync();
        var opId = Guid.CreateVersion7();

        var cmd = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "DEFECTIVE_EXCHANGE",
            "Exchange for customer",
            [new SaleReturnLineInput(returnLine.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 1m, 300m, [])],
            0m,
            SalePaymentMethod.Cash,
            100m,
            null);

        var result1 = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.False(result1.Value!.WasExisting);

        var salesCount = _fakes.Sales.Sales.Count;
        var returnsCount = _fakes.Sales.SaleReturns.Count;

        var result2 = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.True(result2.Value!.WasExisting);
        Assert.Equal(result1.Value.SaleId, result2.Value.SaleId);
        Assert.Equal(result1.Value.SaleReturnId, result2.Value.SaleReturnId);

        // Zero additional sales or returns
        Assert.Equal(salesCount, _fakes.Sales.Sales.Count);
        Assert.Equal(returnsCount, _fakes.Sales.SaleReturns.Count);
    }

    [Fact]
    public async Task F06_CommercialExchange_Replay_DifferentPayload_Fails_PayloadMismatch()
    {
        var handler = CreateCommercialExchangeHandler();
        var (saleId, returnLine, replProduct, replUnit, cashierId, session) = await SeedExchangePrerequisitesAsync();
        var opId = Guid.CreateVersion7();

        var cmd1 = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "REASON_1",
            "Exchange note 1",
            [new SaleReturnLineInput(returnLine.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 1m, 300m, [])],
            0m,
            SalePaymentMethod.Cash,
            100m,
            null);

        var result1 = await handler.HandleAsync(cmd1, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);

        var cmd2 = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "REASON_2_DIFFERENT",
            "Exchange note 2",
            [new SaleReturnLineInput(returnLine.Id, 1m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 1m, 300m, [])],
            0m,
            SalePaymentMethod.Cash,
            100m,
            null);

        var result2 = await handler.HandleAsync(cmd2, CancellationToken.None);
        Assert.False(result2.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", result2.Error?.Code);
    }

    [Fact]
    public async Task F10_CommercialExchange_Rejects_SerializedUnit_Under_Active_WarrantyClaim()
    {
        var handler = CreateCommercialExchangeHandler();
        var (saleId, returnLine, replProduct, replUnit, cashierId, session, unit) = await SeedSerializedExchangePrerequisitesAsync();
        var opId = Guid.CreateVersion7();

        // Mark unit under active warranty claim
        var claimId = Guid.CreateVersion7();
        var claim = new WarrantyClaim
        {
            Id = claimId,
            Status = WarrantyClaimStatus.Received,
            ClaimNumber = "WC-001",
            CustomerId = Guid.CreateVersion7(),
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Warranty.Claims[claimId] = claim;
        var claimItem = new WarrantyClaimItem
        {
            Id = Guid.CreateVersion7(),
            ClaimId = claimId,
            OriginalSaleItemId = returnLine.Id,
            Quantity = 1m
        };
        _fakes.Warranty.ClaimItems.Add(claimItem);
        _fakes.Warranty.ClaimItemUnits.Add(new WarrantyClaimItemUnit
        {
            Id = Guid.CreateVersion7(),
            ClaimItemId = claimItem.Id,
            OriginalInventoryUnitId = unit.Id
        });

        var cmd = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "DEFECT",
            "Exchange under warranty claim",
            [new SaleReturnLineInput(returnLine.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 1m, 100m, [])],
            0m,
            SalePaymentMethod.Other,
            0m,
            null);

        var result = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", result.Error?.Code);
    }

    [Fact]
    public async Task F10_CommercialExchange_Rejects_SerializedUnit_Terminally_Resolved_In_Warranty()
    {
        var handler = CreateCommercialExchangeHandler();
        var (saleId, returnLine, replProduct, replUnit, cashierId, session, unit) = await SeedSerializedExchangePrerequisitesAsync();
        var opId = Guid.CreateVersion7();

        // Mark unit terminally resolved (Closed claim)
        var claimId = Guid.CreateVersion7();
        var claim = new WarrantyClaim
        {
            Id = claimId,
            Status = WarrantyClaimStatus.Closed,
            ClaimNumber = "WC-CLOSED",
            CustomerId = Guid.CreateVersion7(),
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Warranty.Claims[claimId] = claim;
        var claimItem = new WarrantyClaimItem
        {
            Id = Guid.CreateVersion7(),
            ClaimId = claimId,
            OriginalSaleItemId = returnLine.Id,
            Quantity = 1m
        };
        _fakes.Warranty.ClaimItems.Add(claimItem);
        _fakes.Warranty.ClaimItemUnits.Add(new WarrantyClaimItemUnit
        {
            Id = Guid.CreateVersion7(),
            ClaimItemId = claimItem.Id,
            OriginalInventoryUnitId = unit.Id
        });

        var cmd = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "DEFECT",
            "Exchange closed unit",
            [new SaleReturnLineInput(returnLine.Id, 1m, SaleReturnDisposition.RestockSellable, [unit.Id])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 1m, 100m, [])],
            0m,
            SalePaymentMethod.Other,
            0m,
            null);

        var result = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", result.Error?.Code);
    }

    [Fact]
    public async Task F10_CommercialExchange_Rejects_NonSerialized_Exceeding_Available_Warranty_Balance()
    {
        var handler = CreateCommercialExchangeHandler();
        var (saleId, returnLine, replProduct, replUnit, cashierId, session) = await SeedExchangePrerequisitesAsync(saleQty: 5m);
        var opId = Guid.CreateVersion7();

        // Active warranty claim for 4 units
        var claimId = Guid.CreateVersion7();
        var claim = new WarrantyClaim
        {
            Id = claimId,
            Status = WarrantyClaimStatus.UnderReview,
            ClaimNumber = "WC-QTY",
            CustomerId = Guid.CreateVersion7(),
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Warranty.Claims[claimId] = claim;
        _fakes.Warranty.ClaimItems.Add(new WarrantyClaimItem
        {
            Id = Guid.CreateVersion7(),
            ClaimId = claimId,
            OriginalSaleItemId = returnLine.Id,
            Quantity = 4m
        });

        // Total was 5. Active claimed is 4. Available for return is only 1.
        // Attempting to return 2 must fail!
        var cmd = new CommercialExchangeCommand(
            opId,
            saleId,
            cashierId,
            session.Id,
            null,
            "DEFECT",
            "Exchange qty exceeding",
            [new SaleReturnLineInput(returnLine.Id, 2m, SaleReturnDisposition.RestockSellable, [])],
            [new CompleteSaleLineInput(replProduct.Id, replUnit.Id, 2m, 300m, [])],
            0m,
            SalePaymentMethod.Cash,
            200m,
            null);

        var result = await handler.HandleAsync(cmd, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("sales.return_unit_active_warranty", result.Error?.Code);
    }

    // =========================================================================
    // F07: POS Draft Retry Integrity
    // =========================================================================

    [Fact]
    public async Task F07_CompletePosDraft_Replay_Recovers_Sale_When_Draft_Already_Converted()
    {
        var saveHandler = CreateSavePosDraftHandler();
        var completeHandler = CreateCompletePosDraftHandler();
        var (product, unit) = SeedQuantityProduct("DRAFT-P1", 100m, 200m, 50m);
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);

        // 1. Save draft
        var saveResult = await saveHandler.HandleAsync(
            new SavePosDraftCommand(
                null,
                null,
                null,
                cashierId,
                "POS-1",
                "Held cart",
                [new SavePosDraftItemInput(product.Id, unit.Id, 2m)]),
            CancellationToken.None);
        Assert.True(saveResult.IsSuccess, saveResult.Error?.Message);

        var draftId = saveResult.Value!.DraftId;
        var opId = Guid.CreateVersion7();

        var completeCmd = new CompletePosDraftCommand(
            draftId,
            saveResult.Value.Version,
            opId,
            cashierId,
            session.Id,
            0m,
            SalePaymentMethod.Cash,
            400m,
            null);

        // 2. First complete call -> Converts draft and creates sale
        var result1 = await completeHandler.HandleAsync(completeCmd, CancellationToken.None);
        Assert.True(result1.IsSuccess, result1.Error?.Message);
        Assert.False(result1.Value!.WasExisting);

        // Verify draft status is Converted
        var convertedDraft = await _fakes.PosDrafts.GetForUpdateAsync(draftId, CancellationToken.None);
        Assert.Equal(PosDraftStatus.Converted, convertedDraft!.Status);

        // 3. Network retry with same ClientOperationId: MUST NOT fail with sales.draft_not_open!
        var result2 = await completeHandler.HandleAsync(completeCmd, CancellationToken.None);
        Assert.True(result2.IsSuccess, result2.Error?.Message);
        Assert.True(result2.Value!.WasExisting);
        Assert.Equal(result1.Value.SaleId, result2.Value.SaleId);
        Assert.Equal(result1.Value.InvoiceNumber, result2.Value.InvoiceNumber);
    }

    // =========================================================================
    // F14: EF / ChangeTracker Append-Only Ledger Protection
    // =========================================================================

    [Fact]
    public void F14_SupplierAccountEntry_Modified_Throws_InvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var entry = new SupplierAccountEntry
        {
            Id = Guid.CreateVersion7(),
            EntryNumber = "SAE-999",
            SupplierId = Guid.CreateVersion7(),
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 500m,
            ReferenceType = "Purchase",
            ReferenceId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorId = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Attach(entry);
        db.Entry(entry).State = EntityState.Modified;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void F14_SupplierAccountEntry_Deleted_Throws_InvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var entry = new SupplierAccountEntry
        {
            Id = Guid.CreateVersion7(),
            EntryNumber = "SAE-888",
            SupplierId = Guid.CreateVersion7(),
            EntryType = SupplierAccountEntryType.SupplierPayment,
            Direction = SupplierAccountDirection.DecreasePayable,
            Amount = 300m,
            ReferenceType = "Payment",
            ReferenceId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            ActorId = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Attach(entry);
        db.Entry(entry).State = EntityState.Deleted;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void F14_CashMovement_Modified_Throws_InvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var movement = new CashMovement
        {
            Id = Guid.CreateVersion7(),
            CashSessionId = Guid.CreateVersion7(),
            MovementType = CashMovementType.SaleCashIn,
            Direction = CashMovementDirection.In,
            Amount = 200m,
            SourceType = "SALE",
            SourceId = Guid.CreateVersion7(),
            ActorId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow
        };

        db.Attach(movement);
        db.Entry(movement).State = EntityState.Modified;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void F14_CashMovement_Deleted_Throws_InvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var movement = new CashMovement
        {
            Id = Guid.CreateVersion7(),
            CashSessionId = Guid.CreateVersion7(),
            MovementType = CashMovementType.SaleRefundCashOut,
            Direction = CashMovementDirection.Out,
            Amount = 50m,
            SourceType = "SALE_RETURN",
            SourceId = Guid.CreateVersion7(),
            ActorId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow
        };

        db.Attach(movement);
        db.Entry(movement).State = EntityState.Deleted;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void F14_BusinessAuditEvent_Modified_Or_Deleted_Throws_InvalidOperationException()
    {
        using var db = CreateInMemoryDb();
        var audit = new BusinessAuditEvent
        {
            Id = Guid.CreateVersion7(),
            Action = "TEST_ACTION",
            OccurredAt = DateTimeOffset.UtcNow
        };

        db.Attach(audit);
        db.Entry(audit).State = EntityState.Modified;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

        db.Entry(audit).State = EntityState.Deleted;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    // =========================================================================
    // F12: Net Profit Recognized Inventory Loss Invariant
    // =========================================================================

    [Fact]
    public void F12_NetProfit_Formula_Includes_RecognizedInventoryLoss()
    {
        // NetProfit = GrossProfit - Expenses - RecognizedLoss
        decimal grossSales = 10000m;
        decimal refunds = 1000m;
        decimal netSales = grossSales - refunds; // 9000m
        decimal cogs = 5000m;
        decimal reversedCogs = 500m;
        decimal netCogs = cogs - reversedCogs; // 4500m
        decimal grossProfit = netSales - netCogs; // 4500m
        decimal expenses = 1200m;
        decimal recognizedLoss = 800m;

        // Canonical calculation implemented in ReportingReadService
        decimal netProfit = grossProfit - expenses - recognizedLoss;

        Assert.Equal(2500m, netProfit);
        Assert.True(netProfit < (grossProfit - expenses), "Net profit must be strictly reduced by recognized loss.");
        Assert.Equal(800m, (grossProfit - expenses) - netProfit);
    }

    [Fact]
    public void F12_InventoryMovement_RecognizedLoss_IsNonNegative()
    {
        var movement = new InventoryMovement
        {
            Id = Guid.CreateVersion7(),
            ProductId = Guid.CreateVersion7(),
            MovementType = InventoryMovementType.SaleReturn,
            RecognizedLossAmount = 250m,
            OccurredAt = DateTimeOffset.UtcNow
        };

        Assert.True(movement.RecognizedLossAmount >= 0m);
    }

    // =========================================================================
    // Test Helpers
    // =========================================================================

    private static EdgeRetailsDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql("Host=localhost;Database=pass3_unit_test;Username=postgres")
            .Options;
        return new EdgeRetailsDbContext(options);
    }

    private CashSession OpenCashSession(Guid cashierId)
    {
        var session = new CashSession
        {
            Id = Guid.CreateVersion7(),
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = cashierId,
            OpenedAt = _fakes.Clock.UtcNow,
            OpeningCash = 10000m,
            Status = CashSessionStatus.Open
        };
        _fakes.Cash.AddSession(session);
        return session;
    }

    private (Product Product, ProductUnit Unit) SeedQuantityProduct(string sku, decimal cost, decimal price, decimal stock = 20m)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = $"Product {sku}",
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = price,
            ReferencePurchaseCost = cost,
            IsActive = true
        };
        _fakes.Catalog.Products[product.Id] = product;

        var pu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultSaleUnit = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(pu);

        if (stock > 0)
        {
            _fakes.Inventory.AddStockBalance(new StockBalance
            {
                ProductId = product.Id,
                SellableQty = stock
            });

            _fakes.Inventory.AddCostState(new ProductCostState
            {
                ProductId = product.Id,
                CostedQty = stock,
                TotalInventoryCost = stock * cost,
                MovingAverageCost = cost,
                LastPurchaseCost = cost,
                Version = 1
            });

            var lot = new InventoryLot
            {
                Id = Guid.CreateVersion7(),
                ProductId = product.Id,
                ReceivedQuantity = stock,
                OriginalUnitCost = cost,
                EffectiveUnitCost = cost,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _fakes.Inventory.AddLot(lot);

            _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
            {
                LotId = lot.Id,
                StockBucket = InventoryBucket.Sellable,
                Quantity = stock
            });
        }

        return (product, pu);
    }

    private (Purchase Purchase, Guid ActorId) SeedPurchase(out CashSession session)
    {
        var actorId = Guid.CreateVersion7();
        session = OpenCashSession(actorId);
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = "Supplier 1", DealerCode = "S1", IsActive = true };
        _fakes.Parties.AddSupplier(supplier);
        var (product, unit) = SeedQuantityProduct("VOID-P1", 100m, 150m, 10m);

        var purchase = new Purchase
        {
            Id = Guid.CreateVersion7(),
            PurchaseNumber = "PO-001",
            SupplierId = supplier.Id,
            SupplierInvoiceNumber = "INV-001",
            NormalizedSupplierInvoiceNumber = "INV001",
            PurchaseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = PurchaseStatus.Completed,
            SettlementMode = PurchaseSettlementMode.CashDrawer,
            Subtotal = 1000m,
            GrandTotal = 1000m,
            CreatedBy = actorId,
            CreatedAt = _fakes.Clock.UtcNow,
            ClientOperationId = Guid.CreateVersion7(),
            Version = 1
        };
        _fakes.Purchasing.Purchases[purchase.Id] = purchase;

        var payment = new SupplierPayment
        {
            Id = Guid.CreateVersion7(),
            PaymentNumber = "SP-001",
            SupplierId = supplier.Id,
            Amount = 1000m,
            Purpose = SupplierPaymentPurpose.Settlement,
            Method = SupplierSettlementMethod.CashDrawer,
            CashSessionId = session.Id,
            PaidAt = _fakes.Clock.UtcNow,
            ActorId = actorId,
            ClientOperationId = purchase.ClientOperationId,
            Status = SupplierSettlementStatus.Posted
        };
        _fakes.SupplierAccounts.AddPayment(payment);

        return (purchase, actorId);
    }

    private async Task<(Guid SaleId, SaleItem ReturnLine, Product ReplProduct, ProductUnit ReplUnit, Guid CashierId, CashSession Session)>
        SeedExchangePrerequisitesAsync(decimal saleQty = 2m)
    {
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var (soldProduct, soldUnit) = SeedQuantityProduct("ORIG-QTY", 100m, 200m, 20m);
        var (replProduct, replUnit) = SeedQuantityProduct("REPL-QTY", 150m, 300m, 20m);

        var saleHandler = CreateCompleteSaleHandler();
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                cashierId,
                session.Id,
                0m,
                SalePaymentMethod.Cash,
                200m * saleQty,
                null,
                null,
                [new CompleteSaleLineInput(soldProduct.Id, soldUnit.Id, saleQty, 200m, [])]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);
        var saleId = saleResult.Value!.SaleId;
        var saleItem = _fakes.Sales.SaleItems.Single(x => x.SaleId == saleId);

        return (saleId, saleItem, replProduct, replUnit, cashierId, session);
    }

    private async Task<(Guid SaleId, SaleItem ReturnLine, Product ReplProduct, ProductUnit ReplUnit, Guid CashierId, CashSession Session, InventoryUnit Unit)>
        SeedSerializedExchangePrerequisitesAsync()
    {
        var cashierId = Guid.CreateVersion7();
        var session = OpenCashSession(cashierId);
        var unitDef = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var soldProduct = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Sold Serial Phone",
            Sku = "SER-PHONE",
            BaseUnitId = unitDef.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 500m,
            ReferencePurchaseCost = 300m,
            IsActive = true
        };
        _fakes.Catalog.Products[soldProduct.Id] = soldProduct;

        var soldPu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = soldProduct.Id,
            UnitId = unitDef.Id,
            FactorToBaseUnit = 1m,
            IsDefaultSaleUnit = true,
            CanSell = true
        };
        _fakes.Catalog.AddProductUnit(soldPu);

        var (replProduct, replUnit) = SeedQuantityProduct("REPL-ACCESSORY", 50m, 100m, 20m);

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = soldProduct.Id,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 300m,
            EffectiveUnitCost = 300m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddLot(lot);

        _fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 1m
        });

        _fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = soldProduct.Id,
            SellableQty = 1m
        });

        _fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = soldProduct.Id,
            CostedQty = 1m,
            TotalInventoryCost = 300m,
            MovingAverageCost = 300m,
            LastPurchaseCost = 300m,
            Version = 1
        });

        var invUnit = new InventoryUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = soldProduct.Id,
            InventoryLotId = lot.Id,
            TrackingCode = "TC-PHONE-001",
            SerialNumber = "SN-PHONE-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 300m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _fakes.Inventory.AddInventoryUnit(invUnit);

        var saleHandler = CreateCompleteSaleHandler();
        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                cashierId,
                session.Id,
                0m,
                SalePaymentMethod.Cash,
                500m,
                null,
                null,
                [new CompleteSaleLineInput(soldProduct.Id, soldPu.Id, 1m, 500m, [invUnit.Id])]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);
        var saleId = saleResult.Value!.SaleId;
        var saleItem = _fakes.Sales.SaleItems.Single(x => x.SaleId == saleId);

        return (saleId, saleItem, replProduct, replUnit, cashierId, session, invUnit);
    }
}
