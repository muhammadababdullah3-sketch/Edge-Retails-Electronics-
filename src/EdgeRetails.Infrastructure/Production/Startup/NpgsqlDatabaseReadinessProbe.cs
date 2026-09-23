using System.Diagnostics;
using EdgeRetails.Application.Production.Startup;
using Npgsql;

namespace EdgeRetails.Infrastructure.Production.Startup;

public sealed class NpgsqlDatabaseReadinessProbe : IDatabaseReadinessProbe
{
    private readonly string _connectionString;
    private readonly string? _expectedDatabase;

    public NpgsqlDatabaseReadinessProbe(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        try { _expectedDatabase = new NpgsqlConnectionStringBuilder(connectionString).Database; }
        catch { _expectedDatabase = null; }
    }

    public async Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        var effectiveToken = timeoutCts.Token;

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(effectiveToken);
            await using var command = new NpgsqlCommand("SELECT current_database(), current_setting('server_version'), pg_is_in_recovery(), current_setting('fsync', true), current_setting('full_page_writes', true), current_setting('synchronous_commit', true);", connection);
            await using var reader = await command.ExecuteReaderAsync(effectiveToken);
            if (!await reader.ReadAsync(effectiveToken))
            {
                return Failure(DatabaseReadinessCode.ProbeFailed, "PostgreSQL readiness probe returned no result.", stopwatch.Elapsed);
            }

            var database = reader.GetString(0);
            var serverVersion = reader.GetString(1);
            var isInRecovery = reader.GetBoolean(2);
            var fsync = reader.IsDBNull(3) ? null : reader.GetString(3);
            var fullPageWrites = reader.IsDBNull(4) ? null : reader.GetString(4);
            var syncCommit = reader.IsDBNull(5) ? null : reader.GetString(5);

            if (isInRecovery)
            {
                return Failure(DatabaseReadinessCode.ProbeFailed, "PostgreSQL is in recovery mode and not writable.", stopwatch.Elapsed);
            }

            if (string.Equals(fsync, "off", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(DatabaseReadinessCode.ProbeFailed, "PostgreSQL durability setting breached: fsync is 'off'.", stopwatch.Elapsed);
            }

            if (string.Equals(fullPageWrites, "off", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(DatabaseReadinessCode.ProbeFailed, "PostgreSQL durability setting breached: full_page_writes is 'off'.", stopwatch.Elapsed);
            }

            if (string.Equals(syncCommit, "off", StringComparison.OrdinalIgnoreCase))
            {
                return Failure(DatabaseReadinessCode.ProbeFailed, "PostgreSQL durability setting breached: synchronous_commit is 'off'.", stopwatch.Elapsed);
            }

            if (!string.IsNullOrWhiteSpace(_expectedDatabase) && !string.Equals(database, _expectedDatabase, StringComparison.Ordinal))
            {
                return new DatabaseReadinessResult(false, "PostgreSQL connected to an unexpected database identity.")
                {
                    Code = DatabaseReadinessCode.UnexpectedDatabase,
                    DatabaseName = database,
                    PostgreSqlVersion = serverVersion,
                    Latency = stopwatch.Elapsed
                };
            }

            return new DatabaseReadinessResult(true, "PostgreSQL is ready.")
            {
                Code = DatabaseReadinessCode.Ready,
                DatabaseName = database,
                PostgreSqlVersion = serverVersion,
                Latency = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(DatabaseReadinessCode.Unavailable, "PostgreSQL readiness probe timed out after 3 seconds.", stopwatch.Elapsed);
        }
        catch (NpgsqlException)
        {
            return Failure(DatabaseReadinessCode.Unavailable, "PostgreSQL is unavailable or rejected the configured runtime connection.", stopwatch.Elapsed);
        }
        catch
        {
            return Failure(DatabaseReadinessCode.ProbeFailed, "PostgreSQL readiness could not be verified safely.", stopwatch.Elapsed);
        }
    }

    private static DatabaseReadinessResult Failure(DatabaseReadinessCode code, string message, TimeSpan latency)
        => new(false, message) { Code = code, Latency = latency };
}
