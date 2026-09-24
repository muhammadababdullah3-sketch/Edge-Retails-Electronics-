using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed class PostgresBackupEngine : IPostgresBackupEngine
{
    private const long MaximumManifestBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan CutoverSafetyTimeout = TimeSpan.FromMinutes(2);

    private readonly IPostgresProcessRunner _runner;
    private readonly IBackupProtector _protector;
    private readonly IRestoreSessionStore _restoreSessions;
    private readonly IPostgresMaintenanceConnectionProvider _maintenanceConnectionProvider;
    private readonly IProductionMaintenanceBarrier _maintenanceBarrier;
    private readonly IRestoreStagingValidator _stagingValidator;
    private readonly IBackupManifestAuthenticator _manifestAuthenticator;
    private readonly BackupHistoryService _history;
    private readonly string _pgDump;
    private readonly string _pgRestore;
    private readonly string _psql;
    private readonly string _createdb;

    public PostgresBackupEngine(
        string pgDumpPath,
        string pgRestorePath,
        string psqlPath,
        string createdbPath,
        IBackupProtector protector,
        IRestoreSessionStore restoreSessions,
        IPostgresMaintenanceConnectionProvider maintenanceConnectionProvider,
        IProductionMaintenanceBarrier maintenanceBarrier,
        IRestoreStagingValidator stagingValidator,
        IBackupManifestAuthenticator manifestAuthenticator,
        IPostgresProcessRunner? runner = null)
    {
        _pgDump = RequireExecutable(pgDumpPath, nameof(pgDumpPath));
        _pgRestore = RequireExecutable(pgRestorePath, nameof(pgRestorePath));
        _psql = RequireExecutable(psqlPath, nameof(psqlPath));
        _createdb = RequireExecutable(createdbPath, nameof(createdbPath));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _restoreSessions = restoreSessions ?? throw new ArgumentNullException(nameof(restoreSessions));
        _maintenanceConnectionProvider = maintenanceConnectionProvider ?? throw new ArgumentNullException(nameof(maintenanceConnectionProvider));
        _maintenanceBarrier = maintenanceBarrier ?? throw new ArgumentNullException(nameof(maintenanceBarrier));
        _stagingValidator = stagingValidator ?? throw new ArgumentNullException(nameof(stagingValidator));
        _manifestAuthenticator = manifestAuthenticator ?? throw new ArgumentNullException(nameof(manifestAuthenticator));
        _history = new BackupHistoryService(_manifestAuthenticator);
        _runner = runner ?? new ProcessRunner();
    }

    public async Task<BackupCreateResult> CreateAsync(BackupCreateRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.BackupDirectory);
        _ = BackupArtifactPathSafety.ResolveOwnedBackupPath(request.BackupDirectory, "probe.erbak");
        var backupId = Guid.NewGuid();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var safeDb = SafeFilePart(request.Connection.Database);
        var fileName = $"{safeDb}_{stamp}_{backupId:N}.erbak";
        var fullPath = BackupArtifactPathSafety.ResolveOwnedBackupPath(request.BackupDirectory, fileName);
        var protectedTemp = fullPath + ".partial";
        var tempRoot = CreateOperationTempRoot("backup", backupId);
        var plainTemp = Path.Combine(tempRoot, "database.dump");
        var env = PasswordEnvironment(request.Connection);
        var committed = false;

        try
        {
            var dump = await _runner.RunAsync(_pgDump, new[]
            {
                "--format=custom", "--no-password", "--verbose",
                "--host", request.Connection.Host,
                "--port", request.Connection.Port.ToString(),
                "--username", request.Connection.Username,
                "--file", plainTemp,
                request.Connection.Database
            }, env, cancellationToken);
            EnsureSuccess("pg_dump", dump);
            var list = await _runner.RunAsync(_pgRestore, new[] { "--list", plainTemp }, null, cancellationToken);
            EnsureSuccess("pg_restore --list", list);

            await _protector.ProtectAsync(plainTemp, protectedTemp, cancellationToken);
            TryDelete(plainTemp);
            File.Move(protectedTemp, fullPath, overwrite: false);

            var info = new FileInfo(fullPath);
            var hash = await ComputeSha256Async(fullPath, cancellationToken);
            var pgDumpVersion = await ReadToolVersionAsync(_pgDump, cancellationToken);
            var serverVersion = await QueryScalarAsync(request.Connection, request.Connection.Database, "SHOW server_version;", cancellationToken);
            var manifest = new BackupManifest(
                backupId, request.Connection.Database, fileName, hash, info.Length, DateTimeOffset.UtcNow,
                serverVersion, pgDumpVersion, request.ApplicationVersion, request.SchemaVersion, _protector.ProtectionName)
            {
                FormatVersion = 2
            };
            var authentication = await _manifestAuthenticator.ComputeAuthenticationAsync(manifest, cancellationToken);
            var envelope = new BackupManifestEnvelope(manifest, authentication);
            await AtomicWriteAsync(fullPath + ".manifest.json", JsonSerializer.Serialize(envelope, JsonOptions), cancellationToken);
            committed = true;

            string? retentionWarning = null;
            try
            {
                // Once the new backup is committed, caller cancellation must not turn a successful backup
                // into a failure that then deletes the newly-created artifact. Retention is post-commit maintenance.
                await _history.ApplyRetentionAsync(request.BackupDirectory, request.RetentionPolicy, CancellationToken.None);
            }
            catch
            {
                retentionWarning = "backup.retention_failed";
            }

            return new BackupCreateResult(manifest, fullPath) { RetentionWarningCode = retentionWarning };
        }
        catch
        {
            if (!committed)
            {
                TryDelete(protectedTemp);
                TryDelete(fullPath);
                TryDelete(fullPath + ".manifest.json");
            }
            throw;
        }
        finally
        {
            TryDelete(plainTemp);
            TryDeleteDirectory(tempRoot);
        }
    }

    public Task<IReadOnlyList<BackupManifest>> ReadHistoryAsync(string backupDirectory, CancellationToken cancellationToken = default)
        => _history.ReadHistoryAsync(backupDirectory, cancellationToken);

    public async Task<RestoreSessionToken> PrepareRestoreAsync(RestorePrepareRequest request, CancellationToken cancellationToken = default)
    {
        var currentMaintenanceState = await _maintenanceBarrier.GetStateAsync(cancellationToken);
        if (currentMaintenanceState != ProductionMaintenanceState.Normal)
        {
            throw new ProductionMaintenanceException(currentMaintenanceState);
        }

        await using var maintenanceLease = await _maintenanceBarrier.EnterExclusiveAsync(
            ProductionMaintenanceState.RestorePreparing,
            cancellationToken);
        var maintenanceConnection = await _maintenanceConnectionProvider.GetAsync(cancellationToken);
        ValidateMaintenanceEndpoint(request.RuntimeConnection, maintenanceConnection);
        var backupPath = Path.GetFullPath(request.BackupFilePath);
        var manifestPath = Path.GetFullPath(request.ManifestFilePath);
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("Backup file was not found.", backupPath);
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Backup manifest was not found.", manifestPath);
        }

        if (!string.Equals(manifestPath, backupPath + ".manifest.json", PathComparison))
        {
            throw new InvalidDataException("Restore manifest must be the exact companion manifest of the selected backup file.");
        }

        RejectReparsePoint(backupPath, "Backup artifact");
        RejectReparsePoint(manifestPath, "Backup manifest");
        var envelope = await ReadManifestEnvelopeAsync(manifestPath, cancellationToken);
        var manifest = envelope.Manifest ?? throw new InvalidDataException("Backup manifest payload is missing.");
        if (manifest.FormatVersion != 2)
        {
            throw new InvalidDataException("Backup manifest format is unsupported by this application build.");
        }

        if (!await _manifestAuthenticator.VerifyAuthenticationAsync(manifest, envelope.Authentication, cancellationToken))
        {
            throw new InvalidDataException("Backup manifest authentication failed.");
        }

        if (!string.Equals(manifest.FileName, Path.GetFileName(backupPath), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Backup manifest filename does not match the selected backup artifact.");
        }

        if (!string.Equals(manifest.DatabaseName, request.RuntimeConnection.Database, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Backup manifest database identity does not match the configured restore target.");
        }

        if (!string.Equals(manifest.Protection, _protector.ProtectionName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup protection '{manifest.Protection}' is not supported by the active protector.");
        }

        if (new FileInfo(backupPath).Length != manifest.SizeBytes)
        {
            throw new InvalidDataException("Backup file size does not match its manifest.");
        }

        var actualHash = await ComputeSha256Async(backupPath, cancellationToken);
        if (!TryFixedTimeHexEquals(actualHash, manifest.Sha256))
        {
            throw new InvalidDataException("Backup checksum verification failed.");
        }

        var originalOid = await RequireDatabaseOidAsync(maintenanceConnection, request.RuntimeConnection.Database, cancellationToken);
        var restoreId = Guid.NewGuid();
        var tempRoot = CreateOperationTempRoot("restore", restoreId);
        var plainTemp = Path.Combine(tempRoot, "database.dump");
        var stagingDb = PostgresRecoveryDatabaseName.Create("rst", request.RuntimeConnection.Database, restoreId);

        try
        {
            await _protector.UnprotectAsync(backupPath, plainTemp, cancellationToken);
            var list = await _runner.RunAsync(_pgRestore, new[] { "--list", plainTemp }, null, cancellationToken);
            EnsureSuccess("pg_restore --list", list);

            var create = await _runner.RunAsync(_createdb, new[]
            {
                "--no-password", "--host", maintenanceConnection.Host,
                "--port", maintenanceConnection.Port.ToString(),
                "--username", maintenanceConnection.Username,
                "--maintenance-db", maintenanceConnection.MaintenanceDatabase,
                "--template", "template0", stagingDb
            }, PasswordEnvironment(maintenanceConnection), cancellationToken);
            EnsureSuccess("createdb", create);

            try
            {
                var restore = await _runner.RunAsync(_pgRestore, new[]
                {
                    "--no-password", "--exit-on-error", "--verbose",
                    "--host", maintenanceConnection.Host,
                    "--port", maintenanceConnection.Port.ToString(),
                    "--username", maintenanceConnection.Username,
                    "--dbname", stagingDb, plainTemp
                }, PasswordEnvironment(maintenanceConnection), cancellationToken);
                EnsureSuccess("pg_restore", restore);

                var probe = await QueryScalarAsync(maintenanceConnection, stagingDb, "SELECT 1;", cancellationToken);
                if (probe?.Trim() != "1")
                {
                    throw new InvalidDataException("Restored staging database did not pass the SQL readiness probe.");
                }

                await _stagingValidator.ValidateAsync(new RestoreStagingValidationContext(
                    request.RuntimeConnection, maintenanceConnection, stagingDb, manifest, restoreId), cancellationToken);

                var stagingOid = await RequireDatabaseOidAsync(maintenanceConnection, stagingDb, cancellationToken);
                var session = new RestoreSessionRecord(
                    restoreId,
                    request.RuntimeConnection.Database,
                    stagingDb,
                    originalOid,
                    stagingOid,
                    backupPath,
                    actualHash,
                    DateTimeOffset.UtcNow,
                    RestoreSessionState.Prepared);
                await _restoreSessions.CreateAsync(session, cancellationToken);
                await maintenanceLease.SetExitStateAsync(
                    ProductionMaintenanceState.RestorePreparing,
                    CancellationToken.None);
                return new RestoreSessionToken(restoreId);
            }
            catch
            {
                await maintenanceLease.SetExitStateAsync(
                    ProductionMaintenanceState.Normal,
                    CancellationToken.None);
                await DropDatabaseBestEffortAsync(maintenanceConnection, stagingDb, CancellationToken.None);
                throw;
            }
        }
        finally { TryDelete(plainTemp); TryDeleteDirectory(tempRoot); }
    }

    public async Task<RestoreCutoverResult> CutoverAsync(
        PostgresConnectionDescriptor runtimeConnection,
        RestoreSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var currentMaintenanceState = await _maintenanceBarrier.GetStateAsync(cancellationToken);
        if (currentMaintenanceState != ProductionMaintenanceState.RestorePreparing)
        {
            throw new ProductionMaintenanceException(currentMaintenanceState);
        }

        await using var maintenanceLease = await _maintenanceBarrier.EnterExclusiveAsync(
            ProductionMaintenanceState.RestoreCutover,
            cancellationToken);
        await maintenanceLease.SetExitStateAsync(
            ProductionMaintenanceState.RestorePreparing,
            CancellationToken.None);

        var maintenanceConnection = await _maintenanceConnectionProvider.GetAsync(cancellationToken);
        ValidateMaintenanceEndpoint(runtimeConnection, maintenanceConnection);
        var session = await RequireSessionAsync(token, cancellationToken);
        ValidatePreparedSession(runtimeConnection, session);

        cancellationToken.ThrowIfCancellationRequested();
        await VerifySessionDatabaseIdentityAsync(maintenanceConnection, session, cancellationToken);

        var preserved = PostgresRecoveryDatabaseName.Create("pre", runtimeConnection.Database, session.RestoreId);
        var failedRestored = PostgresRecoveryDatabaseName.Create("bad", runtimeConnection.Database, session.RestoreId);
        session = session with
        {
            State = RestoreSessionState.CutoverInProgress,
            PreservedDatabase = preserved,
            FailedRestoredDatabase = failedRestored
        };
        await _restoreSessions.UpdateAsync(session, CancellationToken.None);

        using var safety = new CancellationTokenSource(CutoverSafetyTimeout);
        var safetyToken = safety.Token;
        try
        {
            await SetAllowConnectionsAsync(maintenanceConnection, runtimeConnection.Database, allow: false, safetyToken);
            await SetAllowConnectionsAsync(maintenanceConnection, session.StagingDatabase, allow: false, safetyToken);
            await TerminateConnectionsAsync(maintenanceConnection, runtimeConnection.Database, safetyToken);
            await TerminateConnectionsAsync(maintenanceConnection, session.StagingDatabase, safetyToken);

            await ExecuteSqlAsync(maintenanceConnection, maintenanceConnection.MaintenanceDatabase,
                $"ALTER DATABASE {SqlIdentifier(runtimeConnection.Database)} RENAME TO {SqlIdentifier(preserved)};", safetyToken);
            await ExecuteSqlAsync(maintenanceConnection, maintenanceConnection.MaintenanceDatabase,
                $"ALTER DATABASE {SqlIdentifier(session.StagingDatabase)} RENAME TO {SqlIdentifier(runtimeConnection.Database)};", safetyToken);

            await SetAllowConnectionsAsync(maintenanceConnection, runtimeConnection.Database, allow: true, safetyToken);
            await SetAllowConnectionsAsync(maintenanceConnection, preserved, allow: true, safetyToken);

            var activeOid = await RequireDatabaseOidAsync(maintenanceConnection, runtimeConnection.Database, safetyToken);
            if (activeOid != session.StagingDatabaseOid)
            {
                throw new InvalidDataException("Post-cutover database identity does not match the verified staging database.");
            }

            var preservedOid = await RequireDatabaseOidAsync(maintenanceConnection, preserved, safetyToken);
            if (preservedOid != session.OriginalDatabaseOid)
            {
                throw new InvalidDataException("Preserved pre-restore database identity does not match the original production database.");
            }

            var finalProbe = await QueryScalarAsync(maintenanceConnection, runtimeConnection.Database, "SELECT 1;", safetyToken);
            if (finalProbe?.Trim() != "1")
            {
                throw new InvalidDataException("Restored production database failed the post-cutover SQL readiness probe.");
            }

            var completedAt = DateTimeOffset.UtcNow;
            session = session with { State = RestoreSessionState.Completed, CompletedAtUtc = completedAt };
            await _restoreSessions.UpdateAsync(session, CancellationToken.None);
            await maintenanceLease.SetExitStateAsync(
                ProductionMaintenanceState.Normal,
                CancellationToken.None);
            return new RestoreCutoverResult(session.RestoreId, runtimeConnection.Database, preserved, completedAt);
        }
        catch (Exception cutoverError)
        {
            try
            {
                using var rollbackSafety = new CancellationTokenSource(CutoverSafetyTimeout);
                await RollBackByDatabaseOidAsync(maintenanceConnection, runtimeConnection.Database, session, rollbackSafety.Token);
                session = session with { State = RestoreSessionState.RolledBack };
                await _restoreSessions.UpdateAsync(session, CancellationToken.None);
                await maintenanceLease.SetExitStateAsync(
                    ProductionMaintenanceState.Normal,
                    CancellationToken.None);
            }
            catch (Exception rollbackError)
            {
                session = session with { State = RestoreSessionState.RecoveryRequired };
                try { await _restoreSessions.UpdateAsync(session, CancellationToken.None); } catch { }
                await maintenanceLease.SetExitStateAsync(ProductionMaintenanceState.RecoveryRequired, CancellationToken.None);
                throw new RestoreRecoveryRequiredException(
                    "Restore cutover failed and automatic rollback could not be verified. Edge Retails must remain in recovery mode until database identity is reconciled.",
                    new AggregateException(cutoverError, rollbackError));
            }
            throw;
        }
    }

    public async Task DiscardPreparedRestoreAsync(
        PostgresConnectionDescriptor runtimeConnection,
        RestoreSessionToken token,
        CancellationToken cancellationToken = default)
    {
        var priorMaintenanceState = await _maintenanceBarrier.GetStateAsync(cancellationToken);
        if (priorMaintenanceState is not (ProductionMaintenanceState.RestorePreparing or ProductionMaintenanceState.Normal))
        {
            throw new ProductionMaintenanceException(priorMaintenanceState);
        }

        await using var maintenanceLease = await _maintenanceBarrier.EnterExclusiveAsync(
            ProductionMaintenanceState.RestorePreparing,
            cancellationToken);
        await maintenanceLease.SetExitStateAsync(priorMaintenanceState, CancellationToken.None);

        var maintenanceConnection = await _maintenanceConnectionProvider.GetAsync(cancellationToken);
        ValidateMaintenanceEndpoint(runtimeConnection, maintenanceConnection);
        var session = await RequireSessionAsync(token, cancellationToken);

        if (!string.Equals(runtimeConnection.Database, session.TargetDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Restore session target does not match the configured production database.");
        }

        if (session.State is not (RestoreSessionState.Prepared or RestoreSessionState.RolledBack))
        {
            throw new InvalidOperationException($"Restore session in state '{session.State}' cannot be discarded.");
        }

        if (session.State == RestoreSessionState.Prepared &&
            priorMaintenanceState != ProductionMaintenanceState.RestorePreparing)
        {
            throw new InvalidOperationException("Prepared restore authority is inconsistent with the production maintenance state.");
        }

        var currentStagingName = await QueryDatabaseNameByOidAsync(
            maintenanceConnection,
            session.StagingDatabaseOid,
            cancellationToken);

        if (currentStagingName is not null &&
            string.Equals(currentStagingName, runtimeConnection.Database, StringComparison.Ordinal))
        {
            session = session with { State = RestoreSessionState.RecoveryRequired };
            await _restoreSessions.UpdateAsync(session, CancellationToken.None);
            await maintenanceLease.SetExitStateAsync(
                ProductionMaintenanceState.RecoveryRequired,
                CancellationToken.None);

            throw new RestoreRecoveryRequiredException(
                "Restore discard detected that the expected staging database OID resolves to the active production database. Destructive cleanup was refused and recovery is required.",
                new InvalidOperationException("Staging database identity collides with the active production database."));
        }

        // Revoke cutover authority before destructive cleanup. If the subsequent staging drop fails,
        // the stale prepared restore cannot later be used for cutover.
        session = session with { State = RestoreSessionState.Discarded };
        await _restoreSessions.UpdateAsync(session, CancellationToken.None);

        if (currentStagingName is not null)
        {
            await DropDatabaseAsync(maintenanceConnection, currentStagingName, cancellationToken);
        }

        await maintenanceLease.SetExitStateAsync(
            ProductionMaintenanceState.Normal,
            CancellationToken.None);
    }

    private async Task<RestoreSessionRecord> RequireSessionAsync(RestoreSessionToken token, CancellationToken cancellationToken)
        => await _restoreSessions.GetAsync(token.RestoreId, cancellationToken)
            ?? throw new InvalidOperationException("Restore session is unknown or no longer available.");

    private static void ValidatePreparedSession(PostgresConnectionDescriptor runtimeConnection, RestoreSessionRecord session)
    {
        if (!string.Equals(runtimeConnection.Database, session.TargetDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Restore session target does not match the configured production database.");
        }

        if (session.State != RestoreSessionState.Prepared)
        {
            throw new InvalidOperationException($"Restore session in state '{session.State}' cannot be cut over.");
        }
    }

    private async Task VerifySessionDatabaseIdentityAsync(PostgresMaintenanceDescriptor maintenance, RestoreSessionRecord session, CancellationToken cancellationToken)
    {
        var originalName = await QueryDatabaseNameByOidAsync(maintenance, session.OriginalDatabaseOid, cancellationToken);
        var stagingName = await QueryDatabaseNameByOidAsync(maintenance, session.StagingDatabaseOid, cancellationToken);
        if (!string.Equals(originalName, session.TargetDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Production database identity changed after restore preparation; cutover is refused.");
        }

        if (!string.Equals(stagingName, session.StagingDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Staging database identity changed after restore preparation; cutover is refused.");
        }
    }

    private async Task RollBackByDatabaseOidAsync(
        PostgresMaintenanceDescriptor maintenance,
        string targetDatabase,
        RestoreSessionRecord session,
        CancellationToken cancellationToken)
    {
        var originalName = await QueryDatabaseNameByOidAsync(maintenance, session.OriginalDatabaseOid, cancellationToken)
            ?? throw new InvalidOperationException("Original pre-restore database OID can no longer be found.");
        var restoredName = await QueryDatabaseNameByOidAsync(maintenance, session.StagingDatabaseOid, cancellationToken);
        var failedName = session.FailedRestoredDatabase ?? PostgresRecoveryDatabaseName.Create("bad", targetDatabase, session.RestoreId);

        if (restoredName is not null && string.Equals(restoredName, targetDatabase, StringComparison.Ordinal))
        {
            await SetAllowConnectionsAsync(maintenance, targetDatabase, allow: false, cancellationToken);
            await TerminateConnectionsAsync(maintenance, targetDatabase, cancellationToken);
            await ExecuteSqlAsync(maintenance, maintenance.MaintenanceDatabase,
                $"ALTER DATABASE {SqlIdentifier(targetDatabase)} RENAME TO {SqlIdentifier(failedName)};", cancellationToken);
        }

        originalName = await QueryDatabaseNameByOidAsync(maintenance, session.OriginalDatabaseOid, cancellationToken)
            ?? throw new InvalidOperationException("Original pre-restore database disappeared during rollback.");
        if (!string.Equals(originalName, targetDatabase, StringComparison.Ordinal))
        {
            if (await DatabaseExistsAsync(maintenance, targetDatabase, cancellationToken))
            {
                throw new InvalidOperationException("Rollback target database name is occupied by an unexpected database.");
            }

            await TerminateConnectionsAsync(maintenance, originalName, cancellationToken);
            await ExecuteSqlAsync(maintenance, maintenance.MaintenanceDatabase,
                $"ALTER DATABASE {SqlIdentifier(originalName)} RENAME TO {SqlIdentifier(targetDatabase)};", cancellationToken);
        }

        await SetAllowConnectionsAsync(maintenance, targetDatabase, allow: true, cancellationToken);
        var verifiedOid = await RequireDatabaseOidAsync(maintenance, targetDatabase, cancellationToken);
        if (verifiedOid != session.OriginalDatabaseOid)
        {
            throw new InvalidOperationException("Rollback completed by name but original production database identity was not restored.");
        }

        var probe = await QueryScalarAsync(maintenance, targetDatabase, "SELECT 1;", cancellationToken);
        if (probe?.Trim() != "1")
        {
            throw new InvalidOperationException("Rolled-back production database failed readiness verification.");
        }
    }

    private async Task<uint> RequireDatabaseOidAsync(PostgresMaintenanceDescriptor connection, string database, CancellationToken cancellationToken)
    {
        var raw = await QueryScalarAsync(connection, connection.MaintenanceDatabase,
            $"SELECT oid::text FROM pg_database WHERE datname = {SqlLiteral(database)};", cancellationToken);
        if (!uint.TryParse(raw?.Trim(), out var oid) || oid == 0)
        {
            throw new InvalidOperationException($"Required PostgreSQL database '{database}' was not found.");
        }

        return oid;
    }

    private async Task<string?> QueryDatabaseNameByOidAsync(PostgresMaintenanceDescriptor connection, uint oid, CancellationToken cancellationToken)
    {
        var raw = await QueryScalarAsync(connection, connection.MaintenanceDatabase,
            $"SELECT datname FROM pg_database WHERE oid = {oid};", cancellationToken);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private async Task<bool> DatabaseExistsAsync(PostgresMaintenanceDescriptor connection, string database, CancellationToken cancellationToken)
    {
        var raw = await QueryScalarAsync(connection, connection.MaintenanceDatabase,
            $"SELECT CASE WHEN EXISTS(SELECT 1 FROM pg_database WHERE datname = {SqlLiteral(database)}) THEN '1' ELSE '0' END;", cancellationToken);
        return raw?.Trim() == "1";
    }

    private async Task SetAllowConnectionsAsync(PostgresMaintenanceDescriptor connection, string database, bool allow, CancellationToken cancellationToken)
        => await ExecuteSqlAsync(connection, connection.MaintenanceDatabase,
            $"ALTER DATABASE {SqlIdentifier(database)} ALLOW_CONNECTIONS {(allow ? "true" : "false")};", cancellationToken);

    private async Task DropDatabaseAsync(PostgresMaintenanceDescriptor connection, string database, CancellationToken cancellationToken)
    {
        await SetAllowConnectionsBestEffortAsync(connection, database, allow: false, cancellationToken);
        await TerminateConnectionsAsync(connection, database, cancellationToken);
        await ExecuteSqlAsync(connection, connection.MaintenanceDatabase, $"DROP DATABASE {SqlIdentifier(database)};", cancellationToken);
    }

    private async Task DropDatabaseBestEffortAsync(PostgresMaintenanceDescriptor connection, string database, CancellationToken cancellationToken)
    {
        try { await DropDatabaseAsync(connection, database, cancellationToken); } catch { }
    }

    private async Task TerminateConnectionsAsync(PostgresMaintenanceDescriptor connection, string database, CancellationToken cancellationToken)
        => await ExecuteSqlAsync(connection, connection.MaintenanceDatabase,
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = {SqlLiteral(database)} AND pid <> pg_backend_pid();", cancellationToken);

    private async Task SetAllowConnectionsBestEffortAsync(PostgresMaintenanceDescriptor connection, string database, bool allow, CancellationToken cancellationToken)
    {
        try { await SetAllowConnectionsAsync(connection, database, allow, cancellationToken); } catch { }
    }

    private async Task<string?> QueryScalarAsync(PostgresConnectionDescriptor connection, string database, string sql, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_psql, new[]
        {
            "--no-password", "--tuples-only", "--no-align", "--quiet",
            "--host", connection.Host, "--port", connection.Port.ToString(),
            "--username", connection.Username, "--dbname", database, "--command", sql
        }, PasswordEnvironment(connection), cancellationToken);
        EnsureSuccess("psql", result);
        return result.StandardOutput.Trim();
    }

    private async Task<string?> QueryScalarAsync(PostgresMaintenanceDescriptor connection, string database, string sql, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(_psql, new[]
        {
            "--no-password", "--tuples-only", "--no-align", "--quiet",
            "--host", connection.Host, "--port", connection.Port.ToString(),
            "--username", connection.Username, "--dbname", database, "--command", sql
        }, PasswordEnvironment(connection), cancellationToken);
        EnsureSuccess("psql", result);
        return result.StandardOutput.Trim();
    }

    private async Task ExecuteSqlAsync(PostgresMaintenanceDescriptor connection, string database, string sql, CancellationToken cancellationToken)
        => _ = await QueryScalarAsync(connection, database, sql, cancellationToken);

    private async Task<string> ReadToolVersionAsync(string tool, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(tool, new[] { "--version" }, null, cancellationToken);
        EnsureSuccess(Path.GetFileName(tool), result);
        return result.StandardOutput.Trim();
    }

    private static IReadOnlyDictionary<string, string?> PasswordEnvironment(PostgresConnectionDescriptor connection)
        => new Dictionary<string, string?> { ["PGPASSWORD"] = connection.Password.Reveal() };

    private static IReadOnlyDictionary<string, string?> PasswordEnvironment(PostgresMaintenanceDescriptor connection)
        => new Dictionary<string, string?> { ["PGPASSWORD"] = connection.Password.Reveal() };

    private static void ValidateMaintenanceEndpoint(PostgresConnectionDescriptor runtime, PostgresMaintenanceDescriptor maintenance)
    {
        if (!string.Equals(runtime.Host, maintenance.Host, StringComparison.OrdinalIgnoreCase) || runtime.Port != maintenance.Port)
        {
            throw new InvalidOperationException("Runtime and recovery identities must point to the same PostgreSQL server endpoint.");
        }

        if (string.IsNullOrWhiteSpace(maintenance.Username))
        {
            throw new InvalidOperationException("Recovery identity is not configured.");
        }

        if (string.Equals(runtime.Username, maintenance.Username, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery identity must be distinct from the least-privilege runtime PostgreSQL identity.");
        }

        if (string.Equals(runtime.Database, maintenance.MaintenanceDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Recovery maintenance connection must not connect to the production database being renamed.");
        }
    }

    private static async Task<BackupManifestEnvelope> ReadManifestEnvelopeAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Backup manifest size is invalid.");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var json = await reader.ReadToEndAsync(cancellationToken);
            return JsonSerializer.Deserialize<BackupManifestEnvelope>(json, JsonOptions)
                ?? throw new InvalidDataException("Backup manifest is invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Backup manifest JSON is invalid.", ex);
        }
    }

    private static void RejectReparsePoint(string path, string artifactName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{artifactName} cannot be a reparse point.");
        }
    }

    private static bool IsSha256Hex(string value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool TryFixedTimeHexEquals(string left, string right)
    {
        if (!IsSha256Hex(left) || !IsSha256Hex(right))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException) { return false; }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            Array.Clear(bytes, 0, bytes.Length);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void EnsureSuccess(string operation, ProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var error = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        throw new InvalidOperationException($"{operation} failed with exit code {result.ExitCode}: {error}");
    }

    private static string RequireExecutable(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Executable path is required.", name);
        }

        if (!File.Exists(value))
        {
            throw new FileNotFoundException($"Required PostgreSQL executable was not found: {value}", value);
        }

        return value;
    }

    private static string SafeFilePart(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    private static string SqlIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''") + "'";
    private static void TryDelete(string path) { try { if (File.Exists(path)) { File.Delete(path); } } catch { } }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static string CreateOperationTempRoot(string kind, Guid operationId)
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetails", "ProtectedPostgresTemp", $"{kind}_{operationId:N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } } catch { }
    }

}
