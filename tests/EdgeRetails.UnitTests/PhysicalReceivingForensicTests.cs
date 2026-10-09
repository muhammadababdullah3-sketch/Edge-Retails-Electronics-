using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.UnitTests;

/// <summary>
/// Physical Receiving Forensic & Hardening Test Suite (Phase 2 - BLOCKER A / AGENT R).
/// Verifies:
/// 1. Canonical outcome ledger write path and durable recording
/// 2. ClientOperationId stability and idempotency replay
/// 3. Payload fingerprinting and mismatch protection
/// 4. Invariant safety: no duplicate units, no duplicate movements, no duplicate sequence advancement
/// 5. Restart recovery with fresh service provider and DbContext
/// 6. Production DI wireup with EfOperationOutcomeLedger
/// </summary>
public sealed class PhysicalReceivingForensicTests
{
    private sealed class PhysicalReceivingTestFixture
    {
        public Phase2TestDoubles Doubles { get; } = new();
        public Guid ActorId { get; } = Guid.NewGuid();
        public ReceiveProductIntakeHandler IntakeHandler { get; }

        public PhysicalReceivingTestFixture()
        {
            IntakeHandler = new ReceiveProductIntakeHandler(
                Doubles.Purchasing,
                Doubles.Parties,
                Doubles.Catalog,
                Doubles.Inventory,
                Doubles.CostAllocator,
                Doubles.Traceability,
                Doubles.OperationLock,
                Doubles.ResourceLock,
                Doubles.Audit,
                Doubles.Clock,
                Doubles.Transactions,
                Doubles.Authorization,
                Doubles.UnitOfWork,
                NullSequenceHighWaterService.Instance,
                Doubles.OutcomeLedger,
                Doubles.PhysicalUnits);
        }

        public async Task<(Purchase purchase, PurchaseItem item, Product product, SupplierProduct supplierProduct)> SeedSetupAsync(
            TrackingMode mode = TrackingMode.IndividualPiece,
            long startingSequence = 10,
            bool serialTracking = false,
            bool imeiTracking = false)
        {
            var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Alpha Supplier", DealerCode = "ALP", IsActive = true };
            Doubles.Parties.AddSupplier(supplier);

            var company = new Company { Id = Guid.NewGuid(), Name = "Sony", Code = "SNY", IsActive = true };
            Doubles.Catalog.AddCompany(company);

            var category = new Category { Id = Guid.NewGuid(), Name = "Audio", IdentitySymbol = "A", IsActive = true };
            Doubles.Catalog.AddCategory(category);

            var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
            Doubles.Catalog.AddUnit(unit);

            var product = new Product
            {
                Id = Guid.NewGuid(),
                Name = "Headphones WH-1000XM5",
                CompanyId = company.Id,
                CategoryId = category.Id,
                Model = "XM5",
                ModelCode = "XM5",
                Sku = "SNYA-XM5",
                TrackingMode = mode,
                SerialTrackingEnabled = serialTracking,
                ImeiTrackingEnabled = imeiTracking,
                IsActive = true
            };
            var productUnit = new ProductUnit
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                UnitId = unit.Id,
                FactorToBaseUnit = 1m,
                CanPurchase = true,
                IsActive = true
            };
            Doubles.Catalog.AddProduct(product);
            Doubles.Catalog.AddProductUnit(productUnit);

            var supplierProduct = new SupplierProduct
            {
                Id = Guid.NewGuid(),
                SupplierId = supplier.Id,
                ProductId = product.Id,
                NextItemSequence = startingSequence,
                IsActive = true
            };
            Doubles.Traceability.AddSupplierProduct(supplierProduct);

            var purchase = new Purchase
            {
                Id = Guid.NewGuid(),
                PurchaseNumber = "PUR-RECEIVE-001",
                SupplierId = supplier.Id,
                SupplierInvoiceNumber = "INV-SUPP-99",
                Status = PurchaseStatus.Completed,
                PurchaseDate = Doubles.Clock.ShopDate,
                CreatedBy = ActorId
            };
            Doubles.Purchasing.AddPurchase(purchase);

            var item = new PurchaseItem
            {
                Id = Guid.NewGuid(),
                PurchaseId = purchase.Id,
                ProductId = product.Id,
                ProductUnitId = productUnit.Id,
                EnteredQuantity = 10m,
                EnteredUnitCost = 300m,
                BaseQuantity = 10m,
                EffectiveBaseUnitCost = 300m,
                FactorToBaseSnapshot = (10m) / (10m),
                EffectiveLineCost = (10m) * (300m),
                BaseLineTotal = 3000m
            };
            Doubles.Purchasing.AddPurchaseItem(item);

            return (purchase, item, product, supplierProduct);
        }
    }

    private static ServiceProvider CreateRestartServiceProvider(
        string dbName,
        InMemoryDatabaseRoot root,
        Phase2TestDoubles fakes)
    {
        var services = new ServiceCollection();
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;

        services.AddSingleton(options);
        services.AddScoped(sp => new EdgeRetailsDbContext(sp.GetRequiredService<DbContextOptions<EdgeRetailsDbContext>>()));
        services.AddScoped<IOperationOutcomeLedger, EfOperationOutcomeLedger>();

        services.AddSingleton<ISalesRepository>(fakes.Sales);
        services.AddSingleton<IPurchasingRepository>(fakes.Purchasing);
        services.AddSingleton<ISupplierAccountRepository>(fakes.SupplierAccounts);
        services.AddSingleton<IInventoryRepository>(fakes.Inventory);
        services.AddSingleton<IInventoryCostAllocator>(fakes.CostAllocator);
        services.AddSingleton<ITraceabilityRepository>(fakes.Traceability);
        services.AddSingleton<ICatalogRepository>(fakes.Catalog);
        services.AddSingleton<IPartyRepository>(fakes.Parties);
        services.AddSingleton<IOperationLock>(fakes.OperationLock);
        services.AddSingleton<IResourceLock>(fakes.ResourceLock);
        services.AddSingleton<IBusinessAuditWriter>(fakes.Audit);
        services.AddSingleton<IClock>(fakes.Clock);
        services.AddSingleton<ITransactionRunner>(fakes.Transactions);
        services.AddSingleton<IApplicationPermissionAuthorizer>(fakes.Authorization);
        services.AddSingleton<IUnitOfWork>(fakes.UnitOfWork);
        services.AddSingleton<ISequenceHighWaterService>(NullSequenceHighWaterService.Instance);
        services.AddSingleton<IPhysicalUnitCreationAuthority>(fakes.PhysicalUnits);

        services.AddScoped<ReceiveProductIntakeHandler>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReceiveIntake_SameOperationId_ReplayReturnsOriginalUnits()
    {
        var fixture = new PhysicalReceivingTestFixture();
        var (purchase, item, product, _) = await fixture.SeedSetupAsync(startingSequence: 100);

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 3m,
            EnteredUnitCost: 300m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: fixture.ActorId,
            ClientOperationId: opId);

        // Act 1: Initial receipt
        var firstResult = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        Assert.False(firstResult.Value!.WasExisting);
        Assert.Equal(3, firstResult.Value.CommittedUnits.Count);

        // Act 2: Replay same ClientOperationId
        var replayResult = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(replayResult.IsSuccess, replayResult.Error?.Message);
        Assert.True(replayResult.Value!.WasExisting, "Replay must indicate WasExisting == true");
        Assert.Equal(3, replayResult.Value.CommittedUnits.Count);

        // Assert: Exact matching units
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(firstResult.Value.CommittedUnits[i].Id, replayResult.Value.CommittedUnits[i].Id);
            Assert.Equal(firstResult.Value.CommittedUnits[i].TrackingCode, replayResult.Value.CommittedUnits[i].TrackingCode);
            Assert.Equal(firstResult.Value.CommittedUnits[i].ItemSequence, replayResult.Value.CommittedUnits[i].ItemSequence);
            Assert.Equal(firstResult.Value.CommittedUnits[i].AcquisitionCost, replayResult.Value.CommittedUnits[i].AcquisitionCost);
        }
    }

    [Fact]
    public async Task ReceiveIntake_ResponseLost_ReplayDoesNotDuplicateInventoryUnits()
    {
        var fixture = new PhysicalReceivingTestFixture();
        var (purchase, item, product, _) = await fixture.SeedSetupAsync(startingSequence: 1);

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 4m,
            EnteredUnitCost: 300m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: fixture.ActorId,
            ClientOperationId: opId);

        // First attempt commits but response is lost
        var res1 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.Equal(4, fixture.Doubles.Inventory.Units.Count);

        // Replay
        var res2 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res2.IsSuccess, res2.Error?.Message);
        Assert.True(res2.Value!.WasExisting);

        // Invariant: Exactly 4 units in repository, NOT 8
        Assert.Equal(4, fixture.Doubles.Inventory.Units.Count);
    }

    [Fact]
    public async Task ReceiveIntake_ReplayDoesNotAdvanceSequenceTwice()
    {
        var fixture = new PhysicalReceivingTestFixture();
        var (purchase, item, product, supplierProduct) = await fixture.SeedSetupAsync(startingSequence: 50);

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 5m,
            EnteredUnitCost: 300m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: fixture.ActorId,
            ClientOperationId: opId);

        var res1 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.Equal(55, supplierProduct.NextItemSequence);

        // Replay
        var res2 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res2.IsSuccess, res2.Error?.Message);

        // Invariant: NextItemSequence must remain strictly 55, NOT advance to 60
        Assert.Equal(55, supplierProduct.NextItemSequence);
    }

    [Fact]
    public async Task ReceiveIntake_ReplayDoesNotDuplicateInventoryMovement()
    {
        var fixture = new PhysicalReceivingTestFixture();
        var (purchase, item, product, _) = await fixture.SeedSetupAsync();

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 2m,
            EnteredUnitCost: 300m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: fixture.ActorId,
            ClientOperationId: opId);

        var res1 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        Assert.Single(fixture.Doubles.Inventory.Movements);
        Assert.Equal(opId, fixture.Doubles.Inventory.Movements[0].CorrelationId);

        // Replay
        var res2 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res2.IsSuccess, res2.Error?.Message);

        // Invariant: Exactly 1 movement in repository, NOT 2
        Assert.Single(fixture.Doubles.Inventory.Movements);
    }

    [Fact]
    public async Task ReceiveIntake_ReplayPreservesTrackingCodes()
    {
        var fixture = new PhysicalReceivingTestFixture();
        var (purchase, item, product, _) = await fixture.SeedSetupAsync(startingSequence: 10);

        var opId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 3m,
            EnteredUnitCost: 300m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: fixture.ActorId,
            ClientOperationId: opId);

        var res1 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res1.IsSuccess, res1.Error?.Message);
        var expectedCodes = res1.Value!.CommittedUnits.Select(u => u.TrackingCode).ToList();
        Assert.Equal(new[] { "ALP-SNYA-XM5-000010", "ALP-SNYA-XM5-000011", "ALP-SNYA-XM5-000012" }, expectedCodes);

        // Replay
        var res2 = await fixture.IntakeHandler.HandleAsync(command);
        Assert.True(res2.IsSuccess, res2.Error?.Message);
        var replayedCodes = res2.Value!.CommittedUnits.Select(u => u.TrackingCode).ToList();

        Assert.Equal(expectedCodes, replayedCodes);
    }

    [Fact]
    public async Task ReceiveIntake_DifferentPayloadSameOperationId_IsRejected()
    {
        var fixture = new PhysicalReceivingTestFixture();
        var (purchase, item, product, _) = await fixture.SeedSetupAsync();

        var opId = Guid.NewGuid();
        var command1 = new ReceiveProductIntakeCommand(
            purchase.Id,
            item.ProductId,
            item.ProductUnitId,
            EnteredQuantity: 2m,
            EnteredUnitCost: 300m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: fixture.ActorId,
            ClientOperationId: opId);

        var res1 = await fixture.IntakeHandler.HandleAsync(command1);
        Assert.True(res1.IsSuccess, res1.Error?.Message);

        // Replay with same ClientOperationId but different quantity (5 instead of 2)
        var commandDifferentQty = command1 with { EnteredQuantity = 5m };
        var resQty = await fixture.IntakeHandler.HandleAsync(commandDifferentQty);

        Assert.False(resQty.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", resQty.Error?.Code);
    }

    [Fact]
    public async Task ReceiveIntake_OutcomeSurvivesServiceRestart()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = "receive_restart_test_" + Guid.NewGuid();
        var sharedFakes = new Phase2TestDoubles();
        var actorId = Guid.NewGuid();

        // Seed domain data into shared repository
        var supplier = new Supplier { Id = Guid.NewGuid(), Name = "Restart Supplier", DealerCode = "RST", IsActive = true };
        sharedFakes.Parties.AddSupplier(supplier);

        var company = new Company { Id = Guid.NewGuid(), Name = "Sony", Code = "SNY", IsActive = true };
        sharedFakes.Catalog.AddCompany(company);

        var category = new Category { Id = Guid.NewGuid(), Name = "Audio", IdentitySymbol = "A", IsActive = true };
        sharedFakes.Catalog.AddCategory(category);

        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        sharedFakes.Catalog.AddUnit(unit);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Restart Headphones",
            CompanyId = company.Id,
            CategoryId = category.Id,
            Model = "RH1",
            ModelCode = "RH1",
            Sku = "SNYA-RH1",
            TrackingMode = TrackingMode.IndividualPiece,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            IsActive = true
        };
        sharedFakes.Catalog.AddProduct(product);
        sharedFakes.Catalog.AddProductUnit(productUnit);

        var supplierProduct = new SupplierProduct
        {
            Id = Guid.NewGuid(),
            SupplierId = supplier.Id,
            ProductId = product.Id,
            NextItemSequence = 10,
            IsActive = true
        };
        sharedFakes.Traceability.AddSupplierProduct(supplierProduct);

        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            PurchaseNumber = "PUR-RESTART-001",
            SupplierId = supplier.Id,
            SupplierInvoiceNumber = "INV-RST-1",
            Status = PurchaseStatus.Completed,
            PurchaseDate = sharedFakes.Clock.ShopDate,
            CreatedBy = actorId
        };
        sharedFakes.Purchasing.AddPurchase(purchase);

        var purchaseItem = new PurchaseItem
        {
            Id = Guid.NewGuid(),
            PurchaseId = purchase.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            EnteredQuantity = 5m,
            EnteredUnitCost = 200m,
            BaseQuantity = 5m,
            EffectiveBaseUnitCost = 200m,
            FactorToBaseSnapshot = (5m) / (5m),
            EffectiveLineCost = (5m) * (200m),
            BaseLineTotal = 1000m
        };
        sharedFakes.Purchasing.AddPurchaseItem(purchaseItem);

        var clientOpId = Guid.NewGuid();
        var command = new ReceiveProductIntakeCommand(
            purchase.Id,
            product.Id,
            productUnit.Id,
            EnteredQuantity: 2m,
            EnteredUnitCost: 200m,
            SerializedUnits: Array.Empty<SerializedIdentityInput>(),
            CreatedBy: actorId,
            ClientOperationId: clientOpId);

        Guid originalMovementId;
        IReadOnlyList<CommittedInventoryUnitDto> originalUnits;

        // Stage 1: Provider 1 with its own DbContext and EfOperationOutcomeLedger
        using (var provider1 = CreateRestartServiceProvider(dbName, root, sharedFakes))
        {
            using var scope = provider1.CreateScope();
            var handler1 = scope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>();

            var res1 = await handler1.HandleAsync(command);
            Assert.True(res1.IsSuccess, res1.Error?.Message);
            Assert.False(res1.Value!.WasExisting);
            Assert.Equal(2, res1.Value.CommittedUnits.Count);

            originalUnits = res1.Value.CommittedUnits;
            originalMovementId = sharedFakes.Inventory.Movements.First(m => m.CorrelationId == clientOpId).Id;

            // Invariant: Sequence advanced to 12
            Assert.Equal(12, supplierProduct.NextItemSequence);
        } // Provider 1 and its DbContext disposed here

        // Stage 2: Provider 2 (fresh service provider / fresh DbContext, NO shared in-memory ledger)
        using (var provider2 = CreateRestartServiceProvider(dbName, root, sharedFakes))
        {
            using var scope = provider2.CreateScope();
            var handler2 = scope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>();

            // Replay same ClientOperationId
            var res2 = await handler2.HandleAsync(command);
            Assert.True(res2.IsSuccess, res2.Error?.Message);
            Assert.True(res2.Value!.WasExisting, "Must recover existing execution on restart replay");
            Assert.Equal(2, res2.Value.CommittedUnits.Count);

            // Verify units match identically
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(originalUnits[i].Id, res2.Value.CommittedUnits[i].Id);
                Assert.Equal(originalUnits[i].TrackingCode, res2.Value.CommittedUnits[i].TrackingCode);
                Assert.Equal(originalUnits[i].ItemSequence, res2.Value.CommittedUnits[i].ItemSequence);
            }

            // Invariant: sequence NOT advanced again
            Assert.Equal(12, supplierProduct.NextItemSequence);

            // Invariant: no duplicate movements
            Assert.Single(sharedFakes.Inventory.Movements, m => m.CorrelationId == clientOpId);

            // Invariant: exactly 2 units in inventory
            Assert.Equal(2, sharedFakes.Inventory.Units.Count);

            // Invariant: canonical durable ledger contains the Succeeded outcome
            var ledger2 = scope.ServiceProvider.GetRequiredService<IOperationOutcomeLedger>();
            var outcome = await ledger2.GetOutcomeAsync(clientOpId);
            Assert.NotNull(outcome);
            Assert.Equal(OperationOutcomeState.Succeeded, outcome.State);
            Assert.Equal(originalMovementId, outcome.EntityId);
            Assert.True(outcome.WasCommitted);
        }
    }

    [Fact]
    public void ReceiveIntake_ProductionUsesDurableOutcomeLedger()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEdgeRetailsInfrastructure("Host=127.0.0.1;Port=5432;Database=edgeretails_prod;Username=postgres;Password=postgres");
        services.AddSingleton<ISequenceHighWaterService>(NullSequenceHighWaterService.Instance);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReceiveProductIntakeHandler>();

        Assert.NotNull(handler.OutcomeLedger);
        Assert.IsType<EfOperationOutcomeLedger>(handler.OutcomeLedger);
    }
}
