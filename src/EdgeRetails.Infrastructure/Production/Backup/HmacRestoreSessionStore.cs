using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed class HmacRestoreSessionStore : IRestoreSessionStore
{
    private const long MaximumJournalBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _directory;
    private readonly IRestoreJournalIntegrityKeyProvider _keys;

    public HmacRestoreSessionStore(string directory, IRestoreJournalIntegrityKeyProvider keys)
    {
        _directory = Path.GetFullPath(directory ?? throw new ArgumentNullException(nameof(directory)));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public async Task CreateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var path = GetPath(session.RestoreId);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("Restore session already exists.");
        }

        await WriteProtectedAsync(path, session, overwrite: false, cancellationToken);
    }

    public async Task<RestoreSessionRecord?> GetAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(restoreId);
        if (!File.Exists(path))
        {
            return null;
        }

        JournalEnvelope envelope;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumJournalBytes)
            {
                throw new InvalidDataException("Restore journal size is invalid.");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: false);
            var json = await reader.ReadToEndAsync(cancellationToken);
            envelope = JsonSerializer.Deserialize<JournalEnvelope>(json, JsonOptions)
                ?? throw new InvalidDataException("Restore journal envelope is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Restore journal JSON is invalid.", ex);
        }
        if (string.IsNullOrWhiteSpace(envelope.PayloadBase64) || string.IsNullOrWhiteSpace(envelope.HmacBase64))
        {
            throw new InvalidDataException("Restore journal envelope is incomplete.");
        }

        byte[] payload;
        byte[] suppliedMac;
        try
        {
            payload = Convert.FromBase64String(envelope.PayloadBase64);
            suppliedMac = Convert.FromBase64String(envelope.HmacBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Restore journal encoding is invalid.", ex);
        }

        byte[]? key = null;
        byte[]? expected = null;
        try
        {
            key = await GetValidatedKeyAsync(cancellationToken);
            using (var hmac = new HMACSHA256(key))
            {
                expected = hmac.ComputeHash(payload);
            }

            if (suppliedMac.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(suppliedMac, expected))
            {
                throw new InvalidDataException("Restore journal integrity verification failed.");
            }

            RestoreSessionRecord session;
            try
            {
                session = JsonSerializer.Deserialize<RestoreSessionRecord>(payload, JsonOptions)
                    ?? throw new InvalidDataException("Restore journal payload is invalid.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Restore journal payload JSON is invalid.", ex);
            }

            if (session.RestoreId != restoreId)
            {
                throw new InvalidDataException("Restore journal identity mismatch.");
            }

            return session;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(suppliedMac);
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            if (expected is not null)
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
    }

    public async Task UpdateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default)
    {
        var path = GetPath(session.RestoreId);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Restore session does not exist.");
        }

        _ = await GetAsync(session.RestoreId, cancellationToken); // fail closed if existing journal was tampered.
        await WriteProtectedAsync(path, session, overwrite: true, cancellationToken);
    }

    private async Task WriteProtectedAsync(string path, RestoreSessionRecord session, bool overwrite, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var key = await GetValidatedKeyAsync(cancellationToken);
        byte[]? mac = null;
        byte[]? body = null;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var hmac = new HMACSHA256(key))
            {
                mac = hmac.ComputeHash(payload);
            }

            var envelope = new JournalEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(mac));
            body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (body.LongLength > MaximumJournalBytes)
            {
                throw new InvalidDataException("Restore journal exceeded its maximum size.");
            }

            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
            }

            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(key);
            if (mac is not null)
            {
                CryptographicOperations.ZeroMemory(mac);
            }

            if (body is not null)
            {
                CryptographicOperations.ZeroMemory(body);
            }
        }
    }

    private async Task<byte[]> GetValidatedKeyAsync(CancellationToken cancellationToken)
    {
        var key = await _keys.GetIntegrityKeyAsync(cancellationToken);
        if (key is null || key.Length < 32)
        {
            throw new InvalidOperationException("Restore journal integrity key must contain at least 32 bytes.");
        }

        return key.ToArray();
    }

    private string GetPath(Guid restoreId)
        => Path.Combine(_directory, $"restore-{restoreId:N}.journal");

    private sealed record JournalEnvelope(string PayloadBase64, string HmacBase64);
}
