using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production;
using EdgeRetails.Domain.Common;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EdgeRetails.Infrastructure.Services;

public class ShopClock : IShopClock
{
    private readonly Func<TimeZoneInfo> _shopTimeZoneProvider;
    private readonly Func<DateTimeOffset>? _utcNowProvider;

    public ShopClock()
        : this(ResolveShopTimeZone(), null)
    {
    }

    public ShopClock(TimeZoneInfo shopTimeZone, Func<DateTimeOffset>? utcNowProvider = null)
        : this(() => shopTimeZone ?? throw new ArgumentNullException(nameof(shopTimeZone)), utcNowProvider)
    {
    }

    public ShopClock(Func<TimeZoneInfo> shopTimeZoneProvider, Func<DateTimeOffset>? utcNowProvider = null)
    {
        _shopTimeZoneProvider = shopTimeZoneProvider ?? throw new ArgumentNullException(nameof(shopTimeZoneProvider));
        _utcNowProvider = utcNowProvider;
    }

    public DateTimeOffset UtcNow => _utcNowProvider?.Invoke() ?? DateTimeOffset.UtcNow;

    public DateTimeOffset ShopNow => TimeZoneInfo.ConvertTime(UtcNow, ShopTimeZone);

    public TimeZoneInfo ShopTimeZone => _shopTimeZoneProvider();

    public DateOnly ShopDate => ComputeShopDate(UtcNow);

    public DateOnly ToShopDate(DateTimeOffset utcTimestamp) => ComputeShopDate(utcTimestamp);

    protected virtual DateOnly ComputeShopDate(DateTimeOffset utcTimestamp)
    {
        var local = TimeZoneInfo.ConvertTime(utcTimestamp, ShopTimeZone);
        return DateOnly.FromDateTime(local.DateTime);
    }

    public DateTimeOffset ToUtc(DateOnly shopDate, TimeOnly time)
    {
        var zone = ShopTimeZone;
        var localDateTime = shopDate.ToDateTime(time);
        var offset = zone.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, offset).ToUniversalTime();
    }

    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetShopDayUtcRange(DateOnly shopDate)
    {
        var startUtc = ToUtc(shopDate, TimeOnly.MinValue);
        var nextDay = shopDate.AddDays(1);
        var endUtc = ToUtc(nextDay, TimeOnly.MinValue);
        return (startUtc, endUtc);
    }

    public static TimeZoneInfo ResolveTimeZoneById(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new BusinessRuleException("time.shop_timezone_missing", "Shop time zone id is required.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            throw new BusinessRuleException(
                "time.shop_timezone_unresolvable",
                $"Authoritative shop timezone '{timeZoneId}' could not be resolved on this system.");
        }
        catch (InvalidTimeZoneException)
        {
            throw new BusinessRuleException(
                "time.shop_timezone_unresolvable",
                $"Authoritative shop timezone '{timeZoneId}' is invalid on this system.");
        }
    }

    public static TimeZoneInfo ResolveShopTimeZone(IEnumerable<string>? candidateZoneIds = null)
    {
        var candidates = candidateZoneIds ?? new[] { "Asia/Karachi", "Pakistan Standard Time" };
        foreach (var id in candidates)
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

        throw new BusinessRuleException(
            "time.shop_timezone_unresolvable",
            "Authoritative shop timezone 'Asia/Karachi' or 'Pakistan Standard Time' could not be resolved on this system. Silent fallback to UTC or host local time is forbidden.");
    }
}

public sealed class SystemClock : ShopClock
{
    public SystemClock() : base()
    {
    }

    public SystemClock(IShopTimeZoneCache shopTimeZoneCache)
        : base(() => shopTimeZoneCache.Current, null)
    {
    }

    public SystemClock(TimeZoneInfo shopTimeZone, Func<DateTimeOffset>? utcNowProvider = null)
        : base(shopTimeZone, utcNowProvider)
    {
    }
}

/// <summary>Phase 8 shop time authority: configured zone, business-day cutoff, and clock integrity.</summary>
public sealed class ShopTimeAuthority : ShopClock, IShopTimeAuthority
{
    private static readonly long MaxBackwardUtcTicks = TimeSpan.FromMinutes(5).Ticks;
    private readonly IShopTimeZoneCache _cache;
    private long _lastUtcTicks;
    private ShopClockIntegrity _integrity = new(true, null);

    public ShopTimeAuthority(IShopTimeZoneCache cache)
        : base(() => cache.Current, null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _utcNowProvider = ReadUtcNow;
    }

    private readonly Func<DateTimeOffset> _utcNowProvider;

    public new DateTimeOffset UtcNow => _utcNowProvider();

    private DateTimeOffset ReadUtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        var ticks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastUtcTicks);
        if (last > 0 && ticks < last - MaxBackwardUtcTicks)
        {
            _integrity = new ShopClockIntegrity(false, "time.clock_moved_backward");
        }
        else if (ticks >= last)
        {
            Interlocked.Exchange(ref _lastUtcTicks, ticks);
        }

        return now;
    }

    public ShopClockIntegrity Integrity => _integrity;

    public DateOnly GetCurrentShopDate() => ShopDate;

    protected override DateOnly ComputeShopDate(DateTimeOffset utcTimestamp)
    {
        var local = TimeZoneInfo.ConvertTime(utcTimestamp, ShopTimeZone);
        var calendar = DateOnly.FromDateTime(local.DateTime);
        var cutoff = _cache.BusinessDayCutoffLocalTime;
        if (cutoff is null || cutoff.Value == TimeOnly.MinValue)
        {
            return calendar;
        }

        var localTime = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return localTime < cutoff.Value ? calendar.AddDays(-1) : calendar;
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
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch
            {
            }
            _db.ChangeTracker.Clear();
            throw;
        }
    }
}

public sealed class PostgresOperationLock : IOperationLock, IResourceLock
{
    private readonly EdgeRetailsDbContext _db;
    private Guid? _lockTransactionId;
    private readonly HashSet<string> _heldKeys = new(StringComparer.Ordinal);

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
        var transaction = _db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Advisory locks require an active database transaction.");
        cancellationToken.ThrowIfCancellationRequested();
        if (_lockTransactionId != transaction.TransactionId)
        {
            _heldKeys.Clear();
            _lockTransactionId = transaction.TransactionId;
        }
        var scopedKey = $"{scope}:{key}";
        if (_heldKeys.Contains(scopedKey))
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes(scopedKey);
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
        _heldKeys.Add(scopedKey);
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

