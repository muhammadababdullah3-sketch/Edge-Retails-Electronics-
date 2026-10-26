using System.Net;
using System.Net.Http.Json;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class CashSessionCurrentApiContractTests
{
    private static HttpRequestMessage CreateRequest(
        Phase2ServerWebApplicationFactory factory,
        HttpMethod method,
        string path,
        Guid sessionId,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString());
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
        return request;
    }

    [Fact]
    public async Task GetCurrent_WithoutSession_Returns_404()
    {
        using var factory = new Phase2ServerWebApplicationFactory();
        factory.Reset();
        using var client = factory.CreateClient();
        var sessionId = factory.SeedValidSession();

        using var request = CreateRequest(factory, HttpMethod.Get, "/api/finance/cash-session/current", sessionId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Open_GetCurrent_Close_CashSessionLifecycle_Returns_200()
    {
        using var factory = new Phase2ServerWebApplicationFactory();
        factory.Reset();
        using var client = factory.CreateClient();
        var sessionId = factory.SeedValidSession();
        var actorId = factory.ValidUserId;

        using var openRequest = CreateRequest(
            factory,
            HttpMethod.Post,
            "/api/finance/cash-session/open",
            sessionId,
            JsonContent.Create(new OpenCashSessionCommand(100m, actorId, "Shift open")));
        using var openResponse = await client.SendAsync(openRequest);
        Assert.Equal(HttpStatusCode.OK, openResponse.StatusCode);
        var openedSessionId = await openResponse.Content.ReadFromJsonAsync<Guid>();

        using var currentRequest = CreateRequest(factory, HttpMethod.Get, "/api/finance/cash-session/current", sessionId);
        using var currentResponse = await client.SendAsync(currentRequest);
        Assert.Equal(HttpStatusCode.OK, currentResponse.StatusCode);
        var current = await currentResponse.Content.ReadFromJsonAsync<CashSessionSummaryDto>();
        Assert.NotNull(current);
        Assert.Equal(openedSessionId, current.SessionId);
        Assert.Equal(100m, current.OpeningCash);

        factory.Identity.SetPermissions(actorId, new HashSet<string>
        {
            PermissionKeys.SalesPosUse,
            PermissionKeys.CashSessionClose
        });

        using var closeRequest = CreateRequest(
            factory,
            HttpMethod.Post,
            "/api/finance/cash-session/close",
            sessionId,
            JsonContent.Create(new CloseCashSessionCommand(openedSessionId, 100m, actorId, null)));
        using var closeResponse = await client.SendAsync(closeRequest);
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
    }
}
