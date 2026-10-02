using EdgeRetails.Application.Production;
using EdgeRetails.Infrastructure.Production.Backup;

namespace EdgeRetails.UnitTests;

public sealed class Phase3ConditionalRestoreBarrierTests
{
    [Fact]
    public async Task ConditionalTransition_AllowsNormalToRestorePreparing()
    {
        var root = CreateRoot();
        var barrier = new FileProductionMaintenanceBarrier(root, new FixedIntegrityKeyProvider());

        try
        {
            await using (var lease = await barrier.EnterExclusiveAsync(
                ProductionMaintenanceState.Normal,
                ProductionMaintenanceState.RestorePreparing))
            {
                Assert.Equal(ProductionMaintenanceState.RestorePreparing, await barrier.GetStateAsync());
                await lease.SetExitStateAsync(ProductionMaintenanceState.Normal);
            }

            Assert.Equal(ProductionMaintenanceState.Normal, await barrier.GetStateAsync());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ConditionalTransition_RefusesUnexpectedCurrentStateAndPreservesRecoveryRequired()
    {
        var root = CreateRoot();
        var barrier = new FileProductionMaintenanceBarrier(root, new FixedIntegrityKeyProvider());

        try
        {
            await using (var lease = await barrier.EnterExclusiveAsync(ProductionMaintenanceState.RestoreCutover))
            {
                await lease.SetExitStateAsync(ProductionMaintenanceState.RecoveryRequired);
            }

            var error = await Assert.ThrowsAsync<ProductionMaintenanceException>(() =>
                barrier.EnterExclusiveAsync(
                    ProductionMaintenanceState.Normal,
                    ProductionMaintenanceState.RestorePreparing));

            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, error.State);
            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, await barrier.GetStateAsync());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ConditionalTransition_RacingRecoveryRequiredStateCannotBeOverwritten()
    {
        var root = CreateRoot();
        var barrier = new FileProductionMaintenanceBarrier(root, new FixedIntegrityKeyProvider());

        try
        {
            var restoreLease = await barrier.EnterExclusiveAsync(
                ProductionMaintenanceState.Normal,
                ProductionMaintenanceState.RestorePreparing);
            var contender = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                barrier.EnterExclusiveAsync(
                    ProductionMaintenanceState.Normal,
                    ProductionMaintenanceState.RestorePreparing));
            Assert.Contains("already active", contender.Message, StringComparison.OrdinalIgnoreCase);

            await restoreLease.SetExitStateAsync(ProductionMaintenanceState.RecoveryRequired);
            await restoreLease.DisposeAsync();

            var staleTransition = await Assert.ThrowsAsync<ProductionMaintenanceException>(() =>
                barrier.EnterExclusiveAsync(
                    ProductionMaintenanceState.Normal,
                    ProductionMaintenanceState.RestorePreparing));
            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, staleTransition.State);
            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, await barrier.GetStateAsync());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsPhase3ConditionalBarrier_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } } catch { }
    }

    private sealed class FixedIntegrityKeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray());
    }
}
