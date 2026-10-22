using EdgeRetails.Central.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeRetails.Central.Infrastructure.Persistence;

public class CentralDbContextFactory : IDesignTimeDbContextFactory<CentralDbContext>
{
    public CentralDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("CENTRAL_DATABASE_CONNECTION_STRING environment variable is required to create CentralDbContext. Silent connection fallback is strictly prohibited.");

        var targetGuard = new DatabaseTargetGuard();
        targetGuard.ValidateConnectionString(connectionString);

        var optionsBuilder = new DbContextOptionsBuilder<CentralDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema);
        });

        return new CentralDbContext(optionsBuilder.Options, targetGuard);
    }
}
