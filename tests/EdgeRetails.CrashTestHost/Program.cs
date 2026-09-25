using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Licensing;
using EdgeRetails.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.CrashTestHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var scenario = GetArg(args, "--scenario") ?? "unknown";
        if (string.Equals(scenario, "generate-license", StringComparison.OrdinalIgnoreCase))
        {
            var dest = GetArg(args, "--output") ?? "license.erlic";
            var deviceProvider = new WindowsMachineIdentityProvider();
            var deviceId = await deviceProvider.GetDeviceIdAsync();
            Console.WriteLine($"Machine DeviceId: {deviceId}");

            using var rsa = RSA.Create(2048);
            var pubKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var keysDir = Path.Combine(commonAppData, "EdgeRetails", "keys");
            Directory.CreateDirectory(keysDir);
            var pubKeyFile = Path.Combine(keysDir, "license.pub");
            await File.WriteAllTextAsync(pubKeyFile, pubKeyPem);
            Console.WriteLine($"Public key written to: {pubKeyFile}");

            var now = DateTimeOffset.UtcNow;
            var expiry = now.AddYears(10);
            var payload = new LicensePayload(
                LicenseId: "ER-PROD-2026-001",
                CustomerName: "Edge Retails Customer",
                StoreName: "Edge Retails Electronics Hub",
                Plan: "Enterprise",
                IssueDate: now,
                ExpiryDate: expiry,
                DeviceId: deviceId,
                MaxTerminals: 10,
                EnabledModules: new[] { "Electronics", "pos", "purchasing", "inventory", "thaka", "warranty", "reports" });

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var envelope = new SignedLicenseEnvelope(
                "RS256",
                Convert.ToBase64String(payloadBytes),
                Convert.ToBase64String(signature));

            var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

            var targets = new List<string>
            {
                dest,
                Path.Combine(commonAppData, "EdgeRetails", "license.erlic")
            };

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (!string.IsNullOrEmpty(desktop))
            {
                targets.Add(Path.Combine(desktop, "license.erlic"));
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                targets.Add(Path.Combine(userProfile, "OneDrive", "Desktop", "license.erlic"));
            }

            foreach (var target in targets.Distinct())
            {
                try
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await File.WriteAllTextAsync(target, envelopeJson);
                    Console.WriteLine($"License written to: {target}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to write {target}: {ex.Message}");
                }
            }

            Console.WriteLine("SUCCESS:LICENSE_GENERATED");
            return 0;
        }

        var dbConn = GetArg(args, "--db")
            ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
            ?? throw new InvalidOperationException("Missing --db argument or EDGE_RETAILS_TEST_DB environment variable.");
        var opIdString = GetArg(args, "--op-id") ?? Guid.NewGuid().ToString();
        var opId = Guid.Parse(opIdString);
        var stateDir = GetArg(args, "--state-dir") ?? Path.Combine(Path.GetTempPath(), "EdgeRetailsCrashHost", Guid.NewGuid().ToString("N"));

        switch (scenario)
        {
            case "crash-before-commit":
            {
                var services = new ServiceCollection();
                services.AddEdgeRetailsInfrastructure(dbConn);
                services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

                // Ensure base catalog data exists or query existing
                var product = await db.Products.FirstOrDefaultAsync();
                var user = await db.Users.FirstOrDefaultAsync();
                var unit = await db.ProductUnits.FirstOrDefaultAsync();

                if (product is null || user is null || unit is null)
                {
                    Console.WriteLine("ERROR:Prerequisites missing in DB");
                    return 2;
                }

                // Begin explicit transaction
                await using var tx = await db.Database.BeginTransactionAsync();

                var saleId = Guid.NewGuid();
                var movementId = Guid.NewGuid();

                var movement = new InventoryMovement
                {
                    Id = movementId,
                    ProductId = product.Id,
                    MovementType = InventoryMovementType.SaleOut,
                    ReferenceType = "Sale",
                    ReferenceId = saleId,
                    ActorId = user.Id,
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = Guid.NewGuid()
                };
                db.InventoryMovements.Add(movement);

                var sale = new Sale
                {
                    Id = saleId,
                    InvoiceNumber = $"INV-CRASH-{opId:N}"[..18],
                    CashierUserId = user.Id,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Subtotal = 100m,
                    InvoiceDiscount = 0m,
                    GrandTotal = 100m,
                    Status = SaleStatus.Completed,
                    PaymentStatus = SalePaymentStatus.Paid,
                    ClientOperationId = opId,
                    ReceiptTemplateSnapshot = "{}",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Sales.Add(sale);

                var item = new SaleItem
                {
                    Id = Guid.NewGuid(),
                    SaleId = saleId,
                    InventoryMovementId = movementId,
                    ProductId = product.Id,
                    ProductUnitId = unit.Id,
                    ProductNameSnapshot = product.Name,
                    EnteredQuantity = 1m,
                    FactorToBaseSnapshot = 1m,
                    BaseQuantity = 1m,
                    UnitPrice = 100m,
                    GrossLineTotal = 100m,
                    AllocatedInvoiceDiscount = 0m,
                    NetLineTotal = 100m,
                    UnitCostSnapshot = 50m,
                    TotalCostSnapshot = 50m,
                    GrossProfitSnapshot = 50m
                };
                db.SaleItems.Add(item);

                outbox.Enqueue(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    EffectType = "PrintDocument",
                    SourceType = "Sale",
                    SourceId = saleId.ToString(),
                    PayloadJson = "{}",
                    IdempotencyKey = $"print_crash_{opId:N}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    AttemptCount = 0,
                    Status = OutboxMessageStatus.Pending
                });

                // Flush changes to PostgreSQL inside the uncommitted transaction
                await db.SaveChangesAsync();

                // Signal parent test that transaction is dirty and uncommitted
                Console.WriteLine("READY:CRASH_BEFORE_COMMIT");
                Console.Out.Flush();

                // Block until parent test terminates the process via Process.Kill()
                while (true)
                {
                    await Task.Delay(500);
                }
            }

            case "crash-after-commit":
            {
                var services = new ServiceCollection();
                services.AddEdgeRetailsInfrastructure(dbConn);
                services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

                var product = await db.Products.FirstOrDefaultAsync();
                var user = await db.Users.FirstOrDefaultAsync();
                var unit = await db.ProductUnits.FirstOrDefaultAsync();

                if (product is null || user is null || unit is null)
                {
                    Console.WriteLine("ERROR:Prerequisites missing in DB");
                    return 2;
                }

                await using var tx = await db.Database.BeginTransactionAsync();

                var saleId = Guid.NewGuid();
                var movementId = Guid.NewGuid();

                var movement = new InventoryMovement
                {
                    Id = movementId,
                    ProductId = product.Id,
                    MovementType = InventoryMovementType.SaleOut,
                    ReferenceType = "Sale",
                    ReferenceId = saleId,
                    ActorId = user.Id,
                    OccurredAt = DateTimeOffset.UtcNow,
                    CorrelationId = Guid.NewGuid()
                };
                db.InventoryMovements.Add(movement);

                var sale = new Sale
                {
                    Id = saleId,
                    InvoiceNumber = $"INV-COMM-{opId:N}"[..18],
                    CashierUserId = user.Id,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Subtotal = 200m,
                    InvoiceDiscount = 0m,
                    GrandTotal = 200m,
                    Status = SaleStatus.Completed,
                    PaymentStatus = SalePaymentStatus.Paid,
                    ClientOperationId = opId,
                    ReceiptTemplateSnapshot = "{}",
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Sales.Add(sale);

                var item = new SaleItem
                {
                    Id = Guid.NewGuid(),
                    SaleId = saleId,
                    InventoryMovementId = movementId,
                    ProductId = product.Id,
                    ProductUnitId = unit.Id,
                    ProductNameSnapshot = product.Name,
                    EnteredQuantity = 1m,
                    FactorToBaseSnapshot = 1m,
                    BaseQuantity = 1m,
                    UnitPrice = 200m,
                    GrossLineTotal = 200m,
                    AllocatedInvoiceDiscount = 0m,
                    NetLineTotal = 200m,
                    UnitCostSnapshot = 50m,
                    TotalCostSnapshot = 50m,
                    GrossProfitSnapshot = 150m
                };
                db.SaleItems.Add(item);

                outbox.Enqueue(new OutboxMessage
                {
                    Id = Guid.NewGuid(),
                    EffectType = "PrintDocument",
                    SourceType = "Sale",
                    SourceId = saleId.ToString(),
                    PayloadJson = "{}",
                    IdempotencyKey = $"print_comm_{opId:N}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    AttemptCount = 0,
                    Status = OutboxMessageStatus.Pending
                });

                await db.SaveChangesAsync();
                await tx.CommitAsync();

                // Signal that commit succeeded, but process is about to die before acknowledging
                Console.WriteLine("READY:COMMITTED_BEFORE_ACK");
                Console.Out.Flush();

                // Hard process crash before response can be returned
                Process.GetCurrentProcess().Kill();
                return 0;
            }

            case "worker-active":
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddEdgeRetailsInfrastructure(dbConn);
                var workerDir = Path.Combine(stateDir, "worker");
                Directory.CreateDirectory(workerDir);

                builder.Services.AddSingleton<IWorkerJobLock>(_ => new FileWorkerJobLock(Path.Combine(workerDir, "locks")));
                builder.Services.AddSingleton(sp => new WorkerHeartbeatService(
                    Path.Combine(workerDir, "worker.heartbeat"),
                    sp.GetRequiredService<ILogger<WorkerHeartbeatService>>()));

                builder.Services.AddSingleton<IWorkerJob, OutboxDispatcherJob>();
                builder.Services.AddHostedService<EdgeRetails.Worker.Worker>();

                var host = builder.Build();
                await host.StartAsync();

                Console.WriteLine("READY:WORKER_ACTIVE");
                Console.Out.Flush();

                // Wait until parent terminates process
                while (true)
                {
                    await Task.Delay(500);
                }
            }

            default:
                Console.WriteLine($"ERROR:Unknown scenario '{scenario}'");
                return 1;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
