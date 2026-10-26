using System.Text.Json;
using EdgeRetails.Central.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace EdgeRetails.Central.UnitTests.Security;

public sealed class AnonymousPortalSessionGateMiddlewareTests
{
    [Fact]
    public async Task GetPortalSession_WithoutCookie_Returns401SessionRequired_AndDoesNotInvokeNext()
    {
        var nextInvoked = false;
        var middleware = new AnonymousPortalSessionGateMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = HttpMethods.Get,
                Path = "/api/admin/v1/portal/session",
                Scheme = "https"
            },
            Response = { Body = new MemoryStream() }
        };

        await middleware.InvokeAsync(context);

        Assert.False(nextInvoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("SESSION_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetPortalSession_WithValidCookie_InvokesNext()
    {
        var nextInvoked = false;
        var sessionId = Guid.NewGuid();
        var middleware = new AnonymousPortalSessionGateMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = HttpMethods.Get,
                Path = "/api/admin/v1/portal/session",
                Scheme = "https"
            },
            Response = { Body = new MemoryStream() }
        };
        context.Request.Headers.Cookie = $"central_session={sessionId:D}";

        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
    }
}
