using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Purchasing;

public static class PurchaseReceiptCost
{
    public static decimal Round(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    // Cumulative shares of the frozen landed line value preserve carrying cost
    // across partial receipts, including the final division/rounding residual.
    public static decimal Allocate(decimal orderedQuantity, decimal effectiveLineCost,
        decimal alreadyReceived, decimal receiptQuantity, decimal priorCarryingValue)
    {
        var cumulativeQuantity = alreadyReceived + receiptQuantity;
        var cumulativeValue = cumulativeQuantity == orderedQuantity
            ? effectiveLineCost
            : Round(effectiveLineCost * (cumulativeQuantity / orderedQuantity));
        var amount = Round(cumulativeValue - priorCarryingValue);
        if (amount < 0)
        {
            throw new BusinessRuleException("purchasing.purchase_cost_snapshot_invalid",
                "Purchase effective cost snapshot cannot fund the outstanding receipt.");
        }
        return amount;
    }

    public static decimal PhysicalAcquisitionCost(decimal receiptValue, int count, int index)
    {
        return Round(receiptValue * (index + 1) / count) - Round(receiptValue * index / count);
    }
}
