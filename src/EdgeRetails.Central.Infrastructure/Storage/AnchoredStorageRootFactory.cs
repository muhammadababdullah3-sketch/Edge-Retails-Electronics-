namespace EdgeRetails.Central.Infrastructure.Storage;

internal static class AnchoredStorageRootFactory
{
    public static IAnchoredStorageRoot OpenOrCreate(string absoluteRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteRoot);

        if (OperatingSystem.IsWindows())
        {
            return WindowsAnchoredStorageRoot.OpenOrCreate(absoluteRoot);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxAnchoredStorageRoot.OpenOrCreate(absoluteRoot);
        }

        throw new PlatformNotSupportedException("Anchored object storage is not supported on this operating system.");
    }
}
