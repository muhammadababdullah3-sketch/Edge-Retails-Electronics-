using System.Text.Json;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Production.Printing;

internal sealed record ParsedReceiptSnapshot(
    string ShopName,
    string? Phone,
    string? Address,
    string? Header,
    string? Footer,
    bool ShowCustomer,
    bool ShowCashier,
    string? LogoBehavior,
    string? TemplateVersion);

public sealed class PosSaleReceiptKindSource : IProductionDocumentKindSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly EdgeRetailsDbContext _db;
    public PosSaleReceiptKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.PosSaleReceipt;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var sale = await _db.Sales.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Sale '{businessDocumentId}' not found for printing.");
        var items = await _db.SaleItems.AsNoTracking()
            .Where(x => x.SaleId == sale.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var customer = sale.CustomerId is Guid customerId
            ? await _db.Customers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken)
            : null;

        ParsedReceiptSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(sale.ReceiptTemplateSnapshot))
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<ParsedReceiptSnapshot>(
                    sale.ReceiptTemplateSnapshot,
                    JsonOptions);
            }
            catch (JsonException)
            {
            }
        }

        var shopProfile = snapshot is null
            ? await _db.ShopProfiles.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ProfileKey == "PRIMARY", cancellationToken)
            : null;

        var lines = items.Select(item => new ProductionDocumentLine(
            item.ProductNameSnapshot,
            item.BaseQuantity,
            null,
            item.UnitPrice,
            item.NetLineTotal)).ToList();

        var totals = new List<ProductionDocumentTotal>
        {
            new("Subtotal", sale.Subtotal)
        };
        if (sale.InvoiceDiscount > 0m)
        {
            totals.Add(new("Discount", sale.InvoiceDiscount));
        }
        totals.Add(new("Total", sale.GrandTotal, true));

        return new ProductionDocument(
            Kind,
            sale.Id,
            sale.InvoiceNumber,
            sale.CompletedAt,
            snapshot?.ShopName ?? shopProfile?.ShopName ?? "Edge Retails",
            snapshot?.Address ?? shopProfile?.Address,
            snapshot?.Phone ?? shopProfile?.Phone,
            customer?.Name,
            snapshot?.Header ?? "SALES RECEIPT",
            lines,
            totals,
            [],
            snapshot?.Footer);
    }
}

public sealed class SaleReturnReceiptKindSource : IProductionDocumentKindSource
{
    private readonly EdgeRetailsDbContext _db;
    public SaleReturnReceiptKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.SaleReturnReceipt;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var ret = await _db.SaleReturns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Sale return '{businessDocumentId}' not found for printing.");
        var items = await _db.SaleReturnItems.AsNoTracking()
            .Where(x => x.SaleReturnId == ret.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var shop = await PrimaryShopAsync(_db, cancellationToken);

        var lines = items.Select(item => new ProductionDocumentLine(
            $"Returned Item ({item.ProductId})",
            item.BaseQuantity,
            null,
            item.BaseQuantity == 0m ? 0m : item.RefundAmount / item.BaseQuantity,
            item.RefundAmount)).ToList();

        return new ProductionDocument(
            Kind,
            ret.Id,
            ret.ReturnNumber,
            ret.CreatedAt,
            shop?.ShopName ?? "Edge Retails",
            shop?.Address,
            shop?.Phone,
            null,
            "SALE RETURN CREDIT NOTE",
            lines,
            [new("Refund Total", ret.RefundAmount, true)],
            string.IsNullOrWhiteSpace(ret.ReasonNote) ? [] : [ret.ReasonNote],
            "Refund issued.");
    }

    internal static Task<EdgeRetails.Domain.SystemConfiguration.ShopProfile?> PrimaryShopAsync(
        EdgeRetailsDbContext db,
        CancellationToken cancellationToken) =>
        db.ShopProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProfileKey == "PRIMARY", cancellationToken);
}

public sealed class PurchaseDocumentKindSource : IProductionDocumentKindSource
{
    private readonly EdgeRetailsDbContext _db;
    public PurchaseDocumentKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.PurchaseDocument;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var purchase = await _db.Purchases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Purchase '{businessDocumentId}' not found for printing.");
        var items = await _db.PurchaseItems.AsNoTracking()
            .Where(x => x.PurchaseId == purchase.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var supplier = await _db.Suppliers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == purchase.SupplierId, cancellationToken);
        var shop = await SaleReturnReceiptKindSource.PrimaryShopAsync(_db, cancellationToken);

        var lines = items.Select(item => new ProductionDocumentLine(
            item.ProductNameSnapshot,
            item.BaseQuantity,
            null,
            item.EnteredUnitCost,
            item.BaseLineTotal)).ToList();

        return new ProductionDocument(
            Kind,
            purchase.Id,
            purchase.PurchaseNumber,
            purchase.CreatedAt,
            shop?.ShopName ?? "Edge Retails",
            shop?.Address,
            shop?.Phone,
            supplier?.Name,
            "GOODS RECEIPT NOTE",
            lines,
            [
                new("Subtotal", purchase.Subtotal),
                new("Other Charges", purchase.OtherCharges),
                new("Total Amount", purchase.GrandTotal, true)
            ],
            string.IsNullOrWhiteSpace(purchase.Note) ? [] : [purchase.Note],
            supplier?.DealerCode is null ? null : $"Supplier DealerCode: {supplier.DealerCode}");
    }
}

public sealed class PurchaseReturnDocumentKindSource : IProductionDocumentKindSource
{
    private readonly EdgeRetailsDbContext _db;
    public PurchaseReturnDocumentKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.PurchaseReturnDocument;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var ret = await _db.PurchaseReturns.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Purchase return '{businessDocumentId}' not found for printing.");
        var items = await _db.PurchaseReturnItems.AsNoTracking()
            .Where(x => x.PurchaseReturnId == ret.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var purchase = await _db.Purchases.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == ret.PurchaseId, cancellationToken);
        var supplier = purchase is null
            ? null
            : await _db.Suppliers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == purchase.SupplierId, cancellationToken);
        var shop = await SaleReturnReceiptKindSource.PrimaryShopAsync(_db, cancellationToken);

        var lines = items.Select(item => new ProductionDocumentLine(
            $"Returned Item ({item.ProductId})",
            item.BaseQuantity,
            null,
            item.SupplierUnitReturnValue,
            item.SupplierReturnValue)).ToList();

        return new ProductionDocument(
            Kind,
            ret.Id,
            ret.ReturnNumber,
            ret.CreatedAt,
            shop?.ShopName ?? "Edge Retails",
            shop?.Address,
            shop?.Phone,
            supplier?.Name,
            "PURCHASE DEBIT NOTE",
            lines,
            [new("Credit Total", ret.SupplierReturnValue, true)],
            string.IsNullOrWhiteSpace(ret.Note) ? [ret.Reason] : [ret.Reason, ret.Note],
            "Returned to supplier.");
    }
}

public sealed class ThakaMaterialChallanKindSource : IProductionDocumentKindSource
{
    private readonly EdgeRetailsDbContext _db;
    public ThakaMaterialChallanKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.ThakaMaterialChallan;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var issue = await _db.ThakaMaterialIssues.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Thaka material issue '{businessDocumentId}' not found for printing.");
        var items = await _db.ThakaMaterialIssueItems.AsNoTracking()
            .Where(x => x.MaterialIssueId == issue.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var project = await _db.ThakaProjects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == issue.ProjectId, cancellationToken);
        var customer = project is null
            ? null
            : await _db.Customers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == project.CustomerId, cancellationToken);
        var shop = await SaleReturnReceiptKindSource.PrimaryShopAsync(_db, cancellationToken);

        var lines = items.Select(item => new ProductionDocumentLine(
            item.ProductNameSnapshot,
            item.BaseQuantity,
            null,
            item.UnitCharge,
            item.LineCharge)).ToList();

        return new ProductionDocument(
            Kind,
            issue.Id,
            issue.ChallanNumber,
            issue.IssuedAt,
            shop?.ShopName ?? "Edge Retails",
            shop?.Address,
            shop?.Phone,
            customer?.Name,
            "THAKA MATERIAL DELIVERY CHALLAN",
            lines,
            [
                new("Material Charges", issue.TotalCharge),
                new("Material Cost", issue.TotalCost, true)
            ],
            string.IsNullOrWhiteSpace(issue.Note) ? [] : [issue.Note],
            project is null ? null : $"Project: {project.ProjectNumber} - {project.ProjectName}");
    }
}

public sealed class ThakaPaymentReceiptKindSource : IProductionDocumentKindSource
{
    private readonly EdgeRetailsDbContext _db;
    public ThakaPaymentReceiptKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.ThakaPaymentReceipt;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var payment = await _db.ThakaPayments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Thaka payment '{businessDocumentId}' not found for printing.");
        var project = await _db.ThakaProjects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payment.ProjectId, cancellationToken);
        var customer = project is null
            ? null
            : await _db.Customers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == project.CustomerId, cancellationToken);
        var shop = await SaleReturnReceiptKindSource.PrimaryShopAsync(_db, cancellationToken);

        var projectName = project?.ProjectName ?? "Unknown project";
        return new ProductionDocument(
            Kind,
            payment.Id,
            payment.ReceiptNumber,
            payment.RecordedAt,
            shop?.ShopName ?? "Edge Retails",
            shop?.Address,
            shop?.Phone,
            customer?.Name,
            "THAKA PAYMENT RECEIPT",
            [new($"Payment for project '{projectName}'", 1m, null, payment.Amount, payment.Amount)],
            [new("Amount Paid", payment.Amount, true)],
            string.IsNullOrWhiteSpace(payment.Note) ? [] : [payment.Note],
            $"Method: {payment.PaymentMethod}");
    }
}

public sealed class FinalSettlementStatementKindSource : IProductionDocumentKindSource
{
    private readonly EdgeRetailsDbContext _db;
    public FinalSettlementStatementKindSource(EdgeRetailsDbContext db) => _db = db;
    public ProductionDocumentKind Kind => ProductionDocumentKind.FinalSettlementStatement;

    public async Task<ProductionDocument> LoadAsync(
        Guid businessDocumentId,
        CancellationToken cancellationToken = default)
    {
        var settlement = await _db.ThakaSettlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == businessDocumentId, cancellationToken)
            ?? throw new InvalidOperationException($"Thaka settlement '{businessDocumentId}' not found for printing.");
        var project = await _db.ThakaProjects.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == settlement.ProjectId, cancellationToken);
        var customer = project is null
            ? null
            : await _db.Customers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == project.CustomerId, cancellationToken);
        var shop = await SaleReturnReceiptKindSource.PrimaryShopAsync(_db, cancellationToken);

        var lines = new List<ProductionDocumentLine>
        {
            new("Material Charges", 1m, null, settlement.GrossMaterialChargesSnapshot, settlement.GrossMaterialChargesSnapshot),
            new("Payments Collected", 1m, null, settlement.PaymentsCollectedSnapshot, settlement.PaymentsCollectedSnapshot),
            new("Settlement Discount", 1m, null, settlement.SettlementDiscount, settlement.SettlementDiscount),
            new("Final Payment", 1m, null, settlement.FinalPaymentAmount, settlement.FinalPaymentAmount)
        };

        return new ProductionDocument(
            Kind,
            settlement.Id,
            settlement.SettlementNumber,
            settlement.SettledAt,
            shop?.ShopName ?? "Edge Retails",
            shop?.Address,
            shop?.Phone,
            customer?.Name,
            "THAKA PROJECT FINAL SETTLEMENT STATEMENT",
            lines,
            [new("Balance Before Settlement", settlement.BalanceBeforeSettlement, true)],
            [],
            project is null ? "Final Status: Closed" : $"Project: {project.ProjectNumber} - Final Status: Closed");
    }
}
