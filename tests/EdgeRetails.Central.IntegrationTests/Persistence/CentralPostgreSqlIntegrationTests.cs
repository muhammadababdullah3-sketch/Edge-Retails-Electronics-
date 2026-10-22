using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Operations;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.IntegrationTests.Persistence;

public class CentralPostgreSqlIntegrationTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly DatabaseTargetGuard _guard = new();
    private readonly CanonicalJsonSerializer _serializer = new();

    public CentralPostgreSqlIntegrationTests()
    {
        var connectionString = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Explicit owned CENTRAL_DATABASE_CONNECTION_STRING required.");

        _guard.ValidateConnectionString(connectionString);

        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema);
            })
            .Options;

        _dbContext = new CentralDbContext(options, _guard);
    }

    [Fact]
    public void TargetGuard_StrictlyRejectsForbiddenPorts()
    {
        // Must reject default PostgreSQL port 5432
        var ex5432 = Assert.Throws<DatabaseTargetGuardException>(() =>
            _guard.ValidateConnectionString("Host=127.0.0.1;Port=5432;Database=edgeretails_central_test;Username=central_test_admin"));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex5432.ViolationCode);

        // Must reject POS port 7150
        var ex7150 = Assert.Throws<DatabaseTargetGuardException>(() =>
            _guard.ValidateConnectionString("Host=127.0.0.1;Port=7150;Database=edgeretails_central_test;Username=central_test_admin"));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex7150.ViolationCode);
    }

    [Fact]
    public async Task PostgreSql_SchemaIsolation_ProvesOnlyCentralTablesExist_ZeroShopTables()
    {
        // Query pg_tables to inspect all non-system tables in PostgreSQL 18
        using var connection = _dbContext.Database.GetDbConnection();
        await _dbContext.Database.OpenConnectionAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT schemaname, tablename FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema');";

        var tables = new List<(string Schema, string Table)>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        // 1. All non-system tables must belong exclusively to 'central' schema
        Assert.All(tables, t => Assert.Equal("central", t.Schema));

        // 2. Exact expected central tables must be present
        var tableNames = tables.Select(t => t.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("__ef_migrations_history", tableNames);
        Assert.Contains("admin_users", tableNames);
        Assert.Contains("admin_sessions", tableNames);
        Assert.Contains("admin_roles", tableNames);
        Assert.Contains("admin_user_roles", tableNames);
        Assert.Contains("login_challenges", tableNames);
        Assert.Contains("step_up_grants", tableNames);
        Assert.Contains("operation_records", tableNames);
        Assert.Contains("audit_records", tableNames);
        Assert.Contains("outbox_messages", tableNames);
        Assert.Contains("signing_keys", tableNames);
        Assert.Contains("signing_requests", tableNames);
        Assert.Contains("trust_sets", tableNames);

        Assert.Contains("outbox_consumer_effects", tableNames);
        Assert.Contains("admin_notifications", tableNames);
        // Includes durable consumer receipt/effect table and Wave 2 commercial/licensing tables
        Assert.Contains("customers", tableNames);
        Assert.Contains("shops", tableNames);
        Assert.Contains("plan_catalog_items", tableNames);
        Assert.Contains("pricing_snapshots", tableNames);
        Assert.Contains("orders", tableNames);
        Assert.Contains("payment_evidences", tableNames);
        Assert.Contains("payment_review_records", tableNames);
        Assert.Contains("upload_sessions", tableNames);
        Assert.Contains("commercial_licenses", tableNames);
        Assert.Contains("commercial_entitlements", tableNames);
        Assert.Contains("installations", tableNames);
        Assert.Contains("activation_tokens", tableNames);
        Assert.Contains("bootstrap_challenges", tableNames);
        Assert.Contains("installation_request_nonces", tableNames);
        Assert.Contains("activation_redemptions", tableNames);
        Assert.Contains("license_certificates", tableNames);
        Assert.Contains("hardware_reviews", tableNames);
        Assert.Contains("primary_transfers", tableNames);
        Assert.Contains("sensitive_approval_requests", tableNames);
        Assert.Contains("shop_enforcement_policies", tableNames);
        Assert.Contains("encrypted_objects", tableNames);
        Assert.Contains("object_download_grants", tableNames);
        Assert.Contains("support_tickets", tableNames);
        Assert.Equal(38, tableNames.Count);

        // 4. Shop domain tables must be completely absent
        Assert.DoesNotContain("sales", tableNames);
        Assert.DoesNotContain("sales_lines", tableNames);
        Assert.DoesNotContain("stock", tableNames);
        Assert.DoesNotContain("inventory", tableNames);
        Assert.DoesNotContain("expenses", tableNames);
        Assert.DoesNotContain("cash_sessions", tableNames);
    }

    [Fact]
    public async Task PostgreSql_AppendOnlyAudit_BlocksUpdateAndDelete()
    {
        var now = DateTimeOffset.UtcNow;
        var auditRecord = CentralAuditRecord.Create(
            actorId: "admin:test_isolation",
            actorUsername: "admin",
            sessionId: Guid.NewGuid(),
            action: "SECURITY_TEST",
            resource: "TestEntity",
            resourceId: "123",
            reason: "Test reason",
            correlationId: Guid.NewGuid().ToString(),
            clientIp: null,
            userAgent: null,
            payloadSha256: null,
            detailsJson: "{}",
            isSuccess: true,
            timestampUtc: now);

        _dbContext.CentralAuditRecords.Add(auditRecord);
        await _dbContext.SaveChangesAsync();

        // Attempting to modify audit entry in EF Core change tracker must throw
        var entry = _dbContext.Entry(auditRecord);
        entry.State = EntityState.Modified;

        var updateEx = await Assert.ThrowsAsync<InvalidOperationException>(() => _dbContext.SaveChangesAsync());
        Assert.Contains("strictly append-only", updateEx.Message);

        // Detach and try remove
        entry.State = EntityState.Detached;
        _dbContext.CentralAuditRecords.Attach(auditRecord);
        _dbContext.CentralAuditRecords.Remove(auditRecord);

        var deleteEx = await Assert.ThrowsAsync<InvalidOperationException>(() => _dbContext.SaveChangesAsync());
        Assert.Contains("strictly append-only", deleteEx.Message);
    }

    [Fact]
    public async Task PostgreSql_TransactionalAtomicity_GenuineW1Command_AllOrNothing()
    {
        var ledger = new OperationLedgerService(_dbContext, _serializer);
        var auditService = new CentralAuditService(_dbContext);
        var now = DateTimeOffset.UtcNow;

        // Create test user and role
        var testUser = AdminUser.Create($"admin_{Guid.NewGuid():N}", "hash", "salt", "secret", true, now);
        var testRole = AdminRole.Create($"Role_{Guid.NewGuid():N}", "Test role description", new[] { AdminCapabilities.AdminManage });

        _dbContext.AdminUsers.Add(testUser);
        _dbContext.AdminRoles.Add(testRole);
        await _dbContext.SaveChangesAsync();

        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = testUser.Id,
            Action = "ASSIGN_ROLE",
            RoleId = testRole.Id
        };

        var (_, sha256) = _serializer.SerializeAndHash(command);
        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256,
            Reason = "Integration test role assignment",
            ExpectedVersion = testUser.RowVersion,
            PolicyVersion = 1
        };

        var actorScope = "admin:test_atomicity";

        // Execute genuine command: domain mutation + audit + outbox + operation outcome
        var outcome = await ledger.ExecuteOrReplayAsync(
            actorScope: actorScope,
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: async cmd =>
            {
                var user = await _dbContext.AdminUsers.FirstAsync(u => u.Id == cmd.AdminUserId);
                var role = await _dbContext.AdminRoles.FirstAsync(r => r.Id == cmd.RoleId!.Value);

                var userRole = new AdminUserRole(user.Id, role.Id, DateTimeOffset.UtcNow);
                _dbContext.AdminUserRoles.Add(userRole);
                user.BumpSecurityRevision();

                await auditService.RecordAuditAsync(
                    eventType: "admin.assign_role",
                    actorScope: actorScope,
                    adminUserId: user.Id,
                    sessionId: Guid.NewGuid(),
                    reason: meta.Reason,
                    correlationId: opId,
                    clientOperationId: opId,
                    entityName: "AdminUser",
                    entityId: user.Id.ToString(),
                    detailsJson: _serializer.SerializeCanonical(cmd));

                var outboxMsg = CentralOutboxMessage.Create(
                    messageId: Guid.NewGuid(),
                    eventType: "AdminRoleAssigned",
                    aggregateType: "AdminUser",
                    aggregateId: user.Id.ToString(),
                    payloadJson: _serializer.SerializeCanonical(cmd),
                    occurredOnUtc: DateTimeOffset.UtcNow);

                _dbContext.CentralOutboxMessages.Add(outboxMsg);

                return (true, $"admin_users/{user.Id}", user.RowVersion, user.SecurityRevision);
            });

        Assert.Equal("SUCCEEDED", outcome.State);

        // Verify all 4 artifacts are persisted in PostgreSQL
        var userRoleInDb = await _dbContext.AdminUserRoles.FirstOrDefaultAsync(ur => ur.AdminUserId == testUser.Id && ur.RoleId == testRole.Id);
        Assert.NotNull(userRoleInDb);

        var opRecordInDb = await _dbContext.CentralOperationRecords.FirstOrDefaultAsync(r => r.ClientOperationId == opId);
        Assert.NotNull(opRecordInDb);
        Assert.Equal(OperationState.SUCCEEDED, opRecordInDb.State);

        var auditInDb = await _dbContext.CentralAuditRecords.FirstOrDefaultAsync(a => a.CorrelationId == opId.ToString());
        Assert.NotNull(auditInDb);
        Assert.Equal("admin.assign_role", auditInDb.Action);

        var outboxInDb = await _dbContext.CentralOutboxMessages.FirstOrDefaultAsync(o => o.PayloadJson.Contains(testUser.Id.ToString()));
        Assert.NotNull(outboxInDb);

        // Now test rollback on error:
        var failedOpId = Guid.NewGuid();
        var failedCommand = new AdminActionCommandDto
        {
            AdminUserId = testUser.Id,
            Action = "ASSIGN_ROLE",
            RoleId = testRole.Id
        };
        var (_, failedSha256) = _serializer.SerializeAndHash(failedCommand);
        var failedMeta = new CommandMetaDto
        {
            ClientOperationId = failedOpId,
            OperationType = "admin.action",
            PayloadSha256 = failedSha256,
            Reason = "Rollback test",
            ExpectedVersion = testUser.RowVersion,
            PolicyVersion = 1
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            ledger.ExecuteOrReplayAsync<AdminActionCommandDto, bool>(
                actorScope: actorScope,
                shopId: null,
                meta: failedMeta,
                payload: failedCommand,
                executeAsync: _ => throw new ApplicationException("Simulated mid-transaction failure.")));

        Assert.Equal("OPERATION_EXECUTION_FAILED", ex.Code);

        // Verify that failed operation record was NOT committed
        var failedOpInDb = await _dbContext.CentralOperationRecords.FirstOrDefaultAsync(r => r.ClientOperationId == failedOpId);
        Assert.Null(failedOpInDb);
    }

    [Fact]
    public async Task PostgreSql_IdempotentReplay_And_ConflictDetection()
    {
        var ledger = new OperationLedgerService(_dbContext, _serializer);
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create($"admin_{Guid.NewGuid():N}", "hash", "salt", "secret", true, now);
        _dbContext.AdminUsers.Add(user);
        await _dbContext.SaveChangesAsync();

        var opId = Guid.NewGuid();
        var command1 = new AdminActionCommandDto
        {
            AdminUserId = user.Id,
            Action = "DISABLE"
        };
        var (_, sha256_1) = _serializer.SerializeAndHash(command1);
        var meta1 = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256_1,
            Reason = "First run",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var actorScope = "admin:test_idempotency";
        var execCount = 0;

        var firstOutcome = await ledger.ExecuteOrReplayAsync(
            actorScope: actorScope,
            shopId: null,
            meta: meta1,
            payload: command1,
            executeAsync: _ =>
            {
                execCount++;
                return Task.FromResult<(bool, string?, int, int)>((true, "ref1", 2, 1));
            });

        Assert.Equal(1, execCount);
        Assert.Equal("SUCCEEDED", firstOutcome.State);

        // Replay with identical parameters
        var replayOutcome = await ledger.ExecuteOrReplayAsync(
            actorScope: actorScope,
            shopId: null,
            meta: meta1,
            payload: command1,
            executeAsync: _ =>
            {
                execCount++;
                return Task.FromResult<(bool, string?, int, int)>((true, "ref_WRONG", 99, 99));
            });

        Assert.Equal(1, execCount); // Replay did not re-execute logic
        Assert.Equal(firstOutcome.OutcomeId, replayOutcome.OutcomeId);
        Assert.Equal(firstOutcome.ResultReference, replayOutcome.ResultReference);

        // Attempt execution with SAME ClientOperationId but DIFFERENT payload
        var command2 = new AdminActionCommandDto
        {
            AdminUserId = user.Id,
            Action = "ASSIGN_ROLE",
            RoleId = Guid.NewGuid()
        };
        var (_, sha256_2) = _serializer.SerializeAndHash(command2);
        var meta2 = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256_2,
            Reason = "Conflict run",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var conflictEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            ledger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: meta2,
                payload: command2,
                executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref2", 3, 1))));

        Assert.Equal("OPERATION_ID_CONFLICT", conflictEx.Code);
    }

    [Fact]
    public async Task PostgreSql_TargetGuard_VerifiesOwnedServerIdentityAndMarker()
    {
        var markerPath = Path.Combine(
            AppContext.BaseDirectory,
            "../../../../test-artifacts/disposable-pg18/data/owned_data_dir_marker.txt");

        if (File.Exists(markerPath))
        {
            _guard.VerifyDataDirMarker(markerPath);
        }

        using var connection = _dbContext.Database.GetDbConnection();
        await _guard.VerifyServerIdentityAsync(
            connection,
            expectedDatabase: DatabaseTargetGuard.Required("CENTRAL_PG_DATABASE"),
            expectedUser: DatabaseTargetGuard.Required("CENTRAL_PG_ROLE"));
    }

    [Fact]
    public async Task PostgreSql_DbConcurrency_SimultaneousModifications_OneWinnerOneConflict()
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create($"admin_{Guid.NewGuid():N}", "hash", "salt", "secret", true, now);
        _dbContext.AdminUsers.Add(user);
        await _dbContext.SaveChangesAsync();

        var initialVersion = user.RowVersion;

        // Context 1 and Context 2 both load the same initial user
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(
                Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING") ?? throw new InvalidOperationException("Explicit owned CENTRAL_DATABASE_CONNECTION_STRING required."),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema))
            .Options;

        using var context1 = new CentralDbContext(options, _guard);
        using var context2 = new CentralDbContext(options, _guard);

        var user1 = await context1.AdminUsers.FirstAsync(u => u.Id == user.Id);
        var user2 = await context2.AdminUsers.FirstAsync(u => u.Id == user.Id);

        Assert.Equal(initialVersion, user1.RowVersion);
        Assert.Equal(initialVersion, user2.RowVersion);

        // Transaction 1 modifies and commits
        user1.BumpSecurityRevision();
        await context1.SaveChangesAsync();

        // Transaction 2 attempts to modify using stale RowVersion
        user2.BumpSecurityRevision();
        var concEx = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());
        Assert.NotNull(concEx);

        // Verify database holds exactly one winner's modification
        using var verifyContext = new CentralDbContext(options, _guard);
        var finalUser = await verifyContext.AdminUsers.FirstAsync(u => u.Id == user.Id);
        Assert.Equal(initialVersion + 1, finalUser.RowVersion);
    }

    [Fact]
    public async Task PostgreSql_ConcurrentIdenticalRequests_ReconcilesToSameCanonicalOutcome()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(
                Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING") ?? throw new InvalidOperationException("Explicit owned CENTRAL_DATABASE_CONNECTION_STRING required."),
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema))
            .Options;

        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = Guid.NewGuid(),
            Action = "DISABLE"
        };
        var (_, sha256) = _serializer.SerializeAndHash(command);
        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.user.action",
            PayloadSha256 = sha256,
            Reason = "Concurrent race test",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var actorScope = "admin:concurrent_test";
        var executionCounter = 0;

        // Launch two parallel tasks calling ExecuteOrReplayAsync simultaneously with the exact same opId
        var task1 = Task.Run(async () =>
        {
            using var ctx = new CentralDbContext(options, _guard);
            var ledger = new OperationLedgerService(ctx, _serializer);
            return await ledger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: meta,
                payload: command,
                executeAsync: _ =>
                {
                    Interlocked.Increment(ref executionCounter);
                    return Task.FromResult<(bool, string?, int, int)>((true, "ref/winner", 2, 1));
                },
                targetResource: "ref/winner");
        });

        var task2 = Task.Run(async () =>
        {
            using var ctx = new CentralDbContext(options, _guard);
            var ledger = new OperationLedgerService(ctx, _serializer);
            return await ledger.ExecuteOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: meta,
                payload: command,
                executeAsync: _ =>
                {
                    Interlocked.Increment(ref executionCounter);
                    return Task.FromResult<(bool, string?, int, int)>((true, "ref/winner", 2, 1));
                },
                targetResource: "ref/winner");
        });

        var results = await Task.WhenAll(task1, task2);

        // Exactly one business logic execution occurred
        Assert.Equal(1, executionCounter);

        // Both callers received the exact same canonical outcome
        Assert.Equal(results[0].OutcomeId, results[1].OutcomeId);
        Assert.Equal("SUCCEEDED", results[0].State);
        Assert.Equal("SUCCEEDED", results[1].State);
        Assert.Equal("ref/winner", results[0].ResultReference);
        Assert.Equal("ref/winner", results[1].ResultReference);
    }

    [Fact]
    public async Task PostgreSql_OutboxDispatcher_DeliversToSinkWithDeduplication()
    {
        // A PostgreSQL integration test must use the durable consumer receipt. The
        // in-memory sink let this dispatcher mark database rows DISPATCHED without
        // persisting their consumer effects, contaminating later recovery checks.
        var sink = new DurableOutboxConsumerSink(_dbContext);
        var dispatcher = new OutboxDispatcher(_dbContext, sink);

        var msgId = Guid.NewGuid();
        var outboxMsg = CentralOutboxMessage.Create(
            messageId: msgId,
            eventType: "TestDeliveryEvent",
            aggregateType: "TestAggregate",
            aggregateId: "agg-1",
            payloadJson: "{\"test\": true}",
            occurredOnUtc: DateTimeOffset.UtcNow);

        _dbContext.CentralOutboxMessages.Add(outboxMsg);
        await _dbContext.SaveChangesAsync();

        // Dispatch pending
        var dispatchedCount = 0;
        while (!sink.HasProcessed(msgId))
        {
            var count = await dispatcher.DispatchPendingMessagesAsync(100);
            if (count == 0)
            {
                break;
            }
            dispatchedCount += count;
        }
        Assert.True(dispatchedCount >= 1);
        Assert.True(sink.HasProcessed(msgId));
        Assert.Equal(1, sink.GetDeliveryCount(msgId));

        // Re-dispatching does not cause duplicate delivery in sink
        await sink.DeliverAsync(msgId, "TestDeliveryEvent", "TestAggregate", "agg-1", "{\"test\": true}");
        Assert.Equal(1, sink.GetDeliveryCount(msgId));
        Assert.Single(sink.DeliveredMessageIds, id => id == msgId);
    }

    [Fact]
    public async Task PostgreSql_RuntimeUser_DirectSqlAuditDenial_ProvesAppendOnlyAtDatabaseBoundary()
    {
        var runtimeConnStr = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Explicit owned CENTRAL_DATABASE_CONNECTION_STRING required.");

        using var conn = new Npgsql.NpgsqlConnection(runtimeConnStr);
        await conn.OpenAsync();

        var recordId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // 1. Direct SQL INSERT as runtime user must succeed
        using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.CommandText = @"
                INSERT INTO central.audit_records (
                    ""Id"", ""ActorId"", ""ActorUsername"", ""SessionId"", ""Action"",
                    ""Resource"", ""ResourceId"", ""Reason"", ""CorrelationId"", ""DetailsJson"", ""IsSuccess"", ""TimestampUtc""
                ) VALUES (
                    @id, 'admin:runtime_test', 'runtime_admin', @sessId, 'AUDIT_TEST',
                    'TestEntity', '100', 'Direct SQL test', @corrId, '{}', true, @now
                );";
            insertCmd.Parameters.AddWithValue("id", recordId);
            insertCmd.Parameters.AddWithValue("sessId", Guid.NewGuid());
            insertCmd.Parameters.AddWithValue("corrId", Guid.NewGuid().ToString());
            insertCmd.Parameters.AddWithValue("now", now);

            var rows = await insertCmd.ExecuteNonQueryAsync();
            Assert.Equal(1, rows);
        }

        // 2. Direct SQL UPDATE as runtime user must be rejected at DB boundary
        using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.CommandText = @"UPDATE central.audit_records SET ""Reason"" = 'TAMPERED' WHERE ""Id"" = @id;";
            updateCmd.Parameters.AddWithValue("id", recordId);

            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => updateCmd.ExecuteNonQueryAsync());
            Assert.True(
                ex.SqlState == "42501" || // permission denied
                ex.Message.Contains("AUDIT_IMMUTABILITY_VIOLATION") ||
                ex.Message.Contains("permission denied"),
                $"Expected permission denial or trigger exception, got: {ex.Message}");
        }

        // 3. Direct SQL DELETE as runtime user must be rejected at DB boundary
        using (var deleteCmd = conn.CreateCommand())
        {
            deleteCmd.CommandText = @"DELETE FROM central.audit_records WHERE ""Id"" = @id;";
            deleteCmd.Parameters.AddWithValue("id", recordId);

            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => deleteCmd.ExecuteNonQueryAsync());
            Assert.True(
                ex.SqlState == "42501" ||
                ex.Message.Contains("AUDIT_IMMUTABILITY_VIOLATION") ||
                ex.Message.Contains("permission denied"),
                $"Expected permission denial or trigger exception, got: {ex.Message}");
        }

        // 4. Even as admin user, direct SQL UPDATE must be rejected by trigger
        var migrationConnStr = Environment.GetEnvironmentVariable("CENTRAL_MIGRATION_CONNECTION_STRING") ?? runtimeConnStr;
        using var adminConn = new Npgsql.NpgsqlConnection(migrationConnStr);
        await adminConn.OpenAsync();

        using (var adminUpdateCmd = adminConn.CreateCommand())
        {
            adminUpdateCmd.CommandText = @"UPDATE central.audit_records SET ""Reason"" = 'ADMIN_TAMPER' WHERE ""Id"" = @id;";
            var p = adminUpdateCmd.CreateParameter();
            p.ParameterName = "id";
            p.Value = recordId;
            adminUpdateCmd.Parameters.Add(p);

            var triggerEx = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => adminUpdateCmd.ExecuteNonQueryAsync());
            Assert.Contains("AUDIT_IMMUTABILITY_VIOLATION", triggerEx.Message);
        }
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }
}
