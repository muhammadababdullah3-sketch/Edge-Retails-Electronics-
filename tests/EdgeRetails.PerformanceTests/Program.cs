using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using EdgeRetails.Application.Production.Diagnostics;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args);
        if (!options.TryGetValue("connection", out var connection) ||
            string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("--connection is required.");
            return 2;
        }

        var output = options.GetValueOrDefault("output", "docs/Phase5_Performance_Evidence.json")!;
        var runType = options.ContainsKey("final") ? "final" : "baseline";
        var load = options.ContainsKey("load");
        var samples = ParseInt(options.GetValueOrDefault("samples"), 120, 30, 500);
        var warmup = ParseInt(options.GetValueOrDefault("warmup"), 15, 5, 100);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await using var dataSource = NpgsqlDataSource.Create(connection);
        await using var db = await dataSource.OpenConnectionAsync();

        if (load)
        {
            Console.WriteLine("PHASE5_DATASET_LOAD_START");
            await LoadDatasetAsync(db);
            await AnalyzeBenchmarkTablesAsync(db);
            Console.WriteLine("PHASE5_DATASET_LOAD_COMPLETE");
        }
        var counts = await ReadDatasetCountsAsync(db);
        PrintCounts(counts);

        if (!RequiredCountsMet(counts))
        {
            Console.Error.WriteLine("Required dataset counts are incomplete.");
            return 3;
        }

        var environment = await ReadEnvironmentAsync(db);
        var architectureSha = ComputeSha256(Path.Combine(
            Directory.GetCurrentDirectory(),
            "docs",
            "Edge_Retails_Final_Architecture_Report_v1.md"));
        var buildFingerprint = ComputeSha256(typeof(Program).Assembly.Location);
        var results = new List<BenchmarkResult>();
        results.Add(await BenchmarkAsync(db, "P0_ProductSearch", ProductSearchSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P0_ProductExactSku", ProductExactSkuSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P0_SalesHistory", SalesHistorySql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_InventoryMovementHistory", InventoryMovementSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_SupplierLedgerPage", SupplierLedgerSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_WarrantyQueueRead", WarrantyQueueSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P2_AuditHistoryPage", AuditHistorySql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P2_ReportDailySlice", ReportDailySliceSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P2_ReportAggregate", ReportAggregateSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_PurchaseHistory", PurchaseHistorySql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P0_CustomerLookup", CustomerLookupSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P0_SupplierLookup", SupplierLookupSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_ThakaProjectPage", ThakaProjectPageSql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_CashHistory", CashHistorySql, samples, warmup));
        results.Add(await BenchmarkAsync(db, "P1_OutboxHistory", OutboxHistorySql, samples, warmup));

        if (options.ContainsKey("probe-unbounded"))
        {
            results.Add(await BenchmarkAsync(db, "Audit_UnboundedInventoryMaterialization", InventoryMaterializationSql, 1, 0));
            results.Add(await BenchmarkAsync(db, "Audit_UnboundedInventoryUnitMaterialization", InventoryUnitMaterializationSql, 1, 0));
        }

        var indexEvidence = await CapturePurchaseHistoryIndexEvidenceAsync(db, samples, warmup);

        var plans = new List<QueryPlanEvidence>();
        foreach (var plan in PlanQueries)
        {
            plans.Add(await CapturePlanAsync(db, plan.Name, plan.Sql));
        }

        var process = Process.GetCurrentProcess();
        var memoryBefore = process.WorkingSet64;
        var gcBefore = ReadGcCounts();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memoryAfter = Process.GetCurrentProcess().WorkingSet64;
        var gcAfter = ReadGcCounts();

        Phase5DiagnosticsSnapshot? diagnosticsSnapshot = null;
        if (options.ContainsKey("diagnostics"))
        {
            var services = new ServiceCollection();
            services.AddEdgeRetailsInfrastructure(connection);
            services.AddScoped<RuntimeLicenseService>(_ =>
                new RuntimeLicenseService(new BenchmarkLicenseStore(), new BenchmarkLicenseValidator()));
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            await using var scope = provider.CreateAsyncScope();
            var diagnostics = scope.ServiceProvider.GetRequiredService<IPhase5DiagnosticsService>();
            diagnosticsSnapshot = await diagnostics.CaptureAsync();
            Console.WriteLine("PHASE5_DIAGNOSTICS_RESULT " +
                diagnosticsSnapshot.OverallClassification);
        }

        var growth = await ReadGrowthEvidenceAsync(db);
        var cancellation = await CaptureCancellationEvidenceAsync(connection);

        var evidence = new Phase5Evidence(
            runType,
            DateTimeOffset.UtcNow,
            architectureSha,
            buildFingerprint,
            environment,
            counts,
            results,
            plans,
            indexEvidence,
            new MemoryEvidence(memoryBefore, memoryAfter, gcBefore, gcAfter),
            growth,
            cancellation,
            diagnosticsSnapshot,
            "SEQUENTIAL IMPLEMENTATION MODE",
            new[]
            {
                "Console/Npgsql timings represent application-side command elapsed time against disposable PostgreSQL 18.",
                "Percentiles use measured samples after warm-up; no SLO threshold is invented.",
                "WPF evidence is intentionally not synthesized by the console harness."
            });

        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(evidence, JsonOptions));

        Console.WriteLine($"PHASE5_{runType.ToUpperInvariant()}_EVIDENCE_WRITTEN {Path.GetFullPath(output)}");
        return 0;
    }

    private static async Task LoadDatasetAsync(NpgsqlConnection db)
    {
        await ExecuteBatchAsync(db, DatasetSqlParts);
        await ExecuteBatchAsync(db, TransactionDatasetSqlParts);
        await ExecuteBatchAsync(db, FinanceDatasetSqlParts);
        await ExecuteBatchAsync(db, RelatedDatasetSqlParts);
        await ExecuteBatchAsync(db, WorkflowDatasetSqlParts);
        await ExecuteBatchAsync(db, DomainDatasetSqlParts);
    }
    private static async Task ExecuteBatchAsync(NpgsqlConnection db, IReadOnlyList<string> sqlParts)
    {
        await using var transaction = await db.BeginTransactionAsync();
        try
        {
            foreach (var sql in sqlParts)
            {
                await using var command = new NpgsqlCommand(sql, db, transaction)
                {
                    CommandTimeout = 1800
                };
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task AnalyzeBenchmarkTablesAsync(NpgsqlConnection db)
    {
        const string sql = """
ANALYZE catalog.products;
ANALYZE inventory.units;
ANALYZE inventory.movements;
ANALYZE purchasing.purchases;
ANALYZE purchasing.purchase_items;
ANALYZE sales.sales;
ANALYZE sales.sale_items;
ANALYZE finance.supplier_account_entries;
ANALYZE finance.cash_movements;
ANALYZE warranty.claims;
ANALYZE warranty.claim_events;
ANALYZE thaka.projects;
ANALYZE thaka.material_issues;
ANALYZE audit.business_events;
ANALYZE system.outbox_messages;
""";
        await using var command = new NpgsqlCommand(sql, db)
        {
            CommandTimeout = 180
        };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string,long>> ReadDatasetCountsAsync(NpgsqlConnection db)
    {
        const string sql = """
SELECT key,value FROM (
  SELECT 'catalog.products' AS key,count(*)::bigint AS value FROM catalog.products
  UNION ALL SELECT 'inventory.units',count(*)::bigint FROM inventory.units
  UNION ALL SELECT 'sales.sales',count(*)::bigint FROM sales.sales
  UNION ALL SELECT 'sales.sale_items',count(*)::bigint FROM sales.sale_items
  UNION ALL SELECT 'inventory.movements',count(*)::bigint FROM inventory.movements
  UNION ALL SELECT 'finance.supplier_account_entries',count(*)::bigint FROM finance.supplier_account_entries
  UNION ALL SELECT 'purchasing.purchases',count(*)::bigint FROM purchasing.purchases
  UNION ALL SELECT 'purchasing.purchase_items',count(*)::bigint FROM purchasing.purchase_items
  UNION ALL SELECT 'warranty.claims',count(*)::bigint FROM warranty.claims
  UNION ALL SELECT 'warranty.claim_items',count(*)::bigint FROM warranty.claim_items
  UNION ALL SELECT 'warranty.claim_events',count(*)::bigint FROM warranty.claim_events
  UNION ALL SELECT 'warranty.shop_stock_cases',count(*)::bigint FROM warranty.shop_stock_cases
  UNION ALL SELECT 'thaka.projects',count(*)::bigint FROM thaka.projects
  UNION ALL SELECT 'thaka.material_issues',count(*)::bigint FROM thaka.material_issues
  UNION ALL SELECT 'finance.cash_movements',count(*)::bigint FROM finance.cash_movements
  UNION ALL SELECT 'audit.business_events',count(*)::bigint FROM audit.business_events
  UNION ALL SELECT 'system.outbox_messages',count(*)::bigint FROM system.outbox_messages
  UNION ALL SELECT 'sales.pos_drafts',count(*)::bigint FROM sales.pos_drafts
  UNION ALL SELECT 'sales.quotations',count(*)::bigint FROM sales.quotations
  UNION ALL SELECT 'sales.quotation_items',count(*)::bigint FROM sales.quotation_items
) counted
ORDER BY key
""";
        await using var command = new NpgsqlCommand(sql, db);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetInt64(1);
        }
        return result;
    }

    private static bool RequiredCountsMet(Dictionary<string,long> counts)
    {
        return counts.GetValueOrDefault("catalog.products") >= 10_000 &&
               counts.GetValueOrDefault("inventory.units") >= 100_000 &&
               counts.GetValueOrDefault("sales.sales") >= 250_000 &&
               counts.GetValueOrDefault("sales.sale_items") >= 500_000 &&
               counts.GetValueOrDefault("inventory.movements") >= 500_000 &&
               counts.GetValueOrDefault("finance.supplier_account_entries") >= 250_000;
    }

    private static async Task<EnvironmentEvidence> ReadEnvironmentAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('server_version'), current_setting('server_version_num')",
            db);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new EnvironmentEvidence(
            reader.GetString(0),
            reader.GetString(1),
            Environment.OSVersion.ToString(),
            Environment.ProcessorCount,
            DateTimeOffset.UtcNow);
    }

    private static async Task<BenchmarkResult> BenchmarkAsync(
        NpgsqlConnection db,
        string name,
        string sql,
        int sampleCount,
        int warmupCount)
    {
        for (var i = 0; i < warmupCount; i++)
        {
            await ExecuteReadAsync(db, sql);
        }

        var values = new double[sampleCount];
        long totalRows = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var started = Stopwatch.GetTimestamp();
            totalRows += await ExecuteReadAsync(db, sql);
            values[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(values);
        return new BenchmarkResult(
            name,
            sampleCount,
            warmupCount,
            values[0],
            Percentile(values, 0.50),
            Percentile(values, 0.95),
            Percentile(values, 0.99),
            values[^1],
            totalRows / (double)sampleCount);
    }

    private static async Task<long> ExecuteReadAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db)
        {
            CommandTimeout = 180
        };
        await using var reader = await command.ExecuteReaderAsync();
        long rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
        }
        return rows;
    }
    private static async Task<IndexEvidence> CapturePurchaseHistoryIndexEvidenceAsync(
        NpgsqlConnection db,
        int sampleCount,
        int warmupCount)
    {
        const string indexName = "ix_purchases_purchase_date_id";
        const string createSql = "CREATE INDEX ix_purchases_purchase_date_id_phase5_measurement ON purchasing.purchases (purchase_date DESC, id DESC);";
        const string dropMeasurementSql = "DROP INDEX IF EXISTS purchasing.ix_purchases_purchase_date_id_phase5_measurement;";
        const string dropProductionSql = "DROP INDEX IF EXISTS purchasing.ix_purchases_purchase_date_id;";

        var hasProduction = await ExecuteScalarAsync(db,
            "SELECT EXISTS (SELECT 1 FROM pg_class i JOIN pg_namespace n ON n.oid=i.relnamespace WHERE n.nspname='purchasing' AND i.relname='ix_purchases_purchase_date_id');");
        if (hasProduction is not true)
        {
            throw new InvalidOperationException(
                $"Required production index {indexName} is not present for measurement.");
        }

        BenchmarkResult before;
        QueryPlanEvidence beforePlan;
        BenchmarkResult after;
        QueryPlanEvidence afterPlan;

        await using (var command = new NpgsqlCommand(dropProductionSql, db)
        {
            CommandTimeout = 180
        })
        {
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            before = await BenchmarkAsync(db, "P1_PurchaseHistory_IndexBefore", PurchaseHistorySql, sampleCount, warmupCount);
            beforePlan = await CapturePlanAsync(db, "purchase_history_before_index", PurchaseHistorySql);

            await using (var command = new NpgsqlCommand(createSql, db)
            {
                CommandTimeout = 600
            })
            {
                await command.ExecuteNonQueryAsync();
            }

            await using (var analyze = new NpgsqlCommand("ANALYZE purchasing.purchases;", db)
            {
                CommandTimeout = 60
            })
            {
                await analyze.ExecuteNonQueryAsync();
            }

            after = await BenchmarkAsync(db, "P1_PurchaseHistory_IndexAfter", PurchaseHistorySql, sampleCount, warmupCount);
            afterPlan = await CapturePlanAsync(db, "purchase_history_after_index", PurchaseHistorySql);
        }
        finally
        {
            await using var cleanupMeasurement = new NpgsqlCommand(dropMeasurementSql, db)
            {
                CommandTimeout = 180
            };
            await cleanupMeasurement.ExecuteNonQueryAsync();

            var restored = await ExecuteScalarAsync(db,
                "SELECT EXISTS (SELECT 1 FROM pg_class i JOIN pg_namespace n ON n.oid=i.relnamespace WHERE n.nspname='purchasing' AND i.relname='ix_purchases_purchase_date_id');");
            if (restored is not true)
            {
                await using var restore = new NpgsqlCommand(
                    "CREATE INDEX ix_purchases_purchase_date_id ON purchasing.purchases (purchase_date DESC, id DESC);",
                    db)
                {
                    CommandTimeout = 600
                };
                await restore.ExecuteNonQueryAsync();
                await using var analyze = new NpgsqlCommand("ANALYZE purchasing.purchases;", db)
                {
                    CommandTimeout = 60
                };
                await analyze.ExecuteNonQueryAsync();
            }
        }

        return new IndexEvidence(
            "purchasing.purchases ORDER BY purchase_date DESC, id DESC LIMIT 100",
            before,
            after,
            beforePlan,
            afterPlan,
            after.P95Ms < before.P95Ms &&
            after.P99Ms < before.P99Ms &&
            Array.Exists(afterPlan.NodeTypes, static x => x == "Index Scan"));
    }

    private static async Task<QueryPlanEvidence> CapturePlanAsync(
        NpgsqlConnection db,
        string name,
        string sql)
    {
        var explain = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql;
        await using var command = new NpgsqlCommand(explain, db)
        {
            CommandTimeout = 300
        };

        var raw = (string?)await command.ExecuteScalarAsync() ?? "[]";
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement[0];
        var plan = root.GetProperty("Plan");

        return new QueryPlanEvidence(
            name,
            root.TryGetProperty("Planning Time", out var planning) ? planning.GetDouble() : 0,
            root.TryGetProperty("Execution Time", out var execution) ? execution.GetDouble() : 0,
            plan.TryGetProperty("Actual Rows", out var rows) && rows.ValueKind == JsonValueKind.Number ? rows.GetDouble() : 0,
            plan.TryGetProperty("Actual Loops", out var loops) && loops.ValueKind == JsonValueKind.Number ? loops.GetDouble() : 0,
            FindPlanMetric(plan, "Actual Rows", scanOnly: true),
            FindPlanMetric(plan, "Shared Read Blocks"),
            FindPlanMetric(plan, "Shared Hit Blocks"),
            FindNodeTypes(plan),
            raw);
    }

    private static long FindPlanMetric(
        JsonElement plan,
        string property,
        bool scanOnly = false)
    {
        long total = 0;
        if (plan.ValueKind != JsonValueKind.Object)
            return total;

        var isScanNode = plan.TryGetProperty("Node Type", out var nodeType) &&
                         nodeType.ValueKind == JsonValueKind.String &&
                         (nodeType.GetString()!.Contains("Scan", StringComparison.OrdinalIgnoreCase));
        if ((!scanOnly || isScanNode) &&
            plan.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.Number)
            total += Convert.ToInt64(Math.Round(value.GetDouble()));

        if (plan.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                total += FindPlanMetric(child, property, scanOnly);
        }

        return total;
    }

    private static string[] FindNodeTypes(JsonElement plan)
    {
        var nodes = new List<string>();
        Visit(plan, nodes);
        return nodes.Distinct().OrderBy(x => x).ToArray();
    }

    private static void Visit(JsonElement node, List<string> nodes)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty("Node Type", out var nodeType) &&
            nodeType.ValueKind == JsonValueKind.String)
        {
            nodes.Add(nodeType.GetString()!);
        }

        if (node.TryGetProperty("Plans", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                Visit(child, nodes);
        }
    }
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var weight = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
    }

    private static async Task<IReadOnlyList<GrowthEvidence>> ReadGrowthEvidenceAsync(
        NpgsqlConnection db)
    {
        const string sql = """
SELECT 'audit.business_events' AS name, count(*)::bigint AS rows, pg_total_relation_size('audit.business_events'::regclass) AS bytes FROM audit.business_events
UNION ALL SELECT 'inventory.movements', count(*)::bigint, pg_total_relation_size('inventory.movements'::regclass) FROM inventory.movements
UNION ALL SELECT 'finance.cash_movements', count(*)::bigint, pg_total_relation_size('finance.cash_movements'::regclass) FROM finance.cash_movements
UNION ALL SELECT 'finance.supplier_account_entries', count(*)::bigint, pg_total_relation_size('finance.supplier_account_entries'::regclass) FROM finance.supplier_account_entries
UNION ALL SELECT 'system.outbox_messages', count(*)::bigint, pg_total_relation_size('system.outbox_messages'::regclass) FROM system.outbox_messages
UNION ALL SELECT 'sales.pos_drafts', count(*)::bigint, pg_total_relation_size('sales.pos_drafts'::regclass) FROM sales.pos_drafts
UNION ALL SELECT 'warranty.claims', count(*)::bigint, pg_total_relation_size('warranty.claims'::regclass) FROM warranty.claims
UNION ALL SELECT 'warranty.claim_events', count(*)::bigint, pg_total_relation_size('warranty.claim_events'::regclass) FROM warranty.claim_events
UNION ALL SELECT 'thaka.projects', count(*)::bigint, pg_total_relation_size('thaka.projects'::regclass) FROM thaka.projects
UNION ALL SELECT 'thaka.material_issues', count(*)::bigint, pg_total_relation_size('thaka.material_issues'::regclass) FROM thaka.material_issues
ORDER BY name;
""";
        await using var command = new NpgsqlCommand(sql, db)
        {
            CommandTimeout = 60
        };
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<GrowthEvidence>();
        while (await reader.ReadAsync())
        {
            rows.Add(new GrowthEvidence(
                reader.GetString(0),
                reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
        }
        return rows;
    }

    private static async Task<object?> ExecuteScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<CancellationEvidence> CaptureCancellationEvidenceAsync(
        string connectionString)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var blocker = await source.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "LOCK TABLE sales.sales IN ACCESS EXCLUSIVE MODE;",
            blocker,
            blockerTransaction)
        {
            CommandTimeout = 30
        })
        {
            await lockCommand.ExecuteNonQueryAsync();
        }

        var reportConnectionString = $"{connectionString};Application Name=Phase5ReportCancellation";
        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(reportConnectionString);
        services.AddScoped<RuntimeLicenseService>(_ =>
            new RuntimeLicenseService(new BenchmarkLicenseStore(), new BenchmarkLicenseValidator()));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        await using var scope = provider.CreateAsyncScope();
        var reporting = scope.ServiceProvider.GetRequiredService<IReportingReadService>();
        using var cts = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var reportTask = reporting.GetSnapshotAsync(
            ReportingPeriodKind.Yearly,
            DateOnly.FromDateTime(DateTime.UtcNow),
            1,
            DateTime.UtcNow.Year,
            cts.Token);

        var observed = await WaitForReportActivityAsync(
            source,
            connectionString,
            TimeSpan.FromSeconds(10));
        if (!observed)
        {
            cts.Cancel();
            try { await reportTask; } catch (OperationCanceledException) { }
            elapsed.Stop();
            await blockerTransaction.RollbackAsync();
            return new CancellationEvidence(
                false,
                false,
                false,
                false,
                false,
                elapsed.Elapsed.TotalMilliseconds);
        }

        cts.Cancel();
        var cancellationObserved = false;
        try
        {
            await reportTask;
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }

        elapsed.Stop();
        var released = await WaitForNoReportActivityAsync(
            source,
            connectionString,
            TimeSpan.FromSeconds(10));
        await blockerTransaction.RollbackAsync();
        var pooledConnectionUsable = false;
        try
        {
            await using var pooledProbe = await source.OpenConnectionAsync();
            pooledConnectionUsable = Convert.ToInt32(
                await ExecuteScalarAsync(pooledProbe, "SELECT 1;")) == 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cancellation probe error: {ex.Message}");
        }

        return new CancellationEvidence(
            cancellationObserved,
            pooledConnectionUsable,
            released,
            cancellationObserved,
            released,
            elapsed.Elapsed.TotalMilliseconds);
    }

    private static async Task<bool> WaitForReportActivityAsync(
        NpgsqlDataSource source,
        string connectionString,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        await using var probe = await source.OpenConnectionAsync();
        while (DateTimeOffset.UtcNow < deadline)
        {
            const string sql = "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND application_name='Phase5ReportCancellation' AND state='active');";
            if (Convert.ToBoolean(await ExecuteScalarAsync(probe, sql)))
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    private static async Task<bool> WaitForNoReportActivityAsync(
        NpgsqlDataSource source,
        string connectionString,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        await using var probe = await source.OpenConnectionAsync();
        while (DateTimeOffset.UtcNow < deadline)
        {
            const string sql = "SELECT NOT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND application_name='Phase5ReportCancellation' AND state='active');";
            if (Convert.ToBoolean(await ExecuteScalarAsync(probe, sql)))
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    private static Dictionary<string,string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            var separator = arg.IndexOf('=');
            if (separator > 2)
                result[arg[2..separator]] = arg[(separator + 1)..];
            else
                result[arg[2..]] = "true";
        }
        return result;
    }

    private static int ParseInt(
        string? value,
        int fallback,
        int min,
        int max)
    {
        return int.TryParse(value, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static GcCounts ReadGcCounts()
    {
        return new GcCounts(
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    private static void PrintCounts(Dictionary<string,long> counts)
    {
        Console.WriteLine("DATASET_COUNTS");
        foreach (var item in counts.OrderBy(x => x.Key))
            Console.WriteLine($"{item.Key}={item.Value}");
    }
    private static readonly string[] DatasetSqlParts =
    [
        "INSERT INTO catalog.units(id,name,symbol,display_decimal_places,is_active) VALUES (md5('p5:unit:piece')::uuid,'Piece','pcs',0,true)",
        "INSERT INTO catalog.categories(id,name,is_active) SELECT md5('p5:category:'||g)::uuid,'Category '||lpad(g::text,2,'0'),true FROM generate_series(1,20) g",
        @"INSERT INTO parties.suppliers(id,name,phone,city,address,is_active,created_at,version,notes,dealer_code)
          SELECT md5('p5:supplier:'||g)::uuid,'Supplier '||lpad(g::text,4,'0'),'0300'||lpad(g::text,7,'0'),'City '||((g-1)%20+1),
          'Address '||g,true,now()-(g%1000)*interval '1 day',1,NULL,'SUP-'||lpad(g::text,5,'0')
          FROM generate_series(1,1000) g",
        @"INSERT INTO parties.customers(id,name,phone,address,is_walk_in,is_active,created_at,version,notes)
          SELECT md5('p5:customer:'||g)::uuid,'Customer '||lpad(g::text,5,'0'),'0312'||lpad(g::text,7,'0'),
          'Customer Address '||g,false,true,now()-(g%1500)*interval '1 day',1,NULL
          FROM generate_series(1,5000) g",
        @"INSERT INTO catalog.products(
            id,name,sku,base_unit_id,category_id,tracking_mode,serial_tracking_enabled,imei_tracking_enabled,
            reference_purchase_cost,default_sale_price,is_active,version,default_warranty_months,
            attributes_json,attributes_schema_version,brand,minimum_stock_level,model)
          SELECT md5('p5:product:'||g)::uuid,'Product '||lpad(g::text,5,'0'),'SKU-'||lpad(g::text,6,'0'),
            md5('p5:unit:piece')::uuid,md5('p5:category:'||(((g-1)%20)+1))::uuid,
            CASE WHEN g<=1000 THEN 3 ELSE 1 END,g<=1000,g<=1000,50+(g%100),100+(g%500),
            true,1,12,NULL,1,'Brand '||((g-1)%50+1),5,'Model-'||((g-1)%200+1)
          FROM generate_series(1,10000) g",
        @"INSERT INTO catalog.product_units(
            id,product_id,unit_id,factor_to_base_unit,can_purchase,can_sell,can_use_in_thaka,
            is_default_purchase_unit,is_default_sale_unit,is_active)
          SELECT md5('p5:product-unit:'||g)::uuid,md5('p5:product:'||g)::uuid,
            md5('p5:unit:piece')::uuid,1,true,true,true,true,true,true
          FROM generate_series(1,10000) g",
        @"INSERT INTO catalog.product_unit_barcodes(id,product_unit_id,barcode,is_active)
          SELECT md5('p5:barcode-row:'||g)::uuid,md5('p5:product-unit:'||g)::uuid,
            '890P5'||lpad(g::text,10,'0'),true
          FROM generate_series(1,10000) g",
        @"INSERT INTO catalog.supplier_products(
            id,supplier_id,product_id,next_item_sequence,is_active,created_at,updated_at,version)
          SELECT md5('p5:supplier-product:'||g)::uuid,
            md5('p5:supplier:'||(((g-1)%1000)+1))::uuid,md5('p5:product:'||g)::uuid,
            101,true,now(),now(),1
          FROM generate_series(1,10000) g",
        @"INSERT INTO inventory.stock_balances(
            id,product_id,sellable_qty,damaged_qty,defective_qty,with_supplier_qty,scrap_qty,version)
          SELECT md5('p5:stock-balance:'||g)::uuid,md5('p5:product:'||g)::uuid,
            500+(g%25),g%3,g%2,g%4,g%2,1
          FROM generate_series(1,10000) g",
        @"INSERT INTO inventory.cost_states(
            id,product_id,costed_qty,total_inventory_cost,moving_average_cost,last_purchase_cost,last_purchase_at,version)
          SELECT md5('p5:cost-state:'||g)::uuid,md5('p5:product:'||g)::uuid,
            500+(g%25),(500+(g%25))*(50+(g%100)),50+(g%100),50+(g%100),
            now()-(g%365)*interval '1 day',1
          FROM generate_series(1,10000) g"
    ,
        @"INSERT INTO purchasing.purchases(
            id,purchase_number,supplier_id,supplier_invoice_number,normalized_supplier_invoice_number,purchase_date,
            note,subtotal,other_charges,grand_total,status,settlement_mode,created_by,created_at,client_operation_id,version)
          SELECT md5('p5:purchase:'||g)::uuid,'P5-PUR-'||lpad(g::text,7,'0'),
            md5('p5:supplier:'||(((g-1)%1000)+1))::uuid,'INV-'||lpad(g::text,7,'0'),
            'INV-'||lpad(g::text,7,'0'),current_date-((g-1)%1095),NULL,200,0,200,1,1,
            md5('p5:actor')::uuid,now()-((g-1)%1095)*interval '1 day',
            md5('p5:purchase-op:'||g)::uuid,1
          FROM generate_series(1,125000) g",
        @"INSERT INTO purchasing.purchase_items(
            id,purchase_id,product_id,product_unit_id,product_name_snapshot,sku_snapshot,
            entered_quantity,factor_to_base_snapshot,base_quantity,entered_unit_cost,
            base_line_total,allocated_other_cost,effective_base_unit_cost,effective_line_cost,sale_price_at_purchase)
          SELECT md5('p5:purchase-item:'||g)::uuid,
            md5('p5:purchase:'||(((g-1)/2)+1))::uuid,
            md5('p5:product:'||(((g-1)%10000)+1))::uuid,
            md5('p5:product-unit:'||(((g-1)%10000)+1))::uuid,
            'Product '||lpad((((g-1)%10000)+1)::text,5,'0'),
            'SKU-'||lpad((((g-1)%10000)+1)::text,6,'0'),
            2,1,2,100,200,0,100,200,150
          FROM generate_series(1,250000) g",
        @"INSERT INTO inventory.movements(
            id,product_id,movement_type,reference_type,reference_id,unit_cost_snapshot,
            recognized_loss_amount,actor_id,occurred_at,correlation_id,reason,note)
          SELECT md5('p5:movement:'||g)::uuid,
            md5('p5:product:'||(((g-1)%10000)+1))::uuid,3,'BENCHMARK_SALE_ITEM',NULL,100,0,
            md5('p5:actor')::uuid,
            now()-((g-1)%3650)*interval '1 day'-((g%86400))*interval '1 second',
            md5('p5:correlation:'||g)::uuid,NULL,'deterministic benchmark movement'
          FROM generate_series(1,500000) g",
        @"INSERT INTO inventory.movement_effects(
            id,movement_id,stock_bucket,quantity_delta,quantity_before,quantity_after)
          SELECT md5('p5:movement-effect:'||g)::uuid,md5('p5:movement:'||g)::uuid,1,-1,10000,9999
          FROM generate_series(1,500000) g",
        @"INSERT INTO inventory.lots(
            id,product_id,source_movement_id,purchase_item_id,received_quantity,original_unit_cost,effective_unit_cost,created_at)
          SELECT md5('p5:lot:'||g)::uuid,
            md5('p5:product:'||(((g-1)%1000)+1))::uuid,
            md5('p5:movement:'||g)::uuid,md5('p5:purchase-item:'||g)::uuid,
            1,100,100,now()-((g-1)%1095)*interval '1 day'
          FROM generate_series(1,100000) g",
        @"INSERT INTO inventory.units(
            id,product_id,serial_number,imei1,imei2,status,acquisition_cost,inventory_lot_id,
            source_purchase_item_id,source_warranty_case_id,created_at,version,item_sequence,origin_type,
            product_sku_snapshot,supplier_code_snapshot,supplier_product_id,tracking_code,
            source_stock_adjustment_item_id,source_warranty_claim_item_id)
          SELECT md5('p5:inventory-unit:'||g)::uuid,
            md5('p5:product:'||(((g-1)%1000)+1))::uuid,
            'SN-P5-'||lpad(g::text,8,'0'),'IMEI1-P5-'||lpad(g::text,10,'0'),
            'IMEI2-P5-'||lpad(g::text,10,'0'),
            CASE WHEN g<=50000 THEN 1 ELSE 2 END,100,md5('p5:lot:'||g)::uuid,
            md5('p5:purchase-item:'||g)::uuid,NULL,
            now()-((g-1)%1095)*interval '1 day',1,((g-1)/100)+1,1,
            'SKU-'||lpad((((g-1)%1000)+1)::text,6,'0'),
            'SUP-'||lpad((((g-1)%1000)+1)::text,5,'0'),
            md5('p5:supplier-product:'||(((g-1)%1000)+1))::uuid,
            'TRK-P5-'||lpad(g::text,8,'0'),NULL,NULL
          FROM generate_series(1,100000) g"
    ];

    private static readonly string[] TransactionDatasetSqlParts =
    [
        @"INSERT INTO sales.sales(
            id,invoice_number,customer_id,cashier_user_id,session_id,completed_at,subtotal,
            invoice_discount,grand_total,status,payment_status,client_operation_id,receipt_template_snapshot,created_at)
          SELECT md5('p5:sale:'||g)::uuid,'P5-SALE-'||lpad(g::text,8,'0'),
            CASE WHEN g%20=0 THEN NULL ELSE md5('p5:customer:'||(((g-1)%5000)+1))::uuid END,
            md5('p5:actor')::uuid,NULL,
            now()-((g-1)%3650)*interval '1 day'-((g%86400))*interval '1 second',
            120+(2*(g%20)),0,120+(2*(g%20)),1,1,md5('p5:sale-op:'||g)::uuid,NULL,
            now()-((g-1)%3650)*interval '1 day'-((g%86400))*interval '1 second'
          FROM generate_series(1,250000) g",
        @"INSERT INTO sales.sale_items(
            id,sale_id,inventory_movement_id,product_id,product_unit_id,product_name_snapshot,sku_snapshot,
            entered_quantity,factor_to_base_snapshot,base_quantity,unit_price,gross_line_total,
            allocated_invoice_discount,net_line_total,unit_cost_snapshot,total_cost_snapshot,gross_profit_snapshot,warranty_valid_until)
          SELECT md5('p5:sale-item:'||g)::uuid,
            md5('p5:sale:'||(((g-1)/2)+1))::uuid,
            md5('p5:movement:'||g)::uuid,
            md5('p5:product:'||(((g-1)%10000)+1))::uuid,
            md5('p5:product-unit:'||(((g-1)%10000)+1))::uuid,
            'Product '||lpad((((g-1)%10000)+1)::text,5,'0'),
            'SKU-'||lpad((((g-1)%10000)+1)::text,6,'0'),
            1,1,1,60+((((g-1)/2)+1)%20),60+((((g-1)/2)+1)%20),0,
            60+((((g-1)/2)+1)%20),50,50,10,current_date+365
          FROM generate_series(1,500000) g",
        @"INSERT INTO sales.sale_payments(
            id,sale_id,method,amount_tendered,applied_amount,change_given,reference)
          SELECT md5('p5:sale-payment:'||g)::uuid,
            md5('p5:sale:'||g)::uuid,1,120+(2*(g%20)),120+(2*(g%20)),0,NULL
          FROM generate_series(1,250000) g",
        @"INSERT INTO finance.supplier_payments(
            id,payment_number,supplier_id,amount,purpose,method,cash_session_id,external_reference,paid_at,
            actor_id,client_operation_id,note,status)
          SELECT md5('p5:supplier-payment:'||g)::uuid,'P5-PAY-'||lpad(g::text,7,'0'),
            md5('p5:supplier:'||(((g-1)%1000)+1))::uuid,100,1,2,NULL,'P5-EXT-'||g,
            now()-((g-1)%1095)*interval '1 day',md5('p5:actor')::uuid,
            md5('p5:supplier-payment-op:'||g)::uuid,NULL,1
          FROM generate_series(1,125000) g",
        @"INSERT INTO finance.supplier_account_entries(
            id,entry_number,supplier_id,entry_type,direction,amount,reference_type,reference_id,
            occurred_at,actor_id,client_operation_id,note,created_at)
          SELECT md5('p5:supplier-entry:'||g)::uuid,'P5-LEDGER-'||lpad(g::text,8,'0'),
            md5('p5:supplier:'||(((g-1)%1000)+1))::uuid,
            CASE WHEN g<=125000 THEN 2 ELSE 3 END,CASE WHEN g<=125000 THEN 1 ELSE 2 END,
            200+(g%50),
            CASE WHEN g<=125000 THEN 'PURCHASE' ELSE 'SUPPLIER_PAYMENT' END,
            CASE WHEN g<=125000 THEN md5('p5:purchase:'||g)::uuid
                 ELSE md5('p5:supplier-payment:'||(g-125000))::uuid END,
            now()-((g-1)%1095)*interval '1 day',md5('p5:actor')::uuid,NULL,NULL,
            now()-((g-1)%1095)*interval '1 day'
          FROM generate_series(1,250000) g"
    ];

    private static readonly string[] FinanceDatasetSqlParts =
    [
        @"INSERT INTO finance.cash_sessions(
            id,business_date,opened_by,opened_at,opening_cash,status,expected_closing_cash,
            counted_closing_cash,difference,closed_by,closed_at,note,version)
          SELECT md5('p5:cash-session:'||g)::uuid,current_date-((g-1)%365),
            md5('p5:actor')::uuid,now()-((g-1)%365)*interval '1 day',10000,2,
            10000,10000,0,md5('p5:actor')::uuid,
            now()-((g-1)%365)*interval '1 day'+interval '8 hours',NULL,1
          FROM generate_series(1,100) g",
        @"INSERT INTO finance.cash_movements(
            id,cash_session_id,movement_type,direction,amount,source_type,source_id,reason,note,actor_id,occurred_at)
          SELECT md5('p5:cash-movement:'||g)::uuid,
            md5('p5:cash-session:'||(((g-1)%100)+1))::uuid,
            CASE WHEN g%2=0 THEN 1 ELSE 4 END,
            CASE WHEN g%2=0 THEN 1 ELSE 2 END,25+(g%500),'BENCHMARK',NULL,
            'benchmark cash movement',NULL,md5('p5:actor')::uuid,
            now()-((g-1)%3650)*interval '1 day'
          FROM generate_series(1,20000) g",
        @"INSERT INTO finance.expense_categories(id,name,is_system,is_active,version)
          SELECT md5('p5:expense-category:'||g)::uuid,'Benchmark Expense Category '||g,false,true,1
          FROM generate_series(1,5) g",
        @"INSERT INTO finance.expenses(
            id,expense_number,category_id,subcategory_id,expense_date,amount,payment_method,cash_session_id,
            reference,description,status,client_operation_id,created_by,created_at,voided_by,voided_at,void_reason,version)
          SELECT md5('p5:expense:'||g)::uuid,'P5-EXP-'||lpad(g::text,7,'0'),
            md5('p5:expense-category:'||(((g-1)%5)+1))::uuid,NULL,
            current_date-((g-1)%1095),50+(g%200),1,
            md5('p5:cash-session:'||(((g-1)%100)+1))::uuid,
            'P5-REF-'||g,'Benchmark expense',1,md5('p5:expense-op:'||g)::uuid,
            md5('p5:actor')::uuid,now()-((g-1)%1095)*interval '1 day',
            NULL,NULL,NULL,1
          FROM generate_series(1,20000) g"
    ];
    private static readonly string[] RelatedDatasetSqlParts =
    [
        @"INSERT INTO warranty.claims(
            id,claim_number,customer_id,original_sale_id,supplier_id,status,current_custody,received_at,
            resolved_at,closed_at,created_by,created_at,version,client_operation_id)
          SELECT md5('p5:warranty-claim:'||g)::uuid,'P5-WC-'||lpad(g::text,6,'0'),
            md5('p5:customer:'||(((g-1)%5000)+1))::uuid,
            md5('p5:sale:'||(((g-1)%250000)+1))::uuid,
            md5('p5:supplier:'||(((g-1)%1000)+1))::uuid,
            ((g-1)%6)+1,CASE WHEN g%3=0 THEN 3 ELSE 2 END,
            now()-((g-1)%730)*interval '1 day',
            CASE WHEN g%6=0 THEN now()-((g-1)%365)*interval '1 day' END,
            CASE WHEN g%6=0 THEN now()-((g-1)%365)*interval '1 day' END,
            md5('p5:actor')::uuid,now()-((g-1)%730)*interval '1 day',1,
            md5('p5:warranty-op:'||g)::uuid
          FROM generate_series(1,5000) g",
        @"INSERT INTO warranty.claim_items(
            id,claim_id,original_sale_item_id,product_id,quantity,fault_description,warranty_valid_until,
            resolution_type,resolution_note,replacement_product_id,replacement_reference)
          SELECT md5('p5:warranty-item:'||g)::uuid,
            md5('p5:warranty-claim:'||g)::uuid,
            md5('p5:sale-item:'||(((g-1)%500000)+1))::uuid,
            md5('p5:product:'||(((g-1)%1000)+1))::uuid,1,'Benchmark fault',
            current_date+180,NULL,NULL,NULL,NULL
          FROM generate_series(1,5000) g",
        @"INSERT INTO warranty.claim_item_units(
            id,claim_item_id,original_inventory_unit_id,active_original_inventory_unit_id,
            original_identity_snapshot,replacement_inventory_unit_id,replacement_identity_snapshot)
          SELECT md5('p5:warranty-item-unit:'||g)::uuid,
            md5('p5:warranty-item:'||g)::uuid,
            md5('p5:inventory-unit:'||(50000+g))::uuid,
            CASE WHEN g%5<>0 THEN md5('p5:inventory-unit:'||(50000+g))::uuid END,
            'TRK-P5-'||lpad((50000+g)::text,8,'0'),NULL,NULL
          FROM generate_series(1,5000) g",
        @"INSERT INTO warranty.claim_events(
            id,claim_id,status,custody,event_type,note,actor_id,occurred_at)
          SELECT md5('p5:warranty-event:'||g)::uuid,
            md5('p5:warranty-claim:'||(((g-1)/3)+1))::uuid,
            (((g-1)%6)+1),CASE WHEN g%3=0 THEN 3 ELSE 2 END,
            'Benchmark event','Deterministic benchmark event',md5('p5:actor')::uuid,
            now()-((g-1)%730)*interval '1 day'
          FROM generate_series(1,15000) g",
        @"INSERT INTO warranty.shop_stock_cases(
            id,case_number,product_id,base_quantity,supplier_id,source_purchase_item_id,fault_description,
            supplier_reference,status,resolution_type,created_at,sent_at,received_at,closed_at,
            inventory_carrying_cost_resolved,recovery_difference,resolution_client_operation_id,created_by,version)
          SELECT md5('p5:shop-case:'||g)::uuid,'P5-SW-'||lpad(g::text,6,'0'),
            md5('p5:product:'||(1000+((g-1)%1000)+1))::uuid,1,
            md5('p5:supplier:'||(((g-1)%1000)+1))::uuid,md5('p5:purchase-item:'||g)::uuid,
            'Benchmark shop-stock warranty fault','SUPREF-'||g,((g-1)%5)+1,
            CASE WHEN g%5>=2 THEN ((g-1)%7)+1 ELSE NULL END,
            now()-((g-1)%730)*interval '1 day',
            CASE WHEN g%2=0 THEN now()-((g-1)%300)*interval '1 day' END,
            CASE WHEN g%5>=2 THEN now()-((g-1)%200)*interval '1 day' END,
            CASE WHEN g%5>=3 THEN now()-((g-1)%150)*interval '1 day' END,
            CASE WHEN g%5>=3 THEN 100 END,CASE WHEN g%5>=3 THEN 5 END,
            CASE WHEN g%5>=3 THEN md5('p5:shop-resolution:'||g)::uuid END,
            md5('p5:actor')::uuid,1
          FROM generate_series(1,5000) g"
    ];
    private static readonly string[] WorkflowDatasetSqlParts =
    [
        @"INSERT INTO sales.pos_drafts(
            id,draft_number,customer_id,created_by,terminal_id,status,note,created_at,updated_at,expires_at,version)
          SELECT md5('p5:draft:'||g)::uuid,'P5-DRAFT-'||lpad(g::text,6,'0'),
            CASE WHEN g%10=0 THEN NULL ELSE md5('p5:customer:'||(((g-1)%5000)+1))::uuid END,
            md5('p5:actor')::uuid,'P5-T'||((g-1)%8+1),1,NULL,
            now()-((g-1)%90)*interval '1 day',
            now()-((g-1)%7)*interval '1 day',
            now()+interval '7 day',1
          FROM generate_series(1,5000) g",
        @"INSERT INTO sales.pos_draft_items(
            id,draft_id,product_id,product_unit_id,entered_quantity,factor_to_base_snapshot,
            base_quantity,displayed_unit_price_snapshot,selected_inventory_unit_id,note)
          SELECT md5('p5:draft-item:'||g)::uuid,
            md5('p5:draft:'||(((g-1)/2)+1))::uuid,
            md5('p5:product:'||(((g-1)%10000)+1))::uuid,
            md5('p5:product-unit:'||(((g-1)%10000)+1))::uuid,
            1,1,1,100,NULL,NULL
          FROM generate_series(1,10000) g",
        @"INSERT INTO sales.quotations(
            id,quotation_number,customer_id,customer_name_snapshot,quotation_date,valid_until,status,
            subtotal,discount,grand_total,notes,created_by,created_at,converted_sale_id,version)
          SELECT md5('p5:quotation:'||g)::uuid,'P5-QUO-'||lpad(g::text,6,'0'),
            CASE WHEN g%10=0 THEN NULL ELSE md5('p5:customer:'||(((g-1)%5000)+1))::uuid END,
            'Customer '||(((g-1)%5000)+1),current_date-((g-1)%365),current_date+30,1,
            200,0,200,NULL,md5('p5:actor')::uuid,
            now()-((g-1)%365)*interval '1 day',NULL,1
          FROM generate_series(1,5000) g",
        @"INSERT INTO sales.quotation_items(
            id,quotation_id,product_id,product_name,sku,selected_unit_id,entered_quantity,
            factor_to_base_snapshot,base_quantity,quoted_unit_price,line_total)
          SELECT md5('p5:quotation-item:'||g)::uuid,
            md5('p5:quotation:'||(((g-1)/2)+1))::uuid,
            md5('p5:product:'||(((g-1)%10000)+1))::uuid,
            'Product '||lpad((((g-1)%10000)+1)::text,5,'0'),
            'SKU-'||lpad((((g-1)%10000)+1)::text,6,'0'),
            md5('p5:unit:piece')::uuid,
            1,1,1,100,100
          FROM generate_series(1,10000) g",
        @"INSERT INTO sales.quotation_operations(
            id,quotation_id,client_operation_id,operation_type,actor_id,occurred_at)
          SELECT md5('p5:quotation-op:'||g)::uuid,
            md5('p5:quotation:'||g)::uuid,
            md5('p5:quotation-client-op:'||g)::uuid,1,md5('p5:actor')::uuid,
            now()-((g-1)%365)*interval '1 day'
          FROM generate_series(1,5000) g"
    ];

    private static readonly string[] DomainDatasetSqlParts =
    [
        @"INSERT INTO thaka.projects(
            id,project_number,customer_id,project_name,site_address,note,started_on,status,created_by,created_at,version)
          SELECT md5('p5:thaka-project:'||g)::uuid,'P5-TH-'||lpad(g::text,6,'0'),
            md5('p5:customer:'||(((g-1)%5000)+1))::uuid,'Project '||g,'Site '||g,NULL,
            current_date-((g-1)%1000),CASE WHEN g%10=0 THEN 2 ELSE 1 END,
            md5('p5:actor')::uuid,now()-((g-1)%1000)*interval '1 day',1
          FROM generate_series(1,2000) g",
        @"INSERT INTO thaka.material_issues(
            id,project_id,challan_number,client_operation_id,total_charge,total_cost,gross_profit,note,issued_by,issued_at)
          SELECT md5('p5:material-issue:'||g)::uuid,
            md5('p5:thaka-project:'||(((g-1)%2000)+1))::uuid,
            'P5-MI-'||lpad(g::text,7,'0'),md5('p5:material-op:'||g)::uuid,
            200,150,50,NULL,md5('p5:actor')::uuid,
            now()-((g-1)%1000)*interval '1 day'
          FROM generate_series(1,5000) g",
        @"INSERT INTO thaka.material_issue_items(
            id,material_issue_id,inventory_movement_id,product_id,product_unit_id,product_name_snapshot,sku_snapshot,
            entered_quantity,factor_to_base_snapshot,base_quantity,unit_charge,line_charge,
            unit_cost_snapshot,total_cost_snapshot,gross_profit_snapshot)
          SELECT md5('p5:material-item:'||g)::uuid,
            md5('p5:material-issue:'||(((g-1)/2)+1))::uuid,
            md5('p5:movement:'||g)::uuid,
            md5('p5:product:'||(((g-1)%10000)+1))::uuid,
            md5('p5:product-unit:'||(((g-1)%10000)+1))::uuid,
            'Product '||lpad((((g-1)%10000)+1)::text,5,'0'),
            'SKU-'||lpad((((g-1)%10000)+1)::text,6,'0'),
            1,1,1,100,100,75,75,25
          FROM generate_series(1,10000) g",
        @"INSERT INTO audit.business_events(
            id,actor_id,action,entity_type,entity_id,correlation_id,occurred_at,summary)
          SELECT md5('p5:audit:'||g)::uuid,md5('p5:actor')::uuid,'BENCHMARK_READ','Sale',
            md5('p5:sale:'||(((g-1)%250000)+1))::uuid,
            md5('p5:audit-correlation:'||g)::uuid,
            now()-((g-1)%3650)*interval '1 day','Deterministic benchmark audit event'
          FROM generate_series(1,100000) g",
        @"INSERT INTO system.outbox_messages(
            id,effect_type,source_type,source_id,payload_json,idempotency_key,created_at,
            attempt_count,next_attempt_at,status,last_error,completed_at)
          SELECT md5('p5:outbox:'||g)::uuid,
            CASE WHEN g%3=0 THEN 'PrintReceipt' ELSE 'BenchmarkEffect' END,
            'Sale',md5('p5:sale:'||(((g-1)%250000)+1))::uuid::text,
            '{""benchmark"":true}','P5-OUTBOX-'||lpad(g::text,8,'0'),
            now()-((g-1)%30)*interval '1 day',
            CASE WHEN g%10=0 THEN 2 ELSE 0 END,
            CASE WHEN g%4=0 THEN now() ELSE now()+interval '1 hour' END,
            CASE WHEN g%10=0 THEN 4 ELSE 1 END,
            CASE WHEN g%10=0 THEN 'benchmark failure' END,
            CASE WHEN g%10=0 THEN now() ELSE NULL END
          FROM generate_series(1,10000) g"
    ];
    private const string ProductSearchSql = """
SELECT id,name,sku,default_sale_price
FROM catalog.products
WHERE is_active AND (name ILIKE '%'||'0099'||'%' OR sku ILIKE '%'||'0099'||'%')
ORDER BY name,id
LIMIT 50
""";

    private const string ProductExactSkuSql = """
SELECT id,name,sku,default_sale_price
FROM catalog.products
WHERE is_active AND sku='SKU-000099'
LIMIT 1
""";

    private const string SalesHistorySql = """
SELECT id,invoice_number,completed_at,customer_id,grand_total
FROM sales.sales
ORDER BY completed_at DESC,id DESC
LIMIT 50
""";

    private const string InventoryMovementSql = """
SELECT mv.id,mv.product_id,mv.movement_type,mv.reference_type,me.quantity_delta
FROM inventory.movements mv
LEFT JOIN inventory.movement_effects me ON me.movement_id=mv.id
ORDER BY mv.occurred_at DESC,mv.id DESC
LIMIT 500
""";

    private const string SupplierLedgerSql = """
SELECT id,entry_number,entry_type,direction,amount,occurred_at
FROM finance.supplier_account_entries
WHERE supplier_id=md5('p5:supplier:1')::uuid
ORDER BY occurred_at DESC,id DESC
LIMIT 100
""";

    private const string WarrantyQueueSql = """
SELECT c.id,c.claim_number,c.customer_id,c.supplier_id,c.status,c.current_custody,c.received_at
FROM warranty.claims c
WHERE c.status NOT IN (6,7)
ORDER BY c.received_at DESC,c.id DESC
LIMIT 100
""";

    private const string AuditHistorySql = """
SELECT id,action,entity_type,entity_id,occurred_at,summary
FROM audit.business_events
ORDER BY occurred_at DESC,id DESC
LIMIT 200
""";

    private const string ReportDailySliceSql = """
SELECT count(*)::bigint,coalesce(sum(grand_total),0)::numeric
FROM sales.sales
WHERE completed_at >= now()-interval '1 day'
""";

    private const string ReportAggregateSql = """
SELECT date_trunc('day',completed_at AT TIME ZONE 'UTC') AS bucket,
       count(*)::bigint,coalesce(sum(grand_total),0)::numeric
FROM sales.sales
WHERE completed_at >= now()-interval '31 days'
GROUP BY 1 ORDER BY 1
""";

    private const string PurchaseHistorySql = """
SELECT id,purchase_number,supplier_id,purchase_date,grand_total,status
FROM purchasing.purchases
ORDER BY purchase_date DESC,id DESC
LIMIT 100
""";

    private const string CustomerLookupSql = """
SELECT id,name,phone,address
FROM parties.customers
WHERE is_active AND NOT is_walk_in
  AND (name ILIKE '%'||'0123'||'%' OR phone ILIKE '%'||'0123'||'%')
ORDER BY name,id
LIMIT 100
""";

    private const string SupplierLookupSql = """
SELECT id,name,phone,city
FROM parties.suppliers
WHERE is_active
  AND (name ILIKE '%'||'0123'||'%' OR phone ILIKE '%'||'0123'||'%' OR city ILIKE '%'||'0123'||'%')
ORDER BY name,id
LIMIT 100
""";

    private const string ThakaProjectPageSql = """
SELECT p.id,p.project_number,p.customer_id,p.project_name,p.started_on,p.status
FROM thaka.projects p
JOIN parties.customers c ON c.id=p.customer_id
WHERE (p.project_name ILIKE '%'||'012'||'%' OR p.project_number ILIKE '%'||'012'||'%' OR c.name ILIKE '%'||'012'||'%')
ORDER BY p.started_on DESC,p.id DESC
LIMIT 200
""";

    private const string CashHistorySql = """
SELECT id,cash_session_id,direction,amount,reason,occurred_at
FROM finance.cash_movements
ORDER BY occurred_at DESC,id DESC
LIMIT 100
""";

    private const string OutboxHistorySql = """
SELECT id,effect_type,source_type,status,attempt_count,created_at,next_attempt_at
FROM system.outbox_messages
ORDER BY created_at DESC,id DESC
LIMIT 100
""";

    private const string InventoryMaterializationSql = """
SELECT p.id,p.name,p.sku,p.default_sale_price,s.sellable_qty,s.damaged_qty,
       s.defective_qty,s.with_supplier_qty,s.scrap_qty
FROM catalog.products p
LEFT JOIN inventory.stock_balances s ON s.product_id=p.id
WHERE p.is_active
ORDER BY p.name,p.id
""";

    private const string InventoryUnitMaterializationSql = """
SELECT id,product_id,serial_number,imei1,imei2,status,acquisition_cost,created_at,tracking_code
FROM inventory.units
ORDER BY created_at,id
""";

    private static readonly (string Name,string Sql)[] PlanQueries =
    [
        ("product_search", ProductSearchSql),
        ("product_exact_sku", ProductExactSkuSql),
        ("sales_history", SalesHistorySql),
        ("inventory_movement_page", InventoryMovementSql),
        ("supplier_ledger_page", SupplierLedgerSql),
        ("warranty_queue", WarrantyQueueSql),
        ("audit_history", AuditHistorySql),
        ("report_aggregate", ReportAggregateSql),
        ("purchase_history", PurchaseHistorySql),
        ("customer_lookup", CustomerLookupSql),
        ("supplier_lookup", SupplierLookupSql),
        ("thaka_project_page", ThakaProjectPageSql),
        ("cash_history", CashHistorySql),
        ("outbox_history", OutboxHistorySql)
    ];

    private sealed record EnvironmentEvidence(
        string PostgreSqlVersion,
        string PostgreSqlVersionNumber,
        string OperatingSystem,
        int ProcessorCount,
        DateTimeOffset CapturedAtUtc);

    private sealed record IndexEvidence(
        string Query,
        BenchmarkResult Before,
        BenchmarkResult After,
        QueryPlanEvidence BeforePlan,
        QueryPlanEvidence AfterPlan,
        bool MeasuredBenefit);

    private sealed record BenchmarkResult(
        string Name,
        int SampleCount,
        int WarmupCount,
        double MinMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs,
        double AverageRowsReturned);

    private sealed record QueryPlanEvidence(
        string Name,
        double PlanningMs,
        double ExecutionMs,
        double ActualRows,
        double ActualLoops,
        long RowsScanned,
        long SharedReadBlocks,
        long SharedHitBlocks,
        string[] NodeTypes,
        string PlanJson);

    private sealed record GcCounts(
        int Gen0,
        int Gen1,
        int Gen2);

    private sealed record MemoryEvidence(
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        GcCounts GcBefore,
        GcCounts GcAfter);

    private sealed record GrowthEvidence(
        string Table,
        long RowCount,
        long TotalRelationBytes);

    private sealed record CancellationEvidence(
        bool CancellationObserved,
        bool ConnectionUsableAfterCancel,
        bool DatabaseQueryReleased,
        bool ReportCancellationObserved,
        bool ReportDatabaseQueryReleased,
        double ElapsedMs);

    private sealed record Phase5Evidence(
        string RunType,
        DateTimeOffset TimestampUtc,
        string ArchitectureSha,
        string BuildFingerprint,
        EnvironmentEvidence Environment,
        Dictionary<string,long> DatasetCounts,
        List<BenchmarkResult> Benchmarks,
        List<QueryPlanEvidence> QueryPlans,
        IndexEvidence IndexEvidence,
        MemoryEvidence Memory,
        IReadOnlyList<GrowthEvidence> Growth,
        CancellationEvidence Cancellation,
        Phase5DiagnosticsSnapshot? Diagnostics,
        string ExecutionMode,
        string[] KnownLimitations);

    private sealed class BenchmarkLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BenchmarkLicenseValidator : ILicenseValidator
    {
        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseValidationResult(
                LicenseValidationStatus.Missing,
                null,
                "No benchmark license is installed.",
                "license.missing"));
    }
}
