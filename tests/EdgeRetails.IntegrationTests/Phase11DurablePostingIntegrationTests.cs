using System.Collections.Concurrent;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Posting;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Production;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase11DurablePostingIntegrationTests
{
    private static readonly TimeZoneInfo KarachiZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pakistan Standard Time" : "Asia/Karachi");

    private static readonly Pbkdf2PinCredentialService PinService = new();

    private sealed class NoOpMaintenanceBarrier : IProductionMaintenanceBarrier
    {
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ProductionMaintenanceState.Normal);
    }

    private static string GetConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        // Load isolated test credential from uncommitted disposable cluster workspace
        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var builtConn = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                AssertDisposablePostgresTargetGuard(builtConn, performLiveProbe: false);
                return builtConn;
            }
        }

        throw new InvalidOperationException(
            "FAIL-CLOSED: Isolated test connection is not configured. Provide EDGE_RETAILS_TEST_DB or uncommitted disposable cluster credential file.");
    }

    internal static void AssertDisposablePostgresTargetGuard(string connectionString, bool performLiveProbe = false)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("FAIL-CLOSED: Target connection string cannot be null or empty.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Invalid connection string format: {ex.Message}", ex);
        }

        // Guard 1: Host must be loopback strictly (127.0.0.1 or localhost)
        if (builder.Host != "127.0.0.1" && !string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"FAIL-CLOSED: Target host '{builder.Host}' is prohibited. Only isolated loopback (127.0.0.1 or localhost) is permitted.");
        }

        // Guard 2: Port must be the dedicated disposable test port (55437), strictly never production port 5432
        if (builder.Port == 5432)
        {
            throw new InvalidOperationException(
                "FAIL-CLOSED: Port 5432 is prohibited. Connection to production or default cluster is forbidden.");
        }

        if (builder.Port != 55437 && builder.Port != 55438)
        {
            throw new InvalidOperationException(
                $"FAIL-CLOSED: Port {builder.Port} is prohibited. Expected disposable test port 55437 or 55438.");
        }

        // Guard 3: Database must be the dedicated test database
        if (string.IsNullOrWhiteSpace(builder.Database) ||
            builder.Database.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
            builder.Database.Equals("edgeretails", StringComparison.OrdinalIgnoreCase) ||
            builder.Database.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            !builder.Database.StartsWith("edgeretails_test_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"FAIL-CLOSED: Target database '{builder.Database}' is prohibited. Must be isolated test database 'edgeretails_test_*'.");
        }

        // Guard 4: Role must be an approved disposable test role
        if (string.IsNullOrWhiteSpace(builder.Username) ||
            (!builder.Username.Equals("er_provisioner", StringComparison.OrdinalIgnoreCase) &&
             !builder.Username.Equals("er_runtime", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"FAIL-CLOSED: Target user '{builder.Username}' is prohibited. Only dedicated test roles (er_provisioner/er_runtime) are permitted.");
        }

        // Live engine verification (verifying process data directory and PostgreSQL 18 engine version)
        if (performLiveProbe)
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();

            using var cmdDir = new NpgsqlCommand("SHOW data_directory;", conn);
            var dataDir = cmdDir.ExecuteScalar()?.ToString() ?? string.Empty;
            if (!dataDir.Contains("EdgeRetailsDisposable", StringComparison.OrdinalIgnoreCase) &&
                !dataDir.Contains("ER-RUN-", StringComparison.OrdinalIgnoreCase) &&
                !dataDir.Contains("EdgeRetailsMasterPg_", StringComparison.OrdinalIgnoreCase) &&
                !dataDir.Contains("EdgeRetailsCatalogPg_", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "FAIL-CLOSED: Connected PostgreSQL cluster data directory does not match disposable test runner path.");
            }

            using var cmdVer = new NpgsqlCommand("SHOW server_version_num;", conn);
            var verStr = cmdVer.ExecuteScalar()?.ToString() ?? "0";
            AssertEngineVersionGuard(verStr);

            using var cmdRole = new NpgsqlCommand("SELECT current_user;", conn);
            var currentRole = cmdRole.ExecuteScalar()?.ToString() ?? string.Empty;
            if (!currentRole.Equals("er_provisioner", StringComparison.OrdinalIgnoreCase) &&
                !currentRole.Equals("er_runtime", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"FAIL-CLOSED: Authenticated role '{currentRole}' is not an authorized test role.");
            }
        }
    }

    internal static void AssertEngineVersionGuard(string verStr)
    {
        if (!int.TryParse(verStr, out var verNum) || verNum < 180000 || verNum >= 190000)
        {
            throw new InvalidOperationException(
                $"FAIL-CLOSED: PostgreSQL version {verStr} must be exact engine version 18 (>= 180000 and < 190000).");
        }
    }

    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DataSources = new();

    private static NpgsqlDataSource GetOrCreateDataSource(string connectionString)
    {
        return DataSources.GetOrAdd(connectionString, connStr =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
            dataSourceBuilder.MapEnum<PackState>("inventory.pack_state", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            return dataSourceBuilder.Build();
        });
    }

    private static EdgeRetailsDbContext CreateDbContext(string connectionString)
    {
        AssertDisposablePostgresTargetGuard(connectionString, performLiveProbe: true);

        var dataSource = GetOrCreateDataSource(connectionString);

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "system");
                npgsql.MapEnum<PackState>("pack_state", "inventory", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            })
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
        return new EdgeRetailsDbContext(options);
    }

    private static (FinancialPostingAdapter Adapter, EdgeRetailsDbContext Db) CreatePostingAdapter(
        EdgeRetailsDbContext db,
        IShopClock clock)
    {
        var resourceLock = new PostgresOperationLock(db);
        var periodRepo = new EfPeriodLockRepository(db, resourceLock);
        var auditRepo = new EfPostingAuditRepository(db);
        var identityRepo = new IdentityReadRepository(db);
        var businessAuditWriter = new BusinessAuditWriter(db, new SystemClock(KarachiZone, () => clock.UtcNow));
        var guard = new ProductionMaintenanceWriteGuard(new NoOpMaintenanceBarrier(), null);
        var txRunner = new EfTransactionRunner(db, guard);

        var postingService = new ShopPostingValidationService(
            clock, periodRepo, auditRepo, identityRepo, PinService, businessAuditWriter, new EfSupervisorApprovalStore(db));

        var adapter = new FinancialPostingAdapter(postingService, db, txRunner);
        return (adapter, db);
    }

    private static async Task<(User User, string Pin)> SeedSupervisorAsync(EdgeRetailsDbContext db)
    {
        var pin = "8899";
        var cred = PinService.Hash(pin);

        var supervisorRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "Supervisor");
        if (supervisorRole is null)
        {
            supervisorRole = new Role
            {
                Name = "Supervisor",
                IsSystem = false,
                IsActive = true
            };
            db.Roles.Add(supervisorRole);
            await db.SaveChangesAsync();
        }

        var perm = await db.Permissions.FirstOrDefaultAsync(p => p.Key == PermissionKeys.SupervisorPostingOverride);
        if (perm is null)
        {
            perm = new Permission
            {
                Key = PermissionKeys.SupervisorPostingOverride,
                Description = "Supervisor posting override",
                IsActive = true
            };
            db.Permissions.Add(perm);
            await db.SaveChangesAsync();
        }

        var rp = await db.RolePermissions.FirstOrDefaultAsync(x => x.RoleId == supervisorRole.Id && x.PermissionId == perm.Id);
        if (rp is null)
        {
            db.RolePermissions.Add(new RolePermission
            {
                RoleId = supervisorRole.Id,
                PermissionId = perm.Id
            });
            await db.SaveChangesAsync();
        }

        var supervisorUser = new User
        {
            DisplayName = "Test Supervisor",
            RoleId = supervisorRole.Id,
            PinHash = cred.Hash,
            PinSalt = cred.Salt,
            PinIterations = cred.Iterations,
            PinAlgorithm = cred.Algorithm,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        db.Users.Add(supervisorUser);
        await db.SaveChangesAsync();

        return (supervisorUser, pin);
    }

    [Fact]
    public async Task T01_DurablePersistence_SurvivesProcessAndRepositoryRecreation()
    {
        var connStr = GetConnectionString();
        var periodId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var testDate = new DateOnly(2026, 7, 10); // Isolated date range to avoid concurrency conflicts
        var now = DateTimeOffset.UtcNow;

        // Pass 1: Write PeriodLock and PostingAuditEntry using initial DbContext & repositories
        await using (var db1 = CreateDbContext(connStr))
        {
            await db1.Database.ExecuteSqlRawAsync("DELETE FROM posting.period_locks WHERE period_name = 'Audit Pass 1 Locked Month';");
            var periodRepo1 = new EfPeriodLockRepository(db1);
            var auditRepo1 = new EfPostingAuditRepository(db1);

            var periodLock = new PeriodLock
            {
                Id = periodId,
                PeriodName = "Audit Pass 1 Locked Month",
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 15),
                IsLocked = true,
                LockedAt = now,
                LockedBy = actorId,
                LockReason = "Durable persistence integration test"
            };
            periodRepo1.AddPeriodLock(periodLock);

            var auditEntry = new PostingAuditEntry
            {
                Id = auditId,
                OperationType = PostingOperationTypes.Purchase,
                EntityId = entityId,
                EntityNumber = "PUR-20260710-099",
                ActorId = actorId,
                ShopDate = new DateOnly(2026, 10, 12),
                PostingDate = testDate,
                EventTimestampUtc = now,
                Reason = "Integration test audit entry",
                CreatedAt = now
            };
            auditRepo1.Record(auditEntry);

            await db1.SaveChangesAsync();
        }

        // Pass 2: Completely fresh DbContext and repositories (simulating process restart)
        await using (var db2 = CreateDbContext(connStr))
        {
            var periodRepo2 = new EfPeriodLockRepository(db2);
            var auditRepo2 = new EfPostingAuditRepository(db2);

            var isLocked = await periodRepo2.IsPeriodLockedAsync(testDate);
            Assert.True(isLocked, "Period lock must survive repository recreation and query accurately from PostgreSQL.");

            var lockedPeriod = await periodRepo2.GetLockedPeriodForDateAsync(testDate);
            Assert.NotNull(lockedPeriod);
            Assert.Equal(periodId, lockedPeriod!.Id);
            Assert.Equal("Audit Pass 1 Locked Month", lockedPeriod.PeriodName);

            var auditEntries = await auditRepo2.GetEntriesByEntityAsync(entityId);
            Assert.Single(auditEntries);
            Assert.Equal(auditId, auditEntries[0].Id);
            Assert.Equal("PUR-20260710-099", auditEntries[0].EntityNumber);
            Assert.Equal(testDate, auditEntries[0].PostingDate);
            Assert.Equal(PostingOperationTypes.Purchase, auditEntries[0].OperationType);
        }
    }

    [Fact]
    public async Task T02_AtomicPosting_FinancialMutationAndAudit_CommitTogether()
    {
        var connStr = GetConnectionString();
        var now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var clock = new ShopClock(KarachiZone, () => now);
        var entityId = Guid.NewGuid();

        await using var db = CreateDbContext(connStr);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM posting.period_locks;");
        var (supervisor, pin) = await SeedSupervisorAsync(db);
        var actorId = supervisor.Id;

        var (adapter, _) = CreatePostingAdapter(db, clock);

        var postingDate = new DateOnly(2026, 10, 11); // 1 day backdated
        var expenseNumber = "EXP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Expense,
            EntityId: entityId,
            EntityNumber: expenseNumber,
            ActorId: actorId,
            PostingDate: postingDate,
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.Id,
                SupervisorPin: pin,
                Reason: "Authorized backdated utility payment",
                RequestedAtUtc: now));

        var category = new ExpenseCategory
        {
            Name = "Utilities-" + Guid.NewGuid().ToString("N")[..6],
            IsActive = true
        };
        db.ExpenseCategories.Add(category);
        await db.SaveChangesAsync();

        var expense = new Expense
        {
            Id = entityId,
            ExpenseNumber = expenseNumber,
            CategoryId = category.Id,
            Amount = 1500.00m,
            ExpenseDate = postingDate,
            PaymentMethod = ExpensePaymentMethod.Cash,
            Description = "Test note",
            Status = ExpenseStatus.Posted,
            ClientOperationId = Guid.NewGuid(),
            CreatedBy = actorId,
            CreatedAt = now
        };

        var committedAudit = await adapter.ExecuteTransactionalPostingAsync(command, async () =>
        {
            db.Expenses.Add(expense);
            await Task.CompletedTask;
        });

        Assert.NotNull(committedAudit);
        Assert.Equal(entityId, committedAudit.EntityId);

        // Verify in fresh DbContext that both Expense and PostingAuditEntry exist in PostgreSQL
        await using var verifyDb = CreateDbContext(connStr);
        var loadedExpense = await verifyDb.Expenses.FirstOrDefaultAsync(e => e.Id == entityId);
        Assert.NotNull(loadedExpense);
        Assert.Equal(1500.00m, loadedExpense!.Amount);

        var loadedAudits = await new EfPostingAuditRepository(verifyDb).GetEntriesByEntityAsync(entityId);
        Assert.Single(loadedAudits);
        Assert.Equal(PostingOperationTypes.Expense, loadedAudits[0].OperationType);
        Assert.Equal(supervisor.Id, loadedAudits[0].SupervisorId);
    }

    [Fact]
    public async Task T03_DomainFailure_RollsBackAudit_LeavesNoSurvivingFalseAudit()
    {
        var connStr = GetConnectionString();
        var now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        var clock = new ShopClock(KarachiZone, () => now);
        var entityId = Guid.NewGuid();

        await using var db = CreateDbContext(connStr);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM posting.period_locks;");
        var (supervisor, _) = await SeedSupervisorAsync(db);
        var actorId = supervisor.Id;

        var (adapter, _) = CreatePostingAdapter(db, clock);

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-FAIL-001",
            ActorId: actorId,
            PostingDate: new DateOnly(2026, 10, 12)); // Today, no supervisor needed

        // Execute transactional posting where domain action throws
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await adapter.ExecuteTransactionalPostingAsync(command, async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("Simulated catastrophic domain stock allocation failure.");
            });
        });

        Assert.Equal("Simulated catastrophic domain stock allocation failure.", ex.Message);

        // Verify that NO posting audit entry exists in PostgreSQL for this entity!
        await using var verifyDb = CreateDbContext(connStr);
        var audits = await new EfPostingAuditRepository(verifyDb).GetEntriesByEntityAsync(entityId);
        Assert.Empty(audits);
    }

    [Fact]
    public async Task T04_PostingAuditEntry_IsAppendOnly_RejectsModificationAndDeletion()
    {
        var connStr = GetConnectionString();
        var entityId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Step 1: Insert an audit entry cleanly
        await using (var db = CreateDbContext(connStr))
        {
            var auditRepo = new EfPostingAuditRepository(db);
            auditRepo.Record(new PostingAuditEntry
            {
                Id = auditId,
                OperationType = PostingOperationTypes.Sale,
                EntityId = entityId,
                EntityNumber = "INV-20261012-001",
                ActorId = actorId,
                ShopDate = new DateOnly(2026, 10, 12),
                PostingDate = new DateOnly(2026, 10, 12),
                EventTimestampUtc = now,
                Reason = "Original immutable entry",
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        // Step 2: Attempt to modify the audit entry via EF Core -> must throw InvalidOperationException
        await using (var db = CreateDbContext(connStr))
        {
            var loaded = await db.PostingAuditEntries.FirstAsync(a => a.Id == auditId);
            loaded.Reason = "Illicit tampering attempt";

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("Posting audit entries are append-only ledger facts and cannot be modified or deleted", ex.Message);
        }

        // Step 3: Attempt to delete the audit entry via EF Core -> must throw InvalidOperationException
        await using (var db = CreateDbContext(connStr))
        {
            var loaded = await db.PostingAuditEntries.FirstAsync(a => a.Id == auditId);
            db.PostingAuditEntries.Remove(loaded);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("Posting audit entries are append-only ledger facts and cannot be modified or deleted", ex.Message);
        }

        // Step 4: Attempt raw SQL UPDATE -> PostgreSQL database trigger must throw error 55000
        await using (var db = CreateDbContext(connStr))
        {
            var npgsqlEx = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"UPDATE posting.posting_audit_entries SET reason = 'Tampered' WHERE id = {auditId}"));
            Assert.Equal("55000", npgsqlEx.SqlState);
            Assert.Contains("Posting audit entries are append-only ledger facts and cannot be modified or deleted", npgsqlEx.MessageText);
        }

        // Step 5: Attempt raw SQL DELETE -> PostgreSQL database trigger must throw error 55000
        await using (var db = CreateDbContext(connStr))
        {
            var npgsqlEx = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM posting.posting_audit_entries WHERE id = {auditId}"));
            Assert.Equal("55000", npgsqlEx.SqlState);
            Assert.Contains("Posting audit entries are append-only ledger facts and cannot be modified or deleted", npgsqlEx.MessageText);
        }
    }

    [Fact]
    public async Task T05_PeriodLock_AdvisoryLockRaceProtection_PreventsConflictingCreation()
    {
        var connStr = GetConnectionString();
        var targetDate = new DateOnly(2026, 6, 8); // Unique date to avoid concurrency overlap
        var actorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db1 = CreateDbContext(connStr);
        await using var db2 = CreateDbContext(connStr);

        var lockService1 = new PostgresOperationLock(db1);
        var lockService2 = new PostgresOperationLock(db2);

        var periodRepo1 = new EfPeriodLockRepository(db1, lockService1);
        var periodRepo2 = new EfPeriodLockRepository(db2, lockService2);

        // Start Transaction 1 and acquire advisory lock on targetDate
        await using var tx1 = await db1.Database.BeginTransactionAsync();
        var isLocked1 = await periodRepo1.IsPeriodLockedAsync(targetDate);
        Assert.False(isLocked1);

        // Now Transaction 2 attempts to lock a period covering targetDate with AddPeriodLockTransactionalAsync
        // It must block / be held by the advisory lock until tx1 completes
        var tx2TaskStarted = new TaskCompletionSource<bool>();
        var tx2Committed = false;

        var task2 = Task.Run(async () =>
        {
            await using var tx2 = await db2.Database.BeginTransactionAsync();
            var periodLock = new PeriodLock
            {
                Id = Guid.NewGuid(),
                PeriodName = "Concurrent Period Lock",
                PeriodStartDate = targetDate.AddDays(-2),
                PeriodEndDate = targetDate.AddDays(2),
                IsLocked = true,
                LockedAt = now,
                LockedBy = actorId,
                LockReason = "Concurrent lock test"
            };
            tx2TaskStarted.SetResult(true);

            // This will block waiting on tx1's advisory lock
            await periodRepo2.AddPeriodLockTransactionalAsync(periodLock);
            await db2.SaveChangesAsync();
            await tx2.CommitAsync();
            tx2Committed = true;
        });

        await tx2TaskStarted.Task;
        // Small delay to ensure task2 is blocked on pg_advisory_xact_lock
        await Task.Delay(200);
        Assert.False(tx2Committed, "Transaction 2 must wait on Transaction 1's advisory lock.");

        // Transaction 1 commits and releases its advisory xact lock
        await tx1.CommitAsync();

        // Transaction 2 unblocks and completes
        await task2;
        Assert.True(tx2Committed, "Transaction 2 should complete after Transaction 1 commits.");

        // Fresh query confirms targetDate is now locked
        await using var verifyDb = CreateDbContext(connStr);
        var periodRepoVerify = new EfPeriodLockRepository(verifyDb);
        var isLockedAfter = await periodRepoVerify.IsPeriodLockedAsync(targetDate);
        Assert.True(isLockedAfter);
    }

    [Fact]
    public void T06_Guard_Rejects_ProductionPort_5432_WithoutConnection()
    {
        var prodConn = "Host=127.0.0.1;Port=5432;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() => AssertDisposablePostgresTargetGuard(prodConn, performLiveProbe: false));
        Assert.Contains("Port 5432 is prohibited", ex.Message);
    }

    [Fact]
    public void T07_Guard_Rejects_NonLoopbackHost_WithoutConnection()
    {
        var remoteConn = "Host=192.168.1.100;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() => AssertDisposablePostgresTargetGuard(remoteConn, performLiveProbe: false));
        Assert.Contains("Target host '192.168.1.100' is prohibited", ex.Message);
    }

    [Fact]
    public void T08_Guard_Rejects_ProductionDatabaseName_WithoutConnection()
    {
        var prodDbConn = "Host=127.0.0.1;Port=55437;Database=edgeretails;Username=er_provisioner;Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() => AssertDisposablePostgresTargetGuard(prodDbConn, performLiveProbe: false));
        Assert.Contains("Target database 'edgeretails' is prohibited", ex.Message);

        var postgresDbConn = "Host=127.0.0.1;Port=55437;Database=postgres;Username=er_provisioner;Password=dummy";
        var ex2 = Assert.Throws<InvalidOperationException>(() => AssertDisposablePostgresTargetGuard(postgresDbConn, performLiveProbe: false));
        Assert.Contains("Target database 'postgres' is prohibited", ex2.Message);
    }

    [Fact]
    public void T09_Guard_Rejects_UnauthorizedRole_WithoutConnection()
    {
        var badUserConn = "Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=postgres;Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() => AssertDisposablePostgresTargetGuard(badUserConn, performLiveProbe: false));
        Assert.Contains("Target user 'postgres' is prohibited", ex.Message);
    }

    [Fact]
    public void T10_Guard_Rejects_EmptyConnectionString_WithoutConnection()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AssertDisposablePostgresTargetGuard("", performLiveProbe: false));
        Assert.Contains("Target connection string cannot be null or empty", ex.Message);
    }

    [Fact]
    public void T11_Guard_AcceptsValidDisposableTarget_WithoutMutation()
    {
        var validConn = "Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=dummy";
        // Must not throw any exception when syntax and target constraints are validated
        AssertDisposablePostgresTargetGuard(validConn, performLiveProbe: false);
    }

    [Theory]
    [InlineData("170005")]
    [InlineData("170000")]
    [InlineData("190000")]
    [InlineData("190001")]
    [InlineData("invalid")]
    [InlineData("0")]
    public void T11A_Guard_Rejects_NonPostgreSql18_Version(string badVer)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AssertEngineVersionGuard(badVer));
        Assert.Contains("must be exact engine version 18", ex.Message);
    }

    [Theory]
    [InlineData("180000")]
    [InlineData("180001")]
    [InlineData("180005")]
    public void T11B_Guard_Accepts_ExactPostgreSql18_Version(string goodVer)
    {
        AssertEngineVersionGuard(goodVer);
    }

    private static async Task<(Product Product, ProductUnit ProductUnit, Guid ActorId)> SeedProductAndActorAsync(
        EdgeRetailsDbContext db,
        decimal defaultPrice = 50m)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "s-" + suffix,
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Product-" + suffix,
            Sku = ("SKU-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = defaultPrice,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        return (product, productUnit, actorId);
    }

    [Fact]
    public async Task T12_SalePosting_ConservationAndSubledger_ConservesQuantitiesAndCostBasis()
    {
        var connStr = GetConnectionString();
        var now = new DateTimeOffset(2026, 10, 12, 11, 0, 0, TimeSpan.Zero);
        var clock = new ShopClock(KarachiZone, () => now);

        await using var db = CreateDbContext(connStr);
        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, defaultPrice: 50m);
        var (adapter, _) = CreatePostingAdapter(db, clock);

        var initialQty = 100m;
        var unitCost = 25m;
        var saleQty = 10m;
        var salePrice = 50m;
        var initialCarryingCost = initialQty * unitCost; // 2500m

        // Seed stock balance & product cost state
        var stockBalance = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = initialQty,
            Version = 1
        };
        db.StockBalances.Add(stockBalance);

        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = initialQty,
            TotalInventoryCost = initialCarryingCost,
            MovingAverageCost = unitCost,
            Version = 1
        };
        db.ProductCostStates.Add(costState);

        var adj = new StockAdjustment
        {
            AdjustmentNumber = "ADJ-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Mode = StockAdjustmentMode.Delta,
            Reason = StockAdjustmentReason.OpeningStock,
            Status = StockAdjustmentStatus.Posted,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now,
            Version = 1
        };
        db.StockAdjustments.Add(adj);

        var adjItem = new StockAdjustmentItem
        {
            StockAdjustmentId = adj.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = initialQty,
            UnitCostSnapshot = unitCost,
            TotalCostSnapshot = initialCarryingCost
        };
        db.StockAdjustmentItems.Add(adjItem);

        var intakeMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StockAdjustment",
            ReferenceId = adj.Id,
            UnitCostSnapshot = unitCost,
            RecognizedLossAmount = 0m,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(intakeMovement);

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SourceMovementId = intakeMovement.Id,
            ReceivedQuantity = initialQty,
            OriginalUnitCost = unitCost,
            EffectiveUnitCost = unitCost,
            CreatedAt = now
        };
        db.InventoryLots.Add(lot);

        // Seed Sealed pack inventory unit
        var packUnit = new InventoryUnit
        {
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = adjItem.Id,
            AcquisitionCost = unitCost,
            Status = InventoryUnitStatus.InStock,
            PackState = PackState.Sealed,
            InitialBaseQuantity = initialQty,
            RemainingBaseQuantity = initialQty,
            IsLooseStock = false,
            CreatedAt = now,
            Version = 1
        };
        db.InventoryUnits.Add(packUnit);
        await db.SaveChangesAsync();

        var saleId = Guid.NewGuid();
        var invoiceNumber = "INV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var postingCommand = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: saleId,
            EntityNumber: invoiceNumber,
            ActorId: actorId,
            PostingDate: new DateOnly(2026, 10, 12));

        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            ReferenceId = saleId,
            UnitCostSnapshot = unitCost,
            RecognizedLossAmount = 0m,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid()
        };

        var sale = new Sale
        {
            Id = saleId,
            InvoiceNumber = invoiceNumber,
            CashierUserId = actorId,
            CompletedAt = now,
            Subtotal = saleQty * salePrice,
            InvoiceDiscount = 0m,
            GrandTotal = saleQty * salePrice,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = now
        };

        var saleItem = new SaleItem
        {
            SaleId = saleId,
            InventoryMovementId = movement.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            ProductNameSnapshot = product.Name,
            EnteredQuantity = saleQty,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = saleQty,
            ListUnitPriceSnapshot = salePrice,
            UnitPrice = salePrice,
            GrossLineTotal = saleQty * salePrice,
            AllocatedInvoiceDiscount = 0m,
            NetLineTotal = saleQty * salePrice,
            UnitCostSnapshot = unitCost,
            TotalCostSnapshot = saleQty * unitCost,
            GrossProfitSnapshot = (saleQty * salePrice) - (saleQty * unitCost)
        };

        var salePayment = new SalePayment
        {
            SaleId = saleId,
            Method = SalePaymentMethod.Cash,
            AmountTendered = saleQty * salePrice,
            AppliedAmount = saleQty * salePrice,
            ChangeGiven = 0m
        };

        // Execute transactional posting
        await adapter.ExecuteTransactionalPostingAsync(postingCommand, async () =>
        {
            db.InventoryMovements.Add(movement);
            db.Sales.Add(sale);
            db.SaleItems.Add(saleItem);
            db.SalePayments.Add(salePayment);

            // Deduct stock balance
            stockBalance.ApplyDelta(InventoryBucket.Sellable, -saleQty);

            // Deduct cost pool
            costState.RemoveCarryingValue(saleQty);

            // Deduct pack unit
            packUnit.ConsumeFromPack(saleQty);

            // Record pack allocation
            var packAlloc = new PackAllocation
            {
                PackUnitId = packUnit.Id,
                MovementId = movement.Id,
                LotId = lot.Id,
                OperationType = "Sale",
                SaleItemId = saleItem.Id,
                AllocatedBaseQuantity = saleQty,
                RemainingQuantityAfter = packUnit.RemainingBaseQuantity.GetValueOrDefault(),
                PackStateAfter = packUnit.PackState.GetValueOrDefault(),
                ActorId = actorId,
                OccurredAt = now,
                CreatedAt = now
            };
            db.PackAllocations.Add(packAlloc);
            await Task.CompletedTask;
        });

        // Verification in fresh DbContext
        await using var verifyDb = CreateDbContext(connStr);
        var loadedSale = await verifyDb.Sales.FirstAsync(s => s.Id == saleId);
        Assert.Equal(500m, loadedSale.GrandTotal);

        var loadedStock = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == product.Id);
        Assert.Equal(90m, loadedStock.SellableQty);

        var loadedCost = await verifyDb.ProductCostStates.FirstAsync(c => c.ProductId == product.Id);
        Assert.Equal(90m, loadedCost.CostedQty);
        Assert.Equal(2250m, loadedCost.TotalInventoryCost);
        Assert.Equal(25m, loadedCost.MovingAverageCost);

        // Quantity & Cost Conservation: Remaining carrying value (2250) + Cost of Goods Sold (250) == Initial Carrying Value (2500)
        Assert.Equal(initialCarryingCost, loadedCost.TotalInventoryCost + saleItem.TotalCostSnapshot);

        var loadedPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == packUnit.Id);
        Assert.Equal(90m, loadedPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, loadedPack.PackState);

        var audits = await new EfPostingAuditRepository(verifyDb).GetEntriesByEntityAsync(saleId);
        Assert.Single(audits);
        Assert.Equal(PostingOperationTypes.Sale, audits[0].OperationType);
    }

    [Fact]
    public async Task T13_PaymentFailure_AbortsTransaction_LeavesNoOrphanSaleOrAudit()
    {
        var connStr = GetConnectionString();
        var now = new DateTimeOffset(2026, 10, 12, 11, 0, 0, TimeSpan.Zero);
        var clock = new ShopClock(KarachiZone, () => now);

        await using var db = CreateDbContext(connStr);
        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, defaultPrice: 50m);
        var (adapter, _) = CreatePostingAdapter(db, clock);

        var stockBalance = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = 100m,
            Version = 1
        };
        db.StockBalances.Add(stockBalance);
        await db.SaveChangesAsync();

        var saleId = Guid.NewGuid();
        var invoiceNumber = "INV-FAIL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var postingCommand = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: saleId,
            EntityNumber: invoiceNumber,
            ActorId: actorId,
            PostingDate: new DateOnly(2026, 10, 12));

        var sale = new Sale
        {
            Id = saleId,
            InvoiceNumber = invoiceNumber,
            CashierUserId = actorId,
            CompletedAt = now,
            Subtotal = 100m,
            GrandTotal = 100m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = now
        };

        // Simulated payment terminal failure throws exception
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await adapter.ExecuteTransactionalPostingAsync(postingCommand, async () =>
            {
                db.Sales.Add(sale);
                stockBalance.ApplyDelta(InventoryBucket.Sellable, -2m);
                await Task.Yield();
                throw new InvalidOperationException("External Payment Terminal Timeout: Transaction aborted by customer.");
            });
        });

        Assert.Contains("External Payment Terminal Timeout", ex.Message);

        // Verify in fresh DbContext that nothing was committed to PostgreSQL
        await using var verifyDb = CreateDbContext(connStr);
        var loadedSale = await verifyDb.Sales.FirstOrDefaultAsync(s => s.Id == saleId);
        Assert.Null(loadedSale);

        var loadedStock = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == product.Id);
        Assert.Equal(100m, loadedStock.SellableQty); // Untouched

        var audits = await new EfPostingAuditRepository(verifyDb).GetEntriesByEntityAsync(saleId);
        Assert.Empty(audits);
    }

    [Fact]
    public async Task T14_ReplayAndIdempotency_SameClientOperationId_PreventsDuplicatePosting()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var invoiceNum = "INV-REPLAY-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        // Pass 1: Record Succeeded operation outcome in PostgreSQL
        await using (var db1 = CreateDbContext(connStr))
        {
            var ledger1 = new EfOperationOutcomeLedger(db1);
            await ledger1.RecordPendingAsync(clientOpId, "Sale", actorId: actorId);
            await ledger1.RecordSuccessAsync(clientOpId, "Sale", saleId, invoiceNum);
        }

        // Pass 2: In fresh DbContext, query outcome ledger using the same ClientOperationId
        await using (var db2 = CreateDbContext(connStr))
        {
            var ledger2 = new EfOperationOutcomeLedger(db2);
            var outcome = await ledger2.GetOutcomeAsync(clientOpId);

            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome!.State);
            Assert.Equal(saleId, outcome.EntityId);
            Assert.Equal(invoiceNum, outcome.DocumentNumber);
            Assert.True(outcome.WasCommitted);

            // Caller idempotency assertion: Detecting existing Succeeded outcome short-circuits transaction, preventing duplicate sale
            var isDuplicateReplay = outcome.State == OperationOutcomeState.Succeeded;
            Assert.True(isDuplicateReplay, "Replayed client operation ID must be detected as already succeeded.");
        }
    }

    [Fact]
    public async Task T15_ConcurrentStockAllocationRace_SeparateSessions_OneSucceeds_OneFailsClosed()
    {
        var connStr = GetConnectionString();

        Guid productId;
        // Seed stock balance with exactly 5 units
        await using (var seedDb = CreateDbContext(connStr))
        {
            var (product, _, _) = await SeedProductAndActorAsync(seedDb);
            productId = product.Id;

            seedDb.StockBalances.Add(new StockBalance
            {
                ProductId = productId,
                SellableQty = 5m,
                Version = 1
            });
            await seedDb.SaveChangesAsync();
        }

        var session1Succeeded = false;
        var session2Succeeded = false;
        var barrier = new TaskCompletionSource<bool>();

        var task1 = Task.Run(async () =>
        {
            try
            {
                await using var db1 = CreateDbContext(connStr);
                await using var tx1 = await db1.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await barrier.Task;

                var stock = await db1.StockBalances.FirstAsync(b => b.ProductId == productId);
                if (stock.SellableQty >= 4m)
                {
                    stock.ApplyDelta(InventoryBucket.Sellable, -4m);
                    await db1.SaveChangesAsync();
                    await tx1.CommitAsync();
                    session1Succeeded = true;
                }
            }
            catch
            {
                session1Succeeded = false;
            }
        });

        var task2 = Task.Run(async () =>
        {
            try
            {
                await using var db2 = CreateDbContext(connStr);
                await using var tx2 = await db2.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                await barrier.Task;

                var stock = await db2.StockBalances.FirstAsync(b => b.ProductId == productId);
                if (stock.SellableQty >= 4m)
                {
                    stock.ApplyDelta(InventoryBucket.Sellable, -4m);
                    await db2.SaveChangesAsync();
                    await tx2.CommitAsync();
                    session2Succeeded = true;
                }
            }
            catch
            {
                session2Succeeded = false;
            }
        });

        // Release barrier to run concurrently
        barrier.SetResult(true);
        await Task.WhenAll(task1, task2);

        // One must succeed, but both CANNOT succeed because 4 + 4 = 8 > 5
        Assert.True(session1Succeeded || session2Succeeded, "At least one session should complete allocation.");
        Assert.False(session1Succeeded && session2Succeeded, "Both concurrent sessions cannot allocate 4 from 5 units.");

        // Check remaining stock in PostgreSQL: must be non-negative and conserved!
        await using var verifyDb = CreateDbContext(connStr);
        var finalStock = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == productId);
        Assert.Equal(1m, finalStock.SellableQty);
    }

    [Fact]
    public async Task T16_TimezonePeriodBoundary_ReportingQueries_StrictKarachiUtcMapping()
    {
        var connStr = GetConnectionString();
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var clock = new ShopClock(KarachiZone, () => now);
        var boundaryService = new ShopPeriodBoundaryService(clock);

        var oct12Date = new DateOnly(2026, 10, 12);
        var oct13Date = new DateOnly(2026, 10, 13);
        var (oct12Start, oct12End) = boundaryService.GetDailyUtcBoundary(oct12Date);
        var (oct13Start, oct13End) = boundaryService.GetDailyUtcBoundary(oct13Date);

        var saleAId = Guid.NewGuid();
        var saleBId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();

        // Sale A completed at 2026-10-12T18:59:30Z (23:59:30 on Oct 12 in Karachi, UTC+5)
        var timeA = new DateTimeOffset(2026, 10, 12, 18, 59, 30, TimeSpan.Zero);
        // Sale B completed at 2026-10-12T19:00:30Z (00:00:30 on Oct 13 in Karachi, UTC+5)
        var timeB = new DateTimeOffset(2026, 10, 12, 19, 0, 30, TimeSpan.Zero);

        await using (var db = CreateDbContext(connStr))
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            db.Sales.Add(new Sale
            {
                Id = saleAId,
                InvoiceNumber = "INV-BOUNDARY-A-" + suffix,
                CashierUserId = cashierId,
                CompletedAt = timeA,
                Subtotal = 100m,
                GrandTotal = 100m,
                Status = SaleStatus.Completed,
                PaymentStatus = SalePaymentStatus.Paid,
                ClientOperationId = Guid.NewGuid(),
                CreatedAt = timeA
            });

            db.Sales.Add(new Sale
            {
                Id = saleBId,
                InvoiceNumber = "INV-BOUNDARY-B-" + suffix,
                CashierUserId = cashierId,
                CompletedAt = timeB,
                Subtotal = 200m,
                GrandTotal = 200m,
                Status = SaleStatus.Completed,
                PaymentStatus = SalePaymentStatus.Paid,
                ClientOperationId = Guid.NewGuid(),
                CreatedAt = timeB
            });

            await db.SaveChangesAsync();
        }

        // Query PostgreSQL for Oct 12 sales
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var oct12Sales = await verifyDb.Sales
                .Where(s => s.CompletedAt >= oct12Start && s.CompletedAt < oct12End)
                .Where(s => s.Id == saleAId || s.Id == saleBId)
                .ToListAsync();

            Assert.Single(oct12Sales);
            Assert.Equal(saleAId, oct12Sales[0].Id);

            // Query PostgreSQL for Oct 13 sales
            var oct13Sales = await verifyDb.Sales
                .Where(s => s.CompletedAt >= oct13Start && s.CompletedAt < oct13End)
                .Where(s => s.Id == saleAId || s.Id == saleBId)
                .ToListAsync();

            Assert.Single(oct13Sales);
            Assert.Equal(saleBId, oct13Sales[0].Id);
        }
    }

    private static async Task<(Product Product, InventoryUnit PackUnit, InventoryLot Lot, Guid ActorId)> SeedPackContainerTestScenarioAsync(
        EdgeRetailsDbContext db,
        decimal initialQuantity = 100m,
        decimal acquisitionCost = 1500m)
    {
        var now = DateTimeOffset.UtcNow;
        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db);

        product.TrackingMode = TrackingMode.Container;
        await db.SaveChangesAsync();

        var adj = new StockAdjustment
        {
            AdjustmentNumber = "ADJ-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Mode = StockAdjustmentMode.Delta,
            Reason = StockAdjustmentReason.OpeningStock,
            Status = StockAdjustmentStatus.Posted,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now,
            Version = 1
        };
        db.StockAdjustments.Add(adj);

        var adjItem = new StockAdjustmentItem
        {
            StockAdjustmentId = adj.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = initialQuantity,
            UnitCostSnapshot = decimal.Round(acquisitionCost / initialQuantity, 6),
            TotalCostSnapshot = acquisitionCost
        };
        db.StockAdjustmentItems.Add(adjItem);

        var intakeMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StockAdjustment",
            ReferenceId = adj.Id,
            UnitCostSnapshot = decimal.Round(acquisitionCost / initialQuantity, 6),
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(intakeMovement);

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SourceMovementId = intakeMovement.Id,
            ReceivedQuantity = initialQuantity,
            OriginalUnitCost = decimal.Round(acquisitionCost / initialQuantity, 6),
            EffectiveUnitCost = decimal.Round(acquisitionCost / initialQuantity, 6),
            CreatedAt = now
        };
        db.InventoryLots.Add(lot);

        var stockBalance = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = initialQuantity,
            Version = 1
        };
        db.StockBalances.Add(stockBalance);

        var lotBalance = new InventoryLotBucketBalance
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = initialQuantity
        };
        db.InventoryLotBucketBalances.Add(lotBalance);

        var packUnit = new InventoryUnit
        {
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = adjItem.Id,
            TrackingCode = "COIL-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            AcquisitionCost = acquisitionCost,
            Status = InventoryUnitStatus.InStock,
            PackState = PackState.Sealed,
            InitialBaseQuantity = initialQuantity,
            RemainingBaseQuantity = initialQuantity,
            IsLooseStock = false,
            CreatedAt = now,
            Version = 1
        };
        db.InventoryUnits.Add(packUnit);
        await db.SaveChangesAsync();

        return (product, packUnit, lot, actorId);
    }

    private static PackContainersController CreateRealPostgresPackController(
        EdgeRetailsDbContext db,
        Guid actorId)
    {
        var clock = new SystemClock(KarachiZone, () => DateTimeOffset.UtcNow);
        var invRepo = new InventoryRepository(db);
        var packAuthority = new PackSubLedgerAllocationAuthority();
        var controller = new PackContainersController(db, invRepo, db, clock, packAuthority);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["ActorContext"] = new ActorContext(
            UserId: actorId,
            SessionId: Guid.NewGuid(),
            DisplayName: "Test Cashier",
            RoleId: Guid.NewGuid(),
            RoleName: "Cashier",
            Permissions: new HashSet<string> { PermissionKeys.SalesPosUse, PermissionKeys.InventoryManage });

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    [Fact]
    public async Task T17_AllocatePack_RealPostgres_ConcurrentDuplicates_ExactOneEffect_ConservesBalanceAndCost()
    {
        var connStr = GetConnectionString();
        Guid productId;
        Guid containerId;
        Guid lotId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (prod, pack, lot, act) = await SeedPackContainerTestScenarioAsync(seedDb, initialQuantity: 100m, acquisitionCost: 1500m);
            productId = prod.Id;
            containerId = pack.Id;
            lotId = lot.Id;
            actorId = act;
        }

        var txId = Guid.NewGuid();
        var lineItemId = Guid.Empty;
        var barrier = new TaskCompletionSource<bool>();

        var task1 = Task.Run(async () =>
        {
            await using var db1 = CreateDbContext(connStr);
            var controller1 = CreateRealPostgresPackController(db1, actorId);
            await barrier.Task;
            var req = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: lineItemId,
                ContainerId: containerId,
                RequestedQuantity: 25m,
                AllowSplitCut: false);
            return await controller1.AllocatePack(req);
        });

        var task2 = Task.Run(async () =>
        {
            await using var db2 = CreateDbContext(connStr);
            var controller2 = CreateRealPostgresPackController(db2, actorId);
            await barrier.Task;
            var req = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: lineItemId,
                ContainerId: containerId,
                RequestedQuantity: 25m,
                AllowSplitCut: false);
            return await controller2.AllocatePack(req);
        });

        barrier.SetResult(true);
        var results = await Task.WhenAll(task1, task2);

        // Both concurrent duplicate requests must return OK with identical outcome (idempotent replay coordination)
        var ok1 = Assert.IsType<OkObjectResult>(results[0]);
        var ok2 = Assert.IsType<OkObjectResult>(results[1]);

        var resp1 = Assert.IsType<PackAllocationResponse>(ok1.Value);
        var resp2 = Assert.IsType<PackAllocationResponse>(ok2.Value);

        Assert.Equal(resp1.AllocationId, resp2.AllocationId);
        Assert.Equal(25m, resp1.AllocatedQuantity);
        Assert.Equal(75m, resp1.RemainingQuantity);
        Assert.Equal(25m, resp2.AllocatedQuantity);
        Assert.Equal(75m, resp2.RemainingQuantity);

        // Verify PostgreSQL durable state: exactly ONE business effect occurred
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var allocations = await verifyDb.PackAllocations
                .Where(a => a.MovementId == txId)
                .ToListAsync();
            Assert.Single(allocations);
            Assert.Equal(containerId, allocations[0].PackUnitId);
            Assert.Equal(lotId, allocations[0].LotId);
            Assert.Equal(25m, allocations[0].AllocatedBaseQuantity);
            Assert.Equal(15.000000m, allocations[0].UnitCostSnapshot);
            Assert.Equal(375.000000m, allocations[0].TotalCostSnapshot);
            Assert.Equal(75m, allocations[0].RemainingQuantityAfter);
            Assert.Equal(PackState.Open, allocations[0].PackStateAfter);

            var movements = await verifyDb.InventoryMovements
                .Where(m => m.Id == txId)
                .ToListAsync();
            Assert.Single(movements);
            Assert.Equal(InventoryMovementType.SaleOut, movements[0].MovementType);
            Assert.Equal("PACK_ALLOCATION", movements[0].ReferenceType);
            Assert.Null(movements[0].ReferenceId);
            Assert.Equal(15.000000m, movements[0].UnitCostSnapshot);

            var effects = await verifyDb.InventoryMovementEffects
                .Where(e => e.MovementId == txId)
                .ToListAsync();
            Assert.Single(effects);
            Assert.Equal(InventoryBucket.Sellable, effects[0].StockBucket);
            Assert.Equal(-25m, effects[0].QuantityDelta);
            Assert.Equal(100m, effects[0].QuantityBefore);
            Assert.Equal(75m, effects[0].QuantityAfter);

            var consumptions = await verifyDb.InventoryLotConsumptions
                .Where(c => c.MovementId == txId)
                .ToListAsync();
            Assert.Single(consumptions);
            Assert.Equal(lotId, consumptions[0].LotId);
            Assert.Equal(25m, consumptions[0].Quantity);
            Assert.Equal(15.000000m, consumptions[0].UnitCostSnapshot);
            Assert.Equal(375.000000m, consumptions[0].TotalCostSnapshot);

            var movementUnits = await verifyDb.InventoryMovementUnits
                .Where(u => u.MovementId == txId)
                .ToListAsync();
            Assert.Single(movementUnits);
            Assert.Equal(containerId, movementUnits[0].InventoryUnitId);

            var stock = await verifyDb.StockBalances.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(75m, stock.SellableQty);

            var lotBal = await verifyDb.InventoryLotBucketBalances.FirstAsync(l => l.LotId == lotId);
            Assert.Equal(75m, lotBal.Quantity);

            var unit = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
            Assert.Equal(75m, unit.RemainingBaseQuantity);
            Assert.Equal(PackState.Open, unit.PackState);
        }
    }

    [Fact]
    public async Task T18_AllocatePack_RealPostgres_ChangedPayload_RejectsWith409Conflict()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerTestScenarioAsync(seedDb, initialQuantity: 100m, acquisitionCost: 1500m);
            containerId = pack.Id;
            actorId = act;
        }

        var txId = Guid.NewGuid();
        var lineItemId = Guid.Empty;

        await using (var db = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db, actorId);

            // 1. Initial valid allocation
            var initialReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: lineItemId,
                ContainerId: containerId,
                RequestedQuantity: 20m);
            var initialResult = await controller.AllocatePack(initialReq);
            Assert.IsType<OkObjectResult>(initialResult);

            // 2. Changed Quantity with same TransactionId -> 409 Conflict
            var changedQtyReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: lineItemId,
                ContainerId: containerId,
                RequestedQuantity: 30m);
            var conflictQtyResult = await controller.AllocatePack(changedQtyReq);
            var conflictObj1 = Assert.IsType<ConflictObjectResult>(conflictQtyResult);
            var code1 = conflictObj1.Value?.GetType().GetProperty("code")?.GetValue(conflictObj1.Value)?.ToString();
            Assert.Equal("pack.idempotency_payload_mismatch", code1);

            // 3. Different Container with same TransactionId -> 409 Conflict
            var changedContainerReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: lineItemId,
                ContainerId: Guid.NewGuid(),
                RequestedQuantity: 20m);
            var conflictContainerResult = await controller.AllocatePack(changedContainerReq);
            var conflictObj2 = Assert.IsType<ConflictObjectResult>(conflictContainerResult);
            var code2 = conflictObj2.Value?.GetType().GetProperty("code")?.GetValue(conflictObj2.Value)?.ToString();
            Assert.Equal("pack.idempotency_payload_mismatch", code2);

            // 4. Different Line Item with same TransactionId -> 409 Conflict
            var changedLineReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: Guid.NewGuid(),
                ContainerId: containerId,
                RequestedQuantity: 20m);
            var conflictLineResult = await controller.AllocatePack(changedLineReq);
            var conflictObj3 = Assert.IsType<ConflictObjectResult>(conflictLineResult);
            var code3 = conflictObj3.Value?.GetType().GetProperty("code")?.GetValue(conflictObj3.Value)?.ToString();
            Assert.Equal("pack.idempotency_payload_mismatch", code3);
        }
    }

    [Fact]
    public async Task T19_AllocatePack_RealPostgres_StaleVersion_RejectsWith409Conflict()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerTestScenarioAsync(seedDb, initialQuantity: 50m, acquisitionCost: 1000m);
            containerId = pack.Id;
            actorId = act;
        }

        await using (var db = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db, actorId);

            // Submit with mismatched expected version (container is version 1, expected 999)
            var staleReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 10m,
                ExpectedVersion: 999);

            var result = await controller.AllocatePack(staleReq);
            var conflictResult = Assert.IsType<ConflictObjectResult>(result);
            var code = conflictResult.Value?.GetType().GetProperty("code")?.GetValue(conflictResult.Value)?.ToString();
            Assert.Equal("pack.stale_version_conflict", code);
        }
    }

    [Fact]
    public async Task T20_AllocatePack_RealPostgres_PostFailureRollback_NoPoisonedTransactionState()
    {
        var connStr = GetConnectionString();
        Guid productId;
        Guid containerId;
        Guid lotId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (prod, pack, lot, act) = await SeedPackContainerTestScenarioAsync(seedDb, initialQuantity: 10m, acquisitionCost: 200m);
            productId = prod.Id;
            containerId = pack.Id;
            lotId = lot.Id;
            actorId = act;
        }

        await using (var db = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db, actorId);

            // Step 1: Submit a request that passes all business prechecks:
            // - Container exists and is InStock (10m remaining)
            // - RequestedQuantity = 5m (<= 10m)
            // - Valid Lot and Stock balance exist
            // BUT provide a non-existent LineItemId (foreign key to sale_items)
            // This triggers a real PostgreSQL persistence failure during SaveChangesAsync (FK constraint violation),
            // exercising the catch (DbUpdateException) block, EF ChangeTracker.Clear(), and PostgreSQL rollback.
            var failedTxId = Guid.NewGuid();
            var nonExistentSaleItemId = Guid.NewGuid();
            var invalidFkReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: failedTxId,
                LineItemId: nonExistentSaleItemId,
                ContainerId: containerId,
                RequestedQuantity: 5m,
                AllowSplitCut: false);

            // Assert that the failure genuinely reached the persistence boundary and threw DbUpdateException
            var dbEx = await Assert.ThrowsAsync<DbUpdateException>(() => controller.AllocatePack(invalidFkReq));
            Assert.NotNull(dbEx);

            // Step 2: Verify PostgreSQL durable state was completely rolled back and remains UNCHANGED:
            await using (var verifyDb = CreateDbContext(connStr))
            {
                var containerAfter = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
                Assert.Equal(10m, containerAfter.RemainingBaseQuantity); // Preserved at 10m (not mutated to 5m)
                Assert.Equal(1, containerAfter.Version); // Version preserved at 1 (not bumped to 2)
                Assert.Equal(PackState.Sealed, containerAfter.PackState);

                var stockAfter = await verifyDb.StockBalances.FirstAsync(s => s.ProductId == productId);
                Assert.Equal(10m, stockAfter.SellableQty); // Stock preserved at 10m

                var lotAfter = await verifyDb.InventoryLotBucketBalances.FirstAsync(l => l.LotId == lotId);
                Assert.Equal(10m, lotAfter.Quantity); // Lot balance preserved at 10m

                // Verify ZERO orphan rows exist across all subledger tables
                var orphanAlloc = await verifyDb.PackAllocations.FirstOrDefaultAsync(a => a.MovementId == failedTxId);
                Assert.Null(orphanAlloc);
                var orphanMovement = await verifyDb.InventoryMovements.FirstOrDefaultAsync(m => m.Id == failedTxId);
                Assert.Null(orphanMovement);
                var orphanEffect = await verifyDb.InventoryMovementEffects.FirstOrDefaultAsync(e => e.MovementId == failedTxId);
                Assert.Null(orphanEffect);
                var orphanLot = await verifyDb.InventoryLotConsumptions.FirstOrDefaultAsync(c => c.MovementId == failedTxId);
                Assert.Null(orphanLot);
                var orphanUnit = await verifyDb.InventoryMovementUnits.FirstOrDefaultAsync(u => u.MovementId == failedTxId);
                Assert.Null(orphanUnit);
            }

            // Step 3: Now immediately submit a valid allocation on the EXACT SAME DbContext and controller instance.
            // This proves ChangeTracker.Clear() cleared all modified entity snapshots and the PostgreSQL connection
            // is NOT left in state 25P02 (current transaction is in aborted state).
            var validTxId = Guid.NewGuid();
            var validReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: validTxId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 5m,
                AllowSplitCut: false);

            var validResult = await controller.AllocatePack(validReq);
            var okResult = Assert.IsType<OkObjectResult>(validResult);
            var response = Assert.IsType<PackAllocationResponse>(okResult.Value);

            Assert.Equal(5m, response.AllocatedQuantity);
            Assert.Equal(5m, response.RemainingQuantity);

            // Step 4: Confirm that the valid allocation committed cleanly to PostgreSQL on that same connection
            await using (var verifyDb = CreateDbContext(connStr))
            {
                var committedAlloc = await verifyDb.PackAllocations.FirstOrDefaultAsync(a => a.MovementId == validTxId);
                Assert.NotNull(committedAlloc);
                Assert.Equal(5m, committedAlloc.AllocatedBaseQuantity);
                Assert.Equal(5m, committedAlloc.RemainingQuantityAfter);

                var finalContainer = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
                Assert.Equal(5m, finalContainer.RemainingBaseQuantity);
                Assert.Equal(2, finalContainer.Version);
                Assert.Equal(PackState.Open, finalContainer.PackState);

                var finalStock = await verifyDb.StockBalances.FirstAsync(s => s.ProductId == productId);
                Assert.Equal(5m, finalStock.SellableQty);

                var finalLot = await verifyDb.InventoryLotBucketBalances.FirstAsync(l => l.LotId == lotId);
                Assert.Equal(5m, finalLot.Quantity);
            }
        }
    }

    [Fact]
    public async Task T21_SerializedStocktake_ContainerMeasurementAndLooseStock_RealPostgres_AuthoritativeCountMovementAndReplayConflict()
    {
        var connStr = GetConnectionString();
        Guid productId;
        Guid containerId;
        Guid actorId;
        Guid categoryId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var openStocktakes = await seedDb.Stocktakes
                .Where(s => s.Status == StocktakeStatus.Draft || s.Status == StocktakeStatus.Counting || s.Status == StocktakeStatus.Review)
                .ToListAsync();
            foreach (var st in openStocktakes)
            {
                st.Cancel(DateTimeOffset.UtcNow);
            }
            if (openStocktakes.Count > 0)
            {
                await seedDb.SaveChangesAsync();
            }

            var (prod, pack, _, act) = await SeedPackContainerTestScenarioAsync(seedDb, initialQuantity: 100m, acquisitionCost: 250m);
            productId = prod.Id;
            containerId = pack.Id;
            actorId = act;

            var category = new Category
            {
                Name = "TestCat-" + Guid.NewGuid().ToString("N")[..8],
                IdentitySymbol = ("T" + Guid.NewGuid().ToString("N")[..7]).ToUpperInvariant(),
                IsActive = true
            };
            seedDb.Categories.Add(category);
            prod.CategoryId = category.Id;
            categoryId = category.Id;
            await seedDb.SaveChangesAsync();

            var existingCostState = await seedDb.ProductCostStates.FirstOrDefaultAsync(c => c.ProductId == productId);
            if (existingCostState is null)
            {
                seedDb.ProductCostStates.Add(new ProductCostState
                {
                    ProductId = productId,
                    CostedQty = 100m,
                    TotalInventoryCost = 250m,
                    MovingAverageCost = 2.50m,
                    LastPurchaseCost = 2.50m,
                    Version = 1
                });
                await seedDb.SaveChangesAsync();
            }
        }

        var clientOpId = Guid.NewGuid();
        Guid stocktakeId;

        await using (var db = CreateDbContext(connStr))
        {
            var clock = new SystemClock(KarachiZone, () => DateTimeOffset.UtcNow);
            var catalogRepo = new CatalogRepository(db);
            var invRepo = new InventoryRepository(db);
            var lockService = new PostgresOperationLock(db);
            var guard = new ProductionMaintenanceWriteGuard(new NoOpMaintenanceBarrier(), null);
            var txRunner = new EfTransactionRunner(db, guard);
            var outcomeLedger = new EfOperationOutcomeLedger(db);
            var costAllocator = new InventoryCostAllocator(db, invRepo, clock);

            var createHandler = new CreateStocktakeHandler(invRepo, txRunner, db, clock, EdgeRetails.Application.Features.Identity.AllowAllPermissionAuthorizer.Instance, outcomeLedger, lockService);
            var startHandler = new StartStocktakeHandler(catalogRepo, invRepo, lockService, txRunner, db, clock, lockService, outcomeLedger);
            var serializedCountHandler = new RecordSerializedStocktakeHandler(catalogRepo, invRepo, txRunner, db, clock, lockService, outcomeLedger);
            var reviewHandler = new ReviewStocktakeHandler(invRepo, txRunner, db, clock, lockService, outcomeLedger);
            var postHandler = new PostStocktakeHandler(catalogRepo, invRepo, costAllocator, txRunner, db, clock, lockService, outcomeLedger);

            // Step 1: Create and Start Stocktake on real PostgreSQL 18
            var createRes = await createHandler.HandleAsync(
                new CreateStocktakeCommand(StocktakeScope.Category, categoryId, actorId, "Real PG18 Container and loose stock reconciliation"),
                CancellationToken.None);
            Assert.True(createRes.IsSuccess, createRes.Error?.Message);
            stocktakeId = createRes.Value;

            var startRes = await startHandler.HandleAsync(new StartStocktakeCommand(stocktakeId, ActorId: actorId), CancellationToken.None);
            Assert.True(startRes.IsSuccess, startRes.Error?.Message);

            // Step 2: Initial count on real Postgres: Container measured remaining = 55.0m, Separate loose stock = 12.0m. Total counted = 67.0m.
            var measured = new Dictionary<Guid, decimal> { [containerId] = 55.0m };
            var countCmd1 = new RecordSerializedStocktakeCommand(
                stocktakeId,
                productId,
                [containerId],
                [],
                actorId,
                "Partial container plus loose cut ends",
                ClientOperationId: clientOpId,
                MeasuredContainerQuantities: measured,
                MinimumSellableIncrement: 1m,
                SeparateLooseQuantity: 12.0m);

            var countRes1 = await serializedCountHandler.HandleAsync(countCmd1, CancellationToken.None);
            Assert.True(countRes1.IsSuccess, countRes1.Error?.Message);

            // Verify StocktakeItem in real PostgreSQL
            var item = await invRepo.GetStocktakeItemForUpdateAsync(stocktakeId, productId, CancellationToken.None);
            Assert.NotNull(item);
            Assert.Equal(67.0m, item.CountedSellableQty);
            Assert.Equal(100.0m, item.ExpectedSellableQty);
            Assert.Equal(-33.0m, item.VarianceQty);
            Assert.NotNull(item.ReviewNote);
            Assert.Contains("[Loose stock counted: 12]", item.ReviewNote);

            // Step 3: Exact same measurement replay under same ClientOperationId preserves identity (idempotent replay)
            var replayRes = await serializedCountHandler.HandleAsync(countCmd1, CancellationToken.None);
            Assert.True(replayRes.IsSuccess, replayRes.Error?.Message);

            // Step 4: Changed measurement under same ClientOperationId triggers conflict
            var changedMeasurementCmd = countCmd1 with
            {
                MeasuredContainerQuantities = new Dictionary<Guid, decimal> { [containerId] = 60.0m }
            };
            var conflictRes1 = await serializedCountHandler.HandleAsync(changedMeasurementCmd, CancellationToken.None);
            Assert.False(conflictRes1.IsSuccess);
            Assert.Equal("inventory.stocktake_operation_conflict", conflictRes1.Error?.Code);

            // Changed loose count under same ClientOperationId also triggers conflict
            var changedLooseCmd = countCmd1 with
            {
                SeparateLooseQuantity = 15.0m
            };
            var conflictRes2 = await serializedCountHandler.HandleAsync(changedLooseCmd, CancellationToken.None);
            Assert.False(conflictRes2.IsSuccess);
            Assert.Equal("inventory.stocktake_operation_conflict", conflictRes2.Error?.Code);

            // Step 5: Negative loose count fails closed
            var negativeLooseCmd = countCmd1 with
            {
                ClientOperationId = Guid.NewGuid(),
                SeparateLooseQuantity = -5.0m
            };
            var negRes = await serializedCountHandler.HandleAsync(negativeLooseCmd, CancellationToken.None);
            Assert.False(negRes.IsSuccess);
            Assert.Equal("inventory.stocktake_loose_quantity_negative", negRes.Error?.Code);

            // Step 6: Review stocktake
            var reviewRes = await reviewHandler.HandleAsync(new ReviewStocktakeCommand(stocktakeId), CancellationToken.None);
            Assert.True(reviewRes.IsSuccess, reviewRes.Error?.Message);

            // Step 7: Post stocktake to durable PostgreSQL 18
            var postOpId = Guid.NewGuid();
            var postRes = await postHandler.HandleAsync(
                new PostStocktakeCommand(stocktakeId, actorId, null, postOpId),
                CancellationToken.None);
            Assert.True(postRes.IsSuccess, postRes.Error?.Message);
        }

        // Step 8: Assert direct, durable PostgreSQL 18 table effects
        await using (var verifyDb = CreateDbContext(connStr))
        {
            // Authoritative stock balance updated to 67.0m
            var balance = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == productId);
            Assert.Equal(67.0m, balance.SellableQty);

            // Authoritative movement recorded with -33.0m shrinkage
            var movement = await verifyDb.InventoryMovements.SingleAsync(m => m.ProductId == productId && m.ReferenceId == stocktakeId);
            Assert.Equal(InventoryMovementType.PhysicalCountCorrection, movement.MovementType);
            Assert.Equal(82.50m, movement.RecognizedLossAmount); // 33m * 2.50 = 82.50m

            var effect = await verifyDb.InventoryMovementEffects.SingleAsync(e => e.MovementId == movement.Id);
            Assert.Equal(-33.0m, effect.QuantityDelta);
            Assert.Equal(100.0m, effect.QuantityBefore);
            Assert.Equal(67.0m, effect.QuantityAfter);

            // Cost conservation in ProductCostState
            var costState = await verifyDb.ProductCostStates.FirstAsync(c => c.ProductId == productId);
            Assert.Equal(67.0m, costState.CostedQty);
            Assert.Equal(167.50m, costState.TotalInventoryCost); // 250m - 82.50m = 167.50m
            Assert.Equal(2.50m, costState.MovingAverageCost);

            // Verified stocktake status is Posted
            var st = await verifyDb.Stocktakes.FirstAsync(s => s.Id == stocktakeId);
            Assert.Equal(StocktakeStatus.Posted, st.Status);
        }
    }
}
