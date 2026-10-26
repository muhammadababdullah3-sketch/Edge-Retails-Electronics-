namespace EdgeRetails.Infrastructure.Services;

/// <summary>
/// Single process-wide gate for harness / guarded sequence fixture directory setup.
/// Integration tests use both <see cref="GuardedTestSequenceAuthorityExtensions"/> and test harness paths.
/// </summary>
internal static class SequenceFixtureCoordination
{
    internal static readonly object InitLock = new();

    internal static bool SequenceArtifactsComplete(string manifestPath) =>
        File.Exists(manifestPath) &&
        File.Exists(manifestPath + ".checkpoint") &&
        File.Exists(manifestPath + ".lock");

    internal static void WaitForFile(string path, int maxAttempts = 100, int delayMs = 20)
    {
        for (var attempt = 0; attempt < maxAttempts && !File.Exists(path); attempt++)
        {
            Thread.Sleep(delayMs);
        }
    }

    internal static void WaitForSequenceArtifacts(string manifestPath, int maxAttempts = 100, int delayMs = 20)
    {
        for (var attempt = 0; attempt < maxAttempts && !SequenceArtifactsComplete(manifestPath); attempt++)
        {
            Thread.Sleep(delayMs);
        }
    }
}
