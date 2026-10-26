using System.Text.Json;
using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EdgeRetails.Central.UnitTests.Commercial;

public sealed class CanonicalManualPaymentReviewTests
{
    [Fact]
    public async Task Explicit_admin_payment_review_approves_evidence_but_never_issues_license()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CentralDbContext(options);
        var now = DateTimeOffset.UtcNow;
        var admin = Guid.NewGuid();
        var customer = Customer.Create("Synthetic Payment Buyer", "buyer@example.test", "", now);
        var shop = Shop.Create(customer.Id, $"PAY_{Guid.NewGuid():N}"[..12], "Synthetic Payment Shop", now);
        var plan = PlanCatalogItem.Create("TEST-ANNUAL", "Synthetic annual term", PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermFixed, "YEARLY", 100m, "USD");
        var snapshot = PricingSnapshot.Create(shop.Id, plan, 1, now);
        var order = Order.CreateFromSnapshot(snapshot, customer.Id, now);
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var upload = UploadSession.Create(UploadSession.PurposePaymentProof, shop.Id, admin, order.Id, 16, digest,
            "pending/payment-proof.bin", now.AddHours(1), now, "application/pdf");
        upload.AppendChunk(16);
        upload.FinalizeUpload(digest);
        upload.SetStoragePath("shops/payment/proof.bin");
        var evidence = PaymentEvidence.CreateFromSession(upload, order.Id, "application/pdf", now);
        db.Customers.Add(customer);
        db.Shops.Add(shop);
        db.PlanCatalogItems.Add(plan);
        db.PricingSnapshots.Add(snapshot);
        db.Orders.Add(order);
        db.UploadSessions.Add(upload);
        db.PaymentEvidences.Add(evidence);
        await db.SaveChangesAsync();

        var serializer = new CanonicalJsonSerializer();
        var controller = new CanonicalPaymentReviewCommandsController(db, new OperationLedgerService(db, serializer),
            new CentralAuditService(db), serializer);
        var context = new DefaultHttpContext();
        context.Items["AdminSession"] = new AdminSessionDto { AdminUserId = admin, SessionId = Guid.NewGuid() };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var payload = JsonSerializer.SerializeToElement(new
        {
            orderId = order.Id,
            proofId = evidence.Id,
            decision = "APPROVE",
            reasonCode = "MANUALLY_VERIFIED",
            internalNote = "Synthetic bank evidence checked by an authorized administrator."
        });
        var envelope = JsonSerializer.SerializeToElement(new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(), operationType = "payment.review", fingerprintVersion = 1,
                payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion = evidence.RowVersion, policyVersion = 0, reason = "manual payment verification"
            },
            payload
        });

        var result = await controller.Review(evidence.Id, envelope, CancellationToken.None);

        var outcome = Assert.IsType<JsonResult>(result).Value as OutcomeDto;
        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        Assert.Equal(Order.StatusPaidApproved, order.Status);
        Assert.Equal(admin, order.ApprovedByAdminId);
        Assert.Equal(evidence.Id, order.ApprovedEvidenceId);
        Assert.Equal(PaymentEvidence.ReviewApproved, evidence.ReviewStatus);
        Assert.Single(db.PaymentReviewRecords.Where(x => x.EvidenceId == evidence.Id && x.ReviewerAdminId == admin));
        Assert.Empty(db.CommercialLicenses);
        Assert.Contains(db.CentralOutboxMessages, x => x.EventType == "commercial.payment_reviewed");
        Assert.DoesNotContain(db.CentralOutboxMessages, x => x.EventType.Contains("license", StringComparison.OrdinalIgnoreCase));
    }
}
