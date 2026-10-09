using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class CustomerKhataSuspensionPostgresTests
{
    [Fact]
    public async Task ConcurrentDuplicateStatusCommandsCommitOneVersionChange()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        long customerVersion;
        long projectVersion;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            customerVersion = (await db.Customers.SingleAsync(x => x.Id == fixture.Customer)).Version;
            projectVersion = (await db.ThakaProjects.SingleAsync(x => x.Id == fixture.Project)).Version;
        }
        var customerCommand = new SetCustomerSuspensionCommand(Guid.NewGuid(), fixture.Customer, true, fixture.Actor);
        var projectCommand = new SetThakaSuspensionCommand(Guid.NewGuid(), fixture.Project, true, "Concurrent test", fixture.Actor);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = provider.CreateAsyncScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<SetCustomerSuspensionHandler>()
                .HandleAsync(customerCommand, default)).IsSuccess);
            Assert.True((await scope.ServiceProvider.GetRequiredService<SetThakaSuspensionHandler>()
                .HandleAsync(projectCommand, default)).IsSuccess);
        }));
        await using var verification = provider.CreateAsyncScope();
        var state = verification.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(customerVersion + 1, (await state.Customers.SingleAsync(x => x.Id == fixture.Customer)).Version);
        Assert.Equal(projectVersion + 1, (await state.ThakaProjects.SingleAsync(x => x.Id == fixture.Project)).Version);
    }

    [Fact]
    public async Task SuspendedKhata_BlocksIssue_AllowsRecovery_AndOldReplayCannotResuspend()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        var operation = Guid.NewGuid();
        var command = new SetThakaSuspensionCommand(operation, fixture.Project, true, "Owned test suspension", fixture.Actor);
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<SetThakaSuspensionHandler>();
            Assert.True((await handler.HandleAsync(command, default)).IsSuccess);
            Assert.True((await handler.HandleAsync(command, default)).IsSuccess);
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(ThakaProjectStatus.Suspended, (await db.ThakaProjects.AsNoTracking().SingleAsync(x => x.Id == fixture.Project)).Status);
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var issue = await scope.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(
                new(Guid.NewGuid(), fixture.Project, fixture.Actor, "must fail", [new(fixture.Product, fixture.Unit, 1, 150, [])]), default);
            Assert.Equal("thaka.project_not_active", issue.Error?.Code);
            var payment = await scope.ServiceProvider.GetRequiredService<RecordThakaPaymentHandler>().HandleAsync(
                new(Guid.NewGuid(), fixture.Project, 10m, ThakaPaymentMethod.Bank, "recovery", null, fixture.Actor), default);
            Assert.True(payment.IsSuccess, payment.Error?.Message);
            var reads = scope.ServiceProvider.GetRequiredService<IThakaReadService>();
            var detail = await reads.GetProjectAsync(fixture.Project, default);
            Assert.NotNull(detail);
            Assert.Single(detail.Payments);
            Assert.Equal(90m, detail.Project.Balance);
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<SetThakaSuspensionHandler>();
            Assert.True((await handler.HandleAsync(command with { ClientOperationId = Guid.NewGuid(), IsSuspended = false }, default)).IsSuccess);
            Assert.True((await handler.HandleAsync(command, default)).IsSuccess);
            Assert.Equal("thaka.operation_id_conflict", (await handler.HandleAsync(command with { IsSuspended = false }, default)).Error?.Code);
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(ThakaProjectStatus.Active, (await db.ThakaProjects.AsNoTracking().SingleAsync(x => x.Id == fixture.Project)).Status);
        }
    }

    [Fact]
    public async Task SuspendedCustomer_StaysDiscoverable_BlocksNewKhataAndIssue_EditCannotResumeIt()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SetCustomerSuspensionHandler>();
        var operation = Guid.NewGuid();
        var command = new SetCustomerSuspensionCommand(operation, fixture.Customer, true, fixture.Actor);
        Assert.True((await handler.HandleAsync(command, default)).IsSuccess);
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var original = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == fixture.Customer);
        var edit = await scope.ServiceProvider.GetRequiredService<SaveCustomerHandler>().HandleAsync(
            new(original.Id, original.Name, original.Phone, "Changed address", true, fixture.Actor, Guid.NewGuid(),
                ClientOperationId: Guid.NewGuid(), PreserveActivityStatus: true), default);
        Assert.True(edit.IsSuccess, edit.Error?.Message);
        var after = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == fixture.Customer);
        Assert.False(after.IsActive);
        Assert.Equal("Changed address", after.Address);
        var directory = scope.ServiceProvider.GetRequiredService<IPartyDirectoryReadService>();
        Assert.DoesNotContain(await directory.GetCustomersAsync(original.Name, cancellationToken: default), x => x.CustomerId == original.Id);
        Assert.Contains(await directory.GetCustomersAsync(original.Name, includeInactive: true), x => x.CustomerId == original.Id && !x.IsActive);
        var project = await scope.ServiceProvider.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(
            new(fixture.Customer, "Blocked new khata", null, null, DateOnly.FromDateTime(DateTime.Today), fixture.Actor, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.False(project.IsSuccess);
        var issue = await scope.ServiceProvider.GetRequiredService<IssueThakaMaterialHandler>().HandleAsync(
            new(Guid.NewGuid(), fixture.Project, fixture.Actor, "must fail", [new(fixture.Product, fixture.Unit, 1, 150, [])]), default);
        Assert.Equal("thaka.customer_suspended", issue.Error?.Code);
        var payment = await scope.ServiceProvider.GetRequiredService<RecordThakaPaymentHandler>().HandleAsync(
            new(Guid.NewGuid(), fixture.Project, 10, ThakaPaymentMethod.Bank, "recovery", null, fixture.Actor), default);
        Assert.True(payment.IsSuccess, payment.Error?.Message);
        var suspendedWorkspace = await scope.ServiceProvider.GetRequiredService<IThakaReadService>().GetProjectAsync(fixture.Project, default);
        Assert.False(suspendedWorkspace!.Project.CustomerIsActive);
        Assert.True((await handler.HandleAsync(command with { ClientOperationId = Guid.NewGuid(), IsSuspended = false }, default)).IsSuccess);
        Assert.True((await handler.HandleAsync(command, default)).IsSuccess);
        Assert.True((await db.Customers.AsNoTracking().SingleAsync(x => x.Id == fixture.Customer)).IsActive);
        Assert.Equal("Changed address", (await db.Customers.AsNoTracking().SingleAsync(x => x.Id == fixture.Customer)).Address);
    }

    [Fact]
    public async Task UnauthorizedAndSettledTransitions_AreRejectedWithoutChangingState()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var fixture = await SeedAsync(provider);
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SetThakaSuspensionHandler>();
        Assert.False((await handler.HandleAsync(new(Guid.NewGuid(), fixture.Project, true, "test", Guid.Empty), default)).IsSuccess);
        Assert.False((await scope.ServiceProvider.GetRequiredService<SetCustomerSuspensionHandler>()
            .HandleAsync(new(Guid.NewGuid(), fixture.Customer, true, Guid.Empty), default)).IsSuccess);
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var project = await db.ThakaProjects.SingleAsync(x => x.Id == fixture.Project);
        project.Status = ThakaProjectStatus.Settled;
        await db.SaveChangesAsync();
        Assert.Equal("thaka.suspension_invalid_state", (await handler.HandleAsync(
            new(Guid.NewGuid(), fixture.Project, true, "test", fixture.Actor), default)).Error?.Code);
        Assert.True((await db.Customers.AsNoTracking().SingleAsync(x => x.Id == fixture.Customer)).IsActive);
    }

    private static async Task<Fixture> SeedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var product = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Suspension fixture " + Guid.NewGuid());
        var created = await scope.ServiceProvider.GetRequiredService<CreateThakaProjectHandler>().HandleAsync(
            new(customer.Id, "Suspension fixture", null, null, DateOnly.FromDateTime(DateTime.Today), product.ActorId, Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.True(created.IsSuccess, created.Error?.Message);
        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue { ProjectId = created.Value,
            ChallanNumber = "SUSP-" + Guid.NewGuid().ToString("N"), ClientOperationId = Guid.NewGuid(),
            TotalCharge = 100m, TotalCost = 10m, GrossProfit = 90m, IssuedBy = product.ActorId, IssuedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return new(created.Value, customer.Id, product.ActorId, product.ProductId, product.ProductUnitId);
    }

    private sealed record Fixture(Guid Project, Guid Customer, Guid Actor, Guid Product, Guid Unit);
}
