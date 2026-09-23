using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;
using Xunit;

namespace EdgeRetails.IntegrationTests.Sprint8;

public sealed class PostgresBackupRestoreIntegrationTests
{
    [Fact]
    public async Task RealPostgres_BackupManifestChecksumAndProtectedStagingRestore_Verify()
    {
        var host = Require("EDGE_RETAILS_TEST_DB_HOST");
        var port = int.Parse(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB_PORT") ?? "5432");
        var database = Require("EDGE_RETAILS_TEST_DB_NAME");
        var runtimeUser = Require("EDGE_RETAILS_TEST_DB_USER");
        var runtimePassword = Require("EDGE_RETAILS_TEST_DB_PASSWORD");
        var maintenanceUser = Require("EDGE_RETAILS_TEST_DB_MAINT_USER");
        var maintenancePassword = Require("EDGE_RETAILS_TEST_DB_MAINT_PASSWORD");
        var tools = Require("EDGE_RETAILS_PG_BIN");
        var temp = Path.Combine(Path.GetTempPath(), "EdgeRetailsSprint8", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        var runtime = new PostgresConnectionDescriptor(host, port, database, runtimeUser, new SensitiveString(runtimePassword));
        var maintenance = new PostgresMaintenanceDescriptor(host, port, maintenanceUser, new SensitiveString(maintenancePassword));
        var keyProvider = new FixedBackupKeyProvider();
        var protector = new AesGcmBackupProtector(keyProvider);
        var manifestAuthenticator = new HmacBackupManifestAuthenticator(keyProvider);
        var sessions = new HmacRestoreSessionStore(Path.Combine(temp, "restore-journal"), new FixedJournalKeyProvider());
        var barrier = new FileProductionMaintenanceBarrier(Path.Combine(temp, "maintenance"), new FixedMaintenanceIntegrityKeyProvider());
        var engine = new PostgresBackupEngine(
            Path.Combine(tools, "pg_dump.exe"), Path.Combine(tools, "pg_restore.exe"),
            Path.Combine(tools, "psql.exe"), Path.Combine(tools, "createdb.exe"),
            protector, sessions, new FixedMaintenanceProvider(maintenance), barrier,
            new CanonicalRestoreStagingValidator(
                Path.Combine(tools, "psql.exe"),
                new IRestoreStagingCompatibilityProbe[]
                {
                    new EdgeRetailsEfRestoreCompatibilityProbe(),
                    new EdgeRetailsBusinessRestoreCompatibilityProbe(Path.Combine(tools, "psql.exe"))
                }),
            manifestAuthenticator);

        var backup = await engine.CreateAsync(new BackupCreateRequest(runtime, temp, "integration-test", null, new BackupRetentionPolicy(null, null), "test"));
        Assert.True(File.Exists(backup.FullPath));
        Assert.True(File.Exists(backup.FullPath + ".manifest.json"));
        Assert.Equal(64, backup.Manifest.Sha256.Length);

        RestoreSessionToken? token = null;
        try
        {
            token = await engine.PrepareRestoreAsync(new RestorePrepareRequest(runtime, backup.FullPath, backup.FullPath + ".manifest.json", "test"));
            Assert.Equal(
                ProductionMaintenanceState.RestorePreparing,
                await barrier.GetStateAsync());

            var session = await sessions.GetAsync(token.RestoreId);
            Assert.NotNull(session);
            Assert.Equal(RestoreSessionState.Prepared, session!.State);
            Assert.StartsWith("er_rst_", session.StagingDatabase, StringComparison.Ordinal);
            Assert.Equal(backup.Manifest.Sha256, session.VerifiedSha256);
            Assert.DoesNotContain(database + "__restore_", session.StagingDatabase, StringComparison.Ordinal);
            // Destructive cutover remains a separate explicit closure gate on the authorized Ali PostgreSQL test environment.
        }
        finally
        {
            if (token is not null)
            {
                await engine.DiscardPreparedRestoreAsync(runtime, token);
            }

            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static string Require(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required integration-test environment variable '{name}' is missing.");

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
            Assert.NotEqual(Guid.Empty, context.RestoreId);
            Assert.Equal(context.RuntimeConnection.Database, context.Manifest.DatabaseName);
            return Task.CompletedTask;
        }
    }
}
