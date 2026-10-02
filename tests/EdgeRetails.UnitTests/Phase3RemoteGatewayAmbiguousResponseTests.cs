using System.Net;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.UnitTests;

public sealed class Phase3RemoteGatewayAmbiguousResponseTests
{
    [Fact]
    public async Task MalformedSuccessBodyAfterSaleCommit_ReturnsUncertainGatewayFailure()
    {
        using var http = new HttpClient(new InvalidJsonHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:7150")
        };
        var gateway = new RemoteApplicationGateway(http);
        var result = await gateway.CompleteSaleAsync(new CompleteSaleCommand(
            Guid.CreateVersion7(), null, Guid.CreateVersion7(), null, 0m,
            SalePaymentMethod.Cash, 100m, null, null,
            [new CompleteSaleLineInput(Guid.CreateVersion7(), Guid.CreateVersion7(),
                1m, 100m, [])]));

        Assert.False(result.IsSuccess);
        Assert.Equal("gateway.invalid_response", result.Error?.Code);
    }

    private sealed class InvalidJsonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{partial-json")
            });
    }
}
