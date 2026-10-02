using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;

namespace EdgeRetails.UnitTests;

public sealed class Phase3CutoverConditionalBarrierTests
{
    [Fact]
    public async Task Cutover_InterveningRecoveryRequiredState_IsRefusedBeforeAnySql()
    {
        var runtime = new PostgresConnectionDescriptor("localhost", 5432, "prod", "runtime", new SensitiveString("runtime-secret"));
        var maintenance = new PostgresMaintenanceDescriptor("localhost", 5432, "recovery", new SensitiveString("maintenance-secret"));
        var restoreId = Guid.NewGuid();
        var token = new RestoreSessionToken(restoreId);
        var sessions = new InMemorySessionStore(new RestoreSessionRecord(
            restoreId,
            "prod",
            "er_rst_phase3",
            10,
            11,
            "backup.erbak",
            new string('A', 64),
            DateTimeOffset.UtcNow,
            RestoreSessionState.Prepared));
        var barrier = new RacingRecoveryBarrier();
        var runner = new CountingRunner();
        var marker = typeof(Phase3CutoverConditionalBarrierTests).Assembly.Location;
        var engine = new PostgresBackupEngine(
            marker,
            marker,
            marker,
            marker,
            new NoopProtector(),
            sessions,
            new FixedMaintenanceProvider(maintenance),
            barrier,
            new NoopStagingValidator(),
            new TestManifestAuthenticator(),
            runner);

        var error = await Assert.ThrowsAsync<ProductionMaintenanceException>(
            () => engine.CutoverAsync(runtime, token));

        Assert.Equal(ProductionMaintenanceState.RecoveryRequired, error.State);
        Assert.Equal(1, barrier.ConditionalAttemptCount);
        Assert.Equal(0, runner.CommandCount);
        Assert.Equal(RestoreSessionState.Prepared, (await sessions.GetAsync(restoreId))!.State);
    }

    private sealed class RacingRecoveryBarrier : IConditionalProductionMaintenanceBarrier
    {
        private ProductionMaintenanceState _state = ProductionMaintenanceState.RestorePreparing;
        public int ConditionalAttemptCount { get; private set; }

        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_state);

        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(
            ProductionMaintenanceState state,
            CancellationToken cancellationToken = default)
            => Task.FromException<IProductionMaintenanceLease>(new InvalidOperationException("Legacy unconditional transition was not expected."));

        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(
            ProductionMaintenanceState expectedCurrentState,
            ProductionMaintenanceState state,
            CancellationToken cancellationToken = default)
        {
            ConditionalAttemptCount++;
            _state = ProductionMaintenanceState.RecoveryRequired; // Simulate a transition between the read and compare-and-enter.
            if (_state != expectedCurrentState)
            {
                throw new ProductionMaintenanceException(_state);
            }

            return Task.FromResult<IProductionMaintenanceLease>(new Lease());
        }

        private sealed class Lease : IProductionMaintenanceLease
        {
            public Task SetExitStateAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CountingRunner : IPostgresProcessRunner
    {
        public int CommandCount { get; private set; }
        public Task<ProcessResult> RunAsync(
            string fileName,
            IEnumerable<string> arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken cancellationToken)
        {
            CommandCount++;
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class InMemorySessionStore : IRestoreSessionStore
    {
        private readonly Dictionary<Guid, RestoreSessionRecord> _sessions;
        public InMemorySessionStore(params RestoreSessionRecord[] sessions)
            => _sessions = sessions.ToDictionary(x => x.RestoreId);
        public Task CreateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default)
        {
            _sessions.Add(session.RestoreId, session);
            return Task.CompletedTask;
        }
        public Task<RestoreSessionRecord?> GetAsync(Guid restoreId, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions.TryGetValue(restoreId, out var session) ? session : null);
        public Task UpdateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default)
        {
            _sessions[session.RestoreId] = session;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedMaintenanceProvider(PostgresMaintenanceDescriptor connection) : IPostgresMaintenanceConnectionProvider
    {
        public Task<PostgresMaintenanceDescriptor> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(connection);
    }

    private sealed class NoopProtector : IBackupProtector
    {
        public string ProtectionName => "test";
        public Task ProtectAsync(string plainDumpPath, string protectedBackupPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task UnprotectAsync(string protectedBackupPath, string plainDumpPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopStagingValidator : IRestoreStagingValidator
    {
        public Task ValidateAsync(RestoreStagingValidationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class TestManifestAuthenticator : IBackupManifestAuthenticator
    {
        public Task<string> ComputeAuthenticationAsync(BackupManifest manifest, CancellationToken cancellationToken = default)
            => Task.FromResult("test");
        public Task<bool> VerifyAuthenticationAsync(BackupManifest manifest, string authentication, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
