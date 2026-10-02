using Microsoft.Extensions.Configuration;

namespace EdgeRetails.Infrastructure;

/// <summary>
/// Resolves the database authority using the same key precedence for every
/// installed production process that opens the operational database.
/// </summary>
public static class ProductionDatabaseConnectionStringResolver
{
    public static string Resolve(
        IConfiguration configuration,
        bool isTesting = false,
        string? isolatedTestConnectionString = null,
        bool allowProcessEnvironmentFallback = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var resolved = isTesting ? isolatedTestConnectionString : null;
        resolved ??= configuration.GetConnectionString("DefaultConnection");
        resolved ??= configuration["EDGE_RETAILS_DB"];
        resolved ??= configuration["DatabaseConnectionString"];
        resolved ??= isTesting ? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB") : null;
        if (allowProcessEnvironmentFallback)
        {
            resolved ??= Environment.GetEnvironmentVariable("EDGE_RETAILS_DB");
        }

        return !string.IsNullOrWhiteSpace(resolved)
            ? resolved
            : throw new InvalidOperationException(
                "No database connection string configured. Set ConnectionStrings:DefaultConnection or EDGE_RETAILS_DB.");
    }
}
