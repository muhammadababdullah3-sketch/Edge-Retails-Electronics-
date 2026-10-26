using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.Common;

public sealed class CentralLocalPortalCredentialRecord
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("mfaSecret")]
    public string MfaSecret { get; set; } = string.Empty;

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public static class CentralLocalPortalCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string? ResolveRecordPath(string contentRootPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(contentRootPath));
        while (directory is not null)
        {
            var localDir = Path.Combine(directory.FullName, "local");
            if (Directory.Exists(localDir))
            {
                return Path.Combine(localDir, "central-dev.portal.json");
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static CentralLocalPortalCredentialRecord? TryRead(string contentRootPath)
    {
        var path = ResolveRecordPath(contentRootPath);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CentralLocalPortalCredentialRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string contentRootPath, CentralLocalPortalCredentialRecord record)
    {
        var path = ResolveRecordPath(contentRootPath);
        if (path is null)
        {
            throw new InvalidOperationException("Could not resolve repository local/ directory for portal credentials.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
    }
}
