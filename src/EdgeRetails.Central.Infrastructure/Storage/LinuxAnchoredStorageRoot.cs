using System.ComponentModel;
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EdgeRetails.Central.Infrastructure.Storage;

/// <summary>
/// Linux implementation of the anchored object-store filesystem. All descendant
/// lookups and mutations are relative to directory descriptors opened with
/// O_NOFOLLOW. The configured root and every component are pinned while used.
/// Missing components are created owner-only; the storage root and descendants
/// must be service-user-owned and not group/other-writable. Writable ancestors
/// are accepted only where sticky-directory ownership protects the entry.
/// </summary>
internal sealed class LinuxAnchoredStorageRoot : IAnchoredStorageRoot
{
    private const int O_RDONLY = 0;
    private const int O_WRONLY = 1;
    private const int O_RDWR = 2;
    private const int O_CREAT = 0x40;
    private const int O_EXCL = 0x80;
    private const int O_TRUNC = 0x200;
    private const int O_APPEND = 0x400;
    private const int O_CLOEXEC = 0x80000;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_PATH = 0x200000;
    private const int LOCK_SH = 1;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int RENAME_NOREPLACE = 1;
    private const int AT_REMOVEDIR = 0x200;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_TYPE = 0x0001;
    private const uint STATX_NLINK = 0x0004;
    private const uint STATX_UID = 0x0008;
    private const uint STATX_MTIME = 0x0040;
    private const uint STATX_INO = 0x0100;
    private const uint STATX_BASIC_STATS = 0x07ff;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFREG = 0x8000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFLNK = 0xA000;
    private const ushort S_IWGRP = 0x0010;
    private const ushort S_IWOTH = 0x0002;
    private const ushort S_ISVTX = 0x0200;
    private static readonly ConcurrentDictionary<string, object> MutationGates = new(StringComparer.Ordinal);

    private readonly SafeFileHandle _rootHandle;
    private readonly object _mutationGate;
    private bool _disposed;

    internal static IAnchoredStorageRoot OpenOrCreate(string absoluteRoot) => new LinuxAnchoredStorageRoot(absoluteRoot);

    internal LinuxAnchoredStorageRoot(string absoluteRoot)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The anchored Linux storage adapter requires Linux.");
        }

        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("The Linux storage adapter supports only 64-bit x64 and arm64 runtimes.");
        }

        if (string.IsNullOrWhiteSpace(absoluteRoot) || !Path.IsPathFullyQualified(absoluteRoot))
        {
            throw new ArgumentException("An absolute storage root is required.", nameof(absoluteRoot));
        }

        AbsoluteRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteRoot));
        if (AbsoluteRoot.Length == 0)
        {
            AbsoluteRoot = Path.GetPathRoot(Path.GetFullPath(absoluteRoot))!;
        }

        _mutationGate = MutationGates.GetOrAdd(AbsoluteRoot, static _ => new object());
        _rootHandle = OpenAbsoluteDirectory(AbsoluteRoot);
    }

    public string AbsoluteRoot { get; }

    public void EnsureDirectory(IReadOnlyList<string> components)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(components);
        var fd = DupCloexec(Fd(_rootHandle));
        if (fd < 0)
        {
            ThrowNative("dup root");
        }

        var current = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            foreach (var component in components)
            {
                ValidateComponent(component);
                ValidatePrivateStorageDirectory(current);
                if (Native.mkdirat(Fd(current), component, Convert.ToUInt32("700", 8)) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno != Native.EEXIST)
                    {
                        ThrowNative("mkdirat", errno);
                    }
                }
                var next = Native.openat(Fd(current), component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                if (next < 0)
                {
                    ThrowNative("openat directory component");
                }

                var nextHandle = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                try
                {
                    ValidatePrivateStorageDirectory(nextHandle);
                }
                catch
                {
                    nextHandle.Dispose();
                    throw;
                }
                current.Dispose();
                current = nextHandle;
            }
        }
        finally
        {
            current.Dispose();
        }
    }

    public Stream OpenFile(IReadOnlyList<string> components, FileMode mode, FileAccess access, FileShare share, FileOptions options = FileOptions.None)
    {
        lock (_mutationGate)
        {
            return OpenFileUnderMutationGate(components, mode, access, share, options);
        }
    }

    private Stream OpenFileUnderMutationGate(IReadOnlyList<string> components, FileMode mode, FileAccess access, FileShare share, FileOptions options)
    {
        ThrowIfDisposed();
        ValidateLeafComponents(components);
        if (access == 0 || (access & ~(FileAccess.ReadWrite)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        if (share is not (FileShare.None or FileShare.Read))
        {
            throw new PlatformNotSupportedException("Linux anchored storage supports only exclusive access or shared readers.");
        }

        if (share == FileShare.Read && access != FileAccess.Read)
        {
            throw new PlatformNotSupportedException("Shared-read sharing is supported only for read-only handles.");
        }

        if ((options & ~FileOptions.Asynchronous) != 0)
        {
            throw new PlatformNotSupportedException("The Linux anchored storage adapter supports only asynchronous stream options.");
        }

        if ((mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate or FileMode.Append) && access == FileAccess.Read)
        {
            throw new ArgumentException("The selected file mode requires write access.", nameof(access));
        }

        if (mode == FileMode.Append && access != FileAccess.Write)
        {
            throw new ArgumentException("Append mode requires write-only access.", nameof(access));
        }

        // These modes combine opening with truncation. Reject them until the adapter can
        // prove the single-link identity and make the destructive operation atomic.
        if (mode is FileMode.Create or FileMode.Truncate)
        {
            throw new PlatformNotSupportedException("Destructive create/truncate modes are not supported by anchored storage.");
        }

        var flags = access switch { FileAccess.Read => O_RDONLY, FileAccess.Write => O_WRONLY, _ => O_RDWR };
        flags |= O_CLOEXEC | O_NOFOLLOW;
        flags |= mode switch
        {
            FileMode.CreateNew => O_CREAT | O_EXCL,
            FileMode.Create => O_CREAT | O_TRUNC,
            FileMode.Open => 0,
            FileMode.OpenOrCreate => O_CREAT,
            FileMode.Truncate => O_TRUNC,
            FileMode.Append => O_CREAT | O_APPEND,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var parent = OpenDirectoryPrefix(components, components.Count - 1);
        try
        {
            if (mode != FileMode.CreateNew)
            {
                var existingFd = Native.openat(Fd(parent), components[^1], O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
                if (existingFd >= 0)
                {
                    using var existing = new SafeFileHandle((IntPtr)existingFd, ownsHandle: true);
                    ValidateRegularFile(GetStat(existing), components[^1]);
                }
                else
                {
                    var existingErrno = Marshal.GetLastPInvokeError();
                    if (existingErrno != Native.ENOENT || mode is FileMode.Open or FileMode.Truncate)
                    {
                        ThrowNative("openat existing file for type/link validation", existingErrno);
                    }
                }
            }
        }
        catch
        {
            parent.Dispose();
            throw;
        }
        var fd = Native.openat(Fd(parent), components[^1], flags, Convert.ToUInt32("600", 8));
        if (fd < 0)
        {
            parent.Dispose();
            ThrowNative("openat");
        }
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            ValidateRegularFile(GetStat(handle), components[^1]);
            if (share == FileShare.None)
            {
                if (Native.flock(fd, LOCK_EX | LOCK_NB) != 0)
                {
                    ThrowNative("flock");
                }
            }
            else if (access == FileAccess.Read)
            {
                if (Native.flock(fd, LOCK_SH | LOCK_NB) != 0)
                {
                    ThrowNative("flock");
                }
            }
            return new AnchoredFileStream(handle, access, parent, components[^1], this, options.HasFlag(FileOptions.Asynchronous));
        }
        catch
        {
            handle.Dispose();
            parent.Dispose();
            throw;
        }
    }

    public bool FileExists(IReadOnlyList<string> components)
    {
        ThrowIfDisposed();
        ValidateLeafComponents(components);
        using var parent = OpenDirectoryPrefix(components, components.Count - 1);
        var fd = Native.openat(Fd(parent), components[^1], O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Native.ENOENT)
            {
                return false;
            }

            ThrowNative("openat");
        }
        using var leaf = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        var stat = GetStat(leaf);
        return (stat.Mode & S_IFMT) switch
        {
            S_IFREG when stat.LinkCount == 1 => true,
            S_IFREG => throw UnsafeEntry(components[^1]),
            S_IFDIR => false,
            _ => throw UnsafeEntry(components[^1])
        };
    }

    public void DeleteFile(IReadOnlyList<string> components)
    {
        lock (_mutationGate)
        {
        ThrowIfDisposed();
        ValidateLeafComponents(components);
        using var parent = OpenDirectoryPrefix(components, components.Count - 1);
        var fd = Native.openat(Fd(parent), components[^1], O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0)
        {
            ThrowNative("openat");
        }

        using (var leaf = new SafeFileHandle((IntPtr)fd, ownsHandle: true))
        {
            ValidateRegularFile(GetStat(leaf), components[^1]);
        }
        if (Native.unlinkat(Fd(parent), components[^1], 0) != 0)
        {
            ThrowNative("unlinkat");
        }
        }
    }

    public void MoveFileNoReplace(IReadOnlyList<string> source, IReadOnlyList<string> destination)
    {
        lock (_mutationGate)
        {
        ThrowIfDisposed();
        ValidateLeafComponents(source);
        ValidateLeafComponents(destination);
        using var sourceParent = OpenDirectoryPrefix(source, source.Count - 1);
        using var destinationParent = OpenDirectoryPrefix(destination, destination.Count - 1);
        var sourceFd = Native.openat(Fd(sourceParent), source[^1], O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
        if (sourceFd < 0)
        {
            ThrowNative("openat");
        }

        using (var sourceHandle = new SafeFileHandle((IntPtr)sourceFd, ownsHandle: true))
        {
            ValidateRegularFile(GetStat(sourceHandle), source[^1]);
        }
        if (Native.renameat2(Fd(sourceParent), source[^1], Fd(destinationParent), destination[^1], RENAME_NOREPLACE) != 0)
        {
            ThrowNative("renameat2(RENAME_NOREPLACE)");
        }
        }
    }

    public IReadOnlyList<AnchoredStorageEntry> EnumerateEntries(IReadOnlyList<string> directory, string searchPattern)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(searchPattern);
        using var directoryHandle = OpenDirectoryPrefix(directory, directory.Count);
        var duplicate = DupCloexec(Fd(directoryHandle));
        if (duplicate < 0)
        {
            ThrowNative("dup");
        }

        var dir = Native.fdopendir(duplicate);
        if (dir == IntPtr.Zero)
        {
            Native.close(duplicate);
            ThrowNative("fdopendir");
        }

        var results = new List<AnchoredStorageEntry>();
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = Native.readdir(dir);
                if (entry == IntPtr.Zero)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno != 0)
                    {
                        ThrowNative("readdir", errno);
                    }

                    break;
                }
                var record = Marshal.PtrToStructure<DirentPrefix>(entry);
                var prefixSize = Marshal.SizeOf<DirentPrefix>();
                if (record.RecordLength <= prefixSize)
                {
                    throw new IOException("Linux returned a malformed directory entry.");
                }

                var name = Marshal.PtrToStringUTF8(entry + prefixSize);
                if (string.IsNullOrEmpty(name) || name is "." or "..")
                {
                    continue;
                }

                ValidateComponent(name);
                if (!FileSystemName.MatchesSimpleExpression(searchPattern, name, ignoreCase: false))
                {
                    continue;
                }

                var fd = Native.openat(Fd(directoryHandle), name, O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
                if (fd < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == Native.ENOENT)
                    {
                        continue;
                    }

                    ThrowNative("openat");
                }
                using var child = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
                var stat = GetStat(child);
                var kind = stat.Mode & S_IFMT;
                if (kind == S_IFLNK || (kind != S_IFREG && kind != S_IFDIR))
                {
                    throw UnsafeEntry(name);
                }
                if (kind == S_IFREG)
                {
                    ValidateRegularFile(stat, name);
                }

                results.Add(new AnchoredStorageEntry(name, kind == S_IFDIR, ToUtc(stat.MTime.Seconds, stat.MTime.Nanoseconds)));
            }
        }
        finally
        {
            Native.closedir(dir);
        }
        return results;
    }

    public DateTime GetLastWriteTimeUtc(Stream openFile)
    {
        var anchored = RequireOwnedStream(openFile);
        var stat = GetStat(anchored.SafeFileHandle);
        return ToUtc(stat.MTime.Seconds, stat.MTime.Nanoseconds);
    }

    public void DeleteOpenFile(Stream openFile)
    {
        lock (_mutationGate)
        {
        var anchored = RequireOwnedStream(openFile);
        if (anchored.Deleted)
        {
            throw new IOException("The open storage file has already been deleted.");
        }

        var opened = GetStat(anchored.SafeFileHandle);
        ValidateRegularFile(opened, anchored.LeafName);

        var currentFd = Native.openat(Fd(anchored.Parent), anchored.LeafName, O_PATH | O_NOFOLLOW | O_CLOEXEC, 0);
        if (currentFd < 0)
        {
            ThrowNative("openat pending file");
        }

        using var current = new SafeFileHandle((IntPtr)currentFd, ownsHandle: true);
        var currentStat = GetStat(current);
        if ((currentStat.Mode & S_IFMT) != S_IFREG || currentStat.LinkCount != 1 || currentStat.Inode != opened.Inode ||
            currentStat.DevMajor != opened.DevMajor || currentStat.DevMinor != opened.DevMinor)
        {
            throw new IOException("The pending storage name no longer identifies the exclusively opened file.");
        }

        // The parent descriptor and basename are captured when the file is opened;
        // cleanup never reconstructs or resolves the configured storage path.
        if (Native.unlinkat(Fd(anchored.Parent), anchored.LeafName, 0) != 0)
        {
            ThrowNative("unlinkat open file");
        }

        anchored.Deleted = true;
        }
    }

    public void FlushToDisk(Stream openFile)
    {
        var anchored = RequireOwnedStream(openFile);
        if (Native.fsync(Fd(anchored.SafeFileHandle)) != 0)
        {
            ThrowNative("fsync");
        }
    }

    public bool DeleteDirectoryIfEmpty(IReadOnlyList<string> components)
    {
        lock (_mutationGate)
        {
        ThrowIfDisposed();
        ValidateLeafComponents(components);
        using var parent = OpenDirectoryPrefix(components, components.Count - 1);
        var fd = Native.openat(Fd(parent), components[^1], O_PATH | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Native.ENOENT)
            {
                return false;
            }

            ThrowNative("openat");
        }
        using (var directoryHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: true)) { }
        if (Native.unlinkat(Fd(parent), components[^1], AT_REMOVEDIR) == 0)
        {
            return true;
        }

        var deleteErrno = Marshal.GetLastPInvokeError();
        if (deleteErrno is Native.ENOTEMPTY or Native.EEXIST)
        {
            return false;
        }

        if (deleteErrno == Native.ENOENT)
        {
            return false;
        }

        ThrowNative("unlinkat(AT_REMOVEDIR)", deleteErrno);
        return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rootHandle.Dispose();
    }

    private SafeFileHandle OpenAbsoluteDirectory(string absolutePath)
    {
        var root = Native.open("/", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
        if (root < 0)
        {
            ThrowNative("open root");
        }

        var current = new SafeFileHandle((IntPtr)root, ownsHandle: true);
        try
        {
            var components = absolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in components)
            {
                ValidateComponent(component);
                var next = Native.openat(Fd(current), component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                if (next < 0)
                {
                    var openErrno = Marshal.GetLastPInvokeError();
                    if (openErrno != Native.ENOENT)
                    {
                        ThrowNative("openat root component", openErrno);
                    }

                    // Create only after a no-follow lookup established that the
                    // component is absent. If another actor wins the race,
                    // mkdirat reports EEXIST and the no-follow open below
                    // validates that the winner created a real directory.
                    if (Native.mkdirat(Fd(current), component, Convert.ToUInt32("700", 8)) != 0)
                    {
                        var mkdirErrno = Marshal.GetLastPInvokeError();
                        if (mkdirErrno != Native.EEXIST)
                        {
                            ThrowNative("mkdirat root component", mkdirErrno);
                        }
                    }

                    next = Native.openat(Fd(current), component, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                    if (next < 0)
                    {
                        ThrowNative("openat created root component");
                    }
                }
                var nextHandle = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                try
                {
                    ValidateStableNamespaceParent(current, nextHandle);
                }
                catch
                {
                    nextHandle.Dispose();
                    throw;
                }
                current.Dispose();
                current = nextHandle;
            }
            ValidatePrivateStorageDirectory(current);
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private SafeFileHandle OpenDirectoryPrefix(IReadOnlyList<string> components, int count)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (count < 0 || count > components.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var fd = DupCloexec(Fd(_rootHandle));
        if (fd < 0)
        {
            ThrowNative("dup root");
        }

        var current = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            for (var i = 0; i < count; i++)
            {
                ValidateComponent(components[i]);
                var next = Native.openat(Fd(current), components[i], O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, 0);
                if (next < 0)
                {
                    ThrowNative("openat directory component");
                }

                var nextHandle = new SafeFileHandle((IntPtr)next, ownsHandle: true);
                try
                {
                    ValidatePrivateStorageDirectory(nextHandle);
                }
                catch
                {
                    nextHandle.Dispose();
                    throw;
                }
                current.Dispose();
                current = nextHandle;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static Statx GetStat(SafeFileHandle handle)
    {
        var stat = new Statx();
        if (Native.statx(Fd(handle), "", AT_EMPTY_PATH, STATX_BASIC_STATS, ref stat) != 0)
        {
            ThrowNative("statx");
        }

        if ((stat.Mask & (STATX_TYPE | STATX_NLINK | STATX_UID | STATX_MTIME | STATX_INO)) != (STATX_TYPE | STATX_NLINK | STATX_UID | STATX_MTIME | STATX_INO))
        {
            throw new PlatformNotSupportedException("The filesystem did not provide required statx metadata.");
        }

        return stat;
    }

    private static void ValidatePrivateStorageDirectory(SafeFileHandle directory)
    {
        var stat = GetStat(directory);
        if ((stat.Mode & S_IFMT) != S_IFDIR || stat.UserId != Native.geteuid() ||
            (stat.Mode & (S_IWGRP | S_IWOTH)) != 0)
        {
            throw new UnsafeStorageEntryException("Linux object-storage directories must be owned by the service user and not writable by group or other users.");
        }
    }

    private static void ValidateRegularFile(Statx stat, string name)
    {
        if ((stat.Mode & S_IFMT) != S_IFREG || stat.LinkCount != 1)
        {
            throw UnsafeEntry(name);
        }
    }

    private static void ValidateStableNamespaceParent(SafeFileHandle parent, SafeFileHandle child)
    {
        var parentStat = GetStat(parent);
        if ((parentStat.Mode & (S_IWGRP | S_IWOTH)) == 0)
        {
            return;
        }

        var childStat = GetStat(child);
        var sticky = (parentStat.Mode & S_ISVTX) != 0;
        var serviceOwnsProtectedEntry = childStat.UserId == Native.geteuid() || parentStat.UserId == Native.geteuid();
        if (!sticky || !serviceOwnsProtectedEntry)
        {
            throw new UnsafeStorageEntryException("Linux object-storage root has a writable ancestor that does not protect its directory entry.");
        }
    }

    private AnchoredFileStream RequireOwnedStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is not AnchoredFileStream anchored || !ReferenceEquals(anchored.Owner, this) || anchored.SafeFileHandle.IsClosed)
        {
            throw new ArgumentException("The stream is not an open file from this anchored storage root.", nameof(stream));
        }

        return anchored;
    }

    private static DateTime ToUtc(long seconds, uint nanoseconds)
    {
        try { return DateTime.UnixEpoch.AddSeconds(seconds).AddTicks(nanoseconds / 100); }
        catch (ArgumentOutOfRangeException exception) { throw new IOException("Filesystem timestamp is outside the supported range.", exception); }
    }

    private static void ValidateLeafComponents(IReadOnlyList<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0)
        {
            throw new ArgumentException("A leaf component is required.", nameof(components));
        }

        foreach (var component in components)
        {
            ValidateComponent(component);
        }
    }

    private static void ValidateComponent(string component)
    {
        if (string.IsNullOrEmpty(component) || component is "." or ".." || component.Contains('/') || component.Contains('\0'))
        {
            throw new ArgumentException("Storage path contains an invalid component.");
        }
    }

    private static UnsafeStorageEntryException UnsafeEntry(string name) => new($"Storage entry '{name}' is a link or unsupported file type.");
    private void ThrowIfDisposed() { if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LinuxAnchoredStorageRoot));
        }
    }
    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private static int DupCloexec(int fd)
    {
        var duplicate = Native.fcntl(fd, Native.F_DUPFD_CLOEXEC, 0);
        if (duplicate < 0)
        {
            ThrowNative("fcntl(F_DUPFD_CLOEXEC)");
        }

        return duplicate;
    }

    private static void ThrowNative(string operation, int? errno = null)
    {
        var error = errno ?? Marshal.GetLastPInvokeError();
        if (error is Native.ELOOP or Native.ENOTDIR)
        {
            throw new UnsafeStorageEntryException($"Linux storage operation {operation} encountered a link or non-directory component.");
        }
        if (error == Native.ENOENT)
        {
            throw new FileNotFoundException($"Linux storage entry was not found during {operation}.");
        }
        throw new IOException($"Linux storage operation {operation} failed: {new Win32Exception(error).Message} (errno {error}).");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DirentPrefix { public ulong Inode; public long Offset; public ushort RecordLength; public byte Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatxTimestamp { public long Seconds; public uint Nanoseconds; public int Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Statx
    {
        public uint Mask; public uint BlockSize; public ulong Attributes; public uint LinkCount; public uint UserId; public uint GroupId;
        public ushort Mode; public ushort Spare0; public ulong Inode; public ulong Size; public ulong Blocks; public ulong AttributesMask;
        public StatxTimestamp ATime; public StatxTimestamp BTime; public StatxTimestamp CTime; public StatxTimestamp MTime;
        public uint RDevMajor; public uint RDevMinor; public uint DevMajor; public uint DevMinor; public ulong MountId; public uint DioMemoryAlignment; public uint DioOffsetAlignment;
        public ulong Spare1; public ulong Spare2; public ulong Spare3; public ulong Spare4; public ulong Spare5; public ulong Spare6;
        public ulong Spare7; public ulong Spare8; public ulong Spare9; public ulong Spare10; public ulong Spare11; public ulong Spare12;
    }

    private sealed class AnchoredFileStream : FileStream
    {
        internal AnchoredFileStream(SafeFileHandle handle, FileAccess access, SafeFileHandle parent, string name, LinuxAnchoredStorageRoot owner, bool isAsync)
            : base(handle, access, 81920, isAsync)
        {
            Parent = parent;
            LeafName = name;
            Owner = owner;
        }

        internal SafeFileHandle Parent { get; }
        internal string LeafName { get; }
        internal LinuxAnchoredStorageRoot Owner { get; }
        internal bool Deleted { get; set; }

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing)
                {
                    Parent.Dispose();
                }
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync().ConfigureAwait(false); }
            finally { Parent.Dispose(); }
        }
    }

    private static class Native
    {
        internal const int ENOENT = 2, EEXIST = 17, ENOTDIR = 20, ELOOP = 40, ENOTEMPTY = 39, F_DUPFD_CLOEXEC = 1030;
        private const string LibC = "libc";
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)] internal static extern int open(string path, int flags, uint mode);
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)] internal static extern int openat(int dirfd, string path, int flags, uint mode);
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)] internal static extern int mkdirat(int dirfd, string path, uint mode);
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)] internal static extern int unlinkat(int dirfd, string path, int flags);
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "renameat2")] internal static extern int renameat2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags);
        [DllImport(LibC, SetLastError = true)] internal static extern int fcntl(int fd, int command, int argument);
        [DllImport(LibC, SetLastError = true)] internal static extern int close(int fd);
        [DllImport(LibC, SetLastError = true)] internal static extern int flock(int fd, int operation);
        [DllImport(LibC, SetLastError = true)] internal static extern int fsync(int fd);
        [DllImport(LibC)] internal static extern uint geteuid();
        [DllImport(LibC, SetLastError = true)] internal static extern IntPtr fdopendir(int fd);
        [DllImport(LibC, SetLastError = true)] internal static extern IntPtr readdir(IntPtr directory);
        [DllImport(LibC, SetLastError = true)] internal static extern int closedir(IntPtr directory);
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)] internal static extern int statx(int dirfd, string path, int flags, uint mask, ref Statx stat);
    }
}
