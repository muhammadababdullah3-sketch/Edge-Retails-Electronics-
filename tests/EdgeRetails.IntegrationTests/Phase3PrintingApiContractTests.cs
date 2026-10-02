using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase3PrintingApiContractTests
{
    [Fact]
    public async Task ProductLabel_AndProductReceiptRequireInventoryPermission()
    {
        var controller = Controller(new RecordingStickerDocumentSource(), new HashSet<string>());
        var document = await controller.GetProductLabel(Guid.NewGuid());
        var receipt = await controller.RecordProductLabelReceipt(new(Guid.NewGuid(), false, true, null, Guid.NewGuid()));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(document).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(receipt).StatusCode);
    }

    [Fact]
    public async Task ProductLabel_AndProductReceiptReprintRequireDedicatedPermission()
    {
        var controller = Controller(new RecordingStickerDocumentSource(), new HashSet<string>(["inventory.manage"]));
        var document = await controller.GetProductLabel(Guid.NewGuid(), true);
        var receipt = await controller.RecordProductLabelReceipt(new(Guid.NewGuid(), true, true, null, Guid.NewGuid()));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(document).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(receipt).StatusCode);
    }

    // NEW_COVERAGE: export and workstation receipts retain inventory/reprint authority.
    [Fact]
    public async Task LabelExport_RequiresInventoryPermissionBeforeLoadingCommittedIdentity()
    {
        var source = new RecordingStickerDocumentSource();
        var response = await Controller(source, new HashSet<string>()).ExportLabels(new([Guid.NewGuid()], []));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public async Task LabelExport_DuplicateSelectionRejectedBeforeReadingIdentity()
    {
        var source = new RecordingStickerDocumentSource();
        var id = Guid.NewGuid();
        var response = await Controller(source, new HashSet<string>(["inventory.manage"])).ExportLabels(new([id, id], []));
        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public async Task LabelExport_AndPrintReceiptReprintRequireDedicatedPermission()
    {
        var source = new RecordingStickerDocumentSource();
        var controller = Controller(source, new HashSet<string>(["inventory.manage"]));
        var export = await controller.ExportLabels(new([Guid.NewGuid()], [], true));
        var receipt = await controller.RecordStickerReceipt(new(Guid.NewGuid(), true, true, null, Guid.NewGuid()));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(export).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(receipt).StatusCode);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public async Task PrintReceipt_RejectsUnsafeErrorContentBeforeReadingIdentity()
    {
        var source = new RecordingStickerDocumentSource();
        var response = await Controller(source, new HashSet<string>(["inventory.manage"])).RecordStickerReceipt(new(Guid.NewGuid(), false, false, "bad\ncontent", Guid.NewGuid()));
        Assert.IsType<BadRequestObjectResult>(response);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public async Task StickerRead_RequiresInventoryPermissionBeforeLoadingDocument()
    {
        var source = new RecordingStickerDocumentSource();
        var controller = Controller(source, new HashSet<string>());

        var response = await controller.GetStickerDocument(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public async Task StickerReprint_RequiresDedicatedReprintPermission()
    {
        var source = new RecordingStickerDocumentSource();
        var controller = Controller(source, new HashSet<string>(["inventory.manage"]));

        var response = await controller.GetStickerDocument(Guid.NewGuid(), reprint: true);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(response).StatusCode);
        Assert.Equal(0, source.LoadCount);
    }

    [Fact]
    public async Task AuthorizedStickerRead_ReturnsCanonicalServerIdentityAndReprintMarker()
    {
        var unitId = Guid.NewGuid();
        var source = new RecordingStickerDocumentSource();
        var controller = Controller(source, new HashSet<string>(["inventory.manage", "printing.reprint"]));

        var response = await controller.GetStickerDocument(unitId, reprint: true);

        var document = Assert.IsType<PhysicalItemStickerDocument>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(unitId, document.InventoryUnitId);
        Assert.Equal("SUP-UNIT-000042", document.TrackingCode);
        Assert.Equal(42, document.ItemSequence);
        Assert.True(document.IsReprint);
        Assert.Equal(unitId, source.LastUnitId);
        Assert.True(source.LastReprint);
    }

    private static PrintingController Controller(
        IPhysicalStickerDocumentSource source,
        IReadOnlySet<string> permissions)
    {
        var controller = new PrintingController(source, new RecordingProductionDocumentSource(), new AllowDocumentAuthorization())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.HttpContext.Items["ActorContext"] = new ActorContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Operator",
            Guid.NewGuid(),
            "Manager",
            permissions);
        return controller;
    }

    private sealed class RecordingStickerDocumentSource : IPhysicalStickerDocumentSource
    {
        public int LoadCount { get; private set; }
        public Guid LastUnitId { get; private set; }
        public bool LastReprint { get; private set; }

        public Task<PhysicalItemStickerDocument> LoadStickerDocumentAsync(
            Guid inventoryUnitId,
            bool isReprint = false,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            LastUnitId = inventoryUnitId;
            LastReprint = isReprint;
            return Task.FromResult(new PhysicalItemStickerDocument(
                inventoryUnitId,
                "SUP-UNIT-000042",
                "Edge Retails",
                "Test unit",
                "Model A",
                "SKU-42",
                42,
                null,
                null,
                null,
                "Edge Retails",
                500m,
                isReprint,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingProductionDocumentSource : IProductionDocumentSource
    {
        public Task<ProductionDocument> LoadAsync(
            ProductionDocumentKind kind,
            Guid businessDocumentId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductionDocument(
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

    private sealed class AllowDocumentAuthorization : IProductionDocumentAuthorizationPolicy
    {
        public Task EnsureCanPrintAsync(
            ProductionDocumentKind kind,
            Guid businessDocumentId,
            bool isReprint,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
