using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Recovery;
using EdgeRetails.Infrastructure.Production.Recovery;
using Microsoft.Extensions.Configuration;

namespace EdgeRetails.UnitTests;

public sealed class SignedRecoveryAuthorizationValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Protected_machine_trust_accepts_separate_ProgramData_users_read_and_create_aces()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows ACL certification required.");
        }

        var rules = TrustedDirectoryRules(
            Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
            Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Write));

        Assert.True(ProtectedFileRecoveryAuthorizationTrustProvider.HasTrustedDirectoryAccessRules(rules, allowProgramDataCreate: true));
        Assert.False(ProtectedFileRecoveryAuthorizationTrustProvider.HasTrustedDirectoryAccessRules(rules, allowProgramDataCreate: false));
    }

    [Fact]
    public void Protected_machine_trust_still_rejects_users_delete_rights()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows ACL certification required.");
        }

        var rules = TrustedDirectoryRules(
            Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
            Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Write | FileSystemRights.Delete));

        Assert.False(ProtectedFileRecoveryAuthorizationTrustProvider.HasTrustedDirectoryAccessRules(rules, allowProgramDataCreate: true));
    }

    [Fact]
    public void Protected_machine_trust_requires_users_read_even_when_ProgramData_create_is_allowed()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows ACL certification required.");
        }

        var rules = TrustedDirectoryRules(Rule(WellKnownSidType.BuiltinUsersSid, FileSystemRights.Write));

        Assert.False(ProtectedFileRecoveryAuthorizationTrustProvider.HasTrustedDirectoryAccessRules(rules, allowProgramDataCreate: true));
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule[] TrustedDirectoryRules(params FileSystemAccessRule[] usersRules) =>
    [
        Rule(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
        Rule(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
        ..usersRules
    ];

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule Rule(WellKnownSidType principal, FileSystemRights rights) =>
        new(new SecurityIdentifier(principal, null), rights, AccessControlType.Allow);

    [Fact]
    public async Task Unprovisioned_recovery_trust_fails_closed()
    {
        using var rsa = RSA.Create(2048);
        var validator = CreateValidator(new UnprovisionedRecoveryAuthorizationTrustProvider());

        var result = await validator.ValidateAsync("{}", Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("recovery.authorization_not_provisioned", result.FailureCode);
    }

    [Fact]
    public void Recovery_action_is_narrowly_scoped_to_account_pin_reset()
    {
        Assert.Equal("RESET_ACCOUNT_PIN", SignedRecoveryAuthorizationValidator.RequiredAction);
    }

    [Fact]
    public void Configured_recovery_trust_accepts_only_a_valid_public_key_and_issuer()
    {
        using var rsa = RSA.Create(2048);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RecoveryAuthorization:IssuerId"] = "governance-recovery-v1",
            ["RecoveryAuthorization:PublicKeyPem"] = rsa.ExportSubjectPublicKeyInfoPem()
        }).Build();

        var trust = new ConfiguredRecoveryAuthorizationTrustProvider(configuration).GetTrust();

        Assert.Equal("governance-recovery-v1", trust.IssuerId);
        Assert.Equal(rsa.ExportSubjectPublicKeyInfoPem(), trust.PublicKeyPem);
    }

    [Fact]
    public void Protected_file_recovery_trust_rejects_a_key_outside_the_canonical_machine_location()
    {
        using var rsa = RSA.Create(3072);
        var directory = Path.Combine(Path.GetTempPath(), "EdgeRetailsRecoveryTrustTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var trustPath = Path.Combine(directory, "trust.json");
        try
        {
            File.WriteAllText(trustPath, JsonSerializer.Serialize(new
            {
                RecoveryAuthorization = new
                {
                    IssuerId = "governance-recovery-v1",
                    PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem()
                }
            }, JsonOptions));

            var trust = new ProtectedFileRecoveryAuthorizationTrustProvider(trustPath).GetTrust();

            Assert.Null(trust.IssuerId);
            Assert.Null(trust.PublicKeyPem);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Protected_file_recovery_trust_fails_closed_when_the_trust_file_is_missing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "EdgeRetailsRecoveryTrustTests", Guid.NewGuid().ToString("N"), "trust.json");

        var trust = new ProtectedFileRecoveryAuthorizationTrustProvider(missingPath).GetTrust();

        Assert.Null(trust.IssuerId);
        Assert.Null(trust.PublicKeyPem);
    }

    [Theory]
    [InlineData("not a PEM key")]
    [InlineData("-----BEGIN " + "PRIVATE KEY-----\\nnot-runtime-trust\\n-----END PRIVATE KEY-----")]
    public void Configured_recovery_trust_fails_closed_for_invalid_or_private_material(string pem)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RecoveryAuthorization:IssuerId"] = "governance-recovery-v1",
            ["RecoveryAuthorization:PublicKeyPem"] = pem
        }).Build();

        var trust = new ConfiguredRecoveryAuthorizationTrustProvider(configuration).GetTrust();

        Assert.Null(trust.IssuerId);
        Assert.Null(trust.PublicKeyPem);
    }

    [Fact]
    public async Task Valid_signature_with_license_device_action_target_and_expiry_binding_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        var target = Guid.NewGuid();
        var nonce = Guid.NewGuid();
        var trust = new RecoveryAuthorizationTrust(rsa.ExportSubjectPublicKeyInfoPem(), "governance-recovery-v1");
        var validator = CreateValidator(trust);
        var token = Sign(rsa, new RecoveryAuthorizationPayload(
            trust.IssuerId!, "license-123", "device-456", SignedRecoveryAuthorizationValidator.RequiredAction,
            Now.AddMinutes(-1), Now.AddMinutes(5), nonce, target));

        var result = await validator.ValidateAsync(token, target, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(nonce, result.Authorization!.Nonce);
        Assert.Equal(target, result.Authorization.TargetUserId);
        Assert.Equal(trust.IssuerId, result.Authorization.IssuerId);
    }

    [Fact]
    public async Task Invalid_signature_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var trust = new RecoveryAuthorizationTrust(rsa.ExportSubjectPublicKeyInfoPem(), "governance-recovery-v1");
        var validator = CreateValidator(trust);
        var target = Guid.NewGuid();
        var token = Sign(attacker, Payload(target));

        var result = await validator.ValidateAsync(token, target, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("recovery.authorization_invalid", result.FailureCode);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("license")]
    [InlineData("device")]
    [InlineData("action")]
    [InlineData("issuer")]
    public async Task Invalid_signed_bindings_are_rejected(string mutation)
    {
        using var rsa = RSA.Create(2048);
        var target = Guid.NewGuid();
        var trust = new RecoveryAuthorizationTrust(rsa.ExportSubjectPublicKeyInfoPem(), "governance-recovery-v1");
        var validator = CreateValidator(trust);
        var payload = Payload(target) with
        {
            ExpiresAt = mutation == "expiry" ? Now.AddSeconds(-1) : Now.AddMinutes(5),
            LicenseId = mutation == "license" ? "other-license" : "license-123",
            DeviceId = mutation == "device" ? "other-device" : "device-456",
            Action = mutation == "action" ? "LICENSE_REPLACE" : SignedRecoveryAuthorizationValidator.RequiredAction,
            IssuerId = mutation == "issuer" ? "untrusted-issuer" : trust.IssuerId!
        };

        var result = await validator.ValidateAsync(Sign(rsa, payload), target, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Target_substitution_and_overlong_authorization_are_rejected()
    {
        using var rsa = RSA.Create(2048);
        var target = Guid.NewGuid();
        var trust = new RecoveryAuthorizationTrust(rsa.ExportSubjectPublicKeyInfoPem(), "governance-recovery-v1");
        var validator = CreateValidator(trust);
        var token = Sign(rsa, Payload(target));

        var wrongTarget = await validator.ValidateAsync(token, Guid.NewGuid(), CancellationToken.None);
        var tooLarge = await validator.ValidateAsync(new string('x', 33 * 1024), target, CancellationToken.None);

        Assert.False(wrongTarget.IsValid);
        Assert.False(tooLarge.IsValid);
    }

    private static SignedRecoveryAuthorizationValidator CreateValidator(RecoveryAuthorizationTrust trust)
        => CreateValidator(new TestTrustProvider(trust));

    private static SignedRecoveryAuthorizationValidator CreateValidator(IRecoveryAuthorizationTrustProvider trustProvider)
    {
        var store = new TestLicenseStore();
        var licenseValidator = new TestLicenseValidator();
        var runtimeLicense = new RuntimeLicenseService(store, licenseValidator);
        var device = new TestDeviceIdentityProvider("device-456");
        return new SignedRecoveryAuthorizationValidator(
            trustProvider, runtimeLicense, device, new FrozenTimeProvider(Now));
    }

    private static RecoveryAuthorizationPayload Payload(Guid target) => new(
        "governance-recovery-v1", "license-123", "device-456", SignedRecoveryAuthorizationValidator.RequiredAction,
        Now.AddMinutes(-1), Now.AddMinutes(5), Guid.NewGuid(), target);

    private static string Sign(RSA rsa, RecoveryAuthorizationPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return JsonSerializer.Serialize(new SignedRecoveryAuthorization(
            SignedRecoveryAuthorizationValidator.RequiredAlgorithm,
            Convert.ToBase64String(bytes),
            Convert.ToBase64String(signature)), JsonOptions);
    }

    private sealed class TestTrustProvider(RecoveryAuthorizationTrust trust) : IRecoveryAuthorizationTrustProvider
    {
        public RecoveryAuthorizationTrust GetTrust() => trust;
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDeviceIdentityProvider(string deviceId) : IDeviceIdentityProvider
    {
        public Task<string> GetDeviceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(deviceId);
    }

    private sealed class TestLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("valid-license");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestLicenseValidator : ILicenseValidator
    {
        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseValidationResult(LicenseValidationStatus.Valid,
                new LicensePayload("license-123", "Fixture", "Fixture", "Test", Now, null, "device-456", null, []),
                null));
    }
}
