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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace EdgeRetails.Central.UnitTests.Support;

public sealed class CanonicalSupportWorkflowTests
{
    [Fact]
    public async Task Admin_ticket_and_reply_persist_across_context_reopen_without_external_delivery()
    {
        var root = new InMemoryDatabaseRoot();
        var databaseName = $"central-support-workflow-{Guid.NewGuid():N}";
        var serializer = new CanonicalJsonSerializer();
        var admin = AdminUser.Create("support-staff", "fixture-hash", "fixture-salt", null, false, DateTimeOffset.UtcNow);
        var shop = Shop.Create(Guid.NewGuid(), $"SUP_{Guid.NewGuid():N}"[..12], "Support workflow fixture", DateTimeOffset.UtcNow);
        var attachment = EncryptedObjectRecord.Create(Guid.NewGuid(), shop.Id, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeSupportAttachment, 32, "application/octet-stream", new string('a', 64),
            $"fixtures/{Guid.NewGuid():N}.bin", Guid.NewGuid(), null, DateTimeOffset.UtcNow);

        Guid ticketId;
        Guid adminId = admin.Id;
        await using (var db = CreateDb(databaseName, root))
        {
            db.AdminUsers.Add(admin);
            db.Shops.Add(shop);
            db.EncryptedObjects.Add(attachment);
            await db.SaveChangesAsync();
            var controller = CreateController(db, serializer, adminId);
            var createPayload = JsonSerializer.SerializeToElement(new
            {
                shopId = shop.Id,
                subject = "Backup upload issue",
                summary = "The encrypted upload stopped before completion.",
                attachmentObjectIds = new[] { attachment.Id },
                redactionProfileVersion = 1
            });
            var created = Assert.IsType<JsonResult>(await controller.Create(
                Envelope(serializer, "support.create", createPayload, 0), CancellationToken.None)).Value as OutcomeDto;
            Assert.NotNull(created);
            Assert.Equal("SUCCEEDED", created.State);
            var resultReference = Assert.IsType<string>(created.ResultReference);
            Assert.StartsWith("support/tickets/", resultReference, StringComparison.Ordinal);
            ticketId = Guid.Parse(resultReference["support/tickets/".Length..]);
            Assert.Single(db.CentralOutboxMessages.Where(x => x.EventType == "support.ticket_created"));
        }

        await using (var db = CreateDb(databaseName, root))
        {
            var persisted = await db.SupportTickets.SingleAsync(x => x.Id == ticketId);
            Assert.Equal(shop.Id, persisted.ShopId);
            Assert.Equal(adminId, persisted.RequesterPrincipalId);
            Assert.Equal(new[] { attachment.Id }, JsonSerializer.Deserialize<Guid[]>(persisted.AttachmentObjectIdsJson));
            Assert.Equal("[]", persisted.RepliesJson);

            var controller = CreateController(db, serializer, adminId);
            var replyPayload = JsonSerializer.SerializeToElement(new
            {
                ticketId,
                action = "REPLY",
                replyText = "We received the diagnostic attachment and recorded your request."
            });
            var reply = Assert.IsType<JsonResult>(await controller.Command(ticketId,
                Envelope(serializer, "support.reply", replyPayload, persisted.RowVersion), CancellationToken.None)).Value as OutcomeDto;
            Assert.NotNull(reply);
            Assert.Equal("SUCCEEDED", reply.State);
        }

        await using (var reopened = CreateDb(databaseName, root))
        {
            var ticket = await reopened.SupportTickets.SingleAsync(x => x.Id == ticketId);
            var history = JsonSerializer.Deserialize<SupportReplyDto[]>(ticket.RepliesJson);
            var savedReply = Assert.Single(history!);
            Assert.Equal(SupportReplyDto.KindMessage, savedReply.Kind);
            Assert.Equal(adminId.ToString("D"), savedReply.Author);
            Assert.Equal("We received the diagnostic attachment and recorded your request.", savedReply.Text);
            Assert.Equal(2, ticket.RowVersion);
            Assert.Contains(reopened.CentralOutboxMessages, x => x.EventType == "support.ticket_replied");
            Assert.Equal(2, reopened.CentralAuditRecords.Count(x =>
                x.Resource == nameof(SupportTicket) && x.ResourceId == ticketId.ToString("D") && x.IsSuccess));
            Assert.Empty(reopened.CentralConsumerEffects);
        }
    }

    [Fact]
    public async Task Ticket_creation_rejects_attachment_owned_by_another_shop_without_creating_ticket()
    {
        var root = new InMemoryDatabaseRoot();
        var databaseName = $"central-support-cross-shop-{Guid.NewGuid():N}";
        var serializer = new CanonicalJsonSerializer();
        await using var db = CreateDb(databaseName, root);
        var shopA = Shop.Create(Guid.NewGuid(), $"SA_{Guid.NewGuid():N}"[..12], "Shop A", DateTimeOffset.UtcNow);
        var shopB = Shop.Create(Guid.NewGuid(), $"SB_{Guid.NewGuid():N}"[..12], "Shop B", DateTimeOffset.UtcNow);
        var attachment = EncryptedObjectRecord.Create(Guid.NewGuid(), shopB.Id, Guid.NewGuid(),
            EncryptedObjectRecord.PurposeSupportAttachment, 12, "application/octet-stream", new string('b', 64),
            $"fixtures/{Guid.NewGuid():N}.bin", Guid.NewGuid(), null, DateTimeOffset.UtcNow);
        db.Shops.AddRange(shopA, shopB);
        db.EncryptedObjects.Add(attachment);
        await db.SaveChangesAsync();
        var controller = CreateController(db, serializer, Guid.NewGuid());
        var payload = JsonSerializer.SerializeToElement(new
        {
            shopId = shopA.Id,
            subject = "Cross-shop attachment attempt",
            summary = "This attachment belongs to a different persisted shop.",
            attachmentObjectIds = new[] { attachment.Id },
            redactionProfileVersion = 1
        });

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.Create(Envelope(serializer, "support.create", payload, 0), CancellationToken.None));

        Assert.Equal("SUPPORT_ATTACHMENT_ACCESS_DENIED", error.Code);
        Assert.Empty(db.SupportTickets);
        Assert.Empty(db.CentralAuditRecords);
        Assert.Empty(db.CentralOutboxMessages.Where(x => x.EventType == "support.ticket_created"));
    }

    private static CentralDbContext CreateDb(string name, InMemoryDatabaseRoot root)
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(name, root)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new CentralDbContext(options);
    }

    private static CanonicalSupportCommandsController CreateController(CentralDbContext db, CanonicalJsonSerializer serializer, Guid adminId)
    {
        var controller = new CanonicalSupportCommandsController(db, new OperationLedgerService(db, serializer),
            new CentralAuditService(db), serializer);
        var context = new DefaultHttpContext();
        context.Items["AdminSession"] = new AdminSessionDto { AdminUserId = adminId, SessionId = Guid.NewGuid() };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    private static JsonElement Envelope(CanonicalJsonSerializer serializer, string operationType, JsonElement payload, int expectedVersion)
    {
        return JsonSerializer.SerializeToElement(new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(), operationType, fingerprintVersion = 1,
                payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion, policyVersion = 0, reason = "synthetic Central support workflow fixture"
            },
            payload
        });
    }
}
