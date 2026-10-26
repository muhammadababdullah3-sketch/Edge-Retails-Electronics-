using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace EdgeRetails.IntegrationTests;

internal static class Phase2PostgresTestHarness
{
    public static string ResolveConnectionString()
    {
        return PostgresFixtureProfile.Resolve().ConnectionString;
    }

    private static readonly Npgsql.NameTranslation.NpgsqlNullNameTranslator PackStateTranslator = new();

    internal static DbContextOptions<EdgeRetailsDbContext> NewDbContextOptions(string connectionString) =>
        new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly(typeof(EdgeRetailsDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__ef_migrations_history", "system")
                .MapEnum<PackState>("pack_state", "inventory", PackStateTranslator))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

    /// <summary>
    /// The current EF model includes columns from later migrations. Historical rehearsal databases intentionally
    /// stop at an earlier migration but still seed through the present model; add only the missing columns.
    /// </summary>
    internal static async Task EnsureHistoricalEfSchemaShimsAsync(EdgeRetailsDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            DO $shim$ BEGIN
              IF to_regclass('catalog.units') IS NOT NULL THEN
                ALTER TABLE catalog.units ADD COLUMN IF NOT EXISTS dimension character varying(20) NOT NULL DEFAULT 'Count';
                ALTER TABLE catalog.units ADD COLUMN IF NOT EXISTS factor_to_reference numeric(18,9) NOT NULL DEFAULT 1.0;
                ALTER TABLE catalog.units ADD COLUMN IF NOT EXISTS conversion_kind character varying(20) NOT NULL DEFAULT 'FIXED';
              END IF;
              IF to_regclass('system.shop_profile') IS NOT NULL THEN
                ALTER TABLE system.shop_profile ADD COLUMN IF NOT EXISTS business_day_cutoff_local_time time without time zone NULL;
                ALTER TABLE system.shop_profile ADD COLUMN IF NOT EXISTS time_zone_id character varying(100) NOT NULL DEFAULT '';
              END IF;
              IF to_regclass('identity.user_sessions') IS NOT NULL THEN
                ALTER TABLE identity.user_sessions ADD COLUMN IF NOT EXISTS last_activity_at timestamp with time zone NULL;
                UPDATE identity.user_sessions SET last_activity_at = started_at WHERE last_activity_at IS NULL;
                BEGIN
                  ALTER TABLE identity.user_sessions ALTER COLUMN last_activity_at SET NOT NULL;
                EXCEPTION WHEN others THEN
                  NULL;
                END;
              END IF;
              IF to_regclass('finance.cash_sessions') IS NOT NULL THEN
                ALTER TABLE finance.cash_sessions ADD COLUMN IF NOT EXISTS void_reason character varying(500) NULL;
                ALTER TABLE finance.cash_sessions ADD COLUMN IF NOT EXISTS voided_at timestamp with time zone NULL;
                ALTER TABLE finance.cash_sessions ADD COLUMN IF NOT EXISTS voided_by uuid NULL;
              END IF;
              IF to_regclass('catalog.products') IS NOT NULL THEN
                ALTER TABLE catalog.products ADD COLUMN IF NOT EXISTS class_ref character varying(20) NULL;
              END IF;
              BEGIN
                CREATE TYPE inventory.pack_state AS ENUM ('Sealed', 'Open', 'Consumed');
              EXCEPTION
                WHEN duplicate_object THEN NULL;
              END;
              IF to_regclass('inventory.units') IS NOT NULL THEN
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS initial_base_quantity numeric(18,4) NULL;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS remaining_base_quantity numeric(18,4) NULL;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS is_loose_stock boolean NOT NULL DEFAULT false;
                ALTER TABLE inventory.units ADD COLUMN IF NOT EXISTS pack_state inventory.pack_state NULL;
              END IF;
              IF to_regclass('system.operation_outcomes') IS NOT NULL THEN
                ALTER TABLE system.operation_outcomes ADD COLUMN IF NOT EXISTS response_content_type character varying(100) NULL;
                ALTER TABLE system.operation_outcomes ADD COLUMN IF NOT EXISTS response_payload jsonb NULL;
                ALTER TABLE system.operation_outcomes ADD COLUMN IF NOT EXISTS response_status_code integer NULL;
              END IF;
            END $shim$;
            """);
        await db.Database.OpenConnectionAsync();
        if (db.Database.GetDbConnection() is NpgsqlConnection npgsql)
        {
            await npgsql.ReloadTypesAsync();
        }
    }

    internal static async Task ReloadNpgsqlEnumTypesAsync(EdgeRetailsDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        if (db.Database.GetDbConnection() is NpgsqlConnection npgsql)
        {
            await npgsql.ReloadTypesAsync();
        }
    }

    internal static async Task MigrateToHistoricalBaselineAsync(EdgeRetailsDbContext db, string migrationId)
    {
        await db.GetService<IMigrator>().MigrateAsync(migrationId);
        await EnsureHistoricalEfSchemaShimsAsync(db);
    }

    internal static async Task RunWithOwnedEmptyDatabaseAsync(string namePrefix, Func<EdgeRetailsDbContext, Task> test)
    {
        await using var attested = BuildProvider();
        var name = namePrefix + Guid.NewGuid().ToString("N");
        var profile = PostgresFixtureProfile.Resolve();
        await using var admin = profile.OpenMaintenance();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin))
        {
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var connectionString = profile.MaintenanceForDatabase(name);
            await using var db = new EdgeRetailsDbContext(NewDbContextOptions(connectionString));
            profile.GrantWriteAccess(name);
            await test(db);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    public static ServiceProvider BuildProvider(IClock? clock = null, Action<IServiceCollection>? configure = null)
        => BuildProvider(PostgresFixtureProfile.Resolve(), clock, configure);

    public static ServiceProvider BuildProvider(PostgresFixtureProfile profile, IClock? clock = null,
        Action<IServiceCollection>? configure = null)
    {
        profile.Attest();
        var connectionString = profile.ConnectionString;
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var (_, runId) = AttestOwnedPostgresRoot(connectionString, profile.OwnedRoot);
        VerifyOrCreateCustodyBinding(profile.CustodyRoot, profile.OwnedRoot, runId, connection.Port, profile.Database);
        services.RemoveAll<ISequenceHighWaterService>();
        services.AddSingleton<ISequenceHighWaterService>(new MachineSequenceHighWaterService(
            Path.Combine(profile.CustodyRoot, "highwater.manifest"), new OwnedSequenceAuthorityCustody(profile.CustodyRoot)));
        if (clock is not null)
        {
            services.RemoveAll<IClock>();
            services.RemoveAll<IShopClock>();
            services.AddSingleton(clock);
            if (clock is IShopClock shopClock)
            {
                services.AddSingleton<IShopClock>(shopClock);
            }
            else
            {
                services.AddSingleton<IShopClock>(new ShopClock(ShopClock.ResolveShopTimeZone(), () => clock.UtcNow));
            }
        }
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public sealed class ScenarioClock(DateTimeOffset utcNow) : ShopClock(ResolveShopTimeZone(), () => utcNow)
    {
    }

    public static async Task<DateTimeOffset> ResolveIsolatedScenarioDateAsync(int baseYear, int baseMonth, int baseDay)
    {
        var connStr = ResolveConnectionString();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        var shopZone = ShopClock.ResolveShopTimeZone();
        var candidate = new DateOnly(baseYear, baseMonth, baseDay);
        while (true)
        {
            var localDt = DateTime.SpecifyKind(candidate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localDt, shopZone), TimeSpan.Zero);
            var endUtc = startUtc.AddDays(1);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT 1 FROM sales.sales WHERE completed_at >= @start AND completed_at < @end
                UNION ALL
                SELECT 1 FROM inventory.movements WHERE occurred_at >= @start AND occurred_at < @end AND recognized_loss_amount > 0
                UNION ALL
                SELECT 1 FROM finance.expenses WHERE expense_date = @candidateDate
                LIMIT 1;";
            cmd.Parameters.AddWithValue("start", startUtc);
            cmd.Parameters.AddWithValue("end", endUtc);
            cmd.Parameters.AddWithValue("candidateDate", candidate);

            var exists = await cmd.ExecuteScalarAsync();
            if (exists is null)
            {
                return new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day, 10, 0, 0, TimeSpan.Zero);
            }

            candidate = candidate.AddDays(1);
        }
    }

    internal static void VerifyOrCreateCustodyBinding(string authorityRoot, string root, string? clusterRunId, int port, string database)
    {
        lock (SequenceFixtureCoordination.InitLock)
        {
            if (Directory.Exists(authorityRoot) && !File.Exists(Path.Combine(authorityRoot, "cluster_binding.json")) &&
                !Directory.EnumerateFileSystemEntries(authorityRoot).Any())
            {
                Directory.Delete(authorityRoot, recursive: false);
            }

            if (!Directory.Exists(authorityRoot))
            {
                InitializeCustodyBinding(authorityRoot, root, clusterRunId, port, database);
            }
            else
            {
                var bindingFile = Path.Combine(authorityRoot, "cluster_binding.json");
                var manifest = Path.Combine(authorityRoot, "highwater.manifest");
                if (!File.Exists(bindingFile))
                {
                    SequenceFixtureCoordination.WaitForFile(bindingFile);
                }

                if (!File.Exists(bindingFile))
                {
                    if (!Directory.EnumerateFileSystemEntries(authorityRoot).Any())
                    {
                        Directory.Delete(authorityRoot, recursive: false);
                        InitializeCustodyBinding(authorityRoot, root, clusterRunId, port, database);
                        return;
                    }

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

                if (!SequenceFixtureCoordination.SequenceArtifactsComplete(manifest))
                {
                    SequenceFixtureCoordination.WaitForSequenceArtifacts(manifest);
                }

                if (!SequenceFixtureCoordination.SequenceArtifactsComplete(manifest))
                {
                    throw new InvalidOperationException("FAIL-CLOSED: Sequence fixture artifacts are incomplete or corrupt in existing authority root. Deleting or recreating legitimate authority is forbidden.");
                }
            }
        }
    }

    private static void InitializeCustodyBinding(string authorityRoot, string root, string? clusterRunId, int port, string database)
    {
        var manifest = Path.Combine(authorityRoot, "highwater.manifest");
        var bindingFile = Path.Combine(authorityRoot, "cluster_binding.json");
        Directory.CreateDirectory(authorityRoot);
        MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, new OwnedSequenceAuthorityCustody(authorityRoot));
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

        var (_, clusterRunId) = OwnedPostgresRunnerAttestation.TryApproveRunnerRoot(root, connection.Port);
        OwnedPostgresRunnerAttestation.EnsureRunnerRootApproved(root, connection.Port);

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

    /// <summary>Stock, terminal, and cash session prerequisites for crash-host complete-sale scenarios.</summary>
    public static async Task<(QuantityProductFixture Product, Guid TerminalId)> SetupCrashSalePrerequisitesAsync(
        ServiceProvider provider,
        decimal initialStock = 20m)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await EnsureReceiptConfigurationAsync(db);
        var product = await SeedQuantityProductAsync(db, defaultSalePrice: 200m);

        var purchase = await scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(
            new CreatePurchaseCommand(
                product.SupplierId,
                "PURCHASE-CRASH-" + Guid.NewGuid().ToString("N")[..8],
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                product.ActorId,
                Guid.NewGuid(),
                [new(product.ProductId, product.ProductUnitId, initialStock, 100m, 200m, [])]),
            default);
        if (!purchase.IsSuccess)
        {
            throw new InvalidOperationException(purchase.Error?.Message ?? "Purchase seed failed for crash scenario.");
        }

        var terminal = new Terminal
        {
            Name = "POS-CRASH-" + Guid.NewGuid().ToString("N")[..8],
            TerminalCode = "CR-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            Status = TerminalStatus.Active,
            RegisteredAt = DateTimeOffset.UtcNow
        };
        db.Terminals.Add(terminal);
        await db.SaveChangesAsync();

        if (!await db.CashSessions.AnyAsync(x => x.Status == CashSessionStatus.Open))
        {
            var openRes = await scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>()
                .HandleAsync(new OpenCashSessionCommand(1000m, product.ActorId, "Crash test opening drawer"), default);
            if (!openRes.IsSuccess)
            {
                throw new InvalidOperationException(openRes.Error?.Message ?? "Cash session open failed for crash scenario.");
            }
        }

        return (product, terminal.Id);
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
