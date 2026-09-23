using System.Printing;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Desktop.Production.Printing;

public sealed class WpfPrinterProfileValidator : IPrinterProfileValidator
{
    public Task<PrinterProfileValidationResult> ValidateAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(profile.PrinterName);
            queue.Refresh();
            var capabilities = queue.GetPrintCapabilities();
            var media = WpfPrintTicketFactory.MatchMedia(capabilities, profile.Paper);
            if (media is null)
            {
                return Task.FromResult(new PrinterProfileValidationResult(false, "print.media_not_supported", WpfPrintTicketFactory.UnsupportedMediaMessage(profile.Paper)));
            }

            if (capabilities.MaxCopyCount is int maxCopies && profile.Copies > maxCopies)
            {
                return Task.FromResult(new PrinterProfileValidationResult(false, "print.copy_count_not_supported", $"The selected printer supports at most {maxCopies} copies per job."));
            }

            var validated = WpfPrintTicketFactory.CreateValidated(queue, media, profile.Copies);
            if (!validated.Supported)
            {
                return Task.FromResult(new PrinterProfileValidationResult(false, validated.ErrorCode, validated.Message));
            }

            return Task.FromResult(PrinterProfileValidationResult.Valid);
        }
        catch (PrintSystemException)
        {
            return Task.FromResult(new PrinterProfileValidationResult(false, "print.printer_unavailable", "Windows could not query the selected printer."));
        }
    }
}
