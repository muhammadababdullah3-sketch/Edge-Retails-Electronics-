using EdgeRetails.Application.Abstractions;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.UnitTests;

public sealed class MasterMachineHighWaterTests
{
    [Fact]
    public void UnprovisionedAuthorityCannotSilentlyBecomeAnEmptyProductionAuthority()
    {
        using var files = new OwnedFiles(initialize: false);
        Assert.Throws<InvalidOperationException>(() => new MachineSequenceHighWaterService(files.Manifest));
        Assert.False(File.Exists(files.Manifest));
        Assert.False(File.Exists(files.Manifest + ".lock"));
    }

    [Fact]
    public void RestartAfterLossOfBothAuthorityFilesCannotResetSequences()
    {
        using var files = new OwnedFiles();
        var service = files.Open();
        service.RecordSupplierProductHighWater(Guid.NewGuid(), Guid.NewGuid(), 20);
        File.Delete(files.Manifest);
        File.Delete(files.Manifest + ".lock");
        Assert.Throws<InvalidOperationException>(() => files.Open());
    }

    [Fact]
    public void AuthenticatedOlderSnapshotCannotLowerAnExistingReadersKnownMaximum()
    {
        using var files = new OwnedFiles();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();
        var service = files.Open();
        service.RecordSupplierProductHighWater(supplier, product, 8);
        var previous = File.ReadAllBytes(files.Manifest);
        service.RecordSupplierProductHighWater(supplier, product, 20);
        File.WriteAllBytes(files.Manifest, previous);
        Assert.Throws<InvalidOperationException>(() => service.GetSupplierProductHighWater(supplier, product));
        Assert.Throws<InvalidOperationException>(() => service.RecordSupplierProductHighWater(supplier, product, 9));
    }

    [Fact]
    public void RestartCannotAcceptAnAuthenticatedOlderSnapshot()
    {
        using var files = new OwnedFiles();
        var service = files.Open();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();
        service.RecordSupplierProductHighWater(supplier, product, 8);
        var previous = File.ReadAllBytes(files.Manifest);
        service.RecordSupplierProductHighWater(supplier, product, 20);
        File.WriteAllBytes(files.Manifest, previous);
        Assert.Throws<InvalidOperationException>(() => files.Open());
    }

    [Fact]
    public void OrdinaryUserOwnedAuthorityCannotClaimProductionCustody()
    {
        using var files = new OwnedFiles();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 8);
        Assert.Throws<InvalidOperationException>(() => new MachineSequenceHighWaterService(files.Manifest));
    }

    [Fact]
    public void ReloadPreservesNextUnusedSequenceAndNeverLowersIt()
    {
        using var files = new OwnedFiles();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();
        var first = files.Open();
        first.RecordSupplierProductHighWater(supplier, product, 8);
        first.RecordSupplierProductHighWater(supplier, product, 3);
        var reloaded = files.Open();
        Assert.Equal(8, reloaded.GetSupplierProductHighWater(supplier, product));
    }

    [Fact]
    public void CorruptedDurableAuthorityMustFailClosed()
    {
        using var files = new OwnedFiles();
        var first = files.Open();
        first.RecordSupplierProductHighWater(Guid.NewGuid(), Guid.NewGuid(), 8);
        File.WriteAllText(files.Manifest, "corrupt owned fixture");
        Assert.Throws<InvalidOperationException>(() => files.Open());
    }

    [Fact]
    public void UnsignedAuthorityCannotBeAcceptedAsDurableSequenceTruth()
    {
        using var files = new OwnedFiles();
        File.WriteAllText(files.Manifest, "{\"Version\":1,\"PayloadJson\":\"{\\\"DealerPrefixes\\\":{},\\\"SupplierProducts\\\":{}}\"}");
        Assert.Throws<InvalidOperationException>(() => files.Open());
    }

    [Fact]
    public void CannotClaimRecordedHighWaterWhenStorageWriteFails()
    {
        using var files = new OwnedFiles();
        var service = files.Open(stage => { if (stage == SequenceAuthorityWriteStage.CheckpointFlushed) { throw new IOException("Injected owned storage write failure."); } });
        Assert.ThrowsAny<Exception>(() => service.RecordSupplierProductHighWater(Guid.NewGuid(), Guid.NewGuid(), 8));
    }

    [Fact]
    public void TwoServiceInstancesCannotOverwriteNewerDurableAuthority()
    {
        using var files = new OwnedFiles();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();
        var first = files.Open();
        var stale = files.Open();
        first.RecordSupplierProductHighWater(supplier, product, 8);
        stale.RecordSupplierProductHighWater(supplier, product, 3);
        var reloaded = files.Open();
        Assert.Equal(8, reloaded.GetSupplierProductHighWater(supplier, product));
    }

    [Fact]
    public void MissingPreviouslyLoadedAuthorityCannotResetTheNextUnusedSequence()
    {
        using var files = new OwnedFiles();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();
        var service = files.Open();
        service.RecordSupplierProductHighWater(supplier, product, 8);
        File.Delete(files.Manifest);
        Assert.Throws<InvalidOperationException>(() => service.GetSupplierProductHighWater(supplier, product));
    }

    [Fact]
    public void RestartCannotAcceptMissingAuthorityWhenDurableLeaseFileProvesPriorWrite()
    {
        using var files = new OwnedFiles();
        var service = files.Open();
        service.RecordSupplierProductHighWater(Guid.NewGuid(), Guid.NewGuid(), 8);
        File.Delete(files.Manifest);
        Assert.Throws<InvalidOperationException>(() => files.Open());
    }

    [Fact]
    public void ExistingReaderRefreshesOtherInstancesDurableProgress()
    {
        using var files = new OwnedFiles();
        var supplier = Guid.NewGuid();
        var product = Guid.NewGuid();
        var writer = files.Open();
        var reader = files.Open();
        writer.RecordSupplierProductHighWater(supplier, product, 8);
        Assert.Equal(8, reader.GetSupplierProductHighWater(supplier, product));
    }

    [Fact]
    public void ProductionDiRejectsOrdinaryUserOwnedAuthorityEvenWhenConfiguredInTemp()
    {
        using var files = new OwnedFiles();
        var prev = Environment.GetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH", files.Manifest);
            var services = new ServiceCollection();
            services.AddEdgeRetailsInfrastructure("Host=localhost;Database=edge_retails_test;Username=test;Password=test");
            using var sp = services.BuildServiceProvider();
            // Production DI unconditionally binds WindowsSequenceAuthorityCustody.
            // When resolved, ordinary user custody in temp is rejected fail-closed immediately.
            Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<ISequenceHighWaterService>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH", prev);
        }
    }

    [Fact]
    public void ProductionDiDoesNotAutoDiscoverCandidateManifestsOnNonstandardPort()
    {
        using var files = new OwnedFiles();
        var prev = Environment.GetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH", null);
            var services = new ServiceCollection();
            services.AddEdgeRetailsInfrastructure("Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=test");
            using var sp = services.BuildServiceProvider();
            // Must not auto-discover temp manifests; attempts production custody on default path which fails closed on non-elevated identity.
            Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<ISequenceHighWaterService>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH", prev);
        }
    }

    [Fact]
    public void GuardedTestSequenceAuthorityRejectsPort5432()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddGuardedTestSequenceAuthority("Host=127.0.0.1;Port=5432;Database=edgeretails_test_001;Username=er_provisioner;Password=test", skipLiveConnectionCheck: true));
        Assert.Contains("Port 5432 is prohibited", ex.Message);
    }

    [Fact]
    public void GuardedTestSequenceAuthorityRejectsNonLoopbackHost()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddGuardedTestSequenceAuthority("Host=192.168.1.150;Port=55437;Database=edgeretails_test_001;Username=er_provisioner;Password=test", skipLiveConnectionCheck: true));
        Assert.Contains("Only isolated loopback", ex.Message);
    }

    [Fact]
    public void GuardedTestSequenceAuthorityRejectsUnapprovedDatabaseOrRole()
    {
        var services = new ServiceCollection();
        var exDb = Assert.Throws<InvalidOperationException>(() =>
            services.AddGuardedTestSequenceAuthority("Host=127.0.0.1;Port=55437;Database=edgeretails;Username=er_provisioner;Password=test", skipLiveConnectionCheck: true));
        Assert.Contains("Database 'edgeretails' is prohibited", exDb.Message);

        var exRole = Assert.Throws<InvalidOperationException>(() =>
            services.AddGuardedTestSequenceAuthority("Host=127.0.0.1;Port=55437;Database=edgeretails_test_001;Username=postgres;Password=test", skipLiveConnectionCheck: true));
        Assert.Contains("User 'postgres' is prohibited", exRole.Message);
    }

    [Fact]
    public void GuardedTestSequenceAuthorityRejectsMismatchedClusterBinding()
    {
        var tempCluster = Directory.CreateTempSubdirectory("er-cluster-test-");
        try
        {
            var markerPath = Path.Combine(tempCluster.FullName, "OWNERSHIP_MARKER.json");
            File.WriteAllText(markerPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                marker = "ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER",
                run_id = "RUN_MOCK_001",
                port = 55600,
                isolation_mode = "owned_disposable_local_cluster"
            }));

            var authorityRoot = Path.Combine(tempCluster.FullName, "harness-highwater");
            Directory.CreateDirectory(authorityRoot);
            try
            {
                // Write mismatched binding
                var bindingPath = Path.Combine(authorityRoot, "cluster_binding.json");
                File.WriteAllText(bindingPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ClusterRoot = tempCluster.FullName,
                    RunId = "DIFFERENT_RUN_ID",
                    Port = 55600,
                    Database = "edgeretails_test_001",
                    BoundAtUtc = DateTimeOffset.UtcNow
                }));

                var services = new ServiceCollection();
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    services.AddGuardedTestSequenceAuthority("Host=127.0.0.1;Port=55600;Database=edgeretails_test_001;Username=er_provisioner;Password=test",
                        runnerRootOverride: tempCluster.FullName, skipLiveConnectionCheck: true));
                Assert.Contains("Cluster binding mismatch", ex.Message);
            }
            finally
            {
                if (Directory.Exists(authorityRoot)) { Directory.Delete(authorityRoot, true); }
            }
        }
        finally
        {
            if (Directory.Exists(tempCluster.FullName)) { Directory.Delete(tempCluster.FullName, true); }
        }
    }

    [Fact]
    public void GuardedTestSequenceAuthorityRejectsBindingWithMissingRunId()
    {
        var tempCluster = Directory.CreateTempSubdirectory("er-cluster-test-missing-run-");
        try
        {
            var markerPath = Path.Combine(tempCluster.FullName, "OWNERSHIP_MARKER.json");
            File.WriteAllText(markerPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                marker = "ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER",
                run_id = "RUN_MOCK_REQUIRED_ID",
                port = 55602,
                isolation_mode = "owned_disposable_local_cluster"
            }));

            var authorityRoot = Path.Combine(tempCluster.FullName, "harness-highwater");
            Directory.CreateDirectory(authorityRoot);
            try
            {
                // Write existing binding with missing (null/empty) RunId
                var bindingPath = Path.Combine(authorityRoot, "cluster_binding.json");
                File.WriteAllText(bindingPath, System.Text.Json.JsonSerializer.Serialize(new
                {
                    ClusterRoot = tempCluster.FullName,
                    RunId = (string?)null,
                    Port = 55602,
                    Database = "edgeretails_test_001",
                    BoundAtUtc = DateTimeOffset.UtcNow
                }));

                var services = new ServiceCollection();
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    services.AddGuardedTestSequenceAuthority(
                        "Host=127.0.0.1;Port=55602;Database=edgeretails_test_001;Username=er_provisioner;Password=test",
                        runnerRootOverride: tempCluster.FullName, skipLiveConnectionCheck: true));
                Assert.Contains("Cluster binding mismatch", ex.Message);
            }
            finally
            {
                if (Directory.Exists(authorityRoot)) { Directory.Delete(authorityRoot, true); }
            }
        }
        finally
        {
            if (Directory.Exists(tempCluster.FullName)) { Directory.Delete(tempCluster.FullName, true); }
        }
    }

    [Fact]
    public void GuardedTestSequenceAuthorityResolvesOwnedFixtureForVerifiedTarget()
    {
        var tempCluster = Directory.CreateTempSubdirectory("er-cluster-test-valid-");
        try
        {
            var markerPath = Path.Combine(tempCluster.FullName, "OWNERSHIP_MARKER.json");
            File.WriteAllText(markerPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                marker = "ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER",
                run_id = "RUN_MOCK_VALID",
                port = 55601,
                isolation_mode = "owned_disposable_local_cluster"
            }));

            var services = new ServiceCollection();
            services.AddEdgeRetailsInfrastructure("Host=127.0.0.1;Port=55601;Database=edgeretails_test_valid;Username=er_provisioner;Password=test");
            services.AddGuardedTestSequenceAuthority("Host=127.0.0.1;Port=55601;Database=edgeretails_test_valid;Username=er_provisioner;Password=test",
                runnerRootOverride: tempCluster.FullName, skipLiveConnectionCheck: true);

            using var sp = services.BuildServiceProvider();
            var service = sp.GetRequiredService<ISequenceHighWaterService>();
            Assert.NotNull(service);

            // Verified target binds owned sequence authority successfully
            Assert.Equal(0, service.GetDealerPrefixHighWater("VALID"));
            service.RecordDealerPrefixHighWater("VALID", 100);
            Assert.Equal(100, service.GetDealerPrefixHighWater("VALID"));
        }
        finally
        {
            var authorityRoot = Path.Combine(Path.GetTempPath(), "EdgeRetailsMasterPg_55601_RUN_MOCK_VALID", "harness-highwater");
            if (Directory.Exists(authorityRoot)) { Directory.Delete(authorityRoot, true); }
            if (Directory.Exists(tempCluster.FullName)) { Directory.Delete(tempCluster.FullName, true); }
        }
    }

    private sealed class OwnedFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("edge-master-highwater-").FullName;
        public string Manifest => Path.Combine(Directory, "highwater.manifest");
        public OwnedSequenceAuthorityCustody Custody { get; }
        public OwnedFiles(bool initialize = true)
        {
            Custody = new OwnedSequenceAuthorityCustody(Directory);
            if (initialize) { MachineSequenceHighWaterService.InitializeOwnedFixture(Manifest, Custody); }
        }
        public MachineSequenceHighWaterService Open(Action<SequenceAuthorityWriteStage>? observer = null)
            => new(Manifest, Custody, observer);
        public void Dispose()
        {
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "edge-master-highwater-";
            if (!Path.GetFullPath(Directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Owned temporary fixture escaped its root.");
            }
            System.IO.Directory.Delete(Directory, true);
        }
    }
}

