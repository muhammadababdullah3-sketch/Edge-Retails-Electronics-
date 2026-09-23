using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Production;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddEdgeRetailsInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<EdgeRetailsDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql
                    .MigrationsAssembly(typeof(EdgeRetailsDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ef_migrations_history", "system")
                    .CommandTimeout(ResolveDbCommandTimeoutSeconds())));

        var productionStateRoot = ResolveProductionStateRoot();
        services.AddSingleton<IProductionMaintenanceIntegrityKeyProvider>(
            _ => new FileProductionMaintenanceIntegrityKeyProvider(
                Path.Combine(productionStateRoot, "maintenance-integrity.key")));
        services.AddSingleton<IProductionMaintenanceBarrier>(provider =>
            new FileProductionMaintenanceBarrier(
                Path.Combine(productionStateRoot, "maintenance"),
                provider.GetRequiredService<IProductionMaintenanceIntegrityKeyProvider>()));
        services.AddSingleton<EdgeRetails.Application.Production.Diagnostics.IDiskSpaceProbe>(_ =>
            new EdgeRetails.Infrastructure.Production.Diagnostics.DriveDiskSpaceProbe(
                productionStateRoot,
                500L * 1024 * 1024));
        services.AddSingleton<ProductionMaintenanceWriteGuard>();
        services.AddSingleton<EdgeRetails.Application.Production.Diagnostics.Phase5DiagnosticsPolicy>(
            _ => EdgeRetails.Application.Production.Diagnostics.Phase5DiagnosticsPolicy.FromEnvironment());
        services.AddScoped<EdgeRetails.Application.Production.Diagnostics.IPhase5DiagnosticsService>(
            provider => new EdgeRetails.Infrastructure.Production.Diagnostics.Phase5DiagnosticsService(
                provider.GetRequiredService<EdgeRetails.Infrastructure.Persistence.EdgeRetailsDbContext>(),
                provider.GetRequiredService<EdgeRetails.Application.Production.Diagnostics.Phase5DiagnosticsPolicy>(),
                productionStateRoot));

        services.AddSingleton<EdgeRetails.Application.Production.Backup.IBackupJobLock>(_ =>
            new FileBackupJobLock(Path.Combine(productionStateRoot, "backup.lock")));
        services.AddSingleton<IProductionAuditSink>(provider =>
            new EdgeRetails.Infrastructure.Production.FileProductionAuditSink(
                Path.Combine(productionStateRoot, "production-audit.jsonl"),
                provider.GetService<IProductionMaintenanceIntegrityKeyProvider>()));
        services.AddSingleton<IProductionAuditFailureReporter, EdgeRetails.Infrastructure.Production.LoggingProductionAuditFailureReporter>();
        services.AddSingleton<ProductionAuditCoordinator>();

        services.AddSingleton<EdgeRetails.Application.Production.Printing.IPrinterProfileStore>(_ =>
            new EdgeRetails.Infrastructure.Production.Printing.JsonPrinterProfileStore(Path.Combine(productionStateRoot, "printer-profiles.json")));
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IPrintJobStore>(_ =>
            new EdgeRetails.Infrastructure.Production.Printing.JsonPrintJobStore(Path.Combine(productionStateRoot, "print-jobs.json")));
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IPrinterProfileValidator, EdgeRetails.Infrastructure.Production.Printing.BasicPrinterProfileValidator>();
        services.AddSingleton<EdgeRetails.Application.Production.Printing.IProductionPrintEngine, EdgeRetails.Infrastructure.Production.Printing.SimulatedProductionPrintEngine>();
        services.AddScoped<IProductionAuthorization, EdgeRetails.Infrastructure.Production.DefaultProductionAuthorization>();
        services.AddScoped<EdgeRetails.Application.Production.IProductionDocumentAuthorizationPolicy, EdgeRetails.Application.Production.Printing.ProductionDocumentAuthorizationPolicy>();

        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.PosSaleReceiptKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.SaleReturnReceiptKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.PurchaseDocumentKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.PurchaseReturnDocumentKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.ThakaMaterialChallanKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.ThakaPaymentReceiptKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentKindSource, EdgeRetails.Infrastructure.Production.Printing.FinalSettlementStatementKindSource>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.IProductionDocumentSource, EdgeRetails.Application.Production.Printing.ProductionDocumentSourceRouter>();

        services.AddScoped<EdgeRetails.Application.Production.Printing.PrintDocumentHandler>();
        services.AddScoped<EdgeRetails.Application.Production.Printing.SavePrinterProfileHandler>();
        services.AddScoped<EdgeRetails.Application.Production.Outbox.IOutboxEffectHandler, EdgeRetails.Application.Production.Outbox.PrintOutboxEffectHandler>();
        services.AddScoped<EdgeRetails.Application.Production.Outbox.OutboxProcessor>();

        services.AddScoped<IUnitOfWork>(
            provider => provider.GetRequiredService<EdgeRetailsDbContext>());
        services.AddScoped<ITransactionRunner, EfTransactionRunner>();
        services.AddScoped<IReceiptSnapshotProvider, ReceiptSnapshotProvider>();
        services.AddScoped<IBusinessAuditWriter, BusinessAuditWriter>();
        services.AddScoped<PostgresOperationLock>();
        services.AddScoped<IOperationLock>(
            provider => provider.GetRequiredService<PostgresOperationLock>());
        services.AddScoped<IResourceLock>(
            provider => provider.GetRequiredService<PostgresOperationLock>());

        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IWarrantyRepository, WarrantyRepository>();
        services.AddScoped<ICashRepository, CashRepository>();
        services.AddScoped<IQuotationRepository, QuotationRepository>();
        services.AddScoped<ISalesRepository, SalesRepository>();
        services.AddScoped<IPurchasingRepository, PurchasingRepository>();
        services.AddScoped<IPartyRepository, PartyRepository>();
        services.AddScoped<ITraceabilityRepository, TraceabilityRepository>();
        services.AddScoped<ISupplierAccountRepository, SupplierAccountRepository>();
        services.AddScoped<IPosDraftRepository, PosDraftRepository>();
        services.AddScoped<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<IThakaRepository, ThakaRepository>();
        services.AddScoped<EdgeRetails.Application.Features.Terminals.ITerminalRepository, TerminalRepository>();
        services.AddScoped<OutboxRepository>();
        services.AddScoped<EdgeRetails.Application.Production.Outbox.IOutboxRepository>(p => p.GetRequiredService<OutboxRepository>());
        services.AddScoped<EdgeRetails.Application.Production.Outbox.IOutboxWriter>(p => p.GetRequiredService<OutboxRepository>());
        services.AddScoped<ISetupRepository, SetupRepository>();
        services.AddScoped<IIdentityReadRepository, IdentityReadRepository>();
        services.AddScoped<IIdentitySessionRepository, IdentitySessionRepository>();
        services.AddScoped<IPinCredentialService, Pbkdf2PinCredentialService>();
        services.AddScoped<IDatabaseReadinessService, EfDatabaseReadinessService>();
        services.AddScoped<IInstallationStateReadService, InstallationStateReadService>();
        services.AddScoped<IInventoryCostAllocator, InventoryCostAllocator>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.IInventoryConditionService, EdgeRetails.Application.Features.Inventory.InventoryConditionService>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.ICashMovementService, EdgeRetails.Application.Features.Finance.CashMovementService>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.ISalesReadService, SalesReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.IPosCatalogReadService, PosCatalogReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Purchasing.IPurchasingReadService, PurchasingReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Purchasing.IPurchaseCatalogReadService, PurchaseCatalogReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.IInventoryProvenanceReadService, InventoryProvenanceReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.IInventoryOverviewReadService, InventoryOverviewReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.IPhase4WorkflowReadService, Phase4WorkflowReadService>();
        services.AddScoped<ProductManagementReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.IProductManagementReadService>(
            provider => provider.GetRequiredService<ProductManagementReadService>());
        services.AddScoped<EdgeRetails.Application.Features.Catalog.IProductCatalogSafetyReadService>(
            provider => provider.GetRequiredService<ProductManagementReadService>());
        services.AddScoped<EdgeRetails.Application.Features.Finance.IExpenseReadService, ExpenseReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.ISupplierAccountReadService, SupplierAccountReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Parties.IPartyDirectoryReadService, PartyDirectoryReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Reporting.IReportingReadService, ReportingReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.IThakaReadService, ThakaReadService>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.IWarrantyReadService, WarrantyReadService>();

        services.AddScoped<EdgeRetails.Application.Features.Sales.CompleteSaleHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.CreateSaleReturnHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.SavePosDraftHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.CancelPosDraftHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.CompletePosDraftHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Sales.CommercialExchangeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Purchasing.CreatePurchaseHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Purchasing.CreatePurchaseReturnHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Purchasing.VoidPurchaseHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.TransferInventoryConditionHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.CreateStockAdjustmentHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.CreateWarrantyClaimHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.BeginWarrantyClaimReviewHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.CancelWarrantyClaimHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.SendWarrantyClaimToSupplierHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.MarkWarrantySupplierProcessingHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.RecordWarrantyResolutionHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.ReceiveCustomerWarrantyReplacementHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.HandoverWarrantyItemHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.SendShopStockToSupplierWarrantyHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Warranty.ReceiveShopStockWarrantyHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Parties.GetSuppliersHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Parties.GetCustomersHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Parties.SaveCustomerHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Parties.SaveSupplierHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.PostExpenseHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.VoidExpenseHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.CreateSupplierPaymentHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.ReverseSupplierPaymentHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.CreateSupplierRefundHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.ReverseSupplierRefundHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.SupplierAccountAdjustmentHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.OpenCashSessionHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.RecordManualCashMovementHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Finance.CloseCashSessionHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.CreateStocktakeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.StartStocktakeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.RecordStocktakeCountHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.RecordSerializedStocktakeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.ReviewStocktakeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.PostStocktakeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Inventory.CancelStocktakeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.CreateProductHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.UpdateProductHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.DeactivateProductHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.ReactivateProductHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.SetSupplierProductActiveHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.SaveCategoryHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.SetCategoryActiveHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.SaveUnitHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.SetUnitActiveHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.ConfigureProductUnitsHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Catalog.SetProductUnitBarcodeHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.CreateThakaProjectHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.IssueThakaMaterialHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.RecordThakaPaymentHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.SettleThakaHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.ReopenThakaHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.ReverseThakaMaterialHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Thaka.ReverseThakaPaymentHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Setup.FirstSetupBootstrapHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Settings.GetSettingsConfigurationHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Settings.UpdateShopProfileHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Settings.UpdateReceiptTemplateHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Identity.GetLoginAccountsHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Identity.AuthenticateUserHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Identity.EndUserSessionHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Identity.IApplicationPermissionAuthorizer, EdgeRetails.Application.Features.Identity.ApplicationPermissionAuthorizer>();
        services.AddScoped<EdgeRetails.Application.Features.Terminals.RegisterTerminalHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Terminals.TerminalHeartbeatHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Terminals.UpdateTerminalStatusHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Terminals.AuthoritativeRevalidationHandler>();
        services.AddScoped<EdgeRetails.Application.Features.Terminals.OperationStatusQueryHandler>();
        services.AddScoped<EdgeRetails.Application.Gateways.IApplicationGateway, EdgeRetails.Application.Gateways.LocalApplicationGateway>();

        services.AddScoped<IDocumentNumberService, PostgresDocumentNumberService>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, UuidV7IdGenerator>();

        return services;
    }

    private static int ResolveDbCommandTimeoutSeconds()
    {
        var configured = Environment.GetEnvironmentVariable("EDGE_RETAILS_DB_COMMAND_TIMEOUT_SECONDS");
        return int.TryParse(configured, out var seconds) && seconds is >= 1 and <= 3600
            ? seconds
            : 180;
    }

    private static string ResolveProductionStateRoot()
    {
        var configured = Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            throw new InvalidOperationException("Local application-data directory is unavailable for production state.");
        }

        return Path.Combine(local, "EdgeRetails", "Production");
    }
}

