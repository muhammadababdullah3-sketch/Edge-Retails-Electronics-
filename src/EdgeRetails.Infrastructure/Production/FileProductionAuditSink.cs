using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production;

namespace EdgeRetails.Infrastructure.Production;

public sealed class FileProductionAuditSink : IProductionAuditSink
{
    private readonly string _path;
    private readonly IProductionMaintenanceIntegrityKeyProvider? _keyProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public FileProductionAuditSink(string path, IProductionMaintenanceIntegrityKeyProvider? keyProvider = null)
    {
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        _keyProvider = keyProvider;
    }

    public async Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // The audit file itself is the shared lease: separate sink instances
            // and processes cannot race the read/check/append receipt operation.
            await using var stream = await AcquireFileAsync(cancellationToken);
            byte[]? key = null;
            try
            {
                if (_keyProvider is not null)
                {
                    key = await _keyProvider.GetIntegrityKeyAsync(cancellationToken);
                    if (key.Length < 32) { throw new InvalidOperationException("Audit integrity key is invalid."); }
                }
                if (IsLabelReceipt(record))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
                    var replay = false;
                    while (await reader.ReadLineAsync(cancellationToken) is { } existingLine)
                    {
                        var existing = ReadVerified(existingLine, key);
                        if (!IsLabelReceipt(existing) || existing.EntityType != record.EntityType ||
                            existing.EntityId != record.EntityId || existing.CorrelationId != record.CorrelationId) { continue; }
                        if (existing.EventType != record.EventType || existing.Detail != record.Detail)
                        {
                            throw new InvalidOperationException("A label print attempt cannot change its reported outcome.");
                        }
                        replay = true;
                    }
                    if (replay) { return; }
                }
                var payloadJson = JsonSerializer.Serialize(record, Options);
                var line = key is null ? payloadJson :
                    $"{Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payloadJson)))}:{payloadJson}";
                stream.Seek(0, SeekOrigin.End);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + Environment.NewLine), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                if (key is not null) { CryptographicOperations.ZeroMemory(key); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsLabelReceipt(ProductionAuditRecord record) =>
        record.EntityType is "PhysicalItemSticker" or "ProductUnitLabel" &&
        record.EventType is ProductionAuditEvents.DocumentPrinted or ProductionAuditEvents.DocumentReprinted or ProductionAuditEvents.DocumentPrintFailed &&
        !string.IsNullOrWhiteSpace(record.CorrelationId);

    private static ProductionAuditRecord ReadVerified(string line, byte[]? key)
    {
        try
        {
            var payload = line;
            if (key is not null)
            {
                if (line.Length < 66 || line[64] != ':') { throw new InvalidOperationException("Audit history authentication is missing."); }
                payload = line[65..];
                var supplied = Convert.FromHexString(line[..64]);
                var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
                if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                {
                    throw new InvalidOperationException("Audit history authentication failed.");
                }
            }
            return JsonSerializer.Deserialize<ProductionAuditRecord>(payload, Options)
                ?? throw new InvalidOperationException("Audit history contains an invalid record.");
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new InvalidOperationException("Audit history is invalid.", error);
        }
    }

    private async Task<FileStream> AcquireFileAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }
}

public sealed class LoggingProductionAuditFailureReporter : IProductionAuditFailureReporter
{
    public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default)
    {
        try
        {
            Console.Error.WriteLine($"[AUDIT FAILURE] Event: {record.EventType}, Error: {error.Message}");
        }
        catch
        {
        }

        return Task.CompletedTask;
    }
}
