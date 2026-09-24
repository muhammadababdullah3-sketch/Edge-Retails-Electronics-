using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EdgeRetails.Infrastructure.Persistence;

public sealed class DesignTimeEdgeRetailsDbContextFactory
    : IDesignTimeDbContextFactory<EdgeRetailsDbContext>
{
    public EdgeRetailsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var commonConfig = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EdgeRetails",
                "config.json");
            if (System.IO.File.Exists(commonConfig))
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(commonConfig);
                    using var doc = System.Text.Json.JsonDocument.Parse(stream);
                    if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                        cs.TryGetProperty("DefaultConnection", out var def) &&
                        !string.IsNullOrWhiteSpace(def.GetString()))
                    {
                        connectionString = def.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("EDGE_RETAILS_DB", out var edb) &&
                             !string.IsNullOrWhiteSpace(edb.GetString()))
                    {
                        connectionString = edb.GetString();
                    }
                }
                catch { }
            }
        }

        connectionString ??= "Host=localhost;Database=edge_retails_design;Username=edge_retails_design";

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
