using System.Reflection;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.UnitTests;

public sealed class Phase3PosPriceOverrideTests
{
    [Fact]
    public async Task Replaying_sale_operation_with_changed_override_is_rejected_as_payload_mismatch()
    {
        var fakes = new Phase2TestDoubles();
        var authorizer = new RecordingAuthorizer(PermissionKeys.SalesCreate, PermissionKeys.SalesPriceOverride);
        var product = SeedQuantityProduct(fakes, listPrice: 300m, cost: 200m, stock: 4m);
        var cashierId = Guid.CreateVersion7();
        var operationId = Guid.CreateVersion7();
        var cashSession = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 1000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        fakes.Cash.AddSession(cashSession);
        var handler = CreateHandler(fakes, authorizer);
        var first = CreateLine(product.Product.Id, product.ProductUnit.Id, 250m, "Approved match");
        var replay = CreateLine(product.Product.Id, product.ProductUnit.Id, 220m, "New reason");

        var firstResult = await handler.HandleAsync(CreateCommand(operationId, cashierId, cashSession.Id, first), CancellationToken.None);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);

        var replayResult = await handler.HandleAsync(CreateCommand(operationId, cashierId, cashSession.Id, replay), CancellationToken.None);

        Assert.False(replayResult.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", replayResult.Error?.Code);
        Assert.Single(fakes.Sales.Sales);
    }

    [Fact]
    public async Task Replaying_sale_operation_with_new_bank_reference_returns_canonical_payment()
    {
        var fakes = new Phase2TestDoubles();
        var cashierId = Guid.CreateVersion7();
        var product = SeedQuantityProduct(fakes, listPrice: 300m, cost: 200m, stock: 4m);
        var cashSession = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 1000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        fakes.Cash.AddSession(cashSession);

        var operationId = Guid.CreateVersion7();
        var handler = CreateHandler(fakes, new RecordingAuthorizer(PermissionKeys.SalesCreate));
        var line = new CompleteSaleLineInput(product.Product.Id, product.ProductUnit.Id, 1m, 300m, []);
        var firstCommand = CreateCommand(operationId, cashierId, cashSession.Id, line) with
        {
            PaymentMethod = SalePaymentMethod.Bank,
            AmountTendered = 300m,
            PaymentReference = "BANK-ORIGINAL"
        };
        var replayCommand = firstCommand with { PaymentReference = "BANK-RETRY" };

        var first = await handler.HandleAsync(firstCommand, CancellationToken.None);
        var replay = await handler.HandleAsync(replayCommand, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        Assert.Single(fakes.Sales.Sales);
        Assert.Single(fakes.Sales.SalePayments);
        Assert.Equal("BANK-ORIGINAL", fakes.Sales.SalePayments.Single().Reference);
    }

    [Fact]
    public async Task Authorized_one_sale_override_is_audited_and_separate_from_invoice_discount()
    {
        var fakes = new Phase2TestDoubles();
        var authorizer = new RecordingAuthorizer(
            PermissionKeys.SalesCreate,
            "sales.price_override");
        var product = SeedQuantityProduct(fakes, listPrice: 300m, cost: 200m, stock: 4m);
        var cashierId = Guid.CreateVersion7();
        var input = new CompleteSaleLineInput(
            product.Product.Id,
            product.ProductUnit.Id,
            EnteredQuantity: 1m,
            ExpectedUnitPrice: 300m,
            InventoryUnitIds: []);

        // Keep the regression executable against the pre-contract baseline: these
        // properties are supplied when the backend command contract adds them.
        SetIfPresent(input, "PriceOverrideUnitPrice", 250m);
        SetIfPresent(input, "PriceOverrideReason", "Matched an approved competitor offer");
        var cashSession = new CashSession
        {
            Id = Guid.CreateVersion7(),
            OpenedBy = cashierId,
            OpeningCash = 1000m,
            Status = CashSessionStatus.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        fakes.Cash.AddSession(cashSession);

        var handler = CreateHandler(fakes, authorizer);

        var result = await handler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                CustomerId: null,
                CashierUserId: cashierId,
                SessionId: cashSession.Id,
                InvoiceDiscount: 10m,
                PaymentMethod: SalePaymentMethod.Cash,
                AmountTendered: 300m,
                PaymentReference: null,
                QuotationId: null,
                Lines: [input]),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains("sales.price_override", authorizer.RequestedPermissions);
        var sale = Assert.Single(fakes.Sales.Sales.Values);
        var saleItem = Assert.Single(fakes.Sales.SaleItems);
        Assert.Equal(250m, sale.Subtotal);
        Assert.Equal(10m, sale.InvoiceDiscount);
        Assert.Equal(240m, sale.GrandTotal);
        Assert.Equal(250m, saleItem.UnitPrice);
        Assert.Equal(240m, saleItem.NetLineTotal);
        Assert.Equal(300m, ReadDecimal(saleItem, "ListUnitPriceSnapshot"));
        Assert.Equal("Matched an approved competitor offer", ReadString(saleItem, "PriceOverrideReason"));
        Assert.Equal(cashierId, ReadGuid(saleItem, "PriceOverrideBy"));
    }

    private static CompleteSaleHandler CreateHandler(Phase2TestDoubles fakes, IApplicationPermissionAuthorizer authorizer) =>
        new(
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
            authorizer,
            fakes.UnitOfWork);

    private static CompleteSaleLineInput CreateLine(Guid productId, Guid productUnitId, decimal price, string reason)
    {
        var input = new CompleteSaleLineInput(productId, productUnitId, 1m, 300m, []);
        SetIfPresent(input, "PriceOverrideUnitPrice", price);
        SetIfPresent(input, "PriceOverrideReason", reason);
        return input;
    }

    private static CompleteSaleCommand CreateCommand(Guid operationId, Guid actorId, Guid sessionId, CompleteSaleLineInput line) =>
        new(
            operationId,
            CustomerId: null,
            CashierUserId: actorId,
            SessionId: sessionId,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: null,
            QuotationId: null,
            Lines: [line]);

    private static (Product Product, ProductUnit ProductUnit) SeedQuantityProduct(
        Phase2TestDoubles fakes,
        decimal listPrice,
        decimal cost,
        decimal stock)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Override Test Product",
            Sku = "OVERRIDE-01",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = listPrice,
            ReferencePurchaseCost = cost,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        fakes.Catalog.Products[product.Id] = product;
        fakes.Catalog.AddProductUnit(productUnit);
        fakes.Inventory.AddStockBalance(new StockBalance { ProductId = product.Id, SellableQty = stock });
        fakes.Inventory.AddCostState(new ProductCostState
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
        fakes.Inventory.AddLot(lot);
        fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = stock
        });
        return (product, productUnit);
    }

    private static void SetIfPresent<T>(object instance, string propertyName, T value)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property?.SetValue(instance, value);
    }

    private static decimal ReadDecimal(object instance, string propertyName) =>
        (decimal?)instance.GetType().GetProperty(propertyName)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Missing snapshot property {propertyName}.");

    private static string ReadString(object instance, string propertyName) =>
        (string?)instance.GetType().GetProperty(propertyName)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Missing snapshot property {propertyName}.");

    private static Guid ReadGuid(object instance, string propertyName) =>
        (Guid?)instance.GetType().GetProperty(propertyName)?.GetValue(instance)
        ?? throw new InvalidOperationException($"Missing snapshot property {propertyName}.");

    private sealed class RecordingAuthorizer(params string[] granted) : IApplicationPermissionAuthorizer
    {
        private readonly HashSet<string> _granted = granted.ToHashSet(StringComparer.Ordinal);

        public List<string> RequestedPermissions { get; } = [];

        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken)
        {
            RequestedPermissions.Add(permissionKey);
            return Task.FromResult(_granted.Contains(permissionKey)
                ? Result.Success()
                : Result.Failure("authorization.denied", $"Permission '{permissionKey}' is required."));
        }
    }
}
