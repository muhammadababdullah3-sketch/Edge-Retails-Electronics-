using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed class AesGcmBackupProtector : IBackupProtector
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ERBAK002");
    private const int ChunkSize = 1024 * 1024;
    private readonly IBackupEncryptionKeyProvider _keyProvider;
    public string ProtectionName => "AES-256-GCM-CHUNKED-V2";

    public AesGcmBackupProtector(IBackupEncryptionKeyProvider keyProvider)
        => _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));

    public async Task ProtectAsync(string plainDumpPath, string protectedBackupPath, CancellationToken cancellationToken = default)
    {
        var key = await GetValidatedKeyCloneAsync(cancellationToken);
        try
        {
            var noncePrefix = RandomNumberGenerator.GetBytes(8);
            await using var input = new FileStream(plainDumpPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
            await using var output = new FileStream(protectedBackupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true);
            await output.WriteAsync(Magic, cancellationToken);
            await output.WriteAsync(noncePrefix, cancellationToken);

            var buffer = new byte[ChunkSize];
            var chunkIndex = 0;
            long totalPlaintextBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                var cipher = new byte[read];
                var tag = new byte[16];
                var nonce = BuildNonce(noncePrefix, chunkIndex);
                var aad = BuildChunkAad(chunkIndex, read);
                using (var aes = new AesGcm(key, tagSizeInBytes: 16))
                {
                    aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher, tag, aad);
                }

                await WriteHeaderAsync(output, chunkIndex, read, cancellationToken);
                await output.WriteAsync(tag, cancellationToken);
                await output.WriteAsync(cipher, cancellationToken);
                checked
                {
                    chunkIndex++;
                    totalPlaintextBytes += read;
                }
            }

            // Authenticated end-of-stream marker. Without this, a backup truncated cleanly at a chunk
            // boundary could look complete because every surviving chunk would still authenticate.
            var footerTag = new byte[16];
            var footerNonce = BuildNonce(noncePrefix, chunkIndex);
            var footerAad = BuildFooterAad(chunkIndex, totalPlaintextBytes);
            using (var aes = new AesGcm(key, tagSizeInBytes: 16))
            {
                aes.Encrypt(footerNonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, footerTag, footerAad);
            }

            await WriteHeaderAsync(output, chunkIndex, 0, cancellationToken);
            await output.WriteAsync(footerTag, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task UnprotectAsync(string protectedBackupPath, string plainDumpPath, CancellationToken cancellationToken = default)
    {
        var key = await GetValidatedKeyCloneAsync(cancellationToken);
        try
        {
            await using var input = new FileStream(protectedBackupPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);
            var magic = new byte[Magic.Length];
            await ReadExactlyAsync(input, magic, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(magic, Magic))
            {
                throw new InvalidDataException("Backup encryption header is invalid or uses an unsupported format.");
            }

            var noncePrefix = new byte[8];
            await ReadExactlyAsync(input, noncePrefix, cancellationToken);
            await using var output = new FileStream(plainDumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, useAsync: true);

            var expectedIndex = 0;
            long totalPlaintextBytes = 0;
            var footerSeen = false;
            while (input.Position < input.Length)
            {
                var header = new byte[8];
                await ReadExactlyAsync(input, header, cancellationToken);
                var chunkIndex = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                if (chunkIndex != expectedIndex || length < 0 || length > ChunkSize)
                {
                    throw new InvalidDataException("Backup encryption chunk metadata is invalid.");
                }

                var tag = new byte[16];
                await ReadExactlyAsync(input, tag, cancellationToken);

                if (length == 0)
                {
                    if (input.Position != input.Length)
                    {
                        throw new InvalidDataException("Encrypted backup contains data after its authenticated end marker.");
                    }

                    var footerNonce = BuildNonce(noncePrefix, chunkIndex);
                    var footerAad = BuildFooterAad(chunkIndex, totalPlaintextBytes);
                    using (var aes = new AesGcm(key, tagSizeInBytes: 16))
                    {
                        aes.Decrypt(footerNonce, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, footerAad);
                    }

                    footerSeen = true;
                    break;
                }

                var cipher = new byte[length];
                await ReadExactlyAsync(input, cipher, cancellationToken);
                var plain = new byte[length];
                var nonce = BuildNonce(noncePrefix, chunkIndex);
                var aad = BuildChunkAad(chunkIndex, length);
                using (var aes = new AesGcm(key, tagSizeInBytes: 16))
                {
                    aes.Decrypt(nonce, cipher, tag, plain, aad);
                }

                await output.WriteAsync(plain, cancellationToken);
                checked
                {
                    expectedIndex++;
                    totalPlaintextBytes += length;
                }
            }

            if (!footerSeen)
            {
                throw new InvalidDataException("Encrypted backup is missing its authenticated end marker.");
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        catch (CryptographicException ex)
        {
            TryDelete(plainDumpPath);
            throw new InvalidDataException("Backup authentication failed.", ex);
        }
        catch
        {
            TryDelete(plainDumpPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<byte[]> GetValidatedKeyCloneAsync(CancellationToken cancellationToken)
    {
        var providerKey = await _keyProvider.GetKeyAsync(cancellationToken);
        if (providerKey is null || providerKey.Length != 32)
        {
            throw new InvalidOperationException("Backup encryption key provider must return exactly 32 bytes.");
        }

        return providerKey.ToArray();
    }

    private static byte[] BuildNonce(byte[] prefix, int index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] BuildChunkAad(int index, int length)
    {
        var aad = new byte[16];
        Magic.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(8, 4), index);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(12, 4), length);
        return aad;
    }

    private static byte[] BuildFooterAad(int index, long totalPlaintextBytes)
    {
        var aad = new byte[24];
        Magic.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(8, 4), index);
        Encoding.ASCII.GetBytes("END!").CopyTo(aad, 12);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(16, 8), totalPlaintextBytes);
        return aad;
    }

    private static async Task WriteHeaderAsync(Stream output, int index, int length, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), index);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), length);
        await output.WriteAsync(header, cancellationToken);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (n == 0)
            {
                throw new EndOfStreamException("Encrypted backup is truncated.");
            }

            read += n;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch { }
    }
}
