using System.Text.Json;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Production.Outbox;

public sealed record PrintOutboxPayload(
    ProductionDocumentKind Kind,
    Guid BusinessDocumentId,
    string PrinterProfileName,
    bool IsReprint,
    string? CorrelationId = null);

public sealed class PrintOutboxEffectHandler : IOutboxEffectHandler
{
    public const string TypeName = "PrintDocument";
    public string EffectType => TypeName;

    private readonly PrintDocumentHandler _printHandler;

    public PrintOutboxEffectHandler(PrintDocumentHandler printHandler)
    {
        _printHandler = printHandler ?? throw new ArgumentNullException(nameof(printHandler));
    }

    public async Task ExecuteAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<PrintOutboxPayload>(message.PayloadJson)
            ?? throw new InvalidOperationException("Print outbox payload is invalid.");

        var command = new PrintDocumentCommand(
            payload.Kind,
            payload.BusinessDocumentId,
            payload.PrinterProfileName,
            IsReprint: payload.IsReprint,
            CorrelationId: payload.CorrelationId,
            PrintJobId: null,
            RequestMode: payload.IsReprint ? PrintRequestMode.Reprint : PrintRequestMode.Initial);

        var result = await _printHandler.HandleAsync(command, cancellationToken);
        if (!result.Succeeded && !result.AlreadyCompleted)
        {
            if (string.Equals(result.ErrorCode, "print.outcome_unknown", StringComparison.OrdinalIgnoreCase))
            {
                throw new NonRetryableOutboxEffectException(
                    $"Print outcome unknown: [{result.ErrorCode}] {result.ErrorMessage}");
            }

            throw new InvalidOperationException(
                $"Print failed: [{result.ErrorCode}] {result.ErrorMessage}");
        }
    }
}
