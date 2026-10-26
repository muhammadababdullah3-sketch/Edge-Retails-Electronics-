using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.IntegrationTests.Licensing;

public sealed class CanonicalHardwareTransferPostgreSqlTests
{
    [Fact]
    public async Task Rebind_step_up_denial_rolls_back_then_fresh_session_grant_commits_once_and_replays()
    {
        var serializer = new CanonicalJsonSerializer();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("Synthetic step-up", $"stepup-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"SU{Guid.NewGuid():N}"[..12], "Step-up fixture", now);
        var installation = Installation.Register(shop.Id, Guid.NewGuid(), "synthetic-machine", "synthetic-key", "fixture", "Windows", now);
        var review = HardwareReview.Create(shop.Id, installation.Id, "reported", "stored", "synthetic mismatch", now);
        var requester = AdminUser.Create($"requester-{Guid.NewGuid():N}", "fixture", "fixture", null, false, now);
        var executor = AdminUser.Create($"executor-{Guid.NewGuid():N}", "fixture", "fixture", null, false, now);
        var session = AdminSession.Create(executor.Id, $"synthetic-{Guid.NewGuid():N}", now, TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(8), true, executor.SecurityRevision, ["hardware.review"]);
        using var storage = new EdgeRetails.Central.Infrastructure.Storage.DiskObjectStorageProvider();
        var evidenceId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("isolated-rebind-stepup-evidence");
        var path = await storage.SaveCiphertextAsync(shop.Id, evidenceId, new MemoryStream(bytes));
        var evidence = EncryptedObjectRecord.Create(evidenceId, shop.Id, installation.Id, EncryptedObjectRecord.PurposeSupportAttachment,
            bytes.Length, "application/octet-stream", Convert.ToHexStringLower(SHA256.HashData(bytes)), path, Guid.NewGuid(), null, now);
        var payload = JsonSerializer.SerializeToElement(new { caseId = review.Id, installationId = installation.Id,
            previousFingerprintId = review.StoredMachineFingerprintId, currentFingerprintId = review.ReportedMachineFingerprintId,
            decision = "APPROVE_REBIND", verificationEvidenceId = evidence.Id });
        var hash = serializer.SerializeAndHash(payload).Sha256Hex;
        var approval = SensitiveApprovalRequest.Request("HARDWARE_REVIEW", review.Id, hash, requester.Id, "hardware.review",
            Guid.NewGuid(), now, now.AddHours(1));
        approval.Approve(executor.Id, hash, now);
        var operationId = Guid.NewGuid();
        var envelope = JsonSerializer.SerializeToElement(new { meta = new { clientOperationId = operationId, operationType = "hardware.review",
            fingerprintVersion = 1, payloadSha256 = hash, expectedVersion = 1, policyVersion = 0, reason = "Synthetic step-up regression",
            sensitiveApprovalId = approval.Id }, payload });
        await using (var setup = new CentralDbContextFactory().CreateDbContext([]))
        {
            setup.Customers.Add(customer); setup.Shops.Add(shop); setup.Installations.Add(installation); setup.HardwareReviews.Add(review);
            setup.AdminUsers.AddRange(requester, executor); setup.AdminSessions.Add(session); setup.EncryptedObjects.Add(evidence);
            setup.SensitiveApprovalRequests.Add(approval); await setup.SaveChangesAsync();
        }
        await using (var deniedDb = new CentralDbContextFactory().CreateDbContext([]))
        {
            var controller = CreateController(deniedDb, serializer, executor.Id, session.Id);
            var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
                () => controller.DecideHardware(review.Id, envelope, CancellationToken.None));
            Assert.Equal("STEP_UP_REQUIRED", error.Code);
        }
        Guid grantId;
        await using (var freshDb = new CentralDbContextFactory().CreateDbContext([]))
        {
            Assert.False(await freshDb.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == operationId));
            Assert.Equal(HardwareReview.StatusPending, (await freshDb.HardwareReviews.SingleAsync(x => x.Id == review.Id)).Status);
            Assert.Equal(SensitiveApprovalRequest.StatusApproved, (await freshDb.SensitiveApprovalRequests.SingleAsync(x => x.Id == approval.Id)).Status);
            Assert.False(await freshDb.CentralOutboxMessages.AnyAsync(x => x.Id == operationId));
            var grant = StepUpGrant.Create(session.Id, "hardware.review", hash, Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
            grantId = grant.Id; freshDb.StepUpGrants.Add(grant); await freshDb.SaveChangesAsync();
            var controller = CreateController(freshDb, serializer, executor.Id, session.Id);
            var result = Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(await controller.DecideHardware(review.Id, envelope, CancellationToken.None)).Value);
            var replay = Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(await controller.DecideHardware(review.Id, envelope, CancellationToken.None)).Value);
            Assert.Equal("SUCCEEDED", result.State); Assert.Equal(result.OutcomeId, replay.OutcomeId);
        }
        await using var verify = new CentralDbContextFactory().CreateDbContext([]);
        Assert.Equal(HardwareReview.StatusApproved, (await verify.HardwareReviews.SingleAsync(x => x.Id == review.Id)).Status);
        Assert.True((await verify.StepUpGrants.SingleAsync(x => x.Id == grantId)).IsConsumed);
        Assert.Equal(SensitiveApprovalRequest.StatusConsumed, (await verify.SensitiveApprovalRequests.SingleAsync(x => x.Id == approval.Id)).Status);
        Assert.Equal(1, await verify.CentralOperationRecords.CountAsync(x => x.ClientOperationId == operationId));
        Assert.Equal(1, await verify.CentralOutboxMessages.CountAsync(x => x.Id == operationId));
        Assert.Equal(1, await verify.CentralAuditRecords.CountAsync(x => x.CorrelationId == operationId.ToString() && x.IsSuccess));
    }

    [Fact]
    public async Task Hardware_review_and_transfer_decisions_persist_evidence_and_contract_states_across_reopen()
    {
        var connectionString = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Explicit owned CENTRAL_DATABASE_CONNECTION_STRING required.");
        var guard = new DatabaseTargetGuard();
        guard.ValidateConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema))
            .Options;
        var serializer = new CanonicalJsonSerializer();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("Synthetic licensing workflow", $"hardware-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"HW{Guid.NewGuid():N}"[..12], "Hardware transfer fixture", now);
        var licenseId = Guid.NewGuid();
        var source = Installation.Register(shop.Id, licenseId, $"source-{Guid.NewGuid():N}", "synthetic-public-key-source", "source", "Windows", now);
        var target = Installation.Register(shop.Id, licenseId, $"target-{Guid.NewGuid():N}", "synthetic-public-key-target", "target", "Windows", now);
        using var objectStorage = new EdgeRetails.Central.Infrastructure.Storage.DiskObjectStorageProvider();
        var evidenceBytes = Encoding.UTF8.GetBytes($"synthetic-hardware-evidence-{Guid.NewGuid():N}");
        var foreignEvidenceBytes = Encoding.UTF8.GetBytes($"synthetic-foreign-hardware-evidence-{Guid.NewGuid():N}");
        var evidenceId = Guid.NewGuid();
        var foreignShopId = Guid.NewGuid();
        var foreignEvidenceId = Guid.NewGuid();
        var evidencePath = await objectStorage.SaveCiphertextAsync(shop.Id, evidenceId, new MemoryStream(evidenceBytes));
        var foreignEvidencePath = await objectStorage.SaveCiphertextAsync(foreignShopId, foreignEvidenceId, new MemoryStream(foreignEvidenceBytes));
        var evidence = EncryptedObjectRecord.Create(evidenceId, shop.Id, source.Id,
            EncryptedObjectRecord.PurposeSupportAttachment, evidenceBytes.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(evidenceBytes)), evidencePath, Guid.NewGuid(), null, now);
        var foreignEvidence = EncryptedObjectRecord.Create(foreignEvidenceId, foreignShopId, source.Id,
            EncryptedObjectRecord.PurposeSupportAttachment, foreignEvidenceBytes.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(foreignEvidenceBytes)), foreignEvidencePath, Guid.NewGuid(), null, now);
        var review = HardwareReview.Create(shop.Id, source.Id, "reported-fingerprint", "stored-fingerprint", "synthetic mismatch", now);
        var transferId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        await using (var db = new CentralDbContext(options, guard))
        {
            await db.Database.OpenConnectionAsync();
            await guard.VerifyServerIdentityAsync(db.Database.GetDbConnection(), DatabaseTargetGuard.Required("CENTRAL_PG_DATABASE"), DatabaseTargetGuard.Required("CENTRAL_PG_ROLE"));
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.Installations.AddRange(source, target);
            db.EncryptedObjects.AddRange(evidence, foreignEvidence);
            db.HardwareReviews.Add(review);
            await db.SaveChangesAsync();

            var hardwarePayload = JsonSerializer.SerializeToElement(new
            {
                caseId = review.Id,
                installationId = source.Id,
                previousFingerprintId = review.StoredMachineFingerprintId,
                currentFingerprintId = review.ReportedMachineFingerprintId,
                decision = "REQUEST_INFORMATION",
                verificationEvidenceId = evidence.Id
            });
            var hardware = CreateController(db, serializer, adminId);
            var hardwareResult = await hardware.DecideHardware(review.Id, Envelope(serializer, "hardware.review", hardwarePayload, review.RowVersion), CancellationToken.None);
            Assert.Equal("SUCCEEDED", Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(hardwareResult).Value).State);

            var requestPayload = TransferPayload(transferId, source.Id, target.Id, evidence.Id, "REQUEST");
            var transferController = CreateController(db, serializer, adminId);
            var requested = await transferController.DecideTransfer(transferId, Envelope(serializer, "primary.transfer", requestPayload, 0), CancellationToken.None);
            Assert.Equal("SUCCEEDED", Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(requested).Value).State);

            var foreignPayload = TransferPayload(transferId, source.Id, target.Id, foreignEvidence.Id, "CANCEL");
            var denied = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
                () => transferController.DecideTransfer(transferId, Envelope(serializer, "primary.transfer", foreignPayload, 2), CancellationToken.None));
            Assert.Equal("EVIDENCE_SCOPE_INVALID", denied.Code);
        }

        await using (var reopened = new CentralDbContext(options, guard))
        {
            var persistedReview = await reopened.HardwareReviews.SingleAsync(x => x.Id == review.Id);
            Assert.Equal("REQUEST_INFORMATION", persistedReview.ContractDecision);
            Assert.Equal("REQUEST_INFORMATION", persistedReview.Status);
            Assert.Equal(evidence.Id, persistedReview.VerificationEvidenceId);
            Assert.Equal(2, persistedReview.RowVersion);

            var persistedTransfer = await reopened.PrimaryTransfers.SingleAsync(x => x.Id == transferId);
            Assert.Equal(PrimaryTransfer.StatusPendingApproval, persistedTransfer.Status);
            Assert.Equal("REQUEST", persistedTransfer.ContractDecision);
            Assert.Equal("STANDARD_TRANSFER", persistedTransfer.RecoveryType);
            Assert.Equal(evidence.Id, persistedTransfer.VerificationEvidenceId);
            Assert.Equal(2, persistedTransfer.RowVersion);

            var cancelPayload = TransferPayload(transferId, source.Id, target.Id, evidence.Id, "CANCEL");
            var cancelController = CreateController(reopened, serializer, adminId);
            var cancelled = await cancelController.DecideTransfer(transferId, Envelope(serializer, "primary.transfer", cancelPayload, persistedTransfer.RowVersion), CancellationToken.None);
            Assert.Equal("SUCCEEDED", Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(cancelled).Value).State);
        }

        await using (var final = new CentralDbContext(options, guard))
        {
            var persisted = await final.PrimaryTransfers.SingleAsync(x => x.Id == transferId);
            Assert.Equal(PrimaryTransfer.StatusCancelled, persisted.Status);
            Assert.Equal("CANCEL", persisted.ContractDecision);
            Assert.Equal("STANDARD_TRANSFER", persisted.RecoveryType);
            Assert.Equal(evidence.Id, persisted.VerificationEvidenceId);
            Assert.Equal(3, persisted.RowVersion);
            Assert.Equal(2, await final.CentralAuditRecords.CountAsync(x => x.Resource == nameof(PrimaryTransfer) && x.ResourceId == transferId.ToString("D") && x.IsSuccess));
            Assert.Equal(2, await final.CentralOutboxMessages.CountAsync(x => x.AggregateId == transferId.ToString("D")));
            Assert.Equal(2, await final.CentralOperationRecords.CountAsync(x => x.TargetResource == $"primary_transfer/{transferId:D}"));
            Assert.Empty(await final.CentralConsumerEffects.Where(x => x.AggregateId == transferId.ToString("D")).ToListAsync());
        }
    }

    private static CanonicalHardwareTransferController CreateController(CentralDbContext db, CanonicalJsonSerializer serializer, Guid adminId, Guid? sessionId = null)
    {
        var controller = new CanonicalHardwareTransferController(db, null!, new OperationLedgerService(db, serializer), new CentralAuditService(db), serializer,
            new StepUpService(db, new TotpService(), AdminAuthenticationPolicy.FromEnvironment()));
        var context = new DefaultHttpContext();
        context.Items["AdminSession"] = new AdminSessionDto { AdminUserId = adminId, SessionId = sessionId ?? Guid.NewGuid() };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static JsonElement TransferPayload(Guid id, Guid oldId, Guid newId, Guid evidenceId, string decision) =>
        JsonSerializer.SerializeToElement(new
        {
            transferId = id,
            oldInstallationId = oldId,
            newInstallationId = newId,
            recoveryType = "STANDARD_TRANSFER",
            verificationEvidenceId = evidenceId,
            decision
        });

    private static JsonElement Envelope(CanonicalJsonSerializer serializer, string operationType, JsonElement payload, int expectedVersion) =>
        JsonSerializer.SerializeToElement(new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(), operationType, fingerprintVersion = 1,
                payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion, policyVersion = 0, reason = "synthetic Central PostgreSQL hardware and transfer fixture"
            },
            payload
        });
}
