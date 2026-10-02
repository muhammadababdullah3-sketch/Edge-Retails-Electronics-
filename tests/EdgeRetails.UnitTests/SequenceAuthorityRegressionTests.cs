using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Infrastructure.Services;

namespace EdgeRetails.UnitTests;

public sealed class SequenceAuthorityRegressionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void ExplicitSupportedEnvelopeVersionIsRequired(int? version)
    {
        using var files = new Fixture();
        files.ReplaceBoth("{\"DealerPrefixes\":{},\"SupplierProducts\":{}}", version);
        Assert.Throws<InvalidOperationException>(() => files.Open());
    }

    [Fact]
    public void WriteCapIsCheckedBeforeAnyDurableOrMemoryMutation()
    {
        using var files = new Fixture();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 8);
        var manifest = File.ReadAllBytes(files.Manifest);
        var checkpoint = File.ReadAllBytes(files.Manifest + ".checkpoint");
        Assert.Throws<InvalidOperationException>(() => service.RecordDealerPrefixHighWater(new string('A', 4 * 1024 * 1024), 1));
        Assert.Equal(manifest, File.ReadAllBytes(files.Manifest));
        Assert.Equal(checkpoint, File.ReadAllBytes(files.Manifest + ".checkpoint"));
        Assert.Equal(8, service.GetDealerPrefixHighWater("AB"));
    }

    [Fact]
    public void ExplicitFixtureInitializationCannotOverwriteEstablishedState()
    {
        using var files = new Fixture();
        files.Open().RecordDealerPrefixHighWater("AB", 20);
        Assert.Throws<InvalidOperationException>(() => MachineSequenceHighWaterService.InitializeOwnedFixture(files.Manifest, files.Custody));
        Assert.Equal(20, files.Open().GetDealerPrefixHighWater("AB"));
    }

    [Theory]
    [InlineData(".checkpoint")]
    [InlineData(".lock")]
    [InlineData("")]
    public void MissingEstablishedArtifactFailsWithoutReinitialization(string suffix)
    {
        using var files = new Fixture();
        File.Delete(files.Manifest + suffix);
        Assert.Throws<InvalidOperationException>(() => files.Open());
        Assert.False(File.Exists(files.Manifest + suffix));
    }

    [Fact]
    public void LossOfAllThreeFilesDoesNotPermitAutomaticBootstrap()
    {
        using var files = new Fixture();
        File.Delete(files.Manifest);
        File.Delete(files.Manifest + ".lock");
        File.Delete(files.Manifest + ".checkpoint");
        Assert.Throws<InvalidOperationException>(() => files.Open());
        Assert.False(File.Exists(files.Manifest));
        Assert.False(File.Exists(files.Manifest + ".lock"));
        Assert.False(File.Exists(files.Manifest + ".checkpoint"));
    }

    [Fact]
    public void CheckpointReplayAloneFailsAcrossRestart()
    {
        using var files = new Fixture();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 8);
        var old = File.ReadAllBytes(files.Manifest + ".checkpoint");
        service.RecordDealerPrefixHighWater("AB", 20);
        File.WriteAllBytes(files.Manifest + ".checkpoint", old);
        Assert.Throws<InvalidOperationException>(() => files.Open());
        Assert.Throws<InvalidOperationException>(() => service.GetDealerPrefixHighWater("AB"));
    }

    [Theory]
    [InlineData((int)SequenceAuthorityWriteStage.CheckpointWriteStarted, false)]
    [InlineData((int)SequenceAuthorityWriteStage.CheckpointFlushed, false)]
    [InlineData((int)SequenceAuthorityWriteStage.CheckpointPublished, false)]
    [InlineData((int)SequenceAuthorityWriteStage.ManifestFlushed, false)]
    [InlineData((int)SequenceAuthorityWriteStage.ManifestPublished, true)]
    public void PublicationFaultNeverReportsSuccessOrLowersNextUnused(
        int failureStageValue, bool completedPublication)
    {
        var failureStage = (SequenceAuthorityWriteStage)failureStageValue;
        using var files = new Fixture();
        files.Open().RecordDealerPrefixHighWater("AB", 8);
        var service = files.Open(stage => { if (stage == failureStage) { throw new IOException("Owned publication fault."); } });
        Assert.Throws<InvalidOperationException>(() => service.RecordDealerPrefixHighWater("AB", 20));
        if (completedPublication)
        {
            Assert.Equal(20, files.Open().GetDealerPrefixHighWater("AB"));
            Assert.Equal(20, service.GetDealerPrefixHighWater("AB"));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => files.Open());
            Assert.Throws<InvalidOperationException>(() => service.GetDealerPrefixHighWater("AB"));
            Assert.Throws<InvalidOperationException>(() => service.RecordDealerPrefixHighWater("AB", 9));
        }
    }

    [Theory]
    [InlineData("{\"DealerPrefixes\":{\"ab\":8},\"SupplierProducts\":{}}")]
    [InlineData("{\"DealerPrefixes\":{\"AB\":8,\"ab\":20},\"SupplierProducts\":{}}")]
    [InlineData("{\"DealerPrefixes\":{\"AB\":8,\"AB\":20},\"SupplierProducts\":{}}")]
    [InlineData("{\"DealerPrefixes\":{\" AB \":8},\"SupplierProducts\":{}}")]
    [InlineData("{\"SupplierProducts\":{}}")]
    [InlineData("{\"DealerPrefixes\":{},\"SupplierProducts\":null}")]
    public void FullyAuthenticatedMalformedMapsAreRejectedBeforeMemoryChanges(string payload)
    {
        using var files = new Fixture();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 20);
        var goodManifest = File.ReadAllBytes(files.Manifest);
        var goodCheckpoint = File.ReadAllBytes(files.Manifest + ".checkpoint");
        files.ReplaceBoth(payload);
        Assert.Throws<InvalidOperationException>(() => service.GetDealerPrefixHighWater("AB"));
        Assert.Throws<InvalidOperationException>(() => files.Open());
        File.WriteAllBytes(files.Manifest, goodManifest);
        File.WriteAllBytes(files.Manifest + ".checkpoint", goodCheckpoint);
        Assert.Equal(20, service.GetDealerPrefixHighWater("AB"));
    }

    [Fact]
    public void RemovedEstablishedKeyIsRegressionEvenWhenBothFilesAgree()
    {
        using var files = new Fixture();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 20);
        files.ReplaceBoth("{\"DealerPrefixes\":{},\"SupplierProducts\":{}}");
        Assert.Throws<InvalidOperationException>(() => service.GetDealerPrefixHighWater("AB"));
        Assert.Throws<InvalidOperationException>(() => service.RecordDealerPrefixHighWater("CD", 1));
    }

    [Fact]
    public void InvalidSupplierMapCannotPartiallyAdvanceValidatedDealerMemory()
    {
        using var files = new Fixture();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 8);
        var goodManifest = File.ReadAllBytes(files.Manifest);
        var goodCheckpoint = File.ReadAllBytes(files.Manifest + ".checkpoint");
        files.ReplaceBoth("{\"DealerPrefixes\":{\"AB\":120},\"SupplierProducts\":{\"invalid-pair\":1}}");
        Assert.Throws<InvalidOperationException>(() => service.GetDealerPrefixHighWater("AB"));
        File.WriteAllBytes(files.Manifest, goodManifest);
        File.WriteAllBytes(files.Manifest + ".checkpoint", goodCheckpoint);
        Assert.Equal(8, service.GetDealerPrefixHighWater("AB"));
    }

    [Fact]
    public void NoncanonicalSupplierKeyIsRejectedAndRemovedSupplierKeyRegresses()
    {
        using var files = new Fixture();
        var supplier = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var product = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var service = files.Open();
        service.RecordSupplierProductHighWater(supplier, product, 20);
        var upper = $"{supplier:D}:{product:D}".ToUpperInvariant();
        files.ReplaceBoth(JsonSerializer.Serialize(new { DealerPrefixes = new Dictionary<string, long>(), SupplierProducts = new Dictionary<string, long> { [upper] = 20 } }));
        Assert.Throws<InvalidOperationException>(() => files.Open());
        files.ReplaceBoth("{\"DealerPrefixes\":{},\"SupplierProducts\":{}}");
        Assert.Throws<InvalidOperationException>(() => service.GetSupplierProductHighWater(supplier, product));
    }

    [Fact]
    public void InvalidCheckpointCannotAdvanceMemoryFromAnOtherwiseValidManifest()
    {
        using var files = new Fixture();
        var service = files.Open();
        service.RecordDealerPrefixHighWater("AB", 8);
        var goodManifest = File.ReadAllBytes(files.Manifest);
        var goodCheckpoint = File.ReadAllBytes(files.Manifest + ".checkpoint");
        files.ReplaceBoth("{\"DealerPrefixes\":{\"AB\":120},\"SupplierProducts\":{}}");
        File.WriteAllText(files.Manifest + ".checkpoint", "invalid owned checkpoint");
        Assert.Throws<InvalidOperationException>(() => service.GetDealerPrefixHighWater("AB"));
        File.WriteAllBytes(files.Manifest, goodManifest);
        File.WriteAllBytes(files.Manifest + ".checkpoint", goodCheckpoint);
        Assert.Equal(8, service.GetDealerPrefixHighWater("AB"));
    }

    [Fact]
    public async Task ReaderWaitsForTheSameLeaseUntilManifestPublication()
    {
        using var files = new Fixture();
        using var checkpointPublished = new ManualResetEventSlim();
        using var publishManifest = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        var writer = files.Open(stage =>
        {
            if (stage == SequenceAuthorityWriteStage.CheckpointPublished)
            {
                checkpointPublished.Set();
                if (!publishManifest.Wait(TimeSpan.FromSeconds(15))) { throw new IOException("Owned publication barrier timed out."); }
            }
        });
        var writing = Task.Run(() => writer.RecordDealerPrefixHighWater("AB", 20));
        Assert.True(checkpointPublished.Wait(TimeSpan.FromSeconds(10)));
        var reading = Task.Run(() => { readerStarted.Set(); return files.Open().GetDealerPrefixHighWater("AB"); });
        try
        {
            Assert.True(readerStarted.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotSame(reading, await Task.WhenAny(reading, Task.Delay(100)));
        }
        finally { publishManifest.Set(); }
        await writing.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(20, await reading.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Theory]
    [InlineData(0x40000000u)]
    [InlineData(0x10000000u)]
    [InlineData(0x00000002u)]
    [InlineData(0x00000004u)]
    [InlineData(0x00000010u)]
    [InlineData(0x00000100u)]
    [InlineData(0x00000040u)]
    [InlineData(0x00010000u)]
    [InlineData(0x00040000u)]
    [InlineData(0x00080000u)]
    public void UntrustedMutationIncludingGenericRightsIsRejected(uint rights)
    {
        Assert.True(WindowsSequenceAuthorityCustody.HasUntrustedMutation(
            [new("untrusted", WindowsSequenceAuthorityCustody.MapGenericRights(rights), false, false)]));
    }

    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0x20000000u)]
    public void GenericReadAndExecuteDoNotBecomeMutation(uint rights)
    {
        Assert.False(WindowsSequenceAuthorityCustody.HasUntrustedMutation(
            [new("untrusted", WindowsSequenceAuthorityCustody.MapGenericRights(rights), false, false)]));
    }

    [Fact]
    public void DenyBeforeAllowResolvesTheSameSidButLateDenyCannotUndoAccess()
    {
        Assert.False(WindowsSequenceAuthorityCustody.HasUntrustedMutation(
            [new("untrusted", 2, true, false), new("untrusted", 2, false, false)]));
        Assert.True(WindowsSequenceAuthorityCustody.HasUntrustedMutation(
            [new("untrusted", 2, false, false), new("untrusted", 2, true, false)]));
    }

    [Fact]
    public void InheritOnlyDenyDoesNotMaskEffectiveWriteAndInheritOnlyAllowDoesNotGrantIt()
    {
        Assert.True(WindowsSequenceAuthorityCustody.HasUntrustedMutation(
            [new("untrusted", 2, true, false, true), new("untrusted", 2, false, false)]));
        Assert.False(WindowsSequenceAuthorityCustody.HasUntrustedMutation(
            [new("untrusted", 2, false, false, true)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReparseAttributesAlwaysRejectBothFilesAndAncestors(bool directory)
    {
        var attributes = FileAttributes.ReparsePoint | (directory ? FileAttributes.Directory : FileAttributes.Normal);
        Assert.Throws<InvalidDataException>(() => WindowsSequenceAuthorityCustody.ValidateAttributes(attributes, directory));
    }

    [Fact]
    public void NetworkAndUncRootsCannotEstablishLocalMachineCustody()
    {
        Assert.False(WindowsSequenceAuthorityCustody.IsLocalFixedVolume(@"\\untrusted\share\", DriveType.Fixed));
        Assert.False(WindowsSequenceAuthorityCustody.IsLocalFixedVolume(@"Z:\", DriveType.Network));
        Assert.False(WindowsSequenceAuthorityCustody.IsLocalFixedVolume(@"E:\", DriveType.Removable));
        Assert.True(WindowsSequenceAuthorityCustody.IsLocalFixedVolume(@"C:\", DriveType.Fixed));
    }

    [Fact]
    public void FixturePolicyCannotEscapeItsOwnedRoot()
    {
        using var files = new Fixture();
        Assert.Throws<InvalidOperationException>(() => files.Custody.ValidateDirectory(Path.GetTempPath()));
        Assert.Throws<InvalidOperationException>(() => files.Custody.ValidateFile(Path.Combine(files.Root, "..", "escaped.manifest")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("edge-highwater-regression-").FullName;
        public string Manifest => Path.Combine(Root, "highwater.manifest");
        public OwnedSequenceAuthorityCustody Custody { get; }
        public Fixture()
        {
            Custody = new OwnedSequenceAuthorityCustody(Root);
            MachineSequenceHighWaterService.InitializeOwnedFixture(Manifest, Custody);
        }
        public MachineSequenceHighWaterService Open(Action<SequenceAuthorityWriteStage>? observer = null) => new(Manifest, Custody, observer);
        public void ReplaceBoth(string payload, int? version = 1)
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + ":EdgeRetails:HighWater:v1"));
            using var hmac = new HMACSHA256(key);
            var envelopeValues = new Dictionary<string, object>
            {
                ["UpdatedAtUtc"] = DateTimeOffset.UtcNow, ["PayloadJson"] = payload,
                ["Signature"] = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
            };
            if (version.HasValue) { envelopeValues["Version"] = version.Value; }
            var envelope = JsonSerializer.Serialize(envelopeValues);
            File.WriteAllText(Manifest, envelope);
            File.WriteAllText(Manifest + ".checkpoint", envelope);
        }
        public void Dispose()
        {
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "edge-highwater-regression-";
            if (!Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Sequence fixture escaped its owned root.");
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}

