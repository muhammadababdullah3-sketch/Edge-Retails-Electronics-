using System.Text;
using System.Runtime.InteropServices;
using System.Reflection;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Infrastructure.Storage;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Storage;

public class DiskObjectStorageContainmentTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _storageDirectory;
    private readonly DiskObjectStorageProvider _provider;
    private readonly List<string> _directoryLinks = new();
    private readonly List<string> _fileLinks = new();

    public DiskObjectStorageContainmentTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"central_storage_containment_{Guid.NewGuid():N}");
        _storageDirectory = Path.Combine(_testDirectory, "objects");
        _provider = new DiskObjectStorageProvider(_storageDirectory);
    }

    [Fact]
    public async Task SFR17_ContainedAbsolutePath_SupportsCiphertextLifecycle()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("owned ciphertext");
        using var content = new MemoryStream(bytes);

        var storagePath = await _provider.SaveCiphertextAsync(shopId, objectId, content);

        Assert.Equal(Path.Combine(_storageDirectory, shopId.ToString("N"), $"{objectId:N}.bin"), storagePath);
        Assert.True(Path.IsPathFullyQualified(storagePath));
        Assert.True(await _provider.ExistsAsync(storagePath));
        await using (var stored = await _provider.GetCiphertextAsync(storagePath))
        {
            using var copy = new MemoryStream();
            await stored.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        await _provider.DeleteCiphertextAsync(storagePath);
        await _provider.DeleteCiphertextAsync(storagePath);

        Assert.False(await _provider.ExistsAsync(storagePath));
        var exception = await Assert.ThrowsAsync<CentralDomainException>(() => _provider.GetCiphertextAsync(storagePath));
        Assert.Equal("OBJECT_NOT_FOUND", exception.Code);
        Assert.DoesNotContain(_testDirectory, exception.ToString());
        Assert.DoesNotContain(storagePath, exception.ToString());
    }

    [Fact]
    public async Task SFR17_RelativePath_IsResolvedAgainstStorageRoot()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("relative ciphertext"));
        var absolutePath = await _provider.SaveCiphertextAsync(shopId, objectId, content);
        var relativePath = Path.Combine(shopId.ToString("N"), "..", shopId.ToString("N"), $"{objectId:N}.bin");

        Assert.True(await _provider.ExistsAsync(relativePath));
        await using (var stored = await _provider.GetCiphertextAsync(relativePath))
        {
            Assert.Equal(content.Length, stored.Length);
        }

        await _provider.DeleteCiphertextAsync(relativePath);

        Assert.False(await _provider.ExistsAsync(absolutePath));
    }

    [Theory]
    [InlineData("sibling-prefix")]
    [InlineData("parent-relative")]
    [InlineData("outside-rooted")]
    [InlineData("dotdot-rooted")]
    [InlineData("storage-root")]
    [InlineData("relative-root")]
    [InlineData("relative-parent")]
    public async Task SFR17_OutsideOrRootPath_IsRejectedBeforeAccess(string pathKind)
    {
        var siblingDirectory = Path.Combine(_testDirectory, "objects-sibling");
        var outsideDirectory = Path.Combine(_testDirectory, "outside");
        Directory.CreateDirectory(siblingDirectory);
        Directory.CreateDirectory(outsideDirectory);
        var siblingFile = Path.Combine(siblingDirectory, "ciphertext.bin");
        var outsideFile = Path.Combine(outsideDirectory, "ciphertext.bin");
        var parentFile = Path.Combine(_testDirectory, "parent.bin");
        var bytes = Encoding.UTF8.GetBytes("outside sentinel");
        await File.WriteAllBytesAsync(siblingFile, bytes);
        await File.WriteAllBytesAsync(outsideFile, bytes);
        await File.WriteAllBytesAsync(parentFile, bytes);
        var storagePath = pathKind switch
        {
            "sibling-prefix" => siblingFile,
            "parent-relative" => Path.Combine("..", "parent.bin"),
            "outside-rooted" => outsideFile,
            "dotdot-rooted" => Path.Combine(_storageDirectory, "..", "outside", "ciphertext.bin"),
            "storage-root" => _storageDirectory,
            "relative-root" => ".",
            "relative-parent" => "..",
            _ => throw new ArgumentOutOfRangeException(nameof(pathKind))
        };

        await AssertRejectedByPathOperations(storagePath);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(siblingFile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(parentFile));
        Assert.True(Directory.Exists(_storageDirectory));
    }

    [Fact]
    public async Task SFR17_MissingOutsidePath_IsRejectedWithoutExistenceDisclosure()
    {
        var outsidePath = Path.Combine(_testDirectory, "objects-sibling", "missing.bin");

        await AssertRejectedByPathOperations(outsidePath);

        Assert.False(Directory.Exists(Path.GetDirectoryName(outsidePath)));
    }

    [Theory]
    [InlineData("root-relative")]
    [InlineData("drive-relative")]
    public async Task SFR17_PartiallyRootedPath_IsRejectedOnWindows(string pathKind)
    {
        // These forms only have partial-root semantics on Windows. Other platforms still
        // exercise the required absolute, relative, sibling, parent and link cases.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var storagePath = pathKind == "root-relative"
            ? $"{Path.DirectorySeparatorChar}object.bin"
            : $"{Path.GetPathRoot(_storageDirectory)![..2]}object.bin";

        await AssertRejectedByPathOperations(storagePath);
    }

    [Theory]
    [InlineData("dotdot-space")]
    [InlineData("alternate-stream")]
    [InlineData("trailing-dot")]
    public async Task SFR17_WindowsAliasedComponent_IsRejectedBeforeAccess(string pathKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var storagePath = pathKind switch
        {
            "dotdot-space" => Path.Combine(".. ", "outside.bin"),
            "alternate-stream" => "object.bin:stream",
            "trailing-dot" => "object.bin.",
            _ => throw new ArgumentOutOfRangeException(nameof(pathKind))
        };

        await AssertRejectedByPathOperations(storagePath);
        await AssertRejectedByPathOperations(Path.Combine("missing-directory", storagePath));
    }

    [Fact]
    public async Task SFR17_LinkedShopDirectory_RejectsAllOperationsWithoutTouchingTarget()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var targetDirectory = Path.Combine(_testDirectory, "outside-shop");
        Directory.CreateDirectory(targetDirectory);
        var outsideFile = Path.Combine(targetDirectory, $"{objectId:N}.bin");
        var bytes = Encoding.UTF8.GetBytes("linked outside sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        var linkedDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        CreateRequiredDirectoryLink(linkedDirectory, targetDirectory);

        await AssertRejectedByPathOperations(Path.Combine(linkedDirectory, $"{objectId:N}.bin"));
        await AssertSaveRejected(shopId, objectId);
        await AssertSaveRejected(shopId, Guid.NewGuid());

        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Single(Directory.GetFiles(targetDirectory));
    }

    [Fact]
    public async Task SFR17_LinkedFile_RejectsAllOperationsWithoutTouchingTarget()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var shopDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        Directory.CreateDirectory(shopDirectory);
        var outsideFile = Path.Combine(_testDirectory, "outside-file.bin");
        var bytes = Encoding.UTF8.GetBytes("linked file sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        var linkedFile = Path.Combine(shopDirectory, $"{objectId:N}.bin");
        CreateRequiredFileLink(linkedFile, outsideFile);

        await AssertRejectedByPathOperations(linkedFile);
        await AssertSaveRejected(shopId, objectId);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
    }

    [Fact]
    public async Task SFR17_HardLinkedFile_IsRejectedWithoutTouchingAliasedBytes()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var shopDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        Directory.CreateDirectory(shopDirectory);
        var outsideFile = Path.Combine(_testDirectory, "outside-hardlink-target.bin");
        var linkedFile = Path.Combine(shopDirectory, $"{objectId:N}.bin");
        var bytes = Encoding.UTF8.GetBytes("hard-link alias sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        CreateRequiredHardLink(linkedFile, outsideFile);

        await AssertRejectedByPathOperations(linkedFile);
        await AssertSaveRejected(shopId, objectId);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(linkedFile));
    }

    [Theory]
    [InlineData(FileMode.Create)]
    [InlineData(FileMode.Truncate)]
    public async Task SFR17_DestructiveOpenModes_FailClosedBeforeTouchingHardLinkedBytes(FileMode mode)
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var shopDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        Directory.CreateDirectory(shopDirectory);
        var outsideFile = Path.Combine(_testDirectory, "outside-destructive-mode-target.bin");
        var linkedFile = Path.Combine(shopDirectory, $"{objectId:N}.bin");
        var bytes = Encoding.UTF8.GetBytes("destructive mode alias sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        CreateRequiredHardLink(linkedFile, outsideFile);

        var factoryType = typeof(DiskObjectStorageProvider).Assembly.GetType(
            "EdgeRetails.Central.Infrastructure.Storage.AnchoredStorageRootFactory",
            throwOnError: true)!;
        var openFactory = factoryType.GetMethod("OpenOrCreate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        using var storageRoot = (IDisposable)openFactory.Invoke(null, new object[] { _storageDirectory })!;
        var openFile = storageRoot.GetType().GetMethod("OpenFile", BindingFlags.Instance | BindingFlags.Public)!;
        var exception = Assert.Throws<TargetInvocationException>(() => openFile.Invoke(
            storageRoot,
            new object[]
            {
                new[] { shopId.ToString("N"), $"{objectId:N}.bin" },
                mode,
                FileAccess.Write,
                FileShare.None,
                FileOptions.None
            }));

        var unsupported = Assert.IsType<PlatformNotSupportedException>(exception.InnerException);
        Assert.Contains("create/truncate", unsupported.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(linkedFile));
    }

    [Fact]
    public async Task SFR17_DeleteOnClose_FailsClosedBeforeRemovingHardLinkNames()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var shopDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        Directory.CreateDirectory(shopDirectory);
        var outsideFile = Path.Combine(_testDirectory, "outside-delete-on-close-target.bin");
        var linkedFile = Path.Combine(shopDirectory, $"{objectId:N}.bin");
        var bytes = Encoding.UTF8.GetBytes("delete-on-close alias sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        CreateRequiredHardLink(linkedFile, outsideFile);

        var factoryType = typeof(DiskObjectStorageProvider).Assembly.GetType(
            "EdgeRetails.Central.Infrastructure.Storage.AnchoredStorageRootFactory",
            throwOnError: true)!;
        var openFactory = factoryType.GetMethod("OpenOrCreate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        using var storageRoot = (IDisposable)openFactory.Invoke(null, new object[] { _storageDirectory })!;
        var openFile = storageRoot.GetType().GetMethod("OpenFile", BindingFlags.Instance | BindingFlags.Public)!;
        var exception = Assert.Throws<TargetInvocationException>(() => openFile.Invoke(
            storageRoot,
            new object[]
            {
                new[] { shopId.ToString("N"), $"{objectId:N}.bin" },
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.DeleteOnClose
            }));

        Assert.IsAssignableFrom<NotSupportedException>(exception.InnerException);
        Assert.True(File.Exists(outsideFile));
        Assert.True(File.Exists(linkedFile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(linkedFile));
    }

    [Fact]
    public async Task SFR17_DanglingFileLink_RejectsAllOperationsWithoutCreatingTarget()
    {
        var shopId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var shopDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        Directory.CreateDirectory(shopDirectory);
        var outsideFile = Path.Combine(_testDirectory, "missing-link-target.bin");
        var linkedFile = Path.Combine(shopDirectory, $"{objectId:N}.bin");
        CreateRequiredFileLink(linkedFile, outsideFile);

        await AssertRejectedByPathOperations(linkedFile);
        await AssertSaveRejected(shopId, objectId);

        Assert.False(File.Exists(outsideFile));
    }

    [Fact]
    public void SFR17_LinkedStorageRoot_IsRejectedBeforeDirectoryCreation()
    {
        var outsideDirectory = Path.Combine(_testDirectory, "outside-root");
        Directory.CreateDirectory(outsideDirectory);
        var linkedRoot = Path.Combine(_testDirectory, "linked-root");
        CreateRequiredDirectoryLink(linkedRoot, outsideDirectory);

        var exception = Assert.Throws<CentralDomainException>(() => new DiskObjectStorageProvider(linkedRoot));

        AssertPathRejected(exception);
        Assert.Empty(Directory.GetFileSystemEntries(outsideDirectory));
    }

    [Fact]
    public void SFR17_LinkedRootAncestor_IsRejectedBeforeDirectoryCreation()
    {
        var outsideDirectory = Path.Combine(_testDirectory, "outside-ancestor");
        Directory.CreateDirectory(outsideDirectory);
        var linkedAncestor = Path.Combine(_testDirectory, "linked-ancestor");
        CreateRequiredDirectoryLink(linkedAncestor, outsideDirectory);

        var exception = Assert.Throws<CentralDomainException>(() =>
            new DiskObjectStorageProvider(Path.Combine(linkedAncestor, "new-storage")));

        AssertPathRejected(exception);
        Assert.Empty(Directory.GetFileSystemEntries(outsideDirectory));
    }

    [Fact]
    public async Task SFR17_PinnedRootAncestorCannotRedirectOperationsAfterRename()
    {
        var ownedAncestor = Path.Combine(_testDirectory, "ancestor");
        var nestedRoot = Path.Combine(ownedAncestor, "objects");
        using var provider = new DiskObjectStorageProvider(nestedRoot);
        var outsideDirectory = Path.Combine(_testDirectory, "replacement-ancestor");
        var outsideRoot = Path.Combine(outsideDirectory, "objects");
        Directory.CreateDirectory(outsideRoot);
        var outsideFile = Path.Combine(outsideRoot, "sentinel.bin");
        var bytes = Encoding.UTF8.GetBytes("ancestor sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        var originalFile = Path.Combine(nestedRoot, "sentinel.bin");
        await File.WriteAllBytesAsync(originalFile, bytes);

        var movedAncestor = Path.Combine(_testDirectory, "moved-ancestor");
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => Directory.Move(ownedAncestor, movedAncestor));
        }
        else
        {
            Directory.Move(ownedAncestor, movedAncestor);
            Directory.CreateSymbolicLink(ownedAncestor, outsideRoot);
        }
        var storagePath = Path.Combine(nestedRoot, "sentinel.bin");
        await using var stored = await provider.GetCiphertextAsync(storagePath);
        using var copy = new MemoryStream();
        await stored.CopyToAsync(copy);

        Assert.Equal(bytes, copy.ToArray());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Single(Directory.GetFileSystemEntries(outsideRoot));
    }

    [Fact]
    public async Task SFR17_PinnedStorageRootCannotRedirectOperationsAfterRename()
    {
        var outsideDirectory = Path.Combine(_testDirectory, "replacement-root");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "sentinel.bin");
        var bytes = Encoding.UTF8.GetBytes("replacement sentinel");
        await File.WriteAllBytesAsync(outsideFile, bytes);
        var originalFile = Path.Combine(_storageDirectory, "sentinel.bin");
        await File.WriteAllBytesAsync(originalFile, bytes);

        var movedRoot = Path.Combine(_testDirectory, "moved-root");
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => Directory.Move(_storageDirectory, movedRoot));
        }
        else
        {
            Directory.Move(_storageDirectory, movedRoot);
            Directory.CreateSymbolicLink(_storageDirectory, outsideDirectory);
        }
        await using var stored = await _provider.GetCiphertextAsync(originalFile);
        using var copy = new MemoryStream();
        await stored.CopyToAsync(copy);

        Assert.Equal(bytes, copy.ToArray());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outsideFile));
        Assert.Single(Directory.GetFileSystemEntries(outsideDirectory));
    }

    [Fact]
    public async Task SFR17_StorageIoFailure_DoesNotExposePhysicalPath()
    {
        var shopId = Guid.NewGuid();
        var shopDirectory = Path.Combine(_storageDirectory, shopId.ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("owned blocking file");
        await File.WriteAllBytesAsync(shopDirectory, bytes);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("must not be written"));

        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _provider.SaveCiphertextAsync(shopId, Guid.NewGuid(), content));

        Assert.Equal("STORAGE_IO_FAILED", exception.Code);
        Assert.DoesNotContain(_testDirectory, exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Equal(0L, content.Position);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(shopDirectory));
    }

    [Fact]
    public async Task SFR17_BlankPath_PreservesRequiredReadAndNoOpDeleteExists()
    {
        var exception = await Assert.ThrowsAsync<CentralDomainException>(() => _provider.GetCiphertextAsync(" "));
        Assert.Equal("STORAGE_PATH_REQUIRED", exception.Code);
        await _provider.DeleteCiphertextAsync(" ");
        Assert.False(await _provider.ExistsAsync(" "));
    }

    private async Task AssertRejectedByPathOperations(string storagePath)
    {
        AssertPathRejected(await Assert.ThrowsAsync<CentralDomainException>(() => _provider.GetCiphertextAsync(storagePath)));
        AssertPathRejected(await Assert.ThrowsAsync<CentralDomainException>(() => _provider.DeleteCiphertextAsync(storagePath)));
        AssertPathRejected(await Assert.ThrowsAsync<CentralDomainException>(() => _provider.ExistsAsync(storagePath)));
    }

    private async Task AssertSaveRejected(Guid shopId, Guid objectId)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("must not be written"));
        var exception = await Assert.ThrowsAsync<CentralDomainException>(() => _provider.SaveCiphertextAsync(shopId, objectId, content));

        AssertPathRejected(exception);
        Assert.Equal(0L, content.Position);
    }

    private void AssertPathRejected(CentralDomainException exception)
    {
        Assert.Equal("PATH_TRAVERSAL_DETECTED", exception.Code);
        Assert.Equal("storage.path_traversal", exception.SafeMessageKey);
        Assert.DoesNotContain(_testDirectory, exception.ToString());
        Assert.Null(exception.InnerException);
    }

    private void CreateRequiredDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            _directoryLinks.Add(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                "Required directory-link fixture could not be created. On Windows enable Developer Mode or run the coordinator with symbolic-link privilege; link coverage must not be skipped.",
                exception);
        }
    }

    private void CreateRequiredFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            _fileLinks.Add(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException(
                "Required file-link fixture could not be created. On Windows enable Developer Mode or run the coordinator with symbolic-link privilege; link coverage must not be skipped.",
                exception);
        }
    }

    private static void CreateRequiredHardLink(string linkPath, string targetPath)
    {
        var created = OperatingSystem.IsWindows()
            ? CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero)
            : CreateHardLinkUnix(targetPath, linkPath) == 0;
        if (!created)
        {
            throw new IOException($"Required hard-link fixture could not be created (native error {Marshal.GetLastPInvokeError()}).");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingFileName, string newFileName);

    public void Dispose()
    {
        _provider.Dispose();

        // Unlink fixtures before recursively deleting only this test's unique owned directory.
        foreach (var linkPath in _fileLinks)
        {
            File.Delete(linkPath);
        }

        foreach (var linkPath in _directoryLinks)
        {
            Directory.Delete(linkPath);
        }

        Directory.Delete(_testDirectory, recursive: true);
    }
}
