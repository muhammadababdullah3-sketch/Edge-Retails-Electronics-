using EdgeRetails.Server.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Application.Features.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3WarrantySupplierApiContractTests
{
    [Fact]
    public void WarrantyAndSupplierReversalRoutes_ExposeCanonicalMutationSurface()
    {
        AssertRoutes<WarrantyController>(
            "claims/{id:guid}/review",
            "claims/{id:guid}/send-to-supplier",
            "claims/{id:guid}/supplier-processing",
            "claims/{id:guid}/resolution",
            "claims/{id:guid}/replacement-receipt",
            "claims/{id:guid}/handover",
            "claims/{id:guid}/cancel",
            "shop-stock/send",
            "shop-stock/{id:guid}/receive");

        AssertRoutes<FinanceController>(
            "supplier-payment/{id:guid}/reverse",
            "supplier-refund/{id:guid}/reverse");
    }

    [Fact]
    public async Task WarrantyReviewAndSupplierPaymentReversal_AreSessionProtectedAndReachApplicationValidation()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        baseFactory.Identity.SetPermissions(baseFactory.ValidUserId,
            [PermissionKeys.WarrantyClaimUpdate, PermissionKeys.SupplierPaymentReverse]);
        using var factory = baseFactory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();

        using var anonymous = CreateRequest(baseFactory, $"/api/warranty/claims/{Guid.NewGuid():D}/review",
            new { clientOperationId = Guid.Empty, note = "" });
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var review = CreateRequest(baseFactory, $"/api/warranty/claims/{Guid.NewGuid():D}/review",
            new { clientOperationId = Guid.Empty, note = "" }, sessionId);
        using var reviewResponse = await client.SendAsync(review);
        Assert.Equal(HttpStatusCode.BadRequest, reviewResponse.StatusCode);
        Assert.Equal("validation.client_operation_id_required", await ReadCodeAsync(reviewResponse));

        using var reversal = CreateRequest(baseFactory, $"/api/finance/supplier-payment/{Guid.NewGuid():D}/reverse",
            new { clientOperationId = Guid.Empty, reason = "" }, sessionId);
        using var reversalResponse = await client.SendAsync(reversal);
        Assert.Equal(HttpStatusCode.BadRequest, reversalResponse.StatusCode);
        Assert.Equal("supplier.payment_reversal_invalid", await ReadCodeAsync(reversalResponse));
    }

    private static HttpRequestMessage CreateRequest(Phase2ServerWebApplicationFactory factory, string path,
        object body, Guid? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString("D"));
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        if (sessionId.HasValue)
        {
            request.Headers.Add("X-Session-Id", sessionId.Value.ToString("D"));
        }
        return request;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("code").GetString();
    }

    private static void AssertRoutes<TController>(params string[] expected)
    {
        var actual = typeof(TController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), inherit: true)
                .Cast<HttpMethodAttribute>())
            .Where(attribute => attribute.HttpMethods.Contains("POST", StringComparer.OrdinalIgnoreCase))
            .Select(attribute => attribute.Template)
            .Where(template => template is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var route in expected)
        {
            Assert.Contains(route, actual);
        }
    }
}
