using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Desktop.Services;

/// <summary>Authenticated committed-label reads/output; never allocates inventory identities.</summary>
public interface IBackendLabelService
{
    Task<Result<PrintJobResult>> PrintProductLabelAsync(Guid productUnitId, string? printerName,
        bool isReprint, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<string>>> ExportLabelPdfAsync(IReadOnlyList<Guid> inventoryUnitIds,
        IReadOnlyList<Guid> productUnitIds, string outputDirectory, bool isReprint = false,
        CancellationToken cancellationToken = default);
}

internal static class LabelExportFolderPicker
{
    public static string? Choose()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a new folder for the label PDFs and manifest",
            Multiselect = false
        };
        return picker.ShowDialog() == true ? picker.FolderName : null;
    }
}
