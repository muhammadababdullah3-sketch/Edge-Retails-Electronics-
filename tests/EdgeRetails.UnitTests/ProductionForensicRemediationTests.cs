using System.Security.Cryptography;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Production.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class ProductionForensicRemediationTests
{
    private const string DummyConnectionString =
        "Host=127.0.0.1;Port=5432;Database=edgeretails_prod;Username=postgres;Password=test_password";

    [Fact]
    public void ProductionInfrastructure_DiContainer_PassesStrictScopeAndBuildValidation()
    {
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(DummyConnectionString);

        // Build with strict scope and build validation enabled
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // 1. Singletons & Root Services
        Assert.NotNull(provider.GetRequiredService<ILicensePublicKeyProvider>());
        Assert.NotNull(provider.GetRequiredService<ILicenseSignatureVerifier>());

        // 2. Scoped Handlers, Licensing, Gateway and Startup Operations
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILicenseValidator>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILicenseStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RuntimeLicenseService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ProductionStartupCoordinator>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IApplicationGateway>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDatabaseReadinessService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IInstallationStateReadService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RegisterTerminalHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CompletePosDraftHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreateSaleReturnHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CommercialExchangeHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreatePurchaseReturnHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VoidPurchaseHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreateSupplierPaymentHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreateSupplierRefundHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CreateWarrantyClaimHandler>());
    }

    [Fact]
    public void ProductionLicensePublicKeyProvider_DefaultVendorMasterKey_LoadsValidRsaKey()
    {
        var provider = new ProductionLicensePublicKeyProvider();
        var pem = provider.GetPublicKeyPem();
        Assert.False(string.IsNullOrWhiteSpace(pem));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        Assert.Equal(2048, rsa.KeySize);
    }

    [Fact]
    public void ProductionLicensePublicKeyProvider_RejectsPrivateKeyMaterial()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportRSAPrivateKeyPem();

        var prev = Environment.GetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY", privateKeyPem);
            var provider = new ProductionLicensePublicKeyProvider();
            var ex = Assert.Throws<InvalidOperationException>(() => provider.GetPublicKeyPem());
            Assert.Contains("Private key material is forbidden", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY", prev);
        }
    }

    [Fact]
    public void ProductionLicensePublicKeyProvider_AcceptsValidRsaPublicKeyPemFromEnvironment()
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var prev = Environment.GetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY", publicKeyPem);
            var provider = new ProductionLicensePublicKeyProvider();
            var loadedPem = provider.GetPublicKeyPem();
            Assert.Equal(publicKeyPem, loadedPem);

            using var loadedRsa = RSA.Create();
            loadedRsa.ImportFromPem(loadedPem);
            Assert.Equal(2048, loadedRsa.KeySize);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_LICENSE_PUBLIC_KEY", prev);
        }
    }

    [Fact]
    public async Task OfflineGateway_MutationsFailClosed_WhenDisconnected()
    {
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(DummyConnectionString);
        using var provider = services.BuildServiceProvider();

        var gateway = new LocalApplicationGateway(provider, ConnectivityState.Disconnected);

        Assert.False(gateway.CanMutate);
        Assert.Equal(ConnectivityState.Disconnected, gateway.CurrentState);

        // Sales complete
        var saleResult = await gateway.CompleteSaleAsync(
            new CompleteSaleCommand(
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                null,
                0m,
                SalePaymentMethod.Cash,
                100m,
                null,
                null,
                Array.Empty<CompleteSaleLineInput>()));
        Assert.False(saleResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", saleResult.Error?.Code);

        // Draft complete
        var draftResult = await gateway.CompletePosDraftAsync(
            new CompletePosDraftCommand(
                Guid.NewGuid(),
                1,
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                0m,
                SalePaymentMethod.Cash,
                100m,
                null));
        Assert.False(draftResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", draftResult.Error?.Code);

        // Sales return
        var returnResult = await gateway.CreateSaleReturnAsync(
            new CreateSaleReturnCommand(
                Guid.NewGuid(),
                "DEFECTIVE",
                null,
                RefundMethod.Cash,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Array.Empty<SaleReturnLineInput>()));
        Assert.False(returnResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", returnResult.Error?.Code);

        // Purchase create
        var purchaseResult = await gateway.CreatePurchaseAsync(
            new CreatePurchaseCommand(
                Guid.NewGuid(),
                "INV-001",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                EdgeRetails.Domain.Purchasing.PurchaseSettlementMode.External,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Array.Empty<CreatePurchaseLineInput>()));
        Assert.False(purchaseResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", purchaseResult.Error?.Code);

        // Purchase return
        var purchaseReturnResult = await gateway.CreatePurchaseReturnAsync(
            new CreatePurchaseReturnCommand(
                Guid.NewGuid(),
                "RETURN",
                null,
                EdgeRetails.Domain.Purchasing.PurchaseReturnSettlementMode.External,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Array.Empty<PurchaseReturnLineInput>()));
        Assert.False(purchaseReturnResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", purchaseReturnResult.Error?.Code);

        // Void purchase
        var voidResult = await gateway.VoidPurchaseAsync(
            new VoidPurchaseCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Void reason"));
        Assert.False(voidResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", voidResult.Error?.Code);

        // Supplier payment
        var supplierPayResult = await gateway.CreateSupplierPaymentAsync(
            new CreateSupplierPaymentCommand(
                Guid.NewGuid(),
                100m,
                SupplierPaymentPurpose.Settlement,
                SupplierSettlementMethod.CashDrawer,
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                null));
        Assert.False(supplierPayResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", supplierPayResult.Error?.Code);

        // Supplier refund
        var supplierRefundResult = await gateway.CreateSupplierRefundAsync(
            new CreateSupplierRefundCommand(
                Guid.NewGuid(),
                100m,
                SupplierSettlementMethod.CashDrawer,
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                null,
                null,
                null));
        Assert.False(supplierRefundResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", supplierRefundResult.Error?.Code);

        // Warranty claim
        var warrantyResult = await gateway.CreateWarrantyClaimAsync(
            new CreateWarrantyClaimCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                Guid.NewGuid(),
                Array.Empty<WarrantyClaimItemInput>(),
                Guid.NewGuid()));
        Assert.False(warrantyResult.IsSuccess);
        Assert.Equal("network.offline_mutation_forbidden", warrantyResult.Error?.Code);
    }
}
