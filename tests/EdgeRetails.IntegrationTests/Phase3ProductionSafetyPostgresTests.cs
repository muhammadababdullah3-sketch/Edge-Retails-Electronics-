using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Production.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase3ProductionSafetyPostgresTests
{
    [Fact]
    public async Task PostgresReadinessProbe_ReportsReady_OnLivePostgres18()
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB")
            ?? Phase2PostgresTestHarness.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("EDGE_RETAILS_TEST_DB is not configured.");
        }

        var probe = new NpgsqlDatabaseReadinessProbe(connectionString);
        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(DatabaseReadinessCode.Ready, result.Code);
        Assert.NotNull(result.PostgreSqlVersion);
        Assert.StartsWith("18.", result.PostgreSqlVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.DatabaseName));
    }

    [Fact]
    public async Task OutboxRepository_PersistsAndTransitionsMessages_InPostgres()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var messageId = Guid.NewGuid();
        var idempotencyKey = $"test_idemp_{Guid.NewGuid():N}";
        var msg = new OutboxMessage
        {
            Id = messageId,
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = Guid.NewGuid().ToString(),
            PayloadJson = "{\"test\": true}",
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            NextAttemptAt = null,
            Status = OutboxMessageStatus.Pending
        };

        repo.Enqueue(msg);
        await db.SaveChangesAsync(CancellationToken.None);

        // Verify message is written to PostgreSQL
        var inDb = await repo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.NotNull(inDb);
        Assert.Equal(OutboxMessageStatus.Pending, inDb.Status);
        Assert.Equal(idempotencyKey, inDb.IdempotencyKey);

        // Verify pending batch retrieval
        var pending = await repo.GetPendingMessagesAsync(500, CancellationToken.None);
        Assert.Contains(pending, x => x.Id == messageId);

        // Mark failed with next attempt
        var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);
        await repo.MarkFailedAsync(messageId, "Transient print error", nextAttempt, CancellationToken.None);

        var afterFail = await repo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.NotNull(afterFail);
        Assert.Equal(OutboxMessageStatus.Pending, afterFail.Status);
        Assert.Equal(1, afterFail.AttemptCount);
        Assert.Contains("Transient print error", afterFail.LastError);

        // Mark completed
        var completedAt = DateTimeOffset.UtcNow;
        await repo.MarkCompletedAsync(messageId, completedAt, CancellationToken.None);

        var afterComplete = await repo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.NotNull(afterComplete);
        Assert.Equal(OutboxMessageStatus.Completed, afterComplete.Status);
        Assert.Equal(2, afterComplete.AttemptCount);
        Assert.NotNull(afterComplete.CompletedAt);
    }

    [Fact]
    public async Task OutboxRepository_EnforcesUniqueIdempotencyKeyConstraint_InPostgres()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var sharedKey = $"duplicate_key_{Guid.NewGuid():N}";

        var msg1 = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = "source_1",
            PayloadJson = "{}",
            IdempotencyKey = sharedKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            Status = OutboxMessageStatus.Pending
        };

        repo.Enqueue(msg1);
        await db.SaveChangesAsync(CancellationToken.None);

        using var scope2 = provider.CreateScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db2 = scope2.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var msg2 = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EffectType = "PrintDocument",
            SourceType = "Sale",
            SourceId = "source_2",
            PayloadJson = "{}",
            IdempotencyKey = sharedKey,
            CreatedAt = DateTimeOffset.UtcNow,
            AttemptCount = 0,
            Status = OutboxMessageStatus.Pending
        };

        repo2.Enqueue(msg2);

        // Expect unique constraint violation in PostgreSQL
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db2.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OutboxMessage_RollsBackAtomically_WhenTransactionFails()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var messageId = Guid.NewGuid();
        var sharedKey = $"rollback_key_{Guid.NewGuid():N}";

        await using (var tx = await db.Database.BeginTransactionAsync(CancellationToken.None))
        {
            var msg = new OutboxMessage
            {
                Id = messageId,
                EffectType = "PrintDocument",
                SourceType = "Sale",
                SourceId = "source_tx",
                PayloadJson = "{}",
                IdempotencyKey = sharedKey,
                CreatedAt = DateTimeOffset.UtcNow,
                AttemptCount = 0,
                Status = OutboxMessageStatus.Pending
            };

            repo.Enqueue(msg);
            await db.SaveChangesAsync(CancellationToken.None);

            // Explicitly roll back the transaction
            await tx.RollbackAsync(CancellationToken.None);
        }

        // Verify message was NOT committed to PostgreSQL
        using var verifyScope = provider.CreateScope();
        var verifyRepo = verifyScope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var found = await verifyRepo.GetByIdAsync(messageId, CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task PosSaleReceiptKindSource_LoadsCanonicalDocumentFromPostgres()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);

        var saleId = Guid.NewGuid();
        var movement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = fixture.ProductId,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            ReferenceId = saleId,
            ActorId = fixture.ActorId,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(movement);

        var sale = new Sale
        {
            Id = saleId,
            InvoiceNumber = $"INV-P3-{Guid.NewGuid():N}"[..16],
            CashierUserId = fixture.ActorId,
            CompletedAt = DateTimeOffset.UtcNow,
            Subtotal = 500m,
            InvoiceDiscount = 0m,
            GrandTotal = 500m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            ReceiptTemplateSnapshot = "{\"shopName\":\"Edge Retails Test Shop\",\"header\":\"Thank you\",\"footer\":\"Visit Again\"}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var item = new SaleItem
        {
            Id = Guid.NewGuid(),
            SaleId = saleId,
            InventoryMovementId = movement.Id,
            ProductId = fixture.ProductId,
            ProductUnitId = fixture.ProductUnitId,
            ProductNameSnapshot = "Test Product Safety Item",
            EnteredQuantity = 2m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 2m,
            UnitPrice = 250m,
            GrossLineTotal = 500m,
            AllocatedInvoiceDiscount = 0m,
            NetLineTotal = 500m,
            UnitCostSnapshot = 100m,
            TotalCostSnapshot = 200m,
            GrossProfitSnapshot = 300m
        };

        db.Sales.Add(sale);
        db.SaleItems.Add(item);
        await db.SaveChangesAsync(CancellationToken.None);

        var router = scope.ServiceProvider.GetRequiredService<IProductionDocumentSource>();
        var doc = await router.LoadAsync(ProductionDocumentKind.PosSaleReceipt, saleId, CancellationToken.None);

        Assert.NotNull(doc);
        Assert.Equal(ProductionDocumentKind.PosSaleReceipt, doc.Kind);
        Assert.Equal(sale.InvoiceNumber, doc.DocumentNumber);
        Assert.Equal("Edge Retails Test Shop", doc.ShopName);
        Assert.Single(doc.Lines);
        Assert.Equal("Test Product Safety Item", doc.Lines[0].Description);
        Assert.Equal(2m, doc.Lines[0].Quantity);
        Assert.Equal(250m, doc.Lines[0].UnitPrice);
        Assert.Equal(500m, doc.Lines[0].LineTotal);
    }

    // =========================================================================
    // MON-W4-003: Negative Guard Tests for Phase2PostgresTestHarness
    // =========================================================================

    [Theory]
    [InlineData("postgres")]
    [InlineData("admin")]
    [InlineData("sa")]
    [InlineData("root")]
    [InlineData("cashier")]
    public void AttestOwnedPostgresRoot_RejectsUnapprovedRole_FailsClosed(string unapprovedRole)
    {
        var connStr = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username={unapprovedRole};Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Phase2PostgresTestHarness.AttestOwnedPostgresRoot(connStr));

        Assert.Contains("FAIL-CLOSED", ex.Message);
        Assert.Contains("prohibited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttestOwnedPostgresRoot_RejectsPort5432_FailsClosed()
    {
        var connStr = "Host=127.0.0.1;Port=5432;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Phase2PostgresTestHarness.AttestOwnedPostgresRoot(connStr));

        Assert.Contains("FAIL-CLOSED: Port 5432 is prohibited", ex.Message);
    }

    [Fact]
    public void AttestOwnedPostgresRoot_RejectsNonLoopbackHost_FailsClosed()
    {
        var connStr = "Host=192.168.1.50;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=dummy";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Phase2PostgresTestHarness.AttestOwnedPostgresRoot(connStr));

        Assert.Contains("FAIL-CLOSED: Host '192.168.1.50' is prohibited", ex.Message);
    }

    [Fact]
    public void AttestOwnedPostgresRoot_RejectsUnownedLookalikeRoot_FailsClosed()
    {
        var fakeDir = Path.Combine(Path.GetTempPath(), "FakeLookalike_EdgeRetailsDisposable_ER-RUN-999");
        Directory.CreateDirectory(fakeDir);
        try
        {
            var connStr = "Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password=dummy";
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Phase2PostgresTestHarness.AttestOwnedPostgresRoot(connStr, runnerRootOverride: fakeDir));

            Assert.Contains("FAIL-CLOSED", ex.Message);
            Assert.Contains("outside the approved isolated runner or lacks verified ownership marker", ex.Message);
        }
        finally
        {
            if (Directory.Exists(fakeDir))
            {
                Directory.Delete(fakeDir, true);
            }
        }
    }

    [Fact]
    public void HighwaterCustody_RejectsCorruptOrMissingArtifacts_FailsClosed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"EdgeRetailsMasterPg_Test_{Guid.NewGuid():N}", "harness-highwater");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Create lock file but intentionally omit checkpoint and manifest
            File.WriteAllText(Path.Combine(tempRoot, "highwater.manifest.lock"), "");

            // Attempting to build or initialize with missing artifacts in existing dir must fail closed
            var manifest = Path.Combine(tempRoot, "highwater.manifest");
            var custody = new EdgeRetails.Infrastructure.Services.OwnedSequenceAuthorityCustody(tempRoot);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                EdgeRetails.Infrastructure.Services.MachineSequenceHighWaterService.InitializeOwnedFixture(manifest, custody));

            Assert.Contains("Sequence fixture initialization requires three absent artifacts", ex.Message);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(tempRoot)!))
            {
                Directory.Delete(Path.GetDirectoryName(tempRoot)!, true);
            }
        }
    }

    [Fact]
    public void CustodyBinding_RejectsDifferentClusterRoot_EvenWhenPortDbAndRunIdMatch_FailsClosed()
    {
        var tempAuthRoot = Path.Combine(Path.GetTempPath(), $"EdgeRetailsMasterPg_MismatchRoot_{Guid.NewGuid():N}", "harness-highwater");
        Directory.CreateDirectory(tempAuthRoot);
        try
        {
            // Seed a binding file pointing to a different cluster root
            var boundJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ClusterRoot = @"C:\FakeDifferentRoot\ClusterA",
                RunId = "ER-RUN-20261011-005",
                Port = 55437,
                Database = "edgeretails_test_run20261012_001",
                BoundAtUtc = DateTimeOffset.UtcNow
            });
            File.WriteAllText(Path.Combine(tempAuthRoot, "cluster_binding.json"), boundJson);
            File.WriteAllText(Path.Combine(tempAuthRoot, "highwater.manifest"), "dummy");
            File.WriteAllText(Path.Combine(tempAuthRoot, "highwater.manifest.checkpoint"), "dummy");
            File.WriteAllText(Path.Combine(tempAuthRoot, "highwater.manifest.lock"), "");

            // Attempt to verify custody with a different current root
            var currentRoot = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Phase2PostgresTestHarness.VerifyOrCreateCustodyBinding(
                    tempAuthRoot, currentRoot, "ER-RUN-20261011-005", 55437, "edgeretails_test_run20261012_001"));

            Assert.Contains("FAIL-CLOSED", ex.Message);
            Assert.Contains("Cluster binding mismatch", ex.Message);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(tempAuthRoot)!))
            {
                Directory.Delete(Path.GetDirectoryName(tempAuthRoot)!, true);
            }
        }
    }

    [Fact]
    public void CustodyBinding_RejectsExistingAuthorityDirectory_WhenBindingFileIsMissing_FailsClosed()
    {
        var tempAuthRoot = Path.Combine(Path.GetTempPath(), $"EdgeRetailsMasterPg_MissingBinding_{Guid.NewGuid():N}", "harness-highwater");
        Directory.CreateDirectory(tempAuthRoot);
        try
        {
            // Create sequence files but intentionally omit cluster_binding.json
            File.WriteAllText(Path.Combine(tempAuthRoot, "highwater.manifest"), "dummy");
            File.WriteAllText(Path.Combine(tempAuthRoot, "highwater.manifest.checkpoint"), "dummy");
            File.WriteAllText(Path.Combine(tempAuthRoot, "highwater.manifest.lock"), "");

            var currentRoot = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Phase2PostgresTestHarness.VerifyOrCreateCustodyBinding(
                    tempAuthRoot, currentRoot, "ER-RUN-20261011-005", 55437, "edgeretails_test_run20261012_001"));

            Assert.Contains("FAIL-CLOSED", ex.Message);
            Assert.Contains("missing required 'cluster_binding.json'", ex.Message);
            Assert.Contains("Silently binding unverified existing authority is forbidden", ex.Message);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(tempAuthRoot)!))
            {
                Directory.Delete(Path.GetDirectoryName(tempAuthRoot)!, true);
            }
        }
    }

    [Fact]
    public void CustodyBinding_RejectsCorruptBindingFile_FailsClosed()
    {
        var tempAuthRoot = Path.Combine(Path.GetTempPath(), $"EdgeRetailsMasterPg_CorruptBinding_{Guid.NewGuid():N}", "harness-highwater");
        Directory.CreateDirectory(tempAuthRoot);
        try
        {
            // Write corrupt invalid JSON to cluster_binding.json
            File.WriteAllText(Path.Combine(tempAuthRoot, "cluster_binding.json"), "{ NOT_VALID_JSON !!!");

            var currentRoot = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Phase2PostgresTestHarness.VerifyOrCreateCustodyBinding(
                    tempAuthRoot, currentRoot, "ER-RUN-20261011-005", 55437, "edgeretails_test_run20261012_001"));

            Assert.Contains("FAIL-CLOSED", ex.Message);
            Assert.Contains("Corrupt cluster binding file", ex.Message);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(tempAuthRoot)!))
            {
                Directory.Delete(Path.GetDirectoryName(tempAuthRoot)!, true);
            }
        }
    }

    [Fact]
    public void CustodyBinding_SurvivesRestartAndReplay_PreservingSequenceAuthority()
    {
        var tempAuthRoot = Path.Combine(Path.GetTempPath(), $"EdgeRetailsMasterPg_RestartReplay_{Guid.NewGuid():N}", "harness-highwater");
        try
        {
            var currentRoot = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";

            // Initial startup: creates authority and binds it
            Phase2PostgresTestHarness.VerifyOrCreateCustodyBinding(
                tempAuthRoot, currentRoot, "ER-RUN-20261011-005", 55437, "edgeretails_test_run20261012_001");

            var manifestPath = Path.Combine(tempAuthRoot, "highwater.manifest");
            var bindingPath = Path.Combine(tempAuthRoot, "cluster_binding.json");
            Assert.True(File.Exists(manifestPath));
            Assert.True(File.Exists(bindingPath));

            var bindingContentFirst = File.ReadAllText(bindingPath);

            // Re-verify (simulating a test fixture restart/replay against the same cluster)
            Phase2PostgresTestHarness.VerifyOrCreateCustodyBinding(
                tempAuthRoot, currentRoot, "ER-RUN-20261011-005", 55437, "edgeretails_test_run20261012_001");

            var bindingContentSecond = File.ReadAllText(bindingPath);
            Assert.Equal(bindingContentFirst, bindingContentSecond);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(tempAuthRoot)!))
            {
                Directory.Delete(Path.GetDirectoryName(tempAuthRoot)!, true);
            }
        }
    }

    // =========================================================================
    // GATE19 & GATE20: EF Model Snapshot and Historical Migration Hash Guards
    // =========================================================================

    [Fact]
    public async Task EFModelSnapshot_HasZeroPendingModelChanges_And_MatchesAppliedMigrations()
    {
        using var provider = Phase2PostgresTestHarness.BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        // 1. Verify that all 27 migrations are applied to the live PostgreSQL 18 database
        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(27, appliedMigrations.Count);

        // Verify the Phase 11 and Phase 12 migrations are applied in sequence
        Assert.Equal("20261012050955_Phase11PhysicalPackContainerSubLedger", appliedMigrations[^3]);
        Assert.Equal("20261012144915_Phase11DurablePostingPeriodLockAndAudit", appliedMigrations[^2]);
        Assert.Equal("20261015103137_Phase12OperationOutcomeResponsePayload", appliedMigrations[^1]);

        // 2. Real EF Core Model Differ / Snapshot check
        bool hasPendingChanges = db.Database.HasPendingModelChanges();
        Assert.False(hasPendingChanges, "EF Core reports pending model changes between DbContext and ModelSnapshot!");

        // 3. Verify via IMigrationsModelDiffer and IMigrationsAssembly directly using DesignTimeModel
        var migrationsAssembly = db.GetInfrastructure().GetService<IMigrationsAssembly>();
        Assert.NotNull(migrationsAssembly);
        Assert.NotNull(migrationsAssembly.ModelSnapshot);
        Assert.NotNull(migrationsAssembly.ModelSnapshot.Model);

        var designTimeModel = db.GetInfrastructure().GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>();
        Assert.NotNull(designTimeModel);

        var modelDiffer = db.GetInfrastructure().GetService<IMigrationsModelDiffer>();
        Assert.NotNull(modelDiffer);

        var differences = modelDiffer.GetDifferences(
            migrationsAssembly.ModelSnapshot.Model.GetRelationalModel(),
            designTimeModel.Model.GetRelationalModel());
        Assert.Empty(differences);

        // 4. Drift detection proof: verify that IMigrationsModelDiffer actively detects differences when models diverge
        var driftFromEmpty = modelDiffer.GetDifferences(
            null,
            designTimeModel.Model.GetRelationalModel());
        Assert.NotEmpty(driftFromEmpty);
        Assert.True(driftFromEmpty.Count > 10, "ModelDiffer must produce migration operations when comparing against different model state");
    }

    [Fact]
    public void MigrationHistory_VerifiesAllHistoricalAndPhase11MigrationHashes()
    {
        // Locate migrations directory relative to solution root
        var currentDir = AppDomain.CurrentDomain.BaseDirectory;
        var solutionDir = currentDir;
        while (!string.IsNullOrEmpty(solutionDir) && !File.Exists(Path.Combine(solutionDir, "EdgeRetails.sln")))
        {
            solutionDir = Path.GetDirectoryName(solutionDir);
        }

        Assert.False(string.IsNullOrEmpty(solutionDir), "Solution directory not found");
        var migrationsDir = Path.Combine(solutionDir, "src", "EdgeRetails.Infrastructure", "Persistence", "Migrations");
        Assert.True(Directory.Exists(migrationsDir), $"Migrations directory missing: {migrationsDir}");

        var migrationFiles = Directory.GetFiles(migrationsDir, "*.cs");
        Assert.Equal(53, migrationFiles.Length); // 24 historical + 2 Phase 11 + 1 Phase 12 classes, 22 historical + 2 Phase 11 + 1 Phase 12 designers, 1 snapshot

        // Verify known historical migration hashes
        var knownHistoricalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["20260920094824_InitialProductionBaseline.cs"] = "A688CD9E809046479BE5076851AAD249E5154FE580A2186F2E94E6F777B45660",
            ["20260920094824_InitialProductionBaseline.Designer.cs"] = "C57CFF81529EEB841BE07C7C354388398BE35FF89B3CBBF8542772C308E1959E",
            ["20260920111318_Sprint7Phase1SetupIdentity.cs"] = "C6136C90644F0D6E7ACA3B0A31E62DBABD98A5A5CE867C4912CDB4151EC30750",
            ["20260920111318_Sprint7Phase1SetupIdentity.Designer.cs"] = "BAD57181FBB1E049B35859C73094BB7D3EBA0A2B70DF044AC50603C250B2DEC7",
            ["20260920164958_Sprint7ProductionCutover.cs"] = "A5411C1E1EB24FE7306F20CF848EFADAEF52794C0CADDAB7B5820384ACAD03B0",
            ["20260920164958_Sprint7ProductionCutover.Designer.cs"] = "08306416CB32E3E9FCEB3159441A737CAEED74064DC62EB5B1F8F80981C964B2",
            ["20260921101001_Sprint8CanonicalReportingSchema.cs"] = "87307B6BA4A5FDC4DDE00A4AD2D57BF989D373AF96CFDEFCB15AF7512913719A",
            ["20260921101001_Sprint8CanonicalReportingSchema.Designer.cs"] = "A1B736FE6D92A170770341F0FFE62F4A71CFEF4382DE3BDDD15ADB09987F7B8F",
            ["20260921143542_Sprint8FinalProductionAlignment.cs"] = "72439707E319C8113413BF7C46ECD9659AC4C3299688B2CFF1CFB6488DB14FFC",
            ["20260921143542_Sprint8FinalProductionAlignment.Designer.cs"] = "13107331FE99A55E5F4471CB4122C68E99070CE3E9A43E59163AB7BDC8F49F6F",
            ["20260921152602_Sprint8WarrantyAlignment.cs"] = "C20CEF42A1CB630627FE67DA2238518D0BAF6842D678325EF53ACDD40C1F0515",
            ["20260921152602_Sprint8WarrantyAlignment.Designer.cs"] = "F0B6AA5CCF454A7F72329033A54D5DB518AFC2B7DB625F1EA30EB40E20F393D6",
            ["20260922120000_Phase1CanonicalSchemaAlignment.cs"] = "454F282252C9C26DF31CBAC903BE45035A0DD1759F22FF92603D920985DA93AE",
            ["20260922120000_Phase1CanonicalSchemaAlignment.Designer.cs"] = "6D545A8EB9B1560EEB2044A97F7E4197F40E8B6B059EDAA770647606C1BD774D",
            ["20260922135055_Phase3ProductionSafetyOutbox.cs"] = "028DEABB516A254F596DA4F151F48AD9425FE0BBDC73CD7F2E090313F948DF12",
            ["20260922135055_Phase3ProductionSafetyOutbox.Designer.cs"] = "FE86DD2F2DC621CC9D085713360B101A99DCEF55B1C5AF23AF1A4FAD328FCA5C",
            ["20260923071510_Phase4MultiTerminalSchema.cs"] = "BA1C923C863503297017ECF0719AEC865A256AC45078255363AC7B48B4DCBCC8",
            ["20260923071510_Phase4MultiTerminalSchema.Designer.cs"] = "67607D46D3CC2A59385E769BC2E76272D0DD75F31085D0A8D78F9AADFE33ABDF",
            ["20260923095632_Phase5WarrantyClaimClientOperationId.cs"] = "4E8BDFD1E6DA2B71F494F4FD575EEF3F892FC0B2B113915355590BCAB74FBA66",
            ["20260923095632_Phase5WarrantyClaimClientOperationId.Designer.cs"] = "3D27998E0367E2F58586EE332D0BB90762F9002909F97A3C4C56E933BB97F7E7",
            ["20260923110943_Phase5WarrantyLifecycleIdempotency.cs"] = "513DD46DE938ADC77C4AF10BFA80082300EF234D81507067D0CFDA78129755E3",
            ["20260923110943_Phase5WarrantyLifecycleIdempotency.Designer.cs"] = "F206C03DCB2140861804D164B85BC18E0B9537588E9FB21AD11F137467C4C84A",
            ["20260923111027_Phase5MovementHistoryOrderingIndex.cs"] = "87C577863177D71864D73C3064862A71A20871DC254C1821325FFF5D77BF9398",
            ["20260923111027_Phase5MovementHistoryOrderingIndex.Designer.cs"] = "E0EC6FEC6FB4E8833527DC5556A1C7CE24BEFB5406B486468A6E8323666029B5",
            ["20260923125420_Phase5PurchaseHistoryOrderingIndex.cs"] = "BB6F0897F8BE68795C8007EF4614E9C79D28DFF329A863D1A2132783B2A7719F",
            ["20260923125420_Phase5PurchaseHistoryOrderingIndex.Designer.cs"] = "1EF6E2691ECD3EA12F03F59DDE0C9D01583AD7DB894EDC09D9E06DBC0D63251F",
            ["20260925142150_Phase1SemanticProductIdentity.cs"] = "307BA075A56E683457321C479BD15C646B8AD10727AF0287014F8B01B3247842",
            ["20260925142150_Phase1SemanticProductIdentity.Designer.cs"] = "068279861318B4CF92D2D5520930B79DEAD1B0CA0A3E01C7996E4AFA78D6BD45",
            ["20260927062206_Phase2DurableOperationOutcome.cs"] = "DFE8BCE79C3A411FFAB13856E6C79351D3703822102D0299316305DE55294D86",
            ["20260927062206_Phase2DurableOperationOutcome.Designer.cs"] = "3E88821AC135A58AF76C4E00CF522838118EC79ACBFE5D9A2071B92AEFF4BE77",
            ["20260927121724_Phase2OutboxLeaseFencing.cs"] = "11407B2FD56E516FADDEAF5EBE019C44D6CA0F2A94A546F370729146A2AE2F18",
            ["20260927121724_Phase2OutboxLeaseFencing.Designer.cs"] = "9BEA031B27536AA8B23C7A5CB2820DDA7BCB0B8182AB6F9CD3A3087CC8AEABFF",
            ["20260928150000_Phase3PosPriceOverride.cs"] = "564DA28049ACEA109C48F49FE214237E2AB7F439B397DF79984495008C8C437F",
            ["20260929100000_Phase3LegacyCategoryUpgradeRecovery.cs"] = "E17551FBDA6A77AC50CF93D4031C8E1D7C9E591794292549B2F47A9E3228A0D6",
            ["20260930053030_Phase3COwnerPinRecoveryReplaySafety.cs"] = "A43A802FB9F017DFB2298CA8A1ED23A789075BA1ECD6B8DDADBAAE8A53986043",
            ["20260930053030_Phase3COwnerPinRecoveryReplaySafety.Designer.cs"] = "41536C7C2BF75F30EDF0D8978B512602849B53FB45864D283795D73BFA8A5F87",
            ["20260930065058_Phase3COwnerPinAuthorizationConsumption.cs"] = "A1B46B7D074F723A360B16C4D6AB8628A896E5A23D99EC3CEE9B7FB2AA40C4DE",
            ["20260930065058_Phase3COwnerPinAuthorizationConsumption.Designer.cs"] = "500BFA1B0B03F0BDD3F71113C2454C42A3DD9BCD18BCD159114DC6DA85250177",
            ["20261002101709_TrackingManufacturerIdentityAuthorityV1.cs"] = "F35CDB8124EE96B335547AD69992923A42E587B08077F341F894DBF3BFDDDE9B",
            ["20261002101709_TrackingManufacturerIdentityAuthorityV1.Designer.cs"] = "901C2140CB4C5741FED9CF2CBF702E068A1B87A968B17A9DD275BA33B0D43704",
            ["20261006083906_Phase7Pass5WarrantySourceAuthority.cs"] = "27894C760915668AEA74F3E85F81DC397F38EEE826A189FFA42832E409EC4CB8",
            ["20261006083906_Phase7Pass5WarrantySourceAuthority.Designer.cs"] = "BEF26856317DDAFC71CEDBD784002D29B51B67F79EF6A664DEA7AE31FAF77E3B",
            ["20261007063406_Phase7Pass5ExactWarrantyMultiSourceAuthority.cs"] = "147FD3941A316075EF5BBCAA1A4613EE43690CE608F431906910E7EA90E3ACE3",
            ["20261007063406_Phase7Pass5ExactWarrantyMultiSourceAuthority.Designer.cs"] = "9BE433147FBBA8D3514908EB4B16274B7727674B86FAF3D67BA0E97F0F33F1B1"
        };

        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var (fileName, expectedHash) in knownHistoricalHashes)
        {
            var filePath = Path.Combine(migrationsDir, fileName);
            Assert.True(File.Exists(filePath), $"Historical migration file missing: {fileName}");

            var actualBytes = File.ReadAllBytes(filePath);
            var actualHash = Convert.ToHexString(sha.ComputeHash(actualBytes));
            Assert.Equal(expectedHash, actualHash);
        }

        // Verify Phase 11 & Phase 12 migrations are present and have non-empty valid content
        var p11Pack = Path.Combine(migrationsDir, "20261012050955_Phase11PhysicalPackContainerSubLedger.cs");
        var p11Posting = Path.Combine(migrationsDir, "20261012144915_Phase11DurablePostingPeriodLockAndAudit.cs");
        var p12Payload = Path.Combine(migrationsDir, "20261015103137_Phase12OperationOutcomeResponsePayload.cs");
        Assert.True(File.Exists(p11Pack));
        Assert.True(File.Exists(p11Posting));
        Assert.True(File.Exists(p12Payload));
        Assert.True(new FileInfo(p11Pack).Length > 0);
        Assert.True(new FileInfo(p11Posting).Length > 0);
        Assert.True(new FileInfo(p12Payload).Length > 0);
    }
}

