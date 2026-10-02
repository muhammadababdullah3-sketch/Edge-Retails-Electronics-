using System.IO;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Desktop.Production.Printing;
using EdgeRetails.Infrastructure.Production.Printing;

namespace EdgeRetails.Desktop.Services;

public interface IProductionDocumentPrintService
{
    Task<PrintJobResult> PrintAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        PrinterProfile profile,
        bool isReprint,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the canonical business document from the shop Server, then submits it to the local
/// Desktop printer. Physical printer access stays on the Desktop; the Server never prints.
/// </summary>
public sealed class RemoteProductionDocumentPrintService : IProductionDocumentPrintService
{
    private readonly DesktopApiClient _apiClient;
    private readonly IProductionPrintEngine _printEngine;
    private readonly IPrintJobStore _jobs;

    public RemoteProductionDocumentPrintService(
        DesktopApiClient apiClient,
        IProductionPrintEngine? printEngine = null,
        IPrintJobStore? jobs = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _printEngine = printEngine ?? new WpfProductionPrintEngine();
        _jobs = jobs ?? CreateDesktopPrintJobStore();
    }

    public async Task<PrintJobResult> PrintAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        PrinterProfile profile,
        bool isReprint,
        CancellationToken cancellationToken = default)
    {
        if (businessDocumentId == Guid.Empty)
        {
            return new PrintJobResult(false, "printing.document_id_required", "A business document is required before printing.");
        }

        if (profile is null)
        {
            return new PrintJobResult(false, "print.profile_required", "Choose a printer profile before printing.");
        }

        PrintJobRecord job;
        try
        {
            job = isReprint
                ? await CreateReprintJobAsync(kind, businessDocumentId, profile, cancellationToken)
                : await GetOrCreateInitialJobAsync(kind, businessDocumentId, profile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (ex.Message == "print.job_identity_mismatch")
        {
            return new PrintJobResult(false, "print.job_identity_mismatch", "The existing print job belongs to a different document or printer profile.");
        }
        catch (Exception)
        {
            return new PrintJobResult(false, "print.job_state_unavailable", "The print job state could not be read or saved. Printing was not started.");
        }

        // A stable initial ID makes repeated initial requests converge on one durable record.
        // Prepared/Failed jobs are safe to retry; Printing and OutcomeUnknown fail closed across
        // a process restart because the operating system may already have accepted the job.
        if (job.State == PrintJobState.Succeeded)
        {
            return new PrintJobResult(true, null, null, job.PrintJobId, AlreadyCompleted: true);
        }

        if (job.State is PrintJobState.Printing or PrintJobState.OutcomeUnknown)
        {
            return UnknownOutcome(job.PrintJobId);
        }

        var mode = isReprint
            ? PrintRequestMode.Reprint
            : job.AttemptNumber > 0 ? PrintRequestMode.Retry : PrintRequestMode.Initial;

        ProductionDocument document;
        try
        {
            var kindSegment = Uri.EscapeDataString(kind.ToString());
            document = await _apiClient.GetAsync<ProductionDocument>(
                $"/api/printing/documents/{kindSegment}/{businessDocumentId:D}?reprint={(mode == PrintRequestMode.Reprint).ToString().ToLowerInvariant()}",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DesktopApiException ex)
        {
            return new PrintJobResult(false, ex.Code, ex.Message, job.PrintJobId);
        }

        if (document.Kind != kind || document.BusinessDocumentId != businessDocumentId)
        {
            return new PrintJobResult(
                false,
                "printing.document_identity_mismatch",
                "The Server returned a document that does not match the requested business document.",
                job.PrintJobId);
        }

        if (mode == PrintRequestMode.Reprint)
        {
            document = document with { CopyLabel = "REPRINT" };
        }

        var attempt = checked(job.AttemptNumber + 1);
        bool acquired;
        try
        {
            acquired = await _jobs.TryTransitionAsync(
                job.PrintJobId,
                job.State,
                PrintJobState.Printing,
                attempt,
                null,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new PrintJobResult(false, "print.job_state_unavailable", "The print attempt could not be durably reserved. Printing was not started.", job.PrintJobId);
        }

        if (!acquired)
        {
            var current = await SafeGetJobAsync(job.PrintJobId, cancellationToken);
            return current?.State is PrintJobState.Printing or PrintJobState.OutcomeUnknown
                ? UnknownOutcome(job.PrintJobId)
                : new PrintJobResult(false, "print.concurrent_attempt", "Another print attempt changed this job. Refresh its status before retrying.", job.PrintJobId);
        }

        PrintJobResult physical;
        try
        {
            physical = await _printEngine.PrintAsync(document, profile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryRecordFinalStateAsync(job.PrintJobId, attempt, PrintJobState.OutcomeUnknown, "print.outcome_unknown");
            return UnknownOutcome(job.PrintJobId);
        }
        catch (Exception)
        {
            await TryRecordFinalStateAsync(job.PrintJobId, attempt, PrintJobState.OutcomeUnknown, "print.engine_exception");
            return UnknownOutcome(job.PrintJobId);
        }

        var finalState = physical.Succeeded
            ? PrintJobState.Succeeded
            : string.Equals(physical.ErrorCode, "print.cancelled", StringComparison.OrdinalIgnoreCase)
                ? PrintJobState.Cancelled
                : string.Equals(physical.ErrorCode, "print.outcome_unknown", StringComparison.OrdinalIgnoreCase)
                    ? PrintJobState.OutcomeUnknown
                    : PrintJobState.Failed;
        var persisted = await TryRecordFinalStateAsync(job.PrintJobId, attempt, finalState, physical.ErrorCode);

        if (!persisted && !physical.Succeeded)
        {
            return UnknownOutcome(job.PrintJobId);
        }

        return physical with { PrintJobId = job.PrintJobId };
    }

    private async Task<PrintJobRecord> GetOrCreateInitialJobAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        PrinterProfile profile,
        CancellationToken cancellationToken)
    {
        var printJobId = GetStableInitialJobId(kind, businessDocumentId);
        var job = await _jobs.GetAsync(printJobId, cancellationToken);
        if (job is null)
        {
            var now = DateTimeOffset.UtcNow;
            var candidate = new PrintJobRecord(
                printJobId,
                kind,
                businessDocumentId,
                profile.ProfileName,
                PrintRequestMode.Initial,
                PrintJobState.Prepared,
                0,
                now,
                now);
            try
            {
                await _jobs.CreateAsync(candidate, cancellationToken);
                job = candidate;
            }
            catch (InvalidOperationException)
            {
                // A concurrent initial request may have won creation of the same stable job id.
                job = await _jobs.GetAsync(printJobId, cancellationToken);
                if (job is null)
                {
                    throw;
                }
            }
        }

        EnsureMatchingJob(job, kind, businessDocumentId, profile);
        return job;
    }

    private async Task<PrintJobRecord> CreateReprintJobAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        PrinterProfile profile,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new PrintJobRecord(
            Guid.NewGuid().ToString("N"),
            kind,
            businessDocumentId,
            profile.ProfileName,
            PrintRequestMode.Reprint,
            PrintJobState.Prepared,
            0,
            now,
            now);
        await _jobs.CreateAsync(job, cancellationToken);
        return job;
    }

    private static void EnsureMatchingJob(
        PrintJobRecord job,
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        PrinterProfile profile)
    {
        if (job.Kind != kind || job.BusinessDocumentId != businessDocumentId ||
            !string.Equals(job.PrinterProfileName, profile.ProfileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("print.job_identity_mismatch");
        }
    }

    private async Task<PrintJobRecord?> SafeGetJobAsync(string printJobId, CancellationToken cancellationToken)
    {
        try
        {
            return await _jobs.GetAsync(printJobId, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> TryRecordFinalStateAsync(
        string printJobId,
        int attempt,
        PrintJobState state,
        string? errorCode)
    {
        try
        {
            return await _jobs.TryTransitionAsync(
                printJobId,
                PrintJobState.Printing,
                state,
                attempt,
                errorCode,
                CancellationToken.None);
        }
        catch
        {
            return false;
        }
    }

    private static PrintJobResult UnknownOutcome(string printJobId) => new(
        false,
        "print.outcome_unknown",
        "The printer response could not be confirmed. Check the physical printer before choosing Reprint.",
        printJobId);

    private static IPrintJobStore CreateDesktopPrintJobStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable for durable print state.");
        }

        return new JsonPrintJobStore(Path.Combine(localAppData, "EdgeRetails", "Desktop", "production-document-print-jobs.json"));
    }

    private static string GetStableInitialJobId(ProductionDocumentKind kind, Guid businessDocumentId)
    {
        var identity = Encoding.UTF8.GetBytes($"production-document-print-v1|{kind}|{businessDocumentId:D}");
        return "desktopdoc-" + Convert.ToHexString(SHA256.HashData(identity));
    }
}
