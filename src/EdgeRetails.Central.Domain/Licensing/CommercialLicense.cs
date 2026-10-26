using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class CommercialLicense : Entity
{
    public const string StatusActive = "ACTIVE";
    public const string StatusPaused = "PAUSED";
    public const string StatusSuspended = "SUSPENDED";
    public const string StatusExpired = "EXPIRED";
    public const string StatusRevoked = "REVOKED";

    public Guid ShopId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid InitialOrderId { get; private set; }
    public string TermKind { get; private set; } = CommercialTerm.FixedTerm;
    public DateTimeOffset ValidFromUtc { get; private set; }
    public DateTimeOffset? ValidUntilUtc { get; private set; }
    public string Status { get; private set; } = StatusActive;
    public int AuthorityRevision { get; private set; } = 1;
    public int PrimaryCapacity { get; private set; } = 1; // Single-primary V1 invariant
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? LastRenewedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private CommercialLicense() { }

    public static CommercialLicense Issue(
        Order approvedOrder,
        DateTimeOffset validFromUtc,
        DateTimeOffset? validUntilUtc,
        DateTimeOffset createdAtUtc)
    {
        if (approvedOrder == null || approvedOrder.Status != Order.StatusPaidApproved)
        {
            throw new CentralDomainException("ORDER_NOT_APPROVED", "license.order_not_approved", "License can only be issued from an approved paid order.");
        }
        if (approvedOrder.PurchaseTarget != PlanCatalogItem.TargetBaseLicense)
        {
            throw new CentralDomainException("NOT_BASE_LICENSE_ORDER", "license.not_base_target", "Base license can only be issued from a BASE_LICENSE order.");
        }

        var term = new CommercialTerm(approvedOrder.TermKind, validFromUtc, validUntilUtc);

        return new CommercialLicense
        {
            Id = Guid.NewGuid(),
            ShopId = approvedOrder.ShopId,
            CustomerId = approvedOrder.CustomerId,
            InitialOrderId = approvedOrder.Id,
            TermKind = term.TermKind,
            ValidFromUtc = term.ValidFromUtc,
            ValidUntilUtc = term.ValidUntilUtc,
            Status = StatusActive,
            AuthorityRevision = 1,
            PrimaryCapacity = 1,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void Pause()
    {
        if (Status != StatusActive)
        {
            throw new CentralDomainException("INVALID_STATE_TRANSITION", "license.invalid_transition", $"Cannot pause license in status: {Status}");
        }
        Status = StatusPaused;
        BumpAuthority();
    }

    public void Resume()
    {
        if (Status != StatusPaused)
        {
            throw new CentralDomainException("INVALID_STATE_TRANSITION", "license.invalid_transition", $"Cannot resume license in status: {Status}");
        }
        Status = StatusActive;
        BumpAuthority();
    }

    public void Suspend()
    {
        if (Status != StatusActive && Status != StatusPaused)
        {
            throw new CentralDomainException("INVALID_STATE_TRANSITION", "license.invalid_transition", $"Cannot suspend license in status: {Status}");
        }
        Status = StatusSuspended;
        BumpAuthority();
    }

    public void Reinstate()
    {
        if (Status != StatusSuspended)
        {
            throw new CentralDomainException("INVALID_STATE_TRANSITION", "license.invalid_transition", $"Cannot reinstate license in status: {Status}");
        }
        Status = StatusActive;
        BumpAuthority();
    }

    public void RenewFixedTerm(DateTimeOffset newValidUntilUtc, DateTimeOffset renewedAtUtc)
    {
        if (TermKind == CommercialTerm.Lifetime)
        {
            throw new CentralDomainException("LIFETIME_CANNOT_RENEW", "license.lifetime_cannot_renew_or_extend", "Lifetime license has no expiration and cannot be renewed.");
        }
        if (Status == StatusRevoked)
        {
            throw new CentralDomainException("REVOKED_CANNOT_RENEW", "license.revoked_cannot_renew", "Revoked license cannot be renewed.");
        }
        if (newValidUntilUtc <= (ValidUntilUtc ?? ValidFromUtc))
        {
            throw new CentralDomainException("INVALID_RENEWAL_DATE", "license.invalid_renewal_date", "New expiration date must be after current expiration.");
        }

        ValidUntilUtc = newValidUntilUtc;
        Status = StatusActive;
        LastRenewedAtUtc = renewedAtUtc;
        BumpAuthority();
    }

    public void Extend(DateTimeOffset extensionEndUtc)
    {
        if (TermKind == CommercialTerm.Lifetime)
        {
            throw new CentralDomainException("LIFETIME_CANNOT_EXTEND", "license.lifetime_cannot_renew_or_extend", "Lifetime license has no expiration and cannot be extended.");
        }
        if (Status == StatusRevoked)
        {
            throw new CentralDomainException("REVOKED_CANNOT_EXTEND", "license.revoked_cannot_extend", "Revoked license cannot be extended.");
        }
        if (extensionEndUtc <= (ValidUntilUtc ?? ValidFromUtc))
        {
            throw new CentralDomainException("INVALID_EXTENSION_DATE", "license.invalid_extension_date", "Extension end date must be after current expiration.");
        }

        ValidUntilUtc = extensionEndUtc;
        BumpAuthority();
    }

    public void Revoke()
    {
        if (Status == StatusRevoked)
        {
            throw new CentralDomainException("ALREADY_REVOKED", "license.already_revoked", "License is already revoked.");
        }
        Status = StatusRevoked;
        BumpAuthority();
    }

    public void CheckExpiration(DateTimeOffset trustedTimeUtc)
    {
        if (TermKind == CommercialTerm.Lifetime)
        {
            // Lifetime base license NEVER expires!
            return;
        }

        if (ValidUntilUtc.HasValue && trustedTimeUtc > ValidUntilUtc.Value && Status == StatusActive)
        {
            Status = StatusExpired;
            BumpAuthority();
        }
    }

    private void BumpAuthority()
    {
        AuthorityRevision++;
        RowVersion++;
    }
}
