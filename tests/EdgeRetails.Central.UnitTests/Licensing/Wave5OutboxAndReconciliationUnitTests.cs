using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.UnitTests.Activation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Licensing;

public class Wave5OutboxAndReconciliationUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly TestProtectedSignerClient _signerClient;
    private readonly CentralAuditService _auditService;
    private readonly ICanonicalJsonSerializer _serializer;
    private readonly ActivationService _activationService;

    public Wave5OutboxAndReconciliationUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(databaseName: $"CentralWave5UnitTestDb_{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CentralDbContext(options);
        _signerClient = new TestProtectedSignerClient();
        _auditService = new CentralAuditService(_dbContext);
        _serializer = new CanonicalJsonSerializer();

        _activationService = new ActivationService(
            _dbContext,
            _signerClient,
            _auditService,
            serializer: _serializer);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _signerClient.Dispose();
    }

    // ==========================================
    // CONE-T24: Outbox Message Lifecycle Tests
    // ==========================================

    [Fact]
    public void CONE_T24_OutboxMessage_Create_InitializesPendingStatus()
    {
        var msgId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var msg = CentralOutboxMessage.Create(
            msgId,
            "shop.license_reconciled",
            "Installation",
            "agg-123",
            "{\"status\":\"active\"}",
            now);

        Assert.Equal(msgId, msg.Id);
        Assert.Equal(OutboxStatus.PENDING, msg.Status);
        Assert.Equal("shop.license_reconciled", msg.EventType);
        Assert.Equal(0, msg.RetryCount);
        Assert.Null(msg.ProcessedAtUtc);
        Assert.Null(msg.LastError);
    }

    [Fact]
    public void CONE_T24_OutboxMessage_MarkDispatched_UpdatesProcessedTimestamp()
    {
        var msg = CentralOutboxMessage.Create(
            Guid.NewGuid(), "test.event", "Order", "ord-1", "{}", DateTimeOffset.UtcNow);

        var dispatchTime = DateTimeOffset.UtcNow;
        msg.MarkDispatched(dispatchTime);

        Assert.Equal(OutboxStatus.DISPATCHED, msg.Status);
        Assert.Equal(dispatchTime, msg.ProcessedAtUtc);
    }

    [Fact]
    public void CONE_T24_OutboxMessage_MarkFailed_IncrementsRetryCount()
    {
        var msg = CentralOutboxMessage.Create(
            Guid.NewGuid(), "test.event", "Order", "ord-1", "{}", DateTimeOffset.UtcNow);

        msg.MarkFailed("Network timeout", maxRetries: 3);

        Assert.Equal(OutboxStatus.FAILED, msg.Status);
        Assert.Equal(1, msg.RetryCount);
        Assert.Equal("Network timeout", msg.LastError);
    }

    [Fact]
    public void CONE_T24_OutboxMessage_MaxRetries_TransitionsToDeadLetter()
    {
        var msg = CentralOutboxMessage.Create(
            Guid.NewGuid(), "test.event", "Order", "ord-1", "{}", DateTimeOffset.UtcNow);

        msg.MarkFailed("Attempt 1", maxRetries: 2);
        Assert.Equal(OutboxStatus.FAILED, msg.Status);
        Assert.Equal(1, msg.RetryCount);

        msg.MarkFailed("Attempt 2 - final", maxRetries: 2);
        Assert.Equal(OutboxStatus.DEAD_LETTER, msg.Status);
        Assert.Equal(2, msg.RetryCount);
        Assert.Equal("Attempt 2 - final", msg.LastError);
    }

    [Fact]
    public void CONE_T24_OutboxMessage_MarkDeadLetter_ImmediatelySetsDeadLetterStatus()
    {
        var msg = CentralOutboxMessage.Create(
            Guid.NewGuid(), "fatal.event", "Security", "sec-1", "{}", DateTimeOffset.UtcNow);

        msg.MarkDeadLetter("Permanent payload deserialization corruption");

        Assert.Equal(OutboxStatus.DEAD_LETTER, msg.Status);
        Assert.Equal("Permanent payload deserialization corruption", msg.LastError);
    }

    // ==========================================
    // CONE-T25: Hardware Review & Machine Binding
    // ==========================================

    [Fact]
    public void CONE_T25_HardwareReview_Create_SetsPendingStatusAndDetails()
    {
        var shopId = Guid.NewGuid();
        var installId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var review = HardwareReview.Create(
            shopId,
            installId,
            "HW_NEW_12345",
            "HW_OLD_00000",
            "CPU replacement following surge",
            now);

        Assert.NotEqual(Guid.Empty, review.Id);
        Assert.Equal(shopId, review.ShopId);
        Assert.Equal(installId, review.InstallationId);
        Assert.Equal("HW_NEW_12345", review.ReportedMachineFingerprint);
        Assert.Equal("HW_OLD_00000", review.StoredMachineFingerprint);
        Assert.Equal(HardwareReview.StatusPending, review.Status);
        Assert.Null(review.ReviewedByAdminId);
        Assert.Null(review.DecidedAtUtc);
    }

    [Fact]
    public void CONE_T25_HardwareReview_DecideApprove_UpdatesStatusAndAdminId()
    {
        var review = HardwareReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), "HW_NEW", "HW_OLD", "Upgrade", DateTimeOffset.UtcNow);

        var adminId = Guid.NewGuid();
        var decidedAt = DateTimeOffset.UtcNow;

        review.Decide(approve: true, adminId, decidedAt);

        Assert.Equal(HardwareReview.StatusApproved, review.Status);
        Assert.Equal(adminId, review.ReviewedByAdminId);
        Assert.Equal(decidedAt, review.DecidedAtUtc);
    }

    [Fact]
    public void CONE_T25_HardwareReview_DecideReject_UpdatesStatusToRejected()
    {
        var review = HardwareReview.Create(
            Guid.NewGuid(), Guid.NewGuid(), "HW_SUSPICIOUS", "HW_ORIGINAL", "Unknown drift", DateTimeOffset.UtcNow);

        var adminId = Guid.NewGuid();
        var decidedAt = DateTimeOffset.UtcNow;

        review.Decide(approve: false, adminId, decidedAt);

        Assert.Equal(HardwareReview.StatusRejected, review.Status);
        Assert.Equal(adminId, review.ReviewedByAdminId);
        Assert.Equal(decidedAt, review.DecidedAtUtc);
    }

    // ==========================================
    // CONE-T25: Reconciliation & Entitlement Tests
    // ==========================================

    [Fact]
    public async Task CONE_T25_Reconciliation_PreservesSignedLifetimeTerm_WhenValidUntilUtcIsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var shopId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installId = Guid.NewGuid();
        var certId = Guid.NewGuid();
        var hwFingerprint = "HW_VALID_LIFETIME_TERMINAL";

        var shop = Shop.Create(customerId, "SHOP01", "Wave 5 Lifetime Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        var catalogItem = PlanCatalogItem.Create("PLAN_LIFE", "Lifetime Retail", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 2500m, "USD");
        _dbContext.PlanCatalogItems.Add(catalogItem);

        var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
        _dbContext.PricingSnapshots.Add(snapshot);

        var order = Order.CreateFromSnapshot(snapshot, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        _dbContext.Orders.Add(order);

        // Commercial license with NULL ValidUntilUtc (Lifetime base)
        var license = CommercialLicense.Issue(order, now, null, now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        _dbContext.CommercialLicenses.Add(license);

        var installation = Installation.Register(shopId, licenseId, hwFingerprint, "PEM_KEY", "terminal-1", "Win11", now);
        installation.Approve(now);
        typeof(Installation).GetProperty("Id")!.SetValue(installation, installId);
        _dbContext.Installations.Add(installation);

        var cert = LicenseCertificate.Create(
            installId, licenseId, shopId, 1, "CERT123", "{}", "sig", "sha", now, now.AddDays(7), now.AddDays(14), now);
        typeof(LicenseCertificate).GetProperty("Id")!.SetValue(cert, certId);
        _dbContext.LicenseCertificates.Add(cert);

        await _dbContext.SaveChangesAsync();

        var reconcileCmd = new ReconcileCommandDto
        {
            InstallationId = installId,
            CertificateId = certId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hwFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        var entitlement = await _activationService.ReconcileInstallationAsync(reconcileCmd);

        Assert.NotNull(entitlement);
        Assert.NotNull(entitlement.Payload);
        Assert.Equal(CommercialTerm.Lifetime, entitlement.Payload.CommercialTerm.TermKind);
        Assert.Null(entitlement.Payload.CommercialTerm.ValidUntilUtc); // Strictly NULL for Lifetime
        Assert.Equal(hwFingerprint, entitlement.Payload.HardwareFingerprintHash);
    }

    [Fact]
    public async Task CONE_T25_Reconciliation_MaintainsIndependentLeaseAndGraceWindows()
    {
        var now = DateTimeOffset.UtcNow;
        var shopId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var installId = Guid.NewGuid();
        var certId = Guid.NewGuid();
        var hwFingerprint = "HW_TIMED_WINDOWS_TERMINAL";

        var shop = Shop.Create(customerId, "SHOP02", "Wave 5 Lease Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        var catalogItem = PlanCatalogItem.Create("PLAN_SUB", "Annual Sub", PlanCatalogItem.TargetBaseLicense, CommercialTerm.FixedTerm, "ANNUAL", 500m, "USD");
        _dbContext.PlanCatalogItems.Add(catalogItem);

        var snapshot = PricingSnapshot.Create(shopId, catalogItem, 1, now);
        _dbContext.PricingSnapshots.Add(snapshot);

        var order = Order.CreateFromSnapshot(snapshot, customerId, now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        _dbContext.Orders.Add(order);

        var license = CommercialLicense.Issue(order, now, now.AddYears(1), now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        _dbContext.CommercialLicenses.Add(license);

        var installation = Installation.Register(shopId, licenseId, hwFingerprint, "PEM_KEY", "terminal-2", "Win11", now);
        installation.Approve(now);
        typeof(Installation).GetProperty("Id")!.SetValue(installation, installId);
        _dbContext.Installations.Add(installation);

        var cert = LicenseCertificate.Create(
            installId, licenseId, shopId, 1, "CERT456", "{}", "sig", "sha", now, now.AddDays(7), now.AddDays(14), now);
        typeof(LicenseCertificate).GetProperty("Id")!.SetValue(cert, certId);
        _dbContext.LicenseCertificates.Add(cert);

        // Custom enforcement policy with 86400s lease duration and 172800s grace
        var policy = ShopEnforcementPolicy.Create(
            shopId, licenseId, 1, ShopEnforcementPolicy.FullStop, null, Guid.Empty, 86400, 172800, 300, Guid.Empty, "{\"policy\":\"full_stop\"}", "sig123", "central-ca-v1", Guid.NewGuid(), now);
        _dbContext.ShopEnforcementPolicies.Add(policy);

        await _dbContext.SaveChangesAsync();

        var reconcileCmd = new ReconcileCommandDto
        {
            InstallationId = installId,
            CertificateId = certId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = hwFingerprint,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        var entitlement = await _activationService.ReconcileInstallationAsync(reconcileCmd);

        Assert.NotNull(entitlement);
        Assert.NotNull(entitlement.Payload);
        // Lease expires 86400s after now
        Assert.True(entitlement.Payload.LeaseExpiresAtUtc > now);
        // Offline grace extends beyond lease expiration
        Assert.True(entitlement.Payload.OfflineGraceUntilUtc > entitlement.Payload.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task CONE_T25_Reconciliation_MismatchedHardware_ThrowsWithoutApprovedReview()
    {
        var now = DateTimeOffset.UtcNow;
        var shopId = Guid.NewGuid();
        var installId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var certId = Guid.NewGuid();

        var shop = Shop.Create(Guid.NewGuid(), "SHOP03", "Hardware Guard Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        var item = PlanCatalogItem.Create("PLAN_BASE", "Base", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1000m, "USD");
        _dbContext.PlanCatalogItems.Add(item);
        var snapshot = PricingSnapshot.Create(shopId, item, 1, now);
        _dbContext.PricingSnapshots.Add(snapshot);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        _dbContext.Orders.Add(order);

        var license = CommercialLicense.Issue(order, now, null, now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        _dbContext.CommercialLicenses.Add(license);

        var installation = Installation.Register(shopId, licenseId, "HW_REGISTERED_PRIMARY", "PEM", "term", "Win", now);
        installation.Approve(now);
        typeof(Installation).GetProperty("Id")!.SetValue(installation, installId);
        _dbContext.Installations.Add(installation);

        var cert = LicenseCertificate.Create(installId, licenseId, shopId, 1, "CERT789", "{}", "sig", "sha", now, now.AddDays(7), now.AddDays(14), now);
        typeof(LicenseCertificate).GetProperty("Id")!.SetValue(cert, certId);
        _dbContext.LicenseCertificates.Add(cert);

        await _dbContext.SaveChangesAsync();

        var reconcileCmd = new ReconcileCommandDto
        {
            InstallationId = installId,
            CertificateId = certId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = "HW_TAMPERED_MACHINE",
            ApplicationVersion = "1.0.0",
            LastVerifiedVendorTimeUtc = now
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _activationService.ReconcileInstallationAsync(reconcileCmd));

        Assert.Equal("HARDWARE_FINGERPRINT_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task CONE_T25_Reconciliation_MismatchedHardware_SucceedsWithApprovedReview()
    {
        var now = DateTimeOffset.UtcNow;
        var shopId = Guid.NewGuid();
        var installId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var certId = Guid.NewGuid();
        var newHw = "HW_NEW_REPLACED_UNDER_WARRANTY";

        var shop = Shop.Create(Guid.NewGuid(), "SHOP04", "Approved HW Shop", now);
        typeof(Shop).GetProperty("Id")!.SetValue(shop, shopId);
        _dbContext.Shops.Add(shop);

        var item = PlanCatalogItem.Create("PLAN_BASE2", "Base2", PlanCatalogItem.TargetBaseLicense, CommercialTerm.Lifetime, null, 1000m, "USD");
        _dbContext.PlanCatalogItems.Add(item);
        var snapshot = PricingSnapshot.Create(shopId, item, 1, now);
        _dbContext.PricingSnapshots.Add(snapshot);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        typeof(Order).GetProperty("Status")!.SetValue(order, Order.StatusPaidApproved);
        _dbContext.Orders.Add(order);

        var license = CommercialLicense.Issue(order, now, null, now);
        typeof(CommercialLicense).GetProperty("Id")!.SetValue(license, licenseId);
        _dbContext.CommercialLicenses.Add(license);

        var installation = Installation.Register(shopId, licenseId, "HW_OLD_MOTHERBOARD", "PEM", "term", "Win", now);
        installation.Approve(now);
        typeof(Installation).GetProperty("Id")!.SetValue(installation, installId);
        _dbContext.Installations.Add(installation);

        var cert = LicenseCertificate.Create(installId, licenseId, shopId, 1, "CERT999", "{}", "sig", "sha", now, now.AddDays(7), now.AddDays(14), now);
        typeof(LicenseCertificate).GetProperty("Id")!.SetValue(cert, certId);
        _dbContext.LicenseCertificates.Add(cert);

        // Add APPROVED hardware review for the replacement machine
        var review = HardwareReview.Create(shopId, installId, newHw, "HW_OLD_MOTHERBOARD", "RMA Replacement", now);
        review.Decide(true, Guid.NewGuid(), now);
        _dbContext.HardwareReviews.Add(review);

        await _dbContext.SaveChangesAsync();

        var reconcileCmd = new ReconcileCommandDto
        {
            InstallationId = installId,
            CertificateId = certId,
            AuthorityRevision = 1,
            HardwareFingerprintHash = newHw,
            ApplicationVersion = "1.0.0-central",
            LastVerifiedVendorTimeUtc = now
        };

        var result = await _activationService.ReconcileInstallationAsync(reconcileCmd);

        Assert.NotNull(result);
        Assert.NotNull(result.Payload);
        Assert.Equal(newHw, result.Payload.HardwareFingerprintHash);
    }

    // ==========================================
    // CONE-T26: Policy Presentation & Modes
    // ==========================================

    [Fact]
    public void CONE_T26_EnforcementPolicy_FullStopVsExtension_PreservesDistinctModes()
    {
        var now = DateTimeOffset.UtcNow;
        var fullStopPolicy = ShopEnforcementPolicy.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, ShopEnforcementPolicy.FullStop, null, Guid.Empty, 86400, 86400, 300, Guid.Empty, "{\"policy\":\"full_stop\"}", "sig1", "central-ca-v1", Guid.NewGuid(), now);

        var extensionPolicy = ShopEnforcementPolicy.Create(
            Guid.NewGuid(), Guid.NewGuid(), 2, ShopEnforcementPolicy.ConfigurableExtension, now.AddDays(30), Guid.Empty, 86400, 86400, 300, Guid.Empty, "{\"policy\":\"extension\"}", "sig2", "central-ca-v1", Guid.NewGuid(), now);

        Assert.Equal("FULL_STOP", fullStopPolicy.EnforcementMode);
        Assert.Null(fullStopPolicy.ExtensionEndUtc);

        Assert.Equal("CONFIGURABLE_EXTENSION", extensionPolicy.EnforcementMode);
        Assert.NotNull(extensionPolicy.ExtensionEndUtc);
    }

    [Fact]
    public void CONE_T26_OperationOutcome_IdempotentReplay_ProducesConsistentHash()
    {
        var payload = new AdminActionCommandDto
        {
            AdminUserId = Guid.NewGuid(),
            Action = "DISABLE"
        };

        var (canonical1, sha1) = _serializer.SerializeAndHash(payload);
        var (canonical2, sha2) = _serializer.SerializeAndHash(payload);

        Assert.Equal(canonical1, canonical2);
        Assert.Equal(sha1, sha2);
    }

    // ==========================================
    // CONE-T27: DatabaseTargetGuard Safety Tests
    // ==========================================

    [Fact]
    public void CONE_T27_DatabaseTargetGuard_RejectsNonLoopbackIP()
    {
        var guard = new DatabaseTargetGuard();
        var ex = Assert.Throws<DatabaseTargetGuardException>(() =>
            guard.ValidateConnectionString("Host=192.168.1.100;Port=55454;Database=central_test;Username=usr;Password=pwd"));

        Assert.Equal("NON_LOOPBACK_TARGET", ex.ViolationCode);
    }

    [Fact]
    public void CONE_T27_DatabaseTargetGuard_RejectsForbiddenPorts_5432_7150()
    {
        var guard = new DatabaseTargetGuard();

        var ex5432 = Assert.Throws<DatabaseTargetGuardException>(() =>
            guard.ValidateConnectionString("Host=127.0.0.1;Port=5432;Database=central_test;Username=usr;Password=pwd"));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex5432.ViolationCode);

        var ex7150 = Assert.Throws<DatabaseTargetGuardException>(() =>
            guard.ValidateConnectionString("Host=127.0.0.1;Port=7150;Database=central_test;Username=usr;Password=pwd"));
        Assert.Equal("FORBIDDEN_PORT_TARGET", ex7150.ViolationCode);
    }
}
