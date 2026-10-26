using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class ActivationRedemption : Entity
{
    public const string StatusPending = "PENDING";
    public const string StatusApproved = "APPROVED";
    public const string StatusRejected = "REJECTED";
    public const string StatusRequestInformation = "REQUEST_INFORMATION";
    public const string StatusOpenHardwareReview = "OPEN_HARDWARE_REVIEW";

    public Guid TokenId { get; private set; }
    public Guid InstallationId { get; private set; }
    public string ChallengeNonce { get; private set; } = string.Empty;
    public string ClientSignatureHex { get; private set; } = string.Empty;
    public DateTimeOffset RedeemedAtUtc { get; private set; }
    public string Status { get; private set; } = StatusPending;
    public string? DecisionReasonCode { get; private set; }
    public Guid? ReviewedByAdminId { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public string? ValidationSnapshotJson { get; private set; }
    public int? ValidationSnapshotVersion { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private ActivationRedemption() { }

    public static ActivationRedemption Create(
        Guid tokenId,
        Guid installationId,
        string challengeNonce,
        string clientSignatureHex,
        DateTimeOffset redeemedAtUtc,
        string? validationSnapshotJson = null,
        int? validationSnapshotVersion = null)
    {
        if (tokenId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_TOKEN_ID", "redemption.invalid_token_id", "Token ID is required.");
        }
        if (installationId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_INSTALL_ID", "redemption.invalid_install_id", "Installation ID is required.");
        }
        if (string.IsNullOrWhiteSpace(challengeNonce))
        {
            throw new CentralDomainException("INVALID_NONCE", "redemption.invalid_nonce", "Challenge nonce is required.");
        }
        if (string.IsNullOrWhiteSpace(clientSignatureHex))
        {
            throw new CentralDomainException("INVALID_SIGNATURE", "redemption.invalid_signature", "Client signature is required.");
        }
        var hasSnapshotJson = !string.IsNullOrWhiteSpace(validationSnapshotJson);
        if (hasSnapshotJson != validationSnapshotVersion.HasValue)
        {
            throw new CentralDomainException("INVALID_VALIDATION_SNAPSHOT", "activation.invalid_validation_snapshot", "Validation snapshot JSON and version must be supplied together.");
        }
        if (hasSnapshotJson && validationSnapshotVersion != 1)
        {
            throw new CentralDomainException("INVALID_VALIDATION_SNAPSHOT_VERSION", "activation.invalid_validation_snapshot_version", "The initial validation snapshot version must be 1.");
        }
        ValidateSnapshotJson(validationSnapshotJson);

        return new ActivationRedemption
        {
            Id = Guid.NewGuid(),
            TokenId = tokenId,
            InstallationId = installationId,
            ChallengeNonce = challengeNonce.Trim(),
            ClientSignatureHex = clientSignatureHex.Trim().ToLowerInvariant(),
            RedeemedAtUtc = redeemedAtUtc,
            Status = StatusPending,
            ValidationSnapshotJson = validationSnapshotJson,
            ValidationSnapshotVersion = validationSnapshotVersion,
            RowVersion = 1
        };
    }

    public void Decide(
        string decision,
        string reasonCode,
        Guid adminUserId,
        DateTimeOffset decidedAtUtc,
        string validationSnapshotJson,
        int validationSnapshotVersion)
    {
        if (Status != StatusPending)
        {
            throw new CentralDomainException("NOT_PENDING", "activation.not_pending", $"Cannot decide activation in status: {Status}");
        }
        if (adminUserId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ADMIN_ID", "activation.invalid_admin_id", "Admin ID is required.");
        }
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new CentralDomainException("INVALID_REASON_CODE", "activation.invalid_reason_code", "Decision reason code is required.");
        }
        if (reasonCode.Trim().Length > 128)
        {
            throw new CentralDomainException("INVALID_REASON_CODE", "activation.invalid_reason_code", "Decision reason code cannot exceed 128 characters.");
        }
        if (string.IsNullOrWhiteSpace(ValidationSnapshotJson) || ValidationSnapshotVersion is null)
        {
            throw new CentralDomainException("VALIDATION_SNAPSHOT_MISSING", "activation.validation_snapshot_missing", "The activation request has no persisted validation snapshot.");
        }
        if (validationSnapshotVersion != ValidationSnapshotVersion || !string.Equals(validationSnapshotJson, ValidationSnapshotJson, StringComparison.Ordinal))
        {
            throw new CentralDomainException("VALIDATION_SNAPSHOT_CHANGED", "activation.validation_snapshot_changed", "The submitted validation snapshot does not match the persisted request snapshot.");
        }
        ValidateSnapshotJson(validationSnapshotJson);

        var normalizedDecision = decision?.Trim().ToUpperInvariant();
        Status = normalizedDecision switch
        {
            StatusApproved => StatusApproved,
            StatusRejected => StatusRejected,
            StatusRequestInformation => StatusRequestInformation,
            StatusOpenHardwareReview => StatusOpenHardwareReview,
            _ => throw new CentralDomainException("INVALID_ACTIVATION_DECISION", "activation.invalid_decision", "Activation decision is not supported.")
        };
        DecisionReasonCode = reasonCode.Trim();
        ReviewedByAdminId = adminUserId;
        DecidedAtUtc = decidedAtUtc;
        RowVersion++;
    }

    public void Approve()
    {
        EnsurePending();
        Status = StatusApproved;
        RowVersion++;
    }

    public void Reject()
    {
        EnsurePending();
        Status = StatusRejected;
        RowVersion++;
    }

    private void EnsurePending()
    {
        if (Status != StatusPending)
        {
            throw new CentralDomainException("NOT_PENDING", "activation.not_pending", $"Cannot decide activation in status: {Status}");
        }
    }

    private static void ValidateSnapshotJson(string? validationSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(validationSnapshotJson))
        {
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(validationSnapshotJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new CentralDomainException("INVALID_VALIDATION_SNAPSHOT", "activation.invalid_validation_snapshot", "Validation snapshot must be a JSON object.");
            }
        }
        catch (System.Text.Json.JsonException)
        {
            throw new CentralDomainException("INVALID_VALIDATION_SNAPSHOT", "activation.invalid_validation_snapshot", "Validation snapshot must contain valid JSON.");
        }
    }
}
