using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using Xunit;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE. These tests support, and do not replace, real PostgreSQL proof.
public sealed class Phase7Pass5CommercialTests
{
    [Theory]
    [InlineData("reason")]
    [InlineData("actor")]
    [InlineData("direction")]
    [InlineData("precision")]
    public async Task InvalidManualCashCreatesNoMovementAuditOrSave(string invalid)
    {
        var f = new Phase2TestDoubles();
        var actor = Guid.NewGuid();
        var command = new RecordManualCashMovementCommand(CashMovementDirection.In, 1m, actor, "Owner float", null);
        command = invalid switch
        {
            "reason" => command with { Reason = " " },
            "actor" => command with { ActorId = Guid.Empty },
            "direction" => command with { Direction = (CashMovementDirection)99 },
            _ => command with { Amount = 0.004m }
        };
        var result = await new RecordManualCashMovementHandler(f.CashMovements, f.Transactions, f.UnitOfWork, f.Audit)
            .HandleAsync(command, default);
        Assert.False(result.IsSuccess);
        Assert.Empty(f.Cash.Movements);
        Assert.Empty(f.Audit.Records);
        Assert.Equal(0, f.UnitOfWork.SavedCount);
    }

    [Theory]
    [InlineData(CashMovementDirection.In)]
    [InlineData(CashMovementDirection.Out)]
    public async Task ManualCashSavesOneMovementWithActorReasonAndAudit(CashMovementDirection direction)
    {
        var f = new Phase2TestDoubles();
        var actor = Guid.NewGuid();
        f.Cash.Sessions.Add(Guid.NewGuid(), new CashSession { Status = CashSessionStatus.Open });
        var result = await new RecordManualCashMovementHandler(f.CashMovements, f.Transactions, f.UnitOfWork, f.Audit)
            .HandleAsync(new(direction, 1.005m, actor, " Owner float ", null), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var movement = Assert.Single(f.Cash.Movements);
        Assert.Equal(result.Value, movement.Id);
        Assert.Equal(actor, movement.ActorId);
        Assert.Equal("Owner float", movement.Reason);
        Assert.Equal(1.01m, movement.Amount);
        Assert.Equal(direction, movement.Direction);
        Assert.Null(movement.SourceType);
        Assert.Null(movement.SourceId);
        var audit = Assert.Single(f.Audit.Records);
        Assert.Equal(movement.Id, audit.EntityId);
        Assert.Equal(actor, audit.ActorId);
        Assert.Equal(movement.Id, audit.CorrelationId);
        Assert.Equal(direction == CashMovementDirection.In ? "MANUAL_CASH_IN" : "MANUAL_CASH_OUT", audit.Action);
        Assert.Equal(1, f.UnitOfWork.SavedCount);
    }

    // =========================================================================
    // C26 MONETARY PRECISION & RESIDUAL ALLOCATION TESTS
    // =========================================================================

    [Fact]
    public void C26_AwayFromZeroRounding_RejectsBankersRoundingAcrossAllMidpoints()
    {
        // Under IEEE 754 / Banker's ToEven rounding, even-preceding midpoints round towards zero (down for positive).
        // Under MidpointRounding.AwayFromZero, they must round strictly away from zero.
        Assert.Equal(1.01m, MoneyRoundingPolicy.Round(1.005m));
        Assert.Equal(1.02m, MoneyRoundingPolicy.Round(1.015m));
        Assert.Equal(1.03m, MoneyRoundingPolicy.Round(1.025m)); // ToEven would be 1.02m
        Assert.Equal(1.04m, MoneyRoundingPolicy.Round(1.035m));
        Assert.Equal(1.05m, MoneyRoundingPolicy.Round(1.045m)); // ToEven would be 1.04m
        Assert.Equal(1.07m, MoneyRoundingPolicy.Round(1.065m)); // ToEven would be 1.06m
        Assert.Equal(-1.03m, MoneyRoundingPolicy.Round(-1.025m)); // ToEven would be -1.02m
        Assert.Equal(-1.05m, MoneyRoundingPolicy.Round(-1.045m)); // ToEven would be -1.04m
    }

    [Fact]
    public void C26_FractionalThreeWaySplit_SumEqualsDiscountExactAndNoPennyLost()
    {
        // 3 items with gross totals 33.33 each (subtotal 99.99), total invoice discount = 10.00
        decimal[] grossLines = [33.33m, 33.33m, 33.33m];
        const decimal totalDiscount = 10.00m;

        var salesShares = SaleMath.AllocateInvoiceDiscount(grossLines, totalDiscount);
        var purchaseShares = PurchaseMath.AllocateOtherCharges(grossLines, totalDiscount);

        // Deterministic cumulative allocation:
        // i=0: Round(10.00 * 33.33 / 99.99) = 3.33m
        // i=1: Round(10.00 * 66.66 / 99.99) = 6.67m -> share = 6.67 - 3.33 = 3.34m
        // i=2: total - 6.67 = 3.33m
        decimal[] expectedShares = [3.33m, 3.34m, 3.33m];

        Assert.Equal(expectedShares, salesShares);
        Assert.Equal(expectedShares, purchaseShares);
        Assert.Equal(totalDiscount, salesShares.Sum());
        Assert.Equal(totalDiscount, purchaseShares.Sum());

        // Document invariant: net line totals sum exactly to subtotal - discount
        var subtotal = grossLines.Sum();
        var netLineTotals = grossLines.Zip(salesShares, (gross, disc) => gross - disc).ToArray();
        Assert.Equal(subtotal - totalDiscount, netLineTotals.Sum());
        Assert.Equal(89.99m, netLineTotals.Sum());
    }

    [Fact]
    public void C26_FractionalSevenWaySplit_SumEqualsDiscountOrChargesExact()
    {
        // 7 items with gross total 10.00 each (subtotal 70.00), total allocation = 10.00
        decimal[] lines = [10.00m, 10.00m, 10.00m, 10.00m, 10.00m, 10.00m, 10.00m];
        const decimal documentAdjustment = 10.00m;

        var shares = SaleMath.AllocateInvoiceDiscount(lines, documentAdjustment);

        // Deterministic cumulative rounding yields 6 lines of 1.43m and 1 line of 1.42m
        decimal[] expectedShares = [1.43m, 1.43m, 1.43m, 1.42m, 1.43m, 1.43m, 1.43m];
        Assert.Equal(expectedShares, shares);
        Assert.Equal(documentAdjustment, shares.Sum());
        Assert.All(shares, share => Assert.True(share > 0m));

        // Uneven 7-way split
        decimal[] unevenLines = [7.11m, 13.25m, 22.80m, 45.19m, 9.99m, 88.45m, 3.12m];
        const decimal charges = 23.47m;
        var chargeShares = PurchaseMath.AllocateOtherCharges(unevenLines, charges);

        Assert.Equal(charges, chargeShares.Sum());
        Assert.All(chargeShares, share => Assert.True(share >= 0m));

        var effectiveTotals = unevenLines.Zip(chargeShares, (baseLine, charge) => baseLine + charge).ToArray();
        Assert.Equal(unevenLines.Sum() + charges, effectiveTotals.Sum());
    }

    [Fact]
    public void C26_MixedQuantitiesAndLines_PreservesSubtotalDiscountAndChargeInvariants()
    {
        // Mixed commercial basket:
        // Line 1: 3 @ 19.99 = 59.97
        // Line 2: 7 @ 7.49  = 52.43
        // Line 3: 1 @ 125.50 = 125.50
        // Line 4: 12 @ 2.15 = 25.80
        decimal[] lineTotals = [59.97m, 52.43m, 125.50m, 25.80m];
        var subtotal = lineTotals.Sum(); // 263.70m
        const decimal discount = 35.15m;
        const decimal otherCharges = 18.25m;

        // 1. Sales Discount Allocation
        var discountAllocations = SaleMath.AllocateInvoiceDiscount(lineTotals, discount);
        Assert.Equal(discount, discountAllocations.Sum());
        var netLines = lineTotals.Zip(discountAllocations, (gross, disc) => gross - disc).ToArray();
        Assert.Equal(subtotal - discount, netLines.Sum());
        for (var i = 0; i < lineTotals.Length; i++)
        {
            Assert.True(discountAllocations[i] >= 0m);
            Assert.True(discountAllocations[i] <= lineTotals[i]);
            Assert.True(netLines[i] >= 0m);
        }

        // 2. Purchasing Charges Allocation
        var chargeAllocations = PurchaseMath.AllocateOtherCharges(lineTotals, otherCharges);
        Assert.Equal(otherCharges, chargeAllocations.Sum());
        var effectiveLines = lineTotals.Zip(chargeAllocations, (b, c) => b + c).ToArray();
        Assert.Equal(subtotal + otherCharges, effectiveLines.Sum());
        Assert.All(chargeAllocations, c => Assert.True(c >= 0m));
    }

    [Fact]
    public void C26_ProportionalAllocation_IsDeterministicAndOrderStable()
    {
        decimal[] weights = [19.99m, 45.50m, 12.00m, 89.95m, 1.25m];
        const decimal total = 17.77m;

        var baseline = MoneyRoundingPolicy.Allocate(total, weights);

        // 100 consecutive executions must produce identical array elements
        for (var i = 0; i < 100; i++)
        {
            var repeated = MoneyRoundingPolicy.Allocate(total, weights);
            Assert.Equal(baseline, repeated);
            Assert.Equal(total, repeated.Sum());
        }
    }

    [Fact]
    public void C26_PhysicalUnitAcquisitionCost_PreservesLineEffectiveCostWithoutResidualLoss()
    {
        // 3 serialized units sharing an effective line cost of 100.00m
        const decimal effectiveLineCost = 100.00m;
        const int count = 3;

        var unitCosts = Enumerable.Range(0, count)
            .Select(idx => PurchaseReceiptCost.PhysicalAcquisitionCost(effectiveLineCost, count, idx))
            .ToArray();

        // 6-decimal precision acquisition costs:
        // idx 0: 33.333333m
        // idx 1: 33.333334m
        // idx 2: 33.333333m
        decimal[] expected = [33.333333m, 33.333334m, 33.333333m];
        Assert.Equal(expected, unitCosts);
        Assert.Equal(effectiveLineCost, unitCosts.Sum());

        // 7 serialized units sharing 10.00m
        const decimal effective7 = 10.00m;
        var unitCosts7 = Enumerable.Range(0, 7)
            .Select(idx => PurchaseReceiptCost.PhysicalAcquisitionCost(effective7, 7, idx))
            .ToArray();
        Assert.Equal(effective7, unitCosts7.Sum());
    }

    [Fact]
    public void C26_AllocationValidation_RejectsNegativeOrInvalidInputs()
    {
        // Negative discount
        Assert.Throws<BusinessRuleException>(() =>
            SaleMath.AllocateInvoiceDiscount([10m, 20m], -1m));

        // Discount exceeding subtotal
        Assert.Throws<BusinessRuleException>(() =>
            SaleMath.AllocateInvoiceDiscount([10m, 20m], 30.01m));

        // Negative line total in sales
        Assert.Throws<BusinessRuleException>(() =>
            SaleMath.AllocateInvoiceDiscount([-10m, 20m], 5m));

        // Negative charges in purchasing
        Assert.Throws<BusinessRuleException>(() =>
            PurchaseMath.AllocateOtherCharges([10m, 20m], -0.01m));

        // Negative line total in purchasing
        Assert.Throws<BusinessRuleException>(() =>
            PurchaseMath.AllocateOtherCharges([10m, -5m], 2m));

        // Charges on zero subtotal
        Assert.Throws<BusinessRuleException>(() =>
            PurchaseMath.AllocateOtherCharges([0m, 0m], 5m));
    }
}
