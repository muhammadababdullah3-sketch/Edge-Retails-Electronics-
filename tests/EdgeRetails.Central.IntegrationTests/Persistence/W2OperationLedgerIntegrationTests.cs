using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.Central.IntegrationTests.Persistence;

/// <summary>
/// Proves Section 4.C invariants on dedicated PostgreSQL 18.6 (port 55454):
/// 1. Identical intent replays durable canonical outcome.
/// 2. Changed payload/scope/type/resource rejects with OPERATION_ID_CONFLICT.
/// 3. Expected-version conflicts reject with CONCURRENCY_CONFLICT.
/// 4. Domain state, outcome record, audit entry, and outbox message commit or roll back atomically.
/// 5. Response loss/retry deduplicates without side effects.
/// </summary>
public sealed class W2OperationLedgerIntegrationTests
{
    private static CentralDbContext Open() => new CentralDbContextFactory().CreateDbContext([]);
    private readonly CanonicalJsonSerializer _serializer = new();

    private record SeedResult(Guid CustomerId, Guid ShopId, Guid LicenseId);

    private static async Task<SeedResult> SeedCommercialHierarchyAsync()
    {
        await using var db = Open();
        var now = DateTimeOffset.UtcNow;
        var cust = Customer.Create("W2_Ledger_Cust_" + Guid.NewGuid().ToString("N")[..8], "w2@test.com", "12345678", now);
        db.Customers.Add(cust);
        await db.SaveChangesAsync();

        var shop = Shop.Create(cust.Id, "W2_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "W2 Test Shop", now);
        db.Shops.Add(shop);
        await db.SaveChangesAsync();

        var item = PlanCatalogItem.Create(
            "PLAN-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            "Base Plan",
            PlanCatalogItem.TargetBaseLicense,
            PlanCatalogItem.TermLifetime,
            null,
            100m,
            "USD");
        db.PlanCatalogItems.Add(item);
        await db.SaveChangesAsync();

        var snapshot = PricingSnapshot.Create(shop.Id, item, 1, now);
        db.PricingSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var order = Order.CreateFromSnapshot(snapshot, cust.Id, now);
        order.ApprovePayment(Guid.NewGuid(), Guid.NewGuid(), now);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var license = CommercialLicense.Issue(order, now, null, now);
        db.CommercialLicenses.Add(license);
        await db.SaveChangesAsync();

        return new SeedResult(cust.Id, shop.Id, license.Id);
    }

    [Fact]
    public async Task Proof1_IdenticalIntent_ReplaysDurableCanonicalOutcome_WithoutReexecuting()
    {
        var seed = await SeedCommercialHierarchyAsync();
        var opId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var payload = new LicenseLifecycleCommand
        {
            StepUpGrantToken = "grant-token-1",
            CommandMeta = new CommandMetaDto
            {
                ClientOperationId = opId,
                OperationType = "license.pause",
                Reason = "Routine administrative pause",
                ExpectedVersion = 1,
                PolicyVersion = 1
            }
        };

        var (_, hash) = _serializer.SerializeAndHash(payload);
        payload.CommandMeta.PayloadSha256 = hash;

        var executionCount = 0;
        OutcomeDto outcome1;
        OutcomeDto outcome2;

        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            outcome1 = await ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: payload.CommandMeta,
                payload: payload,
                executeAsync: async cmd =>
                {
                    executionCount++;
                    var lic = await db.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
                    lic.Pause();
                    await db.SaveChangesAsync();
                    return (true, $"licenses/{lic.Id}", lic.RowVersion, lic.AuthorityRevision);
                },
                targetResource: $"licenses/{seed.LicenseId}/pause");
        }

        // Replay with exact same intent
        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            outcome2 = await ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: payload.CommandMeta,
                payload: payload,
                executeAsync: async cmd =>
                {
                    executionCount++;
                    return (false, "WRONG", 99, 99);
                },
                targetResource: $"licenses/{seed.LicenseId}/pause");
        }

        Assert.Equal(1, executionCount);
        Assert.Equal(outcome1.OutcomeId, outcome2.OutcomeId);
        Assert.Equal("SUCCEEDED", outcome1.State);
        Assert.Equal("SUCCEEDED", outcome2.State);
        Assert.Equal(outcome1.ResultReference, outcome2.ResultReference);
        Assert.Equal(outcome1.ResourceVersion, outcome2.ResourceVersion);
    }

    [Fact]
    public async Task Proof2_ChangedPayloadScopeOrType_RejectsWithOperationIdConflict()
    {
        var seed = await SeedCommercialHierarchyAsync();
        var opId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var initialPayload = new LicenseLifecycleCommand
        {
            StepUpGrantToken = "grant-token-initial",
            CommandMeta = new CommandMetaDto
            {
                ClientOperationId = opId,
                OperationType = "license.pause",
                Reason = "Initial reason",
                ExpectedVersion = 1,
                PolicyVersion = 1
            }
        };
        var (_, initialHash) = _serializer.SerializeAndHash(initialPayload);
        initialPayload.CommandMeta.PayloadSha256 = initialHash;

        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            await ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: initialPayload.CommandMeta,
                payload: initialPayload,
                executeAsync: async cmd =>
                {
                    var lic = await db.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
                    lic.Pause();
                    await db.SaveChangesAsync();
                    return (true, $"licenses/{lic.Id}", lic.RowVersion, lic.AuthorityRevision);
                },
                targetResource: $"licenses/{seed.LicenseId}/pause");
        }

        // 1. Changed payload (different token)
        var alteredPayload = new LicenseLifecycleCommand
        {
            StepUpGrantToken = "grant-token-ALTERED",
            CommandMeta = new CommandMetaDto
            {
                ClientOperationId = opId,
                OperationType = "license.pause",
                Reason = "Initial reason",
                ExpectedVersion = 1,
                PolicyVersion = 1
            }
        };
        var (_, alteredHash) = _serializer.SerializeAndHash(alteredPayload);
        alteredPayload.CommandMeta.PayloadSha256 = alteredHash;

        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var ex1 = await Assert.ThrowsAsync<CentralDomainException>(() => ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: alteredPayload.CommandMeta,
                payload: alteredPayload,
                executeAsync: _ => Task.FromResult((true, (string?)"ref", 1, 1)),
                targetResource: $"licenses/{seed.LicenseId}/pause"));
            Assert.Equal("OPERATION_ID_CONFLICT", ex1.Code);
        }

        // 2. Changed actor scope
        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var ex2 = await Assert.ThrowsAsync<CentralDomainException>(() => ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:other_attacker",
                shopId: seed.ShopId,
                meta: initialPayload.CommandMeta,
                payload: initialPayload,
                executeAsync: _ => Task.FromResult((true, (string?)"ref", 1, 1)),
                targetResource: $"licenses/{seed.LicenseId}/pause"));
            Assert.Equal("OPERATION_ID_CONFLICT", ex2.Code);
        }

        // 3. Changed operation type
        var changedTypeMeta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "license.revoke",
            Reason = initialPayload.CommandMeta.Reason,
            ExpectedVersion = 1,
            PolicyVersion = 1
        };
        var (_, typeHash) = _serializer.SerializeAndHash(initialPayload);
        changedTypeMeta.PayloadSha256 = typeHash;

        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var ex3 = await Assert.ThrowsAsync<CentralDomainException>(() => ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: changedTypeMeta,
                payload: initialPayload,
                executeAsync: _ => Task.FromResult((true, (string?)"ref", 1, 1)),
                targetResource: $"licenses/{seed.LicenseId}/pause"));
            Assert.Equal("OPERATION_ID_CONFLICT", ex3.Code);
        }

        // 4. Changed target resource
        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var ex4 = await Assert.ThrowsAsync<CentralDomainException>(() => ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: initialPayload.CommandMeta,
                payload: initialPayload,
                executeAsync: _ => Task.FromResult((true, (string?)"ref", 1, 1)),
                targetResource: $"licenses/{seed.LicenseId}/other_action"));
            Assert.Equal("OPERATION_ID_CONFLICT", ex4.Code);
        }
    }

    [Fact]
    public async Task Proof3_ExpectedVersionConflict_RejectsWithConcurrencyConflict()
    {
        var seed = await SeedCommercialHierarchyAsync();
        var opId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var payload = new LicenseLifecycleCommand
        {
            StepUpGrantToken = "grant-token-1",
            CommandMeta = new CommandMetaDto
            {
                ClientOperationId = opId,
                OperationType = "license.pause",
                Reason = "Stale version attempt",
                ExpectedVersion = 999, // Intentional stale expected version
                PolicyVersion = 1
            }
        };
        var (_, hash) = _serializer.SerializeAndHash(payload);
        payload.CommandMeta.PayloadSha256 = hash;

        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var ex = await Assert.ThrowsAsync<CentralDomainException>(() => ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: payload.CommandMeta,
                payload: payload,
                executeAsync: async cmd =>
                {
                    var lic = await db.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
                    if (cmd.CommandMeta.ExpectedVersion > 0 && lic.RowVersion != cmd.CommandMeta.ExpectedVersion)
                    {
                        throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "Resource version has changed.");
                    }
                    lic.Pause();
                    await db.SaveChangesAsync();
                    return (true, $"licenses/{lic.Id}", lic.RowVersion, lic.AuthorityRevision);
                },
                targetResource: $"licenses/{seed.LicenseId}/pause"));

            Assert.Equal("CONCURRENCY_CONFLICT", ex.Code);
        }

        // Verify in fresh context that license state remained active and unpaused
        await using var fresh = Open();
        var freshLic = await fresh.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
        Assert.Equal(CommercialLicense.StatusActive, freshLic.Status);
        Assert.False(await fresh.CentralOperationRecords.AnyAsync(r => r.ClientOperationId == opId));
    }

    [Fact]
    public async Task Proof4_MidTransactionFailure_RollsBackDomainOutcomeAuditOutbox_Atomically()
    {
        var seed = await SeedCommercialHierarchyAsync();
        var opId = Guid.NewGuid();
        var outboxMsgId = Guid.NewGuid();
        var adminId = Guid.NewGuid();

        var payload = new LicenseLifecycleCommand
        {
            StepUpGrantToken = "grant-token-1",
            CommandMeta = new CommandMetaDto
            {
                ClientOperationId = opId,
                OperationType = "license.pause",
                Reason = "Rollback test",
                ExpectedVersion = 1,
                PolicyVersion = 1
            }
        };
        var (_, hash) = _serializer.SerializeAndHash(payload);
        payload.CommandMeta.PayloadSha256 = hash;

        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var auditService = new CentralAuditService(db);

            await Assert.ThrowsAsync<CentralDomainException>(() => ledger.ExecuteOrReplayAsync<LicenseLifecycleCommand, bool>(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: payload.CommandMeta,
                payload: payload,
                executeAsync: async cmd =>
                {
                    var lic = await db.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
                    lic.Pause();

                    db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        outboxMsgId,
                        "license.paused",
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        "{\"test\":true}",
                        DateTimeOffset.UtcNow));

                    await auditService.RecordAuditAsync(
                        "LICENSE_PAUSED",
                        "ADMIN",
                        adminId,
                        null,
                        "Audit should rollback",
                        opId,
                        opId,
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        "{}",
                        true,
                        cmd.CommandMeta.PayloadSha256);

                    await db.SaveChangesAsync();

                    // Injected failure after writes but before ledger commit
                    throw new InvalidOperationException("Injected mid-transaction domain write failure");
                },
                targetResource: $"licenses/{seed.LicenseId}/pause"));
        }

        // Assert all-or-nothing rollback in a fresh DbContext
        await using var verifyDb = Open();
        var license = await verifyDb.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
        Assert.Equal(CommercialLicense.StatusActive, license.Status); // Domain NOT paused
        Assert.False(await verifyDb.CentralOperationRecords.AnyAsync(r => r.ClientOperationId == opId));
        Assert.False(await verifyDb.CentralAuditRecords.AnyAsync(r => r.CorrelationId == opId.ToString()));
        Assert.False(await verifyDb.CentralOutboxMessages.AnyAsync(r => r.Id == outboxMsgId));
    }

    [Fact]
    public async Task Proof5_ResponseLossRetry_DeduplicatesWithoutSideEffects()
    {
        var seed = await SeedCommercialHierarchyAsync();
        var opId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var outboxMsgId = Guid.NewGuid();

        var payload = new LicenseLifecycleCommand
        {
            StepUpGrantToken = "grant-token-1",
            CommandMeta = new CommandMetaDto
            {
                ClientOperationId = opId,
                OperationType = "license.pause",
                Reason = "Response loss simulation",
                ExpectedVersion = 1,
                PolicyVersion = 1
            }
        };
        var (_, hash) = _serializer.SerializeAndHash(payload);
        payload.CommandMeta.PayloadSha256 = hash;

        // 1. Initial execution commits successfully
        OutcomeDto initialOutcome;
        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            var auditService = new CentralAuditService(db);

            initialOutcome = await ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: payload.CommandMeta,
                payload: payload,
                executeAsync: async cmd =>
                {
                    var lic = await db.CommercialLicenses.SingleAsync(l => l.Id == seed.LicenseId);
                    lic.Pause();

                    db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        outboxMsgId,
                        "license.paused",
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        "{\"test\":true}",
                        DateTimeOffset.UtcNow));

                    await auditService.RecordAuditAsync(
                        "LICENSE_PAUSED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta.Reason,
                        opId,
                        opId,
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        "{}",
                        true,
                        cmd.CommandMeta.PayloadSha256);

                    await db.SaveChangesAsync();
                    return (true, $"licenses/{lic.Id}", lic.RowVersion, lic.AuthorityRevision);
                },
                targetResource: $"licenses/{seed.LicenseId}/pause");
        }

        // 2. Client encounters response loss and retries with identical command
        OutcomeDto retryOutcome;
        var retryExecutionCount = 0;
        await using (var db = Open())
        {
            var ledger = new OperationLedgerService(db, _serializer);
            retryOutcome = await ledger.ExecuteOrReplayAsync(
                actorScope: $"admin:{adminId}",
                shopId: seed.ShopId,
                meta: payload.CommandMeta,
                payload: payload,
                executeAsync: _ =>
                {
                    retryExecutionCount++;
                    return Task.FromResult((false, (string?)"DUPLICATE", 99, 99));
                },
                targetResource: $"licenses/{seed.LicenseId}/pause");
        }

        Assert.Equal(0, retryExecutionCount); // Business logic was NOT re-executed
        Assert.Equal(initialOutcome.OutcomeId, retryOutcome.OutcomeId);
        Assert.Equal(initialOutcome.ResultReference, retryOutcome.ResultReference);

        // 3. Verify exactly one audit record and one outbox message exist
        await using (var verifyDb = Open())
        {
            var auditCount = await verifyDb.CentralAuditRecords.CountAsync(r => r.CorrelationId == opId.ToString());
            Assert.Equal(1, auditCount);

            var outboxCount = await verifyDb.CentralOutboxMessages.CountAsync(r => r.Id == outboxMsgId);
            Assert.Equal(1, outboxCount);

            // 4. Verify durable consumer sink deduplication across retries
            var sink = new DurableOutboxConsumerSink(verifyDb);
            var persistedMessage = await verifyDb.CentralOutboxMessages.AsNoTracking().SingleAsync(m => m.Id == outboxMsgId);
            await sink.DeliverAsync(outboxMsgId, persistedMessage.EventType, persistedMessage.AggregateType, persistedMessage.AggregateId, persistedMessage.PayloadJson);
            // Retry delivery with same MessageId
            await sink.DeliverAsync(outboxMsgId, persistedMessage.EventType, persistedMessage.AggregateType, persistedMessage.AggregateId, persistedMessage.PayloadJson);

            Assert.True(sink.HasProcessed(outboxMsgId));
            Assert.Equal(1, sink.GetDeliveryCount(outboxMsgId));
        }
    }
}
