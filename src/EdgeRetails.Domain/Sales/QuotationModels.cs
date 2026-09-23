using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Sales;

public enum QuotationStatus
{
    Draft = 1,
    Issued = 2,
    Expired = 3,
    Converted = 4,
    Cancelled = 5
}

public enum QuotationOperationType
{
    Create = 1,
    Update = 2,
    Issue = 3,
    Cancel = 4
}

public sealed class QuotationOperation : Entity
{
    public Guid QuotationId { get; set; }
    public Guid ClientOperationId { get; set; }
    public QuotationOperationType OperationType { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class Quotation : Entity
{
    public string QuotationNumber { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string? CustomerNameSnapshot { get; set; }
    public DateOnly QuotationDate { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public QuotationStatus Status { get; set; } = QuotationStatus.Draft;
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal GrandTotal { get; set; }
    public string? Notes { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? ConvertedSaleId { get; set; }
    public long Version { get; set; }

    public void PrepareForUpdate()
    {
        if (Status == QuotationStatus.Converted)
        {
            throw new BusinessRuleException(
                "sales.quotation_converted",
                "A converted quotation cannot be updated.");
        }

        if (Status != QuotationStatus.Draft)
        {
            Status = QuotationStatus.Draft;
            ConvertedSaleId = null;
            Version++;
        }
    }

    public void Issue()
    {
        if (Status != QuotationStatus.Draft)
        {
            throw new BusinessRuleException(
                "sales.quotation_not_draft",
                "Only a draft quotation can be issued.");
        }

        Status = QuotationStatus.Issued;
        Version++;
    }

    public void MarkConverted(Guid saleId, DateOnly today)
    {
        if (Status != QuotationStatus.Issued)
        {
            throw new BusinessRuleException(
                "sales.quotation_not_issued",
                "Only an issued quotation can be converted.");
        }

        if (ValidUntil is not null && today > ValidUntil.Value)
        {
            Status = QuotationStatus.Expired;
            Version++;
            throw new BusinessRuleException(
                "sales.quotation_expired",
                "Expired quotation cannot be converted.");
        }

        ConvertedSaleId = saleId;
        Status = QuotationStatus.Converted;
        Version++;
    }

    public void Cancel()
    {
        if (Status == QuotationStatus.Converted)
        {
            throw new BusinessRuleException(
                "sales.quotation_converted",
                "A converted quotation cannot be cancelled.");
        }

        if (Status == QuotationStatus.Cancelled)
        {
            return;
        }

        Status = QuotationStatus.Cancelled;
        Version++;
    }
}

public sealed class QuotationItem : Entity
{
    public Guid QuotationId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public Guid SelectedUnitId { get; set; }
    public decimal EnteredQuantity { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal QuotedUnitPrice { get; set; }
    public decimal LineTotal { get; set; }

    public static QuotationItem Create(
        Guid quotationId,
        Product product,
        ProductUnit productUnit,
        decimal enteredQuantity,
        decimal quotedUnitPrice)
    {
        if (!productUnit.CanSell || !productUnit.IsActive)
        {
            throw new BusinessRuleException(
                "sales.quotation_unit_not_sellable",
                "Selected unit is not allowed for sale.");
        }

        if (quotedUnitPrice < 0)
        {
            throw new BusinessRuleException(
                "sales.quotation_price_negative",
                "Quoted price cannot be negative.");
        }

        var quantity = TransactionQuantitySnapshot.Create(
            productUnit,
            enteredQuantity,
            product.TrackingMode);

        return new QuotationItem
        {
            QuotationId = quotationId,
            ProductId = product.Id,
            ProductName = product.Name,
            Sku = product.Sku,
            SelectedUnitId = productUnit.UnitId,
            EnteredQuantity = quantity.EnteredQuantity,
            FactorToBaseSnapshot = quantity.FactorToBaseSnapshot,
            BaseQuantity = quantity.BaseQuantity,
            QuotedUnitPrice = decimal.Round(quotedUnitPrice, 2, MidpointRounding.AwayFromZero),
            LineTotal = decimal.Round(
                quantity.EnteredQuantity * quotedUnitPrice,
                2,
                MidpointRounding.AwayFromZero)
        };
    }
}
