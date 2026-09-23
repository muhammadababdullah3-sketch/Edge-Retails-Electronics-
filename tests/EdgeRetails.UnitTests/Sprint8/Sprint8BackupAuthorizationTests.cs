using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8BackupAuthorizationTests
{
    [Fact]
    public async Task ManualBackup_RequiresSettingsManageAtApplicationLayer()
    {
        var auth = new RecordingAuthorization();
        var handler = new CreateBackupHandler(auth, new FakeEngine(), Audit());
        await handler.HandleAsync(Request());
        Assert.Equal(ProductionPermissionNames.SettingsManage, auth.LastPermission);
    }

    [Fact]
    public async Task RestorePrepare_RequiresSettingsManageAtApplicationLayer()
    {
        var auth = new RecordingAuthorization();
        var handler = new PrepareRestoreHandler(auth, new FakeEngine(), Audit());
        var request = Request();
        await handler.HandleAsync(new RestorePrepareRequest(
            request.Connection,
            "b.erbak",
            "b.erbak.manifest.json",
            "c"));
        Assert.Equal(ProductionPermissionNames.SettingsManage, auth.LastPermission);
    }

    [Fact]
    public async Task RestoreCutover_RequiresSettingsManageAtApplicationLayer()
    {
        var auth = new RecordingAuthorization();
        var handler = new CutoverRestoreHandler(auth, new FakeEngine(), Audit());
        await handler.HandleAsync(Request().Connection, new RestoreSessionToken(Guid.NewGuid()), "c");
        Assert.Equal(ProductionPermissionNames.SettingsManage, auth.LastPermission);
    }

    private static BackupCreateRequest Request()
        => new(new PostgresConnectionDescriptor("localhost", 5432, "db", "runtime", Secret()), ".", "test", null, new BackupRetentionPolicy(null, null), "c");
    private static PostgresMaintenanceDescriptor Maintenance()
        => new("localhost", 5432, "recovery", Secret());
    private static SensitiveString Secret() => new(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_SECRET") ?? string.Empty);
    private static ProductionAuditCoordinator Audit() => new(new NullAudit(), new NullAuditFailureReporter());

    private sealed class RecordingAuthorization : IProductionAuthorization
    {
        public string? LastPermission { get; private set; }
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) { LastPermission = permission; return Task.CompletedTask; }
    }

    private sealed class NullAudit : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeEngine : IPostgresBackupEngine
    {
        public Task<BackupCreateResult> CreateAsync(BackupCreateRequest request, CancellationToken cancellationToken = default)
        {
            var manifest = new BackupManifest(Guid.NewGuid(), request.Connection.Database, "b.erbak", new string('A', 64), 1, DateTimeOffset.UtcNow, null, "pg_dump", request.ApplicationVersion, request.SchemaVersion, "test");
            return Task.FromResult(new BackupCreateResult(manifest, "b.erbak"));
        }

        public Task<IReadOnlyList<BackupManifest>> ReadHistoryAsync(string backupDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BackupManifest>>(Array.Empty<BackupManifest>());
        public Task<RestoreSessionToken> PrepareRestoreAsync(RestorePrepareRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new RestoreSessionToken(Guid.NewGuid()));
        public Task<RestoreCutoverResult> CutoverAsync(PostgresConnectionDescriptor runtimeConnection, RestoreSessionToken token, CancellationToken cancellationToken = default)
            => Task.FromResult(new RestoreCutoverResult(token.RestoreId, runtimeConnection.Database, "preserved", DateTimeOffset.UtcNow));
        public Task DiscardPreparedRestoreAsync(PostgresConnectionDescriptor runtimeConnection, RestoreSessionToken token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
