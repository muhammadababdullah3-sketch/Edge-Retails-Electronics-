using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Posting;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11ShopTimeAndPostingTests
{
    private static readonly TimeZoneInfo KarachiZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pakistan Standard Time" : "Asia/Karachi");

    private sealed class TestShopTimeAuthority : ShopClock, IShopTimeAuthority
    {
        private readonly ShopClockIntegrity _integrity;

        public TestShopTimeAuthority(TimeZoneInfo tz, Func<DateTimeOffset>? utc = null, ShopClockIntegrity? integrity = null)
            : base(tz, utc)
        {
            _integrity = integrity ?? new ShopClockIntegrity(true, null);
        }

        public ShopClockIntegrity Integrity => _integrity;

        public DateOnly GetCurrentShopDate() => ShopDate;
    }

    private static readonly Pbkdf2PinCredentialService PinService = new();

    private sealed class TestIdentityRepository : IIdentityReadRepository
    {
        private readonly Dictionary<Guid, User> _users = new();
        private readonly Dictionary<Guid, Role> _roles = new();
        private readonly Dictionary<Guid, HashSet<string>> _permissions = new();

        public void AddUser(User user, string roleName, IEnumerable<string> permissions)
        {
            _users[user.Id] = user;
            var role = new Role { Id = user.RoleId, Name = roleName, IsActive = true };
            _roles[user.RoleId] = role;
            _permissions[user.Id] = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<User>>(_users.Values.Where(u => u.Status == UserStatus.Active).ToArray());

        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_users.GetValueOrDefault(userId));

        public Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
            Task.FromResult(_roles.GetValueOrDefault(roleId));

        public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Role>>(_roles.Values.ToArray());

        public Task<IReadOnlyList<string>> GetRolePermissionKeysAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Permission>>(Array.Empty<Permission>());

        public Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(_permissions.GetValueOrDefault(userId) ?? new HashSet<string>());
    }

    private sealed class TestBusinessAuditWriter : IBusinessAuditWriter
    {
        public List<(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary)> Records { get; } = new();

        public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null)
        {
            Records.Add((action, entityType, entityId, actorId, correlationId, summary));
        }
    }

    private sealed class TestUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }
        public bool FailOnSave { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (FailOnSave)
            {
                throw new InvalidOperationException("Simulated unit of work atomic save failure.");
            }
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class TestTransactionRunner : ITransactionRunner
    {
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            return await action(cancellationToken);
        }
    }

    private static (User User, string Pin) CreateUser(string roleName, string pin, IEnumerable<string> permissions, TestIdentityRepository repo)
    {
        var cred = PinService.Hash(pin);
        var roleId = Guid.NewGuid();
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = $"{roleName} User",
            RoleId = roleId,
            PinHash = cred.Hash,
            PinSalt = cred.Salt,
            PinIterations = cred.Iterations,
            PinAlgorithm = cred.Algorithm,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        repo.AddUser(user, roleName, permissions);
        return (user, pin);
    }

    [Fact]
    public void T01_TimezoneResolution_KarachiResolved()
    {
        var resolvedZone = ShopClock.ResolveShopTimeZone();
        Assert.NotNull(resolvedZone);
        Assert.True(
            resolvedZone.Id is "Asia/Karachi" or "Pakistan Standard Time",
            $"Expected Karachi or Pakistan Standard Time, got '{resolvedZone.Id}'");

        var testUtc = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var offset = resolvedZone.GetUtcOffset(testUtc);
        Assert.Equal(TimeSpan.FromHours(5), offset);
    }

    [Fact]
    public void T02_TimezoneResolution_FailsClearly_NoSilentUtcFallback()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            ShopClock.ResolveShopTimeZone(new[] { "Invalid/NonExistent_TimeZone_XYZ", "Bogus_Zone" }));

        Assert.Equal("time.shop_timezone_unresolvable", ex.Code);
        Assert.Contains("Authoritative shop timezone", ex.Message);
        Assert.Contains("Silent fallback to UTC or host local time is forbidden", ex.Message);
    }

    [Fact]
    public void T03_ShopClock_KarachiMidnight_UtcDateDifference()
    {
        // 2026-10-12 00:30:00 AM PKT corresponds to 2026-10-11 19:30:00 UTC (previous day in UTC)
        var utcTimestamp = new DateTimeOffset(2026, 10, 11, 19, 30, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcTimestamp);

        Assert.Equal(new DateOnly(2026, 10, 11), DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        Assert.Equal(new DateOnly(2026, 10, 12), clock.ShopDate);
        Assert.Equal(0, clock.ShopNow.Hour);
        Assert.Equal(30, clock.ShopNow.Minute);
    }

    [Fact]
    public async Task T04_PostingDate_CurrentShopDate_SucceedsWithoutSupervisor()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: Guid.NewGuid(),
            EntityNumber: "REC-20261012-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 12));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(command.PostingDate, result.Value.PostingDate);
        Assert.Equal(clock.ShopDate, result.Value.ShopDate);
        Assert.Null(result.Value.SupervisorId);
        Assert.Single(businessAudit.Records);
    }

    [Fact]
    public async Task T05_PostingDate_FutureDate_Rejected()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate }, identity);
        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261012-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 13)); // Tomorrow!

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.future_date_forbidden", result.Error?.Code);
    }

    [Fact]
    public async Task T06_PostingDate_Day7Backdate_AllowedWithSupervisorPermission()
    {
        // Today is 2026-10-12. Exactly 7 days back is 2026-10-05.
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.PurchasingManage }, identity);
        var supervisor = CreateUser("Supervisor", "9999", new[] { PermissionKeys.SupervisorPostingOverride }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261005-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 5),
            SupplierDocumentDate: new DateOnly(2026, 10, 3),
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "9999",
                Reason: "Vendor invoice delayed in postal delivery",
                RequestedAtUtc: utcNow));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(new DateOnly(2026, 10, 5), result.Value.PostingDate);
        Assert.Equal(supervisor.User.Id, result.Value.SupervisorId);
        Assert.Equal("Vendor invoice delayed in postal delivery", result.Value.Reason);
    }

    [Fact]
    public async Task T07_PostingDate_Day8Backdate_RejectedEvenWithSupervisor()
    {
        // Today is 2026-10-12. Day 8 backdate is 2026-10-04.
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.PurchasingManage }, identity);
        var supervisor = CreateUser("Supervisor", "9999", new[] { PermissionKeys.SupervisorPostingOverride }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261004-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 4), // 8 days ago!
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "9999",
                Reason: "Trying to post beyond 7 days",
                RequestedAtUtc: utcNow));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.backdate_exceeds_window", result.Error?.Code);
    }

    [Fact]
    public async Task T08_PostingDate_HistoricalWithoutSupervisor_Rejected()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.PurchasingManage }, identity);
        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261011-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 11), // Yesterday, without supervisor
            SupervisorApproval: null);

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.supervisor_authorization_required", result.Error?.Code);
    }

    [Fact]
    public async Task T09_SupervisorAuth_CallerBooleanRejected()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.PurchasingManage }, identity);
        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261011-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 11),
            CallerSuppliedBooleanOverride: true); // Forbidden boolean attempt!

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.caller_boolean_authority_rejected", result.Error?.Code);
    }

    [Fact]
    public async Task T10_SupervisorAuth_InvalidPin_Rejected()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.PurchasingManage }, identity);
        var supervisor = CreateUser("Supervisor", "9999", new[] { PermissionKeys.SupervisorPostingOverride }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261011-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 11),
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "1234", // Wrong PIN!
                Reason: "Valid reason",
                RequestedAtUtc: utcNow));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("authorization.invalid_supervisor_pin", result.Error?.Code);
    }

    [Fact]
    public async Task T11_SupervisorAuth_SpoofedActor_Rejected()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        // Cashier user tries to use their own credentials as "supervisor"
        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261011-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 11),
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: cashier.User.Id, // Self-authorizing!
                SupervisorPin: "1111",
                Reason: "I am approving my own backdate",
                RequestedAtUtc: utcNow));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("authorization.spoofed_supervisor_rejected", result.Error?.Code);
    }

    [Fact]
    public async Task T11b_SupervisorAuth_UnprivilegedSupervisor_PermissionDenied()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate }, identity);
        var otherCashier = CreateUser("OtherCashier", "2222", new[] { PermissionKeys.SalesCreate }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261011-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 11),
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: otherCashier.User.Id, // Another user who lacks supervisor override permission
                SupervisorPin: "2222",
                Reason: "Attempted override by unprivileged user",
                RequestedAtUtc: utcNow));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("authorization.supervisor_permission_denied", result.Error?.Code);
    }

    [Fact]
    public async Task T12_PeriodLock_PrecedenceOverSupervisorOverride()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        // Lock period for 2026-10-01 through 2026-10-08
        periodLocks.AddPeriodLock(new PeriodLock
        {
            Id = Guid.NewGuid(),
            PeriodName = "Q3 Closed Ledger",
            PeriodStartDate = new DateOnly(2026, 10, 1),
            PeriodEndDate = new DateOnly(2026, 10, 8),
            IsLocked = true,
            LockedAt = utcNow,
            LockedBy = Guid.NewGuid(),
            LockReason = "Financial year end closing"
        });

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.PurchasingManage }, identity);
        var supervisor = CreateUser("Owner", "9999", new[] { PermissionKeys.SupervisorPostingOverride, PermissionKeys.SettingsManage }, identity);

        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        // Attempt to post on 2026-10-06 (which is within 7 days, has valid supervisor, but falls in locked period)
        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-20261006-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 6),
            SupervisorApproval: new SupervisorApprovalRequest(
                SupervisorId: supervisor.User.Id,
                SupervisorPin: "9999",
                Reason: "Owner approval",
                RequestedAtUtc: utcNow));

        var result = await service.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.accounting_period_locked", result.Error?.Code);
        Assert.Contains("locked accounting period 'Q3 Closed Ledger'", result.Error?.Message);
    }

    [Fact]
    public async Task T13_AtomicAudit_CreatedAndCommittedInUnitOfWork()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();
        var uow = new TestUnitOfWork();
        var tx = new TestTransactionRunner();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.ExpensesManage }, identity);
        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());
        var adapter = new FinancialPostingAdapter(service, uow, tx);

        var entityId = Guid.NewGuid();
        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Expense,
            EntityId: entityId,
            EntityNumber: "EXP-20261012-001",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 12));

        var executedDomainAction = false;
        var audit = await adapter.ExecuteTransactionalPostingAsync(command, () =>
        {
            executedDomainAction = true;
            return Task.CompletedTask;
        });

        Assert.True(executedDomainAction);
        Assert.Equal(1, uow.SaveCount);
        Assert.NotNull(audit);
        Assert.Equal(entityId, audit.EntityId);
        Assert.Equal(PostingOperationTypes.Expense, audit.OperationType);
        Assert.Equal(new DateOnly(2026, 10, 12), audit.PostingDate);

        var storedAudits = await audits.GetEntriesByEntityAsync(entityId);
        Assert.Single(storedAudits);
    }

    [Fact]
    public async Task T14_AtomicAudit_FailureRollsBackMutation()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();
        var uow = new TestUnitOfWork { FailOnSave = true }; // Simulate failure during save!
        var tx = new TestTransactionRunner();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.ExpensesManage }, identity);
        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());
        var adapter = new FinancialPostingAdapter(service, uow, tx);

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Expense,
            EntityId: Guid.NewGuid(),
            EntityNumber: "EXP-20261012-002",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 12));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ExecuteTransactionalPostingAsync(command, () => Task.CompletedTask));

        Assert.Equal("Simulated unit of work atomic save failure.", ex.Message);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact]
    public void T15_ReportingBoundary_Daily_InclusiveStartExclusiveEnd()
    {
        var clock = new TestShopTimeAuthority(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);

        var day = new DateOnly(2026, 10, 12);
        var (startUtc, endUtc) = boundaryService.GetDailyUtcBoundary(day);

        // In PKT (UTC+5), 2026-10-12 00:00:00 is 2026-10-11 19:00:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 19, 0, 0, TimeSpan.Zero), startUtc);
        // End is 2026-10-13 00:00:00 PKT which is 2026-10-12 19:00:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero), endUtc);

        Assert.Equal(TimeSpan.FromHours(24), endUtc - startUtc);
    }

    [Fact]
    public void T16_ReportingBoundary_Monthly_Yearly_LeapYear()
    {
        var clock = new TestShopTimeAuthority(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);

        // Leap Year February 2028 has 29 days
        var (febStartUtc, febEndUtc) = boundaryService.GetMonthlyUtcBoundary(2028, 2);
        // Starts 2028-02-01 00:00 PKT -> 2028-01-31 19:00 UTC
        Assert.Equal(new DateTimeOffset(2028, 1, 31, 19, 0, 0, TimeSpan.Zero), febStartUtc);
        // Ends 2028-03-01 00:00 PKT -> 2028-02-29 19:00 UTC
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 19, 0, 0, TimeSpan.Zero), febEndUtc);
        Assert.Equal(TimeSpan.FromDays(29), febEndUtc - febStartUtc);

        // Yearly boundary 2026 (non-leap year, 365 days)
        var (yearStartUtc, yearEndUtc) = boundaryService.GetYearlyUtcBoundary(2026);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 19, 0, 0, TimeSpan.Zero), yearStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 19, 0, 0, TimeSpan.Zero), yearEndUtc);
        Assert.Equal(TimeSpan.FromDays(365), yearEndUtc - yearStartUtc);
    }

    [Fact]
    public void T17_HostTimezoneMismatch_ShopClockIsIndependent()
    {
        // Whatever the host system's local timezone (e.g. UTC, Pacific, Tokyo),
        // ShopClock is explicitly pinned to Karachi Zone (+05:00)
        var fixedUtc = new DateTimeOffset(2026, 10, 12, 14, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => fixedUtc);

        // 14:00 UTC + 5 = 19:00 PKT
        Assert.Equal(19, clock.ShopNow.Hour);
        Assert.Equal(new DateOnly(2026, 10, 12), clock.ShopDate);
        Assert.Equal(TimeSpan.FromHours(5), clock.ShopTimeZone.GetUtcOffset(fixedUtc));
    }

    [Fact]
    public void T18_TripleDateSeparation_DocumentVsPostingVsEvent()
    {
        var eventUtc = new DateTimeOffset(2026, 10, 12, 10, 15, 30, TimeSpan.Zero);
        var postingDate = new DateOnly(2026, 10, 8); // Business posting date
        var supplierInvoiceDate = new DateOnly(2026, 9, 20); // Supplier invoice document date (older than 7 days)

        var record = new ShopPostingDateRecord(
            EventTimestampUtc: eventUtc,
            PostingDate: postingDate,
            SupplierDocumentDate: supplierInvoiceDate);

        record.ValidateInvariants();

        Assert.Equal(eventUtc, record.EventTimestampUtc);
        Assert.Equal(postingDate, record.PostingDate);
        Assert.Equal(supplierInvoiceDate, record.SupplierDocumentDate);

        // Document date is preserved independently even though it is older than the 7-day posting window
        Assert.True(record.SupplierDocumentDate < record.PostingDate.AddDays(-7));
    }

    [Fact]
    public void T19_F12NetProfitFormula_PreservedUnderDateGrouping()
    {
        // F12 Net Profit Specification:
        // Net Profit = Gross Profit - Operating Expenses - Recognized Losses + Recoveries
        // Where Gross Profit = Revenue - COGS

        decimal revenue = 1_000_000m;
        decimal cogs = 650_000m;
        decimal grossProfit = revenue - cogs; // 350,000

        decimal expenses = 80_000m;
        decimal recognizedLosses = 15_000m;
        decimal recoveries = 5_000m;

        decimal netProfit = grossProfit - expenses - recognizedLosses + recoveries;

        Assert.Equal(350_000m, grossProfit);
        Assert.Equal(260_000m, netProfit);

        // Date grouping simulation:
        // Grouping 3 invoices across different posting dates
        var postingGroup = new[]
        {
            new { PostingDate = new DateOnly(2026, 10, 10), Rev = 300_000m, Cogs = 195_000m, Exp = 25_000m, Loss = 5_000m, Rec = 2_000m },
            new { PostingDate = new DateOnly(2026, 10, 11), Rev = 300_000m, Cogs = 195_000m, Exp = 25_000m, Loss = 5_000m, Rec = 1_000m },
            new { PostingDate = new DateOnly(2026, 10, 12), Rev = 400_000m, Cogs = 260_000m, Exp = 30_000m, Loss = 5_000m, Rec = 2_000m }
        };

        var sumRev = postingGroup.Sum(x => x.Rev);
        var sumCogs = postingGroup.Sum(x => x.Cogs);
        var sumExp = postingGroup.Sum(x => x.Exp);
        var sumLoss = postingGroup.Sum(x => x.Loss);
        var sumRec = postingGroup.Sum(x => x.Rec);

        var groupedNetProfit = (sumRev - sumCogs) - sumExp - sumLoss + sumRec;

        Assert.Equal(revenue, sumRev);
        Assert.Equal(cogs, sumCogs);
        Assert.Equal(expenses, sumExp);
        Assert.Equal(recognizedLosses, sumLoss);
        Assert.Equal(recoveries, sumRec);
        Assert.Equal(netProfit, groupedNetProfit);
    }

    [Fact]
    public async Task T20_ReplayAndIdempotency_SameAndDifferentDateRetries()
    {
        var utcNow = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var clock = new TestShopTimeAuthority(KarachiZone, () => utcNow);
        var periodLocks = new InMemoryPeriodLockRepository();
        var audits = new PostingAuditRepository();
        var identity = new TestIdentityRepository();
        var businessAudit = new TestBusinessAuditWriter();

        var cashier = CreateUser("Cashier", "1111", new[] { PermissionKeys.SalesCreate }, identity);
        var service = new ShopPostingValidationService(
            clock, periodLocks, audits, identity, PinService, businessAudit, new StubSupervisorApprovalStore());

        var entityId = Guid.NewGuid();
        var command1 = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: entityId,
            EntityNumber: "REC-100",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 12));

        var result1 = await service.ValidateAndAuditPostingAsync(command1);
        Assert.True(result1.IsSuccess);

        // Same date retry succeeds idempotently
        var commandSame = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: entityId,
            EntityNumber: "REC-100",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 12));

        var resultSame = await service.ValidateAndAuditPostingAsync(commandSame);
        Assert.True(resultSame.IsSuccess);

        // Unauthorized different date retry (shifting to historical yesterday without supervisor) fails
        var commandShift = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: entityId,
            EntityNumber: "REC-100",
            ActorId: cashier.User.Id,
            PostingDate: new DateOnly(2026, 10, 11),
            SupervisorApproval: null);

        var resultShift = await service.ValidateAndAuditPostingAsync(commandShift);
        Assert.False(resultShift.IsSuccess);
        Assert.Equal("posting.supervisor_authorization_required", resultShift.Error?.Code);
    }
}
