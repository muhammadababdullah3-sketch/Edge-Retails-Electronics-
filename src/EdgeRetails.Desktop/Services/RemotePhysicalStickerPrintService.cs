using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Production.Printing;
using System.IO;
using System.Text.Json;

namespace EdgeRetails.Desktop.Services;

/// <summary>
/// Loads canonical sticker identity through the authenticated Server API and performs only the
/// physical printer interaction on Desktop. It never creates or changes inventory identity.
/// </summary>
public sealed class RemotePhysicalStickerPrintService
{
    public async Task<Result<PrintJobResult>> PrintProductAsync(Guid productUnitId, string? printerName, bool isReprint, CancellationToken cancellationToken = default)
    {
        if (productUnitId == Guid.Empty)
        {
            return Result<PrintJobResult>.Failure("printing.no_product_selected", "Select a committed product unit before printing its label.");
        }
        if (_printEngine is not IProductLabelPrintEngine productEngine)
        {
            return Result<PrintJobResult>.Failure("printing.product_engine_unavailable", "Product label printing is unavailable on this workstation.");
        }
        ProductLabelDocument document;
        try
        {
            document = await _apiClient.GetAsync<ProductLabelDocument>($"/api/printing/product-labels/{productUnitId:D}?reprint={isReprint.ToString().ToLowerInvariant()}", cancellationToken);
        }
        catch (DesktopApiException ex)
        {
            return Result<PrintJobResult>.Failure(ex.Code, ex.Message);
        }
        PrintJobResult job;
        try
        {
            job = await productEngine.PrintProductLabelAsync(document, printerName, 1, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            job = new(false, "print.outcome_unknown", "The printer response could not be confirmed. Check the printer before reprinting.");
        }
        var attemptId = Guid.NewGuid();
        try
        {
            var receipt = await _apiClient.PostAsync<ProductLabelPrintReceipt, StickerPrintReceiptResult>("/api/printing/product-labels/receipts",
                new(productUnitId, isReprint, job.Succeeded, job.ErrorCode, attemptId), CancellationToken.None);
            job = job with { PrintJobId = attemptId.ToString(), AuditPersisted = receipt.AuditPersisted };
        }
        catch (Exception)
        {
            job = job with { PrintJobId = attemptId.ToString(), AuditPersisted = false };
        }
        return Result<PrintJobResult>.Success(job);
    }

    public async Task<Result<IReadOnlyList<string>>> ExportPdfAsync(
        IReadOnlyList<Guid> inventoryUnitIds, IReadOnlyList<Guid> productUnitIds, string outputDirectory,
        bool isReprint = false, CancellationToken cancellationToken = default)
    {
        if (inventoryUnitIds.Count + productUnitIds.Count == 0)
        {
            return Result<IReadOnlyList<string>>.Failure("printing.no_units_selected", "Select committed units or product labels first.");
        }

        var canonicalNames = new[] { "Pilot_Exact_Unit_Labels.pdf", "Pilot_Product_Labels.pdf", "Pilot_Label_Manifest.pdf", "Pilot_Label_Manifest.json" };
        var packDirectory = Path.Combine(outputDirectory, "Pilot_Label_Pack");

        if (canonicalNames.Any(name => File.Exists(Path.Combine(outputDirectory, name))) ||
            Directory.Exists(packDirectory) && Directory.EnumerateFileSystemEntries(packDirectory).Any())
        {
            return Result<IReadOnlyList<string>>.Failure("printing.export_exists", "Choose a new export folder to preserve earlier labels.");
        }

        var stagingDirectory = Path.Combine(outputDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = await _apiClient.PostAsync<LabelExportRequest, LabelExportBundle>("/api/printing/labels/export",
                new(inventoryUnitIds, productUnitIds, isReprint), cancellationToken);

            Directory.CreateDirectory(stagingDirectory);
            var files = new[]
            {
                ("Pilot_Exact_Unit_Labels.pdf", bundle.ExactUnitPdf),
                ("Pilot_Product_Labels.pdf", bundle.ProductPdf),
                ("Pilot_Label_Manifest.pdf", bundle.ManifestPdf),
                ("Pilot_Label_Manifest.json", JsonSerializer.SerializeToUtf8Bytes(bundle.Manifest))
            };

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = Path.Combine(stagingDirectory, file.Item1);
                if (_exportFiles is not null)
                {
                    await _exportFiles.WriteAsync(stagedPath, file.Item2, cancellationToken);
                }
                else
                {
                    await File.WriteAllBytesAsync(stagedPath, file.Item2, cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            if (Directory.Exists(packDirectory))
            {
                Directory.Delete(packDirectory, true);
            }
            Directory.Move(stagingDirectory, packDirectory);

            var finalPaths = files.Select(f => Path.Combine(packDirectory, f.Item1)).ToArray();
            return Result<IReadOnlyList<string>>.Success(finalPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DesktopApiException ex)
        {
            return Result<IReadOnlyList<string>>.Failure(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<string>>.Failure("printing.pdf_export_failed", "Labels could not be saved. Stock and tracking identities remain committed; choose another folder and retry.");
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, true); } catch { }
            }
        }
    }

    private readonly DesktopApiClient _apiClient;
    private readonly IPhysicalStickerPrintEngine _printEngine;
    private readonly string? _attemptStorePath;
    private readonly ILabelPackFileWriter? _exportFiles;

    public RemotePhysicalStickerPrintService(
        DesktopApiClient apiClient,
        IPhysicalStickerPrintEngine? printEngine = null,
        string? attemptStorePath = null,
        ILabelPackFileWriter? exportFiles = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _printEngine = printEngine ?? new WpfPhysicalStickerPrintEngine();
        _attemptStorePath = attemptStorePath;
        _exportFiles = exportFiles;
    }

    public async Task<Result<PrintPhysicalStickersResult>> PrintAsync(
        IReadOnlyList<Guid> inventoryUnitIds,
        string? printerName,
        bool isReprint,
        CancellationToken cancellationToken = default,
        string? correlationId = null,
        PrintRequestMode? requestMode = null)
    {
        if (inventoryUnitIds is null || inventoryUnitIds.Count == 0)
        {
            return Result<PrintPhysicalStickersResult>.Failure(
                "printing.no_units_selected",
                "Select at least one physical unit before printing labels.");
        }

        if (inventoryUnitIds.Distinct().Count() != inventoryUnitIds.Count)
        {
            return Result<PrintPhysicalStickersResult>.Failure(
                "printing.duplicate_unit",
                "A physical unit can appear only once in a label print request.");
        }

        var attempts = LoadAttempts();
        var results = new List<PrintJobResult>(inventoryUnitIds.Count);
        foreach (var unitId in inventoryUnitIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var effectiveCorrelation = !string.IsNullOrWhiteSpace(correlationId) ? correlationId.Trim() : null;

            if (requestMode == PrintRequestMode.Retry)
            {
                if (effectiveCorrelation is null || !attempts.TryGetValue(effectiveCorrelation, out var recorded))
                {
                    results.Add(new PrintJobResult(
                        false,
                        "print.attempt_unavailable",
                        "Print attempt is unavailable in the workstation journal.",
                        effectiveCorrelation ?? Guid.NewGuid().ToString("D"),
                        InventoryUnitId: unitId));
                    continue;
                }

                if (recorded.Succeeded)
                {
                    results.Add(new PrintJobResult(
                        true,
                        null,
                        null,
                        effectiveCorrelation,
                        AlreadyCompleted: true,
                        AuditPersisted: true,
                        InventoryUnitId: unitId));
                    continue;
                }
            }

            if (!isReprint && attempts.Values.Any(a => a.UnitId == unitId && a.ErrorCode == "print.outcome_unknown"))
            {
                results.Add(new PrintJobResult(
                    false,
                    "print.outcome_unknown",
                    "The printer response could not be confirmed. Check the physical printer before choosing Reprint.",
                    effectiveCorrelation ?? Guid.NewGuid().ToString("D"),
                    InventoryUnitId: unitId));
                continue;
            }

            Guid attemptGuid = Guid.Empty;
            if (effectiveCorrelation is not null && Guid.TryParse(effectiveCorrelation, out var parsedGuid))
            {
                attemptGuid = parsedGuid;
            }
            if (attemptGuid == Guid.Empty)
            {
                attemptGuid = Guid.NewGuid();
            }

            if (effectiveCorrelation is not null)
            {
                attempts[effectiveCorrelation] = new PrintAttemptRecord
                {
                    CorrelationId = effectiveCorrelation,
                    UnitId = unitId,
                    Succeeded = false,
                    ErrorCode = "print.pending",
                    IsReprint = isReprint,
                    Timestamp = DateTimeOffset.UtcNow
                };
                SaveAttempts(attempts);
            }

            try
            {
                var document = await _apiClient.GetAsync<PhysicalItemStickerDocument>(
                    $"/api/printing/stickers/{unitId:D}?reprint={isReprint.ToString().ToLowerInvariant()}",
                    cancellationToken);

                document = document with { ClientPrintAttemptId = attemptGuid };

                PrintJobResult jobResult;
                try
                {
                    jobResult = await _printEngine.PrintStickerAsync(
                        document,
                        printerName,
                        1,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    jobResult = new PrintJobResult(
                        false,
                        "print.outcome_unknown",
                        "The printer response could not be confirmed. Check the physical printer before choosing Reprint.");
                }

                try
                {
                    var receipt = await _apiClient.PostAsync<StickerPrintReceipt, StickerPrintReceiptResult>(
                        "/api/printing/stickers/receipts",
                        new(unitId, isReprint, jobResult.Succeeded, jobResult.ErrorCode, attemptGuid), CancellationToken.None);
                    jobResult = jobResult with { PrintJobId = attemptGuid.ToString("D"), AuditPersisted = receipt.AuditPersisted, InventoryUnitId = unitId };
                }
                catch (Exception)
                {
                    // A receipt failure cannot change whether Windows accepted the job or cause resubmission.
                    jobResult = jobResult with { PrintJobId = attemptGuid.ToString("D"), AuditPersisted = false, InventoryUnitId = unitId };
                }

                if (effectiveCorrelation is not null)
                {
                    attempts[effectiveCorrelation] = new PrintAttemptRecord
                    {
                        CorrelationId = effectiveCorrelation,
                        UnitId = unitId,
                        Succeeded = jobResult.Succeeded,
                        ErrorCode = jobResult.ErrorCode,
                        ErrorMessage = jobResult.ErrorMessage,
                        IsReprint = isReprint,
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    SaveAttempts(attempts);
                }

                results.Add(jobResult);
            }
            catch (DesktopApiException ex)
            {
                results.Add(new PrintJobResult(
                    false,
                    ex.Code,
                    ex.Message,
                    attemptGuid.ToString("D"),
                    InventoryUnitId: unitId));
            }
        }

        var succeeded = results.Count(result => result.Succeeded);
        var failed = results.Count - succeeded;
        return Result<PrintPhysicalStickersResult>.Success(new PrintPhysicalStickersResult(
            results,
            inventoryUnitIds.Count,
            succeeded,
            failed));
    }

    private Dictionary<string, PrintAttemptRecord> LoadAttempts()
    {
        if (string.IsNullOrWhiteSpace(_attemptStorePath) || !File.Exists(_attemptStorePath))
        {
            return new Dictionary<string, PrintAttemptRecord>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var json = File.ReadAllText(_attemptStorePath);
            return JsonSerializer.Deserialize<Dictionary<string, PrintAttemptRecord>>(json)
                ?? new Dictionary<string, PrintAttemptRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, PrintAttemptRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveAttempts(Dictionary<string, PrintAttemptRecord> attempts)
    {
        if (string.IsNullOrWhiteSpace(_attemptStorePath))
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_attemptStorePath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(attempts);
            File.WriteAllText(_attemptStorePath, json);
        }
        catch { }
    }

    private sealed class PrintAttemptRecord
    {
        public string CorrelationId { get; set; } = string.Empty;
        public Guid UnitId { get; set; }
        public bool Succeeded { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsReprint { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}

/// <summary>File writes for a staged label pack; publication is owned by the output service.</summary>
public interface ILabelPackFileWriter
{
    Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken);
}
