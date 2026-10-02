using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Desktop.Services;

/// <summary>Opaque output IDs and safe outcomes only; never allocates or edits inventory identity.</summary>
internal sealed record LabelOutputAttempt(Guid AttemptId, string Kind, Guid EntityId, bool IsReprint,
    string PrinterHash, PrintJobState State, bool Submitted, bool AuditPersisted, string? ErrorCode,
    Guid[] ExactIds, Guid[] ProductIds, string? PublicationOutcome = null, bool GenerationAuditPersisted = true);

internal sealed class LabelPrintAttemptStore
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public LabelPrintAttemptStore(string? path)
    {
        _path = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_LABEL_ATTEMPTS_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EdgeRetails", "Desktop", "label-print-attempts.json"));
    }

    public (LabelOutputAttempt? Attempt, bool Submit) PreparePrint(Guid attemptId, string kind, Guid entityId,
        bool isReprint, string? printer, PrintRequestMode mode)
    {
        if (mode == PrintRequestMode.Retry && !File.Exists(_path) && !File.Exists(_path + ".lock"))
        {
            return (null, false);
        }
        using var lease = Lease(out var created);
        var all = Read(allowMissing: created && mode != PrintRequestMode.Retry);
        var printerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(printer ?? "default")));
        var existing = all.SingleOrDefault(x => x.AttemptId == attemptId);
        if (existing is not null)
        {
            if (existing.Kind != kind || existing.EntityId != entityId || existing.IsReprint != isReprint || existing.PrinterHash != printerHash)
            {
                throw new InvalidOperationException("The saved label attempt does not match this request.");
            }
            if (existing.State == PrintJobState.Printing)
            {
                existing = existing with { State = PrintJobState.OutcomeUnknown, ErrorCode = "print.outcome_unknown" };
                Replace(all, existing);
                Write(all);
            }
            return (existing, false);
        }
        if (mode == PrintRequestMode.Retry) { return (null, false); }
        if (!isReprint)
        {
            var prior = all.LastOrDefault(x => x.Kind == kind && x.EntityId == entityId &&
                x.State is PrintJobState.Printing or PrintJobState.Succeeded or PrintJobState.OutcomeUnknown);
            if (prior is not null)
            {
                if (prior.State == PrintJobState.Printing)
                {
                    prior = prior with { State = PrintJobState.OutcomeUnknown, ErrorCode = "print.outcome_unknown" };
                    Replace(all, prior);
                    Write(all);
                }
                return (prior, false);
            }
        }
        var prepared = new LabelOutputAttempt(attemptId, kind, entityId, isReprint, printerHash,
            PrintJobState.Printing, false, false, null, [], []);
        all.Add(prepared);
        Write(all); // durable intent precedes the OS call
        return (prepared, true);
    }

    public LabelOutputAttempt CompletePrint(LabelOutputAttempt attempt, PrintJobResult job)
    {
        var completed = attempt with
        {
            State = job.Succeeded ? PrintJobState.Succeeded : job.ErrorCode == "print.outcome_unknown"
                ? PrintJobState.OutcomeUnknown : PrintJobState.Failed,
            Submitted = job.Succeeded, ErrorCode = SafeError(job.ErrorCode), AuditPersisted = false
        };
        Save(completed);
        return completed;
    }

    public LabelOutputAttempt PreparePdf(Guid attemptId, IReadOnlyList<Guid> exactIds, IReadOnlyList<Guid> productIds, bool reprint)
    {
        var attempt = new LabelOutputAttempt(attemptId, "pdf", Guid.Empty, reprint, string.Empty,
            PrintJobState.Prepared, false, false, null, exactIds.ToArray(), productIds.ToArray());
        using var lease = Lease(out var created);
        var all = Read(created);
        if (all.Any(x => x.AttemptId == attemptId)) { throw new InvalidOperationException("The export attempt already exists."); }
        all.Add(attempt);
        Write(all);
        return attempt;
    }

    public LabelOutputAttempt CompletePdf(LabelOutputAttempt attempt, string outcome, bool generationAuditPersisted, string? errorCode = null)
    {
        var completed = attempt with { State = outcome == "Published" ? PrintJobState.Succeeded : PrintJobState.Failed,
            PublicationOutcome = outcome, GenerationAuditPersisted = generationAuditPersisted,
            ErrorCode = SafeError(errorCode), AuditPersisted = false };
        Save(completed);
        return completed;
    }

    public void RecordAudit(LabelOutputAttempt attempt, bool persisted, bool? generationPersisted = null)
        => Save(attempt with { AuditPersisted = persisted, GenerationAuditPersisted = generationPersisted ?? attempt.GenerationAuditPersisted });

    public IReadOnlyList<LabelOutputAttempt> PendingReceipts()
    {
        if (!File.Exists(_path) && !File.Exists(_path + ".lock")) { return []; }
        using var lease = Lease(out _);
        return Read(false).Where(x => !x.AuditPersisted && (x.Kind == "pdf"
            ? x.PublicationOutcome is not null : x.State != PrintJobState.Printing)).ToArray();
    }

    private void Save(LabelOutputAttempt attempt)
    {
        using var lease = Lease(out _);
        var all = Read(false);
        Replace(all, attempt);
        Write(all);
    }

    private static void Replace(List<LabelOutputAttempt> all, LabelOutputAttempt attempt)
    {
        var index = all.FindIndex(x => x.AttemptId == attempt.AttemptId);
        if (index < 0) { throw new InvalidOperationException("The durable label attempt is missing."); }
        all[index] = attempt;
    }

    private FileStream Lease(out bool created)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        for (var retry = 0; ; retry++)
        {
            try
            {
                if (File.Exists(_path + ".lock"))
                {
                    created = false;
                    return new(_path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                created = true;
                return new(_path + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (retry < 100) { Thread.Sleep(25); }
        }
    }

    private List<LabelOutputAttempt> Read(bool allowMissing)
    {
        if (!File.Exists(_path))
        {
            if (!allowMissing) { throw new InvalidDataException("The established label attempt journal is missing."); }
            return [];
        }
        if ((File.GetAttributes(_path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("The label attempt journal is invalid.");
        }
        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("The label attempt journal size is invalid."); }
        var all = JsonSerializer.Deserialize<List<LabelOutputAttempt>>(input, Options)
            ?? throw new InvalidDataException("The label attempt journal is empty.");
        if (all.Count > 4096 || all.Any(x => x is null || x.AttemptId == Guid.Empty || x.Kind is not ("exact" or "product" or "pdf")
            || !Enum.IsDefined(x.State) || x.ExactIds is null || x.ProductIds is null
            || x.ExactIds.Length + x.ProductIds.Length > 500 || (x.Kind != "pdf" && x.EntityId == Guid.Empty)
            || x.ErrorCode != SafeError(x.ErrorCode) || x.PublicationOutcome is not (null or "Published" or "Failed" or "Unknown"))
            || all.Select(x => x.AttemptId).Distinct().Count() != all.Count)
        {
            throw new InvalidDataException("The label attempt journal format is invalid.");
        }
        return all;
    }

    private void Write(List<LabelOutputAttempt> all)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(all, Options);
        if (bytes.Length > MaximumBytes || all.Count > 4096) { throw new InvalidDataException("The label attempt journal is full."); }
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static string? SafeError(string? code) => code is null ? null
        : code.Length <= 100 && code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_') ? code : "print.failed";
}
