using System.Diagnostics;
using System.Text;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IPostgresProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken);
}

public sealed class ProcessRunner : IPostgresProcessRunner
{
    private const int MaximumCapturedCharactersPerStream = 256 * 1024;
    private static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(5);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    start.Environment.Remove(pair.Key);
                }
                else
                {
                    start.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start '{fileName}'.");
        }
        // Draining is independent from caller cancellation so redirected pipes cannot deadlock.
        var stdoutTask = DrainBoundedAsync(process.StandardOutput);
        var stderrTask = DrainBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(CancellationDrainTimeout);
            }
            catch
            {
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(CancellationDrainTimeout);
            }
            catch
            {
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder(Math.Min(MaximumCapturedCharactersPerStream, 16 * 1024));

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            if (read == 0)
            {
                break;
            }

            var remaining = MaximumCapturedCharactersPerStream - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return captured.ToString();
    }
}
