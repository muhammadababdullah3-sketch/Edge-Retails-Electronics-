using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Application.Production.Recovery;
using Microsoft.Extensions.Configuration;

namespace EdgeRetails.Infrastructure.Production.Recovery;

/// <summary>
/// Verifies one-time Owner PIN recovery authorizations. The recovery trust
/// domain is intentionally independent from the license signing key.
/// </summary>
public sealed class SignedRecoveryAuthorizationValidator : IRecoveryAuthorizationValidator
{
    public const string RequiredAlgorithm = "RS256";
    public const string RequiredAction = "RESET_ACCOUNT_PIN";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private const int MaximumEnvelopeCharacters = 32 * 1024;
    private const int MaximumPayloadBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRecoveryAuthorizationTrustProvider _trustProvider;
    private readonly RuntimeLicenseService _runtimeLicense;
    private readonly IDeviceIdentityProvider _deviceIdentity;
    private readonly TimeProvider _timeProvider;

    public SignedRecoveryAuthorizationValidator(
        IRecoveryAuthorizationTrustProvider trustProvider,
        RuntimeLicenseService runtimeLicense,
        IDeviceIdentityProvider deviceIdentity,
        TimeProvider? timeProvider = null)
    {
        _trustProvider = trustProvider;
        _runtimeLicense = runtimeLicense;
        _deviceIdentity = deviceIdentity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RecoveryAuthorizationValidation> ValidateAsync(
        string? signedAuthorization,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(signedAuthorization) ||
            signedAuthorization.Length > MaximumEnvelopeCharacters || targetUserId == Guid.Empty)
        {
            return RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid");
        }

        var trust = _trustProvider.GetTrust();
        if (string.IsNullOrWhiteSpace(trust.PublicKeyPem) ||
            string.IsNullOrWhiteSpace(trust.IssuerId) ||
            trust.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return RecoveryAuthorizationValidation.Failed("recovery.authorization_not_provisioned");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<SignedRecoveryAuthorization>(signedAuthorization, JsonOptions);
            if (envelope is null || !string.Equals(envelope.Algorithm, RequiredAlgorithm, StringComparison.Ordinal))
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid");
            }

            var payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
            var signature = Convert.FromBase64String(envelope.SignatureBase64);
            if (payloadBytes.Length is 0 or > MaximumPayloadBytes || signature.Length is 0 or > 1024)
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid");
            }

            using var rsa = RSA.Create();
            rsa.ImportFromPem(trust.PublicKeyPem);
            if (rsa.KeySize < 2048 || !rsa.VerifyData(
                    payloadBytes,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid");
            }

            var payload = JsonSerializer.Deserialize<RecoveryAuthorizationPayload>(payloadBytes, JsonOptions);
            if (payload is null ||
                !string.Equals(payload.IssuerId, trust.IssuerId, StringComparison.Ordinal) ||
                !string.Equals(payload.Action, RequiredAction, StringComparison.Ordinal) ||
                payload.Nonce == Guid.Empty || payload.TargetUserId != targetUserId)
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid");
            }

            var now = _timeProvider.GetUtcNow();
            if (payload.IssuedAt > now + ClockSkew ||
                payload.ExpiresAt <= now ||
                payload.ExpiresAt <= payload.IssuedAt ||
                payload.ExpiresAt - payload.IssuedAt > MaximumLifetime ||
                payload.IssuedAt < now - MaximumLifetime - ClockSkew)
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_expired");
            }

            var license = await _runtimeLicense.ValidatePersistedAsync(cancellationToken);
            if (!license.IsValid || license.Payload is null ||
                !string.Equals(payload.LicenseId, license.Payload.LicenseId, StringComparison.Ordinal) ||
                !string.Equals(payload.DeviceId, license.Payload.DeviceId, StringComparison.Ordinal))
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_binding_mismatch");
            }

            var currentDeviceId = await _deviceIdentity.GetDeviceIdAsync(cancellationToken);
            if (!string.Equals(payload.DeviceId, currentDeviceId, StringComparison.Ordinal))
            {
                return RecoveryAuthorizationValidation.Failed("recovery.authorization_binding_mismatch");
            }

            return RecoveryAuthorizationValidation.Valid(new VerifiedRecoveryAuthorization(
                payload.IssuerId,
                payload.Nonce,
                payload.TargetUserId,
                payload.ExpiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            return RecoveryAuthorizationValidation.Failed("recovery.authorization_invalid");
        }
    }
}

/// <summary>
/// Deliberately fail-closed until governance provisions a Recovery public key
/// and its issuer identifier. Never falls back to the license trust key.
/// </summary>
public sealed class UnprovisionedRecoveryAuthorizationTrustProvider : IRecoveryAuthorizationTrustProvider
{
    public RecoveryAuthorizationTrust GetTrust() => new(null, null);
}

/// <summary>Reads only the separately provisioned Recovery public trust configuration.</summary>
public sealed class ConfiguredRecoveryAuthorizationTrustProvider : IRecoveryAuthorizationTrustProvider
{
    private readonly IConfiguration _configuration;

    public ConfiguredRecoveryAuthorizationTrustProvider(IConfiguration configuration) => _configuration = configuration;

    public RecoveryAuthorizationTrust GetTrust()
    {
        return CreateValidatedTrust(
            _configuration["RecoveryAuthorization:PublicKeyPem"],
            _configuration["RecoveryAuthorization:IssuerId"]);
    }

    internal static RecoveryAuthorizationTrust CreateValidatedTrust(string? publicKeyPem, string? issuerId)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem) || string.IsNullOrWhiteSpace(issuerId) ||
            publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return new RecoveryAuthorizationTrust(null, null);
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            return rsa.KeySize >= 2048
                ? new RecoveryAuthorizationTrust(publicKeyPem, issuerId)
                : new RecoveryAuthorizationTrust(null, null);
        }
        catch (CryptographicException)
        {
            return new RecoveryAuthorizationTrust(null, null);
        }
        catch (ArgumentException)
        {
            return new RecoveryAuthorizationTrust(null, null);
        }
    }
}

/// <summary>
/// Loads Recovery trust from one explicitly selected machine file. Production
/// composition uses this provider so app settings, user settings, environment
/// variables, and command-line configuration cannot establish a Recovery key.
/// </summary>
public sealed class ProtectedFileRecoveryAuthorizationTrustProvider : IRecoveryAuthorizationTrustProvider
{
    private readonly string _trustFilePath;

    public ProtectedFileRecoveryAuthorizationTrustProvider(string trustFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustFilePath);
        _trustFilePath = Path.GetFullPath(trustFilePath);
    }

    public RecoveryAuthorizationTrust GetTrust()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RecoveryAuthorizationTrust(null, null);
        }

        try
        {
            if (!HasProtectedMachineTrustLocation(_trustFilePath) || !File.Exists(_trustFilePath))
            {
                return new RecoveryAuthorizationTrust(null, null);
            }

            var info = new FileInfo(_trustFilePath);
            if (info.Length is <= 0 or > 64 * 1024)
            {
                return new RecoveryAuthorizationTrust(null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_trustFilePath), new JsonDocumentOptions
            {
                MaxDepth = 8
            });
            if (!TryGetProperty(document.RootElement, "RecoveryAuthorization", out var recovery) ||
                recovery.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(recovery, "PublicKeyPem", out var publicKey) ||
                !TryGetProperty(recovery, "IssuerId", out var issuer) ||
                publicKey.ValueKind != JsonValueKind.String || issuer.ValueKind != JsonValueKind.String)
            {
                return new RecoveryAuthorizationTrust(null, null);
            }

            return ConfiguredRecoveryAuthorizationTrustProvider.CreateValidatedTrust(
                publicKey.GetString(), issuer.GetString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException)
        {
            return new RecoveryAuthorizationTrust(null, null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasProtectedMachineTrustLocation(string trustFilePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var commonData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        var applicationDirectory = Path.Combine(commonData, "EdgeRetails");
        var recoveryDirectory = Path.Combine(applicationDirectory, "recovery");
        var expectedTrustPath = Path.Combine(recoveryDirectory, "trust.json");
        if (!string.Equals(Path.GetFullPath(trustFilePath), expectedTrustPath, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(commonData) || !Directory.Exists(applicationDirectory) || !Directory.Exists(recoveryDirectory))
        {
            return false;
        }

        return HasNoReparseDirectoryAncestors(commonData) &&
               IsNotReparsePoint(applicationDirectory) &&
               IsNotReparsePoint(recoveryDirectory) &&
               IsNotReparsePoint(trustFilePath) &&
               HasTrustedDirectoryAcl(commonData, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), allowProgramDataCreate: true) &&
               HasTrustedDirectoryAcl(applicationDirectory, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), allowProgramDataCreate: false) &&
               HasTrustedDirectoryAcl(recoveryDirectory, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), allowProgramDataCreate: false) &&
               HasTrustedFileAcl(trustFilePath);
    }

    private static bool IsNotReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    [SupportedOSPlatform("windows")]
    private static bool HasNoReparseDirectoryAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (!IsNotReparsePoint(current.FullName))
            {
                return false;
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasTrustedDirectoryAcl(string path, SecurityIdentifier expectedOwner, bool allowProgramDataCreate)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected ||
            !expectedOwner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            return false;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        return HasTrustedDirectoryAccessRules(rules.Cast<FileSystemAccessRule>(), allowProgramDataCreate);
    }

    // Windows can store the standard ProgramData Users read and create rights
    // as separate ACEs. Validate each ACE's prohibited rights, then aggregate
    // read access across the same trusted principal.
    [SupportedOSPlatform("windows")]
    internal static bool HasTrustedDirectoryAccessRules(
        IEnumerable<FileSystemAccessRule> rules,
        bool allowProgramDataCreate)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
        var hasAdmins = false;
        var hasSystem = false;
        var hasUsersRead = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                return false;
            }

            if (admins.Equals(rule.IdentityReference))
            {
                hasAdmins |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            else if (system.Equals(rule.IdentityReference))
            {
                hasSystem |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            else if (users.Equals(rule.IdentityReference))
            {
                var forbidden = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & forbidden) != 0 ||
                    (!allowProgramDataCreate && (rule.FileSystemRights & FileSystemRights.Write) != 0))
                {
                    return false;
                }

                hasUsersRead |= (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute;
            }
            else if (!(allowProgramDataCreate && creatorOwner.Equals(rule.IdentityReference)))
            {
                return false;
            }
        }

        return hasAdmins && hasSystem && hasUsersRead;
    }

    [SupportedOSPlatform("windows")]
    private static bool HasTrustedFileAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        if (!security.AreAccessRulesProtected ||
            !admins.Equals(security.GetOwner(typeof(SecurityIdentifier))))
        {
            return false;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        var hasAdmins = false;
        var hasSystem = false;
        var hasUsersRead = false;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                return false;
            }

            if (admins.Equals(rule.IdentityReference))
            {
                hasAdmins |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            else if (system.Equals(rule.IdentityReference))
            {
                hasSystem |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
            }
            else if (users.Equals(rule.IdentityReference))
            {
                var forbidden = FileSystemRights.Write | FileSystemRights.Delete |
                                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & FileSystemRights.Read) != FileSystemRights.Read ||
                    (rule.FileSystemRights & forbidden) != 0)
                {
                    return false;
                }

                hasUsersRead = true;
            }
            else
            {
                return false;
            }
        }

        return hasAdmins && hasSystem && hasUsersRead;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
