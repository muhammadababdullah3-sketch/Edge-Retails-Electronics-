using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EdgeRetails.ProtectedSigner.Keys;

/// <summary>
/// Development-only persistent private-key storage protected by Windows DPAPI CurrentUser.
/// The same Windows identity and explicit storage root must be used on restart. This is not
/// a production HSM, a cross-account recovery mechanism, or an installation-proof validator.
/// </summary>
public sealed class PersistentDevelopmentPrivateKeyStore : IPrivateKeyStore, IDisposable
{
    public const string StoreFileName = "central-private-keys.dpapi";

    private const int FormatVersion = 1;
    private const int MaximumStoreBytes = 1024 * 1024;
    private const int MaximumProtectedStoreBytes = MaximumStoreBytes + 4096;
    private const int MaximumKeyCount = 1024;
    private const int MaximumKeyIdBytes = 512;
    private const int MaximumPrivateKeyBytes = 64 * 1024;
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Magic = "ERPKST01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _storageRoot;
    private readonly string _storePath;
    private readonly string _lockPath;
    private readonly byte[] _entropy;
    private readonly Dictionary<string, RSA> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public PersistentDevelopmentPrivateKeyStore(string storageRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Persistent development keys require Windows DPAPI CurrentUser; no in-memory fallback is provided.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _storageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot));
        EnsureExistingPathHasNoReparseComponents(_storageRoot);
        Directory.CreateDirectory(_storageRoot);
        EnsureExistingPathHasNoReparseComponents(_storageRoot);

        _storePath = Path.Combine(_storageRoot, StoreFileName);
        _lockPath = Path.Combine(_storageRoot, ".central-private-keys.lock");
        _entropy = Encoding.UTF8.GetBytes($"EdgeRetails.Central.DevelopmentPrivateKeys.v1|{_storageRoot}");

        using var lease = AcquireStoreLock();
        if (File.Exists(_storePath))
        {
            var loaded = ReadStore();
            try
            {
                if (!loaded.ContainsKey(InMemoryPrivateKeyStore.RootAuthorityKeyId))
                {
                    throw new InvalidDataException("Persistent signer store has no root authority key; refusing to generate a replacement.");
                }

                foreach (var pair in loaded)
                {
                    _keys.Add(pair.Key, pair.Value);
                }
                loaded.Clear();
            }
            finally
            {
                DisposeKeys(loaded);
            }
        }
        else
        {
            var unexpected = Directory.EnumerateFileSystemEntries(_storageRoot)
                .Where(path => !string.Equals(path, _lockPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (unexpected.Length != 0)
            {
                throw new InvalidDataException("The configured signer root is not empty but its protected key store is missing.");
            }

            using var root = RSA.Create(2048);
            var rootOwned = CloneKey(root);
            try
            {
                var initial = new Dictionary<string, RSA>(StringComparer.Ordinal)
                {
                    [InMemoryPrivateKeyStore.RootAuthorityKeyId] = rootOwned
                };
                PersistStore(initial);
                _keys.Add(InMemoryPrivateKeyStore.RootAuthorityKeyId, rootOwned);
                rootOwned = null!;
            }
            finally
            {
                rootOwned?.Dispose();
            }
        }
    }

    public void StoreKey(string keyId, RSA rsa)
    {
        ValidateKeyId(keyId);
        ArgumentNullException.ThrowIfNull(rsa);

        lock (_gate)
        {
            ThrowIfDisposed();
            using var lease = AcquireStoreLock();
            var diskKeys = ReadStore();
            RSA? ownedKey = null;
            try
            {
                ValidateAndMergeDiskKeys(diskKeys);
                if (diskKeys.ContainsKey(keyId) || _keys.ContainsKey(keyId))
                {
                    throw new InvalidOperationException($"Private key ID '{keyId}' is already persisted and cannot be reused.");
                }

                ownedKey = CloneKey(rsa);
                var next = new Dictionary<string, RSA>(diskKeys, StringComparer.Ordinal)
                {
                    [keyId] = ownedKey
                };
                PersistStore(next);
                _keys.Add(keyId, ownedKey);
                ownedKey = null;
            }
            finally
            {
                ownedKey?.Dispose();
                DisposeKeys(diskKeys);
            }
        }
    }

    public RSA? GetKey(string keyId)
    {
        ValidateKeyId(keyId);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_keys.TryGetValue(keyId, out var cached))
            {
                return cached;
            }

            using var lease = AcquireStoreLock();
            var diskKeys = ReadStore();
            try
            {
                ValidateAndMergeDiskKeys(diskKeys);
                return _keys.TryGetValue(keyId, out cached) ? cached : null;
            }
            finally
            {
                DisposeKeys(diskKeys);
            }
        }
    }

    public bool HasKey(string keyId) => GetKey(keyId) is not null;

    public string? GetPublicKeyPem(string keyId) => GetKey(keyId)?.ExportSubjectPublicKeyInfoPem();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DisposeKeys(_keys);
            _keys.Clear();
            CryptographicOperations.ZeroMemory(_entropy);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private void ValidateAndMergeDiskKeys(Dictionary<string, RSA> diskKeys)
    {
        foreach (var pair in _keys)
        {
            if (!diskKeys.TryGetValue(pair.Key, out var diskKey) ||
                !string.Equals(pair.Value.ExportSubjectPublicKeyInfoPem(), diskKey.ExportSubjectPublicKeyInfoPem(), StringComparison.Ordinal))
            {
                throw new CryptographicException("Persistent signer key state changed or rolled back while this process was running.");
            }
        }

        var additions = new Dictionary<string, RSA>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in diskKeys)
            {
                if (!_keys.ContainsKey(pair.Key))
                {
                    additions.Add(pair.Key, CloneKey(pair.Value));
                }
            }

            foreach (var pair in additions)
            {
                _keys.Add(pair.Key, pair.Value);
            }
            additions.Clear();
        }
        finally
        {
            DisposeKeys(additions);
        }
    }

    private Dictionary<string, RSA> ReadStore()
    {
        EnsureRegularFileIfPresent(_storePath);
        if (!File.Exists(_storePath))
        {
            throw new InvalidDataException("Persistent signer key store disappeared; refusing to generate an in-memory replacement.");
        }

        byte[] ciphertext;
        using (var stream = new FileStream(_storePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length is < 32 or > MaximumProtectedStoreBytes)
            {
                throw new InvalidDataException("Persistent signer key store has an invalid size.");
            }

            ciphertext = new byte[checked((int)stream.Length)];
            stream.ReadExactly(ciphertext);
            if (stream.ReadByte() != -1)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                throw new InvalidDataException("Persistent signer key store changed while it was being read.");
            }
        }

        byte[]? plaintext = null;
        try
        {
            plaintext = DpapiUnprotect(ciphertext, _entropy);
            return DeserializeStore(plaintext);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private void PersistStore(IReadOnlyDictionary<string, RSA> keys)
    {
        var plaintext = SerializeStore(keys);
        byte[]? ciphertext = null;
        try
        {
            ciphertext = DpapiProtect(plaintext, _entropy);
            AtomicWrite(ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
    }

    private static byte[] SerializeStore(IReadOnlyDictionary<string, RSA> keys)
    {
        if (keys.Count is < 1 or > MaximumKeyCount)
        {
            throw new InvalidDataException("Persistent signer key count is outside the supported bounds.");
        }

        using var stream = new MemoryStream();
        try
        {
            stream.Write(Magic);
            WriteInt32(stream, FormatVersion);
            WriteInt32(stream, keys.Count);

            foreach (var pair in keys.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                ValidateKeyId(pair.Key);
                var idBytes = StrictUtf8.GetBytes(pair.Key);
                var privateKeyBytes = pair.Value.ExportPkcs8PrivateKey();
                try
                {
                    if (privateKeyBytes.Length is < 128 or > MaximumPrivateKeyBytes)
                    {
                        throw new InvalidDataException("Private key material is outside the supported bounds.");
                    }

                    WriteInt32(stream, idBytes.Length);
                    stream.Write(idBytes);
                    WriteInt32(stream, privateKeyBytes.Length);
                    stream.Write(privateKeyBytes);
                    if (stream.Length > MaximumStoreBytes)
                    {
                        throw new InvalidDataException("Persistent signer key store exceeds its maximum size.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateKeyBytes);
                    CryptographicOperations.ZeroMemory(idBytes);
                }
            }

            return stream.ToArray();
        }
        finally
        {
            if (stream.TryGetBuffer(out var buffer) && stream.Length > 0)
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)stream.Length)));
            }
        }
    }

    private static Dictionary<string, RSA> DeserializeStore(byte[] plaintext)
    {
        if (plaintext.Length > MaximumStoreBytes || plaintext.Length < Magic.Length + 8 ||
            !plaintext.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Persistent signer key store header is invalid.");
        }

        var offset = Magic.Length;
        var version = ReadInt32(plaintext, ref offset);
        var count = ReadInt32(plaintext, ref offset);
        if (version != FormatVersion || count is < 1 or > MaximumKeyCount)
        {
            throw new InvalidDataException("Persistent signer key store version or key count is invalid.");
        }

        var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var idLength = ReadInt32(plaintext, ref offset);
                if (idLength is < 1 or > MaximumKeyIdBytes)
                {
                    throw new InvalidDataException("Persistent signer key ID length is invalid.");
                }

                var idBytes = ReadBytes(plaintext, ref offset, idLength);
                string keyId;
                try
                {
                    keyId = StrictUtf8.GetString(idBytes);
                    ValidateKeyId(keyId);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException("Persistent signer key ID is not valid UTF-8.", ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(idBytes);
                }

                var privateLength = ReadInt32(plaintext, ref offset);
                if (privateLength is < 128 or > MaximumPrivateKeyBytes)
                {
                    throw new InvalidDataException("Persistent signer private key length is invalid.");
                }

                var privateBytes = ReadBytes(plaintext, ref offset, privateLength);
                try
                {
                    var rsa = RSA.Create();
                    try
                    {
                        rsa.ImportPkcs8PrivateKey(privateBytes, out var consumed);
                        if (consumed != privateBytes.Length || rsa.KeySize < 2048)
                        {
                            throw new InvalidDataException("Persistent signer key is incomplete or below the supported RSA strength.");
                        }

                        if (!keys.TryAdd(keyId, rsa))
                        {
                            throw new InvalidDataException("Persistent signer store contains a duplicate key ID.");
                        }
                        rsa = null!;
                    }
                    finally
                    {
                        rsa?.Dispose();
                    }
                }
                catch (CryptographicException ex)
                {
                    throw new InvalidDataException("Persistent signer store contains invalid private key material.", ex);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateBytes);
                }
            }

            if (offset != plaintext.Length)
            {
                throw new InvalidDataException("Persistent signer key store has trailing data.");
            }

            return keys;
        }
        catch
        {
            DisposeKeys(keys);
            throw;
        }
    }

    private void AtomicWrite(byte[] ciphertext)
    {
        EnsureRegularFileIfPresent(_storePath);
        var tempPath = Path.Combine(_storageRoot, $".keys-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _storePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private FileStream AcquireStoreLock()
    {
        EnsureRegularFileIfPresent(_lockPath);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void EnsureRegularFileIfPresent(string path)
    {
        var info = new FileInfo(path);
        try
        {
            if (info.LinkTarget is not null)
            {
                throw new IOException("Persistent signer store cannot use a symbolic link or reparse point.");
            }
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }

        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new IOException("Persistent signer store contains a directory or reparse point where a file is required.");
        }
    }

    private static void EnsureExistingPathHasNoReparseComponents(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Persistent signer root cannot contain a symbolic link or reparse point.");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }
    }

    private static RSA CloneKey(RSA source)
    {
        var privateKeyBytes = source.ExportPkcs8PrivateKey();
        try
        {
            var clone = RSA.Create();
            try
            {
                clone.ImportPkcs8PrivateKey(privateKeyBytes, out var consumed);
                if (consumed != privateKeyBytes.Length || clone.KeySize < 2048)
                {
                    throw new CryptographicException("Only complete RSA keys of at least 2048 bits are supported.");
                }
                return clone;
            }
            catch
            {
                clone.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }

    private static void ValidateKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (StrictUtf8.GetByteCount(keyId) > MaximumKeyIdBytes)
        {
            throw new ArgumentException("Key ID exceeds the persistent store limit.", nameof(keyId));
        }
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(byte[] source, ref int offset)
    {
        if (offset < 0 || source.Length - offset < sizeof(int))
        {
            throw new InvalidDataException("Persistent signer store is truncated.");
        }
        var value = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static byte[] ReadBytes(byte[] source, ref int offset, int count)
    {
        if (count < 0 || offset < 0 || source.Length - offset < count)
        {
            throw new InvalidDataException("Persistent signer store is truncated.");
        }
        var bytes = source.AsSpan(offset, count).ToArray();
        offset += count;
        return bytes;
    }

    private static void DisposeKeys(IDictionary<string, RSA> keys)
    {
        foreach (var key in keys.Values)
        {
            key.Dispose();
        }
        keys.Clear();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static byte[] DpapiProtect(byte[] plaintext, byte[] entropy)
    {
        var input = AllocateBlob(plaintext);
        var optionalEntropy = AllocateBlob(entropy);
        DataBlob output = default;
        try
        {
            if (!CryptProtectData(ref input, "EdgeRetails Central development signing keys", ref optionalEntropy,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            return CopyNativeBytes(output);
        }
        finally
        {
            FreeInputBlob(ref input);
            FreeInputBlob(ref optionalEntropy);
            FreeDpapiBlob(ref output);
        }
    }

    private static byte[] DpapiUnprotect(byte[] ciphertext, byte[] entropy)
    {
        var input = AllocateBlob(ciphertext);
        var optionalEntropy = AllocateBlob(entropy);
        DataBlob output = default;
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }
            return CopyNativeBytes(output);
        }
        finally
        {
            FreeInputBlob(ref input);
            FreeInputBlob(ref optionalEntropy);
            FreeDpapiBlob(ref output);
        }
    }

    private static DataBlob AllocateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return new DataBlob { Length = bytes.Length, Data = pointer };
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    private static byte[] CopyNativeBytes(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero || blob.Length is < 1 or > MaximumStoreBytes + 4096)
        {
            throw new CryptographicException("Windows DPAPI returned an invalid protected-data buffer.");
        }

        var bytes = new byte[blob.Length];
        Marshal.Copy(blob.Data, bytes, 0, blob.Length);
        return bytes;
    }

    private static void FreeInputBlob(ref DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            if (blob.Length > 0)
            {
                ZeroNativeBytes(blob.Data, blob.Length);
            }
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }
    }

    private static void FreeDpapiBlob(ref DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            if (blob.Length > 0)
            {
                ZeroNativeBytes(blob.Data, blob.Length);
            }
            LocalFree(blob.Data);
            blob = default;
        }
    }

    private static void ZeroNativeBytes(IntPtr pointer, int length)
    {
        var zeroes = new byte[Math.Min(length, 4096)];
        for (var offset = 0; offset < length; offset += zeroes.Length)
        {
            Marshal.Copy(zeroes, 0, IntPtr.Add(pointer, offset), Math.Min(zeroes.Length, length - offset));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
