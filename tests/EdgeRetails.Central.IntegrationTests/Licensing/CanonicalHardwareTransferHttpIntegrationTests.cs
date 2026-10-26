using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Central.IntegrationTests.Licensing;

public sealed class CanonicalHardwareTransferHttpIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Persisted_admin_capabilities_authorize_hardware_review_and_transfer_request_cancel_routes()
    {
        var serializer = new CanonicalJsonSerializer();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("Synthetic hardware route", $"hardware-route-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, $"HR{Guid.NewGuid():N}"[..12], "Hardware route fixture", now);
        var licenseId = Guid.NewGuid();
        var source = Installation.Register(shop.Id, licenseId, $"source-{Guid.NewGuid():N}", "synthetic-route-key-source", "source", "Windows", now);
        var target = Installation.Register(shop.Id, licenseId, $"target-{Guid.NewGuid():N}", "synthetic-route-key-target", "target", "Windows", now);
        var evidenceBytes = Encoding.UTF8.GetBytes($"synthetic-hardware-route-evidence-{Guid.NewGuid():N}");
        var evidenceId = Guid.NewGuid();
        var objectStorage = factory.Services.GetRequiredService<IObjectStorageProvider>();
        var evidencePath = await objectStorage.SaveCiphertextAsync(shop.Id, evidenceId, new MemoryStream(evidenceBytes));
        var evidence = EncryptedObjectRecord.Create(evidenceId, shop.Id, source.Id,
            EncryptedObjectRecord.PurposeSupportAttachment, evidenceBytes.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(evidenceBytes)), evidencePath, Guid.NewGuid(), null, now);
        var review = HardwareReview.Create(shop.Id, source.Id, "reported-fingerprint", "stored-fingerprint", "synthetic mismatch", now);
        var admin = AdminUser.Create($"hardware-route-{Guid.NewGuid():N}", "synthetic-hash", "synthetic-salt", null, false, now);
        var role = AdminRole.Create($"hardware-route-{Guid.NewGuid():N}", "Synthetic hardware review", ["hardware.review", "primary.transfer"]);
        var sessionId = Guid.NewGuid();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString()))).ToLowerInvariant();
        var session = AdminSession.Create(admin.Id, tokenHash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true,
            admin.SecurityRevision, ["hardware.review", "primary.transfer"]);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, sessionId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            db.Customers.Add(customer);
            db.Shops.Add(shop);
            db.Installations.AddRange(source, target);
            db.EncryptedObjects.Add(evidence);
            db.HardwareReviews.Add(review);
            db.AdminUsers.Add(admin);
            db.AdminRoles.Add(role);
            db.AdminUserRoles.Add(new AdminUserRole(admin.Id, role.Id, now));
            db.AdminSessions.Add(session);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Session-Id", sessionId.ToString("D"));
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");

        var hardwarePayload = new
        {
            caseId = review.Id,
            installationId = source.Id,
            previousFingerprintId = review.StoredMachineFingerprintId,
            currentFingerprintId = review.ReportedMachineFingerprintId,
            decision = "REQUEST_INFORMATION",
            verificationEvidenceId = evidence.Id
        };
        using (var response = await client.PostAsJsonAsync($"/api/admin/v1/hardware-reviews/{review.Id:D}/decide",
            Envelope(serializer, "hardware.review", hardwarePayload, review.RowVersion)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("SUCCEEDED", result.RootElement.GetProperty("state").GetString());
        }

        var transferId = Guid.NewGuid();
        var requestPayload = TransferPayload(transferId, source.Id, target.Id, evidence.Id, "REQUEST");
        using (var response = await client.PostAsJsonAsync($"/api/admin/v1/transfers/{transferId:D}/decide",
            Envelope(serializer, "primary.transfer", requestPayload, 0)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("SUCCEEDED", result.RootElement.GetProperty("state").GetString());
        }

        int requestVersion;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var persisted = await db.PrimaryTransfers.AsNoTracking().SingleAsync(x => x.Id == transferId);
            Assert.Equal("REQUEST", persisted.ContractDecision);
            Assert.Equal(evidence.Id, persisted.VerificationEvidenceId);
            requestVersion = persisted.RowVersion;
        }

        var cancelPayload = TransferPayload(transferId, source.Id, target.Id, evidence.Id, "CANCEL");
        using (var response = await client.PostAsJsonAsync($"/api/admin/v1/transfers/{transferId:D}/decide",
            Envelope(serializer, "primary.transfer", cancelPayload, requestVersion)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("SUCCEEDED", result.RootElement.GetProperty("state").GetString());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CentralDbContext>();
            var savedReview = await db.HardwareReviews.AsNoTracking().SingleAsync(x => x.Id == review.Id);
            Assert.Equal("REQUEST_INFORMATION", savedReview.ContractDecision);
            Assert.Equal(evidence.Id, savedReview.VerificationEvidenceId);
            var savedTransfer = await db.PrimaryTransfers.AsNoTracking().SingleAsync(x => x.Id == transferId);
            Assert.Equal(PrimaryTransfer.StatusCancelled, savedTransfer.Status);
            Assert.Equal("CANCEL", savedTransfer.ContractDecision);
            Assert.Equal(evidence.Id, savedTransfer.VerificationEvidenceId);
            Assert.Equal(2, await db.CentralAuditRecords.CountAsync(x => x.ResourceId == transferId.ToString("D") && x.IsSuccess));
            Assert.Equal(2, await db.CentralOutboxMessages.CountAsync(x => x.AggregateId == transferId.ToString("D")));
        }
    }

    private static object TransferPayload(Guid id, Guid oldId, Guid newId, Guid evidenceId, string decision) => new
    {
        transferId = id,
        oldInstallationId = oldId,
        newInstallationId = newId,
        recoveryType = "STANDARD_TRANSFER",
        verificationEvidenceId = evidenceId,
        decision
    };

    private static object Envelope<T>(CanonicalJsonSerializer serializer, string operationType, T payload, int expectedVersion) => new
    {
        meta = new
        {
            clientOperationId = Guid.NewGuid(), operationType, fingerprintVersion = 1,
            payloadSha256 = serializer.SerializeAndHash(payload!).Sha256Hex,
            expectedVersion, policyVersion = 0, reason = "synthetic Central hardware/transfer HTTP fixture"
        },
        payload
    };
}
