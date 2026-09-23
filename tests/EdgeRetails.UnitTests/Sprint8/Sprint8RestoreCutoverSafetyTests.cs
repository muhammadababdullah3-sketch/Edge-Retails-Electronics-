using System.Text.RegularExpressions;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8RestoreCutoverSafetyTests
{
    [Fact]
    public async Task Cutover_AmbiguousFailureAfterFirstRename_RestoresOriginalByOid()
    {
        var fixture = Fixture.Create(FailureMode.AfterFirstRename);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.CutoverAsync(fixture.Runtime, fixture.Token));
        Assert.Equal("prod", fixture.Runner.NameForOid(10));
        Assert.Equal(RestoreSessionState.RolledBack, (await fixture.Sessions.GetAsync(fixture.Token.RestoreId))!.State);
        Assert.Equal(ProductionMaintenanceState.Normal, fixture.Barrier.ExitState);
    }

    [Fact]
    public async Task Cutover_AmbiguousFailureAfterSecondRename_RestoresOriginalByOid()
    {
        var fixture = Fixture.Create(FailureMode.AfterSecondRename);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.CutoverAsync(fixture.Runtime, fixture.Token));
        Assert.Equal("prod", fixture.Runner.NameForOid(10));
        Assert.NotEqual("prod", fixture.Runner.NameForOid(11));
        Assert.Equal(RestoreSessionState.RolledBack, (await fixture.Sessions.GetAsync(fixture.Token.RestoreId))!.State);
    }

    [Fact]
    public async Task Cutover_CallerCancellationAfterDestructivePhaseBegins_DoesNotInterruptSafetyCompletion()
    {
        using var caller = new CancellationTokenSource();
        var fixture = Fixture.Create(FailureMode.None);
        fixture.Runner.AfterFirstRename = caller.Cancel;

        var result = await fixture.Engine.CutoverAsync(fixture.Runtime, fixture.Token, caller.Token);

        Assert.Equal("prod", fixture.Runner.NameForOid(11));
        Assert.NotEqual("prod", fixture.Runner.NameForOid(10));
        Assert.Equal(RestoreSessionState.Completed, (await fixture.Sessions.GetAsync(fixture.Token.RestoreId))!.State);
        Assert.Equal(fixture.Token.RestoreId, result.RestoreId);
    }

    [Fact]
    public async Task Cutover_UnknownOpaqueToken_IsRejectedBeforeDestructiveSql()
    {
        var fixture = Fixture.Create(FailureMode.None);
        var unknown = new RestoreSessionToken(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.CutoverAsync(fixture.Runtime, unknown));
        Assert.Equal(0, fixture.Runner.RenameCount);
        Assert.Equal("prod", fixture.Runner.NameForOid(10));
    }

    [Fact]
    public async Task Cutover_WithoutRestorePreparing_IsRejectedBeforeDestructiveSql()
    {
        var fixture = Fixture.Create(
            FailureMode.None,
            ProductionMaintenanceState.Normal);

        var error = await Assert.ThrowsAsync<ProductionMaintenanceException>(
            () => fixture.Engine.CutoverAsync(fixture.Runtime, fixture.Token));

        Assert.Equal(ProductionMaintenanceState.Normal, error.State);
        Assert.Equal(0, fixture.Runner.RenameCount);
        Assert.Equal("prod", fixture.Runner.NameForOid(10));
        Assert.Equal("er_rst_seed", fixture.Runner.NameForOid(11));
    }

    [Fact]
    public async Task DiscardPreparedRestore_RevokesCutoverAuthorityBeforeDroppingStagingDatabase()
    {
        var fixture = Fixture.Create(FailureMode.None);
        RestoreSessionState? stateAtDrop = null;
        fixture.Runner.BeforeDrop = () =>
            stateAtDrop = fixture.Sessions.GetAsync(fixture.Token.RestoreId).GetAwaiter().GetResult()!.State;

        await fixture.Engine.DiscardPreparedRestoreAsync(fixture.Runtime, fixture.Token);

        Assert.Equal(RestoreSessionState.Discarded, stateAtDrop);
        Assert.Null(fixture.Runner.NameForOid(11));
        Assert.Equal(RestoreSessionState.Discarded, (await fixture.Sessions.GetAsync(fixture.Token.RestoreId))!.State);
        Assert.Equal(ProductionMaintenanceState.Normal, fixture.Barrier.ExitState);
        Assert.Equal(1, fixture.Barrier.EnterCount);
    }

    [Fact]
    public async Task DiscardPreparedRestore_StagingOidResolvingToActiveProduction_FailsClosedWithoutDrop()
    {
        var fixture = Fixture.Create(FailureMode.None);
        fixture.Runner.ForceNameForOid(11, fixture.Runtime.Database);

        await Assert.ThrowsAsync<RestoreRecoveryRequiredException>(
            () => fixture.Engine.DiscardPreparedRestoreAsync(fixture.Runtime, fixture.Token));

        Assert.Equal(0, fixture.Runner.DropCount);
        Assert.Equal(RestoreSessionState.RecoveryRequired, (await fixture.Sessions.GetAsync(fixture.Token.RestoreId))!.State);
        Assert.Equal(ProductionMaintenanceState.RecoveryRequired, fixture.Barrier.ExitState);
    }

    [Fact]
    public async Task RestoreCutoverSuccess_AuditFailure_DoesNotBecomeFalseRestoreFailure()
    {
        var engine = new SuccessfulApplicationEngine();
        var reporter = new RecordingAuditFailureReporter();
        var handler = new CutoverRestoreHandler(
            new AllowAuthorization(),
            engine,
            new ProductionAuditCoordinator(new ThrowingAuditSink(), reporter));
        var runtime = new PostgresConnectionDescriptor("localhost", 5432, "prod", "runtime", new SensitiveString("secret"));
        var token = new RestoreSessionToken(Guid.NewGuid());

        var result = await handler.HandleAsync(runtime, token, "c");

        Assert.Equal(token.RestoreId, result.RestoreId);
        Assert.Equal(1, reporter.Count);
    }

    private enum FailureMode { None, AfterFirstRename, AfterSecondRename }

    private sealed class Fixture
    {
        public required PostgresBackupEngine Engine { get; init; }
        public required FakePostgresRunner Runner { get; init; }
        public required InMemorySessionStore Sessions { get; init; }
        public required RecordingBarrier Barrier { get; init; }
        public required PostgresConnectionDescriptor Runtime { get; init; }
        public required RestoreSessionToken Token { get; init; }

        public static Fixture Create(
            FailureMode failure,
            ProductionMaintenanceState maintenanceState = ProductionMaintenanceState.RestorePreparing)
        {
            var marker = typeof(Sprint8RestoreCutoverSafetyTests).Assembly.Location;
            var runtime = new PostgresConnectionDescriptor("localhost", 5432, "prod", "runtime", new SensitiveString("runtime-secret"));
            var maintenance = new PostgresMaintenanceDescriptor("localhost", 5432, "recovery", new SensitiveString("maintenance-secret"));
            var id = Guid.NewGuid();
            var token = new RestoreSessionToken(id);
            var sessions = new InMemorySessionStore(new RestoreSessionRecord(
                id, "prod", "er_rst_seed", 10, 11, "backup.erbak", new string('A', 64), DateTimeOffset.UtcNow, RestoreSessionState.Prepared));
            var runner = new FakePostgresRunner(failure);
            runner.AddDatabase("prod", 10);
            runner.AddDatabase("er_rst_seed", 11);
            var barrier = new RecordingBarrier(maintenanceState);
            var engine = new PostgresBackupEngine(
                marker, marker, marker, marker,
                new NoopProtector(), sessions, new FixedMaintenanceProvider(maintenance), barrier, new NoopStagingValidator(), new TestManifestAuthenticator(), runner);
            return new Fixture { Engine = engine, Runner = runner, Sessions = sessions, Barrier = barrier, Runtime = runtime, Token = token };
        }
    }

    private sealed class FakePostgresRunner : IPostgresProcessRunner
    {
        private readonly FailureMode _failureMode;
        private readonly Dictionary<string, uint> _oidsByName = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, string> _namesByOid = new();
        public int RenameCount { get; private set; }
        public int DropCount { get; private set; }
        public Action? AfterFirstRename { get; set; }
        public Action? BeforeDrop { get; set; }

        public FakePostgresRunner(FailureMode failureMode) => _failureMode = failureMode;

        public void AddDatabase(string name, uint oid) { _oidsByName[name] = oid; _namesByOid[oid] = name; }
        public void ForceNameForOid(uint oid, string name) => _namesByOid[oid] = name;
        public string? NameForOid(uint oid) => _namesByOid.TryGetValue(oid, out var name) ? name : null;

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = arguments.ToArray();
            var commandIndex = Array.IndexOf(args, "--command");
            if (commandIndex < 0)
            {
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            var sql = args[commandIndex + 1];

            if (sql.StartsWith("SELECT oid::text FROM pg_database WHERE datname = ", StringComparison.Ordinal))
            {
                var name = ParseSingleQuoted(sql);
                return Ok(_oidsByName.TryGetValue(name, out var oid) ? oid.ToString() : string.Empty);
            }
            if (sql.StartsWith("SELECT datname FROM pg_database WHERE oid = ", StringComparison.Ordinal))
            {
                var match = Regex.Match(sql, @"oid = (\d+)");
                var oid = uint.Parse(match.Groups[1].Value);
                return Ok(NameForOid(oid) ?? string.Empty);
            }
            if (sql.StartsWith("SELECT CASE WHEN EXISTS", StringComparison.Ordinal))
            {
                var name = ParseSingleQuoted(sql);
                return Ok(_oidsByName.ContainsKey(name) ? "1" : "0");
            }
            if (sql.StartsWith("ALTER DATABASE ", StringComparison.Ordinal) && sql.Contains(" RENAME TO ", StringComparison.Ordinal))
            {
                var match = Regex.Match(sql, "ALTER DATABASE \\\"(?<from>[^\\\"]+)\\\" RENAME TO \\\"(?<to>[^\\\"]+)\\\"");
                var from = match.Groups["from"].Value;
                var to = match.Groups["to"].Value;
                if (!_oidsByName.Remove(from, out var oid))
                {
                    return Fail("source missing");
                }

                _oidsByName[to] = oid;
                _namesByOid[oid] = to;
                RenameCount++;
                if (RenameCount == 1)
                {
                    AfterFirstRename?.Invoke();
                }

                if ((_failureMode == FailureMode.AfterFirstRename && RenameCount == 1) ||
                    (_failureMode == FailureMode.AfterSecondRename && RenameCount == 2))
                {
                    return Fail("simulated ambiguous client failure after server commit");
                }

                return Ok(string.Empty);
            }
            if (sql.StartsWith("DROP DATABASE ", StringComparison.Ordinal))
            {
                BeforeDrop?.Invoke();
                DropCount++;
                var match = Regex.Match(sql, "DROP DATABASE \\\"(?<name>[^\\\"]+)\\\"");
                var name = match.Groups["name"].Value;
                if (_oidsByName.Remove(name, out var oid))
                {
                    _namesByOid.Remove(oid);
                }

                return Ok(string.Empty);
            }
            if (sql == "SELECT 1;")
            {
                return Ok("1");
            }

            return Ok(string.Empty);
        }

        private static Task<ProcessResult> Ok(string output) => Task.FromResult(new ProcessResult(0, output, string.Empty));
        private static Task<ProcessResult> Fail(string error) => Task.FromResult(new ProcessResult(1, string.Empty, error));
        private static string ParseSingleQuoted(string sql)
        {
            var matches = Regex.Matches(sql, "'(?<value>[^']*)'");
            return matches.Count == 0 ? string.Empty : matches[matches.Count - 1].Groups["value"].Value.Replace("''", "'");
        }
    }

    private sealed class InMemorySessionStore : IRestoreSessionStore
    {
        private readonly Dictionary<Guid, RestoreSessionRecord> _sessions = new();
        public InMemorySessionStore(params RestoreSessionRecord[] sessions)
        {
            foreach (var s in sessions)
            {
                _sessions[s.RestoreId] = s;
            }
        }
        public Task CreateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default) { _sessions.Add(session.RestoreId, session); return Task.CompletedTask; }
        public Task<RestoreSessionRecord?> GetAsync(Guid restoreId, CancellationToken cancellationToken = default) => Task.FromResult(_sessions.TryGetValue(restoreId, out var value) ? value : null);
        public Task UpdateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default) { _sessions[session.RestoreId] = session; return Task.CompletedTask; }
    }

    private sealed class FixedMaintenanceProvider : IPostgresMaintenanceConnectionProvider
    {
        private readonly PostgresMaintenanceDescriptor _connection;
        public FixedMaintenanceProvider(PostgresMaintenanceDescriptor connection) => _connection = connection;
        public Task<PostgresMaintenanceDescriptor> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_connection);
    }

    private sealed class RecordingBarrier : IProductionMaintenanceBarrier
    {
        public RecordingBarrier(ProductionMaintenanceState initialState) => ExitState = initialState;

        public ProductionMaintenanceState ExitState { get; private set; }
        public int EnterCount { get; private set; }
        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(ExitState);
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default)
        {
            EnterCount++;
            return Task.FromResult<IProductionMaintenanceLease>(new Lease(this));
        }
        private sealed class Lease : IProductionMaintenanceLease
        {
            private readonly RecordingBarrier _owner;
            private ProductionMaintenanceState _exit = ProductionMaintenanceState.Normal;
            public Lease(RecordingBarrier owner) => _owner = owner;
            public Task SetExitStateAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default) { _exit = state; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { _owner.ExitState = _exit; return ValueTask.CompletedTask; }
        }
    }

    private sealed class TestManifestAuthenticator : IBackupManifestAuthenticator
    {
        public Task<string> ComputeAuthenticationAsync(BackupManifest manifest, CancellationToken cancellationToken = default) => Task.FromResult(new string('A', 64));
        public Task<bool> VerifyAuthenticationAsync(BackupManifest manifest, string authentication, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class NoopProtector : IBackupProtector
    {
        public string ProtectionName => "test";
        public Task ProtectAsync(string plainDumpPath, string protectedBackupPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnprotectAsync(string protectedBackupPath, string plainDumpPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopStagingValidator : IRestoreStagingValidator
    {
        public Task ValidateAsync(RestoreStagingValidationContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SuccessfulApplicationEngine : IPostgresBackupEngine
    {
        public Task<BackupCreateResult> CreateAsync(BackupCreateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BackupManifest>> ReadHistoryAsync(string backupDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreSessionToken> PrepareRestoreAsync(RestorePrepareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RestoreCutoverResult> CutoverAsync(PostgresConnectionDescriptor runtimeConnection, RestoreSessionToken token, CancellationToken cancellationToken = default)
            => Task.FromResult(new RestoreCutoverResult(token.RestoreId, runtimeConnection.Database, "preserved", DateTimeOffset.UtcNow));
        public Task DiscardPreparedRestoreAsync(PostgresConnectionDescriptor runtimeConnection, RestoreSessionToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAuthorization : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => throw new IOException("audit failed");
    }

    private sealed class RecordingAuditFailureReporter : IProductionAuditFailureReporter
    {
        public int Count { get; private set; }
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) { Count++; return Task.CompletedTask; }
    }
}
