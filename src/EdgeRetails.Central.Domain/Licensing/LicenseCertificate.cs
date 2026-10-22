using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class LicenseCertificate : Entity
{
    public const string StatusPending = "PENDING";
    public const string StatusActive = "ACTIVE";
    public const string StatusSuperseded = "SUPERSEDED";
    public const string StatusRevoked = "REVOKED";

    public Guid InstallationId { get; private set; }
    public Guid LicenseId { get; private set; }
    public Guid ShopId { get; private set; }
    public int LineageRevision { get; private set; } = 1;
    public string CertificateSerial { get; private set; } = string.Empty;
    public string SignedEnvelopeJson { get; private set; } = string.Empty;
    public string RS256Signature { get; private set; } = string.Empty;
    public string DigestSha256 { get; private set; } = string.Empty;
    public DateTimeOffset ValidFromUtc { get; private set; }
    public DateTimeOffset LeaseExpiresUtc { get; private set; }
    public DateTimeOffset OfflineGraceUntilUtc { get; private set; }
    public string Status { get; private set; } = StatusActive;
    public DateTimeOffset IssuedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private LicenseCertificate() { }

    public static LicenseCertificate Create(
        Guid installationId,
        Guid licenseId,
        Guid shopId,
        int lineageRevision,
        string certificateSerial,
        string signedEnvelopeJson,
        string rs256Signature,
        string digestSha256,
        DateTimeOffset validFromUtc,
        DateTimeOffset leaseExpiresUtc,
        DateTimeOffset offlineGraceUntilUtc,
        DateTimeOffset issuedAtUtc)
    {
        if (installationId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_INSTALL_ID", "cert.invalid_install_id", "Installation ID is required.");
        }
        if (licenseId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_LICENSE_ID", "cert.invalid_license_id", "License ID is required.");
        }
        if (string.IsNullOrWhiteSpace(certificateSerial))
        {
            throw new CentralDomainException("INVALID_SERIAL", "cert.invalid_serial", "Certificate serial is required.");
        }
        if (string.IsNullOrWhiteSpace(signedEnvelopeJson))
        {
            throw new CentralDomainException("INVALID_ENVELOPE", "cert.invalid_envelope", "Signed envelope JSON is required.");
        }
        if (string.IsNullOrWhiteSpace(rs256Signature))
        {
            throw new CentralDomainException("INVALID_SIGNATURE", "cert.invalid_signature", "RS256 signature is required.");
        }
        if (leaseExpiresUtc < validFromUtc || offlineGraceUntilUtc < leaseExpiresUtc)
        {
            throw new CentralDomainException("INVALID_TIME_BOUNDS", "cert.invalid_time_bounds", "Time bounds must satisfy: validFromUtc <= leaseExpiresUtc <= offlineGraceUntilUtc.");
        }

        return new LicenseCertificate
        {
            Id = Guid.NewGuid(),
            InstallationId = installationId,
            LicenseId = licenseId,
            ShopId = shopId,
            LineageRevision = lineageRevision,
            CertificateSerial = certificateSerial.Trim(),
            SignedEnvelopeJson = signedEnvelopeJson,
            RS256Signature = rs256Signature.Trim(),
            DigestSha256 = digestSha256.Trim().ToLowerInvariant(),
            ValidFromUtc = validFromUtc,
            LeaseExpiresUtc = leaseExpiresUtc,
            OfflineGraceUntilUtc = offlineGraceUntilUtc,
            Status = StatusActive,
            IssuedAtUtc = issuedAtUtc,
            RowVersion = 1
        };
    }

    public void Supersede()
    {
        if (Status != StatusActive)
        {
            return;
        }
        Status = StatusSuperseded;
        RowVersion++;
    }

    public void Revoke()
    {
        Status = StatusRevoked;
        RowVersion++;
    }
}
