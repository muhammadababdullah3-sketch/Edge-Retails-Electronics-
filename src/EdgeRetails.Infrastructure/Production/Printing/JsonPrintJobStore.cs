using System.Text.Json;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Infrastructure.Production.Printing;

/// <summary>
/// Durable, atomic print-job state for standalone V1. The live merge may replace this adapter with
/// the canonical Infrastructure persistence implementation without changing Application semantics.
/// Desktop never owns this persistence.
/// </summary>
public sealed class JsonPrintJobStore : IPrintJobStore
{
    private const long MaximumStoreBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JsonPrintJobStore(string path)
        => _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));

    public async Task<PrintJobRecord?> GetAsync(string printJobId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadUnsafeAsync(cancellationToken)).FirstOrDefault(x => x.PrintJobId == printJobId); }
        finally { _gate.Release(); }
    }
    public async Task CreateAsync(PrintJobRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = (await ReadUnsafeAsync(cancellationToken)).ToList();
            if (all.Any(x => x.PrintJobId == record.PrintJobId))
            {
                throw new InvalidOperationException("Print job already exists.");
            }

            all.Add(record);
            await WriteUnsafeAsync(all, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryTransitionAsync(
        string printJobId,
        PrintJobState expectedState,
        PrintJobState newState,
        int attemptNumber,
        string? lastErrorCode,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = (await ReadUnsafeAsync(cancellationToken)).ToList();
            var index = all.FindIndex(x => x.PrintJobId == printJobId);
            if (index < 0 || all[index].State != expectedState)
            {
                return false;
            }

            all[index] = all[index] with
            {
                State = newState,
                AttemptNumber = attemptNumber,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastErrorCode = lastErrorCode
            };
            await WriteUnsafeAsync(all, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<PrintJobRecord>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<PrintJobRecord>();
        }
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumStoreBytes)
            {
                throw new InvalidDataException("Print job store size is invalid.");
            }

            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(ct);
            return JsonSerializer.Deserialize<List<PrintJobRecord>>(json, Options)
                ?? throw new InvalidDataException("Print job store payload is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Print job store JSON is invalid.", ex);
        }
    }

    private async Task WriteUnsafeAsync(IReadOnlyList<PrintJobRecord> records, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var body = JsonSerializer.SerializeToUtf8Bytes(records.OrderBy(x => x.CreatedAtUtc), Options);
        if (body.LongLength > MaximumStoreBytes)
        {
            throw new InvalidDataException("Print job store exceeded its maximum size.");
        }

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(body, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
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
        }
    }
}
