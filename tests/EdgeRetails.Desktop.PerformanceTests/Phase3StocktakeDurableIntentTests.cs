using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3StocktakeDurableIntentTests
{
    [Fact]
    public async Task CountRetryAfterServiceRecreationReusesIdentityAndRejectsEditedPayload()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-stocktake-intent-");
        try
        {
            var storePath = Path.Combine(directory.FullName, "operation-intents.json");
            var stocktakeId = Guid.CreateVersion7();
            var productId = Guid.CreateVersion7();
            var firstOperationId = Guid.Empty;
            using (var firstClient = CreateClient(async request =>
                   {
                       if (request.Method == HttpMethod.Post)
                       {
                           var body = await request.Content!.ReadFromJsonAsync<CountRequest>();
                           firstOperationId = body!.ClientOperationId;
                       }

                       throw new HttpRequestException("Simulated response loss.");
                   }))
            {
                var firstService = new RemoteStocktakeWorkflowService(
                    firstClient, new FileClientOperationIntentStore(storePath));
                await Assert.ThrowsAsync<BackendOperationException>(() =>
                    firstService.RecordStocktakeCountAsync(stocktakeId, productId, 12.5m, "shelf count"));
            }

            Assert.NotEqual(Guid.Empty, firstOperationId);
            var changedPayloadRequests = 0;
            using (var changedPayloadClient = CreateClient(_ =>
                   {
                       changedPayloadRequests++;
                       return Task.FromResult(JsonResponse("{\"success\":true}"));
                   }))
            {
                var changedPayloadService = new RemoteStocktakeWorkflowService(
                    changedPayloadClient, new FileClientOperationIntentStore(storePath));
                await Assert.ThrowsAsync<BackendOperationException>(() =>
                    changedPayloadService.RecordStocktakeCountAsync(stocktakeId, productId, 13m, "shelf count"));
            }
            Assert.Equal(0, changedPayloadRequests);

            var replayOperationId = Guid.Empty;
            using (var retryClient = CreateClient(async request =>
                   {
                       var body = await request.Content!.ReadFromJsonAsync<CountRequest>();
                       replayOperationId = body!.ClientOperationId;
                       return JsonResponse("{\"success\":true}");
                   }))
            {
                var recreatedService = new RemoteStocktakeWorkflowService(
                    retryClient, new FileClientOperationIntentStore(storePath));
                await recreatedService.RecordStocktakeCountAsync(stocktakeId, productId, 12.5m, "shelf count");
            }

            Assert.Equal(firstOperationId, replayOperationId);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static DesktopApiClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var http = new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private sealed record CountRequest(Guid ProductId, decimal CountedSellableQty, string? ReviewNote, Guid ClientOperationId);

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
