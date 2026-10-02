using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

const string MissingKey = "Backup protection is not configured. Set EDGE_RETAILS_BACKUP_KEY in the Server environment.";
const string InvalidKey = "Backup protection key must be exactly 64 hexadecimal characters.";
const string Busy = "Another backup operation is currently active.";
const string InstalledServer = @"C:\Program Files\Edge Retails\server\EdgeRetails.Server.exe";

// No dump or trace file is created. Runtime payloads are streamed in memory; only exact,
// source-reviewed static messages may cross the output boundary. Unknown payloads are discarded.
var safeMessages = new Dictionary<string, string>(StringComparer.Ordinal)
{
    [MissingKey] = "backup.protection_not_provisioned",
    [InvalidKey] = "backup.protection_invalid",
    [Busy] = "backup.busy"
};
if (args is ["--canary"])
{
    await Task.Delay(TimeSpan.FromSeconds(3));
    foreach (var message in new[] { MissingKey, InvalidKey, Busy, "PRIVATE_CANARY_MUST_NEVER_APPEAR_IN_PROBE_OUTPUT" })
    {
        try { throw new InvalidOperationException(message); }
        catch (InvalidOperationException) { }
    }
    await Task.Delay(TimeSpan.FromSeconds(10));
    return 0;
}

Process? ownedCanary = null;
try
{
    var selfTest = args is ["--self-test"];
    int targetPid;
    int seconds;
    if (selfTest)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--canary");
        ownedCanary = Process.Start(start) ?? throw new InvalidOperationException("Probe canary could not start.");
        targetPid = ownedCanary.Id;
        seconds = 8;
    }
    else if (args.Length == 2 && int.TryParse(args[0], out targetPid) && int.TryParse(args[1], out seconds) && seconds is >= 5 and <= 120)
    {
        using var target = Process.GetProcessById(targetPid);
        var path = target.MainModule?.FileName;
        if (path is null || !string.Equals(path, InstalledServer, StringComparison.OrdinalIgnoreCase) ||
            FileVersionInfo.GetVersionInfo(path).FileVersion != "1.0.11")
        {
            Console.WriteLine("PROBE_TARGET_REJECTED");
            return 3;
        }
    }
    else
    {
        Console.WriteLine("Usage: EdgeRetails.BackupFailureProbe <installed-server-pid> <5..120-seconds> OR --self-test");
        return 3;
    }

    var client = new DiagnosticsClient(targetPid);
    using var session = client.StartEventPipeSession(
        [new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, (long)ClrTraceEventParser.Keywords.Exception)],
        requestRundown: false,
        circularBufferMB: 1);
    using var source = new EventPipeEventSource(session.EventStream);
    var codes = new HashSet<string>(StringComparer.Ordinal);
    source.Clr.ExceptionStart += data =>
    {
        if (data.ExceptionType != "System.InvalidOperationException" || !safeMessages.TryGetValue(data.ExceptionMessage, out var code))
        {
            return;
        }
        codes.Add(code);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Event = "BACKUP_EXCEPTION_OBSERVED",
            ProcessId = targetPid,
            TimestampUtc = data.TimeStamp.ToUniversalTime(),
            ExceptionType = "System.InvalidOperationException",
            SafeCode = code,
            SafeMessage = safeMessages.Single(pair => pair.Value == code).Key
        }));
    };
    Console.WriteLine($"PROBE_STARTED PID={targetPid} Seconds={seconds} RawTraceSaved=False");
    var stopTask = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        session.Stop();
    });
    source.Process();
    await stopTask;
    if (ownedCanary is not null)
    {
        await ownedCanary.WaitForExitAsync();
    }
    var passed = selfTest ? codes.SetEquals(safeMessages.Values) : codes.Count > 0;
    Console.WriteLine($"PROBE_COMPLETED MatchedCategories={codes.Count} CompletionStatus={(passed ? "PASS" : "NO_MATCH")} ExitCode={(passed ? 0 : 2)}");
    return passed ? 0 : 2;
}
catch (Exception)
{
    // Diagnostics errors may contain OS details; never print raw exception messages.
    Console.WriteLine("PROBE_FAILED_SAFE NoRawExceptionOutput=True");
    return 1;
}
finally
{
    ownedCanary?.Dispose();
}
