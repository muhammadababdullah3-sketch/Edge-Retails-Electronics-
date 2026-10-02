using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace EdgeRetails.Recovery;

/// <summary>
/// Fail-closed checks for the installed loopback Shop Server before changing
/// Recovery trust or restarting its Windows service.
/// </summary>
internal static class RecoveryServerRuntimePolicy
{
    internal const int ShopServerPort = 7150;

    internal static bool IsApprovedServerVersion(string? fileVersion, string? productVersion, string requiredVersion)
    {
        if (!string.Equals(fileVersion, requiredVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(productVersion))
        {
            return false;
        }

        return string.Equals(productVersion, requiredVersion, StringComparison.Ordinal) ||
               string.Equals(productVersion, requiredVersion + ".0", StringComparison.Ordinal) ||
               Regex.IsMatch(productVersion, @"\A" + Regex.Escape(requiredVersion) + @"\+[0-9a-fA-F]{40}\z",
                   RegexOptions.CultureInvariant);
    }

    internal static bool IsApprovedServerImagePath(string? configuredPath, string? programFilesDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) ||
            string.IsNullOrWhiteSpace(programFilesDirectory) ||
            !Path.IsPathFullyQualified(configuredPath) ||
            !Path.IsPathFullyQualified(programFilesDirectory))
        {
            return false;
        }

        try
        {
            var configuredFullPath = Path.GetFullPath(configuredPath);
            var approvedFullPath = Path.GetFullPath(Path.Combine(
                programFilesDirectory,
                "Edge Retails",
                "server",
                "EdgeRetails.Server.exe"));

            return string.Equals(configuredFullPath, approvedFullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool HasNoReparsePointComponents(string? path, string? trustedRoot)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(trustedRoot) ||
            !Path.IsPathFullyQualified(path) ||
            !Path.IsPathFullyQualified(trustedRoot))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(trustedRoot);
            var relativePath = Path.GetRelativePath(fullRoot, fullPath);
            if (relativePath == "." || Path.IsPathRooted(relativePath) ||
                relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            var current = fullPath;
            while (true)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return (attributes & FileAttributes.Directory) != 0;
                }

                var parent = Path.GetDirectoryName(current);
                if (parent is null ||
                    (parent != fullRoot && !parent.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
                current = parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool HasOnlyLoopbackListeners(IEnumerable<IPEndPoint>? activeListeners, int port = ShopServerPort)
    {
        if (activeListeners is null || port is < 1 or > 65535)
        {
            return false;
        }

        var serverListeners = activeListeners.Where(listener => listener.Port == port).ToArray();
        return serverListeners.Length > 0 && serverListeners.All(listener => IPAddress.IsLoopback(listener.Address));
    }
}
