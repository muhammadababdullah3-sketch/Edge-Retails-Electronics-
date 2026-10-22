using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace EdgeRetails.Central.IntegrationTests.Persistence;
public sealed class W1ClosurePersistenceTests
{
    private static CentralDbContext Open() => new CentralDbContextFactory().CreateDbContext([]);
    [Fact]
    public async Task Ledger_ConcurrentIdenticalRequests_ReturnOneCanonicalOutcomeAndOneEffect()
    {
        var userId = await Seed(); var op = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Invoke(userId, op, 1), Invoke(userId, op, 1));
        Assert.Equal(outcomes[0].OutcomeId, outcomes[1].OutcomeId);
        await using var db = Open();
        Assert.Equal(2, (await db.AdminUsers.SingleAsync(u => u.Id == userId)).RowVersion);
        Assert.Single(await db.CentralOperationRecords.Where(x => x.ClientOperationId == op).ToListAsync());
    }
    [Fact]
    public async Task Ledger_DistinctConcurrentOperations_SameVersion_OneWinnerOneConflict()
    {
        var userId = await Seed();
        async Task<bool> Attempt()
        {
            try { await Invoke(userId, Guid.NewGuid(), 1); return true; }
            catch (EdgeRetails.Central.Domain.Common.CentralDomainException e) { Assert.Equal("CONCURRENCY_CONFLICT", e.Code); return false; }
        }
        var wins = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(wins, x => x);
        await using var db = Open(); Assert.Equal(2, (await db.AdminUsers.SingleAsync(u => u.Id == userId)).RowVersion);
    }
    private static async Task<Guid> Seed()
    {
        await using var db = Open(); var u = AdminUser.Create("race_"+Guid.NewGuid().ToString("N"), "hash", "salt", null, false, DateTimeOffset.UtcNow);
        db.AdminUsers.Add(u); await db.SaveChangesAsync(); return u.Id;
    }
    private static async Task<OutcomeDto> Invoke(Guid userId, Guid operation, int version)
    {
        await using var db = Open(); var serializer = new CanonicalJsonSerializer();
        var p = new AdminActionCommandDto { AdminUserId=userId, Action="DISABLE" };
        var meta = new CommandMetaDto { ClientOperationId=operation, OperationType="admin.manage", PayloadSha256=serializer.SerializeAndHash(p).Sha256Hex, ExpectedVersion=version, Reason="race", PolicyVersion=1 };
        return await new OperationLedgerService(db,serializer).ExecuteOrReplayAsync<AdminActionCommandDto,bool>("race-actor",null,meta,p,async _ =>
        {
            var u=await db.AdminUsers.SingleAsync(x=>x.Id==userId);
            if (u.RowVersion != version)
            {
                throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "stale");
            }
            u.SetActive(false); return (true,"admin-users/"+userId,u.RowVersion,u.SecurityRevision);
        },targetResource:"admin-users/"+userId);
    }
    [Fact]
    public async Task DurableConsumer_AckLoss_NewContext_DeduplicatesRealEffect()
    {
        var id = Guid.NewGuid();
        await using (var first = Open())
        {
            first.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                id,
                "W1_TEST",
                "Admin",
                "test",
                "{}",
                DateTimeOffset.UtcNow));
            await first.SaveChangesAsync();
            await new DurableOutboxConsumerSink(first).DeliverAsync(id, "W1_TEST", "Admin", "test", "{}");
        }
        await using var restarted = Open();
        var consumer = new DurableOutboxConsumerSink(restarted);
        await consumer.DeliverAsync(id, "W1_TEST", "Admin", "test", "{}");
        Assert.True(consumer.HasProcessed(id)); Assert.Equal(1, consumer.GetDeliveryCount(id));
    }
    [Fact]
    public async Task RuntimeRole_DirectSql_AuditUpdateDeleteTruncate_Denied()
    {
        await using var db = Open();
        foreach (var sql in new[] { "UPDATE central.audit_records SET \"Reason\"='tamper'", "DELETE FROM central.audit_records", "TRUNCATE central.audit_records" })
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
        }
    }
    [Fact]
    public async Task Ledger_PostWriteFailure_RollsBackDomainOutcomeAuditOutbox_InFreshContext()
    {
        var userId = Guid.Empty; var operation = Guid.NewGuid(); var message = Guid.NewGuid();
        await using (var setup = Open())
        {
            var u = AdminUser.Create("rollback_" + Guid.NewGuid().ToString("N"), "hash", "salt", null, false, DateTimeOffset.UtcNow);
            setup.AdminUsers.Add(u); await setup.SaveChangesAsync(); userId = u.Id;
        }
        await using (var db = Open())
        {
            var payload = new AdminActionCommandDto { AdminUserId = userId, Action = "DISABLE" };
            var serializer = new CanonicalJsonSerializer();
            var meta = new CommandMetaDto { ClientOperationId = operation, OperationType = "admin.manage", PayloadSha256 = serializer.SerializeAndHash(payload).Sha256Hex, ExpectedVersion = 1, Reason = "test", PolicyVersion = 1 };
            await Assert.ThrowsAsync<EdgeRetails.Central.Domain.Common.CentralDomainException>(() => new OperationLedgerService(db, serializer).ExecuteOrReplayAsync<AdminActionCommandDto, bool>("test-actor", null, meta, payload, async cmd =>
            {
                var u = await db.AdminUsers.SingleAsync(x => x.Id == userId); u.SetActive(false);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(message, "W1_TEST", "Admin", userId.ToString(), "{}", DateTimeOffset.UtcNow));
                await new CentralAuditService(db).RecordAuditAsync("test.rollback", "test-actor", null, null, "test", operation, operation, "Admin", userId.ToString(), "{}");
                // Actually persisted within the transaction BEFORE failure.
                throw new InvalidOperationException("post-write injected failure");
            }, targetResource: "admin-users/" + userId));
        }
        await using var fresh = Open();
        Assert.True((await fresh.AdminUsers.SingleAsync(x => x.Id == userId)).IsActive);
        Assert.False(await fresh.CentralOperationRecords.AnyAsync(x => x.ClientOperationId == operation));
        Assert.False(await fresh.CentralAuditRecords.AnyAsync(x => x.CorrelationId == operation.ToString()));
        Assert.False(await fresh.CentralOutboxMessages.AnyAsync(x => x.Id == message));
    }
}
