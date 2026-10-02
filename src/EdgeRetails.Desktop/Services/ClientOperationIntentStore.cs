using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EdgeRetails.Desktop.Services;

/// <summary>Persists opaque client operation identities so retries survive a desktop restart.</summary>
public interface IClientOperationIntentStore
{
    Guid GetOrCreate(string operationKey, string payload);

    IReadOnlyList<ClientOperationIntent> FindPendingByPrefix(string operationKeyPrefix)
        => [];

    Guid GetOrCreate(string operationKey, string payload, Guid preferredOperationId)
    {
        if (preferredOperationId == Guid.Empty)
        {
            throw new ArgumentException("A preferred operation id must be non-empty.", nameof(preferredOperationId));
        }

        throw new NotSupportedException("This operation intent store does not support preferred operation ids.");
    }

    void Complete(string operationKey, Guid operationId);
}

public sealed record ClientOperationIntent(string OperationKey, Guid OperationId);

/// <summary>Runs durable local intent-store I/O without blocking the calling UI thread.</summary>
public static class ClientOperationIntentStoreAsyncExtensions
{
    public static Task<IReadOnlyList<ClientOperationIntent>> FindPendingByPrefixAsync(
        this IClientOperationIntentStore store,
        string operationKeyPrefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Task.Run(() => store.FindPendingByPrefix(operationKeyPrefix), cancellationToken);
    }

    public static Task<Guid> GetOrCreateAsync(
        this IClientOperationIntentStore store,
        string operationKey,
        string payload,
        Guid preferredOperationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Task.Run(
            () => store.GetOrCreate(operationKey, payload, preferredOperationId),
            cancellationToken);
    }

    public static Task CompleteAsync(
        this IClientOperationIntentStore store,
        string operationKey,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Task.Run(() => store.Complete(operationKey, operationId), cancellationToken);
    }
}

/// <summary>
/// Stores operation IDs and payload hashes only. The unhashed payload can contain business data and
/// therefore is never written to the local intent file.
/// </summary>
public sealed class FileClientOperationIntentStore : IClientOperationIntentStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _mutexName;

    public FileClientOperationIntentStore(string? path = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        _path = Path.GetFullPath(path ?? Path.Combine(localAppData, "EdgeRetails", "Desktop", "operation-intents.json"));
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
        _mutexName = $"Local\\EdgeRetails.OperationIntents.{pathHash}";
    }

    public Guid GetOrCreate(string operationKey, string payload) =>
        GetOrCreateCore(operationKey, payload, null);

    public IReadOnlyList<ClientOperationIntent> FindPendingByPrefix(string operationKeyPrefix)
    {
        if (string.IsNullOrWhiteSpace(operationKeyPrefix))
        {
            throw new ArgumentException("A non-empty operation key prefix is required.", nameof(operationKeyPrefix));
        }

        return WithExclusiveAccess(document =>
            (document.Entries
                .Where(entry => entry.OperationKey.StartsWith(operationKeyPrefix, StringComparison.Ordinal))
                .Select(entry => new ClientOperationIntent(entry.OperationKey, entry.OperationId))
                .ToArray(), false));
    }

    public Guid GetOrCreate(string operationKey, string payload, Guid preferredOperationId) =>
        GetOrCreateCore(operationKey, payload, preferredOperationId);

    private Guid GetOrCreateCore(string operationKey, string payload, Guid? preferredOperationId)
    {
        ValidateKey(operationKey);
        ArgumentNullException.ThrowIfNull(payload);
        if (preferredOperationId == Guid.Empty)
        {
            throw new ArgumentException("A preferred operation id must be non-empty.", nameof(preferredOperationId));
        }

        var payloadHash = Hash(payload);

        return WithExclusiveAccess(document =>
        {
            var existing = document.Entries.SingleOrDefault(x => string.Equals(x.OperationKey, operationKey, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!string.Equals(existing.PayloadSha256, payloadHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "An earlier operation with this identity has an unresolved outcome and a different payload.");
                }

                if (preferredOperationId.HasValue && existing.OperationId != preferredOperationId.Value)
                {
                    throw new InvalidOperationException("The operation identity changed before its outcome was resolved.");
                }

                return (existing.OperationId, false);
            }

            var entry = new OperationIntent(operationKey, payloadHash, preferredOperationId ?? Guid.CreateVersion7());
            document.Entries.Add(entry);
            return (entry.OperationId, true);
        });
    }

    public void Complete(string operationKey, Guid operationId)
    {
        ValidateKey(operationKey);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty operation id is required.", nameof(operationId));
        }

        _ = WithExclusiveAccess(document =>
        {
            var existing = document.Entries.SingleOrDefault(x => string.Equals(x.OperationKey, operationKey, StringComparison.Ordinal));
            if (existing is null)
            {
                return (false, false);
            }

            if (existing.OperationId != operationId)
            {
                throw new InvalidOperationException("The operation identity changed before its intent was completed.");
            }

            document.Entries.Remove(existing);
            return (true, true);
        });
    }

    private T WithExclusiveAccess<T>(Func<IntentDocument, (T Result, bool Changed)> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException("Timed out waiting to update the desktop operation intent store.");
            }

            var document = ReadDocument();
            var (result, changed) = action(document);
            if (changed)
            {
                WriteDocument(document);
            }

            return result;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private IntentDocument ReadDocument()
    {
        if (!File.Exists(_path))
        {
            return new IntentDocument();
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<IntentDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("The desktop operation intent store is empty.");
            if (document.Version != CurrentVersion || document.Entries is null ||
                document.Entries.Any(x => x is null || string.IsNullOrWhiteSpace(x.OperationKey) ||
                    x.OperationId == Guid.Empty || !IsSha256(x.PayloadSha256)) ||
                document.Entries.Select(x => x.OperationKey).Distinct(StringComparer.Ordinal).Count() != document.Entries.Count)
            {
                throw new InvalidDataException("The desktop operation intent store has an unsupported or invalid format.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The desktop operation intent store is corrupt; unresolved operation identities were preserved.", ex);
        }
    }

    private void WriteDocument(IntentDocument document)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The operation intent path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string Hash(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateKey(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            throw new ArgumentException("A non-empty operation key is required.", nameof(operationKey));
        }
    }

    private sealed class IntentDocument
    {
        public IntentDocument()
        {
        }

        public int Version { get; set; } = CurrentVersion;
        public List<OperationIntent> Entries { get; set; } = [];
    }

    private sealed class OperationIntent
    {
        public OperationIntent()
        {
        }

        public OperationIntent(string operationKey, string payloadSha256, Guid operationId)
        {
            OperationKey = operationKey;
            PayloadSha256 = payloadSha256;
            OperationId = operationId;
        }

        public string OperationKey { get; set; } = string.Empty;
        public string PayloadSha256 { get; set; } = string.Empty;
        public Guid OperationId { get; set; }
    }
}
