using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3ProductionDocumentReadApiTests
{
    [Fact]
    public async Task DocumentRead_IsSessionProtected_AndUsesCanonicalSourceAndAuthorizationPolicy()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var source = new RecordingDocumentSource();
        var authorization = new RecordingDocumentAuthorizationPolicy();
        using var factory = CreateFactory(baseFactory, source, authorization);
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();
        var documentId = Guid.NewGuid();

        using var anonymous = CreateRequest(baseFactory, $"/api/printing/documents/PosSaleReceipt/{documentId:D}");
        using var anonymousResponse = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(0, source.LoadCount);

        using var request = CreateRequest(baseFactory, $"/api/printing/documents/PosSaleReceipt/{documentId:D}", sessionId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var document = await response.Content.ReadFromJsonAsync<ProductionDocument>(jsonOptions);
        Assert.NotNull(document);
        Assert.Equal(ProductionDocumentKind.PosSaleReceipt, document.Kind);
        Assert.Equal(documentId, document.BusinessDocumentId);
        Assert.Equal((ProductionDocumentKind.PosSaleReceipt, documentId), source.LastRequest);
        Assert.Equal((ProductionDocumentKind.PosSaleReceipt, documentId, false), authorization.LastRequest);
    }

    [Fact]
    public async Task ReprintRequiresExistingPrintingReprintPermissionBeforeDocumentRead()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var source = new RecordingDocumentSource();
        var authorization = new RecordingDocumentAuthorizationPolicy();
        using var factory = CreateFactory(baseFactory, source, authorization);
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();
        var documentId = Guid.NewGuid();

        using var denied = CreateRequest(baseFactory,
            $"/api/printing/documents/PurchaseDocument/{documentId:D}?reprint=true", sessionId);
        using var deniedResponse = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        Assert.Equal(0, source.LoadCount);
        Assert.Null(authorization.LastRequest);

        baseFactory.GrantPermission(baseFactory.ValidUserId, ProductionPermissionNames.PrintingReprint);
        using var allowed = CreateRequest(baseFactory,
            $"/api/printing/documents/PurchaseDocument/{documentId:D}?reprint=true", sessionId);
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal((ProductionDocumentKind.PurchaseDocument, documentId, true), authorization.LastRequest);
        Assert.Equal((ProductionDocumentKind.PurchaseDocument, documentId), source.LastRequest);
    }

    [Theory]
    [InlineData("not-a-document-kind", "11111111-1111-1111-1111-111111111111", "printing.document_kind_invalid")]
    [InlineData("PosSaleReceipt", "00000000-0000-0000-0000-000000000000", "printing.document_id_invalid")]
    public async Task InvalidKindOrEmptyId_IsRejectedBeforeAuthorizationOrSourceRead(
        string kind,
        string businessDocumentId,
        string expectedCode)
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var source = new RecordingDocumentSource();
        var authorization = new RecordingDocumentAuthorizationPolicy();
        using var factory = CreateFactory(baseFactory, source, authorization);
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();

        using var request = CreateRequest(baseFactory,
            $"/api/printing/documents/{kind}/{businessDocumentId}", sessionId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ReadCodeAsync(response));
        Assert.Equal(0, source.LoadCount);
        Assert.Null(authorization.LastRequest);
    }

    [Fact]
    public async Task MissingCanonicalDocument_ReturnsSanitizedNotFound()
    {
        using var baseFactory = new Phase2ServerWebApplicationFactory();
        baseFactory.Reset();
        var source = new RecordingDocumentSource { Missing = true };
        var authorization = new RecordingDocumentAuthorizationPolicy();
        using var factory = CreateFactory(baseFactory, source, authorization);
        using var client = factory.CreateClient();
        var sessionId = baseFactory.SeedValidSession();
        using var request = CreateRequest(baseFactory,
            $"/api/printing/documents/SaleReturnReceipt/{Guid.NewGuid():D}", sessionId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("printing.document_not_found", await ReadCodeAsync(response));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("internal source detail", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, source.LoadCount);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        Phase2ServerWebApplicationFactory baseFactory,
        IProductionDocumentSource source,
        IProductionDocumentAuthorizationPolicy authorization)
        => baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProductionDocumentSource>();
            services.AddSingleton(source);
            services.RemoveAll<IProductionDocumentAuthorizationPolicy>();
            services.AddSingleton(authorization);
        }));

    private static HttpRequestMessage CreateRequest(
        Phase2ServerWebApplicationFactory factory,
        string path,
        Guid? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
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

    private sealed class RecordingDocumentSource : IProductionDocumentSource
    {
        public bool Missing { get; init; }
        public int LoadCount { get; private set; }
        public (ProductionDocumentKind Kind, Guid Id)? LastRequest { get; private set; }

        public Task<ProductionDocument> LoadAsync(
            ProductionDocumentKind kind,
            Guid businessDocumentId,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LastRequest = (kind, businessDocumentId);
            if (Missing)
            {
                throw new InvalidOperationException("internal source detail");
            }

            return Task.FromResult(new ProductionDocument(
                kind,
                businessDocumentId,
                "DOC-42",
                DateTimeOffset.UtcNow,
                "Edge Retails",
                null,
                null,
                null,
                "Test document",
                [],
                [],
                [],
                null));
        }
    }

    private sealed class RecordingDocumentAuthorizationPolicy : IProductionDocumentAuthorizationPolicy
    {
        public (ProductionDocumentKind Kind, Guid Id, bool IsReprint)? LastRequest { get; private set; }

        public Task EnsureCanPrintAsync(
            ProductionDocumentKind kind,
            Guid businessDocumentId,
            bool isReprint,
            CancellationToken cancellationToken = default)
        {
            LastRequest = (kind, businessDocumentId, isReprint);
            return Task.CompletedTask;
        }
    }
}
