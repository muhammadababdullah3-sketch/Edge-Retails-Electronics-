using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Identity;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3PurchaseVoidAuthorizationTests
{
    [Fact]
    public async Task PurchaseVoid_RejectsEndedSessionBeforeMutation()
    {
        using var factory = new Phase2ServerWebApplicationFactory();
        factory.Reset();
        using var client = factory.CreateClient();
        var endedSessionId = Guid.CreateVersion7();
        factory.Sessions.SeedSession(new UserSession
        {
            Id = endedSessionId,
            UserId = factory.ValidUserId,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            IsRevoked = false
        });

        using var request = CreateRequest(factory, endedSessionId, new
        {
            purchaseId = Guid.NewGuid(),
            clientOperationId = Guid.CreateVersion7(),
            voidedBy = factory.ValidUserId,
            reason = "Expired session must not mutate"
        });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.session_invalid", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task PurchaseVoid_RequiresPurchasingManagePermissionBeforeApplicationMutation()
    {
        using var factory = new Phase2ServerWebApplicationFactory();
        factory.Reset();
        using var client = factory.CreateClient();
        var sessionId = factory.SeedValidSession();
        var command = new
        {
            purchaseId = Guid.NewGuid(),
            clientOperationId = Guid.CreateVersion7(),
            voidedBy = factory.ValidUserId,
            reason = "Phase 3 authorization contract"
        };

        using var deniedRequest = CreateRequest(factory, sessionId, command);
        using var deniedResponse = await client.SendAsync(deniedRequest);

        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        Assert.Equal("authorization.denied", await ReadCodeAsync(deniedResponse));

        factory.GrantPermission(factory.ValidUserId, PermissionKeys.PurchasingManage);
        using var allowedRequest = CreateRequest(factory, sessionId, command);
        using var allowedResponse = await client.SendAsync(allowedRequest);

        Assert.Equal(HttpStatusCode.BadRequest, allowedResponse.StatusCode);
        Assert.Equal("purchasing.purchase_not_voidable", await ReadCodeAsync(allowedResponse));
    }

    private static HttpRequestMessage CreateRequest(
        Phase2ServerWebApplicationFactory factory,
        Guid sessionId,
        object command)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/purchasing/void")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add("X-Terminal-Id", factory.ActiveTerminalId.ToString("D"));
        request.Headers.Add("X-Terminal-Secret", factory.ActiveTerminalSecret);
        request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
        return request;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }
}
