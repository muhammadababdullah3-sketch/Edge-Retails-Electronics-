using System.Security.Cryptography;
using EdgeRetails.ProtectedSigner.Keys;

namespace EdgeRetails.Central.UnitTests.Signing;

public sealed class PersistentDevelopmentPrivateKeyStoreTests
{
    [Fact]
    public void RootAndRegisteredKeysPersistAcrossStoreRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var root = NewOwnedTempRoot();
        try
        {
            string rootPublicKey;
            string signingPublicKey;
            byte[] protectedFile;
            byte[] rawPrivateKey;
            using (var store = new PersistentDevelopmentPrivateKeyStore(root))
            using (var key = RSA.Create(2048))
            {
                store.StoreKey("development-signing-key-1", key);
                rootPublicKey = store.GetPublicKeyPem(InMemoryPrivateKeyStore.RootAuthorityKeyId)!;
                signingPublicKey = store.GetPublicKeyPem("development-signing-key-1")!;
                protectedFile = File.ReadAllBytes(Path.Combine(root, PersistentDevelopmentPrivateKeyStore.StoreFileName));
                rawPrivateKey = key.ExportPkcs8PrivateKey();
            }

            try
            {
                Assert.Equal(-1, protectedFile.AsSpan().IndexOf(rawPrivateKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawPrivateKey);
            }

            using var restarted = new PersistentDevelopmentPrivateKeyStore(root);
            Assert.Equal(rootPublicKey, restarted.GetPublicKeyPem(InMemoryPrivateKeyStore.RootAuthorityKeyId));
            Assert.Equal(signingPublicKey, restarted.GetPublicKeyPem("development-signing-key-1"));
            Assert.True(restarted.HasKey("development-signing-key-1"));

            var payload = "central development restart proof"u8.ToArray();
            var signature = restarted.GetKey("development-signing-key-1")!.SignData(
                payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(signingPublicKey);
            Assert.True(publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        finally
        {
            DeleteOwnedTempRoot(root);
        }
    }

    [Fact]
    public void TamperedProtectedFileFailsClosedWithoutGeneratingReplacementRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var root = NewOwnedTempRoot();
        try
        {
            using (new PersistentDevelopmentPrivateKeyStore(root)) { }
            var storeFile = Path.Combine(root, PersistentDevelopmentPrivateKeyStore.StoreFileName);
            var ciphertext = File.ReadAllBytes(storeFile);
            ciphertext[^1] ^= 0x5A;
            File.WriteAllBytes(storeFile, ciphertext);

            Assert.ThrowsAny<CryptographicException>(() => new PersistentDevelopmentPrivateKeyStore(root));
        }
        finally
        {
            DeleteOwnedTempRoot(root);
        }
    }

    [Fact]
    public void ExistingKeyIdCannotBeOverwrittenOrReused()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var root = NewOwnedTempRoot();
        try
        {
            using var store = new PersistentDevelopmentPrivateKeyStore(root);
            using var first = RSA.Create(2048);
            using var replacement = RSA.Create(2048);
            store.StoreKey("immutable-development-key", first);
            var originalPublicKey = store.GetPublicKeyPem("immutable-development-key");

            Assert.Throws<InvalidOperationException>(() => store.StoreKey("immutable-development-key", replacement));
            Assert.Equal(originalPublicKey, store.GetPublicKeyPem("immutable-development-key"));
        }
        finally
        {
            DeleteOwnedTempRoot(root);
        }
    }

    [Fact]
    public void MissingStoreWithUnexpectedFilesDoesNotCreateNewRootKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var root = NewOwnedTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "unrecognized.data"), "preserve");
            Assert.Throws<InvalidDataException>(() => new PersistentDevelopmentPrivateKeyStore(root));
            Assert.False(File.Exists(Path.Combine(root, PersistentDevelopmentPrivateKeyStore.StoreFileName)));
        }
        finally
        {
            DeleteOwnedTempRoot(root);
        }
    }

    [Fact]
    public void OversizedProtectedFileFailsClosedBeforeReadingItsContents()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var root = NewOwnedTempRoot();
        try
        {
            using (new PersistentDevelopmentPrivateKeyStore(root)) { }
            var storeFile = Path.Combine(root, PersistentDevelopmentPrivateKeyStore.StoreFileName);
            using (var stream = new FileStream(storeFile, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(1024 * 1024 + 4097);
            }

            Assert.Throws<InvalidDataException>(() => new PersistentDevelopmentPrivateKeyStore(root));
        }
        finally
        {
            DeleteOwnedTempRoot(root);
        }
    }

    [Fact]
    public void StoreAndLockSymbolicLinksAreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows symbolic-link semantics are required for this development-key store test.");
        }

        var storeRoot = NewOwnedTempRoot();
        var targetRoot = NewOwnedTempRoot();
        var storePath = Path.Combine(storeRoot, PersistentDevelopmentPrivateKeyStore.StoreFileName);
        var lockPath = Path.Combine(storeRoot, ".central-private-keys.lock");
        var targetPath = Path.Combine(targetRoot, "external-target.bin");
        var danglingTargetPath = Path.Combine(targetRoot, "dangling-target.bin");

        try
        {
            Directory.CreateDirectory(storeRoot);
            Directory.CreateDirectory(targetRoot);
            File.WriteAllText(targetPath, "preserve external target");

            File.CreateSymbolicLink(storePath, targetPath);
            Assert.Throws<IOException>(() => new PersistentDevelopmentPrivateKeyStore(storeRoot));
            Assert.Equal("preserve external target", File.ReadAllText(targetPath));

            File.Delete(storePath);
            using (new PersistentDevelopmentPrivateKeyStore(storeRoot)) { }
            File.Delete(lockPath);
            File.CreateSymbolicLink(lockPath, danglingTargetPath);

            Assert.Throws<IOException>(() => new PersistentDevelopmentPrivateKeyStore(storeRoot));
            Assert.True(File.Exists(targetPath));
            Assert.False(File.Exists(danglingTargetPath));
        }
        finally
        {
            if (File.Exists(storePath))
            {
                File.Delete(storePath);
            }
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }
            DeleteOwnedTempRoot(storeRoot);
            DeleteOwnedTempRoot(targetRoot);
        }
    }

    [Fact]
    public async Task IndependentInstancesSerializeConcurrentWritesAndBothKeysSurviveRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var root = NewOwnedTempRoot();
        try
        {
            using var firstStore = new PersistentDevelopmentPrivateKeyStore(root);
            using var secondStore = new PersistentDevelopmentPrivateKeyStore(root);
            using var firstKey = RSA.Create(2048);
            using var secondKey = RSA.Create(2048);
            using var start = new ManualResetEventSlim(false);

            var firstWrite = Task.Run(() =>
            {
                start.Wait();
                firstStore.StoreKey("concurrent-development-key-1", firstKey);
            });
            var secondWrite = Task.Run(() =>
            {
                start.Wait();
                secondStore.StoreKey("concurrent-development-key-2", secondKey);
            });

            start.Set();
            await Task.WhenAll(firstWrite, secondWrite);

            var firstPublicKey = firstKey.ExportSubjectPublicKeyInfoPem();
            var secondPublicKey = secondKey.ExportSubjectPublicKeyInfoPem();
            using var restarted = new PersistentDevelopmentPrivateKeyStore(root);
            Assert.Equal(firstPublicKey, restarted.GetPublicKeyPem("concurrent-development-key-1"));
            Assert.Equal(secondPublicKey, restarted.GetPublicKeyPem("concurrent-development-key-2"));
        }
        finally
        {
            DeleteOwnedTempRoot(root);
        }
    }

    [Fact]
    public void CopyingProtectedStoreToAnotherRootDoesNotRecreateOrExposeKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows DPAPI CurrentUser is required for this development-key store test.");
        }

        var originalRoot = NewOwnedTempRoot();
        var copiedRoot = NewOwnedTempRoot();
        try
        {
            using (new PersistentDevelopmentPrivateKeyStore(originalRoot)) { }
            Directory.CreateDirectory(copiedRoot);
            var copiedStorePath = Path.Combine(copiedRoot, PersistentDevelopmentPrivateKeyStore.StoreFileName);
            File.Copy(Path.Combine(originalRoot, PersistentDevelopmentPrivateKeyStore.StoreFileName), copiedStorePath);

            Assert.ThrowsAny<CryptographicException>(() => new PersistentDevelopmentPrivateKeyStore(copiedRoot));
            Assert.True(File.Exists(copiedStorePath));
        }
        finally
        {
            DeleteOwnedTempRoot(originalRoot);
            DeleteOwnedTempRoot(copiedRoot);
        }
    }

    private static string NewOwnedTempRoot() =>
        Path.Combine(Path.GetTempPath(), "EdgeRetails-Central-DevKeyStoreTests", Guid.NewGuid().ToString("N"));

    private static void DeleteOwnedTempRoot(string root)
    {
        var ownedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EdgeRetails-Central-DevKeyStoreTests"));
        var fullRoot = Path.GetFullPath(root);
        var prefix = Path.TrimEndingDirectorySeparator(ownedParent) + Path.DirectorySeparatorChar;
        if (fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
