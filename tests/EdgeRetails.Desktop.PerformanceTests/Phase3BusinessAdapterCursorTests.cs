using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Domain.Finance;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3BusinessAdapterCursorTests
{
    [Fact]
    public async Task SupplierWorkspace_DoesNotAppendAnExhaustedListAgainWhileOtherListsContinue()
    {
        var entryId = Guid.CreateVersion7();
        var firstPaymentId = Guid.CreateVersion7();
        var secondPaymentId = Guid.CreateVersion7();
        var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        var entry = new SupplierKhataEntryDto(entryId, "SUP-1", SupplierAccountEntryType.Purchase,
            SupplierAccountDirection.IncreasePayable, 10m, 10m, 10m, "PURCHASE", Guid.NewGuid(),
            now, now, Guid.NewGuid(), null, null);
        var workspace = new SupplierAccountWorkspaceDto(
            new(Guid.NewGuid(), 10m, 10m, 0m, 0m, 0m, 10m, 0m, 10m, 0m, 0m),
            [entry],
            [
                new(firstPaymentId, "PAY-1", 1m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.External,
                    now, SupplierSettlementStatus.Posted, null, null),
                new(secondPaymentId, "PAY-2", 2m, SupplierPaymentPurpose.Settlement, SupplierSettlementMethod.External,
                    now.AddMinutes(-1), SupplierSettlementStatus.Posted, null, null)
            ],
            [],
            [],
            new(0, 0, 0, 0, 0, 0));
        var pageNumber = 0;
        using var client = CreateApiClient(request =>
        {
            pageNumber++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(pageNumber == 1 ? workspace : workspace with { Payments = [] })
            };
        });
        var service = new RemoteBackendOperationsService(client);

        var result = await service.GetSupplierWorkspaceAsync(Guid.NewGuid(), pageSize: 2);

        Assert.Equal(2, pageNumber);
        Assert.Single(result.Statement);
        Assert.Equal(2, result.Payments.Count);
    }

    [Fact]
    public async Task SupplierWorkspace_ForwardsCompleteServerCursor()
    {
        Uri? observed = null;
        using var client = CreateApiClient(request =>
        {
            observed = request.RequestUri;
            return JsonResponse("{\"summary\":{\"supplierId\":\"00000000-0000-0000-0000-000000000001\",\"currentBalance\":0,\"grossPurchased\":0,\"purchaseReturnCredits\":0,\"purchaseVoidReversals\":0,\"warrantyCredits\":0,\"netPurchased\":0,\"netPaidToSupplier\":0,\"outstandingPayable\":0,\"supplierCreditOrAdvance\":0,\"netSupplierRefundsReceived\":0},\"statement\":[],\"payments\":[],\"refunds\":[],\"suppliedProducts\":[],\"warranty\":{\"customerClaimCount\":0,\"shopStockCaseCount\":0,\"trackedUnitCount\":0,\"currentlyWithSupplier\":0,\"readyOrReturned\":0,\"closedCount\":0}}");
        });
        var service = new RemoteBackendOperationsService(client);
        var occurredAt = new DateTimeOffset(2026, 9, 1, 10, 30, 0, TimeSpan.FromHours(5));
        var createdAt = occurredAt.AddMinutes(-2);
        var entryId = Guid.NewGuid();

        await service.GetSupplierWorkspaceAsync(Guid.NewGuid(), 40, occurredAt, createdAt, entryId);

        var query = ParseQuery(observed!);
        Assert.Equal(occurredAt.ToString("O"), query["beforeOccurredAt"]);
        Assert.Equal(createdAt.ToString("O"), query["beforeCreatedAt"]);
        Assert.Equal(entryId.ToString("D"), query["beforeEntryId"]);
        Assert.Equal("40", query["pageSize"]);
    }

    [Fact]
    public async Task WarrantyDashboard_ForwardsKindAndRejectsIncompleteCursor()
    {
        Uri? observed = null;
        using var client = CreateApiClient(request =>
        {
            observed = request.RequestUri;
            return JsonResponse("{\"summary\":{\"openCount\":0,\"withSupplierCount\":0,\"readyCount\":0,\"closedCount\":0,\"customerClaimCount\":0,\"shopStockCaseCount\":0,\"trackedUnitCount\":0},\"rows\":[]}");
        });
        var service = new RemoteBackendOperationsService(client);
        var createdAt = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var workId = Guid.NewGuid();

        await service.GetWarrantyDashboardAsync("charger", 30, createdAt, workId,
            beforeWorkKind: WarrantyWorkKind.ShopStock);

        var query = ParseQuery(observed!);
        Assert.Equal(createdAt.ToString("O"), query["beforeCreatedAt"]);
        Assert.Equal(nameof(WarrantyWorkKind.ShopStock), query["beforeWorkKind"]);
        Assert.Equal(workId.ToString("D"), query["beforeWorkId"]);
        Assert.Equal("30", query["pageSize"]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetWarrantyDashboardAsync(
            null, 30, createdAt, workId, beforeWorkKind: null));
    }

    [Fact]
    public async Task ExpensePost_ReadsAuthoritativeRowAfterPostExpenseResult()
    {
        var categoryId = Guid.NewGuid();
        var expenseId = Guid.NewGuid();
        using var client = CreateApiClient(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/expenses/categories" => JsonResponse($"[{{\"id\":\"{categoryId:D}\",\"name\":\"Rent\"}}]"),
            "/api/expenses/subcategories" => JsonResponse("[]"),
            "/api/expenses" when request.Method == HttpMethod.Post => JsonResponse(
                $"{{\"expenseId\":\"{expenseId:D}\",\"expenseNumber\":\"EXP-009\",\"wasExisting\":false}}"),
            "/api/expenses" => JsonResponse($"[{{\"expenseId\":\"{expenseId:D}\",\"expenseNumber\":\"EXP-009\",\"categoryId\":\"{categoryId:D}\",\"categoryName\":\"Rent\",\"subcategoryId\":null,\"subcategoryName\":null,\"expenseDate\":\"2026-09-28\",\"amount\":2500,\"paymentMethod\":\"Cash\",\"description\":\"September rent\",\"reference\":null,\"status\":\"Posted\",\"createdBy\":\"00000000-0000-0000-0000-000000000001\",\"createdByName\":\"Owner\"}}]"),
            _ => throw new InvalidOperationException($"Unexpected route: {request.RequestUri}")
        });
        var service = new RemoteBackendBusinessOperationsService(client);

        var result = await service.PostExpenseAsync("Rent", "September rent", 2500,
            new DateTime(2026, 9, 28), "Cash", "counter rent");

        Assert.Equal("EXP-009", result.Id);
        Assert.Equal(expenseId, result.BackendId);
        Assert.Equal("Owner", result.StaffMember);
    }

    private static DesktopApiClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var http = new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        return new DesktopApiClient(http, ownsClient: true);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
            parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty,
            StringComparer.OrdinalIgnoreCase);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
