using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

internal static class Phase2PostgresTestHarness
{
    private static readonly object SequenceFixtureInitialization = new();
    public static ServiceProvider BuildProvider(IClock? clock = null, Action<IServiceCollection>? configure = null)
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        var root = AttestOwnedPostgresRoot(connectionString);
        var authorityRoot = Path.Combine(root, "harness-highwater");
        var manifest = Path.Combine(authorityRoot, "highwater.manifest");
        lock (SequenceFixtureInitialization)
        {
            // A fresh runner directory establishes an explicit new fixture. Once
            // created, loss of its artifacts is an error, never a reinitialization.
            if (!Directory.Exists(authorityRoot))
            {
                Directory.CreateDirectory(authorityRoot);
                MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, new OwnedSequenceAuthorityCustody(authorityRoot));
            }
        }
        var custody = new OwnedSequenceAuthorityCustody(authorityRoot);
        services.RemoveAll<ISequenceHighWaterService>();
        services.AddSingleton<ISequenceHighWaterService>(new MachineSequenceHighWaterService(manifest, custody));
        if (clock is not null)
        {
            services.RemoveAll<IClock>();
            services.AddSingleton(clock);
        }
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static string AttestOwnedPostgresRoot(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT")
            ?? throw new InvalidOperationException("An owned PostgreSQL runner root is required."));
        var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar + "EdgeRetailsMasterPg_";
        if (connection.Host != "127.0.0.1" || connection.Port < 55000 || connection.Port > 65535
            || !(connection.Database?.StartsWith("edge_retails_", StringComparison.Ordinal) ?? false)
            || !root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PostgreSQL fixture authority is outside the approved isolated runner.");
        }
        using var pg = new NpgsqlConnection(connectionString);
        pg.Open();
        using var query = new NpgsqlCommand("SHOW data_directory", pg);
        if (query.ExecuteScalar() is not string actual
            || !Path.GetFullPath(actual).Equals(Path.Combine(root, "data"), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PostgreSQL server does not match its owned runner root.");
        }
        return root;
    }

    public static async Task EnsureReceiptConfigurationAsync(EdgeRetailsDbContext db)
    {
        if (!await db.ShopProfiles.AnyAsync(x => x.ProfileKey == "PRIMARY"))
        {
            db.ShopProfiles.Add(new ShopProfile
            {
                ProfileKey = "PRIMARY",
                ShopName = "Edge Retails Integration Shop",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!await db.ReceiptTemplateSettings.AnyAsync(x => x.TemplateKey == "PRIMARY"))
        {
            db.ReceiptTemplateSettings.Add(new ReceiptTemplateSettings
            {
                TemplateKey = "PRIMARY",
                LogoBehavior = "NONE",
                TemplateVersion = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    public static async Task<QuantityProductFixture> SeedQuantityProductAsync(
        EdgeRetailsDbContext db,
        decimal defaultSalePrice = 150m)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "pc-" + suffix[..8],
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Product-" + suffix,
            Sku = ("SKU-" + suffix[..12]).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = defaultSalePrice,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        var supplier = new Supplier
        {
            Name = "Supplier-" + suffix,
            DealerCode = await AllocateFixtureDealerCodeAsync(db, "SU"),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        db.ChangeTracker.Clear();

        return new QuantityProductFixture(
            product.Id,
            productUnit.Id,
            supplier.Id,
            actorId,
            unit.Id);
    }

    public static async Task<SerializedProductFixture> SeedSerializedProductAsync(
        EdgeRetailsDbContext db,
        decimal defaultSalePrice = 5000m,
        int defaultWarrantyMonths = 12)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new Unit
        {
            Name = "Serialized Unit-" + suffix,
            Symbol = "su-" + suffix[..8],
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Serialized Product-" + suffix,
            Sku = ("SP-" + suffix[..12]).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = false,
            DefaultSalePrice = defaultSalePrice,
            DefaultWarrantyMonths = defaultWarrantyMonths,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };
        var supplier = new Supplier
        {
            Name = "Serialized Supplier-" + suffix,
            DealerCode = await AllocateFixtureDealerCodeAsync(db, "SS"),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);
        db.ChangeTracker.Clear();

        return new SerializedProductFixture(
            product.Id,
            productUnit.Id,
            supplier.Id,
            actorId,
            unit.Id);
    }

    public static async Task<Customer> SeedCustomerAsync(
        EdgeRetailsDbContext db,
        string name = "Test Customer")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customer = new Customer
        {
            Name = $"{name} {suffix}",
            Phone = "0300" + Random.Shared.Next(1000000, 9999999),
            Address = "Test City",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    public static async Task<Supplier> SeedSupplierAsync(
        EdgeRetailsDbContext db,
        string name = "Test Supplier",
        string prefix = "TS")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplier = new Supplier
        {
            Name = $"{name} {suffix}",
            DealerCode = await AllocateFixtureDealerCodeAsync(db, prefix),
            Phone = "0300" + Random.Shared.Next(1000000, 9999999),
            Address = "Market Area",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    private static async Task<string> AllocateFixtureDealerCodeAsync(EdgeRetailsDbContext db, string prefix)
    {
        var persisted = await db.Suppliers.AsNoTracking().Select(x => x.DealerCode).ToListAsync();
        var used = new HashSet<string>(persisted.OfType<string>(), StringComparer.Ordinal);
        used.UnionWith(db.ChangeTracker.Entries<Supplier>().Select(x => x.Entity.DealerCode).OfType<string>());
        for (var number = 1000; number < 999999; number++)
        {
            var candidate = TraceabilityCodeRules.BuildDealerCode(prefix, number);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("The isolated fixture dealer-code range is exhausted.");
    }

    public static async Task<CashSession> SeedOpenCashSessionAsync(
        EdgeRetailsDbContext db,
        Guid actorId,
        decimal openingCash = 10000m)
    {
        var session = new CashSession
        {
            BusinessDate = DateOnly.FromDateTime(DateTime.UtcNow),
            OpenedBy = actorId,
            OpenedAt = DateTimeOffset.UtcNow,
            OpeningCash = openingCash,
            Status = CashSessionStatus.Open,
            Version = 1
        };
        db.CashSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }
}

internal sealed record QuantityProductFixture(
    Guid ProductId,
    Guid ProductUnitId,
    Guid SupplierId,
    Guid ActorId,
    Guid UnitId);

internal sealed record SerializedProductFixture(
    Guid ProductId,
    Guid ProductUnitId,
    Guid SupplierId,
    Guid ActorId,
    Guid UnitId);
