using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Identity;
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
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CentralDbContext(options);
        _auditService = new CentralAuditService(_dbContext);
        _signerClient = new TestProtectedSignerClient();
        _stepUpService = new StepUpService(
            _dbContext,
            new TotpService(),
            AdminAuthenticationPolicy.FromValues("300", "900", "28800", "300"));
        _lifecycleService = new SensitiveLifecycleService(
            _dbContext, _signerClient, _stepUpService, _auditService,
            approvalPolicy: new SensitiveApprovalPolicy { RequestLifetime = TimeSpan.FromHours(1) });
    }

    [Fact]
    public async Task StepUp_MissingLifetimeConfiguration_DeniesGrantAndValidation()
    {
        var unconfigured = new StepUpService(
            _dbContext,
            new TotpService(),
            AdminAuthenticationPolicy.FromValues("300", "900", "28800", null));

        var exception = await Assert.ThrowsAsync<CentralDomainException>(() =>
            unconfigured.VerifyAndGrantStepUpAsync(new StepUpRequestDto()));

        Assert.Equal("AUTH_POLICY_NOT_CONFIGURED", exception.Code);
        Assert.False(await unconfigured.ValidateStepUpGrantAsync(Guid.NewGuid(), "license.revoke", new string('a', 64)));
        Assert.Empty(await _dbContext.StepUpGrants.ToListAsync());
    }

    private async Task<(SensitiveApprovalRequest Request, Guid RequesterAdminId)> RequestApprovalWithStepUpAsync(
        string operationType, Guid targetEntityId, string digest, string actionScope)
    {
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create($"approval-{Guid.NewGuid():N}", "hash", "salt", "synthetic", true, now);
        var session = AdminSession.Create(user.Id, "token-hash", now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true,
            user.SecurityRevision, new[] { "sensitive.approve" });
        var grant = StepUpGrant.Create(session.Id, actionScope, digest, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        _dbContext.AdminUsers.Add(user);
        _dbContext.AdminSessions.Add(session);
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();
        var request = await _lifecycleService.RequestApprovalAsync(
            operationType, targetEntityId, digest, actionScope, user.Id, session.Id, grant.Id);
        return (request, user.Id);
    }

    [Fact]
    public async Task RequestApproval_RequiresConfiguredLifetimeBeforeAnyPersistence()
    {
        var service = new SensitiveLifecycleService(
            _dbContext, _signerClient, _stepUpService, _auditService,
            approvalPolicy: new SensitiveApprovalPolicy());

        var error = await Assert.ThrowsAsync<CentralDomainException>(() => service.RequestApprovalAsync(
            "license.revoke", Guid.NewGuid(), new string('a', 64), "license.revoke",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal("APPROVAL_POLICY_NOT_CONFIGURED", error.Code);
        Assert.Empty(await _dbContext.SensitiveApprovalRequests.ToListAsync());
        Assert.Empty(await _dbContext.CentralOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task RequestApproval_RejectsWrongScopeStepUpWithoutPersistingApproval()
    {
        var now = DateTimeOffset.UtcNow;
        var requester = AdminUser.Create("scope-check", "hash", "salt", "synthetic", true, now);
        var session = AdminSession.Create(requester.Id, "scope-token", now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true,
            requester.SecurityRevision, new[] { "sensitive.approve" });
        var digest = new string('a', 64);
        var grant = StepUpGrant.Create(session.Id, "license.pause", digest, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        _dbContext.AdminUsers.Add(requester);
        _dbContext.AdminSessions.Add(session);
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<CentralDomainException>(() => _lifecycleService.RequestApprovalAsync(
            "license.revoke", Guid.NewGuid(), digest, "license.revoke", requester.Id, session.Id, grant.Id));

        Assert.Equal("STEP_UP_REQUIRED", error.Code);
        Assert.Empty(await _dbContext.SensitiveApprovalRequests.ToListAsync());
        Assert.Empty(await _dbContext.CentralOutboxMessages.ToListAsync());
        Assert.False((await _dbContext.StepUpGrants.FindAsync(grant.Id))!.IsConsumed);
    }

    [Fact]
    public async Task RequestApproval_RejectsSessionOwnedByDifferentRequester()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionOwner = AdminUser.Create("session-owner", "hash", "salt", "synthetic", true, now);
        var session = AdminSession.Create(sessionOwner.Id, "owner-token", now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true,
            sessionOwner.SecurityRevision, new[] { "sensitive.approve" });
        var digest = new string('a', 64);
        var grant = StepUpGrant.Create(session.Id, "license.revoke", digest, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        _dbContext.AdminUsers.Add(sessionOwner);
        _dbContext.AdminSessions.Add(session);
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<CentralDomainException>(() => _lifecycleService.RequestApprovalAsync(
            "license.revoke", Guid.NewGuid(), digest, "license.revoke", Guid.NewGuid(), session.Id, grant.Id));

        Assert.Equal("STEP_UP_REQUIRED", error.Code);
        Assert.Empty(await _dbContext.SensitiveApprovalRequests.ToListAsync());
        Assert.False((await _dbContext.StepUpGrants.FindAsync(grant.Id))!.IsConsumed);
    }

    [Fact]
    public async Task RequestApproval_RejectsExpiredStepUpGrantWithoutPersistence()
    {
        var now = DateTimeOffset.UtcNow;
        var requester = AdminUser.Create("expired-stepup", "hash", "salt", "synthetic", true, now.AddHours(-1));
        var session = AdminSession.Create(requester.Id, "expired-stepup-token", now.AddMinutes(-1), TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true,
            requester.SecurityRevision, new[] { "sensitive.approve" });
        var digest = new string('a', 64);
        var grant = StepUpGrant.Create(session.Id, "license.revoke", digest, Guid.NewGuid(), now.AddMinutes(-10), TimeSpan.FromMinutes(5));
        _dbContext.AdminUsers.Add(requester);
        _dbContext.AdminSessions.Add(session);
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<CentralDomainException>(() => _lifecycleService.RequestApprovalAsync(
            "license.revoke", Guid.NewGuid(), digest, "license.revoke", requester.Id, session.Id, grant.Id));

        Assert.Equal("STEP_UP_REQUIRED", error.Code);
        Assert.Empty(await _dbContext.SensitiveApprovalRequests.ToListAsync());
        Assert.Empty(await _dbContext.CentralOutboxMessages.ToListAsync());
        Assert.False((await _dbContext.StepUpGrants.FindAsync(grant.Id))!.IsConsumed);
    }

    [Fact]
    public async Task RequestApproval_RetryWithConsumedGrantReturnsSamePersistedRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var requester = AdminUser.Create($"approval-retry-{Guid.NewGuid():N}", "hash", "salt", "synthetic", true, now);
        var session = AdminSession.Create(requester.Id, "approval-retry-session", now,
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, requester.SecurityRevision, new[] { "sensitive.approve" });
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("canonical revoke payload")));
        var grant = StepUpGrant.Create(session.Id, "license.revoke", digest, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        var targetId = Guid.NewGuid();
        _dbContext.AdminUsers.Add(requester);
        _dbContext.AdminSessions.Add(session);
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        var first = await _lifecycleService.RequestApprovalAsync(
            "LICENSE_REVOKE", targetId, digest, "license.revoke", requester.Id, session.Id, grant.Id);
        var retry = await _lifecycleService.RequestApprovalAsync(
            "LICENSE_REVOKE", targetId, digest, "license.revoke", requester.Id, session.Id, grant.Id);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(SensitiveApprovalRequest.StatusPending, retry.Status);
        Assert.Single(await _dbContext.SensitiveApprovalRequests.Where(x => x.Id == first.Id).ToListAsync());
        Assert.Single(await _dbContext.CentralOperationRecords.Where(x => x.ClientOperationId == grant.Id).ToListAsync());
        Assert.Single(await _dbContext.CentralOutboxMessages.Where(x => x.Id == first.Id).ToListAsync());
        Assert.Single(await _dbContext.CentralAuditRecords.Where(x => x.Action == "SENSITIVE_APPROVAL_REQUESTED" && x.ResourceId == first.Id.ToString()).ToListAsync());
        Assert.True((await _dbContext.StepUpGrants.FindAsync(grant.Id))!.IsConsumed);
    }

    [Fact]
    public async Task RequestApproval_ConsumedGrantCannotBeReplayedWithDifferentPayload()
    {
        var now = DateTimeOffset.UtcNow;
        var requester = AdminUser.Create($"approval-conflict-{Guid.NewGuid():N}", "hash", "salt", "synthetic", true, now);
        var session = AdminSession.Create(requester.Id, "approval-conflict-session", now,
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, requester.SecurityRevision, new[] { "sensitive.approve" });
        var firstDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("original canonical payload")));
        var changedDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("changed canonical payload")));
        var grant = StepUpGrant.Create(session.Id, "license.revoke", firstDigest, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        var targetId = Guid.NewGuid();
        _dbContext.AdminUsers.Add(requester);
        _dbContext.AdminSessions.Add(session);
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        var first = await _lifecycleService.RequestApprovalAsync(
            "LICENSE_REVOKE", targetId, firstDigest, "license.revoke", requester.Id, session.Id, grant.Id);
        var error = await Assert.ThrowsAsync<CentralDomainException>(() => _lifecycleService.RequestApprovalAsync(
            "LICENSE_REVOKE", Guid.NewGuid(), changedDigest, "license.revoke", requester.Id, session.Id, grant.Id));

        Assert.Equal("OPERATION_ID_CONFLICT", error.Code);
        Assert.Single(await _dbContext.SensitiveApprovalRequests.Where(x => x.Id == first.Id).ToListAsync());
        Assert.Single(await _dbContext.CentralOperationRecords.Where(x => x.ClientOperationId == grant.Id).ToListAsync());
        Assert.True((await _dbContext.StepUpGrants.FindAsync(grant.Id))!.IsConsumed);
    }

    [Fact]
    public async Task LifeSecurity_Oracle_DualControl_StrictlyProhibitsSelfApproval()
    {
        var targetEntityId = Guid.NewGuid();
        string targetDigest = new string('a', 64);

        var (request, requesterAdminId) = await RequestApprovalWithStepUpAsync(
            "license.revoke",
            targetEntityId,
            targetDigest,
            "license.revoke");

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
        var targetEntityId = Guid.NewGuid();
        string initialDigest = new string('a', 64);

        var (request, _) = await RequestApprovalWithStepUpAsync(
            "primary.transfer",
            targetEntityId,
            initialDigest,
            "primary.transfer");

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
    public async Task CanonicalLicenseLifecycle_RequiresAndConsumesFreshExactStepUpAtExecution()
    {
        var now = DateTimeOffset.UtcNow;
        var requester = AdminUser.Create($"canonical-lifecycle-{Guid.NewGuid():N}", "hash", "salt", "synthetic", true, now);
        var session = AdminSession.Create(requester.Id, "canonical-lifecycle-session", now,
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, requester.SecurityRevision, new[] { "license.pause" });
        var approver = AdminUser.Create($"canonical-approver-{Guid.NewGuid():N}", "hash", "salt", "synthetic", true, now);
        var catalogItem = PlanCatalogItem.Create("PLAN-CANONICAL-LIFECYCLE", "Canonical lifecycle", PlanCatalogItem.TargetBaseLicense, "LIFETIME", null, 500m, "USD");
        var snapshot = PricingSnapshot.Create(Guid.NewGuid(), catalogItem, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, Guid.NewGuid(), now);
        order.ApprovePayment(Guid.NewGuid(), approver.Id, now);
        var license = CommercialLicense.Issue(order, now, null, now);
        var capabilityPolicyId = Guid.NewGuid();
        var policy = ShopEnforcementPolicy.Create(license.ShopId, license.Id, 1, ShopEnforcementPolicy.FullStop,
            null, Guid.NewGuid(), 86400, 86400, 300, capabilityPolicyId, "{}", "synthetic-signature", "synthetic-key", approver.Id, now);
        using var payloadDocument = JsonDocument.Parse($"{{\"licenseId\":\"{license.Id:D}\",\"effectiveAtUtc\":\"{now:O}\",\"capabilityPolicyId\":\"{capabilityPolicyId:D}\"}}");
        var payload = payloadDocument.RootElement.Clone();
        var serializer = new CanonicalJsonSerializer();
        var (_, payloadHash) = serializer.SerializeAndHash(payload);
        var approval = SensitiveApprovalRequest.Request("LICENSE_PAUSE", license.Id, payloadHash, requester.Id,
            "license.pause", session.Id, now, now.AddHours(1));
        approval.Approve(approver.Id, payloadHash, now);

        _dbContext.AdminUsers.AddRange(requester, approver);
        _dbContext.AdminSessions.Add(session);
        _dbContext.CommercialLicenses.Add(license);
        _dbContext.ShopEnforcementPolicies.Add(policy);
        _dbContext.SensitiveApprovalRequests.Add(approval);
        await _dbContext.SaveChangesAsync();

        CanonicalDirectoryMeta BuildMeta() => new()
        {
            ClientOperationId = Guid.NewGuid(),
            OperationType = "license.pause",
            FingerprintVersion = 1,
            PayloadSha256 = payloadHash,
            ExpectedVersion = 1,
            PolicyVersion = 1,
            Reason = "Synthetic canonical lifecycle test",
            SensitiveApprovalId = approval.Id
        };

        var missingGrant = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.ExecuteCanonicalLicenseLifecycleAsync("license.pause", license.Id, capabilityPolicyId,
                now, payload, BuildMeta(), requester.Id, session.Id));
        Assert.Equal("STEP_UP_REQUIRED", missingGrant.Code);
        Assert.Equal(CommercialLicense.StatusActive, (await _dbContext.CommercialLicenses.FindAsync(license.Id))!.Status);
        Assert.Equal(SensitiveApprovalRequest.StatusApproved, (await _dbContext.SensitiveApprovalRequests.FindAsync(approval.Id))!.Status);

        var wrongActionGrant = StepUpGrant.Create(session.Id, "license.resume", payloadHash, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        var wrongDigest = new string('f', 64);
        var wrongDigestGrant = StepUpGrant.Create(session.Id, "license.pause", wrongDigest, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        _dbContext.StepUpGrants.AddRange(wrongActionGrant, wrongDigestGrant);
        await _dbContext.SaveChangesAsync();

        var mismatchedGrant = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _lifecycleService.ExecuteCanonicalLicenseLifecycleAsync("license.pause", license.Id, capabilityPolicyId,
                now, payload, BuildMeta(), requester.Id, session.Id));
        Assert.Equal("STEP_UP_REQUIRED", mismatchedGrant.Code);
        Assert.False((await _dbContext.StepUpGrants.FindAsync(wrongActionGrant.Id))!.IsConsumed);
        Assert.False((await _dbContext.StepUpGrants.FindAsync(wrongDigestGrant.Id))!.IsConsumed);
        Assert.Equal(CommercialLicense.StatusActive, (await _dbContext.CommercialLicenses.FindAsync(license.Id))!.Status);
        Assert.Equal(SensitiveApprovalRequest.StatusApproved, (await _dbContext.SensitiveApprovalRequests.FindAsync(approval.Id))!.Status);

        var grant = StepUpGrant.Create(session.Id, "license.pause", payloadHash, Guid.NewGuid(), now, TimeSpan.FromMinutes(5));
        _dbContext.StepUpGrants.Add(grant);
        await _dbContext.SaveChangesAsync();

        var outcome = await _lifecycleService.ExecuteCanonicalLicenseLifecycleAsync("license.pause", license.Id,
            capabilityPolicyId, now, payload, BuildMeta(), requester.Id, session.Id);

        Assert.Equal("SUCCEEDED", outcome.State);
        Assert.Equal(CommercialLicense.StatusPaused, (await _dbContext.CommercialLicenses.FindAsync(license.Id))!.Status);
        Assert.True((await _dbContext.StepUpGrants.FindAsync(grant.Id))!.IsConsumed);
        Assert.Equal(SensitiveApprovalRequest.StatusConsumed, (await _dbContext.SensitiveApprovalRequests.FindAsync(approval.Id))!.Status);
        Assert.Single(await _dbContext.CentralOutboxMessages.Where(x => x.AggregateId == license.Id.ToString("D")).ToListAsync());
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
            var (req, requesterAdminId) = await RequestApprovalWithStepUpAsync(
                opType,
                license.Id,
                new string('0', 64),
                opType);
            adminId = requesterAdminId;
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
