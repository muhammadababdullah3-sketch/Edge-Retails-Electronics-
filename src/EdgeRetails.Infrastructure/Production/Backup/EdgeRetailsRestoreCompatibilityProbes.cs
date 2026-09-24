using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EdgeRetails.Infrastructure.Production.Backup;

/// <summary>
/// Verifies that a restored staging database is exactly compatible with the current EF migration
/// history/model snapshot. The recovery identity is used only for the staging verification connection.
/// </summary>
public sealed class EdgeRetailsEfRestoreCompatibilityProbe : IRestoreStagingCompatibilityProbe
{
    public string Name => "ef-migration-model";

    public async Task ValidateAsync(
        RestoreStagingValidationContext context,
        CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = context.MaintenanceConnection.Host,
            Port = context.MaintenanceConnection.Port,
            Database = context.StagingDatabase,
            Username = context.MaintenanceConnection.Username,
            Password = context.MaintenanceConnection.Password.Reveal(),
            Pooling = false,
            Timeout = 15,
            CommandTimeout = 30
        };

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(
                builder.ConnectionString,
                npgsql => npgsql
                    .MigrationsAssembly(typeof(EdgeRetailsDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ef_migrations_history", "system"))
            .Options;

        await using var db = new EdgeRetailsDbContext(options);
        var known = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var modelDrift = db.Database.HasPendingModelChanges();
        var result = EdgeRetails.Infrastructure.Production.Startup.MigrationHistoryCompatibilityEvaluator
            .Evaluate(known, applied, modelDrift);

        if (!result.Compatible)
        {
            throw new InvalidDataException(
                $"Restored staging database failed EF compatibility: {result.State}.");
        }
    }
}

/// <summary>
/// Verifies critical restored business invariants beyond schema existence. PostgreSQL constraints are
/// re-checked explicitly so a dump from an incompatible/partially restored source cannot become Prepared.
/// </summary>
public sealed class EdgeRetailsBusinessRestoreCompatibilityProbe : IRestoreStagingCompatibilityProbe
{
    private readonly string _psql;
    private readonly IPostgresProcessRunner _runner;

    public EdgeRetailsBusinessRestoreCompatibilityProbe(
        string psqlPath,
        IPostgresProcessRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(psqlPath) || !File.Exists(psqlPath))
        {
            throw new FileNotFoundException("Required PostgreSQL psql executable was not found.", psqlPath);
        }

        _psql = psqlPath;
        _runner = runner ?? new ProcessRunner();
    }

    public string Name => "setup-inventory-finance";

    public async Task ValidateAsync(
        RestoreStagingValidationContext context,
        CancellationToken cancellationToken = default)
    {
        // Exactly zero or one installation singleton is valid. If present it must be PRIMARY.
        await RequireZeroAsync(
            context,
            """
            SELECT COUNT(*)::text
            FROM system.installation_state
            WHERE singleton_key <> 'PRIMARY';
            """,
            "Installation singleton state is invalid.",
            cancellationToken);

        await RequireAtMostOneAsync(
            context,
            "SELECT COUNT(*)::text FROM system.installation_state;",
            "Multiple installation singleton rows were restored.",
            cancellationToken);

        // Inventory snapshots must remain non-negative and movement snapshots internally coherent.
        await RequireZeroAsync(
            context,
            """
            SELECT COUNT(*)::text
            FROM inventory.stock_balances
            WHERE sellable_qty < 0
               OR damaged_qty < 0
               OR defective_qty < 0
               OR with_supplier_qty < 0
               OR scrap_qty < 0;
            """,
            "Inventory stock balances contain negative quantities.",
            cancellationToken);

        await RequireZeroAsync(
            context,
            """
            SELECT COUNT(*)::text
            FROM inventory.cost_states
            WHERE costed_qty < 0
               OR total_inventory_cost < 0
               OR moving_average_cost < 0
               OR (last_purchase_cost IS NOT NULL AND last_purchase_cost < 0);
            """,
            "Inventory cost state contains invalid negative values.",
            cancellationToken);

        await RequireZeroAsync(
            context,
            """
            SELECT COUNT(*)::text
            FROM inventory.movement_effects
            WHERE quantity_before < 0
               OR quantity_after < 0
               OR quantity_after - quantity_before <> quantity_delta;
            """,
            "Inventory movement effects are internally inconsistent.",
            cancellationToken);

        // Finance snapshots must remain within the canonical non-negative/positive constraints.
        await RequireZeroAsync(
            context,
            """
            SELECT COUNT(*)::text
            FROM finance.cash_sessions
            WHERE opening_cash < 0
               OR (counted_closing_cash IS NOT NULL AND counted_closing_cash < 0);
            """,
            "Finance cash-session state contains invalid negative values.",
            cancellationToken);

        await RequireZeroAsync(
            context,
            "SELECT COUNT(*)::text FROM finance.cash_movements WHERE amount <= 0;",
            "Finance cash movements contain non-positive amounts.",
            cancellationToken);
    }

    private async Task RequireZeroAsync(
        RestoreStagingValidationContext context,
        string sql,
        string message,
        CancellationToken cancellationToken)
    {
        var value = await QueryCountAsync(context, sql, cancellationToken);
        if (value != 0)
        {
            throw new InvalidDataException(message);
        }
    }

    private async Task RequireAtMostOneAsync(
        RestoreStagingValidationContext context,
        string sql,
        string message,
        CancellationToken cancellationToken)
    {
        var value = await QueryCountAsync(context, sql, cancellationToken);
        if (value > 1)
        {
            throw new InvalidDataException(message);
        }
    }

    private async Task<long> QueryCountAsync(
        RestoreStagingValidationContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            _psql,
            new[]
            {
                "--no-password", "--tuples-only", "--no-align", "--quiet",
                "--host", context.MaintenanceConnection.Host,
                "--port", context.MaintenanceConnection.Port.ToString(),
                "--username", context.MaintenanceConnection.Username,
                "--dbname", context.StagingDatabase,
                "--command", sql
            },
            new Dictionary<string, string?>
            {
                ["PGPASSWORD"] = context.MaintenanceConnection.Password.Reveal()
            },
            cancellationToken);

        if (result.ExitCode != 0 ||
            !long.TryParse(result.StandardOutput.Trim(), out var count) ||
            count < 0)
        {
            throw new InvalidDataException("Restore invariant query could not be verified safely.");
        }

        return count;
    }
}
