using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Backup;

public sealed class BackupHistoryService
{
    private const long MaximumManifestBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IBackupManifestAuthenticator _manifestAuthenticator;

    public BackupHistoryService(IBackupManifestAuthenticator manifestAuthenticator)
        => _manifestAuthenticator = manifestAuthenticator ?? throw new ArgumentNullException(nameof(manifestAuthenticator));

    public async Task<IReadOnlyList<BackupManifest>> ReadHistoryAsync(string backupDirectory, CancellationToken cancellationToken = default)
        => (await ReadDiagnosticsAsync(backupDirectory, cancellationToken)).ValidBackups;

    public async Task<BackupHistoryDiagnostics> ReadDiagnosticsAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(backupDirectory))
        {
            return new BackupHistoryDiagnostics(Array.Empty<BackupManifest>(), Array.Empty<BackupHistoryIssue>());
        }

        var read = await ReadSafeEntriesAsync(backupDirectory, cancellationToken);
        return new BackupHistoryDiagnostics(
            read.Entries.Select(x => x.Manifest).OrderByDescending(x => x.CreatedAtUtc).ToArray(),
            read.Issues.ToArray());
    }

    public async Task ApplyRetentionAsync(string backupDirectory, BackupRetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        if (policy.MaximumBackupCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Maximum backup count must be greater than zero when specified.");
        }

        if (policy.MaximumBackupAge is { } maximumAge && maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Maximum backup age must be greater than zero when specified.");
        }

        if (!Directory.Exists(backupDirectory))
        {
            return;
        }

        var read = await ReadSafeEntriesAsync(backupDirectory, cancellationToken);
        var entries = read.Entries.OrderByDescending(x => x.Manifest.CreatedAtUtc).ToList();
        if (entries.Count == 0)
        {
            return;
        }

        var deleteFiles = new HashSet<string>(PathComparer);

        if (policy.MaximumBackupCount is > 0 && entries.Count > policy.MaximumBackupCount.Value)
        {
            foreach (var item in entries.Skip(policy.MaximumBackupCount.Value))
            {
                deleteFiles.Add(item.BackupPath);
            }
        }

        if (policy.MaximumBackupAge is not null)
        {
            var threshold = DateTimeOffset.UtcNow - policy.MaximumBackupAge.Value;
            foreach (var item in entries.Skip(1).Where(x => x.Manifest.CreatedAtUtc < threshold))
            {
                deleteFiles.Add(item.BackupPath);
            }
        }

        deleteFiles.Remove(entries[0].BackupPath);

        foreach (var item in entries.Where(x => deleteFiles.Contains(x.BackupPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteOwnedPair(item);
        }
    }

    private async Task<SafeReadResult> ReadSafeEntriesAsync(string backupDirectory, CancellationToken cancellationToken)
    {
        var entries = new List<SafeEntry>();
        var issues = new List<BackupHistoryIssue>();
        var seenFiles = new HashSet<string>(PathComparer);

        foreach (var manifestPath in Directory.EnumerateFiles(backupDirectory, "*.manifest.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestFileName = Path.GetFileName(manifestPath);
            try
            {
                if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(new(manifestFileName, "backup.manifest_reparse_point"));
                    continue;
                }

                BackupManifestEnvelope? envelope;
                await using (var manifestStream = new FileStream(
                    manifestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (manifestStream.Length <= 0 || manifestStream.Length > MaximumManifestBytes)
                    {
                        issues.Add(new(manifestFileName, "backup.manifest_size_invalid"));
                        continue;
                    }

                    using var reader = new StreamReader(manifestStream);
                    var json = await reader.ReadToEndAsync(cancellationToken);
                    envelope = JsonSerializer.Deserialize<BackupManifestEnvelope>(json, JsonOptions);
                }
                if (envelope?.Manifest is null)
                {
                    issues.Add(new(manifestFileName, "backup.manifest_invalid"));
                    continue;
                }
                var manifest = envelope.Manifest;
                if (manifest.FormatVersion != 2 || manifest.BackupId == Guid.Empty || manifest.SizeBytes < 0 || !IsSha256Hex(manifest.Sha256))
                {
                    issues.Add(new(manifestFileName, "backup.manifest_fields_invalid"));
                    continue;
                }
                if (!await _manifestAuthenticator.VerifyAuthenticationAsync(manifest, envelope.Authentication, cancellationToken))
                {
                    issues.Add(new(manifestFileName, "backup.manifest_authentication_failed"));
                    continue;
                }

                var backupPath = BackupArtifactPathSafety.ResolveOwnedBackupPath(backupDirectory, manifest.FileName);
                if (!BackupArtifactPathSafety.ManifestPathMatchesBackup(manifestPath, backupPath))
                {
                    issues.Add(new(manifestFileName, "backup.manifest_companion_mismatch"));
                    continue;
                }
                if (!File.Exists(backupPath))
                {
                    issues.Add(new(manifestFileName, "backup.artifact_missing"));
                    continue;
                }
                if ((File.GetAttributes(backupPath) & FileAttributes.ReparsePoint) != 0)
                {
                    issues.Add(new(manifestFileName, "backup.artifact_reparse_point"));
                    continue;
                }
                if (new FileInfo(backupPath).Length != manifest.SizeBytes)
                {
                    issues.Add(new(manifestFileName, "backup.artifact_size_mismatch"));
                    continue;
                }

                var actualSha256 = await ComputeSha256Async(backupPath, cancellationToken);
                if (!string.Equals(actualSha256, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new(manifestFileName, "backup.artifact_checksum_mismatch"));
                    continue;
                }

                if (!seenFiles.Add(backupPath))
                {
                    issues.Add(new(manifestFileName, "backup.duplicate_artifact"));
                    continue;
                }
                entries.Add(new SafeEntry(manifest, backupPath, Path.GetFullPath(manifestPath)));
            }
            catch (JsonException) { issues.Add(new(manifestFileName, "backup.manifest_invalid_json")); }
            catch (InvalidDataException) { issues.Add(new(manifestFileName, "backup.manifest_path_invalid")); }
            catch (UnauthorizedAccessException) { issues.Add(new(manifestFileName, "backup.manifest_access_denied")); }
            catch (IOException) { issues.Add(new(manifestFileName, "backup.manifest_io_failed")); }
        }

        return new SafeReadResult(entries, issues);
    }

    private static bool IsSha256Hex(string value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken));
    }

    private static void DeleteOwnedPair(SafeEntry entry)
    {
        // Quarantine both verified companion files before deletion. If the second move fails, the first
        // move is rolled back so retention cannot silently leave the active backup set half-deleted.
        var directory = Path.GetDirectoryName(entry.BackupPath) ?? throw new InvalidDataException("Backup directory is unavailable.");
        var token = Guid.NewGuid().ToString("N");
        var quarantinedBackup = Path.Combine(directory, $".retention-{token}.erbak");
        var quarantinedManifest = Path.Combine(directory, $".retention-{token}.manifest");

        File.Move(entry.BackupPath, quarantinedBackup, overwrite: false);
        try
        {
            File.Move(entry.ManifestPath, quarantinedManifest, overwrite: false);
        }
        catch
        {
            try { File.Move(quarantinedBackup, entry.BackupPath, overwrite: false); } catch { }
            throw;
        }

        try { File.Delete(quarantinedBackup); } catch { }
        try { File.Delete(quarantinedManifest); } catch { }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record SafeEntry(BackupManifest Manifest, string BackupPath, string ManifestPath);
    private sealed record SafeReadResult(IReadOnlyList<SafeEntry> Entries, IReadOnlyList<BackupHistoryIssue> Issues);
}
