using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Recovery;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Licensing;
using EdgeRetails.Infrastructure.Production.Recovery;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Worker.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Services;

namespace EdgeRetails.CrashTestHost;

public sealed class CrashBeforeCommitInterceptor : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("READY:CRASH_BEFORE_COMMIT");
        Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
        return ValueTask.FromResult(result);
    }
}

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
            ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");

        // Scenarios that exercise the desktop client journal and cold recovery flows
        if (scenario.StartsWith("desktop-journal-", StringComparison.OrdinalIgnoreCase) ||
            scenario.StartsWith("desktop-expense-", StringComparison.OrdinalIgnoreCase))
        {
            var journalStateDir = GetArg(args, "--state-dir") ?? Path.Combine(Path.GetTempPath(), "EdgeRetailsCrashHost", Guid.NewGuid().ToString("N"));
            var journalPath = GetArg(args, "--journal-path") ?? Path.Combine(journalStateDir, "operation-intents.json");
            var store = new FileClientOperationIntentStore(journalPath);
            var opIdStr = GetArg(args, "--op-id") ?? Guid.NewGuid().ToString();
            var intentOpId = Guid.Parse(opIdStr);

            switch (scenario)
            {
                case "desktop-expense-crash-after-submit":
                {
                    if (string.IsNullOrWhiteSpace(dbConn))
                    {
                        throw new InvalidOperationException("desktop-expense-crash-after-submit requires --db or EDGE_RETAILS_TEST_DB");
                    }

                    var services = new ServiceCollection();
                    services.AddEdgeRetailsInfrastructure(dbConn);
                    services.AddScoped<IApplicationGateway, LocalApplicationGateway>();
                    ConfigureTestCustody(services, journalStateDir);
                    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                    using var provider = services.BuildServiceProvider();
                    using var scope = provider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

                    var userIdArg = GetArg(args, "--user-id");
                    var categoryArg = GetArg(args, "--category") ?? "Office Supplies";
                    var subcategoryArg = GetArg(args, "--subcategory") ?? "Stationery";
                    var amountArg = GetArg(args, "--amount");
                    var paymentMethodArg = GetArg(args, "--payment-method") ?? "CASH";
                    var noteArg = GetArg(args, "--note") ?? "Crash test expense";

                    var user = !string.IsNullOrEmpty(userIdArg) && Guid.TryParse(userIdArg, out var uid)
                        ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid)
                        : await db.Users.FirstOrDefaultAsync();

                    if (user is null)
                    {
                        Console.WriteLine("ERROR:User missing in DB");
                        return 2;
                    }

                    var category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Name == categoryArg);
                    if (category is null)
                    {
                        category = new ExpenseCategory { Name = categoryArg, IsActive = true };
                        db.ExpenseCategories.Add(category);
                        await db.SaveChangesAsync();
                    }

                    var subcategory = await db.ExpenseSubcategories.FirstOrDefaultAsync(s => s.CategoryId == category.Id && s.Name == subcategoryArg);
                    if (subcategory is null)
                    {
                        subcategory = new ExpenseSubcategory { CategoryId = category.Id, Name = subcategoryArg, IsActive = true };
                        db.ExpenseSubcategories.Add(subcategory);
                        await db.SaveChangesAsync();
                    }

                    var amount = !string.IsNullOrEmpty(amountArg) && decimal.TryParse(amountArg, out var a) ? a : 150m;

                    var crashHandler = new DelegatingExpenseHttpMessageHandler(
                        provider,
                        actorId: user.Id,
                        shouldCrashOnReadback: true);

                    var httpClient = new HttpClient(crashHandler) { BaseAddress = new Uri("http://127.0.0.1:55437") };
                    var desktopApiClient = new DesktopApiClient(httpClient, ownsClient: true);

                    var baseIntentStore = new FileClientOperationIntentStore(
                        Path.Combine(journalStateDir ?? AppContext.BaseDirectory, "operation-intents.json"));
                    var clientIntentStore = new LoggingIntentStore(baseIntentStore, intentOpId);

                    var expenseService = new RemoteBackendBusinessOperationsService(
                        desktopApiClient,
                        clientIntentStore);

                    // PostExpenseAsync:
                    // 1. Atomically persists intent to FileClientOperationIntentStore before send
                    // 2. Transmits mutation to server via /api/expenses
                    // 3. Server commits expense, cash movement, and OperationOutcome in PostgreSQL
                    // 4. Client attempts read-back via /api/expenses?pageSize=50 -> crashHandler intercepts and kills process!
                    await expenseService.PostExpenseAsync(
                        category.Name,
                        subcategory.Name,
                        amount,
                        DateTime.Today,
                        paymentMethodArg,
                        noteArg);

                    return 0;
                }

                case "desktop-expense-recover":
                {
                    if (string.IsNullOrWhiteSpace(dbConn))
                    {
                        throw new InvalidOperationException("desktop-expense-recover requires --db or EDGE_RETAILS_TEST_DB");
                    }

                    var baseIntentStore = new FileClientOperationIntentStore(
                        Path.Combine(journalStateDir ?? AppContext.BaseDirectory, "operation-intents.json"));
                    var pending = await baseIntentStore.FindPendingByPrefixAsync("expense:create");
                    var matching = pending.FirstOrDefault(p => p.OperationId == intentOpId);
                    if (matching is null)
                    {
                        var allPending = await baseIntentStore.FindPendingByPrefixAsync("expense:");
                        matching = allPending.FirstOrDefault(p => p.OperationId == intentOpId);
                        if (matching is null)
                        {
                            Console.WriteLine("ERROR:DESKTOP_EXPENSE_JOURNAL_NOT_FOUND");
                            return 4;
                        }
                    }

                    Console.WriteLine($"READY:DESKTOP_EXPENSE_RECOVERED:{matching.OperationId:N}");
                    Console.Out.Flush();

                    var services = new ServiceCollection();
                    services.AddEdgeRetailsInfrastructure(dbConn);
                    services.AddScoped<IApplicationGateway, LocalApplicationGateway>();
                    ConfigureTestCustody(services, journalStateDir ?? AppContext.BaseDirectory);
                    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                    using var provider = services.BuildServiceProvider();
                    using var scope = provider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

                    var userIdArg = GetArg(args, "--user-id");
                    var categoryArg = GetArg(args, "--category") ?? "Office Supplies";
                    var subcategoryArg = GetArg(args, "--subcategory") ?? "Stationery";
                    var amountArg = GetArg(args, "--amount");
                    var paymentMethodArg = GetArg(args, "--payment-method") ?? "CASH";
                    var noteArg = GetArg(args, "--note") ?? "Crash test expense";

                    var user = !string.IsNullOrEmpty(userIdArg) && Guid.TryParse(userIdArg, out var uid)
                        ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid)
                        : await db.Users.FirstOrDefaultAsync();

                    if (user is null)
                    {
                        Console.WriteLine("ERROR:User missing in DB");
                        return 2;
                    }

                    var category = await db.ExpenseCategories.FirstOrDefaultAsync(c => c.Name == categoryArg);
                    var subcategory = category is not null
                        ? await db.ExpenseSubcategories.FirstOrDefaultAsync(s => s.CategoryId == category.Id && s.Name == subcategoryArg)
                        : null;

                    if (category is null || subcategory is null)
                    {
                        Console.WriteLine("ERROR:Prerequisites missing in DB");
                        return 3;
                    }

                    var amount = !string.IsNullOrEmpty(amountArg) && decimal.TryParse(amountArg, out var a) ? a : 150m;

                    var recoveryHandler = new DelegatingExpenseHttpMessageHandler(
                        provider,
                        actorId: user.Id,
                        shouldCrashOnReadback: false);

                    var httpClient = new HttpClient(recoveryHandler) { BaseAddress = new Uri("http://127.0.0.1:55437") };
                    var desktopApiClient = new DesktopApiClient(httpClient, ownsClient: true);

                    var expenseService = new RemoteBackendBusinessOperationsService(
                        desktopApiClient,
                        baseIntentStore);

                    // Replay call:
                    // 1. Reads pending intent from FileClientOperationIntentStore
                    // 2. Re-invokes PostExpenseAsync with the SAME ClientOperationId
                    // 3. Server confirms existing committed outcome (WasExisting == true) without double-booking
                    // 4. Client reads back committed record and completes disk journal
                    var recovered = await expenseService.PostExpenseAsync(
                        category.Name,
                        subcategory.Name,
                        amount,
                        DateTime.Today,
                        paymentMethodArg,
                        noteArg,
                        clientOperationId: matching.OperationId);

                    if (recovered is null)
                    {
                        Console.WriteLine("ERROR:RECOVERY_RETURNED_NULL");
                        return 6;
                    }

                    var remaining = await baseIntentStore.FindPendingByPrefixAsync("expense:create");
                    if (remaining.Any(p => p.OperationId == intentOpId))
                    {
                        Console.WriteLine("ERROR:JOURNAL_STILL_PENDING");
                        return 7;
                    }

                    Console.WriteLine($"SUCCESS:DESKTOP_EXPENSE_COMPLETED:{recovered.BackendId ?? intentOpId:N}");
                    return 0;
                }
                case "desktop-journal-crash-after-submit":
                {
                    if (string.IsNullOrWhiteSpace(dbConn))
                    {
                        throw new InvalidOperationException("desktop-journal-crash-after-submit requires --db or EDGE_RETAILS_TEST_DB");
                    }

                    var terminalIdArg = GetArg(args, "--terminal-id");
                    Guid? parsedTerminalId = !string.IsNullOrEmpty(terminalIdArg) && Guid.TryParse(terminalIdArg, out var tid) ? tid : null;

                    var services = new ServiceCollection();
                    services.AddEdgeRetailsInfrastructure(dbConn);
                    services.AddScoped<IApplicationGateway>(sp =>
                    {
                        var gw = new LocalApplicationGateway(sp);
                        if (parsedTerminalId.HasValue)
                        {
                            gw.SetTerminalContext(parsedTerminalId.Value);
                        }
                        return gw;
                    });
                    ConfigureTestCustody(services, journalStateDir);
                    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                    using var provider = services.BuildServiceProvider();
                    using var scope = provider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

                    var userIdArg = GetArg(args, "--user-id");
                    var productIdArg = GetArg(args, "--product-id");
                    var unitIdArg = GetArg(args, "--unit-id");
                    var qtyArg = GetArg(args, "--qty");
                    var amountArg = GetArg(args, "--amount");

                    var product = !string.IsNullOrEmpty(productIdArg) && Guid.TryParse(productIdArg, out var pid)
                        ? await db.Products.FirstOrDefaultAsync(p => p.Id == pid)
                        : await db.Products.FirstOrDefaultAsync();

                    var user = !string.IsNullOrEmpty(userIdArg) && Guid.TryParse(userIdArg, out var uid)
                        ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid)
                        : await db.Users.FirstOrDefaultAsync();

                    var unit = !string.IsNullOrEmpty(unitIdArg) && Guid.TryParse(unitIdArg, out var unId)
                        ? await db.ProductUnits.FirstOrDefaultAsync(u => u.Id == unId)
                        : (product is not null
                            ? await db.ProductUnits.FirstOrDefaultAsync(u => u.ProductId == product.Id)
                            : await db.ProductUnits.FirstOrDefaultAsync());

                    Guid? terminalId = parsedTerminalId ?? (await db.Terminals.FirstOrDefaultAsync())?.Id;

                    if (product is null || user is null || unit is null)
                    {
                        Console.WriteLine("ERROR:Prerequisites missing in DB");
                        return 2;
                    }

                    var qty = !string.IsNullOrEmpty(qtyArg) && decimal.TryParse(qtyArg, out var q) ? q : 1m;
                    var amount = !string.IsNullOrEmpty(amountArg) && decimal.TryParse(amountArg, out var a) ? a : 200m;

                    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

                    var crashHandler = new DelegatingCrashHttpMessageHandler(saleId =>
                    {
                        Console.WriteLine($"READY:DESKTOP_SALE_COMMITTED:{saleId:N}");
                        Console.Out.Flush();
                        Process.GetCurrentProcess().Kill();
                    });

                    var httpClient = new HttpClient(crashHandler) { BaseAddress = new Uri("http://127.0.0.1:55437") };
                    var desktopApiClient = new DesktopApiClient(httpClient, ownsClient: true);

                    var baseIntentStore = new FileClientOperationIntentStore(
                        Path.Combine(journalStateDir ?? AppContext.BaseDirectory, "operation-intents.json"));
                    var clientIntentStore = new LoggingIntentStore(baseIntentStore);

                    var desktopTxService = new BackendTransactionService(
                        scopeFactory,
                        () => user.Id,
                        desktopApiClient,
                        clientIntentStore);

                    var saleRequest = new RecordSaleRequest
                    {
                        ClientOperationId = intentOpId,
                        CustomerId = null,
                        CustomerName = "Walk-in Customer",
                        CashierName = user.DisplayName,
                        TotalAmount = amount,
                        Subtotal = amount,
                        DiscountAmount = 0m,
                        PaymentMethod = PaymentMethod.Cash,
                        AmountReceived = amount,
                        ChangeReturned = 0m,
                        PaymentReference = $"Desktop Crash Test Sale {intentOpId:N}",
                        Notes = null,
                        PostingDate = null,
                        Items = [
                            new SaleTransactionItem
                            {
                                ProductId = product.Id.ToString("D"),
                                BackendProductId = product.Id,
                                BackendProductUnitId = unit.Id,
                                ProductName = product.Name,
                                Quantity = qty,
                                UnitPrice = amount,
                                ListUnitPrice = amount,
                                InventoryUnitIds = []
                            }
                        ]
                    };

                    // RecordTransactionAsync:
                    // 1. Atomically persists intent to FileClientOperationIntentStore before send
                    // 2. Transmits mutation to server via IApplicationGateway.CompleteSaleAsync
                    // 3. Server commits sale and outbox in PostgreSQL
                    // 4. Client attempts read-back via DesktopApiClient -> crashHandler intercepts and kills process!
                    await desktopTxService.RecordTransactionAsync(saleRequest);
                    return 0;
                }

                case "desktop-journal-recover":
                {
                    if (string.IsNullOrWhiteSpace(dbConn))
                    {
                        throw new InvalidOperationException("desktop-journal-recover requires --db or EDGE_RETAILS_TEST_DB");
                    }

                    var baseIntentStore = new FileClientOperationIntentStore(
                        Path.Combine(journalStateDir ?? AppContext.BaseDirectory, "operation-intents.json"));
                    var pending = await baseIntentStore.FindPendingByPrefixAsync(BackendTransactionService.SaleCheckoutIntentKey);
                    var matching = pending.FirstOrDefault(p => p.OperationId == intentOpId);
                    if (matching is null)
                    {
                        var allPending = await baseIntentStore.FindPendingByPrefixAsync("sale:");
                        matching = allPending.FirstOrDefault(p => p.OperationId == intentOpId);
                        if (matching is null)
                        {
                            Console.WriteLine("ERROR:DESKTOP_JOURNAL_NOT_FOUND");
                            return 4;
                        }
                    }

                    Console.WriteLine($"READY:DESKTOP_JOURNAL_RECOVERED:{matching.OperationId:N}");
                    Console.Out.Flush();

                    var services = new ServiceCollection();
                    services.AddEdgeRetailsInfrastructure(dbConn);
                    services.AddScoped<IApplicationGateway, LocalApplicationGateway>();
                    ConfigureTestCustody(services, journalStateDir ?? AppContext.BaseDirectory);
                    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                    using var provider = services.BuildServiceProvider();

                    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

                    var recoveryHandler = new RecoveryDetailHttpMessageHandler(provider);
                    var httpClient = new HttpClient(recoveryHandler) { BaseAddress = new Uri("http://127.0.0.1:55437") };
                    var desktopApiClient = new DesktopApiClient(httpClient, ownsClient: true);

                    var desktopTxService = new BackendTransactionService(
                        scopeFactory,
                        () => null,
                        desktopApiClient,
                        baseIntentStore);

                    // TryRecoverPendingSaleAsync:
                    // 1. Reads pending intent from FileClientOperationIntentStore
                    // 2. Queries authoritative status via IApplicationGateway.QueryOperationStatusAsync(entry.OperationId)
                    // 3. Verifies status.WasCommitted && status.OperationType == "Sale"
                    // 4. Loads sale detail through DesktopApiClient
                    // 5. Authoritatively completes the intent journal only after outcome confirmation
                    var recovered = await desktopTxService.TryRecoverPendingSaleAsync();
                    if (recovered is null)
                    {
                        Console.WriteLine("ERROR:RECOVERY_RETURNED_NULL");
                        return 6;
                    }

                    var remaining = await baseIntentStore.FindPendingByPrefixAsync(BackendTransactionService.SaleCheckoutIntentKey);
                    if (remaining.Any(p => p.OperationId == intentOpId))
                    {
                        Console.WriteLine("ERROR:JOURNAL_STILL_PENDING");
                        return 7;
                    }

                    Console.WriteLine($"SUCCESS:DESKTOP_JOURNAL_COMPLETED:{recovered.BackendSaleId ?? intentOpId:N}");
                    return 0;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(dbConn))
        {
            throw new InvalidOperationException("Missing --db argument or EDGE_RETAILS_TEST_DB environment variable.");
        }

        var opIdString = GetArg(args, "--op-id") ?? Guid.NewGuid().ToString();
        var opId = Guid.Parse(opIdString);
        var stateDir = GetArg(args, "--state-dir") ?? Path.Combine(Path.GetTempPath(), "EdgeRetailsCrashHost", Guid.NewGuid().ToString("N"));

        switch (scenario)
        {
            case "crash-before-commit":
            {
                // MON-P12W3-R7-002: Execute production CompleteSaleHandler inside an uncommitted database
                // transaction with an EF Core DbTransactionInterceptor that terminates the child process at TransactionCommittingAsync.
                var services = new ServiceCollection();
                services.AddEdgeRetailsInfrastructure(dbConn, options =>
                {
                    options.AddInterceptors(new CrashBeforeCommitInterceptor());
                });
                ConfigureTestCustody(services, stateDir);
                services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

                var userIdArg = GetArg(args, "--user-id");
                var productIdArg = GetArg(args, "--product-id");
                var unitIdArg = GetArg(args, "--unit-id");
                var terminalIdArg = GetArg(args, "--terminal-id");
                var qtyArg = GetArg(args, "--qty");
                var amountArg = GetArg(args, "--amount");

                var product = !string.IsNullOrEmpty(productIdArg) && Guid.TryParse(productIdArg, out var pid)
                    ? await db.Products.FirstOrDefaultAsync(p => p.Id == pid)
                    : await db.Products.FirstOrDefaultAsync();

                var user = !string.IsNullOrEmpty(userIdArg) && Guid.TryParse(userIdArg, out var uid)
                    ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid)
                    : await db.Users.FirstOrDefaultAsync();

                var unit = !string.IsNullOrEmpty(unitIdArg) && Guid.TryParse(unitIdArg, out var unId)
                    ? await db.ProductUnits.FirstOrDefaultAsync(u => u.Id == unId)
                    : (product is not null
                        ? await db.ProductUnits.FirstOrDefaultAsync(u => u.ProductId == product.Id)
                        : await db.ProductUnits.FirstOrDefaultAsync());

                Guid? terminalId = !string.IsNullOrEmpty(terminalIdArg) && Guid.TryParse(terminalIdArg, out var tid)
                    ? tid
                    : (await db.Terminals.FirstOrDefaultAsync())?.Id;

                if (product is null || user is null || unit is null)
                {
                    Console.WriteLine("ERROR:Prerequisites missing in DB");
                    return 2;
                }

                var qty = !string.IsNullOrEmpty(qtyArg) && decimal.TryParse(qtyArg, out var q) ? q : 1m;
                var amount = !string.IsNullOrEmpty(amountArg) && decimal.TryParse(amountArg, out var a) ? a : 200m;

                var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
                var saleCmd = new CompleteSaleCommand(
                    ClientOperationId: opId,
                    CustomerId: null,
                    CashierUserId: user.Id,
                    SessionId: null,
                    InvoiceDiscount: 0m,
                    PaymentMethod: SalePaymentMethod.Cash,
                    AmountTendered: amount,
                    PaymentReference: $"Crash Host Sale {opId:N}",
                    QuotationId: null,
                    Lines: [new CompleteSaleLineInput(product.Id, unit.Id, qty, amount, [])],
                    RegisteredTerminalId: terminalId);

                // This triggers CrashBeforeCommitInterceptor.TransactionCommittingAsync which kills the process before commit
                var saleResult = await saleHandler.HandleAsync(saleCmd, default);
                Console.WriteLine($"UNEXPECTED:Sale completed without crash: {saleResult.IsSuccess}");
                return 0;
            }

            case "crash-after-commit":
            {
                var services = new ServiceCollection();
                services.AddEdgeRetailsInfrastructure(dbConn);
                ConfigureTestCustody(services, stateDir);
                services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

                var userIdArg = GetArg(args, "--user-id");
                var productIdArg = GetArg(args, "--product-id");
                var unitIdArg = GetArg(args, "--unit-id");
                var terminalIdArg = GetArg(args, "--terminal-id");
                var qtyArg = GetArg(args, "--qty");
                var amountArg = GetArg(args, "--amount");

                var product = !string.IsNullOrEmpty(productIdArg) && Guid.TryParse(productIdArg, out var pid)
                    ? await db.Products.FirstOrDefaultAsync(p => p.Id == pid)
                    : await db.Products.FirstOrDefaultAsync();

                var user = !string.IsNullOrEmpty(userIdArg) && Guid.TryParse(userIdArg, out var uid)
                    ? await db.Users.FirstOrDefaultAsync(u => u.Id == uid)
                    : await db.Users.FirstOrDefaultAsync();

                var unit = !string.IsNullOrEmpty(unitIdArg) && Guid.TryParse(unitIdArg, out var unId)
                    ? await db.ProductUnits.FirstOrDefaultAsync(u => u.Id == unId)
                    : (product is not null
                        ? await db.ProductUnits.FirstOrDefaultAsync(u => u.ProductId == product.Id)
                        : await db.ProductUnits.FirstOrDefaultAsync());

                Guid? terminalId = !string.IsNullOrEmpty(terminalIdArg) && Guid.TryParse(terminalIdArg, out var tid)
                    ? tid
                    : (await db.Terminals.FirstOrDefaultAsync())?.Id;

                if (product is null || user is null || unit is null)
                {
                    Console.WriteLine("ERROR:Prerequisites missing in DB");
                    return 2;
                }

                var qty = !string.IsNullOrEmpty(qtyArg) && decimal.TryParse(qtyArg, out var q) ? q : 1m;
                var amount = !string.IsNullOrEmpty(amountArg) && decimal.TryParse(amountArg, out var a) ? a : 200m;

                var saleHandler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
                var saleCmd = new CompleteSaleCommand(
                    ClientOperationId: opId,
                    CustomerId: null,
                    CashierUserId: user.Id,
                    SessionId: null,
                    InvoiceDiscount: 0m,
                    PaymentMethod: SalePaymentMethod.Cash,
                    AmountTendered: amount,
                    PaymentReference: $"Crash Host Sale {opId:N}",
                    QuotationId: null,
                    Lines: [new CompleteSaleLineInput(product.Id, unit.Id, qty, amount, [])],
                    RegisteredTerminalId: terminalId);

                var saleResult = await saleHandler.HandleAsync(saleCmd, default);
                if (!saleResult.IsSuccess)
                {
                    Console.WriteLine($"ERROR:Sale failed: {saleResult.Error?.Code} - {saleResult.Error?.Message}");
                    return 3;
                }

                Console.WriteLine("READY:COMMITTED_BEFORE_ACK");
                Console.Out.Flush();

                // Hard process crash before response can be returned
                Process.GetCurrentProcess().Kill();
                return 0;
            }

            case "server-http-crash-after-commit":
            {
                // MON-P12W3-R7-001: Integrate real EdgeRetails.Server ASP.NET Core HTTP pipeline / response boundary
                var portArg = GetArg(args, "--port") ?? "7199";
                var port = int.Parse(portArg);

                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
                builder.Services.AddEdgeRetailsInfrastructure(dbConn);
                ConfigureTestCustody(builder.Services, stateDir);
                builder.Services.AddSingleton<IRecoveryAuthorizationTrustProvider, UnprovisionedRecoveryAuthorizationTrustProvider>();
                builder.Services.AddControllers()
                    .AddApplicationPart(typeof(SalesController).Assembly)
                    .AddJsonOptions(options =>
                    {
                        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    });
                builder.Services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

                var app = builder.Build();

                // Middleware to buffer response before commit and terminate process before sending response to client
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.Value?.Contains("complete", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var originalBodyStream = context.Response.Body;
                        using var responseBody = new MemoryStream();
                        context.Response.Body = responseBody;

                        await next(context);

                        if (context.Response.StatusCode == 200)
                        {
                            Console.WriteLine("READY:SERVER_HTTP_COMMITTED_KILLING");
                            Console.Out.Flush();
                            // Kill process before responseBody is copied to originalBodyStream.
                            // Client connection is severed with zero bytes sent back, proving genuine response loss.
                            Process.GetCurrentProcess().Kill();
                        }
                        else
                        {
                            Console.WriteLine($"ERROR:SERVER_HTTP_STATUS_{context.Response.StatusCode}");
                            Console.Out.Flush();
                            responseBody.Position = 0;
                            await responseBody.CopyToAsync(originalBodyStream);
                            Process.GetCurrentProcess().Kill();
                        }
                    }
                    else
                    {
                        await next(context);
                    }
                });

                app.MapControllers();
                await app.StartAsync();

                Console.WriteLine($"READY:SERVER_LISTENING:{port}");
                Console.Out.Flush();

                await app.WaitForShutdownAsync();
                return 0;
            }

            case "worker-active":
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.AddEdgeRetailsInfrastructure(dbConn);
                ConfigureTestCustody(builder.Services, stateDir);
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

    private static void ConfigureTestCustody(IServiceCollection services, string stateDir)
    {
        var highwaterDir = Path.Combine(stateDir, "highwater_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(highwaterDir);
        var manifest = Path.Combine(highwaterDir, "highwater.manifest");
        var custody = new OwnedSequenceAuthorityCustody(highwaterDir);
        MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, custody);
        services.RemoveAll<ISequenceHighWaterService>();
        services.AddSingleton<ISequenceHighWaterService>(new MachineSequenceHighWaterService(manifest, custody));
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

    private sealed class LoggingIntentStore : IClientOperationIntentStore
    {
        private readonly IClientOperationIntentStore _inner;
        private readonly Guid? _preferredOpId;

        public LoggingIntentStore(IClientOperationIntentStore inner, Guid? preferredOpId = null)
        {
            _inner = inner;
            _preferredOpId = preferredOpId;
        }

        public Guid GetOrCreate(string operationKey, string payload)
        {
            var id = _preferredOpId.HasValue
                ? _inner.GetOrCreate(operationKey, payload, _preferredOpId.Value)
                : _inner.GetOrCreate(operationKey, payload);
            Console.WriteLine($"READY:DESKTOP_JOURNAL_WRITTEN:{id:N}");
            Console.Out.Flush();
            return id;
        }

        public Guid GetOrCreate(string operationKey, string payload, Guid preferredOperationId)
        {
            var id = _inner.GetOrCreate(operationKey, payload, preferredOperationId);
            Console.WriteLine($"READY:DESKTOP_JOURNAL_WRITTEN:{id:N}");
            Console.Out.Flush();
            return id;
        }

        public Guid GetOrCreate(string operationKey, string payload, Guid preferredOperationId, string? operationType, string? safeDisplayContext = null, string? safeReplayEnvelope = null)
        {
            var id = _inner.GetOrCreate(operationKey, payload, preferredOperationId, operationType, safeDisplayContext, safeReplayEnvelope);
            Console.WriteLine($"READY:DESKTOP_JOURNAL_WRITTEN:{id:N}");
            Console.Out.Flush();
            return id;
        }

        public IReadOnlyList<ClientOperationIntent> FindPendingByPrefix(string operationKeyPrefix) =>
            _inner.FindPendingByPrefix(operationKeyPrefix);

        public void Complete(string operationKey, Guid operationId) =>
            _inner.Complete(operationKey, operationId);
    }

    private sealed class DelegatingCrashHttpMessageHandler : HttpMessageHandler
    {
        private readonly Action<Guid> _onCommittedSaleRequested;

        public DelegatingCrashHttpMessageHandler(Action<Guid> onCommittedSaleRequested)
        {
            _onCommittedSaleRequested = onCommittedSaleRequested;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0 && Guid.TryParse(segments[^1], out var saleId))
                {
                    _onCommittedSaleRequested(saleId);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed class RecoveryDetailHttpMessageHandler : HttpMessageHandler
    {
        private readonly IServiceProvider _provider;

        public RecoveryDetailHttpMessageHandler(IServiceProvider provider)
        {
            _provider = provider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0 && Guid.TryParse(segments[^1], out var saleId))
                {
                    using var scope = _provider.CreateScope();
                    var salesReads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
                    var dto = await salesReads.GetDetailAsync(new GetSaleDetailQuery(saleId), cancellationToken);
                    if (dto is not null)
                    {
                        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }
                    Console.WriteLine($"[RecoveryDetailHttpMessageHandler] dto was null for saleId={saleId}");
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class DelegatingExpenseHttpMessageHandler : HttpMessageHandler
    {
        private readonly IServiceProvider _provider;
        private readonly Guid _actorId;
        private readonly bool _shouldCrashOnReadback;
        private Guid _lastCommittedExpenseId;

        public DelegatingExpenseHttpMessageHandler(IServiceProvider provider, Guid actorId, bool shouldCrashOnReadback)
        {
            _provider = provider;
            _actorId = actorId;
            _shouldCrashOnReadback = shouldCrashOnReadback;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

            using var scope = _provider.CreateScope();

            if (request.Method == HttpMethod.Get && path.Equals("/api/expenses/categories", StringComparison.OrdinalIgnoreCase))
            {
                var reads = scope.ServiceProvider.GetRequiredService<IExpenseReadService>();
                var categories = await reads.GetCategoriesAsync(cancellationToken);
                var json = JsonSerializer.Serialize(categories, jsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && path.Equals("/api/expenses/subcategories", StringComparison.OrdinalIgnoreCase))
            {
                var reads = scope.ServiceProvider.GetRequiredService<IExpenseReadService>();
                var subcategories = await reads.GetSubcategoriesAsync(cancellationToken);
                var json = JsonSerializer.Serialize(subcategories, jsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Post && path.Equals("/api/expenses", StringComparison.OrdinalIgnoreCase))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var req = JsonSerializer.Deserialize<PostExpenseRequest>(body, jsonOptions);
                if (req is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"code\":\"invalid_json\",\"message\":\"Invalid request body\"}", Encoding.UTF8, "application/json")
                    };
                }

                var actorId = req.ActorId ?? _actorId;
                var handler = scope.ServiceProvider.GetRequiredService<PostExpenseHandler>();
                var cmd = new PostExpenseCommand(
                    ClientOperationId: req.ClientOperationId,
                    CategoryId: req.CategoryId,
                    SubcategoryId: req.SubcategoryId,
                    ExpenseDate: req.ExpenseDate,
                    Amount: req.Amount,
                    PaymentMethod: req.PaymentMethod,
                    Description: req.Description,
                    Reference: req.Reference,
                    ActorId: actorId);

                var result = await handler.HandleAsync(cmd, cancellationToken);
                if (!result.IsSuccess)
                {
                    var errJson = JsonSerializer.Serialize(new { code = result.Error!.Code, message = result.Error.Message }, jsonOptions);
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(errJson, Encoding.UTF8, "application/json")
                    };
                }

                _lastCommittedExpenseId = result.Value!.ExpenseId;
                var resJson = JsonSerializer.Serialize(result.Value, jsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resJson, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && path.Equals("/api/expenses", StringComparison.OrdinalIgnoreCase))
            {
                if (_shouldCrashOnReadback)
                {
                    Console.WriteLine($"READY:DESKTOP_EXPENSE_COMMITTED:{_lastCommittedExpenseId:N}");
                    Console.Out.Flush();
                    Process.GetCurrentProcess().Kill();
                }

                var reads = scope.ServiceProvider.GetRequiredService<IExpenseReadService>();
                var query = new GetExpensesPageQuery(PageSize: 50);
                var rows = await reads.GetExpensesPageAsync(query, cancellationToken);
                var json = JsonSerializer.Serialize(rows, jsonOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/api/system/operations/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0 && Guid.TryParse(segments[^1], out var opId))
                {
                    var statusQueryHandler = scope.ServiceProvider.GetRequiredService<OperationStatusQueryHandler>();
                    var statusRes = await statusQueryHandler.HandleAsync(new OperationStatusQuery(opId), cancellationToken);
                    if (statusRes.IsSuccess && statusRes.Value is not null)
                    {
                        var json = JsonSerializer.Serialize(statusRes.Value, jsonOptions);
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"effectiveStatus\":\"NotFound\",\"wasCommitted\":false}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

public sealed record DesktopSaleReplayEnvelope(
    Guid ClientOperationId,
    Guid UserId,
    Guid ProductId,
    Guid ProductUnitId,
    Guid? TerminalId,
    decimal EnteredQuantity,
    decimal ExpectedUnitPrice);

