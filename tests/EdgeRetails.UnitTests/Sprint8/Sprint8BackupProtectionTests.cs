using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8BackupProtectionTests
{
    [Fact]
    public async Task AesGcmBackupProtection_RoundTripsAndRejectsTampering()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EdgeRetailsBackupProtection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var plain = Path.Combine(dir, "input.dump");
        var encrypted = Path.Combine(dir, "backup.erbak");
        var restored = Path.Combine(dir, "restored.dump");
        await File.WriteAllBytesAsync(plain, Enumerable.Range(0, 2_200_000).Select(i => (byte)(i % 251)).ToArray());
        var protector = new AesGcmBackupProtector(new KeyProvider());
        await protector.ProtectAsync(plain, encrypted);
        await protector.UnprotectAsync(encrypted, restored);
        Assert.Equal(await File.ReadAllBytesAsync(plain), await File.ReadAllBytesAsync(restored));

        var bytes = await File.ReadAllBytesAsync(encrypted);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(encrypted, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => protector.UnprotectAsync(encrypted, Path.Combine(dir, "tampered.dump")));
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private sealed class KeyProvider : IBackupEncryptionKeyProvider
    {
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    }
}
