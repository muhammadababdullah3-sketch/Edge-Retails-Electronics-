using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Operations;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Domain.Notifications;
using EdgeRetails.Central.Domain.Signing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EdgeRetails.Central.Application.Abstractions;

public interface ICentralDbContext
{
    // Wave 1 Foundation Sets
    DbSet<CentralOperationRecord> CentralOperationRecords { get; }
    DbSet<AdminUser> AdminUsers { get; }
    DbSet<AdminRole> AdminRoles { get; }
    DbSet<AdminUserRole> AdminUserRoles { get; }
    DbSet<AdminSession> AdminSessions { get; }
    DbSet<LoginChallenge> LoginChallenges { get; }
    DbSet<StepUpGrant> StepUpGrants { get; }
    DbSet<CentralAuditRecord> CentralAuditRecords { get; }
    DbSet<CentralOutboxMessage> CentralOutboxMessages { get; }
    DbSet<CentralConsumerEffect> CentralConsumerEffects { get; }
    DbSet<AdminNotification> AdminNotifications { get; }
    DbSet<CentralSigningRequest> CentralSigningRequests { get; }
    DbSet<CentralSigningKeyRecord> CentralSigningKeyRecords { get; }
    DbSet<CentralTrustSetRecord> CentralTrustSetRecords { get; }

    // Wave 2 Commercial Sets
    DbSet<Customer> Customers { get; }
    DbSet<Shop> Shops { get; }
    DbSet<PlanCatalogItem> PlanCatalogItems { get; }
    DbSet<PricingSnapshot> PricingSnapshots { get; }
    DbSet<Order> Orders { get; }
    DbSet<PaymentEvidence> PaymentEvidences { get; }
    DbSet<PaymentReviewRecord> PaymentReviewRecords { get; }
    DbSet<UploadSession> UploadSessions { get; }

    // Wave 2 Licensing, Activation & Security Sets
    DbSet<CommercialLicense> CommercialLicenses { get; }
    DbSet<CommercialEntitlement> CommercialEntitlements { get; }
    DbSet<Installation> Installations { get; }
    DbSet<ActivationToken> ActivationTokens { get; }
    DbSet<BootstrapChallengeRecord> BootstrapChallenges { get; }
    DbSet<InstallationRequestNonce> InstallationRequestNonces { get; }
    DbSet<ActivationRedemption> ActivationRedemptions { get; }
    DbSet<LicenseCertificate> LicenseCertificates { get; }
    DbSet<HardwareReview> HardwareReviews { get; }
    DbSet<PrimaryTransfer> PrimaryTransfers { get; }
    DbSet<SensitiveApprovalRequest> SensitiveApprovalRequests { get; }
    DbSet<ShopEnforcementPolicy> ShopEnforcementPolicies { get; }

    // Wave 3 Storage & Support Sets
    DbSet<EncryptedObjectRecord> EncryptedObjects { get; }
    DbSet<ObjectDownloadGrant> ObjectDownloadGrants { get; }
    DbSet<SupportTicket> SupportTickets { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
