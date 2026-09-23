using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Infrastructure.Production.Printing;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8InfrastructureHardeningTests
{
    [Fact]
    public async Task ProcessRunner_LargeStdoutAndStderr_AreBoundedWithoutDeadlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new ProcessRunner();
        var result = await runner.RunAsync(
            GetWindowsPowerShell(),
            new[]
            {
                "-NoProfile",
                "-Command",
                "[Console]::Out.Write(('O'*400000)); [Console]::Error.Write(('E'*400000))"
            },
            null,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(256 * 1024, result.StandardOutput.Length);
        Assert.Equal(256 * 1024, result.StandardError.Length);
    }
    [Fact]
    public async Task JsonPrintJobStore_MalformedJson_FailsControlled()
    {
        var root = CreateTempRoot("PrintJobMalformed");
        var path = Path.Combine(root, "jobs.json");
        await File.WriteAllTextAsync(path, "{not-json");

        try
        {
            var store = new JsonPrintJobStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync("J1"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task JsonPrintJobStore_AtomicWrite_LeavesNoTemporaryAuthority()
    {
        var root = CreateTempRoot("PrintJobAtomic");
        var path = Path.Combine(root, "jobs.json");
        var now = DateTimeOffset.UtcNow;

        try
        {
            var store = new JsonPrintJobStore(path);
            await store.CreateAsync(new PrintJobRecord(
                "J1",
                ProductionDocumentKind.PosSaleReceipt,
                Guid.NewGuid(),
                "POS",
                PrintRequestMode.Initial,
                PrintJobState.Prepared,
                0,
                now,
                now));

            Assert.NotNull(await store.GetAsync("J1"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task JsonPrinterProfileStore_MalformedJson_FailsControlled()
    {
        var root = CreateTempRoot("ProfileMalformed");
        var path = Path.Combine(root, "profiles.json");
        await File.WriteAllTextAsync(path, "{not-json");

        try
        {
            var store = new JsonPrinterProfileStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync("POS"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task JsonPrinterProfileStore_AtomicWrite_LeavesNoTemporaryAuthority()
    {
        var root = CreateTempRoot("ProfileAtomic");
        var path = Path.Combine(root, "profiles.json");

        try
        {
            var store = new JsonPrinterProfileStore(path);
            await store.SaveAsync(new PrinterProfile(
                "POS",
                "Printer",
                PaperKind.Thermal80Mm,
                1,
                false));

            Assert.NotNull(await store.GetAsync("POS"));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task JsonStores_OversizedFiles_AreRejectedBeforeDeserialization()
    {
        var root = CreateTempRoot("OversizedJson");
        var jobs = Path.Combine(root, "jobs.json");
        var profiles = Path.Combine(root, "profiles.json");

        try
        {
            await File.WriteAllBytesAsync(jobs, new byte[(4 * 1024 * 1024) + 1]);
            await File.WriteAllBytesAsync(profiles, new byte[(1024 * 1024) + 1]);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new JsonPrintJobStore(jobs).GetAsync("J1"));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new JsonPrinterProfileStore(profiles).GetAsync("POS"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProcessRunner_Cancellation_StopsLongRunningProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var runner = new ProcessRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                GetWindowsPowerShell(),
                new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" },
                null,
                cancellation.Token));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task MaintenanceIntegrityKey_WindowsAcl_IsProtectedAndLimited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot("MaintenanceKeyAcl");
        var path = Path.Combine(root, "maintenance-integrity.key");

        try
        {
            var provider = new FileProductionMaintenanceIntegrityKeyProvider(path);
            var key = await provider.GetIntegrityKeyAsync();
            Assert.Equal(32, key.Length);

            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.True(new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected);

            using var identity = WindowsIdentity.GetCurrent();
            var currentUser = identity.User
                ?? throw new InvalidOperationException("Current user SID is unavailable.");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                currentUser.Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
            };

            var rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();

            Assert.NotEmpty(rules);
            Assert.All(rules, rule =>
            {
                var sid = Assert.IsType<SecurityIdentifier>(rule.IdentityReference);
                Assert.Contains(sid.Value, allowed);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            });

            Assert.Contains(rules, rule =>
                rule.IdentityReference is SecurityIdentifier sid &&
                sid.Equals(currentUser) &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void MaintenanceStateDirectory_WindowsAcl_IsProtected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempRoot("MaintenanceStateAcl");
        try
        {
            _ = new FileProductionMaintenanceBarrier(root, new StaticMaintenanceKeyProvider());
            Assert.True(new DirectoryInfo(root).GetAccessControl().AreAccessRulesProtected);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task MaintenanceIntegrityKey_InvalidExistingLength_IsRejected()
    {
        var root = CreateTempRoot("MaintenanceKeyLength");
        var path = Path.Combine(root, "maintenance-integrity.key");
        await File.WriteAllBytesAsync(path, new byte[31]);

        try
        {
            var provider = new FileProductionMaintenanceIntegrityKeyProvider(path);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => provider.GetIntegrityKeyAsync());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(ProductionMaintenanceState.RestorePreparing)]
    [InlineData(ProductionMaintenanceState.RecoveryRequired)]
    public async Task EfTransactionRunner_NonNormalMaintenanceState_BlocksBeforeBusinessMutation(
        ProductionMaintenanceState state)
    {
        var blockedConnectionString =
            "Host=127.0.0.1;Port=1;Database=blocked;Username=blocked;Password=" +
            "blocked;Timeout=1";
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(blockedConnectionString)
            .Options;
        await using var db = new EdgeRetailsDbContext(options);
        var runner = new EfTransactionRunner(
            db,
            new ProductionMaintenanceWriteGuard(new FixedMaintenanceBarrier(state)));
        var operationCalled = false;

        var error = await Assert.ThrowsAsync<ProductionMaintenanceException>(
            () => runner.ExecuteAsync(
                _ =>
                {
                    operationCalled = true;
                    return Task.FromResult(1);
                },
                CancellationToken.None));

        Assert.Equal(state, error.State);
        Assert.False(operationCalled);
    }

    private static string GetWindowsPowerShell()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    private static string CreateTempRoot(string name)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "EdgeRetailsSprint8",
            name,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class StaticMaintenanceKeyProvider
        : IProductionMaintenanceIntegrityKeyProvider
    {
        public Task<byte[]> GetIntegrityKeyAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    }

    private sealed class FixedMaintenanceBarrier(ProductionMaintenanceState state)
        : IProductionMaintenanceBarrier
    {
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(
            ProductionMaintenanceState requestedState,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProductionMaintenanceState> GetStateAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(state);
    }
}
