using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Sales;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase12Wave2JournalAndRecoveryTests : IDisposable
{
    private readonly string _tempDirectory;

    public Phase12Wave2JournalAndRecoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EdgeRetails_Journal_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in test dispose
        }
    }

    private string CreateTempJournalPath() => Path.Combine(_tempDirectory, "operation-intents.json");

    // =========================================================================
    // P12-W2-006: Journal Schema v2, Canonical Machine Root, Secret Exclusions
    // =========================================================================

    [Fact]
    public void P12_W2_006_Journal_Uses_Schema_Version_2()
    {
        Assert.Equal(2, FileClientOperationIntentStore.CurrentVersion);
    }

    [Fact]
    public void P12_W2_006_Canonical_Path_Resolves_To_Machine_Root_CommonApplicationData()
    {
        var resolved = FileClientOperationIntentStore.ResolveCanonicalPath(null);
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.EndsWith(Path.Combine("EdgeRetails", "Desktop", "operation-intents.json"), resolved);
    }

    [Fact]
    public void P12_W2_006_Canonical_Path_Honors_Configured_State_Dir_Override()
    {
        var originalEnv = Environment.GetEnvironmentVariable("EDGE_RETAILS_STATE_DIR");
        try
        {
            var customStateDir = Path.Combine(_tempDirectory, "custom-state");
            Environment.SetEnvironmentVariable("EDGE_RETAILS_STATE_DIR", customStateDir);

            var resolved = FileClientOperationIntentStore.ResolveCanonicalPath(null);
            Assert.Equal(Path.GetFullPath(Path.Combine(customStateDir, "Desktop", "operation-intents.json")), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_STATE_DIR", originalEnv);
        }
    }

    [Theory]
    [InlineData("{\"pin\": \"1234\"}")]
    [InlineData("supervisorPin=9999")]
    [InlineData("password=SecretPassword1")]
    [InlineData("{\"privateKey\": \"MIIEvgIBADANBgkqhkiG9w0BAQEFAASC...\"}")]
    [InlineData("app_secret_abc123")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...")]
    public void P12_W2_006_AssertNoSecrets_Rejects_Prohibited_Keywords(string sensitivePayload)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FileClientOperationIntentStore.AssertNoSecrets(sensitivePayload));

        Assert.Contains("prohibited sensitive credentials or secret identifier", ex.Message);
    }

    [Fact]
    public void P12_W2_006_GetOrCreate_Rejects_Secrets_In_SafeDisplayContext_And_SafeReplayEnvelope()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opKey = "sale:draft:123";
        var payload = "{\"items\": []}";
        var preferredId = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() =>
            store.GetOrCreate(opKey, payload, preferredId, "Sale", safeDisplayContext: "SupervisorPin=1234"));

        Assert.Throws<InvalidOperationException>(() =>
            store.GetOrCreate(opKey, payload, preferredId, "Sale", safeReplayEnvelope: "{\"token\": \"bearer xyz\"}"));
    }

    [Fact]
    public void P12_W2_006_Reads_Schema_V1_Document_And_Upgrades_To_V2_On_Write()
    {
        var path = CreateTempJournalPath();
        var v1Json = """
            {
              "Version": 1,
              "Entries": [
                {
                  "OperationKey": "sale:draft:v1-test",
                  "PayloadSha256": "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4E5F6A1B2",
                  "OperationId": "018d0000-0000-7000-8000-000000000001"
                }
              ]
            }
            """;
        File.WriteAllText(path, v1Json);

        var store = new FileClientOperationIntentStore(path);
        var pending = store.FindPendingByPrefix("sale:draft:");
        Assert.Single(pending);
        Assert.Equal("sale:draft:v1-test", pending[0].OperationKey);
        Assert.Equal(Guid.Parse("018d0000-0000-7000-8000-000000000001"), pending[0].OperationId);

        // A new write upgrades the document to Schema v2
        var newKey = "sale:draft:v2-new";
        var newPayload = "{\"test\": true}";
        var newId = store.GetOrCreate(newKey, newPayload, Guid.NewGuid(), "Sale", safeDisplayContext: "Invoice 1001");

        var upgradedText = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(upgradedText);
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32());
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
    }

    // =========================================================================
    // P12-W2-007: Durable Atomic Persist + Flush and Concurrent Writer Safety
    // =========================================================================

    [Fact]
    public void P12_W2_007_Write_Is_Atomic_And_Leaves_No_Temporary_Files()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);

        for (int i = 0; i < 5; i++)
        {
            store.GetOrCreate($"op:{i}", $"{{\"i\": {i}}}", Guid.NewGuid(), "Sale");
        }

        var dir = Path.GetDirectoryName(path)!;
        var tmpFiles = Directory.GetFiles(dir, "*.tmp");
        Assert.Empty(tmpFiles);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task P12_W2_007_Concurrent_Writers_Do_Not_Corrupt_Intent_Store()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        const int taskCount = 10;
        const int opsPerTask = 5;

        var tasks = Enumerable.Range(0, taskCount).Select(t => Task.Run(() =>
        {
            var taskStore = new FileClientOperationIntentStore(path);
            for (int i = 0; i < opsPerTask; i++)
            {
                var key = $"concurrent:t{t}:op{i}";
                var payload = $"{{\"thread\": {t}, \"op\": {i}}}";
                taskStore.GetOrCreate(key, payload, Guid.NewGuid(), "Sale", $"Display-{t}-{i}");
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var verifyStore = new FileClientOperationIntentStore(path);
        var allPending = verifyStore.FindPendingByPrefix("concurrent:");
        Assert.Equal(taskCount * opsPerTask, allPending.Count);
    }

    // =========================================================================
    // P12-W2-008: Immutable Operation Identity and Divergent Payload Rejection
    // =========================================================================

    [Fact]
    public void P12_W2_008_Identical_GetOrCreate_Replays_Same_OperationId()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var key = "sale:cart:42";
        var payload = "{\"items\": [{\"id\": 1, \"qty\": 2}]}";
        var preferredId = Guid.NewGuid();

        var id1 = store.GetOrCreate(key, payload, preferredId, "Sale", "Cart 42");
        var id2 = store.GetOrCreate(key, payload, preferredId, "Sale", "Cart 42");

        Assert.Equal(preferredId, id1);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void P12_W2_008_Divergent_Payload_Throws_InvalidOperationException()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var key = "sale:cart:42";
        var payload1 = "{\"items\": [{\"id\": 1, \"qty\": 2}]}";
        var payload2 = "{\"items\": [{\"id\": 1, \"qty\": 3}]}"; // changed qty!
        var preferredId = Guid.NewGuid();

        store.GetOrCreate(key, payload1, preferredId, "Sale");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.GetOrCreate(key, payload2, preferredId, "Sale"));

        Assert.Contains("unresolved outcome and a different payload", ex.Message);
    }

    [Fact]
    public void P12_W2_008_Divergent_PreferredOperationId_Throws_InvalidOperationException()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var key = "sale:cart:42";
        var payload = "{\"items\": []}";
        var preferredId1 = Guid.NewGuid();
        var preferredId2 = Guid.NewGuid();

        store.GetOrCreate(key, payload, preferredId1, "Sale");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.GetOrCreate(key, payload, preferredId2, "Sale"));

        Assert.Contains("operation identity changed before its outcome was resolved", ex.Message);
    }

    // =========================================================================
    // P12-W2-009 & P12-W2-010: Restart Unresolved Recovery and Atomic Resolution
    // =========================================================================

    [Fact]
    public void P12_W2_009_FindPendingByPrefix_Recovers_Pending_Intents_After_Simulated_Restart()
    {
        var path = CreateTempJournalPath();

        // 1. First process session creates intent
        var session1Store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();
        session1Store.GetOrCreate("sale:draft:55", "{\"cart\": 55}", opId, "Sale", "Safe Context 55");

        // 2. Simulated process restart: new instance reading from disk
        var session2Store = new FileClientOperationIntentStore(path);
        var pending = session2Store.FindPendingByPrefix("sale:draft:");

        Assert.Single(pending);
        var intent = pending[0];
        Assert.Equal("sale:draft:55", intent.OperationKey);
        Assert.Equal(opId, intent.OperationId);
        Assert.Equal("Sale", intent.OperationType);
        Assert.Equal("Pending", intent.Status);
        Assert.Equal("Safe Context 55", intent.SafeDisplayContext);
        Assert.NotNull(intent.PayloadSha256);
        Assert.NotNull(intent.StartedAtUtc);
    }

    [Fact]
    public void P12_W2_010_Complete_Removes_Intent_Only_On_Authoritative_Resolution()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var key = "sale:draft:99";
        var opId = Guid.NewGuid();

        store.GetOrCreate(key, "{\"cart\": 99}", opId, "Sale");
        Assert.Single(store.FindPendingByPrefix("sale:draft:"));

        // Attempt to complete with mismatched operationId fails-closed
        var wrongId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => store.Complete(key, wrongId));
        Assert.Single(store.FindPendingByPrefix("sale:draft:"));

        // Complete with correct operationId removes entry
        store.Complete(key, opId);
        Assert.Empty(store.FindPendingByPrefix("sale:draft:"));
    }

    // =========================================================================
    // P12-W2-F003: Safe Fields, Legitimate Customer Names, Migration & Pre-Send Ordering
    // =========================================================================

    [Theory]
    [InlineData("Pintu")]
    [InlineData("Customer: Pintu, Total 100")]
    [InlineData("Walk-in Customer Spinelli")]
    [InlineData("Cart containing Pins and Needles")]
    [InlineData("Item Skus: PIN-001, PIN-002")]
    [InlineData("Sale for customer Pintu")]
    public void P12_W2_F003_AssertNoSecrets_Permits_Legitimate_Customer_And_Display_Names(string legitimateName)
    {
        // Must not throw on legitimate customer names or display texts containing substrings like 'pin'
        FileClientOperationIntentStore.AssertNoSecrets(legitimateName);
    }

    [Theory]
    [InlineData("{\"pin\": \"1234\"}")]
    [InlineData("pin = 1234")]
    [InlineData("{\"supervisorPin\": \"9999\"}")]
    [InlineData("supervisorPin = 9999")]
    [InlineData("{\"accessToken\": \"eyJhbGci...\"}")]
    [InlineData("accessToken = eyJhbGci...")]
    [InlineData("{\"password\": \"secret123\"}")]
    [InlineData("password = secret123")]
    [InlineData("{\"privateKey\": \"MIIEvgIBADANBg...\"}")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...")]
    [InlineData("app_secret_live_998877")]
    public void P12_W2_F003_AssertNoSecrets_Rejects_Targeted_Credential_Keys_And_Tokens(string secretText)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FileClientOperationIntentStore.AssertNoSecrets(secretText));

        Assert.Contains("prohibited sensitive credentials or secret identifier", ex.Message);
    }

    [Fact]
    public void P12_W2_F003_Legacy_LocalAppData_Migrates_To_Canonical_Machine_Root_In_Owned_Fixture()
    {
        var ownedRoot = Path.Combine(_tempDirectory, "migration_fixture");
        var localAppDataDir = Path.Combine(ownedRoot, "LocalAppData");
        var commonAppDataDir = Path.Combine(ownedRoot, "CommonAppData");

        // Set up legacy journal file in LocalAppData
        var legacyFileDir = Path.Combine(localAppDataDir, FileClientOperationIntentStore.DefaultFolderName, "Desktop");
        Directory.CreateDirectory(legacyFileDir);
        var legacyFilePath = Path.Combine(legacyFileDir, FileClientOperationIntentStore.DefaultFileName);

        var existingOpId = Guid.NewGuid();
        var legacyDoc = new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    OperationKey = "sale:draft:legacy-001",
                    PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload-data"))),
                    OperationId = existingOpId,
                    OperationType = "Sale",
                    FingerprintVersion = "v1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Status = "Pending",
                    SafeDisplayContext = "Customer Pintu",
                    SafeReplayEnvelope = "{\"items\":[]}"
                }
            }
        };
        File.WriteAllText(legacyFilePath, JsonSerializer.Serialize(legacyDoc));

        // Resolve canonical path using overrides in our owned temporary fixture
        var canonicalPath = FileClientOperationIntentStore.ResolveCanonicalPath(
            null,
            commonAppDataOverride: commonAppDataDir,
            localAppDataOverride: localAppDataDir);

        var expectedCanonicalPath = Path.GetFullPath(Path.Combine(
            commonAppDataDir,
            FileClientOperationIntentStore.DefaultFolderName,
            "Desktop",
            FileClientOperationIntentStore.DefaultFileName));

        Assert.Equal(expectedCanonicalPath, canonicalPath);
        Assert.True(File.Exists(canonicalPath), "Canonical journal file should exist after migration.");
        Assert.True(File.Exists($"{legacyFilePath}.migrated"), "Legacy journal file should have been renamed to .migrated.");

        // Verify content survived migration and can be loaded by store
        var migratedStore = new FileClientOperationIntentStore(canonicalPath);
        var pending = migratedStore.FindPendingByPrefix("sale:draft:");
        Assert.Single(pending);
        Assert.Equal("sale:draft:legacy-001", pending[0].OperationKey);
        Assert.Equal(existingOpId, pending[0].OperationId);
        Assert.Equal("Customer Pintu", pending[0].SafeDisplayContext);
    }

    [Fact]
    public async Task P12_W2_F003_Pre_First_Send_Flush_Ordering_And_Persistence_Failure_Prevents_Send()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var (gateway, controller) = ControllableGateway.Create();
        using var apiClient = CreateStubApiClient();
        var service = CreateTestService(gateway, apiClient, store);

        var opId = Guid.NewGuid();
        // Request with prohibited secret keyword in customer name that fails journal validation
        var invalidRequest = CreateSampleSaleRequest(opId, customerName: "\"pin\": \"1234\"");

        // Persistence must fail-closed before any network send
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordTransactionAsync(invalidRequest));

        Assert.Contains("prohibited sensitive credentials", ex.Message);
        // Zero mutation calls reached the gateway
        Assert.Empty(controller.CompleteSaleCalls);
        // Journal store has no entries
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    // =========================================================================
    // P12-W2-F002: Non-Final vs Authoritative Final Classification
    // =========================================================================

    [Theory]
    [InlineData("idempotency.operation_in_flight", "Operation is currently in flight.")]
    [InlineData("idempotency.outcome_unknown", "Outcome unknown.")]
    [InlineData("operation.in_flight", "Operation running.")]
    [InlineData("operation.unknown", "Operation state unknown.")]
    [InlineData("network.timeout", "Network timed out.")]
    [InlineData("gateway.unavailable", "Gateway temporarily down.")]
    [InlineData("http.503", "Service unavailable.")]
    public async Task P12_W2_F002_NonFinal_Errors_Retain_Pending_Intent(string errorCode, string errorMessage)
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var (gateway, controller) = ControllableGateway.Create();
        controller.OnCompleteSale = _ => Task.FromResult(Result<CompleteSaleResult>.Failure(errorCode, errorMessage));

        using var apiClient = CreateStubApiClient();
        var service = CreateTestService(gateway, apiClient, store);

        var opId = Guid.NewGuid();
        var request = CreateSampleSaleRequest(opId, "Customer Pintu");

        var ex = await Assert.ThrowsAsync<BackendOperationException>(() =>
            service.RecordTransactionAsync(request));

        Assert.Equal(errorCode, ex.Code);
        // CRITICAL: The pending intent must NOT have been completed; it must remain in the journal for safe recovery
        var pending = store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey);
        Assert.Single(pending);
        Assert.Equal(opId, pending[0].OperationId);
    }

    [Theory]
    [InlineData("validation.failed", "Validation error.")]
    [InlineData("inventory.insufficient_stock", "Stock unavailable.")]
    public async Task P12_W2_F002_Authoritative_Final_Rejection_Resolves_Pending_Intent(string errorCode, string errorMessage)
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var (gateway, controller) = ControllableGateway.Create();
        controller.OnCompleteSale = _ => Task.FromResult(Result<CompleteSaleResult>.Failure(errorCode, errorMessage));

        using var apiClient = CreateStubApiClient();
        var service = CreateTestService(gateway, apiClient, store);

        var opId = Guid.NewGuid();
        var request = CreateSampleSaleRequest(opId, "Customer Pintu");

        var ex = await Assert.ThrowsAsync<BackendOperationException>(() =>
            service.RecordTransactionAsync(request));

        Assert.Equal(errorCode, ex.Code);
        // Authoritative final business rejection MUST resolve and clear the journal
        var pending = store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey);
        Assert.Empty(pending);
    }

    // =========================================================================
    // P12-W2-F001: Bounded NOT_FOUND Recheck, SAME-ID Resend & Desktop Startup Recovery
    // =========================================================================

    [Fact]
    public async Task P12_W2_F001_Uncertain_Sale_Bounded_NotFound_Recheck_Permits_Safe_Same_Id_Resend()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var (gateway, controller) = ControllableGateway.Create();

        var committedSaleId = Guid.NewGuid();
        var attempts = 0;
        controller.OnCompleteSale = cmd =>
        {
            attempts++;
            if (attempts == 1)
            {
                // First call fails with transient network timeout -> marks sale uncertain
                return Task.FromResult(Result<CompleteSaleResult>.Failure("network.timeout", "Request timed out."));
            }

            // Second call succeeds on SAME-ID resend
            return Task.FromResult(Result<CompleteSaleResult>.Success(
                new CompleteSaleResult(committedSaleId, "INV-SAME-ID-1", 100m, 0m, false)));
        };

        // Status query returns NotFound for all queries (initial + 2 bounded rechecks)
        var statusQueries = 0;
        controller.OnQueryOperationStatus = opId =>
        {
            statusQueries++;
            return Task.FromResult(Result<OperationStatusResult>.Success(
                new OperationStatusResult(opId, false, null, null, null, false, Status: "NotFound")));
        };

        using var apiClient = CreateStubApiClient(_ => JsonResponse(new SaleDetailDto(
            committedSaleId, "INV-SAME-ID-1", DateTimeOffset.UtcNow, null, "Walk-in Customer",
            100m, 0m, 100m, SalePaymentMethod.Cash, 100m, 100m, 0m, null, null,
            [], [], [])));

        var service = CreateTestService(gateway, apiClient, store);
        var opId = Guid.NewGuid();
        var request = CreateSampleSaleRequest(opId, "Customer Pintu");

        // Attempt 1: Fails with network timeout and is marked uncertain
        var ex1 = await Assert.ThrowsAsync<BackendOperationException>(() =>
            service.RecordTransactionAsync(request));
        Assert.Equal("network.timeout", ex1.Code);
        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        // Attempt 2: Retried with the SAME ClientOperationId
        // Service runs bounded NotFound recheck (initial query + 2 rechecks = 3 status queries),
        // confirms absence on server, clears uncertain flag, and safely dispatches SAME-ID mutation!
        var record = await service.RecordTransactionAsync(request);

        Assert.NotNull(record);
        Assert.Equal("INV-SAME-ID-1", record.InvoiceNumber);
        Assert.Equal(committedSaleId, record.BackendSaleId);

        // Verification of contract
        Assert.Equal(2, controller.CompleteSaleCalls.Count);
        Assert.Equal(opId, controller.CompleteSaleCalls[0].ClientOperationId);
        Assert.Equal(opId, controller.CompleteSaleCalls[1].ClientOperationId);
        Assert.Equal(3, statusQueries); // Initial check + 2 bounded rechecks

        // Intent is now completed
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    [Fact]
    public async Task P12_W2_F001_Desktop_Startup_Recovers_Committed_Sale_From_Status()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        // Simulate lost response: pending intent was persisted to disk before previous process died
        var replayEnvelope = "{\"DraftId\":null,\"CustomerId\":null,\"Lines\":[]}";
        store.GetOrCreate(
            BackendTransactionService.SaleCheckoutIntentKey,
            "{\"payload\": true}",
            opId,
            "Sale",
            safeDisplayContext: "Customer Pintu",
            safeReplayEnvelope: replayEnvelope);

        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        // Setup fresh service on restart
        var (gateway, controller) = ControllableGateway.Create();
        controller.OnQueryOperationStatus = id => Task.FromResult(Result<OperationStatusResult>.Success(
            new OperationStatusResult(id, true, "Sale", saleId, "INV-RECOVER-1", WasCommitted: true, Status: "Succeeded")));

        using var apiClient = CreateStubApiClient(_ => JsonResponse(new SaleDetailDto(
            saleId, "INV-RECOVER-1", DateTimeOffset.UtcNow, null, "Customer Pintu",
            100m, 0m, 100m, SalePaymentMethod.Cash, 100m, 100m, 0m, null, null,
            [], [], [])));

        var restartService = CreateTestService(gateway, apiClient, store);

        // Startup recovery
        var recovered = await restartService.TryRecoverPendingSaleAsync();

        Assert.NotNull(recovered);
        Assert.Equal(saleId, recovered.BackendSaleId);
        Assert.Equal("INV-RECOVER-1", recovered.InvoiceNumber);

        // The journal entry is completed and removed
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    [Fact]
    public async Task P12_W2_F001_Desktop_Startup_Resolves_Final_Rejection_From_Status()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();

        // Pending intent persisted before process exit
        store.GetOrCreate(
            BackendTransactionService.SaleCheckoutIntentKey,
            "{\"payload\": true}",
            opId,
            "Sale",
            safeDisplayContext: "Customer Pintu");

        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        // On restart, server status reveals the sale was rejected with a final error
        var (gateway, controller) = ControllableGateway.Create();
        controller.OnQueryOperationStatus = id => Task.FromResult(Result<OperationStatusResult>.Success(
            new OperationStatusResult(id, true, "Sale", null, null, WasCommitted: false, Status: "Failed", ErrorCode: "inventory.insufficient_stock")));

        using var apiClient = CreateStubApiClient();
        var restartService = CreateTestService(gateway, apiClient, store);

        // Startup recovery: authoritative final rejection throws BackendOperationException with canonical code/message
        var ex = await Assert.ThrowsAsync<BackendOperationException>(() => restartService.TryRecoverPendingSaleAsync());
        Assert.Equal("inventory.insufficient_stock", ex.Code);
        Assert.Contains("Backend rejected the sale", ex.Message);

        // The journal entry was completed, freeing the terminal for new checkouts
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    [Fact]
    public async Task P12_W2_F001_Desktop_Startup_NotFound_Bounded_Recheck_Reconstitutes_Envelope_And_Resends_Same_OperationId()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var prodId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var invUnitId = Guid.NewGuid();

        // 1. Fresh process startup simulation:
        // Journal contains an uncommitted sale intent written before the crash.
        // RAM is completely empty (no in-memory request or cart).
        var replayEnvelope = new SafeSaleReplayEnvelope
        {
            ClientOperationId = opId,
            CustomerName = "Walk-in Customer",
            CustomerPhone = "03001234567",
            CashierName = "Abdullah, Owner",
            TotalAmount = 250m,
            Subtotal = 250m,
            DiscountAmount = 0m,
            PaymentMethod = "Cash",
            AmountReceived = 250m,
            Notes = "Replayed sale",
            PostingDate = new DateOnly(2026, 10, 16),
            Lines =
            [
                new SafeSaleReplayLine
                {
                    ProductId = prodId,
                    ProductUnitId = unitId,
                    SkuOrCode = "SKU-CABLE-01",
                    ProductName = "Power Cable 2m",
                    Quantity = 2m,
                    UnitPrice = 125m,
                    ListUnitPrice = 150m,
                    PriceOverrideUnitPrice = 125m,
                    PriceOverrideReason = "Contract discount",
                    InventoryUnitIds = [invUnitId]
                }
            ]
        };

        // Reconstitute sample to get the identical payload SHA-256
        var reconstitutedSample = BackendTransactionService.ReconstituteSaleRequest(replayEnvelope, opId);
        var payloadJson = JsonSerializer.Serialize(new
        {
            reconstitutedSample.DraftId,
            reconstitutedSample.DraftVersion,
            reconstitutedSample.CustomerId,
            reconstitutedSample.CustomerName,
            reconstitutedSample.CustomerPhone,
            reconstitutedSample.CashierName,
            reconstitutedSample.TotalAmount,
            reconstitutedSample.Subtotal,
            reconstitutedSample.DiscountAmount,
            reconstitutedSample.PaymentMethod,
            reconstitutedSample.AmountReceived,
            reconstitutedSample.ChangeReturned,
            reconstitutedSample.PaymentReference,
            reconstitutedSample.Notes,
            Items = reconstitutedSample.Items?.Select(item => new
            {
                item.BackendProductId,
                item.BackendProductUnitId,
                item.Quantity,
                item.UnitPrice,
                item.ListUnitPrice,
                item.PriceOverrideUnitPrice,
                item.PriceOverrideReason,
                item.InventoryUnitIds
            }).ToArray()
        });

        store.GetOrCreate(
            BackendTransactionService.SaleCheckoutIntentKey,
            payloadJson,
            opId,
            "Sale",
            safeDisplayContext: "Customer Walk-in",
            safeReplayEnvelope: JsonSerializer.Serialize(replayEnvelope));

        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        // 2. Gateway setup: Status query returns NotFound; bounded rechecks confirm Point-in-time absence
        var (gateway, controller) = ControllableGateway.Create();
        var statusQueryCount = 0;
        controller.OnQueryOperationStatus = id =>
        {
            statusQueryCount++;
            return Task.FromResult(Result<OperationStatusResult>.Success(
                new OperationStatusResult(id, false, null, null, null, false, Status: "NotFound")));
        };

        controller.OnCompleteSale = input =>
        {
            return Task.FromResult(Result<CompleteSaleResult>.Success(
                new CompleteSaleResult(saleId, "INV-REPLAY-SAME-ID", 250m, 0m, false)));
        };

        using var apiClient = CreateStubApiClient(_ => JsonResponse(new SaleDetailDto(
            saleId, "INV-REPLAY-SAME-ID", DateTimeOffset.UtcNow, null, "Walk-in Customer",
            250m, 0m, 250m, SalePaymentMethod.Cash, 250m, 250m, 0m, null, null,
            [], [], [])));

        var restartService = CreateTestService(gateway, apiClient, store);

        // 3. Startup recovery with empty RAM:
        // TryRecoverPendingSaleAsync executes bounded recheck (1 initial + 2 rechecks = 3 status queries),
        // deserializes SafeReplayEnvelope, reconstitutes RecordSaleRequest, reacquires authorization,
        // and safely resends with the SAME ClientOperationId!
        var recovered = await restartService.TryRecoverPendingSaleAsync();

        Assert.NotNull(recovered);
        Assert.Equal(saleId, recovered.BackendSaleId);
        Assert.Equal("INV-REPLAY-SAME-ID", recovered.InvoiceNumber);

        // Verification of invariant: exactly 1 mutation call with the SAME ClientOperationId!
        Assert.Single(controller.CompleteSaleCalls);
        var sentCall = controller.CompleteSaleCalls[0];
        Assert.Equal(opId, sentCall.ClientOperationId);
        Assert.Equal(3, statusQueryCount); // Initial check + 2 bounded rechecks

        // Verify fingerprint-significant fields and price overrides preserved
        Assert.Single(sentCall.Lines);
        Assert.Equal(prodId, sentCall.Lines[0].ProductId);
        Assert.Equal(unitId, sentCall.Lines[0].ProductUnitId);
        Assert.Equal(2m, sentCall.Lines[0].EnteredQuantity);
        Assert.Equal(150m, sentCall.Lines[0].ExpectedUnitPrice);
        Assert.Equal(125m, sentCall.Lines[0].PriceOverrideUnitPrice);
        Assert.Equal("Contract discount", sentCall.Lines[0].PriceOverrideReason);
        Assert.Equal(new[] { invUnitId }, sentCall.Lines[0].InventoryUnitIds);

        // Journal entry completed and removed
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    [Fact]
    public async Task P12_W2_F001_PosViewModel_Startup_Final_Rejection_Presents_Canonical_Error_Code_And_Message()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();

        store.GetOrCreate(
            BackendTransactionService.SaleCheckoutIntentKey,
            "{\"payload\": true}",
            opId,
            "Sale",
            safeDisplayContext: "Customer Pintu");

        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        var (gateway, controller) = ControllableGateway.Create();
        controller.OnQueryOperationStatus = id => Task.FromResult(Result<OperationStatusResult>.Success(
            new OperationStatusResult(id, true, "Sale", null, null, WasCommitted: false, Status: "Failed",
                ErrorCode: "inventory.insufficient_stock", ErrorMessage: "Insufficient stock for SKU-001.")));

        using var apiClient = CreateStubApiClient();
        var restartService = CreateTestService(gateway, apiClient, store);
        using var toastService = new TestToastService();

        var viewModel = new PosViewModel(
            toastService: toastService,
            transactionService: restartService,
            operationIntents: store);

        // Allow async startup pending check to complete
        await viewModel.RefreshPendingSaleOperationIdAsync();

        // Verifications:
        // 1. ScannerStatusMessage visibly displays canonical error code and message
        Assert.NotNull(viewModel.ScannerStatusMessage);
        Assert.Contains("inventory.insufficient_stock", viewModel.ScannerStatusMessage);
        Assert.Contains("Insufficient stock for SKU-001.", viewModel.ScannerStatusMessage);

        // 2. Toast notification displayed to the cashier
        Assert.Contains(toastService.Messages, m =>
            m.Message.Contains("inventory.insufficient_stock") && m.Tone == ToastTone.Warning);

        // 3. Pending operation id cleared and journal resolved
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    [Fact]
    public async Task P12_W2_F001_PosViewModel_Startup_Offline_Retains_Pending_OperationId_And_Restores_Cart()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();
        var prodId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        var replayEnvelope = new SafeSaleReplayEnvelope
        {
            ClientOperationId = opId,
            CustomerName = "Ahmed Khan",
            DiscountAmount = 25m,
            PaymentMethod = "Cash",
            Lines =
            [
                new SafeSaleReplayLine
                {
                    ProductId = prodId,
                    ProductUnitId = unitId,
                    SkuOrCode = "SKU-RESTORE-1",
                    ProductName = "Restored Cable",
                    Quantity = 3m,
                    UnitPrice = 80m,
                    ListUnitPrice = 100m,
                    PriceOverrideUnitPrice = 80m,
                    PriceOverrideReason = "Bulk discount"
                }
            ]
        };

        store.GetOrCreate(
            BackendTransactionService.SaleCheckoutIntentKey,
            "{\"payload\": true}",
            opId,
            "Sale",
            safeDisplayContext: "Customer Ahmed Khan",
            safeReplayEnvelope: JsonSerializer.Serialize(replayEnvelope));

        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        // Gateway offline: returns failure
        var (gateway, controller) = ControllableGateway.Create();
        controller.OnQueryOperationStatus = _ => Task.FromResult(Result<OperationStatusResult>.Failure(
            "network.offline", "Backend server is unreachable."));

        using var apiClient = CreateStubApiClient();
        var restartService = CreateTestService(gateway, apiClient, store);
        using var toastService = new TestToastService();

        var viewModel = new PosViewModel(
            toastService: toastService,
            transactionService: restartService,
            operationIntents: store);

        await viewModel.RefreshPendingSaleOperationIdAsync();

        // Verifications:
        // 1. Pending operation ID is retained in the POS workspace
        Assert.Single(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));

        // 2. CartItems populated from SafeReplayEnvelope
        Assert.Single(viewModel.CartItems);
        var restoredItem = viewModel.CartItems[0];
        Assert.Equal("SKU-RESTORE-1", restoredItem.Sku);
        Assert.Equal(3m, restoredItem.Quantity);
        Assert.Equal(80m, restoredItem.UnitPrice);
        Assert.True(restoredItem.IsPriceOverridden);
        Assert.Equal(80m, restoredItem.PriceOverrideUnitPrice);

        // 3. Customer and discount restored
        Assert.Equal("Ahmed Khan", viewModel.CustomerName);
        Assert.Equal(25m, viewModel.DiscountAmount);

        // 4. Scanner message indicates restored uncommitted sale
        Assert.Contains("Restored pending uncommitted sale", viewModel.ScannerStatusMessage);
    }

    [Fact]
    public async Task P12_W2_F001_Desktop_Startup_FreshStart_NoJournal_CompletesWithoutError()
    {
        var path = CreateTempJournalPath(); // File not created on disk
        var store = new FileClientOperationIntentStore(path);
        var (gateway, _) = ControllableGateway.Create();
        using var apiClient = CreateStubApiClient();
        var service = CreateTestService(gateway, apiClient, store);

        var recovered = await service.TryRecoverPendingSaleAsync();

        Assert.Null(recovered);
        Assert.Empty(store.FindPendingByPrefix(BackendTransactionService.SaleCheckoutIntentKey));
    }

    [Fact]
    public void P12_W2_F003_Canonical_Storage_Failure_Fails_Closed_Preserves_Legacy_Bytes_And_Prohibits_Send()
    {
        var localAppDataDir = Path.Combine(_tempDirectory, "local_app_data_fail");
        var legacyFileDir = Path.Combine(localAppDataDir, FileClientOperationIntentStore.DefaultFolderName, "Desktop");
        Directory.CreateDirectory(legacyFileDir);
        var legacyFilePath = Path.Combine(legacyFileDir, FileClientOperationIntentStore.DefaultFileName);

        var existingOpId = Guid.NewGuid();
        var legacyOriginalContent = JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    OperationKey = "sale:checkout:fail-close-001",
                    PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("legacy-payload-data"))),
                    OperationId = existingOpId,
                    OperationType = "Sale",
                    FingerprintVersion = "v1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Status = "Pending",
                    SafeDisplayContext = "Customer Spinelli",
                    SafeReplayEnvelope = "{\"Lines\":[]}"
                }
            }
        });
        File.WriteAllText(legacyFilePath, legacyOriginalContent);

        // Create a file where the canonical parent directory needs to be, causing Directory.CreateDirectory to fail
        var commonAppDataDir = Path.Combine(_tempDirectory, "common_app_data_fail");
        Directory.CreateDirectory(commonAppDataDir);
        var blockedParent = Path.Combine(commonAppDataDir, FileClientOperationIntentStore.DefaultFolderName);
        File.WriteAllText(blockedParent, "BLOCKING_FILE");

        // Attempting to resolve canonical path when canonical machine-root storage cannot be established MUST fail-closed:
        // throws InvalidOperationException and prohibits falling back to per-user LocalApplicationData
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FileClientOperationIntentStore.ResolveCanonicalPath(
                null,
                commonAppDataOverride: commonAppDataDir,
                localAppDataOverride: localAppDataDir));

        Assert.Contains("cannot be established", ex.Message);
        Assert.Contains("per-user authority is prohibited", ex.Message);

        // Verify that the legacy file and its original bytes/IDs are 100% UNTOUCHED
        Assert.True(File.Exists(legacyFilePath), "Legacy file must remain on disk untouched.");
        Assert.False(File.Exists($"{legacyFilePath}.migrated"), "Legacy file must NOT have been marked as migrated.");
        var legacyCurrentContent = File.ReadAllText(legacyFilePath);
        Assert.Equal(legacyOriginalContent, legacyCurrentContent);
        Assert.Contains(existingOpId.ToString(), legacyCurrentContent);
    }

    [Fact]
    public void P12_W2_PAR003_ActualRuntime_StateDir_Migrates_Legacy_Unresolved_Intents_To_Canonical_Storage()
    {
        var ownedRoot = Path.Combine(_tempDirectory, "actual_runtime_migration");
        var localAppDataDir = Path.Combine(ownedRoot, "LocalAppData");
        var stateDir = Path.Combine(ownedRoot, "StateDir");

        var legacyFileDir = Path.Combine(localAppDataDir, FileClientOperationIntentStore.DefaultFolderName, "Desktop");
        Directory.CreateDirectory(legacyFileDir);
        var legacyFilePath = Path.Combine(legacyFileDir, FileClientOperationIntentStore.DefaultFileName);

        var existingOpId = Guid.NewGuid();
        var legacyDoc = new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    OperationKey = "sale:draft:runtime-legacy-001",
                    PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload-data"))),
                    OperationId = existingOpId,
                    OperationType = "Sale",
                    FingerprintVersion = "v1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Status = "Pending",
                    SafeDisplayContext = "Customer Runtime",
                    SafeReplayEnvelope = "{\"items\":[]}"
                }
            }
        };
        File.WriteAllText(legacyFilePath, JsonSerializer.Serialize(legacyDoc));

        var prevEnv = Environment.GetEnvironmentVariable("EDGE_RETAILS_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_STATE_DIR", stateDir);

            // Invoke without commonAppDataOverride to exercise actual runtime path resolution
            var canonicalPath = FileClientOperationIntentStore.ResolveCanonicalPath(
                path: null,
                commonAppDataOverride: null,
                localAppDataOverride: localAppDataDir);

            var expectedCanonicalPath = Path.GetFullPath(Path.Combine(
                stateDir,
                "Desktop",
                FileClientOperationIntentStore.DefaultFileName));

            Assert.Equal(expectedCanonicalPath, canonicalPath);
            Assert.True(File.Exists(canonicalPath), "Canonical journal file must exist after migration in actual runtime path resolution.");
            Assert.True(File.Exists($"{legacyFilePath}.migrated"), "Legacy journal file must have been renamed to .migrated.");

            var migratedStore = new FileClientOperationIntentStore(canonicalPath);
            var pending = migratedStore.FindPendingByPrefix("sale:draft:");
            Assert.Single(pending);
            Assert.Equal(existingOpId, pending[0].OperationId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_STATE_DIR", prevEnv);
        }
    }

    [Fact]
    public void P12_W2_PAR003_ActualRuntime_StateDir_Storage_Failure_Fails_Closed_Preserves_Legacy_Bytes()
    {
        var ownedRoot = Path.Combine(_tempDirectory, "actual_runtime_fail_closed");
        var localAppDataDir = Path.Combine(ownedRoot, "LocalAppData");
        var stateDir = Path.Combine(ownedRoot, "StateDir");

        var legacyFileDir = Path.Combine(localAppDataDir, FileClientOperationIntentStore.DefaultFolderName, "Desktop");
        Directory.CreateDirectory(legacyFileDir);
        var legacyFilePath = Path.Combine(legacyFileDir, FileClientOperationIntentStore.DefaultFileName);

        var existingOpId = Guid.NewGuid();
        var legacyContent = JsonSerializer.Serialize(new
        {
            Version = 2,
            Entries = new[]
            {
                new
                {
                    OperationKey = "sale:draft:fail-001",
                    PayloadSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload-data"))),
                    OperationId = existingOpId,
                    OperationType = "Sale",
                    FingerprintVersion = "v1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    Status = "Pending",
                    SafeDisplayContext = "Customer Blocked",
                    SafeReplayEnvelope = "{\"items\":[]}"
                }
            }
        });
        File.WriteAllText(legacyFilePath, legacyContent);

        // Block canonical directory creation by creating a file with the name "Desktop" inside stateDir
        Directory.CreateDirectory(stateDir);
        var blockedDesktop = Path.Combine(stateDir, "Desktop");
        File.WriteAllText(blockedDesktop, "BLOCKING_FILE");

        var prevEnv = Environment.GetEnvironmentVariable("EDGE_RETAILS_STATE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_STATE_DIR", stateDir);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                FileClientOperationIntentStore.ResolveCanonicalPath(
                    path: null,
                    commonAppDataOverride: null,
                    localAppDataOverride: localAppDataDir));

            Assert.Contains("cannot be established", ex.Message);
            Assert.Contains("per-user authority is prohibited", ex.Message);

            // Legacy file must remain completely untouched on disk
            Assert.True(File.Exists(legacyFilePath));
            Assert.False(File.Exists($"{legacyFilePath}.migrated"));
            Assert.Equal(legacyContent, File.ReadAllText(legacyFilePath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDGE_RETAILS_STATE_DIR", prevEnv);
        }
    }

    // =========================================================================
    // P12-W2-F004: All 9 Fields of ClientOperationIntent Preserved
    // =========================================================================

    [Fact]
    public void P12_W2_F004_ClientOperationIntent_Exposes_All_V2_Fields_Including_SafeReplayEnvelope()
    {
        var path = CreateTempJournalPath();
        var store = new FileClientOperationIntentStore(path);
        var opId = Guid.NewGuid();
        var payload = "{\"items\": [{\"sku\": \"A\"}]}";
        var replayEnvelope = "{\"DraftId\":\"d1\",\"Lines\":[]}";

        store.GetOrCreate(
            "sale:checkout:001",
            payload,
            opId,
            operationType: "Sale",
            safeDisplayContext: "Customer Pintu",
            safeReplayEnvelope: replayEnvelope);

        var pending = store.FindPendingByPrefix("sale:checkout:");
        var entry = Assert.Single(pending);

        Assert.Equal("sale:checkout:001", entry.OperationKey);
        Assert.Equal(opId, entry.OperationId);
        Assert.Equal("Sale", entry.OperationType);
        Assert.Equal("Pending", entry.Status);
        Assert.Equal("Customer Pintu", entry.SafeDisplayContext);
        Assert.Equal(replayEnvelope, entry.SafeReplayEnvelope);
        Assert.Equal("v1", entry.FingerprintVersion);
        Assert.NotNull(entry.PayloadSha256);
        Assert.NotNull(entry.StartedAtUtc);
    }

    // =========================================================================
    // Test Helpers & Doubles
    // =========================================================================

    private static RecordSaleRequest CreateSampleSaleRequest(Guid operationId, string customerName = "Pintu")
    {
        var productId = Guid.CreateVersion7();
        var unitId = Guid.CreateVersion7();
        return new RecordSaleRequest
        {
            ClientOperationId = operationId,
            CustomerName = customerName,
            TotalAmount = 100m,
            AmountReceived = 100m,
            PaymentMethod = PaymentMethod.Cash,
            Items =
            [
                new SaleTransactionItem
                {
                    ProductId = productId.ToString("D"),
                    BackendProductId = productId,
                    BackendProductUnitId = unitId,
                    ProductName = "Sample Product",
                    Sku = "SKU-001",
                    UnitPrice = 100m,
                    ListUnitPrice = 100m,
                    Quantity = 1m,
                    LineTotal = 100m
                }
            ]
        };
    }

    private static BackendTransactionService CreateTestService(
        IApplicationGateway gateway,
        DesktopApiClient apiClient,
        IClientOperationIntentStore store,
        Guid? actorId = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gateway);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var actor = actorId ?? Guid.CreateVersion7();
        return new BackendTransactionService(scopeFactory, () => actor, apiClient, store);
    }

    private static DesktopApiClient CreateStubApiClient(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        responder ??= _ => JsonResponse(new SaleDetailDto(
            Guid.NewGuid(), "INV-TEST-001", DateTimeOffset.UtcNow, null, "Walk-in Customer",
            100m, 0m, 100m, SalePaymentMethod.Cash, 100m, 100m, 0m, null, null,
            [], [], []));

        var handler = new StubHttpMessageHandler(responder);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:55437")
        };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value))
    };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    public class ControllableGateway : DispatchProxy
    {
        public Func<CompleteSaleCommand, Task<Result<CompleteSaleResult>>>? OnCompleteSale { get; set; }
        public Func<Guid, Task<Result<OperationStatusResult>>>? OnQueryOperationStatus { get; set; }
        public List<CompleteSaleCommand> CompleteSaleCalls { get; } = [];
        public List<Guid> QueryStatusCalls { get; } = [];

        public static (IApplicationGateway Gateway, ControllableGateway Controller) Create()
        {
            var proxy = DispatchProxy.Create<IApplicationGateway, ControllableGateway>();
            var controller = (ControllableGateway)(object)proxy;
            return (proxy, controller);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IApplicationGateway.CompleteSaleAsync))
            {
                var cmd = (CompleteSaleCommand)args![0]!;
                CompleteSaleCalls.Add(cmd);
                if (OnCompleteSale is not null)
                {
                    return OnCompleteSale(cmd);
                }

                return Task.FromResult(Result<CompleteSaleResult>.Success(
                    new CompleteSaleResult(Guid.NewGuid(), "INV-101", 100m, 0m, false)));
            }

            if (targetMethod?.Name == nameof(IApplicationGateway.QueryOperationStatusAsync))
            {
                var opId = (Guid)args![0]!;
                QueryStatusCalls.Add(opId);
                if (OnQueryOperationStatus is not null)
                {
                    return OnQueryOperationStatus(opId);
                }

                return Task.FromResult(Result<OperationStatusResult>.Success(
                    new OperationStatusResult(opId, false, null, null, null, false, Status: "NotFound")));
            }

            return null;
        }
    }

    private sealed class TestToastService : IToastService, IDisposable
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
            => _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));

        public void Dispose() => _messages.Clear();
    }
}
