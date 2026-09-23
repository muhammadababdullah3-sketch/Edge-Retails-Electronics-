using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Backup;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests.Sprint8;

public sealed class PostgresRestoreCutoverLiveClosureTests
{
    [Fact]
    [Trait("ClosureGate", "DestructivePostgres")]
    public async Task DisposableDatabase_RealBackupRestoreCutover_PreservesOriginalAndActivatesRestoredCopy()
    {
        const string armName = "EDGE_RETAILS_SPRINT8_ALLOW_DESTRUCTIVE_CUTOVER_TEST";
        if (!string.Equals(Environment.GetEnvironmentVariable(armName), "YES_DISPOSABLE_ONLY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Live cutover gate is not armed. Set {armName}=YES_DISPOSABLE_ONLY only on the authorized disposable PostgreSQL test environment.");
        }

        var host = Require("EDGE_RETAILS_TEST_DB_HOST");
        var port = int.Parse(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB_PORT") ?? "5432");
        var runtimeUser = Require("EDGE_RETAILS_TEST_DB_USER");
        var runtimePassword = Require("EDGE_RETAILS_TEST_DB_PASSWORD");
        var maintenanceUser = Require("EDGE_RETAILS_TEST_DB_MAINT_USER");
        var maintenancePassword = Require("EDGE_RETAILS_TEST_DB_MAINT_PASSWORD");
        var maintenanceDb = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB_MAINT_DATABASE") ?? "postgres";
        var tools = Require("EDGE_RETAILS_PG_BIN");
        if (string.Equals(runtimeUser, maintenanceUser, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Disposable cutover gate requires distinct runtime and maintenance PostgreSQL identities.");
        }

        var database = "er_s8_cut_" + Guid.NewGuid().ToString("N")[..12];
        var marker = Guid.NewGuid().ToString("N");
        var temp = Path.Combine(Path.GetTempPath(), "EdgeRetailsSprint8Cutover", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var runner = new ProcessRunner();
        var pgDump = Tool(tools, "pg_dump.exe");
        var pgRestore = Tool(tools, "pg_restore.exe");
        var psql = Tool(tools, "psql.exe");
        var createdb = Tool(tools, "createdb.exe");
        var runtime = new PostgresConnectionDescriptor(host, port, database, runtimeUser, new SensitiveString(runtimePassword));
        var maintenance = new PostgresMaintenanceDescriptor(host, port, maintenanceUser, new SensitiveString(maintenancePassword), maintenanceDb);
        var sessions = new HmacRestoreSessionStore(Path.Combine(temp, "restore-journal"), new FixedJournalKeyProvider());
        var keyProvider = new FixedBackupKeyProvider();
        var engine = new PostgresBackupEngine(
            pgDump, pgRestore, psql, createdb,
            new AesGcmBackupProtector(keyProvider), sessions,
            new FixedMaintenanceProvider(maintenance),
            new FileProductionMaintenanceBarrier(Path.Combine(temp, "maintenance"), new FixedMaintenanceIntegrityKeyProvider()),
            new CanonicalRestoreStagingValidator(
                psql,
                new IRestoreStagingCompatibilityProbe[]
                {
                    new EdgeRetailsEfRestoreCompatibilityProbe(),
                    new EdgeRetailsBusinessRestoreCompatibilityProbe(psql)
                }),
            new HmacBackupManifestAuthenticator(keyProvider));

        RestoreSessionToken? token = null;
        RestoreSessionRecord? prepared = null;
        RestoreCutoverResult? cutover = null;
        try
        {
            await RunAsync(runner, createdb, new[] {
                "--no-password", "--host", host, "--port", port.ToString(), "--username", maintenanceUser,
                "--maintenance-db", maintenanceDb, "--owner", runtimeUser, database
            }, maintenancePassword);

            await MigrateCanonicalSchemaAsync(
                host,
                port,
                database,
                runtimeUser,
                runtimePassword);

            await RunAsync(runner, psql, RuntimeArgs(host, port, runtimeUser, database,
                $"CREATE TABLE sprint8_cutover_sentinel(value text NOT NULL); INSERT INTO sprint8_cutover_sentinel(value) VALUES ('{marker}');"), runtimePassword);

            var backup = await engine.CreateAsync(new BackupCreateRequest(
                runtime, temp, "phase4-disposable-cutover", null, new BackupRetentionPolicy(null, null), "phase4-cutover"));
            token = await engine.PrepareRestoreAsync(new RestorePrepareRequest(
                runtime, backup.FullPath, backup.FullPath + ".manifest.json", "phase4-cutover"));
            prepared = await sessions.GetAsync(token.RestoreId) ?? throw new InvalidOperationException("Restore session journal was not persisted.");

            cutover = await engine.CutoverAsync(runtime, token);
            Assert.Equal(database, cutover.ActiveDatabase);
            Assert.NotEqual(database, cutover.PreservedPreRestoreDatabase);

            var activeValue = await QueryAsync(runner, psql, host, port, runtimeUser, runtimePassword, database,
                "SELECT value FROM sprint8_cutover_sentinel LIMIT 1;");
            var preservedValue = await QueryAsync(runner, psql, host, port, runtimeUser, runtimePassword, cutover.PreservedPreRestoreDatabase,
                "SELECT value FROM sprint8_cutover_sentinel LIMIT 1;");
            Assert.Equal(marker, activeValue);
            Assert.Equal(marker, preservedValue);
        }
        finally
        {
            if (token is not null && cutover is null)
            {
                try { await engine.DiscardPreparedRestoreAsync(runtime, token); } catch { }
            }

            var cleanup = new HashSet<string>(StringComparer.Ordinal) { database };
            if (prepared is not null)
            {
                cleanup.Add(prepared.StagingDatabase);
                cleanup.Add(PostgresRecoveryDatabaseName.Create("pre", database, prepared.RestoreId));
                cleanup.Add(PostgresRecoveryDatabaseName.Create("bad", database, prepared.RestoreId));
            }
            if (cutover is not null)
            {
                cleanup.Add(cutover.PreservedPreRestoreDatabase);
            }

            foreach (var db in cleanup)
            {
                if (!IsOwnedDisposableDatabaseName(db, database, prepared?.RestoreId))
                {
                    continue;
                }

                try
                {
                    await RunAsync(runner, psql, MaintenanceArgs(host, port, maintenanceUser, maintenanceDb,
                        $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='{SqlLiteral(db)}' AND pid<>pg_backend_pid();"), maintenancePassword);
                    await RunAsync(runner, psql, MaintenanceArgs(host, port, maintenanceUser, maintenanceDb,
                        $"DROP DATABASE IF EXISTS {SqlIdentifier(db)};"), maintenancePassword);
                }
                catch { }
            }
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static bool IsOwnedDisposableDatabaseName(string candidate, string target, Guid? restoreId)
    {
        if (string.Equals(candidate, target, StringComparison.Ordinal))
        {
            return candidate.StartsWith("er_s8_cut_", StringComparison.Ordinal);
        }

        if (restoreId is null)
        {
            return false;
        }

        return string.Equals(candidate, PostgresRecoveryDatabaseName.Create("rst", target, restoreId.Value), StringComparison.Ordinal)
            || string.Equals(candidate, PostgresRecoveryDatabaseName.Create("pre", target, restoreId.Value), StringComparison.Ordinal)
            || string.Equals(candidate, PostgresRecoveryDatabaseName.Create("bad", target, restoreId.Value), StringComparison.Ordinal);
    }

    private static string Tool(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required PostgreSQL tool was not found: {path}", path);
        }

        return path;
    }

    private static async Task MigrateCanonicalSchemaAsync(
        string host,
        int port,
        string database,
        string user,
        string password)
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = user,
            Password = password,
            Pooling = false,
            Timeout = 15,
            CommandTimeout = 30
        };

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(
                connection.ConnectionString,
                npgsql => npgsql
                    .MigrationsAssembly(typeof(EdgeRetailsDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ef_migrations_history", "system"))
            .Options;

        await using var db = new EdgeRetailsDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static async Task<string> QueryAsync(ProcessRunner runner, string psql, string host, int port, string user, string password, string db, string sql)
    {
        var result = await RunAsync(runner, psql, RuntimeArgs(host, port, user, db, sql), password);
        return result.StandardOutput.Trim();
    }

    private static string[] RuntimeArgs(string host, int port, string user, string db, string sql) => new[] {
        "--no-password", "--tuples-only", "--no-align", "--quiet", "--host", host, "--port", port.ToString(),
        "--username", user, "--dbname", db, "--command", sql
    };

    private static string[] MaintenanceArgs(string host, int port, string user, string maintenanceDb, string sql)
        => RuntimeArgs(host, port, user, maintenanceDb, sql);

    private static async Task<ProcessResult> RunAsync(ProcessRunner runner, string tool, IEnumerable<string> args, string password)
    {
        var result = await runner.RunAsync(tool, args, new Dictionary<string, string?> { ["PGPASSWORD"] = password }, CancellationToken.None);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(tool)} failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return result;
    }

    private static string Require(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required live-closure environment variable '{name}' is missing.");

    private static string SqlLiteral(string value) => value.Replace("'", "''");
    private static string SqlIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private sealed class FixedBackupKeyProvider : IBackupEncryptionKeyProvider
    {
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    }

    private sealed class FixedJournalKeyProvider : IRestoreJournalIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());
    }

    private sealed class FixedMaintenanceIntegrityKeyProvider : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(65, 32).Select(i => (byte)i).ToArray());
    }

    private sealed class FixedMaintenanceProvider : IPostgresMaintenanceConnectionProvider
    {
        private readonly PostgresMaintenanceDescriptor _connection;
        public FixedMaintenanceProvider(PostgresMaintenanceDescriptor connection) => _connection = connection;
        public Task<PostgresMaintenanceDescriptor> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_connection);
    }

    private sealed class IntegrationStagingValidator : IRestoreStagingValidator
    {
        public Task ValidateAsync(RestoreStagingValidationContext context, CancellationToken cancellationToken = default)
        {
            Assert.NotEqual(context.RuntimeConnection.Database, context.StagingDatabase);
            Assert.Equal(context.RuntimeConnection.Database, context.Manifest.DatabaseName);
            return Task.CompletedTask;
        }
    }
}
