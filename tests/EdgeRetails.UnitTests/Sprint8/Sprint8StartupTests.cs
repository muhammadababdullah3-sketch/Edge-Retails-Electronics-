using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Infrastructure.Production.Licensing;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8StartupTests
{
    [Fact]
    public async Task StartupOrder_FailsAtDatabaseWithoutDemoFallback()
    {
        using var rsa = RSA.Create(2048);
        var raw = Signed(rsa, "D1");
        var license = new RuntimeLicenseService(new MemoryStore(raw), CreateValidator(rsa, "D1"));
        var coordinator = new ProductionStartupCoordinator(license, new Db(false), new Migration(true), new Maintenance(ProductionMaintenanceState.Normal), new Setup(true), new Session(false));
        var result = await coordinator.RunAsync();
        Assert.Equal(StartupDisposition.Blocked, result.Disposition);
        Assert.Equal("Database", result.Stage);
    }

    [Theory]
    [InlineData(ProductionMaintenanceState.RestorePreparing)]
    [InlineData(ProductionMaintenanceState.RestoreCutover)]
    [InlineData(ProductionMaintenanceState.RecoveryRequired)]
    public async Task Startup_NonNormalMaintenanceState_BlocksBeforeSetupAndSession(
        ProductionMaintenanceState state)
    {
        using var rsa = RSA.Create(2048);
        var raw = Signed(rsa, "D1");
        var license = new RuntimeLicenseService(new MemoryStore(raw), CreateValidator(rsa, "D1"));
        var setup = new TrackingSetup();
        var session = new TrackingSession();
        var coordinator = new ProductionStartupCoordinator(
            license,
            new Db(true),
            new Migration(true),
            new Maintenance(state),
            setup,
            session);

        var result = await coordinator.RunAsync();

        Assert.Equal(StartupDisposition.Blocked, result.Disposition);
        Assert.Equal("Maintenance", result.Stage);
        Assert.Equal(
            state == ProductionMaintenanceState.RecoveryRequired
                ? "maintenance.recovery_required"
                : "maintenance.restore_active",
            result.ErrorCode);
        Assert.False(setup.Called);
        Assert.False(session.Called);
    }

    [Fact]
    public async Task ValidProductionStartup_ReachesLogin()
    {
        using var rsa = RSA.Create(2048);
        var raw = Signed(rsa, "D1");
        var license = new RuntimeLicenseService(new MemoryStore(raw), CreateValidator(rsa, "D1"));
        var coordinator = new ProductionStartupCoordinator(license, new Db(true), new Migration(true), new Maintenance(ProductionMaintenanceState.Normal), new Setup(true), new Session(true));
        var result = await coordinator.RunAsync();
        Assert.Equal(StartupDisposition.LoginReady, result.Disposition);
        Assert.NotNull(result.RecoveryHint);
    }

    private static SignedLicenseValidator CreateValidator(RSA rsa, string device)
        => new(new RsaSha256LicenseSignatureVerifier(new Key(rsa.ExportSubjectPublicKeyInfoPem())), new Device(device));

    private static string Signed(RSA rsa, string device)
    {
        var payload = new LicensePayload("L1", "C", "S", "P", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), device, null, new[] { "Electronics" });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var sig = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new SignedLicenseEnvelope("RS256", Convert.ToBase64String(bytes), Convert.ToBase64String(sig)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class MemoryStore(string raw) : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(raw);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Key(string pem) : ILicensePublicKeyProvider { public string GetPublicKeyPem() => pem; }
    private sealed class Device(string id) : IDeviceIdentityProvider { public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id); }
    private sealed class Db(bool ready) : IDatabaseReadinessProbe { public Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DatabaseReadinessResult(ready, ready ? "ok" : "db unavailable")); }
    private sealed class Migration(bool ok) : IMigrationCompatibilityProbe { public Task<MigrationCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MigrationCompatibilityResult(ok, ok ? "ok" : "pending")); }
    private sealed class Maintenance(ProductionMaintenanceState state) : IProductionMaintenanceBarrier
    {
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState requestedState, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(state);
    }
    private sealed class Setup(bool ok) : ISetupStateProbe { public Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SetupStateResult(ok, ok ? "ok" : "setup")); }

    private sealed class TrackingSetup : ISetupStateProbe
    {
        public bool Called { get; private set; }
        public Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new SetupStateResult(true, "ok"));
        }
    }

    private sealed class Session(bool recovery) : ISessionRecoveryProbe { public Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SessionRecoveryResult(recovery, recovery ? "Recover prior session after login." : null)); }

    private sealed class TrackingSession : ISessionRecoveryProbe
    {
        public bool Called { get; private set; }
        public Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new SessionRecoveryResult(false, null));
        }
    }
}
