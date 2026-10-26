namespace EdgeRetails.Central.Application.Common;

/// <summary>
/// Loads optional local development environment variables from a dotenv-style file.
/// Never overrides variables already set in the process environment.
/// </summary>
public static class CentralLocalEnvironmentLoader
{
    public const string LocalEnvFileVariable = "CENTRAL_LOCAL_ENV_FILE";
    public const string DefaultRelativePath = "local/central-dev.env";

    public static bool TryApplyFromRepository(string contentRootPath, string? environmentName)
    {
        if (!IsDevelopmentEnvironment(environmentName))
        {
            return false;
        }

        var path = ResolveEnvFilePath(contentRootPath);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        ApplyDotEnvFile(path);
        return true;
    }

    public static string? ResolveEnvFilePath(string contentRootPath)
    {
        var explicitPath = Environment.GetEnvironmentVariable(LocalEnvFileVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var directory = new DirectoryInfo(Path.GetFullPath(contentRootPath));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, DefaultRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public static void ApplyDotEnvFile(string path)
    {
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
            {
                value = value[1..^1];
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static bool IsDevelopmentEnvironment(string? environmentName) =>
        string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
}
