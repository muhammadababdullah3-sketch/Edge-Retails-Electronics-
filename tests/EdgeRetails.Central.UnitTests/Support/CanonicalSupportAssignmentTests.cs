using System.Text.Json;
using EdgeRetails.Central.Api.Controllers;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Support;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EdgeRetails.Central.UnitTests.Support;

public sealed class CanonicalSupportAssignmentTests
{
    [Fact]
    public async Task Assign_rechecks_persisted_assignee_activity_inside_ledger_execution()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CentralDbContext(options);
        var inactiveAdmin = AdminUser.Create("inactive-assignee", "hash", "salt", null, false, DateTimeOffset.UtcNow);
        inactiveAdmin.SetActive(false);
        var ticket = SupportTicket.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Subject", "Summary", [], 1, DateTimeOffset.UtcNow);
        db.AdminUsers.Add(inactiveAdmin);
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync();

        var serializer = new CanonicalJsonSerializer();
        var payload = JsonSerializer.SerializeToElement(new
        {
            ticketId = ticket.Id,
            action = "ASSIGN",
            assigneeId = inactiveAdmin.Id
        });
        var envelope = JsonSerializer.SerializeToElement(new
        {
            meta = new
            {
                clientOperationId = Guid.NewGuid(),
                operationType = "support.reply",
                fingerprintVersion = 1,
                payloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex,
                expectedVersion = ticket.RowVersion,
                policyVersion = 0,
                reason = "synthetic assignment regression"
            },
            payload
        });
        var controller = new CanonicalSupportCommandsController(db,
            new OperationLedgerService(db, serializer), new CentralAuditService(db), serializer);
        var context = new DefaultHttpContext();
        context.Items["AdminSession"] = new AdminSessionDto { AdminUserId = Guid.NewGuid(), SessionId = Guid.NewGuid() };
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var error = await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(
            () => controller.Command(ticket.Id, envelope, CancellationToken.None));

        Assert.Equal("SUPPORT_ASSIGNEE_NOT_ACTIVE", error.Code);
        var unchanged = await db.SupportTickets.SingleAsync(x => x.Id == ticket.Id);
        Assert.Null(unchanged.AssignedAdminUserId);
        Assert.Equal(ticket.RowVersion, unchanged.RowVersion);
        Assert.Empty(db.CentralOutboxMessages);
        Assert.Empty(db.CentralAuditRecords);
    }
}
