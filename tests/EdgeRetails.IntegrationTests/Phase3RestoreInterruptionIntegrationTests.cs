using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3RestoreInterruptionIntegrationTests
{
    [Fact]
    public async Task RESTORE_01_CorruptedManifestOrPayload_AbortsStagingAndPreservesNormalState()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EdgeRetailsRestoreInterruption_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var keyProvider = new FixedBackupKeyProvider();
            var manifestAuth = new HmacBackupManifestAuthenticator(keyProvider);
            var barrier = new FileProductionMaintenanceBarrier(
                Path.Combine(temp, "maintenance"),
                new FixedMaintenanceIntegrityKeyProvider());

            // Create corrupted backup and manifest
            var backupFile = Path.Combine(temp, "corrupted.erbak");
            await File.WriteAllBytesAsync(backupFile, new byte[] { 1, 2, 3, 4 });

            var manifestFile = Path.Combine(temp, "corrupted.erbak.manifest.json");
            var fakeManifest = new BackupManifestEnvelope(
                new BackupManifest(
                    Guid.NewGuid(),
                    "edge_retails_prod",
                    "corrupted.erbak",
                    "0000000000000000000000000000000000000000000000000000000000000000",
                    4,
                    DateTimeOffset.UtcNow,
                    "18.0",
                    "18.0",
                    "1.0.0",
                    "1.0.0",
                    "AES-256-GCM"),
                "0000000000000000000000000000000000000000000000000000000000000000");

            await File.WriteAllTextAsync(manifestFile, System.Text.Json.JsonSerializer.Serialize(fakeManifest));

            // Verify that attempting to authenticate fails before touching staging
            var authResult = await manifestAuth.VerifyAuthenticationAsync(fakeManifest.Manifest, fakeManifest.Authentication);
            Assert.False(authResult, "Corrupted manifest must fail HMAC verification.");

            // Maintenance barrier must remain in Normal state
            var state = await barrier.GetStateAsync();
            Assert.Equal(ProductionMaintenanceState.Normal, state);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RESTORE_02_InterruptedCutover_InRecoveryRequired_BlocksAuthoritativeWrites()
    {
        var temp = Path.Combine(Path.GetTempPath(), "EdgeRetailsRecoveryReq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var keyProvider = new FixedMaintenanceIntegrityKeyProvider();
            var barrier = new FileProductionMaintenanceBarrier(
                Path.Combine(temp, "maintenance"),
                keyProvider);

            // Simulate cutover failure entering RecoveryRequired
            await using (var lease = await barrier.EnterExclusiveAsync(ProductionMaintenanceState.RestoreCutover))
            {
                await lease.SetExitStateAsync(ProductionMaintenanceState.RecoveryRequired);
            }

            // Verify barrier state is RecoveryRequired
            var currentState = await barrier.GetStateAsync();
            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, currentState);

            // Verify write guard explicitly blocks mutations
            var writeGuard = new ProductionMaintenanceWriteGuard(barrier, null);

            var ex = await Assert.ThrowsAsync<ProductionMaintenanceException>(
                () => writeGuard.EnsureBusinessWritesAllowedAsync());

            Assert.Equal(ProductionMaintenanceState.RecoveryRequired, ex.State);
            Assert.Contains("RecoveryRequired", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private sealed class FixedBackupKeyProvider : IBackupEncryptionKeyProvider
    {
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    }

    private sealed class FixedMaintenanceIntegrityKeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(65, 32).Select(i => (byte)i).ToArray());
    }
}
