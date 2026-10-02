using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using System.Net.Http;
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
    private readonly DesktopApiClient? _apiClient;
    private readonly IServiceScopeFactory? _scopeFactory;

    public BackendPosCatalogGateway(DesktopApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public BackendPosCatalogGateway(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<PosCatalogGatewayItem>> LoadAsync(
        string? search,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PosCatalogProductDto> rows;
        if (_apiClient is not null)
        {
            var query = $"/api/sales/catalog?pageSize={Math.Clamp(pageSize, 1, 200)}";
            if (!string.IsNullOrWhiteSpace(search))
            {
                query += $"&search={Uri.EscapeDataString(search.Trim())}";
            }

            rows = await _apiClient.GetAsync<PosCatalogProductDto[]>(query, cancellationToken);
        }
        else
        {
            await using var scope = _scopeFactory!.CreateAsyncScope();
            var reads = scope.ServiceProvider.GetRequiredService<IPosCatalogReadService>();
            rows = await reads.GetSellableCatalogAsync(
                search, Math.Clamp(pageSize, 1, 200), cancellationToken);
        }

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

    private BackendRuntime(ServiceProvider provider, DesktopApiClient apiClient)
    {
        _provider = provider;
        ApiClient = apiClient;
        RemoteGateway = provider.GetRequiredService<RemoteApplicationGateway>();
        ScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        PosCatalogGateway = new BackendPosCatalogGateway(ApiClient);
        SetupService = new RemoteBackendSetupService(ApiClient);
    }

    public DesktopApiClient ApiClient { get; }
    public RemoteApplicationGateway RemoteGateway { get; }

    public IServiceScopeFactory ScopeFactory { get; }

    public IPosCatalogGateway PosCatalogGateway { get; }

    public IBackendSetupService SetupService { get; }

    public async Task<BackendStartupState> CheckStartupAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await ApiClient.GetAsync<ServerVersion>(
                "/api/system/version", cancellationToken);
            if (!TerminalProtocol.IsCompatible(version.ProtocolVersion) ||
                !string.Equals(version.ProtocolVersion, TerminalProtocol.CurrentProtocolVersion, StringComparison.Ordinal))
            {
                return new BackendStartupState(false, false,
                    "The local Server protocol is incompatible with this Desktop version.", []);
            }

            var readiness = await ApiClient.GetAsync<ServerReady>(
                "/api/system/ready", cancellationToken);
            if (!string.Equals(readiness.Status, "Ready", StringComparison.Ordinal) ||
                !readiness.CanConnect || readiness.HasPendingMigrations)
            {
                return new BackendStartupState(false, false,
                    "The local Server did not report a ready database state.", []);
            }

            var terminalSecret = Environment.GetEnvironmentVariable("EDGE_RETAILS_TERMINAL_SECRET");
            if (string.IsNullOrWhiteSpace(terminalSecret))
            {
                return new BackendStartupState(false, false,
                    "Terminal authentication is not configured. Set EDGE_RETAILS_TERMINAL_SECRET for this installation.", []);
            }

            var terminalCode = Environment.GetEnvironmentVariable("EDGE_RETAILS_TERMINAL_CODE");
            if (string.IsNullOrWhiteSpace(terminalCode))
            {
                terminalCode = "LOCAL";
            }

            var registered = await ApiClient.PostAsync<RegisterTerminalCommand, RegisterTerminalResult>(
                "/api/terminals/register",
                new RegisterTerminalCommand(
                    terminalCode, Environment.MachineName, null,
                    TerminalProtocol.CurrentProtocolVersion, null, terminalSecret),
                cancellationToken);
            if (registered.Status != TerminalStatus.Active)
            {
                return new BackendStartupState(false, false,
                    $"Terminal is {registered.Status}. Access must be restored on the Server.", []);
            }

            ApiClient.SetTerminalContext(registered.TerminalId, terminalSecret);
            RemoteGateway.SetTerminalContext(registered.TerminalId, terminalSecret);

            var setup = await ApiClient.GetAsync<ServerSetupState>(
                "/api/setup/state", cancellationToken);
            return new BackendStartupState(true, setup.IsSetupRequired, null, []);
        }
        catch (DesktopApiException ex)
        {
            return new BackendStartupState(false, false,
                $"[{ex.Code}] {ex.Message}", []);
        }
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

    public static BackendRuntime CreateFromEnvironment(Action<IServiceCollection>? configureServices = null)
    {
        var serverUrl = Environment.GetEnvironmentVariable("EDGE_RETAILS_SERVER_URL");
        DesktopApiClient? apiClient = null;
        ServiceProvider? provider = null;
        var apiHttpClient = new HttpClient(
            new DesktopSessionForwardingHandler(
                () => apiClient?.CurrentSessionId,
                (sessionId, code) => apiClient?.InvalidateSessionIfMatches(sessionId, code)))
        {
            BaseAddress = new Uri(string.IsNullOrWhiteSpace(serverUrl)
                ? "http://127.0.0.1:7150"
                : serverUrl.Trim(), UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
        try
        {
            apiClient = new DesktopApiClient(apiHttpClient, ownsClient: true);
            var services = CreateRemoteDesktopServiceCollection();
            services.AddSingleton(apiClient);
            services.AddSingleton(_ => new RemoteApplicationGateway(apiHttpClient));
            services.AddSingleton<IApplicationGateway>(sp =>
                sp.GetRequiredService<RemoteApplicationGateway>());
            configureServices?.Invoke(services);

            provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            return new BackendRuntime(provider, apiClient);
        }
        catch
        {
            provider?.Dispose();
            if (apiClient is null)
            {
                apiHttpClient.Dispose();
            }
            else
            {
                apiClient.Dispose();
            }
            throw;
        }
    }

    public static ServiceCollection CreateDesktopServiceCollection(string connectionString)
    {
        var services = new ServiceCollection();
        // Transitional Phase 3 composition: PageViewModelFactory still creates DB-backed
        // purchasing, product, Thaka, business operations, sales history, workflow, dashboard,
        // and settings adapters. Wave 2/3 must replace those adapters before removing this
        // Desktop Infrastructure/connection-string dependency. POS catalog and mutations use HTTP.
        services.AddEdgeRetailsInfrastructure(connectionString);

        // Production Desktop explicitly overrides simulated engines with WPF print engines:
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IPhysicalStickerPrintEngine, EdgeRetails.Desktop.Production.Printing.WpfPhysicalStickerPrintEngine>();
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IProductionPrintEngine, EdgeRetails.Desktop.Production.Printing.WpfProductionPrintEngine>();

        return services;
    }

    public static ServiceCollection CreateRemoteDesktopServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IPhysicalStickerPrintEngine,
            EdgeRetails.Desktop.Production.Printing.WpfPhysicalStickerPrintEngine>();
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IProductionPrintEngine,
            EdgeRetails.Desktop.Production.Printing.WpfProductionPrintEngine>();
        return services;
    }

    public void Dispose()
    {
        ApiClient.Dispose();
        _provider.Dispose();
    }

    private sealed record ServerVersion(string ProtocolVersion);
    private sealed record ServerReady(string Status, bool CanConnect, bool HasPendingMigrations);
    private sealed record ServerSetupState(bool IsSetupRequired);
}
