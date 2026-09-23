using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Infrastructure.Production.Printing;

public sealed class BasicPrinterProfileValidator : IPrinterProfileValidator
{
    public Task<PrinterProfileValidationResult> ValidateAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(profile.ProfileName))
        {
            return Task.FromResult(new PrinterProfileValidationResult(false, "print.profile_name_required", "Profile name is required."));
        }

        if (string.IsNullOrWhiteSpace(profile.PrinterName))
        {
            return Task.FromResult(new PrinterProfileValidationResult(false, "print.printer_name_required", "Printer name is required."));
        }

        if (profile.Copies < 1 || profile.Copies > 10)
        {
            return Task.FromResult(new PrinterProfileValidationResult(false, "print.invalid_copy_count", "Copies must be between 1 and 10."));
        }

        return Task.FromResult(PrinterProfileValidationResult.Valid);
    }
}
