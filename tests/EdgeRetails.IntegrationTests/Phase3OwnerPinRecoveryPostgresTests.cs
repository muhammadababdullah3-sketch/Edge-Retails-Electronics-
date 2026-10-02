using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using EdgeRetails.Application.Production.Recovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3OwnerPinRecoveryPostgresTests
{
    [Fact]
    public async Task PostgreSql_recovery_is_audited_revokes_sessions_and_replay_is_rejected()
    {
        Guid userId;
        Guid roleId;
        Guid operationId = Guid.CreateVersion7();
        var pinCredentials = new Pbkdf2PinCredentialService();
        var originalCredential = pinCredentials.Hash("2580");

        await using (var provider = Phase2PostgresTestHarness.BuildProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == "Owner");
            if (role is null)
            {
                role = new Role { Name = "Owner", IsSystem = true, IsActive = true };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }

            roleId = role.Id;
            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                DisplayName = "Recovery PostgreSQL Fixture " + Guid.NewGuid().ToString("N")[..8],
                RoleId = roleId,
                PinHash = originalCredential.Hash,
                PinSalt = originalCredential.Salt,
                PinIterations = originalCredential.Iterations,
                PinAlgorithm = originalCredential.Algorithm,
                Status = UserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(user);
            var session = new UserSession
            {
                UserId = user.Id,
                ClientSessionId = Guid.CreateVersion7(),
                StartedAt = now,
                IsRevoked = false
            };
            db.UserSessions.Add(session);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        string replacementPin = "7316";
        var replacementOperation = new RecoverOwnerPinCommand(operationId.ToString(), userId, replacementPin);
        await using (var provider = BuildAuthorizedProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<RecoverOwnerPinHandler>();
            var result = await handler.HandleAsync(replacementOperation, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var audit = await db.BusinessAuditEvents.SingleAsync(
                x => x.Action == "USER_PIN_RECOVERY_SUCCEEDED" && x.CorrelationId == operationId);
            Assert.Equal(userId, audit.EntityId);
            Assert.NotEqual(Guid.Empty, audit.ActorId);
            Assert.Contains("signed Recovery Authorization", audit.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(replacementPin, audit.Summary, StringComparison.Ordinal);
            Assert.Single(await db.BusinessAuditEvents.Where(x =>
                x.Action == "USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED" && x.CorrelationId == operationId).ToListAsync());
            Assert.Equal(1, await db.UserSessions.CountAsync(x => x.UserId == userId && x.IsRevoked));
        }

        await using (var provider = BuildAuthorizedProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<RecoverOwnerPinHandler>();
            var replay = await handler.HandleAsync(
                replacementOperation with { NewPin = "8427" },
                CancellationToken.None);
            Assert.False(replay.IsSuccess);
            Assert.Equal("identity.recovery_replay", replay.Error?.Code);

            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            var pinService = scope.ServiceProvider.GetRequiredService<IPinCredentialService>();
            Assert.True(pinService.Verify("7316", new PinCredential(
                user.PinHash, user.PinSalt, user.PinIterations, user.PinAlgorithm)));
            Assert.False(pinService.Verify("8427", new PinCredential(
                user.PinHash, user.PinSalt, user.PinIterations, user.PinAlgorithm)));
            Assert.Contains(await db.BusinessAuditEvents.ToListAsync(), x =>
                x.Action == "USER_PIN_RECOVERY_DENIED" && x.EntityId == userId);
        }

        await using (var provider = BuildAuthorizedProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<AuthenticateUserHandler>();
            var oldPin = await handler.HandleAsync(
                new AuthenticateUserCommand(userId, "2580", Guid.CreateVersion7()),
                CancellationToken.None);
            var newPin = await handler.HandleAsync(
                new AuthenticateUserCommand(userId, "7316", Guid.CreateVersion7()),
                CancellationToken.None);
            Assert.False(oldPin.IsSuccess);
            Assert.True(newPin.IsSuccess, newPin.Error?.Message);
        }

        Assert.NotEqual(Guid.Empty, roleId);
    }

    [Fact]
    public async Task PostgreSql_concurrent_recovery_for_same_owner_allows_only_one_pin_change()
    {
        var pins = new Pbkdf2PinCredentialService();
        var initial = pins.Hash("2580");
        Guid userId;
        Guid roleId;
        await using (var provider = Phase2PostgresTestHarness.BuildProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var role = await db.Roles.SingleAsync(x => x.Name == "Owner");
            roleId = role.Id;
            var user = new User
            {
                DisplayName = "Recovery Concurrency Fixture " + Guid.NewGuid().ToString("N")[..8],
                RoleId = role.Id,
                PinHash = initial.Hash,
                PinSalt = initial.Salt,
                PinIterations = initial.Iterations,
                PinAlgorithm = initial.Algorithm,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = 1
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var readGate = new RecoveryReadGate();
        var commands = new[]
        {
            new RecoverOwnerPinCommand(Guid.CreateVersion7().ToString(), userId, "7316"),
            new RecoverOwnerPinCommand(Guid.CreateVersion7().ToString(), userId, "8427")
        };
        var outcomes = await Task.WhenAll(commands.Select(async command =>
        {
            await using var provider = BuildAuthorizedProvider(readGate);
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<RecoverOwnerPinHandler>();
            try
            {
                var result = await handler.HandleAsync(command, CancellationToken.None);
                return result.IsSuccess;
            }
            catch (DbUpdateConcurrencyException)
            {
                return false;
            }
        }));

        Assert.Single(outcomes, success => success);
        Assert.Single(outcomes, success => !success);
        await using (var provider = Phase2PostgresTestHarness.BuildProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == userId);
            var acceptedPins = new[] { "7316", "8427" }.Count(pin => pins.Verify(
                pin,
                new PinCredential(user.PinHash, user.PinSalt, user.PinIterations, user.PinAlgorithm)));
            Assert.Equal(1, acceptedPins);
            Assert.Equal(1, await db.BusinessAuditEvents.CountAsync(x =>
                x.EntityId == userId && x.Action == "USER_PIN_RECOVERY_SUCCEEDED"));
            var denialSummaries = await db.BusinessAuditEvents
                .Where(x => x.EntityId == userId && x.Action == "USER_PIN_RECOVERY_DENIED")
                .Select(x => x.Summary)
                .ToListAsync();
            Assert.Single(denialSummaries, summary =>
                summary?.Contains("identity.recovery_concurrent", StringComparison.Ordinal) == true);
            Assert.Equal(2, user.Version);
        }
    }

    [Fact]
    public async Task PostgreSql_concurrent_replay_of_same_signed_nonce_is_consumed_once()
    {
        var pins = new Pbkdf2PinCredentialService();
        var initial = pins.Hash("2580");
        Guid userId;
        await using (var provider = Phase2PostgresTestHarness.BuildProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var role = await db.Roles.SingleOrDefaultAsync(x => x.Name == "Owner");
            if (role is null)
            {
                role = new Role { Name = "Owner", IsSystem = true, IsActive = true };
                db.Roles.Add(role);
                await db.SaveChangesAsync();
            }
            var user = new User
            {
                DisplayName = "Recovery Nonce Replay Fixture " + Guid.NewGuid().ToString("N")[..8],
                RoleId = role.Id,
                PinHash = initial.Hash,
                PinSalt = initial.Salt,
                PinIterations = initial.Iterations,
                PinAlgorithm = initial.Algorithm,
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = 1
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var nonce = Guid.CreateVersion7().ToString();
        var readGate = new RecoveryReadGate();
        var commands = new[]
        {
            new RecoverOwnerPinCommand(nonce, userId, "7316"),
            new RecoverOwnerPinCommand(nonce, userId, "8427")
        };
        var outcomes = await Task.WhenAll(commands.Select(async command =>
        {
            await using var provider = BuildAuthorizedProvider(readGate);
            await using var scope = provider.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<RecoverOwnerPinHandler>();
            return await handler.HandleAsync(command, CancellationToken.None);
        }));

        Assert.Single(outcomes, result => result.IsSuccess);
        var rejectedReplay = Assert.Single(outcomes, result => !result.IsSuccess);
        Assert.Equal("identity.recovery_replay", rejectedReplay.Error?.Code);
        await using var verifyProvider = Phase2PostgresTestHarness.BuildProvider();
        await using var verifyScope = verifyProvider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var finalUser = await verifyDb.Users.SingleAsync(x => x.Id == userId);
        var finalCredential = new PinCredential(finalUser.PinHash, finalUser.PinSalt, finalUser.PinIterations, finalUser.PinAlgorithm);
        Assert.Single(new[] { "7316", "8427" }, pin => pins.Verify(pin, finalCredential));
        Assert.Equal(1, await verifyDb.BusinessAuditEvents.CountAsync(x =>
            x.EntityId == userId && x.Action == "USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED" && x.CorrelationId == Guid.Parse(nonce)));
        Assert.Equal(1, await verifyDb.BusinessAuditEvents.CountAsync(x =>
            x.EntityId == userId && x.Action == "USER_PIN_RECOVERY_SUCCEEDED" && x.CorrelationId == Guid.Parse(nonce)));
    }

    private static ServiceProvider BuildAuthorizedProvider(RecoveryReadGate? readGate = null)
    {
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(
            Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
            ?? throw new InvalidOperationException("Isolated PostgreSQL test configuration is required."));
        services.AddScoped<RecoverOwnerPinHandler>();
        if (readGate is not null)
        {
            services.RemoveAll<IIdentityCredentialRecoveryRepository>();
            services.AddScoped<IIdentityCredentialRecoveryRepository>(sp =>
                new RecoveryReadGateRepository(
                    new IdentityCredentialRecoveryRepository(sp.GetRequiredService<EdgeRetailsDbContext>()),
                    readGate));
        }
        services.AddSingleton<IRecoveryAuthorizationValidator, PostgreSqlTestRecoveryAuthorizationValidator>();
        return services.BuildServiceProvider();
    }

    private sealed class PostgreSqlTestRecoveryAuthorizationValidator : IRecoveryAuthorizationValidator
    {
        public Task<RecoveryAuthorizationValidation> ValidateAsync(
            string? signedAuthorization,
            Guid targetUserId,
            CancellationToken cancellationToken) =>
            Guid.TryParse(signedAuthorization, out var nonce) && nonce != Guid.Empty && targetUserId != Guid.Empty
                ? Task.FromResult(RecoveryAuthorizationValidation.Valid(new VerifiedRecoveryAuthorization(
                    "isolated-postgresql-test-issuer", nonce, targetUserId, DateTimeOffset.UtcNow.AddMinutes(5))))
                : Task.FromResult(RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid"));
    }

    private sealed class RecoveryReadGate
    {
        private readonly TaskCompletionSource _bothReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public async Task WaitForBothReadsAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                _bothReads.TrySetResult();
            }

            await _bothReads.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }

    private sealed class RecoveryReadGateRepository(
        IdentityCredentialRecoveryRepository inner,
        RecoveryReadGate gate) : IIdentityCredentialRecoveryRepository
    {
        public Task<IReadOnlyList<PinRecoveryTarget>> GetActiveOwnerTargetsAsync(CancellationToken cancellationToken) =>
            inner.GetActiveOwnerTargetsAsync(cancellationToken);

        public async Task<User?> GetUserForRecoveryUpdateAsync(Guid userId, CancellationToken cancellationToken)
        {
            var user = await inner.GetUserForRecoveryUpdateAsync(userId, cancellationToken);
            await gate.WaitForBothReadsAsync(cancellationToken);
            return user;
        }

        public Task<bool> HasRecoveryOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
            inner.HasRecoveryOperationAsync(operationId, cancellationToken);

        public Task RevokeActiveSessionsAsync(Guid userId, DateTimeOffset revokedAt, CancellationToken cancellationToken) =>
            inner.RevokeActiveSessionsAsync(userId, revokedAt, cancellationToken);

        public Task<bool> TrySaveRecoveryChangesAsync(CancellationToken cancellationToken) =>
            inner.TrySaveRecoveryChangesAsync(cancellationToken);
    }
}
