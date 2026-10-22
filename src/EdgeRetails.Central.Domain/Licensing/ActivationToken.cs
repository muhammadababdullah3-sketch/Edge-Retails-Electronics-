using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class ActivationToken : Entity
{
    public const string StatusIssued = "ISSUED";
    public const string StatusRedeemed = "REDEEMED";
    public const string StatusPendingApproval = "PENDING_APPROVAL";
    public const string StatusConsumed = "CONSUMED";
    public const string StatusExpired = "EXPIRED";
    public const string StatusRejected = "REJECTED";
    public const string StatusRevoked = "REVOKED";

    public Guid LicenseId { get; private set; }
    public Guid ShopId { get; private set; }
    public string TokenHashSha256 { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusIssued;
    public DateTimeOffset FirstRedemptionExpiresAtUtc { get; private set; }
    public Guid IssuedByAdminId { get; private set; }
    public DateTimeOffset IssuedAtUtc { get; private set; }
    public DateTimeOffset? RedeemedAtUtc { get; private set; }
    public DateTimeOffset? ConsumedAtUtc { get; private set; }
    public Guid? TokenPolicyId { get; private set; }
    public Guid? DeliveryChannelId { get; private set; }
    public string? DeliveryReference { get; private set; }
    public string? DeliverySecretKeyId { get; private set; }
    public string? ProtectedDeliverySecret { get; private set; }
    public string DeliveryState { get; private set; } = "NOT_CONFIGURED";
    public int RowVersion { get; private set; } = 1;

    private ActivationToken() { }

    public static ActivationToken Issue(
        Guid licenseId,
        Guid shopId,
        string tokenHashSha256,
        DateTimeOffset firstRedemptionExpiresAtUtc,
        Guid issuedByAdminId,
        DateTimeOffset issuedAtUtc)
    {
        if (licenseId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_LICENSE_ID", "token.invalid_license_id", "License ID is required.");
        }
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "token.invalid_shop_id", "Shop ID is required.");
        }
        if (string.IsNullOrWhiteSpace(tokenHashSha256) || tokenHashSha256.Length != 64)
        {
            throw new CentralDomainException("INVALID_TOKEN_HASH", "token.invalid_token_hash", "Valid 64-character token hash is required.");
        }
        if (firstRedemptionExpiresAtUtc <= issuedAtUtc)
        {
            throw new CentralDomainException("INVALID_EXPIRATION", "token.invalid_expiration", "First redemption expiration must be in the future.");
        }

        return new ActivationToken
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ShopId = shopId,
            TokenHashSha256 = tokenHashSha256.Trim().ToLowerInvariant(),
            Status = StatusIssued,
            FirstRedemptionExpiresAtUtc = firstRedemptionExpiresAtUtc,
            IssuedByAdminId = issuedByAdminId,
            IssuedAtUtc = issuedAtUtc,
            RowVersion = 1
        };
    }

    public void MarkRedeemed(DateTimeOffset redeemedAtUtc)
    {
        if (Status != StatusIssued)
        {
            throw new CentralDomainException("TOKEN_NOT_ISSUED", "token.not_issued", $"Token cannot be redeemed from status: {Status}");
        }
        if (redeemedAtUtc > FirstRedemptionExpiresAtUtc)
        {
            Status = StatusExpired;
            RowVersion++;
            throw new CentralDomainException("TOKEN_EXPIRED", "token.expired", "Token redemption window has expired.");
        }

        Status = StatusRedeemed;
        RedeemedAtUtc = redeemedAtUtc;
        ClearProtectedDeliverySecret();
        RowVersion++;
    }

    public void BindPendingDelivery(Guid tokenPolicyId, Guid deliveryChannelId, string deliveryReference, string keyId, string protectedSecret)
    {
        if (tokenPolicyId == Guid.Empty || deliveryChannelId == Guid.Empty || string.IsNullOrWhiteSpace(deliveryReference) ||
            string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(protectedSecret) || DeliveryReference is not null)
        {
            throw new CentralDomainException("INVALID_ACTIVATION_DELIVERY", "activation.delivery_invalid", "A protected one-time delivery binding is required.");
        }
        TokenPolicyId = tokenPolicyId;
        DeliveryChannelId = deliveryChannelId;
        DeliveryReference = deliveryReference.Trim();
        DeliverySecretKeyId = keyId.Trim();
        ProtectedDeliverySecret = protectedSecret;
        DeliveryState = "DELIVERY_PENDING";
    }

    public void ClearProtectedDeliverySecret()
    {
        ProtectedDeliverySecret = null;
        DeliverySecretKeyId = null;
    }

    public bool ExpireDeliverySecretIfWindowElapsed(DateTimeOffset nowUtc)
    {
        if (Status != StatusIssued || nowUtc <= FirstRedemptionExpiresAtUtc || ProtectedDeliverySecret is null)
        {
            return false;
        }

        Status = StatusExpired;
        DeliveryState = "EXPIRED";
        ClearProtectedDeliverySecret();
        RowVersion++;
        return true;
    }

    public void MoveToPendingApproval()
    {
        if (Status != StatusRedeemed)
        {
            throw new CentralDomainException("INVALID_TOKEN_STATE", "token.invalid_state", $"Token must be REDEEMED before moving to PENDING_APPROVAL; current: {Status}");
        }
        Status = StatusPendingApproval;
        RowVersion++;
    }

    public void Consume(DateTimeOffset consumedAtUtc)
    {
        if (Status != StatusPendingApproval && Status != StatusRedeemed)
        {
            throw new CentralDomainException("TOKEN_CANNOT_CONSUME", "token.cannot_consume", $"Token cannot be consumed from status: {Status}");
        }
        Status = StatusConsumed;
        ConsumedAtUtc = consumedAtUtc;
        RowVersion++;
    }

    public void Reject()
    {
        Status = StatusRejected;
        RowVersion++;
    }

    public void Revoke()
    {
        Status = StatusRevoked;
        RowVersion++;
    }
}
