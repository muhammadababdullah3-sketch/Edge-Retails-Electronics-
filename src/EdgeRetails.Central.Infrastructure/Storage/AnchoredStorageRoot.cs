using System.IO;

namespace EdgeRetails.Central.Infrastructure.Storage;

/// <summary>
/// Performs object-store operations relative to a pinned, validated storage root.
/// Implementations must never turn a relative component list back into a path for I/O.
/// </summary>
internal interface IAnchoredStorageRoot : IDisposable
{
    string AbsoluteRoot { get; }

    void EnsureDirectory(IReadOnlyList<string> components);

    Stream OpenFile(
        IReadOnlyList<string> components,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options = FileOptions.None);

    bool FileExists(IReadOnlyList<string> components);

    void DeleteFile(IReadOnlyList<string> components);

    void FlushToDisk(Stream openFile);

    DateTime GetLastWriteTimeUtc(Stream openFile);

    void DeleteOpenFile(Stream openFile);

    void MoveFileNoReplace(IReadOnlyList<string> source, IReadOnlyList<string> destination);

    IReadOnlyList<AnchoredStorageEntry> EnumerateEntries(
        IReadOnlyList<string> directory,
        string searchPattern);

    bool DeleteDirectoryIfEmpty(IReadOnlyList<string> components);
}

internal readonly record struct AnchoredStorageEntry(string Name, bool IsDirectory, DateTime LastWriteTimeUtc);

/// <summary>
/// Indicates that an anchored lookup found a symbolic link, reparse point, or
/// unsupported filesystem entry. The provider translates this to its stable
/// path-rejection contract instead of reporting an ordinary storage outage.
/// </summary>
internal sealed class UnsafeStorageEntryException : IOException
{
    public UnsafeStorageEntryException(string message) : base(message) { }
}

