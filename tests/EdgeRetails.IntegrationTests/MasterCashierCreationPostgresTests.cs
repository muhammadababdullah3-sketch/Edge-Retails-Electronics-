using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

[Collection("MasterSupplierSequence")]
public sealed class MasterCashierCreationPostgresTests
{
    // NEW_COVERAGE: real PostgreSQL transactions, separate-scope concurrency, durable replay,
    // authority revocation and rollback after the canonical ledger has flushed all three records.
    // All credentials below are synthetic fixtures. No PIN or connection is written to test output.
    private const string SyntheticPin = "1248";

    [Fact]
    public async Task ConcurrentSameOperation_SeparateScopesCommitOneUserAuditAndOutcome()
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var command = seed.Command();
        var results = await ConcurrentAsync(provider, command, command);
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Value!.UserId, results[1].Value!.UserId);
        await AssertCommittedOnceAsync(provider, seed, command, results[0].Value!.UserId);
    }

    [Fact]
    public async Task ConcurrentDifferentOperations_NormalizedSameNameCreatesExactlyOneCashier()
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var first = seed.Command();
        var second = seed.Command() with { DisplayName = "  " + seed.Name.ToUpperInvariant() + "  " };
        var results = await ConcurrentAsync(provider, first, second);
        var successful = Assert.Single(results, result => result.IsSuccess);
        var rejected = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal("identity.display_name_duplicate", rejected.Error!.Code);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var normalized = seed.Name.ToUpperInvariant();
        Assert.Equal(1, await db.Users.CountAsync(user => user.DisplayName.Trim().ToUpper() == normalized));
        Assert.Equal(1, await db.BusinessAuditEvents.CountAsync(audit => audit.ActorId == seed.ActorId && audit.Action == "USER_CREATED"));
        var outcomes = await db.OperationOutcomes.AsNoTracking()
            .Where(outcome => outcome.ClientOperationId == first.ClientOperationId || outcome.ClientOperationId == second.ClientOperationId)
            .ToArrayAsync();
        var committed = Assert.Single(outcomes);
        Assert.Equal(successful.Value!.UserId, committed.ResultEntityId);
        Assert.Equal(OperationOutcomeStatus.Succeeded, committed.Status);
        Assert.True(committed.WasCommitted);
    }

    [Fact]
    public async Task LostResponse_ReplayAfterProviderRestartReturnsOriginalCommittedCashier()
    {
        Seed seed;
        CreateCashierCommand command;
        Guid originalUserId;
        await using (var provider = await OwnedProviderAsync())
        {
            seed = await SeedAsync(provider);
            command = seed.Command();
            var result = await CreateAsync(provider, command);
            Assert.True(result.IsSuccess);
            originalUserId = result.Value!.UserId;
            // Simulate an application losing the response after the transaction committed.
            // No result or credential is persisted by the fixture; only the stable operation is replayed.
        }
        await using var restarted = await OwnedProviderAsync();
        var replay = await CreateAsync(restarted, command);
        Assert.True(replay.IsSuccess);
        Assert.Equal(originalUserId, replay.Value!.UserId);
        await AssertCommittedOnceAsync(restarted, seed, command, originalUserId);
    }

    [Fact]
    public async Task ChangedPinReplay_IsRejectedWithoutChangingCredentialOrCreatingAnotherRecord()
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var command = seed.Command();
        var original = await CreateAsync(provider, command);
        Assert.True(original.IsSuccess);
        var replay = await CreateAsync(provider, command with { Pin = "2468" });
        Assert.False(replay.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", replay.Error!.Code);
        await AssertCommittedOnceAsync(provider, seed, command, original.Value!.UserId);
    }

    [Fact]
    public async Task OwnerPermissionOverrideRevocation_BlocksFreshCreateAndPreviouslySuccessfulReplay()
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var command = seed.Command();
        var original = await CreateAsync(provider, command);
        Assert.True(original.IsSuccess);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                UserId = seed.ActorId,
                PermissionId = seed.SettingsPermissionId,
                IsAllowed = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = 1
            });
            await db.SaveChangesAsync();
        }
        var deniedReplay = await CreateAsync(provider, command);
        Assert.Equal("authorization.denied", deniedReplay.Error!.Code);
        var deniedFresh = seed.Command() with { DisplayName = seed.Name + " denied" };
        Assert.Equal("authorization.denied", (await CreateAsync(provider, deniedFresh)).Error!.Code);
        await AssertAbsentAsync(provider, deniedFresh);
        await AssertCommittedOnceAsync(provider, seed, command, original.Value!.UserId);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("ended")]
    [InlineData("missing")]
    public async Task InvalidSession_FailsWithoutUserAuditOrOutcome(string fault)
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var command = seed.Command();
        if (fault == "missing")
        {
            command = command with { SessionId = Guid.CreateVersion7() };
        }
        else
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var session = await db.UserSessions.SingleAsync(row => row.Id == seed.SessionId);
            session.IsRevoked = fault == "revoked";
            session.EndedAt = fault == "ended" ? DateTimeOffset.UtcNow : null;
            await db.SaveChangesAsync();
        }
        var rejected = await CreateAsync(provider, command);
        Assert.False(rejected.IsSuccess);
        Assert.Equal("auth.session_invalid", rejected.Error!.Code);
        await AssertAbsentAsync(provider, command);
    }

    [Fact]
    public async Task ExactInjectedFailureAfterLedgerFlush_RollsBackAllRecordsThenSameOperationSucceeds()
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var command = seed.Command();
        var injected = new InjectedCashierCommitException();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var failing = new FailingUnitOfWork(db, command, injected);
            var handler = ActivatorUtilities.CreateInstance<CreateCashierHandler>(scope.ServiceProvider, failing);
            var observed = await Assert.ThrowsAsync<InjectedCashierCommitException>(() => handler.HandleAsync(command, default));
            Assert.Same(injected, observed);
            Assert.Equal("Injected isolated Cashier failure after user, audit and outcome flush.", observed.Message);
            Assert.True(failing.ObservedFlushedState);
            Assert.Null(db.Database.CurrentTransaction);
        }
        await AssertAbsentAsync(provider, command);
        var retried = await CreateAsync(provider, command);
        Assert.True(retried.IsSuccess);
        await AssertCommittedOnceAsync(provider, seed, command, retried.Value!.UserId);
    }

    [Fact]
    public async Task SuccessfulCommit_PreservesCanonicalRoleHashAndRedactedAtomicMetadata()
    {
        await using var provider = await OwnedProviderAsync();
        var seed = await SeedAsync(provider);
        var command = seed.Command();
        var created = await CreateAsync(provider, command);
        Assert.True(created.IsSuccess);
        await AssertCommittedOnceAsync(provider, seed, command, created.Value!.UserId);
        Assert.Equal("Cashier", created.Value.RoleName);
        Assert.DoesNotContain("Pin", JsonSerializer.Serialize(created.Value), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pin =", command.ToString(), StringComparison.Ordinal);
    }

    private static async Task<Result<CreatedCashierDto>> CreateAsync(ServiceProvider provider, CreateCashierCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<CreateCashierHandler>().HandleAsync(command, default);
    }

    private static async Task<Result<CreatedCashierDto>[]> ConcurrentAsync(ServiceProvider provider,
        CreateCashierCommand first, CreateCashierCommand second)
    {
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();
        Assert.NotSame(firstScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>(),
            secondScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>());
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task<Result<CreatedCashierDto>> Run(IServiceProvider services, CreateCashierCommand command)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
            {
                ready.TrySetResult();
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await services.GetRequiredService<CreateCashierHandler>().HandleAsync(command, default);
        }
        return await Task.WhenAll(Run(firstScope.ServiceProvider, first), Run(secondScope.ServiceProvider, second));
    }

    private static async Task AssertAbsentAsync(ServiceProvider provider, CreateCashierCommand command)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var name = command.DisplayName.Trim().ToUpperInvariant();
        Assert.Equal(0, await db.Users.CountAsync(user => user.DisplayName.Trim().ToUpper() == name));
        Assert.Equal(0, await db.BusinessAuditEvents.CountAsync(audit => audit.CorrelationId == command.CorrelationId));
        Assert.Equal(0, await db.OperationOutcomes.CountAsync(outcome => outcome.ClientOperationId == command.ClientOperationId));
    }

    private static async Task AssertCommittedOnceAsync(ServiceProvider provider, Seed seed,
        CreateCashierCommand command, Guid userId)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var normalized = seed.Name.ToUpperInvariant();
        var user = Assert.Single(await db.Users.AsNoTracking().Where(row => row.DisplayName.Trim().ToUpper() == normalized).ToArrayAsync());
        Assert.Equal(userId, user.Id);
        Assert.Equal(seed.CashierRoleId, user.RoleId);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal("PBKDF2-SHA256", user.PinAlgorithm);
        Assert.True(user.PinIterations >= 210_000);
        Assert.Equal(32, user.PinHash.Length);
        Assert.Equal(16, user.PinSalt.Length);
        Assert.True(scope.ServiceProvider.GetRequiredService<IPinCredentialService>()
            .Verify(SyntheticPin, new(user.PinHash, user.PinSalt, user.PinIterations, user.PinAlgorithm)));
        var audit = Assert.Single(await db.BusinessAuditEvents.AsNoTracking()
            .Where(row => row.ActorId == seed.ActorId && row.Action == "USER_CREATED").ToArrayAsync());
        Assert.Equal(userId, audit.EntityId);
        Assert.Equal(command.CorrelationId, audit.CorrelationId);
        Assert.Equal("Active Cashier account created.", audit.Summary);
        var outcome = Assert.Single(await db.OperationOutcomes.AsNoTracking()
            .Where(row => row.ClientOperationId == command.ClientOperationId).ToArrayAsync());
        Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
        Assert.True(outcome.WasCommitted);
        Assert.Equal(CreateCashierHandler.OperationType, outcome.OperationType);
        Assert.Equal(userId, outcome.ResultEntityId);
        Assert.Equal(seed.ActorId, outcome.ActorUserId);
        Assert.Equal(seed.TerminalId, outcome.TerminalId);
        Assert.Equal(seed.SessionId, outcome.SessionId);
        Assert.Equal(OperationPayloadFingerprint.ComputeSha256(CreateCashierHandler.OperationType,
            command.DisplayName.Trim(), "Cashier", "Active"), outcome.PayloadFingerprint);
        Assert.Null(outcome.ErrorMessage);
        Assert.Null(outcome.DocumentNumber);
    }

    private static async Task<ServiceProvider> OwnedProviderAsync()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
            ?? throw new InvalidOperationException("The approved owned PostgreSQL fixture is required."));
        if (connection.Host != "127.0.0.1" || connection.Port is < 55000 or > 55999 ||
            !(connection.Database?.StartsWith("edge_retails_", StringComparison.Ordinal) ?? false))
        {
            throw new InvalidOperationException("Cashier tests require the owned isolated loopback PostgreSQL fixture.");
        }
        var root = Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT")
            ?? throw new InvalidOperationException("The approved runner must attest the owned PostgreSQL root.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar + "EdgeRetailsMasterPg_";
        if (!fullRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fullRoot) ||
            (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("PostgreSQL fixture ownership escaped the approved temporary root.");
        }
        await using var pg = new NpgsqlConnection(connection.ConnectionString);
        await pg.OpenAsync();
        await using var directory = new NpgsqlCommand("SHOW data_directory", pg);
        var actualDirectory = (string)(await directory.ExecuteScalarAsync())!;
        var expectedDirectory = Path.GetFullPath(Path.Combine(fullRoot, "data"));
        Assert.Equal(expectedDirectory, Path.GetFullPath(actualDirectory), ignoreCase: true);
        Assert.True(Directory.Exists(expectedDirectory));
        Assert.Equal(0, (int)(File.GetAttributes(expectedDirectory) & FileAttributes.ReparsePoint));
        await using var version = new NpgsqlCommand("SHOW server_version_num", pg);
        Assert.InRange(int.Parse((string)(await version.ExecuteScalarAsync())!), 180000, 189999);
        var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        Assert.Contains("Npgsql", scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>().Database.ProviderName);
        return provider;
    }

    private static async Task<Seed> SeedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var owner = await db.Roles.SingleOrDefaultAsync(role => role.Name.ToUpper() == "OWNER");
        if (owner is null)
        {
            owner = new Role { Name = "Owner", IsSystem = true, IsActive = true };
            db.Roles.Add(owner);
        }
        var cashier = await db.Roles.SingleOrDefaultAsync(role => role.Name.ToUpper() == "CASHIER");
        if (cashier is null)
        {
            cashier = new Role { Name = "Cashier", IsSystem = true, IsActive = true };
            db.Roles.Add(cashier);
        }
        Assert.True(owner.IsSystem && owner.IsActive);
        Assert.True(cashier.IsSystem && cashier.IsActive);
        var permission = await db.Permissions.SingleOrDefaultAsync(row => row.Key == PermissionKeys.SettingsManage);
        if (permission is null)
        {
            permission = new Permission { Key = PermissionKeys.SettingsManage, IsActive = true };
            db.Permissions.Add(permission);
        }
        Assert.True(permission.IsActive);
        if (!await db.RolePermissions.AnyAsync(row => row.RoleId == owner.Id && row.PermissionId == permission.Id))
        {
            db.RolePermissions.Add(new RolePermission { RoleId = owner.Id, PermissionId = permission.Id });
        }
        var suffix = Guid.NewGuid().ToString("N");
        var credential = scope.ServiceProvider.GetRequiredService<IPinCredentialService>().Hash(SyntheticPin);
        var actor = new User
        {
            DisplayName = "Owned Cashier Test Owner " + suffix,
            RoleId = owner.Id,
            Status = UserStatus.Active,
            PinHash = credential.Hash,
            PinSalt = credential.Salt,
            PinIterations = credential.Iterations,
            PinAlgorithm = credential.Algorithm,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = 1
        };
        var terminal = new Terminal { TerminalCode = "cashier-fixture-" + suffix, Name = "Owned Cashier Fixture", Status = TerminalStatus.Active };
        var session = new UserSession { UserId = actor.Id, ClientSessionId = Guid.CreateVersion7(), StartedAt = DateTimeOffset.UtcNow };
        db.Users.Add(actor);
        db.Terminals.Add(terminal);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        return new Seed(actor.Id, session.Id, terminal.Id, cashier.Id, permission.Id, "Owned Ali Cashier " + suffix);
    }

    private sealed record Seed(Guid ActorId, Guid SessionId, Guid TerminalId, Guid CashierRoleId,
        Guid SettingsPermissionId, string Name)
    {
        public CreateCashierCommand Command() => new(Name, SyntheticPin, Guid.CreateVersion7(), ActorId,
            SessionId, TerminalId, Guid.CreateVersion7());
    }

    private sealed class InjectedCashierCommitException()
        : Exception("Injected isolated Cashier failure after user, audit and outcome flush.")
    {
    }

    private sealed class FailingUnitOfWork(EdgeRetailsDbContext db, CreateCashierCommand command,
        InjectedCashierCommitException injected) : IUnitOfWork
    {
        public bool ObservedFlushedState { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Assert.NotNull(db.Database.CurrentTransaction);
            Assert.Equal(1, await db.Users.AsNoTracking().CountAsync(row => row.DisplayName == command.DisplayName.Trim(), cancellationToken));
            Assert.Equal(1, await db.BusinessAuditEvents.AsNoTracking().CountAsync(row => row.CorrelationId == command.CorrelationId, cancellationToken));
            Assert.Equal(1, await db.OperationOutcomes.AsNoTracking().CountAsync(row => row.ClientOperationId == command.ClientOperationId &&
                row.Status == OperationOutcomeStatus.Succeeded && row.WasCommitted, cancellationToken));
            ObservedFlushedState = true;
            throw injected;
        }
    }
}
