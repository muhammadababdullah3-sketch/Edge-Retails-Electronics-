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

    public static BackendRuntime CreateFromEnvironment()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_DB is not configured. Production startup cannot fall back to demo data.");
        }

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString.Trim());

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
