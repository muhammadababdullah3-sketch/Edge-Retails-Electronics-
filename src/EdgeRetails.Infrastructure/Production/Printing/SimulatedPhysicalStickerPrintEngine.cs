using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class SimulatedPhysicalStickerPrintEngine : IPhysicalStickerPrintEngine
{
    private readonly Func<PhysicalItemStickerDocument, string?, int, Task<PrintJobResult>>? _handler;

    public SimulatedPhysicalStickerPrintEngine(Func<PhysicalItemStickerDocument, string?, int, Task<PrintJobResult>>? handler = null)
    {
        _handler = handler;
    }

    public Task<PrintJobResult> PrintStickerAsync(
        PhysicalItemStickerDocument document,
        string? printerName = null,
        int copies = 1,
        CancellationToken cancellationToken = default)
    {
        if (_handler is not null)
        {
            return _handler(document, printerName, copies);
        }

        return Task.FromResult(new PrintJobResult(
            Succeeded: true,
            ErrorCode: null,
            ErrorMessage: null,
            PrintJobId: null,
            AlreadyCompleted: false));
    }
}
