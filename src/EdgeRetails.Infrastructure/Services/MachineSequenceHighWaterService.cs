using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class MachineSequenceHighWaterService : ISequenceHighWaterService
{
    private readonly string _storagePath;
    private readonly object _syncLock = new();
    private readonly byte[] _hmacKey;
    private readonly ISequenceAuthorityCustody _custody;
    private readonly Action<SequenceAuthorityWriteStage>? _writeObserver;
    private readonly ConcurrentDictionary<string, long> _dealerHighWater = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _supplierProductHighWater = new(StringComparer.OrdinalIgnoreCase);

    public MachineSequenceHighWaterService(string? customPath = null)
        : this(customPath, new WindowsSequenceAuthorityCustody()) { }

    internal MachineSequenceHighWaterService(string? customPath, ISequenceAuthorityCustody custody,
        Action<SequenceAuthorityWriteStage>? writeObserver = null)
    {
        _storagePath = Path.GetFullPath(customPath
            ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_HIGHWATER_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EdgeRetails",
                "security",
                "highwater.manifest"));

        _custody = custody;
        _writeObserver = writeObserver;
        _hmacKey = DeriveMachineKey();
        ReadUnderLease();
    }

    public long GetDealerPrefixHighWater(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) { throw new ArgumentException("Dealer prefix is required.", nameof(prefix)); }
        var normalized = prefix.Trim().ToUpperInvariant();
        lock (_syncLock)
        {
            ReadUnderLease();
            return _dealerHighWater.TryGetValue(normalized, out var val) ? val : 0;
        }
    }

    public void RecordDealerPrefixHighWater(string prefix, long sequence)
    {
        if (string.IsNullOrWhiteSpace(prefix)) { throw new ArgumentException("Dealer prefix is required.", nameof(prefix)); }
        var normalized = prefix.Trim().ToUpperInvariant();
        if (sequence < 0) { throw new ArgumentOutOfRangeException(nameof(sequence)); }
        lock (_syncLock)
        {
            RecordHighWater(normalized, sequence, dealer: true);
        }
    }

    public long GetSupplierProductHighWater(Guid supplierId, Guid productId)
    {
        var key = $"{supplierId:D}:{productId:D}";
        lock (_syncLock)
        {
            ReadUnderLease();
            return _supplierProductHighWater.TryGetValue(key, out var val) ? val : 0;
        }
    }

    public void RecordSupplierProductHighWater(Guid supplierId, Guid productId, long sequence)
    {
        var key = $"{supplierId:D}:{productId:D}";
        if (sequence < 0) { throw new ArgumentOutOfRangeException(nameof(sequence)); }
        lock (_syncLock)
        {
            RecordHighWater(key, sequence, dealer: false);
        }
    }

    public async Task ReconcileDatabaseHighWaterAsync(EdgeRetailsDbContext db, CancellationToken cancellationToken)
    {
        // 1. Reconcile SupplierCodeSequences
        var dbSequences = await db.SupplierCodeSequences.ToListAsync(cancellationToken);
        var dbPrefixMap = dbSequences.ToDictionary(x => x.Prefix.ToUpperInvariant());

        foreach (var (prefix, highWater) in _dealerHighWater)
        {
            if (dbPrefixMap.TryGetValue(prefix.ToUpperInvariant(), out var seq))
            {
                if (highWater > seq.NextValue)
                {
                    seq.NextValue = highWater;
                }
                else if (seq.NextValue > highWater)
                {
                    RecordDealerPrefixHighWater(prefix, seq.NextValue);
                }
            }
            else
            {
                db.SupplierCodeSequences.Add(new SupplierCodeSequence
                {
                    Prefix = prefix.ToUpperInvariant(),
                    NextValue = highWater
                });
            }
        }

        foreach (var seq in dbSequences)
        {
            var machineVal = GetDealerPrefixHighWater(seq.Prefix);
            if (seq.NextValue > machineVal)
            {
                RecordDealerPrefixHighWater(seq.Prefix, seq.NextValue);
            }
        }

        // 2. Reconcile SupplierProducts
        var sps = await db.SupplierProducts.ToListAsync(cancellationToken);
        foreach (var sp in sps)
        {
            var machineVal = GetSupplierProductHighWater(sp.SupplierId, sp.ProductId);
            if (machineVal > sp.NextItemSequence)
            {
                sp.NextItemSequence = machineVal;
                sp.Version++;
            }
            else if (sp.NextItemSequence > machineVal)
            {
                RecordSupplierProductHighWater(sp.SupplierId, sp.ProductId, sp.NextItemSequence);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void ReadUnderLease()
    {
        try
        {
            using var lease = AcquireWriteLease();
            Adopt(LoadValidatedState());
        }
        catch (Exception ex) when (AuthorityFailure(ex))
        {
            throw Unavailable(ex);
        }
    }

    private void RecordHighWater(string key, long sequence, bool dealer)
    {
        try
        {
            using var lease = AcquireWriteLease();
            var candidate = LoadValidatedState();
            var map = dealer ? candidate.DealerPrefixes : candidate.SupplierProducts;
            map[key] = Math.Max(map.GetValueOrDefault(key), sequence);
            SaveToDisk(candidate);
            Adopt(candidate);
        }
        catch (Exception ex) when (AuthorityFailure(ex))
        {
            throw Unavailable(ex);
        }
    }

    private HighWaterPayloadDto LoadValidatedState()
    {
        var manifest = ReadPayload(_storagePath);
        var checkpoint = ReadPayload(_storagePath + ".checkpoint");
        RequireAtLeast(manifest.DealerPrefixes, checkpoint.DealerPrefixes);
        RequireAtLeast(manifest.SupplierProducts, checkpoint.SupplierProducts);
        RequireAtLeast(checkpoint.DealerPrefixes, manifest.DealerPrefixes);
        RequireAtLeast(checkpoint.SupplierProducts, manifest.SupplierProducts);
        RequireAtLeast(manifest.DealerPrefixes, _dealerHighWater);
        RequireAtLeast(manifest.SupplierProducts, _supplierProductHighWater);
        return manifest;
    }

    private HighWaterPayloadDto ReadPayload(string path)
    {
        _custody.ValidateFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid sequence authority size.");
        }
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var json = reader.ReadToEnd();
        RejectDuplicateProperties(json);
        using var envelope = JsonDocument.Parse(json);
        if (envelope.RootElement.ValueKind != JsonValueKind.Object
            || !envelope.RootElement.TryGetProperty("Version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1)
        {
            throw new InvalidDataException("Sequence authority has no explicit supported version.");
        }
        var doc = JsonSerializer.Deserialize<HighWaterManifestDto>(json);
        if (doc is null || doc.Version != 1 || string.IsNullOrWhiteSpace(doc.PayloadJson)
            || doc.Signature is null || doc.Signature.Length != 64 || !doc.Signature.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Invalid sequence authority envelope.");
        }
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(doc.Signature), Convert.FromHexString(ComputeSignature(doc.PayloadJson))))
        {
            throw new InvalidDataException("Sequence authority authentication failed.");
        }
        RejectDuplicateProperties(doc.PayloadJson);
        using var structure = JsonDocument.Parse(doc.PayloadJson);
        if (structure.RootElement.ValueKind != JsonValueKind.Object
            || !structure.RootElement.TryGetProperty("DealerPrefixes", out var dealers) || dealers.ValueKind != JsonValueKind.Object
            || !structure.RootElement.TryGetProperty("SupplierProducts", out var pairs) || pairs.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Sequence authority maps are missing.");
        }
        var payload = JsonSerializer.Deserialize<HighWaterPayloadDto>(doc.PayloadJson);
        if (payload?.DealerPrefixes is null || payload.SupplierProducts is null
            || payload.DealerPrefixes.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Key != pair.Key.Trim().ToUpperInvariant() || pair.Value < 0)
            || payload.SupplierProducts.Any(pair => !ValidSupplierProductKey(pair.Key) || pair.Value < 0))
        {
            throw new InvalidDataException("Invalid sequence authority payload.");
        }
        return payload;
    }

    private static void RejectDuplicateProperties(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        Visit(parsed.RootElement);
        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) { throw new InvalidDataException("Sequence authority contains duplicate properties."); }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) { Visit(item); }
            }
        }
    }

    private static void RequireAtLeast(IReadOnlyDictionary<string, long> candidate, IReadOnlyDictionary<string, long> established)
    {
        foreach (var (key, value) in established)
        {
            if (!candidate.TryGetValue(key, out var actual) || actual < value)
            {
                throw new InvalidDataException("Sequence authority regressed or its checkpoint is inconsistent.");
            }
        }
    }

    private void Adopt(HighWaterPayloadDto payload)
    {
        // Validation of every map and durable artifact finishes before memory changes.
        foreach (var (key, value) in payload.DealerPrefixes) { _dealerHighWater[key] = value; }
        foreach (var (key, value) in payload.SupplierProducts) { _supplierProductHighWater[key] = value; }
    }

    private void SaveToDisk(HighWaterPayloadDto payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var manifest = new HighWaterManifestDto
        {
            Version = 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            PayloadJson = payloadJson,
            Signature = ComputeSignature(payloadJson)
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        if (bytes.Length > 4 * 1024 * 1024) { throw new InvalidDataException("Sequence authority exceeds its storage limit."); }
        // A torn two-file publication blocks further allocation. Neither file is
        // automatically reconstructed from the other after a crash or replay.
        WriteCheckpoint(bytes);
        Publish(_storagePath, bytes, SequenceAuthorityWriteStage.ManifestFlushed, SequenceAuthorityWriteStage.ManifestPublished);
    }

    private void WriteCheckpoint(byte[] bytes)
    {
        var checkpoint = _storagePath + ".checkpoint";
        _custody.ValidateFile(checkpoint);
        // Flush the established file itself before publishing the manifest. A
        // partial checkpoint blocks allocation; no rename durability is assumed.
        using (var output = new FileStream(checkpoint, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.SetLength(0);
            _writeObserver?.Invoke(SequenceAuthorityWriteStage.CheckpointWriteStarted);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            _writeObserver?.Invoke(SequenceAuthorityWriteStage.CheckpointFlushed);
        }
        _custody.ValidateFile(checkpoint);
        _writeObserver?.Invoke(SequenceAuthorityWriteStage.CheckpointPublished);
    }

    private void Publish(string target, byte[] bytes, SequenceAuthorityWriteStage flushed, SequenceAuthorityWriteStage published)
    {
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _custody.ValidateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            _custody.ValidateFile(temp);
            _writeObserver?.Invoke(flushed);
            File.Move(temp, target, overwrite: true);
            _custody.ValidateFile(target);
            _writeObserver?.Invoke(published);
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }
    }

    private FileStream AcquireWriteLease()
    {
        _custody.ValidateDirectory(Path.GetDirectoryName(_storagePath)!);
        _custody.ValidateFile(_storagePath + ".lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(_storagePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (attempt < 100 && (ex.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(25);
            }
        }
    }

    // Only friend test assemblies can initialize owned fixture state. Operational
    // initialization and legacy migration require separately approved custody/history.
    internal static void InitializeOwnedFixture(string storagePath, OwnedSequenceAuthorityCustody custody)
    {
        var full = Path.GetFullPath(storagePath);
        custody.ValidateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full) || File.Exists(full + ".checkpoint") || File.Exists(full + ".lock"))
        {
            throw new InvalidOperationException("Sequence fixture initialization requires three absent artifacts.");
        }
        using var lease = new FileStream(full + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        lease.Flush(flushToDisk: true);
        custody.ValidateFile(full + ".lock");
        var payloadJson = JsonSerializer.Serialize(new HighWaterPayloadDto());
        using var hmac = new HMACSHA256(DeriveMachineKey());
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new HighWaterManifestDto
        {
            Version = 1, UpdatedAtUtc = DateTimeOffset.UtcNow, PayloadJson = payloadJson,
            Signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson)))
        }));
        foreach (var target in new[] { full + ".checkpoint", full })
        {
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            custody.ValidateFile(target);
        }
    }

    private static bool ValidSupplierProductKey(string key)
    {
        var parts = key.Split(':');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "D", out var supplier)
            && Guid.TryParseExact(parts[1], "D", out var product) && key == $"{supplier:D}:{product:D}";
    }

    private static bool AuthorityFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or System.Security.SecurityException;
    private static InvalidOperationException Unavailable(Exception ex) => new("Persistent sequence authority is unavailable or invalid; allocation is stopped.", ex);

    private string ComputeSignature(string payloadJson)
    {
        using var hmac = new HMACSHA256(_hmacKey);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson)));
    }

    private static byte[] DeriveMachineKey()
    {
        var machineId = Environment.MachineName + ":EdgeRetails:HighWater:v1";
        return SHA256.HashData(Encoding.UTF8.GetBytes(machineId));
    }

    private sealed class HighWaterManifestDto
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public string? PayloadJson { get; set; }
        public string? Signature { get; set; }
    }

    private sealed class HighWaterPayloadDto
    {
        public Dictionary<string, long> DealerPrefixes { get; set; } = new();
        public Dictionary<string, long> SupplierProducts { get; set; } = new();
    }
}

internal enum SequenceAuthorityWriteStage { CheckpointWriteStarted, CheckpointFlushed, CheckpointPublished, ManifestFlushed, ManifestPublished }
