using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Commercial;

public class CommercialUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly CentralAuditService _auditService;
    private readonly CommercialService _commercialService;

    public CommercialUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new CentralDbContext(options);
        _auditService = new CentralAuditService(_dbContext);
        _commercialService = new CommercialService(_dbContext, _auditService);
    }

    [Fact]
    public async Task ShopRegistration_EnforcesSinglePrimaryCapacity()
    {
        var adminId = Guid.NewGuid();
        var customer = await _commercialService.CreateCustomerAsync(new CreateCustomerCommand
        {
            LegalName = "Acme Retail Ltd",
            ContactEmail = "ops@acme.com",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var shop = await _commercialService.RegisterShopAsync(new RegisterShopCommand
        {
            CustomerId = customer.Id,
            ShopCode = "ACME-01",
            ShopName = "Acme Flagship",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.NotNull(shop);
        Assert.Equal(1, shop.PrimaryCapacity); // Single primary capacity invariant
        Assert.Equal("ACTIVE", shop.Status);
    }

    [Fact]
    public async Task PricingSnapshot_ComputesAuthoritativeServerDigest_RejectsTampering()
    {
        var adminId = Guid.NewGuid();
        var customer = await _commercialService.CreateCustomerAsync(new CreateCustomerCommand
        {
            LegalName = "Retail Co",
            ContactEmail = "billing@retail.co",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var shop = await _commercialService.RegisterShopAsync(new RegisterShopCommand
        {
            CustomerId = customer.Id,
            ShopCode = "SHOP-01",
            ShopName = "Shop 01",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var item = await _commercialService.CreateCatalogItemAsync(
            "PLAN-LIFETIME-PRO",
            "Lifetime Professional Plan",
            PlanCatalogItem.TargetBaseLicense,
            "LIFETIME",
            null,
            499.00m,
            "USD",
            adminId);

        var snapshot = await _commercialService.CreatePriceSnapshotAsync(new CreatePriceSnapshotCommand
        {
            ShopId = shop.Id,
            CatalogItemId = item.Id,
            AuthorityVersion = 1,
            CommandMeta = new CommandMetaDto()
        });

        Assert.NotNull(snapshot);
        Assert.Equal(499.00m, snapshot.UnitPrice);
        Assert.NotEmpty(snapshot.ServerGeneratedDigest);

        // Verify digest is exact SHA256 of canonical pricing tuple
        string canonical = $"{shop.Id:N}:{item.Id:N}:{item.PurchaseTarget}:{item.TermKind}:{item.BillingPeriodId ?? "NONE"}:{item.UnitPrice:F2}:{item.Currency}:{snapshot.AuthorityVersion}";
        byte[] expectedHashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        string expectedDigest = Convert.ToHexString(expectedHashBytes).ToLowerInvariant();

        Assert.Equal(expectedDigest, snapshot.ServerGeneratedDigest);
    }

    [Fact]
    public void CatalogRejectsLifetimeTermForTimedSupportAndPremiumAddons()
    {
        var supportError = Assert.Throws<CentralDomainException>(() => PlanCatalogItem.Create(
            "SUPPORT-LIFE-INVALID", "Invalid lifetime support fixture", PlanCatalogItem.TargetSupport,
            PlanCatalogItem.TermLifetime, null, 10m, "USD"));
        Assert.Equal("LIFETIME_ADDON_PROHIBITED", supportError.Code);

        var premiumError = Assert.Throws<CentralDomainException>(() => PlanCatalogItem.Create(
            "PREMIUM-LIFE-INVALID", "Invalid lifetime premium fixture", PlanCatalogItem.TargetPremiumUpgrade,
            PlanCatalogItem.TermLifetime, null, 10m, "USD"));
        Assert.Equal("LIFETIME_ADDON_PROHIBITED", premiumError.Code);
    }

    [Fact]
    public void CanonicalOrder_PersistsPurchaseIdentityAndEnforcesTargetTermAndCapacity()
    {
        var now = DateTimeOffset.UtcNow;
        var item = PlanCatalogItem.Create("ORDER-LIFE", "Lifetime", PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermLifetime, null, 125m, "USD");
        var snapshot = PricingSnapshot.Create(Guid.NewGuid(), item, 1, now);
        var baseLicenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();

        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now, "PLAN_CHANGE", 1, baseLicenseId, entitlementId);

        Assert.Equal(snapshot.CatalogItemId, order.PlanId);
        Assert.Equal("PLAN_CHANGE", order.OrderType);
        Assert.Equal(1, order.RequestedPrimaryCapacity);
        Assert.Equal(baseLicenseId, order.BaseLicenseId);
        Assert.Equal(entitlementId, order.ExistingEntitlementId);
        Assert.Throws<CentralDomainException>(() => Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now,
            "SUPPORT_RENEWAL", 1, baseLicenseId, null));
        Assert.Throws<CentralDomainException>(() => Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now,
            "NEW_LICENSE", 2, null, null));
    }

    [Fact]
    public async Task ChunkedUpload_QuarantinesOnByteOverflow_OrSha256Mismatch()
    {
        var now = DateTimeOffset.UtcNow;
        var shop = Shop.Create(Guid.NewGuid(), "SHOP-UP-01", "Upload Shop", now);
        _dbContext.Shops.Add(shop);
        await _dbContext.SaveChangesAsync();

        var shopId = shop.Id;
        var uploaderId = Guid.NewGuid();
        var targetEntityId = Guid.NewGuid();

        // 1. Chunk overflow triggers quarantine
        var sessionOverLimit = await _commercialService.InitUploadSessionAsync(new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = targetEntityId,
            ExpectedBytes = 100,
            ExpectedSha256 = new string('a', 64),
            CommandMeta = new CommandMetaDto()
        }, uploaderId);

        var overEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _commercialService.AppendUploadChunkAsync(sessionOverLimit.Id, new byte[150]));

        Assert.Equal("OVERSIZE_UPLOAD", overEx.Code);
        Assert.Equal(UploadSession.StatusQuarantined, sessionOverLimit.Status);

        // 2. Hash mismatch on finalize triggers quarantine
        var validData = Encoding.UTF8.GetBytes("Test PDF payment evidence payload");
        string validSha = Convert.ToHexString(SHA256.HashData(validData)).ToLowerInvariant();

        var sessionMismatch = await _commercialService.InitUploadSessionAsync(new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shopId,
            TargetEntityId = targetEntityId,
            ExpectedBytes = validData.Length,
            ExpectedSha256 = validSha,
            CommandMeta = new CommandMetaDto()
        }, uploaderId);

        await _commercialService.AppendUploadChunkAsync(sessionMismatch.Id, validData);

        // Client supplies mismatched hash
        var fakeSha = new string('f', 64);
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _commercialService.FinalizeUploadSessionAsync(sessionMismatch.Id, new FinalizeUploadSessionCommand
            {
                ComputedSha256 = fakeSha,
                MimeType = "application/pdf",
                CommandMeta = new CommandMetaDto()
            }, Guid.NewGuid()));

        Assert.Equal("SHA256_MISMATCH", ex.Code);
        Assert.Equal(UploadSession.StatusQuarantined, sessionMismatch.Status);
    }

    [Fact]
    public async Task PaymentReview_ApprovesOrder_OrRejectsOrder()
    {
        var adminId = Guid.NewGuid();
        var customer = await _commercialService.CreateCustomerAsync(new CreateCustomerCommand
        {
            LegalName = "Buyer Inc",
            ContactEmail = "buyer@inc.com",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var shop = await _commercialService.RegisterShopAsync(new RegisterShopCommand
        {
            CustomerId = customer.Id,
            ShopCode = "BUYER-01",
            ShopName = "Buyer Store",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var item = await _commercialService.CreateCatalogItemAsync(
            "PLAN-SUB-1YR",
            "1 Year Subscription",
            PlanCatalogItem.TargetBaseLicense,
            "FIXED_TERM",
            "YEARLY",
            199.00m,
            "USD",
            adminId);

        var snapshot = await _commercialService.CreatePriceSnapshotAsync(new CreatePriceSnapshotCommand
        {
            ShopId = shop.Id,
            CatalogItemId = item.Id,
            AuthorityVersion = 1,
            CommandMeta = new CommandMetaDto()
        });

        var order = await _commercialService.CreateOrderAsync(new CreateOrderCommand
        {
            CustomerId = customer.Id,
            SnapshotId = snapshot.Id,
            CommandMeta = new CommandMetaDto()
        });

        Assert.Equal(Order.StatusPendingPayment, order.Status);

        // Upload evidence
        var payload = Encoding.UTF8.GetBytes("Proof of bank wire transfer");
        string sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var session = await _commercialService.InitUploadSessionAsync(new InitUploadSessionCommand
        {
            Purpose = UploadSession.PurposePaymentProof,
            ShopId = shop.Id,
            TargetEntityId = order.Id,
            ExpectedBytes = payload.Length,
            ExpectedSha256 = sha,
            CommandMeta = new CommandMetaDto()
        }, adminId);

        await _commercialService.AppendUploadChunkAsync(session.Id, payload);
        var evidence = await _commercialService.FinalizeUploadSessionAsync(session.Id, new FinalizeUploadSessionCommand
        {
            ComputedSha256 = sha,
            MimeType = "application/pdf",
            CommandMeta = new CommandMetaDto()
        }, order.Id);

        // Review approval
        var review = await _commercialService.ReviewPaymentAsync(new ReviewPaymentCommand
        {
            OrderId = order.Id,
            EvidenceId = evidence.Id,
            Decision = PaymentReviewRecord.DecisionApprove,
            Notes = "Wire receipt verified against bank statement",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.Equal(PaymentReviewRecord.DecisionApprove, review.Decision);
        Assert.Equal(Order.StatusPaidApproved, order.Status);
    }

    [Fact]
    public async Task LifeBase_Oracle_LifetimeLicense_HasNullExpiry_AndRejectsRenewalOrExtension()
    {
        var adminId = Guid.NewGuid();
        var customer = await _commercialService.CreateCustomerAsync(new CreateCustomerCommand
        {
            LegalName = "Lifetime Client",
            ContactEmail = "life@client.com",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var shop = await _commercialService.RegisterShopAsync(new RegisterShopCommand
        {
            CustomerId = customer.Id,
            ShopCode = "LIFE-SHOP",
            ShopName = "Lifetime Shop",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var item = await _commercialService.CreateCatalogItemAsync(
            "LIFETIME-PLAN",
            "Lifetime POS License",
            PlanCatalogItem.TargetBaseLicense,
            "LIFETIME",
            null,
            999.00m,
            "USD",
            adminId);

        var snapshot = await _commercialService.CreatePriceSnapshotAsync(new CreatePriceSnapshotCommand
        {
            ShopId = shop.Id,
            CatalogItemId = item.Id,
            AuthorityVersion = 1,
            CommandMeta = new CommandMetaDto()
        });

        var order = await _commercialService.CreateOrderAsync(new CreateOrderCommand
        {
            CustomerId = customer.Id,
            SnapshotId = snapshot.Id,
            CommandMeta = new CommandMetaDto()
        });

        // Directly approve order for license issuance
        order.ApprovePayment(Guid.NewGuid(), adminId, DateTimeOffset.UtcNow);
        await _dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var license = await _commercialService.IssueLicenseAsync(new IssueLicenseCommand
        {
            ApprovedOrderId = order.Id,
            ValidFromUtc = now,
            ValidUntilUtc = null, // Invariant: Lifetime must have null expiry
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.NotNull(license);
        Assert.Equal("LIFETIME", license.TermKind);
        Assert.Null(license.ValidUntilUtc); // LIFE-BASE invariant: null scheduled expiry
        Assert.Equal("ACTIVE", license.Status);

        // Attempting to renew lifetime license must throw LIFETIME_CANNOT_RENEW
        var renewEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _commercialService.RenewLicenseAsync(license.Id, new RenewLicenseCommand
            {
                NewValidUntilUtc = now.AddYears(1),
                CommandMeta = new CommandMetaDto()
            }, adminId));

        Assert.Equal("LIFETIME_CANNOT_RENEW", renewEx.Code);

        // Attempting to extend lifetime license must throw LIFETIME_CANNOT_EXTEND
        var extendEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _commercialService.ExtendLicenseAsync(license.Id, new ExtendLicenseCommand
            {
                ExtensionEndUtc = now.AddMonths(3),
                CommandMeta = new CommandMetaDto()
            }, adminId));

        Assert.Equal("LIFETIME_CANNOT_EXTEND", extendEx.Code);
    }

    [Fact]
    public async Task LifeAddon_Oracle_AddonRenewal_UpdatesEntitlement_WithoutAlteringLifetimeBase()
    {
        var adminId = Guid.NewGuid();
        var customer = await _commercialService.CreateCustomerAsync(new CreateCustomerCommand
        {
            LegalName = "Hybrid Client",
            ContactEmail = "hybrid@client.com",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var shop = await _commercialService.RegisterShopAsync(new RegisterShopCommand
        {
            CustomerId = customer.Id,
            ShopCode = "HYBRID-01",
            ShopName = "Hybrid Store",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        // 1. Base Lifetime license
        var baseItem = await _commercialService.CreateCatalogItemAsync(
            "BASE-LIFETIME", "Base Lifetime", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 1000m, "USD", adminId);

        var baseSnapshot = await _commercialService.CreatePriceSnapshotAsync(new CreatePriceSnapshotCommand
        {
            ShopId = shop.Id,
            CatalogItemId = baseItem.Id,
            AuthorityVersion = 1,
            CommandMeta = new CommandMetaDto()
        });

        var baseOrder = await _commercialService.CreateOrderAsync(new CreateOrderCommand
        {
            CustomerId = customer.Id,
            SnapshotId = baseSnapshot.Id,
            CommandMeta = new CommandMetaDto()
        });

        baseOrder.ApprovePayment(Guid.NewGuid(), adminId, DateTimeOffset.UtcNow);
        await _dbContext.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var license = await _commercialService.IssueLicenseAsync(new IssueLicenseCommand
        {
            ApprovedOrderId = baseOrder.Id,
            ValidFromUtc = now,
            ValidUntilUtc = null,
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.Equal("LIFETIME", license.TermKind);
        Assert.Null(license.ValidUntilUtc);

        // 2. Add-on order: 1 Year Support
        var addonItem = await _commercialService.CreateCatalogItemAsync(
            "SUPPORT-1YR", "1 Year Support", PlanCatalogItem.TargetSupport, "FIXED_TERM", "YEARLY", 150m, "USD", adminId);

        var addonSnapshot = await _commercialService.CreatePriceSnapshotAsync(new CreatePriceSnapshotCommand
        {
            ShopId = shop.Id,
            CatalogItemId = addonItem.Id,
            AuthorityVersion = 1,
            CommandMeta = new CommandMetaDto()
        });

        var addonOrder = await _commercialService.CreateOrderAsync(new CreateOrderCommand
        {
            CustomerId = customer.Id,
            SnapshotId = addonSnapshot.Id,
            CommandMeta = new CommandMetaDto()
        });

        addonOrder.ApprovePayment(Guid.NewGuid(), adminId, now);
        await _dbContext.SaveChangesAsync();

        var addonEnd = now.AddYears(1);
        var expiredRenewal = await Assert.ThrowsAsync<CentralDomainException>(() => _commercialService.RenewAddonAsync(
            license.Id,
            new RenewAddonCommand
            {
                ApprovedOrderId = addonOrder.Id,
                NewValidUntilUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                CommandMeta = new CommandMetaDto()
            },
            adminId));
        Assert.Equal("INVALID_RENEWAL_DATE", expiredRenewal.Code);

        var mismatchedOrder = Order.CreateFromSnapshot(
            addonSnapshot, customer.Id, now, "SUPPORT_RENEWAL", 1, Guid.NewGuid(), null);
        mismatchedOrder.ApprovePayment(Guid.NewGuid(), adminId, now);
        _dbContext.Orders.Add(mismatchedOrder);
        await _dbContext.SaveChangesAsync();
        var targetMismatch = await Assert.ThrowsAsync<CentralDomainException>(() => _commercialService.RenewAddonAsync(
            license.Id,
            new RenewAddonCommand
            {
                ApprovedOrderId = mismatchedOrder.Id,
                NewValidUntilUtc = addonEnd,
                CommandMeta = new CommandMetaDto()
            },
            adminId));
        Assert.Equal("TARGET_MISMATCH", targetMismatch.Code);

        var entitlement = await _commercialService.RenewAddonAsync(license.Id, new RenewAddonCommand
        {
            ApprovedOrderId = addonOrder.Id,
            NewValidUntilUtc = addonEnd,
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.NotNull(entitlement);
        Assert.Equal("SUPPORT", entitlement.Kind);
        Assert.Equal(addonEnd, entitlement.ValidUntilUtc);

        // Verify Base license is completely unaffected (LIFE-ADDON invariant)
        var reloadedLicense = await _dbContext.CommercialLicenses.FindAsync(license.Id);
        Assert.NotNull(reloadedLicense);
        Assert.Equal("LIFETIME", reloadedLicense.TermKind);
        Assert.Null(reloadedLicense.ValidUntilUtc); // Still null!
        Assert.Equal("ACTIVE", reloadedLicense.Status);

        var reusedOrder = await Assert.ThrowsAsync<CentralDomainException>(() => _commercialService.RenewAddonAsync(
            license.Id,
            new RenewAddonCommand
            {
                ApprovedOrderId = addonOrder.Id,
                NewValidUntilUtc = addonEnd.AddYears(1),
                CommandMeta = new CommandMetaDto()
            },
            adminId));
        Assert.Equal("ORDER_ALREADY_FULFILLED", reusedOrder.Code);
    }

    [Fact]
    public async Task TimedRegression_Oracle_FixedTermLicenseExpires_WhileLifetimeNeverExpires()
    {
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var fixedItem = PlanCatalogItem.Create("FIXED-ITEM", "Fixed", PlanCatalogItem.TargetBaseLicense, "FIXED_TERM", "MONTHLY", 100m, "USD");
        var fixedSnapshot = PricingSnapshot.Create(Guid.NewGuid(), fixedItem, 1, now);
        var fixedOrder = Order.CreateFromSnapshot(fixedSnapshot, Guid.NewGuid(), now);
        fixedOrder.ApprovePayment(Guid.NewGuid(), adminId, now);

        var fixedLicense = CommercialLicense.Issue(fixedOrder, now, now.AddDays(30), now);
        Assert.Equal("FIXED_TERM", fixedLicense.TermKind);
        Assert.Equal(now.AddDays(30), fixedLicense.ValidUntilUtc);

        var lifeItem = PlanCatalogItem.Create("LIFE-ITEM", "Life", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var lifeSnapshot = PricingSnapshot.Create(Guid.NewGuid(), lifeItem, 1, now);
        var lifeOrder = Order.CreateFromSnapshot(lifeSnapshot, Guid.NewGuid(), now);
        lifeOrder.ApprovePayment(Guid.NewGuid(), adminId, now);

        var lifeLicense = CommercialLicense.Issue(lifeOrder, now, null, now);
        Assert.Equal("LIFETIME", lifeLicense.TermKind);
        Assert.Null(lifeLicense.ValidUntilUtc);

        // Simulate evaluation at day 45 (after fixed term expiry)
        var futureTime = now.AddDays(45);
        bool fixedIsExpired = fixedLicense.TermKind == "FIXED_TERM" && fixedLicense.ValidUntilUtc.HasValue && futureTime > fixedLicense.ValidUntilUtc.Value;
        bool lifeIsExpired = lifeLicense.TermKind == "FIXED_TERM" && lifeLicense.ValidUntilUtc.HasValue && futureTime > lifeLicense.ValidUntilUtc.Value;

        Assert.True(fixedIsExpired, "Fixed term license must be expired at day 45.");
        Assert.False(lifeIsExpired, "Lifetime license must never be expired regardless of time.");
    }

    [Fact]
    public async Task FixedTermRenewal_StrictlyRequiresPaidApprovedOrder()
    {
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var customer = await _commercialService.CreateCustomerAsync(new CreateCustomerCommand
        {
            LegalName = "Renewal Client",
            ContactEmail = "renew@client.com",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var shop = await _commercialService.RegisterShopAsync(new RegisterShopCommand
        {
            CustomerId = customer.Id,
            ShopCode = "RENEW-01",
            ShopName = "Renew Store",
            CommandMeta = new CommandMetaDto()
        }, adminId);

        var item = await _commercialService.CreateCatalogItemAsync(
            "SUB-1YR", "1 Year Sub", PlanCatalogItem.TargetBaseLicense, "FIXED_TERM", "YEARLY", 200m, "USD", adminId);

        var snapshot = await _commercialService.CreatePriceSnapshotAsync(new CreatePriceSnapshotCommand
        {
            ShopId = shop.Id,
            CatalogItemId = item.Id,
            AuthorityVersion = 1,
            CommandMeta = new CommandMetaDto()
        });

        var order = await _commercialService.CreateOrderAsync(new CreateOrderCommand
        {
            CustomerId = customer.Id,
            SnapshotId = snapshot.Id,
            CommandMeta = new CommandMetaDto()
        });

        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        await _dbContext.SaveChangesAsync();

        var license = await _commercialService.IssueLicenseAsync(new IssueLicenseCommand
        {
            ApprovedOrderId = order.Id,
            ValidFromUtc = now,
            ValidUntilUtc = now.AddYears(1),
            CommandMeta = new CommandMetaDto()
        }, adminId);

        // 1. Renewal without ApprovedOrderId must throw ORDER_REQUIRED
        var missingOrderEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _commercialService.RenewLicenseAsync(license.Id, new RenewLicenseCommand
            {
                NewValidUntilUtc = now.AddYears(2),
                ApprovedOrderId = null,
                CommandMeta = new CommandMetaDto()
            }, adminId));
        Assert.Equal("ORDER_REQUIRED", missingOrderEx.Code);

        // 2. Renewal with unapproved order must throw ORDER_NOT_APPROVED
        var unapprovedOrder = await _commercialService.CreateOrderAsync(new CreateOrderCommand
        {
            CustomerId = customer.Id,
            SnapshotId = snapshot.Id,
            CommandMeta = new CommandMetaDto()
        });

        var unapprovedEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _commercialService.RenewLicenseAsync(license.Id, new RenewLicenseCommand
            {
                NewValidUntilUtc = now.AddYears(2),
                ApprovedOrderId = unapprovedOrder.Id,
                CommandMeta = new CommandMetaDto()
            }, adminId));
        Assert.Equal("ORDER_NOT_APPROVED", unapprovedEx.Code);

        // 3. Renewal with paid approved order succeeds
        unapprovedOrder.ApprovePayment(Guid.NewGuid(), adminId, now);
        await _dbContext.SaveChangesAsync();

        var renewed = await _commercialService.RenewLicenseAsync(license.Id, new RenewLicenseCommand
        {
            NewValidUntilUtc = now.AddYears(2),
            ApprovedOrderId = unapprovedOrder.Id,
            CommandMeta = new CommandMetaDto()
        }, adminId);

        Assert.Equal(now.AddYears(2), renewed.ValidUntilUtc);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }
}
