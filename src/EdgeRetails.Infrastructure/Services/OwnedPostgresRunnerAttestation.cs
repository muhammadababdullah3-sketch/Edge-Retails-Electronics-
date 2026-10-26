using System.Text.Json;

namespace EdgeRetails.Infrastructure.Services;

/// <summary>
/// Shared approval rules for disposable PostgreSQL runner roots (harness + guarded sequence authority).
/// </summary>
internal static class OwnedPostgresRunnerAttestation
{
    internal static (bool Approved, string? ClusterRunId) TryApproveRunnerRoot(string root, int connectionPort)
    {
        if (TryReadOwnershipMarker(root, connectionPort, out var runId))
        {
            return (true, runId);
        }

        if (IsApprovedRunnerFilesystemPath(root))
        {
            return (true, null);
        }

        return (false, null);
    }

    internal static void EnsureRunnerRootApproved(string root, int connectionPort)
    {
        if (!TryApproveRunnerRoot(root, connectionPort).Approved)
        {
            throw new InvalidOperationException(
                "FAIL-CLOSED: PostgreSQL fixture authority is outside the approved isolated runner or lacks verified ownership marker.");
        }
    }

    internal static bool HasClusterDataDirectory(string root) =>
        Directory.Exists(Path.Combine(root, "data")) || Directory.Exists(Path.Combine(root, "pgdata"));

    internal static bool IsApprovedRunnerFilesystemPath(string root)
    {
        if (!HasClusterDataDirectory(root))
        {
            return false;
        }

        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Contains("EdgeRetailsDisposable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (full.Contains("ER-RUN-", StringComparison.OrdinalIgnoreCase) &&
            full.Contains("EdgeRetailsMasterPg_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var tempBase in GetTempRootPrefixes())
        {
            if (!full.StartsWith(tempBase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (full.Contains("EdgeRetailsMasterPg_", StringComparison.OrdinalIgnoreCase) ||
                full.Contains("EdgeRetailsCatalogPg_", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadOwnershipMarker(string root, int connectionPort, out string? clusterRunId)
    {
        clusterRunId = null;
        var ownershipMarkerPath = Path.Combine(root, "OWNERSHIP_MARKER.json");
        if (!File.Exists(ownershipMarkerPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ownershipMarkerPath));
            var rootElem = doc.RootElement;
            var marker = rootElem.TryGetProperty("marker", out var m) ? m.GetString() : null;
            var runId = rootElem.TryGetProperty("run_id", out var r) ? r.GetString() : null;
            var port = rootElem.TryGetProperty("port", out var p) ? p.GetInt32() : 0;
            var isolation = rootElem.TryGetProperty("isolation_mode", out var i) ? i.GetString() : null;

            if (string.Equals(marker, "ER_DISPOSABLE_CLUSTER_OWNERSHIP_MARKER", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(runId) &&
                port == connectionPort &&
                string.Equals(isolation, "owned_disposable_local_cluster", StringComparison.Ordinal))
            {
                clusterRunId = runId;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static IEnumerable<string> GetTempRootPrefixes()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            seen.Add(full);
        }

        Add(Path.GetTempPath());
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            Add(Path.Combine(localAppData, "Temp"));
        }

        return seen;
    }
}
