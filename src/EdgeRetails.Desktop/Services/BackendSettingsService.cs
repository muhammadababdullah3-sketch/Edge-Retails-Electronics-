using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Settings;
using EdgeRetails.Application.Production;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendSettingsSnapshot(
    IReadOnlyList<SettingsUserRecord> Users,
    string ShopName,
    string OwnerName,
    string Phone,
    string Address,
    string ReceiptHeader,
    string ReceiptFooter,
    bool ShowCustomer,
    bool ShowCashier,
    bool AutoPrintDefault,
    string DatabaseName,
    string DatabaseSize,
    string DatabaseStatus,
    string ConnectionStatus,
    string WorkerStatus,
    string LicenseId,
    string LicenseStore,
    string LicenseModule,
    string LicenseExpiry,
    string LicensedTerminals,
    string LicenseStatus,
    string LastBackupDisplay,
    string MaintenanceStatus,
    IReadOnlyList<string> Issues);

public interface IBackendSettingsService
{
    Task<BackendSettingsSnapshot> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveShopAsync(
        string shopName,
        string? phone,
        string? address,
        CancellationToken cancellationToken = default);

    Task SaveReceiptTemplateAsync(
        string? header,
        string? footer,
        bool showCustomer,
        bool showCashier,
        bool autoPrintDefault,
        CancellationToken cancellationToken = default);
}

public sealed class BackendSettingsService : IBackendSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendSettingsService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }

    public async Task<BackendSettingsSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var users = new List<SettingsUserRecord>();
        var issues = new List<string>();

        var shopName = string.Empty;
        var ownerName = string.Empty;
        var phone = string.Empty;
        var address = string.Empty;
        var receiptHeader = string.Empty;
        var receiptFooter = string.Empty;
        var showCustomer = false;
        var showCashier = false;
        var autoPrintDefault = false;

        var databaseStatus = "Unavailable";
        var connectionStatus = "Database readiness unavailable";
        var maintenanceStatus = "Unavailable";

        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            var accounts = scope.ServiceProvider
                .GetRequiredService<GetLoginAccountsHandler>();
            var rows = await accounts.HandleAsync(cancellationToken);
            users.AddRange(rows.Select(row =>
                new SettingsUserRecord(row.DisplayName, row.RoleName, isActive: true)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"User directory unavailable: {ex.Message}");
        }

        ownerName = users.FirstOrDefault(user =>
            string.Equals(user.Role, "Owner", StringComparison.OrdinalIgnoreCase))?.Name
            ?? string.Empty;

        try
        {
            var settings = scope.ServiceProvider
                .GetRequiredService<GetSettingsConfigurationHandler>();
            var configuration = await settings.HandleAsync(cancellationToken);
            if (configuration is null)
            {
                issues.Add("Authoritative shop/receipt settings are not configured.");
            }
            else
            {
                shopName = configuration.ShopName;
                phone = configuration.Phone ?? string.Empty;
                address = configuration.Address ?? string.Empty;
                receiptHeader = configuration.ReceiptHeader ?? string.Empty;
                receiptFooter = configuration.ReceiptFooter ?? string.Empty;
                showCustomer = configuration.ShowCustomer;
                showCashier = configuration.ShowCashier;
                autoPrintDefault = configuration.AutoPrintDefault;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"Shop/receipt settings unavailable: {ex.Message}");
        }

        try
        {
            var readiness = scope.ServiceProvider
                .GetRequiredService<IDatabaseReadinessService>();
            var result = await readiness.CheckAsync(cancellationToken);

            databaseStatus = result.IsReady
                ? "Ready"
                : result.HasPendingMigrations
                    ? "Migration Pending"
                    : "Unavailable";

            connectionStatus = result.IsReady
                ? "Connected · schema ready"
                : !string.IsNullOrWhiteSpace(result.FailureReason)
                    ? result.FailureReason
                    : result.HasPendingMigrations
                        ? "Connected · pending migrations"
                        : "Connection unavailable";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"Database diagnostics unavailable: {ex.Message}");
        }

        try
        {
            var maintenance = scope.ServiceProvider
                .GetRequiredService<IProductionMaintenanceBarrier>();
            maintenanceStatus = (await maintenance.GetStateAsync(cancellationToken)).ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            maintenanceStatus = "RecoveryRequired";
            issues.Add($"Maintenance state unavailable: {ex.Message}");
        }

        // The hardened Sprint 8 subsystems exist, but the current Desktop DI graph
        // does not expose the final Phase 6 operator adapters for license, worker,
        // backup/restore history, or database-size diagnostics. Do not fake them.
        return new BackendSettingsSnapshot(
            users,
            shopName,
            ownerName,
            phone,
            address,
            receiptHeader,
            receiptFooter,
            showCustomer,
            showCashier,
            autoPrintDefault,
            "PostgreSQL",
            "Unavailable",
            databaseStatus,
            connectionStatus,
            "Unavailable · worker diagnostics not attached",
            "Unavailable",
            "Unavailable",
            "Unavailable",
            "Unavailable",
            "Unavailable",
            "Unavailable · license diagnostics not attached",
            "Unavailable · backup diagnostics not attached",
            maintenanceStatus,
            issues);
    }

    public async Task SaveShopAsync(
        string shopName,
        string? phone,
        string? address,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<UpdateShopProfileHandler>();
        var result = await handler.HandleAsync(
            new UpdateShopProfileCommand(
                shopName,
                phone,
                address,
                actor,
                Guid.CreateVersion7()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Shop settings update failed.");
        }
    }

    public async Task SaveReceiptTemplateAsync(
        string? header,
        string? footer,
        bool showCustomer,
        bool showCashier,
        bool autoPrintDefault,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<UpdateReceiptTemplateHandler>();
        var result = await handler.HandleAsync(
            new UpdateReceiptTemplateCommand(
                header,
                footer,
                showCustomer,
                showCashier,
                autoPrintDefault,
                actor,
                Guid.CreateVersion7()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Receipt-template update failed.");
        }
    }

    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new InvalidOperationException(
            "A persistent backend user session is required for Settings changes.");
}
