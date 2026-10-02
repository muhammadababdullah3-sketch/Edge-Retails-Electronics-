using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Audit;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Application.Production.Recovery;

namespace EdgeRetails.UnitTests;

public sealed class OwnerPinRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Authorized_recovery_changes_pin_revokes_sessions_and_audits_without_plaintext()
    {
        var setup = new RecoveryTestSetup();
        setup.AddActiveSession();

        var result = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "7316"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(setup.Sessions.Single().IsRevoked);
        Assert.Equal(Now, setup.Sessions.Single().EndedAt);
        var successAudit = Assert.Single(setup.Audit.Events, x => x.Action == "USER_PIN_RECOVERY_SUCCEEDED");
        Assert.NotEqual(Guid.Empty, successAudit.ActorId);
        Assert.Equal(Guid.Parse(setup.Authorization), successAudit.CorrelationId);
        Assert.DoesNotContain(setup.Audit.Events, x => (x.Summary ?? string.Empty).Contains("7316", StringComparison.Ordinal));
        Assert.DoesNotContain(setup.Audit.Events, x => (x.Summary ?? string.Empty).Contains("2580", StringComparison.Ordinal));

        var oldLogin = await setup.AuthenticateAsync("2580");
        var newLogin = await setup.AuthenticateAsync("7316");
        Assert.False(oldLogin.IsSuccess);
        Assert.True(newLogin.IsSuccess);
    }

    [Fact]
    public async Task Unauthorized_recovery_is_denied_and_audited_without_mutation()
    {
        var setup = new RecoveryTestSetup(authorizationValid: false);
        var originalHash = setup.User.PinHash.ToArray();

        var result = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "7316"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(originalHash, setup.User.PinHash);
        var deniedAudit = Assert.Single(setup.Audit.Events, x => x.Action == "USER_PIN_RECOVERY_DENIED");
        Assert.Equal(Guid.Empty, deniedAudit.ActorId);
        Assert.NotEqual(Guid.Empty, deniedAudit.CorrelationId);
        Assert.DoesNotContain(setup.Audit.Events, x => (x.Summary ?? string.Empty).Contains("7316", StringComparison.Ordinal));
        Assert.Equal(1, setup.User.Version);
    }

    [Theory]
    [InlineData(UserStatus.Disabled, "Owner")]
    [InlineData(UserStatus.Active, "Cashier")]
    public async Task Disabled_or_non_owner_target_is_rejected(UserStatus status, string roleName)
    {
        var setup = new RecoveryTestSetup(roleName: roleName);
        setup.User.Status = status;
        var originalHash = setup.User.PinHash.ToArray();

        var result = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "7316"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(originalHash, setup.User.PinHash);
        Assert.Contains(setup.Audit.Events, x => x.Action == "USER_PIN_RECOVERY_FAILED");
    }

    [Fact]
    public async Task Successful_operation_cannot_be_replayed()
    {
        var setup = new RecoveryTestSetup();
        var first = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "7316"),
            CancellationToken.None);
        var versionAfterFirst = setup.User.Version;
        var second = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "8427"),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("identity.recovery_replay", second.Error?.Code);
        Assert.Equal(versionAfterFirst, setup.User.Version);
        Assert.DoesNotContain(setup.Audit.Events, x => x.Action == "USER_PIN_RECOVERY_SUCCEEDED" && x.Summary!.Contains("8427", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_target_and_invalid_pin_are_rejected()
    {
        var setup = new RecoveryTestSetup();
        var missing = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(Guid.NewGuid().ToString(), Guid.NewGuid(), "7316"),
            CancellationToken.None);
        var malformed = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(Guid.NewGuid().ToString(), setup.User.Id, "7x16"),
            CancellationToken.None);

        Assert.False(missing.IsSuccess);
        Assert.False(malformed.IsSuccess);
        Assert.Contains(setup.Audit.Events, x => x.Action == "USER_PIN_RECOVERY_FAILED");
        Assert.DoesNotContain(setup.Audit.Events, x => (x.Summary ?? string.Empty).Contains("7x16", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Valid_authorization_is_consumed_when_pin_input_is_rejected()
    {
        var setup = new RecoveryTestSetup();
        var invalid = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "7x16"),
            CancellationToken.None);
        var replay = await setup.Handler.HandleAsync(
            new RecoverOwnerPinCommand(setup.Authorization, setup.User.Id, "7316"),
            CancellationToken.None);

        Assert.False(invalid.IsSuccess);
        Assert.False(replay.IsSuccess);
        Assert.Equal("identity.recovery_replay", replay.Error?.Code);
        Assert.Contains(setup.Audit.Events, x => x.Action == "USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED");
        Assert.Equal(1, setup.User.Version);
        Assert.False(new Pbkdf2PinCredentialService().Verify("7316", new PinCredential(
            setup.User.PinHash, setup.User.PinSalt, setup.User.PinIterations, setup.User.PinAlgorithm)));
    }

    [Fact]
    public void Recovery_success_correlation_has_unique_filtered_database_index()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata_only;Username=metadata;Password=metadata")
            .Options;
        using var db = new EdgeRetailsDbContext(options);
        var indexes = db.Model.FindEntityType(typeof(BusinessAuditEvent))!.GetIndexes().ToArray();
        Assert.Contains(indexes, x => x.GetDatabaseName() == "ux_business_events_pin_recovery_success_operation");
        var index = indexes.Single(x => x.GetDatabaseName() == "ux_business_events_pin_recovery_success_operation");

        Assert.True(index.IsUnique);
        Assert.Equal(new[] { nameof(BusinessAuditEvent.CorrelationId), nameof(BusinessAuditEvent.Action) }, index.Properties.Select(x => x.Name));
        Assert.Contains("USER_PIN_RECOVERY_SUCCEEDED", index.GetFilter(), StringComparison.Ordinal);

        var consumedIndex = db.Model.FindEntityType(typeof(BusinessAuditEvent))!
            .GetIndexes()
            .Single(x => x.GetDatabaseName() == "ux_business_events_pin_recovery_consumed_nonce");
        Assert.True(consumedIndex.IsUnique);
        Assert.Contains("USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED", consumedIndex.GetFilter(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_database_resolution_uses_server_key_precedence_and_layered_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "base-default",
                ["EDGE_RETAILS_DB"] = "base-env-key",
                ["DatabaseConnectionString"] = "base-alias"
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EDGE_RETAILS_DB"] = "programdata-override"
            })
            .Build();

        Assert.Equal("base-default", ProductionDatabaseConnectionStringResolver.Resolve(configuration));

        var aliasConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EDGE_RETAILS_DB"] = "programdata-override",
                ["DatabaseConnectionString"] = "base-alias"
            })
            .Build();
        Assert.Equal("programdata-override", ProductionDatabaseConnectionStringResolver.Resolve(aliasConfiguration));
    }

    [Fact]
    public void Recovery_database_resolution_does_not_fall_back_to_interactive_process_environment()
    {
        var previous = Environment.GetEnvironmentVariable("EDGE_RETAILS_DB");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_DB", "interactive-user-override");
            var emptyConfiguration = new ConfigurationBuilder().Build();

            Assert.Throws<InvalidOperationException>(() =>
                ProductionDatabaseConnectionStringResolver.Resolve(
                    emptyConfiguration,
                    allowProcessEnvironmentFallback: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_DB", previous);
        }
    }

    private sealed class RecoveryTestSetup
    {
        private readonly Pbkdf2PinCredentialService _pins = new();
        private readonly FakeIdentityRepository _identity;
        private readonly FakeRecoveryRepository _recovery;
        private readonly FakeSessionRepository _sessionsRepository = new();
        private readonly FakeClock _clock = new();

        public RecoveryTestSetup(bool authorizationValid = true, string roleName = "Owner")
        {
            var original = _pins.Hash("2580");
            User = new User
            {
                DisplayName = "Test Owner",
                RoleId = Guid.NewGuid(),
                PinHash = original.Hash,
                PinSalt = original.Salt,
                PinIterations = original.Iterations,
                PinAlgorithm = original.Algorithm,
                Status = UserStatus.Active,
                UpdatedAt = Now,
                Version = 1
            };
            Role = new Role { Id = User.RoleId, Name = roleName, IsActive = true };
            _identity = new FakeIdentityRepository(User, Role);
            _recovery = new FakeRecoveryRepository(User, Audit);
            Authorization = Guid.NewGuid().ToString();
            UnitOfWork = new FakeUnitOfWork();
            Handler = new RecoverOwnerPinHandler(
                _recovery,
                _identity,
                new FakeRecoveryAuthorizationValidator(authorizationValid, Guid.Parse(Authorization)),
                _pins,
                Audit,
                _clock);
        }

        public User User { get; }
        public Role Role { get; }
        public string Authorization { get; }
        public FakeAuditWriter Audit { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; }
        public RecoverOwnerPinHandler Handler { get; }
        public List<UserSession> Sessions => _recovery.Sessions;

        public void AddActiveSession() => _recovery.AddSession(new UserSession { UserId = User.Id, IsRevoked = false });

        public Task<EdgeRetails.Application.Common.Result<AuthenticatedUserDto>> AuthenticateAsync(string pin) =>
            new AuthenticateUserHandler(_identity, _sessionsRepository, _pins, Audit, _clock, UnitOfWork)
                .HandleAsync(new AuthenticateUserCommand(User.Id, pin, Guid.NewGuid()), CancellationToken.None);
    }

    private sealed class FakeRecoveryAuthorizationValidator(bool isValid, Guid nonce) : IRecoveryAuthorizationValidator
    {
        public Task<RecoveryAuthorizationValidation> ValidateAsync(string? signedAuthorization, Guid targetUserId, CancellationToken cancellationToken) =>
            Task.FromResult(isValid
                ? RecoveryAuthorizationValidation.Valid(new VerifiedRecoveryAuthorization("test-governance-issuer", nonce, targetUserId, Now.AddMinutes(5)))
                : RecoveryAuthorizationValidation.Failed("recovery.authorization_not_provisioned"));
    }

    private sealed class FakeRecoveryRepository(User user, FakeAuditWriter audit) : IIdentityCredentialRecoveryRepository
    {
        private readonly List<UserSession> _sessions = [];

        public Task<IReadOnlyList<PinRecoveryTarget>> GetActiveOwnerTargetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PinRecoveryTarget>>([new PinRecoveryTarget(user.Id, user.DisplayName)]);
        public Task<User?> GetUserForRecoveryUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<User?>(user.Id == userId ? user : null);
        public Task<bool> HasRecoveryOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(audit.Events.Any(x => x.Action == "USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED" && x.CorrelationId == operationId));
        public Task RevokeActiveSessionsAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
        {
            foreach (var session in _sessions.Where(x => x.UserId == userId && !x.IsRevoked && x.EndedAt is null))
            {
                session.IsRevoked = true;
                session.EndedAt = revokedAt;
            }

            return Task.CompletedTask;
        }
        public Task<bool> TrySaveRecoveryChangesAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public void AddSession(UserSession session) => _sessions.Add(session);
        public List<UserSession> Sessions => _sessions;
    }

    private sealed class FakeIdentityRepository(User user, Role role) : IIdentityReadRepository
    {
        public Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<User>>(user.Status == UserStatus.Active ? [user] : []);
        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<User?>(user.Id == userId ? user : null);
        public Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
            Task.FromResult<Role?>(role.Id == roleId ? role : null);
        public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Role>>([role]);
        public Task<IReadOnlyList<string>> GetRolePermissionKeysAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Permission>>([]);
        public Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class FakeSessionRepository : IIdentitySessionRepository
    {
        private readonly List<UserSession> _sessions = [];
        public List<UserSession> Items => _sessions;
        public void AddSession(UserSession session) => _sessions.Add(session);
        public Task<UserSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult<UserSession?>(_sessions.SingleOrDefault(x => x.Id == sessionId));
        public Task<UserSession?> GetSessionForUpdateAsync(Guid sessionId, CancellationToken cancellationToken) =>
            GetSessionAsync(sessionId, cancellationToken);
    }

    private sealed class FakeAuditWriter : IBusinessAuditWriter
    {
        public List<BusinessAuditEvent> Events { get; } = [];
        public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null) =>
            Events.Add(new BusinessAuditEvent
            {
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                ActorId = actorId,
                CorrelationId = correlationId,
                OccurredAt = Now,
                Summary = summary
            });
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
        public DateOnly ShopDate => DateOnly.FromDateTime(Now.UtcDateTime);
    }
}
