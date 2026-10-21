using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using System.Windows.Input;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase12Wave3DurableClientOwnershipTests : IDisposable
{
    private readonly string _tempDirectory;

    public Phase12Wave3DurableClientOwnershipTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"EdgeRetails_W3_Ownership_{Guid.NewGuid():N}");
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
            // Ignore cleanup failure in test teardown
        }
    }

    private string CreateJournalPath() => Path.Combine(_tempDirectory, "operation-intents.json");

    // =========================================================================
    // 1. NewPurchaseViewModel Durable Ownership Tests
    // =========================================================================

    [Fact]
    public async Task NewPurchase_ColdRestart_ReusesOperationId_OnSamePayload()
    {
        var journalPath = CreateJournalPath();
        var supplierId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        var createdPurchases = new List<(Guid SupplierId, string Invoice, Guid OperationId)>();
        var simulateFailure = true;

        var backend = PurchasingMockProxy.Create((method, args) =>
        {
            if (method.Name == "get_ReceivesStockImmediately")
            {
                return false;
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.GetSuppliersAsync))
            {
                return Task.FromResult<IReadOnlyList<BackendSupplierOption>>([new BackendSupplierOption(supplierId, "Mock Supplier", "Lahore")]);
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.GetCatalogAsync))
            {
                return Task.FromResult<IReadOnlyList<BackendPurchaseCatalogItem>>([
                    new BackendPurchaseCatalogItem(productId, productUnitId, "Test Product", "SKU-01", "Category", "Pcs",
                        100m, 50m, 75m, 1m, false, false, false)
                ]);
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.CreatePurchaseAsync))
            {
                var supId = (Guid)args[0]!;
                var invNum = (string)args[1]!;
                var pDate = (DateTime)args[2]!;
                var opId = (Guid)args[6]!;
                createdPurchases.Add((supId, invNum, opId));

                if (simulateFailure)
                {
                    throw new DesktopApiException("network.timeout", "Simulated lost response.");
                }

                return Task.FromResult(new PurchaseRecord
                {
                    BackendPurchaseId = Guid.NewGuid(),
                    BackendSupplierId = supId,
                    PurchaseNumber = "PO-" + invNum,
                    Supplier = "Mock Supplier",
                    InvoiceNumber = invNum,
                    Date = pDate,
                    Items = []
                });
            }

            return null;
        });

        var firstStore = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();
        var vm1 = CreateNewPurchaseVm(backend, toastService, firstStore);
        await PopulateAndSavePurchaseAsync(vm1, supplierId, "INV-1001", "Initial purchase note", 10m, 50m);

        Assert.Single(createdPurchases);
        var originalOperationId = createdPurchases[0].OperationId;
        Assert.NotEqual(Guid.Empty, originalOperationId);

        // Verify pending intent exists on disk
        var pendingBefore = firstStore.FindPendingByPrefix("purchase:create");
        Assert.Single(pendingBefore);
        Assert.Equal(originalOperationId, pendingBefore[0].OperationId);

        // Simulate cold process restart with new VM and new store instance
        var restartStore = new FileClientOperationIntentStore(journalPath);
        simulateFailure = false;
        var vmRestart = CreateNewPurchaseVm(backend, toastService, restartStore);
        await PopulateAndSavePurchaseAsync(vmRestart, supplierId, "INV-1001", "Initial purchase note", 10m, 50m);

        Assert.Equal(2, createdPurchases.Count);
        Assert.Equal(originalOperationId, createdPurchases[1].OperationId);

        // Authoritative success clears pending intent from disk
        var pendingAfter = restartStore.FindPendingByPrefix("purchase:create");
        Assert.Empty(pendingAfter);
    }

    [Fact]
    public async Task NewPurchase_PendingIntent_RejectsChangedPayload_FailClosed()
    {
        var journalPath = CreateJournalPath();
        var supplierId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        var createdPurchases = new List<(Guid SupplierId, string Invoice, Guid OperationId)>();

        var backend = PurchasingMockProxy.Create((method, args) =>
        {
            if (method.Name == "get_ReceivesStockImmediately")
            {
                return false;
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.GetSuppliersAsync))
            {
                return Task.FromResult<IReadOnlyList<BackendSupplierOption>>([new BackendSupplierOption(supplierId, "Mock Supplier", "Lahore")]);
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.GetCatalogAsync))
            {
                return Task.FromResult<IReadOnlyList<BackendPurchaseCatalogItem>>([
                    new BackendPurchaseCatalogItem(productId, productUnitId, "Test Product", "SKU-01", "Category", "Pcs",
                        100m, 50m, 75m, 1m, false, false, false)
                ]);
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.CreatePurchaseAsync))
            {
                var supId = (Guid)args[0]!;
                var invNum = (string)args[1]!;
                var opId = (Guid)args[6]!;
                createdPurchases.Add((supId, invNum, opId));
                throw new DesktopApiException("network.timeout", "Simulated lost response.");
            }

            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();
        var vm1 = CreateNewPurchaseVm(backend, toastService, store);
        await PopulateAndSavePurchaseAsync(vm1, supplierId, "INV-1001", "Note A", 10m, 50m);

        Assert.Single(createdPurchases);

        // User edits payload while previous operation remains unresolved
        var vmEdited = CreateNewPurchaseVm(backend, toastService, store);
        await PopulateAndSavePurchaseAsync(vmEdited, supplierId, "INV-1001-EDITED", "Note B", 10m, 50m);

        // Verify backend was NOT called with changed payload
        Assert.Single(createdPurchases);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("unresolved outcome and different details"));
    }

    [Fact]
    public async Task NewPurchase_AuthoritativeCompletion_ClearsIntent_And_AllowsFreshSubsequentOperation()
    {
        var journalPath = CreateJournalPath();
        var supplierId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        var createdPurchases = new List<(Guid SupplierId, string Invoice, Guid OperationId)>();

        var backend = PurchasingMockProxy.Create((method, args) =>
        {
            if (method.Name == "get_ReceivesStockImmediately")
            {
                return false;
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.GetSuppliersAsync))
            {
                return Task.FromResult<IReadOnlyList<BackendSupplierOption>>([new BackendSupplierOption(supplierId, "Mock Supplier", "Lahore")]);
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.GetCatalogAsync))
            {
                return Task.FromResult<IReadOnlyList<BackendPurchaseCatalogItem>>([
                    new BackendPurchaseCatalogItem(productId, productUnitId, "Test Product", "SKU-01", "Category", "Pcs",
                        100m, 50m, 75m, 1m, false, false, false)
                ]);
            }
            if (method.Name == nameof(IBackendPurchasingInventoryService.CreatePurchaseAsync))
            {
                var supId = (Guid)args[0]!;
                var invNum = (string)args[1]!;
                var pDate = (DateTime)args[2]!;
                var opId = (Guid)args[6]!;
                createdPurchases.Add((supId, invNum, opId));

                return Task.FromResult(new PurchaseRecord
                {
                    BackendPurchaseId = Guid.NewGuid(),
                    BackendSupplierId = supId,
                    PurchaseNumber = "PO-" + invNum,
                    Supplier = "Mock Supplier",
                    InvoiceNumber = invNum,
                    Date = pDate,
                    Items = []
                });
            }

            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        // Operation 1 completes
        var vm1 = CreateNewPurchaseVm(backend, toastService, store);
        await PopulateAndSavePurchaseAsync(vm1, supplierId, "INV-001", "Note 1", 5m, 100m);
        Assert.Single(createdPurchases);
        var op1Id = createdPurchases[0].OperationId;

        // Operation 2 is legitimately separate
        var vm2 = CreateNewPurchaseVm(backend, toastService, store);
        await PopulateAndSavePurchaseAsync(vm2, supplierId, "INV-002", "Note 2", 8m, 120m);
        Assert.Equal(2, createdPurchases.Count);
        var op2Id = createdPurchases[1].OperationId;

        Assert.NotEqual(op1Id, op2Id);
        Assert.Empty(store.FindPendingByPrefix("purchase:create"));
    }

    // =========================================================================
    // 2. PurchaseReturnViewModel Durable Ownership Tests
    // =========================================================================

    [Fact]
    public async Task PurchaseReturn_ColdRestart_ReusesOperationId_OnSamePayload()
    {
        var journalPath = CreateJournalPath();
        var purchaseId = Guid.NewGuid();
        var purchaseItemId = Guid.NewGuid();

        var purchase = CreateTestPurchase(purchaseId, purchaseItemId);
        var returnCalls = new List<(PurchaseRecord Purchase, Guid OperationId)>();
        var simulateFailure = true;

        var backend = PurchasingMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendPurchasingInventoryService.ReturnPurchaseAsync))
            {
                var p = (PurchaseRecord)args[0]!;
                var opId = (Guid)args[4]!;
                returnCalls.Add((p, opId));

                if (simulateFailure)
                {
                    throw new DesktopApiException("network.timeout", "Simulated lost response.");
                }

                return Task.FromResult(200m);
            }
            return null;
        });

        var store1 = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();
        var vm1 = new PurchaseReturnViewModel(
            purchase,
            toastService,
            close: () => { },
            backendService: backend,
            operationIntents: store1);

        vm1.Lines[0].ReturnQuantity = 2m;
        vm1.Reason = "Defective batch";
        vm1.Note = "Return note 1";

        var confirmMethod = typeof(PurchaseReturnViewModel).GetMethod("ConfirmAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)confirmMethod.Invoke(vm1, null)!;

        Assert.Single(returnCalls);
        var originalOperationId = returnCalls[0].OperationId;
        Assert.NotEqual(Guid.Empty, originalOperationId);

        // Cold restart with new store instance
        var restartStore = new FileClientOperationIntentStore(journalPath);
        simulateFailure = false;
        var vmRestart = new PurchaseReturnViewModel(
            purchase,
            toastService,
            close: () => { },
            backendService: backend,
            operationIntents: restartStore);

        vmRestart.Lines[0].ReturnQuantity = 2m;
        vmRestart.Reason = "Defective batch";
        vmRestart.Note = "Return note 1";

        await (Task)confirmMethod.Invoke(vmRestart, null)!;

        Assert.Equal(2, returnCalls.Count);
        Assert.Equal(originalOperationId, returnCalls[1].OperationId);
        Assert.Empty(restartStore.FindPendingByPrefix($"purchase:return:{purchaseId:D}"));
    }

    [Fact]
    public async Task PurchaseReturn_PendingIntent_RejectsChangedPayload_FailClosed()
    {
        var journalPath = CreateJournalPath();
        var purchaseId = Guid.NewGuid();
        var purchaseItemId = Guid.NewGuid();
        var purchase = CreateTestPurchase(purchaseId, purchaseItemId);

        var returnCalls = new List<(PurchaseRecord Purchase, Guid OperationId)>();

        var backend = PurchasingMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendPurchasingInventoryService.ReturnPurchaseAsync))
            {
                var p = (PurchaseRecord)args[0]!;
                var opId = (Guid)args[4]!;
                returnCalls.Add((p, opId));
                throw new DesktopApiException("network.timeout", "Simulated lost response.");
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();
        var vm1 = new PurchaseReturnViewModel(
            purchase,
            toastService,
            close: () => { },
            backendService: backend,
            operationIntents: store);

        vm1.Lines[0].ReturnQuantity = 2m;
        vm1.Reason = "Defective batch";

        var confirmMethod = typeof(PurchaseReturnViewModel).GetMethod("ConfirmAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)confirmMethod.Invoke(vm1, null)!;

        Assert.Single(returnCalls);

        // Edit return quantity while pending
        var vmEdited = new PurchaseReturnViewModel(
            purchase,
            toastService,
            close: () => { },
            backendService: backend,
            operationIntents: store);

        vmEdited.Lines[0].ReturnQuantity = 4m; // changed!
        vmEdited.Reason = "Defective batch";

        await (Task)confirmMethod.Invoke(vmEdited, null)!;

        Assert.Single(returnCalls);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("unresolved outcome and different details"));
    }

    // =========================================================================
    // 3. ThakaAddMaterialViewModel Durable Ownership Tests
    // =========================================================================

    [Fact]
    public async Task ThakaAddMaterial_ColdRestart_ReusesOperationId_OnSamePayload()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = CreateThakaProject(projectId);

        var issuedMaterials = new List<(Guid ProjectId, PosProductItemViewModel Product, decimal Quantity, Guid OperationId)>();
        var simulateFailure = true;

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.IssueMaterialAsync))
            {
                var p = (ThakaProjectListItemViewModel)args[0]!;
                var prd = (PosProductItemViewModel)args[1]!;
                var qty = (decimal)args[2]!;
                var opId = (Guid)args[4]!;
                issuedMaterials.Add((p.BackendProjectId!.Value, prd, qty, opId));

                if (simulateFailure)
                {
                    throw new DesktopApiException("network.timeout", "Simulated timeout");
                }

                return Task.FromResult(new BackendThakaIssueResult("CH-100", qty * prd.Price));
            }
            return null;
        });

        var store1 = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm1 = new ThakaAddMaterialViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store1);

        var testProduct = CreatePosProduct("Steel Pipe", 500m);
        vm1.Products.Add(new ThakaMaterialProductOption(testProduct));
        vm1.SelectedProduct = vm1.Products[0];
        vm1.Quantity = 3m;

        var issueMethod = typeof(ThakaAddMaterialViewModel).GetMethod("IssueMaterialAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)issueMethod.Invoke(vm1, null)!;

        Assert.Single(issuedMaterials);
        var originalOpId = issuedMaterials[0].OperationId;
        Assert.NotEqual(Guid.Empty, originalOpId);

        // Cold restart with new VM and store
        var restartStore = new FileClientOperationIntentStore(journalPath);
        simulateFailure = false;

        var vmRestart = new ThakaAddMaterialViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: restartStore);

        vmRestart.Products.Add(new ThakaMaterialProductOption(testProduct));
        vmRestart.SelectedProduct = vmRestart.Products[0];
        vmRestart.Quantity = 3m;

        await (Task)issueMethod.Invoke(vmRestart, null)!;

        Assert.Equal(2, issuedMaterials.Count);
        Assert.Equal(originalOpId, issuedMaterials[1].OperationId);
        Assert.Empty(restartStore.FindPendingByPrefix($"thaka:material-issue:{projectId:D}"));
    }

    [Fact]
    public async Task ThakaAddMaterial_PendingIntent_RejectsChangedPayload_FailClosed()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = CreateThakaProject(projectId);

        var issuedMaterials = new List<(Guid ProjectId, PosProductItemViewModel Product, decimal Quantity, Guid OperationId)>();

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.IssueMaterialAsync))
            {
                var p = (ThakaProjectListItemViewModel)args[0]!;
                var prd = (PosProductItemViewModel)args[1]!;
                var qty = (decimal)args[2]!;
                var opId = (Guid)args[4]!;
                issuedMaterials.Add((p.BackendProjectId!.Value, prd, qty, opId));
                throw new DesktopApiException("network.timeout", "Simulated timeout");
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm1 = new ThakaAddMaterialViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store);

        var testProduct = CreatePosProduct("Cement Bag", 1200m);
        vm1.Products.Add(new ThakaMaterialProductOption(testProduct));
        vm1.SelectedProduct = vm1.Products[0];
        vm1.Quantity = 5m;

        var issueMethod = typeof(ThakaAddMaterialViewModel).GetMethod("IssueMaterialAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)issueMethod.Invoke(vm1, null)!;

        Assert.Single(issuedMaterials);

        // Change quantity while unresolved
        var vmEdited = new ThakaAddMaterialViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store);

        vmEdited.Products.Add(new ThakaMaterialProductOption(testProduct));
        vmEdited.SelectedProduct = vmEdited.Products[0];
        vmEdited.Quantity = 10m; // Changed!

        await (Task)issueMethod.Invoke(vmEdited, null)!;

        Assert.Single(issuedMaterials);
        Assert.True(vmEdited.HasValidationMessage);
        Assert.Contains("unresolved outcome and different details", vmEdited.ValidationMessage!);
    }

    // =========================================================================
    // 4. ThakaWorkspaceViewModel (Material Reversal) Durable Ownership Tests
    // =========================================================================

    [Fact]
    public async Task ThakaMaterialReversal_ColdRestart_ReusesOperationId()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = CreateThakaProject(projectId);
        var issueId = Guid.NewGuid();

        var reversedMaterials = new List<(Guid ProjectId, Guid IssueId, Guid OperationId)>();
        var simulateFailure = true;

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.ReverseMaterialAsync))
            {
                var p = (ThakaProjectListItemViewModel)args[0]!;
                var issId = (Guid)args[1]!;
                var opId = (Guid)args[3]!;
                reversedMaterials.Add((p.BackendProjectId!.Value, issId, opId));

                if (simulateFailure)
                {
                    throw new DesktopApiException("network.timeout", "Simulated timeout");
                }

                return Task.CompletedTask;
            }
            if (method.Name == nameof(IBackendThakaService.GetWorkspaceAsync))
            {
                return Task.FromResult(new BackendThakaWorkspaceSnapshot(project, [], []));
            }
            return null;
        });

        var store1 = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm1 = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store1);

        var entry = new MaterialLedgerEntry
        {
            BackendMaterialIssueId = issueId,
            ChallanNumber = "CH-001",
            ProductName = "Wire Roll",
            Quantity = 2m,
            Rate = 450m,
            TotalValue = 900m
        };
        vm1.MaterialLedger.Add(entry);
        vm1.SelectedMaterial = entry;

        var reverseMethod = typeof(ThakaWorkspaceViewModel).GetMethod("ReverseSelectedMaterialAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)reverseMethod.Invoke(vm1, null)!;

        Assert.Single(reversedMaterials);
        var originalOpId = reversedMaterials[0].OperationId;
        Assert.NotEqual(Guid.Empty, originalOpId);

        // Cold restart
        var restartStore = new FileClientOperationIntentStore(journalPath);
        simulateFailure = false;

        var vmRestart = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: restartStore);

        vmRestart.MaterialLedger.Add(entry);
        vmRestart.SelectedMaterial = entry;

        await (Task)reverseMethod.Invoke(vmRestart, null)!;

        Assert.Equal(2, reversedMaterials.Count);
        Assert.Equal(originalOpId, reversedMaterials[1].OperationId);
        Assert.Empty(restartStore.FindPendingByPrefix($"thaka:material-reversal:{issueId:D}"));
    }

    [Fact]
    public async Task ThakaMaterialReversal_DefinitiveRejection_CompletesIntent_While_401_Retains()
    {
        var journalPath = CreateJournalPath();
        var projectId = Guid.NewGuid();
        var project = CreateThakaProject(projectId);
        var issueId = Guid.NewGuid();

        var throwStatusCode = HttpStatusCode.Unauthorized;

        var backend = ThakaMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendThakaService.ReverseMaterialAsync))
            {
                throw new DesktopApiException("thaka.reversal_rejected", "Simulated rejection", throwStatusCode);
            }
            if (method.Name == nameof(IBackendThakaService.GetWorkspaceAsync))
            {
                return Task.FromResult(new BackendThakaWorkspaceSnapshot(project, [], []));
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm = new ThakaWorkspaceViewModel(
            project,
            toastService: toastService,
            backendService: backend,
            operationIntents: store);

        var entry = new MaterialLedgerEntry
        {
            BackendMaterialIssueId = issueId,
            ChallanNumber = "CH-002"
        };
        vm.MaterialLedger.Add(entry);
        vm.SelectedMaterial = entry;

        var reverseMethod = typeof(ThakaWorkspaceViewModel).GetMethod("ReverseSelectedMaterialAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 401 Unauthorized must NOT complete the intent
        await (Task)reverseMethod.Invoke(vm, null)!;
        var pending = store.FindPendingByPrefix($"thaka:material-reversal:{issueId:D}");
        Assert.Single(pending);

        // Definitive 400 Bad Request MUST complete the intent without rethrowing
        throwStatusCode = HttpStatusCode.BadRequest;
        await (Task)reverseMethod.Invoke(vm, null)!;

        Assert.Empty(store.FindPendingByPrefix($"thaka:material-reversal:{issueId:D}"));
    }

    // =========================================================================
    // 5. SupplierDetailViewModel (Finance) Durable Ownership Tests
    // =========================================================================

    [Fact]
    public async Task SupplierPayment_ColdRestart_ReusesOperationId_OnSamePayload()
    {
        var journalPath = CreateJournalPath();
        var supplierId = Guid.NewGuid();
        var supplier = CreateSupplierRecord(supplierId);

        var createdPayments = new List<(Guid SupplierId, decimal Amount, SupplierPaymentPurpose Purpose, Guid OperationId)>();
        var simulateFailure = true;

        var opsService = OperationsMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetSupplierWorkspaceAsync))
            {
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(supplierId, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
            }
            if (method.Name == nameof(IBackendOperationsService.CreateSupplierPaymentAsync))
            {
                var sId = (Guid)args[0]!;
                var amt = (decimal)args[1]!;
                var purp = (SupplierPaymentPurpose)args[2]!;
                var opId = (Guid)args[4]!;
                createdPayments.Add((sId, amt, purp, opId));

                if (simulateFailure)
                {
                    throw new DesktopApiException("network.timeout", "Simulated timeout");
                }

                return Task.CompletedTask;
            }
            return null;
        });

        var store1 = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm1 = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: opsService,
            operationIntents: store1);

        vm1.TransactionAmount = 15000m;
        vm1.SelectedMethod = SupplierSettlementMethod.CashDrawer;
        vm1.ExternalReference = "REF-001";
        vm1.TransactionNote = "Payment note";

        var postMethod = typeof(SupplierDetailViewModel).GetMethod("PostPaymentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)postMethod.Invoke(vm1, [SupplierPaymentPurpose.Settlement])!;

        Assert.Single(createdPayments);
        var originalOpId = createdPayments[0].OperationId;
        Assert.NotEqual(Guid.Empty, originalOpId);

        // Cold restart
        var restartStore = new FileClientOperationIntentStore(journalPath);
        simulateFailure = false;

        var vmRestart = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: opsService,
            operationIntents: restartStore);

        vmRestart.TransactionAmount = 15000m;
        vmRestart.SelectedMethod = SupplierSettlementMethod.CashDrawer;
        vmRestart.ExternalReference = "REF-001";
        vmRestart.TransactionNote = "Payment note";

        await (Task)postMethod.Invoke(vmRestart, [SupplierPaymentPurpose.Settlement])!;

        Assert.Equal(2, createdPayments.Count);
        Assert.Equal(originalOpId, createdPayments[1].OperationId);
        Assert.Empty(restartStore.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Settlement"));
    }

    [Fact]
    public async Task SupplierPayment_PendingIntent_RejectsChangedPayload_FailClosed()
    {
        var journalPath = CreateJournalPath();
        var supplierId = Guid.NewGuid();
        var supplier = CreateSupplierRecord(supplierId);

        var createdPayments = new List<(Guid SupplierId, decimal Amount, SupplierPaymentPurpose Purpose, Guid OperationId)>();

        var opsService = OperationsMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetSupplierWorkspaceAsync))
            {
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(supplierId, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
            }
            if (method.Name == nameof(IBackendOperationsService.CreateSupplierPaymentAsync))
            {
                var sId = (Guid)args[0]!;
                var amt = (decimal)args[1]!;
                var purp = (SupplierPaymentPurpose)args[2]!;
                var opId = (Guid)args[4]!;
                createdPayments.Add((sId, amt, purp, opId));
                throw new DesktopApiException("network.timeout", "Simulated timeout");
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm1 = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: opsService,
            operationIntents: store);

        vm1.TransactionAmount = 15000m;
        vm1.SelectedMethod = SupplierSettlementMethod.CashDrawer;

        var postMethod = typeof(SupplierDetailViewModel).GetMethod("PostPaymentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)postMethod.Invoke(vm1, [SupplierPaymentPurpose.Settlement])!;

        Assert.Single(createdPayments);

        // Edit amount while pending
        var vmEdited = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: opsService,
            operationIntents: store);

        vmEdited.TransactionAmount = 20000m; // Changed amount!
        vmEdited.SelectedMethod = SupplierSettlementMethod.CashDrawer;

        await (Task)postMethod.Invoke(vmEdited, [SupplierPaymentPurpose.Settlement])!;

        Assert.Single(createdPayments);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("unresolved outcome and different details"));
    }

    [Fact]
    public async Task SupplierPayment_DefinitiveRejection_ClearsIntent_While_401_Retains()
    {
        var journalPath = CreateJournalPath();
        var supplierId = Guid.NewGuid();
        var supplier = CreateSupplierRecord(supplierId);

        var throwStatusCode = HttpStatusCode.Unauthorized;

        var opsService = OperationsMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetSupplierWorkspaceAsync))
            {
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(supplierId, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
            }
            if (method.Name == nameof(IBackendOperationsService.CreateSupplierPaymentAsync))
            {
                throw new DesktopApiException("supplier.payment_rejected", "Simulated rejection", throwStatusCode);
            }
            return null;
        });

        var store = new FileClientOperationIntentStore(journalPath);
        var toastService = new TestToastService();

        var vm = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: opsService,
            operationIntents: store);

        vm.TransactionAmount = 5000m;

        var postMethod = typeof(SupplierDetailViewModel).GetMethod("PostPaymentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 401 Unauthorized retains intent
        await (Task)postMethod.Invoke(vm, [SupplierPaymentPurpose.Advance])!;
        Assert.Single(store.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Advance"));

        // Definitive 400 Bad Request completes intent without escaping the UI boundary
        throwStatusCode = HttpStatusCode.BadRequest;
        await (Task)postMethod.Invoke(vm, [SupplierPaymentPurpose.Advance])!;

        Assert.Empty(store.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Advance"));
        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("rejected"));
    }

    // =========================================================================
    // 6. RemoteBackendBusinessOperationsService (Expense) Durable Ownership
    // =========================================================================

    [Fact]
    public async Task RemoteBackendBusinessOperationsService_PostExpense_ReusesId_And_DefinitiveRejection_CompletesIntent()
    {
        var journalPath = CreateJournalPath();
        var store = new FileClientOperationIntentStore(journalPath);
        var postedIds = new List<Guid>();

        var categoryId = Guid.NewGuid();
        var subcategoryId = Guid.NewGuid();
        var expenseId = Guid.NewGuid();
        var shouldFailDefinitive = false;
        var shouldFailUnauthorized = false;

        using var client = CreateApiClient(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/expenses/categories")
            {
                return JsonResponse<IReadOnlyList<ExpenseCategoryDto>>([new(categoryId, "Utilities")]);
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/expenses/subcategories")
            {
                return JsonResponse<IReadOnlyList<ExpenseSubcategoryDto>>([new(subcategoryId, categoryId, "Electricity")]);
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/expenses")
            {
                return JsonResponse<IReadOnlyList<ExpenseRowDto>>([
                    new ExpenseRowDto(
                        expenseId, "EXP-101", categoryId, "Utilities", subcategoryId, "Electricity",
                        DateOnly.FromDateTime(DateTime.Today), 1500m, ExpensePaymentMethod.Cash,
                        "Office bill", "EXP-101", ExpenseStatus.Posted, Guid.NewGuid(), "User")
                ]);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/expenses")
            {
                if (shouldFailUnauthorized)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                if (shouldFailDefinitive)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = JsonContent.Create(new { error = "Invalid category" })
                    };
                }

                var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
                var opId = Guid.Parse(body.GetProperty("clientOperationId").GetString()!);
                postedIds.Add(opId);
                return JsonResponse(new PostExpenseResult(expenseId, "EXP-101", false));
            }

            throw new InvalidOperationException($"Unexpected uri: {request.RequestUri}");
        });

        var service = new RemoteBackendBusinessOperationsService(client, store);

        // 1. Post succeeds
        var record1 = await service.PostExpenseAsync("Utilities", "Electricity", 1500m, DateTime.Today, "Cash", "Office bill");
        Assert.Single(postedIds);
        Assert.Empty(store.FindPendingByPrefix("expense:create"));

        // 2. 401 Session Expiry retains pending intent
        shouldFailUnauthorized = true;
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            service.PostExpenseAsync("Utilities", "Electricity", 2000m, DateTime.Today, "Cash", "Pending bill"));
        Assert.Single(store.FindPendingByPrefix("expense:create"));

        // 3. Definitive 400 Bad Request clears pending intent
        shouldFailUnauthorized = false;
        shouldFailDefinitive = true;
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            service.PostExpenseAsync("Utilities", "Electricity", 2000m, DateTime.Today, "Cash", "Pending bill"));
        Assert.Empty(store.FindPendingByPrefix("expense:create"));
    }

    // =========================================================================
    // 7. RemoteBackendThakaService Ambiguous Status Recovery and Session Guard
    // =========================================================================

    [Fact]
    public async Task RemoteBackendThakaService_AmbiguousTransport_RecoversViaStatusQuery_And_401_ThrowsDirectly()
    {
        var journalPath = CreateJournalPath();
        var store = new FileClientOperationIntentStore(journalPath);
        var projectId = Guid.NewGuid();
        var project = CreateThakaProject(projectId);
        var product = CreatePosProduct("Steel Bar", 800m);

        var simulateSocketDrop = false;
        var shouldFail401 = false;
        var statusQueryCount = 0;

        using var client = CreateApiClient(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/thaka/material-issue")
            {
                if (shouldFail401)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                if (simulateSocketDrop)
                {
                    throw new HttpRequestException("Socket reset after transmit.");
                }

                return JsonResponse(new
                {
                    ChallanNumber = "CH-500",
                    TotalCharge = 1600m
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.StartsWith("/api/system/operations/"))
            {
                statusQueryCount++;
                return JsonResponse(new
                {
                    OperationId = Guid.NewGuid(),
                    WasCommitted = true,
                    DocumentNumber = "CH-RECOVERED"
                });
            }

            throw new InvalidOperationException($"Unexpected uri: {request.RequestUri}");
        });

        var service = new RemoteBackendThakaService(client, store);
        var clientOpId = Guid.NewGuid();

        // 1. Transport failure queries status and recovers committed document number
        simulateSocketDrop = true;
        var result = await service.IssueMaterialAsync(project, product, 2m, [], clientOpId);
        Assert.Equal("CH-RECOVERED", result.ChallanNumber);
        Assert.Equal(1, statusQueryCount);

        // 2. 401 Session Expiry does NOT treat failure as ambiguous; throws without querying status
        simulateSocketDrop = false;
        shouldFail401 = true;
        await Assert.ThrowsAsync<DesktopApiException>(() =>
            service.IssueMaterialAsync(project, product, 2m, [], clientOpId));
        Assert.Equal(1, statusQueryCount); // statusQueryCount did NOT increment
    }

    // =========================================================================
    // 8. Targeted Test Oracles (J1–J5 Root Review)
    // =========================================================================

    [Fact]
    public async Task Targeted_J1_Expenses_RealAdapter_CommittedPostMissingReadback_OperationIdReuse_And_PrePostValidation_NoPost()
    {
        var journalPath = CreateJournalPath();
        var store = new FileClientOperationIntentStore(journalPath);
        var categoryId = Guid.NewGuid();
        var subcategoryId = Guid.NewGuid();
        var expenseId = Guid.NewGuid();

        var postCount = 0;
        var postedOperationIds = new List<Guid>();
        var simulateReadbackMiss = false;

        using var client = CreateApiClient(async request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/expenses/categories")
            {
                return JsonResponse<IReadOnlyList<ExpenseCategoryDto>>([
                    new ExpenseCategoryDto(categoryId, "Utilities")
                ]);
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/expenses/subcategories")
            {
                return JsonResponse<IReadOnlyList<ExpenseSubcategoryDto>>([
                    new ExpenseSubcategoryDto(subcategoryId, categoryId, "Electricity")
                ]);
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/expenses")
            {
                postCount++;
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
                var opId = Guid.Parse(body.GetProperty("clientOperationId").GetString()!);
                postedOperationIds.Add(opId);
                return JsonResponse(new PostExpenseResult(expenseId, "EXP-202", false));
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/expenses")
            {
                if (simulateReadbackMiss)
                {
                    return JsonResponse<IReadOnlyList<ExpenseRowDto>>([]);
                }
                return JsonResponse<IReadOnlyList<ExpenseRowDto>>([
                    new ExpenseRowDto(
                        expenseId, "EXP-202", categoryId, "Utilities", subcategoryId, "Electricity",
                        DateOnly.FromDateTime(DateTime.Today), 3500m, ExpensePaymentMethod.Cash,
                        "Power bill", "EXP-202", ExpenseStatus.Posted, Guid.NewGuid(), "Admin")
                ]);
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var service = new RemoteBackendBusinessOperationsService(client, store);
        var toastService = new TestToastService();

        // ---------------------------------------------------------------------
        // Part A: Pre-POST validation rejection (stale/unknown category)
        // Throws ExpensePrePostValidationException before ANY network POST
        // ---------------------------------------------------------------------
        var vmPrePost = new ExpenseEditViewModel(
            toastService,
            close: () => { },
            backendService: service,
            categories: ["NonExistentCategory"],
            operationIntents: store);

        vmPrePost.SelectedCategory = "NonExistentCategory";
        vmPrePost.Subcategory = "None";
        vmPrePost.AmountText = "1000";
        vmPrePost.SelectedPaymentMethod = "Cash";

        var saveMethod = typeof(ExpenseEditViewModel).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)saveMethod.Invoke(vmPrePost, null)!;

        // Zero POST requests were sent to the network
        Assert.Equal(0, postCount);
        // Intent store was immediately cleared/completed
        Assert.Empty(store.FindPendingByPrefix("expense:create"));
        // Editor remains unlocked for immediate user correction
        Assert.True(vmPrePost.CanEdit);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("could not be saved"));

        // ---------------------------------------------------------------------
        // Part B: Committed POST + missing readback -> Client intent retained -> Retry reuses operation ID
        // Note: Exercises client-side HTTP adapter and intent store retry behavior.
        // Server-side single durable cash effect on database ledger remains qualified as UNPROVEN / OPEN
        // without raw isolated-server/ledger database query assertions.
        // ---------------------------------------------------------------------
        simulateReadbackMiss = true;
        var vmCommitted = new ExpenseEditViewModel(
            toastService,
            close: () => { },
            backendService: service,
            categories: ["Utilities"],
            operationIntents: store);

        vmCommitted.SelectedCategory = "Utilities";
        vmCommitted.Subcategory = "Electricity";
        vmCommitted.AmountText = "3500";
        vmCommitted.SelectedPaymentMethod = "Cash";

        await (Task)saveMethod.Invoke(vmCommitted, null)!;

        // Exactly 1 POST was sent to the server
        Assert.Equal(1, postCount);
        // Because readback failed, the intent was PRESERVED in the store
        var pendingIntents = store.FindPendingByPrefix("expense:create");
        Assert.Single(pendingIntents);
        var originalOpId = pendingIntents[0].OperationId;
        Assert.Equal(postedOperationIds[0], originalOpId);

        // Reconstruct / retry with original envelope and readback restored
        simulateReadbackMiss = false;
        await (Task)saveMethod.Invoke(vmCommitted, null)!;

        // Second POST reused the EXACT SAME operation ID on retry
        Assert.Equal(2, postCount);
        Assert.Equal(originalOpId, postedOperationIds[1]);
        // All pending intents are now successfully completed
        Assert.Empty(store.FindPendingByPrefix("expense:create"));
    }

    [Fact]
    public async Task Targeted_J2_SupplierDetailViewModel_Command_DeterministicRejection_HandledWithoutUIEscape_ClearsMatchingAction_AllowsFollowUp()
    {
        var journalPath = CreateJournalPath();
        var store = new FileClientOperationIntentStore(journalPath);
        var supplierId = Guid.NewGuid();
        var supplier = CreateSupplierRecord(supplierId);

        // Pre-populate an unrelated journal intent to prove unrelated intents remain intact
        var unrelatedKey = "supplier-payment:unrelated:Settlement";
        var unrelatedOpId = store.GetOrCreate(unrelatedKey, "unrelated-payload");

        var simulateDeterministicRejection = true;
        var paymentCalls = new List<(decimal Amount, SupplierPaymentPurpose Purpose, Guid OperationId)>();
        var refundCalls = new List<(decimal Amount, Guid OperationId)>();
        var reversalCalls = new List<(Guid PaymentId, string Reason, Guid OperationId)>();
        var refundReversalCalls = new List<(Guid RefundId, string Reason, Guid OperationId)>();

        var opsService = OperationsMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetSupplierWorkspaceAsync))
            {
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(supplierId, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
            }
            if (method.Name == nameof(IBackendOperationsService.CreateSupplierPaymentAsync))
            {
                var amt = (decimal)args[1]!;
                var purp = (SupplierPaymentPurpose)args[2]!;
                var opId = (Guid)args[4]!;
                paymentCalls.Add((amt, purp, opId));

                if (simulateDeterministicRejection)
                {
                    throw new DesktopApiException("finance.supplier_account_closed", "Supplier account is closed.", HttpStatusCode.BadRequest);
                }
                return Task.CompletedTask;
            }
            if (method.Name == nameof(IBackendOperationsService.CreateSupplierRefundAsync))
            {
                var amt = (decimal)args[1]!;
                var opId = (Guid)args[3]!;
                refundCalls.Add((amt, opId));

                if (simulateDeterministicRejection)
                {
                    throw new DesktopApiException("finance.invalid_refund", "Invalid refund amount.", HttpStatusCode.BadRequest);
                }
                return Task.CompletedTask;
            }
            if (method.Name == nameof(IBackendOperationsService.ReverseSupplierPaymentAsync))
            {
                var pId = (Guid)args[0]!;
                var reason = (string)args[1]!;
                var opId = (Guid)args[2]!;
                reversalCalls.Add((pId, reason, opId));

                if (simulateDeterministicRejection)
                {
                    throw new DesktopApiException("finance.payment_already_reversed", "Payment already reversed.", HttpStatusCode.BadRequest);
                }
                return Task.CompletedTask;
            }
            if (method.Name == nameof(IBackendOperationsService.ReverseSupplierRefundAsync))
            {
                var rId = (Guid)args[0]!;
                var reason = (string)args[1]!;
                var opId = (Guid)args[2]!;
                refundReversalCalls.Add((rId, reason, opId));

                if (simulateDeterministicRejection)
                {
                    throw new DesktopApiException("finance.refund_already_reversed", "Refund already reversed.", HttpStatusCode.BadRequest);
                }
                return Task.CompletedTask;
            }
            return null;
        });

        var toastService = new TestToastService();
        var vm = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: opsService,
            operationIntents: store);

        vm.TransactionAmount = 12000m;
        vm.SelectedMethod = SupplierSettlementMethod.CashDrawer;

        var postPaymentMethod = typeof(SupplierDetailViewModel).GetMethod("PostPaymentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var receiveRefundMethod = typeof(SupplierDetailViewModel).GetMethod("ReceiveRefundAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reversePaymentMethod = typeof(SupplierDetailViewModel).GetMethod("ReversePaymentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reverseRefundMethod = typeof(SupplierDetailViewModel).GetMethod("ReverseRefundAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 1. PostSettlementCommand deterministic rejection: handled cleanly, clears actionToken "payment:Settlement"
        await (Task)postPaymentMethod.Invoke(vm, [SupplierPaymentPurpose.Settlement])!;

        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Equal(string.Empty, vm.FinancialOperationStatus);
        Assert.Empty(store.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Settlement"));
        Assert.Contains(toastService.Messages, m => m.Message.Contains("rejected"));

        // Follow-up operation can execute and succeeds
        simulateDeterministicRejection = false;
        vm.TransactionAmount = 5000m;
        await (Task)postPaymentMethod.Invoke(vm, [SupplierPaymentPurpose.Settlement])!;

        Assert.Equal(2, paymentCalls.Count);
        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Empty(store.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Settlement"));

        // 2. PostAdvanceCommand deterministic rejection: handled cleanly, clears actionToken "payment:Advance"
        simulateDeterministicRejection = true;
        vm.TransactionAmount = 8000m;
        await (Task)postPaymentMethod.Invoke(vm, [SupplierPaymentPurpose.Advance])!;

        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Empty(store.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Advance"));

        // 3. ReceiveRefundCommand deterministic rejection: handled cleanly, clears actionToken "refund"
        vm.TransactionAmount = 1500m;
        await (Task)receiveRefundMethod.Invoke(vm, null)!;

        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Empty(store.FindPendingByPrefix($"supplier-refund:{supplierId:D}"));

        // 4. ReversePaymentCommand deterministic rejection: handled cleanly, clears actionToken "payment-reversal:<id>"
        var paymentRow = new SupplierPaymentReadDto(
            Guid.NewGuid(), "PAY-001", 3000m, SupplierPaymentPurpose.Settlement,
            SupplierSettlementMethod.CashDrawer, DateTimeOffset.UtcNow,
            SupplierSettlementStatus.Posted, null, null);
        vm.ReversalReason = "Audit correction";

        await (Task)reversePaymentMethod.Invoke(vm, [paymentRow])!;

        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Empty(store.FindPendingByPrefix($"supplier-payment-reversal:{paymentRow.PaymentId:D}"));

        // 5. ReverseRefundCommand deterministic rejection: handled cleanly, clears actionToken "refund-reversal:<id>"
        var refundRow = new SupplierRefundReadDto(
            Guid.NewGuid(), "REF-001", 1200m, SupplierSettlementMethod.CashDrawer,
            DateTimeOffset.UtcNow, SupplierSettlementStatus.Posted, null, null, null, null);
        vm.ReversalReason = "Dispute resolution";

        await (Task)reverseRefundMethod.Invoke(vm, [refundRow])!;

        Assert.False(vm.HasUnresolvedFinancialOperation);
        Assert.Empty(store.FindPendingByPrefix($"supplier-refund-reversal:{refundRow.RefundId:D}"));

        // 6. Command boundary execution verification:
        // Execute ICommand.Execute across all 5 RelayCommands to verify that deterministic rejections
        // handled inside async void delegates do NOT throw escaping unhandled exceptions across the command boundary.
        simulateDeterministicRejection = true;
        vm.TransactionAmount = 1000m;
        ((ICommand)vm.PostSettlementCommand).Execute(null);
        ((ICommand)vm.PostAdvanceCommand).Execute(null);
        ((ICommand)vm.ReceiveRefundCommand).Execute(null);
        ((ICommand)vm.ReversePaymentCommand).Execute(paymentRow);
        ((ICommand)vm.ReverseRefundCommand).Execute(refundRow);

        Assert.False(vm.HasUnresolvedFinancialOperation);

        // 7. Unrelated journal intent remains intact through all rejections!
        var remainingUnrelated = store.FindPendingByPrefix(unrelatedKey);
        Assert.Single(remainingUnrelated);
        Assert.Equal(unrelatedOpId, remainingUnrelated[0].OperationId);

        // 8. 409 Conflict / Unknown non-deterministic error retains pending intent and keeps unresolved state
        var conflictOpsService = OperationsMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetSupplierWorkspaceAsync))
            {
                return Task.FromResult(new SupplierAccountWorkspaceDto(
                    new SupplierAccountSummaryDto(supplierId, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                    [], [], [], [],
                    new SupplierWarrantySummaryDto(0, 0, 0, 0, 0, 0)));
            }
            if (method.Name == nameof(IBackendOperationsService.CreateSupplierPaymentAsync))
            {
                throw new DesktopApiException("finance.concurrency_conflict", "Conflict.", HttpStatusCode.Conflict);
            }
            return null;
        });

        var vmConflict = new SupplierDetailViewModel(
            supplier,
            new TestDrawerService(),
            new DialogService(),
            toastService,
            operationsService: conflictOpsService,
            operationIntents: store);

        vmConflict.TransactionAmount = 2500m;
        vmConflict.SelectedMethod = SupplierSettlementMethod.CashDrawer;

        await (Task)postPaymentMethod.Invoke(vmConflict, [SupplierPaymentPurpose.Settlement])!;

        // 409 conflict retains unresolved state and preserves intent
        Assert.True(vmConflict.HasUnresolvedFinancialOperation);
        Assert.Contains("Unresolved supplier operation retained", vmConflict.FinancialOperationStatus);
        Assert.Single(store.FindPendingByPrefix($"supplier-payment:{supplierId:D}:Settlement"));
    }

    [Fact]
    public async Task Targeted_J3_StockAdjustmentViewModel_PayloadMismatch_LeavesEditorUsable_And_RestoringOriginal_ReusesId()
    {
        var journalPath = CreateJournalPath();
        var store = new FileClientOperationIntentStore(journalPath);
        var productId = Guid.NewGuid();
        var product = new PosProductItemViewModel("PRD-1", "Bolt M8", "SKU-B8", "Brand", "Hardware", 50m, 10m, backendProductId: productId);

        // Pre-journal an unresolved attempt for this product with quantity = 5
        var operationKey = $"stock-adjustment:{productId:D}";
        var originalPayload = $"{productId:D}||5|True|PhysicalCountCorrection|Audit note";
        var originalOpId = store.GetOrCreate(operationKey, originalPayload);

        var appliedAdjustments = new List<(Guid ProductId, decimal Qty, Guid OpId)>();
        var backendProxy = StockAdjustmentMockProxy.Create((method, args) =>
        {
            var pId = (Guid)args[0]!;
            var qty = (decimal)args[2]!;
            var opId = (Guid)args[6]!;
            appliedAdjustments.Add((pId, qty, opId));
            return Task.FromResult(Guid.NewGuid());
        });

        var toastService = new TestToastService();
        var vm = new StockAdjustmentViewModel(
            product,
            toastService,
            close: () => { },
            backendService: backendProxy,
            operationIntents: store);

        // 1. User enters mismatching quantity = 10
        vm.QuantityText = "10";
        vm.Reason = "Physical CountCorrection";
        vm.Note = "Audit note";
        vm.IsIncrease = true;

        await vm.ApplyAsync();

        // Zero adjustments applied
        Assert.Empty(appliedAdjustments);
        // Editor remains usable (CanEdit is true) because unsubmitted attempt was NOT frozen
        Assert.True(vm.CanEdit);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("unresolved outcome and different details"));
        // Old journal remains intact
        var pending = store.FindPendingByPrefix(operationKey);
        Assert.Single(pending);
        Assert.Equal(originalOpId, pending[0].OperationId);

        // 2. User restores original quantity = 5
        vm.QuantityText = "5";
        await vm.ApplyAsync();

        // Adjustment applied with the ORIGINAL operation ID from the journal!
        Assert.Single(appliedAdjustments);
        Assert.Equal(originalOpId, appliedAdjustments[0].OpId);
        Assert.Equal(5m, appliedAdjustments[0].Qty);
        Assert.Empty(store.FindPendingByPrefix(operationKey));
    }

    [Fact]
    public async Task Targeted_J4_WarrantyViewModel_ChangedUnit_ChangesFingerprint_SortedPermutationStable_NextPartialClaimAllowedAfterReconciliation()
    {
        var journalPath = CreateJournalPath();
        var store = new FileClientOperationIntentStore(journalPath);
        var customerId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var saleItemId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        var unitA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var unitB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var unitC = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var intakeRow = new WarrantyClaimIntakeRowDto(
            saleId,
            "INV-100",
            customerId,
            "Walk-in Customer",
            saleItemId,
            productId,
            "Smartphone Alpha",
            2m,
            DateOnly.FromDateTime(DateTime.Today.AddYears(1)),
            true,
            null,
            [
                new WarrantyClaimIntakeUnitDto(unitA, "TC1", "SN1", null, null, true, null),
                new WarrantyClaimIntakeUnitDto(unitB, "TC2", "SN2", null, null, true, null),
                new WarrantyClaimIntakeUnitDto(unitC, "TC3", "SN3", null, null, true, null)
            ]);

        var shouldFailWithTimeout = false;
        var claimOpIds = new List<Guid>();

        var opsService = OperationsMockProxy.Create((method, args) =>
        {
            if (method.Name == nameof(IBackendOperationsService.GetWarrantyDashboardAsync))
            {
                return Task.FromResult(new WarrantyDashboardDto(
                    new WarrantyDashboardSummaryDto(0, 0, 0, 0, 0, 0, 0),
                    []));
            }
            if (method.Name == nameof(IBackendOperationsService.CreateWarrantyClaimAsync))
            {
                var opId = (Guid)args[8]!;
                claimOpIds.Add(opId);
                if (shouldFailWithTimeout)
                {
                    throw new OperationException("network.timeout", "Network timeout occurred.");
                }
                return Task.FromResult(Guid.NewGuid());
            }
            return null;
        });

        var toastService = new TestToastService();
        var vm = new WarrantyViewModel(opsService, toastService, store);
        vm.SelectedIntakeRow = intakeRow;
        vm.ClaimIntakeCustomerIdText = customerId.ToString("D");
        vm.ClaimIntakeFaultText = "Fault X";

        var createClaimMethod = typeof(WarrantyViewModel).GetMethod("CreateClaimAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 1. Initial attempt with Unit A and Unit B encounters network timeout:
        // Production code creates pending intent and attempts CreateWarrantyClaimAsync.
        // Catch block retains pending client operation ID for retry.
        shouldFailWithTimeout = true;
        vm.ClaimIntakeUnitIdsText = $"{unitA}, {unitB}";
        await (Task)createClaimMethod.Invoke(vm, null)!;

        Assert.Single(claimOpIds);
        var opId1 = claimOpIds[0];
        var operationKey = $"warranty-claim:{saleId:D}:{saleItemId:D}";
        var pending1 = store.FindPendingByPrefix(operationKey);
        Assert.Single(pending1);
        Assert.Equal(opId1, pending1[0].OperationId);

        // 2. Permuting unit order (Unit B then Unit A) produces IDENTICAL canonical fingerprint
        // Production OrderBy sorting ensures identical payload, reusing opId1 on retry.
        vm.ClaimIntakeUnitIdsText = $"{unitB}, {unitA}";
        await (Task)createClaimMethod.Invoke(vm, null)!;

        Assert.Equal(2, claimOpIds.Count);
        Assert.Equal(opId1, claimOpIds[1]);

        // 3. Swapping Unit B for Unit C produces a DIFFERENT canonical fingerprint
        // Production code catches InvalidOperationException (mismatched payload) and shows warning toast
        // without calling the backend service or mutating the pending intent.
        vm.ClaimIntakeUnitIdsText = $"{unitA}, {unitC}";
        await (Task)createClaimMethod.Invoke(vm, null)!;

        // Backend was NOT called; call count remains 2
        Assert.Equal(2, claimOpIds.Count);
        Assert.Contains(toastService.Messages, m => m.Message.Contains("unresolved outcome and different details"));
        var stillPending = store.FindPendingByPrefix(operationKey);
        Assert.Single(stillPending);
        Assert.Equal(opId1, stillPending[0].OperationId);

        // 4. Reconcile / complete claim 1 outcome
        store.Complete(operationKey, opId1);
        Assert.Empty(store.FindPendingByPrefix(operationKey));

        // 5. Next legitimate partial claim on the same sale item is now allowed
        // Succeeds with a fresh operation ID
        shouldFailWithTimeout = false;
        vm.ClaimIntakeUnitIdsText = unitC.ToString();
        vm.ClaimIntakeFaultText = "Fault C";
        await (Task)createClaimMethod.Invoke(vm, null)!;

        Assert.Equal(3, claimOpIds.Count);
        var opId2 = claimOpIds[2];
        Assert.NotEqual(Guid.Empty, opId2);
        Assert.NotEqual(opId1, opId2);
        // Completed successfully
        Assert.Empty(store.FindPendingByPrefix(operationKey));
        Assert.Contains(toastService.Messages, m => m.Message.Contains("created"));

        // 6. Command boundary execution: execute CreateClaimCommand via ICommand
        ((ICommand)vm.CreateClaimCommand).Execute(null);
    }

    [Fact]
    public async Task Targeted_J5_RemoteStocktakeWorkflowService_ExactUnits_Unit201_Succeeds_And_CapExceeded_Or_CursorCycle_Throws()
    {
        var productId = Guid.NewGuid();
        var store = new FileClientOperationIntentStore(CreateJournalPath());

        // 1. 201 units across 2 pages succeeds
        var page1 = Enumerable.Range(1, 200).Select(i => new ExactInventoryUnitDto(
            Guid.NewGuid(), productId, "Item", "SKU", null, null, null, null,
            InventoryUnitStatus.InStock, 100m, null, null, null, DateTimeOffset.UtcNow, 1)).ToArray();
        var page2 = new[] { new ExactInventoryUnitDto(
            Guid.NewGuid(), productId, "Item", "SKU", null, null, null, null,
            InventoryUnitStatus.InStock, 100m, null, null, null, DateTimeOffset.UtcNow, 1) };

        using var client201 = CreateApiClient(async request =>
        {
            if (request.RequestUri!.Query.Contains("beforeUnitId"))
            {
                return JsonResponse(page2);
            }
            return JsonResponse(page1);
        });

        var service201 = new RemoteStocktakeWorkflowService(client201, store);
        var units201 = await service201.GetExactUnitsAsync(productId);
        Assert.Equal(201, units201.Count);

        // 2. Page cap exceeded (50 full pages of 200 items = 10,000 units) throws
        using var clientCap = CreateApiClient(async request =>
        {
            var p = Enumerable.Range(1, 200).Select(i => new ExactInventoryUnitDto(
                Guid.NewGuid(), productId, "Item", "SKU", null, null, null, null,
                InventoryUnitStatus.InStock, 100m, null, null, null, DateTimeOffset.UtcNow, 1)).ToArray();
            return JsonResponse(p);
        });

        var serviceCap = new RemoteStocktakeWorkflowService(clientCap, store);
        var capEx = await Assert.ThrowsAsync<BackendOperationException>(() =>
            serviceCap.GetExactUnitsAsync(productId));
        Assert.Equal("inventory.exact_units_page_limit_exceeded", capEx.Code);

        // 3. Repeated full page with 0 new units (cursor cycle) throws
        var cyclePage = Enumerable.Range(1, 200).Select(i => new ExactInventoryUnitDto(
            Guid.NewGuid(), productId, "Item", "SKU", null, null, null, null,
            InventoryUnitStatus.InStock, 100m, null, null, null, DateTimeOffset.UtcNow, 1)).ToArray();

        using var clientCycle = CreateApiClient(async request =>
        {
            // Always return the exact same 200 unit IDs
            return JsonResponse(cyclePage);
        });

        var serviceCycle = new RemoteStocktakeWorkflowService(clientCycle, store);
        var cycleEx = await Assert.ThrowsAsync<BackendOperationException>(() =>
            serviceCycle.GetExactUnitsAsync(productId));
        Assert.Equal("inventory.exact_units_cursor_cycle", cycleEx.Code);
    }

    // =========================================================================
    // Helpers, Proxies, and Stubs
    // =========================================================================

    private static NewPurchaseViewModel CreateNewPurchaseVm(
        IBackendPurchasingInventoryService backend,
        IToastService toast,
        IClientOperationIntentStore store)
    {
        return new NewPurchaseViewModel(
            toastService: toast,
            cancel: () => { },
            saved: _ => { },
            backendService: backend,
            operationIntents: store);
    }

    private static async Task PopulateAndSavePurchaseAsync(
        NewPurchaseViewModel vm,
        Guid supplierId,
        string invoiceNumber,
        string note,
        decimal quantity,
        decimal cost)
    {
        // Wait for LoadBackendDataAsync to complete
        for (int i = 0; i < 20 && vm.Suppliers.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        vm.SelectedSupplier = "Mock Supplier";
        vm.InvoiceNumber = invoiceNumber;
        vm.Note = note;
        if (vm.Products.Count > 0)
        {
            vm.SelectedProduct = vm.Products[0];
            vm.AddSelectedProductCommand.Execute(null);
            if (vm.Lines.Count > 0)
            {
                vm.Lines[0].Quantity = quantity;
                vm.Lines[0].Cost = cost;
                vm.Lines[0].SalePrice = cost * 1.5m;
            }
        }

        var saveMethod = typeof(NewPurchaseViewModel).GetMethod("SavePurchaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)saveMethod.Invoke(vm, null)!;
    }

    private static PurchaseRecord CreateTestPurchase(Guid purchaseId, Guid purchaseItemId) => new()
    {
        BackendPurchaseId = purchaseId,
        BackendSupplierId = Guid.NewGuid(),
        PurchaseNumber = "PO-RET-01",
        Supplier = "Test Supplier",
        InvoiceNumber = "INV-RET-01",
        Date = DateTime.Today,
        Items =
        [
            new PurchaseItemRecord
            {
                BackendPurchaseItemId = purchaseItemId,
                Product = new PosProductItemViewModel("P1", "Item 1", "SKU1", "Brand", "Cat", 10m, 100m,
                    backendProductId: Guid.NewGuid(), backendProductUnitId: Guid.NewGuid()),
                PurchasedQuantity = 10m,
                Cost = 100m,
                BackendEligibleReturnQuantity = 10m
            }
        ]
    };

    private static ThakaProjectListItemViewModel CreateThakaProject(Guid projectId) => new(
        id: "TH-001",
        projectName: "Test Project",
        customerName: "Test Customer",
        phone: "03001234567",
        location: "Test Site",
        startDate: DateTime.Today,
        materialValue: 50000m,
        paid: 20000m,
        backendProjectId: projectId);

    private static PosProductItemViewModel CreatePosProduct(string name, decimal price) => new(
        id: "PRD-1",
        name: name,
        sku: "SKU-P1",
        brand: "Brand",
        category: "General",
        stock: 100m,
        price: price,
        backendProductId: Guid.NewGuid(),
        backendProductUnitId: Guid.NewGuid());

    private static SupplierDirectoryRecord CreateSupplierRecord(Guid supplierId) => new()
    {
        Id = supplierId.ToString("D"),
        BackendId = supplierId,
        Name = "Test Supplier Co",
        Phone = "03001234567",
        City = "Lahore",
        Address = "123 Market Rd"
    };

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _messages = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_messages);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null)
        {
            _messages.Add(new ToastMessage(Guid.NewGuid(), message, tone));
        }
    }

    private sealed class TestDrawerService : IDrawerService
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsOpen => false;
        public object? Content => null;
        public void Show(object content) { }
        public void Close() { }
    }

    public class PurchasingMockProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static IBackendPurchasingInventoryService Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<IBackendPurchasingInventoryService, PurchasingMockProxy>();
            ((PurchasingMockProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }

    public class ThakaMockProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static IBackendThakaService Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<IBackendThakaService, ThakaMockProxy>();
            ((ThakaMockProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }

    public class OperationsMockProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static IBackendOperationsService Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<IBackendOperationsService, OperationsMockProxy>();
            ((OperationsMockProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }

    public class StockAdjustmentMockProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[], object?> _handler = null!;

        public static IBackendStockAdjustmentService Create(Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create<IBackendStockAdjustmentService, StockAdjustmentMockProxy>();
            ((StockAdjustmentMockProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _handler(targetMethod!, args ?? []);
    }
}
