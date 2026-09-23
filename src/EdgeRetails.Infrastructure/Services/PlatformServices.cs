using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EdgeRetails.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    private readonly TimeZoneInfo _shopTimeZone;

    public SystemClock()
    {
        _shopTimeZone = ResolveShopTimeZone();
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly ShopDate
    {
        get
        {
            var local = TimeZoneInfo.ConvertTime(UtcNow, _shopTimeZone);
            return DateOnly.FromDateTime(local.DateTime);
        }
    }

    private static TimeZoneInfo ResolveShopTimeZone()
    {
        foreach (var id in new[] { "Asia/Karachi", "Pakistan Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}

public sealed class UuidV7IdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}

public sealed class EfTransactionRunner : ITransactionRunner
{
    private readonly EdgeRetailsDbContext _db;
    private readonly ProductionMaintenanceWriteGuard _maintenanceWriteGuard;

    public EfTransactionRunner(
        EdgeRetailsDbContext db,
        ProductionMaintenanceWriteGuard maintenanceWriteGuard)
    {
        _db = db;
        _maintenanceWriteGuard = maintenanceWriteGuard;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _maintenanceWriteGuard.EnsureBusinessWritesAllowedAsync(cancellationToken);

        if (_db.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            var result = await operation(cancellationToken);

            if (result is IResult applicationResult && !applicationResult.IsSuccess)
            {
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                return result;
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            throw;
        }
    }
}

public sealed class PostgresOperationLock : IOperationLock, IResourceLock
{
    private readonly EdgeRetailsDbContext _db;

    public PostgresOperationLock(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task AcquireAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        AcquireCoreAsync(
            "operation",
            clientOperationId,
            cancellationToken);

    public Task AcquireAsync(
        string resourceType,
        Guid resourceId,
        CancellationToken cancellationToken) =>
        AcquireAsync(
            resourceType,
            resourceId.ToString("D"),
            cancellationToken);

    public Task AcquireAsync(
        string resourceType,
        string resourceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        return AcquireCoreAsync(
            $"resource:{resourceType.Trim().ToLowerInvariant()}",
            resourceKey.Trim(),
            cancellationToken);
    }

    private Task AcquireCoreAsync(
        string scope,
        Guid id,
        CancellationToken cancellationToken) =>
        AcquireCoreAsync(
            scope,
            id.ToString("D"),
            cancellationToken);

    private async Task AcquireCoreAsync(
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Advisory locks require an active database transaction.");
        }

        var payload = Encoding.UTF8.GetBytes($"{scope}:{key}");
        var hash = SHA256.HashData(payload);
        var lockKey = BitConverter.ToInt64(hash, 0);

        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(@key);";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = lockKey;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class PostgresDocumentNumberService : IDocumentNumberService
{
    private readonly EdgeRetailsDbContext _db;

    public PostgresDocumentNumberService(EdgeRetailsDbContext db) => _db = db;

    public async Task<string> NextAsync(
        string series,
        CancellationToken cancellationToken)
    {
        var normalized = series.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(series),
                "Document series must contain between 1 and 20 characters.");
        }

        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO system.document_sequences (series, last_value)
                VALUES (@series, 1)
                ON CONFLICT (series)
                DO UPDATE SET last_value = document_sequences.last_value + 1
                RETURNING last_value;
                """;

            if (_db.Database.CurrentTransaction is not null)
            {
                command.Transaction = _db.Database.CurrentTransaction.GetDbTransaction();
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = "series";
            parameter.Value = normalized;
            command.Parameters.Add(parameter);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            var next = Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
            return $"{normalized}-{next:000000}";
        }
        finally
        {
            if (shouldClose)
            {
                await _db.Database.CloseConnectionAsync();
            }
        }
    }
}

