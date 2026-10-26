using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.IntegrationTests.Support;

public sealed class CanonicalSupportWorkflowPostgreSqlTests
{
    [Fact]
    public async Task Persisted_admin_ticket_and_reply_survive_postgresql_reopen_and_reject_cross_shop_attachment()
    {
        var connectionString = Environment.GetEnvironmentVariable("CENTRAL_DATABASE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("Explicit owned CENTRAL_DATABASE_CONNECTION_STRING required.");
        var guard = new DatabaseTargetGuard();
        guard.ValidateConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CentralDbContext.CentralSchema))
            .Options;
        var serializer = new CanonicalJsonSerializer();
        var admin = AdminUser.Create($"support-staff-{Guid.NewGuid():N}", "synthetic-fixture-hash", "synthetic-fixture-salt", null, false, DateTimeOffset.UtcNow);
        var shop = Shop.Create(Guid.NewGuid(), $"S{Guid.NewGuid():N}"[..12], "Support PostgreSQL fixture", DateTimeOffset.UtcNow);
        var otherShop = Shop.Create(Guid.NewGuid(), $"S{Guid.NewGuid():N}"[..12], "Other support PostgreSQL fixture", DateTimeOffset.UtcNow);
        using var objectStorage = new EdgeRetails.Central.Infrastructure.Storage.DiskObjectStorageProvider();
        var attachmentBytes = Encoding.UTF8.GetBytes($"synthetic-support-attachment-{Guid.NewGuid():N}");
        var foreignAttachmentBytes = Encoding.UTF8.GetBytes($"synthetic-foreign-attachment-{Guid.NewGuid():N}");
        var attachmentId = Guid.NewGuid();
        var foreignAttachmentId = Guid.NewGuid();
        var attachmentPath = await objectStorage.SaveCiphertextAsync(shop.Id, attachmentId, new MemoryStream(attachmentBytes));
        var foreignAttachmentPath = await objectStorage.SaveCiphertextAsync(otherShop.Id, foreignAttachmentId, new MemoryStream(foreignAttachmentBytes));
        var attachment = EncryptedObjectRecord.Create(attachmentId, shop.Id, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeSupportAttachment, attachmentBytes.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(attachmentBytes)), attachmentPath, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        var foreignAttachment = EncryptedObjectRecord.Create(foreignAttachmentId, otherShop.Id, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeSupportAttachment, foreignAttachmentBytes.Length, "application/octet-stream",
            Convert.ToHexStringLower(SHA256.HashData(foreignAttachmentBytes)), foreignAttachmentPath, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        Guid ticketId;

        await using (var db = new CentralDbContext(options, guard))
        {
            await db.Database.OpenConnectionAsync();
            await guard.VerifyServerIdentityAsync(db.Database.GetDbConnection(), DatabaseTargetGuard.Required("CENTRAL_PG_DATABASE"), DatabaseTargetGuard.Required("CENTRAL_PG_ROLE"));
            db.AdminUsers.Add(admin);
            db.Shops.AddRange(shop, otherShop);
            db.EncryptedObjects.AddRange(attachment, foreignAttachment);
            await db.SaveChangesAsync();

            var controller = CreateController(db, serializer, admin.Id);
            var createPayload = JsonSerializer.SerializeToElement(new
            {
                shopId = shop.Id,
                subject = "Backup upload issue",
                summary = "The encrypted upload stopped before completion.",
                attachmentObjectIds = new[] { attachment.Id },
                redactionProfileVersion = 1
            });
            var created = await controller.Create(Envelope(serializer, "support.create", createPayload, 0), CancellationToken.None);
            var outcome = Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(created).Value);
            Assert.Equal("SUCCEEDED", outcome.State);
            var resultReference = Assert.IsType<string>(outcome.ResultReference);
            Assert.StartsWith("support/tickets/", resultReference, StringComparison.Ordinal);
            ticketId = Guid.Parse(resultReference["support/tickets/".Length..]);

            var rejectedPayload = JsonSerializer.SerializeToElement(new
            {
                shopId = shop.Id,
                subject = "Cross-shop attachment attempt",
                summary = "This attachment belongs to another persisted shop.",
                attachmentObjectIds = new[] { foreignAttachment.Id },
                redactionProfileVersion = 1
            });
            var rejected = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
                () => controller.Create(Envelope(serializer, "support.create", rejectedPayload, 0), CancellationToken.None));
            Assert.Equal("SUPPORT_ATTACHMENT_ACCESS_DENIED", rejected.Code);
            Assert.Equal(1, await db.SupportTickets.CountAsync(x => x.ShopId == shop.Id));
        }

        await using (var db = new CentralDbContext(options, guard))
        {
            var persisted = await db.SupportTickets.SingleAsync(x => x.Id == ticketId);
            Assert.Equal(shop.Id, persisted.ShopId);
            Assert.Equal(admin.Id, persisted.RequesterPrincipalId);
            Assert.Equal(new[] { attachment.Id }, JsonSerializer.Deserialize<Guid[]>(persisted.AttachmentObjectIdsJson));
            Assert.Equal("[]", persisted.RepliesJson);

            var payload = JsonSerializer.SerializeToElement(new
            {
                ticketId,
                action = "REPLY",
                replyText = "We received the diagnostic attachment and recorded your request."
            });
            var controller = CreateController(db, serializer, admin.Id);
            var result = await controller.Command(ticketId, Envelope(serializer, "support.reply", payload, persisted.RowVersion), CancellationToken.None);
            var outcome = Assert.IsType<OutcomeDto>(Assert.IsType<JsonResult>(result).Value);
            Assert.Equal("SUCCEEDED", outcome.State);
        }

        await using (var reopened = new CentralDbContext(options, guard))
        {
            var ticket = await reopened.SupportTickets.SingleAsync(x => x.Id == ticketId);
            var savedReply = Assert.Single(JsonSerializer.Deserialize<SupportReplyDto[]>(ticket.RepliesJson)!);
            Assert.Equal(SupportReplyDto.KindMessage, savedReply.Kind);
            Assert.Equal(admin.Id.ToString("D"), savedReply.Author);
            Assert.Equal("We received the diagnostic attachment and recorded your request.", savedReply.Text);
            Assert.Equal(2, ticket.RowVersion);
            Assert.Contains(await reopened.CentralOutboxMessages.Where(x => x.AggregateId == ticketId.ToString("D")).ToListAsync(),
                x => x.EventType == "support.ticket_created");
            Assert.Contains(await reopened.CentralOutboxMessages.Where(x => x.AggregateId == ticketId.ToString("D")).ToListAsync(),
                x => x.EventType == "support.ticket_replied");
            Assert.Equal(2, await reopened.CentralAuditRecords.CountAsync(x =>
                x.Resource == nameof(SupportTicket) && x.ResourceId == ticketId.ToString("D") && x.IsSuccess));
            Assert.DoesNotContain(await reopened.CentralConsumerEffects.Where(x => x.AggregateId == ticketId.ToString("D")).ToListAsync(),
                x => x.EventType is "support.ticket_created" or "support.ticket_replied");
        }
    }

    private static CanonicalSupportCommandsController CreateController(CentralDbContext db, CanonicalJsonSerializer serializer, Guid adminId)
    {
        var controller = new CanonicalSupportCommandsController(db, new OperationLedgerService(db, serializer), new CentralAuditService(db), serializer);
        var context = new DefaultHttpContext();
        context.Items["AdminSession"] = new AdminSessionDto { AdminUserId = adminId, SessionId = Guid.NewGuid() };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static JsonElement Envelope(CanonicalJsonSerializer serializer, string operationType, JsonElement payload, int expectedVersion) =>
        JsonSerializer.SerializeToElement(new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(), operationType, fingerprintVersion = 1,
                payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion, policyVersion = 0, reason = "synthetic Central PostgreSQL support workflow fixture"
            },
            payload
        });
}
