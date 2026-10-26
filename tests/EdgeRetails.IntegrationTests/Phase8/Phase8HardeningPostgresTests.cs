using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Settings;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase8;

/// <summary>Phase 8 lifecycle, time, and cash-session hardening against disposable PostgreSQL.</summary>
public sealed class Phase8HardeningPostgresTests
{
    [Fact]
    public async Task Permissions_CashSessionCloseAndOwnerVoid_ExistAfterMigrations()
    {
        await using var provider = await CreateMigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var keys = await db.Permissions
            .AsNoTracking()
            .Where(p => p.Key == PermissionKeys.CashSessionClose || p.Key == PermissionKeys.CashSessionOwnerVoid)
            .Select(p => p.Key)
            .ToListAsync();

        Assert.Contains(PermissionKeys.CashSessionClose, keys);
        Assert.Contains(PermissionKeys.CashSessionOwnerVoid, keys);
    }

    [Fact]
    public async Task UpdateShopProfile_PersistsBusinessDayCutoff_OnPostgres()
    {
        await using var provider = await CreateMigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var cache = scope.ServiceProvider.GetRequiredService<IShopTimeZoneCache>();
        var handler = scope.ServiceProvider.GetRequiredService<UpdateShopProfileHandler>();
        var actorId = Guid.CreateVersion7();

        var result = await handler.HandleAsync(
            new UpdateShopProfileCommand(
                "Edge Retails Integration Shop",
                null,
                null,
                "Asia/Karachi",
                "04:15",
                actorId,
                Guid.CreateVersion7()),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var profile = await db.ShopProfiles.AsNoTracking().SingleAsync(x => x.ProfileKey == "PRIMARY");
        Assert.Equal(new TimeOnly(4, 15), profile.BusinessDayCutoffLocalTime);
        Assert.Equal(new TimeOnly(4, 15), cache.BusinessDayCutoffLocalTime);
    }

    [Fact]
    public async Task VoidCashSessionByOwner_OnClosedSession_PersistsVoidedStatus()
    {
        await using var provider = await CreateMigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var actorId = Guid.CreateVersion7();
        var open = scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>();
        var close = scope.ServiceProvider.GetRequiredService<CloseCashSessionHandler>();
        var voidHandler = scope.ServiceProvider.GetRequiredService<VoidCashSessionByOwnerHandler>();

        await EnsureNoOpenCashSessionAsync(scope.ServiceProvider, actorId);

        var opened = await open.HandleAsync(new OpenCashSessionCommand(100m, actorId, null), CancellationToken.None);
        Assert.True(opened.IsSuccess, opened.Error?.Message);

        var closed = await close.HandleAsync(
            new CloseCashSessionCommand(opened.Value, 100m, actorId, null),
            CancellationToken.None);
        Assert.True(closed.IsSuccess, closed.Error?.Message);

        var voided = await voidHandler.HandleAsync(
            new VoidCashSessionByOwnerCommand(opened.Value, actorId, "Integration correction"),
            CancellationToken.None);
        Assert.True(voided.IsSuccess, voided.Error?.Message);

        var session = await db.CashSessions.AsNoTracking().SingleAsync(x => x.Id == opened.Value);
        Assert.Equal(CashSessionStatus.VoidedByOwner, session.Status);
        Assert.Equal(actorId, session.VoidedBy);
        Assert.Equal("Integration correction", session.VoidReason);
    }

    [Fact]
    public async Task LockAndUnlockUserSession_PersistsScreenLockState()
    {
        await using var provider = await CreateMigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var pinService = scope.ServiceProvider.GetRequiredService<IPinCredentialService>();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityReadRepository>();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        var user = await identity.GetUserAsync(actorId, CancellationToken.None)
            ?? throw new InvalidOperationException("Seeded integration actor was not found.");

        var auth = scope.ServiceProvider.GetRequiredService<AuthenticateUserHandler>();
        var clientSessionId = Guid.CreateVersion7();
        var pin = "1234";
        var credential = pinService.Hash(pin);
        var tracked = await db.Users.SingleAsync(x => x.Id == user.Id);
        tracked.PinHash = credential.Hash;
        tracked.PinSalt = credential.Salt;
        tracked.PinIterations = credential.Iterations;
        tracked.PinAlgorithm = credential.Algorithm;
        await db.SaveChangesAsync();

        var login = await auth.HandleAsync(
            new AuthenticateUserCommand(user.Id, pin, clientSessionId),
            CancellationToken.None);
        Assert.True(login.IsSuccess, login.Error?.Message);

        var lockHandler = scope.ServiceProvider.GetRequiredService<LockUserSessionHandler>();
        var unlockHandler = scope.ServiceProvider.GetRequiredService<UnlockUserSessionHandler>();
        var sessionId = login.Value!.SessionId;

        var locked = await lockHandler.HandleAsync(
            new LockUserSessionCommand(actorId, sessionId, Guid.CreateVersion7()),
            CancellationToken.None);
        Assert.True(locked.IsSuccess, locked.Error?.Message);

        var lockedRow = await db.UserSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        Assert.NotNull(lockedRow.ScreenLockedAt);

        var badPin = await unlockHandler.HandleAsync(
            new UnlockUserSessionCommand(actorId, sessionId, "0000", Guid.CreateVersion7()),
            CancellationToken.None);
        Assert.False(badPin.IsSuccess);
        Assert.Equal("identity.invalid_unlock", badPin.Error?.Code);

        var unlocked = await unlockHandler.HandleAsync(
            new UnlockUserSessionCommand(actorId, sessionId, pin, Guid.CreateVersion7()),
            CancellationToken.None);
        Assert.True(unlocked.IsSuccess, unlocked.Error?.Message);

        var unlockedRow = await db.UserSessions.AsNoTracking().SingleAsync(x => x.Id == sessionId);
        Assert.Null(unlockedRow.ScreenLockedAt);
    }

    [Fact]
    public async Task CashSession_OptimisticConcurrency_MapsToApplicationError()
    {
        await using var provider = await CreateMigratedProviderAsync();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var actorId = Guid.CreateVersion7();
        var open = scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>();
        await EnsureNoOpenCashSessionAsync(scope.ServiceProvider, actorId);

        var opened = await open.HandleAsync(new OpenCashSessionCommand(50m, actorId, null), CancellationToken.None);
        Assert.True(opened.IsSuccess, opened.Error?.Message);

        var tracked = await db.CashSessions.FirstAsync(x => x.Id == opened.Value);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE finance.cash_sessions
             SET version = version + 1
             WHERE id = {opened.Value}
             """);

        tracked.Note = "Stale tracked write";
        var ex = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var concurrency = Assert.IsType<DbUpdateConcurrencyException>(ex);
        var mapper = new EfExceptionToErrorMapper();
        Assert.True(mapper.TryMap(concurrency, out var error));
        Assert.Equal("cash.session_concurrency_conflict", error.Code);
    }

    private static async Task EnsureNoOpenCashSessionAsync(IServiceProvider services, Guid actorId)
    {
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        var open = await db.CashSessions.FirstOrDefaultAsync(x => x.Status == CashSessionStatus.Open);
        if (open is null)
        {
            return;
        }

        var close = services.GetRequiredService<CloseCashSessionHandler>();
        var movements = await db.CashMovements.Where(m => m.CashSessionId == open.Id).ToListAsync();
        var cashIn = movements.Where(m => m.Direction == CashMovementDirection.In).Sum(m => m.Amount);
        var cashOut = movements.Where(m => m.Direction == CashMovementDirection.Out).Sum(m => m.Amount);
        var expected = open.OpeningCash + cashIn - cashOut;
        await close.HandleAsync(
            new CloseCashSessionCommand(open.Id, expected, actorId, "Test cleanup"),
            CancellationToken.None);
    }

    private static async Task<ServiceProvider> CreateMigratedProviderAsync()
    {
        var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await db.Database.MigrateAsync();
        return provider;
    }

    private static ServiceProvider CreateProvider()
    {
        var connectionString = ResolveConnectionString();
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        services.RemoveAll<IApplicationPermissionAuthorizer>();
        services.AddSingleton<IApplicationPermissionAuthorizer, PermissiveAuthorizer>();
        return services.BuildServiceProvider();
    }

    private static string ResolveConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var builtConn =
                    $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(builtConn, performLiveProbe: false);
                return builtConn;
            }
        }

        throw new InvalidOperationException(
            "FAIL-CLOSED: Isolated test connection is not configured. Provide EDGE_RETAILS_TEST_DB or disposable cluster credential file.");
    }

    private sealed class PermissiveAuthorizer : IApplicationPermissionAuthorizer
    {
        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
