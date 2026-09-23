using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Domain.Sales;

public enum SaleStatus
{
    Completed = 1
}

public enum SalePaymentStatus
{
    Paid = 1
}

public enum SalePaymentMethod
{
    Cash = 1,
    Bank = 2,
    Other = 3
}

public enum RefundMethod
{
    Cash = 1,
    Bank = 2,
    Other = 3
}

public enum SaleReturnDisposition
{
    RestockSellable = 1,
    Damaged = 2,
    Defective = 3,
    Scrap = 4
}

public sealed class Sale : Entity
{
    public string InvoiceNumber { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Guid CashierUserId { get; set; }
    public Guid? SessionId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal InvoiceDiscount { get; set; }
    public decimal GrandTotal { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Completed;
    public SalePaymentStatus PaymentStatus { get; set; } = SalePaymentStatus.Paid;
    public Guid ClientOperationId { get; set; }
    public string? ReceiptTemplateSnapshot { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SaleItem : Entity
{
    public Guid SaleId { get; set; }
    public Guid InventoryMovementId { get; set; }
    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? SkuSnapshot { get; set; }
    public decimal EnteredQuantity { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal GrossLineTotal { get; set; }
    public decimal AllocatedInvoiceDiscount { get; set; }
    public decimal NetLineTotal { get; set; }
    public decimal UnitCostSnapshot { get; set; }
    public decimal TotalCostSnapshot { get; set; }
    public decimal GrossProfitSnapshot { get; set; }
    public DateOnly? WarrantyValidUntil { get; set; }
}

public sealed class SalePayment : Entity
{
    public Guid SaleId { get; set; }
    public SalePaymentMethod Method { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal ChangeGiven { get; set; }
    public string? Reference { get; set; }
}

public sealed class SaleItemUnit : Entity
{
    public Guid SaleItemId { get; set; }
    public Guid InventoryUnitId { get; set; }
    public decimal UnitCostSnapshot { get; set; }
    public DateOnly? WarrantyValidUntil { get; set; }
}

public sealed class SaleReturn : Entity
{
    public string ReturnNumber { get; set; } = string.Empty;
    public Guid SaleId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? ReasonNote { get; set; }
    public RefundMethod RefundMethod { get; set; }
    public decimal RefundAmount { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid ClientOperationId { get; set; }
}

public sealed class SaleReturnItem : Entity
{
    public Guid SaleReturnId { get; set; }
    public Guid SaleItemId { get; set; }
    public Guid ProductId { get; set; }
    public decimal EnteredQuantity { get; set; }
    public Guid ProductUnitId { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public SaleReturnDisposition Disposition { get; set; }
    public decimal RefundAmount { get; set; }
    public decimal OriginalCostAmount { get; set; }
    public decimal CostReversalAmount { get; set; }
}

public sealed class SaleReturnItemUnit : Entity
{
    public Guid SaleReturnItemId { get; set; }
    public Guid InventoryUnitId { get; set; }
}

public static class SaleMath
{
    public static IReadOnlyList<decimal> AllocateInvoiceDiscount(
        IReadOnlyList<decimal> grossLineTotals,
        decimal invoiceDiscount)
    {
        if (grossLineTotals.Count == 0)
        {
            throw new BusinessRuleException("sales.items_required", "At least one sale item is required.");
        }

        if (grossLineTotals.Any(x => x < 0))
        {
            throw new BusinessRuleException("sales.negative_line_total", "Sale line totals cannot be negative.");
        }

        var subtotal = decimal.Round(grossLineTotals.Sum(), 2, MidpointRounding.AwayFromZero);
        var discount = decimal.Round(invoiceDiscount, 2, MidpointRounding.AwayFromZero);

        if (discount < 0 || discount > subtotal)
        {
            throw new BusinessRuleException(
                "sales.invalid_discount",
                "Invoice discount must be between zero and the subtotal.");
        }

        if (discount == 0)
        {
            return Enumerable.Repeat(0m, grossLineTotals.Count).ToArray();
        }

        if (subtotal <= 0)
        {
            throw new BusinessRuleException(
                "sales.discount_requires_positive_subtotal",
                "Invoice discount requires a positive subtotal.");
        }

        var allocations = new decimal[grossLineTotals.Count];
        var remaining = discount;

        for (var i = 0; i < grossLineTotals.Count; i++)
        {
            var allocated = i == grossLineTotals.Count - 1
                ? remaining
                : decimal.Round(
                    discount * grossLineTotals[i] / subtotal,
                    2,
                    MidpointRounding.AwayFromZero);

            allocations[i] = allocated;
            remaining = decimal.Round(remaining - allocated, 2, MidpointRounding.AwayFromZero);
        }

        return allocations;
    }

    public static InventoryBucket ToInventoryBucket(SaleReturnDisposition disposition) => disposition switch
    {
        SaleReturnDisposition.RestockSellable => InventoryBucket.Sellable,
        SaleReturnDisposition.Damaged => InventoryBucket.Damaged,
        SaleReturnDisposition.Defective => InventoryBucket.Defective,
        SaleReturnDisposition.Scrap => InventoryBucket.Scrap,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition))
    };
}

