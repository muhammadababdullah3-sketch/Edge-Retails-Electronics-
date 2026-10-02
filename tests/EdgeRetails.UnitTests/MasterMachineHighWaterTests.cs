using EdgeRetails.Infrastructure.Services;

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

