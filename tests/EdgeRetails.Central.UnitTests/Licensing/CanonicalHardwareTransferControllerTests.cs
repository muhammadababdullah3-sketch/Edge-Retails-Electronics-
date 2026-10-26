using System.Text.Json;
using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using EdgeRetails.Central.UnitTests.Activation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EdgeRetails.Central.UnitTests.Licensing;

public sealed class CanonicalHardwareTransferControllerTests
{
    private readonly CanonicalJsonSerializer _serializer = new();

    [Fact]
    public async Task Hardware_decision_persists_contract_state_evidence_outcome_audit_and_outbox()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var installation = AddInstallation(db, shopId, Guid.NewGuid());
        var review = HardwareReview.Create(shopId, installation.Id, "reported-hash", "stored-hash", "fingerprint changed", DateTimeOffset.UtcNow);
        var evidence = AddEvidence(db, shopId, installation.Id);
        db.HardwareReviews.Add(review);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            caseId = review.Id, installationId = installation.Id, previousFingerprintId = review.StoredMachineFingerprintId,
            currentFingerprintId = review.ReportedMachineFingerprintId, decision = "REQUEST_INFORMATION", verificationEvidenceId = evidence.Id
        });
        var envelope = Envelope("hardware.review", payload, review.RowVersion);
        var controller = CreateController(db, out var adminId);

        var result = await controller.DecideHardware(review.Id, envelope, CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        Assert.Equal(2, outcome.ResourceVersion);
        var replay = Assert.IsType<JsonResult>(await controller.DecideHardware(review.Id, envelope, CancellationToken.None)).Value as OutcomeDto;
        Assert.NotNull(replay);
        Assert.Equal(outcome.OutcomeId, replay.OutcomeId);
        Assert.Equal(outcome.ResourceVersion, replay.ResourceVersion);
        var saved = await db.HardwareReviews.SingleAsync(x => x.Id == review.Id);
        Assert.Equal("REQUEST_INFORMATION", saved.Status);
        Assert.Equal("REQUEST_INFORMATION", saved.ContractDecision);
        Assert.Equal(evidence.Id, saved.VerificationEvidenceId);
        Assert.Equal(adminId, saved.ReviewedByAdminId);
        Assert.Single(db.CentralAuditRecords.Where(x => x.Action == "HARDWARE_REVIEW_DECIDED"));
        Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "activation.hardware_review_decided"));
        Assert.Single(db.CentralOperationRecords.Where(x => x.ClientOperationId == outcome.ClientOperationId));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("wrong-session")]
    [InlineData("wrong-payload")]
    [InlineData("wrong-action")]
    [InlineData("consumed")]
    public async Task Hardware_rebind_requires_and_consumes_payload_bound_independent_approval(string grantMode)
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var installation = AddInstallation(db, shopId, Guid.NewGuid());
        var review = HardwareReview.Create(shopId, installation.Id, "reported-hash", "stored-hash", "fingerprint changed", DateTimeOffset.UtcNow);
        var evidence = AddEvidence(db, shopId, installation.Id);
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            caseId = review.Id, installationId = installation.Id, previousFingerprintId = review.StoredMachineFingerprintId,
            currentFingerprintId = review.ReportedMachineFingerprintId, decision = "APPROVE_REBIND", verificationEvidenceId = evidence.Id
        });
        var approvedPayloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex;
        var approval = EdgeRetails.Central.Domain.Licensing.SensitiveApprovalRequest.Request("HARDWARE_REVIEW", review.Id,
            approvedPayloadSha256, requesterId, "hardware.review", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        approval.Approve(approverId, approvedPayloadSha256, DateTimeOffset.UtcNow);
        db.HardwareReviews.Add(review);
        db.EncryptedObjects.Add(evidence);
        db.SensitiveApprovalRequests.Add(approval);
        await db.SaveChangesAsync();
        var controller = CreateController(db, approverId);
        AddExecutionGrant(db, controller, "hardware.review", approvedPayloadSha256, grantMode);

        if (grantMode != "valid")
        {
            var denied = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
                () => controller.DecideHardware(review.Id, Envelope("hardware.review", payload, review.RowVersion, approval.Id), CancellationToken.None));
            Assert.Equal("STEP_UP_REQUIRED", denied.Code);
            Assert.Equal(HardwareReview.StatusPending, review.Status);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, approval.Status);
            Assert.Empty(db.CentralAuditRecords);
            Assert.Empty(db.CentralOutboxMessages);
            return;
        }

        var result = await controller.DecideHardware(review.Id, Envelope("hardware.review", payload, review.RowVersion, approval.Id), CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        var saved = await db.HardwareReviews.SingleAsync(x => x.Id == review.Id);
        Assert.Equal("APPROVE_REBIND", saved.ContractDecision);
        Assert.Equal(approverId, saved.ReviewedByAdminId);
        Assert.Equal(EdgeRetails.Central.Domain.Licensing.SensitiveApprovalRequest.StatusConsumed,
            (await db.SensitiveApprovalRequests.SingleAsync(x => x.Id == approval.Id)).Status);
        Assert.Single(db.CentralAuditRecords.Where(x => x.Action == "HARDWARE_REVIEW_DECIDED"));
        Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "activation.hardware_review_decided"));
        Assert.Single(db.CentralOperationRecords.Where(x => x.ClientOperationId == outcome.ClientOperationId));
        Assert.True((await db.StepUpGrants.SingleAsync()).IsConsumed);
        var replay = Assert.IsType<JsonResult>(await controller.DecideHardware(review.Id,
            Envelope("hardware.review", payload, 1, approval.Id, outcome.ClientOperationId), CancellationToken.None)).Value as OutcomeDto;
        Assert.Equal(outcome.OutcomeId, replay!.OutcomeId);
    }

    [Fact]
    public async Task Hardware_rebind_without_separate_approval_is_rejected_without_writes()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var installation = AddInstallation(db, shopId, Guid.NewGuid());
        var review = HardwareReview.Create(shopId, installation.Id, "reported-hash", "stored-hash", "fingerprint changed", DateTimeOffset.UtcNow);
        var evidence = AddEvidence(db, shopId, installation.Id);
        db.HardwareReviews.Add(review);
        db.EncryptedObjects.Add(evidence);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            caseId = review.Id, installationId = installation.Id, previousFingerprintId = review.StoredMachineFingerprintId,
            currentFingerprintId = review.ReportedMachineFingerprintId, decision = "APPROVE_REBIND", verificationEvidenceId = evidence.Id
        });
        var controller = CreateController(db, Guid.NewGuid());

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.DecideHardware(review.Id, Envelope("hardware.review", payload, review.RowVersion), CancellationToken.None));

        Assert.Equal("APPROVAL_REQUIRED", error.Code);
        Assert.Equal(HardwareReview.StatusPending, (await db.HardwareReviews.SingleAsync(x => x.Id == review.Id)).Status);
        Assert.Empty(db.CentralAuditRecords);
        Assert.Empty(db.CentralOutboxMessages);
        Assert.Empty(db.CentralOperationRecords);
    }

    [Fact]
    public async Task Transfer_request_creates_persisted_same_license_transfer_from_server_scope()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var evidence = AddEvidence(db, shopId, source.Id);
        await db.SaveChangesAsync();
        var transferId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId, oldInstallationId = source.Id, newInstallationId = target.Id, recoveryType = "STANDARD_TRANSFER",
            verificationEvidenceId = evidence.Id, decision = "REQUEST"
        });
        var envelope = Envelope("primary.transfer", payload, expectedVersion: 0);
        var controller = CreateController(db, out _);

        var result = await controller.DecideTransfer(transferId, envelope, CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        Assert.Equal($"transfers/{transferId:D}", outcome.ResultReference);
        var saved = await db.PrimaryTransfers.SingleAsync(x => x.Id == transferId);
        Assert.Equal(shopId, saved.ShopId);
        Assert.Equal("REQUEST", saved.ContractDecision);
        Assert.Equal("STANDARD_TRANSFER", saved.RecoveryType);
        Assert.Equal(evidence.Id, saved.VerificationEvidenceId);
        Assert.Single(db.CentralAuditRecords.Where(x => x.Action == "PRIMARY_TRANSFER_REQUESTED"));
        Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "activation.primary_transfer_initiated"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("wrong-session")]
    [InlineData("wrong-payload")]
    [InlineData("wrong-action")]
    [InlineData("consumed")]
    public async Task Transfer_approval_consumes_dual_control_and_persists_contract_evidence(string grantMode)
    {
        await using var db = CreateDb();
        using var signer = new TestProtectedSignerClient();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var evidence = AddEvidence(db, shopId, source.Id);
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var transfer = PrimaryTransfer.Initiate(shopId, source.Id, target.Id, "synthetic recovery", requesterId, DateTimeOffset.UtcNow);
        transfer.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", evidence.Id, requesterId);
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId = transfer.Id, oldInstallationId = source.Id, newInstallationId = target.Id,
            recoveryType = "STANDARD_TRANSFER", verificationEvidenceId = evidence.Id, decision = "APPROVE"
        });
        var approvedPayloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex;
        var approval = SensitiveApprovalRequest.Request("PRIMARY_TRANSFER", transfer.Id, approvedPayloadSha256, requesterId,
            "shop.transfer", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        approval.Approve(approverId, approvedPayloadSha256, DateTimeOffset.UtcNow);
        db.PrimaryTransfers.Add(transfer);
        db.SensitiveApprovalRequests.Add(approval);
        await db.SaveChangesAsync();

        var envelope = Envelope("primary.transfer", payload, transfer.RowVersion, approval.Id);
        var controller = CreateController(db, approverId, signer);
        AddExecutionGrant(db, controller, "primary.transfer", approvedPayloadSha256, grantMode);

        if (grantMode != "valid")
        {
            var denied = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
                () => controller.DecideTransfer(transfer.Id, envelope, CancellationToken.None));
            Assert.Equal("STEP_UP_REQUIRED", denied.Code);
            Assert.Equal(PrimaryTransfer.StatusPendingApproval, transfer.Status);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, approval.Status);
            Assert.Empty(db.CentralAuditRecords);
            Assert.Empty(db.CentralOutboxMessages);
            return;
        }

        var result = await controller.DecideTransfer(transfer.Id, envelope, CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        var replay = Assert.IsType<JsonResult>(await controller.DecideTransfer(transfer.Id, envelope, CancellationToken.None)).Value as OutcomeDto;
        Assert.NotNull(replay);
        Assert.Equal(outcome.OutcomeId, replay.OutcomeId);
        Assert.Equal(outcome.ResourceVersion, replay.ResourceVersion);
        var saved = await db.PrimaryTransfers.SingleAsync(x => x.Id == transfer.Id);
        Assert.Equal(PrimaryTransfer.StatusApproved, saved.Status);
        Assert.Equal("APPROVE", saved.ContractDecision);
        Assert.Equal("STANDARD_TRANSFER", saved.RecoveryType);
        Assert.Equal(evidence.Id, saved.VerificationEvidenceId);
        Assert.Equal(approverId, saved.ApprovedByAdminId);
        Assert.Equal(SensitiveApprovalRequest.StatusConsumed, (await db.SensitiveApprovalRequests.SingleAsync(x => x.Id == approval.Id)).Status);
        Assert.Single(db.CentralAuditRecords.Where(x => x.Action == "PRIMARY_TRANSFER_APPROVED"));
        Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "activation.primary_transfer_approved"));
        Assert.Single(db.CentralOperationRecords.Where(x => x.ClientOperationId == outcome.ClientOperationId));
        Assert.True((await db.StepUpGrants.SingleAsync()).IsConsumed);
    }

    [Fact]
    public async Task Transfer_approval_rejects_approved_payload_digest_mismatch_without_consuming_approval()
    {
        await using var db = CreateDb();
        using var signer = new TestProtectedSignerClient();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var evidence = AddEvidence(db, shopId, source.Id);
        var requesterId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var transfer = PrimaryTransfer.Initiate(shopId, source.Id, target.Id, "synthetic recovery", requesterId, DateTimeOffset.UtcNow);
        transfer.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", evidence.Id, requesterId);
        var approval = SensitiveApprovalRequest.Request("PRIMARY_TRANSFER", transfer.Id, new string('a', 64), requesterId,
            "shop.transfer", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));
        approval.Approve(approverId, new string('a', 64), DateTimeOffset.UtcNow);
        db.PrimaryTransfers.Add(transfer);
        db.SensitiveApprovalRequests.Add(approval);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId = transfer.Id, oldInstallationId = source.Id, newInstallationId = target.Id,
            recoveryType = "STANDARD_TRANSFER", verificationEvidenceId = evidence.Id, decision = "APPROVE"
        });
        var controller = CreateController(db, approverId, signer);
        AddExecutionGrant(db, controller, "primary.transfer", _serializer.SerializeAndHash(payload).Sha256Hex, "valid");

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.DecideTransfer(transfer.Id, Envelope("primary.transfer", payload, transfer.RowVersion, approval.Id), CancellationToken.None));

        Assert.Equal("APPROVAL_PAYLOAD_MISMATCH", error.Code);
        Assert.Equal(PrimaryTransfer.StatusPendingApproval, (await db.PrimaryTransfers.SingleAsync(x => x.Id == transfer.Id)).Status);
        Assert.Equal(SensitiveApprovalRequest.StatusApproved, (await db.SensitiveApprovalRequests.SingleAsync(x => x.Id == approval.Id)).Status);
        Assert.Empty(db.CentralAuditRecords);
        Assert.Empty(db.CentralOutboxMessages);
        Assert.Empty(db.CentralOperationRecords);
    }

    [Fact]
    public async Task Transfer_cancellation_persists_contract_state_and_audit()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var evidence = AddEvidence(db, shopId, source.Id);
        var requesterId = Guid.NewGuid();
        var transfer = PrimaryTransfer.Initiate(shopId, source.Id, target.Id, "synthetic transfer", requesterId, DateTimeOffset.UtcNow);
        transfer.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", evidence.Id, requesterId);
        db.PrimaryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId = transfer.Id, oldInstallationId = source.Id, newInstallationId = target.Id,
            recoveryType = "STANDARD_TRANSFER", verificationEvidenceId = evidence.Id, decision = "CANCEL"
        });
        var controller = CreateController(db, requesterId, (ActivationService)null!);

        var result = await controller.DecideTransfer(transfer.Id, Envelope("primary.transfer", payload, transfer.RowVersion), CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        var saved = await db.PrimaryTransfers.SingleAsync(x => x.Id == transfer.Id);
        Assert.Equal(PrimaryTransfer.StatusCancelled, saved.Status);
        Assert.Equal("CANCEL", saved.ContractDecision);
        Assert.Equal(evidence.Id, saved.VerificationEvidenceId);
        Assert.Single(db.CentralAuditRecords.Where(x => x.Action == "PRIMARY_TRANSFER_DECIDED"));
        Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "activation.primary_transfer_decided"));
    }

    [Fact]
    public async Task Transfer_cancellation_rejects_unexpected_sensitive_approval_without_writes()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var evidence = AddEvidence(db, shopId, source.Id);
        var requesterId = Guid.NewGuid();
        var transfer = PrimaryTransfer.Initiate(shopId, source.Id, target.Id, "synthetic transfer", requesterId, DateTimeOffset.UtcNow);
        transfer.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", evidence.Id, requesterId);
        db.PrimaryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId = transfer.Id, oldInstallationId = source.Id, newInstallationId = target.Id,
            recoveryType = "STANDARD_TRANSFER", verificationEvidenceId = evidence.Id, decision = "CANCEL"
        });
        var controller = CreateController(db, requesterId, (ActivationService)null!);

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.DecideTransfer(transfer.Id, Envelope("primary.transfer", payload, transfer.RowVersion, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("UNEXPECTED_APPROVAL", error.Code);
        Assert.Equal(PrimaryTransfer.StatusPendingApproval, (await db.PrimaryTransfers.SingleAsync(x => x.Id == transfer.Id)).Status);
        Assert.Empty(db.CentralAuditRecords);
        Assert.Empty(db.CentralOutboxMessages);
        Assert.Empty(db.CentralOperationRecords);
    }

    [Fact]
    public async Task Transfer_rejection_persists_contract_state_and_audit()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var evidence = AddEvidence(db, shopId, source.Id);
        var requesterId = Guid.NewGuid();
        var transfer = PrimaryTransfer.Initiate(shopId, source.Id, target.Id, "synthetic transfer", requesterId, DateTimeOffset.UtcNow);
        transfer.ApplyContractDecision("REQUEST", "LOST_HARDWARE_RECOVERY", evidence.Id, requesterId);
        db.PrimaryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId = transfer.Id, oldInstallationId = source.Id, newInstallationId = target.Id,
            recoveryType = "LOST_HARDWARE_RECOVERY", verificationEvidenceId = evidence.Id, decision = "REJECT"
        });
        var controller = CreateController(db, Guid.NewGuid(), (ActivationService)null!);

        var result = await controller.DecideTransfer(transfer.Id, Envelope("primary.transfer", payload, transfer.RowVersion), CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        var saved = await db.PrimaryTransfers.SingleAsync(x => x.Id == transfer.Id);
        Assert.Equal(PrimaryTransfer.StatusRejected, saved.Status);
        Assert.Equal("REJECT", saved.ContractDecision);
        Assert.Equal("LOST_HARDWARE_RECOVERY", saved.RecoveryType);
        Assert.Equal(evidence.Id, saved.VerificationEvidenceId);
        Assert.Single(db.CentralAuditRecords.Where(x => x.Action == "PRIMARY_TRANSFER_DECIDED"));
        Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "activation.primary_transfer_decided"));
    }

    [Fact]
    public async Task Transfer_decision_rejects_cross_shop_evidence_without_mutation()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var source = AddInstallation(db, shopId, licenseId);
        var target = AddInstallation(db, shopId, licenseId);
        var foreignEvidence = AddEvidence(db, Guid.NewGuid(), source.Id);
        var requesterId = Guid.NewGuid();
        var transfer = PrimaryTransfer.Initiate(shopId, source.Id, target.Id, "synthetic transfer", requesterId, DateTimeOffset.UtcNow);
        transfer.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", AddEvidence(db, shopId, source.Id).Id, requesterId);
        db.PrimaryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            transferId = transfer.Id, oldInstallationId = source.Id, newInstallationId = target.Id,
            recoveryType = "STANDARD_TRANSFER", verificationEvidenceId = foreignEvidence.Id, decision = "CANCEL"
        });
        var controller = CreateController(db, requesterId, (ActivationService)null!);

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.DecideTransfer(transfer.Id, Envelope("primary.transfer", payload, transfer.RowVersion), CancellationToken.None));

        Assert.Equal("EVIDENCE_SCOPE_INVALID", error.Code);
        var unchanged = await db.PrimaryTransfers.SingleAsync(x => x.Id == transfer.Id);
        Assert.Equal(PrimaryTransfer.StatusPendingApproval, unchanged.Status);
        Assert.Equal("REQUEST", unchanged.ContractDecision);
        Assert.Empty(db.CentralAuditRecords);
        Assert.Empty(db.CentralOutboxMessages);
        Assert.Empty(db.CentralOperationRecords);
    }

    [Fact]
    public async Task Hardware_decision_rejects_cross_shop_support_evidence_without_writes()
    {
        await using var db = CreateDb();
        var shopId = Guid.NewGuid();
        var installation = AddInstallation(db, shopId, Guid.NewGuid());
        var review = HardwareReview.Create(shopId, installation.Id, "reported-hash", "stored-hash", "fingerprint changed", DateTimeOffset.UtcNow);
        var evidence = AddEvidence(db, Guid.NewGuid(), installation.Id);
        db.HardwareReviews.Add(review);
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new
        {
            caseId = review.Id, installationId = installation.Id, previousFingerprintId = review.StoredMachineFingerprintId,
            currentFingerprintId = review.ReportedMachineFingerprintId, decision = "REJECT", verificationEvidenceId = evidence.Id
        });
        var envelope = Envelope("hardware.review", payload, review.RowVersion);
        var controller = CreateController(db, out _);

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.DecideHardware(review.Id, envelope, CancellationToken.None));

        Assert.Equal("EVIDENCE_SCOPE_INVALID", error.Code);
        Assert.Equal(HardwareReview.StatusPending, (await db.HardwareReviews.SingleAsync(x => x.Id == review.Id)).Status);
        Assert.Empty(db.CentralAuditRecords);
        Assert.Empty(db.CentralOutboxMessages);
        Assert.Empty(db.CentralOperationRecords);
    }

    private CanonicalHardwareTransferController CreateController(CentralDbContext db, out Guid adminId)
    {
        adminId = Guid.NewGuid();
        return CreateController(db, adminId);
    }

    private CanonicalHardwareTransferController CreateController(CentralDbContext db, Guid adminId)
    {
        return CreateController(db, adminId, (ActivationService)null!);
    }

    private CanonicalHardwareTransferController CreateController(CentralDbContext db, Guid adminId, TestProtectedSignerClient signer)
    {
        var activation = new ActivationService(db, signer, new CentralAuditService(db), new OperationLedgerService(db, _serializer), _serializer, 300);
        return CreateController(db, adminId, activation);
    }

    private CanonicalHardwareTransferController CreateController(CentralDbContext db, Guid adminId, ActivationService activation)
    {
        var controller = new CanonicalHardwareTransferController(db, activation, new OperationLedgerService(db, _serializer),
            new CentralAuditService(db), _serializer,
            new StepUpService(db, new TotpService(), AdminAuthenticationPolicy.FromValues("300", "900", "28800", "300")));
        var now = DateTimeOffset.UtcNow;
        var admin = db.AdminUsers.Find(adminId);
        if (admin is null)
        {
            admin = AdminUser.Create($"synthetic-stepup-{adminId:N}", "fixture", "fixture", null, false, now);
            typeof(AdminUser).GetProperty(nameof(AdminUser.Id))!.SetValue(admin, adminId);
            db.AdminUsers.Add(admin);
        }
        var persistedSession = AdminSession.Create(adminId, $"synthetic-{Guid.NewGuid():N}", now,
            TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, ["hardware.review", "primary.transfer"]);
        db.AdminSessions.Add(persistedSession);
        db.SaveChanges();
        var context = new DefaultHttpContext();
        context.Items["AdminSession"] = new AdminSessionDto { AdminUserId = adminId, SessionId = persistedSession.Id };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static void AddExecutionGrant(CentralDbContext db, CanonicalHardwareTransferController controller, string action, string hash, string mode)
    {
        if (mode == "missing")
        {
            return;
        }
        var session = (AdminSessionDto)controller.HttpContext.Items["AdminSession"]!;
        var grant = StepUpGrant.Create(mode == "wrong-session" ? Guid.NewGuid() : session.SessionId,
            mode == "wrong-action" ? "unrelated.action" : action,
            mode == "wrong-payload" ? new string('f', 64) : hash, Guid.NewGuid(),
            mode == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-10) : DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        if (mode == "consumed")
        {
            grant.Consume(DateTimeOffset.UtcNow);
        }
        db.StepUpGrants.Add(grant);
        db.SaveChanges();
    }

    private static CentralDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CentralDbContext(options);
    }

    private static Installation AddInstallation(CentralDbContext db, Guid shopId, Guid licenseId)
    {
        var install = Installation.Register(shopId, licenseId, Guid.NewGuid().ToString("N"), "test-public-key", "fixture", "windows", DateTimeOffset.UtcNow);
        db.Installations.Add(install);
        return install;
    }

    private static EncryptedObjectRecord AddEvidence(CentralDbContext db, Guid shopId, Guid installationId)
    {
        var record = EncryptedObjectRecord.Create(Guid.NewGuid(), shopId, installationId, EncryptedObjectRecord.PurposeSupportAttachment, 16,
            "application/octet-stream", new string('a', 64), $"fixtures/{Guid.NewGuid():N}.bin", Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        db.EncryptedObjects.Add(record);
        return record;
    }

    private JsonElement Envelope(string operationType, JsonElement payload, int expectedVersion, Guid? sensitiveApprovalId = null, Guid? operationId = null)
    {
        var payloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex;
        return JsonSerializer.SerializeToElement(new
        {
            meta = new { clientOperationId = operationId ?? Guid.NewGuid(), operationType, fingerprintVersion = 1, payloadSha256,
                expectedVersion, policyVersion = 0, reason = "synthetic test decision", sensitiveApprovalId },
            payload
        });
    }
}
