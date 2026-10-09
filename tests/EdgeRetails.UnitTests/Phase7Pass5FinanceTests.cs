using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using Xunit;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: source-proven negative final allocation and source loss caps.
public sealed class Phase7Pass5FinanceTests
{
    [Theory]
    [InlineData(EdgeRetails.Domain.Finance.SupplierAccountDirection.IncreasePayable)]
    [InlineData(EdgeRetails.Domain.Finance.SupplierAccountDirection.DecreasePayable)]
    public async Task SupplierOpeningIsAppendOnlyAndRetainsEconomicDirectionAndContext(EdgeRetails.Domain.Finance.SupplierAccountDirection direction)
    {
        var (f, handler, command) = OpeningFixture();
        command = command with { Direction = direction };
        var result = await handler.HandleAsync(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var replay = await handler.HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.True(replay.Value!.WasExisting);
        Assert.Equal(result.Value!.EntryId, replay.Value.EntryId);
        var entry = Assert.Single(f.SupplierAccounts.Entries);
        Assert.Equal(EdgeRetails.Domain.Finance.SupplierAccountEntryType.OpeningBalance, entry.EntryType);
        Assert.Equal(direction, entry.Direction);
        Assert.Equal(100.01m, entry.Amount);
        Assert.Equal(command.EffectiveAt.ToUniversalTime(), entry.OccurredAt);
        Assert.Equal(TimeSpan.Zero, entry.OccurredAt.Offset);
        Assert.Equal(command.ActorId, entry.ActorId);
        Assert.Equal(command.ClientOperationId, entry.ReferenceId);
        using var metadata = System.Text.Json.JsonDocument.Parse(entry.Note!);
        Assert.Equal("Approved cutover", metadata.RootElement.GetProperty("Reason").GetString());
        Assert.Equal("IMPORT-42", metadata.RootElement.GetProperty("CutoverReference").GetString());
        Assert.Equal(TimeSpan.FromHours(5), metadata.RootElement.GetProperty("EffectiveAt").GetDateTimeOffset().Offset);
        Assert.Single(f.Audit.Records);
        Assert.Empty(f.Cash.Movements);
        var conflict = await handler.HandleAsync(command with { Amount = 90m }, default);
        Assert.Equal("idempotency.payload_mismatch", conflict.Error?.Code);
        Assert.Single(f.SupplierAccounts.Entries);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("amount")]
    [InlineData("date")]
    [InlineData("reason")]
    public async Task SupplierOpeningRejectsInvalidAuthorityBeforeAnyFact(string invalid)
    {
        var (f, handler, command) = OpeningFixture();
        command = invalid switch
        {
            "direction" => command with { Direction = (EdgeRetails.Domain.Finance.SupplierAccountDirection)99 },
            "amount" => command with { Amount = 0.004m },
            "date" => command with { EffectiveAt = default },
            _ => command with { Reason = " " }
        };
        var result = await handler.HandleAsync(command, default);
        Assert.False(result.IsSuccess);
        Assert.Equal("supplier.opening_invalid", result.Error!.Code);
        Assert.Empty(f.SupplierAccounts.Entries);
        Assert.Empty(f.Audit.Records);
        Assert.Empty(f.Cash.Movements);
    }

    private static (Phase2TestDoubles Fakes, EdgeRetails.Application.Features.Finance.SupplierOpeningBalanceHandler Handler,
        EdgeRetails.Application.Features.Finance.SupplierOpeningBalanceCommand Command) OpeningFixture()
    {
        var f = new Phase2TestDoubles();
        var supplier = new EdgeRetails.Domain.Parties.Supplier { Name = "Opening fixture", DealerCode = "OPEN", IsActive = true };
        f.Parties.AddSupplier(supplier);
        var handler = new EdgeRetails.Application.Features.Finance.SupplierOpeningBalanceHandler(f.SupplierAccounts, f.Parties,
            f.Authorization, f.OperationLock, f.ResourceLock, f.Numbers, f.Clock, f.Audit, f.Transactions, f.UnitOfWork,
            new EdgeRetails.Application.Features.Terminals.InMemoryOperationOutcomeLedger());
        return (f, handler, new(supplier.Id, EdgeRetails.Domain.Finance.SupplierAccountDirection.IncreasePayable, 100.005m,
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(5)), " Approved cutover ", Guid.NewGuid(), Guid.NewGuid(), " IMPORT-42 "));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SmallEqualLinesNeverHaveNegativeFinalAllocation(bool purchasing)
    {
        decimal[] weights = [0.01m, 0.01m, 0.01m, 0.01m];
        var shares = purchasing ? PurchaseMath.AllocateOtherCharges(weights, 0.02m)
            : SaleMath.AllocateInvoiceDiscount(weights, 0.02m);
        Assert.Equal(new[] { 0.01m, 0m, 0.01m, 0m }, shares);
        Assert.Equal(0.02m, shares.Sum());
        Assert.All(shares, share => Assert.InRange(share, 0m, 0.01m));
    }

    [Theory]
    [InlineData("0.01", "0.004", "0.004")]
    [InlineData("1.01", "1", "2")]
    [InlineData("0", "0", "0")]
    public void SourceLossAllocationConservesRoundedAuthority(string totalText, string a, string b)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var total = decimal.Parse(totalText, culture);
        decimal[] weights = [decimal.Parse(a, culture), decimal.Parse(b, culture)];
        var shares = MoneyRoundingPolicy.Allocate(total, weights);
        Assert.Equal(total, shares.Sum());
        Assert.All(shares, x => Assert.InRange(x, 0m, total));
        Assert.Equal(shares, MoneyRoundingPolicy.Allocate(total, weights));
    }

    [Fact]
    public void MoneyMidpointUsesCanonicalAwayFromZero()
    {
        Assert.Equal(1.01m, MoneyRoundingPolicy.Round(1.005m));
        Assert.Equal(-1.01m, MoneyRoundingPolicy.Round(-1.005m));
    }

    [Fact]
    public void PositiveTotalWithoutWeightFailsRatherThanInventingAllocation() =>
        Assert.Throws<ArgumentException>(() => MoneyRoundingPolicy.Allocate(1m, new[] { 0m, 0m }));
}
