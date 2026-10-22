using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class Installation : Entity
{
    public const string StatusRegistered = "REGISTERED";
    public const string StatusPendingApproval = "PENDING_APPROVAL";
    public const string StatusActive = "ACTIVE";
    public const string StatusReplaced = "REPLACED";
    public const string StatusRevoked = "REVOKED";

    public Guid ShopId { get; private set; }
    public Guid LicenseId { get; private set; }
    public string MachineFingerprint { get; private set; } = string.Empty;
    public string PublicKeyPem { get; private set; } = string.Empty;
    public string PublicKeyFingerprintSha256 { get; private set; } = string.Empty;
    public string Hostname { get; private set; } = string.Empty;
    public string OsPlatform { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusRegistered;
    public int DeviceIdentityVersion { get; private set; }
    public string DeviceIdentityHash { get; private set; } = string.Empty;
    public int HardwareFingerprintVersion { get; private set; }
    public string HardwareFingerprintHash { get; private set; } = string.Empty;
    public string SignalQuality { get; private set; } = "UNKNOWN";
    public string ApplicationVersion { get; private set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private Installation() { }

    public static Installation Register(
        Guid shopId,
        Guid licenseId,
        string machineFingerprint,
        string publicKeyPem,
        string hostname,
        string osPlatform,
        DateTimeOffset registeredAtUtc)
    {
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "install.invalid_shop_id", "Shop ID is required.");
        }
        if (licenseId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_LICENSE_ID", "install.invalid_license_id", "License ID is required.");
        }
        if (string.IsNullOrWhiteSpace(machineFingerprint))
        {
            throw new CentralDomainException("INVALID_FINGERPRINT", "install.invalid_fingerprint", "Machine fingerprint is required.");
        }
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new CentralDomainException("INVALID_PUBLIC_KEY", "install.invalid_public_key", "Public key PEM is required.");
        }

        string keyFingerprint = ComputePublicKeyFingerprintSha256(publicKeyPem);

        return new Installation
        {
            Id = Guid.NewGuid(),
            ShopId = shopId,
            LicenseId = licenseId,
            MachineFingerprint = machineFingerprint.Trim(),
            PublicKeyPem = publicKeyPem.Trim(),
            PublicKeyFingerprintSha256 = keyFingerprint,
            Hostname = hostname?.Trim() ?? string.Empty,
            OsPlatform = osPlatform?.Trim() ?? string.Empty,
            Status = StatusRegistered,
            RegisteredAtUtc = registeredAtUtc,
            RowVersion = 1
        };
    }

    public static string ComputePublicKeyFingerprintSha256(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new CentralDomainException("INVALID_PUBLIC_KEY", "install.invalid_public_key", "Public key PEM is required.");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyPem.Trim()))).ToLowerInvariant();
    }

    public static Installation RegisterBootstrap(
        Guid installationId,
        Guid shopId,
        Guid licenseId,
        string hardwareFingerprintHash,
        string publicKeyPem,
        int deviceIdentityVersion,
        string deviceIdentityHash,
        int hardwareFingerprintVersion,
        string signalQuality,
        string applicationVersion,
        DateTimeOffset registeredAtUtc)
    {
        if (installationId == Guid.Empty || deviceIdentityVersion < 1 || hardwareFingerprintVersion < 1 ||
            !IsSha256(deviceIdentityHash) || !IsSha256(hardwareFingerprintHash) ||
            signalQuality is not ("STRONG" or "WEAK" or "UNKNOWN") ||
            string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 128)
        {
            throw new CentralDomainException("INVALID_DEVICE_IDENTITY", "install.device_identity_invalid", "Bootstrap device identity, fingerprint versions, signal quality, and application version are invalid.");
        }

        var installation = Register(
            shopId,
            licenseId,
            hardwareFingerprintHash,
            publicKeyPem,
            string.Empty,
            string.Empty,
            registeredAtUtc);
        installation.Id = installationId;
        installation.DeviceIdentityVersion = deviceIdentityVersion;
        installation.DeviceIdentityHash = deviceIdentityHash.ToLowerInvariant();
        installation.HardwareFingerprintVersion = hardwareFingerprintVersion;
        installation.HardwareFingerprintHash = hardwareFingerprintHash.ToLowerInvariant();
        installation.SignalQuality = signalQuality;
        installation.ApplicationVersion = applicationVersion.Trim();
        return installation;
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public void MoveToPendingApproval()
    {
        if (Status != StatusRegistered)
        {
            throw new CentralDomainException("INVALID_TRANSITION", "install.invalid_transition", $"Cannot move to pending approval from: {Status}");
        }
        Status = StatusPendingApproval;
        RowVersion++;
    }

    public void Approve(DateTimeOffset approvedAtUtc)
    {
        if (Status != StatusPendingApproval && Status != StatusRegistered)
        {
            throw new CentralDomainException("INVALID_TRANSITION", "install.invalid_transition", $"Cannot approve installation in status: {Status}");
        }
        Status = StatusActive;
        ApprovedAtUtc = approvedAtUtc;
        RowVersion++;
    }

    public void Replace()
    {
        Status = StatusReplaced;
        RowVersion++;
    }

    public void Revoke()
    {
        Status = StatusRevoked;
        RowVersion++;
    }
}
