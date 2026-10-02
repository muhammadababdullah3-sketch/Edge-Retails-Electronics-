using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Settings;
using EdgeRetails.Application.Features.Setup;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Printing;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.UnitTests;

/// <summary>
/// Production DI Composition Certification (AGENT E - Mission §50, §53):
/// Proves that the production composition root registers canonical production services,
/// repositories, handlers, and engines, and NEVER registers demo authorities or test doubles.
/// </summary>
public sealed class ProductionInfrastructureDiCompositionTests
{
    private static IServiceCollection CreateProductionServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEdgeRetailsInfrastructure("Host=localhost;Database=edge_retails_prod;Username=test;Password=test");
        return services;
    }

    [Fact]
    public void Infrastructure_Registers_All_Canonical_Repositories()
    {
        var services = CreateProductionServices();

        AssertRegistration<ICatalogRepository, CatalogRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IInventoryRepository, InventoryRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<ISalesRepository, SalesRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IPurchasingRepository, PurchasingRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IWarrantyRepository, WarrantyRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<ISupplierAccountRepository, SupplierAccountRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<ITerminalRepository, TerminalRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IThakaRepository, ThakaRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IPosDraftRepository, PosDraftRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IExpenseRepository, ExpenseRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IPartyRepository, PartyRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<ITraceabilityRepository, TraceabilityRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<ISetupRepository, SetupRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IIdentityReadRepository, IdentityReadRepository>(services, ServiceLifetime.Scoped);
        AssertRegistration<IIdentitySessionRepository, IdentitySessionRepository>(services, ServiceLifetime.Scoped);
    }

    [Fact]
    public void Infrastructure_Registers_All_Canonical_Infrastructure_Services()
    {
        var services = CreateProductionServices();

        AssertRegistration<ITransactionRunner, EfTransactionRunner>(services, ServiceLifetime.Scoped);
        AssertRegistration<IDocumentNumberService, PostgresDocumentNumberService>(services, ServiceLifetime.Scoped);
        AssertRegistration<IClock, SystemClock>(services, ServiceLifetime.Singleton);
        AssertRegistration<IIdGenerator, UuidV7IdGenerator>(services, ServiceLifetime.Singleton);
        AssertRegistration<ISequenceHighWaterService, MachineSequenceHighWaterService>(services, ServiceLifetime.Singleton);
        AssertRegistration<IApplicationGateway, LocalApplicationGateway>(services, ServiceLifetime.Scoped);
        AssertRegistration<IProductionPrintEngine, SimulatedProductionPrintEngine>(services, ServiceLifetime.Singleton);
        AssertRegistration<IPhysicalStickerPrintEngine, SimulatedPhysicalStickerPrintEngine>(services, ServiceLifetime.Singleton);
        AssertRegistration<IPhysicalStickerDocumentSource, EfPhysicalStickerDocumentSource>(services, ServiceLifetime.Scoped);
        AssertRegistration<IApplicationPermissionAuthorizer, ApplicationPermissionAuthorizer>(services, ServiceLifetime.Scoped);
        AssertRegistration<IInventoryCostAllocator, InventoryCostAllocator>(services, ServiceLifetime.Scoped);
        AssertRegistration<IPinCredentialService, Pbkdf2PinCredentialService>(services, ServiceLifetime.Scoped);
        AssertRegistration<IDatabaseReadinessService, EfDatabaseReadinessService>(services, ServiceLifetime.Scoped);
        AssertRegistration<IInstallationStateReadService, InstallationStateReadService>(services, ServiceLifetime.Scoped);
    }

    [Fact]
    public void Infrastructure_BuildsProviderAndResolvesProductionReadServices()
    {
        using var provider = CreateProductionServices().BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var serviceProvider = scope.ServiceProvider;

        Assert.IsType<ExpenseReadService>(serviceProvider.GetRequiredService<IExpenseReadService>());
        Assert.IsType<ReportingReadService>(serviceProvider.GetRequiredService<IReportingReadService>());
        Assert.IsType<SalesReadService>(serviceProvider.GetRequiredService<ISalesReadService>());
        Assert.IsType<PurchasingReadService>(serviceProvider.GetRequiredService<IPurchasingReadService>());
        Assert.IsType<InventoryOverviewReadService>(serviceProvider.GetRequiredService<IInventoryOverviewReadService>());
        Assert.IsType<Phase4WorkflowReadService>(serviceProvider.GetRequiredService<IPhase4WorkflowReadService>());
        Assert.IsType<SupplierAccountReadService>(serviceProvider.GetRequiredService<ISupplierAccountReadService>());
        Assert.IsType<WarrantyReadService>(serviceProvider.GetRequiredService<IWarrantyReadService>());
        Assert.IsType<ThakaReadService>(serviceProvider.GetRequiredService<IThakaReadService>());
        Assert.IsType<ProductManagementReadService>(serviceProvider.GetRequiredService<IProductManagementReadService>());
        Assert.IsType<InventoryProvenanceReadService>(serviceProvider.GetRequiredService<IInventoryProvenanceReadService>());
        Assert.IsType<PartyDirectoryReadService>(serviceProvider.GetRequiredService<IPartyDirectoryReadService>());
    }

    [Fact]
    public void Infrastructure_Registers_All_Canonical_Business_Handlers()
    {
        var services = CreateProductionServices();

        // Sales Handlers
        AssertHandlerRegistration<CompleteSaleHandler>(services);
        AssertHandlerRegistration<CompletePosDraftHandler>(services);
        AssertHandlerRegistration<CreateSaleReturnHandler>(services);
        AssertHandlerRegistration<CommercialExchangeHandler>(services);
        AssertHandlerRegistration<SavePosDraftHandler>(services);
        AssertHandlerRegistration<CancelPosDraftHandler>(services);

        // Purchasing Handlers
        AssertHandlerRegistration<CreatePurchaseHandler>(services);
        AssertHandlerRegistration<ReceiveProductIntakeHandler>(services);
        AssertHandlerRegistration<CreatePurchaseReturnHandler>(services);
        AssertHandlerRegistration<VoidPurchaseHandler>(services);

        // Inventory & Traceability Handlers
        AssertHandlerRegistration<TransferInventoryConditionHandler>(services);
        AssertHandlerRegistration<CreateStockAdjustmentHandler>(services);
        AssertHandlerRegistration<CreateStocktakeHandler>(services);
        AssertHandlerRegistration<StartStocktakeHandler>(services);
        AssertHandlerRegistration<RecordStocktakeCountHandler>(services);
        AssertHandlerRegistration<RecordSerializedStocktakeHandler>(services);
        AssertHandlerRegistration<ReviewStocktakeHandler>(services);
        AssertHandlerRegistration<PostStocktakeHandler>(services);
        AssertHandlerRegistration<CancelStocktakeHandler>(services);

        // Warranty Handlers
        AssertHandlerRegistration<CreateWarrantyClaimHandler>(services);
        AssertHandlerRegistration<BeginWarrantyClaimReviewHandler>(services);
        AssertHandlerRegistration<CancelWarrantyClaimHandler>(services);
        AssertHandlerRegistration<SendWarrantyClaimToSupplierHandler>(services);
        AssertHandlerRegistration<MarkWarrantySupplierProcessingHandler>(services);
        AssertHandlerRegistration<RecordWarrantyResolutionHandler>(services);
        AssertHandlerRegistration<ReceiveCustomerWarrantyReplacementHandler>(services);
        AssertHandlerRegistration<HandoverWarrantyItemHandler>(services);
        AssertHandlerRegistration<SendShopStockToSupplierWarrantyHandler>(services);
        AssertHandlerRegistration<ReceiveShopStockWarrantyHandler>(services);

        // Finance Handlers
        AssertHandlerRegistration<CreateSupplierPaymentHandler>(services);
        AssertHandlerRegistration<ReverseSupplierPaymentHandler>(services);
        AssertHandlerRegistration<CreateSupplierRefundHandler>(services);
        AssertHandlerRegistration<ReverseSupplierRefundHandler>(services);
        AssertHandlerRegistration<SupplierAccountAdjustmentHandler>(services);
        AssertHandlerRegistration<PostExpenseHandler>(services);
        AssertHandlerRegistration<VoidExpenseHandler>(services);
        AssertHandlerRegistration<OpenCashSessionHandler>(services);
        AssertHandlerRegistration<RecordManualCashMovementHandler>(services);
        AssertHandlerRegistration<CloseCashSessionHandler>(services);

        // Thaka Handlers
        AssertHandlerRegistration<CreateThakaProjectHandler>(services);
        AssertHandlerRegistration<IssueThakaMaterialHandler>(services);
        AssertHandlerRegistration<RecordThakaPaymentHandler>(services);
        AssertHandlerRegistration<SettleThakaHandler>(services);
        AssertHandlerRegistration<ReopenThakaHandler>(services);
        AssertHandlerRegistration<ReverseThakaMaterialHandler>(services);
        AssertHandlerRegistration<ReverseThakaPaymentHandler>(services);

        // Terminal & Security Handlers
        AssertHandlerRegistration<RegisterTerminalHandler>(services);
        AssertHandlerRegistration<TerminalHeartbeatHandler>(services);
        AssertHandlerRegistration<UpdateTerminalStatusHandler>(services);
        AssertHandlerRegistration<AuthoritativeRevalidationHandler>(services);
        AssertHandlerRegistration<OperationStatusQueryHandler>(services);
        AssertHandlerRegistration<FirstSetupBootstrapHandler>(services);
        AssertHandlerRegistration<GetSettingsConfigurationHandler>(services);
        AssertHandlerRegistration<UpdateShopProfileHandler>(services);
        AssertHandlerRegistration<UpdateReceiptTemplateHandler>(services);
        AssertHandlerRegistration<GetLoginAccountsHandler>(services);
        AssertHandlerRegistration<AuthenticateUserHandler>(services);
        AssertHandlerRegistration<EndUserSessionHandler>(services);

        // Printing Handlers
        AssertHandlerRegistration<PrintDocumentHandler>(services);
        AssertHandlerRegistration<SavePrinterProfileHandler>(services);
        AssertHandlerRegistration<PrintPhysicalStickersHandler>(services);
        AssertHandlerRegistration<OutboxProcessor>(services);
    }

    [Fact]
    public void Infrastructure_Never_Registers_Any_Demo_Authorities()
    {
        var services = CreateProductionServices();

        var forbiddenSubstrings = new[]
        {
            "DemoRetailState",
            "DemoTransactionService",
            "DemoPurchaseInventoryService",
            "DemoBusinessDirectoryService",
            "DemoIdentityService",
            "DemoReportingService",
            "DemoSettingsState",
            "DemoFrontendPermissionService",
            "DemoFirstRunSetupState",
            "FakeCatalogRepository",
            "FakeInventoryRepository",
            "FakeSalesRepository",
            "FakePurchasingRepository"
        };

        foreach (var descriptor in services)
        {
            var serviceName = descriptor.ServiceType.FullName ?? string.Empty;
            var implName = descriptor.ImplementationType?.FullName ?? string.Empty;
            var instanceName = descriptor.ImplementationInstance?.GetType().FullName ?? string.Empty;

            foreach (var forbidden in forbiddenSubstrings)
            {
                Assert.DoesNotContain(forbidden, serviceName);
                Assert.DoesNotContain(forbidden, implName);
                Assert.DoesNotContain(forbidden, instanceName);
            }
        }
    }

    private static void AssertRegistration<TService, TImplementation>(
        IServiceCollection services,
        ServiceLifetime expectedLifetime)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
        Assert.NotNull(descriptor);
        Assert.Equal(expectedLifetime, descriptor.Lifetime);

        if (descriptor.ImplementationType is not null)
        {
            Assert.Equal(typeof(TImplementation), descriptor.ImplementationType);
        }
        else if (descriptor.ImplementationInstance is not null)
        {
            Assert.IsType<TImplementation>(descriptor.ImplementationInstance);
        }
    }

    private static void AssertHandlerRegistration<THandler>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(THandler));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
