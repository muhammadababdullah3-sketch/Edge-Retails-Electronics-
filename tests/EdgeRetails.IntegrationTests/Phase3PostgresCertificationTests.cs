using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3PostgresCertificationTests
{
    [Fact]
    public async Task PosDraftSave_ResponseLossReplayAcrossFreshProviders_PersistsOneDraft()
    {
        var operationId = Guid.CreateVersion7();
        SavePosDraftCommand command;
        Guid firstDraftId;

        await using (var provider = Phase2PostgresTestHarness.BuildProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            command = new SavePosDraftCommand(
                DraftId: null,
                ExpectedVersion: null,
                CustomerId: null,
                ActorId: fixture.ActorId,
                TerminalId: "LOCAL",
                Note: "Hold for customer",
                Items: [new SavePosDraftItemInput(fixture.ProductId, fixture.ProductUnitId, 1m)],
                ClientOperationId: operationId);
            var handler = scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>();
            var first = await handler.HandleAsync(command, CancellationToken.None);
            Assert.True(first.IsSuccess, first.Error?.Message);
            firstDraftId = first.Value!.DraftId;
        }

        await using (var provider = Phase2PostgresTestHarness.BuildProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<SavePosDraftHandler>();
            var replay = await handler.HandleAsync(command, CancellationToken.None);
            Assert.True(replay.IsSuccess, replay.Error?.Message);
            Assert.Equal(firstDraftId, replay.Value!.DraftId);

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(1, await db.PosDrafts.CountAsync(x => x.Id == firstDraftId));
            Assert.Equal(1, await db.OperationOutcomes.CountAsync(
                x => x.ClientOperationId == operationId));
        }
    }

    [Fact]
    public async Task StocktakeCreate_ResponseLossReplayAcrossFreshProviders_PersistsOneIntentAndRejectsChangedPayload()
    {
        var operationId = Guid.CreateVersion7();
        CreateStocktakeCommand command;
        Guid? firstStocktakeId = null;

        try
        {
            await using (var provider = Phase2PostgresTestHarness.BuildProvider())
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
                command = new CreateStocktakeCommand(
                    StocktakeScope.FullShop,
                    null,
                    fixture.ActorId,
                    "Cycle count",
                    operationId);
                var handler = scope.ServiceProvider.GetRequiredService<CreateStocktakeHandler>();
                var first = await handler.HandleAsync(command, CancellationToken.None);
                Assert.True(first.IsSuccess, first.Error?.Message);
                firstStocktakeId = first.Value;
            }

            await using (var provider = Phase2PostgresTestHarness.BuildProvider())
            {
                await using var scope = provider.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<CreateStocktakeHandler>();
                var replay = await handler.HandleAsync(command, CancellationToken.None);
                Assert.True(replay.IsSuccess, replay.Error?.Message);
                Assert.Equal(firstStocktakeId, replay.Value);

                var changed = await handler.HandleAsync(command with { Note = "Different intent" }, CancellationToken.None);
                Assert.False(changed.IsSuccess);
                Assert.Equal("inventory.stocktake_operation_conflict", changed.Error?.Code);

                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                Assert.Equal(1, await db.Stocktakes.CountAsync(x => x.Id == firstStocktakeId));
                var outcome = await db.OperationOutcomes.SingleAsync(x => x.ClientOperationId == operationId);
                Assert.Equal("CreateStocktake", outcome.OperationType);
                Assert.False(string.IsNullOrWhiteSpace(outcome.PayloadFingerprint));
            }
        }
        finally
        {
            if (firstStocktakeId.HasValue)
            {
                await using var provider = Phase2PostgresTestHarness.BuildProvider();
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                await db.Stocktakes
                    .Where(x => x.Id == firstStocktakeId.Value)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.Status, StocktakeStatus.Cancelled));
            }
        }
    }
}
