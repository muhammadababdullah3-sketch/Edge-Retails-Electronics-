using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeRetails.Infrastructure.Persistence;

public sealed class DesignTimeEdgeRetailsDbContextFactory
    : IDesignTimeDbContextFactory<EdgeRetailsDbContext>
{
    public EdgeRetailsDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("EDGE_RETAILS_DB")
            ?? "Host=localhost;Database=edge_retails_design;Username=edge_retails_design";

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql
                    .MigrationsAssembly(typeof(EdgeRetailsDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ef_migrations_history", "system"))
            .Options;

        return new EdgeRetailsDbContext(options);
    }
}
