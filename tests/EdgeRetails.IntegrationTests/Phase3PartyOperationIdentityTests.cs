using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Application.Features.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3PartyOperationIdentityTests
{
    [Fact]
    public async Task CustomerCreate_ReplayedCorrelationIdReturnsOriginalParty()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        baseFactory.Identity.SetPermissions(baseFactory.ValidUserId, [PermissionKeys.CustomersManage]);
        using var factory = baseFactory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();
        var correlationId = Guid.NewGuid();
        var clientOperationId = Guid.NewGuid();

        var first = await CreateCustomerAsync(client, baseFactory, sessionId, correlationId, clientOperationId);
        var replay = await CreateCustomerAsync(client, baseFactory, sessionId, correlationId, clientOperationId);

        Assert.Equal(first, replay);
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client,
        Phase2ServerWebApplicationFactory factory, Guid sessionId, Guid correlationId, Guid clientOperationId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/customers")
        {
            Content = JsonContent.Create(new
            {
                name = "Response Lost Customer",
                phone = "03001234567",
                address = "Test",
                isActive = true,
                correlationId,
                clientOperationId
            })
        };
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString("D"));
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonSerializer.Deserialize<Guid>(await response.Content.ReadAsStringAsync());
    }
}
