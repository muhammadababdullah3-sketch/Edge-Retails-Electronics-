using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace EdgeRetails.IntegrationTests;

/// <summary>One validated SDK and Release child output for this candidate and test process.</summary>
internal static class IntegrationChildHost
{
    private const string HostOutput = "tests/EdgeRetails.CrashTestHost/bin/Release/net10.0-windows";
    private static readonly Lazy<Binding> Current = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string DotnetExecutable => Current.Value.DotnetExecutable;
    public static string CrashHostDll => Current.Value.CrashHostDll;

    private static Binding Resolve()
    {
        var root = FindCandidateRoot();
        var executable = ResolveDotnetExecutable();
        var sdkVersion = VerifySdk(executable, root);
        var manifestPath = Environment.GetEnvironmentVariable("EDGE_RETAILS_CRASH_HOST_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifestPath) || !Path.IsPathFullyQualified(manifestPath) || !File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_CRASH_HOST_MANIFEST must identify the candidate-bound manifest generated after the Release CrashTestHost build.");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var document = manifest.RootElement;
        if (!string.Equals(Path.GetFullPath(document.GetProperty("candidate_root").GetString()!), root, StringComparison.OrdinalIgnoreCase) ||
            document.GetProperty("configuration").GetString() != "Release" ||
            document.GetProperty("dotnet_sdk_version").GetString() != sdkVersion)
        {
            throw new InvalidOperationException("CrashTestHost manifest does not bind this candidate root, Release configuration and selected SDK.");
        }
        if (!DateTimeOffset.TryParse(document.GetProperty("built_utc").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var builtAt) || builtAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            throw new InvalidOperationException("CrashTestHost manifest has no valid completed build timestamp.");
        }

        var inputs = EnumerateInputs(root).ToArray();
        if (inputs.Any(p => File.GetLastWriteTimeUtc(p) > builtAt.UtcDateTime.AddSeconds(1)))
        {
            throw new InvalidOperationException("Candidate inputs changed after the attested CrashTestHost build; rebuild and regenerate its manifest.");
        }
        VerifyFiles(document.GetProperty("source_inputs"), inputs, root);

        var outputDirectory = Path.Combine(root, HostOutput);
        var host = Path.Combine(outputDirectory, "EdgeRetails.CrashTestHost.dll");
        foreach (var required in new[] { host, Path.ChangeExtension(host, ".deps.json"), Path.ChangeExtension(host, ".runtimeconfig.json") })
        {
            if (!File.Exists(required))
            {
                throw new InvalidOperationException("The candidate Release CrashTestHost DLL, dependencies and runtime configuration must all be built before integration tests.");
            }
        }
        VerifyFiles(document.GetProperty("output_files"), Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories), root);
        return new Binding(executable, host);
    }

    private static string FindCandidateRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                File.Exists(Path.Combine(directory.FullName, "tests/EdgeRetails.CrashTestHost/EdgeRetails.CrashTestHost.csproj")) &&
                File.Exists(Path.Combine(directory.FullName, "tests/EdgeRetails.IntegrationTests/EdgeRetails.IntegrationTests.csproj")))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }
        throw new InvalidOperationException("Integration tests must execute from the current candidate's build output; candidate root could not be established.");
    }

    private static string ResolveDotnetExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DOTNET");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!Path.IsPathFullyQualified(explicitPath) || !File.Exists(explicitPath))
            {
                throw new InvalidOperationException("EDGE_RETAILS_TEST_DOTNET must be an existing absolute dotnet executable path.");
            }
            return Path.GetFullPath(explicitPath);
        }

        var candidates = new List<string?> { Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") };
        var runtimeRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(runtimeRoot))
        {
            candidates.Add(Path.Combine(runtimeRoot, "dotnet.exe"));
        }
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Environment.ProcessPath);
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe"));
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(entry.Trim('"'), "dotnet.exe"));
        }
        var selected = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && File.Exists(p));
        return selected is not null ? Path.GetFullPath(selected) : throw new InvalidOperationException(
            "No absolute dotnet executable could be resolved. Configure EDGE_RETAILS_TEST_DOTNET for the SDK in global.json.");
    }

    private static string VerifySdk(string executable, string root)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        var sdk = configuration.RootElement.GetProperty("sdk");
        var requiredText = sdk.GetProperty("version").GetString();
        var rollForward = sdk.TryGetProperty("rollForward", out var policy) ? policy.GetString() : "patch";
        if (!Version.TryParse(requiredText, out var required) || rollForward is not ("patch" or "latestPatch" or "disable"))
        {
            throw new InvalidOperationException("The child-host resolver requires a stable pinned SDK with disable, patch or latestPatch roll-forward in global.json.");
        }
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The configured dotnet SDK executable could not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidOperationException("SDK version validation exceeded its ten-second setup bound.");
        }
        var actualText = standardOutput.GetAwaiter().GetResult().Trim();
        _ = standardError.GetAwaiter().GetResult(); // Do not expose command output or environment values in a setup error.
        if (process.ExitCode != 0 || !Version.TryParse(actualText, out var actual) ||
            (rollForward == "disable" ? actual != required :
                actual.Major != required.Major || actual.Minor != required.Minor || actual.Build / 100 != required.Build / 100 || actual < required))
        {
            throw new InvalidOperationException("The selected dotnet executable does not resolve the SDK required by this candidate's global.json.");
        }
        return actualText;
    }

    private static IEnumerable<string> EnumerateInputs(string root)
    {
        foreach (var inputRoot in new[] { "src", "tests/EdgeRetails.CrashTestHost", "tests/EdgeRetails.IntegrationTests", ".config" })
        {
            var directory = Path.Combine(root, inputRoot);
            if (!Directory.Exists(directory))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var segments = Path.GetRelativePath(directory, file).Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                if (!segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals(".git", StringComparison.OrdinalIgnoreCase) || s.Equals("artifacts", StringComparison.OrdinalIgnoreCase) || s.Equals("TestResults", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return Path.GetFullPath(file);
                }
            }
        }
        foreach (var name in new[] { "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.config" })
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file))
            {
                yield return file;
            }
        }
    }

    private static void VerifyFiles(JsonElement entries, IEnumerable<string> expectedFiles, string root)
    {
        var expected = expectedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            {
                throw new InvalidOperationException("Child-host manifests require bounded repository-relative file paths.");
            }
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!expected.Contains(path) || !observed.Add(path))
            {
                throw new InvalidOperationException("Child-host manifest has an unexpected or duplicate candidate file.");
            }
            using var content = File.OpenRead(path);
            var actualHash = Convert.ToHexString(SHA256.HashData(content));
            if (!string.Equals(actualHash, entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Child-host manifest hash differs from the current candidate or Release output; rebuild and rebind before testing.");
            }
        }
        if (!observed.SetEquals(expected))
        {
            throw new InvalidOperationException("Child-host manifest omits required source inputs or Release runtime files.");
        }
    }

    private sealed record Binding(string DotnetExecutable, string CrashHostDll);
}
