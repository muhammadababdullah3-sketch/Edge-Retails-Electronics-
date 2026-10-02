using System.Text.Json;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase3PosDraftResponseLossTests
{
    [Fact]
    public async Task LostSaveResponse_ReplayingSameIntentReturnsOneDraft()
    {
        var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        var command = fixture.Command(operationId);

        var first = await fixture.Handler.HandleAsync(command, CancellationToken.None);
        var retry = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(retry.IsSuccess, retry.Error?.Message);
        Assert.Equal(first.Value!.DraftId, retry.Value!.DraftId);
        Assert.Single(fixture.Fakes.PosDrafts.Drafts);
    }

    [Fact]
    public async Task ReusingDraftOperationIdWithDifferentPayloadIsRejected()
    {
        var fixture = new Fixture();
        var operationId = Guid.NewGuid();

        var first = await fixture.Handler.HandleAsync(
            fixture.Command(operationId), CancellationToken.None);
        var second = await fixture.Handler.HandleAsync(
            fixture.Command(operationId, note: "different hold"), CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", second.Error?.Code);
        Assert.Single(fixture.Fakes.PosDrafts.Drafts);
    }

    private sealed class Fixture
    {
        public Phase2TestDoubles Fakes { get; } = new();
        public Guid ProductId { get; } = Guid.NewGuid();
        public Guid ProductUnitId { get; } = Guid.NewGuid();
        public Guid ActorId { get; } = Guid.NewGuid();
        public SavePosDraftHandler Handler { get; }

        public Fixture()
        {
            Fakes.Catalog.Products[ProductId] = new Product
            {
                Id = ProductId,
                Name = "Cable",
                Sku = "CABLE-01",
                TrackingMode = TrackingMode.Quantity,
                DefaultSalePrice = 25m,
                IsActive = true
            };
            Fakes.Catalog.AddProductUnit(new ProductUnit
            {
                Id = ProductUnitId,
                ProductId = ProductId,
                UnitId = Guid.NewGuid(),
                FactorToBaseUnit = 1m,
                CanSell = true,
                IsActive = true
            });
            Handler = new SavePosDraftHandler(
                Fakes.PosDrafts,
                Fakes.Catalog,
                Fakes.Numbers,
                Fakes.Clock,
                Fakes.Authorization,
                Fakes.Transactions,
                Fakes.UnitOfWork,
                Fakes.OperationLock,
                Fakes.OutcomeLedger);
        }

        // Serialize the HTTP payload so this test also catches a silently ignored operation ID.
        public SavePosDraftCommand Command(Guid operationId, string note = "hold")
        {
            var payload = new
            {
                draftId = (Guid?)null,
                expectedVersion = (long?)null,
                customerId = (Guid?)null,
                actorId = ActorId,
                terminalId = "LOCAL",
                note,
                items = new[]
                {
                    new
                    {
                        productId = ProductId,
                        productUnitId = ProductUnitId,
                        enteredQuantity = 1m,
                        selectedInventoryUnitId = (Guid?)null,
                        note = (string?)null
                    }
                },
                clientOperationId = operationId
            };
            return JsonSerializer.Deserialize<SavePosDraftCommand>(
                JsonSerializer.Serialize(payload),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }
}
