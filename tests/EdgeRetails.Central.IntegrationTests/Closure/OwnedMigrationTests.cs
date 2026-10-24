using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        Assert.Same(typeof(CentralDbContext).Assembly, migrationsAssembly.Assembly);
        Assert.DoesNotContain(migrationsAssembly.Migrations.Keys, migrationId =>
            migrationId.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
            migrationId.Contains("PointOfSale", StringComparison.OrdinalIgnoreCase));

        var applicationRelationsOutsideCentral = await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::int AS "Value"
            FROM pg_class AS relation
            JOIN pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE relation.relkind IN ('r', 'p', 'v', 'm')
              AND schema.nspname NOT IN ('central', 'pg_catalog', 'information_schema')
              AND schema.nspname !~ '^pg_toast'
              AND schema.nspname !~ '^pg_temp'
            """).SingleAsync();
        Assert.Equal(0, applicationRelationsOutsideCentral);

        await db.Database.MigrateAsync();
        Assert.Equal(first, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
