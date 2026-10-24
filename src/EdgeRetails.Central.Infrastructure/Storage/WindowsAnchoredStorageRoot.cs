using System.ComponentModel;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EdgeRetails.Central.Infrastructure.Storage;

/// <summary>
/// Windows implementation that pins the storage root and performs each lookup relative
/// to an already-open directory handle. Reparse points are opened as objects and rejected;
/// they are never traversed.
/// </summary>
internal sealed class WindowsAnchoredStorageRoot : IAnchoredStorageRoot
{
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint ObjDontReparse = 0x00001000;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonalert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileSequentialOnly = 0x00000004;
    private const uint FileWriteThrough = 0x00000002;
    private const uint FileRandomAccess = 0x00000800;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint FileOverwrite = 4;
    private const uint FileOverwriteIf = 5;
    private const uint FileReadData = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileAppendData = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint AttributeDirectory = 0x00000010;
    private const uint AttributeReparsePoint = 0x00000400;
    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileRenameInformation = 10;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileDirectoryInformation = 1;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);
    private const int StatusFileIsADirectory = unchecked((int)0xC00000BA);
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorDirNotEmpty = 145;

    private readonly object _gate = new();
    private readonly SafeFileHandle _rootHandle;
    private readonly DirectoryChain _storageRootChain;
    private bool _disposed;

    public WindowsAnchoredStorageRoot(string absoluteRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteRoot);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The anchored Windows storage adapter is only available on Windows.");
        }

        ValidateRawAbsoluteRoot(absoluteRoot);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(absoluteRoot));
        var driveRoot = Path.GetPathRoot(fullPath);
        if (driveRoot is null || driveRoot.Length != 3 || driveRoot[1] != ':' || driveRoot[2] != Path.DirectorySeparatorChar)
        {
            throw new NotSupportedException("Only local drive-letter storage roots are supported by the anchored Windows adapter.");
        }
        if (GetDriveType(driveRoot) != 3)
        {
            throw new NotSupportedException("The anchored Windows adapter requires a fixed local volume.");
        }
        var fileSystemName = new StringBuilder(64);
        if (!GetVolumeInformation(driveRoot, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, fileSystemName, (uint)fileSystemName.Capacity) ||
            !string.Equals(fileSystemName.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The anchored Windows adapter currently supports NTFS volumes only.");
        }

        AbsoluteRoot = fullPath;
        var nativeDriveRoot = "\\??\\" + driveRoot;
        _rootHandle = OpenAbsoluteDirectory(nativeDriveRoot);
        try
        {
            ValidateDirectoryHandle(_rootHandle);
            var relative = fullPath[driveRoot.Length..];
            var components = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            ValidateComponents(components);
            _storageRootChain = WalkDirectoryFrom(_rootHandle, components, create: true, addFileAccess: true);
        }
        catch
        {
            _rootHandle.Dispose();
            throw;
        }
    }

    internal static IAnchoredStorageRoot OpenOrCreate(string absoluteRoot) => new WindowsAnchoredStorageRoot(absoluteRoot);

    public string AbsoluteRoot { get; }

    public void EnsureDirectory(IReadOnlyList<string> components)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(components);
            using var directory = WalkDirectory(components, create: true);
        }
    }

    public Stream OpenFile(IReadOnlyList<string> components, FileMode mode, FileAccess access, FileShare share, FileOptions options = FileOptions.None)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(components, requireLeaf: true);
            ValidateOptions(options);
            // The native overwrite dispositions truncate as part of opening, before the
            // returned handle can be checked for hard-link aliases. Fail closed instead.
            if (mode is FileMode.Create or FileMode.Truncate)
            {
                throw new PlatformNotSupportedException("Destructive create/truncate modes are not supported by anchored storage.");
            }

            var parent = WalkDirectory(components.Take(components.Count - 1).ToArray(), create: false);
            var leaf = components[^1];
            var disposition = mode switch
            {
                FileMode.CreateNew => FileCreate,
                FileMode.Create => FileOverwriteIf,
                FileMode.Open => FileOpen,
                FileMode.OpenOrCreate => FileOpenIf,
                FileMode.Truncate => FileOverwrite,
                FileMode.Append => FileOpenIf,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            var desiredAccess = access switch
            {
                FileAccess.Read => FileReadData | FileReadAttributes | Synchronize,
                FileAccess.Write => FileWriteData | FileReadAttributes | Synchronize | DeleteAccess,
                FileAccess.ReadWrite => FileReadData | FileWriteData | FileReadAttributes | Synchronize | DeleteAccess,
                _ => throw new ArgumentOutOfRangeException(nameof(access))
            };
            if (mode == FileMode.Append)
            {
                if (access == FileAccess.Read)
                {
                    throw new ArgumentException("Append mode requires write access.", nameof(access));
                }
                desiredAccess = FileAppendData | FileReadAttributes | Synchronize;
            }
            var nativeOptions = FileNonDirectoryFile | FileOpenReparsePoint;
            if ((options & FileOptions.Asynchronous) == 0)
            {
                nativeOptions |= FileSynchronousIoNonalert;
            }
            if ((options & FileOptions.SequentialScan) != 0)
            {
                nativeOptions |= FileSequentialOnly;
            }
            if ((options & FileOptions.RandomAccess) != 0)
            {
                nativeOptions |= FileRandomAccess;
            }
            if ((options & FileOptions.WriteThrough) != 0)
            {
                nativeOptions |= FileWriteThrough;
            }
            SafeFileHandle? handle = null;
            try
            {
                handle = CreateRelative(parent.Handle, leaf, desiredAccess, ToNativeShare(share), disposition, nativeOptions);
                ValidateFileHandle(handle);
                var stream = new FileStream(handle, access, 81920, (options & FileOptions.Asynchronous) != 0);
                if (mode == FileMode.Append)
                {
                    stream.Position = stream.Length;
                }
                return new AnchoredStream(stream, parent);
            }
            catch
            {
                handle?.Dispose();
                parent.Dispose();
                throw;
            }
        }
    }

    public bool FileExists(IReadOnlyList<string> components)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(components, requireLeaf: true);
            try
            {
                using var parent = WalkDirectory(components.Take(components.Count - 1).ToArray(), create: false);
                using var file = CreateRelative(parent.Handle, components[^1], FileReadAttributes | Synchronize, ShareRead | ShareWrite | ShareDelete, FileOpen, FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonalert);
                ValidateFileHandle(file);
                return true;
            }
            catch (NativeStorageException exception) when (IsMissing(exception) || exception.NtStatus == StatusFileIsADirectory)
            {
                return false;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }
    }

    public void DeleteFile(IReadOnlyList<string> components)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(components, requireLeaf: true);
            try
            {
                using var parent = WalkDirectory(components.Take(components.Count - 1).ToArray(), create: false);
                using var file = CreateRelative(parent.Handle, components[^1], DeleteAccess | FileReadAttributes | Synchronize, ShareRead | ShareWrite, FileOpen, FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonalert);
                ValidateFileHandle(file);
                SetDisposition(file);
            }
            catch (NativeStorageException exception) when (IsMissing(exception))
            {
                // Idempotent deletion.
            }
        }
    }

    public void MoveFileNoReplace(IReadOnlyList<string> source, IReadOnlyList<string> destination)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(source, requireLeaf: true);
            ValidateComponents(destination, requireLeaf: true);
            using var sourceParent = WalkDirectory(source.Take(source.Count - 1).ToArray(), create: false);
            using var destinationParent = WalkDirectory(destination.Take(destination.Count - 1).ToArray(), create: false, addFileAccess: true);
            using var file = CreateRelative(sourceParent.Handle, source[^1], DeleteAccess | FileReadAttributes | Synchronize, ShareRead | ShareWrite | ShareDelete, FileOpen, FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonalert);
            ValidateFileHandle(file);
            SetRenameNoReplace(file, destinationParent.Handle, destination[^1]);
        }
    }

    public IReadOnlyList<AnchoredStorageEntry> EnumerateEntries(IReadOnlyList<string> directory, string searchPattern)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(directory);
            ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
            if (searchPattern.Contains('/') || searchPattern.Contains('\\') || searchPattern.Contains(':') || searchPattern.Contains('\0'))
            {
                throw new ArgumentException("Search pattern must be a single filename pattern.", nameof(searchPattern));
            }
            using var handle = WalkDirectory(directory, create: false);
            return EnumerateDirectory(handle.Handle).Where(entry => FileSystemName.MatchesSimpleExpression(searchPattern, entry.Name, ignoreCase: true)).ToArray();
        }
    }

    public bool DeleteDirectoryIfEmpty(IReadOnlyList<string> components)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidateComponents(components, requireLeaf: true);
            try
            {
                using var parent = WalkDirectory(components.Take(components.Count - 1).ToArray(), create: false);
                using var directory = CreateRelative(parent.Handle, components[^1], DeleteAccess | FileReadAttributes | Synchronize | 0x00000001, ShareRead | ShareWrite, FileOpen, FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonalert);
                ValidateDirectoryHandle(directory);
                if (EnumerateDirectory(directory).Count != 0)
                {
                    return false;
                }
                try
                {
                    SetDisposition(directory);
                    return true;
                }
                catch (NativeStorageException exception) when (exception.Win32Error == ErrorDirNotEmpty)
                {
                    return false;
                }
            }
            catch (NativeStorageException exception) when (IsMissing(exception))
            {
                return true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _storageRootChain.Dispose();
            _rootHandle.Dispose();
        }
    }

    public DateTime GetLastWriteTimeUtc(Stream openFile)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        var handle = GetStreamHandle(openFile);
        ValidateFileHandle(handle);
        return DateTime.FromFileTimeUtc(GetBasicInfo(handle).LastWriteTime);
    }

    public void DeleteOpenFile(Stream openFile)
    {
        ArgumentNullException.ThrowIfNull(openFile);
        var handle = GetStreamHandle(openFile);
        ValidateFileHandle(handle);
        SetDisposition(handle);
    }

    public void FlushToDisk(Stream openFile)
    {
        if (openFile is not AnchoredStream anchored)
        {
            throw new ArgumentException("The stream is not owned by this anchored storage root.", nameof(openFile));
        }
        anchored.FlushToDisk();
    }

    private DirectoryChain WalkDirectory(IReadOnlyList<string> components, bool create, bool addFileAccess = false) =>
        WalkDirectoryFrom(_storageRootChain.Handle, components, create, addFileAccess);

    private static DirectoryChain WalkDirectoryFrom(SafeFileHandle anchor, IReadOnlyList<string> components, bool create, bool addFileAccess = false)
    {
        ValidateComponents(components);
        var handles = new List<SafeFileHandle> { DuplicateHandle(anchor) };
        try
        {
            for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
            {
                var component = components[componentIndex];
                var directoryAccess = FileReadAttributes | 0x00000001 | Synchronize;
                if (addFileAccess && componentIndex == components.Count - 1)
                {
                    directoryAccess |= 0x00000002;
                }
                var next = CreateRelative(handles[^1], component, directoryAccess, ShareRead | ShareWrite,
                    create ? FileOpenIf : FileOpen, FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonalert);
                ValidateDirectoryHandle(next);
                handles.Add(next);
            }
            return new DirectoryChain(handles);
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string nativePath) =>
        CreateRelative(null, nativePath, FileReadAttributes | 0x00000001 | Synchronize, ShareRead | ShareWrite | ShareDelete,
            FileOpen, FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonalert, absolute: true);

    private static SafeFileHandle CreateRelative(SafeFileHandle? parent, string name, uint desiredAccess, uint shareAccess, uint disposition, uint createOptions, bool absolute = false)
    {
        var namePointer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var length = checked((ushort)(name.Length * sizeof(char)));
            Marshal.StructureToPtr(new UnicodeString { Length = length, MaximumLength = length, Buffer = namePointer }, unicodePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = absolute ? IntPtr.Zero : parent?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = unicodePointer,
                Attributes = ObjCaseInsensitive | ObjDontReparse
            };
            var status = NtCreateFile(out var handle, desiredAccess, ref attributes, out _, IntPtr.Zero, 0, shareAccess,
                disposition, createOptions | FileOpenReparsePoint, IntPtr.Zero, 0);
            if (status < 0)
            {
                handle?.Dispose();
                if (status == StatusReparsePointEncountered)
                {
                    throw new UnsafeStorageEntryException("Windows encountered a reparse point in the anchored storage path.");
                }
                if (status is StatusObjectNameNotFound or StatusObjectPathNotFound)
                {
                    throw new FileNotFoundException("The anchored storage entry was not found.");
                }
                throw NativeStorageException.FromStatus(status, "NtCreateFile");
            }
            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(namePointer);
        }
    }

    private static SafeFileHandle DuplicateHandle(SafeFileHandle source)
    {
        if (!DuplicateHandleNative(GetCurrentProcess(), source, GetCurrentProcess(), out var duplicate, 0, false, 0x00000002))
        {
            throw new NativeStorageException("Could not duplicate the pinned storage root handle.", Marshal.GetLastWin32Error());
        }
        return duplicate;
    }

    private static void ValidateFileHandle(SafeFileHandle handle)
    {
        var tag = GetTagInfo(handle);
        if ((tag.FileAttributes & (AttributeDirectory | AttributeReparsePoint)) != 0)
        {
            throw new UnsafeStorageEntryException("Storage leaf is not a regular non-reparse file.");
        }

        var standard = GetStandardInfo(handle);
        if (standard.NumberOfLinks != 1)
        {
            throw new UnsafeStorageEntryException("Storage leaf has a hard-link alias outside its canonical object path.");
        }
    }

    private static void ValidateDirectoryHandle(SafeFileHandle handle)
    {
        var tag = GetTagInfo(handle);
        if ((tag.FileAttributes & AttributeReparsePoint) != 0 || (tag.FileAttributes & AttributeDirectory) == 0)
        {
            throw new UnsafeStorageEntryException("Storage component is not a regular non-reparse directory.");
        }
    }

    private static FileAttributeTagInfo GetTagInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out FileAttributeTagInfo info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            throw new NativeStorageException("Could not inspect a storage handle.", Marshal.GetLastWin32Error());
        }
        return info;
    }

    private static FileBasicInfo GetBasicInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo info, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            throw new NativeStorageException("Could not inspect storage timestamps.", Marshal.GetLastWin32Error());
        }
        return info;
    }

    private static FileStandardInfo GetStandardInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo info, (uint)Marshal.SizeOf<FileStandardInfo>()))
        {
            throw new NativeStorageException("Could not inspect a storage file's link count.", Marshal.GetLastWin32Error());
        }
        return info;
    }

    private static void SetDisposition(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(handle, FileDispositionInfo, buffer, 1))
            {
                throw new NativeStorageException("Could not delete the opened storage entry.", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetRenameNoReplace(SafeFileHandle file, SafeFileHandle destinationDirectory, string leaf)
    {
        var encoded = Encoding.Unicode.GetBytes(leaf);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferLength = nameOffset + encoded.Length;
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            for (var index = 0; index < bufferLength; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            Marshal.WriteByte(buffer, 0, 0); // ReplaceIfExists = false.
            Marshal.WriteIntPtr(buffer, rootOffset, destinationDirectory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, encoded.Length);
            Marshal.Copy(encoded, 0, IntPtr.Add(buffer, nameOffset), encoded.Length);
            var status = NtSetInformationFile(file, out _, buffer, (uint)bufferLength, FileRenameInformation);
            if (status < 0)
            {
                throw NativeStorageException.FromStatus(status, "Handle-relative no-replace rename");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<AnchoredStorageEntry> EnumerateDirectory(SafeFileHandle directory)
    {
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var results = new List<AnchoredStorageEntry>();
        var restart = true;
        try
        {
            while (true)
            {
                var status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, buffer, (uint)bufferSize,
                    FileDirectoryInformation, false, IntPtr.Zero, restart);
                restart = false;
                if (status == StatusNoMoreFiles)
                {
                    break;
                }
                if (status < 0)
                {
                    throw NativeStorageException.FromStatus(status, "NtQueryDirectoryFile");
                }

                var offset = 0;
                while (true)
                {
                    var record = IntPtr.Add(buffer, offset);
                    var nextOffset = Marshal.ReadInt32(record, 0);
                    var lastWrite = Marshal.ReadInt64(record, 24);
                    var attributes = unchecked((uint)Marshal.ReadInt32(record, 56));
                    var nameLength = Marshal.ReadInt32(record, 60);
                    if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > bufferSize - 64)
                    {
                        throw new IOException("Windows returned an invalid directory entry.");
                    }
                    var chars = new char[nameLength / sizeof(char)];
                    Marshal.Copy(IntPtr.Add(record, 64), chars, 0, chars.Length);
                    var name = new string(chars);
                    if (name is not "." and not "..")
                    {
                        ValidateComponent(name);
                        results.Add(new AnchoredStorageEntry(name, (attributes & AttributeDirectory) != 0, DateTime.FromFileTimeUtc(lastWrite)));
                    }
                    if (nextOffset == 0)
                    {
                        break;
                    }
                    if (nextOffset < 64 || offset + nextOffset > bufferSize)
                    {
                        throw new IOException("Windows returned an invalid directory record chain.");
                    }
                    offset += nextOffset;
                }
            }
            return results;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ToNativeShare(FileShare share)
    {
        var result = 0u;
        if ((share & FileShare.Read) != 0)
        {
            result |= ShareRead;
        }

        if ((share & FileShare.Write) != 0)
        {
            result |= ShareWrite;
        }

        if ((share & FileShare.Delete) != 0)
        {
            result |= ShareDelete;
        }

        return result;
    }

    private static void ValidateOptions(FileOptions options)
    {
        if ((options & FileOptions.DeleteOnClose) != 0)
        {
            throw new PlatformNotSupportedException("Delete-on-close is not supported by anchored storage.");
        }

        const FileOptions supported = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.RandomAccess | FileOptions.WriteThrough;
        if ((options & ~supported) != 0)
        {
            throw new NotSupportedException("The requested file options are not supported by the anchored Windows adapter.");
        }
    }

    private static void ValidateRawAbsoluteRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new NotSupportedException("The anchored Windows adapter requires a local drive-letter path.");
        }
        var root = Path.GetPathRoot(path) ?? string.Empty;
        foreach (var component in path[root.Length..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateComponent(component);
        }
    }

    private static void ValidateComponents(IReadOnlyList<string> components, bool requireLeaf = false)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (requireLeaf && components.Count == 0)
        {
            throw new ArgumentException("A storage leaf component is required.", nameof(components));
        }
        foreach (var component in components)
        {
            ValidateComponent(component);
        }
    }

    private static void ValidateComponent(string component)
    {
        if (string.IsNullOrWhiteSpace(component) || component is "." or ".." || component.Length > 255 ||
            component.IndexOfAny(new[] { '\\', '/', ':', '\0', '<', '>', '"', '|', '?', '*' }) >= 0 ||
            component.Any(character => character < 32) || component.EndsWith('.') || component.EndsWith(' '))
        {
            throw new ArgumentException("Storage paths must contain only safe single Windows path components.", nameof(component));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsMissing(NativeStorageException exception) => exception.NtStatus is StatusObjectNameNotFound or StatusObjectPathNotFound || exception.Win32Error is ErrorFileNotFound or ErrorPathNotFound;

    private static SafeFileHandle GetStreamHandle(Stream stream) => stream is AnchoredStream anchored
        ? anchored.Handle
        : throw new ArgumentException("The stream was not opened by this anchored storage root.", nameof(stream));

    private sealed class DirectoryChain : IDisposable
    {
        private readonly List<SafeFileHandle> _handles;
        public DirectoryChain(List<SafeFileHandle> handles) => _handles = handles;
        public SafeFileHandle Handle => _handles[^1];
        public void Dispose()
        {
            for (var index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index].Dispose();
            }
        }
    }

    private sealed class AnchoredStream : Stream
    {
        private readonly Stream _inner;
        private DirectoryChain? _parent;
        public AnchoredStream(Stream inner, DirectoryChain parent) { _inner = inner; _parent = parent; }
        public SafeFileHandle Handle => _inner is FileStream file ? file.SafeFileHandle : throw new ObjectDisposedException(nameof(AnchoredStream));
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        internal void FlushToDisk() => ((FileStream)_inner).Flush(flushToDisk: true);
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); }
                finally { Interlocked.Exchange(ref _parent, null)?.Dispose(); }
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _parent, null)?.Dispose(); }
            GC.SuppressFinalize(this);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    private sealed class NativeStorageException : IOException
    {
        public NativeStorageException(string message, int win32Error) : base(message, new Win32Exception(win32Error)) => Win32Error = win32Error;
        private NativeStorageException(string message, int status, int win32Error) : this(message, win32Error) => NtStatus = status;
        public int Win32Error { get; }
        public int NtStatus { get; }
        public static NativeStorageException FromStatus(int status, string operation) => new($"{operation} failed with NTSTATUS 0x{status:X8}.", status, unchecked((int)RtlNtStatusToDosError(status)));
    }

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition,
        uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle fileHandle, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext,
        out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry, IntPtr fileName, [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, out FileAttributeTagInfo information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, out FileBasicInfo information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle fileHandle, int fileInformationClass, out FileStandardInfo information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle fileHandle, int fileInformationClass, IntPtr information, uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer, uint volumeNameSize,
        IntPtr volumeSerialNumber, IntPtr maximumComponentLength, IntPtr fileSystemFlags, StringBuilder fileSystemNameBuffer, uint fileSystemNameSize);

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandleNative(IntPtr sourceProcess, SafeFileHandle sourceHandle, IntPtr targetProcess,
        out SafeFileHandle targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
}
