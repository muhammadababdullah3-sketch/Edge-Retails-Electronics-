using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Finance;

public enum CashSessionStatus
{
    Open = 1,
    Closed = 2,
    VoidedByOwner = 3
}

public enum CashMovementDirection
{
    In = 1,
    Out = 2
}

public enum CashMovementType
{
    SaleCashIn = 1,
    SaleRefundCashOut = 2,
    ThakaPaymentCashIn = 3,
    ExpenseCashOut = 4,
    ManualCashIn = 5,
    ManualCashOut = 6,
    PurchaseCashOut = 7,
    PurchaseVoidCashIn = 8,
    PurchaseReturnCashIn = 9,
    ThakaPaymentReversalCashOut = 10,
    SupplierPaymentCashOut = 11,
    SupplierPaymentReversalCashIn = 12,
    SupplierRefundCashIn = 13,
    SupplierRefundReversalCashOut = 14
}

public sealed class CashSession : Entity
{
    public DateOnly BusinessDate { get; set; }
    public Guid OpenedBy { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public decimal OpeningCash { get; set; }
    public CashSessionStatus Status { get; set; } = CashSessionStatus.Open;
    public decimal? ExpectedClosingCash { get; set; }
    public decimal? CountedClosingCash { get; set; }
    public decimal? Difference { get; set; }
    public Guid? ClosedBy { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? Note { get; set; }
    public long Version { get; set; }

    public void Close(
        decimal cashIn,
        decimal cashOut,
        decimal countedCash,
        Guid closedBy,
        DateTimeOffset now)
    {
        if (Status != CashSessionStatus.Open)
        {
            throw new BusinessRuleException(
                "cash.session_not_open",
                "Only an open cash session can be closed.");
        }

        if (countedCash < 0)
        {
            throw new BusinessRuleException(
                "cash.counted_negative",
                "Counted closing cash cannot be negative.");
        }

        var expected = decimal.Round(OpeningCash + cashIn - cashOut, 2, MidpointRounding.AwayFromZero);
        var counted = decimal.Round(countedCash, 2, MidpointRounding.AwayFromZero);

        ExpectedClosingCash = expected;
        CountedClosingCash = counted;
        Difference = decimal.Round(counted - expected, 2, MidpointRounding.AwayFromZero);
        ClosedBy = closedBy;
        ClosedAt = now;
        Status = CashSessionStatus.Closed;
        Version++;
    }
}

public sealed class CashMovement : Entity
{
    public Guid CashSessionId { get; set; }
    public CashMovementType MovementType { get; set; }
    public CashMovementDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string? SourceType { get; set; }
    public Guid? SourceId { get; set; }
    public string? Reason { get; set; }
    public string? Note { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public decimal SignedAmount =>
        Direction == CashMovementDirection.In ? Amount : -Amount;
}
