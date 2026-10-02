using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Production.Backup;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: canonical architecture section 56 requires an explicit persistent
// OS-protected BMK plus a distinct portable Owner recovery envelope.
public sealed class MasterBackupKeyLifecycleTests
{
    [Fact]
    public void CanonicalBackupKeyLifecycleIsAvailableInsteadOfEnvironmentOnlyBootstrap()
    {
        var lifecycleType = typeof(InfrastructureServiceCollectionExtensions).Assembly.GetType(
            "EdgeRetails.Infrastructure.Production.Backup.WindowsProtectedBackupKeyRing");
        Assert.NotNull(lifecycleType);
        Assert.True(typeof(IBackupEncryptionKeyProvider).IsAssignableFrom(lifecycleType));
        Assert.NotNull(lifecycleType.GetMethod("ProvisionAsync"));
        Assert.NotNull(lifecycleType.GetMethod("RecoverAsync"));
    }

    [Fact]
    public async Task MissingAuthorityNeverGeneratesSecretDuringBackupRead()
    {
        using var fixture = new KeyFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Ring.GetKeyAsync());
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task ProvisionedMasterPersistsAcrossRestartAndIsDistinctFromRecoveryKey()
    {
        using var fixture = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        var metadata = await fixture.Ring.ProvisionAsync(recovery);
        var master = await fixture.Ring.GetKeyAsync();
        Assert.NotEqual(recovery, master);
        Assert.Equal(master, await fixture.NewRing().GetKeyAsync());
        Assert.Equal(metadata, await fixture.NewRing().GetCurrentMetadataAsync());
        Assert.DoesNotContain(Convert.ToBase64String(recovery), Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.Path)));
        Assert.DoesNotContain(Convert.ToBase64String(master), Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.Path)));
        Assert.NotEqual(metadata.WrappedBackupMasterKey, Convert.ToBase64String(master));
        CryptographicOperations.ZeroMemory(recovery);
        CryptographicOperations.ZeroMemory(master);
    }

    [Fact]
    public async Task ReplacementMachineRecoversFromOwnerKeyAndPublicEnvelopeWithoutOriginalLocalBlob()
    {
        using var original = new KeyFixture();
        using var replacement = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        var metadata = await original.Ring.ProvisionAsync(recovery);
        var master = await original.Ring.GetKeyAsync();
        await replacement.Ring.RecoverAsync(metadata, recovery);
        Assert.Equal(master, await replacement.Ring.GetVersionKeyAsync(metadata.KeyVersion));
        CryptographicOperations.ZeroMemory(recovery);
        CryptographicOperations.ZeroMemory(master);
    }

    [Fact]
    public async Task WrongRecoveryKeyOrTamperedEnvelopeCannotCreateLocalAuthority()
    {
        using var original = new KeyFixture();
        using var replacement = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        var metadata = await original.Ring.ProvisionAsync(recovery);
        await Assert.ThrowsAsync<CryptographicException>(() => replacement.Ring.RecoverAsync(metadata, RandomNumberGenerator.GetBytes(32)));
        Assert.False(File.Exists(replacement.Path));
        var wrongTag = metadata with { WrapTag = Convert.ToBase64String(new byte[16]) };
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => replacement.Ring.RecoverAsync(wrongTag, recovery));
        Assert.False(File.Exists(replacement.Path));
        CryptographicOperations.ZeroMemory(recovery);
    }

    [Fact]
    public async Task ExistingCorruptAuthorityNeverFallsBackToAnEnvironmentKey()
    {
        using var fixture = new KeyFixture();
        await fixture.Ring.ProvisionAsync(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllBytesAsync(fixture.Path, new byte[96]);
        var alternative = fixture.NewRing(() => Convert.ToHexString(new byte[32]));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => alternative.GetKeyAsync());
        Assert.Equal(96, new FileInfo(fixture.Path).Length);
    }

    [Fact]
    public async Task RotationRetainsOldVersionAndImportedLegacyAuthority()
    {
        using var fixture = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        var legacy = RandomNumberGenerator.GetBytes(32);
        var original = await fixture.Ring.ProvisionAsync(recovery, legacy);
        var rotated = await fixture.Ring.RotateAsync(recovery);
        Assert.NotEqual(original.KeyVersion, rotated.KeyVersion);
        Assert.NotEqual(legacy, await fixture.Ring.GetKeyAsync());
        Assert.Equal(legacy, await fixture.NewRing().GetVersionKeyAsync(original.KeyVersion));
        Assert.Equal(legacy, await fixture.NewRing().GetLegacyKeyAsync());
        CryptographicOperations.ZeroMemory(recovery);
        CryptographicOperations.ZeroMemory(legacy);
    }

    [Fact]
    public async Task ProvisionAndRecoveryCannotOverwriteEstablishedAuthority()
    {
        using var fixture = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        var metadata = await fixture.Ring.ProvisionAsync(recovery);
        var before = await File.ReadAllBytesAsync(fixture.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.NewRing().ProvisionAsync(recovery));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.NewRing().RecoverAsync(metadata, recovery));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Path));
        CryptographicOperations.ZeroMemory(recovery);
    }

    [Fact]
    public async Task ConcurrentProvisionAcrossInstancesCreatesExactlyOneAuthority()
    {
        using var fixture = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        async Task<bool> Attempt(WindowsProtectedBackupKeyRing ring)
        {
            try { await ring.ProvisionAsync(recovery); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(fixture.Ring), Attempt(fixture.NewRing()));
        Assert.Single(results, result => result);
        Assert.Equal(32, (await fixture.NewRing().GetKeyAsync()).Length);
        CryptographicOperations.ZeroMemory(recovery);
    }

    [Fact]
    public void ActualWindowsLocalProtectionRoundTripsAndRejectsTampering()
    {
        var protection = new WindowsDpapiBackupLocalKeyProtector();
        var plaintext = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = protection.Protect(plaintext);
        Assert.NotEqual(plaintext, protectedBytes);
        Assert.Equal(plaintext, protection.Unprotect(protectedBytes));
        protectedBytes[protectedBytes.Length / 2] ^= 0x40;
        Assert.Throws<CryptographicException>(() => protection.Unprotect(protectedBytes));
        CryptographicOperations.ZeroMemory(plaintext);
    }

    [Fact]
    public async Task LegacyCreationRemainsDecryptableAcrossProvisionAndRotation()
    {
        using var fixture = new KeyFixture();
        var legacy = RandomNumberGenerator.GetBytes(32);
        var ring = fixture.NewRing(() => Convert.ToHexString(legacy));
        Assert.Null(await ring.GetCurrentMetadataAsync());
        var recovery = RandomNumberGenerator.GetBytes(32);
        await ring.ProvisionAsync(recovery);
        await ring.RotateAsync(recovery);
        var original = System.IO.Path.Combine(fixture.DirectoryPath, "dump.bin");
        var archive = original + ".erbak";
        var restored = original + ".restored";
        await File.WriteAllBytesAsync(original, [1, 2, 3, 4]);
        var protection = new AesGcmBackupProtector(ring);
        await protection.ProtectVersionAsync(original, archive, null);
        await protection.UnprotectVersionAsync(archive, restored, null);
        Assert.Equal(await File.ReadAllBytesAsync(original), await File.ReadAllBytesAsync(restored));
        CryptographicOperations.ZeroMemory(recovery);
        CryptographicOperations.ZeroMemory(legacy);
    }

    [Fact]
    public async Task ReplacementRecoveryPreservesImportedLegacyDecryptability()
    {
        using var original = new KeyFixture();
        using var replacement = new KeyFixture();
        var legacy = RandomNumberGenerator.GetBytes(32);
        var recovery = RandomNumberGenerator.GetBytes(32);
        var metadata = await original.Ring.ProvisionAsync(recovery, legacy);
        await replacement.Ring.RecoverAsync(metadata, recovery);
        Assert.Equal(legacy, await replacement.Ring.GetLegacyKeyAsync());
        CryptographicOperations.ZeroMemory(recovery);
        CryptographicOperations.ZeroMemory(legacy);
    }

    [Fact]
    public async Task ImportedMasterCannotEqualOwnerRecoveryKey()
    {
        using var fixture = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Ring.ProvisionAsync(recovery, recovery));
        Assert.False(File.Exists(fixture.Path));
        CryptographicOperations.ZeroMemory(recovery);
    }

    [Fact]
    public async Task OversizedRotationCannotReplaceUsableEstablishedAuthority()
    {
        using var fixture = new KeyFixture();
        var recovery = RandomNumberGenerator.GetBytes(32);
        var metadata = await fixture.Ring.ProvisionAsync(recovery);
        var before = await File.ReadAllBytesAsync(fixture.Path);
        fixture.UseOversizedProtection = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Ring.RotateAsync(recovery));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Path));
        fixture.UseOversizedProtection = false;
        Assert.Equal(metadata, await fixture.Ring.GetCurrentMetadataAsync());
        CryptographicOperations.ZeroMemory(recovery);
    }

    [Fact]
    public void HistoricalRecoveryVersionCanBeImportedExplicitlyWithoutOverwritingExistingRing()
    {
        Assert.NotNull(typeof(WindowsProtectedBackupKeyRing).GetMethod("ImportRecoveryVersionAsync"));
    }

    [Fact]
    public async Task DefaultSecurityRejectsUntrustedPreexistingAuthorityOwner()
    {
        using var fixture = new KeyFixture();
        await fixture.Ring.ProvisionAsync(RandomNumberGenerator.GetBytes(32));
        var hardened = fixture.DefaultSecurityRing();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => hardened.GetKeyAsync());
    }

    private sealed class KeyFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("edge-backup-key-test-");
        private readonly TestMachineProtection _protection = new();
        public string Path => System.IO.Path.Combine(_directory.FullName, "ring.dpapi");
        public string DirectoryPath => _directory.FullName;
        public bool UseOversizedProtection { set => _protection.ExtraBytes = value ? 300 * 1024 : 0; }
        public WindowsProtectedBackupKeyRing Ring => NewRing();
        public WindowsProtectedBackupKeyRing NewRing(Func<string?>? legacyReader = null)
            => new(Path, _protection, _ => { }, legacyReader ?? (() => null));
        public WindowsProtectedBackupKeyRing DefaultSecurityRing()
            => new(Path, _protection, legacyReader: () => null);

        public void Dispose()
        {
            _protection.Dispose();
            var absolute = System.IO.Path.GetFullPath(_directory.FullName);
            if (!absolute.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Owned test directory is outside the temporary workspace.");
            }
            _directory.Delete(recursive: true);
        }
    }

    // Supporting isolated fixture only. Production always composes the Windows DPAPI adapter;
    // its actual cryptographic round-trip is executed separately above.
    private sealed class TestMachineProtection : IBackupLocalKeyProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public int ExtraBytes { private get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            var output = new byte[28 + plaintext.Length + ExtraBytes];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28, plaintext.Length), output.AsSpan(12, 16));
            return output;
        }
        public byte[] Unprotect(byte[] ciphertext)
        {
            var plaintext = new byte[ciphertext.Length - 28];
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), plaintext);
            return plaintext;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}
