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

            var payloadJson = JsonSerializer.Serialize(record, Options);
            string line;
            if (_keyProvider is not null)
            {
                var key = await _keyProvider.GetIntegrityKeyAsync(cancellationToken);
                using var hmac = new HMACSHA256(key);
                var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson)));
                line = $"{hash}:{payloadJson}{Environment.NewLine}";
            }
            else
            {
                line = payloadJson + Environment.NewLine;
            }

            await File.AppendAllTextAsync(_path, line, cancellationToken);
        }
        finally
        {
            _gate.Release();
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
