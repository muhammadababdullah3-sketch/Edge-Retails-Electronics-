using System.Text.Json;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.UnitTests;

public sealed class TrackingPurchaseReplayTests
{
    [Theory]
    [InlineData("supplier")]
    [InlineData("invoice")]
    [InlineData("date")]
    [InlineData("note")]
    [InlineData("charges")]
    [InlineData("settlement")]
    [InlineData("actor")]
    [InlineData("receive")]
    [InlineData("payment")]
    [InlineData("method")]
    [InlineData("reference")]
    [InlineData("quantity")]
    [InlineData("cost")]
    [InlineData("price")]
    [InlineData("unit")]
    [InlineData("identities")]
    public async Task Successful_purchase_replay_rejects_changed_intent_without_new_effects(string field)
    {
        var f = new Phase2TestDoubles();
        var supplier = new Supplier { Name = "Replay supplier", DealerCode = "REP", IsActive = true };
        f.Parties.AddSupplier(supplier);
        var product = new Product { Name = "Replay product", Sku = "REP", BaseUnitId = Guid.NewGuid(),
            TrackingMode = TrackingMode.Quantity, IsActive = true };
        f.Catalog.Products[product.Id] = product;
        var unit = new ProductUnit { ProductId = product.Id, UnitId = product.BaseUnitId, FactorToBaseUnit = 1m,
            CanPurchase = true, CanSell = true, IsActive = true };
        f.Catalog.AddProductUnit(unit);
        var command = new CreatePurchaseCommand(supplier.Id, "INV-REPLAY", new(2026, 10, 7), "Note", 0m,
            PurchaseSettlementMode.External, Guid.NewGuid(), Guid.NewGuid(), [new(product.Id, unit.Id, 2m, 10m, 15m, [])],
            InitialPaymentAmount: 0m, ReceiveStockImmediately: false);
        var handler = Handler(f);
        var first = await handler.HandleAsync(command, default);
        Assert.True(first.IsSuccess, first.Error?.ToString());
        var replay = await handler.HandleAsync(command with { SupplierInvoiceNumber = " INV-REPLAY ", Note = " Note " }, default);
        Assert.True(replay.IsSuccess, replay.Error?.ToString());
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(first.Value!.PurchaseId, replay.Value.PurchaseId);
        var before = Snapshot(f);
        var line = command.Lines[0];
        var changed = field switch
        {
            "supplier" => command with { SupplierId = Guid.NewGuid() },
            "invoice" => command with { SupplierInvoiceNumber = "OTHER" },
            "date" => command with { PurchaseDate = command.PurchaseDate.AddDays(1) },
            "note" => command with { Note = "Other" },
            "charges" => command with { OtherCharges = 1m },
            "settlement" => command with { SettlementMode = PurchaseSettlementMode.CashDrawer },
            "actor" => command with { CreatedBy = Guid.NewGuid() },
            "receive" => command with { ReceiveStockImmediately = true },
            "payment" => command with { InitialPaymentAmount = 1m },
            "method" => command with { InitialPaymentMethod = EdgeRetails.Domain.Finance.SupplierSettlementMethod.Bank },
            "reference" => command with { InitialPaymentExternalReference = "Other" },
            "quantity" => command with { Lines = [line with { EnteredQuantity = 3m }] },
            "cost" => command with { Lines = [line with { EnteredUnitCost = 11m }] },
            "price" => command with { Lines = [line with { BaseUnitSalePrice = 16m }] },
            "unit" => command with { Lines = [line with { ProductUnitId = Guid.NewGuid() }] },
            _ => command with { Lines = [line with { SerializedUnits = [new("Other")] }] }
        };
        Assert.Equal("idempotency.payload_mismatch", (await handler.HandleAsync(changed, default)).Error?.Code);
        Assert.Equal(before, Snapshot(f));
        Assert.Single(f.Purchasing.Purchases);
        Assert.Single(f.SupplierAccounts.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_purchase_cannot_be_blessed_by_retry_payload(bool hasLegacyOutcome)
    {
        var f = new Phase2TestDoubles();
        var command = new CreatePurchaseCommand(Guid.NewGuid(), "LEGACY", new(2026, 10, 7), null, 0m,
            PurchaseSettlementMode.External, Guid.NewGuid(), Guid.NewGuid(), [new(Guid.NewGuid(), Guid.NewGuid(), 1m, 1m, 1m, [])]);
        var purchase = new Purchase { ClientOperationId = command.ClientOperationId, SupplierId = command.SupplierId,
            SupplierInvoiceNumber = command.SupplierInvoiceNumber, PurchaseNumber = "LEGACY" };
        f.Purchasing.AddPurchase(purchase);
        if (hasLegacyOutcome)
        {
            await f.OutcomeLedger.RecordSuccessAsync(command.ClientOperationId, "Purchase", purchase.Id, actorId: command.CreatedBy);
        }
        var before = JsonSerializer.Serialize(await f.OutcomeLedger.GetOutcomeAsync(command.ClientOperationId));
        var result = await Handler(f).HandleAsync(command, default);
        Assert.Equal(hasLegacyOutcome ? "idempotency.payload_mismatch" : "idempotency.legacy_purchase_requires_reconciliation", result.Error?.Code);
        Assert.Equal(before, JsonSerializer.Serialize(await f.OutcomeLedger.GetOutcomeAsync(command.ClientOperationId)));
        Assert.Single(f.Purchasing.Purchases);
        Assert.Empty(f.SupplierAccounts.Entries);
        Assert.Empty(f.Inventory.Lots);
        Assert.Empty(f.Audit.Records);
    }

    private static string Snapshot(Phase2TestDoubles f) => JsonSerializer.Serialize(new
    {
        f.Purchasing.Purchases, f.Purchasing.PurchaseItems, f.SupplierAccounts.Entries, f.Inventory.Lots, f.Audit.Records
    });

    private static CreatePurchaseHandler Handler(Phase2TestDoubles f) => new(f.Purchasing, f.Parties, f.Catalog,
        f.Inventory, f.CostAllocator, f.Cash, f.Traceability, f.SupplierAccounts, f.OperationLock, f.ResourceLock,
        f.Audit, f.Numbers, f.Clock, f.Transactions, f.Authorization, f.UnitOfWork,
        outcomeLedger: f.OutcomeLedger, physicalUnitCreationAuthority: f.PhysicalUnits);
}
