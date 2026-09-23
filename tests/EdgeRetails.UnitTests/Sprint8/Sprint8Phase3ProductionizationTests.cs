using EdgeRetails.Application.Production.Printing;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8Phase3ProductionizationTests
{
    [Fact]
    public void PrintRequestModes_SeparateRetryFromExplicitReprint()
    {
        Assert.Contains(PrintRequestMode.Retry, Enum.GetValues<PrintRequestMode>());
        Assert.Contains(PrintRequestMode.Reprint, Enum.GetValues<PrintRequestMode>());
    }

    [Fact]
    public void ReprintLabel_IsPartOfCanonicalDocumentContract()
    {
        var doc = new ProductionDocument(ProductionDocumentKind.PosSaleReceipt, Guid.NewGuid(), "S-1", DateTimeOffset.UtcNow,
            "Shop", null, null, null, "Receipt", Array.Empty<ProductionDocumentLine>(), Array.Empty<ProductionDocumentTotal>(), Array.Empty<string>(), null, "REPRINT");
        Assert.Equal("REPRINT", doc.CopyLabel);
    }

    [Fact]
    public void AllSevenProductionDocumentsRemainCanonical()
        => Assert.Equal(7, Enum.GetValues<ProductionDocumentKind>().Length);
    [Fact]
    public void DocumentSourceRouter_RequiresAllSevenCanonicalSources()
    {
        var partial = new[] { new StubKindSource(ProductionDocumentKind.PosSaleReceipt) };
        var error = Assert.Throws<InvalidOperationException>(() => new ProductionDocumentSourceRouter(partial));
        Assert.Contains("Missing", error.Message);
    }

    [Fact]
    public async Task DocumentSourceRouter_RoutesEveryCanonicalKind()
    {
        var sources = Enum.GetValues<ProductionDocumentKind>().Select(x => new StubKindSource(x)).ToArray();
        var router = new ProductionDocumentSourceRouter(sources);
        foreach (var kind in Enum.GetValues<ProductionDocumentKind>())
        {
            var doc = await router.LoadAsync(kind, Guid.NewGuid());
            Assert.Equal(kind, doc.Kind);
        }
    }

    private sealed class StubKindSource(ProductionDocumentKind kind) : IProductionDocumentKindSource
    {
        public ProductionDocumentKind Kind { get; } = kind;
        public Task<ProductionDocument> LoadAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductionDocument(Kind, id, $"DOC-{Kind}", DateTimeOffset.UtcNow, "Shop", null, null, null, Kind.ToString(), Array.Empty<ProductionDocumentLine>(), Array.Empty<ProductionDocumentTotal>(), Array.Empty<string>(), null));
    }

}
