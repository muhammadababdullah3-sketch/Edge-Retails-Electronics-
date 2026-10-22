using System.Text;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Infrastructure.Storage;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public sealed class StorageRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"central-storage-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task ImmutablePublication_IdenticalRetrySucceeds_ChangedBytesCannotOverwrite()
    {
        var provider = new DiskObjectStorageProvider(_root);
        var shop = Guid.NewGuid();
        var target = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("original ciphertext");
        var path = await provider.SaveCiphertextAsync(shop, target, new MemoryStream(bytes));
        var restarted = new DiskObjectStorageProvider(_root);
        Assert.Equal(path, await restarted.SaveCiphertextAsync(shop, target, new MemoryStream(bytes)));
        var denied = await Assert.ThrowsAsync<CentralDomainException>(() => restarted.SaveCiphertextAsync(shop, target, new MemoryStream(Encoding.UTF8.GetBytes("different ciphertext"))));
        Assert.Equal("STORAGE_OBJECT_CONFLICT", denied.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.pending"));
    }

    [Fact]
    public async Task InterruptedPublication_DoesNotExposePartialPermanentBytes()
    {
        var provider = new DiskObjectStorageProvider(_root);
        var shop = Guid.NewGuid();
        var target = Guid.NewGuid();
        var failure = await Assert.ThrowsAsync<CentralDomainException>(() => provider.SaveCiphertextAsync(shop, target, new InterruptedSource()));
        Assert.Equal("STORAGE_IO_FAILED", failure.Code);
        Assert.False(File.Exists(Path.Combine(_root, shop.ToString("N"), $"{target:N}.bin")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, shop.ToString("N")), "*.pending"));
    }

    [Fact]
    public async Task NewProvider_ResumesFromAuthoritativeOffsetAndDiscardsUncommittedTail()
    {
        var shop = Guid.NewGuid();
        var upload = Guid.NewGuid();
        var first = new DiskObjectStorageProvider(_root);
        await first.AppendUploadChunkAsync(shop, upload, 0, 0, new byte[] { 1, 2, 3, 99, 99 });
        var restarted = new DiskObjectStorageProvider(_root);
        await restarted.AppendUploadChunkAsync(shop, upload, 3, 3, new byte[] { 4, 5 });
        await using var stream = await restarted.OpenUploadContentAsync(shop, upload);
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes.ToArray());
    }

    [Fact]
    public async Task PublicationCleanup_RemovesOnlyStaleOwnedNamesAndDefersActiveWriter()
    {
        var provider = new DiskObjectStorageProvider(_root);
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string Name() => Path.Combine(directory, $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.pending");
        var stale = Name();
        var fresh = Name();
        var active = Name();
        var unrelated = Path.Combine(directory, "unrelated.pending");
        foreach (var path in new[] { stale, fresh, active, unrelated })
        {
            await File.WriteAllBytesAsync(path, new byte[] { 42 });
        }
        foreach (var path in new[] { stale, active, unrelated })
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        }
        using (var writer = new FileStream(active, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await provider.CleanupPendingPublicationsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 10);
            Assert.Equal(1, result.Removed);
            Assert.Equal(1, result.Deferred);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(active));
            Assert.True(File.Exists(unrelated));
        }
    }

    [Fact]
    public async Task BoundedCleanup_AdvancesPastLockedFirstFileAndEventuallyRevisitsIt()
    {
        var provider = new DiskObjectStorageProvider(_root);
        var directory = Path.Combine(_root, new Guid("00000000-0000-0000-0000-000000000001").ToString("N"));
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "00000000000000000000000000000001.00000000000000000000000000000001.pending");
        var second = Path.Combine(directory, "00000000000000000000000000000002.00000000000000000000000000000002.pending");
        foreach (var path in new[] { first, second })
        {
            await File.WriteAllBytesAsync(path, new byte[] { 42 });
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        }
        using (var writer = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(1, (await provider.CleanupPendingPublicationsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 1)).Deferred);
            Assert.Equal(1, (await provider.CleanupPendingPublicationsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 1)).Removed);
            Assert.False(File.Exists(second));
        }
        Assert.Equal(1, (await provider.CleanupPendingPublicationsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), 1)).Removed);
        Assert.False(File.Exists(first));
    }

    private sealed class InterruptedSource : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken);
            throw new IOException("Synthetic interruption after partial temporary write.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
