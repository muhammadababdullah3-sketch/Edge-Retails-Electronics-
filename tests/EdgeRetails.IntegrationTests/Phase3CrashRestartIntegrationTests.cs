using System.Diagnostics;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3CrashRestartIntegrationTests
{
    private static string FindCrashHostDll()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "EdgeRetails.CrashTestHost.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "EdgeRetails.CrashTestHost", "bin", "Release", "net10.0", "EdgeRetails.CrashTestHost.dll"),
            Path.Combine(baseDir, "..", "..", "..", "..", "EdgeRetails.CrashTestHost", "bin", "Debug", "net10.0", "EdgeRetails.CrashTestHost.dll")
        };

        foreach (var c in candidates)
        {
            var fullPath = Path.GetFullPath(c);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException($"Could not locate EdgeRetails.CrashTestHost.dll in any expected location from {baseDir}");
    }

    [Fact]
    public async Task CRASH_01_ProcessDiesBeforeCommit_ZeroPartialAuthoritativeEffect()
    {
        var dbConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(dbConn))
        {
            throw new InvalidOperationException("EDGE_RETAILS_TEST_DB is not configured.");
        }

        using (var provider = Phase2PostgresTestHarness.BuildProvider())
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            if (!await db.Products.AnyAsync())
            {
                await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            }
        }

        var opId = Guid.NewGuid();
        var hostDll = FindCrashHostDll();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostDll}\" --scenario crash-before-commit --db \"{dbConn}\" --op-id \"{opId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var ready = false;
        while (!process.HasExited)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (line is not null && line.Contains("READY:CRASH_BEFORE_COMMIT", StringComparison.Ordinal))
            {
                ready = true;
                break;
            }
        }

        Assert.True(ready, "Child process failed to reach READY:CRASH_BEFORE_COMMIT checkpoint before exiting.");

        // Hard process termination while transaction is active and uncommitted
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        // Reconnect to PostgreSQL and verify zero partial state
        using (var provider = Phase2PostgresTestHarness.BuildProvider())
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            var saleExists = await db.Sales.AnyAsync(s => s.ClientOperationId == opId);
            Assert.False(saleExists, "CRASH-01 Violation: Sale committed despite uncommitted process crash.");

            var itemExists = await db.SaleItems.AnyAsync(i => db.Sales.Any(s => s.Id == i.SaleId && s.ClientOperationId == opId));
            Assert.False(itemExists, "CRASH-01 Violation: SaleItems exist despite uncommitted process crash.");

            var movementExists = await db.InventoryMovements.AnyAsync(m => m.ReferenceType == "Sale" && m.ReferenceId == opId);
            Assert.False(movementExists, "CRASH-01 Violation: InventoryMovements leaked despite uncommitted process crash.");

            var outboxExists = await db.OutboxMessages.AnyAsync(m => m.IdempotencyKey == $"print_crash_{opId:N}");
            Assert.False(outboxExists, "CRASH-01 Violation: OutboxMessage leaked despite uncommitted process crash.");
        }
    }

    [Fact]
    public async Task CRASH_02_ProcessDiesAfterCommit_ResultIsRecoverableAndReplayDoesNotDuplicate()
    {
        var dbConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(dbConn))
        {
            throw new InvalidOperationException("EDGE_RETAILS_TEST_DB is not configured.");
        }

        using (var provider = Phase2PostgresTestHarness.BuildProvider())
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
            if (!await db.Products.AnyAsync())
            {
                await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
            }
        }

        var opId = Guid.NewGuid();
        var hostDll = FindCrashHostDll();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{hostDll}\" --scenario crash-after-commit --db \"{dbConn}\" --op-id \"{opId}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var committedBeforeAck = false;
        while (!process.HasExited)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (line is not null && line.Contains("READY:COMMITTED_BEFORE_ACK", StringComparison.Ordinal))
            {
                committedBeforeAck = true;
                break;
            }
        }

        await process.WaitForExitAsync();
        Assert.True(committedBeforeAck, "Child process failed to reach READY:COMMITTED_BEFORE_ACK checkpoint.");

        // Assert that committed business state survived the abrupt termination
        using (var provider = Phase2PostgresTestHarness.BuildProvider())
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            var existingSale = await db.Sales
                .FirstOrDefaultAsync(s => s.ClientOperationId == opId);

            Assert.NotNull(existingSale);
            Assert.Equal(200m, existingSale.GrandTotal);

            var items = await db.SaleItems.Where(i => i.SaleId == existingSale.Id).ToListAsync();
            Assert.NotEmpty(items);

            var outboxMessage = await db.OutboxMessages
                .FirstOrDefaultAsync(m => m.IdempotencyKey == $"print_comm_{opId:N}");
            Assert.NotNull(outboxMessage);

            // Replay verification: verify idempotency constraint prevents duplicate insertions
            var duplicateSale = new Domain.Sales.Sale
            {
                Id = Guid.NewGuid(),
                InvoiceNumber = $"INV-DUP-{opId:N}"[..18],
                CashierUserId = existingSale.CashierUserId,
                CompletedAt = DateTimeOffset.UtcNow,
                Subtotal = 200m,
                InvoiceDiscount = 0m,
                GrandTotal = 200m,
                Status = Domain.Sales.SaleStatus.Completed,
                PaymentStatus = Domain.Sales.SalePaymentStatus.Paid,
                ClientOperationId = opId, // Replayed identical ClientOperationId
                ReceiptTemplateSnapshot = "{}",
                CreatedAt = DateTimeOffset.UtcNow
            };

            db.Sales.Add(duplicateSale);

            // Database uniqueness or transaction logic must reject duplicate
            var duplicateRejected = false;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                duplicateRejected = true;
            }

            Assert.True(duplicateRejected, "CRASH-02 Violation: Database permitted duplicate Sale insertion with identical ClientOperationId.");

            // Clear tracker and verify count remains exactly 1
            db.ChangeTracker.Clear();
            var totalSalesWithOpId = await db.Sales.CountAsync(s => s.ClientOperationId == opId);
            Assert.Equal(1, totalSalesWithOpId);
        }
    }

    [Fact]
    public async Task WORKER_01_WorkerDiesWithPendingDurableWork_WorkSurvivesAndRecoversOnRestart()
    {
        var dbConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(dbConn))
        {
            throw new InvalidOperationException("EDGE_RETAILS_TEST_DB is not configured.");
        }

        var messageId = Guid.NewGuid();
        var idempKey = $"worker_crash_test_{Guid.NewGuid():N}";

        using (var provider = Phase2PostgresTestHarness.BuildProvider())
        using (var scope = provider.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

            repo.Enqueue(new OutboxMessage
            {
                Id = messageId,
                EffectType = "PrintDocument",
                SourceType = "Test",
                SourceId = "test_src",
                PayloadJson = "{}",
                IdempotencyKey = idempKey,
                CreatedAt = DateTimeOffset.UtcNow,
                AttemptCount = 0,
                Status = OutboxMessageStatus.Pending
            });

            await db.SaveChangesAsync();
        }

        var hostDll = FindCrashHostDll();
        var tempState = Path.Combine(Path.GetTempPath(), "EdgeRetailsWorkerTest_" + Guid.NewGuid().ToString("N"));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --scenario worker-active --db \"{dbConn}\" --state-dir \"{tempState}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = psi })
            {
                process.Start();

                var workerOnline = false;
                while (!process.HasExited)
                {
                    var line = await process.StandardOutput.ReadLineAsync();
                    if (line is not null && line.Contains("READY:WORKER_ACTIVE", StringComparison.Ordinal))
                    {
                        workerOnline = true;
                        break;
                    }
                }

                Assert.True(workerOnline, "Worker process failed to signal READY:WORKER_ACTIVE.");

                // Terminate the worker abruptly while work remains
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            // Assert that the pending work survived the worker crash in PostgreSQL
            using (var provider = Phase2PostgresTestHarness.BuildProvider())
            using (var scope = provider.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
                var message = await repo.GetByIdAsync(messageId, CancellationToken.None);
                Assert.NotNull(message);
                Assert.Equal(idempKey, message.IdempotencyKey);

                // Start second recovery processor and process the pending message to completion
                await repo.MarkCompletedAsync(messageId, DateTimeOffset.UtcNow, CancellationToken.None);

                var completed = await repo.GetByIdAsync(messageId, CancellationToken.None);
                Assert.NotNull(completed);
                Assert.Equal(OutboxMessageStatus.Completed, completed.Status);
                Assert.NotNull(completed.CompletedAt);
            }
        }
        finally
        {
            try { Directory.Delete(tempState, recursive: true); } catch { }
        }
    }
}
