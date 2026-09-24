using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Production;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendStartupState(
    bool IsReady,
    bool IsSetupRequired,
    string? FailureReason,
    IReadOnlyList<string> PendingMigrations);

public sealed record PosCatalogGatewayItem(
    Guid ProductId,
    Guid ProductUnitId,
    string Name,
    string Sku,
    string Category,
    string UnitSymbol,
    decimal SellableStock,
    decimal UnitPrice,
    decimal ReferenceCost,
    bool IsSerialized);

public interface IPosCatalogGateway
{
    Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public sealed class BackendPosCatalogGateway : IPosCatalogGateway
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BackendPosCatalogGateway(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();
        var rows = await reads.GetSellableCatalogAsync(
            search,
            Math.Clamp(pageSize, 1, 200),
            cancellationToken);

        return rows.Select(row => new PosCatalogGatewayItem(
            row.ProductId,
            row.ProductUnitId,
            row.Name,
            row.Sku ?? string.Empty,
            row.Category,
            row.UnitSymbol,
            row.SellableStock,
            row.UnitPrice,
            row.ReferenceCost,
            row.IsSerialized)).ToArray();
    }
}

public sealed class BackendRuntime : IDisposable
{
    private readonly ServiceProvider _provider;

    private BackendRuntime(ServiceProvider provider)
    {
        _provider = provider;
        ScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        PosCatalogGateway = new BackendPosCatalogGateway(ScopeFactory);
        SetupService = new BackendSetupService(ScopeFactory);
    }

    public IServiceScopeFactory ScopeFactory { get; }

    public IPosCatalogGateway PosCatalogGateway { get; }

    public IBackendSetupService SetupService { get; }

    public async Task<BackendStartupState> CheckStartupAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var readiness = scope.ServiceProvider
            .GetRequiredService<IDatabaseReadinessService>();
        var result = await readiness.CheckAsync(cancellationToken);

        if (!result.IsReady)
        {
            return new BackendStartupState(
                false,
                false,
                result.FailureReason,
                result.PendingMigrations);
        }

        ProductionMaintenanceState maintenanceState;
        try
        {
            maintenanceState = await scope.ServiceProvider
                .GetRequiredService<IProductionMaintenanceBarrier>()
                .GetStateAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new BackendStartupState(
                false,
                false,
                "Production maintenance/recovery state could not be verified safely.",
                Array.Empty<string>());
        }

        if (maintenanceState != ProductionMaintenanceState.Normal)
        {
            return new BackendStartupState(
                false,
                false,
                maintenanceState == ProductionMaintenanceState.RecoveryRequired
                    ? "Restore recovery is required before setup, login, or business activity."
                    : $"A restore operation is active ({maintenanceState}); normal startup is blocked.",
                Array.Empty<string>());
        }

        var installation = scope.ServiceProvider
            .GetRequiredService<IInstallationStateReadService>();
        var state = await installation.GetAsync(cancellationToken);

        return new BackendStartupState(
            true,
            state?.SetupStatus != SetupStatus.Complete,
            null,
            Array.Empty<string>());
    }

    public static string ResolveConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_DB");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString.Trim();
        }

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programDataConfig = System.IO.Path.Combine(commonAppData, "EdgeRetails", "config.json");
        connectionString = TryReadConnectionString(programDataConfig);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString.Trim();
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localConfig = System.IO.Path.Combine(localAppData, "EdgeRetails", "config.json");
        connectionString = TryReadConnectionString(localConfig);
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString.Trim();
        }

        throw new InvalidOperationException(
            $"EDGE_RETAILS_DB is not configured and no persistent configuration file was found at '{programDataConfig}' or '{localConfig}'. Production startup cannot fall back to demo data.");
    }

    private static string? TryReadConnectionString(string filePath)
    {
        if (!System.IO.File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(filePath);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("ConnectionStrings", out var connStrings) &&
                connStrings.ValueKind == System.Text.Json.JsonValueKind.Object &&
                connStrings.TryGetProperty("DefaultConnection", out var defaultConn) &&
                defaultConn.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var val = defaultConn.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }

            if (root.TryGetProperty("EDGE_RETAILS_DB", out var edgeDb) &&
                edgeDb.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var val = edgeDb.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }

            if (root.TryGetProperty("DatabaseConnectionString", out var dbConn) &&
                dbConn.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var val = dbConn.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return val;
                }
            }
        }
        catch
        {
            // Ignore unreadable/malformed files and fall back
        }

        return null;
    }

    public static BackendRuntime CreateFromEnvironment()
    {
        var connectionString = ResolveConnectionString();

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        return new BackendRuntime(provider);
    }

    public void Dispose()
    {
        _provider.Dispose();
    }
}
