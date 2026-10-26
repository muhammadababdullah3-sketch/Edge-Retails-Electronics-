using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public sealed class InstallationRequestNonce : Entity
{
    private InstallationRequestNonce() { }

    private InstallationRequestNonce(Guid installationId, string nonce, DateTimeOffset timestampUtc)
    {
        if (installationId == Guid.Empty || string.IsNullOrWhiteSpace(nonce) || nonce.Length > 128)
        {
            throw new CentralDomainException("INVALID_INSTALLATION_PROOF", "auth.installation_proof_invalid", "Installation request proof fields are invalid.");
        }
        InstallationId = installationId;
        Nonce = nonce;
        TimestampUtc = timestampUtc;
    }

    public Guid InstallationId { get; private set; }
    public string Nonce { get; private set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; private set; }

    public static InstallationRequestNonce Create(Guid installationId, string nonce, DateTimeOffset timestampUtc) =>
        new(installationId, nonce, timestampUtc);
}
