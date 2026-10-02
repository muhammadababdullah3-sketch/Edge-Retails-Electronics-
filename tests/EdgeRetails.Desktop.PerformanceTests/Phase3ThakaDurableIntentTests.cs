using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3ThakaDurableIntentTests
{
    [Fact]
    public async Task PaymentRetryAfterDesktopServiceRestartReusesOperationIdAndClearsIntent()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-thaka-payment-intent-");
        try
        {
            var storePath = Path.Combine(directory.FullName, "operation-intents.json");
            var projectId = Guid.CreateVersion7();
            var committedPaymentId = Guid.CreateVersion7();
            Guid? firstOperationId = null;
            Guid? replayOperationId = null;
            var postCount = 0;

            using (var firstApi = CreateClient(async request =>
                   {
                       Assert.Equal("/api/thaka/payments", request.RequestUri!.AbsolutePath);
                       firstOperationId = await ReadOperationIdAsync(request);
                       Interlocked.Increment(ref postCount);
                       // Simulates the server committing the operation before its response is lost.
                       throw new HttpRequestException("Simulated response loss after commit.");
                   }))
            {
                var firstService = new RemoteBackendThakaService(
                    firstApi, new FileClientOperationIntentStore(storePath));
                var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
                    firstService.RecordPaymentAsync(Project(projectId), 25m, "Cash", "receipt-1"));
                Assert.Equal("network.server_unavailable", exception.Code);
            }

            Assert.NotNull(firstOperationId);
            Assert.Contains(firstOperationId!.Value.ToString("D"), File.ReadAllText(storePath), StringComparison.OrdinalIgnoreCase);

            using (var restartedApi = CreateClient(async request =>
                   {
                       Assert.Equal("/api/thaka/payments", request.RequestUri!.AbsolutePath);
                       replayOperationId = await ReadOperationIdAsync(request);
                       Interlocked.Increment(ref postCount);
                       return JsonResponse(JsonSerializer.Serialize(new
                       {
                           PaymentId = committedPaymentId,
                           ReceiptNumber = "THK-PAY-0001",
                           BalanceAfter = 75m,
                           WasExisting = true
                       }));
                   }))
            {
                var restartedService = new RemoteBackendThakaService(
                    restartedApi, new FileClientOperationIntentStore(storePath));
                var result = await restartedService.RecordPaymentAsync(Project(projectId), 25m, "Cash", "receipt-1");
                Assert.Equal("THK-PAY-0001", result.ReceiptNumber);
                Assert.Equal(75m, result.BalanceAfter);
            }

            Assert.Equal(2, postCount);
            Assert.Equal(firstOperationId, replayOperationId);
            Assert.DoesNotContain(firstOperationId.Value.ToString("D"), File.ReadAllText(storePath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task FinalSettlementRetryAfterDesktopServiceRestartReusesOperationIdAndClearsIntent()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-thaka-settlement-intent-");
        try
        {
            var storePath = Path.Combine(directory.FullName, "operation-intents.json");
            var projectId = Guid.CreateVersion7();
            Guid? firstOperationId = null;
            Guid? replayOperationId = null;

            using (var firstApi = CreateClient(async request =>
                   {
                       Assert.Equal("/api/thaka/settlement", request.RequestUri!.AbsolutePath);
                       firstOperationId = await ReadOperationIdAsync(request);
                       // Simulates the server committing the settlement before its response is lost.
                       throw new HttpRequestException("Simulated response loss after commit.");
                   }))
            {
                var firstService = new RemoteBackendThakaService(
                    firstApi, new FileClientOperationIntentStore(storePath));
                var exception = await Assert.ThrowsAsync<DesktopApiException>(() =>
                    firstService.SettleAsync(Project(projectId), 75m, "Bank"));
                Assert.Equal("network.server_unavailable", exception.Code);
            }

            Assert.NotNull(firstOperationId);
            Assert.Contains(firstOperationId!.Value.ToString("D"), File.ReadAllText(storePath), StringComparison.OrdinalIgnoreCase);

            using (var restartedApi = CreateClient(async request =>
                   {
                       Assert.Equal("/api/thaka/settlement", request.RequestUri!.AbsolutePath);
                       replayOperationId = await ReadOperationIdAsync(request);
                       return JsonResponse(JsonSerializer.Serialize(new
                       {
                           SettlementId = Guid.CreateVersion7(),
                           SettlementNumber = "THK-SET-0001",
                           SettlementDiscount = 0m,
                           FinalPaymentAmount = 75m,
                           WasExisting = true
                       }));
                   }))
            {
                var restartedService = new RemoteBackendThakaService(
                    restartedApi, new FileClientOperationIntentStore(storePath));
                await restartedService.SettleAsync(Project(projectId), 75m, "Bank");
            }

            Assert.Equal(firstOperationId, replayOperationId);
            Assert.DoesNotContain(firstOperationId.Value.ToString("D"), File.ReadAllText(storePath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static ThakaProjectListItemViewModel Project(Guid projectId) => new(
        "THK-TEST", "Test project", "Test customer", "N/A", "N/A", DateTime.Today,
        materialValue: 100m, paid: 25m, backendProjectId: projectId);

    private static DesktopApiClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        }, ownsClient: true);

    private static async Task<Guid> ReadOperationIdAsync(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        return document.RootElement.GetProperty("clientOperationId").GetGuid();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
