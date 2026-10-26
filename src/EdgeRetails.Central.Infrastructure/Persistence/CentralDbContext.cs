using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
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
using EdgeRetails.Central.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Infrastructure.Persistence;

public class CentralDbContext : DbContext, ICentralDbContext
{
    public const string CentralSchema = "central";

    private readonly IDatabaseTargetGuard? _targetGuard;

    // Wave 1 Foundation Sets
    public DbSet<CentralOperationRecord> CentralOperationRecords => Set<CentralOperationRecord>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AdminRole> AdminRoles => Set<AdminRole>();
    public DbSet<AdminUserRole> AdminUserRoles => Set<AdminUserRole>();
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();
    public DbSet<LoginChallenge> LoginChallenges => Set<LoginChallenge>();
    public DbSet<StepUpGrant> StepUpGrants => Set<StepUpGrant>();
    public DbSet<CentralAuditRecord> CentralAuditRecords => Set<CentralAuditRecord>();
    public DbSet<CentralOutboxMessage> CentralOutboxMessages => Set<CentralOutboxMessage>();
    public DbSet<CentralConsumerEffect> CentralConsumerEffects => Set<CentralConsumerEffect>();
    public DbSet<AdminNotification> AdminNotifications => Set<AdminNotification>();
    public DbSet<CentralSigningRequest> CentralSigningRequests => Set<CentralSigningRequest>();
    public DbSet<CentralSigningKeyRecord> CentralSigningKeyRecords => Set<CentralSigningKeyRecord>();
    public DbSet<CentralTrustSetRecord> CentralTrustSetRecords => Set<CentralTrustSetRecord>();

    // Wave 2 Commercial Sets
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Shop> Shops => Set<Shop>();
    public DbSet<PlanCatalogItem> PlanCatalogItems => Set<PlanCatalogItem>();
    public DbSet<PricingSnapshot> PricingSnapshots => Set<PricingSnapshot>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PaymentEvidence> PaymentEvidences => Set<PaymentEvidence>();
    public DbSet<PaymentReviewRecord> PaymentReviewRecords => Set<PaymentReviewRecord>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    // Wave 2 Licensing, Activation & Security Sets
    public DbSet<CommercialLicense> CommercialLicenses => Set<CommercialLicense>();
    public DbSet<CommercialEntitlement> CommercialEntitlements => Set<CommercialEntitlement>();
    public DbSet<Installation> Installations => Set<Installation>();
    public DbSet<ActivationToken> ActivationTokens => Set<ActivationToken>();
    public DbSet<BootstrapChallengeRecord> BootstrapChallenges => Set<BootstrapChallengeRecord>();
    public DbSet<InstallationRequestNonce> InstallationRequestNonces => Set<InstallationRequestNonce>();
    public DbSet<ActivationRedemption> ActivationRedemptions => Set<ActivationRedemption>();
    public DbSet<LicenseCertificate> LicenseCertificates => Set<LicenseCertificate>();
    public DbSet<HardwareReview> HardwareReviews => Set<HardwareReview>();
    public DbSet<PrimaryTransfer> PrimaryTransfers => Set<PrimaryTransfer>();
    public DbSet<SensitiveApprovalRequest> SensitiveApprovalRequests => Set<SensitiveApprovalRequest>();
    public DbSet<ShopEnforcementPolicy> ShopEnforcementPolicies => Set<ShopEnforcementPolicy>();

    // Wave 3 Storage & Support Sets
    public DbSet<EncryptedObjectRecord> EncryptedObjects => Set<EncryptedObjectRecord>();
    public DbSet<ObjectDownloadGrant> ObjectDownloadGrants => Set<ObjectDownloadGrant>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();

    public CentralDbContext(DbContextOptions<CentralDbContext> options, IDatabaseTargetGuard? targetGuard = null)
        : base(options)
    {
        _targetGuard = targetGuard;
        ValidateTarget();
    }

    private void ValidateTarget()
    {
        if (_targetGuard is not null && Database.IsRelational())
        {
            var connStr = Database.GetConnectionString();
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                _targetGuard.ValidateConnectionString(connStr);
            }
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(new AppendOnlyAuditInterceptor(), new OwnedPostgresConnectionInterceptor());
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(CentralSchema);

        modelBuilder.Entity<CentralConsumerEffect>(b => { b.ToTable("outbox_consumer_effects"); b.HasKey(e => e.MessageId); });
        modelBuilder.Entity<AdminNotification>(b =>
        {
            b.ToTable("admin_notifications"); b.HasKey(e => e.Id);
            b.Property(e => e.Title).HasMaxLength(200).IsRequired();
            b.Property(e => e.Category).HasMaxLength(80).IsRequired();
            b.Property(e => e.TargetType).HasMaxLength(80).IsRequired();
            b.Property(e => e.RequiredCapability).HasMaxLength(100).IsRequired();
            b.HasIndex(e => new { e.OccurredAtUtc, e.Id });
        });
        // CentralOperationRecord
        modelBuilder.Entity<CentralOperationRecord>(b =>
        {
            b.ToTable("operation_records");
            b.HasKey(e => e.Id);
            b.Property(e => e.OperationType).HasMaxLength(128).IsRequired();
            b.Property(e => e.ActorScope).HasMaxLength(256).IsRequired();
            b.Property(e => e.PayloadSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.Reason).HasMaxLength(512).IsRequired();
            b.Property(e => e.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(e => e.ResultReference).HasMaxLength(256);
            b.Property(e => e.ErrorCode).HasMaxLength(64);
            b.Property(e => e.ErrorMessageKey).HasMaxLength(128);

            b.HasIndex(e => e.ClientOperationId).IsUnique();
            b.HasIndex(e => new { e.ActorScope, e.OperationType, e.PayloadSha256 });
            b.HasIndex(e => e.CreatedAtUtc);
        });

        // AdminUser
        modelBuilder.Entity<AdminUser>(b =>
        {
            b.ToTable("admin_users");
            b.HasKey(e => e.Id);
            b.Property(e => e.Username).HasMaxLength(128).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();
            b.Property(e => e.PasswordHash).HasMaxLength(256).IsRequired();
            b.Property(e => e.Salt).HasMaxLength(128).IsRequired();
            b.Property(e => e.MfaSecret).HasMaxLength(128);
            b.Property(e => e.RowVersion).IsConcurrencyToken().IsRequired();

            b.HasIndex(e => e.Username).IsUnique();
        });

        // AdminRole
        modelBuilder.Entity<AdminRole>(b =>
        {
            b.ToTable("admin_roles");
            b.HasKey(e => e.Id);
            b.Property(e => e.Name).HasMaxLength(64).IsRequired();
            b.Property(e => e.Description).HasMaxLength(256);
            b.Property(e => e.CapabilitiesJson).HasColumnType("text").IsRequired();

            b.HasIndex(e => e.Name).IsUnique();
        });

        // AdminUserRole
        modelBuilder.Entity<AdminUserRole>(b =>
        {
            b.ToTable("admin_user_roles");
            b.HasKey(e => new { e.AdminUserId, e.RoleId });

            b.HasOne(e => e.AdminUser)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(e => e.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(e => e.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // AdminSession
        modelBuilder.Entity<AdminSession>(b =>
        {
            b.ToTable("admin_sessions");
            b.HasKey(e => e.Id);
            b.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.CapabilitiesJson).HasColumnType("text").IsRequired();
            b.Property(e => e.RevocationReason).HasMaxLength(256);

            b.HasIndex(e => e.TokenHash).IsUnique();
            b.HasIndex(e => e.AdminUserId);
            b.HasIndex(e => e.ExpiresAtUtc);

            b.HasOne(e => e.AdminUser)
                .WithMany()
                .HasForeignKey(e => e.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // LoginChallenge
        modelBuilder.Entity<LoginChallenge>(b =>
        {
            b.ToTable("login_challenges");
            b.HasKey(e => e.Id);

            b.HasIndex(e => e.AdminUserId);
            b.HasIndex(e => e.ExpiresAtUtc);

            b.HasOne(e => e.AdminUser)
                .WithMany()
                .HasForeignKey(e => e.AdminUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // StepUpGrant
        modelBuilder.Entity<StepUpGrant>(b =>
        {
            b.ToTable("step_up_grants");
            b.HasKey(e => e.Id);
            b.Property(e => e.ActionScope).HasMaxLength(128).IsRequired();
            b.Property(e => e.TargetPayloadSha256).HasMaxLength(64).IsRequired();

            b.HasIndex(e => new { e.SessionId, e.ActionScope, e.TargetPayloadSha256 });
            b.HasIndex(e => e.ExpiresAtUtc);
        });

        // CentralAuditRecord
        modelBuilder.Entity<CentralAuditRecord>(b =>
        {
            b.ToTable("audit_records");
            b.HasKey(e => e.Id);
            b.Property(e => e.ActorId).HasMaxLength(128).IsRequired();
            b.Property(e => e.ActorUsername).HasMaxLength(128);
            b.Property(e => e.Action).HasMaxLength(128).IsRequired();
            b.Property(e => e.Resource).HasMaxLength(128).IsRequired();
            b.Property(e => e.ResourceId).HasMaxLength(128);
            b.Property(e => e.Reason).HasMaxLength(512);
            b.Property(e => e.CorrelationId).HasMaxLength(128).IsRequired();
            b.Property(e => e.ClientIp).HasMaxLength(64);
            b.Property(e => e.UserAgent).HasMaxLength(256);
            b.Property(e => e.PayloadSha256).HasMaxLength(64);
            b.Property(e => e.DetailsJson).HasColumnType("text").IsRequired();

            b.HasIndex(e => e.TimestampUtc);
            b.HasIndex(e => e.CorrelationId);
            b.HasIndex(e => e.ActorId);
        });

        // CentralOutboxMessage
        modelBuilder.Entity<CentralOutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(e => e.Id);
            b.Property(e => e.EventType).HasMaxLength(128).IsRequired();
            b.Property(e => e.AggregateType).HasMaxLength(128).IsRequired();
            b.Property(e => e.AggregateId).HasMaxLength(128).IsRequired();
            b.Property(e => e.PayloadJson).HasColumnType("text").IsRequired();
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(e => e.LastError).HasMaxLength(1024);

            b.HasIndex(e => new { e.Status, e.OccurredOnUtc });
        });

        // CentralSigningRequest
        modelBuilder.Entity<CentralSigningRequest>(b =>
        {
            b.ToTable("signing_requests");
            b.HasKey(e => e.Id);
            b.Property(e => e.PayloadSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.KeyId).HasMaxLength(128).IsRequired();
            b.Property(e => e.Purpose).HasMaxLength(64).IsRequired();
            b.Property(e => e.CanonicalPayload).HasColumnType("text").IsRequired();
            b.Property(e => e.Signature).HasMaxLength(1024).IsRequired();

            b.HasIndex(e => e.IssuanceRequestId);
            b.HasIndex(e => new { e.KeyId, e.KeyVersion });
        });

        // CentralSigningKeyRecord
        modelBuilder.Entity<CentralSigningKeyRecord>(b =>
        {
            b.ToTable("signing_keys");
            b.HasKey(e => e.KeyId);
            b.Property(e => e.AlgorithmProfileId).HasMaxLength(32).IsRequired();
            b.Property(e => e.Purpose).HasMaxLength(64).IsRequired();
            b.Property(e => e.PublicKeyPem).HasColumnType("text").IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();

            b.HasIndex(e => e.Status);
        });

        // CentralTrustSetRecord
        modelBuilder.Entity<CentralTrustSetRecord>(b =>
        {
            b.ToTable("trust_sets");
            b.HasKey(e => e.TrustSetVersion);
            b.Property(e => e.SignedByTrustedRootKeyId).HasMaxLength(128).IsRequired();
            b.Property(e => e.Signature).HasMaxLength(1024).IsRequired();
            b.Property(e => e.KeysJson).HasColumnType("text").IsRequired();
            b.Property(e => e.RevokedKeyIdsJson).HasColumnType("text").IsRequired();
        });

        // -------------------------------------------------------------
        // Wave 2 Commercial Entities
        // -------------------------------------------------------------

        // Customer
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("customers");
            b.HasKey(e => e.Id);
            b.Property(e => e.LegalName).HasMaxLength(256).IsRequired();
            b.Property(e => e.ContactEmail).HasMaxLength(256).IsRequired();
            b.Property(e => e.ContactPhone).HasMaxLength(64);
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ContactEmail);
        });

        // Shop
        modelBuilder.Entity<Shop>(b =>
        {
            b.ToTable("shops");
            b.HasKey(e => e.Id);
            b.Property(e => e.ShopCode).HasMaxLength(64).IsRequired();
            b.Property(e => e.ShopName).HasMaxLength(256).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.PrimaryCapacity).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ShopCode).IsUnique();
            b.HasIndex(e => e.CustomerId);
        });

        // PlanCatalogItem
        modelBuilder.Entity<PlanCatalogItem>(b =>
        {
            b.ToTable("plan_catalog_items");
            b.HasKey(e => e.Id);
            b.Property(e => e.Code).HasMaxLength(64).IsRequired();
            b.Property(e => e.Name).HasMaxLength(256).IsRequired();
            b.Property(e => e.PurchaseTarget).HasMaxLength(64).IsRequired();
            b.Property(e => e.TermKind).HasMaxLength(64).IsRequired();
            b.Property(e => e.BillingPeriodId).HasMaxLength(64);
            b.Property(e => e.UnitPrice).HasPrecision(18, 2).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(8).IsRequired();
            b.Property(e => e.IsActive).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.Code).IsUnique();
        });

        // PricingSnapshot
        modelBuilder.Entity<PricingSnapshot>(b =>
        {
            b.ToTable("pricing_snapshots");
            b.HasKey(e => e.Id);
            b.Property(e => e.PurchaseTarget).HasMaxLength(64).IsRequired();
            b.Property(e => e.TermKind).HasMaxLength(64).IsRequired();
            b.Property(e => e.BillingPeriodId).HasMaxLength(64);
            b.Property(e => e.UnitPrice).HasPrecision(18, 2).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(8).IsRequired();
            b.Property(e => e.ServerGeneratedDigest).HasMaxLength(64).IsRequired();
            b.Property(e => e.AuthorityVersion).IsRequired();

            b.HasIndex(e => new { e.ShopId, e.CatalogItemId });
        });

        // Order
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.HasKey(e => e.Id);
            b.Property(e => e.OrderType).HasMaxLength(32).IsRequired();
            b.Property(e => e.PurchaseTarget).HasMaxLength(64).IsRequired();
            b.Property(e => e.TermKind).HasMaxLength(64).IsRequired();
            b.Property(e => e.BillingPeriodId).HasMaxLength(64);
            b.Property(e => e.TotalAmount).HasPrecision(18, 2).IsRequired();
            b.Property(e => e.Currency).HasMaxLength(8).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.PlanId);
            b.HasIndex(e => e.BaseLicenseId);
            b.HasIndex(e => e.ExistingEntitlementId);
            b.HasIndex(e => new { e.CustomerId, e.ShopId });
            b.HasIndex(e => e.SnapshotId);
        });

        // PaymentEvidence
        modelBuilder.Entity<PaymentEvidence>(b =>
        {
            b.ToTable("payment_evidences");
            b.HasKey(e => e.Id);
            b.Property(e => e.StoragePath).HasMaxLength(512).IsRequired();
            b.Property(e => e.Sha256Digest).HasMaxLength(64).IsRequired();
            b.Property(e => e.MimeType).HasMaxLength(128).IsRequired();
            b.Property(e => e.ReviewStatus).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.OrderId);
            b.HasIndex(e => e.UploadSessionId);
        });

        // PaymentReviewRecord
        modelBuilder.Entity<PaymentReviewRecord>(b =>
        {
            b.ToTable("payment_review_records");
            b.HasKey(e => e.Id);
            b.Property(e => e.Decision).HasMaxLength(32).IsRequired();
            b.Property(e => e.ReasonCode);
            b.Property(e => e.Notes).HasMaxLength(1024);

            b.HasIndex(e => e.OrderId);
            b.HasIndex(e => e.EvidenceId);
        });

        // UploadSession
        modelBuilder.Entity<UploadSession>(b =>
        {
            b.ToTable("upload_sessions");
            b.HasKey(e => e.Id);
            b.Property(e => e.Purpose).HasMaxLength(64).IsRequired();
            b.Property(e => e.ExpectedSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.ActualSha256).HasMaxLength(64);
            b.Property(e => e.StoragePath).HasMaxLength(512).IsRequired();
            b.Property(e => e.ContentType).HasMaxLength(128);
            b.Property(e => e.RetentionPolicyId);
            b.Property(e => e.RetentionUntilUtc);
            b.Property(e => e.IsUnderLegalHold);
            b.Property(e => e.LocalManifestAuthenticationOpaque).HasMaxLength(512);
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.TargetEntityId);
            b.HasIndex(e => e.ShopId);
        });

        // -------------------------------------------------------------
        // Wave 2 Licensing, Activation & Security Entities
        // -------------------------------------------------------------

        // CommercialLicense
        modelBuilder.Entity<CommercialLicense>(b =>
        {
            b.ToTable("commercial_licenses");
            b.HasKey(e => e.Id);
            b.Property(e => e.TermKind).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.AuthorityRevision).IsRequired();
            b.Property(e => e.PrimaryCapacity).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ShopId);
            b.HasIndex(e => e.CustomerId);
        });

        // CommercialEntitlement
        modelBuilder.Entity<CommercialEntitlement>(b =>
        {
            b.ToTable("commercial_entitlements");
            b.HasKey(e => e.Id);
            b.Property(e => e.Kind).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => new { e.LicenseId, e.Kind });
        });

        // Installation
        modelBuilder.Entity<Installation>(b =>
        {
            b.ToTable("installations");
            b.HasKey(e => e.Id);
            b.Property(e => e.MachineFingerprint).HasMaxLength(128).IsRequired();
            b.Property(e => e.PublicKeyPem).HasColumnType("text").IsRequired();
            b.Property(e => e.PublicKeyFingerprintSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.Hostname).HasMaxLength(256);
            b.Property(e => e.OsPlatform).HasMaxLength(64);
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.DeviceIdentityHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.HardwareFingerprintHash).HasMaxLength(64).IsRequired();
            b.Property(e => e.SignalQuality).HasMaxLength(16).IsRequired();
            b.Property(e => e.ApplicationVersion).HasMaxLength(128).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ShopId);
            b.HasIndex(e => e.LicenseId);
        });

        // ActivationToken
        modelBuilder.Entity<ActivationToken>(b =>
        {
            b.ToTable("activation_tokens");
            b.HasKey(e => e.Id);
            b.Property(e => e.TokenHashSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.TokenPolicyId);
            b.Property(e => e.DeliveryChannelId);
            b.Property(e => e.DeliveryReference).HasMaxLength(256);
            b.Property(e => e.DeliverySecretKeyId).HasMaxLength(128);
            b.Property(e => e.ProtectedDeliverySecret).HasColumnType("text");
            b.Property(e => e.DeliveryState).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.TokenHashSha256).IsUnique();
            b.HasIndex(e => e.LicenseId);
            b.HasIndex(e => e.ShopId);
            b.HasIndex(e => e.DeliveryReference).IsUnique().HasFilter("\"DeliveryReference\" IS NOT NULL");
        });

        modelBuilder.Entity<BootstrapChallengeRecord>(b =>
        {
            b.ToTable("bootstrap_challenges");
            b.HasKey(e => e.Id);
            b.Property(e => e.ActivationTokenHashSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.InstallationPublicKeyPem).HasMaxLength(8192).IsRequired();
            b.Property(e => e.InstallationPublicKeyFingerprintSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.ChallengeNonce).HasMaxLength(128).IsRequired();
            b.Property(e => e.ChallengeNonceHashSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.RedeemedAtUtc).IsConcurrencyToken();
            b.Property(e => e.RegisteredAtUtc).IsConcurrencyToken();
            b.HasIndex(e => e.RequestNonce).IsUnique();
            b.HasIndex(e => e.ChallengeNonceHashSha256).IsUnique();
            b.HasIndex(e => new { e.InstallationId, e.ExpiresAtUtc });
        });

        modelBuilder.Entity<InstallationRequestNonce>(b =>
        {
            b.ToTable("installation_request_nonces", CentralSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Nonce).HasMaxLength(128).IsRequired();
            b.HasIndex(x => new { x.InstallationId, x.Nonce }).IsUnique();
            b.HasIndex(x => x.TimestampUtc);
        });

        // ActivationRedemption
        modelBuilder.Entity<ActivationRedemption>(b =>
        {
            b.ToTable("activation_redemptions");
            b.HasKey(e => e.Id);
            b.Property(e => e.ChallengeNonce).HasMaxLength(128).IsRequired();
            b.Property(e => e.ClientSignatureHex).HasMaxLength(512).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.DecisionReasonCode).HasMaxLength(128);
            b.Property(e => e.ValidationSnapshotJson).HasColumnType("text");
            b.Property(e => e.ValidationSnapshotVersion);
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.TokenId);
            b.HasIndex(e => e.InstallationId);
        });

        // LicenseCertificate
        modelBuilder.Entity<LicenseCertificate>(b =>
        {
            b.ToTable("license_certificates");
            b.HasKey(e => e.Id);
            b.Property(e => e.CertificateSerial).HasMaxLength(64).IsRequired();
            b.Property(e => e.SignedEnvelopeJson).HasColumnType("text").IsRequired();
            b.Property(e => e.RS256Signature).HasMaxLength(1024).IsRequired();
            b.Property(e => e.DigestSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.InstallationId);
            b.HasIndex(e => e.LicenseId);
            b.HasIndex(e => e.CertificateSerial).IsUnique();
        });

        // HardwareReview
        modelBuilder.Entity<HardwareReview>(b =>
        {
            b.ToTable("hardware_reviews");
            b.HasKey(e => e.Id);
            b.Property(e => e.ReportedMachineFingerprint).HasMaxLength(128).IsRequired();
            b.Property(e => e.StoredMachineFingerprint).HasMaxLength(128);
            b.Property(e => e.ReportedMachineFingerprintId).IsRequired();
            b.Property(e => e.StoredMachineFingerprintId).IsRequired();
            b.Property(e => e.ContractDecision).HasMaxLength(32);
            b.Property(e => e.VerificationEvidenceId);
            b.Property(e => e.DiscrepancyReason).HasMaxLength(512);
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.InstallationId);
            b.HasIndex(e => e.ShopId);
        });

        // PrimaryTransfer
        modelBuilder.Entity<PrimaryTransfer>(b =>
        {
            b.ToTable("primary_transfers");
            b.HasKey(e => e.Id);
            b.Property(e => e.TransferReason).HasMaxLength(512);
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.ContractDecision).HasMaxLength(32);
            b.Property(e => e.RecoveryType).HasMaxLength(32);
            b.Property(e => e.VerificationEvidenceId);
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ShopId);
            b.HasIndex(e => e.SourceInstallationId);
            b.HasIndex(e => e.TargetInstallationId);
        });

        // SensitiveApprovalRequest
        modelBuilder.Entity<SensitiveApprovalRequest>(b =>
        {
            b.ToTable("sensitive_approval_requests");
            b.HasKey(e => e.Id);
            b.Property(e => e.OperationType).HasMaxLength(64).IsRequired();
            b.Property(e => e.TargetPayloadSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.ActionScope).HasMaxLength(64).IsRequired();
            b.Property(e => e.RequestReason).HasMaxLength(500).IsRequired();
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => new { e.TargetEntityId, e.OperationType });
            b.HasIndex(e => e.Status);
        });

        // ShopEnforcementPolicy
        modelBuilder.Entity<ShopEnforcementPolicy>(b =>
        {
            b.ToTable("shop_enforcement_policies");
            b.HasKey(e => e.Id);
            b.Property(e => e.EnforcementMode).HasMaxLength(32).IsRequired();
            b.Property(e => e.SignedPolicyEnvelopeJson).HasColumnType("text").IsRequired();
            b.Property(e => e.RS256Signature).HasMaxLength(1024).IsRequired();
            b.Property(e => e.KeyId).HasMaxLength(128);
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => new { e.ShopId, e.PolicyVersion });
        });

        // -------------------------------------------------------------
        // Wave 3 Storage & Support Entities
        // -------------------------------------------------------------

        // EncryptedObjectRecord
        modelBuilder.Entity<EncryptedObjectRecord>(b =>
        {
            b.ToTable("encrypted_objects");
            b.HasKey(e => e.Id);
            b.Property(e => e.Purpose).HasMaxLength(64).IsRequired();
            b.Property(e => e.ContentType).HasMaxLength(128).IsRequired();
            b.Property(e => e.CiphertextSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.StoragePath).HasMaxLength(512).IsRequired();
            b.Property(e => e.RetentionPolicyId);
            b.Property(e => e.RetentionUntilUtc);
            b.Property(e => e.IsUnderLegalHold);
            b.Property(e => e.LocalManifestAuthenticationOpaque).HasMaxLength(512);
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ShopId);
            b.HasIndex(e => e.InstallationId);
            b.HasIndex(e => e.RetentionPolicyId);
        });

        // SupportTicket
        modelBuilder.Entity<SupportTicket>(b =>
        {
            b.ToTable("support_tickets");
            b.HasKey(e => e.Id);
            b.Property(e => e.Subject).HasMaxLength(256).IsRequired();
            b.Property(e => e.Summary).HasMaxLength(2048).IsRequired();
            b.Property(e => e.Status).HasMaxLength(32).IsRequired();
            b.Property(e => e.AttachmentObjectIdsJson).HasColumnType("text").IsRequired();
            b.Property(e => e.RepliesJson).HasColumnType("text").IsRequired();
            b.HasIndex(e => e.AssignedAdminUserId);
            b.Property(e => e.RowVersion).IsConcurrencyToken();

            b.HasIndex(e => e.ShopId);
            b.HasIndex(e => e.RequesterPrincipalId);
            b.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<ObjectDownloadGrant>(b =>
        {
            b.ToTable("object_download_grants");
            b.HasKey(e => e.Id);
            b.Property(e => e.TokenSha256).HasMaxLength(64).IsRequired();
            b.Property(e => e.IssuedAtUtc).IsRequired();
            b.Property(e => e.ExpiresAtUtc).IsRequired();
            b.HasIndex(e => e.TokenSha256).IsUnique();
            b.HasIndex(e => e.ExpiresAtUtc);
            b.HasIndex(e => new { e.InstallationId, e.ObjectId, e.ExpiresAtUtc });
        });
    }
}
