namespace EdgeRetails.Infrastructure.Services;

/// <summary>Atomic heartbeat files for single-machine lifecycle visibility (Phase 8).</summary>
public static class SingleHostHeartbeatFileWriter
{
    public static async Task WriteAsync(string heartbeatPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heartbeatPath);
        var fullPath = Path.GetFullPath(heartbeatPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        File.Move(temp, fullPath, overwrite: true);
    }

    public static string ResolveProductionStateRoot() =>
        Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdgeRetails",
            "Production");

    public static string ResolveServerHeartbeatPath() =>
        Path.Combine(ResolveProductionStateRoot(), "server", "server.heartbeat");
}
