using EdgeRetails.Application.Production;

namespace EdgeRetails.Application.Production.Printing;

public sealed class PrintDocumentHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IProductionDocumentAuthorizationPolicy _documentAuthorization;
    private readonly IProductionDocumentSource _source;
    private readonly IPrinterProfileStore _profiles;
    private readonly IPrinterProfileValidator _profileValidator;
    private readonly IPrintJobStore _jobs;
    private readonly IProductionPrintEngine _engine;
    private readonly ProductionAuditCoordinator _audit;

    public PrintDocumentHandler(
        IProductionAuthorization authorization,
        IProductionDocumentAuthorizationPolicy documentAuthorization,
        IProductionDocumentSource source,
        IPrinterProfileStore profiles,
        IPrinterProfileValidator profileValidator,
        IPrintJobStore jobs,
        IProductionPrintEngine engine,
        ProductionAuditCoordinator audit)
    {
        _authorization = authorization;
        _documentAuthorization = documentAuthorization;
        _source = source;
        _profiles = profiles;
        _profileValidator = profileValidator;
        _jobs = jobs;
        _engine = engine;
        _audit = audit;
    }

    public async Task<PrintJobResult> HandleAsync(PrintDocumentCommand command, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthenticatedAsync(cancellationToken);
        var mode = command.RequestMode ?? (command.IsReprint ? PrintRequestMode.Reprint : command.PrintJobId is null ? PrintRequestMode.Initial : PrintRequestMode.Retry);
        PrintJobRecord? retryJob = null;
        if (mode == PrintRequestMode.Retry)
        {
            retryJob = await LoadRetryJobAsync(command, cancellationToken);
        }

        var effectiveReprint = mode == PrintRequestMode.Reprint || retryJob?.Mode == PrintRequestMode.Reprint;
        await _documentAuthorization.EnsureCanPrintAsync(command.Kind, command.BusinessDocumentId, effectiveReprint, cancellationToken);

        if (retryJob?.State == PrintJobState.Succeeded)
        {
            return new PrintJobResult(true, null, null, retryJob.PrintJobId, AlreadyCompleted: true);
        }

        if (retryJob?.State is PrintJobState.Printing or PrintJobState.OutcomeUnknown)
        {
            return new PrintJobResult(false, "print.outcome_unknown", "The previous physical print outcome is not safely retryable. Use an explicit reprint only after checking the printer.", retryJob.PrintJobId);
        }

        var profile = await _profiles.GetAsync(command.PrinterProfileName, cancellationToken)
            ?? throw new InvalidOperationException($"Printer profile '{command.PrinterProfileName}' is not configured.");
        var validation = await _profileValidator.ValidateAsync(profile, cancellationToken);
        if (!validation.Supported)
        {
            return new PrintJobResult(false, validation.ErrorCode ?? "print.profile_invalid", validation.Message, command.PrintJobId);
        }

        var job = mode == PrintRequestMode.Retry
            ? retryJob!
            : await CreateJobAsync(command, mode, cancellationToken);

        ProductionDocument document;
        try
        {
            document = await _source.LoadAsync(command.Kind, command.BusinessDocumentId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await _audit.AppendFailureBestEffortAsync(new ProductionAuditRecord(
                ProductionAuditEvents.DocumentPrintFailed,
                DateTimeOffset.UtcNow,
                command.Kind.ToString(),
                command.BusinessDocumentId.ToString(),
                $"Mode={job.Mode}; Job={job.PrintJobId}; Error=print.document_load_failed",
                command.CorrelationId));
            return new PrintJobResult(
                false,
                "print.document_load_failed",
                "The document could not be loaded for printing. No print submission was started, so the job remains safely retryable.",
                job.PrintJobId);
        }

        if (job.Mode == PrintRequestMode.Reprint)
        {
            document = document with { CopyLabel = "REPRINT" };
        }

        var attempt = checked(job.AttemptNumber + 1);
        var acquired = await _jobs.TryTransitionAsync(job.PrintJobId, job.State, PrintJobState.Printing, attempt, null, cancellationToken);
        if (!acquired)
        {
            return new PrintJobResult(false, "print.concurrent_attempt", "Another print attempt changed this job. Refresh its status before retrying.", job.PrintJobId);
        }

        PrintJobResult physical;
        try
        {
            physical = await _engine.PrintAsync(document, profile, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _jobs.TryTransitionAsync(job.PrintJobId, PrintJobState.Printing, PrintJobState.Cancelled, attempt, "print.cancelled", CancellationToken.None);
            throw;
        }
        catch
        {
            await _jobs.TryTransitionAsync(job.PrintJobId, PrintJobState.Printing, PrintJobState.OutcomeUnknown, attempt, "print.engine_exception", CancellationToken.None);
            throw;
        }

        var finalState = physical.Succeeded
            ? PrintJobState.Succeeded
            : string.Equals(physical.ErrorCode, "print.cancelled", StringComparison.OrdinalIgnoreCase)
                ? PrintJobState.Cancelled
                : string.Equals(physical.ErrorCode, "print.outcome_unknown", StringComparison.OrdinalIgnoreCase)
                    ? PrintJobState.OutcomeUnknown
                    : PrintJobState.Failed;

        // Physical printing has already happened. Never convert a successful physical print into a false
        // failure merely because post-side-effect persistence/audit has a problem.
        var statePersisted = false;
        try
        {
            statePersisted = await _jobs.TryTransitionAsync(job.PrintJobId, PrintJobState.Printing, finalState, attempt, physical.ErrorCode, CancellationToken.None);
        }
        catch { }

        var eventType = physical.Succeeded
            ? job.Mode == PrintRequestMode.Reprint ? ProductionAuditEvents.DocumentReprinted : ProductionAuditEvents.DocumentPrinted
            : ProductionAuditEvents.DocumentPrintFailed;
        var detail = $"Kind={command.Kind}; Mode={job.Mode}; Job={job.PrintJobId}; Attempt={attempt}; PrinterProfile={profile.ProfileName}; Error={physical.ErrorCode}";
        var audit = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
            eventType,
            DateTimeOffset.UtcNow,
            command.Kind.ToString(),
            command.BusinessDocumentId.ToString(),
            detail,
            command.CorrelationId));

        if (physical.Succeeded)
        {
            return physical with { PrintJobId = job.PrintJobId, AuditPersisted = audit.Persisted };
        }

        if (!statePersisted)
        {
            return physical with { PrintJobId = job.PrintJobId, ErrorCode = "print.outcome_unknown", ErrorMessage = "The printer reported failure but the durable print-job outcome could not be recorded. Check the printer before retrying.", AuditPersisted = audit.Persisted };
        }

        return physical with { PrintJobId = job.PrintJobId, AuditPersisted = audit.Persisted };
    }

    private async Task<PrintJobRecord> CreateJobAsync(PrintDocumentCommand command, PrintRequestMode mode, CancellationToken ct)
    {
        if (command.PrintJobId is not null)
        {
            throw new InvalidOperationException("New print and explicit reprint commands must not reuse an existing print-job id.");
        }

        var now = DateTimeOffset.UtcNow;
        var record = new PrintJobRecord(
            Guid.NewGuid().ToString("N"), command.Kind, command.BusinessDocumentId, command.PrinterProfileName,
            mode, PrintJobState.Prepared, 0, now, now);
        await _jobs.CreateAsync(record, ct);
        return record;
    }

    private async Task<PrintJobRecord> LoadRetryJobAsync(PrintDocumentCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.PrintJobId))
        {
            throw new ArgumentException("Retry requires the original print-job id.", nameof(command));
        }

        var record = await _jobs.GetAsync(command.PrintJobId, ct)
            ?? throw new InvalidOperationException("The print job no longer exists.");
        if (record.Kind != command.Kind || record.BusinessDocumentId != command.BusinessDocumentId ||
            !string.Equals(record.PrinterProfileName, command.PrinterProfileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Retry command does not match the original durable print job.");
        }

        return record;
    }
}

public sealed class SavePrinterProfileHandler
{
    private readonly IProductionAuthorization _authorization;
    private readonly IPrinterProfileStore _profiles;
    private readonly IPrinterProfileValidator _validator;
    private readonly ProductionAuditCoordinator _audit;

    public SavePrinterProfileHandler(IProductionAuthorization authorization, IPrinterProfileStore profiles, IPrinterProfileValidator validator, ProductionAuditCoordinator audit)
    {
        _authorization = authorization;
        _profiles = profiles;
        _validator = validator;
        _audit = audit;
    }

    public async Task HandleAsync(PrinterProfile profile, string? correlationId, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsurePermissionAsync(ProductionPermissionNames.SettingsManage, cancellationToken);
        if (string.IsNullOrWhiteSpace(profile.ProfileName) || string.IsNullOrWhiteSpace(profile.PrinterName))
        {
            throw new ArgumentException("Printer profile and printer name are required.");
        }

        if (profile.Copies < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Copies must be at least 1; the selected printer capability defines the upper limit.");
        }

        var validation = await _validator.ValidateAsync(profile, cancellationToken);
        if (!validation.Supported)
        {
            throw new InvalidOperationException(validation.Message ?? "The selected printer/profile combination is not supported.");
        }

        await _profiles.SaveAsync(profile, cancellationToken);
        _ = await _audit.AppendAfterSideEffectAsync(new ProductionAuditRecord(
            ProductionAuditEvents.PrinterSettingsChanged,
            DateTimeOffset.UtcNow,
            "PrinterProfile",
            profile.ProfileName,
            $"Paper={profile.Paper}; Copies={profile.Copies}",
            correlationId));
    }
}

public sealed class RetryPrintHandler
{
    private readonly PrintDocumentHandler _print;
    public RetryPrintHandler(PrintDocumentHandler print) => _print = print;

    public Task<PrintJobResult> HandleAsync(PrintDocumentCommand failedCommand, CancellationToken cancellationToken = default)
        => _print.HandleAsync(failedCommand with { IsReprint = false, RequestMode = PrintRequestMode.Retry }, cancellationToken);
}
