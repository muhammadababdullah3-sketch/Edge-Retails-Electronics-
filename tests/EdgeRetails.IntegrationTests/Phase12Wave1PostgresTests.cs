using System.Collections.Concurrent;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Exceptions;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Production;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase12Wave1PostgresTests
{
    private sealed class NoOpMaintenanceBarrier : IProductionMaintenanceBarrier
    {
        public Task<IProductionMaintenanceLease> EnterExclusiveAsync(ProductionMaintenanceState state, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<ProductionMaintenanceState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ProductionMaintenanceState.Normal);
    }

    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DataSources = new();

    private static string GetConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var builtConn = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(builtConn, performLiveProbe: false);
                return builtConn;
            }
        }

        throw new InvalidOperationException("FAIL-CLOSED: Isolated test connection is not configured.");
    }

    private static NpgsqlDataSource GetOrCreateDataSource(string connectionString)
    {
        return DataSources.GetOrAdd(connectionString, connStr =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
            dataSourceBuilder.MapEnum<PackState>("inventory.pack_state", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            return dataSourceBuilder.Build();
        });
    }

    private static EdgeRetailsDbContext CreateDbContext(string connectionString)
    {
        Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connectionString, performLiveProbe: true);
        var dataSource = GetOrCreateDataSource(connectionString);

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(EdgeRetailsDbContext).Assembly.FullName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "system");
            })
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        return new EdgeRetailsDbContext(options);
    }

    [Fact]
    public async Task Postgres_ConcurrentClaims_ExactlyOneAcquiresSlot_NineAreInFlight()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        const int concurrentClients = 10;

        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new ConcurrentBag<OperationClaimResult>();

        var tasks = Enumerable.Range(0, concurrentClients).Select(async i =>
        {
            await using var db = CreateDbContext(connStr);
            var ledger = new EfOperationOutcomeLedger(db);

            await startSignal.Task;
            var result = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);
            results.Add(result);
        }).ToArray();

        // Release all 10 threads simultaneously to race at the PostgreSQL engine level
        startSignal.SetResult();
        await Task.WhenAll(tasks);

        var acquiredCount = results.Count(x => x.Status == OperationClaimStatus.Acquired);
        var inFlightCount = results.Count(x => x.Status == OperationClaimStatus.InFlight);

        Assert.Equal(1, acquiredCount);
        Assert.Equal(concurrentClients - 1, inFlightCount);

        // Verify only 1 row was created in PostgreSQL
        await using var verifyDb = CreateDbContext(connStr);
        var outcomes = await verifyDb.OperationOutcomes
            .Where(x => x.ClientOperationId == clientOpId)
            .ToListAsync();

        Assert.Single(outcomes);
        Assert.Equal(fingerprint, outcomes[0].PayloadFingerprint);
        Assert.Equal("Pending", outcomes[0].Status.ToString());
    }

    [Fact]
    public async Task Postgres_ClaimReplay_ReturnsCachedResponsePayloadAndStatusCode()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var entityId = Guid.NewGuid();
        var responsePayload = "{\"saleId\": \"" + entityId + "\", \"invoiceNumber\": \"INV-TEST-001\", \"grandTotal\": 2500.50}";

        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claimResult = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);
            Assert.Equal(OperationClaimStatus.Acquired, claimResult.Status);

            await ledger.RecordSuccessWithResponseAsync(
                clientOpId,
                "Sale",
                entityId,
                responsePayload,
                responseStatusCode: 200,
                responseContentType: "application/json",
                documentNumber: "INV-TEST-001",
                payloadFingerprint: fingerprint);
        }

        // Replay claim from a different DbContext / connection
        await using (var replayDb = CreateDbContext(connStr))
        {
            var replayLedger = new EfOperationOutcomeLedger(replayDb);
            var replayResult = await replayLedger.ClaimAsync(clientOpId, "Sale", fingerprint);

            Assert.Equal(OperationClaimStatus.AlreadySucceeded, replayResult.Status);
            Assert.NotNull(replayResult.Outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, replayResult.Outcome.State);
            Assert.NotNull(replayResult.Outcome.ResponsePayload);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(responsePayload),
                System.Text.Json.Nodes.JsonNode.Parse(replayResult.Outcome.ResponsePayload!)));
            Assert.Equal(200, replayResult.Outcome.ResponseStatusCode);
            Assert.Equal("application/json", replayResult.Outcome.ResponseContentType);
            Assert.Equal("INV-TEST-001", replayResult.Outcome.DocumentNumber);
        }
    }

    [Fact]
    public async Task Postgres_FailClosedTamperDetection_ThrowsIdempotencyPayloadMismatchException()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var originalFingerprint = "sha256:aaaa" + Guid.NewGuid().ToString("N");
        var tamperedFingerprint = "sha256:bbbb" + Guid.NewGuid().ToString("N");

        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", originalFingerprint);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);
        }

        // Adversary or replay attempts same ClientOperationId with divergent payload hash
        await using (var attackDb = CreateDbContext(connStr))
        {
            var attackLedger = new EfOperationOutcomeLedger(attackDb);
            var ex = await Assert.ThrowsAsync<IdempotencyPayloadMismatchException>(async () =>
            {
                await attackLedger.ClaimAsync(clientOpId, "Sale", tamperedFingerprint);
            });

            Assert.Equal("idempotency.payload_mismatch", ex.Code);
            Assert.Equal(clientOpId, ex.ClientOperationId);
            Assert.Equal(originalFingerprint, ex.ExistingFingerprint);
            Assert.Equal(tamperedFingerprint, ex.IncomingFingerprint);
        }
    }

    [Fact]
    public async Task Postgres_RecordFailureWithResponse_StoresFailureAndReplaysAlreadyFailed()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:" + Guid.NewGuid().ToString("N");
        var failurePayload = "{\"code\": \"sales.credit_limit_exceeded\", \"message\": \"Customer khata limit exceeded\"}";

        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);

            await ledger.RecordFailureWithResponseAsync(
                clientOpId,
                "Sale",
                "sales.credit_limit_exceeded",
                "Customer khata limit exceeded",
                responsePayload: failurePayload,
                responseStatusCode: 400,
                responseContentType: "application/json",
                payloadFingerprint: fingerprint);
        }

        // Replay claim after failure
        await using (var replayDb = CreateDbContext(connStr))
        {
            var replayLedger = new EfOperationOutcomeLedger(replayDb);
            var replayResult = await replayLedger.ClaimAsync(clientOpId, "Sale", fingerprint);

            Assert.Equal(OperationClaimStatus.AlreadyFailed, replayResult.Status);
            Assert.NotNull(replayResult.Outcome);
            Assert.Equal(OperationOutcomeState.Failed, replayResult.Outcome.State);
            Assert.Equal("sales.credit_limit_exceeded", replayResult.Outcome.ErrorCode);
            Assert.NotNull(replayResult.Outcome.ResponsePayload);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(failurePayload),
                System.Text.Json.Nodes.JsonNode.Parse(replayResult.Outcome.ResponsePayload!)));
            Assert.Equal(400, replayResult.Outcome.ResponseStatusCode);
        }
    }

    [Fact]
    public async Task Postgres_FailClosedOperationTypeMismatch_ThrowsIdempotencyOperationTypeMismatchException()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:cccc" + Guid.NewGuid().ToString("N");

        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);
        }

        // Adversary or misconfigured caller attempts same ClientOperationId with divergent OperationType
        await using (var attackDb = CreateDbContext(connStr))
        {
            var attackLedger = new EfOperationOutcomeLedger(attackDb);
            var ex = await Assert.ThrowsAsync<IdempotencyOperationTypeMismatchException>(async () =>
            {
                await attackLedger.ClaimAsync(clientOpId, "Purchase", fingerprint);
            });

            Assert.Equal("idempotency.operation_type_mismatch", ex.Code);
            Assert.Equal(clientOpId, ex.ClientOperationId);
            Assert.Equal("Sale", ex.ExistingOperationType);
            Assert.Equal("Purchase", ex.IncomingOperationType);
        }
    }

    [Fact]
    public async Task Postgres_LegacyBackfillSentinel_ReplayAndTamperFailClosed_HistoricalReadSucceeds()
    {
        var connStr = GetConnectionString();
        var legacyOpId = Guid.NewGuid();
        var realIncomingFingerprint = "sha256:dddd" + Guid.NewGuid().ToString("N");

        // Seed a legacy outcome with the authoritative backfill sentinel
        await using (var seedDb = CreateDbContext(connStr))
        {
            var legacyOutcome = new OperationOutcome
            {
                Id = Guid.CreateVersion7(),
                ClientOperationId = legacyOpId,
                OperationType = "Sale",
                PayloadFingerprint = "sha256:legacy_backfill",
                Status = OperationOutcomeStatus.Succeeded,
                ResultEntityType = "Sale",
                ResultEntityId = Guid.NewGuid(),
                DocumentNumber = "INV-LEGACY-001",
                WasCommitted = true,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                CompletedAt = DateTimeOffset.UtcNow.AddDays(-30)
            };
            seedDb.OperationOutcomes.Add(legacyOutcome);
            await seedDb.SaveChangesAsync();
        }

        // 1. Historical read succeeds
        await using (var readDb = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(readDb);
            var outcome = await ledger.GetOutcomeAsync(legacyOpId);

            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal("sha256:legacy_backfill", outcome.PayloadFingerprint);
            Assert.Equal("INV-LEGACY-001", outcome.DocumentNumber);
        }

        // 2. Replay attempt with real 64-hex SHA-256 fails closed (tamper rejection against sentinel)
        await using (var replayDb = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(replayDb);
            var ex = await Assert.ThrowsAsync<IdempotencyPayloadMismatchException>(async () =>
            {
                await ledger.ClaimAsync(legacyOpId, "Sale", realIncomingFingerprint);
            });

            Assert.Equal("idempotency.payload_mismatch", ex.Code);
            Assert.Equal(legacyOpId, ex.ClientOperationId);
            Assert.Equal("sha256:legacy_backfill", ex.ExistingFingerprint);
            Assert.Equal(realIncomingFingerprint, ex.IncomingFingerprint);
        }

        // 3. Verify zero mutation occurred in PostgreSQL
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var record = await verifyDb.OperationOutcomes
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClientOperationId == legacyOpId);

            Assert.NotNull(record);
            Assert.Equal("sha256:legacy_backfill", record.PayloadFingerprint);
            Assert.Equal(OperationOutcomeStatus.Succeeded, record.Status);
        }
    }

    [Fact]
    public async Task Postgres_SharedTransaction_CommitAtomically_ClaimsMutationsAndCachedResponse()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:eeee" + Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var companyCode = ("C" + Guid.NewGuid().ToString("N")[..9]).ToUpperInvariant();
        var cachedResponse = "{\"companyId\": \"" + companyId + "\", \"code\": \"" + companyCode + "\", \"status\": \"Active\"}";

        await using (var db = CreateDbContext(connStr))
        {
            var guard = new ProductionMaintenanceWriteGuard(new NoOpMaintenanceBarrier(), null);
            var txRunner = new EfTransactionRunner(db, guard);
            var ledger = new EfOperationOutcomeLedger(db);

            await txRunner.ExecuteAsync(async ct =>
            {
                // Step 1: Claim within transaction boundary
                var claim = await ledger.ClaimAsync(clientOpId, "CompanySetup", fingerprint, cancellationToken: ct);
                Assert.Equal(OperationClaimStatus.Acquired, claim.Status);

                // Step 2: Mutate business entities in same DbContext / transaction
                var company = new Company
                {
                    Id = companyId,
                    Name = "Enterprise Global " + companyCode,
                    Code = companyCode,
                    IsActive = true
                };
                db.Companies.Add(company);
                await db.SaveChangesAsync(ct);

                // Step 3: Record success with cached response in same transaction
                await ledger.RecordSuccessWithResponseAsync(
                    clientOpId,
                    "CompanySetup",
                    companyId,
                    responsePayload: cachedResponse,
                    responseStatusCode: 201,
                    responseContentType: "application/json",
                    documentNumber: companyCode,
                    payloadFingerprint: fingerprint,
                    cancellationToken: ct);

                return true;
            }, CancellationToken.None);
        }

        // Step 4: Verify durable commit across separate DbContext
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var company = await verifyDb.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == companyId);
            Assert.NotNull(company);
            Assert.Equal(companyCode, company.Code);

            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.True(outcome.WasCommitted);
            Assert.Equal(201, outcome.ResponseStatusCode);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(cachedResponse),
                System.Text.Json.Nodes.JsonNode.Parse(outcome.ResponsePayload!)));

            // Replay claim: delivers cached response without re-executing business mutations
            var replayLedger = new EfOperationOutcomeLedger(verifyDb);
            var replayClaim = await replayLedger.ClaimAsync(clientOpId, "CompanySetup", fingerprint);
            Assert.Equal(OperationClaimStatus.AlreadySucceeded, replayClaim.Status);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(cachedResponse),
                System.Text.Json.Nodes.JsonNode.Parse(replayClaim.Outcome?.ResponsePayload!)));
        }
    }

    [Fact]
    public async Task Postgres_SharedTransaction_FailureInjectionRollsBack_NoOrphanedBusinessRecords_CleanRollback()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "sha256:ffff" + Guid.NewGuid().ToString("N");
        var companyId = Guid.NewGuid();
        var companyCode = ("F" + Guid.NewGuid().ToString("N")[..9]).ToUpperInvariant();

        await using (var db = CreateDbContext(connStr))
        {
            var guard = new ProductionMaintenanceWriteGuard(new NoOpMaintenanceBarrier(), null);
            var txRunner = new EfTransactionRunner(db, guard);
            var ledger = new EfOperationOutcomeLedger(db);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await txRunner.ExecuteAsync<bool>(async ct =>
                {
                    // Step 1: Claim within transaction boundary
                    var claim = await ledger.ClaimAsync(clientOpId, "CompanySetup", fingerprint, cancellationToken: ct);
                    Assert.Equal(OperationClaimStatus.Acquired, claim.Status);

                    // Step 2: Mutate business entity
                    var company = new Company
                    {
                        Id = companyId,
                        Name = "Orphan Candidate " + companyCode,
                        Code = companyCode,
                        IsActive = true
                    };
                    db.Companies.Add(company);
                    await db.SaveChangesAsync(ct);

                    // Step 3: Inject failure before transaction commit
                    throw new InvalidOperationException("INJECTED_TRANSACTION_FAILURE_BEFORE_COMMIT");
                }, CancellationToken.None);
            });

            Assert.Equal("INJECTED_TRANSACTION_FAILURE_BEFORE_COMMIT", ex.Message);
        }

        // Step 4: Verify atomic rollback in PostgreSQL
        await using (var verifyDb = CreateDbContext(connStr))
        {
            // Business entity was completely rolled back: zero orphaned business records
            var company = await verifyDb.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == companyId);
            Assert.Null(company);

            // Operation outcome was rolled back cleanly: no stranded InFlight claim
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.Null(outcome);

            // Step 5: Verify retry succeeds cleanly and acquires the slot because claim was rolled back
            var retryLedger = new EfOperationOutcomeLedger(verifyDb);
            var retryClaim = await retryLedger.ClaimAsync(clientOpId, "CompanySetup", fingerprint);
            Assert.Equal(OperationClaimStatus.Acquired, retryClaim.Status);
        }
    }

    [Fact]
    public async Task Postgres_FailClosedActorContextMismatch_ThrowsIdempotencyContextMismatchException()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();

        // 1. Initial caller claims slot with Actor A
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);
        }

        // 2. Different actor attempts to claim same ClientOperationId -> throws IdempotencyContextMismatchException
        await using (var attackDb = CreateDbContext(connStr))
        {
            var attackLedger = new EfOperationOutcomeLedger(attackDb);
            var ex = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await attackLedger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorB);
            });

            Assert.Equal("idempotency.context_mismatch", ex.Code);
            Assert.Equal(clientOpId, ex.ClientOperationId);
            Assert.Equal("ActorUserId", ex.ContextKey);
            Assert.Equal(actorA.ToString(), ex.ExistingValue);
            Assert.Equal(actorB.ToString(), ex.IncomingValue);
        }

        // 3. Verify in PostgreSQL that ActorUserId remained actorA
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(actorA, outcome.ActorUserId);
            Assert.Equal(OperationOutcomeStatus.Pending, outcome.Status);
        }
    }

    [Fact]
    public async Task Postgres_RecordSuccess_EnforcesIdentityImmutability()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprintA = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var fingerprintB = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();
        var entityId = Guid.NewGuid();

        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprintA, actorId: actorA);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);

            // Attempt divergent operation type
            await Assert.ThrowsAsync<IdempotencyOperationTypeMismatchException>(async () =>
            {
                await ledger.RecordSuccessWithResponseAsync(
                    clientOpId,
                    "Purchase",
                    entityId,
                    "{}",
                    payloadFingerprint: fingerprintA,
                    actorId: actorA);
            });

            // Attempt divergent payload fingerprint
            await Assert.ThrowsAsync<IdempotencyPayloadMismatchException>(async () =>
            {
                await ledger.RecordSuccessWithResponseAsync(
                    clientOpId,
                    "Sale",
                    entityId,
                    "{}",
                    payloadFingerprint: fingerprintB,
                    actorId: actorA);
            });

            // Attempt divergent actor context
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordSuccessWithResponseAsync(
                    clientOpId,
                    "Sale",
                    entityId,
                    "{}",
                    payloadFingerprint: fingerprintA,
                    actorId: actorB);
            });
        }

        // Verify in PostgreSQL that status is still Pending, identities unaltered
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Pending, outcome.Status);
            Assert.Equal("Sale", outcome.OperationType);
            Assert.Equal(fingerprintA, outcome.PayloadFingerprint);
            Assert.Equal(actorA, outcome.ActorUserId);
        }
    }

    [Fact]
    public async Task Postgres_CompleteSale_ReplayReturnsAuthoritativeCachedOutcome()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
        var purchasing = scope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);

        // Stock product via purchase
        var purchaseOpId = Guid.NewGuid();
        var purchaseResult = await purchasing.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            $"INV-SUPP-{purchaseOpId:N}"[..16],
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Stock for Phase 12 sale replay test",
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            purchaseOpId,
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 20m, 80m, 120m, [])],
            0m), CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);

        var saleOpId = Guid.NewGuid();
        var customer = new Customer
        {
            Name = "P12 Replay Customer",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var saleCommand = new CompleteSaleCommand(
            saleOpId,
            customer.Id,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            300m,
            "BANK-REF",
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 150m, [])]);

        // 1. First execution succeeds and commits outcome + cached response to PostgreSQL
        var firstResult = await handler.HandleAsync(saleCommand, CancellationToken.None);
        Assert.True(firstResult.IsSuccess, $"First execution failed: {firstResult.Error?.Code} - {firstResult.Error?.Message}");
        Assert.NotNull(firstResult.Value);
        Assert.False(firstResult.Value.WasExisting);
        var originalSaleId = firstResult.Value.SaleId;
        var originalInvoiceNumber = firstResult.Value.InvoiceNumber;

        // Inspect PostgreSQL outcome row
        using (var verifyScope = provider.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == saleOpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.NotNull(outcome.ResponsePayload);
            Assert.StartsWith("v1:sha256:", outcome.PayloadFingerprint);
            Assert.Equal(fixture.ActorId, outcome.ActorUserId);
        }

        // 2. Replay with identical command: returns authoritative cached outcome
        using (var replayScope = provider.CreateScope())
        {
            var replayHandler = replayScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var replayResult = await replayHandler.HandleAsync(saleCommand, CancellationToken.None);

            Assert.True(replayResult.IsSuccess);
            Assert.NotNull(replayResult.Value);
            Assert.True(replayResult.Value.WasExisting);
            Assert.Equal(originalSaleId, replayResult.Value.SaleId);
            Assert.Equal(originalInvoiceNumber, replayResult.Value.InvoiceNumber);
            Assert.Equal(300m, replayResult.Value.GrandTotal);
        }

        // 3. Replay with altered meaningful field: fails closed with payload mismatch
        using (var alterScope = provider.CreateScope())
        {
            var alterHandler = alterScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var alteredCommand = saleCommand with { AmountTendered = 500m };
            var alterResult = await alterHandler.HandleAsync(alteredCommand, CancellationToken.None);

            Assert.False(alterResult.IsSuccess);
            Assert.Equal("idempotency.payload_mismatch", alterResult.Error?.Code);
        }

        // 4. Replay with divergent actor context: fails closed with context mismatch
        var actorB = await IntegrationIdentitySeeder.CreateActorAsync(db);
        using (var actorScope = provider.CreateScope())
        {
            var actorHandler = actorScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var alteredActorCommand = saleCommand with { CashierUserId = actorB };
            var actorResult = await actorHandler.HandleAsync(alteredActorCommand, CancellationToken.None);

            Assert.False(actorResult.IsSuccess);
            Assert.Equal("idempotency.context_mismatch", actorResult.Error?.Code);
        }

        // 5. Verify exactly 1 Sale exists in PostgreSQL
        using (var finalScope = provider.CreateScope())
        {
            var finalDb = finalScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var salesCount = await finalDb.Sales.CountAsync(s => s.ClientOperationId == saleOpId);
            Assert.Equal(1, salesCount);
        }
    }

    [Fact]
    public async Task Postgres_CompleteSale_CorruptOutcomeFailsClosedWithoutDuplicateMutation()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);

        var corruptOpId = Guid.NewGuid();
        var customer = new Customer
        {
            Name = "P12 Corrupt Test Customer",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);

        var saleCommand = new CompleteSaleCommand(
            corruptOpId,
            customer.Id,
            fixture.ActorId,
            null,
            0m,
            SalePaymentMethod.Bank,
            150m,
            "BANK-REF",
            null,
            [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 150m, [])]);

        var fingerprint = CompleteSaleCanonicalPayload.ComputeFingerprint(saleCommand);

        // Seed an outcome that is Succeeded, but has un-deserializable ResponsePayload and no existing Sale
        var corruptOutcome = new OperationOutcome
        {
            Id = Guid.CreateVersion7(),
            ClientOperationId = corruptOpId,
            OperationType = "Sale",
            PayloadFingerprint = fingerprint,
            Status = OperationOutcomeStatus.Succeeded,
            ResponsePayload = "{\"saleId\": \"corrupted-non-guid\", \"grandTotal\": \"not-a-decimal\"}",
            ResponseStatusCode = 200,
            ActorUserId = fixture.ActorId,
            WasCommitted = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        db.OperationOutcomes.Add(corruptOutcome);
        await db.SaveChangesAsync();

        // Execution must fail closed with idempotency.outcome_corrupted
        using var testScope = provider.CreateScope();
        var testHandler = testScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
        var result = await testHandler.HandleAsync(saleCommand, CancellationToken.None);

        Assert.True(!result.IsSuccess, $"Handler unexpectedly succeeded with WasExisting={result.Value?.WasExisting}, SaleId={result.Value?.SaleId}, Invoice={result.Value?.InvoiceNumber}, Error={result.Error?.Code} - {result.Error?.Message}");
        Assert.Equal("idempotency.outcome_corrupted", result.Error?.Code);

        // CRITICAL INVARIANT: Zero fallthrough mutation occurred in PostgreSQL!
        using var verifyScope = provider.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var salesCount = await verifyDb.Sales.CountAsync(s => s.ClientOperationId == corruptOpId);
        Assert.Equal(0, salesCount);
    }

    [Fact]
    public async Task Postgres_ClaimAsync_ThrowsContextMismatchExceptionOnSuppliedNullActor()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var actorA = Guid.NewGuid();

        // 1. Initial claim with actor A
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);
        }

        // 2. Caller supplying null actor -> fail closed with IdempotencyContextMismatchException
        await using (var attackDb = CreateDbContext(connStr))
        {
            var attackLedger = new EfOperationOutcomeLedger(attackDb);
            var ex = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await attackLedger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: null);
            });

            Assert.Equal("idempotency.context_mismatch", ex.Code);
            Assert.Equal(clientOpId, ex.ClientOperationId);
            Assert.Equal("ActorUserId", ex.ContextKey);
            Assert.Equal(actorA.ToString(), ex.ExistingValue);
            Assert.Equal("null", ex.IncomingValue);
        }

        // 3. Verify in PostgreSQL that ActorUserId remained actorA
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(actorA, outcome.ActorUserId);
            Assert.Equal(OperationOutcomeStatus.Pending, outcome.Status);
        }
    }

    [Fact]
    public async Task Postgres_ClaimAsync_ThrowsContextMismatchExceptionOnTerminalMismatchOrSuppliedNull()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var actorA = Guid.NewGuid();
        var terminalT1 = Guid.NewGuid();
        var terminalT2 = Guid.NewGuid();

        // 1. Initial claim with terminal T1
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);
        }

        // 2. Divergent terminal T2 -> fail closed with IdempotencyContextMismatchException
        await using (var attackDb = CreateDbContext(connStr))
        {
            var attackLedger = new EfOperationOutcomeLedger(attackDb);
            var exDivergent = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await attackLedger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT2);
            });

            Assert.Equal("idempotency.context_mismatch", exDivergent.Code);
            Assert.Equal(clientOpId, exDivergent.ClientOperationId);
            Assert.Equal("TerminalId", exDivergent.ContextKey);
            Assert.Equal(terminalT1.ToString(), exDivergent.ExistingValue);
            Assert.Equal(terminalT2.ToString(), exDivergent.IncomingValue);
        }

        // 3. Supplied-null terminal -> fail closed with IdempotencyContextMismatchException
        await using (var nullDb = CreateDbContext(connStr))
        {
            var nullLedger = new EfOperationOutcomeLedger(nullDb);
            var exNull = await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await nullLedger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: null);
            });

            Assert.Equal("idempotency.context_mismatch", exNull.Code);
            Assert.Equal(clientOpId, exNull.ClientOperationId);
            Assert.Equal("TerminalId", exNull.ContextKey);
            Assert.Equal(terminalT1.ToString(), exNull.ExistingValue);
            Assert.Equal("null", exNull.IncomingValue);
        }

        // 4. Verify in PostgreSQL that TerminalId remained terminalT1
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(terminalT1, outcome.TerminalId);
            Assert.Equal(actorA, outcome.ActorUserId);
            Assert.Equal(OperationOutcomeStatus.Pending, outcome.Status);
        }
    }

    [Fact]
    public async Task Postgres_AuthorizedSameIntentRecovery_SucceedsAfterLegitimateSessionRefresh()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var actorA = Guid.NewGuid();
        var terminalT1 = Guid.NewGuid();
        var sessionS1 = Guid.NewGuid();
        var sessionS2 = Guid.NewGuid();
        var sessionS3 = Guid.NewGuid();

        // 1. Initial claim under session S1
        await using (var db1 = CreateDbContext(connStr))
        {
            var ledger1 = new EfOperationOutcomeLedger(db1);
            var claim1 = await ledger1.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1, sessionId: sessionS1);
            Assert.Equal(OperationClaimStatus.Acquired, claim1.Status);
        }

        // 2. Legitimate recovery on same terminal by same cashier after session refresh to S2
        await using (var db2 = CreateDbContext(connStr))
        {
            var ledger2 = new EfOperationOutcomeLedger(db2);
            var claim2 = await ledger2.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1, sessionId: sessionS2);
            Assert.Equal(OperationClaimStatus.InFlight, claim2.Status);
        }

        // 3. Complete sale with refreshed session S2
        var entityId = Guid.NewGuid();
        await using (var db3 = CreateDbContext(connStr))
        {
            var ledger3 = new EfOperationOutcomeLedger(db3);
            await ledger3.RecordSuccessWithResponseAsync(
                clientOpId,
                "Sale",
                entityId,
                "{\"saleId\":\"" + entityId + "\",\"amount\":150.00}",
                documentNumber: "INV-100",
                actorId: actorA,
                terminalId: terminalT1,
                sessionId: sessionS2,
                payloadFingerprint: fingerprint);
        }

        // Verify in PostgreSQL that TerminalId and ActorUserId are preserved while SessionId is updated to S2
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Succeeded, outcome.Status);
            Assert.Equal(actorA, outcome.ActorUserId);
            Assert.Equal(terminalT1, outcome.TerminalId);
            Assert.Equal(sessionS2, outcome.SessionId);
        }

        // 4. Subsequent replay under session S3 returns authoritative cached outcome
        await using (var db4 = CreateDbContext(connStr))
        {
            var ledger4 = new EfOperationOutcomeLedger(db4);
            var claim3 = await ledger4.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1, sessionId: sessionS3);
            Assert.Equal(OperationClaimStatus.AlreadySucceeded, claim3.Status);
            Assert.NotNull(claim3.Outcome);
            Assert.NotNull(claim3.Outcome.ResponsePayload);
            using var doc = System.Text.Json.JsonDocument.Parse(claim3.Outcome.ResponsePayload);
            Assert.Equal(entityId, doc.RootElement.GetProperty("saleId").GetGuid());
            Assert.Equal(150.00m, doc.RootElement.GetProperty("amount").GetDecimal());
        }
    }

    [Fact]
    public async Task Postgres_RecordMethods_RejectContextMutationsAcrossAllStatusTransitions()
    {
        var connStr = GetConnectionString();
        var clientOpId = Guid.NewGuid();
        var fingerprint = "v1:sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();
        var terminalT1 = Guid.NewGuid();
        var terminalT2 = Guid.NewGuid();

        // 1. Initial claim
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            var claim = await ledger.ClaimAsync(clientOpId, "Sale", fingerprint, actorId: actorA, terminalId: terminalT1);
            Assert.Equal(OperationClaimStatus.Acquired, claim.Status);
        }

        // 2. RecordPendingAsync: divergent actor & divergent terminal
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordPendingAsync(clientOpId, "Sale", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
            });
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordPendingAsync(clientOpId, "Sale", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
            });
        }

        // 3. RecordFailureWithResponseAsync: divergent actor & divergent terminal
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordFailureWithResponseAsync(clientOpId, "Sale", "ERR", "Fail", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
            });
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordFailureWithResponseAsync(clientOpId, "Sale", "ERR", "Fail", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
            });
        }

        // 4. RecordOutcomeUnknownAsync: divergent actor & divergent terminal
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordOutcomeUnknownAsync(clientOpId, "Sale", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
            });
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordOutcomeUnknownAsync(clientOpId, "Sale", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
            });
        }

        // 5. RecordSuccessWithResponseAsync: divergent actor & divergent terminal
        await using (var db = CreateDbContext(connStr))
        {
            var ledger = new EfOperationOutcomeLedger(db);
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordSuccessWithResponseAsync(clientOpId, "Sale", Guid.NewGuid(), "{}", actorId: actorB, terminalId: terminalT1, payloadFingerprint: fingerprint);
            });
            await Assert.ThrowsAsync<IdempotencyContextMismatchException>(async () =>
            {
                await ledger.RecordSuccessWithResponseAsync(clientOpId, "Sale", Guid.NewGuid(), "{}", actorId: actorA, terminalId: terminalT2, payloadFingerprint: fingerprint);
            });
        }

        // 6. Verify in PostgreSQL that state is completely untouched
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var outcome = await verifyDb.OperationOutcomes.AsNoTracking().FirstOrDefaultAsync(x => x.ClientOperationId == clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeStatus.Pending, outcome.Status);
            Assert.Equal(actorA, outcome.ActorUserId);
            Assert.Equal(terminalT1, outcome.TerminalId);
        }
    }
}
