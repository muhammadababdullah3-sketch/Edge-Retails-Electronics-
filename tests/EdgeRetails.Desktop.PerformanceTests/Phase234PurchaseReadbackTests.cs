using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Purchasing;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase234PurchaseReadbackTests
{
    [Fact]
    public async Task PurchasingDocumentNeedsOneReadAndNoInventoryOrCatalogAuthority()
    {
        var document = Document(25);
        var requests = new List<string>();
        using var client = Client(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal($"/api/purchasing/{document.PurchaseId:D}", request.RequestUri.AbsolutePath);
            return Task.FromResult(Json(document));
        });
        var service = new RemotePurchasingInventoryService(client, () => Guid.NewGuid());
        var record = await service.GetPurchaseAsync(document.PurchaseId);
        Assert.NotNull(record);
        Assert.Equal(25, record.Items.Count);
        Assert.Single(requests);
        Assert.Equal(document.Items[0].EligibleBaseReturnQuantity, record.Items[0].EligibleReturnQuantity);
        Assert.Equal(document.Items[0].ProductName, record.Items[0].Product.Name);
        Assert.Equal(document.Items[0].PurchaseItemId, record.Items[0].BackendPurchaseItemId);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ConfirmedCreateSurvivesMissingOrFailedReadback(HttpStatusCode readStatus)
    {
        var result = new CreatePurchaseResult(Guid.NewGuid(), "PO-CONFIRMED", 25.50m, false);
        var writes = 0;
        using var client = Client(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                writes++;
                return Task.FromResult(Json(result));
            }
            return Task.FromResult(new HttpResponseMessage(readStatus)
            {
                Content = JsonContent.Create(new { code = "test.read_unavailable", message = "Read unavailable" })
            });
        });
        var service = new RemotePurchasingInventoryService(client, () => Guid.NewGuid());
        var error = await Assert.ThrowsAsync<PurchaseCommittedReadbackException>(() => service.CreatePurchaseAsync(
            Guid.NewGuid(), "INV-TEST", DateTime.Today, "", 0m, [Draft()], Guid.NewGuid()));
        Assert.Equal(result, error.Result);
        Assert.Contains("is saved", error.Message);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task ConfirmedDialogOffersOnlyReadRefreshAndDoesNotRepost()
    {
        var backend = DispatchProxy.Create<IBackendPurchasingInventoryService, BackendProxy>();
        var proxy = (BackendProxy)(object)backend;
        var saved = 0;
        var vm = new NewPurchaseViewModel(backendService: backend, saved: _ => saved++);
        vm.SelectedSupplier = "Supplier";
        vm.InvoiceNumber = "INV-TEST";
        vm.SelectedProduct = Draft().Product;
        vm.AddSelectedProductCommand.Execute(null);
        await Invoke(vm, "SavePurchaseAsync");
        Assert.True(vm.IsPurchaseCommitted);
        Assert.Equal(proxy.Result.PurchaseId, vm.ConfirmedPurchaseId);
        Assert.False(vm.CanSave);
        Assert.Contains("is saved", vm.CommitStatusMessage);
        await Invoke(vm, "SavePurchaseAsync");
        Assert.Equal(1, proxy.Posts);
        await Invoke(vm, "RefreshCommittedPurchaseAsync");
        Assert.Equal(1, proxy.Reads);
        Assert.Equal(1, saved);
        Assert.Equal(1, proxy.Posts);
    }

    [Fact]
    public async Task FailedRefreshKeepsConfirmedIdentityAndNeverReposts()
    {
        var backend = DispatchProxy.Create<IBackendPurchasingInventoryService, BackendProxy>();
        var proxy = (BackendProxy)(object)backend;
        proxy.FailReads = true;
        var vm = new NewPurchaseViewModel(backendService: backend);
        vm.SelectedSupplier = "Supplier";
        vm.InvoiceNumber = "INV-TEST";
        vm.SelectedProduct = Draft().Product;
        vm.AddSelectedProductCommand.Execute(null);
        await Invoke(vm, "SavePurchaseAsync");
        await Invoke(vm, "RefreshCommittedPurchaseAsync");
        Assert.Equal(proxy.Result.PurchaseId, vm.ConfirmedPurchaseId);
        Assert.True(vm.IsPurchaseCommitted);
        Assert.False(vm.CanSave);
        Assert.True(vm.RefreshCommittedPurchaseCommand.CanExecute(null));
        Assert.Equal(1, proxy.Posts);
        Assert.Equal(1, proxy.Reads);
    }

    private static Task Invoke(object vm, string name) =>
        (Task)vm.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;

    private static PurchaseDraftLine Draft() => new()
    {
        Product = new PosProductItemViewModel("p", "Product", "SKU", "", "", 0m, 25.50m,
            backendProductId: Guid.NewGuid(), backendProductUnitId: Guid.NewGuid()),
        Quantity = 1m, Cost = 25.50m, SalePrice = 25.50m
    };

    private static PurchaseDocumentDto Document(int count) => new(
        Guid.NewGuid(), "PO-READ", DateOnly.FromDateTime(DateTime.Today), Guid.NewGuid(), "Supplier", "INV", null,
        count * 25.50m, 0m, count * 25.50m, PurchaseStatus.Completed, PurchaseSettlementMode.External,
        DateTimeOffset.UtcNow,
        Enumerable.Range(0, count).Select(i => new PurchaseDocumentLineDto(
            Guid.NewGuid(), Guid.NewGuid(), $"Product {i}", "SKU", Guid.NewGuid(), "Pcs", false,
            1m, 1m, 1m, 25.50m, 0m, 25.50m, 25.50m, 25.50m, 0m, 1m, 1m)).ToArray(), []);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: Options)
    };
    private static DesktopApiClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => new(
        new HttpClient(new Handler(respond)) { BaseAddress = new Uri("http://127.0.0.1:7150") }, ownsClient: true);
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    public class BackendProxy : DispatchProxy
    {
        public CreatePurchaseResult Result { get; } = new(Guid.NewGuid(), "PO-SAVED", 25.50m, false);
        public int Posts { get; private set; }
        public int Reads { get; private set; }
        public bool FailReads { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case "get_ReceivesStockImmediately": return false;
                case "GetSuppliersAsync": return Task.FromResult<IReadOnlyList<BackendSupplierOption>>(
                    [new BackendSupplierOption(Guid.NewGuid(), "Supplier", "", "")]);
                case "GetCatalogAsync": return Task.FromResult<IReadOnlyList<BackendPurchaseCatalogItem>>([]);
                case "CreatePurchaseAsync":
                    Posts++;
                    return Task.FromException<PurchaseRecord>(new PurchaseCommittedReadbackException(Result, new IOException("Display response lost")));
                case "GetPurchaseAsync":
                    Reads++;
                    if (FailReads)
                    {
                        return Task.FromException<PurchaseRecord?>(new IOException("Read unavailable"));
                    }
                    return Task.FromResult<PurchaseRecord?>(new PurchaseRecord
                    {
                        BackendPurchaseId = Result.PurchaseId, PurchaseNumber = Result.PurchaseNumber,
                        Supplier = "Supplier", InvoiceNumber = "INV-TEST", Items = []
                    });
                default: throw new InvalidOperationException(targetMethod.Name);
            }
        }
    }
}
