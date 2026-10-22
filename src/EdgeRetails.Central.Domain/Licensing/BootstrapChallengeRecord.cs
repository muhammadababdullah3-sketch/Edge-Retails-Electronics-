using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

/// <summary>
/// Durable, single-use proof that Central issued a bootstrap challenge for one
/// installation, activation-token hash, key fingerprint, and client request nonce.
/// The challenge nonce is public, persisted only to verify the signed one-time proof,
/// and accompanied by a hash for lookup and replay checks.
/// </summary>
public sealed class BootstrapChallengeRecord : Entity
{
    public Guid InstallationId { get; private set; }
    public Guid ShopId { get; private set; }
    public Guid LicenseId { get; private set; }
    public string ActivationTokenHashSha256 { get; private set; } = string.Empty;
    public string InstallationPublicKeyPem { get; private set; } = string.Empty;
    public string InstallationPublicKeyFingerprintSha256 { get; private set; } = string.Empty;
    public Guid RequestNonce { get; private set; }
    public string ChallengeNonce { get; private set; } = string.Empty;
    public string ChallengeNonceHashSha256 { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RegisteredAtUtc { get; private set; }
    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    private BootstrapChallengeRecord() { }

    public static BootstrapChallengeRecord Issue(
        Guid installationId,
        Guid shopId,
        Guid licenseId,
        string activationTokenHashSha256,
        string installationPublicKeyPem,
        string installationPublicKeyFingerprintSha256,
        Guid requestNonce,
        string challengeNonce,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        bool installationAlreadyRegistered = false)
    {
        if (installationId == Guid.Empty || shopId == Guid.Empty || licenseId == Guid.Empty || requestNonce == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_BINDING", "bootstrap.binding_invalid", "Installation, shop, and request nonce are required.");
        }
        if (!IsSha256(activationTokenHashSha256) || !IsSha256(installationPublicKeyFingerprintSha256))
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_BINDING", "bootstrap.binding_invalid", "Token and installation-key fingerprints must be SHA-256 hex values.");
        }
        if (string.IsNullOrWhiteSpace(installationPublicKeyPem) || installationPublicKeyPem.Length > 8192 ||
            string.IsNullOrWhiteSpace(challengeNonce) || challengeNonce.Length > 128)
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_CHALLENGE", "bootstrap.challenge_invalid", "Public key or bootstrap nonce is missing or exceeds the supported bound.");
        }
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new CentralDomainException("INVALID_BOOTSTRAP_CHALLENGE", "bootstrap.challenge_invalid", "A nonempty nonce and future expiry are required.");
        }

        return new BootstrapChallengeRecord
        {
            Id = Guid.NewGuid(),
            InstallationId = installationId,
            ShopId = shopId,
            LicenseId = licenseId,
            ActivationTokenHashSha256 = activationTokenHashSha256.ToLowerInvariant(),
            InstallationPublicKeyPem = installationPublicKeyPem.Trim(),
            InstallationPublicKeyFingerprintSha256 = installationPublicKeyFingerprintSha256.ToLowerInvariant(),
            RequestNonce = requestNonce,
            ChallengeNonce = challengeNonce,
            ChallengeNonceHashSha256 = Hash(challengeNonce),
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            RegisteredAtUtc = installationAlreadyRegistered ? createdAtUtc : null
        };
    }

    public void ConsumeForRegistration(
        Guid installationId,
        Guid shopId,
        Guid licenseId,
        string activationTokenHashSha256,
        string installationPublicKeyFingerprintSha256,
        DateTimeOffset nowUtc)
    {
        ValidateBinding(installationId, shopId, licenseId, activationTokenHashSha256, installationPublicKeyFingerprintSha256, nowUtc);
        if (RegisteredAtUtc is not null)
        {
            throw new CentralDomainException("BOOTSTRAP_ALREADY_REGISTERED", "bootstrap.already_registered", "Bootstrap challenge has already registered an installation.");
        }

        RegisteredAtUtc = nowUtc;
    }

    public void ConsumeForRedemption(
        Guid installationId,
        Guid shopId,
        Guid licenseId,
        string activationTokenHashSha256,
        string installationPublicKeyFingerprintSha256,
        string challengeNonce,
        DateTimeOffset nowUtc)
    {
        ValidateBinding(installationId, shopId, licenseId, activationTokenHashSha256, installationPublicKeyFingerprintSha256, nowUtc);
        if (RedeemedAtUtc is not null)
        {
            throw new CentralDomainException("CHALLENGE_ALREADY_CONSUMED", "redemption.challenge_already_consumed", "Bootstrap challenge has already been redeemed.");
        }
        if (RegisteredAtUtc is null)
        {
            throw new CentralDomainException("BOOTSTRAP_REGISTRATION_REQUIRED", "bootstrap.registration_required", "Installation must complete its bound bootstrap registration before activation redemption.");
        }
        if (!FixedHashEquals(ChallengeNonceHashSha256, Hash(challengeNonce)))
        {
            throw new CentralDomainException("BOOTSTRAP_BINDING_MISMATCH", "redemption.binding_mismatch", "Bootstrap challenge does not match the persisted installation, shop, token, or key.");
        }

        RedeemedAtUtc = nowUtc;
    }

    private void ValidateBinding(
        Guid installationId,
        Guid shopId,
        Guid licenseId,
        string activationTokenHashSha256,
        string installationPublicKeyFingerprintSha256,
        DateTimeOffset nowUtc)
    {
        if (ExpiresAtUtc <= nowUtc)
        {
            throw new CentralDomainException("CHALLENGE_EXPIRED", "redemption.challenge_expired", "Bootstrap challenge has expired.");
        }
        if (InstallationId != installationId || ShopId != shopId || LicenseId != licenseId ||
            !FixedHashEquals(ActivationTokenHashSha256, activationTokenHashSha256) ||
            !FixedHashEquals(InstallationPublicKeyFingerprintSha256, installationPublicKeyFingerprintSha256))
        {
            throw new CentralDomainException("BOOTSTRAP_BINDING_MISMATCH", "redemption.binding_mismatch", "Bootstrap challenge does not match the persisted installation, shop, license, token, or key.");
        }
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedHashEquals(string expected, string actual) =>
        IsSha256(expected) && IsSha256(actual) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
}
