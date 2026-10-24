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

    public static string ResolveConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
            var credFile = Path.Combine(clusterDir, "provpw.tmp");
            if (File.Exists(credFile))
            {
                var pw = File.ReadAllText(credFile).Trim();
                if (!string.IsNullOrWhiteSpace(pw))
                {
                    connectionString = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                }
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "FAIL-CLOSED: EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        return connectionString;
    }

    public static ServiceProvider BuildProvider(IClock? clock = null, Action<IServiceCollection>? configure = null)
    {
        var connectionString = ResolveConnectionString();

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        services.AddGuardedTestSequenceAuthority(connectionString);
        if (clock is not null)
        {
            services.RemoveAll<IClock>();
            services.AddSingleton(clock);
        }
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    internal static void VerifyOrCreateCustodyBinding(string authorityRoot, string root, string? clusterRunId, int port, string database)
    {
        var manifest = Path.Combine(authorityRoot, "highwater.manifest");
        var bindingFile = Path.Combine(authorityRoot, "cluster_binding.json");

        lock (SequenceFixtureInitialization)
        {
            if (!Directory.Exists(authorityRoot))
            {
                Directory.CreateDirectory(authorityRoot);
                MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, new OwnedSequenceAuthorityCustody(authorityRoot));

                // Bind custody directory to the specific cluster root, run ID, port, and database
                var bindingJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    ClusterRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    RunId = clusterRunId ?? "unknown",
                    Port = port,
                    Database = database,
                    BoundAtUtc = DateTimeOffset.UtcNow
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(bindingFile, bindingJson);
            }
            else
            {
                // Verify candidate cluster/run/custody binding contract
                if (!File.Exists(bindingFile))
                {
                    throw new InvalidOperationException(
                        $"FAIL-CLOSED: Custody directory '{authorityRoot}' exists but is missing required 'cluster_binding.json'. Silently binding unverified existing authority is forbidden.");
                }

                try
                {
                    using var bindingDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(bindingFile));
                    var boundRoot = bindingDoc.RootElement.TryGetProperty("ClusterRoot", out var br) ? br.GetString() : null;
                    var boundRunId = bindingDoc.RootElement.TryGetProperty("RunId", out var brid) ? brid.GetString() : null;
                    var boundPort = bindingDoc.RootElement.TryGetProperty("Port", out var bp) ? bp.GetInt32() : 0;
                    var boundDb = bindingDoc.RootElement.TryGetProperty("Database", out var bdb) ? bdb.GetString() : null;

                    var canonicalBoundRoot = !string.IsNullOrWhiteSpace(boundRoot)
                        ? Path.GetFullPath(boundRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : null;
                    var canonicalCurrentRoot = !string.IsNullOrWhiteSpace(root)
                        ? Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : null;

                    if (boundPort != port ||
                        !string.Equals(boundDb, database, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(canonicalBoundRoot, canonicalCurrentRoot, StringComparison.OrdinalIgnoreCase) ||
                        (clusterRunId is not null && boundRunId is not null && !string.Equals(boundRunId, clusterRunId, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            $"FAIL-CLOSED: Custody directory '{authorityRoot}' is bound to cluster root '{boundRoot}', run '{boundRunId}' (port {boundPort}, db '{boundDb}'), not current root '{root}', run '{clusterRunId}' (port {port}, db '{database}'). Cluster binding mismatch.");
                    }
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"FAIL-CLOSED: Corrupt cluster binding file at '{bindingFile}'.", ex);
                }

                // Missing artifact fail-closed check: all three artifacts must exist
                if (!File.Exists(manifest) || !File.Exists(manifest + ".checkpoint") || !File.Exists(manifest + ".lock"))
                {
                    throw new InvalidOperationException("FAIL-CLOSED: Sequence fixture artifacts are incomplete or corrupt in existing authority root. Deleting or recreating legitimate authority is forbidden.");
                }
            }
        }
    }

    internal static (string Root, string? ClusterRunId) AttestOwnedPostgresRoot(string connectionString, string? runnerRootOverride = null)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);

        // Guard 1: Host must be loopback strictly (127.0.0.1 or localhost)
        if (connection.Host != "127.0.0.1" && !string.Equals(connection.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Host '{connection.Host}' is prohibited. Only isolated loopback (127.0.0.1 or localhost) is permitted.");
        }

        // Guard 2: Port must be in isolated range (55000-65535) and strictly never port 5432
        if (connection.Port == 5432)
        {
            throw new InvalidOperationException("FAIL-CLOSED: Port 5432 is prohibited. Connection to production or default cluster is forbidden.");
        }
        if (connection.Port < 55000 || connection.Port > 65535)
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Port {connection.Port} is outside the approved isolated range (55000-65535).");
        }

        // Guard 3: Database must be test database, strictly never production or default
        if (string.IsNullOrWhiteSpace(connection.Database) ||
            connection.Database.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
            connection.Database.Equals("edgeretails", StringComparison.OrdinalIgnoreCase) ||
            connection.Database.Contains("prod", StringComparison.OrdinalIgnoreCase) ||
            (!connection.Database.StartsWith("edge_retails_", StringComparison.OrdinalIgnoreCase) &&
             !connection.Database.StartsWith("edgeretails_test_", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Database '{connection.Database}' is prohibited.");
        }

        // Guard 4: Role must be an approved disposable test role. Blanket postgres acceptance is strictly forbidden.
        if (string.IsNullOrWhiteSpace(connection.Username) ||
            (!connection.Username.Equals("er_provisioner", StringComparison.OrdinalIgnoreCase) &&
             !connection.Username.Equals("er_runtime", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: User '{connection.Username}' is prohibited. Only approved disposable test roles (er_provisioner, er_runtime) are permitted.");
        }

        var rawEnvRoot = runnerRootOverride ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_MASTER_PG_RUN_ROOT");
        string root;
        if (!string.IsNullOrWhiteSpace(rawEnvRoot))
        {
            root = Path.GetFullPath(rawEnvRoot);
        }
        else
        {
            var disposableDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
            if (Directory.Exists(disposableDir))
            {
                root = Path.GetFullPath(disposableDir);
            }
            else
            {
                throw new InvalidOperationException("FAIL-CLOSED: An owned PostgreSQL runner root is required.");
            }
        }

        bool isApprovedOwnedCluster = false;
        string? clusterRunId = null;

        var ownershipMarkerPath = Path.Combine(root, "OWNERSHIP_MARKER.json");
        if (File.Exists(ownershipMarkerPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ownershipMarkerPath));
                var rootElem = doc.RootElement;
                var marker = rootElem.TryGetProperty("marker", out var m) ? m.GetString() : null;
                var runId = rootElem.TryGetProperty("run_id", out var r) ? r.GetString() : null;
                var port = rootElem.TryGetProperty("port", out var p) ? p.GetInt32() : 0;
                var isolation = rootElem.TryGetProperty("isolation_mode", out var i) ? i.GetString() : null;

                if (string.Equals(marker, "ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(runId) &&
                    port == connection.Port &&
                    string.Equals(isolation, "owned_disposable_local_cluster", StringComparison.Ordinal))
                {
                    isApprovedOwnedCluster = true;
                    clusterRunId = runId;
                }
            }
            catch
            {
                isApprovedOwnedCluster = false;
            }
        }

        var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar + "EdgeRetailsMasterPg_";
        var isApprovedTempRunner = root.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) &&
                                  (Directory.Exists(Path.Combine(root, "data")) || Directory.Exists(Path.Combine(root, "pgdata")));

        if (!isApprovedTempRunner && !isApprovedOwnedCluster)
        {
            throw new InvalidOperationException("FAIL-CLOSED: PostgreSQL fixture authority is outside the approved isolated runner or lacks verified ownership marker.");
        }

        using var pg = new NpgsqlConnection(connectionString);
        pg.Open();

        using var cmdLiveUser = new NpgsqlCommand("SELECT current_user;", pg);
        var liveUser = cmdLiveUser.ExecuteScalar()?.ToString() ?? "";
        if (!liveUser.Equals("er_provisioner", StringComparison.OrdinalIgnoreCase) &&
            !liveUser.Equals("er_runtime", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Live PostgreSQL session user '{liveUser}' is not an approved disposable test role.");
        }

        using var query = new NpgsqlCommand("SHOW data_directory", pg);
        var actual = query.ExecuteScalar() as string
            ?? throw new InvalidOperationException("FAIL-CLOSED: Unable to query data_directory.");

        var actualNormalized = Path.GetFullPath(actual);
        var expectedData = Path.GetFullPath(Path.Combine(root, "data"));
        var expectedPgData = Path.GetFullPath(Path.Combine(root, "pgdata"));

        if (!actualNormalized.Equals(expectedData, StringComparison.OrdinalIgnoreCase) &&
            !actualNormalized.Equals(expectedPgData, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FAIL-CLOSED: PostgreSQL server data_directory '{actualNormalized}' does not match its owned runner root '{root}'.");
        }

        using var cmdVer = new NpgsqlCommand("SHOW server_version_num;", pg);
        var verStr = cmdVer.ExecuteScalar()?.ToString() ?? "0";
        if (!int.TryParse(verStr, out var verNum) || verNum < 180000 || verNum >= 190000)
        {
            throw new InvalidOperationException($"FAIL-CLOSED: Target PostgreSQL version {verStr} is not PostgreSQL 18.");
        }

        return (root, clusterRunId);
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
        await db.CashSessions
            .Where(x => x.Status == CashSessionStatus.Open)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, CashSessionStatus.Closed));

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
