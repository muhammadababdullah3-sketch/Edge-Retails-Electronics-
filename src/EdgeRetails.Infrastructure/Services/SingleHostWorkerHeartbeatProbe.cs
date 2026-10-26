using EdgeRetails.Application.Abstractions;

namespace EdgeRetails.Infrastructure.Services;

/// <summary>Reads local worker heartbeat file for single-host lifecycle visibility.</summary>
public sealed class SingleHostWorkerHeartbeatProbe
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(3);

    public SingleHostWorkerHeartbeatStatus Probe(DateTimeOffset utcNow)
    {
        var path = ResolveHeartbeatPath();
        if (!File.Exists(path))
        {
            return new SingleHostWorkerHeartbeatStatus(false, null, true, path);
        }

        try
        {
            var text = File.ReadAllText(path).Trim();
            if (!DateTimeOffset.TryParse(text, out var beat))
            {
                return new SingleHostWorkerHeartbeatStatus(true, null, true, path);
            }

            var isStale = utcNow - beat > StaleThreshold;
            return new SingleHostWorkerHeartbeatStatus(true, beat, isStale, path);
        }
        catch
        {
            return new SingleHostWorkerHeartbeatStatus(true, null, true, path);
        }
    }

    internal static string ResolveHeartbeatPath()
    {
        var productionStateRoot = Environment.GetEnvironmentVariable("EDGE_RETAILS_PRODUCTION_STATE_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EdgeRetails",
                "Production");
        return Path.Combine(productionStateRoot, "worker", "worker.heartbeat");
    }
}
