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

    Guid GetOrCreate(
        string operationKey,
        string payload,
        Guid preferredOperationId,
        string? operationType,
        string? safeDisplayContext = null,
        string? safeReplayEnvelope = null)
    {
        return GetOrCreate(operationKey, payload, preferredOperationId);
    }

    void Complete(string operationKey, Guid operationId);
}

public sealed record ClientOperationIntent(
    string OperationKey,
    Guid OperationId,
    string? OperationType = null,
    string? PayloadSha256 = null,
    DateTimeOffset? StartedAtUtc = null,
    string? Status = null,
    string? SafeDisplayContext = null,
    string? SafeReplayEnvelope = null,
    string? FingerprintVersion = "v1");

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

    public static Task<Guid> GetOrCreateAsync(
        this IClientOperationIntentStore store,
        string operationKey,
        string payload,
        Guid preferredOperationId,
        string? operationType,
        string? safeDisplayContext = null,
        string? safeReplayEnvelope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return Task.Run(
            () => store.GetOrCreate(operationKey, payload, preferredOperationId, operationType, safeDisplayContext, safeReplayEnvelope),
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
/// Stores operation IDs, hashes, and safe recovery context (Journal Schema v2).
/// Raw payload and credentials (PIN, passwords, tokens) are never persisted to disk.
/// </summary>
public sealed class FileClientOperationIntentStore : IClientOperationIntentStore
{
    public const int CurrentVersion = 2;
    public const string DefaultFolderName = "EdgeRetails";
    public const string DefaultFileName = "operation-intents.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _mutexName;

    public string IntentStorePath => _path;

    private static readonly string[] ProhibitedSecretKeywords =
    [
        "pin",
        "supervisorpin",
        "password",
        "privatekey",
        "secret",
        "bearer"
    ];

    public FileClientOperationIntentStore(string? path = null)
    {
        _path = ResolveCanonicalPath(path);
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())));
        _mutexName = $"Local\\EdgeRetails.OperationIntents.{pathHash}";
    }

    public static string ResolveCanonicalPath(
        string? path = null,
        string? commonAppDataOverride = null,
        string? localAppDataOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(path);
        }

        string canonicalCommonPath;
        var configuredStateDir = Environment.GetEnvironmentVariable("EDGE_RETAILS_STATE_DIR")
            ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(configuredStateDir) && string.IsNullOrWhiteSpace(commonAppDataOverride))
        {
            canonicalCommonPath = Path.GetFullPath(Path.Combine(configuredStateDir, "Desktop", DefaultFileName));
        }
        else
        {
            var commonAppData = commonAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(commonAppData))
            {
                throw new InvalidOperationException(
                    "Canonical machine-root directory (CommonApplicationData) is unavailable; per-user LocalApplicationData authority is prohibited.");
            }
            canonicalCommonPath = Path.GetFullPath(Path.Combine(commonAppData, DefaultFolderName, "Desktop", DefaultFileName));
        }

        var localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var canonicalDir = Path.GetDirectoryName(canonicalCommonPath);

        // Backward compatibility and machine root migration:
        // If canonical path does not exist yet and legacy LocalAppData journal exists:
        if (!File.Exists(canonicalCommonPath) && !string.IsNullOrWhiteSpace(localAppData))
        {
            var legacyLocalPath = Path.GetFullPath(Path.Combine(localAppData, DefaultFolderName, "Desktop", DefaultFileName));
            if (File.Exists(legacyLocalPath))
            {
                var hasUnresolved = HasUnresolvedIntents(legacyLocalPath);
                if (hasUnresolved)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(canonicalDir))
                        {
                            Directory.CreateDirectory(canonicalDir);
                        }
                        File.Copy(legacyLocalPath, canonicalCommonPath, overwrite: false);
                        try
                        {
                            File.Move(legacyLocalPath, $"{legacyLocalPath}.migrated", overwrite: true);
                        }
                        catch
                        {
                            // Ignore rename failure in migration cleanup
                        }
                        return canonicalCommonPath;
                    }
                    catch (Exception ex)
                    {
                        // Fail closed: canonical machine-root storage cannot be established.
                        // Legacy file bytes and IDs are preserved untouched at legacyLocalPath.
                        // Silently continuing with per-user authority is strictly prohibited.
                        throw new InvalidOperationException(
                            $"Canonical machine-root directory '{canonicalDir}' cannot be established; legacy operation intents are preserved at '{legacyLocalPath}' but per-user authority is prohibited.", ex);
                    }
                }
            }
        }

        // Verify that canonical directory can be created if canonical path does not exist and an explicit state root or override was provided.
        if (!File.Exists(canonicalCommonPath) && !string.IsNullOrWhiteSpace(canonicalDir) &&
            (!string.IsNullOrWhiteSpace(commonAppDataOverride) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDGE_RETAILS_STATE_DIR"))))
        {
            try
            {
                Directory.CreateDirectory(canonicalDir);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Canonical machine-root directory '{canonicalDir}' cannot be established; per-user authority is prohibited.", ex);
            }
        }

        return canonicalCommonPath;
    }

    private static bool HasUnresolvedIntents(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("Entries", out var entriesProp) &&
                entriesProp.ValueKind == JsonValueKind.Array &&
                entriesProp.GetArrayLength() > 0)
            {
                return true;
            }
            if (doc.RootElement.TryGetProperty("entries", out var lowerEntries) &&
                lowerEntries.ValueKind == JsonValueKind.Array &&
                lowerEntries.GetArrayLength() > 0)
            {
                return true;
            }
            return false;
        }
        catch
        {
            return new FileInfo(filePath).Length > 0;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ProhibitedSecretRegex = new(
        @"(?i)(?:""(?:pin|supervisorpin|password|privatekey|secret|bearer|token|apikey|accesstoken)""\s*:|(?:\b(?:pin|supervisorpin|password|privatekey|secret|bearer|token|apikey|accesstoken)\s*=)|\bbearer\s+[a-z0-9\-_\.]+|\b(?:password|privatekey|secret|accesstoken|apikey)\b|app_secret_[a-z0-9]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static void AssertNoSecrets(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var match = ProhibitedSecretRegex.Match(text);
        if (match.Success)
        {
            throw new InvalidOperationException(
                $"Journal context contains prohibited sensitive credentials or secret identifier ('{match.Value.Trim()}').");
        }
    }

    public Guid GetOrCreate(string operationKey, string payload) =>
        GetOrCreateCore(operationKey, payload, null, null, null, null);

    public IReadOnlyList<ClientOperationIntent> FindPendingByPrefix(string operationKeyPrefix)
    {
        if (string.IsNullOrWhiteSpace(operationKeyPrefix))
        {
            throw new ArgumentException("A non-empty operation key prefix is required.", nameof(operationKeyPrefix));
        }

        return WithExclusiveAccess(document =>
            (document.Entries
                .Where(entry => entry.OperationKey.StartsWith(operationKeyPrefix, StringComparison.Ordinal))
                .Select(entry => new ClientOperationIntent(
                    entry.OperationKey,
                    entry.OperationId,
                    entry.OperationType,
                    entry.PayloadSha256,
                    entry.StartedAtUtc,
                    entry.Status,
                    entry.SafeDisplayContext,
                    entry.SafeReplayEnvelope,
                    entry.FingerprintVersion))
                .ToArray(), false));
    }

    public Guid GetOrCreate(string operationKey, string payload, Guid preferredOperationId) =>
        GetOrCreateCore(operationKey, payload, preferredOperationId, null, null, null);

    public Guid GetOrCreate(
        string operationKey,
        string payload,
        Guid preferredOperationId,
        string? operationType,
        string? safeDisplayContext = null,
        string? safeReplayEnvelope = null) =>
        GetOrCreateCore(operationKey, payload, preferredOperationId, operationType, safeDisplayContext, safeReplayEnvelope);

    private Guid GetOrCreateCore(
        string operationKey,
        string payload,
        Guid? preferredOperationId,
        string? operationType,
        string? safeDisplayContext,
        string? safeReplayEnvelope)
    {
        ValidateKey(operationKey);
        ArgumentNullException.ThrowIfNull(payload);
        if (preferredOperationId == Guid.Empty)
        {
            throw new ArgumentException("A preferred operation id must be non-empty.", nameof(preferredOperationId));
        }

        AssertNoSecrets(safeDisplayContext);
        AssertNoSecrets(safeReplayEnvelope);

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

            var entry = new OperationIntent(
                operationKey,
                payloadHash,
                preferredOperationId ?? Guid.CreateVersion7(),
                operationType,
                "v1",
                DateTimeOffset.UtcNow,
                "Pending",
                safeDisplayContext,
                safeReplayEnvelope);

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
            if ((document.Version != 1 && document.Version != CurrentVersion) || document.Entries is null ||
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
            document.Version = CurrentVersion;
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
        public int Version { get; set; } = CurrentVersion;
        public List<OperationIntent> Entries { get; set; } = [];
    }

    public sealed class OperationIntent
    {
        public OperationIntent()
        {
        }

        public OperationIntent(
            string operationKey,
            string payloadSha256,
            Guid operationId,
            string? operationType = null,
            string? fingerprintVersion = "v1",
            DateTimeOffset? startedAtUtc = null,
            string? status = "Pending",
            string? safeDisplayContext = null,
            string? safeReplayEnvelope = null)
        {
            OperationKey = operationKey;
            PayloadSha256 = payloadSha256;
            OperationId = operationId;
            OperationType = operationType;
            FingerprintVersion = fingerprintVersion ?? "v1";
            StartedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow;
            Status = status ?? "Pending";
            SafeDisplayContext = safeDisplayContext;
            SafeReplayEnvelope = safeReplayEnvelope;
        }

        public string OperationKey { get; set; } = string.Empty;
        public string PayloadSha256 { get; set; } = string.Empty;
        public Guid OperationId { get; set; }
        public string? OperationType { get; set; }
        public string? FingerprintVersion { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public string? Status { get; set; }
        public string? SafeDisplayContext { get; set; }
        public string? SafeReplayEnvelope { get; set; }
    }
}
