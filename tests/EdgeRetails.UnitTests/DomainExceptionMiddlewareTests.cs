using System.Text.Json;
using EdgeRetails.Domain.Common;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class DomainExceptionMiddlewareTests
{
    [Fact]
    public async Task BusinessRuleException_Returns_Json_Code_And_Message()
    {
        var middleware = new DomainExceptionMiddleware(
            _ => throw new BusinessRuleException("cash.session_required", "Open a cash session."),
            NullLogger<DomainExceptionMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("cash.session_required", document.RootElement.GetProperty("code").GetString());
        Assert.Contains("cash session", document.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
