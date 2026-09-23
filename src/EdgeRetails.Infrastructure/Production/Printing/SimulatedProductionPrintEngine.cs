using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class SimulatedProductionPrintEngine : IProductionPrintEngine
{
    private readonly Func<ProductionDocument, PrinterProfile, Task<PrintJobResult>>? _handler;

    public SimulatedProductionPrintEngine(Func<ProductionDocument, PrinterProfile, Task<PrintJobResult>>? handler = null)
    {
        _handler = handler;
    }

    public Task<PrintJobResult> PrintAsync(ProductionDocument document, PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        if (_handler is not null)
        {
            return _handler(document, profile);
        }

        return Task.FromResult(new PrintJobResult(
            Succeeded: true,
            ErrorCode: null,
            ErrorMessage: null,
            PrintJobId: null,
            AlreadyCompleted: false));
    }
}
