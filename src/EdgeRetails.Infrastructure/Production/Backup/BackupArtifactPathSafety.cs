namespace EdgeRetails.Infrastructure.Production.Backup;

public static class BackupArtifactPathSafety
{
    public static string ResolveOwnedBackupPath(string backupDirectory, string manifestFileName)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            throw new ArgumentException("Backup directory is required.", nameof(backupDirectory));
        }

        if (string.IsNullOrWhiteSpace(manifestFileName))
        {
            throw new InvalidDataException("Backup manifest filename is empty.");
        }

        if (Path.IsPathRooted(manifestFileName))
        {
            throw new InvalidDataException("Backup manifest filename must not be rooted.");
        }

        if (!string.Equals(Path.GetFileName(manifestFileName), manifestFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Backup manifest filename must be a basename only.");
        }

        if (!manifestFileName.EndsWith(".erbak", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Backup manifest filename does not identify an Edge Retails backup artifact.");
        }

        var root = EnsureTrailingSeparator(Path.GetFullPath(backupDirectory));
        var candidate = Path.GetFullPath(Path.Combine(root, manifestFileName));
        if (!candidate.StartsWith(root, PathComparison))
        {
            throw new InvalidDataException("Backup artifact resolves outside the configured backup directory.");
        }

        RejectReparsePointIfExisting(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        RejectReparsePointIfExisting(candidate);
        return candidate;
    }

    public static bool ManifestPathMatchesBackup(string manifestPath, string resolvedBackupPath)
        => string.Equals(
            Path.GetFullPath(manifestPath),
            Path.GetFullPath(resolvedBackupPath + ".manifest.json"),
            PathComparison);

    private static void RejectReparsePointIfExisting(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Reparse-point backup path is not accepted: '{Path.GetFileName(path)}'.");
        }
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
