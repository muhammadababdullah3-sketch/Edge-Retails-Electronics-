using EdgeRetails.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EdgeRetails.UnitTests;

public sealed class Phase3CashierCreationTests
{
    [Fact]
    public void CashierCreation_HasDedicatedAuthenticatedPostRoute()
    {
        var routes = typeof(UsersController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpPostAttribute), false)
                .Cast<HttpPostAttribute>())
            .ToArray();
        Assert.Contains(routes, route => route.Template == "cashiers");
        Assert.DoesNotContain(typeof(CreateCashierRequest).GetProperties(), property =>
            property.Name is "ActorId" or "RoleId" or "RoleName" or "Status" or "SessionId" or "TerminalId");
    }

    [Fact]
    public async Task OwnerCreate_UsesExistingCashierRoleHashedCredentialAndRedactedAudit()
    {
        await using var f = new Fixture();
        var result = await f.Handler.HandleAsync(f.Command(), default);
        Assert.True(result.IsSuccess);
        var created = await f.Db.Users.SingleAsync(user => user.Id == result.Value!.UserId);
        Assert.Equal(f.Cashier.Id, created.RoleId);
        Assert.Equal(UserStatus.Active, created.Status);
        Assert.True(f.Credentials.Verify(Fixture.Pin, new(created.PinHash, created.PinSalt, created.PinIterations, created.PinAlgorithm)));
        Assert.NotEmpty(created.PinSalt);
        Assert.Equal(2, await f.Db.Users.CountAsync());
        Assert.Equal(2, await f.Db.Roles.CountAsync());
        var audit = Assert.Single(await f.Db.BusinessAuditEvents.ToArrayAsync());
        Assert.Equal("USER_CREATED", audit.Action);
        Assert.Equal(f.Owner.Id, audit.ActorId);
        Assert.Equal("Active Cashier account created.", audit.Summary);
        var outcome = Assert.Single(await f.Db.OperationOutcomes.ToArrayAsync());
        Assert.Equal(created.Id, outcome.ResultEntityId);
        Assert.Equal(f.Terminal.Id, outcome.TerminalId);
        Assert.Equal(f.Session.Id, outcome.SessionId);
        Assert.Equal(OperationPayloadFingerprint.ComputeSha256(CreateCashierHandler.OperationType, "Ali Cashier", "Cashier", "Active"), outcome.PayloadFingerprint);
        Assert.DoesNotContain(Fixture.Pin, f.Command().ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Pin", JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Cashier", true)]
    [InlineData("Manager", true)]
    [InlineData("Owner", false)]
    public async Task UnauthorizedRoleOrOwnerWithoutPermission_CannotCreate(string roleName, bool permission)
    {
        await using var f = new Fixture(roleName, permission);
        var result = await f.Handler.HandleAsync(f.Command(), default);
        Assert.False(result.IsSuccess);
        Assert.StartsWith("authorization.", result.Error!.Code);
        Assert.Equal(1, await f.Db.Users.CountAsync());
        Assert.Empty(await f.Db.OperationOutcomes.ToArrayAsync());
        Assert.Empty(await f.Db.BusinessAuditEvents.ToArrayAsync());
    }

    [Theory]
    [InlineData("revoked_session")]
    [InlineData("ended_session")]
    [InlineData("disabled_owner")]
    [InlineData("suspended_terminal")]
    [InlineData("disabled_role")]
    public async Task FreshInactiveAuthority_CannotCreate(string fault)
    {
        await using var f = new Fixture();
        switch (fault)
        {
            case "revoked_session": f.Session.IsRevoked = true; break;
            case "ended_session": f.Session.EndedAt = DateTimeOffset.UtcNow; break;
            case "disabled_owner": f.Owner.Status = UserStatus.Disabled; break;
            case "suspended_terminal": f.Terminal.Status = TerminalStatus.Suspended; break;
            case "disabled_role": f.OwnerRole.IsActive = false; break;
        }
        await f.Db.SaveChangesAsync();
        Assert.False((await f.Handler.HandleAsync(f.Command(), default)).IsSuccess);
        Assert.Equal(1, await f.Db.Users.CountAsync());
        Assert.Empty(await f.Db.BusinessAuditEvents.ToArrayAsync());
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("abcd")]
    [InlineData("１２３４")]
    public async Task InvalidPin_DoesNotMutate(string pin)
    {
        await using var f = new Fixture();
        Assert.Equal("identity.invalid_pin", (await f.Handler.HandleAsync(f.Command() with { Pin = pin }, default)).Error!.Code);
        Assert.Equal(1, await f.Db.Users.CountAsync());
    }

    [Fact]
    public async Task SameOperation_ReplaysOneUserAndAudit_ChangedPinRejected()
    {
        await using var f = new Fixture();
        var command = f.Command();
        var first = await f.Handler.HandleAsync(command, default);
        var second = await f.Handler.HandleAsync(command, default);
        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal("idempotency.payload_mismatch",
            (await f.Handler.HandleAsync(command with { Pin = "2468" }, default)).Error!.Code);
        Assert.Equal(2, await f.Db.Users.CountAsync());
        Assert.Single(await f.Db.BusinessAuditEvents.ToArrayAsync());
        Assert.Single(await f.Db.OperationOutcomes.ToArrayAsync());
    }

    [Fact]
    public async Task Replay_RejectsChangedNameAndDifferentTerminal()
    {
        await using var f = new Fixture();
        var command = f.Command();
        Assert.True((await f.Handler.HandleAsync(command, default)).IsSuccess);
        Assert.Equal("idempotency.payload_mismatch",
            (await f.Handler.HandleAsync(command with { DisplayName = "Other Cashier" }, default)).Error!.Code);
        var other = new Terminal();
        f.Db.Terminals.Add(other);
        await f.Db.SaveChangesAsync();
        Assert.Equal("idempotency.payload_mismatch",
            (await f.Handler.HandleAsync(command with { TerminalId = other.Id }, default)).Error!.Code);
    }

    [Fact]
    public async Task DifferentOperationSameName_IsRejectedIncludingCaseAndWhitespace()
    {
        await using var f = new Fixture();
        Assert.True((await f.Handler.HandleAsync(f.Command(), default)).IsSuccess);
        var result = await f.Handler.HandleAsync(f.Command() with { DisplayName = "  ALI CASHIER  " }, default);
        Assert.Equal("identity.display_name_duplicate", result.Error!.Code);
        Assert.Equal(2, await f.Db.Users.CountAsync());
        Assert.Equal("IDENTITY_USER_DISPLAY_NAME", f.Locks.LastResourceType);
    }

    [Fact]
    public async Task MissingCashierRole_IsRejectedWithoutProvisioningNewRole()
    {
        await using var f = new Fixture();
        f.Cashier.IsActive = false;
        await f.Db.SaveChangesAsync();
        Assert.Equal("identity.cashier_role_unavailable", (await f.Handler.HandleAsync(f.Command(), default)).Error!.Code);
        Assert.Equal(2, await f.Db.Roles.CountAsync());
        Assert.Equal(1, await f.Db.Users.CountAsync());
    }

    [Fact]
    public async Task ControllerRequiresSessionAndTerminal_AndUsesTrustedActorContext()
    {
        await using var f = new Fixture();
        var controller = new UsersController(f.Identity, new ApplicationPermissionAuthorizer(f.Identity), f.Handler)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var request = new CreateCashierRequest("Ali Cashier", Fixture.Pin, Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.IsType<UnauthorizedObjectResult>(await controller.CreateCashier(request, default));
        controller.HttpContext.Items["ActorContext"] = f.Actor();
        Assert.IsType<UnauthorizedObjectResult>(await controller.CreateCashier(request, default));
        controller.HttpContext.Items["CurrentTerminal"] = f.Terminal;
        Assert.IsType<OkObjectResult>(await controller.CreateCashier(request, default));
        Assert.Equal(f.Owner.Id, (await f.Db.BusinessAuditEvents.SingleAsync()).ActorId);
        Assert.DoesNotContain(Fixture.Pin, request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticatedStatus_IsActorAndTerminalScopedAndRedactsFingerprint()
    {
        await using var f = new Fixture();
        var command = f.Command();
        Assert.True((await f.Handler.HandleAsync(command, default)).IsSuccess);
        var handler = new OperationStatusQueryHandler(new SalesRepository(f.Db), new PurchasingRepository(f.Db),
            new SupplierAccountRepository(f.Db), outcomeLedger: f.Outcomes);
        var controller = new OperationsController(handler)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.GetOperationStatus(command.ClientOperationId, default)).StatusCode);
        controller.HttpContext.Items["CurrentTerminal"] = f.Terminal;
        Assert.Equal(401, Assert.IsType<ObjectResult>(await controller.GetOperationStatus(command.ClientOperationId, default)).StatusCode);
        controller.HttpContext.Items["ActorContext"] = f.Actor();
        var status = Assert.IsType<OperationStatusResult>((await controller.GetOperationStatus(command.ClientOperationId, default) as OkObjectResult)!.Value);
        Assert.Equal("Succeeded", status.Status);
        Assert.Null(status.PayloadFingerprint);
        Assert.DoesNotContain("Pin", JsonSerializer.Serialize(status), StringComparison.OrdinalIgnoreCase);
        controller.HttpContext.Items["ActorContext"] = f.Actor() with { UserId = Guid.CreateVersion7() };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetOperationStatus(command.ClientOperationId, default)).StatusCode);
        controller.HttpContext.Items["ActorContext"] = f.Actor();
        controller.HttpContext.Items["CurrentTerminal"] = new Terminal();
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.GetOperationStatus(command.ClientOperationId, default)).StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Pin = "1248"; // Synthetic fixture credential, never an installed account.
        public EdgeRetailsDbContext Db { get; }
        public IdentityReadRepository Identity { get; }
        public Pbkdf2PinCredentialService Credentials { get; } = new();
        public EfOperationOutcomeLedger Outcomes { get; }
        public User Owner { get; }
        public Role OwnerRole { get; }
        public Role Cashier { get; } = new() { Name = "Cashier", IsSystem = true };
        public UserSession Session { get; }
        public Terminal Terminal { get; } = new();
        public Locks Locks { get; } = new();
        public CreateCashierHandler Handler { get; }

        public Fixture(string actorRole = "Owner", bool permission = true)
        {
            Db = new(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
                .UseInMemoryDatabase("cashier-unit-owned-" + Guid.NewGuid().ToString("N")).Options);
            OwnerRole = new Role { Name = actorRole, IsSystem = true };
            Owner = new User { DisplayName = "Fixture Owner", RoleId = OwnerRole.Id, PinIterations = 1 };
            Session = new UserSession { UserId = Owner.Id, ClientSessionId = Guid.CreateVersion7() };
            Db.Roles.Add(OwnerRole);
            if (actorRole != "Cashier")
            {
                Db.Roles.Add(Cashier);
            }
            Db.Users.Add(Owner);
            Db.UserSessions.Add(Session);
            Db.Terminals.Add(Terminal);
            if (permission)
            {
                var key = new Permission { Key = PermissionKeys.SettingsManage };
                Db.Permissions.Add(key);
                Db.RolePermissions.Add(new RolePermission { RoleId = OwnerRole.Id, PermissionId = key.Id });
            }
            Db.SaveChanges();
            Identity = new IdentityReadRepository(Db);
            Outcomes = new EfOperationOutcomeLedger(Db);
            var clock = new SystemClock();
            Handler = new(new IdentityAdministrationRepository(Db), Identity, new IdentitySessionRepository(Db),
                new TerminalRepository(Db), new ApplicationPermissionAuthorizer(Identity), Credentials,
                new UnitTransactions(), Locks, Locks, Outcomes, new BusinessAuditWriter(Db, clock), clock, Db);
        }
        public CreateCashierCommand Command() => new("Ali Cashier", Pin, Guid.CreateVersion7(), Owner.Id,
            Session.Id, Terminal.Id, Guid.CreateVersion7());
        public ActorContext Actor() => new(Owner.Id, Session.Id, Owner.DisplayName, OwnerRole.Id, OwnerRole.Name,
            new HashSet<string> { PermissionKeys.SettingsManage });
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class UnitTransactions : ITransactionRunner
    {
        // EF InMemory is unit behavior evidence only; PostgreSQL owns transactional/concurrency proof.
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
            => operation(cancellationToken);
    }
    public sealed class Locks : IOperationLock, IResourceLock
    {
        public string? LastResourceType { get; private set; }
        public Task AcquireAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireAsync(string resourceType, Guid resourceId, CancellationToken cancellationToken)
            => AcquireAsync(resourceType, resourceId.ToString("D"), cancellationToken);
        public Task AcquireAsync(string resourceType, string resourceKey, CancellationToken cancellationToken)
        { LastResourceType = resourceType; return Task.CompletedTask; }
    }
}
