using System.Text.Json;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3BackupRestoreApiContractTests
{
    [Fact]
    public async Task EveryBackupAndRestoreAction_RequiresAnAuthenticatedSettingsManager()
    {
        var controller = CreateController(new FakeBackupEngine(), new FakeRestoreSessionStore(), authorized: false, includeActor: false);

        var responses = await InvokeAllActions(controller);

        Assert.All(responses, response =>
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<UnauthorizedObjectResult>(response).StatusCode));
    }

    [Fact]
    public async Task EveryBackupAndRestoreAction_RejectsAuthenticatedActorsWithoutSettingsManage()
    {
        var engine = new FakeBackupEngine();
        var controller = CreateController(engine, new FakeRestoreSessionStore(), authorized: false);

        var responses = await InvokeAllActions(controller);

        Assert.All(responses, response =>
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(response).StatusCode));
        Assert.Equal(0, engine.CreateCount);
        Assert.Equal(0, engine.PrepareCount);
        Assert.Equal(0, engine.CutoverCount);
        Assert.Equal(0, engine.DiscardCount);
        Assert.Equal(0, engine.RecoveryCount);
    }

    [Fact]
    public async Task CreateBackup_IgnoresHostileClientPathsAndCredentialsAndUsesServerConfiguration()
    {
        var engine = new FakeBackupEngine();
        var controller = CreateController(engine, new FakeRestoreSessionStore(), authorized: true);
        var correlationId = Guid.NewGuid();
        const string hostileJson = """{"correlationId":"00000000-0000-0000-0000-000000000000","backupDirectory":"C:\\attacker","connection":{"host":"evil","database":"other","username":"root","password":"secret"},"key":"client-secret"}""";
        var request = JsonSerializer.Deserialize<CreateBackupApiRequest>(hostileJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        request = request with { CorrelationId = correlationId };

        var result = await controller.Create(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(engine.LastCreateRequest);
        Assert.Equal("127.0.0.1", engine.LastCreateRequest.Connection.Host);
        Assert.Equal("edge-retails", engine.LastCreateRequest.Connection.Database);
        Assert.Equal("server-role", engine.LastCreateRequest.Connection.Username);
        Assert.Equal("server-only-password", engine.LastCreateRequest.Connection.Password.Reveal());
        Assert.Equal(Path.GetFullPath("C:\\server-owned-backups"), engine.LastCreateRequest.BackupDirectory);
        Assert.Equal(correlationId.ToString("D"), engine.LastCreateRequest.CorrelationId);
    }

    [Fact]
    public async Task RestoreStatus_ReturnsOnlySanitizedJournalMetadata()
    {
        var restoreId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var journal = new RestoreSessionRecord(
            restoreId,
            "edge-retails",
            "rst_internal_staging_db",
            100,
            101,
            "C:\\private\\backup.erbak",
            new string('A', 64),
            DateTimeOffset.UtcNow,
            RestoreSessionState.Prepared,
            ClientOperationId: operationId);
        var store = new FakeRestoreSessionStore(journal);
        var controller = CreateController(new FakeBackupEngine(), store, authorized: true);

        var response = await controller.GetRestoreStatus(restoreId, CancellationToken.None);

        var body = Assert.IsType<RestoreStatusApiResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(restoreId, body.Session.RestoreId);
        Assert.Equal(operationId, body.Session.ClientOperationId);
        Assert.Equal(BackupsController.BuildCutoverConfirmation("edge-retails", restoreId), body.CutoverConfirmation);
        Assert.Equal(BackupsController.BuildDiscardConfirmation("edge-retails", restoreId), body.DiscardConfirmation);
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("TargetDatabase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StagingDatabase", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BackupFilePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("VerifiedSha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rst_internal", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupHistoryDiagnostics_ExposesAggregatedSafeCodesWithoutArtifactNamesOrPaths()
    {
        var verified = new BackupManifest(
            Guid.NewGuid(), "edge-retails", "verified.erbak", new string('A', 64), 12,
            DateTimeOffset.UtcNow, "18", "pg_dump 18", "test-version", "schema-1", "AES-256-GCM");
        var engine = new FakeBackupEngine
        {
            History = [verified],
            HistoryDiagnostics = new BackupHistoryDiagnostics(
                [verified],
                [
                    new BackupHistoryIssue("C:\\private\\customer-name.manifest.json", "backup.artifact_checksum_mismatch"),
                    new BackupHistoryIssue("D:\\secret\\another.manifest.json", "hostile.path.and.secret")
                ])
        };
        var controller = CreateController(engine, new FakeRestoreSessionStore(), authorized: true);

        var response = await controller.GetHistoryDiagnostics(CancellationToken.None);

        var body = Assert.IsType<BackupHistoryDiagnosticsApiResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(1, body.VerifiedBackupCount);
        Assert.Equal(2, body.InvalidArtifactCount);
        Assert.Equal(
            new SafeBackupRecord(
                verified.BackupId,
                verified.CreatedAtUtc,
                verified.SizeBytes,
                verified.PostgreSqlServerVersion,
                verified.PgDumpVersion,
                verified.ApplicationVersion,
                verified.SchemaVersion,
                verified.Protection,
                verified.FormatVersion),
            Assert.Single(body.VerifiedBackups));
        Assert.Equal(
            new[] { new BackupHistoryIssueCount("backup.artifact_checksum_mismatch", 1), new BackupHistoryIssueCount("backup.validation_issue", 1) },
            body.Issues);
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("customer-name", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hostile.path", json, StringComparison.OrdinalIgnoreCase);

        // The original history route keeps its array response for existing clients.
        var legacy = Assert.IsType<OkObjectResult>(await controller.GetHistory(CancellationToken.None));
        Assert.IsAssignableFrom<IReadOnlyList<SafeBackupRecord>>(legacy.Value);
    }

    [Fact]
    public async Task RestoreMutations_RequireExactConfirmationBoundToRestoreAndConfiguredTarget()
    {
        var restoreId = Guid.NewGuid();
        var journal = new RestoreSessionRecord(
            restoreId,
            "edge-retails",
            "rst_internal_staging_db",
            100,
            101,
            "C:\\private\\backup.erbak",
            new string('A', 64),
            DateTimeOffset.UtcNow,
            RestoreSessionState.Prepared,
            ClientOperationId: Guid.NewGuid());
        var engine = new FakeBackupEngine();
        var controller = CreateController(engine, new FakeRestoreSessionStore(journal), authorized: true);
        var correlationId = Guid.NewGuid();

        var cutoverDenied = await controller.CutoverRestore(
            restoreId,
            new RestoreConfirmationApiRequest($"CUTOVER other-db {restoreId:N}", correlationId),
            CancellationToken.None);
        var discardDenied = await controller.DiscardRestore(
            restoreId,
            new RestoreConfirmationApiRequest("", correlationId),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(cutoverDenied).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(discardDenied).StatusCode);
        Assert.Equal(0, engine.CutoverCount);
        Assert.Equal(0, engine.DiscardCount);
        Assert.Equal($"CUTOVER edge-retails {restoreId:N}", BackupsController.BuildCutoverConfirmation("edge-retails", restoreId));
        Assert.Equal($"DISCARD edge-retails {restoreId:N}", BackupsController.BuildDiscardConfirmation("edge-retails", restoreId));
    }

    [Fact]
    public async Task PrepareRetry_WithSameOperationIdAndPreparingJournal_ReconcilesInsteadOfStartingAnotherPrepare()
    {
        var backupId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var restoreId = Guid.NewGuid();
        var backup = new BackupManifest(
            backupId,
            "edge-retails",
            "verified.erbak",
            new string('A', 64),
            10,
            DateTimeOffset.UtcNow,
            "18",
            "pg_dump 18",
            "test-version",
            null,
            "AES-256-GCM");
        var journal = new RestoreSessionRecord(
            restoreId,
            "edge-retails",
            "rst_internal_staging_db",
            100,
            101,
            Path.GetFullPath(Path.Combine("C:\\server-owned-backups", backup.FileName)),
            backup.Sha256,
            DateTimeOffset.UtcNow,
            RestoreSessionState.Preparing,
            ClientOperationId: operationId);
        var store = new FakeRestoreSessionStore(journal);
        var engine = new FakeBackupEngine { History = [backup] };
        engine.RecoveryBehavior = async (clientOperationId, _) =>
        {
            Assert.Equal(operationId, clientOperationId);
            var reconciled = journal with { State = RestoreSessionState.Discarded };
            await store.UpdateAsync(reconciled);
            return new RestoreSessionSummary(restoreId, operationId, RestoreSessionState.Discarded, journal.PreparedAtUtc, null);
        };
        var controller = CreateController(engine, store, authorized: true);
        var correlationId = Guid.NewGuid();

        var response = await controller.PrepareRestore(
            backupId,
            new PrepareRestoreApiRequest(operationId, correlationId),
            CancellationToken.None);

        var body = Assert.IsType<RestoreStatusApiResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(restoreId, body.Session.RestoreId);
        Assert.Equal(operationId, body.Session.ClientOperationId);
        Assert.Equal(RestoreSessionState.Discarded, body.Session.State);
        Assert.Null(body.CutoverConfirmation);
        Assert.Null(body.DiscardConfirmation);
        Assert.Equal(1, engine.RecoveryCount);
        Assert.Equal(0, engine.PrepareCount);
    }

    [Fact]
    public void PublicRequestContracts_DoNotExposeServerConnectionOrFilesystemInputs()
    {
        Assert.Equal(new[] { nameof(CreateBackupApiRequest.CorrelationId) }, typeof(CreateBackupApiRequest).GetProperties().Select(x => x.Name));
        Assert.Equal(
            new[] { nameof(PrepareRestoreApiRequest.ClientOperationId), nameof(PrepareRestoreApiRequest.CorrelationId) },
            typeof(PrepareRestoreApiRequest).GetProperties().Select(x => x.Name));
        Assert.Equal(
            new[] { nameof(RestoreConfirmationApiRequest.Confirmation), nameof(RestoreConfirmationApiRequest.CorrelationId) },
            typeof(RestoreConfirmationApiRequest).GetProperties().Select(x => x.Name));
    }

    private static async Task<IActionResult[]> InvokeAllActions(BackupsController controller)
    {
        var restoreId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        return
        [
            await controller.GetHistory(CancellationToken.None),
            await controller.GetHistoryDiagnostics(CancellationToken.None),
            await controller.Create(new CreateBackupApiRequest(correlationId), CancellationToken.None),
            await controller.PrepareRestore(Guid.NewGuid(), new PrepareRestoreApiRequest(operationId, correlationId), CancellationToken.None),
            await controller.GetRestoreStatus(restoreId, CancellationToken.None),
            await controller.GetRestoreOperationStatus(operationId, CancellationToken.None),
            await controller.RecoverRestorePreparation(operationId, new RestoreRecoveryApiRequest(correlationId), CancellationToken.None),
            await controller.CutoverRestore(restoreId, new RestoreConfirmationApiRequest("no", correlationId), CancellationToken.None),
            await controller.DiscardRestore(restoreId, new RestoreConfirmationApiRequest("no", correlationId), CancellationToken.None)
        ];
    }

    private static BackupsController CreateController(
        FakeBackupEngine engine,
        FakeRestoreSessionStore restoreSessions,
        bool authorized,
        bool includeActor = true)
    {
        var inMemory = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=5432;Database=edge-retails;Username=server-role;Password=server-only-password",
            ["EDGE_RETAILS_BACKUP_DIR"] = "C:\\server-owned-backups"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var authorization = new AllowProductionAuthorization();
        var audit = new ProductionAuditCoordinator(new NoOpAuditSink(), new NoOpAuditFailureReporter());
        var controller = new BackupsController(
            configuration,
            new PostgresConnectionDescriptor("127.0.0.1", 5432, "edge-retails", "server-role", new SensitiveString("server-only-password")),
            engine,
            restoreSessions,
            new CreateBackupHandler(authorization, engine, audit),
            new PrepareRestoreHandler(authorization, engine, audit),
            new CutoverRestoreHandler(authorization, engine, audit),
            new DiscardPreparedRestoreHandler(authorization, engine, audit),
            new RecoverRestorePreparationHandler(authorization, engine, audit))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        if (includeActor)
        {
            controller.HttpContext.Items["ActorContext"] = new ActorContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Test Operator",
                Guid.NewGuid(),
                "Test Role",
                authorized ? new HashSet<string>([PermissionKeys.SettingsManage]) : new HashSet<string>());
        }

        return controller;
    }

    private sealed class FakeBackupEngine : IPostgresBackupEngine
    {
        public int CreateCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int CutoverCount { get; private set; }
        public int DiscardCount { get; private set; }
        public int RecoveryCount { get; private set; }
        public BackupCreateRequest? LastCreateRequest { get; private set; }
        public IReadOnlyList<BackupManifest> History { get; init; } = [];
        public BackupHistoryDiagnostics? HistoryDiagnostics { get; init; }
        public Func<Guid, CancellationToken, Task<RestoreSessionSummary>>? RecoveryBehavior { get; set; }

        public Task<BackupCreateResult> CreateAsync(BackupCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreateCount++;
            LastCreateRequest = request;
            var manifest = new BackupManifest(Guid.NewGuid(), request.Connection.Database, "verified.erbak", new string('B', 64), 10,
                DateTimeOffset.UtcNow, "18", "pg_dump 18", request.ApplicationVersion, null, "AES-256-GCM");
            return Task.FromResult(new BackupCreateResult(manifest, "C:\\private\\verified.erbak"));
        }

        public Task<IReadOnlyList<BackupManifest>> ReadHistoryAsync(string backupDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(History);

        public Task<BackupHistoryDiagnostics> ReadDiagnosticsAsync(string backupDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(HistoryDiagnostics ?? new BackupHistoryDiagnostics(History, []));

        public Task<RestoreSessionToken> PrepareRestoreAsync(RestorePrepareRequest request, CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            return Task.FromResult(new RestoreSessionToken(Guid.NewGuid()));
        }

        public Task<RestoreSessionSummary> RecoverRestorePreparationAsync(Guid clientOperationId, CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            if (RecoveryBehavior is not null)
            {
                return RecoveryBehavior(clientOperationId, cancellationToken);
            }

            return Task.FromResult(new RestoreSessionSummary(Guid.NewGuid(), clientOperationId, RestoreSessionState.Discarded, DateTimeOffset.UtcNow, null));
        }

        public Task<RestoreCutoverResult> CutoverAsync(PostgresConnectionDescriptor runtimeConnection, RestoreSessionToken token, CancellationToken cancellationToken = default)
        {
            CutoverCount++;
            return Task.FromResult(new RestoreCutoverResult(token.RestoreId, runtimeConnection.Database, "private-preserved-db", DateTimeOffset.UtcNow));
        }

        public Task DiscardPreparedRestoreAsync(PostgresConnectionDescriptor runtimeConnection, RestoreSessionToken token, CancellationToken cancellationToken = default)
        {
            DiscardCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRestoreSessionStore(RestoreSessionRecord? initial = null) : IRestoreSessionStore
    {
        private readonly Dictionary<Guid, RestoreSessionRecord> _sessions = initial is null
            ? []
            : new Dictionary<Guid, RestoreSessionRecord> { [initial.RestoreId] = initial };

        public Task CreateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default)
        {
            _sessions.Add(session.RestoreId, session);
            return Task.CompletedTask;
        }

        public Task<RestoreSessionRecord?> GetAsync(Guid restoreId, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions.GetValueOrDefault(restoreId));

        public Task<RestoreSessionRecord?> GetJournalByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions.Values.SingleOrDefault(x => x.ClientOperationId == clientOperationId));

        public Task<RestoreSessionSummary?> GetSummaryByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken = default)
        {
            var session = _sessions.Values.SingleOrDefault(x => x.ClientOperationId == clientOperationId);
            return Task.FromResult(session is null
                ? null
                : new RestoreSessionSummary(session.RestoreId, session.ClientOperationId, session.State, session.PreparedAtUtc, session.CompletedAtUtc));
        }

        public Task<IReadOnlyList<RestoreSessionSummary>> ListRecoverableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RestoreSessionSummary>>(_sessions.Values
                .Select(x => new RestoreSessionSummary(x.RestoreId, x.ClientOperationId, x.State, x.PreparedAtUtc, x.CompletedAtUtc))
                .ToArray());

        public Task UpdateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default)
        {
            _sessions[session.RestoreId] = session;
            return Task.CompletedTask;
        }
    }

    private sealed class AllowProductionAuthorization : IProductionAuthorization
    {
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(ProductionAuditRecord record, Exception error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
