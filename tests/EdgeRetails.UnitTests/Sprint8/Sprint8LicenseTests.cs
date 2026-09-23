using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Infrastructure.Production.Licensing;
using Xunit;

namespace EdgeRetails.UnitTests.Sprint8;

public sealed class Sprint8LicenseTests
{
    [Fact]
    public async Task ValidSignedDeviceBoundLicense_Passes()
    {
        using var rsa = RSA.Create(2048);
        var device = "DEVICE-001";
        var json = BuildSignedLicense(rsa, device, DateTimeOffset.UtcNow.AddDays(10));
        var validator = CreateValidator(rsa, device);
        var result = await validator.ValidateAsync(json);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ExpiredLicense_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddMinutes(-1));
        var validator = CreateValidator(rsa, "DEVICE-001");
        var result = await validator.ValidateAsync(json);
        Assert.Equal(LicenseValidationStatus.Expired, result.Status);
    }

    [Fact]
    public async Task CorruptOrWrongSignature_FailsClosed()
    {
        using var signer = RSA.Create(2048);
        using var wrongKey = RSA.Create(2048);
        var json = BuildSignedLicense(signer, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var validator = CreateValidator(wrongKey, "DEVICE-001");
        var result = await validator.ValidateAsync(json);
        Assert.Equal(LicenseValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public async Task DeviceMismatch_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var validator = CreateValidator(rsa, "DEVICE-999");
        var result = await validator.ValidateAsync(json);
        Assert.Equal(LicenseValidationStatus.DeviceMismatch, result.Status);
    }

    [Fact]
    public async Task CorruptJson_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var result = await CreateValidator(rsa, "DEVICE-001").ValidateAsync("{not-json");
        Assert.Equal(LicenseValidationStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task MissingPersistedLicense_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var runtime = new RuntimeLicenseService(new EmptyStore(), CreateValidator(rsa, "DEVICE-001"));
        var result = await runtime.ValidatePersistedAsync();
        Assert.Equal(LicenseValidationStatus.Missing, result.Status);
    }

    [Fact]
    public async Task UnsupportedAlgorithm_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var envelope = JsonSerializer.Deserialize<SignedLicenseEnvelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var unsupported = JsonSerializer.Serialize(
            envelope with { Algorithm = "HS256" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var result = await CreateValidator(rsa, "DEVICE-001").ValidateAsync(unsupported);

        Assert.Equal(LicenseValidationStatus.UnsupportedAlgorithm, result.Status);
    }

    [Fact]
    public async Task OversizedLicense_FailsClosedBeforeParsing()
    {
        using var rsa = RSA.Create(2048);
        var oversized = new string('X', (1024 * 1024) + 1);

        var result = await CreateValidator(rsa, "DEVICE-001").ValidateAsync(oversized);

        Assert.Equal(LicenseValidationStatus.Corrupt, result.Status);
        Assert.Equal("license.too_large", result.ErrorCode);
    }

    [Fact]
    public async Task MalformedPublicVerificationMaterial_FailsClosed()
    {
        using var signer = RSA.Create(2048);
        var json = BuildSignedLicense(signer, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var validator = new SignedLicenseValidator(
            new RsaSha256LicenseSignatureVerifier(new KeyProvider("not-a-pem")),
            new DeviceProvider("DEVICE-001"));

        var result = await validator.ValidateAsync(json);

        Assert.Equal(LicenseValidationStatus.ValidationUnavailable, result.Status);
        Assert.Equal("license.crypto_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task PrivatePemAsVerificationMaterial_FailsClosed()
    {
        using var signer = RSA.Create(2048);
        var json = BuildSignedLicense(signer, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var validator = new SignedLicenseValidator(
            new RsaSha256LicenseSignatureVerifier(new KeyProvider(signer.ExportPkcs8PrivateKeyPem())),
            new DeviceProvider("DEVICE-001"));

        var result = await validator.ValidateAsync(json);

        Assert.Equal(LicenseValidationStatus.ValidationUnavailable, result.Status);
        Assert.Equal("license.crypto_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task IncompleteEnvelope_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var malformed = JsonSerializer.Serialize(
            new SignedLicenseEnvelope("RS256", Convert.ToBase64String(new byte[] { 1 }), string.Empty),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var result = await CreateValidator(rsa, "DEVICE-001").ValidateAsync(malformed);

        Assert.Equal(LicenseValidationStatus.Corrupt, result.Status);
        Assert.Equal("license.incomplete_envelope", result.ErrorCode);
    }

    [Fact]
    public async Task StorageReadFailure_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var runtime = new RuntimeLicenseService(new ThrowingReadStore(), CreateValidator(rsa, "DEVICE-001"));

        var result = await runtime.ValidatePersistedAsync();

        Assert.Equal(LicenseValidationStatus.ValidationUnavailable, result.Status);
        Assert.Equal("license.validation_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentInitialFileImports_OnlyOneWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsLicenseRace", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "license.json");
        try
        {
            var first = new FileLicenseStore(path);
            var second = new FileLicenseStore(path);

            var results = await Task.WhenAll(
                first.TryPersistInitialRawAsync("license-A"),
                second.TryPersistInitialRawAsync("license-B"));

            Assert.Equal(1, results.Count(x => x));
            var persisted = await new FileLicenseStore(path).ReadRawAsync();
            Assert.Contains(persisted, new[] { "license-A", "license-B" });
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ExistingLicense_CannotBeOverwrittenByInitialImport()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsLicenseExisting", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "license.json");
        try
        {
            var store = new FileLicenseStore(path);
            Assert.True(await store.TryPersistInitialRawAsync("first-license"));
            Assert.False(await store.TryPersistInitialRawAsync("second-license"));
            Assert.Equal("first-license", await store.ReadRawAsync());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RestartAfterPersistence_ValidatesPersistedLicense()
    {
        var root = Path.Combine(Path.GetTempPath(), "EdgeRetailsLicenseRestart", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "license.json");
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(10));

        try
        {
            await new FileLicenseStore(path).PersistRawAsync(json);

            var restartedRuntime = new RuntimeLicenseService(
                new FileLicenseStore(path),
                CreateValidator(rsa, "DEVICE-001"));

            var result = await restartedRuntime.ValidatePersistedAsync();
            Assert.Equal(LicenseValidationStatus.Valid, result.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task StorageWriteFailure_FailsClosed()
    {
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var service = new LicenseInstallationService(
            CreateValidator(rsa, "DEVICE-001"),
            new ThrowingWriteStore(),
            Audit());

        var result = await service.ValidateAndPersistInitialAsync(json, "license-test");

        Assert.Equal(LicenseValidationStatus.ValidationUnavailable, result.Status);
        Assert.Equal("license.persistence_failed", result.ErrorCode);
    }

    [Fact]
    public async Task InitialInstallation_PersistsValidatedLicense()
    {
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var store = new RecordingStore();
        var audit = Audit();
        var service = new LicenseInstallationService(CreateValidator(rsa, "DEVICE-001"), store, audit);
        var handler = new ImportLicenseHandler(store, service, audit);

        var result = await handler.HandleAsync(json, "initial-install");

        Assert.Equal(LicenseValidationStatus.Valid, result.Status);
        Assert.Equal(json, store.Raw);
    }

    [Fact]
    public async Task AuthorizedReplacement_RequiresSettingsManageAndPersists()
    {
        using var rsa = RSA.Create(2048);
        var json = BuildSignedLicense(rsa, "DEVICE-001", DateTimeOffset.UtcNow.AddDays(1));
        var store = new RecordingStore { Raw = "old-license" };
        var authorization = new RecordingAuthorization();
        var service = new LicenseInstallationService(
            CreateValidator(rsa, "DEVICE-001"),
            store,
            Audit());
        var handler = new ReplaceLicenseHandler(authorization, service);

        var result = await handler.HandleAsync(json, "replace-license");

        Assert.Equal(LicenseValidationStatus.Valid, result.Status);
        Assert.Equal(ProductionPermissionNames.SettingsManage, authorization.LastPermission);
        Assert.Equal(json, store.Raw);
    }

    private static ProductionAuditCoordinator Audit()
        => new(new NullAuditSink(), new NullAuditFailureReporter());

    private static SignedLicenseValidator CreateValidator(RSA rsa, string deviceId)
        => new(new RsaSha256LicenseSignatureVerifier(new KeyProvider(rsa.ExportSubjectPublicKeyInfoPem())), new DeviceProvider(deviceId));

    private static string BuildSignedLicense(RSA rsa, string deviceId, DateTimeOffset expiry)
    {
        var payload = new LicensePayload("LIC-1", "Customer", "Store", "ExistingPolicyPlan", DateTimeOffset.UtcNow, expiry, deviceId, null, new[] { "Electronics" });
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var signature = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new SignedLicenseEnvelope("RS256", Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed class EmptyStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class ThrowingReadStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => throw new IOException("read failed");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingWriteStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new IOException("write failed");
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => throw new IOException("write failed");
    }

    private sealed class RecordingStore : ILicenseStore
    {
        public string? Raw { get; set; }
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Raw is not null);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult(Raw);
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        {
            if (Raw is not null) { return Task.FromResult(false); }
            Raw = signedLicenseJson;
            return Task.FromResult(true);
        }
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        {
            Raw = signedLicenseJson;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAuthorization : IProductionAuthorization
    {
        public string? LastPermission { get; private set; }
        public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default)
        {
            LastPermission = permission;
            return Task.CompletedTask;
        }
    }

    private sealed class NullAuditSink : IProductionAuditSink
    {
        public Task AppendAsync(ProductionAuditRecord record, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullAuditFailureReporter : IProductionAuditFailureReporter
    {
        public Task ReportAsync(
            ProductionAuditRecord record,
            Exception error,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

    private sealed class KeyProvider(string pem) : ILicensePublicKeyProvider { public string GetPublicKeyPem() => pem; }
    private sealed class DeviceProvider(string id) : IDeviceIdentityProvider { public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id); }
}
