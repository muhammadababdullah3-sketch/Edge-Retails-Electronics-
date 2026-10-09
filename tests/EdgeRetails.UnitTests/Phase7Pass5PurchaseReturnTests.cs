using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: supporting canonical refund validation; relational authority is PostgreSQL.
public sealed class Phase7Pass5PurchaseReturnTests
{
    [Theory]
    [InlineData(SupplierSettlementMethod.Bank)]
    [InlineData(SupplierSettlementMethod.Other)]
    public async Task CanonicalNonDrawerRefundHasIndependentFactsAndReplay(SupplierSettlementMethod method)
    {
        var (f, handler, supplier) = Arrange(100m);
        var command = new CreateSupplierRefundCommand(supplier, 40m, method, Guid.NewGuid(), Guid.NewGuid(),
            "EXTERNAL", "PurchaseReturn", Guid.NewGuid(), "Approved receipt");
        var result = await handler.HandleAsync(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var replay = await handler.HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        var refund = Assert.Single(f.SupplierAccounts.Refunds.Values);
        Assert.Equal(result.Value!.RefundId, refund.Id);
        Assert.Equal(method, refund.Method);
        Assert.Equal(command.ReferenceId, refund.ReferenceId);
        Assert.Equal("PurchaseReturn", refund.ReferenceType);
        Assert.Equal(40m, refund.Amount);
        Assert.Empty(f.Cash.Movements);
        var entry = Assert.Single(f.SupplierAccounts.Entries, x => x.EntryType == SupplierAccountEntryType.SupplierRefundReceived);
        Assert.Equal(refund.Id, entry.ReferenceId);
        Assert.Equal("SupplierRefund", entry.ReferenceType);
        Assert.Equal(-60m, await f.SupplierAccounts.GetCurrentBalanceAsync(supplier, default));
        Assert.Single(f.Audit.Records);
        var conflict = await handler.HandleAsync(command with { Amount = 30m }, default);
        Assert.False(conflict.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", conflict.Error!.Code);
        Assert.Single(f.SupplierAccounts.Refunds);
        Assert.Single(f.Audit.Records);
    }

    [Theory]
    [InlineData("0.004", SupplierSettlementMethod.Bank, "supplier.refund_invalid")]
    [InlineData("1.005", SupplierSettlementMethod.Bank, "supplier.refund_exceeds_credit")]
    [InlineData("1", (SupplierSettlementMethod)99, "supplier.refund_invalid")]
    public async Task MonetaryPrecisionAndMethodValidationCannotInventCredit(string amountText, SupplierSettlementMethod method, string error)
    {
        var (f, handler, supplier) = Arrange(1m);
        var result = await handler.HandleAsync(new(supplier, decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture),
            method, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error!.Code);
        Assert.Empty(f.SupplierAccounts.Refunds);
        Assert.Empty(f.Cash.Movements);
        Assert.Single(f.SupplierAccounts.Entries);
        Assert.Empty(f.Audit.Records);
    }

    private static (Phase2TestDoubles Fakes, CreateSupplierRefundHandler Handler, Guid Supplier) Arrange(decimal credit)
    {
        var f = new Phase2TestDoubles();
        var supplier = new Supplier { Name = "Canonical C01 unit fixture", DealerCode = "C01", IsActive = true };
        f.Parties.AddSupplier(supplier);
        f.SupplierAccounts.AddEntry(new SupplierAccountEntry
        {
            SupplierId = supplier.Id, EntryType = SupplierAccountEntryType.OpeningBalance,
            Direction = SupplierAccountDirection.DecreasePayable, Amount = credit
        });
        return (f, new CreateSupplierRefundHandler(f.SupplierAccounts, f.Parties, f.Cash, f.OperationLock, f.ResourceLock,
            f.Authorization, f.Numbers, f.Clock, f.Audit, f.Transactions, f.UnitOfWork), supplier.Id);
    }
}
