using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Setup;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase11CatalogSeed")]
public sealed class CatalogReferenceUpgradeApiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpRequestMessage CreateRequest(
        Phase2ServerWebApplicationFactory factory,
        HttpMethod method,
        string path,
        Guid? sessionId = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString());
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        if (sessionId.HasValue)
        {
            request.Headers.Add("X-Session-Id", sessionId.Value.ToString());
        }
        return request;
    }

    [Fact]
    public async Task UpgradeFixedUnits_WithoutSession_Returns_401_Unauthorized()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        using var client = baseFactory.CreateClient();

        using var request = CreateRequest(baseFactory, HttpMethod.Post, "/api/catalog/upgrade/fixed-units", sessionId: null);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("auth.session_missing", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpgradeFixedUnits_WithoutInventoryManagePermission_Returns_403_Forbidden()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();

        var unprivilegedUserId = Guid.NewGuid();
        baseFactory.Identity.SeedUser(new User
        {
            Id = unprivilegedUserId,
            RoleId = baseFactory.ValidRoleId,
            DisplayName = "Cashier Without Inventory Manage",
            Status = UserStatus.Active
        });
        baseFactory.Identity.SetPermissions(unprivilegedUserId, new HashSet<string>
        {
            PermissionKeys.SalesCreate,
            PermissionKeys.SalesPosUse
        });

        var sessionId = Guid.NewGuid();
        baseFactory.Sessions.SeedSession(new UserSession
        {
            Id = sessionId,
            UserId = unprivilegedUserId,
            StartedAt = DateTimeOffset.UtcNow,
            IsRevoked = false
        });

        using var client = baseFactory.CreateClient();
        using var request = CreateRequest(baseFactory, HttpMethod.Post, "/api/catalog/upgrade/fixed-units", sessionId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal("authorization.denied", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpgradeFixedUnits_InitialExecution_Returns_200_OK_And_Replay_Returns_AlreadyApplied()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();

        var fakeCatalog = new InMemoryCatalogRepo();
        var fakeSetup = new InMemorySetupRepo();
        var audit = new TrackingAuditWriter(fakeSetup);

        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICatalogRepository>();
                services.AddSingleton<ICatalogRepository>(fakeCatalog);
                services.RemoveAll<ISetupRepository>();
                services.AddSingleton<ISetupRepository>(fakeSetup);
                services.RemoveAll<IBusinessAuditWriter>();
                services.AddSingleton<IBusinessAuditWriter>(audit);
            }));

        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();

        // 1. Initial Upgrade Execution (200 OK)
        using var initialRequest = CreateRequest(baseFactory, HttpMethod.Post, "/api/catalog/upgrade/fixed-units", sessionId);
        using var initialResponse = await client.SendAsync(initialRequest);

        Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
        var initialResult = await initialResponse.Content.ReadFromJsonAsync<CatalogReferenceUpgradeResult>(JsonOptions);
        Assert.NotNull(initialResult);
        Assert.Equal(10, initialResult.UnitsAdded);
        Assert.Equal(0, initialResult.UnitsPreserved);
        Assert.False(initialResult.AlreadyApplied);
        Assert.True(fakeSetup.UpgradeApplied);
        Assert.Equal(10, fakeCatalog.Units.Count);

        // 2. Replay Execution (200 OK, AlreadyApplied: true)
        using var replayRequest = CreateRequest(baseFactory, HttpMethod.Post, "/api/catalog/upgrade/fixed-units", sessionId);
        using var replayResponse = await client.SendAsync(replayRequest);

        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayResult = await replayResponse.Content.ReadFromJsonAsync<CatalogReferenceUpgradeResult>(JsonOptions);
        Assert.NotNull(replayResult);
        Assert.Equal(0, replayResult.UnitsAdded);
        Assert.Equal(0, replayResult.UnitsPreserved);
        Assert.True(replayResult.AlreadyApplied);
    }

    private sealed class TrackingAuditWriter : IBusinessAuditWriter
    {
        private readonly InMemorySetupRepo _setup;
        public List<(string Action, string EntityType, Guid? EntityId)> Events { get; } = [];

        public TrackingAuditWriter(InMemorySetupRepo setup)
        {
            _setup = setup;
        }

        public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null)
        {
            Events.Add((action, entityType, entityId));
            if (entityType == "CATALOG_REFERENCE_UPGRADE" && action == "FIXED_UNITS_SEEDED")
            {
                _setup.UpgradeApplied = true;
            }
        }
    }

    private sealed class InMemorySetupRepo : ISetupRepository
    {
        public InstallationState Installation { get; set; } = new()
        {
            InstallationId = Guid.NewGuid(),
            SetupStatus = SetupStatus.Complete,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Version = 1
        };

        public bool UpgradeApplied { get; set; }

        public Task<InstallationState?> GetInstallationStateForUpdateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<InstallationState?>(Installation);

        public Task<bool> HasCatalogReferenceUpgradeAsync(Guid installationId, CancellationToken cancellationToken) =>
            Task.FromResult(UpgradeApplied);

        public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ShopProfile?> GetShopProfileAsync(CancellationToken cancellationToken) => Task.FromResult<ShopProfile?>(null);
        public Task<ShopProfile?> GetShopProfileForUpdateAsync(CancellationToken cancellationToken) => Task.FromResult<ShopProfile?>(null);
        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsAsync(CancellationToken cancellationToken) => Task.FromResult<ReceiptTemplateSettings?>(null);
        public Task<ReceiptTemplateSettings?> GetReceiptTemplateSettingsForUpdateAsync(CancellationToken cancellationToken) => Task.FromResult<ReceiptTemplateSettings?>(null);
        public void AddInstallationState(InstallationState state) => Installation = state;
        public void AddShopProfile(ShopProfile profile) { }
        public void AddCustomer(Domain.Parties.Customer customer) { }
        public void AddReceiptTemplateSettings(ReceiptTemplateSettings settings) { }
        public void AddRole(Role role) { }
        public void AddPermission(Permission permission) { }
        public void AddRolePermission(RolePermission rolePermission) { }
        public void AddUser(User user) { }
    }

    private sealed class InMemoryCatalogRepo : ICatalogRepository
    {
        public List<Unit> Units { get; } = [];

        public Task<IReadOnlyList<Unit>> GetUnitsAsync(bool includeInactive, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Unit>>(Units.ToList().AsReadOnly());

        public void AddUnit(Unit unit) => Units.Add(unit);

        public Task<Product?> GetProductAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult<Product?>(null);
        public Task<Product?> GetProductForUpdateAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult<Product?>(null);
        public Task<Product?> GetProductBySkuAsync(string normalizedSku, CancellationToken cancellationToken) => Task.FromResult<Product?>(null);
        public Task<IReadOnlyList<Product>> GetProductsAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Product>>([]);
        public Task<Company?> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken) => Task.FromResult<Company?>(null);
        public Task<Company?> GetCompanyForUpdateAsync(Guid companyId, CancellationToken cancellationToken) => Task.FromResult<Company?>(null);
        public Task<Company?> GetCompanyByCodeAsync(string normalizedCode, CancellationToken cancellationToken) => Task.FromResult<Company?>(null);
        public Task<Company?> GetCompanyByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Company?>(null);
        public Task<IReadOnlyList<Company>> GetCompaniesAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Company>>([]);
        public Task<bool> IsCompanyInUseByActiveProductAsync(Guid companyId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken) => Task.FromResult<Category?>(null);
        public Task<Category?> GetCategoryForUpdateAsync(Guid categoryId, CancellationToken cancellationToken) => Task.FromResult<Category?>(null);
        public Task<Category?> GetCategoryByIdentitySymbolAsync(string normalizedSymbol, CancellationToken cancellationToken) => Task.FromResult<Category?>(null);
        public Task<Category?> GetCategoryByNameAsync(string name, CancellationToken cancellationToken) => Task.FromResult<Category?>(null);
        public Task<IReadOnlyList<Category>> GetCategoriesAsync(bool includeInactive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Category>>([]);
        public Task<bool> IsCategoryInUseByActiveProductAsync(Guid categoryId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<Unit?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken) => Task.FromResult<Unit?>(null);
        public Task<Unit?> GetUnitForUpdateAsync(Guid unitId, CancellationToken cancellationToken) => Task.FromResult<Unit?>(null);
        public Task<bool> IsUnitInUseByActiveCatalogAsync(Guid unitId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ProductUnit?> GetProductUnitAsync(Guid productUnitId, CancellationToken cancellationToken) => Task.FromResult<ProductUnit?>(null);
        public Task<ProductUnit?> GetProductUnitSnapshotAsync(Guid productUnitId, CancellationToken cancellationToken) => Task.FromResult<ProductUnit?>(null);
        public Task<ProductUnit?> GetProductUnitAsync(Guid productId, Guid unitId, CancellationToken cancellationToken) => Task.FromResult<ProductUnit?>(null);
        public Task<IReadOnlyList<ProductUnit>> GetProductUnitsAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProductUnit>>([]);
        public Task<ProductUnitBarcode?> GetBarcodeAsync(string barcode, CancellationToken cancellationToken) => Task.FromResult<ProductUnitBarcode?>(null);
        public Task<IReadOnlyList<Product>> GetActiveProductsAsync(StocktakeScope scope, Guid? categoryId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Product>>([]);
        public void AddProduct(Product product) { }
        public void AddProductUnit(ProductUnit productUnit) { }
        public void AddBarcode(ProductUnitBarcode barcode) { }
        public void AddCompany(Company company) { }
        public void AddCategory(Category category) { }
    }
}
