using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.Production;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Printing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase9;

public sealed class Phase9LabelIntentPostgresTests
{
    [Fact]
    public async Task PhysicalLabelPrintIntent_TableExists_AndCoordinatorPersistsOnPostgres()
    {
        await using var provider = await CreateMigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IPhysicalLabelPrintIntentWriter>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IPhysicalLabelPrintIntentCoordinator>();

        var unitId = Guid.CreateVersion7();
        var opId = Guid.CreateVersion7();
        writer.AddOriginalIntents(opId, Guid.CreateVersion7(), [unitId], DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var pending = await coordinator.ListPendingAsync(opId, 10, CancellationToken.None);
        Assert.Single(pending);
        Assert.Equal(PhysicalLabelPrintIntentStatus.PendingDispatch, pending[0].Status);

        var claimed = await coordinator.ClaimOriginalForUnitAsync(unitId, "INTEGRATION-WS", false, CancellationToken.None);
        Assert.True(claimed.IsSuccess);

        await coordinator.ApplyStickerReceiptAsync(
            new ApplyLabelIntentStickerReceipt(unitId, false, true, null, Guid.CreateVersion7()),
            CancellationToken.None);

        var row = await db.PhysicalLabelPrintIntents.AsNoTracking().SingleAsync(x => x.InventoryUnitId == unitId);
        Assert.Equal(PhysicalLabelPrintIntentStatus.Succeeded, row.Status);
        Assert.Equal("INTEGRATION-WS", row.TargetWorkstationId);
    }

    private static async Task<ServiceProvider> CreateMigratedProviderAsync()
    {
        var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.MigrateAsync();
        return provider;
    }

    private static ServiceProvider CreateProvider()
    {
        var connectionString = ResolveConnectionString();
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        return services.BuildServiceProvider();
    }

    private static string ResolveConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var builtConn =
                    $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(builtConn, performLiveProbe: false);
                return builtConn;
            }
        }

        throw new InvalidOperationException(
            "FAIL-CLOSED: Isolated test connection is not configured. Provide EDGE_RETAILS_TEST_DB or disposable cluster credential file.");
    }
}
