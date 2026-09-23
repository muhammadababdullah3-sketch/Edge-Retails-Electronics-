using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Purchasing;

public enum PurchaseStatus
{
    Completed = 1,
    Voided = 2
}

public enum PurchaseSettlementMode
{
    External = 1,
    CashDrawer = 2
}

public enum PurchaseReturnStatus
{
    Completed = 1
}

public enum PurchaseReturnSettlementMode
{
    External = 1,
    CashDrawer = 2
}

public sealed class Purchase : Entity
{
    public string PurchaseNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public string NormalizedSupplierInvoiceNumber { get; set; } = string.Empty;
    public DateOnly PurchaseDate { get; set; }
    public string? Note { get; set; }
    public decimal Subtotal { get; set; }
    public decimal OtherCharges { get; set; }
    public decimal GrandTotal { get; set; }
    public PurchaseStatus Status { get; set; } = PurchaseStatus.Completed;
    public PurchaseSettlementMode SettlementMode { get; set; } = PurchaseSettlementMode.External;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid ClientOperationId { get; set; }
    public long Version { get; set; }
}

public sealed class PurchaseItem : Entity
{
    public Guid PurchaseId { get; set; }
    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? SkuSnapshot { get; set; }
    public decimal EnteredQuantity { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal EnteredUnitCost { get; set; }
    public decimal BaseLineTotal { get; set; }
    public decimal AllocatedOtherCost { get; set; }
    public decimal EffectiveBaseUnitCost { get; set; }
    public decimal EffectiveLineCost { get; set; }
    public decimal SalePriceAtPurchase { get; set; }
}

public sealed class PurchaseItemUnit : Entity
{
    public Guid PurchaseItemId { get; set; }
    public Guid InventoryUnitId { get; set; }
}

public sealed class PurchaseReturn : Entity
{
    public string ReturnNumber { get; set; } = string.Empty;
    public Guid PurchaseId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Note { get; set; }
    public PurchaseReturnStatus Status { get; set; } = PurchaseReturnStatus.Completed;
    public PurchaseReturnSettlementMode SettlementMode { get; set; } = PurchaseReturnSettlementMode.External;
    public decimal SupplierReturnValue { get; set; }
    public decimal InventoryCostRemoved { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid ClientOperationId { get; set; }
}

public sealed class PurchaseReturnItem : Entity
{
    public Guid PurchaseReturnId { get; set; }
    public Guid PurchaseItemId { get; set; }
    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public decimal EnteredQuantity { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal SupplierUnitReturnValue { get; set; }
    public decimal SupplierReturnValue { get; set; }
    public decimal InventoryUnitCostRemoved { get; set; }
    public decimal InventoryCostRemoved { get; set; }
}

public sealed class PurchaseReturnItemUnit : Entity
{
    public Guid PurchaseReturnItemId { get; set; }
    public Guid InventoryUnitId { get; set; }
}

public sealed class PurchaseVoid : Entity
{
    public Guid PurchaseId { get; set; }
    public Guid ClientOperationId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid VoidedBy { get; set; }
    public DateTimeOffset VoidedAt { get; set; }
    public decimal? CashDrawerReversalAmount { get; set; }
}

public static class PurchaseMath
{
    public static string NormalizeSupplierInvoiceNumber(string value)
    {
        var normalized = string.Join(
            ' ',
            value.Trim().ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Length == 0)
        {
            throw new BusinessRuleException(
                "purchasing.supplier_invoice_required",
                "Supplier invoice number is required.");
        }

        return normalized;
    }

    public static IReadOnlyList<decimal> AllocateOtherCharges(
        IReadOnlyList<decimal> baseLineTotals,
        decimal otherCharges)
    {
        if (baseLineTotals.Count == 0)
        {
            throw new BusinessRuleException(
                "purchasing.items_required",
                "At least one purchase item is required.");
        }

        if (baseLineTotals.Any(x => x < 0))
        {
            throw new BusinessRuleException(
                "purchasing.negative_line_total",
                "Purchase line totals cannot be negative.");
        }

        var subtotal = decimal.Round(baseLineTotals.Sum(), 2, MidpointRounding.AwayFromZero);
        var charges = decimal.Round(otherCharges, 2, MidpointRounding.AwayFromZero);

        if (charges < 0)
        {
            throw new BusinessRuleException(
                "purchasing.other_charges_negative",
                "Other charges cannot be negative.");
        }

        if (charges > 0 && subtotal <= 0)
        {
            throw new BusinessRuleException(
                "purchasing.other_charges_require_subtotal",
                "Other charges require a positive purchase subtotal.");
        }

        if (charges == 0)
        {
            return Enumerable.Repeat(0m, baseLineTotals.Count).ToArray();
        }

        var allocations = new decimal[baseLineTotals.Count];
        var remaining = charges;

        for (var i = 0; i < baseLineTotals.Count; i++)
        {
            var allocated = i == baseLineTotals.Count - 1
                ? remaining
                : decimal.Round(
                    charges * baseLineTotals[i] / subtotal,
                    2,
                    MidpointRounding.AwayFromZero);

            allocations[i] = allocated;
            remaining = decimal.Round(
                remaining - allocated,
                2,
                MidpointRounding.AwayFromZero);
        }

        return allocations;
    }
}

