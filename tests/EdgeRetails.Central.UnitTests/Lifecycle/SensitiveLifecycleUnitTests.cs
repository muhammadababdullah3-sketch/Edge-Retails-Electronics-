using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.UnitTests.Activation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.UnitTests.Lifecycle;

public class SensitiveLifecycleUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly CentralAuditService _auditService;
    private readonly TestProtectedSignerClient _signerClient;
    private readonly StepUpService _stepUpService;
    private readonly SensitiveLifecycleService _lifecycleService;

    public SensitiveLifecycleUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new CentralDbContext(options);
        _auditService = new CentralAuditService(_dbContext);
        _signerClient = new TestProtectedSignerClient();
        _stepUpService = new StepUpService(_dbContext, new TotpService());
        _lifecycleService = new SensitiveLifecycleService(_dbContext, _signerClient, _stepUpService, _auditService);
    }

    [Fact]
    public async Task LifeSecurity_Oracle_DualControl_StrictlyProhibitsSelfApproval()
    {
        var requesterAdminId = Guid.NewGuid();
        var targetEntityId = Guid.NewGuid();
        var stepUpSessionId = Guid.NewGuid();
        string targetDigest = new string('a', 64);

        var request = await _lifecycleService.RequestApprovalAsync(
            "license.revoke",
            targetEntityId,
            targetDigest,
            "license.revoke",
            requesterAdminId,
            stepUpSessionId,
            TimeSpan.FromHours(2));

        Assert.NotNull(request);
        Assert.Equal(SensitiveApprovalRequest.StatusPending, request.Status);

        // Self-approval attempt: approver == requester
        var selfApprovalEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.DecideApprovalAsync(request.Id, new DecideSensitiveApprovalCommand
            {
                Decision = "APPROVE",
                TargetPayloadSha256 = targetDigest,
                CommandMeta = new CommandMetaDto()
            }, requesterAdminId)); // Same admin ID!

        Assert.Equal("SELF_APPROVAL_PROHIBITED", selfApprovalEx.Code);

        // Independent distinct reviewer approval succeeds
        var distinctApproverAdminId = Guid.NewGuid();
        await _lifecycleService.DecideApprovalAsync(request.Id, new DecideSensitiveApprovalCommand
        {
            Decision = "APPROVE",
            TargetPayloadSha256 = targetDigest,
            CommandMeta = new CommandMetaDto()
        }, distinctApproverAdminId);

        var reloaded = await _dbContext.SensitiveApprovalRequests.FindAsync(request.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(SensitiveApprovalRequest.StatusApproved, reloaded.Status);
        Assert.Equal(distinctApproverAdminId, reloaded.ApproverAdminId);
    }

    [Fact]
    public async Task LifeSecurity_RejectsPayloadDigestMismatchOnApproval()
    {
        var requesterAdminId = Guid.NewGuid();
        var targetEntityId = Guid.NewGuid();
        var stepUpSessionId = Guid.NewGuid();
        string initialDigest = new string('a', 64);

        var request = await _lifecycleService.RequestApprovalAsync(
            "primary.transfer",
            targetEntityId,
            initialDigest,
            "primary.transfer",
            requesterAdminId,
            stepUpSessionId,
            TimeSpan.FromHours(2));

        var distinctApproverAdminId = Guid.NewGuid();
        string tamperedDigest = new string('b', 64);

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.DecideApprovalAsync(request.Id, new DecideSensitiveApprovalCommand
            {
                Decision = "APPROVE",
                TargetPayloadSha256 = tamperedDigest, // Mismatched!
                CommandMeta = new CommandMetaDto()
            }, distinctApproverAdminId));

        Assert.Equal("PAYLOAD_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task LicenseLifecycle_AllowlistTransitions_Pause_Resume_Suspend_Reinstate_Revoke()
    {
        var adminId = Guid.NewGuid();
        var approverAdminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var item = PlanCatalogItem.Create("PLAN-LIFE", "Plan Life", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(Guid.NewGuid(), item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);

        _dbContext.CommercialLicenses.Add(license);
        await _dbContext.SaveChangesAsync();

        Assert.Equal("ACTIVE", license.Status);

        // Negative: Unapproved Pause must throw APPROVAL_REQUIRED
        var missingApprEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.PauseLicenseAsync(license.Id, new LicenseLifecycleCommand { CommandMeta = new CommandMetaDto() }, adminId));
        Assert.Equal("APPROVAL_REQUIRED", missingApprEx.Code);

        // Helper to create and approve a sensitive approval
        async Task<SensitiveApprovalRequest> CreateAndApprove(string opType)
        {
            var req = await _lifecycleService.RequestApprovalAsync(
                opType,
                license.Id,
                new string('0', 64),
                opType,
                adminId,
                Guid.NewGuid(),
                TimeSpan.FromHours(2));
            await _lifecycleService.DecideApprovalAsync(req.Id, new DecideSensitiveApprovalCommand
            {
                Decision = "APPROVE",
                TargetPayloadSha256 = new string('0', 64),
                CommandMeta = new CommandMetaDto()
            }, approverAdminId);
            return req;
        }

        // Negative: Operation type mismatch must throw OPERATION_TYPE_MISMATCH
        var wrongReq = await CreateAndApprove("LICENSE_REVOKE");
        var mismatchEx = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.PauseLicenseAsync(license.Id, new LicenseLifecycleCommand
            {
                SensitiveApprovalId = wrongReq.Id,
                CommandMeta = new CommandMetaDto()
            }, adminId));
        Assert.Equal("OPERATION_TYPE_MISMATCH", mismatchEx.Code);

        // 1. Pause
        var pauseReq = await CreateAndApprove("LICENSE_PAUSE");
        await _lifecycleService.PauseLicenseAsync(license.Id, new LicenseLifecycleCommand
        {
            SensitiveApprovalId = pauseReq.Id,
            CommandMeta = new CommandMetaDto()
        }, adminId);
        var l1 = await _dbContext.CommercialLicenses.FindAsync(license.Id);
        Assert.Equal("PAUSED", l1!.Status);

        // 2. Resume
        var resumeReq = await CreateAndApprove("LICENSE_RESUME");
        await _lifecycleService.ResumeLicenseAsync(license.Id, new LicenseLifecycleCommand
        {
            SensitiveApprovalId = resumeReq.Id,
            CommandMeta = new CommandMetaDto()
        }, adminId);
        var l2 = await _dbContext.CommercialLicenses.FindAsync(license.Id);
        Assert.Equal("ACTIVE", l2!.Status);

        // 3. Suspend
        var suspendReq = await CreateAndApprove("LICENSE_SUSPEND");
        await _lifecycleService.SuspendLicenseAsync(license.Id, new LicenseLifecycleCommand
        {
            SensitiveApprovalId = suspendReq.Id,
            CommandMeta = new CommandMetaDto()
        }, adminId);
        var l3 = await _dbContext.CommercialLicenses.FindAsync(license.Id);
        Assert.Equal("SUSPENDED", l3!.Status);

        // 4. Reinstate
        var reinstateReq = await CreateAndApprove("LICENSE_REINSTATE");
        await _lifecycleService.ReinstateLicenseAsync(license.Id, new LicenseLifecycleCommand
        {
            SensitiveApprovalId = reinstateReq.Id,
            CommandMeta = new CommandMetaDto()
        }, adminId);
        var l4 = await _dbContext.CommercialLicenses.FindAsync(license.Id);
        Assert.Equal("ACTIVE", l4!.Status);

        // 5. Revoke
        var revokeReq = await CreateAndApprove("LICENSE_REVOKE");
        await _lifecycleService.RevokeLicenseAsync(license.Id, new LicenseLifecycleCommand
        {
            SensitiveApprovalId = revokeReq.Id,
            CommandMeta = new CommandMetaDto()
        }, adminId);
        var l5 = await _dbContext.CommercialLicenses.FindAsync(license.Id);
        Assert.Equal("REVOKED", l5!.Status);

        // Attempting to resume a REVOKED license throws exception
        var resumeRevokedReq = await CreateAndApprove("LICENSE_RESUME");
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.ResumeLicenseAsync(license.Id, new LicenseLifecycleCommand
            {
                SensitiveApprovalId = resumeRevokedReq.Id,
                CommandMeta = new CommandMetaDto()
            }, adminId));
        Assert.Equal("INVALID_STATE_TRANSITION", ex.Code);
    }

    [Fact]
    public async Task ShopEnforcementPolicy_GuaranteesMinimum24HourOfflineContinuity()
    {
        var adminId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var item = PlanCatalogItem.Create("PLAN-POL", "Plan Pol", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(Guid.NewGuid(), item, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), adminId, now);
        var license = CommercialLicense.Issue(order, now, null, now);

        _dbContext.CommercialLicenses.Add(license);
        await _dbContext.SaveChangesAsync();

        // 1. Valid policy with 7 days offline grace (604800s >= 86400s)
        var policy = await _lifecycleService.ConfigurePolicyAsync(new ConfigurePolicyCommand
        {
            CommandMeta = new CommandMetaDto(),
            Policy = new PolicyDto
            {
                ShopId = license.ShopId,
                LicenseId = license.Id,
                PolicyVersion = 1,
                EnforcementMode = "FULL_STOP",
                LeaseDurationSeconds = 86400,
                OfflineGraceSeconds = 604800, // 7 days
                RollbackToleranceSeconds = 300,
                CapabilityPolicyId = Guid.NewGuid(),
                AfterExtensionPolicyId = Guid.NewGuid()
            }
        }, adminId);

        Assert.NotNull(policy);
        Assert.Equal(604800, policy.OfflineGraceSeconds);
        Assert.NotEmpty(policy.RS256Signature);
        Assert.NotEmpty(policy.SignedPolicyEnvelopeJson);

        // 2. Reject policy with insufficient offline grace (< 86400 seconds)
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.ConfigurePolicyAsync(new ConfigurePolicyCommand
            {
                CommandMeta = new CommandMetaDto(),
                Policy = new PolicyDto
                {
                    ShopId = license.ShopId,
                    LicenseId = license.Id,
                    PolicyVersion = 2,
                    EnforcementMode = "FULL_STOP",
                    LeaseDurationSeconds = 86400,
                    OfflineGraceSeconds = 3600, // 1 hour is less than required 24 hours (86400s)
                    RollbackToleranceSeconds = 300,
                    CapabilityPolicyId = Guid.NewGuid(),
                    AfterExtensionPolicyId = Guid.NewGuid()
                }
            }, adminId));

        Assert.Equal("MINIMUM_GRACE_REQUIRED", ex.Code);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _signerClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
