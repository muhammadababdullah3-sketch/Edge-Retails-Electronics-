using System.Diagnostics;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
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
