using System.Text.Json;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class JsonPrinterProfileStore : IPrinterProfileStore
{
    private const long MaximumStoreBytes = 1024 * 1024;
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPrinterProfileStore(string path)
        => _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));

    public async Task<PrinterProfile?> GetAsync(string profileName, CancellationToken cancellationToken = default)
    {
        var all = await ReadAllAsync(cancellationToken);
        return all.FirstOrDefault(x => string.Equals(x.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = (await ReadAllUnsafeAsync(cancellationToken)).ToList();
            all.RemoveAll(x => string.Equals(x.ProfileName, profile.ProfileName, StringComparison.OrdinalIgnoreCase));
            all.Add(profile);
            await WriteAllUnsafeAsync(all, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<PrinterProfile>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadAllUnsafeAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<PrinterProfile>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<PrinterProfile>();
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
                throw new InvalidDataException("Printer profile store size is invalid.");
            }

            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken);
            return JsonSerializer.Deserialize<List<PrinterProfile>>(json, JsonOptions)
                ?? throw new InvalidDataException("Printer profile store payload is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Printer profile store JSON is invalid.", ex);
        }
    }

    private async Task WriteAllUnsafeAsync(IReadOnlyList<PrinterProfile> profiles, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(profiles.OrderBy(x => x.ProfileName), JsonOptions);
        if (body.LongLength > MaximumStoreBytes)
        {
            throw new InvalidDataException("Printer profile store exceeded its maximum size.");
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
                await stream.WriteAsync(body, cancellationToken);
                await stream.FlushAsync(cancellationToken);
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
