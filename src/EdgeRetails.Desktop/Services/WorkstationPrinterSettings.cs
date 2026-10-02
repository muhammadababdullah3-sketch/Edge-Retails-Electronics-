using System.Printing;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Production.Printing;

namespace EdgeRetails.Desktop.Services;

/// <summary>Workstation-local printer discovery and receipt profile persistence.</summary>
public interface IWorkstationPrinterSettings
{
    Task<IReadOnlyList<string>> GetInstalledPrinterNamesAsync(CancellationToken cancellationToken = default);
    Task<PrinterProfile?> GetReceiptProfileAsync(CancellationToken cancellationToken = default);
    Task<PrinterProfileValidationResult> ValidateReceiptProfileAsync(PrinterProfile profile, CancellationToken cancellationToken = default);
    Task SaveReceiptProfileAsync(PrinterProfile profile, CancellationToken cancellationToken = default);
}

public sealed class WindowsWorkstationPrinterSettings(
    IPrinterProfileStore profiles,
    IPrinterProfileValidator? validator = null) : IWorkstationPrinterSettings
{
    private readonly IPrinterProfileValidator _validator = validator ?? new WpfPrinterProfileValidator();
    public const string ReceiptProfileName = "DEFAULT";

    public Task<IReadOnlyList<string>> GetInstalledPrinterNamesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var server = new LocalPrintServer();
        var names = server.GetPrintQueues()
            .Select(queue =>
            {
                using (queue)
                {
                    return queue.Name;
                }
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public Task<PrinterProfile?> GetReceiptProfileAsync(CancellationToken cancellationToken = default)
        => profiles.GetAsync(ReceiptProfileName, cancellationToken);

    public Task<PrinterProfileValidationResult> ValidateReceiptProfileAsync(
        PrinterProfile profile,
        CancellationToken cancellationToken = default)
        => _validator.ValidateAsync(profile, cancellationToken);

    public Task SaveReceiptProfileAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.ProfileName, ReceiptProfileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only the workstation receipt profile can be saved here.", nameof(profile));
        }

        return profiles.SaveAsync(profile, cancellationToken);
    }
}
