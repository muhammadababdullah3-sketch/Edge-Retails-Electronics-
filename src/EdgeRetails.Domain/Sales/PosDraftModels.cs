using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Sales;

public enum PosDraftStatus
{
    Open = 1,
    Converted = 2,
    Cancelled = 3,
    Expired = 4
}

public sealed class PosDraft : Entity
{
    public string DraftNumber { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Guid CreatedBy { get; set; }
    public string? TerminalId { get; set; }
    public PosDraftStatus Status { get; set; } = PosDraftStatus.Open;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long Version { get; set; }
}

public sealed class PosDraftItem : Entity
{
    public Guid DraftId { get; set; }
    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public decimal EnteredQuantity { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal DisplayedUnitPriceSnapshot { get; set; }
    public Guid? SelectedInventoryUnitId { get; set; }
    public string? Note { get; set; }
}
