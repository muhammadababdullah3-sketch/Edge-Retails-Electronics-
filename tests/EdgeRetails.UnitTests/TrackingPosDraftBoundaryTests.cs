using System.Reflection;
using System.Text.Json;
using EdgeRetails.Application.Features.Sales;

namespace EdgeRetails.UnitTests;

public sealed class TrackingPosDraftBoundaryTests
{
    [Fact]
    public void Public_sale_payload_cannot_supply_trusted_draft_context()
    {
        var command = new CompleteSaleCommand(Guid.NewGuid(), null, Guid.NewGuid(), null, 0m,
            EdgeRetails.Domain.Sales.SalePaymentMethod.Bank, 10m, null, null, []);
        var json = JsonSerializer.Serialize(command);
        var forged = json[..^1] + ",\"SourceDraftId\":\"" + Guid.NewGuid() + "\",\"SourceDraftVersion\":1,\"SourceDraftReplayOnly\":true}";
        var rebound = JsonSerializer.Deserialize<CompleteSaleCommand>(forged);
        Assert.NotNull(rebound);
        Assert.DoesNotContain("SourceDraft", JsonSerializer.Serialize(rebound), StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(CompleteSaleCommand).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            x => x.Name.StartsWith("SourceDraft", StringComparison.Ordinal));
    }
}
