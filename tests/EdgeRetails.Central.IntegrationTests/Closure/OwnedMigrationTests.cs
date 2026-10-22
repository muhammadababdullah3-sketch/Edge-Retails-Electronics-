using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.IntegrationTests.Closure;

public class OwnedMigrationTests
{
    [Fact]
    public async Task OwnedMigrations_InitializeAndReapply_WithoutPendingOrShopTables()
    {
        var connection = Environment.GetEnvironmentVariable("CENTRAL_MIGRATION_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Owned migration connection must be explicitly supplied.");
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(connection, p => p.MigrationsHistoryTable("__ef_migrations_history", "central"))
            .Options;
        await using var db = new CentralDbContext(options, new DatabaseTargetGuard());
        await db.Database.MigrateAsync();
        var first = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.NotEmpty(first);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(first, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
